/**
 * Mini-Kitchen runtime constants.
 *
 * Schema versions, identity stamps, default target version, and reserved
 * localStorage keys. Pure values (no logic, no React imports).
 */

/** Envelope version for the lite recipe export shape. */
export const LITE_RECIPE_SCHEMA_VERSION = '1.0' as const;

/**
 * Internal Mini-Kitchen state schema version. Tracks the editor's in-memory
 * recipe shape and saved-recipe storage shape. Bumped independently of the
 * exported lite recipe version.
 */
export const MINI_KITCHEN_SCHEMA_VERSION = '1.0' as const;

/**
 * Reference to the full PAX Cookbook app's recipe schema major. Used by the
 * importer in the full app to validate a lite recipe is compatible with the
 * current Cookbook recipe shape.
 */
export const COOKBOOK_RECIPE_SCHEMA_VERSION = 1 as const;

/**
 * Date-stamped version of the PAX switch catalog shipped with this build.
 * Bumped (with a `-N` suffix on same-day re-edits) whenever any entry in
 * `data/pax-switch-catalog.ts` changes. See risk R13 in the master plan.
 */
export const SWITCH_CATALOG_VERSION = '2026.05.29-2' as const;

/**
 * Default PAX script version targeted by new recipes. Surfaced in the builder
 * header strip ("Supports PAX Script v…") and stamped into the compatibility
 * block of exported lite recipes. Tracks the managed PAX engine that PAX
 * Cookbook bundles and runs (the broker remains the authoritative source of the
 * persisted `paxAdapterVersion`; this constant only drives builder/export copy).
 */
export const TARGET_PAX_VERSION_DEFAULT = '1.11.8' as const;

/** Identity stamps written into the lite recipe `createdBy` block. */
export const MINI_KITCHEN_CREATED_BY_TOOL = 'PAX Cookbook Recipe Builder' as const;
export const MINI_KITCHEN_CREATED_BY_SITE = 'https://microsoft.github.io/PAX-Cookbook/' as const;

/**
 * Canonical URL for the official PAX repository. Surfaced anywhere the user
 * needs to download the PAX PowerShell script (builder header, script-path
 * field, review section, Help / FAQ).
 */
export const PAX_REPO_URL = 'https://github.com/microsoft/pax' as const;

/**
 * The lite recipe import-behavior state. Mini-Kitchen produces drafts that
 * always need a final review pass in the full PAX Cookbook app before they
 * can run.
 */
export const MINI_KITCHEN_IMPORT_STATE = 'needsPrep' as const;

/**
 * Default human-readable reason text written into the lite recipe
 * `importBehavior.reason` field.
 */
export const MINI_KITCHEN_IMPORT_REASON =
  'Drafted in the builder. Validate permissions, paths, and runtime targets before running.' as const;

/** Reserved localStorage key for the saved-recipes array. */
export const STORAGE_KEY_RECIPES = 'pax-mini-kitchen.recipes.v1' as const;

/** Reserved localStorage key for the UI preferences blob. */
export const STORAGE_KEY_PREFERENCES = 'pax-mini-kitchen.preferences.v1' as const;

/**
 * Reserved localStorage key for the single latest browser-local builder
 * draft. Holds at most one autosaved `MiniKitchenRecipeState` so an
 * unsaved in-progress recipe survives reload / tab close. Separate from
 * the saved-recipes list — never shown as a Saved Recipe card and never
 * synced anywhere.
 */
export const STORAGE_KEY_DRAFT = 'pax-mini-kitchen.draft.v1' as const;

/**
 * Envelope schema version for the autosaved draft record. Bumped only if
 * the on-disk draft envelope shape changes in a way that older records
 * cannot be safely consumed. Drafts written with a different value are
 * discarded on load.
 */
export const DRAFT_RECORD_SCHEMA_VERSION = '1.0' as const;

/**
 * File extension for exported lite recipes. The file contents are JSON;
 * the compound extension flags the payload as a Mini-Kitchen lite recipe
 * so the full PAX Cookbook importer can recognize it without ambiguity.
 */
export const LITE_RECIPE_FILE_EXTENSION = '.json.paxlite' as const;

/**
 * Previous lite recipe export extension. The import picker still accepts
 * this so users with archived `.json` exports from earlier builds can
 * re-import them without renaming.
 */
export const LEGACY_LITE_RECIPE_FILE_EXTENSION = '.json' as const;

/**
 * File extension for a full PAX Cookbook recipe export. The file contents are
 * JSON wrapping the same persisted full recipe shape the broker returns from
 * `GET /api/v1/recipes/{id}` under `recipe`; the `.pax` extension flags the
 * payload as a full Cookbook recipe so the importer recognizes it without
 * ambiguity (the `.paxlite` extension stays reserved for the lighter
 * Mini-Kitchen handoff). Both round-trip through the same import picker.
 */
export const FULL_PAX_RECIPE_FILE_EXTENSION = '.pax' as const;
