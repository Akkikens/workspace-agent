import { Injectable, InjectionToken, inject, signal } from '@angular/core';
import { ChatEvent, DemoUser } from './events';
import { flushSse, parseSseChunk, toChatEvent } from './sse';

export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL', {
  providedIn: 'root',
  factory: () => 'http://localhost:5080',
});

/** Used when GET /api/users fails (backend down). Mirrors the contract's demo users. */
export const FALLBACK_USERS: DemoUser[] = [
  { id: 'alice', name: 'Alice Chen', role: 'engineer', businessUnit: 'Modem' },
  { id: 'bob', name: 'Bob Patel', role: 'program_manager', businessUnit: 'Camera' },
  { id: 'carol', name: 'Carol Diaz', role: 'director', businessUnit: null },
];

@Injectable({ providedIn: 'root' })
export class ChatApiService {
  readonly baseUrl = signal(inject(API_BASE_URL).replace(/\/+$/, ''));
  /** Demo identity sent as `X-Demo-User` on every request. */
  readonly userId = signal('alice');

  async users(): Promise<DemoUser[]> {
    const res = await fetch(`${this.baseUrl()}/api/users`, { headers: this.headers() });
    if (!res.ok) throw new Error(`GET /api/users failed: HTTP ${res.status}`);
    return (await res.json()) as DemoUser[];
  }

  /** POST /api/chat; yields parsed SSE events until the server closes the stream. */
  send(message: string, sessionId?: string | null, signal?: AbortSignal): AsyncIterable<ChatEvent> {
    const body: { message: string; sessionId?: string } = { message };
    if (sessionId) body.sessionId = sessionId;
    return this.stream('/api/chat', body, signal);
  }

  /** POST /api/chat/{sessionId}/approvals/{callId}; continues (or ends) the paused run. */
  decide(sessionId: string, callId: string, approved: boolean, signal?: AbortSignal): AsyncIterable<ChatEvent> {
    const path = `/api/chat/${encodeURIComponent(sessionId)}/approvals/${encodeURIComponent(callId)}`;
    return this.stream(path, { approved }, signal);
  }

  private headers(extra: Record<string, string> = {}): Record<string, string> {
    return { 'X-Demo-User': this.userId(), ...extra };
  }

  private async *stream(path: string, body: unknown, signal?: AbortSignal): AsyncGenerator<ChatEvent> {
    const res = await fetch(`${this.baseUrl()}${path}`, {
      method: 'POST',
      headers: this.headers({ 'Content-Type': 'application/json', Accept: 'text/event-stream' }),
      body: JSON.stringify(body),
      signal,
    });
    if (!res.ok || !res.body) {
      throw new Error(`POST ${path} failed: HTTP ${res.status}`);
    }
    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let rest = '';
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        const parsed = parseSseChunk(rest, decoder.decode(value, { stream: true }));
        rest = parsed.rest;
        for (const raw of parsed.events) {
          const ev = toChatEvent(raw);
          if (ev) yield ev;
        }
      }
      const tail = parseSseChunk(rest, decoder.decode());
      for (const raw of [...tail.events, ...flushSse(tail.rest)]) {
        const ev = toChatEvent(raw);
        if (ev) yield ev;
      }
    } finally {
      reader.releaseLock();
    }
  }
}
