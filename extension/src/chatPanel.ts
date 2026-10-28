import * as crypto from 'crypto';
import * as vscode from 'vscode';
import { askCode, checkHealth } from './backend';
import { buildContextChunks, formatContextSummary } from './context';
import { SearchHit } from './indexer/index';

export interface ChatDeps {
  search(question: string): Promise<SearchHit[]>;
  settings(): { backendUrl: string; demoUser: string };
  openCitation(path: string, startLine: number, endLine: number): Promise<boolean>;
}

type FromWebview =
  | { type: 'ask'; question: string }
  | { type: 'open'; path: string; startLine: number; endLine: number };

export class ChatPanel {
  private static current: ChatPanel | undefined;
  private readonly disposables: vscode.Disposable[] = [];
  private abort: AbortController | undefined;
  private seq = 0;

  static show(deps: ChatDeps): ChatPanel {
    if (ChatPanel.current) {
      ChatPanel.current.panel.reveal(vscode.ViewColumn.Beside);
      return ChatPanel.current;
    }
    const panel = vscode.window.createWebviewPanel('workspaceAgent.chat', 'Workspace Agent', vscode.ViewColumn.Beside, {
      enableScripts: true,
      localResourceRoots: [],
      retainContextWhenHidden: true,
    });
    ChatPanel.current = new ChatPanel(panel, deps);
    return ChatPanel.current;
  }

  private constructor(
    private readonly panel: vscode.WebviewPanel,
    private readonly deps: ChatDeps,
  ) {
    panel.webview.html = renderHtml(crypto.randomBytes(16).toString('hex'));
    panel.onDidDispose(() => this.dispose(), undefined, this.disposables);
    panel.webview.onDidReceiveMessage((msg: FromWebview) => this.onMessage(msg), undefined, this.disposables);
    void this.reportHealth();
  }

  private post(message: Record<string, unknown>): void {
    void this.panel.webview.postMessage(message);
  }

  private async reportHealth(): Promise<void> {
    const { backendUrl, demoUser } = this.deps.settings();
    try {
      const health = await checkHealth(backendUrl, demoUser);
      this.post({ type: 'status', text: `Backend ${backendUrl}: ${health.status}${health.model ? ` (model ${health.model})` : ''}, user ${demoUser}` });
    } catch (err) {
      this.post({ type: 'status', error: true, text: err instanceof Error ? err.message : String(err) });
    }
  }

  private async onMessage(msg: FromWebview): Promise<void> {
    if (msg.type === 'ask' && typeof msg.question === 'string' && msg.question.trim()) {
      await this.ask(msg.question.trim());
    } else if (msg.type === 'open') {
      const ok = await this.deps.openCitation(String(msg.path), Number(msg.startLine), Number(msg.endLine));
      if (!ok) void vscode.window.showWarningMessage(`Workspace Agent: cannot open ${msg.path} (not in the workspace).`);
    }
  }

  private async ask(question: string): Promise<void> {
    const id = ++this.seq;
    this.abort?.abort();
    const abort = (this.abort = new AbortController());
    this.post({ type: 'user', id, question });
    try {
      const hits = await this.deps.search(question);
      const { chunks } = buildContextChunks(hits);
      this.post({ type: 'context', id, summary: formatContextSummary(chunks) });
      const { backendUrl, demoUser } = this.deps.settings();
      const result = await askCode({
        baseUrl: backendUrl,
        user: demoUser,
        question,
        chunks,
        signal: abort.signal,
        onText: (delta) => this.post({ type: 'delta', id, text: delta }),
      });
      if (result.error) this.post({ type: 'error', id, message: result.error });
    } catch (err) {
      if (!abort.signal.aborted) this.post({ type: 'error', id, message: err instanceof Error ? err.message : String(err) });
    } finally {
      this.post({ type: 'done', id });
    }
  }

  private dispose(): void {
    this.abort?.abort();
    ChatPanel.current = undefined;
    while (this.disposables.length) this.disposables.pop()?.dispose();
  }
}

function renderHtml(nonce: string): string {
  return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Workspace Agent</title>
<style nonce="${nonce}">
  body { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size); color: var(--vscode-foreground); background: var(--vscode-editor-background); margin: 0; padding: 0; display: flex; flex-direction: column; height: 100vh; }
  #status { padding: 6px 12px; font-size: 0.9em; color: var(--vscode-descriptionForeground); border-bottom: 1px solid var(--vscode-panel-border); }
  #status.error { color: var(--vscode-errorForeground); }
  #log { flex: 1; overflow-y: auto; padding: 12px; }
  .msg { margin: 0 0 12px; white-space: pre-wrap; word-wrap: break-word; line-height: 1.45; }
  .user { font-weight: 600; }
  .context { font-family: var(--vscode-editor-font-family); font-size: 0.85em; color: var(--vscode-descriptionForeground); }
  .assistant { padding: 8px 10px; border-left: 2px solid var(--vscode-focusBorder); }
  .error { color: var(--vscode-errorForeground); }
  a.cite { color: var(--vscode-textLink-foreground); cursor: pointer; text-decoration: none; font-family: var(--vscode-editor-font-family); }
  a.cite:hover { text-decoration: underline; }
  form { display: flex; gap: 6px; padding: 8px 12px; border-top: 1px solid var(--vscode-panel-border); }
  textarea { flex: 1; resize: vertical; min-height: 2.4em; font-family: inherit; font-size: inherit; color: var(--vscode-input-foreground); background: var(--vscode-input-background); border: 1px solid var(--vscode-input-border, transparent); padding: 4px 6px; }
  button { color: var(--vscode-button-foreground); background: var(--vscode-button-background); border: none; padding: 4px 12px; cursor: pointer; }
  button:disabled { opacity: 0.5; cursor: default; }
</style>
</head>
<body>
<div id="status">Checking backend...</div>
<div id="log" aria-live="polite"></div>
<form id="form">
  <textarea id="input" rows="2" placeholder="Ask about this codebase (Enter to send, Shift+Enter for newline)"></textarea>
  <button id="send" type="submit">Ask</button>
</form>
<script nonce="${nonce}">
(function () {
  const vscode = acquireVsCodeApi();
  const log = document.getElementById('log');
  const input = document.getElementById('input');
  const send = document.getElementById('send');
  const status = document.getElementById('status');
  const answers = new Map();
  const CITATION = /\\[([^\\[\\]\\n:]+):(\\d+)(?:-(\\d+))?\\]/g;

  function add(cls, text) {
    const el = document.createElement('div');
    el.className = 'msg ' + cls;
    el.textContent = text;
    log.appendChild(el);
    log.scrollTop = log.scrollHeight;
    return el;
  }

  function renderWithCitations(el, raw) {
    el.textContent = '';
    let last = 0;
    for (const m of raw.matchAll(CITATION)) {
      if (m.index > last) el.appendChild(document.createTextNode(raw.slice(last, m.index)));
      const a = document.createElement('a');
      a.className = 'cite';
      a.textContent = m[0];
      a.dataset.path = m[1];
      a.dataset.start = m[2];
      a.dataset.end = m[3] || m[2];
      el.appendChild(a);
      last = m.index + m[0].length;
    }
    if (last < raw.length) el.appendChild(document.createTextNode(raw.slice(last)));
  }

  function setBusy(busy) {
    send.disabled = busy;
    input.disabled = busy;
    if (!busy) input.focus();
  }

  log.addEventListener('click', function (e) {
    const a = e.target.closest && e.target.closest('a.cite');
    if (!a) return;
    e.preventDefault();
    vscode.postMessage({ type: 'open', path: a.dataset.path, startLine: Number(a.dataset.start), endLine: Number(a.dataset.end) });
  });

  document.getElementById('form').addEventListener('submit', function (e) {
    e.preventDefault();
    const q = input.value.trim();
    if (!q) return;
    input.value = '';
    setBusy(true);
    vscode.postMessage({ type: 'ask', question: q });
  });

  input.addEventListener('keydown', function (e) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      document.getElementById('form').requestSubmit();
    }
  });

  window.addEventListener('message', function (event) {
    const m = event.data;
    switch (m.type) {
      case 'status':
        status.textContent = m.text;
        status.className = m.error ? 'error' : '';
        break;
      case 'user':
        add('user', m.question);
        break;
      case 'context':
        add('context', m.summary);
        break;
      case 'delta': {
        let a = answers.get(m.id);
        if (!a) { a = { el: add('assistant', ''), raw: '' }; answers.set(m.id, a); }
        a.raw += m.text;
        renderWithCitations(a.el, a.raw);
        log.scrollTop = log.scrollHeight;
        break;
      }
      case 'error':
        add('error', m.message);
        break;
      case 'done':
        setBusy(false);
        break;
    }
  });
  input.focus();
})();
</script>
</body>
</html>`;
}
