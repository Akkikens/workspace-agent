import { TestBed } from '@angular/core/testing';
import { API_BASE_URL, ChatApiService } from './chat-api.service';
import { ChatEvent } from './events';

function sseResponse(chunks: string[]) {
  const enc = new TextEncoder();
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const c of chunks) controller.enqueue(enc.encode(c));
      controller.close();
    },
  });
  return { ok: true, status: 200, body } as unknown as Response;
}

async function collect(it: AsyncIterable<ChatEvent>) {
  const out: ChatEvent[] = [];
  for await (const e of it) out.push(e);
  return out;
}

describe('ChatApiService', () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  let api: ChatApiService;

  beforeEach(() => {
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    TestBed.configureTestingModule({ providers: [{ provide: API_BASE_URL, useValue: 'http://api.test/' }] });
    api = TestBed.inject(ChatApiService);
  });

  afterEach(() => vi.unstubAllGlobals());

  it('send() POSTs to /api/chat with X-Demo-User and yields events split across byte chunks', async () => {
    // "é" is two bytes in UTF-8; split it to exercise TextDecoder streaming too.
    const payload = 'event: session\ndata: {"sessionId":"s1"}\n\nevent: text\ndata: {"delta":"café"}\n\nevent: done\ndata: {"stopReason":"end_turn"}\n\n';
    const bytes = new TextEncoder().encode(payload);
    const cut = bytes.indexOf(0xc3) + 1;
    fetchMock.mockResolvedValue({
      ok: true,
      status: 200,
      body: new ReadableStream<Uint8Array>({
        start(c) {
          c.enqueue(bytes.slice(0, cut));
          c.enqueue(bytes.slice(cut));
          c.close();
        },
      }),
    } as unknown as Response);

    api.userId.set('bob');
    const events = await collect(api.send('hello', 's0'));

    expect(events).toEqual([
      { type: 'session', data: { sessionId: 's1' } },
      { type: 'text', data: { delta: 'café' } },
      { type: 'done', data: { stopReason: 'end_turn' } },
    ]);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('http://api.test/api/chat');
    expect(init.method).toBe('POST');
    expect(init.headers['X-Demo-User']).toBe('bob');
    expect(JSON.parse(init.body)).toEqual({ message: 'hello', sessionId: 's0' });
  });

  it('decide() POSTs { approved } to the approvals endpoint', async () => {
    fetchMock.mockResolvedValue(sseResponse(['event: done\ndata: {"stopReason":"end_turn"}\n\n']));
    const events = await collect(api.decide('s 1', 'call/7', false));
    expect(events).toEqual([{ type: 'done', data: { stopReason: 'end_turn' } }]);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('http://api.test/api/chat/s%201/approvals/call%2F7');
    expect(JSON.parse(init.body)).toEqual({ approved: false });
  });

  it('throws on a non-2xx response', async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 401, body: null } as unknown as Response);
    await expect(collect(api.send('x'))).rejects.toThrow('HTTP 401');
  });
});
