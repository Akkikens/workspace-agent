import assert from 'node:assert/strict';
import { DEFAULT_MAX_FILES, buildExcludeGlob, clampMaxFiles, isIndexablePath } from '../../indexer/ignore';

describe('ignore rules', () => {
  it('accepts ordinary source files', () => {
    for (const p of ['src/index.ts', 'backend/Program.cs', 'app.py', 'docs/README.md', 'web/src/app/app.component.html']) {
      assert.equal(isIndexablePath(p), true, p);
    }
  });

  it('rejects excluded directories at any depth', () => {
    for (const p of ['node_modules/lodash/index.js', 'web/node_modules/x/y.ts', '.git/config', 'backend/bin/Debug/app.dll', 'dist/main.js', 'pkg/__pycache__/m.pyc', '.vscode-test/x.js']) {
      assert.equal(isIndexablePath(p), false, p);
    }
  });

  it('only treats directory segments as directories', () => {
    // A file named like an excluded directory is fine.
    assert.equal(isIndexablePath('scripts/build'), true);
    assert.equal(isIndexablePath('src/out.ts'), true);
  });

  it('rejects lock files, binaries and generated assets', () => {
    for (const p of ['package-lock.json', 'web/yarn.lock', 'go.sum', 'logo.PNG', 'fonts/a.woff2', 'vendor/jquery.min.js', 'out.js.map', 'x.vsix']) {
      assert.equal(isIndexablePath(p), false, p);
    }
  });

  it('handles Windows separators and empty paths', () => {
    assert.equal(isIndexablePath('web\\node_modules\\x.js'), false);
    assert.equal(isIndexablePath('src\\app.ts'), true);
    assert.equal(isIndexablePath(''), false);
  });

  it('builds one brace glob covering the same rules', () => {
    const glob = buildExcludeGlob();
    assert.ok(glob.startsWith('{') && glob.endsWith('}'));
    for (const part of ['**/node_modules/**', '**/package-lock.json', '**/*.png', '**/*.min.js']) {
      assert.ok(glob.includes(part), part);
    }
  });

  it('clamps maxFiles', () => {
    assert.equal(clampMaxFiles(undefined), DEFAULT_MAX_FILES);
    assert.equal(clampMaxFiles('10'), DEFAULT_MAX_FILES);
    assert.equal(clampMaxFiles(0), 1);
    assert.equal(clampMaxFiles(12.7), 12);
    assert.equal(clampMaxFiles(1e9), 50_000);
  });
});
