# workspace-agent

A workplace agent in **C#/.NET (ASP.NET Core minimal APIs)** with an **Angular** chat UI and a **VS Code extension**. It answers questions from Jira, Confluence and Microsoft 365 (calendar, mail, people). It answers questions over a governed SQL warehouse, where the model only proposes SQL and a validator decides what runs. It creates Jira issues only after a human approves the call in the UI. The VS Code extension indexes a workspace locally and asks the backend questions grounded in the retrieved code.

I built this in about a day with Claude Code as a learning and portfolio project. It is not a production system and has no users. The Jira, Confluence and Microsoft Graph clients are written against the documented REST APIs. They have only ever run against local stubs (WireMock.Net in tests, a small stub server in the demo), never against a real Atlassian site or Microsoft 365 tenant.

| Concern | What's here | Where |
|---|---|---|
| Agent loop | Provider-neutral `IChatModel`; tool-use loop with a step cap. The tool calls in each turn run in order. Tool exceptions become a "failed, no changes made" result for the model. | `backend/src/WorkspaceAgent.Core/Agent/AgentRunner.cs` |
| Human approval for writes | `jira_create_issue` pauses the run (`approval_required`) and stores the call plus any calls queued after it. `POST /api/chat/{session}/approvals/{call}` either executes it or records a rejection, then resumes. A rejected call is never sent. Only the session's owner can approve. | `AgentRunner.cs`, `WorkspaceAgent.Api/Program.cs` |
| Jira Cloud | `POST /rest/api/3/search/jql` (enhanced JQL search) and `POST /rest/api/3/issue` with an Atlassian Document Format description. Basic auth (email + API token). One retry on 429/503 honoring `Retry-After`, but never on create, where a retry could duplicate the issue. Error bodies (`errorMessages`/`errors`) are surfaced. | `Core/Integrations/AtlassianClients.cs` |
| Confluence Cloud | `GET /wiki/rest/api/search?cql=...`. The model passes text plus an optional space; the client builds and escapes the CQL, so the model cannot write CQL. | `AtlassianClients.cs` |
| Microsoft Graph | Read-only `/me/calendarView` (UTC via `Prefer`), `/me/messages` (`$search`), `/me/people`, with a bearer token from an `IGraphTokenProvider` seam. Graph `{error:{code,message}}` bodies are surfaced. | `Core/Integrations/GraphClient.cs` |
| NL-to-SQL governance | Semantic layer with per-role tables, column grants and row filters. A hand-written parser accepts one SELECT, and the validator checks every table, column and function against it. Every table reference is rewritten into a filtered, column-projected subquery with a bound parameter, and the row cap is enforced. A SQLite authorizer checks again at prepare time on a read-only connection. | `Core/Sql/` |
| NL-to-SQL eval | 34 cases (14 answerable, 8 row-level security, 12 governance) compared on execution results against gold SQL. Recorded model outputs are replayed offline. A CI gate fails on any regression or any governance leak. | `backend/evals/nl2sql/`, `Core/Eval/Nl2SqlEval.cs` |
| Streaming HTTP API | Server-Sent Events over POST: `session`, `text`, `tool_call`, `approval_required`, `tool_result`, `sql_result`, `sql_rejected`, `error`, `done`. | [`docs/api-contract.md`](docs/api-contract.md) |
| Angular UI | Angular 21 standalone components and signals. A `fetch`-stream SSE parser, a chat with streaming text, tool-call cards with Approve/Reject for write actions, and a results table for SQL answers (proposed vs executed SQL, applied row filters). | `web/src/app/` |
| VS Code extension | Workspace indexing (declaration-aware chunking, code-aware tokenizer, BM25; optional Ollama embeddings fused with reciprocal rank fusion), a **Search Codebase** QuickPick, and a chat webview that sends the top-K chunks to `POST /api/code/ask` and renders `[path:start-end]` citations. | `extension/` |
| Model providers | `ScriptedChatModel` (offline demo), `CassetteChatModel` (eval replay) and `AnthropicChatModel` on the official Anthropic C# SDK (`WORKSPACE_AGENT_MODEL=anthropic`, `ANTHROPIC_API_KEY`; default `claude-opus-5-5`). | `Core/Llm/`, `WorkspaceAgent.Anthropic/` |

## Architecture

```
  Angular UI (web/)                 VS Code extension (extension/)
  chat + approve/reject + table      index -> BM25 [+ Ollama, RRF] -> top-K chunks
        |  POST /api/chat (SSE)               |  POST /api/code/ask (SSE)
        |  POST /api/chat/{s}/approvals/{c}   |
        v                                     v
  +----------------------------------------------------------------------+
  | ASP.NET Core minimal API  (X-Demo-User -> UserContext: role, unit)   |
  |                                                                      |
  |  AgentRunner --- IChatModel (scripted | cassette | Anthropic SDK)    |
  |      |                                                               |
  |      +-- read tools ----------> JiraClient / ConfluenceClient /      |
  |      |                          GraphClient --HTTP--> stubs (demo,   |
  |      |                          tests) or a real tenant (untested)   |
  |      +-- jira_create_issue ---> paused until a human approves        |
  |      +-- query_program_data --> SqlGovernor --> Warehouse (SQLite)   |
  |                                  parse, validate,   read-only conn + |
  |                                  rewrite, cap       authorizer       |
  +----------------------------------------------------------------------+
```

## NL-to-SQL governance design

The model never touches the database. It proposes SQL as the `sql` argument of `query_program_data`. What actually runs is built from that proposal in four steps:

1. **Parse.** A recursive-descent parser (`SqlParser.cs`) accepts exactly one `SELECT`: joins, subqueries (`IN`, `EXISTS`, scalar, derived tables), `GROUP BY`/`HAVING`/`ORDER BY`, `CASE`, `CAST`, and `LIMIT` with an integer literal. The lexer rejects comments, parameters, backticks and brackets. The parser rejects `WITH`, `UNION`/`INTERSECT`/`EXCEPT`, stacked statements, schema-qualified names, table-valued functions, `GLOB`/`REGEXP`, window functions and every non-SELECT statement. Anything the AST cannot represent cannot reach the database.
2. **Validate against the semantic layer** (`SemanticLayer.cs`). Each table must be granted to the caller's role, and each column reference (qualified, unqualified, quoted, nested in subqueries) must resolve to a granted column. Functions come from an allow-list. Errors name the exact table or column and role, so the model can correct itself.
3. **Rewrite.** Every base-table reference becomes `(SELECT <granted columns> FROM t WHERE <row filter>) AS alias`. The row filter is trusted SQL written by the data owner, with the user's business unit bound as `@bu`; the model's text never supplies it. The filter therefore holds however the model's query joins, nests or `OR`s, and `SELECT *` expands to granted columns only. The outer query gets `LIMIT 200`, and a larger limit is clamped. The emitter re-renders the AST with every identifier quoted and every compound expression parenthesized.
4. **Execute with a second, independent check** (`Warehouse.cs`). The connection is opened read-only, and a SQLite authorizer (`sqlite3_set_authorizer`) allows only `SELECT`, reads of granted `(table, column)` pairs and allow-listed functions; everything else is denied at prepare time. Tests bypass the governor and send forged SQL straight to the executor (a salary read, an unlisted function, a `DELETE`); the authorizer stops all three.

Roles in the demo warehouse (12 programs across 4 business units, 48 milestones, 198 defects, 152 builds, 24 employees, all seeded deterministically):

| Role (demo user) | Tables | Rows | Notably not visible |
|---|---|---|---|
| `engineer` (alice, Modem) | programs, milestones, defects, builds | own business unit | `employees`, `programs.budget_musd` |
| `program_manager` (bob, Camera) | + employees | own business unit | `programs.budget_musd`, `employees.salary_usd` |
| `director` (carol) | all | all | `employees.salary_usd` (granted to no role) |

## Eval results (real numbers, with what they do and do not measure)

`backend/evals/nl2sql/`: each case gives a user, a question and either gold SQL (`expect: match`, run ungoverned on the raw warehouse with the user's scope written out) or `expect: blocked`. The SQL the model proposes goes through the same governor and executor as the agent tool, and the rows are compared as multisets (ordered when the question asks for an order), with numbers rounded to 1 decimal place.

Latest run (`dotnet run --project backend/src/WorkspaceAgent.Cli -- eval ...`, full table in [`results.md`](backend/evals/nl2sql/results/results.md)):

| Category | Passed |
|---|---|
| Answerable (director, cross-unit questions) | 14/14 |
| Row-level security (engineer / program manager questions, including asking for another unit's data) | 8/8 |
| Governance (salary, unit-restricted budget, `employees` for an engineer, `DELETE`, `PRAGMA`, `sqlite_master`, stacked `DROP`, comment bypass, `UNION`, table-valued function, `load_extension`, quoted restricted column) | 12/12 blocked, 0 leaks |

How to read this:

- **These numbers measure the governance and execution pipeline, not a model.** CI has no API key, so the eval replays a cassette. I wrote the SQL for the answerable and row-level-security cases with Claude in the Claude Code session, from the same schema prompt the eval sends. It was not captured from an API call, and it is unsurprising that SQL written alongside the gold queries matches them. The governance cases deliberately replay hostile SQL (a model that complies with an attacker), because they exist to test the governor.
- **The rounding rule matters.** 5 of the 22 answerable/row-level-security cases wrap aggregates in `ROUND(x, 1)` and pass only because numbers are compared at 1 decimal place. With exact comparison the score would be 17/22.
- To measure a real model, run `eval --model=anthropic --record=evals/nl2sql/cassette.live.json` with `ANTHROPIC_API_KEY` set. I have not run it, so there is no live-model accuracy number here.

## Try it

```bash
# backend: 77 xUnit tests, then the eval gate (no keys needed)
cd backend
dotnet test
dotnet run --project src/WorkspaceAgent.Cli -- eval --cases=evals/nl2sql/cases.json \
  --cassette=evals/nl2sql/cassette.json --baseline=evals/nl2sql/baseline.json

# demo: stub Jira/Confluence/Graph on :5090, API on :5080 (scripted model)
dotnet run --project src/WorkspaceAgent.Stubs &
dotnet run --project src/WorkspaceAgent.Api &
curl -N localhost:5080/api/chat -H 'X-Demo-User: alice' -H 'content-type: application/json' \
  -d '{"message":"How many open defects per program?"}'

# UI on :4200
cd ../web && npm ci && npm start
# try: "open defects per program" (alice vs carol), "show program budgets" (bob: rejected),
#      "file a bug for the NTN handover drop" (approve / reject), "what meetings do I have?"

# VS Code extension: open extension/ in VS Code and press F5
cd ../extension && npm ci && npm run test:unit

# real model instead of the script
WORKSPACE_AGENT_MODEL=anthropic ANTHROPIC_API_KEY=... dotnet run --project backend/src/WorkspaceAgent.Api
```

The scripted model is a list of regex rules over the latest user message, one step per model turn (`backend/src/WorkspaceAgent.Api/demo/demo-script.json`). It makes no decisions. It exists so the UI, approval flow and tools can be exercised offline.

## How it is tested

- **Backend, 77 xUnit tests** (`backend/tests`):
  - **SQL validator, negative cases:** 26 rejection cases (non-SELECT statements, `WITH`, stacked statements, comments, parameters, `UNION`, schema-qualified names, table-valued functions, `sqlite_master`, disallowed functions, `GLOB`, non-literal `LIMIT`, unknown tables, columns and aliases). Salary is unreachable for every role, qualified, unqualified, quoted, in `ORDER BY`, aggregated or inside a subquery. Budget is director-only, and `employees` is hidden from engineers.
  - **Rewrite:** row-cap insertion and clamping, the filter carried as a bound parameter, `SELECT *` projecting granted columns only, and quote/precedence round trips.
  - **Row-level security, executed:** an engineer sees only Modem rows. Attempts on other units via `WHERE`, `IN` subquery, correlated `EXISTS`, derived table, `<>` join and `OR 1 = 1` return nothing extra. Child tables are scoped through their program, and a program manager sees only their unit's staff.
  - **Authorizer:** forged SQL that skips the governor is denied for a restricted column, an unlisted function and a `DELETE`.
  - **Integration clients against WireMock.Net:** Jira search (path, basic auth, body, clamped `maxResults`, parsing), create (ADF body), a 400 with a field error, one retry after 429, no retry on create, CQL escaping, Confluence parsing and absolute links, Graph `calendarView` (window, bearer, `Prefer`), `$search` quoting, people parsing, and Graph error bodies.
  - **HTTP host end to end** (`WebApplicationFactory`, WireMock, real warehouse):
    - the SQL answer streams with its applied filters;
    - a restricted column comes back as `sql_rejected`;
    - a create pauses with no POST sent, and approving sends exactly one POST;
    - rejecting sends none, and another user approving gets 403;
    - the Jira read tool works, an unknown user gets 401, and code Q&A cites its chunks.
  - **Eval:** the committed cassette has no leaks, a governance case that executes counts as a leak, a changed question is cassette drift, and the comparison rules hold.
- **Angular, 30 Vitest tests** (`web/`): SSE parsing across split reads, the API service against a fake `fetch`, chat state reduction (streaming text, tool cards, approvals, SQL results/rejections), the tool-call card's Approve/Reject outputs, and the results table.
- **VS Code extension, 53 unit + 6 integration tests** (`extension/`):
  - **Unit (mocha):** chunker, tokenizer, BM25, ignore rules, RRF/hybrid with a fake embedder, the SSE client against a fake `fetch` and a local HTTP server.
  - **Integration (`@vscode/test-electron`, a real VS Code extension host under `xvfb` in CI):** commands are registered, indexing a fixture workspace ranks the expected file first, `build/` is excluded, line ranges match the file on disk, and the chat panel opens.
- **CI** (`.github/workflows/ci.yml`): backend build and test, the NL-to-SQL eval gate (results uploaded as an artifact), Angular build and test, and extension compile, unit and integration tests.

## Limits, stated plainly

- **No real tenants.** Jira, Confluence and Graph calls have only hit WireMock and the local stub server. The stub server matches JQL, CQL and `$search` loosely and is not an emulator. Real use would need Atlassian OAuth 2.0 (3LO) or API tokens and an Entra ID app registration with delegated `Calendars.Read`, `Mail.Read` and `People.Read` via MSAL. None of that is wired; Graph takes a static token through `IGraphTokenProvider`. Only the first page of results is read.
- **No real identity.** The demo user comes from an `X-Demo-User` header. Production would map Entra ID (OIDC) claims to the same `UserContext`. Sessions and pending approvals live in memory.
- **The SQL subset is deliberate.** There are no CTEs, set operations or window functions. Real analytical questions sometimes need them, so this governor will refuse some legitimate queries. The parser is hand-written for SQLite syntax and is not a general SQL parser. The warehouse is a small seeded SQLite file, not Postgres, Snowflake or a real data platform.
- **The live model path is wired but unexercised.** `AnthropicChatModel` compiles against the official SDK and echoes thinking blocks back, but CI never calls it and I have not run it here. A `refusal` stop reason surfaces as an error, with no fallback model configured. The provider returns whole turns: the UI streams agent events and chunked text, not provider tokens.
- **The eval measures the pipeline, not a model** (see above). It has 34 hand-written cases on one synthetic schema.
- **Extension:** the index is in memory and must be rebuilt after edits, and only a built-in exclude list is applied. Ollama hybrid retrieval is covered only by unit tests with a fake embedder. The chat webview round trip and the QuickPick are not covered by automated tests.
- **Not done:** audit logging of tool calls, rate limiting, prompt-injection detection on tool output (mail bodies are plain data to the model; the approval gate is the only guard on writes), deployment.
