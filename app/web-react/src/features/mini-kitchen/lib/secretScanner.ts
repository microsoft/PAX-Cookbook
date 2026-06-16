/**
 * Heuristic secret scanner.
 *
 * Mini-Kitchen never stores secrets, but users can paste arbitrary text into
 * the Advanced Arguments box or the Identity description. This module flags
 * obvious credential shapes so the UI can warn before rendering or exporting.
 *
 * The scanner is conservative: prefer false positives over false negatives.
 * Phase 2 returns matches only; the UI in later phases decides severity.
 */

export interface SecretMatch {
  /** Stable identifier for de-duplication and UI rendering. */
  id: string;
  /** Short label for the kind of match (e.g. `"password-pair"`). */
  pattern: string;
  /** Human-readable explanation. */
  message: string;
}

const KEYWORD_PAIR_PATTERNS: readonly { id: string; regex: RegExp; message: string }[] = [
  {
    id: 'password-pair',
    regex: /\bpassword\s*=\s*\S+/i,
    message: 'Looks like a password=value pair. Do not paste passwords here.',
  },
  {
    id: 'pwd-pair',
    regex: /\bpwd\s*=\s*\S+/i,
    message: 'Looks like a pwd=value pair. Do not paste passwords here.',
  },
  {
    id: 'secret-pair',
    regex: /\bsecret\s*=\s*\S+/i,
    message: 'Looks like a secret=value pair. Do not paste secrets here.',
  },
  {
    id: 'client-secret-pair',
    regex: /\bclient[_-]?secret\s*=\s*\S+/i,
    message:
      'Looks like a client_secret=value pair. The builder never stores secrets; source from an env var at runtime.',
  },
  {
    id: 'api-key-pair',
    regex: /\bapi[_-]?key\s*=\s*\S+/i,
    message: 'Looks like an api_key=value pair. Do not paste API keys here.',
  },
  {
    id: 'token-pair',
    regex: /\btoken\s*=\s*\S+/i,
    message: 'Looks like a token=value pair. Do not paste tokens here.',
  },
  {
    id: 'bearer-token',
    regex: /\bbearer\s+[A-Za-z0-9._-]+/i,
    message: 'Looks like a bearer token. Do not paste tokens here.',
  },
  {
    id: 'key-pair',
    regex: /(?<![\w-])key\s*=\s*\S+/i,
    message: 'Looks like a key=value pair. Verify this is not a credential.',
  },
];

const KEYWORD_MENTIONS: readonly { id: string; word: string; message: string }[] = [
  { id: 'kw-password', word: 'password', message: 'Contains the word "password".' },
  { id: 'kw-secret', word: 'secret', message: 'Contains the word "secret".' },
  { id: 'kw-token', word: 'token', message: 'Contains the word "token".' },
  { id: 'kw-bearer', word: 'bearer', message: 'Contains the word "bearer".' },
  {
    id: 'kw-client-secret',
    word: 'client_secret',
    message: 'Contains the phrase "client_secret".',
  },
  { id: 'kw-api-key', word: 'api_key', message: 'Contains the phrase "api_key".' },
];

/**
 * Long opaque base64-ish runs are a common secret shape. Mini-Kitchen flags any
 * 32+ char base64-alphabet run as high entropy.
 */
const HIGH_ENTROPY_PATTERN = /[A-Za-z0-9+/=_-]{32,}/;

/** Scan a free-form text blob for credential-shaped content. */
export function scanForSecrets(text: string): readonly SecretMatch[] {
  const matches: SecretMatch[] = [];
  if (!text) {
    return matches;
  }

  for (const p of KEYWORD_PAIR_PATTERNS) {
    if (p.regex.test(text)) {
      matches.push({ id: p.id, pattern: p.id, message: p.message });
    }
  }

  for (const k of KEYWORD_MENTIONS) {
    const word = k.word.replace(/[_-]/g, '[_-]');
    const regex = new RegExp(`\\b${word}\\b`, 'i');
    if (regex.test(text)) {
      matches.push({ id: k.id, pattern: k.id, message: k.message });
    }
  }

  if (HIGH_ENTROPY_PATTERN.test(text)) {
    matches.push({
      id: 'high-entropy',
      pattern: 'high-entropy',
      message:
        'Contains a long opaque string that may be a credential. The builder does not store secrets.',
    });
  }

  // De-duplicate by id, preserving first occurrence order.
  const seen = new Set<string>();
  const deduped: SecretMatch[] = [];
  for (const m of matches) {
    if (seen.has(m.id)) {
      continue;
    }
    seen.add(m.id);
    deduped.push(m);
  }
  return deduped;
}

/** Convenience: does the text contain anything that looks like a secret? */
export function containsLikelySecret(text: string): boolean {
  return scanForSecrets(text).length > 0;
}
