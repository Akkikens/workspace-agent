// CodeIndex: BM25, optionally fused with embeddings via reciprocal rank fusion. Pure: no `vscode` import.

import { Bm25Index } from './bm25';
import { Chunk, ChunkOptions, chunkFile } from './chunker';
import { Embedder, cosineSimilarity } from './embeddings';

export type IndexMode = 'bm25' | 'hybrid';

export interface IndexStats {
  files: number;
  chunks: number;
  mode: IndexMode;
  embedder?: string;
  /** Why a configured embedder is not being used (set once it has failed). */
  fallbackReason?: string;
}

export interface SearchHit {
  chunk: Chunk;
  /** BM25 score in bm25 mode, fused RRF score in hybrid mode. */
  score: number;
  /** 1-based rank in the BM25 list, if the chunk appeared there. */
  bm25Rank?: number;
  /** 1-based rank in the vector list, if the chunk appeared there. */
  vectorRank?: number;
}

export interface CodeIndexOptions {
  embedder?: Embedder;
  chunkOptions?: ChunkOptions;
  embedBatchSize?: number;
}

export const RRF_K = 60;

/** Reciprocal rank fusion: score(d) = sum over lists of 1 / (k + rank), rank 1-based. */
export function reciprocalRankFusion(rankings: readonly (readonly string[])[], k = RRF_K): Map<string, number> {
  const fused = new Map<string, number>();
  for (const list of rankings) {
    list.forEach((id, i) => fused.set(id, (fused.get(id) ?? 0) + 1 / (k + i + 1)));
  }
  return fused;
}

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

export class CodeIndex {
  private readonly bm25 = new Bm25Index();
  private readonly vectors = new Map<string, number[]>();
  private embeddingsFailed = false;
  private fallbackReason?: string;

  constructor(private readonly options: CodeIndexOptions = {}) {}

  get mode(): IndexMode {
    return this.options.embedder && !this.embeddingsFailed ? 'hybrid' : 'bm25';
  }

  hasFile(path: string): boolean {
    return this.bm25.hasFile(path);
  }

  /** Index (or re-index) one file. Returns the number of chunks stored. */
  async addFile(path: string, content: string): Promise<number> {
    const chunks = chunkFile(path, content, this.options.chunkOptions);
    this.removeFile(path);
    if (chunks.length === 0) return 0;
    const ids = this.bm25.addFile(path, chunks);
    if (this.mode === 'hybrid') {
      try {
        const batch = this.options.embedBatchSize ?? 32;
        for (let i = 0; i < chunks.length; i += batch) {
          const slice = chunks.slice(i, i + batch);
          const vecs = await this.options.embedder!.embed(slice.map((c) => `${c.path}\n${c.text}`));
          vecs.forEach((v, j) => this.vectors.set(ids[i + j], v));
        }
      } catch (err) {
        this.fallBack(`embedder failed while indexing ${path}: ${errorMessage(err)}`);
      }
    }
    return chunks.length;
  }

  removeFile(path: string): boolean {
    for (const id of this.bm25.chunkIds(path)) this.vectors.delete(id);
    return this.bm25.removeFile(path);
  }

  async search(query: string, k: number): Promise<SearchHit[]> {
    // An empty query has no meaning for either list; without this guard hybrid mode would rank every chunk.
    if (k <= 0 || query.trim() === '') return [];
    const depth = Math.max(k * 4, 50);
    const lexical = this.bm25.search(query, depth);
    if (this.mode !== 'hybrid') {
      return lexical.slice(0, k).map((h, i) => ({ chunk: h.chunk, score: h.score, bm25Rank: i + 1 }));
    }

    let vectorRanking: string[];
    try {
      const [q] = await this.options.embedder!.embed([query]);
      vectorRanking = [...this.vectors.entries()]
        .map(([id, v]) => [id, cosineSimilarity(q, v)] as const)
        .sort((a, b) => b[1] - a[1])
        .slice(0, depth)
        .map(([id]) => id);
    } catch (err) {
      this.fallBack(`embedder failed at query time: ${errorMessage(err)}`);
      return lexical.slice(0, k).map((h, i) => ({ chunk: h.chunk, score: h.score, bm25Rank: i + 1 }));
    }

    const lexicalIds = lexical.map((h) => h.id);
    const fused = reciprocalRankFusion([lexicalIds, vectorRanking]);
    const bm25Rank = new Map(lexicalIds.map((id, i) => [id, i + 1]));
    const vecRank = new Map(vectorRanking.map((id, i) => [id, i + 1]));
    return [...fused.entries()]
      .sort((a, b) => b[1] - a[1] || (bm25Rank.get(a[0]) ?? Infinity) - (bm25Rank.get(b[0]) ?? Infinity))
      .slice(0, k)
      .map(([id, score]) => ({
        chunk: this.bm25.getChunk(id)!,
        score,
        bm25Rank: bm25Rank.get(id),
        vectorRank: vecRank.get(id),
      }));
  }

  stats(): IndexStats {
    const stats: IndexStats = { files: this.bm25.fileCount, chunks: this.bm25.chunkCount, mode: this.mode };
    if (this.options.embedder) stats.embedder = this.options.embedder.name;
    if (this.fallbackReason) stats.fallbackReason = this.fallbackReason;
    return stats;
  }

  private fallBack(reason: string): void {
    this.embeddingsFailed = true;
    this.fallbackReason = reason;
    this.vectors.clear();
  }
}
