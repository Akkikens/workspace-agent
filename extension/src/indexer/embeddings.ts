// Optional embedding providers. Pure: no `vscode` import.

export interface Embedder {
  /** Human-readable name for stats, e.g. `ollama:nomic-embed-text`. */
  readonly name: string;
  embed(texts: string[]): Promise<number[][]>;
}

type FetchLike = typeof fetch;

/** Calls a local Ollama server: `POST {url}/api/embed` with `{ model, input }`. */
export class OllamaEmbedder implements Embedder {
  readonly name: string;

  constructor(
    private readonly url: string,
    private readonly model: string,
    private readonly fetchImpl: FetchLike = fetch,
    private readonly timeoutMs = 30_000,
  ) {
    this.name = `ollama:${model}`;
  }

  async embed(texts: string[]): Promise<number[][]> {
    if (texts.length === 0) return [];
    const res = await this.fetchImpl(`${this.url.replace(/\/+$/, '')}/api/embed`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ model: this.model, input: texts }),
      signal: AbortSignal.timeout(this.timeoutMs),
    });
    if (!res.ok) {
      const body = await res.text().catch(() => '');
      throw new Error(`Ollama /api/embed returned HTTP ${res.status}${body ? `: ${body.slice(0, 200)}` : ''}`);
    }
    const json = (await res.json()) as { embeddings?: unknown };
    const vectors = json.embeddings;
    if (!Array.isArray(vectors) || vectors.length !== texts.length) {
      throw new Error(`Ollama /api/embed returned ${Array.isArray(vectors) ? vectors.length : 'no'} embeddings for ${texts.length} inputs`);
    }
    return vectors as number[][];
  }
}

export function cosineSimilarity(a: readonly number[], b: readonly number[]): number {
  if (a.length !== b.length) throw new Error(`Vector dimension mismatch: ${a.length} vs ${b.length}`);
  let dot = 0;
  let na = 0;
  let nb = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i] * b[i];
    na += a[i] * a[i];
    nb += b[i] * b[i];
  }
  if (na === 0 || nb === 0) return 0;
  return dot / (Math.sqrt(na) * Math.sqrt(nb));
}
