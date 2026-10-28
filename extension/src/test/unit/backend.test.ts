import assert from 'node:assert/strict';
import * as http from 'node:http';
import { AddressInfo } from 'node:net';
import { BackendHttpError, BackendUnreachableError, SseEvent, SseParser, askCode } from '../../backend';

function parseAll(pieces: string[]): SseEvent[] {
  const p = new SseParser();
  const out = pieces.flatMap((piece) => p.feed(piece));
  return [...out, ...p.flush()];
}

const STREAM =
  'event: session\ndata: {"sessionId":"s1"}\n\n' +
  ': keep-alive comment\n\n' +
  'event: text\ndata: {"delta":"Totals are computed in "}\n\n' +
  'event: text\ndata: {"delta":"[src/invoice.ts:10-13]."}\n\n' +
  'event: done\ndata: {"stopReason":"end_turn"}\n\n';

describe('SseParser', () => {
  const expected: SseEvent[] = [
    { event: 'session', data: '{"sessionId":"s1"}' },
    { event: 'text', data: '{"delta":"Totals are computed in "}' },
    { event: 'text', data: '{"delta":"[src/invoice.ts:10-13]."}' },
    { event: 'done', data: '{"stopReason":"end_turn"}' },
  ];

  it('parses a whole stream and skips comments', () => {
    assert.deepEqual(parseAll([STREAM]), expected);
  });

  it('gives the same events however the stream is split', () => {
    assert.deepEqual(parseAll(STREAM.split('')), expected);
    assert.deepEqual(parseAll([STREAM.slice(0, 17), STREAM.slice(17, 50), STREAM.slice(50)]), expected);
  });

  it('handles CRLF, including a CRLF split across reads', () => {
    const crlf = STREAM.replace(/\n/g, '\r\n');
    assert.deepEqual(parseAll([crlf]), expected);
    const at = crlf.indexOf('\r\n') + 1; // split between \r and \n
    assert.deepEqual(parseAll([crlf.slice(0, at), crlf.slice(at)]), expected);
  });

  it('joins multi-line data, defaults the event type and strips one leading space', () => {
    assert.deepEqual(parseAll(['data: line one\ndata:  line two\n\n']), [{ event: 'message', data: 'line one\n line two' }]);
  });

  it('does not dispatch an event without data, and resets the type afterwards', () => {
    assert.deepEqual(parseAll(['event: ping\n\ndata: x\n\n']), [{ event: 'message', data: 'x' }]);
  });

  it('flushes a final event that lacks the terminating blank line', () => {
    assert.deepEqual(parseAll(['event: done\ndata: {"stopReason":"end_turn"}']), [{ event: 'done', data: '{"stopReason":"end_turn"}' }]);
  });
});

interface Captured {
  url?: string;
  init?: RequestInit;
}

/** A fetch that returns `body` as a stream of the given byte slices. */
function fakeFetch(pieces: Uint8Array[], captured: Captured = {}, status = 200): typeof fetch {
  return (async (url: string | URL | Request, init?: RequestInit) => {
    captured.url = String(url);
    captured.init = init;
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        for (const p of pieces) controller.enqueue(p);
        controller.close();
      },
    });
    return new Response(stream, { status, headers: { 'Content-Type': 'text/event-stream' } });
  }) as typeof fetch;
}

function bytes(s: string): Uint8Array {
  return new TextEncoder().encode(s);
}

describe('askCode', () => {
  const chunks = [{ path: 'src/invoice.ts', startLine: 10, endLine: 13, text: 'export function calculateInvoiceTotal() {}' }];

  it('posts the contract request and accumulates streamed text', async () => {
    const captured: Captured = {};
    const deltas: string[] = [];
    const result = await askCode({
      baseUrl: 'http://localhost:5080/',
      user: 'bob',
      question: 'Where is the invoice total computed?',
      chunks,
      onText: (d) => deltas.push(d),
      fetchImpl: fakeFetch([bytes(STREAM)], captured),
    });
    assert.equal(captured.url, 'http://localhost:5080/api/code/ask');
    assert.equal(captured.init?.method, 'POST');
    const headers = captured.init?.headers as Record<string, string>;
    assert.equal(headers['X-Demo-User'], 'bob');
    assert.equal(headers['Content-Type'], 'application/json');
    assert.deepEqual(JSON.parse(String(captured.init?.body)), { question: 'Where is the invoice total computed?', chunks });
    assert.deepEqual(deltas, ['Totals are computed in ', '[src/invoice.ts:10-13].']);
    assert.deepEqual(result, { text: 'Totals are computed in [src/invoice.ts:10-13].', sessionId: 's1', stopReason: 'end_turn' });
  });

  it('decodes a multi-byte UTF-8 character split across reads', async () => {
    const all = bytes('event: text\ndata: {"delta":"café"}\n\nevent: done\ndata: {"stopReason":"end_turn"}\n\n');
    const cut = all.indexOf(0xc3) + 1; // between the two bytes of U+00E9
    const result = await askCode({ baseUrl: 'http://x', user: 'alice', question: 'q', chunks: [], fetchImpl: fakeFetch([all.slice(0, cut), all.slice(cut)]) });
    assert.equal(result.text, 'café');
  });

  it('reports error events', async () => {
    const stream = 'event: session\ndata: {"sessionId":"s2"}\n\nevent: error\ndata: {"message":"model overloaded"}\n\nevent: done\ndata: {"stopReason":"error"}\n\n';
    const result = await askCode({ baseUrl: 'http://x', user: 'alice', question: 'q', chunks: [], fetchImpl: fakeFetch([bytes(stream)]) });
    assert.equal(result.error, 'model overloaded');
    assert.equal(result.stopReason, 'error');
  });

  it('reports a stream that ends without done', async () => {
    const stream = 'event: text\ndata: {"delta":"partial"}\n\n';
    const result = await askCode({ baseUrl: 'http://x', user: 'alice', question: 'q', chunks: [], fetchImpl: fakeFetch([bytes(stream)]) });
    assert.equal(result.text, 'partial');
    assert.equal(result.error, 'Stream ended without a done event');
  });

  it('throws BackendHttpError on a non-2xx status', async () => {
    await assert.rejects(
      askCode({ baseUrl: 'http://x', user: 'mallory', question: 'q', chunks: [], fetchImpl: fakeFetch([bytes('unknown user')], {}, 401) }),
      (err: unknown) => err instanceof BackendHttpError && err.status === 401 && err.body === 'unknown user',
    );
  });

  it('throws BackendUnreachableError with a start hint when fetch fails', async () => {
    const refused = (async () => {
      throw new TypeError('fetch failed', { cause: { code: 'ECONNREFUSED' } });
    }) as typeof fetch;
    await assert.rejects(
      askCode({ baseUrl: 'http://localhost:5080', user: 'alice', question: 'q', chunks: [], fetchImpl: refused }),
      (err: unknown) => err instanceof BackendUnreachableError && /ECONNREFUSED/.test(err.message) && /dotnet run/.test(err.message),
    );
  });

  it('streams from a real HTTP server sending chunked SSE', async () => {
    let seenUser: string | undefined;
    let seenBody = '';
    const server = http.createServer((req, res) => {
      seenUser = req.headers['x-demo-user'] as string | undefined;
      req.on('data', (d: Buffer) => (seenBody += d.toString()));
      req.on('end', () => {
        res.writeHead(200, { 'Content-Type': 'text/event-stream' });
        // One write per event, with a pause so the client really reads in pieces.
        const events = STREAM.split('\n\n').filter((e) => e.length > 0).map((e) => `${e}\n\n`);
        const next = (): void => {
          const ev = events.shift();
          if (ev === undefined) return void res.end();
          res.write(ev);
          setTimeout(next, 5);
        };
        next();
      });
    });
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    try {
      const { port } = server.address() as AddressInfo;
      const deltas: string[] = [];
      const result = await askCode({ baseUrl: `http://127.0.0.1:${port}`, user: 'carol', question: 'q', chunks, onText: (d) => deltas.push(d) });
      assert.equal(seenUser, 'carol');
      assert.deepEqual(JSON.parse(seenBody), { question: 'q', chunks });
      assert.equal(deltas.length, 2);
      assert.equal(result.text, 'Totals are computed in [src/invoice.ts:10-13].');
      assert.equal(result.stopReason, 'end_turn');
      assert.equal(result.error, undefined);
    } finally {
      server.close();
    }
  });
});
