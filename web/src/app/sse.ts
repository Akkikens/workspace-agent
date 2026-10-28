import { CHAT_EVENT_TYPES, ChatEvent, ChatEventType } from './events';

/** One raw SSE event: the `event:` field and the joined `data:` lines. */
export interface RawSseEvent {
  event: string;
  data: string;
}

export interface SseParseResult {
  events: RawSseEvent[];
  /** Unconsumed text (an incomplete event); pass it back as `buffer` with the next chunk. */
  rest: string;
}

/**
 * Pure incremental SSE parser. Appends `chunk` to `buffer`, returns every complete
 * event (terminated by a blank line) and the trailing partial text. Handles events
 * split anywhere across chunk boundaries, several events per chunk, CRLF / CR / LF
 * line endings, comment lines (`:`), multi-line `data:` and the optional space after `:`.
 */
export function parseSseChunk(buffer: string, chunk: string): SseParseResult {
  let raw = buffer + chunk;
  // Hold back a trailing "\r": it may be the first half of a CRLF split across chunks.
  const hold = raw.endsWith('\r') ? '\r' : '';
  if (hold) raw = raw.slice(0, -1);
  const text = raw.replace(/\r\n?/g, '\n');
  const events: RawSseEvent[] = [];
  let start = 0;
  let sep: number;
  while ((sep = text.indexOf('\n\n', start)) !== -1) {
    const ev = parseBlock(text.slice(start, sep));
    if (ev) events.push(ev);
    start = sep + 2;
  }
  return { events, rest: text.slice(start) + hold };
}

/** Parses a sequence of chunks in one go (used by tests and the end-of-stream flush). */
export function parseSseChunks(chunks: string[]): SseParseResult {
  let rest = '';
  const events: RawSseEvent[] = [];
  for (const chunk of chunks) {
    const r = parseSseChunk(rest, chunk);
    events.push(...r.events);
    rest = r.rest;
  }
  return { events, rest };
}

/** At end of stream, dispatch a final event that is missing its terminating blank line. */
export function flushSse(rest: string): RawSseEvent[] {
  const ev = parseBlock(rest.replace(/\r\n?/g, '\n').replace(/\n+$/, ''));
  return ev ? [ev] : [];
}

function parseBlock(block: string): RawSseEvent | null {
  let event = 'message';
  const data: string[] = [];
  for (const line of block.split('\n')) {
    if (line === '' || line.startsWith(':')) continue;
    const colon = line.indexOf(':');
    const field = colon === -1 ? line : line.slice(0, colon);
    let value = colon === -1 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) value = value.slice(1);
    if (field === 'event') event = value;
    else if (field === 'data') data.push(value);
  }
  return data.length ? { event, data: data.join('\n') } : null;
}

/** Converts a raw SSE event into a typed chat event; unknown types return null. */
export function toChatEvent(raw: RawSseEvent): ChatEvent | null {
  if (!CHAT_EVENT_TYPES.includes(raw.event as ChatEventType)) return null;
  try {
    return { type: raw.event, data: JSON.parse(raw.data) } as ChatEvent;
  } catch {
    return { type: 'error', data: { message: `Malformed ${raw.event} event from server` } };
  }
}
