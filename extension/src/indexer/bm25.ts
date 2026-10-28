// BM25 over chunks with an inverted index and per-file incremental updates. Pure: no `vscode` import.

import { Chunk } from './chunker';
import { tokenize } from './tokenize';

export const BM25_K1 = 1.2;
export const BM25_B = 0.75;

interface Doc {
  chunk: Chunk;
  length: number;
  tf: Map<string, number>;
}

export interface Bm25Hit {
  id: string;
  chunk: Chunk;
  score: number;
}

export class Bm25Index {
  private readonly docs = new Map<string, Doc>();
  private readonly postings = new Map<string, Set<string>>();
  private readonly files = new Map<string, string[]>();
  private totalLength = 0;

  constructor(
    private readonly k1 = BM25_K1,
    private readonly b = BM25_B,
  ) {}

  get fileCount(): number {
    return this.files.size;
  }

  get chunkCount(): number {
    return this.docs.size;
  }

  hasFile(path: string): boolean {
    return this.files.has(path);
  }

  chunkIds(path: string): readonly string[] {
    return this.files.get(path) ?? [];
  }

  getChunk(id: string): Chunk | undefined {
    return this.docs.get(id)?.chunk;
  }

  allChunkIds(): IterableIterator<string> {
    return this.docs.keys();
  }

  /** Replace every chunk of `path` with `chunks`. Returns the chunk ids. */
  addFile(path: string, chunks: readonly Chunk[]): string[] {
    this.removeFile(path);
    const ids: string[] = [];
    chunks.forEach((chunk, i) => {
      const id = `${path}#${i}`;
      const tokens = tokenize(chunk.text);
      const tf = new Map<string, number>();
      for (const t of tokens) tf.set(t, (tf.get(t) ?? 0) + 1);
      this.docs.set(id, { chunk, length: tokens.length, tf });
      this.totalLength += tokens.length;
      for (const term of tf.keys()) {
        let set = this.postings.get(term);
        if (!set) this.postings.set(term, (set = new Set()));
        set.add(id);
      }
      ids.push(id);
    });
    this.files.set(path, ids);
    return ids;
  }

  removeFile(path: string): boolean {
    const ids = this.files.get(path);
    if (!ids) return false;
    for (const id of ids) {
      const doc = this.docs.get(id);
      if (!doc) continue;
      this.totalLength -= doc.length;
      for (const term of doc.tf.keys()) {
        const set = this.postings.get(term);
        if (!set) continue;
        set.delete(id);
        if (set.size === 0) this.postings.delete(term);
      }
      this.docs.delete(id);
    }
    this.files.delete(path);
    return true;
  }

  documentFrequency(term: string): number {
    return this.postings.get(term)?.size ?? 0;
  }

  idf(term: string): number {
    const n = this.docs.size;
    const df = this.documentFrequency(term);
    return Math.log(1 + (n - df + 0.5) / (df + 0.5));
  }

  search(query: string, k: number): Bm25Hit[] {
    const n = this.docs.size;
    if (n === 0 || k <= 0) return [];
    const terms = [...new Set(tokenize(query))];
    if (terms.length === 0) return [];
    const avgdl = this.totalLength / n || 1;
    const scores = new Map<string, number>();
    for (const term of terms) {
      const ids = this.postings.get(term);
      if (!ids) continue;
      const idf = this.idf(term);
      for (const id of ids) {
        const doc = this.docs.get(id)!;
        const f = doc.tf.get(term)!;
        const norm = f + this.k1 * (1 - this.b + (this.b * doc.length) / avgdl);
        scores.set(id, (scores.get(id) ?? 0) + (idf * (f * (this.k1 + 1))) / norm);
      }
    }
    return [...scores.entries()]
      .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
      .slice(0, k)
      .map(([id, score]) => ({ id, chunk: this.docs.get(id)!.chunk, score }));
  }
}
