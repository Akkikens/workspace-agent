// Splits source files into retrieval chunks. Pure: no `vscode` import.

export interface Chunk {
  /** Workspace-relative path with forward slashes. */
  path: string;
  /** 1-based, inclusive. */
  startLine: number;
  /** 1-based, inclusive. */
  endLine: number;
  text: string;
}

export interface ChunkOptions {
  /** Hard cap on lines per chunk. */
  maxLines?: number;
  /** Lines shared between consecutive windows when a long block has to be cut mid-code. */
  overlap?: number;
  /** Segments shorter than this are merged with the following segment. */
  minLines?: number;
  /** Files larger than this (in characters) are skipped. */
  maxFileBytes?: number;
  /** Lines longer than this are treated as minified/generated and blanked out. */
  maxLineLength?: number;
}

export const DEFAULT_CHUNK_OPTIONS: Required<ChunkOptions> = {
  maxLines: 60,
  overlap: 5,
  minLines: 6,
  maxFileBytes: 512 * 1024,
  maxLineLength: 1000,
};

const MAX_DECLARATION_INDENT = 4;

// A top-level-ish declaration across TS/JS, Python, C#, Java, Go, Rust.
const DECLARATION =
  /^(?:export\s|public\s|private\s|protected\s|internal\s|static\s|abstract\s|sealed\s|partial\s|async\s+(?:def|function)\b|def\s|class\s|interface\s|function[\s*]|enum\s|struct\s|record\s|namespace\s|module\s|type\s+\w+|impl\b|fn\s|func\s|trait\s)/;

// Lines that belong to the declaration that follows them (comments, decorators, C# attributes).
const LEADING_ATTACHMENT = /^(?:\/\/|\/\*|\*|#(?!include)|@\w|\[\w)/;

export function indentWidth(line: string): number {
  let width = 0;
  for (const ch of line) {
    if (ch === ' ') width += 1;
    else if (ch === '\t') width += 4;
    else break;
  }
  return width;
}

export function isDeclarationStart(line: string): boolean {
  if (line.trim() === '' || indentWidth(line) > MAX_DECLARATION_INDENT) return false;
  return DECLARATION.test(line.trimStart());
}

export function isProbablyBinary(content: string): boolean {
  const sample = content.length > 8000 ? content.slice(0, 8000) : content;
  return sample.includes('\u0000');
}

function isBlank(line: string): boolean {
  return line.trim() === '';
}

/** Walk upwards over contiguous comment/decorator lines so they stay with their declaration. */
function attachLeading(lines: string[], declIndex: number): number {
  const declIndent = indentWidth(lines[declIndex]);
  let i = declIndex;
  while (i > 0) {
    const prev = lines[i - 1];
    const trimmed = prev.trimStart();
    // Block-comment continuation lines (` * text`, ` */`) sit one column to the right of the `/**`.
    const indent = indentWidth(prev);
    const indentOk = indent === declIndent || (trimmed.startsWith('*') && indent === declIndent + 1);
    if (isBlank(prev) || !indentOk || !LEADING_ATTACHMENT.test(trimmed)) break;
    i--;
  }
  return i;
}

type Range = [number, number]; // 0-based inclusive

function trimRange(lines: string[], [s, e]: Range): Range | undefined {
  while (s <= e && isBlank(lines[s])) s++;
  while (e >= s && isBlank(lines[e])) e--;
  return s <= e ? [s, e] : undefined;
}

function splitLong(lines: string[], [s, e]: Range, o: Required<ChunkOptions>): Range[] {
  const out: Range[] = [];
  let start = s;
  while (start <= e) {
    let end = Math.min(start + o.maxLines - 1, e);
    let cutAtBlank = false;
    if (end < e) {
      // Prefer ending the window on a blank line in its second half.
      for (let i = end; i > start + Math.floor(o.maxLines / 2); i--) {
        if (isBlank(lines[i])) {
          end = i;
          cutAtBlank = true;
          break;
        }
      }
    }
    out.push([start, end]);
    if (end >= e) break;
    const next = cutAtBlank ? end + 1 : end + 1 - o.overlap;
    start = Math.max(next, start + 1);
  }
  return out;
}

export function chunkFile(path: string, content: string, options: ChunkOptions = {}): Chunk[] {
  const o: Required<ChunkOptions> = { ...DEFAULT_CHUNK_OPTIONS, ...options };
  if (content.length === 0 || content.length > o.maxFileBytes || isProbablyBinary(content)) return [];

  const lines = content.split(/\r\n|\r|\n/).map((l) => (l.length > o.maxLineLength ? '' : l));

  const starts = new Set<number>([0]);
  for (let i = 0; i < lines.length; i++) {
    if (isDeclarationStart(lines[i])) starts.add(attachLeading(lines, i));
  }
  const sorted = [...starts].sort((a, b) => a - b);

  const segments: Range[] = [];
  sorted.forEach((s, j) => {
    const end = (j + 1 < sorted.length ? sorted[j + 1] : lines.length) - 1;
    const trimmed = trimRange(lines, [s, end]);
    if (trimmed) segments.push(trimmed);
  });

  // Merge tiny segments (imports, one-line declarations) forward while under the cap.
  const merged: Range[] = [];
  for (const seg of segments) {
    const last = merged[merged.length - 1];
    if (last && last[1] - last[0] + 1 < o.minLines && seg[1] - last[0] + 1 <= o.maxLines) {
      last[1] = seg[1];
    } else {
      merged.push([seg[0], seg[1]]);
    }
  }

  const chunks: Chunk[] = [];
  for (const seg of merged) {
    const windows = seg[1] - seg[0] + 1 > o.maxLines ? splitLong(lines, seg, o) : [seg];
    for (const w of windows) {
      const r = trimRange(lines, w);
      if (!r) continue;
      chunks.push({
        path,
        startLine: r[0] + 1,
        endLine: r[1] + 1,
        text: lines.slice(r[0], r[1] + 1).join('\n'),
      });
    }
  }
  return chunks;
}
