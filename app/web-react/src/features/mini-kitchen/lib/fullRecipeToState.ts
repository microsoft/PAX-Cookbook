/**
 * Full Cookbook recipe -> Mini-Kitchen builder state (read-only open).
 *
 * The inverse of `translateLiteRecipeToFullRecipe`. It takes a persisted full
 * recipe (the object the broker returns from `GET /api/v1/recipes/{id}` under
 * `recipe`) and reconstructs an editable `MiniKitchenRecipeState` so a saved
 * recipe can be opened back into the builder for review or further editing.
 *
 * Drift control:
 *   - The enum/shape inversions here mirror the forward translator one-for-one
 *     (query mode, agent-filter mode, rollup token, destination mode, the
 *     nested ingredients flags). Keep the two field maps in lock-step.
 *   - The result is always passed through `normalizeRecipe`, the same mutex
 *     pass the forward translator and the importer run, so the builder never
 *     receives an out-of-shape state.
 *
 * Safety:
 *   - This module is pure. It never fetches, runs a command, touches storage,
 *     or reads a credential store.
 *   - It reads only known scalar / array leaves into the state. Unknown fields
 *     in the persisted recipe are ignored, never echoed, so opening a recipe
 *     cannot smuggle arbitrary content into the builder.
 *   - The full recipe schema carries no secret fields; auth restores the mode,
 *     tenant id, and the bound Chef's Key id (CK-1). The client id / certificate
 *     thumbprint live in the Chef's Key, not the recipe, so they are not restored
 *     into the builder.
 */

import { createDefaultMiniKitchenRecipe, detectMatchingPreset } from './defaultRecipe';
import { normalizeRecipe } from './normalizeRecipe';
import type {
  AgentFilterMode,
  AuthMode,
  ExecutionMode,
  MiniKitchenRecipeState,
  OutputMode,
  PresetId,
  PromptFilter,
  QueryMode,
  RollupMode,
  StorageTier,
  UserInfoOutputMode,
} from '../types';

export interface FullRecipeOpenResult {
  /** `true` when a builder state was reconstructed. */
  ok: boolean;
  /** The reconstructed, normalized builder state, or `null` on failure. */
  state: MiniKitchenRecipeState | null;
  /** A single friendly error when the recipe object could not be read. */
  error?: string;
}

const PRESET_IDS: ReadonlySet<string> = new Set<PresetId>([
  'aiInOneDashboard',
  'm365UsageAnalyticsDashboard',
  'customAuditExport',
  'userInfoOnly',
  'importLiteRecipeJson',
  'importPaxRecipeJson',
]);
const AUTH_MODES: ReadonlySet<string> = new Set<AuthMode>([
  'WebLogin',
  'DeviceCode',
  'AppRegistrationSecret',
  'AppRegistrationCertificate',
  'ManagedIdentity',
]);
const EXECUTION_MODES: ReadonlySet<string> = new Set<ExecutionMode>([
  'local-manual',
  'local-scheduled',
  'fabric-hosted',
  'azure-hosted',
]);
const PROMPT_FILTERS: ReadonlySet<string> = new Set<PromptFilter>([
  'Prompt',
  'Response',
  'Both',
  'Null',
]);
const STORAGE_TIERS: ReadonlySet<string> = new Set<StorageTier>([
  'local',
  'sharepoint',
  'fabric',
]);

/**
 * Reconstruct a builder state from a persisted full recipe object. Starts from
 * the canonical default state and overlays only the fields the full recipe can
 * express, then normalizes the result.
 */
export function fullRecipeToState(recipe: unknown): FullRecipeOpenResult {
  if (!isObject(recipe)) {
    return {
      ok: false,
      state: null,
      error: 'That saved recipe could not be read. Its stored shape is not an object.',
    };
  }

  const state = createDefaultMiniKitchenRecipe();
  const importMetadata = isObject(recipe.importMetadata) ? recipe.importMetadata : {};

  // ---- Execution mode ----
  const executionMode = asString(recipe.executionMode);
  if (executionMode && EXECUTION_MODES.has(executionMode)) {
    state.executionMode = executionMode as ExecutionMode;
  }

  // ---- Identity (name on the recipe; description/tags/notes in metadata) ----
  const identity = isObject(recipe.identity) ? recipe.identity : {};
  state.identity = { name: asString(identity.name) ?? '' };
  const originalIdentity = isObject(importMetadata.originalIdentity)
    ? importMetadata.originalIdentity
    : {};
  const description = asString(originalIdentity.description);
  if (description) {
    state.identity.description = description;
  }
  const tags = asStringArray(originalIdentity.tags);
  if (tags.length > 0) {
    state.identity.tags = tags;
  }
  const notes = asString(originalIdentity.notes);
  if (notes) {
    state.identity.notes = notes;
  }

  // ---- Query mode + ingredients flags ----
  const fullQuery = isObject(recipe.query) ? recipe.query : {};
  const queryMode: QueryMode =
    asString(fullQuery.mode) === 'userInfoOnly' ? 'user-info-only' : 'audit-query';
  state.query.mode = queryMode;

  const ingredients = isObject(recipe.ingredients) ? recipe.ingredients : {};
  const m365Usage = isObject(ingredients.m365Usage) ? ingredients.m365Usage : {};
  const entraUserData = isObject(ingredients.entraUserData) ? ingredients.entraUserData : {};

  state.query.includeM365Usage = asBool(m365Usage.includeM365Usage) === true;
  state.query.includeUserInfo = asBool(entraUserData.includeUserInfo) === true;
  // includeCopilotInteraction === false is how the full schema encodes the
  // lite "exclude Copilot interaction" flag (only expressible with M365 on).
  state.query.excludeCopilotInteraction = asBool(m365Usage.includeCopilotInteraction) === false;

  // ---- Audit query fields ----
  const startDate = asString(fullQuery.startDate);
  if (startDate) {
    state.query.startDate = startDate;
  }
  const endDate = asString(fullQuery.endDate);
  if (endDate) {
    state.query.endDate = endDate;
  }
  // Absence is the signal: a saved audit recipe with neither -StartDate nor
  // -EndDate is previous-day mode (a half-filled custom range is blocked at
  // save and never persists). Reconstruct that so the builder reopens on
  // Previous day rather than an empty, blocking custom range.
  if (queryMode === 'audit-query' && !startDate && !endDate) {
    state.query.dateMode = 'previous-day';
  }

  // ---- Processing (filters live under full query; rollup under processing) ----
  const activityTypes = asStringArray(fullQuery.activityTypes);
  if (activityTypes.length > 0) {
    state.processing.activityTypes = activityTypes;
  }
  const userIds = asStringArray(fullQuery.userIds);
  if (userIds.length > 0) {
    state.processing.userIds = userIds;
  }
  const groupNames = asStringArray(fullQuery.groupNames);
  if (groupNames.length > 0) {
    state.processing.groupNames = groupNames;
  }

  const fullAgentFilter = isObject(fullQuery.agentFilter) ? fullQuery.agentFilter : null;
  if (fullAgentFilter) {
    const liteMode = inverseAgentFilterMode(asString(fullAgentFilter.mode));
    if (liteMode) {
      const agentIds = asStringArray(fullAgentFilter.agentIds);
      state.processing.agentFilter =
        liteMode === 'ids-only' ? { mode: 'ids-only', ids: agentIds } : { mode: liteMode };
    }
  }

  const promptFilter = asString(fullQuery.promptFilter);
  if (promptFilter && PROMPT_FILTERS.has(promptFilter)) {
    state.processing.promptFilter = promptFilter as PromptFilter;
  }

  const processing = isObject(recipe.processing) ? recipe.processing : {};
  const rollup = inverseRollup(asString(processing.rollup));
  if (rollup) {
    state.processing.rollup = rollup;
  }

  // ---- Destinations ----
  const destinations = isObject(recipe.destinations) ? recipe.destinations : {};
  const factTier = asString(importMetadata.factTier);
  const fact = inverseDestination(destinations.fact);
  state.destinations.fact = {
    mode: fact.mode,
    tier: factTier && STORAGE_TIERS.has(factTier) ? (factTier as StorageTier) : 'local',
    ...(fact.path ? { path: fact.path } : {}),
  };

  const userInfoRaw = destinations.userInfo;
  if (isObject(userInfoRaw)) {
    const ui = inverseDestination(userInfoRaw);
    const uiMode: UserInfoOutputMode = ui.mode === 'append' ? 'append' : 'write-new';
    state.destinations.userInfo = {
      mode: uiMode,
      ...(ui.path ? { path: ui.path } : {}),
    };
  }

  // ---- Auth ----
  const auth = isObject(recipe.auth) ? recipe.auth : {};
  const authMode = asString(auth.mode);
  state.auth = { mode: authMode && AUTH_MODES.has(authMode) ? (authMode as AuthMode) : 'WebLogin' };
  const tenantId = asString(auth.tenantId);
  if (tenantId) {
    state.auth.tenantId = tenantId;
  }
  const chefKeyId = asString(auth.chefKeyId);
  if (chefKeyId) {
    state.auth.chefKeyId = chefKeyId;
  }

  // ---- Advanced ----
  const advanced = isObject(recipe.advanced) ? recipe.advanced : {};
  const extraArguments = asString(advanced.extraArguments);
  if (extraArguments) {
    state.advanced.extraArguments = extraArguments;
  }
  const scriptPathPreview = asString(importMetadata.scriptPathPreview);
  if (scriptPathPreview) {
    state.advanced.scriptPath = scriptPathPreview;
  }

  // ---- Preset (Step 1 highlight): honor the recorded preset if present and
  // valid, otherwise detect the closest match from the reconstructed state. ----
  const recordedPreset = asString(importMetadata.preset);
  state.ingredients.preset =
    recordedPreset && PRESET_IDS.has(recordedPreset)
      ? (recordedPreset as PresetId)
      : detectMatchingPreset(state);

  return { ok: true, state: normalizeRecipe(state) };
}

// -----------------------------------------------------------------------------
// internals
// -----------------------------------------------------------------------------

function inverseAgentFilterMode(mode: string | undefined): AgentFilterMode | null {
  switch (mode) {
    case 'agentIds':
      return 'ids-only';
    case 'agentsOnly':
      return 'agents-only';
    case 'excludeAgents':
      return 'exclude-agents';
    default:
      return null;
  }
}

function inverseRollup(rollup: string | undefined): RollupMode | null {
  switch (rollup) {
    case 'Rollup':
      return 'rollup';
    case 'RollupPlusRaw':
      return 'rollup-plus-raw';
    default:
      return null;
  }
}

interface InverseDestination {
  mode: OutputMode;
  path?: string;
}

/**
 * Invert a full destination block ({ mode: 'outputPath' | 'append', path /
 * appendFile }) into the lite { mode: 'write-new' | 'append', path } shape.
 */
function inverseDestination(value: unknown): InverseDestination {
  if (!isObject(value)) {
    return { mode: 'write-new' };
  }
  if (asString(value.mode) === 'append') {
    const appendFile = asString(value.appendFile) ?? asString(value.path);
    return { mode: 'append', ...(appendFile ? { path: appendFile } : {}) };
  }
  const path = asString(value.path);
  return { mode: 'write-new', ...(path ? { path } : {}) };
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function asString(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function asBool(value: unknown): boolean | undefined {
  return typeof value === 'boolean' ? value : undefined;
}

function asStringArray(value: unknown): string[] {
  if (!Array.isArray(value)) {
    return [];
  }
  return value.filter((v): v is string => typeof v === 'string' && v.trim().length > 0);
}
