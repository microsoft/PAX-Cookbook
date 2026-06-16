/**
 * Secret-scrubbing for Mini-Kitchen recipe state.
 *
 * Pure, browser-only. Walks the editable recipe state and blanks any free-form
 * text whose contents look like a credential, returning the cleaned state plus
 * a list of structured warnings the caller can surface. Used by the lite
 * (`.paxlite`) and full (`.pax`) recipe exporters before an envelope is
 * serialized, so a secret never leaves the browser through an export file.
 *
 * This module never touches storage, the network, the broker, the file system,
 * or any credential store. Structured fields (enums, modes, booleans) and
 * destination paths are passed through untouched — destination paths are not
 * secrets and the high-entropy heuristic otherwise false-positives on real
 * Fabric OneLake URLs whose workspace / lakehouse segments are GUIDs.
 */

import { normalizeRecipe } from './normalizeRecipe';
import { containsLikelySecret } from './secretScanner';
import type { LiteRecipeScrubWarning, MiniKitchenRecipeState } from '../types';

export interface ScrubResult {
  state: MiniKitchenRecipeState;
  warnings: readonly LiteRecipeScrubWarning[];
}

/**
 * Walk the editable recipe state and drop any free-form text whose contents
 * match `containsLikelySecret`. Returns the scrubbed state plus a list of
 * structured warnings the caller can surface.
 */
export function scrubSavedRecipeState(state: MiniKitchenRecipeState): ScrubResult {
  const warnings: LiteRecipeScrubWarning[] = [];
  const next: MiniKitchenRecipeState = {
    identity: { ...state.identity },
    ingredients: { ...state.ingredients },
    query: { ...state.query },
    processing: { ...state.processing },
    destinations: {
      fact: { ...state.destinations.fact },
      userInfo: { ...state.destinations.userInfo },
    },
    auth: { ...state.auth },
    executionMode: state.executionMode,
    advanced: { ...state.advanced },
  };

  scrubStringField(next.identity, 'name', 'state.identity.name', warnings);
  scrubStringField(next.identity, 'description', 'state.identity.description', warnings);
  scrubStringField(next.identity, 'notes', 'state.identity.notes', warnings);
  next.identity.tags = scrubStringList(next.identity.tags, 'state.identity.tags', warnings);

  next.processing.userIds = scrubStringList(
    next.processing.userIds,
    'state.processing.userIds',
    warnings,
  );
  next.processing.groupNames = scrubStringList(
    next.processing.groupNames,
    'state.processing.groupNames',
    warnings,
  );
  if (next.processing.agentFilter) {
    next.processing.agentFilter = {
      ...next.processing.agentFilter,
      ids: scrubStringList(
        next.processing.agentFilter.ids,
        'state.processing.agentFilter.ids',
        warnings,
      ),
    };
  }

  // Destination paths (local paths, SharePoint URLs, OneLake/Fabric URLs) are
  // not secrets and are intentionally NOT scrubbed. The high-entropy secret
  // heuristic otherwise false-positives on real Fabric OneLake URLs whose
  // workspace / lakehouse segments are GUIDs, wiping the path on export. Paths
  // are non-sensitive, so they round-trip through export -> import untouched.

  // Auth reference identifiers are public, not secrets, and must round-trip
  // through export untouched (scrubbing them silently corrupted exports):
  //   - tenantId  : public Azure AD directory GUID (in every sign-in URL/token)
  //   - clientId  : public OAuth app-registration id
  //   - chefKeyId : reference to a Chef's Key in Windows Credential Manager
  //                 (already never scrubbed)
  // They are passed through exactly like destination paths. The actual secrets
  // (client secret, certificate private key, bearer tokens) never live in a
  // recipe — they live only in the Chef's Key / Windows Credential Manager — and
  // the free-form fields above/below are still scrubbed in case one is pasted
  // there. certificateThumbprint is a public fingerprint but is left scrubbed
  // pending a separate decision.
  scrubStringField(
    next.auth,
    'certificateThumbprint',
    'state.auth.certificateThumbprint',
    warnings,
  );

  scrubStringField(
    next.advanced,
    'extraArguments',
    'state.advanced.extraArguments',
    warnings,
  );

  return { state: normalizeRecipe(next), warnings };
}

function scrubStringField<T extends object>(
  obj: T,
  key: keyof T & string,
  path: string,
  warnings: LiteRecipeScrubWarning[],
): void {
  const bag = obj as Record<string, unknown>;
  const raw = bag[key];
  if (typeof raw !== 'string') {
    return;
  }
  if (raw.length === 0) {
    return;
  }
  if (containsLikelySecret(raw)) {
    bag[key] = '';
    warnings.push({
      id: `scrub-${path}`,
      path,
      reason: `The builder removed text from "${path}" that looked like a credential.`,
      severity: 'warning',
    });
  }
}

function scrubStringList(
  list: readonly string[] | undefined,
  path: string,
  warnings: LiteRecipeScrubWarning[],
): readonly string[] | undefined {
  if (!list || list.length === 0) {
    return list;
  }
  const cleaned: string[] = [];
  let dropped = 0;
  for (const item of list) {
    if (typeof item !== 'string') {
      dropped += 1;
      continue;
    }
    if (item.length > 0 && containsLikelySecret(item)) {
      dropped += 1;
      continue;
    }
    cleaned.push(item);
  }
  if (dropped > 0) {
    warnings.push({
      id: `scrub-${path}`,
      path,
      reason: `The builder removed ${dropped} value(s) from "${path}" that looked like credentials.`,
      severity: 'warning',
    });
  }
  if (cleaned.length === 0) {
    return undefined;
  }
  return cleaned;
}
