import assert from 'node:assert/strict';
import { Bm25Index } from '../../indexer/bm25';
import { Chunk } from '../../indexer/chunker';

function chunk(path: string, text: string): Chunk {
  return { path, startLine: 1, endLine: text.split('\n').length, text };
}

function build(docs: Record<string, string>): Bm25Index {
  const index = new Bm25Index();
  for (const [path, text] of Object.entries(docs)) index.addFile(path, [chunk(path, text)]);
  return index;
}

describe('Bm25Index', () => {
  const docs = {
    'invoice.ts': 'function calculateInvoiceTotal(items) { sum quantity unitPrice tax }',
    'auth.py': 'def hash_password(password, salt): pbkdf2 sha256',
    'limiter.cs': 'class RateLimiter { TryAcquire tokens capacity Refill }',
    'readme.md': 'Sample project with invoice examples and a rate limiter',
  };

  it('ranks the document containing the exact query term first', () => {
    const index = build(docs);
    const hits = index.search('hash password', 5);
    assert.equal(hits[0].chunk.path, 'auth.py');
    assert.ok(hits[0].score > 0);
    assert.equal(index.search('TryAcquire', 5)[0].chunk.path, 'limiter.cs');
  });

  it('matches a camelCase identifier from a natural-language query', () => {
    const hits = build(docs).search('invoice total', 5);
    assert.equal(hits[0].chunk.path, 'invoice.ts');
    // readme.md mentions "invoice" too, but not "total".
    assert.ok(hits.some((h) => h.chunk.path === 'readme.md'));
    assert.ok(hits[0].score > hits.find((h) => h.chunk.path === 'readme.md')!.score);
  });

  it('weights rare terms above common ones (IDF)', () => {
    const index = build({
      a: 'widget widget widget alpha',
      b: 'widget beta',
      c: 'widget gamma',
      d: 'zebra delta',
    });
    assert.ok(index.idf('zebra') > index.idf('widget'));
    // "a" has the common term three times, "d" has the rare term once: the rare term wins.
    const hits = index.search('widget zebra', 4);
    assert.equal(hits[0].id, 'd#0');
  });

  it('prefers the shorter document at equal term frequency (length normalization)', () => {
    const index = build({
      short: 'cache eviction',
      long: 'cache eviction policy notes about many unrelated subjects such as logging metrics tracing alerts dashboards',
    });
    const hits = index.search('eviction', 2);
    assert.deepEqual(
      hits.map((h) => h.chunk.path),
      ['short', 'long'],
    );
  });

  it('returns nothing for empty, stopword-only or unknown queries and for k <= 0', () => {
    const index = build(docs);
    assert.deepEqual(index.search('', 5), []);
    assert.deepEqual(index.search('   ', 5), []);
    assert.deepEqual(index.search('the of and', 5), []);
    assert.deepEqual(index.search('nonexistentterm', 5), []);
    assert.deepEqual(index.search('password', 0), []);
    assert.deepEqual(new Bm25Index().search('password', 5), []);
  });

  it('respects k', () => {
    assert.equal(build(docs).search('invoice rate password', 2).length, 2);
  });

  it('updates postings when a file is replaced or removed', () => {
    const index = build(docs);
    assert.equal(index.documentFrequency('password'), 1);
    index.addFile('auth.py', [chunk('auth.py', 'def login(user): session token')]);
    assert.equal(index.documentFrequency('password'), 0);
    assert.equal(index.search('login', 5)[0].chunk.path, 'auth.py');
    assert.equal(index.removeFile('auth.py'), true);
    assert.equal(index.removeFile('auth.py'), false);
    assert.deepEqual(index.search('login', 5), []);
    assert.equal(index.fileCount, 3);
    assert.equal(index.chunkCount, 3);
  });
});
