import { TestBed } from '@angular/core/testing';
import { ToolCallItem } from './chat-state';
import { ToolCallCardComponent, ToolDecision } from './tool-call-card.component';

function setup(call: Partial<ToolCallItem> = {}) {
  const fixture = TestBed.createComponent(ToolCallCardComponent);
  const item: ToolCallItem = {
    kind: 'tool',
    id: 1,
    callId: 'c1',
    tool: 'jira_create_issue',
    args: { project: 'CAM', summary: 'ISP regression' },
    requiresApproval: true,
    status: 'awaiting_approval',
    ...call,
  };
  fixture.componentRef.setInput('call', item);
  const emitted: ToolDecision[] = [];
  fixture.componentInstance.decide.subscribe((d) => emitted.push(d));
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const button = (cls: string) => el.querySelector<HTMLButtonElement>(`button.${cls}`);
  return { fixture, el, emitted, button };
}

describe('ToolCallCardComponent', () => {
  it('shows tool name, pretty args, status and marks write actions', () => {
    const { el } = setup();
    expect(el.querySelector('.tool')?.textContent).toBe('jira_create_issue');
    expect(el.querySelector('.args')?.textContent).toContain('"project": "CAM"');
    expect(el.querySelector('.status')?.textContent).toContain('Awaiting approval');
    expect(el.querySelector('.card')?.classList).toContain('write');
  });

  it('emits approve with the callId and disables both buttons afterwards', () => {
    const { fixture, emitted, button } = setup();
    button('approve')!.click();
    fixture.detectChanges();
    expect(emitted).toEqual([{ callId: 'c1', approved: true }]);
    expect(button('approve')!.disabled).toBe(true);
    expect(button('reject')!.disabled).toBe(true);
    button('reject')!.click();
    expect(emitted).toHaveLength(1);
  });

  it('emits reject', () => {
    const { emitted, button } = setup();
    button('reject')!.click();
    expect(emitted).toEqual([{ callId: 'c1', approved: false }]);
  });

  it('shows no buttons for a read-only call and renders the result summary', () => {
    const { el } = setup({ tool: 'jira_search', requiresApproval: false, status: 'ok', summary: '2 issues found' });
    expect(el.querySelector('button')).toBeNull();
    expect(el.querySelector('.card')?.classList).not.toContain('write');
    expect(el.querySelector('.summary')?.textContent).toBe('2 issues found');
    expect(el.querySelector('.status')?.textContent).toContain('OK');
  });
});
