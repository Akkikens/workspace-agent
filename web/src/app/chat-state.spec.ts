import { ChatState, ToolCallItem, applyEvent, initialChatState, recordDecision, startTurn } from './chat-state';
import { ChatEvent, SqlResult } from './events';

const reduce = (state: ChatState, events: ChatEvent[]) => events.reduce(applyEvent, state);
const tool = (s: ChatState, callId: string) =>
  s.items.find((i): i is ToolCallItem => i.kind === 'tool' && i.callId === callId)!;

const sql: SqlResult = {
  callId: 'c9',
  proposedSql: 'SELECT program, COUNT(*) FROM defects GROUP BY program',
  executedSql: "SELECT program, COUNT(*) FROM defects WHERE business_unit = 'Modem' GROUP BY program",
  columns: ['program', 'count'],
  rows: [['Falcon', 3]],
  rowCount: 1,
  truncated: false,
  appliedFilters: ["business_unit = 'Modem'"],
};

describe('applyEvent reducer', () => {
  it('startTurn adds the user message and sets streaming; session sets sessionId', () => {
    const s = applyEvent(startTurn(initialChatState, 'hi'), { type: 'session', data: { sessionId: 's1' } });
    expect(s.items).toEqual([{ kind: 'user', id: 1, text: 'hi' }]);
    expect(s.streaming).toBe(true);
    expect(s.sessionId).toBe('s1');
  });

  it('appends consecutive text deltas to the same assistant message', () => {
    const s = reduce(startTurn(initialChatState, 'q'), [
      { type: 'text', data: { delta: 'Hel' } },
      { type: 'text', data: { delta: 'lo' } },
      { type: 'text', data: { delta: ' there' } },
    ]);
    expect(s.items).toHaveLength(2);
    expect(s.items[1]).toMatchObject({ kind: 'assistant', text: 'Hello there' });
  });

  it('starts a new assistant message when text follows a tool card', () => {
    const s = reduce(initialChatState, [
      { type: 'text', data: { delta: 'Looking...' } },
      { type: 'tool_call', data: { callId: 'c1', tool: 'jira_search', args: { q: 'x' }, requiresApproval: false } },
      { type: 'text', data: { delta: 'Found 2.' } },
    ]);
    expect(s.items.map((i) => i.kind)).toEqual(['assistant', 'tool', 'assistant']);
    expect(s.items[2]).toMatchObject({ text: 'Found 2.' });
  });

  it('tool_call then approval_required marks the same card as awaiting approval', () => {
    const args = { project: 'CAM', summary: 'ISP regression' };
    const s = reduce(initialChatState, [
      { type: 'tool_call', data: { callId: 'c2', tool: 'jira_create_issue', args, requiresApproval: true } },
      { type: 'approval_required', data: { callId: 'c2', tool: 'jira_create_issue', args } },
    ]);
    expect(s.items.filter((i) => i.kind === 'tool')).toHaveLength(1);
    expect(tool(s, 'c2')).toMatchObject({ status: 'awaiting_approval', requiresApproval: true, args });
  });

  it('tool_result updates the card status and summary', () => {
    const base = applyEvent(initialChatState, {
      type: 'tool_call',
      data: { callId: 'c3', tool: 'confluence_search', args: {}, requiresApproval: false },
    });
    expect(tool(base, 'c3').status).toBe('running');
    const ok = applyEvent(base, {
      type: 'tool_result',
      data: { callId: 'c3', tool: 'confluence_search', ok: true, summary: '3 pages', data: [{ id: 1 }] },
    });
    expect(tool(ok, 'c3')).toMatchObject({ status: 'ok', summary: '3 pages', data: [{ id: 1 }] });
    const failed = applyEvent(base, {
      type: 'tool_result',
      data: { callId: 'c3', tool: 'confluence_search', ok: false, summary: 'timeout', data: null },
    });
    expect(tool(failed, 'c3')).toMatchObject({ status: 'failed', summary: 'timeout' });
  });

  it('recordDecision marks approved/rejected and a later result keeps "rejected"', () => {
    const awaiting = applyEvent(initialChatState, {
      type: 'approval_required',
      data: { callId: 'c4', tool: 'jira_create_issue', args: {} },
    });
    expect(tool(recordDecision(awaiting, 'c4', true), 'c4').status).toBe('approved');
    const rejected = recordDecision(awaiting, 'c4', false);
    expect(rejected.streaming).toBe(true);
    const after = applyEvent(rejected, {
      type: 'tool_result',
      data: { callId: 'c4', tool: 'jira_create_issue', ok: false, summary: 'Rejected by user', data: null },
    });
    expect(tool(after, 'c4')).toMatchObject({ status: 'rejected', summary: 'Rejected by user' });
  });

  it('sql_result, sql_rejected and error each add an item', () => {
    const s = reduce(initialChatState, [
      { type: 'sql_result', data: sql },
      { type: 'sql_rejected', data: { callId: 'c10', proposedSql: 'DROP TABLE x', reasons: ['Only SELECT is allowed'] } },
      { type: 'error', data: { message: 'boom' } },
    ]);
    expect(s.items.map((i) => i.kind)).toEqual(['sql', 'sql_rejected', 'error']);
    expect(s.items[0]).toMatchObject({ result: sql });
  });

  it('done clears streaming and records the stop reason without mutating input state', () => {
    const streaming = startTurn(initialChatState, 'q');
    const done = applyEvent(streaming, { type: 'done', data: { stopReason: 'awaiting_approval' } });
    expect(done.streaming).toBe(false);
    expect(done.stopReason).toBe('awaiting_approval');
    expect(streaming.streaming).toBe(true);
    expect(initialChatState.items).toEqual([]);
  });
});
