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
  parseError?: string;
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

export const ADVANCED_ARGUMENTS_ERROR = 'advanced.extraArguments requires exact supported option names and literal values. Use single or double quotes for literal strings, invariant numbers, and $true/$false for switches; expressions, duplicates, and structured/auth/output overrides are not allowed.';
export interface PaxParameter {
  name: string;
  value: string | readonly string[] | boolean | number;
  type: 'string' | 'string[]' | 'bool' | 'int' | 'double';
}
type AdvancedOption = readonly [name: string, type: 'int' | 'double' | 'bool' | 'string', minimum?: number, maximum?: number];
export const ADVANCED_OPTIONS: readonly AdvancedOption[] = [
  ['BlockHours', 'double', 0.016667, 24],
  ['PartitionHours', 'int', 1, 72],
  ['MaxPartitions', 'int', 1, 1000],
  ['ResultSize', 'int', 1, 10000],
  ['PacingMs', 'int', 0, 10000],
  ['FlatDepth', 'int'],
  ['MaxConcurrency', 'int'],
  ['EnableParallel', 'bool'],
  ['MaxParallelGroups', 'int', 0, 50],
  ['ParallelMode', 'string'],
  ['ExplosionThreads', 'int', 0, 32],
  ['DisableAdaptive', 'bool'],
  ['ProgressSmoothingAlpha', 'double', 0, 1],
  ['HighLatencyMs', 'int', 1000, 600000],
  ['MemoryPressureMB', 'int', 256, 32768],
  ['MaxMemoryMB', 'int', -1, 65536],
  ['StatusIntervalSeconds', 'int', 30, 600],
  ['LowLatencyMs', 'int', 100, 600000],
  ['LowLatencyConsecutive', 'int', 1, 10],
  ['ThroughputDropPct', 'int', 1, 100],
  ['ThroughputSmoothingAlpha', 'double', 0, 1],
  ['AdaptiveConcurrencyCeiling', 'int', 1, 50],
  ['ExportProgressInterval', 'int', 1, 10000],
  ['StreamingSchemaSample', 'int', 100, 50000],
  ['StreamingChunkSize', 'int', 100, 50000],
  ['CircuitBreakerThreshold', 'int', 1, 50],
  ['CircuitBreakerCooldownSeconds', 'int', 5, 3600],
  ['BackoffBaseSeconds', 'double', 0.1, 120],
  ['BackoffMaxSeconds', 'int', 1, 600],
  ['EmitMetricsJson', 'bool'],
  ['AutoCompleteness', 'bool'],
  ['CombineOutput', 'bool'],
  ['Force', 'bool'],
  ['SkipDiagnostics', 'bool'],
  ['SkipVersionCheck', 'bool'],
  ['MaxNetworkOutageMinutes', 'int'],
  ['IncludeTelemetry', 'bool'],
];

const WHITESPACE = /[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]/;

export function parseAdvancedArguments(text: string): { parameters: PaxParameter[]; error?: string } {
  const invalid = () => ({ parameters: [], error: ADVANCED_ARGUMENTS_ERROR });
  if (text.length > 16384 || text.includes('\0')) return invalid();
  const parameters: PaxParameter[] = [];
  const names = new Set<string>();
  let position = 0;
  const skipWhitespace = () => {
    while (position < text.length && WHITESPACE.test(text[position]!)) position++;
  };
  while (position < text.length) {
    skipWhitespace();
    if (position === text.length) break;
    if (parameters.length >= 64 || text[position++] !== '-') return invalid();
    const start = position;
    while (position < text.length && /[A-Za-z0-9]/.test(text[position]!)) position++;
    const option = ADVANCED_OPTIONS.find(([name]) => name.toLowerCase() === text.slice(start, position).toLowerCase());
    if (!option || names.has(option[0])) return invalid();
    const [name, type, minimum = -2147483648, maximum = 2147483647] = option;
    names.add(name);
    const colon = text[position] === ':';
    if (colon) position++;
    else if (position < text.length && !WHITESPACE.test(text[position]!)) return invalid();
    skipWhitespace();
    if (type === 'bool' && !colon && (position === text.length || text[position] === '-')) {
      parameters.push({ name, type, value: true });
      continue;
    }
    if (position === text.length) return invalid();
    let literal = '';
    const quote = text[position];
    if (quote === "'" || quote === '"') {
      position++;
      let closed = false;
      while (position < text.length) {
        const character = text[position++]!;
        if (character === quote) {
          if (text[position] === quote) { literal += quote; position++; }
          else { closed = true; break; }
        } else literal += character;
        if (literal.length > 4096) return invalid();
      }
      if (!closed || (position < text.length && !WHITESPACE.test(text[position]!))) return invalid();
    } else {
      while (position < text.length && !WHITESPACE.test(text[position]!)) {
        const character = text[position++]!;
        if (!/[A-Za-z0-9_+.$-]/.test(character)) return invalid();
        literal += character;
        if (literal.length > 4096) return invalid();
      }
      if (!literal) return invalid();
    }
    let value: string | boolean | number = literal;
    if (type === 'bool') {
      if (!/^\$?(true|false)$/i.test(literal)) return invalid();
      value = /^\$?true$/i.test(literal);
    } else if (type === 'int' || type === 'double') {
      const pattern = type === 'int' ? /^[+-]?\d+$/ : /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/;
      if (!pattern.test(literal)) return invalid();
      value = Number(literal);
      if (!Number.isFinite(value) || value < minimum || value > maximum) return invalid();
    } else if (!/^(Off|On|Auto)$/i.test(literal)) return invalid();
    parameters.push({ name, type, value });
  }
  return { parameters };
}

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
    ...(parseAdvancedArguments(text).error ? { parseError: ADVANCED_ARGUMENTS_ERROR } : {}),
    tokens,
    switchesPresent,
    duplicates: Array.from(duplicates),
    removedSwitches,
    unknownSwitches,
    secretWarnings: scanForSecrets(text),
  };
}
