import { TestBed } from '@angular/core/testing';
import { SqlResult } from './events';
import { ResultsTableComponent } from './results-table.component';

const result: SqlResult = {
  callId: 'c1',
  proposedSql: 'SELECT program, COUNT(*) AS open_critical FROM defects GROUP BY program',
  executedSql: "SELECT program, COUNT(*) AS open_critical FROM defects WHERE business_unit = 'Modem' GROUP BY program",
  columns: ['program', 'open_critical'],
  rows: [
    ['Falcon', 4],
    ['Osprey', null],
  ],
  rowCount: 120,
  truncated: true,
  appliedFilters: ["business_unit = 'Modem'", 'status <> deleted'],
};

describe('ResultsTableComponent', () => {
  it('renders headers, rows, row count, truncation, filters and both SQL variants', () => {
    const fixture = TestBed.createComponent(ResultsTableComponent);
    fixture.componentRef.setInput('result', result);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect([...el.querySelectorAll('th')].map((th) => th.textContent)).toEqual(['program', 'open_critical']);
    const rows = [...el.querySelectorAll('tbody tr')].map((tr) => [...tr.querySelectorAll('td')].map((td) => td.textContent));
    expect(rows).toEqual([
      ['Falcon', '4'],
      ['Osprey', 'NULL'],
    ]);
    expect(el.querySelector('.row-count')?.textContent).toBe('120 rows');
    expect(el.querySelector('.truncated')?.textContent).toContain('truncated');
    expect([...el.querySelectorAll('.chip.filter')].map((c) => c.textContent)).toEqual(result.appliedFilters);
    expect(el.querySelector('pre.executed')?.textContent).toBe(result.executedSql);
    expect(el.querySelector('pre.proposed')?.textContent).toBe(result.proposedSql);
    expect(el.querySelector('details.sql')?.hasAttribute('open')).toBe(false);
  });

  it('omits the truncated note and proposed SQL when not applicable', () => {
    const fixture = TestBed.createComponent(ResultsTableComponent);
    fixture.componentRef.setInput('result', { ...result, truncated: false, proposedSql: result.executedSql, appliedFilters: [] });
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.truncated')).toBeNull();
    expect(el.querySelector('pre.proposed')).toBeNull();
    expect(el.querySelector('.filters')).toBeNull();
  });

  it('renders sql_rejected reasons in an alert box', () => {
    const fixture = TestBed.createComponent(ResultsTableComponent);
    fixture.componentRef.setInput('rejected', {
      callId: 'c2',
      proposedSql: 'DELETE FROM defects',
      reasons: ['Only SELECT statements are allowed', 'Table defects requires a filter'],
    });
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const box = el.querySelector('.rejected[role="alert"]');
    expect(box).not.toBeNull();
    expect([...box!.querySelectorAll('li')].map((li) => li.textContent)).toEqual([
      'Only SELECT statements are allowed',
      'Table defects requires a filter',
    ]);
    expect(el.querySelector('table')).toBeNull();
  });
});
