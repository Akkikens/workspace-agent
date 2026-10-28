# Backend HTTP contract

The ASP.NET Core backend (`backend/src/WorkspaceAgent.Api`) listens on `http://localhost:5080` by default.
Both the Angular app (`web/`) and the VS Code extension (`extension/`) talk to it only through this contract.

## Identity (demo only)

There is no real sign-in. Every request names a demo user in the `X-Demo-User` header:

| id | name | role | business unit |
|---|---|---|---|
| `alice` | Alice Chen | `engineer` | `Modem` |
| `bob` | Bob Patel | `program_manager` | `Camera` |
| `carol` | Carol Diaz | `director` | (all) |

`GET /api/users` returns `[{ "id", "name", "role", "businessUnit" }]` (`businessUnit` is `null` for a director).
A missing or unknown user returns `401`.

## Streaming (Server-Sent Events over POST)

`POST /api/chat`, the approval endpoint and `POST /api/code/ask` all respond with `Content-Type: text/event-stream`.
Each event is

```
event: <type>
data: <single-line JSON>

```

Clients read the body with `fetch` + a stream reader (EventSource cannot POST). Event types:

| type | data |
|---|---|
| `session` | `{ "sessionId": string }` (always first) |
| `text` | `{ "delta": string }` (append to the current assistant message) |
| `tool_call` | `{ "callId": string, "tool": string, "args": object, "requiresApproval": boolean }` |
| `approval_required` | `{ "callId": string, "tool": string, "args": object }` (stream ends with `done` / `awaiting_approval` after this) |
| `tool_result` | `{ "callId": string, "tool": string, "ok": boolean, "summary": string, "data": any \| null }` |
| `sql_result` | `{ "callId": string, "proposedSql": string, "executedSql": string, "columns": string[], "rows": (string\|number\|null)[][], "rowCount": number, "truncated": boolean, "appliedFilters": string[] }` |
| `sql_rejected` | `{ "callId": string, "proposedSql": string, "reasons": string[] }` |
| `error` | `{ "message": string }` |
| `done` | `{ "stopReason": "end_turn" \| "awaiting_approval" \| "max_steps" \| "error" }` (always last) |

## Endpoints

- `GET /api/health` -> `{ "status": "ok", "model": string }`
- `GET /api/users`
- `POST /api/chat` body `{ "sessionId"?: string, "message": string }` -> SSE
- `POST /api/chat/{sessionId}/approvals/{callId}` body `{ "approved": boolean }` -> SSE (continues the paused run; a rejected call is never executed)
- `POST /api/code/ask` body `{ "question": string, "chunks": [{ "path": string, "startLine": number, "endLine": number, "text": string }] }` -> SSE (`session`, `text`..., `done`). The answer cites chunks as `[path:startLine-endLine]`.

CORS allows any origin in development.
