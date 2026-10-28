// Types mirroring docs/api-contract.md. Keep in sync with the backend.

export interface DemoUser {
  id: string;
  name: string;
  role: string;
  businessUnit: string | null;
}

export type StopReason = 'end_turn' | 'awaiting_approval' | 'max_steps' | 'error';

export interface SqlResult {
  callId: string;
  proposedSql: string;
  executedSql: string;
  columns: string[];
  rows: (string | number | null)[][];
  rowCount: number;
  truncated: boolean;
  appliedFilters: string[];
}

export interface SqlRejected {
  callId: string;
  proposedSql: string;
  reasons: string[];
}

export type ChatEvent =
  | { type: 'session'; data: { sessionId: string } }
  | { type: 'text'; data: { delta: string } }
  | {
      type: 'tool_call';
      data: { callId: string; tool: string; args: Record<string, unknown>; requiresApproval: boolean };
    }
  | { type: 'approval_required'; data: { callId: string; tool: string; args: Record<string, unknown> } }
  | {
      type: 'tool_result';
      data: { callId: string; tool: string; ok: boolean; summary: string; data: unknown | null };
    }
  | { type: 'sql_result'; data: SqlResult }
  | { type: 'sql_rejected'; data: SqlRejected }
  | { type: 'error'; data: { message: string } }
  | { type: 'done'; data: { stopReason: StopReason } };

export type ChatEventType = ChatEvent['type'];

export const CHAT_EVENT_TYPES: readonly ChatEventType[] = [
  'session',
  'text',
  'tool_call',
  'approval_required',
  'tool_result',
  'sql_result',
  'sql_rejected',
  'error',
  'done',
];
