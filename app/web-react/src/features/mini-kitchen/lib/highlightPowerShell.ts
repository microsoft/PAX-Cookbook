/**
 * Tiny PowerShell tokenizer for rendered command preview.
 *
 * Splits a PowerShell 7 command string (single-line or multiline-with-
 * backtick-continuation) into typed spans so the UI can render each span
 * with its own color class. The tokenizer is hand-rolled (no regex) so
 * the behavior is fully deterministic and easy to reason about.
 *
 * Token kinds:
 *   - 'whitespace'   : runs of space / tab / newline (preserve verbatim
 *                      so multiline layout is identical to the input)
 *   - 'continuation' : a backtick that immediately precedes a newline
 *                      (the PS7 line-continuation marker)
 *   - 'switch'       : a token that begins with '-' followed by at least
 *                      one identifier-shaped char (parameter names like
 *                      -Auth, -StartDate)
 *   - 'string'       : a quoted run starting with ' or " — the closing
 *                      quote of the same kind ends the run; doubled-quote
 *                      escapes inside the same quote kind are tolerated
 *   - 'number'       : a bareword that parses as a finite number
 *   - 'punct'        : a single ',', '(', ')', or '@' (array literal
 *                      opener / separators)
 *   - 'identifier'   : every other bareword run — covers the leading
 *                      cmdlet name (e.g. `pwsh`) and any unquoted value
 */

export type PowerShellTokenKind =
  | 'whitespace'
  | 'continuation'
  | 'switch'
  | 'string'
  | 'number'
  | 'punct'
  | 'identifier';

export interface PowerShellToken {
  kind: PowerShellTokenKind;
  text: string;
}

export function tokenizePowerShell(input: string): PowerShellToken[] {
  const out: PowerShellToken[] = [];
  const len = input.length;
  let i = 0;

  while (i < len) {
    const ch = input.charCodeAt(i);

    if (isWhitespaceCode(ch)) {
      let j = i + 1;
      while (j < len && isWhitespaceCode(input.charCodeAt(j))) j++;
      out.push({ kind: 'whitespace', text: input.slice(i, j) });
      i = j;
      continue;
    }

    if (ch === 0x60 /* ` */ && i + 1 < len) {
      const next = input.charCodeAt(i + 1);
      if (next === 0x0a /* \n */ || next === 0x0d /* \r */) {
        out.push({ kind: 'continuation', text: '`' });
        i += 1;
        continue;
      }
    }

    if (ch === 0x27 /* ' */ || ch === 0x22 /* " */) {
      const quote = ch;
      let j = i + 1;
      while (j < len) {
        const c = input.charCodeAt(j);
        if (c === quote) {
          if (j + 1 < len && input.charCodeAt(j + 1) === quote) {
            j += 2;
            continue;
          }
          j += 1;
          break;
        }
        if (c === 0x60 /* ` */ && quote === 0x22 && j + 1 < len) {
          j += 2;
          continue;
        }
        j += 1;
      }
      out.push({ kind: 'string', text: input.slice(i, j) });
      i = j;
      continue;
    }

    if (ch === 0x2d /* - */ && i + 1 < len && isParamNameCode(input.charCodeAt(i + 1))) {
      let j = i + 1;
      while (j < len && isParamNameCode(input.charCodeAt(j))) j++;
      out.push({ kind: 'switch', text: input.slice(i, j) });
      i = j;
      continue;
    }

    if (ch === 0x2c /* , */ || ch === 0x28 /* ( */ || ch === 0x29 /* ) */ || ch === 0x40 /* @ */) {
      out.push({ kind: 'punct', text: input[i]! });
      i += 1;
      continue;
    }

    let j = i;
    while (j < len) {
      const c = input.charCodeAt(j);
      if (isWhitespaceCode(c)) break;
      if (c === 0x27 || c === 0x22) break;
      if (c === 0x2c || c === 0x28 || c === 0x29) break;
      if (
        c === 0x60 &&
        j + 1 < len &&
        (input.charCodeAt(j + 1) === 0x0a || input.charCodeAt(j + 1) === 0x0d)
      ) {
        break;
      }
      j += 1;
    }
    const text = input.slice(i, j);
    out.push({ kind: isNumericToken(text) ? 'number' : 'identifier', text });
    i = j;
  }

  return out;
}

function isWhitespaceCode(c: number): boolean {
  return c === 0x20 || c === 0x09 || c === 0x0a || c === 0x0d;
}

function isParamNameCode(c: number): boolean {
  return (
    (c >= 0x41 && c <= 0x5a) ||
    (c >= 0x61 && c <= 0x7a) ||
    (c >= 0x30 && c <= 0x39) ||
    c === 0x5f
  );
}

function isNumericToken(text: string): boolean {
  if (text.length === 0) return false;
  const n = Number(text);
  return Number.isFinite(n) && String(n) === text;
}
