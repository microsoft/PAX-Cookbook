/**
 * Default recipe state and preset application.
 *
 * `createDefaultMiniKitchenRecipe()` returns a fresh, empty recipe state with
 * the documented defaults. `createRecipeFromPreset()` applies a preset's
 * overrides on top of the default state and runs the normalizer.
 */

import { getPresetById, DASHBOARD_PRESETS } from '../data/dashboard-presets';
import type {
  MiniKitchenRecipeState,
  PresetId,
  PresetOverrides,
} from '../types';
import { normalizeRecipe } from './normalizeRecipe';

/** Fresh, empty recipe state. Always uses the `customAuditExport` preset. */
export function createDefaultMiniKitchenRecipe(): MiniKitchenRecipeState {
  return {
    identity: {
      name: defaultRecipeName('Custom audit export'),
      description: '',
      tags: [],
      notes: '',
    },
    ingredients: { preset: 'customAuditExport' },
    query: {
      mode: 'audit-query',
    },
    processing: {},
    destinations: {
      fact: { mode: 'write-new', tier: 'local' },
      userInfo: { mode: 'default-colocate' },
    },
    auth: { mode: 'WebLogin' },
    executionMode: 'local-manual',
    advanced: { extraArguments: '' },
  };
}

/**
 * Apply preset overrides on top of the default state and normalize the
 * result. For the import preset (no overrides), the default state is returned
 * with `ingredients.preset` set to the import id; the importer fills in the
 * rest.
 */
export function createRecipeFromPreset(presetId: PresetId): MiniKitchenRecipeState {
  const base = createDefaultMiniKitchenRecipe();
  base.ingredients = { preset: presetId };
  const preset = getPresetById(presetId);
  if (!preset || preset.overrides === null) {
    return normalizeRecipe(base);
  }
  const seeded = applyOverrides(base, preset.overrides);
  if (overridesProvidedName(preset.overrides) && seeded.identity.name) {
    seeded.identity.name = defaultRecipeName(seeded.identity.name);
  }
  return normalizeRecipe(seeded);
}

function overridesProvidedName(overrides: PresetOverrides): boolean {
  return typeof overrides.identityName === 'string' && overrides.identityName.length > 0;
}

/**
 * Compose `<template> - YYYY-MM-DD HH:MM:SS UTC` so each new draft from a
 * preset is unique at creation time. Users can edit the name freely; the
 * suffix is only added once at the preset / custom-recipe seed step.
 */
function defaultRecipeName(template: string): string {
  const now = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  const stamp =
    now.getUTCFullYear() +
    '-' + pad(now.getUTCMonth() + 1) +
    '-' + pad(now.getUTCDate()) +
    ' ' + pad(now.getUTCHours()) +
    ':' + pad(now.getUTCMinutes()) +
    ':' + pad(now.getUTCSeconds()) +
    ' UTC';
  return `${template} - ${stamp}`;
}

/** Returns the list of built-in presets in catalog order. */
export function listDashboardPresets() {
  return DASHBOARD_PRESETS;
}

/**
 * True when `state` matches a freshly-created default recipe — ignoring the
 * `identity.name` field (whose UTC timestamp suffix changes every second and
 * therefore is not a meaningful signal of user intent).
 */
export function isDefaultMiniKitchenRecipe(state: MiniKitchenRecipeState): boolean {
  const baseline = createDefaultMiniKitchenRecipe();
  const a: MiniKitchenRecipeState = { ...state, identity: { ...state.identity, name: '' } };
  const b: MiniKitchenRecipeState = { ...baseline, identity: { ...baseline.identity, name: '' } };
  return JSON.stringify(a) === JSON.stringify(b);
}

// -----------------------------------------------------------------------------
// Preset auto-detection
// -----------------------------------------------------------------------------

/**
 * The builder settings that determine which Step 1 preset card is
 * highlighted: data scope (query mode / user-info), what-to-collect toggles,
 * custom activity types, rollup, and the dashboard column target. Every other
 * field is intentionally ignored so editing things like the date range,
 * filters, output target, or auth never changes the highlighted preset.
 */
interface PresetSignature {
  mode: string;
  includeUserInfo: boolean;
  onlyUserInfo: boolean;
  includeM365Usage: boolean;
  excludeCopilotInteraction: boolean;
  /** Activity types joined with a control char so order is significant. */
  activityTypes: string;
  rollup: string;
  dashboard: string;
}

function presetSignatureOf(state: MiniKitchenRecipeState): PresetSignature {
  const norm = normalizeRecipe(state);
  return {
    mode: norm.query.mode,
    includeUserInfo: Boolean(norm.query.includeUserInfo),
    onlyUserInfo: Boolean(norm.query.onlyUserInfo),
    includeM365Usage: Boolean(norm.query.includeM365Usage),
    excludeCopilotInteraction: Boolean(norm.query.excludeCopilotInteraction),
    activityTypes: (norm.processing.activityTypes ?? []).join('\u0000'),
    rollup: norm.processing.rollup ?? 'none',
    dashboard: norm.processing.dashboard ?? 'aio',
  };
}

function presetSignaturesEqual(a: PresetSignature, b: PresetSignature): boolean {
  return (
    a.mode === b.mode &&
    a.includeUserInfo === b.includeUserInfo &&
    a.onlyUserInfo === b.onlyUserInfo &&
    a.includeM365Usage === b.includeM365Usage &&
    a.excludeCopilotInteraction === b.excludeCopilotInteraction &&
    a.activityTypes === b.activityTypes &&
    a.rollup === b.rollup &&
    a.dashboard === b.dashboard
  );
}

/**
 * Presets that can be auto-detected from the four signature settings, in
 * priority order. `importLiteRecipeJson` is excluded (it has no overrides),
 * and `customAuditExport` acts as the catch-all fallback.
 */
const DETECTABLE_PRESET_IDS: readonly PresetId[] = [
  'aiInOneDashboard',
  'aiBusinessValueDashboard',
  'm365UsageAnalyticsDashboard',
  'userInfoOnly',
  'customAuditExport',
];

const PRESET_SIGNATURES: ReadonlyArray<{ id: PresetId; sig: PresetSignature }> =
  DETECTABLE_PRESET_IDS.map(id => ({
    id,
    sig: presetSignatureOf(createRecipeFromPreset(id)),
  }));

/**
 * Returns the preset whose four signature settings (data scope,
 * what-to-collect, custom activity types, rollup) exactly match `state`, or
 * `'customAuditExport'` when none match. Used by the builder to keep the Step
 * 1 preset highlight in sync with those settings on a new, unsaved recipe.
 */
export function detectMatchingPreset(state: MiniKitchenRecipeState): PresetId {
  const sig = presetSignatureOf(state);
  for (const entry of PRESET_SIGNATURES) {
    if (presetSignaturesEqual(sig, entry.sig)) {
      return entry.id;
    }
  }
  return 'customAuditExport';
}


// -----------------------------------------------------------------------------
// internals
// -----------------------------------------------------------------------------

function applyOverrides(
  state: MiniKitchenRecipeState,
  overrides: PresetOverrides,
): MiniKitchenRecipeState {
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

  if (overrides.queryMode !== undefined) {
    next.query.mode = overrides.queryMode;
  }
  if (overrides.identityName !== undefined) {
    next.identity.name = overrides.identityName;
  }
  if (overrides.identityDescription !== undefined) {
    next.identity.description = overrides.identityDescription;
  }
  if (overrides.includeM365Usage !== undefined) {
    next.query.includeM365Usage = overrides.includeM365Usage;
  }
  if (overrides.excludeCopilotInteraction !== undefined) {
    next.query.excludeCopilotInteraction = overrides.excludeCopilotInteraction;
  }
  if (overrides.includeUserInfo !== undefined) {
    next.query.includeUserInfo = overrides.includeUserInfo;
  }
  if (overrides.onlyUserInfo !== undefined) {
    next.query.onlyUserInfo = overrides.onlyUserInfo;
  }
  if (overrides.rollup !== undefined) {
    next.processing.rollup = overrides.rollup;
  }
  if (overrides.dashboard !== undefined) {
    next.processing.dashboard = overrides.dashboard;
  }
  if (overrides.activityTypes !== undefined) {
    next.processing.activityTypes = overrides.activityTypes;
  }
  if (overrides.outputMode !== undefined) {
    next.destinations.fact.mode = overrides.outputMode;
  }
  if (overrides.storageTier !== undefined) {
    next.destinations.fact.tier = overrides.storageTier;
  }
  if (overrides.userInfoOutputMode !== undefined) {
    next.destinations.userInfo.mode = overrides.userInfoOutputMode;
  }

  return next;
}
