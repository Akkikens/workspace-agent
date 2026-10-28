// Client for the Workspace Agent backend (see docs/api-contract.md). Pure: no `vscode` import.

export interface ContextChunk {
  path: string;
  startLine: number;
  endLine: number;
  text: string;
}

export interface SseEvent {
  event: string;
  data: string;
}

/** Incremental SSE parser: feed it decoded text in arbitrary pieces, get complete events back. */
export class SseParser {
  private buffer = '';
  private eventType = '';
  private dataLines: string[] = [];

  feed(chunk: string): SseEvent[] {
    this.buffer += chunk;
    const out: SseEvent[] = [];
    for (;;) {
      const m = /\r\n|\r|\n/.exec(this.buffer);
      if (!m) break;
      // A trailing lone CR may be the first half of a CRLF split across chunks.
      if (m[0] === '\r' && m.index === this.buffer.length - 1) break;
      const line = this.buffer.slice(0, m.index);
      this.buffer = this.buffer.slice(m.index + m[0].length);
      const ev = this.processLine(line);
      if (ev) out.push(ev);
    }
    return out;
  }

  /** Call at end of stream: dispatches a final event that lacked its terminating blank line. */
  flush(): SseEvent[] {
    const out: SseEvent[] = [];
    if (this.buffer.length > 0) {
      const ev = this.processLine(this.buffer.replace(/\r$/, ''));
      this.buffer = '';
      if (ev) out.push(ev);
    }
    const last = this.dispatch();
    if (last) out.push(last);
    return out;
  }

  private processLine(line: string): SseEvent | undefined {
    if (line === '') return this.dispatch();
    if (line.startsWith(':')) return undefined;
    const colon = line.indexOf(':');
    const field = colon === -1 ? line : line.slice(0, colon);
    let value = colon === -1 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) value = value.slice(1);
    if (field === 'event') this.eventType = value;
    else if (field === 'data') this.dataLines.push(value);
    return undefined;
  }

  private dispatch(): SseEvent | undefined {
    if (this.dataLines.length === 0) {
      this.eventType = '';
      return undefined;
    }
    const ev = { event: this.eventType || 'message', data: this.dataLines.join('\n') };
    this.eventType = '';
    this.dataLines = [];
    return ev;
  }
}

export const BACKEND_START_HINT = 'dotnet run --project backend/src/WorkspaceAgent.Api';

export class BackendUnreachableError extends Error {
  constructor(
    readonly baseUrl: string,
    cause: unknown,
  ) {
    const detail = describeCause(cause);
    super(`Cannot reach the Workspace Agent backend at ${baseUrl}${detail ? ` (${detail})` : ''}. Start it with: ${BACKEND_START_HINT}`);
    this.name = 'BackendUnreachableError';
  }
}

export class BackendHttpError extends Error {
  constructor(
    readonly status: number,
    readonly body: string,
  ) {
    super(`Backend returned HTTP ${status}${body ? `: ${body.slice(0, 300)}` : ''}`);
    this.name = 'BackendHttpError';
  }
}

function describeCause(cause: unknown): string {
  if (!(cause instanceof Error)) return cause === undefined ? '' : String(cause);
  const inner = (cause as { cause?: unknown }).cause;
  const code = inner && typeof inner === 'object' && 'code' in inner ? String((inner as { code: unknown }).code) : '';
  return code || cause.message;
}

type FetchLike = typeof fetch;

export interface AskCodeRequest {
  baseUrl: string;
  user: string;
  question: string;
  chunks: ContextChunk[];
  onText?: (delta: string) => void;
  onEvent?: (event: string, data: unknown) => void;
  signal?: AbortSignal;
  fetchImpl?: FetchLike;
}

export interface AskCodeResult {
  sessionId?: string;
  text: string;
  stopReason?: string;
  /** Joined messages of `error` events (or a missing `done`), if any. */
  error?: string;
}

function joinUrl(base: string, path: string): string {
  return `${base.replace(/\/+$/, '')}${path}`;
}

function parseJson(data: string): unknown {
  try {
    return JSON.parse(data);
  } catch {
    return undefined;
  }
}

function field(obj: unknown, key: string): unknown {
  return obj && typeof obj === 'object' ? (obj as Record<string, unknown>)[key] : undefined;
}

async function send(url: string, init: RequestInit, baseUrl: string, fetchImpl: FetchLike): Promise<Response> {
  let res: Response;
  try {
    res = await fetchImpl(url, init);
  } catch (err) {
    if (init.signal?.aborted) throw err;
    throw new BackendUnreachableError(baseUrl, err);
  }
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new BackendHttpError(res.status, body);
  }
  return res;
}

/** `POST /api/code/ask` and consume the SSE stream, accumulating `text` deltas. */
export async function askCode(req: AskCodeRequest): Promise<AskCodeResult> {
  const fetchImpl = req.fetchImpl ?? fetch;
  const res = await send(
    joinUrl(req.baseUrl, '/api/code/ask'),
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream', 'X-Demo-User': req.user },
      body: JSON.stringify({ question: req.question, chunks: req.chunks }),
      signal: req.signal,
    },
    req.baseUrl,
    fetchImpl,
  );
  if (!res.body) throw new Error('Backend returned an empty response body');

  const result: AskCodeResult = { text: '' };
  const errors: string[] = [];
  const handle = (ev: SseEvent): void => {
    const data = parseJson(ev.data);
    switch (ev.event) {
      case 'session': {
        const id = field(data, 'sessionId');
        if (typeof id === 'string') result.sessionId = id;
        break;
      }
      case 'text': {
        const delta = field(data, 'delta');
        if (typeof delta === 'string' && delta.length > 0) {
          result.text += delta;
          req.onText?.(delta);
        }
        break;
      }
      case 'error': {
        const message = field(data, 'message');
        errors.push(typeof message === 'string' ? message : ev.data);
        break;
      }
      case 'done': {
        const reason = field(data, 'stopReason');
        if (typeof reason === 'string') result.stopReason = reason;
        break;
      }
    }
    req.onEvent?.(ev.event, data);
  };

  const parser = new SseParser();
  const decoder = new TextDecoder();
  const reader = res.body.getReader();
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      parser.feed(decoder.decode(value, { stream: true })).forEach(handle);
    }
  } catch (err) {
    if (req.signal?.aborted) throw err;
    throw new Error(`Backend stream interrupted: ${describeCause(err)}`);
  }
  parser.feed(decoder.decode()).forEach(handle);
  parser.flush().forEach(handle);

  if (!result.stopReason && errors.length === 0) errors.push('Stream ended without a done event');
  if (errors.length > 0) result.error = errors.join('; ');
  return result;
}

export interface HealthResult {
  status: string;
  model?: string;
}

/** `GET /api/health`. */
export async function checkHealth(baseUrl: string, user: string, fetchImpl: FetchLike = fetch, timeoutMs = 3000): Promise<HealthResult> {
  const res = await send(
    joinUrl(baseUrl, '/api/health'),
    { headers: { 'X-Demo-User': user }, signal: AbortSignal.timeout(timeoutMs) },
    baseUrl,
    fetchImpl,
  );
  const json = (await res.json()) as unknown;
  const status = field(json, 'status');
  const model = field(json, 'model');
  return { status: typeof status === 'string' ? status : 'unknown', model: typeof model === 'string' ? model : undefined };
}
