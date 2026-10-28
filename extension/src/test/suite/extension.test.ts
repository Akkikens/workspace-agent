import assert from 'node:assert/strict';
import * as vscode from 'vscode';
import type { WorkspaceAgentApi } from '../../extension';
import type { IndexStats } from '../../indexer/index';

const EXTENSION_ID = 'akkikens.workspace-agent-vscode';

describe('Workspace Agent extension', () => {
  let api: WorkspaceAgentApi;

  before(async () => {
    const ext = vscode.extensions.getExtension<WorkspaceAgentApi>(EXTENSION_ID);
    assert.ok(ext, `extension ${EXTENSION_ID} not found`);
    api = await ext.activate();
    assert.ok(api && typeof api.search === 'function', 'activate() should return the API');
  });

  it('opens the fixture workspace', () => {
    const folders = vscode.workspace.workspaceFolders ?? [];
    assert.equal(folders.length, 1);
    assert.equal(folders[0].name, 'sample-workspace');
  });

  it('registers the three commands', async () => {
    const commands = await vscode.commands.getCommands(true);
    for (const id of ['workspaceAgent.indexWorkspace', 'workspaceAgent.searchCodebase', 'workspaceAgent.openChat']) {
      assert.ok(commands.includes(id), `${id} is not registered`);
    }
  });

  it('indexes the workspace via the command, skipping excluded directories', async () => {
    const stats = await vscode.commands.executeCommand<IndexStats>('workspaceAgent.indexWorkspace');
    assert.ok(stats, 'indexWorkspace should return stats');
    // src/invoice.ts, src/auth.py, src/RateLimiter.cs, README.md; build/bundle.js is excluded.
    assert.equal(stats.files, 4);
    assert.ok(stats.chunks >= 4);
    assert.equal(stats.mode, 'bm25');
    assert.deepEqual(api.indexStats(), stats);
  });

  it('ranks the expected fixture file first', async () => {
    const invoice = await api.search('where is the invoice total calculated');
    assert.ok(invoice.length > 0);
    assert.equal(invoice[0].chunk.path, 'src/invoice.ts');
    assert.ok(invoice[0].chunk.text.includes('calculateInvoiceTotal'));
    assert.ok(!invoice.some((h) => h.chunk.path.startsWith('build/')), 'build/ must not be indexed');

    const auth = await api.search('verify password hash');
    assert.equal(auth[0].chunk.path, 'src/auth.py');

    const limiter = await api.search('TryAcquire tokens', 1);
    assert.equal(limiter.length, 1);
    assert.equal(limiter[0].chunk.path, 'src/RateLimiter.cs');
  });

  it('returns line ranges that match the file on disk', async () => {
    const [hit] = await api.search('calculateInvoiceTotal', 1);
    const [folder] = vscode.workspace.workspaceFolders!;
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.joinPath(folder.uri, hit.chunk.path));
    const lines: string[] = [];
    for (let i = hit.chunk.startLine - 1; i < hit.chunk.endLine; i++) lines.push(doc.lineAt(i).text);
    assert.equal(lines.join('\n'), hit.chunk.text);
  });

  it('opens the chat panel without a backend running', async () => {
    await vscode.commands.executeCommand('workspaceAgent.openChat');
    // The tab model is updated asynchronously after the panel is created.
    const isChatTab = (t: vscode.Tab) => t.input instanceof vscode.TabInputWebview && t.input.viewType.endsWith('workspaceAgent.chat');
    let tabs: vscode.Tab[] = [];
    for (let i = 0; i < 50; i++) {
      tabs = vscode.window.tabGroups.all.flatMap((g) => g.tabs);
      if (tabs.some(isChatTab)) break;
      await new Promise((r) => setTimeout(r, 100));
    }
    const seen = tabs.map((t) => `${t.label} (${t.input instanceof vscode.TabInputWebview ? t.input.viewType : typeof t.input})`);
    assert.ok(tabs.some(isChatTab), `expected a Workspace Agent webview tab, saw: ${seen.join(', ') || 'none'}`);
  });
});
