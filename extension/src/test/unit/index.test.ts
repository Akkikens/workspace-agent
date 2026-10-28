import assert from 'node:assert/strict';
import { Embedder } from '../../indexer/embeddings';
import { CodeIndex, RRF_K, reciprocalRankFusion } from '../../indexer/index';

/**
 * Deterministic stand-in for an embedding model: each dimension is a "concept" made of
 * synonyms, so a query can be close to a chunk that shares no literal term with it.
 */
class FakeEmbedder implements Embedder {
  readonly name = 'fake';
  calls = 0;
  private static readonly CONCEPTS = [
    ['invoice', 'billing', 'charge', 'total', 'price', 'cost'],
    ['password', 'credential', 'login', 'hash', 'secret'],
    ['limiter', 'throttle', 'quota', 'tokens', 'rate'],
  ];

  constructor(private readonly failAfter = Infinity) {}

  async embed(texts: string[]): Promise<number[][]> {
    if (++this.calls > this.failAfter) throw new Error('connection refused');
    return texts.map((t) => {
      const words = t.toLowerCase().split(/[^a-z]+/);
      return FakeEmbedder.CONCEPTS.map((syn) => words.filter((w) => syn.some((s) => w.includes(s))).length);
    });
  }
}

const FILES: Record<string, string> = {
  'invoice.ts': 'export function computeInvoiceTotal(items) {\n  return items.reduce((s, i) => s + i.price, 0);\n}',
  'auth.py': 'def hash_password(password, salt):\n    return pbkdf2(password, salt)',
  'limiter.cs': 'public class RateLimiter {\n  int tokens;\n  bool TryAcquire() { return tokens-- > 0; }\n}',
};

async function build(embedder?: Embedder): Promise<CodeIndex> {
  const index = new CodeIndex({ embedder });
  for (const [path, text] of Object.entries(FILES)) await index.addFile(path, text);
  return index;
}

describe('reciprocalRankFusion', () => {
  it('sums 1/(k + rank) over lists', () => {
    const fused = reciprocalRankFusion([
      ['a', 'b', 'c'],
      ['b', 'a'],
    ]);
    assert.equal(fused.get('a'), 1 / (RRF_K + 1) + 1 / (RRF_K + 2));
    assert.equal(fused.get('b'), 1 / (RRF_K + 2) + 1 / (RRF_K + 1));
    assert.equal(fused.get('c'), 1 / (RRF_K + 3));
  });

  it('ranks an item found by both lists above items found by one', () => {
    const fused = reciprocalRankFusion([
      ['x', 'shared'],
      ['y', 'shared'],
    ]);
    const order = [...fused.entries()].sort((p, q) => q[1] - p[1]).map(([id]) => id);
    assert.equal(order[0], 'shared');
  });

  it('honours a custom k', () => {
    assert.equal(reciprocalRankFusion([['a']], 0).get('a'), 1);
  });
});

describe('CodeIndex', () => {
  it('is BM25-only without an embedder', async () => {
    const index = await build();
    assert.deepEqual(index.stats(), { files: 3, chunks: 3, mode: 'bm25' });
    const hits = await index.search('hash password', 3);
    assert.equal(hits[0].chunk.path, 'auth.py');
    assert.equal(hits[0].bm25Rank, 1);
    assert.equal(hits[0].vectorRank, undefined);
  });

  it('finds a semantically related chunk in hybrid mode that BM25 alone misses', async () => {
    const query = 'billing cost'; // no literal overlap with any file
    assert.deepEqual(await (await build()).search(query, 3), []);

    const index = await build(new FakeEmbedder());
    assert.equal(index.mode, 'hybrid');
    const hits = await index.search(query, 3);
    assert.equal(hits[0].chunk.path, 'invoice.ts');
    assert.equal(hits[0].vectorRank, 1);
    assert.equal(hits[0].bm25Rank, undefined);
  });

  it('fuses both lists: an exact keyword hit that is also semantically close ranks first', async () => {
    const index = await build(new FakeEmbedder());
    const hits = await index.search('TryAcquire throttle', 3);
    assert.equal(hits[0].chunk.path, 'limiter.cs');
    assert.equal(hits[0].bm25Rank, 1);
    assert.equal(hits[0].vectorRank, 1);
    assert.equal(hits[0].score, 2 / (RRF_K + 1));
  });

  it('falls back to BM25 and records why when the embedder fails while indexing', async () => {
    const index = await build(new FakeEmbedder(1)); // first file embeds, second fails
    assert.equal(index.mode, 'bm25');
    const stats = index.stats();
    assert.equal(stats.embedder, 'fake');
    assert.match(stats.fallbackReason ?? '', /auth\.py: connection refused/);
    const hits = await index.search('hash password', 3);
    assert.equal(hits[0].chunk.path, 'auth.py');
  });

  it('falls back to BM25 results when the embedder fails at query time', async () => {
    const index = await build(new FakeEmbedder(3)); // three files embed, the query fails
    const hits = await index.search('hash password', 3);
    assert.equal(hits[0].chunk.path, 'auth.py');
    assert.equal(index.mode, 'bm25');
    assert.match(index.stats().fallbackReason ?? '', /query time/);
  });

  it('returns nothing for an empty query, even in hybrid mode', async () => {
    const index = await build(new FakeEmbedder());
    assert.deepEqual(await index.search('', 5), []);
    assert.deepEqual(await index.search('  ', 5), []);
  });

  it('replaces and removes files', async () => {
    const index = await build(new FakeEmbedder());
    await index.addFile('auth.py', 'def login(user):\n    return session');
    assert.equal((await index.search('hash password', 3)).some((h) => h.chunk.path === 'auth.py' && h.bm25Rank !== undefined), false);
    assert.equal(index.removeFile('auth.py'), true);
    assert.equal(index.stats().files, 2);
    assert.equal((await index.search('login credential', 3)).some((h) => h.chunk.path === 'auth.py'), false);
  });
});
