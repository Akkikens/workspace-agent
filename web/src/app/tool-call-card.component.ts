import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { ToolCallItem, ToolStatus } from './chat-state';

export interface ToolDecision {
  callId: string;
  approved: boolean;
}

const STATUS_LABELS: Record<ToolStatus, string> = {
  running: 'Running',
  awaiting_approval: 'Awaiting approval',
  approved: 'Approved',
  rejected: 'Rejected',
  ok: 'OK',
  failed: 'Failed',
};

const WRITE_TOOL = /(^|_)(create|update|delete|write|send|post|transition)(_|$)/;

@Component({
  selector: 'app-tool-call-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article class="card" [class.write]="isWrite()" [attr.data-status]="call().status">
      <header>
        <span class="tool">{{ call().tool }}</span>
        @if (isWrite()) {
          <span class="badge write-badge">write action</span>
        }
        <span class="badge status status-{{ call().status }}">{{ statusLabel() }}</span>
      </header>
      @if (prettyArgs(); as args) {
        <pre class="args">{{ args }}</pre>
      }
      @if (call().summary) {
        <p class="summary">{{ call().summary }}</p>
      }
      @if (prettyData(); as data) {
        <details class="data">
          <summary>Result data</summary>
          <pre>{{ data }}</pre>
        </details>
      }
      @if (showButtons()) {
        <div class="actions">
          <button type="button" class="approve" [disabled]="buttonsDisabled()" (click)="choose(true)">Approve</button>
          <button type="button" class="reject" [disabled]="buttonsDisabled()" (click)="choose(false)">Reject</button>
        </div>
      }
    </article>
  `,
  styles: `
    .card { border: 1px solid var(--border); border-radius: 8px; background: var(--surface); padding: 10px 12px; }
    .card.write { border-color: var(--warn-border); background: var(--warn-bg); border-left-width: 4px; }
    header { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
    .tool { font-family: var(--mono); font-weight: 600; font-size: 13px; margin-right: auto; }
    .badge { font-size: 11px; padding: 2px 8px; border-radius: 999px; background: var(--chip); color: var(--muted); }
    .write-badge { background: var(--warn-border); color: #5a3b00; }
    .status-awaiting_approval { background: #fff1c2; color: #6b4e00; }
    .status-ok, .status-approved { background: #dcf3e3; color: #19603a; }
    .status-failed, .status-rejected { background: #fbe0e0; color: #8a1f1f; }
    .args, .data pre { font-family: var(--mono); font-size: 12px; background: var(--code-bg); border-radius: 6px;
      padding: 8px; margin: 8px 0 0; overflow-x: auto; white-space: pre-wrap; word-break: break-word; }
    .summary { margin: 8px 0 0; font-size: 14px; }
    .data { margin-top: 6px; font-size: 13px; }
    .data summary { cursor: pointer; color: var(--muted); }
    .actions { display: flex; gap: 8px; margin-top: 10px; }
    .actions button { padding: 6px 14px; border-radius: 6px; border: 1px solid var(--border); cursor: pointer; font: inherit; }
    .approve { background: var(--accent); color: #fff; border-color: var(--accent) !important; }
    .reject { background: var(--surface); }
    button:disabled { opacity: 0.5; cursor: not-allowed; }
  `,
})
export class ToolCallCardComponent {
  readonly call = input.required<ToolCallItem>();
  readonly decide = output<ToolDecision>();

  /** Set as soon as the user clicks, so a double click can't send two decisions. */
  private readonly decided = signal(false);

  protected readonly isWrite = computed(() => this.call().requiresApproval || WRITE_TOOL.test(this.call().tool));
  protected readonly statusLabel = computed(() => STATUS_LABELS[this.call().status]);
  protected readonly prettyArgs = computed(() => {
    const args = this.call().args;
    return args && Object.keys(args).length ? JSON.stringify(args, null, 2) : '';
  });
  protected readonly prettyData = computed(() => {
    const data = this.call().data;
    return data === undefined || data === null ? '' : JSON.stringify(data, null, 2);
  });
  protected readonly showButtons = computed(() => {
    const { requiresApproval, status } = this.call();
    return requiresApproval && (status === 'awaiting_approval' || this.decided());
  });
  protected readonly buttonsDisabled = computed(() => this.decided() || this.call().status !== 'awaiting_approval');

  protected choose(approved: boolean): void {
    if (this.buttonsDisabled()) return;
    this.decided.set(true);
    this.decide.emit({ callId: this.call().callId, approved });
  }
}
