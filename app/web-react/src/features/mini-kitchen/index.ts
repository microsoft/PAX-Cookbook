/**
 * Mini-Kitchen feature barrel.
 *
 * Convenience re-exports for the Phase 2 surface area. Consumers should
 * prefer importing from the specific sub-path when adding new modules in
 * later phases; this barrel exists so the current set of types, constants,
 * static catalogs, and helpers can be loaded as a single namespace by
 * upcoming UI + renderer work.
 */

export type * from './types';

export {
  LITE_RECIPE_SCHEMA_VERSION,
  MINI_KITCHEN_SCHEMA_VERSION,
  COOKBOOK_RECIPE_SCHEMA_VERSION,
  SWITCH_CATALOG_VERSION,
  TARGET_PAX_VERSION_DEFAULT,
  MINI_KITCHEN_CREATED_BY_TOOL,
  MINI_KITCHEN_CREATED_BY_SITE,
  MINI_KITCHEN_IMPORT_STATE,
  MINI_KITCHEN_IMPORT_REASON,
  STORAGE_KEY_RECIPES,
  STORAGE_KEY_PREFERENCES,
} from './data/mini-kitchen-constants';

export {
  PAX_SWITCH_CATALOG,
  REMOVED_OR_UNSUPPORTED_SWITCHES,
  getSwitchDefinition,
  isRemovedSwitch,
} from './data/pax-switch-catalog';

export {
  DASHBOARD_PRESETS,
  RESERVED_PRESET_NAMES,
  getPresetById,
} from './data/dashboard-presets';

export { STORAGE_TARGETS, getStorageTarget } from './data/storage-targets';

export { PERMISSION_RULES, getPermissionRule } from './data/permission-rules';

export {
  createDefaultMiniKitchenRecipe,
  createRecipeFromPreset,
  isDefaultMiniKitchenRecipe,
  listDashboardPresets,
} from './lib/defaultRecipe';

export { normalizeRecipe } from './lib/normalizeRecipe';

export {
  compareVersions,
  versionAtLeast,
  versionLessThan,
  assessVersionCompatibility,
  type VersionIssueSeverity,
  type VersionCompatibilityIssue,
} from './lib/versionCompatibility';

export {
  detectPathWarnings,
  hasPathWarnings,
  type PathWarning,
  type PathWarningSeverity,
} from './lib/pathWarnings';

export {
  scanForSecrets,
  containsLikelySecret,
  type SecretMatch,
} from './lib/secretScanner';

export {
  tokenizeAdvancedArgs,
  analyzeAdvancedArgs,
  type AdvancedArgToken,
  type AdvancedArgsAnalysis,
} from './lib/advancedArgs';

export {
  renderPaxCommand,
  buildPaxArgv,
  quotePowerShellArg,
  renderPowerShellSingleLine,
  renderPowerShellMultiline,
  PAX_SCRIPT_PATH_PLACEHOLDER,
  type RenderedCommand,
  type CommandRenderBlockedItem,
  type CommandRenderBlockedKind,
  type CommandReadinessBlocker,
} from './lib/commandRenderer';

export { resolvePermissions } from './lib/permissionsResolver';
