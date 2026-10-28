# Workspace Agent web UI (Angular)

Chat UI for the workspace agent backend. It streams SSE from the endpoints in `../docs/api-contract.md`.

- Requires Node `^20.19 || ^22.12 || >=24` (Angular 21) and npm.
- `npm ci` installs dependencies.
- `npm start` serves the app at http://localhost:4200. The backend must be running on http://localhost:5080 (set with `API_BASE_URL` in `src/app/chat-api.service.ts`).
- `npm run build` writes a production build to `dist/web/`.
- `npm test` runs the Vitest unit tests once, headless (no watch mode).
- Pick a demo user (alice / bob / carol) in the header. Each request sends that user as `X-Demo-User`.
- Main files: `sse.ts` (SSE parser), `chat-state.ts` (`applyEvent` reducer), `chat-api.service.ts`, and the `*.component.ts` files.
