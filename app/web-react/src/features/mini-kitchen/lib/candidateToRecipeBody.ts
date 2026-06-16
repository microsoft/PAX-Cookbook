/**
 * Map a translated full-Cookbook recipe candidate into a request body the
 * broker's recipe-create / recipe-update routes will accept.
 *
 * The broker owns and overwrites the provenance leaves on every write
 * (`recipeId`, `recipeSchemaVersion`, `paxAdapterVersion`, `createdAt`,
 * `updatedAt`, `createdBy`), so this mapper omits them — the SPA must not try
 * to mint or assert them. It also rebuilds `importMetadata` as the strict,
 * schema-valid subset the recipe schema permits: the translator's candidate
 * carries a few lite-only provenance fields (`originalIdentity.notes`,
 * `factTier`, `scriptPathPreview`, `preset`) that have no home in
 * `recipe.schema.json`, so they are dropped from the persisted recipe and
 * reported back to the caller. None of those fields are lost to the user — the
 * `.paxlite` export round-trips them — but they never reach the broker.
 *
 * This module is pure. It never fetches, never touches storage, never reads a
 * credential store, and never constructs or runs a command.
 */

import {
  COOKBOOK_RECIPE_SCHEMA_VERSION,
  LITE_RECIPE_SCHEMA_VERSION,
  MINI_KITCHEN_CREATED_BY_SITE,
  MINI_KITCHEN_CREATED_BY_TOOL,
} from '../data/mini-kitchen-constants';
import type {
  FullCookbookRecipeCandidate,
  TranslateResult,
} from './translateLiteRecipeToFullRecipe';

export interface RecipeRequestBodyBuild {
  /** `true` when a structurally complete body was produced from the candidate. */
  ok: boolean;
  /** The schema-shaped body to send to the broker, or `null` when no candidate exists. */
  body: Record<string, unknown> | null;
  /**
   * Human-readable list of fields that the candidate carried but the full
   * recipe schema cannot store. Each is preserved in the `.paxlite` export, so
   * these are portability notes, not data loss.
   */
  droppedForSchema: string[];
}

export interface BuildRecipeRequestBodyOptions {
  /**
   * Whether to include the `importMetadata` provenance bag. `true` for create
   * (the broker stamps it once at creation). `false` for update — the broker
   * preserves the on-disk `importMetadata` verbatim and drops any echoed copy,
   * so an update body should omit it.
   */
  includeImportMetadata?: boolean;
}

/**
 * Build a broker request body from a translation result. Returns the
 * schema-shaped body plus the list of candidate fields the schema cannot hold.
 */
export function buildRecipeRequestBody(
  result: TranslateResult,
  options: BuildRecipeRequestBodyOptions = {},
): RecipeRequestBodyBuild {
  const candidate = result.fullRecipeCandidate;
  if (!candidate) {
    return { ok: false, body: null, droppedForSchema: [] };
  }

  const includeImportMetadata = options.includeImportMetadata ?? true;
  const droppedForSchema = collectDroppedFields(candidate);

  const body: Record<string, unknown> = {
    executionMode: candidate.executionMode,
    identity: { name: candidate.identity.name },
    ingredients: candidate.ingredients,
    query: candidate.query,
    processing: candidate.processing,
    destinations: candidate.destinations,
    auth: candidate.auth,
  };

  if (candidate.advanced && candidate.advanced.extraArguments) {
    body.advanced = { extraArguments: candidate.advanced.extraArguments };
  }

  if (includeImportMetadata) {
    body.importMetadata = buildImportMetadata(candidate, result);
  }

  return { ok: true, body, droppedForSchema };
}

// -----------------------------------------------------------------------------
// internals
// -----------------------------------------------------------------------------

function buildImportMetadata(
  candidate: FullCookbookRecipeCandidate,
  result: TranslateResult,
): Record<string, unknown> {
  const meta: Record<string, unknown> = {
    source: 'mini-kitchen-lite',
    originalKind: 'pax-cookbook-mini-recipe',
    originalSchemaVersion: LITE_RECIPE_SCHEMA_VERSION,
    originalCreatedBy: {
      tool: MINI_KITCHEN_CREATED_BY_TOOL,
      site: MINI_KITCHEN_CREATED_BY_SITE,
    },
    compatibility: {
      cookbookRecipeSchemaVersion: COOKBOOK_RECIPE_SCHEMA_VERSION,
    },
  };

  const originalIdentity: Record<string, unknown> = {};
  const description = candidate.importMetadata.originalIdentity.description;
  const tags = candidate.importMetadata.originalIdentity.tags;
  if (description && description.length > 0) {
    originalIdentity.description = description;
  }
  if (tags && tags.length > 0) {
    originalIdentity.tags = tags;
  }
  if (Object.keys(originalIdentity).length > 0) {
    meta.originalIdentity = originalIdentity;
  }

  if (result.needsPrep) {
    meta.importBehavior = { state: 'needsPrep' };
  }

  if (result.warnings.length > 0) {
    meta.mappingWarnings = result.warnings.map(warning => {
      const mapped: Record<string, unknown> = { code: clamp(warning.id, 64) };
      if (warning.field) {
        mapped.path = clamp(warning.field, 256);
      }
      if (warning.message) {
        mapped.detail = clamp(warning.message, 1024);
      }
      return mapped;
    });
  }

  return meta;
}

function collectDroppedFields(candidate: FullCookbookRecipeCandidate): string[] {
  const dropped: string[] = [];
  const im = candidate.importMetadata;
  if (im.originalIdentity.notes && im.originalIdentity.notes.length > 0) {
    dropped.push(
      'identity notes — the full recipe schema has no notes field; kept only in the .paxlite export.',
    );
  }
  if (im.factTier) {
    dropped.push(
      `destination storage tier "${im.factTier}" — PAX derives the tier from the destination path; kept only in the .paxlite export.`,
    );
  }
  if (im.scriptPathPreview && im.scriptPathPreview.length > 0) {
    dropped.push(
      'advanced script path — the broker resolves the managed engine path; kept only in the .paxlite export.',
    );
  }
  return dropped;
}

function clamp(value: string, max: number): string {
  if (value.length <= max) {
    return value;
  }
  return value.slice(0, max);
}
