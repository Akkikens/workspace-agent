import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  PendingTasks,
  afterRenderEffect,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { ChatApiService } from './chat-api.service';
import { ChatState, applyEvent, initialChatState, recordDecision, startTurn } from './chat-state';
import { ChatEvent } from './events';
import { ResultsTableComponent } from './results-table.component';
import { ToolCallCardComponent, ToolDecision } from './tool-call-card.component';

export const SUGGESTIONS: readonly string[] = [
  'How many open critical defects does each program have?',
  'Find Jira issues about modem firmware crashes',
  'Search Confluence for the release checklist',
  "What's on my calendar this week?",
  'Create a Jira issue for the camera ISP regression',
];

@Component({
  selector: 'app-chat',
  imports: [ToolCallCardComponent, ResultsTableComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="transcript" #transcript aria-live="polite">
      @for (item of state().items; track item.id) {
        @switch (item.kind) {
          @case ('user') {
            <div class="msg user">{{ item.text }}</div>
          }
          @case ('assistant') {
            <div class="msg assistant">{{ item.text }}</div>
          }
          @case ('tool') {
            <app-tool-call-card [call]="item" (decide)="decide($event)" />
          }
          @case ('sql') {
            <app-results-table [result]="item.result" />
          }
          @case ('sql_rejected') {
            <app-results-table [rejected]="item.rejected" />
          }
          @case ('error') {
            <div class="msg error" role="alert">{{ item.message }}</div>
          }
        }
      } @empty {
        <div class="empty">
          <p>Ask about defects, Jira, Confluence or your calendar. Answers respect your role's data access.</p>
        </div>
      }
      @if (streaming()) {
        <div class="typing" aria-label="Assistant is working">
          <span></span><span></span><span></span>
        </div>
      }
    </div>

    @if (!state().items.length) {
      <div class="suggestions">
        @for (s of suggestions; track s) {
          <button type="button" class="chip" [disabled]="streaming()" (click)="send(s)">{{ s }}</button>
        }
      </div>
    }

    <form class="composer" (submit)="$event.preventDefault(); submit()">
      <textarea
        rows="1"
        placeholder="Ask the workspace agent..."
        aria-label="Message"
        [value]="draft()"
        (input)="draft.set($any($event.target).value)"
        (keydown.enter)="onEnter($any($event))"
      ></textarea>
      <button type="submit" class="send" [disabled]="!canSend()">Send</button>
    </form>
  `,
  styles: `
    :host { display: flex; flex-direction: column; min-height: 0; flex: 1; }
    .transcript { flex: 1; overflow-y: auto; display: flex; flex-direction: column; gap: 10px; padding: 16px 0; }
    .msg { padding: 10px 14px; border-radius: 12px; max-width: 85%; white-space: pre-wrap; word-break: break-word; line-height: 1.45; }
    .user { align-self: flex-end; background: var(--accent); color: #fff; border-bottom-right-radius: 4px; }
    .assistant { align-self: flex-start; background: var(--surface); border: 1px solid var(--border); border-bottom-left-radius: 4px; }
    .error { align-self: stretch; max-width: none; background: #fdecec; color: #7a1c1c; border: 1px solid #e8a3a3; }
    app-tool-call-card, app-results-table { align-self: stretch; }
    .empty { color: var(--muted); text-align: center; margin: auto 0; padding: 24px 8px; }
    .suggestions { display: flex; flex-wrap: wrap; gap: 8px; padding-bottom: 12px; }
    .chip { font: inherit; font-size: 13px; padding: 6px 12px; border-radius: 999px; border: 1px solid var(--border);
      background: var(--surface); color: var(--text); cursor: pointer; text-align: left; }
    .chip:hover:not(:disabled) { border-color: var(--accent); }
    .composer { display: flex; gap: 8px; padding: 12px 0 16px; border-top: 1px solid var(--border); }
    textarea { flex: 1; font: inherit; resize: vertical; min-height: 40px; max-height: 200px; padding: 9px 12px;
      border: 1px solid var(--border); border-radius: 8px; background: var(--surface); color: var(--text); }
    textarea:focus { outline: 2px solid var(--accent); outline-offset: -1px; }
    .send { font: inherit; padding: 0 18px; border-radius: 8px; border: none; background: var(--accent); color: #fff; cursor: pointer; }
    .send:disabled { opacity: 0.5; cursor: not-allowed; }
    .typing { display: flex; gap: 4px; padding: 4px 6px; }
    .typing span { width: 6px; height: 6px; border-radius: 50%; background: var(--muted); animation: blink 1.2s infinite ease-in-out; }
    .typing span:nth-child(2) { animation-delay: 0.2s; }
    .typing span:nth-child(3) { animation-delay: 0.4s; }
    @keyframes blink { 0%, 80%, 100% { opacity: 0.25; } 40% { opacity: 1; } }
  `,
})
export class ChatComponent {
  private readonly api = inject(ChatApiService);
  private readonly pendingTasks = inject(PendingTasks);
  private readonly transcriptEl = viewChild<ElementRef<HTMLElement>>('transcript');
  private abort: AbortController | null = null;

  readonly state = signal<ChatState>(initialChatState);
  readonly streaming = computed(() => this.state().streaming);
  readonly draft = signal('');
  protected readonly canSend = computed(() => !this.streaming() && this.draft().trim().length > 0);
  protected readonly suggestions = SUGGESTIONS;

  constructor() {
    afterRenderEffect(() => {
      this.state();
      const el = this.transcriptEl()?.nativeElement;
      if (el) el.scrollTop = el.scrollHeight;
    });
  }

  protected submit(): void {
    if (this.canSend()) void this.send(this.draft());
  }

  protected onEnter(e: KeyboardEvent): void {
    if (e.shiftKey) return;
    e.preventDefault();
    this.submit();
  }

  async send(message: string): Promise<void> {
    const text = message.trim();
    if (!text || this.streaming()) return;
    this.draft.set('');
    this.state.update((s) => startTurn(s, text));
    await this.consume((signal) => this.api.send(text, this.state().sessionId, signal));
  }

  async decide({ callId, approved }: ToolDecision): Promise<void> {
    const sessionId = this.state().sessionId;
    if (!sessionId || this.streaming()) return;
    this.state.update((s) => recordDecision(s, callId, approved));
    await this.consume((signal) => this.api.decide(sessionId, callId, approved, signal));
  }

  /** Clears the conversation (e.g. when the demo user changes) and cancels any live stream. */
  reset(): void {
    this.abort?.abort();
    this.abort = null;
    this.state.set(initialChatState);
    this.draft.set('');
  }

  private async consume(open: (signal: AbortSignal) => AsyncIterable<ChatEvent>): Promise<void> {
    const abort = new AbortController();
    this.abort = abort;
    const removeTask = this.pendingTasks.add();
    try {
      for await (const ev of open(abort.signal)) {
        if (abort.signal.aborted) return;
        this.state.update((s) => applyEvent(s, ev));
      }
      if (this.state().streaming) {
        // Stream closed without a `done` event; don't leave the UI stuck.
        this.state.update((s) => applyEvent(s, { type: 'done', data: { stopReason: 'end_turn' } }));
      }
    } catch (err) {
      if (abort.signal.aborted) return;
      const message = err instanceof Error ? err.message : String(err);
      this.state.update((s) =>
        applyEvent(applyEvent(s, { type: 'error', data: { message: `Connection error: ${message}` } }), {
          type: 'done',
          data: { stopReason: 'error' },
        }),
      );
    } finally {
      if (this.abort === abort) this.abort = null;
      removeTask();
    }
  }
}
