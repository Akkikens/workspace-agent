import assert from 'node:assert/strict';
import { buildContextChunks, formatContextSummary } from '../../context';
import { SearchHit } from '../../indexer/index';

function hit(path: string, startLine: number, text: string): SearchHit {
  return { chunk: { path, startLine, endLine: startLine + text.split('\n').length - 1, text }, score: 1 };
}

describe('buildContextChunks', () => {
  it('takes hits in rank order until the character budget is used, skipping duplicates', () => {
    const hits = [hit('a.ts', 1, 'x'.repeat(40)), hit('a.ts', 1, 'x'.repeat(40)), hit('b.ts', 5, 'y'.repeat(50)), hit('c.ts', 1, 'z'.repeat(11))];
    const r = buildContextChunks(hits, 100);
    assert.deepEqual(
      r.chunks.map((c) => c.path),
      ['a.ts', 'b.ts'],
    );
    assert.equal(r.omitted, 2);
    assert.equal(r.totalChars, 90);
  });

  it('truncates an oversized top hit on a line boundary and adjusts endLine', () => {
    const text = ['line one', 'line two', 'line three', 'line four'].join('\n');
    const r = buildContextChunks([hit('big.ts', 20, text)], 18);
    assert.equal(r.chunks.length, 1);
    assert.equal(r.chunks[0].text, 'line one\nline two');
    assert.equal(r.chunks[0].startLine, 20);
    assert.equal(r.chunks[0].endLine, 21);
  });

  it('formats a summary', () => {
    assert.equal(formatContextSummary([]), 'context: (none)');
    const { chunks } = buildContextChunks([hit('a.ts', 3, 'one\ntwo')]);
    assert.equal(formatContextSummary(chunks), 'context: a.ts:3-4');
  });
});
