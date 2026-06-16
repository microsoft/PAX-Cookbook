/**
 * Advanced arguments analyzer.
 *
 * Tokenizes the free-form Advanced Arguments box, identifies switches, flags
 * duplicates and removed switches, and forwards the raw text to the secret
 * scanner. Phase 2 ships the analyzer; the UI in later phases decides
 * presentation.
 */

import {
  PAX_SWITCH_CATALOG,
  REMOVED_OR_UNSUPPORTED_SWITCHES,
} from '../data/pax-switch-catalog';
import { scanForSecrets, type SecretMatch } from './secretScanner';

export interface AdvancedArgToken {
  /** The raw token as it appeared in the input. */
  raw: string;
  /** True when the token looks like a PowerShell switch (`-Name`). */
  isSwitch: boolean;
  /** Switch name without the leading hyphen, when `isSwitch` is true. */
  switchName?: string;
}

export interface AdvancedArgsAnalysis {
  tokens: readonly AdvancedArgToken[];
  /** All switch names present in the input, in first-seen order. */
  switchesPresent: readonly string[];
  /** Switch names that appear more than once. */
  duplicates: readonly string[];
  /** Switch names that appear in the removed/unsupported catalog. */
  removedSwitches: readonly string[];
  /** Switch names that are not in the known catalog and are not removed. */
  unknownSwitches: readonly string[];
  /** Heuristic secret matches from the raw input text. */
  secretWarnings: readonly SecretMatch[];
}

const SWITCH_TOKEN = /^-[A-Za-z][A-Za-z0-9_-]*$/;

/**
 * Conservative tokenizer that respects single/double-quoted strings. Does not
 * attempt to interpret PowerShell escape syntax; quoted strings are returned
 * including their surrounding quotes.
 */
export function tokenizeAdvancedArgs(text: string): readonly AdvancedArgToken[] {
  const tokens: AdvancedArgToken[] = [];
  if (!text) {
    return tokens;
  }
  let i = 0;
  const len = text.length;
  while (i < len) {
    // Skip whitespace.
    while (i < len && /\s/.test(text[i]!)) {
      i++;
    }
    if (i >= len) {
      break;
    }
    const ch = text[i]!;
    let raw: string;
    if (ch === '"' || ch === "'") {
      const quote = ch;
      const start = i;
      i++;
      while (i < len && text[i] !== quote) {
        i++;
      }
      // Include closing quote when present.
      if (i < len) {
        i++;
      }
      raw = text.slice(start, i);
    } else {
      const start = i;
      while (i < len && !/\s/.test(text[i]!)) {
        i++;
      }
      raw = text.slice(start, i);
    }
    const isSwitch = SWITCH_TOKEN.test(raw);
    const token: AdvancedArgToken = isSwitch
      ? { raw, isSwitch: true, switchName: raw.slice(1) }
      : { raw, isSwitch: false };
    tokens.push(token);
  }
  return tokens;
}

/**
 * Run the full analysis: tokenize, classify, and scan for secrets. Pure
 * function; safe to call on every keystroke.
 */
export function analyzeAdvancedArgs(text: string): AdvancedArgsAnalysis {
  const tokens = tokenizeAdvancedArgs(text);
  const seen = new Set<string>();
  const duplicates = new Set<string>();
  const switchesPresent: string[] = [];
  for (const t of tokens) {
    if (!t.isSwitch || !t.switchName) {
      continue;
    }
    if (seen.has(t.switchName)) {
      duplicates.add(t.switchName);
    } else {
      seen.add(t.switchName);
      switchesPresent.push(t.switchName);
    }
  }

  const knownNames = new Set(PAX_SWITCH_CATALOG.map(s => s.name));
  const removedNames = new Set(REMOVED_OR_UNSUPPORTED_SWITCHES.map(s => s.name));

  const removedSwitches: string[] = [];
  const unknownSwitches: string[] = [];
  for (const name of switchesPresent) {
    if (removedNames.has(name)) {
      removedSwitches.push(name);
    } else if (!knownNames.has(name)) {
      unknownSwitches.push(name);
    }
  }

  return {
    tokens,
    switchesPresent,
    duplicates: Array.from(duplicates),
    removedSwitches,
    unknownSwitches,
    secretWarnings: scanForSecrets(text),
  };
}
