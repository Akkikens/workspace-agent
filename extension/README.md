# Workspace Agent for VS Code

A VS Code extension that indexes the open workspace locally, searches it, and sends the top-ranked
code chunks to the Workspace Agent backend so an answer can cite them (`POST /api/code/ask`, see
[`../docs/api-contract.md`](../docs/api-contract.md)).

## Commands

| Command | What it does |
|---|---|
| `Workspace Agent: Index Workspace` | Walks the workspace (`vscode.workspace.findFiles`), skips excluded paths, splits files into chunks and builds an in-memory BM25 index. Rebuilds from scratch each time. |
| `Workspace Agent: Search Codebase` | Prompts for a query and shows the top-K chunks in a QuickPick (`path:start-end`, score, preview). Picking one opens the file with that range selected. Indexes first if there is no index yet. |
| `Workspace Agent: Open Chat` | Opens a webview. A question is answered by retrieving the top-K chunks locally, then calling the backend from the extension host and streaming the SSE `text` events into the panel. `[path:start-end]` citations in the answer are clickable. |

## Retrieval

- **Chunking**: splits on top-level-ish declarations (TS/JS, Python, C#, Java, Go, Rust patterns),
  keeps leading comments, decorators and attributes with their declaration, merges tiny segments,
  and cuts blocks longer than 60 lines into windows (at a blank line when possible, otherwise with a
  5-line overlap). Files over 512 KB, binary files and lines over 1000 characters are skipped.
- **BM25 (default)**: `k1 = 1.2`, `b = 0.75`, over a code-aware tokenizer that splits camelCase,
  PascalCase, snake_case and kebab-case and also keeps the whole identifier.
- **Hybrid (optional)**: with `workspaceAgent.embeddings.provider` set to `ollama`, each chunk is
  embedded through a local Ollama server (`POST /api/embed`) and the BM25 and cosine-similarity
  rankings are merged with reciprocal rank fusion (`k = 60`). If Ollama fails during indexing or at
  query time, the index falls back to BM25 and reports why. This path is covered only by unit tests
  with a fake embedder; it has not been run against a live Ollama server.
- **Excluded**: `node_modules`, `.git`, `bin`, `obj`, `dist`, `out`, `build`, `target`, virtualenvs and
  similar directories, lock files, images, archives, fonts, media, `*.min.js`, source maps. See
  `src/indexer/ignore.ts`.

## Settings

| Setting | Default |
|---|---|
| `workspaceAgent.backendUrl` | `http://localhost:5080` |
| `workspaceAgent.demoUser` | `alice` (sent as `X-Demo-User`) |
| `workspaceAgent.embeddings.provider` | `none` (`ollama` for hybrid) |
| `workspaceAgent.embeddings.ollamaUrl` | `http://localhost:11434` |
| `workspaceAgent.embeddings.model` | `nomic-embed-text` |
| `workspaceAgent.maxFiles` | `2000` |
| `workspaceAgent.topK` | `8` |

## Development

```sh
npm install
npm run compile
npm run test:unit          # mocha, pure modules only (no VS Code needed)
npm run test:integration   # downloads VS Code into .vscode-test/ and runs the suite in an extension host
```

On a headless Linux machine run the integration tests under a virtual display:
`xvfb-run -a npm run test:integration`. `@vscode/test-electron` 3.x requires Node 22 or newer.

Press F5 in VS Code ("Run Extension") to launch a development host on the fixture workspace in
`src/test/fixtures/sample-workspace`.

### What the tests cover

- Unit: chunk boundaries, overlap and line numbers; tokenizer splitting; BM25 ranking (exact term,
  IDF, length normalization, empty queries, incremental updates); ignore rules; reciprocal rank
  fusion and hybrid search/fallback with a fake embedder; SSE parsing (split reads, CRLF, multi-byte
  UTF-8) and the `/api/code/ask` client against a fake `fetch` and a local HTTP server; context budget.
- Integration: activates the extension in a real VS Code against the fixture workspace, checks the
  three commands are registered, runs `Index Workspace`, asserts the expected fixture file ranks
  first for several queries, that `build/` is excluded, that chunk line ranges match the file on disk,
  and that the chat panel opens.

## Limitations

- The index lives in memory only. It is not persisted, and it is not updated when files change;
  run `Index Workspace` again after edits.
- `.gitignore` and the `files.exclude` / `search.exclude` settings are not applied; only the built-in
  exclude list is.
- Each chat question is sent with only its own retrieved chunks; the extension keeps no conversation
  history.
- The chat webview's question/answer round trip and the Search Codebase QuickPick are not exercised
  by automated tests (no backend is started in the integration test, and the QuickPick needs user input).
- Hybrid retrieval has not been verified against a live Ollama server (see above).
