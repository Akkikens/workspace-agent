// Builds the `chunks` payload for POST /api/code/ask from search hits. Pure: no `vscode` import.

import { ContextChunk } from './backend';
import { SearchHit } from './indexer/index';

export const DEFAULT_CONTEXT_CHAR_BUDGET = 12_000;

export interface ContextResult {
  chunks: ContextChunk[];
  /** Hits that did not fit the budget (or were duplicates). */
  omitted: number;
  totalChars: number;
}

/**
 * Take hits in rank order until the total text length would exceed `maxChars`.
 * A top hit that alone exceeds the budget is truncated on a line boundary (endLine adjusted)
 * so the request is never empty when there is something relevant.
 */
export function buildContextChunks(hits: readonly SearchHit[], maxChars = DEFAULT_CONTEXT_CHAR_BUDGET): ContextResult {
  const chunks: ContextChunk[] = [];
  const seen = new Set<string>();
  let total = 0;
  let omitted = 0;
  for (const { chunk } of hits) {
    const key = `${chunk.path}:${chunk.startLine}-${chunk.endLine}`;
    if (seen.has(key)) {
      omitted++;
      continue;
    }
    seen.add(key);
    const remaining = maxChars - total;
    if (chunk.text.length <= remaining) {
      chunks.push({ path: chunk.path, startLine: chunk.startLine, endLine: chunk.endLine, text: chunk.text });
      total += chunk.text.length;
    } else if (chunks.length === 0 && remaining > 0) {
      const lines = chunk.text.split('\n');
      const kept: string[] = [];
      let size = 0;
      for (const line of lines) {
        const add = line.length + (kept.length > 0 ? 1 : 0);
        if (size + add > remaining) break;
        kept.push(line);
        size += add;
      }
      if (kept.length === 0) {
        omitted++;
        continue;
      }
      chunks.push({ path: chunk.path, startLine: chunk.startLine, endLine: chunk.startLine + kept.length - 1, text: kept.join('\n') });
      total += size;
    } else {
      omitted++;
    }
  }
  return { chunks, omitted, totalChars: total };
}

/** `context: src/a.ts:12-40, src/b.py:1-20` */
export function formatContextSummary(chunks: readonly ContextChunk[]): string {
  if (chunks.length === 0) return 'context: (none)';
  return `context: ${chunks.map((c) => `${c.path}:${c.startLine}-${c.endLine}`).join(', ')}`;
}
