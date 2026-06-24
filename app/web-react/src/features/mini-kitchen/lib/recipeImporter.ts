/**
 * Lite recipe JSON importer + validator.
 *
 * Imported lite recipes always land with `importBehavior.state = 'needsPrep'`.
 * The importer never trusts a payload to be runtime-ready — it produces a fresh
 * `MiniKitchenRecipeState` for the builder to edit, never silently routes
 * content into PAX, and never touches the broker, the network, storage, or any
 * credential store.
 *
 * Safety rules enforced here:
 *   - JSON.parse failures return a friendly single-error result; no raw
 *     payload text is echoed back into the UI.
 *   - `kind` / `schemaVersion` mismatches reject immediately.
 *   - Every free-form text field is checked with `containsLikelySecret`;
 *     matches reject the entire import so the user notices instead of having a
 *     secret quietly persist.
 *   - Unknown enum / mode values fall back to the default state's values
 *     (recorded as warnings).
 *   - Unknown top-level fields beyond the envelope schema are recorded as
 *     warnings.
 *   - Unknown nested fields are silently stripped (not echoed).
 *   - Result `state.ingredients.preset` resolves to a known preset id or falls
 *     back to the default preset.
 */

import { LITE_RECIPE_SCHEMA_VERSION, MINI_KITCHEN_CREATED_BY_TOOL } from '../data/mini-kitchen-constants';
import { createDefaultMiniKitchenRecipe } from './defaultRecipe';
import { normalizeRecipe } from './normalizeRecipe';
import { containsLikelySecret } from './secretScanner';
import type {
  AgentFilterMode,
  AuthMode,
  DashboardTarget,
  DateRangeMode,
  ExecutionMode,
  FillerLabelMode,
  LiteRecipe,
  LiteRecipeImportResult,
  LiteRecipeScrubWarning,
  MiniKitchenRecipeState,
  OutputCombineMode,
  OutputMode,
  PresetId,
  PromptFilter,
  QueryMode,
  RollupMode,
  StorageTier,
  UserInfoOutputMode,
} from '../types';

const PRESET_IDS: ReadonlySet<PresetId> = new Set([
  'aiInOneDashboard',
  'm365UsageAnalyticsDashboard',
  'customAuditExport',
  'userInfoOnly',
  'importLiteRecipeJson',
  'importPaxRecipeJson',
]);
const QUERY_MODES: ReadonlySet<QueryMode> = new Set(['audit-query', 'user-info-only']);
const DATE_RANGE_MODES: ReadonlySet<DateRangeMode> = new Set(['previous-day', 'custom']);
const ROLLUP_MODES: ReadonlySet<RollupMode> = new Set(['none', 'rollup', 'rollup-plus-raw']);
const DASHBOARD_TARGETS: ReadonlySet<DashboardTarget> = new Set(['aio', 'aibv']);
const FILLER_LABEL_MODES: ReadonlySet<FillerLabelMode> = new Set(['Self', 'RepeatManager', 'Fixed']);
const OUTPUT_COMBINE_MODES: ReadonlySet<OutputCombineMode> = new Set(['combined', 'separate']);
const OUTPUT_MODES: ReadonlySet<OutputMode> = new Set(['write-new', 'append']);
const USER_INFO_OUTPUT_MODES: ReadonlySet<UserInfoOutputMode> = new Set([
  'default-colocate',
  'write-new',
  'append',
]);
const STORAGE_TIERS: ReadonlySet<StorageTier> = new Set(['local', 'sharepoint', 'fabric']);
const AUTH_MODES: ReadonlySet<AuthMode> = new Set([
  'WebLogin',
  'DeviceCode',
  'AppRegistrationSecret',
  'AppRegistrationCertificate',
  'ManagedIdentity',
]);
const EXECUTION_MODES: ReadonlySet<ExecutionMode> = new Set([
  'local-manual',
  'local-scheduled',
  'fabric-hosted',
  'azure-hosted',
]);
const AGENT_FILTER_MODES: ReadonlySet<AgentFilterMode> = new Set([
  'none',
  'ids-only',
  'agents-only',
  'exclude-agents',
]);
const PROMPT_FILTERS: ReadonlySet<PromptFilter> = new Set(['Prompt', 'Response', 'Both', 'Null']);

const KNOWN_ENVELOPE_KEYS = new Set([
  'kind',
  'schemaVersion',
  'createdBy',
  'compatibility',
  'recipe',
  'commandPreview',
  'permissions',
  'importBehavior',
]);

// -----------------------------------------------------------------------------
// public API
// -----------------------------------------------------------------------------

/**
 * Parse a JSON string into a validated `LiteRecipe` + builder state. Returns a
 * typed result so the UI can render errors or accept the import.
 */
export function parseLiteRecipeJson(text: string): LiteRecipeImportResult {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return {
      ok: false,
      errors: ['That file is not valid JSON. The builder cannot read it.'],
    };
  }
  return validateLiteRecipe(parsed);
}

/**
 * Validate an already-parsed value against the lite recipe schema. Returns the
 * validated envelope and the matching builder state on success.
 */
export function validateLiteRecipe(value: unknown): LiteRecipeImportResult {
  const errors: string[] = [];
  const warnings: LiteRecipeScrubWarning[] = [];

  if (!isObject(value)) {
    return {
      ok: false,
      errors: ['The lite recipe must be a JSON object.'],
    };
  }

  if (value.kind !== 'pax-cookbook-mini-recipe') {
    errors.push(
      'This file does not look like a PAX Cookbook recipe. ' +
        'The "kind" field must be "pax-cookbook-mini-recipe".',
    );
  }
  if (value.schemaVersion !== LITE_RECIPE_SCHEMA_VERSION) {
    errors.push(
      `Unsupported lite recipe schema version. Expected "${LITE_RECIPE_SCHEMA_VERSION}".`,
    );
  }
  if (errors.length > 0) {
    return { ok: false, errors };
  }

  for (const key of Object.keys(value)) {
    if (!KNOWN_ENVELOPE_KEYS.has(key)) {
      warnings.push({
        id: `unknown-envelope-${key}`,
        path: key,
        reason: `Ignored unknown top-level field "${key}".`,
        severity: 'info',
      });
    }
  }

  const recipeValue = value.recipe;
  if (!isObject(recipeValue)) {
    return {
      ok: false,
      errors: ['The lite recipe is missing its "recipe" block.'],
    };
  }

  const secretHits: string[] = [];
  checkForSecrets(recipeValue, 'recipe', secretHits);
  if (secretHits.length > 0) {
    return {
      ok: false,
      errors: [
        'The import was rejected. The following fields contained ' +
          'values that look like credentials: ' +
          secretHits.join(', ') +
          '. Remove them from the JSON and try again.',
      ],
    };
  }

  const state = liteRecipeBlockToState(recipeValue, warnings);
  const recipe = rebuildLiteRecipe(value, state);
  return { ok: true, recipe, state, warnings };
}

/**
 * Convert a validated lite recipe envelope into a fresh builder state. Always
 * passes the result through `normalizeRecipe` so the UI never receives a
 * malformed in-memory shape.
 */
export function liteRecipeToState(recipe: LiteRecipe): MiniKitchenRecipeState {
  return normalizeRecipe(recipe.recipe);
}

// -----------------------------------------------------------------------------
// internals
// -----------------------------------------------------------------------------

function rebuildLiteRecipe(
  envelope: Record<string, unknown>,
  state: MiniKitchenRecipeState,
): LiteRecipe {
  // We accept the envelope's commandPreview / permissions / importBehavior /
  // createdBy / compatibility as informational; the builder will recompute
  // commandPreview and permissions from `state` after import so they reflect
  // the current renderer / resolver.
  const createdBy = isObject(envelope.createdBy) ? envelope.createdBy : {};
  const compatibility = isObject(envelope.compatibility) ? envelope.compatibility : {};
  const commandPreview = isObject(envelope.commandPreview) ? envelope.commandPreview : {};
  const permissions = isObject(envelope.permissions) ? envelope.permissions : {};

  return {
    kind: 'pax-cookbook-mini-recipe',
    schemaVersion: LITE_RECIPE_SCHEMA_VERSION,
    createdBy: {
      tool: typeof createdBy.tool === 'string' ? createdBy.tool : MINI_KITCHEN_CREATED_BY_TOOL,
      site:
        typeof createdBy.site === 'string'
          ? createdBy.site
          : 'https://microsoft.github.io/PAX-Cookbook/',
    },
    compatibility: {
      targetPaxVersion:
        typeof compatibility.targetPaxVersion === 'string'
          ? compatibility.targetPaxVersion
          : '1.11.8',
      switchCatalogVersion:
        typeof compatibility.switchCatalogVersion === 'string'
          ? compatibility.switchCatalogVersion
          : '2026.05.28-2',
      cookbookRecipeSchemaVersion:
        typeof compatibility.cookbookRecipeSchemaVersion === 'number'
          ? compatibility.cookbookRecipeSchemaVersion
          : 1,
    },
    recipe: {
      identity: state.identity,
      ingredients: state.ingredients,
      query: state.query,
      processing: state.processing,
      destinations: state.destinations,
      auth: state.auth,
      executionMode: state.executionMode,
      advanced: state.advanced,
    },
    commandPreview: {
      shell: 'pwsh',
      argv: Array.isArray(commandPreview.argv)
        ? commandPreview.argv.filter((s): s is string => typeof s === 'string')
        : [],
      multiline:
        typeof commandPreview.multiline === 'string' ? commandPreview.multiline : '',
      singleLine:
        typeof commandPreview.singleLine === 'string' ? commandPreview.singleLine : '',
    },
    permissions: {
      required: Array.isArray(permissions.required)
        ? (permissions.required.filter(isObject) as unknown as LiteRecipe['permissions']['required'])
        : [],
      warnings: Array.isArray(permissions.warnings)
        ? permissions.warnings.filter((s): s is string => typeof s === 'string')
        : [],
    },
    importBehavior: {
      state: 'needsPrep',
      reason:
        isObject(envelope.importBehavior) &&
        typeof envelope.importBehavior.reason === 'string'
          ? envelope.importBehavior.reason
          : 'Drafted in the builder. Validate permissions, paths, and runtime targets before running.',
    },
  };
}

function liteRecipeBlockToState(
  block: Record<string, unknown>,
  warnings: LiteRecipeScrubWarning[],
): MiniKitchenRecipeState {
  const fallback = createDefaultMiniKitchenRecipe();

  const identity = isObject(block.identity) ? block.identity : {};
  const ingredients = isObject(block.ingredients) ? block.ingredients : {};
  const query = isObject(block.query) ? block.query : {};
  const processing = isObject(block.processing) ? block.processing : {};
  const destinations = isObject(block.destinations) ? block.destinations : {};
  const auth = isObject(block.auth) ? block.auth : {};
  const advanced = isObject(block.advanced) ? block.advanced : {};

  const presetCandidate =
    typeof ingredients.preset === 'string' ? (ingredients.preset as PresetId) : undefined;
  const presetId: PresetId =
    presetCandidate && PRESET_IDS.has(presetCandidate)
      ? presetCandidate
      : fallback.ingredients.preset;
  if (presetCandidate && presetId !== presetCandidate) {
    warnings.push({
      id: 'unknown-preset',
      path: 'recipe.ingredients.preset',
      reason: `Unknown preset id "${presetCandidate}". Fell back to "${presetId}".`,
      severity: 'info',
    });
  }

  const queryMode = readEnum<QueryMode>(query.mode, QUERY_MODES, fallback.query.mode);
  const factDestRaw = isObject(destinations.fact) ? destinations.fact : {};
  const userInfoDestRaw = isObject(destinations.userInfo) ? destinations.userInfo : {};
  const agentFilterRaw = isObject(processing.agentFilter) ? processing.agentFilter : undefined;

  const state: MiniKitchenRecipeState = {
    identity: {
      name: readString(identity.name) ?? fallback.identity.name,
      description: readString(identity.description) ?? '',
      tags: readStringList(identity.tags),
      notes: readString(identity.notes) ?? '',
    },
    ingredients: { preset: presetId },
    query: {
      mode: queryMode,
      dateMode: readOptionalEnum<DateRangeMode>(query.dateMode, DATE_RANGE_MODES),
      startDate: readString(query.startDate),
      endDate: readString(query.endDate),
      includeM365Usage: readBoolean(query.includeM365Usage),
      excludeCopilotInteraction: readBoolean(query.excludeCopilotInteraction),
      includeUserInfo: readBoolean(query.includeUserInfo),
      onlyUserInfo: readBoolean(query.onlyUserInfo),
    },
    processing: {
      activityTypes: readStringList(processing.activityTypes),
      userIds: readStringList(processing.userIds),
      groupNames: readStringList(processing.groupNames),
      agentFilter: agentFilterRaw
        ? {
            mode: readEnum<AgentFilterMode>(agentFilterRaw.mode, AGENT_FILTER_MODES, 'none'),
            ids: readStringList(agentFilterRaw.ids),
          }
        : undefined,
      promptFilter: readOptionalEnum<PromptFilter>(processing.promptFilter, PROMPT_FILTERS),
      rollup: readOptionalEnum<RollupMode>(processing.rollup, ROLLUP_MODES),
      dashboard: readOptionalEnum<DashboardTarget>(processing.dashboard, DASHBOARD_TARGETS),
      deidentify: readBoolean(processing.deidentify),
      fillerLabel: readOptionalEnum<FillerLabelMode>(processing.fillerLabel, FILLER_LABEL_MODES),
      fillerLabelText: readString(processing.fillerLabelText),
      outputCombineMode: readOptionalEnum<OutputCombineMode>(
        processing.outputCombineMode,
        OUTPUT_COMBINE_MODES,
      ),
    },
    destinations: {
      fact: {
        mode: readEnum<OutputMode>(factDestRaw.mode, OUTPUT_MODES, fallback.destinations.fact.mode),
        tier: readEnum<StorageTier>(
          factDestRaw.tier,
          STORAGE_TIERS,
          fallback.destinations.fact.tier,
        ),
        path: readString(factDestRaw.path),
      },
      userInfo: {
        mode: readEnum<UserInfoOutputMode>(
          userInfoDestRaw.mode,
          USER_INFO_OUTPUT_MODES,
          fallback.destinations.userInfo.mode,
        ),
        path: readString(userInfoDestRaw.path),
      },
    },
    auth: {
      mode: readEnum<AuthMode>(auth.mode, AUTH_MODES, fallback.auth.mode),
      tenantId: readString(auth.tenantId),
      clientId: readString(auth.clientId),
      certificateThumbprint: readString(auth.certificateThumbprint),
    },
    executionMode: readEnum<ExecutionMode>(
      block.executionMode,
      EXECUTION_MODES,
      fallback.executionMode,
    ),
    advanced: {
      extraArguments: readString(advanced.extraArguments) ?? '',
    },
  };

  return normalizeRecipe(state);
}

function checkForSecrets(value: unknown, path: string, hits: string[]): void {
  if (typeof value === 'string') {
    // Non-secret string leaves that must not trip the high-entropy heuristic:
    //   - Destination paths (local paths, SharePoint URLs, OneLake/Fabric URLs);
    //     real Fabric OneLake URLs carry GUID workspace / lakehouse segments.
    //   - Auth reference identifiers: chefKeyId (reference to a Chef's Key in
    //     Windows Credential Manager), tenantId (public Azure AD directory GUID),
    //     and clientId (public OAuth app-registration id). The real secrets live
    //     only in the Chef's Key / Windows Credential Manager, never in a recipe.
    if (
      path === 'recipe.destinations.fact.path' ||
      path === 'recipe.destinations.userInfo.path' ||
      path === 'recipe.auth.chefKeyId' ||
      path === 'recipe.auth.tenantId' ||
      path === 'recipe.auth.clientId'
    ) {
      return;
    }
    if (value.length > 0 && containsLikelySecret(value)) {
      hits.push(path);
    }
    return;
  }
  if (Array.isArray(value)) {
    for (let i = 0; i < value.length; i += 1) {
      checkForSecrets(value[i], `${path}[${i}]`, hits);
    }
    return;
  }
  if (isObject(value)) {
    for (const [k, v] of Object.entries(value)) {
      checkForSecrets(v, `${path}.${k}`, hits);
    }
  }
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function readString(value: unknown): string | undefined {
  if (typeof value !== 'string') {
    return undefined;
  }
  return value;
}

function readBoolean(value: unknown): boolean | undefined {
  if (typeof value !== 'boolean') {
    return undefined;
  }
  return value;
}

function readStringList(value: unknown): readonly string[] | undefined {
  if (!Array.isArray(value)) {
    return undefined;
  }
  const list = value.filter((s): s is string => typeof s === 'string');
  return list.length > 0 ? list : undefined;
}

function readEnum<T extends string>(value: unknown, set: ReadonlySet<T>, fallback: T): T {
  if (typeof value === 'string' && set.has(value as T)) {
    return value as T;
  }
  return fallback;
}

function readOptionalEnum<T extends string>(value: unknown, set: ReadonlySet<T>): T | undefined {
  if (typeof value === 'string' && set.has(value as T)) {
    return value as T;
  }
  return undefined;
}
