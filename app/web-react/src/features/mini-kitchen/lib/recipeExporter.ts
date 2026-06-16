/**
 * Lite recipe JSON exporter.
 *
 * Mini-Kitchen exports a `LiteRecipe` envelope, not a full PAX Cookbook
 * Recipe Takeout. The envelope intentionally captures workflow intent and a
 * frozen command + permissions snapshot, but leaves credential binding, path
 * validation, schedules, and runtime state to the full Cookbook to finalize.
 * Every exported envelope carries `importBehavior.state = 'needsPrep'` so the
 * importer downstream knows not to treat the recipe as runtime-ready.
 *
 * Safety rules enforced here:
 *   - The recipe fields written into the envelope are scrubbed against
 *     `containsLikelySecret` before serialization. Any scrubbed field is
 *     surfaced as a `LiteRecipeScrubWarning` so the UI can warn the user.
 *   - `AppRegistrationSecret` mode is preserved as a mode signal *only*;
 *     no secret value is ever written into the envelope.
 *   - The download filename is derived from a sanitized recipe name (ASCII
 *     lowercase, `-` separated, max 60 chars; falls back to a generic name
 *     if scrubbing leaves nothing useable).
 */

import {
  COOKBOOK_RECIPE_SCHEMA_VERSION,
  LITE_RECIPE_FILE_EXTENSION,
  LITE_RECIPE_SCHEMA_VERSION,
  MINI_KITCHEN_CREATED_BY_SITE,
  MINI_KITCHEN_CREATED_BY_TOOL,
  MINI_KITCHEN_IMPORT_REASON,
  MINI_KITCHEN_IMPORT_STATE,
  SWITCH_CATALOG_VERSION,
  TARGET_PAX_VERSION_DEFAULT,
} from '../data/mini-kitchen-constants';
import type { RenderedCommand } from './commandRenderer';
import { scrubSavedRecipeState } from './recipeScrub';
import type {
  LiteRecipe,
  LiteRecipeExportResult,
  MiniKitchenRecipeState,
  PermissionsReport,
} from '../types';

interface BuildLiteRecipeExportInput {
  state: MiniKitchenRecipeState;
  command: RenderedCommand;
  permissions: PermissionsReport;
}

/**
 * Build a fully scrubbed `LiteRecipe` envelope from the current builder
 * state plus the already-computed renderer and resolver outputs. Returns
 * `{ ok: true, recipe, warnings }` so the caller can both download the
 * recipe and warn the user about any fields the scrubber removed.
 */
export function buildLiteRecipeExport(
  input: BuildLiteRecipeExportInput,
): LiteRecipeExportResult {
  const scrub = scrubSavedRecipeState(input.state);
  const state = scrub.state;

  const recipe: LiteRecipe = {
    kind: 'pax-cookbook-mini-recipe',
    schemaVersion: LITE_RECIPE_SCHEMA_VERSION,
    createdBy: {
      tool: MINI_KITCHEN_CREATED_BY_TOOL,
      site: MINI_KITCHEN_CREATED_BY_SITE,
    },
    compatibility: {
      targetPaxVersion: TARGET_PAX_VERSION_DEFAULT,
      switchCatalogVersion: SWITCH_CATALOG_VERSION,
      cookbookRecipeSchemaVersion: COOKBOOK_RECIPE_SCHEMA_VERSION,
    },
    recipe: {
      identity: { ...state.identity },
      ingredients: { ...state.ingredients },
      query: { ...state.query },
      processing: cloneProcessing(state),
      destinations: {
        fact: { ...state.destinations.fact },
        userInfo: { ...state.destinations.userInfo },
      },
      auth: { ...state.auth },
      executionMode: state.executionMode,
      advanced: { ...state.advanced },
    },
    commandPreview: {
      shell: input.command.shell,
      argv: [...input.command.argv],
      multiline: input.command.multiline,
      singleLine: input.command.singleLine,
    },
    permissions: {
      required: input.permissions.required.map(entry => ({ ...entry })),
      warnings: [...input.permissions.warnings],
    },
    importBehavior: {
      state: MINI_KITCHEN_IMPORT_STATE,
      reason: MINI_KITCHEN_IMPORT_REASON,
    },
  };

  return { ok: true, recipe, warnings: scrub.warnings };
}

/** Pretty-print a `LiteRecipe` envelope as a 2-space-indented JSON string. */
export function serializeLiteRecipe(recipe: LiteRecipe): string {
  return JSON.stringify(recipe, null, 2);
}

/**
 * Build a download-safe filename for a lite recipe export. Returns
 * `pax-cookbook-<slug>.json.paxlite`, or
 * `pax-cookbook-recipe.json.paxlite` if the recipe name does not
 * contain any usable ASCII characters. The file contents are JSON; the
 * compound extension flags the file as a Mini-Kitchen lite recipe.
 */
export function buildLiteRecipeFileName(recipe: LiteRecipe): string {
  const raw = recipe.recipe.identity.name ?? '';
  const slug = raw
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 60);
  if (slug.length === 0) {
    return `pax-cookbook-recipe${LITE_RECIPE_FILE_EXTENSION}`;
  }
  return `pax-cookbook-${slug}${LITE_RECIPE_FILE_EXTENSION}`;
}

function cloneProcessing(
  state: MiniKitchenRecipeState,
): MiniKitchenRecipeState['processing'] {
  const p = state.processing;
  return {
    ...p,
    activityTypes: p.activityTypes ? [...p.activityTypes] : undefined,
    userIds: p.userIds ? [...p.userIds] : undefined,
    groupNames: p.groupNames ? [...p.groupNames] : undefined,
    agentFilter: p.agentFilter
      ? {
          ...p.agentFilter,
          ids: p.agentFilter.ids ? [...p.agentFilter.ids] : undefined,
        }
      : undefined,
  };
}
