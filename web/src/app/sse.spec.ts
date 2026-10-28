import { flushSse, parseSseChunk, parseSseChunks, toChatEvent } from './sse';

const ev = (type: string, data: unknown) => `event: ${type}\ndata: ${JSON.stringify(data)}\n\n`;

describe('SSE parser', () => {
  it('parses multiple events delivered in one chunk', () => {
    const chunk = ev('session', { sessionId: 's1' }) + ev('text', { delta: 'Hi' }) + ev('done', { stopReason: 'end_turn' });
    const { events, rest } = parseSseChunk('', chunk);
    expect(events.map((e) => e.event)).toEqual(['session', 'text', 'done']);
    expect(JSON.parse(events[1].data)).toEqual({ delta: 'Hi' });
    expect(rest).toBe('');
  });

  it('reassembles an event split mid-line and mid-field across chunks', () => {
    const full = ev('text', { delta: 'hello world' });
    // Split at every possible offset: the result must always be the same single event.
    for (let i = 1; i < full.length; i++) {
      const { events, rest } = parseSseChunks([full.slice(0, i), full.slice(i)]);
      expect(events).toEqual([{ event: 'text', data: '{"delta":"hello world"}' }]);
      expect(rest).toBe('');
    }
  });

  it('handles a stream delivered one character at a time', () => {
    const full = ev('session', { sessionId: 'abc' }) + ev('text', { delta: 'a\nb' }) + ev('done', { stopReason: 'end_turn' });
    const { events } = parseSseChunks([...full]);
    expect(events.map((e) => e.event)).toEqual(['session', 'text', 'done']);
    expect(JSON.parse(events[1].data).delta).toBe('a\nb');
  });

  it('keeps a trailing partial event in rest until its blank line arrives', () => {
    const first = parseSseChunk('', ev('text', { delta: 'a' }) + 'event: text\ndata: {"delta":"b"}\n');
    expect(first.events).toHaveLength(1);
    expect(first.rest).toBe('event: text\ndata: {"delta":"b"}\n');
    const second = parseSseChunk(first.rest, '\n');
    expect(second.events).toEqual([{ event: 'text', data: '{"delta":"b"}' }]);
    expect(second.rest).toBe('');
  });

  it('flushSse dispatches a final event missing its terminating blank line', () => {
    expect(flushSse('event: done\ndata: {"stopReason":"end_turn"}\n')).toEqual([
      { event: 'done', data: '{"stopReason":"end_turn"}' },
    ]);
    expect(flushSse('')).toEqual([]);
    expect(flushSse('event: done\n')).toEqual([]);
  });

  it('supports CRLF line endings, including a CRLF split across chunks', () => {
    const crlf = 'event: text\r\ndata: {"delta":"x"}\r\n\r\nevent: done\r\ndata: {"stopReason":"end_turn"}\r\n\r\n';
    const whole = parseSseChunk('', crlf);
    expect(whole.events.map((e) => e.event)).toEqual(['text', 'done']);
    const cut = crlf.indexOf('\r\n') + 1; // between \r and \n
    const split = parseSseChunks([crlf.slice(0, cut), crlf.slice(cut)]);
    expect(split.events).toEqual(whole.events);
  });

  it('ignores comments, joins multi-line data and defaults the event name to "message"', () => {
    const { events } = parseSseChunk('', ': keep-alive\n\ndata:one\ndata: two\n\n');
    expect(events).toEqual([{ event: 'message', data: 'one\ntwo' }]);
  });

  it('toChatEvent parses known types, drops unknown ones and reports malformed JSON', () => {
    expect(toChatEvent({ event: 'text', data: '{"delta":"hi"}' })).toEqual({ type: 'text', data: { delta: 'hi' } });
    expect(toChatEvent({ event: 'ping', data: '{}' })).toBeNull();
    expect(toChatEvent({ event: 'text', data: '{oops' })?.type).toBe('error');
  });
});
