import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SqlRejected, SqlResult } from './events';

/** Renders a `sql_result` (table + SQL + row-level-security filters) or a `sql_rejected` event. */
@Component({
  selector: 'app-results-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (rejected(); as rej) {
      <div class="rejected" role="alert">
        <strong>SQL rejected by the guard</strong>
        <ul>
          @for (reason of rej.reasons; track $index) {
            <li>{{ reason }}</li>
          }
        </ul>
        <details>
          <summary>Proposed SQL</summary>
          <pre>{{ rej.proposedSql }}</pre>
        </details>
      </div>
    }
    @if (result(); as r) {
      <section class="result">
        @if (r.appliedFilters.length) {
          <div class="filters">
            <span class="label">Row-level security:</span>
            @for (f of r.appliedFilters; track $index) {
              <span class="chip filter">{{ f }}</span>
            }
          </div>
        }
        <div class="scroll">
          <table>
            <thead>
              <tr>
                @for (c of r.columns; track $index) {
                  <th>{{ c }}</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (row of r.rows; track $index) {
                <tr>
                  @for (cell of row; track $index) {
                    <td [class.null]="cell === null" [class.num]="isNumber(cell)">{{ cell === null ? 'NULL' : cell }}</td>
                  }
                </tr>
              } @empty {
                <tr><td class="empty" [attr.colspan]="r.columns.length || 1">No rows</td></tr>
              }
            </tbody>
          </table>
        </div>
        <p class="meta">
          <span class="row-count">{{ r.rowCount }} {{ r.rowCount === 1 ? 'row' : 'rows' }}</span>
          @if (r.truncated) {
            <span class="truncated">Showing the first {{ r.rows.length }}; results truncated.</span>
          }
        </p>
        <details class="sql">
          <summary>Executed SQL</summary>
          <pre class="executed">{{ r.executedSql }}</pre>
        </details>
        @if (sqlDiffers()) {
          <details class="sql">
            <summary>Proposed SQL (before row-level filters)</summary>
            <pre class="proposed">{{ r.proposedSql }}</pre>
          </details>
        }
      </section>
    }
  `,
  styles: `
    :host { display: block; }
    .result { border: 1px solid var(--border); border-radius: 8px; background: var(--surface); padding: 10px 12px; }
    .filters { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin-bottom: 8px; font-size: 12px; }
    .label { color: var(--muted); }
    .chip.filter { font-family: var(--mono); background: #e5edff; color: #1d3a8a; padding: 2px 8px; border-radius: 999px; }
    .scroll { overflow-x: auto; }
    table { border-collapse: collapse; width: 100%; font-size: 13px; }
    th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid var(--border); white-space: nowrap; }
    th { background: var(--code-bg); font-weight: 600; }
    td.num { text-align: right; font-variant-numeric: tabular-nums; }
    td.null, td.empty { color: var(--muted); font-style: italic; }
    .meta { display: flex; flex-wrap: wrap; gap: 12px; font-size: 12px; color: var(--muted); margin: 8px 0 4px; }
    .truncated { color: #8a5a00; }
    details { font-size: 13px; margin-top: 4px; }
    summary { cursor: pointer; color: var(--muted); }
    pre { font-family: var(--mono); font-size: 12px; background: var(--code-bg); border-radius: 6px; padding: 8px;
      margin: 6px 0 0; overflow-x: auto; white-space: pre-wrap; word-break: break-word; }
    .rejected { border: 1px solid #e8a3a3; background: #fdecec; color: #7a1c1c; border-radius: 8px; padding: 10px 12px; }
    .rejected ul { margin: 6px 0; padding-left: 20px; }
  `,
})
export class ResultsTableComponent {
  readonly result = input<SqlResult | null>(null);
  readonly rejected = input<SqlRejected | null>(null);

  protected readonly sqlDiffers = computed(() => {
    const r = this.result();
    return !!r && r.proposedSql.trim() !== r.executedSql.trim();
  });

  protected isNumber(v: unknown): boolean {
    return typeof v === 'number';
  }
}
