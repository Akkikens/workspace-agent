import * as vscode from 'vscode';
import { ChatPanel } from './chatPanel';
import { DEFAULT_CHUNK_OPTIONS } from './indexer/chunker';
import { Embedder, OllamaEmbedder } from './indexer/embeddings';
import { buildExcludeGlob, clampMaxFiles, isIndexablePath } from './indexer/ignore';
import { CodeIndex, IndexStats, SearchHit } from './indexer/index';

/** Returned from `activate`; used by the integration tests and available to other extensions. */
export interface WorkspaceAgentApi {
  /** Build (or rebuild) the index. Concurrent calls share one run. */
  indexWorkspace(): Promise<IndexStats | undefined>;
  /** Top-K hits for `query`; builds the index first if there is none. */
  search(query: string, k?: number): Promise<SearchHit[]>;
  /** Stats of the current index, or undefined before the first index. */
  indexStats(): IndexStats | undefined;
}

interface Settings {
  backendUrl: string;
  demoUser: string;
  provider: 'none' | 'ollama';
  ollamaUrl: string;
  model: string;
  maxFiles: number;
  topK: number;
}

function readSettings(): Settings {
  const c = vscode.workspace.getConfiguration('workspaceAgent');
  const topK = c.get<number>('topK', 8);
  return {
    backendUrl: c.get<string>('backendUrl', 'http://localhost:5080'),
    demoUser: c.get<string>('demoUser', 'alice'),
    provider: c.get<string>('embeddings.provider', 'none') === 'ollama' ? 'ollama' : 'none',
    ollamaUrl: c.get<string>('embeddings.ollamaUrl', 'http://localhost:11434'),
    model: c.get<string>('embeddings.model', 'nomic-embed-text'),
    maxFiles: clampMaxFiles(c.get<number>('maxFiles')),
    topK: Number.isFinite(topK) ? Math.min(Math.max(Math.floor(topK), 1), 50) : 8,
  };
}

function describeStats(s: IndexStats): string {
  const mode = s.mode === 'hybrid' ? `hybrid (BM25 + ${s.embedder})` : 'BM25';
  return `${s.files} files, ${s.chunks} chunks, ${mode}`;
}

function previewOf(text: string): string {
  return text
    .split('\n')
    .map((l) => l.trim())
    .filter((l) => l.length > 0)
    .slice(0, 2)
    .join('  ')
    .slice(0, 160);
}

class WorkspaceAgent implements WorkspaceAgentApi {
  private index: CodeIndex | undefined;
  /** Index path -> file URI, so search results and chat citations can be opened. */
  private uris = new Map<string, vscode.Uri>();
  private running: Promise<IndexStats | undefined> | undefined;

  constructor(private readonly output: vscode.OutputChannel) {}

  indexStats(): IndexStats | undefined {
    return this.index?.stats();
  }

  indexWorkspace(): Promise<IndexStats | undefined> {
    if (!this.running) {
      this.running = this.runIndex().finally(() => {
        this.running = undefined;
      });
    }
    return this.running;
  }

  async search(query: string, k?: number): Promise<SearchHit[]> {
    if (!this.index) await this.indexWorkspace();
    if (!this.index) return [];
    return this.index.search(query, k ?? readSettings().topK);
  }

  async openLocation(path: string, startLine: number, endLine: number): Promise<boolean> {
    const uri = this.uris.get(path.replace(/\\/g, '/'));
    if (!uri) return false;
    const doc = await vscode.workspace.openTextDocument(uri);
    const last = Math.max(doc.lineCount - 1, 0);
    const start = Math.min(Math.max((Number.isFinite(startLine) ? startLine : 1) - 1, 0), last);
    const end = Math.min(Math.max((Number.isFinite(endLine) ? endLine : startLine) - 1, start), last);
    const range = new vscode.Range(start, 0, end, doc.lineAt(end).text.length);
    const editor = await vscode.window.showTextDocument(doc, { viewColumn: vscode.ViewColumn.One, preview: true });
    editor.selection = new vscode.Selection(range.start, range.end);
    editor.revealRange(range, vscode.TextEditorRevealType.InCenterIfOutsideViewport);
    return true;
  }

  private async runIndex(): Promise<IndexStats | undefined> {
    if (!vscode.workspace.workspaceFolders?.length) {
      void vscode.window.showWarningMessage('Workspace Agent: open a folder before indexing.');
      return undefined;
    }
    const s = readSettings();
    const embedder: Embedder | undefined = s.provider === 'ollama' ? new OllamaEmbedder(s.ollamaUrl, s.model) : undefined;
    const next = new CodeIndex({ embedder });
    const uris = new Map<string, vscode.Uri>();
    const started = Date.now();

    const completed = await vscode.window.withProgress(
      { location: vscode.ProgressLocation.Window, title: 'Workspace Agent: indexing', cancellable: true },
      async (progress, token) => {
        const files = await vscode.workspace.findFiles('**/*', buildExcludeGlob(), s.maxFiles, token);
        files.sort((a, b) => a.fsPath.localeCompare(b.fsPath));
        let done = 0;
        for (const uri of files) {
          if (token.isCancellationRequested) return false;
          done++;
          const path = vscode.workspace.asRelativePath(uri).replace(/\\/g, '/');
          if (!isIndexablePath(path)) continue;
          try {
            const stat = await vscode.workspace.fs.stat(uri);
            if (stat.size > DEFAULT_CHUNK_OPTIONS.maxFileBytes) continue;
            const text = new TextDecoder('utf-8').decode(await vscode.workspace.fs.readFile(uri));
            if ((await next.addFile(path, text)) > 0) uris.set(path, uri);
          } catch (err) {
            this.output.appendLine(`skip ${path}: ${err instanceof Error ? err.message : String(err)}`);
          }
          if (done % 50 === 0) progress.report({ message: `${done}/${files.length} files` });
        }
        if (files.length >= s.maxFiles) {
          this.output.appendLine(`maxFiles limit (${s.maxFiles}) reached; some files were not indexed.`);
        }
        return true;
      },
    );

    if (!completed) {
      this.output.appendLine('Indexing cancelled; keeping the previous index.');
      return this.index?.stats();
    }
    this.index = next;
    this.uris = uris;
    const stats = next.stats();
    this.output.appendLine(`Indexed ${describeStats(stats)} in ${Date.now() - started} ms.`);
    if (stats.fallbackReason) {
      this.output.appendLine(`Embeddings disabled, using BM25 only: ${stats.fallbackReason}`);
      void vscode.window.showWarningMessage(`Workspace Agent: embeddings unavailable, using BM25 only (${stats.fallbackReason}).`);
    }
    vscode.window.setStatusBarMessage(`Workspace Agent: indexed ${describeStats(stats)}`, 5000);
    return stats;
  }

  async searchCommand(): Promise<void> {
    const query = await vscode.window.showInputBox({ prompt: 'Search the codebase', placeHolder: 'e.g. where is the invoice total computed' });
    if (!query?.trim()) return;
    const hits = await this.search(query);
    if (hits.length === 0) {
      void vscode.window.showInformationMessage(`Workspace Agent: no matches for "${query}".`);
      return;
    }
    type Item = vscode.QuickPickItem & { hit: SearchHit };
    const items: Item[] = hits.map((hit) => ({
      label: `${hit.chunk.path}:${hit.chunk.startLine}-${hit.chunk.endLine}`,
      description: `score ${hit.score.toFixed(3)}`,
      detail: previewOf(hit.chunk.text),
      hit,
    }));
    const picked = await vscode.window.showQuickPick(items, { title: `Results for "${query}"`, matchOnDetail: true });
    if (!picked) return;
    const { path, startLine, endLine } = picked.hit.chunk;
    if (!(await this.openLocation(path, startLine, endLine))) {
      void vscode.window.showWarningMessage(`Workspace Agent: cannot open ${path}.`);
    }
  }

  openChat(): void {
    ChatPanel.show({
      search: (question) => this.search(question),
      settings: () => {
        const s = readSettings();
        return { backendUrl: s.backendUrl, demoUser: s.demoUser };
      },
      openCitation: (path, startLine, endLine) => this.openLocation(path, startLine, endLine),
    });
  }
}

export function activate(context: vscode.ExtensionContext): WorkspaceAgentApi {
  const output = vscode.window.createOutputChannel('Workspace Agent');
  const agent = new WorkspaceAgent(output);
  context.subscriptions.push(
    output,
    vscode.commands.registerCommand('workspaceAgent.indexWorkspace', () => agent.indexWorkspace()),
    vscode.commands.registerCommand('workspaceAgent.searchCodebase', () => agent.searchCommand()),
    vscode.commands.registerCommand('workspaceAgent.openChat', () => agent.openChat()),
  );
  return {
    indexWorkspace: () => agent.indexWorkspace(),
    search: (query, k) => agent.search(query, k),
    indexStats: () => agent.indexStats(),
  };
}

export function deactivate(): void {}
