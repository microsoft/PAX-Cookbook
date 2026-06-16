/**
 * Full PAX recipe (.pax) exporter.
 *
 * The precise inverse of the `.pax` importer (`fullPaxRecipeImporter.ts`). The
 * full Cookbook app exports the complete, runtime-oriented recipe — the same
 * persisted full recipe shape the broker returns from `GET /api/v1/recipes/{id}`
 * under `recipe` — wrapped in the small `{ kind, schemaVersion, recipe }`
 * envelope the importer consumes. Re-importing an exported `.pax` reproduces the
 * builder state (identity, ingredients, query, processing, destinations, auth
 * mode + bound Chef's Key, execution mode, advanced).
 *
 * How the round-trip is guaranteed:
 *   - The recipe body is built with the exact same translator the Save path
 *     uses: `translateLiteRecipeToFullRecipe` then `buildRecipeRequestBody`.
 *     The importer reads that body back with `fullRecipeToState`, the proven
 *     inverse of that pair (Save → reopen already round-trips in production).
 *
 * Safety rules enforced here (mirroring the lite exporter):
 *   - The state is scrubbed with `scrubSavedRecipeState` before the body is
 *     built, so any free-form text that looks like a credential is removed and
 *     surfaced as a `LiteRecipeScrubWarning`. A `.pax` therefore never carries a
 *     secret. (Secrets live only in Chef's Keys / Windows Credential Manager;
 *     the recipe carries a `chefKeyId` reference, never a secret value.)
 *   - The bound Chef's Key reference (`chefKeyId`) and the public tenant id
 *     (`tenantId`, the Azure AD directory GUID) are non-secret identifiers and
 *     round-trip through export -> import untouched; the importer exempts those
 *     reference paths from its secret scan. The client id and certificate
 *     thumbprint are held by the Chef's Key (resolved at run time from Windows
 *     Credential Manager), not the `.pax` body, so they are not part of the
 *     exported recipe.
 *   - The download filename is derived from a sanitized recipe name (ASCII
 *     lowercase, `-` separated, max 60 chars; falls back to a generic name).
 *
 * This module is pure. It never fetches, never touches storage, never reads a
 * credential store, and never constructs or runs a command.
 */

import { FULL_PAX_RECIPE_FILE_EXTENSION } from '../data/mini-kitchen-constants';
import { buildRecipeRequestBody } from './candidateToRecipeBody';
import { FULL_PAX_RECIPE_KIND } from './fullPaxRecipeImporter';
import { scrubSavedRecipeState } from './recipeScrub';
import { translateLiteRecipeToFullRecipe } from './translateLiteRecipeToFullRecipe';
import type { LiteRecipeScrubWarning, MiniKitchenRecipeState } from '../types';

/**
 * Envelope schema version every exported `.pax` declares. MUST stay within the
 * importer's `SUPPORTED_FULL_PAX_SCHEMA_VERSIONS`; bump both together if the
 * persisted recipe shape changes incompatibly.
 */
export const FULL_PAX_RECIPE_SCHEMA_VERSION = '1.0' as const;

/** The serialized `.pax` envelope: the importer's exact input shape. */
export interface FullPaxRecipeEnvelope {
  kind: typeof FULL_PAX_RECIPE_KIND;
  schemaVersion: string;
  recipe: Record<string, unknown>;
}

export interface FullPaxRecipeExportResult {
  /** `true` when a structurally complete `.pax` envelope was produced. */
  ok: boolean;
  /** The `.pax` envelope to serialize and download, or `null` when no body could be built. */
  recipe: FullPaxRecipeEnvelope | null;
  /** Fields the scrubber removed because they looked like a credential. */
  warnings: readonly LiteRecipeScrubWarning[];
  /**
   * Builder-only details the full recipe schema cannot store (kept only in the
   * `.paxlite` export). Portability notes, not data loss.
   */
  droppedForSchema: readonly string[];
  /** A single friendly error when the recipe could not be exported. */
  error?: string;
}

export interface BuildFullPaxRecipeExportInput {
  state: MiniKitchenRecipeState;
}

/**
 * Build a scrubbed full `.pax` recipe envelope from the current builder state.
 * Returns `{ ok: true, recipe, warnings, droppedForSchema }` so the caller can
 * both download the recipe and surface any fields the scrubber removed.
 */
export function buildFullPaxRecipeExport(
  input: BuildFullPaxRecipeExportInput,
): FullPaxRecipeExportResult {
  const scrub = scrubSavedRecipeState(input.state);
  const translation = translateLiteRecipeToFullRecipe(scrub.state);
  const built = buildRecipeRequestBody(translation, { includeImportMetadata: true });

  if (!built.ok || !built.body) {
    return {
      ok: false,
      recipe: null,
      warnings: scrub.warnings,
      droppedForSchema: built.droppedForSchema,
      error:
        'This recipe needs a few more required details before it can be exported as a full .pax recipe.',
    };
  }

  const recipe: FullPaxRecipeEnvelope = {
    kind: FULL_PAX_RECIPE_KIND,
    schemaVersion: FULL_PAX_RECIPE_SCHEMA_VERSION,
    recipe: built.body,
  };

  return {
    ok: true,
    recipe,
    warnings: scrub.warnings,
    droppedForSchema: built.droppedForSchema,
  };
}

/** Pretty-print a `.pax` envelope as a 2-space-indented JSON string. */
export function serializeFullPaxRecipe(recipe: FullPaxRecipeEnvelope): string {
  return JSON.stringify(recipe, null, 2);
}

/**
 * Build a download-safe filename for a full `.pax` recipe export. Returns
 * `pax-cookbook-<slug>.pax`, or `pax-cookbook-recipe.pax` if the recipe name
 * does not contain any usable ASCII characters.
 */
export function buildFullPaxRecipeFileName(recipe: FullPaxRecipeEnvelope): string {
  const identity = recipe.recipe.identity;
  const rawName =
    identity && typeof identity === 'object'
      ? (identity as Record<string, unknown>).name
      : undefined;
  const raw = typeof rawName === 'string' ? rawName : '';
  const slug = raw
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 60);
  if (slug.length === 0) {
    return `pax-cookbook-recipe${FULL_PAX_RECIPE_FILE_EXTENSION}`;
  }
  return `pax-cookbook-${slug}${FULL_PAX_RECIPE_FILE_EXTENSION}`;
}
