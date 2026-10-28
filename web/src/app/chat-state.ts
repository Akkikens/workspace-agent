import { ChatEvent, SqlRejected, SqlResult, StopReason } from './events';

export type ToolStatus = 'running' | 'awaiting_approval' | 'approved' | 'rejected' | 'ok' | 'failed';

export interface ToolCallItem {
  kind: 'tool';
  id: number;
  callId: string;
  tool: string;
  args: Record<string, unknown>;
  requiresApproval: boolean;
  status: ToolStatus;
  summary?: string;
  data?: unknown;
}

export type ChatItem =
  | { kind: 'user'; id: number; text: string }
  | { kind: 'assistant'; id: number; text: string }
  | ToolCallItem
  | { kind: 'sql'; id: number; result: SqlResult }
  | { kind: 'sql_rejected'; id: number; rejected: SqlRejected }
  | { kind: 'error'; id: number; message: string };

export interface ChatState {
  items: ChatItem[];
  sessionId: string | null;
  streaming: boolean;
  stopReason: StopReason | null;
  /** Monotonic id source so the reducer stays pure (stable `@for` tracking). */
  nextId: number;
}

export const initialChatState: ChatState = {
  items: [],
  sessionId: null,
  streaming: false,
  stopReason: null,
  nextId: 1,
};

type NewItem = DistributiveOmit<ChatItem, 'id'>;
type DistributiveOmit<T, K extends PropertyKey> = T extends unknown ? Omit<T, K> : never;

function push(state: ChatState, item: NewItem): ChatState {
  return {
    ...state,
    items: [...state.items, { ...item, id: state.nextId } as ChatItem],
    nextId: state.nextId + 1,
  };
}

function updateTool(state: ChatState, callId: string, fn: (t: ToolCallItem) => ToolCallItem): ChatState {
  // Search from the end: the latest card with this callId is the live one.
  for (let i = state.items.length - 1; i >= 0; i--) {
    const it = state.items[i];
    if (it.kind === 'tool' && it.callId === callId) {
      const items = state.items.slice();
      items[i] = fn(it);
      return { ...state, items };
    }
  }
  return state;
}

function hasTool(state: ChatState, callId: string): boolean {
  return state.items.some((it) => it.kind === 'tool' && it.callId === callId);
}

/** The user sent a message: add it and start streaming. */
export function startTurn(state: ChatState, message: string): ChatState {
  return { ...push(state, { kind: 'user', text: message }), streaming: true, stopReason: null };
}

/** The user approved/rejected a call: record it locally and start streaming the continuation. */
export function recordDecision(state: ChatState, callId: string, approved: boolean): ChatState {
  const next = updateTool(state, callId, (t) => ({ ...t, status: approved ? 'approved' : 'rejected' }));
  return { ...next, streaming: true, stopReason: null };
}

/** Pure reducer: applies one server event to the chat state. */
export function applyEvent(state: ChatState, event: ChatEvent): ChatState {
  switch (event.type) {
    case 'session':
      return { ...state, sessionId: event.data.sessionId };

    case 'text': {
      const last = state.items[state.items.length - 1];
      if (last?.kind === 'assistant') {
        const items = state.items.slice();
        items[items.length - 1] = { ...last, text: last.text + event.data.delta };
        return { ...state, items };
      }
      return push(state, { kind: 'assistant', text: event.data.delta });
    }

    case 'tool_call': {
      const { callId, tool, args, requiresApproval } = event.data;
      if (hasTool(state, callId)) {
        // Re-announced after approval: keep the card and its decision.
        return updateTool(state, callId, (t) => ({ ...t, tool, args, requiresApproval }));
      }
      return push(state, { kind: 'tool', callId, tool, args, requiresApproval, status: 'running' });
    }

    case 'approval_required': {
      const { callId, tool, args } = event.data;
      if (!hasTool(state, callId)) {
        return push(state, { kind: 'tool', callId, tool, args, requiresApproval: true, status: 'awaiting_approval' });
      }
      return updateTool(state, callId, (t) => ({ ...t, requiresApproval: true, status: 'awaiting_approval' }));
    }

    case 'tool_result': {
      const { callId, tool, ok, summary, data } = event.data;
      if (!hasTool(state, callId)) {
        return push(state, { kind: 'tool', callId, tool, args: {}, requiresApproval: false, status: ok ? 'ok' : 'failed', summary, data });
      }
      return updateTool(state, callId, (t) => ({
        ...t,
        // A rejected call is never executed; keep "rejected" rather than showing "failed".
        status: t.status === 'rejected' ? 'rejected' : ok ? 'ok' : 'failed',
        summary,
        data,
      }));
    }

    case 'sql_result':
      return push(state, { kind: 'sql', result: event.data });

    case 'sql_rejected':
      return push(state, { kind: 'sql_rejected', rejected: event.data });

    case 'error':
      return push(state, { kind: 'error', message: event.data.message });

    case 'done':
      return { ...state, streaming: false, stopReason: event.data.stopReason };
  }
}
