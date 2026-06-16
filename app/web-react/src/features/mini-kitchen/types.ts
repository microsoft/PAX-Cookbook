/**
 * Mini-Kitchen data contracts.
 *
 * This module defines the in-memory recipe state edited by the Mini-Kitchen UI,
 * the exported lite recipe shape consumed by the full PAX Cookbook app, and the
 * supporting catalog / preset / permission shapes used by the static data files
 * and helper libraries in `src/features/mini-kitchen/`.
 *
 * No values are exported from this file. Schema-version literals and other
 * constants live in `./data/mini-kitchen-constants.ts`.
 */

// -----------------------------------------------------------------------------
// Identifier unions
// -----------------------------------------------------------------------------

/** Built-in preset identifiers. Reserved names. */
export type PresetId =
  | 'aiInOneDashboard'
  | 'aiBusinessValueDashboard'
  | 'm365UsageAnalyticsDashboard'
  | 'customAuditExport'
  | 'userInfoOnly'
  | 'importLiteRecipeJson'
  | 'importPaxRecipeJson';

/** Top-level query mode. Drives which fields are active in the recipe. */
export type QueryMode = 'audit-query' | 'user-info-only';

/**
 * Audit date-range mode.
 *  - `'custom'` — an explicit `-StartDate`/`-EndDate` window (the historical
 *    behavior). `undefined` is treated as `'custom'` for backward compatibility
 *    with recipes saved before this field existed.
 *  - `'previous-day'` — deliberately omit both date switches so PAX queries the
 *    previous full UTC day (yesterday 00:00 UTC through today 00:00 UTC). Absence
 *    of the switches IS the signal; PAX keys off `$PSBoundParameters.ContainsKey`.
 *    Ideal for scheduled daily and append runs.
 */
export type DateRangeMode = 'previous-day' | 'custom';

/** Rollup behavior for audit-query results. */
export type RollupMode = 'none' | 'rollup' | 'rollup-plus-raw';

/**
 * Dashboard column target for rollup output. `aibv` selects the AI Business
 * Value 50-column superset; `aio` (and `undefined`) is AI-in-One, PAX's default.
 */
export type DashboardTarget = 'aio' | 'aibv';

/**
 * Audit activity output layout. `combined` emits `-CombineOutput` and writes
 * one combined CSV covering every selected activity type; `separate` omits
 * the switch and lets PAX write one CSV per activity type. Only meaningful
 * when more than one activity type is resolved and `-IncludeM365Usage` is
 * off; PAX v1.11.3 auto-enables `-CombineOutput` for `-IncludeM365Usage`
 * and rollup runs regardless of this value.
 */
export type OutputCombineMode = 'combined' | 'separate';

/** Fact table output mode. */
export type OutputMode = 'write-new' | 'append';

/** User info output mode. */
export type UserInfoOutputMode = 'default-colocate' | 'write-new' | 'append';

/** Storage tier for the fact destination. */
export type StorageTier = 'local' | 'sharepoint' | 'fabric';

/** Authentication mode. Mini-Kitchen never stores secrets. */
export type AuthMode =
  | 'WebLogin'
  | 'DeviceCode'
  | 'AppRegistrationSecret'
  | 'AppRegistrationCertificate'
  | 'ManagedIdentity';

/** Execution context for the generated command. */
export type ExecutionMode =
  | 'local-manual'
  | 'local-scheduled'
  | 'fabric-hosted'
  | 'azure-hosted';

/** Agent filter behavior for audit-query results. */
export type AgentFilterMode = 'none' | 'ids-only' | 'agents-only' | 'exclude-agents';

/** Prompt filter values supported by PAX. */
export type PromptFilter = 'Prompt' | 'Response' | 'Both' | 'Null';

// -----------------------------------------------------------------------------
// Recipe sub-shapes
// -----------------------------------------------------------------------------

export interface LiteRecipeIdentity {
  name: string;
  description?: string;
  tags?: readonly string[];
  notes?: string;
}

export interface LiteRecipeIngredients {
  preset: PresetId;
}

export interface LiteRecipeQuery {
  mode: QueryMode;
  /**
   * Audit date-range mode. `undefined` === `'custom'` for backward
   * compatibility. `'previous-day'` omits both date switches (see DateRangeMode).
   */
  dateMode?: DateRangeMode;
  startDate?: string;
  endDate?: string;
  includeM365Usage?: boolean;
  excludeCopilotInteraction?: boolean;
  includeUserInfo?: boolean;
  onlyUserInfo?: boolean;
}

export interface LiteRecipeAgentFilter {
  mode: AgentFilterMode;
  ids?: readonly string[];
}

export interface LiteRecipeProcessing {
  activityTypes?: readonly string[];
  userIds?: readonly string[];
  groupNames?: readonly string[];
  agentFilter?: LiteRecipeAgentFilter;
  promptFilter?: PromptFilter;
  rollup?: RollupMode;
  /** Dashboard column target. `undefined` means AI-in-One (AIO), the default. */
  dashboard?: DashboardTarget;
  /**
   * Audit activity output layout. Optional so older lite recipes round-trip
   * without rewrite — the renderer treats `undefined` as `'combined'` for
   * eligibility purposes.
   */
  outputCombineMode?: OutputCombineMode;
}

export interface LiteRecipeFactDestination {
  mode: OutputMode;
  tier: StorageTier;
  path?: string;
}

export interface LiteRecipeUserInfoDestination {
  mode: UserInfoOutputMode;
  path?: string;
}

export interface LiteRecipeDestinations {
  fact: LiteRecipeFactDestination;
  userInfo: LiteRecipeUserInfoDestination;
}

/**
 * Auth fields written to the lite recipe. Mini-Kitchen never persists any
 * client secret. `AppRegistrationSecret` mode relies on an out-of-band env
 * variable at runtime; the lite recipe only declares the mode.
 */
export interface LiteRecipeAuth {
  mode: AuthMode;
  tenantId?: string;
  clientId?: string;
  certificateThumbprint?: string;
  /**
   * Optional reference to a saved Chef's Key (CK-1, WCM-backed). App-registration
   * recipes bind one to become READY -- the bound key carries the client id /
   * certificate thumbprint, and any secret lives only in Windows Credential
   * Manager, never in the recipe. WebLogin / DeviceCode may optionally bind one
   * for unattended scheduling. Binding is never a save requirement.
   */
  chefKeyId?: string;
}

export interface LiteRecipeAdvanced {
  extraArguments?: string;
  /**
   * Provenance-only. When a recipe is imported, any prior PAX script path is
   * preserved here and round-tripped into the `.paxlite` export. The app runs
   * the managed PAX engine, so this value is never read, validated, rendered
   * into the command preview, or used to gate save, readiness, or running.
   */
  scriptPath?: string;
}

// -----------------------------------------------------------------------------
// Working recipe state (in-memory; edited by the UI)
// -----------------------------------------------------------------------------

/**
 * The full editable recipe state managed by the Mini-Kitchen UI. Currently a
 * superset-by-name (and identical-by-shape) wrapper around `LiteRecipe['recipe']`.
 * Held separately so future UI-only fields can be added without changing the
 * exported lite recipe schema.
 */
export interface MiniKitchenRecipeState {
  identity: LiteRecipeIdentity;
  ingredients: LiteRecipeIngredients;
  query: LiteRecipeQuery;
  processing: LiteRecipeProcessing;
  destinations: LiteRecipeDestinations;
  auth: LiteRecipeAuth;
  executionMode: ExecutionMode;
  advanced: LiteRecipeAdvanced;
}

// -----------------------------------------------------------------------------
// Lite recipe export envelope
// -----------------------------------------------------------------------------

export interface LiteRecipeCreatedBy {
  tool: string;
  site: string;
}

export interface LiteRecipeCompatibility {
  targetPaxVersion: string;
  switchCatalogVersion: string;
  cookbookRecipeSchemaVersion: number;
}

export interface LiteRecipeImportBehavior {
  state: 'needsPrep';
  reason: string;
}

export interface LiteRecipePermissionsBlock {
  required: readonly PermissionEntry[];
  warnings: readonly string[];
}

export interface LiteRecipe {
  kind: 'pax-cookbook-mini-recipe';
  schemaVersion: '1.0';
  createdBy: LiteRecipeCreatedBy;
  compatibility: LiteRecipeCompatibility;
  recipe: {
    identity: LiteRecipeIdentity;
    ingredients: LiteRecipeIngredients;
    query: LiteRecipeQuery;
    processing: LiteRecipeProcessing;
    destinations: LiteRecipeDestinations;
    auth: LiteRecipeAuth;
    executionMode: ExecutionMode;
    advanced: LiteRecipeAdvanced;
  };
  commandPreview: CommandPreview;
  permissions: LiteRecipePermissionsBlock;
  importBehavior: LiteRecipeImportBehavior;
}

// -----------------------------------------------------------------------------
// Command preview
// -----------------------------------------------------------------------------

export interface CommandPreview {
  shell: 'pwsh';
  argv: readonly string[];
  multiline: string;
  singleLine: string;
}

// -----------------------------------------------------------------------------
// Permissions
// -----------------------------------------------------------------------------

export type PermissionGroup = 'graph' | 'runtime' | 'environment' | 'output' | 'info';

/**
 * Structured category for a permission entry. Surfaces the *kind* of
 * requirement so the future UI can render the right header / icon without
 * re-deriving meaning from the rule copy.
 *
 *   - `tenant-admin-consent`: needs Entra tenant admin consent on the app
 *     registration (Graph application/delegated scope).
 *   - `runtime-prerequisite`: needs to be present at the local runtime
 *     (PowerShell 7, Python, etc.).
 *   - `environment-setting`: a tenant/Fabric/operator-controlled toggle that
 *     must already be enabled (unified audit logging, Fabric service-principal
 *     tenant setting, etc.).
 *   - `output-location`: write access on the destination (local path,
 *     SharePoint library, Fabric workspace/lakehouse).
 *   - `local-machine`: state on the operator's machine (certificate in the
 *     local cert store, scheduled task account).
 *   - `informational`: a callout the future UI should surface but which does
 *     not by itself imply an additional permission grant.
 */
export type PermissionAppliesTo =
  | 'tenant-admin-consent'
  | 'runtime-prerequisite'
  | 'environment-setting'
  | 'output-location'
  | 'local-machine'
  | 'informational';

/**
 * Severity hint for a permission entry. `required` = the generated command
 * will fail at runtime without it; `recommended` = strongly advised but not
 * strictly enforced; `informational` = pure callout (e.g. append-mode caveat,
 * auth-mode mechanics).
 */
export type PermissionSeverity = 'required' | 'recommended' | 'informational';

export interface PermissionEntry {
  id: string;
  name: string;
  group: PermissionGroup;
  appliesTo: PermissionAppliesTo;
  requiredBecause: string;
  triggeredBy: readonly string[];
  /** Free-form human label inherited from the originating rule. */
  appliesToLabel?: string;
  severity?: PermissionSeverity;
  notes?: readonly string[];
}

export interface PermissionsReport {
  required: readonly PermissionEntry[];
  warnings: readonly string[];
  assumptions: readonly string[];
}

// -----------------------------------------------------------------------------
// Warnings
// -----------------------------------------------------------------------------

export type WarningSeverity = 'info' | 'warning' | 'error';

export interface WarningEntry {
  id: string;
  severity: WarningSeverity;
  message: string;
  field?: string;
}

// -----------------------------------------------------------------------------
// PAX switch catalog
// -----------------------------------------------------------------------------

/** How a switch contributes to the rendered command. */
export type PaxSwitchKind = 'flag' | 'string' | 'string-list' | 'enum' | 'path';

export interface PaxSwitchDefinition {
  /** Switch name without the leading hyphen (e.g. `StartDate`). */
  name: string;
  kind: PaxSwitchKind;
  /** For `enum` switches, the allowed values. */
  enumValues?: readonly string[];
  description: string;
  /** Minimum PAX version that ships this switch (semver-lite). */
  since?: string;
  /** First PAX version in which the switch was removed. */
  until?: string;
  /** Other switch names that may not be combined with this one. */
  conflictsWith?: readonly string[];
  /** Other switch names that must also be present when this one is used. */
  requires?: readonly string[];
  notes?: readonly string[];
}

/** Switches that were removed from PAX or are otherwise not supported. */
export interface RemovedSwitch {
  name: string;
  reason: string;
  alternative?: string;
  /** Substituted for `-<name>` in user-facing warnings when set. */
  userFacingName?: string;
}

// -----------------------------------------------------------------------------
// Dashboard presets
// -----------------------------------------------------------------------------

/**
 * Partial recipe overrides applied on top of the base default state.
 * Each preset narrows a subset of fields; `null` for the import preset
 * (which produces no overrides; the importer fills the state instead).
 */
export interface PresetOverrides {
  queryMode?: QueryMode;
  identityName?: string;
  identityDescription?: string;
  includeM365Usage?: boolean;
  excludeCopilotInteraction?: boolean;
  includeUserInfo?: boolean;
  onlyUserInfo?: boolean;
  rollup?: RollupMode;
  dashboard?: DashboardTarget;
  activityTypes?: readonly string[];
  outputMode?: OutputMode;
  storageTier?: StorageTier;
  userInfoOutputMode?: UserInfoOutputMode;
}

export interface DashboardPresetDefinition {
  id: PresetId;
  name: string;
  description: string;
  /** Concise pointers shown alongside the preset in the picker. */
  notes: readonly string[];
  /** `null` for the import preset; otherwise field overrides to merge. */
  overrides: PresetOverrides | null;
}

// -----------------------------------------------------------------------------
// Storage targets
// -----------------------------------------------------------------------------

export type StorageTargetStatus =
  | 'supported'
  | 'catalog-gated'
  | 'not-available-for-selected-version';

/**
 * How PAX picks up the storage tier at runtime. `local-path` means PAX
 * accepts a drive-rooted Windows/POSIX path; `url-auto-detected` means
 * PAX classifies the URL shape via the `Get-PathTier` helper and routes
 * the output through the appropriate writer (SharePoint, Fabric OneLake).
 */
export type StorageTierDetection = 'local-path' | 'url-auto-detected';

export interface StorageTargetDefinition {
  id: StorageTier;
  name: string;
  status: StorageTargetStatus;
  /** PAX switch names this destination relies on. */
  switches: readonly string[];
  /** Permission rule ids triggered by selecting this destination. */
  permissions: readonly string[];
  /** Caveats shown in the UI. */
  warnings: readonly string[];
  notes: readonly string[];
  /** Optional hint for how PAX picks the tier at runtime. */
  tierDetection?: StorageTierDetection;
}

// -----------------------------------------------------------------------------
// Permission rules (static)
// -----------------------------------------------------------------------------

export interface PermissionRule {
  id: string;
  name: string;
  group: PermissionGroup;
  /** Structured category for the future UI. See `PermissionAppliesTo`. */
  appliesTo: PermissionAppliesTo;
  /** Human-readable label for what the rule covers. */
  appliesToLabel: string;
  /** Template text explaining why the permission is required. */
  requiredBecause: string;
  /** Recipe field paths whose values influence this rule. Documentation only. */
  triggeredBy: readonly string[];
  /** Optional severity hint. Defaults to `required` if omitted. */
  severity?: PermissionSeverity;
  notes?: readonly string[];
}

// -----------------------------------------------------------------------------
// Phase 7: saved recipes, export, import
// -----------------------------------------------------------------------------

/**
 * How a saved recipe entered the browser store.
 *
 *   - `created`:    Built directly inside Mini-Kitchen and saved by the user.
 *   - `duplicated`: Created from a `Duplicate` action against an existing
 *                   saved record. Carries over the source record's preset and
 *                   fields but gets a fresh id and timestamps.
 *   - `imported`:   Loaded from a `LiteRecipe` JSON file via the importer.
 *                   Carries the original `LiteRecipe.recipe.ingredients.preset`
 *                   for surface-area labeling.
 */
export type SavedRecipeSource = 'created' | 'duplicated' | 'imported';

/**
 * A single Mini-Kitchen recipe persisted in the browser localStorage list.
 *
 * `state` is the full editable recipe state (the same shape edited by
 * `MiniKitchenShell`). It is stored *after* the scrubber pass — fields that
 * matched `containsLikelySecret` are dropped before persistence and surfaced
 * to the UI as `LiteRecipeScrubWarning`s.
 *
 * `importState` is always `'needsPrep'` per Decision D15: Mini-Kitchen saved
 * recipes never represent runtime-ready Cookbook Recipe Takeout payloads.
 */
export interface SavedMiniKitchenRecipe {
  id: string;
  name: string;
  description?: string;
  presetId: PresetId;
  state: MiniKitchenRecipeState;
  createdAt: string;
  updatedAt: string;
  source: SavedRecipeSource;
  importState: 'needsPrep';
}

/**
 * A scrub finding emitted by the export / save layers.
 *
 *   - `path` is a dot-delimited path into `LiteRecipe.recipe` (or the
 *     saved-recipe state) for the field that was scrubbed. Example:
 *     `'recipe.advanced.extraArguments'`.
 *   - `reason` is a short human-readable explanation. Surfaced verbatim
 *     in the saved-rail and Lite recipe panel.
 *   - `severity` is `'warning'` for "we removed something that looked
 *     sensitive" and `'info'` for "we adjusted this for portability".
 */
export interface LiteRecipeScrubWarning {
  id: string;
  path: string;
  reason: string;
  severity: 'info' | 'warning';
}

/**
 * Successful exporter result. `recipe` is the scrubbed envelope ready to be
 * stringified; `warnings` is the list of fields the scrubber removed.
 */
export interface LiteRecipeExportResult {
  ok: true;
  recipe: LiteRecipe;
  warnings: readonly LiteRecipeScrubWarning[];
}

/**
 * Importer / validator result. Mirrors the renderer / resolver fixture
 * convention so the UI can switch on `ok`.
 */
export type LiteRecipeImportResult =
  | {
      ok: true;
      recipe: LiteRecipe;
      state: MiniKitchenRecipeState;
      warnings: readonly LiteRecipeScrubWarning[];
    }
  | { ok: false; errors: readonly string[] };

/**
 * Whether the browser exposes a usable localStorage instance for the saved
 * recipes list. `'unavailable'` covers private-mode tabs and quota errors;
 * `'error'` covers an exception thrown during the initial read.
 */
export type SavedRecipesStorageState = 'ready' | 'unavailable' | 'error';

