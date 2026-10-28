import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AppComponent } from './app';
import { ChatApiService, FALLBACK_USERS } from './chat-api.service';
import { ChatEvent, DemoUser } from './events';

async function* stream(events: ChatEvent[]): AsyncGenerator<ChatEvent> {
  for (const e of events) {
    await Promise.resolve();
    yield e;
  }
}

const createArgs = { project: 'CAM', summary: 'Camera ISP regression' };

class FakeChatApi {
  readonly userId = signal('alice');
  readonly baseUrl = signal('http://fake');
  usersResult: Promise<DemoUser[]> = Promise.resolve([
    { id: 'alice', name: 'Alice Chen', role: 'engineer', businessUnit: 'Modem' },
    { id: 'bob', name: 'Bob Patel', role: 'program_manager', businessUnit: 'Camera' },
  ]);
  sendCalls: { message: string; sessionId?: string | null }[] = [];
  decideCalls: { sessionId: string; callId: string; approved: boolean }[] = [];

  users() {
    return this.usersResult;
  }
  send(message: string, sessionId?: string | null) {
    this.sendCalls.push({ message, sessionId });
    return stream([
      { type: 'session', data: { sessionId: 'sess-1' } },
      { type: 'text', data: { delta: 'I will ' } },
      { type: 'text', data: { delta: 'create it.' } },
      { type: 'tool_call', data: { callId: 'call-7', tool: 'jira_create_issue', args: createArgs, requiresApproval: true } },
      { type: 'approval_required', data: { callId: 'call-7', tool: 'jira_create_issue', args: createArgs } },
      { type: 'done', data: { stopReason: 'awaiting_approval' } },
    ]);
  }
  decide(sessionId: string, callId: string, approved: boolean) {
    this.decideCalls.push({ sessionId, callId, approved });
    return stream([
      { type: 'session', data: { sessionId: 'sess-1' } },
      { type: 'tool_result', data: { callId, tool: 'jira_create_issue', ok: true, summary: 'Created CAM-42', data: { key: 'CAM-42' } } },
      { type: 'text', data: { delta: 'Done: CAM-42.' } },
      { type: 'done', data: { stopReason: 'end_turn' } },
    ]);
  }
}

describe('AppComponent (integration with a fake ChatApiService)', () => {
  let api: FakeChatApi;
  let fixture: ComponentFixture<AppComponent>;
  let el: HTMLElement;
  const $ = <T extends Element = HTMLElement>(sel: string) => el.querySelector<T>(sel);
  const $$ = (sel: string) => [...el.querySelectorAll<HTMLElement>(sel)];

  async function create() {
    await TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [{ provide: ChatApiService, useValue: api }],
    }).compileComponents();
    fixture = TestBed.createComponent(AppComponent);
    el = fixture.nativeElement;
    await fixture.whenStable();
  }

  beforeEach(() => {
    api = new FakeChatApi();
  });

  it('runs a full turn with an approval round-trip', async () => {
    await create();
    expect($$('.suggestions .chip')).toHaveLength(5);

    const textarea = $<HTMLTextAreaElement>('textarea')!;
    textarea.value = 'Create a Jira issue for the camera ISP regression';
    textarea.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    $<HTMLButtonElement>('button.send')!.click();
    await fixture.whenStable();

    expect(api.sendCalls).toEqual([{ message: 'Create a Jira issue for the camera ISP regression', sessionId: null }]);
    expect($('.msg.user')?.textContent).toBe('Create a Jira issue for the camera ISP regression');
    expect($$('.msg.assistant').map((m) => m.textContent)).toEqual(['I will create it.']);
    expect($('app-tool-call-card .status')?.textContent).toContain('Awaiting approval');
    expect($('app-tool-call-card .card')?.classList).toContain('write');
    expect($<HTMLButtonElement>('button.send')!.disabled).toBe(true); // empty draft

    $<HTMLButtonElement>('app-tool-call-card button.approve')!.click();
    await fixture.whenStable();

    expect(api.decideCalls).toEqual([{ sessionId: 'sess-1', callId: 'call-7', approved: true }]);
    expect($('app-tool-call-card .status')?.textContent).toContain('OK');
    expect($('app-tool-call-card .summary')?.textContent).toBe('Created CAM-42');
    expect($<HTMLButtonElement>('app-tool-call-card button.approve')!.disabled).toBe(true);
    expect($$('.msg.assistant').map((m) => m.textContent)).toEqual(['I will create it.', 'Done: CAM-42.']);
    expect($('.typing')).toBeNull();
  });

  it('disables send while streaming and reuses the session id on the next turn', async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    api.send = (message: string, sessionId?: string | null) => {
      api.sendCalls.push({ message, sessionId });
      return (async function* () {
        yield { type: 'session', data: { sessionId: 'sess-9' } } as ChatEvent;
        await gate;
        yield { type: 'done', data: { stopReason: 'end_turn' } } as ChatEvent;
      })();
    };
    await create();
    $$('.suggestions .chip')[2].click();
    await Promise.resolve();
    await Promise.resolve();
    fixture.detectChanges();
    expect($('.typing')).not.toBeNull();
    const textarea = $<HTMLTextAreaElement>('textarea')!;
    textarea.value = 'next';
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect($<HTMLButtonElement>('button.send')!.disabled).toBe(true);
    expect($<HTMLSelectElement>('select')!.disabled).toBe(true);

    release();
    await fixture.whenStable();
    expect($<HTMLButtonElement>('button.send')!.disabled).toBe(false);
    $<HTMLButtonElement>('button.send')!.click();
    await fixture.whenStable();
    expect(api.sendCalls.map((c) => c.sessionId)).toEqual([null, 'sess-9']);
    expect(api.sendCalls[0].message).toBe('Search Confluence for the release checklist');
  });

  it('falls back to the static demo users when /api/users fails, and switching user resets the chat', async () => {
    api.usersResult = Promise.reject(new Error('ECONNREFUSED'));
    await create();
    expect($$('option').map((o) => o.textContent)).toEqual(FALLBACK_USERS.map((u) => u.name));
    expect($('.offline')).not.toBeNull();

    $$('.suggestions .chip')[0].click();
    await fixture.whenStable();
    expect($$('.msg').length).toBeGreaterThan(0);

    const select = $<HTMLSelectElement>('select')!;
    select.value = 'bob';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(api.userId()).toBe('bob');
    expect($$('.msg')).toHaveLength(0);
    expect($('.role')?.textContent).toContain('Camera');
  });

  it('shows a connection error and stops streaming when the request fails', async () => {
    api.send = () => {
      return (async function* (): AsyncGenerator<ChatEvent> {
        throw new Error('POST /api/chat failed: HTTP 500');
      })();
    };
    await create();
    $$('.suggestions .chip')[3].click();
    await fixture.whenStable();
    expect($('.msg.error')?.textContent).toContain('HTTP 500');
    expect($('.typing')).toBeNull();
  });
});
