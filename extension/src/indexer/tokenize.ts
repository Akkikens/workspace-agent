// Code-aware tokenizer. Pure: no `vscode` import.

export const STOPWORDS: ReadonlySet<string> = new Set([
  'a', 'an', 'and', 'are', 'as', 'at', 'be', 'by', 'do', 'does', 'for', 'from', 'how', 'if', 'in',
  'is', 'it', 'its', 'of', 'on', 'or', 'that', 'the', 'this', 'to', 'was', 'what', 'where', 'which',
  'who', 'why', 'with', 'else', 'return', 'const', 'let', 'var', 'new', 'null', 'undefined', 'true',
  'false', 'void', 'import', 'self',
]);

const WORD = /[A-Za-z0-9_$]+(?:-[A-Za-z0-9_$]+)*/g;

/** Split one identifier on `_`, `-`, `$` and camel/Pascal case boundaries (keeps acronyms together). */
export function splitIdentifier(word: string): string[] {
  return word
    .split(/[_\-$]+/)
    .flatMap((part) =>
      part
        .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
        .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
        .replace(/([A-Za-z])([0-9]+)$/g, '$1 $2')
        .split(' '),
    )
    .filter((p) => p.length > 0);
}

function keep(token: string): boolean {
  return token.length > 1 && !/^\d+$/.test(token) && !STOPWORDS.has(token);
}

/**
 * Tokenize text for BM25: identifier parts, lowercased, plus the original compound
 * (e.g. `parseInvoiceTotal` -> `parse`, `invoice`, `total`, `parseinvoicetotal`).
 */
export function tokenize(text: string): string[] {
  const out: string[] = [];
  for (const match of text.matchAll(WORD)) {
    const word = match[0];
    const parts = splitIdentifier(word).map((p) => p.toLowerCase());
    for (const p of parts) if (keep(p)) out.push(p);
    if (parts.length > 1) {
      const compound = word.toLowerCase();
      if (keep(compound)) out.push(compound);
    }
  }
  return out;
}
