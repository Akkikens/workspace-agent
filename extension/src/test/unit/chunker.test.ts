import assert from 'node:assert/strict';
import { Chunk, chunkFile } from '../../indexer/chunker';

function lines(n: number, make: (i: number) => string): string[] {
  return Array.from({ length: n }, (_, i) => make(i + 1));
}

/** Every chunk's text must be exactly lines [startLine, endLine] of the source. */
function assertLineNumbersMatch(content: string, chunks: Chunk[]): void {
  const src = content.split('\n');
  for (const c of chunks) {
    assert.ok(c.startLine >= 1 && c.endLine >= c.startLine, `bad range ${c.startLine}-${c.endLine}`);
    assert.equal(c.text, src.slice(c.startLine - 1, c.endLine).join('\n'), `text mismatch for ${c.startLine}-${c.endLine}`);
  }
}

describe('chunkFile', () => {
  it('starts chunks at top-level declarations and keeps 1-based line numbers', () => {
    const content = [
      "import { a } from './a';", // 1
      '', // 2
      'export function first(x: number): number {', // 3
      ...lines(6, (i) => `  const v${i} = x + ${i};`), // 4-9
      '  return x;', // 10
      '}', // 11
      '', // 12
      'export function second(): string {', // 13
      ...lines(6, (i) => `  const s${i} = 'v${i}';`), // 14-19
      "  return '';", // 20
      '}', // 21
    ].join('\n');
    const chunks = chunkFile('src/a.ts', content);
    assertLineNumbersMatch(content, chunks);
    // The 1-line import segment is merged forward into the first function.
    assert.deepEqual(
      chunks.map((c) => [c.startLine, c.endLine]),
      [
        [1, 11],
        [13, 21],
      ],
    );
    assert.ok(chunks.every((c) => c.path === 'src/a.ts'));
  });

  it('keeps a JSDoc block attached to the declaration that follows it', () => {
    const body = lines(8, (i) => `  const v${i} = ${i};`);
    const content = [
      'export const ONE = 1;', // 1
      ...lines(5, (i) => `export const C${i} = ${i};`), // 2-6
      '', // 7
      '/**', // 8
      ' * Adds two numbers.', // 9
      ' */', // 10
      'export function add(a: number, b: number): number {', // 11
      ...body, // 12-19
      '  return a + b;', // 20
      '}', // 21
    ].join('\n');
    const chunks = chunkFile('src/add.ts', content);
    assertLineNumbersMatch(content, chunks);
    const add = chunks.find((c) => c.text.includes('function add'));
    assert.ok(add);
    assert.equal(add.startLine, 8);
    assert.equal(add.endLine, 21);
    assert.ok(add.text.startsWith('/**'));
    // ...and the previous chunk does not end with the dangling comment.
    assert.deepEqual([chunks[0].startLine, chunks[0].endLine], [1, 6]);
  });

  it('splits a long block without blank lines into windows that overlap', () => {
    const content = ['function big() {', ...lines(150, (i) => `  step${i}();`), '}'].join('\n');
    const chunks = chunkFile('big.js', content, { maxLines: 40, overlap: 5 });
    assertLineNumbersMatch(content, chunks);
    assert.ok(chunks.length >= 4);
    for (const c of chunks) assert.ok(c.endLine - c.startLine + 1 <= 40);
    for (let i = 1; i < chunks.length; i++) {
      assert.equal(chunks[i].startLine, chunks[i - 1].endLine - 5 + 1, `window ${i} should overlap the previous by 5 lines`);
    }
    assert.equal(chunks[0].startLine, 1);
    assert.equal(chunks[chunks.length - 1].endLine, 152);
  });

  it('prefers cutting long blocks at blank lines, without overlap', () => {
    const block = (n: number) => lines(14, (i) => `  part${n}_${i}();`);
    const content = ['function big() {', ...block(1), '', ...block(2), '', ...block(3), '', ...block(4), '}'].join('\n');
    const chunks = chunkFile('big.js', content, { maxLines: 20, overlap: 5 });
    assertLineNumbersMatch(content, chunks);
    for (let i = 1; i < chunks.length; i++) {
      assert.ok(chunks[i].startLine > chunks[i - 1].endLine, 'blank-line cuts must not overlap');
    }
    // No chunk starts or ends on a blank line.
    for (const c of chunks) {
      const t = c.text.split('\n');
      assert.notEqual(t[0].trim(), '');
      assert.notEqual(t[t.length - 1].trim(), '');
    }
  });

  it('handles CRLF line endings', () => {
    const content = 'def a():\r\n    return 1\r\n\r\ndef b():\r\n    return 2\r\n';
    const chunks = chunkFile('m.py', content, { minLines: 1 });
    assert.deepEqual(
      chunks.map((c) => [c.startLine, c.endLine, c.text]),
      [
        [1, 2, 'def a():\n    return 1'],
        [4, 5, 'def b():\n    return 2'],
      ],
    );
  });

  it('skips empty, binary and oversized files and blanks minified lines', () => {
    assert.deepEqual(chunkFile('e.ts', ''), []);
    assert.deepEqual(chunkFile('b.bin', 'abc\u0000def'), []);
    assert.deepEqual(chunkFile('huge.ts', 'x'.repeat(100), { maxFileBytes: 50 }), []);
    const chunks = chunkFile('m.js', `const a = 1;\n${'y'.repeat(2000)}\nconst b = 2;`);
    assert.equal(chunks.length, 1);
    assert.equal(chunks[0].text, 'const a = 1;\n\nconst b = 2;');
    assert.equal(chunks[0].endLine, 3);
  });
});
