/**
 * Full PAX recipe (.pax) importer + validator.
 *
 * A `.pax` file is a full PAX Cookbook recipe export: the same persisted full
 * recipe shape the broker returns from `GET /api/v1/recipes/{id}` under
 * `recipe`, wrapped in a small versioned envelope. This module turns that
 * untrusted file text into a fresh `MiniKitchenRecipeState` for the Recipes
 * editor to review — exactly like the `.paxlite` importer, but for the fuller
 * recipe shape.
 *
 * Safety rules enforced here (mirroring the `.paxlite` importer):
 *   - The input is treated as untrusted. JSON.parse failures return a friendly
 *     single-error result; no raw payload text is echoed back into the UI.
 *   - `kind` / `schemaVersion` mismatches reject immediately. An envelope whose
 *     schema version this build does not understand returns a distinct
 *     `unsupported-version` result so the UI can tell the user to update.
 *   - Executable / script payloads are rejected: any field that carries raw PAX
 *     script bytes (script content, embedded scripts) rejects the whole import.
 *     The recipe's `commandPreview` is informational only and is never mapped
 *     into editor state.
 *   - Every free-form text field is scanned with `containsLikelySecret`; a
 *     match rejects the entire import so a secret can never be smuggled into the
 *     builder. Destination paths and the non-secret auth reference identifiers
 *     (chefKeyId / tenantId / clientId) are exempt, matching the lite importer,
 *     so real Fabric/OneLake GUID-bearing URLs and public directory / app ids
 *     are not misread as secrets. The actual secrets live only in the Chef's
 *     Key / Windows Credential Manager, never in a recipe.
 *   - Mapping to editor state reuses `fullRecipeToState`, the same pure,
 *     unknown-field-ignoring reader used to open a saved recipe. No duplicate
 *     schema logic, no network, no storage, no credential store, nothing runs.
 *
 * This module never mutates the source file, never saves, and never runs PAX.
 * The caller applies the returned state as an unsaved draft.
 */

import type { MiniKitchenRecipeState } from '../types';
import { fullRecipeToState } from './fullRecipeToState';
import { containsLikelySecret } from './secretScanner';

/** Envelope marker every `.pax` full recipe export carries at its top level. */
export const FULL_PAX_RECIPE_KIND = 'pax-cookbook-recipe';

/** Schema versions this build of PAX Cookbook can import. */
export const SUPPORTED_FULL_PAX_SCHEMA_VERSIONS: ReadonlySet<string> = new Set(['1.0']);

// Field names that would carry raw PAX script bytes. PAX Cookbook never imports
// or runs script content, so their presence (with a non-empty value) rejects
// the whole file rather than silently dropping it.
const FORBIDDEN_SCRIPT_KEYS: ReadonlySet<string> = new Set([
  'scriptcontent',
  'scriptbytes',
  'scripttext',
  'scriptbody',
  'scriptbase64',
  'embeddedscript',
  'runtimescript',
  'paxscript',
]);

// Free-form string leaves that legitimately carry GUID-bearing values and must
// be exempt from the high-entropy secret heuristic, exactly as the `.paxlite`
// importer exempts them. Two groups, neither of which is a secret:
//   - Storage destination paths / URLs: real Fabric OneLake URLs carry GUID
//     workspace / lakehouse segments the high-entropy rule otherwise flags.
//   - Auth reference identifiers: chefKeyId is a reference to a Chef's Key in
//     Windows Credential Manager; tenantId is the public Azure AD directory GUID
//     (it appears in every sign-in URL and token); clientId is the public OAuth
//     app-registration id. The real secrets (client secret, certificate private
//     key, bearer tokens) live only in the Chef's Key / Windows Credential
//     Manager and are never written into a recipe. Only these exact leaf paths
//     are exempt; any other auth field is still scanned.
const SECRET_SCAN_EXEMPT_PATHS: ReadonlySet<string> = new Set([
  'recipe.destinations.fact.path',
  'recipe.destinations.userInfo.path',
  'recipe.auth.chefKeyId',
  'recipe.auth.tenantId',
  'recipe.auth.clientId',
]);

export type FullPaxImportResult =
  | { ok: true; state: MiniKitchenRecipeState; warnings: readonly string[] }
  | { ok: false; reason: 'invalid'; errors: readonly string[] }
  | { ok: false; reason: 'unsupported-version'; version: string; errors: readonly string[] };

/**
 * Parse a `.pax` full recipe JSON string into a validated builder state.
 * Returns a typed result so the UI can render a clear invalid / unsupported
 * message or accept the import as a draft.
 */
export function parseFullPaxRecipeJson(text: string): FullPaxImportResult {
  if (typeof text !== 'string' || text.trim().length === 0) {
    return { ok: false, reason: 'invalid', errors: ['That file is empty. The builder cannot read it.'] };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return {
      ok: false,
      reason: 'invalid',
      errors: ['That file is not valid JSON. The builder cannot read it.'],
    };
  }

  if (!isObject(parsed)) {
    return {
      ok: false,
      reason: 'invalid',
      errors: ['That file is not a PAX Cookbook full recipe. Its top level is not an object.'],
    };
  }

  const kind = asString(parsed.kind);
  if (kind !== FULL_PAX_RECIPE_KIND) {
    return {
      ok: false,
      reason: 'invalid',
      errors: [
        'That file is not a PAX Cookbook full recipe (.pax). It is missing the expected recipe envelope.',
      ],
    };
  }

  const version = asString(parsed.schemaVersion);
  if (!version) {
    return {
      ok: false,
      reason: 'invalid',
      errors: [
        'That .pax recipe does not declare a schema version, so PAX Cookbook cannot read it safely.',
      ],
    };
  }
  if (!SUPPORTED_FULL_PAX_SCHEMA_VERSIONS.has(version)) {
    return {
      ok: false,
      reason: 'unsupported-version',
      version,
      errors: [
        `That .pax recipe uses schema version ${version}, which this build of PAX Cookbook does not support. Update PAX Cookbook to open it.`,
      ],
    };
  }

  const recipe = parsed.recipe;
  if (!isObject(recipe)) {
    return {
      ok: false,
      reason: 'invalid',
      errors: ['That .pax recipe is missing its recipe contents.'],
    };
  }

  // Reject any embedded executable / script bytes anywhere in the file.
  const scriptKey = findForbiddenScriptKey(parsed);
  if (scriptKey) {
    return {
      ok: false,
      reason: 'invalid',
      errors: [
        `That .pax file embeds executable content ("${scriptKey}"). PAX Cookbook never imports or runs script content, so the file was not imported.`,
      ],
    };
  }

  // Reject anything that looks like a credential, mirroring the lite importer.
  const secretHits: string[] = [];
  collectSecretHits(recipe, 'recipe', secretHits);
  if (secretHits.length > 0) {
    return {
      ok: false,
      reason: 'invalid',
      errors: [
        'The import was rejected. The following fields contained values that look like ' +
          'credentials: ' +
          secretHits.join(', ') +
          '. Remove them from the .pax file and export again.',
      ],
    };
  }

  // Reuse the pure saved-recipe reader: known leaves only, unknown fields
  // ignored, always normalized. Never reads a credential store or runs.
  const mapped = fullRecipeToState(recipe);
  if (!mapped.ok || !mapped.state) {
    return {
      ok: false,
      reason: 'invalid',
      errors: [mapped.error ?? 'That .pax recipe could not be read into the builder.'],
    };
  }

  return { ok: true, state: mapped.state, warnings: [] };
}

// -----------------------------------------------------------------------------
// internals
// -----------------------------------------------------------------------------

function findForbiddenScriptKey(value: unknown): string | null {
  if (Array.isArray(value)) {
    for (const item of value) {
      const hit = findForbiddenScriptKey(item);
      if (hit) {
        return hit;
      }
    }
    return null;
  }
  if (isObject(value)) {
    for (const [k, v] of Object.entries(value)) {
      if (FORBIDDEN_SCRIPT_KEYS.has(k.toLowerCase()) && isNonEmpty(v)) {
        return k;
      }
      const hit = findForbiddenScriptKey(v);
      if (hit) {
        return hit;
      }
    }
  }
  return null;
}

function collectSecretHits(value: unknown, path: string, hits: string[]): void {
  if (typeof value === 'string') {
    if (SECRET_SCAN_EXEMPT_PATHS.has(path)) {
      return;
    }
    if (value.length > 0 && containsLikelySecret(value)) {
      hits.push(path);
    }
    return;
  }
  if (Array.isArray(value)) {
    for (let i = 0; i < value.length; i += 1) {
      collectSecretHits(value[i], `${path}[${i}]`, hits);
    }
    return;
  }
  if (isObject(value)) {
    for (const [k, v] of Object.entries(value)) {
      collectSecretHits(v, `${path}.${k}`, hits);
    }
  }
}

function isNonEmpty(value: unknown): boolean {
  if (typeof value === 'string') {
    return value.trim().length > 0;
  }
  if (Array.isArray(value)) {
    return value.length > 0;
  }
  return value !== null && value !== undefined && value !== false;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function asString(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}
