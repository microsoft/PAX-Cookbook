/**
 * Static PAX switch catalog.
 *
 * Single source of truth for known PAX command switches that Mini-Kitchen
 * is allowed to emit, plus the list of switches that the runtime PAX script
 * blocks at the param() gate (so Mini-Kitchen must refuse to emit them
 * regardless of where they came from — structured field, advanced args,
 * or imported recipe).
 *
 * Catalog version: `SWITCH_CATALOG_VERSION` in `./mini-kitchen-constants.ts`.
 * Bump that constant on any edit to this file (same-day re-edits use a
 * `-N` suffix per risk R13 in the master plan).
 *
 * Validated against PAX v1.11.3 (`_temp/PAX_Resources/PAX_Purview_Audit_Log_Processor_v1.11.3.ps1`).
 *
 * Constraints honored here:
 *   - No generic `-RecordTypes` / `-ServiceTypes` switch is exposed. Those
 *     parameters exist in PAX but Mini-Kitchen drives them implicitly
 *     through dashboard presets, `-ActivityTypes`, and the M365 usage /
 *     agent / prompt filters.
 *   - Fabric / OneLake Delta Lake output is supported by PAX v1.11.3
 *     today through the same `-OutputPath` / `-AppendFile` /
 *     `-OutputPathUserInfo` / `-AppendUserInfo` switches as Local and
 *     SharePoint. PAX auto-detects the storage tier (Local / SharePoint /
 *     Fabric) from the URL shape via the `Get-PathTier` helper. There are
 *     no Fabric-specific switches, and none are invented here.
 *   - No `-ClientSecret` entry. The lite recipe never carries a secret;
 *     the runtime sources it from an out-of-band env variable.
 *   - Catalog uses the exact param-block names from v1.11.3, including
 *     `ClientCertificateThumbprint` (not the older `CertificateThumbprint`).
 */

import type { PaxSwitchDefinition, RemovedSwitch } from '../types';

// -----------------------------------------------------------------------------
// Active catalog
// -----------------------------------------------------------------------------

export const PAX_SWITCH_CATALOG: readonly PaxSwitchDefinition[] = [
  // ---- Date range ----------------------------------------------------------
  {
    name: 'StartDate',
    kind: 'string',
    description: 'Inclusive start of the audit date range (yyyy-MM-dd).',
    since: '1.0.0',
    notes: ['The builder does not validate that StartDate <= EndDate.'],
  },
  {
    name: 'EndDate',
    kind: 'string',
    description: 'Exclusive end of the audit date range (yyyy-MM-dd).',
    since: '1.0.0',
  },

  // ---- M365 usage bundle ---------------------------------------------------
  {
    name: 'IncludeM365Usage',
    kind: 'flag',
    description:
      'Pull the broader M365 audit usage bundle (Exchange / OneDrive / SharePoint / Teams audit branches) in addition to Copilot interactions.',
    since: '1.0.0',
    conflictsWith: ['OnlyUserInfo'],
  },
  {
    name: 'ExcludeCopilotInteraction',
    kind: 'flag',
    description:
      'Drop the CopilotInteraction branch from the audit pull. Standalone in PAX — can be combined with `-IncludeM365Usage` to keep only the broader M365 audit data, or used on its own to skip CopilotInteraction in a plain audit query.',
    since: '1.0.0',
    conflictsWith: ['OnlyUserInfo'],
  },

  // ---- User info -----------------------------------------------------------
  {
    name: 'IncludeUserInfo',
    kind: 'flag',
    description: 'Resolve Entra user info for the users seen in the audit data.',
    since: '1.0.0',
    conflictsWith: ['OnlyUserInfo'],
  },
  {
    name: 'OnlyUserInfo',
    kind: 'flag',
    description:
      'Skip the audit query entirely and return only the Entra user info pull. Implies IncludeUserInfo.',
    since: '1.0.0',
    conflictsWith: ['IncludeM365Usage', 'IncludeUserInfo', 'ExcludeCopilotInteraction'],
  },

  // ---- Audit filters -------------------------------------------------------
  {
    name: 'ActivityTypes',
    kind: 'string-list',
    description:
      'Filter to specific audit activity types (e.g. CopilotInteraction). Curated values; no generic RecordTypes / ServiceTypes.',
    since: '1.0.0',
  },
  {
    name: 'UserIds',
    kind: 'string-list',
    description: 'Restrict audit results to one or more user principal names.',
    since: '1.0.0',
  },
  {
    name: 'GroupNames',
    kind: 'string-list',
    description:
      'Restrict audit results to members of one or more Entra groups. Triggers Graph group expansion at runtime.',
    since: '1.0.0',
  },
  {
    name: 'AgentId',
    kind: 'string-list',
    description: 'Filter to one or more Copilot agent ids.',
    since: '1.0.0',
  },
  {
    name: 'AgentsOnly',
    kind: 'flag',
    description: 'Keep only Copilot interactions that involved a Copilot agent.',
    since: '1.0.0',
    conflictsWith: ['ExcludeAgents'],
  },
  {
    name: 'ExcludeAgents',
    kind: 'flag',
    description: 'Drop Copilot interactions that involved a Copilot agent.',
    since: '1.0.0',
    conflictsWith: ['AgentsOnly'],
  },
  {
    name: 'PromptFilter',
    kind: 'enum',
    enumValues: ['Prompt', 'Response', 'Both', 'Null'],
    description: 'Filter Copilot interactions by whether prompt/response text is present.',
    since: '1.0.0',
  },

  // ---- Rollup --------------------------------------------------------------
  {
    name: 'Rollup',
    kind: 'flag',
    description:
      'Aggregate audit rows into per-user / per-period rollups instead of emitting raw rows.',
    since: '1.0.0',
    conflictsWith: ['RollupPlusRaw'],
  },
  {
    name: 'RollupPlusRaw',
    kind: 'flag',
    description: 'Emit both the rollup aggregate and the raw audit rows.',
    since: '1.0.0',
    conflictsWith: ['Rollup'],
  },

  // ---- Fact output ---------------------------------------------------------
  // PAX v1.11.3 auto-detects the storage tier (Local / SharePoint / Fabric)
  // from the URL shape passed here. The same switch carries a Windows path,
  // a SharePoint URL, or a OneLake Lakehouse URL.
  {
    name: 'OutputPath',
    kind: 'path',
    description: 'Write the fact output to a new file at this path (write-new mode). Accepts a drive-rooted local path, a SharePoint document-library URL, or a Fabric OneLake Lakehouse URL.',
    since: '1.0.0',
    conflictsWith: ['AppendFile'],
  },
  {
    name: 'AppendFile',
    kind: 'path',
    description: 'Append the fact output to an existing file (append mode). Accepts a bare filename (resolved against -OutputPath’s directory) or a full path — local, SharePoint URL, or Fabric OneLake URL.',
    since: '1.0.0',
    conflictsWith: ['OutputPath'],
  },

  // ---- User info output ----------------------------------------------------
  {
    name: 'OutputPathUserInfo',
    kind: 'path',
    description:
      'Destination for the EntraUsers / MAC licensing CSV. PAX requires this (or -AppendUserInfo) whenever -IncludeUserInfo or -OnlyUserInfo is in scope. Co-locate mode sets it to the audit output\'s directory so the user-info CSV lands beside the fact file. Accepts a local path, SharePoint URL, or Fabric OneLake URL.',
    since: '1.0.0',
    conflictsWith: ['AppendUserInfo'],
  },
  {
    name: 'AppendUserInfo',
    kind: 'path',
    description: 'Append the user info output to an existing file (append mode). Accepts a bare filename (resolved against -OutputPath’s directory) or a full path — local, SharePoint URL, or Fabric OneLake URL.',
    since: '1.0.0',
    conflictsWith: ['OutputPathUserInfo'],
  },

  // ---- Auth context (non-secret) -------------------------------------------
  {
    name: 'Auth',
    kind: 'enum',
    enumValues: ['WebLogin', 'DeviceCode', 'Credential', 'Silent', 'AppRegistration', 'ManagedIdentity'],
    description:
      'Authentication method. The builder omits this switch for the default WebLogin path; it emits `-Auth AppRegistration` for both app-registration auth variants (secret vs certificate is selected by the companion switch), `-Auth DeviceCode` for device-code auth, and `-Auth ManagedIdentity` for managed-identity auth. `Credential` and `Silent` are accepted by PAX (validated against the param() ValidateSet) but the builder does not surface them as first-class auth modes — supply them through Advanced args if you need them.',
    since: '1.0.0',
  },
  {
    name: 'TenantId',
    kind: 'string',
    description: 'Entra tenant id. Used in app-registration and managed-identity auth modes.',
    since: '1.0.0',
  },
  {
    name: 'ClientId',
    kind: 'string',
    description: 'App registration / managed identity client id.',
    since: '1.0.0',
  },
  {
    name: 'ClientCertificateThumbprint',
    kind: 'string',
    description:
      'Thumbprint of the certificate used by app-registration-with-certificate auth. Paired with TenantId + ClientId.',
    since: '1.0.0',
    requires: ['TenantId', 'ClientId'],
  },

  // ---- Operator knobs (audit query partitioning / pacing) ------------------
  // Names and defaults verified against the PAX v1.11.3 param() block.
  // Numeric params are typed as `string` in this catalog because the
  // advanced-args path is text-based; the renderer passes the raw value
  // through to PowerShell, which coerces it to int/double per the param type.
  {
    name: 'BlockHours',
    kind: 'string',
    description:
      'Hours per audit query block. PAX defaults to 0.5. Larger values reduce request count; smaller values reduce per-block latency and result-size pressure.',
    since: '1.0.0',
  },
  {
    name: 'PartitionHours',
    kind: 'string',
    description:
      'Sub-partition each block into N-hour slices for finer-grained Purview pagination. PAX defaults to 0 (disabled).',
    since: '1.0.0',
  },
  {
    name: 'ResultSize',
    kind: 'string',
    description:
      'Max records returned per Purview audit query call. PAX defaults to 10000; PAX clamps the effective page size to 5000 and uses session pagination above that.',
    since: '1.0.0',
  },
  {
    name: 'PacingMs',
    kind: 'string',
    description: 'Sleep N milliseconds between Purview pages. PAX defaults to 0 (no pacing).',
    since: '1.0.0',
  },

  // ---- Operator knobs (parallelism) ----------------------------------------
  {
    name: 'EnableParallel',
    kind: 'flag',
    description:
      'Force parallel query execution (equivalent to -ParallelMode On). Requires PowerShell 7+.',
    since: '1.0.0',
  },
  {
    name: 'ParallelMode',
    kind: 'enum',
    enumValues: ['Auto', 'On', 'Off'],
    description:
      'Parallel execution mode. Auto = enable on PowerShell 7+ when there is enough work to parallelize; On = force; Off = serial. PAX defaults to Auto.',
    since: '1.0.0',
  },
  {
    name: 'MaxConcurrency',
    kind: 'string',
    description:
      'Max concurrent Purview query / partition workers. PAX defaults to 10 and clamps to 1–10.',
    since: '1.0.0',
  },
  {
    name: 'MaxParallelGroups',
    kind: 'string',
    description: 'Max concurrent activity groups in parallel mode. PAX defaults to 8.',
    since: '1.0.0',
  },

  // ---- Operator knobs (retry / circuit breaker) ----------------------------
  {
    name: 'CircuitBreakerThreshold',
    kind: 'string',
    description:
      'Consecutive block failures before the circuit breaker trips. PAX defaults to 5.',
    since: '1.0.0',
  },
  {
    name: 'CircuitBreakerCooldownSeconds',
    kind: 'string',
    description: 'Seconds the circuit breaker stays open after tripping. PAX defaults to 120.',
    since: '1.0.0',
  },
  {
    name: 'BackoffBaseSeconds',
    kind: 'string',
    description:
      'Base seconds for exponential backoff between block retries. PAX defaults to 1.0.',
    since: '1.0.0',
  },
  {
    name: 'BackoffMaxSeconds',
    kind: 'string',
    description: 'Cap on exponential backoff between block retries. PAX defaults to 45.',
    since: '1.0.0',
  },

  // ---- Operator knobs (diagnostics / output combination) -------------------
  {
    name: 'CombineOutput',
    kind: 'flag',
    description:
      'Combine multi-activity-type output into a single file (auto-enabled when -Rollup or -RollupPlusRaw is set).',
    since: '1.0.0',
  },
  {
    name: 'Force',
    kind: 'flag',
    description: 'Skip the interactive confirmation gate when overwriting outputs or breaking ties.',
    since: '1.0.0',
  },
  {
    name: 'SkipDiagnostics',
    kind: 'flag',
    description: 'Skip the runtime diagnostics preflight (Graph / Exchange / module checks).',
    since: '1.0.0',
  },
  {
    name: 'AutoCompleteness',
    kind: 'flag',
    description: 'Run PAX audit log completeness probes alongside the main query.',
    since: '1.0.0',
  },
  {
    name: 'IncludeTelemetry',
    kind: 'flag',
    description: 'Include PAX runtime telemetry in the standard output stream.',
    since: '1.0.0',
  },
  {
    name: 'EmitMetricsJson',
    kind: 'flag',
    description: 'Emit a metrics JSON alongside the main output (paired with -MetricsPath).',
    since: '1.0.0',
  },
  {
    name: 'MetricsPath',
    kind: 'path',
    description: 'Destination path for the metrics JSON when -EmitMetricsJson is set.',
    since: '1.0.0',
  },
] as const;

// -----------------------------------------------------------------------------
// Runtime-blocked switches
// -----------------------------------------------------------------------------

/**
 * Switches Mini-Kitchen must refuse to emit because PAX v1.11.3 immediately
 * exits when any of them are present on the command line. None of these are
 * emitted by the renderer, and if any appear in advanced args or an imported
 * recipe, the renderer blocks them with a warning.
 *
 * Two distinct runtime gates back this list:
 *
 *   1. **Deprecated switch gate** (PAX v1.11.3, lines 1620–1628).
 *      Triggers `exit 0` with a deprecation message when present.
 *      Members: -ExportWorkbook, -RAWInputCSV, -ExplodeArrays, -ExplodeDeep.
 *      These will be removed in a future release; presence is functionally
 *      a no-op today.
 *
 *   2. **Temporarily-disabled gate** (PAX v1.11.3, lines 1635–1647).
 *      Triggers `exit 0` with a "temporarily disabled" message when present.
 *      Members: -IncludeAgent365Info, -OnlyAgent365Info,
 *      -OutputPathAgent365Info, -AppendAgent365Info.
 *      These are expected to re-enable after further testing; until then,
 *      passing them prevents PAX from doing any work.
 *
 * Names below match the param() block exactly (case-sensitive). Earlier
 * Mini-Kitchen drafts used the wrong names (`Agent365Info`,
 * `Agent365InfoOnly`, `RawInputCSV`) — those have been corrected.
 */
export const REMOVED_OR_UNSUPPORTED_SWITCHES: readonly RemovedSwitch[] = [
  // ---- Deprecated (PAX exits gracefully when present) --------------------
  {
    name: 'ExportWorkbook',
    reason:
      'Deprecated in PAX and slated for removal — the runtime exits immediately if this switch is present. PAX no longer produces non-CSV output formats.',
  },
  {
    name: 'RAWInputCSV',
    reason:
      'Deprecated in PAX — the runtime exits immediately if this switch is present. The builder targets live audit / user-info queries; CSV replay is no longer a supported entry point.',
  },
  {
    name: 'ExplodeArrays',
    reason:
      'Deprecated in PAX — the runtime exits immediately if this switch is present. CSV output no longer exposes the per-row array expansion.',
  },
  {
    name: 'ExplodeDeep',
    reason:
      'Deprecated in PAX — the runtime exits immediately if this switch is present. CSV output no longer exposes the deep array expansion.',
  },

  // ---- Temporarily disabled (PAX exits gracefully when present) ----------
  // userFacingName replaces the literal switch name in warnings so the UI
  // never echoes identifiers that name the gated feature.
  {
    name: 'IncludeAgent365Info',
    userFacingName: 'an unsupported PAX switch',
    reason:
      'Temporarily disabled in PAX — the runtime exits immediately if this switch is present.',
  },
  {
    name: 'OnlyAgent365Info',
    userFacingName: 'an unsupported PAX switch',
    reason:
      'Temporarily disabled in PAX — the runtime exits immediately if this switch is present.',
  },
  {
    name: 'OutputPathAgent365Info',
    userFacingName: 'an unsupported PAX switch',
    reason:
      'Temporarily disabled in PAX — the runtime exits immediately if this switch is present.',
  },
  {
    name: 'AppendAgent365Info',
    userFacingName: 'an unsupported PAX switch',
    reason:
      'Temporarily disabled in PAX — the runtime exits immediately if this switch is present.',
  },
] as const;

// -----------------------------------------------------------------------------
// Convenience lookups
// -----------------------------------------------------------------------------

/** Lookup a switch definition by name. Returns `undefined` for unknown names. */
export function getSwitchDefinition(name: string): PaxSwitchDefinition | undefined {
  return PAX_SWITCH_CATALOG.find(s => s.name === name);
}

/** Returns `true` when the named switch is in the removed/unsupported list. */
export function isRemovedSwitch(name: string): boolean {
  return REMOVED_OR_UNSUPPORTED_SWITCHES.some(s => s.name === name);
}
