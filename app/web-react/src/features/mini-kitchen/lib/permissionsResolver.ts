/**
 * Permissions / prerequisites resolver.
 *
 * `resolvePermissions(state)` takes the in-memory recipe state, normalizes
 * it through `normalizeRecipe`, runs the advanced-args analyzer, and walks
 * `PERMISSION_RULES` (from `data/permission-rules.ts`). For each rule id the
 * resolver has a small predicate function over the normalized recipe + the
 * advanced-args analysis. Predicates return either:
 *
 *   - `null` — the rule does not fire for this recipe; or
 *   - `{ kind: 'required', triggeredBy }` — the rule is required; push a
 *     `PermissionEntry` into `report.required`.
 *
 * Entries are de-duplicated by `id`. When a rule fires from multiple triggers
 * the resolver merges the `triggeredBy` lists into one entry.
 *
 * Mini-Kitchen runs entirely in the browser (Decision D12 + Risk R15). The
 * resolver describes what the generated command will require at runtime and
 * why — it never claims Mini-Kitchen verified tenant consent, admin roles,
 * workspace access, SharePoint site access, certificate validity, managed
 * identity binding, or any local-machine state. Every output destination
 * adds an explicit `Mini-Kitchen cannot validate …` assumption.
 *
 * Pure function. No DOM access. No Node APIs. No new dependencies.
 */

import { PERMISSION_RULES } from '../data/permission-rules';
import { REMOVED_OR_UNSUPPORTED_SWITCHES } from '../data/pax-switch-catalog';
import type {
  MiniKitchenRecipeState,
  PermissionEntry,
  PermissionRule,
  PermissionsReport,
} from '../types';
import {
  analyzeAdvancedArgs,
  type AdvancedArgsAnalysis,
} from './advancedArgs';
import { normalizeRecipe } from './normalizeRecipe';

// -----------------------------------------------------------------------------
// Predicate model
// -----------------------------------------------------------------------------

type RulePredicateOutcome =
  | { kind: 'required'; triggeredBy: readonly string[]; reason?: string }
  | null;

interface ResolverContext {
  /** Recipe state after `normalizeRecipe`. */
  state: MiniKitchenRecipeState;
  /** Result of analyzing `state.advanced.extraArguments`. */
  advanced: AdvancedArgsAnalysis;
  /** True when fact OR user info destination is local. */
  hasLocalOutput: boolean;
  /** True when fact OR user info destination is SharePoint. */
  hasSharePointOutput: boolean;
  /** True when fact OR user info destination is Fabric. */
  hasFabricOutput: boolean;
  /** True when the recipe explicitly enables user info collection. */
  triggersUserInfo: boolean;
  /** True when -GroupNames is requested. */
  hasGroupNames: boolean;
  /** True when rollup is active (rollup or rollup-plus-raw). */
  hasRollup: boolean;
}

type RulePredicate = (ctx: ResolverContext) => RulePredicateOutcome;

// -----------------------------------------------------------------------------
// Public API
// -----------------------------------------------------------------------------

/**
 * Compute the permissions / prerequisites report for a recipe state.
 *
 * Calling code (Phase 5+ UI) is expected to call this on every state change.
 * The function is pure and cheap; it does not memoize internally.
 */
export function resolvePermissions(state: MiniKitchenRecipeState): PermissionsReport {
  const normalized = normalizeRecipe(state);
  const advanced = analyzeAdvancedArgs(normalized.advanced.extraArguments ?? '');

  const factTier = normalized.destinations.fact.tier;
  const userInfoTier = normalized.destinations.userInfo.path
    ? guessUserInfoTier(normalized.destinations.userInfo.path)
    : undefined;

  const hasLocalOutput = factTier === 'local' || userInfoTier === 'local';
  const hasSharePointOutput = factTier === 'sharepoint' || userInfoTier === 'sharepoint';
  const hasFabricOutput = factTier === 'fabric' || userInfoTier === 'fabric';

  const triggersUserInfo =
    normalized.query.includeUserInfo === true ||
    normalized.query.onlyUserInfo === true ||
    normalized.query.mode === 'user-info-only';

  const hasGroupNames =
    Array.isArray(normalized.processing.groupNames) &&
    normalized.processing.groupNames.length > 0;

  const hasRollup =
    normalized.processing.rollup === 'rollup' ||
    normalized.processing.rollup === 'rollup-plus-raw';

  const ctx: ResolverContext = {
    state: normalized,
    advanced,
    hasLocalOutput,
    hasSharePointOutput,
    hasFabricOutput,
    triggersUserInfo,
    hasGroupNames,
    hasRollup,
  };

  const requiredById = new Map<string, PermissionEntry>();

  for (const rule of PERMISSION_RULES) {
    const predicate = RULE_PREDICATES[rule.id];
    if (!predicate) {
      // No predicate registered for this rule id — skip silently. Rules
      // without predicates are catalog-only and never fire.
      continue;
    }
    const outcome = predicate(ctx);
    if (outcome === null) {
      continue;
    }
    const entry = buildEntry(rule, outcome);
    const existing = requiredById.get(entry.id);
    if (existing) {
      requiredById.set(entry.id, mergeEntries(existing, entry));
    } else {
      requiredById.set(entry.id, entry);
    }
  }

  const warnings = collectWarnings(ctx);
  const assumptions = collectAssumptions(ctx, requiredById);

  return {
    required: Array.from(requiredById.values()),
    warnings,
    assumptions,
  };
}

// -----------------------------------------------------------------------------
// Predicates
// -----------------------------------------------------------------------------

const RULE_PREDICATES: Record<string, RulePredicate> = {
  // Audit query
  auditLogsQueryReadAll: (ctx) => {
    if (ctx.state.query.mode !== 'audit-query') {
      return null;
    }
    return { kind: 'required', triggeredBy: ['query.mode'] };
  },
  unifiedAuditLogging: (ctx) => {
    if (ctx.state.query.mode !== 'audit-query') {
      return null;
    }
    return { kind: 'required', triggeredBy: ['query.mode'] };
  },

  // M365 usage bundle branches
  exchangeAuditBranch: (ctx) => evaluateM365Branch(ctx),
  oneDriveAuditBranch: (ctx) => evaluateM365Branch(ctx),
  sharePointAuditBranch: (ctx) => evaluateM365Branch(ctx),

  // User info
  userReadAll: (ctx) => {
    const triggers: string[] = [];
    if (ctx.state.query.includeUserInfo === true) {
      triggers.push('query.includeUserInfo');
    }
    if (ctx.state.query.onlyUserInfo === true || ctx.state.query.mode === 'user-info-only') {
      triggers.push('query.onlyUserInfo');
    }
    if (ctx.hasRollup) {
      triggers.push('processing.rollup');
    }
    if (ctx.hasGroupNames) {
      triggers.push('processing.groupNames');
    }
    if (triggers.length === 0) {
      return null;
    }
    return { kind: 'required', triggeredBy: triggers };
  },
  organizationReadAll: (ctx) => {
    const triggers: string[] = [];
    if (ctx.state.query.includeUserInfo === true) {
      triggers.push('query.includeUserInfo');
    }
    if (ctx.state.query.onlyUserInfo === true || ctx.state.query.mode === 'user-info-only') {
      triggers.push('query.onlyUserInfo');
    }
    if (ctx.hasRollup) {
      triggers.push('processing.rollup');
    }
    if (ctx.hasGroupNames) {
      triggers.push('processing.groupNames');
    }
    if (triggers.length === 0) {
      return null;
    }
    return { kind: 'required', triggeredBy: triggers };
  },

  // Group expansion
  groupMemberReadAll: (ctx) => {
    if (!ctx.hasGroupNames) {
      return null;
    }
    return { kind: 'required', triggeredBy: ['processing.groupNames'] };
  },

  // Runtime / environment
  pythonRuntimeRollup: (ctx) => {
    if (!ctx.hasRollup) {
      return null;
    }
    return { kind: 'required', triggeredBy: ['processing.rollup'] };
  },
  powerShell7: () => ({ kind: 'required', triggeredBy: ['always'] }),
  graphM365EndpointAccess: () => ({ kind: 'required', triggeredBy: ['always'] }),

  // SharePoint output
  sitesReadWriteAll: (ctx) => {
    if (!ctx.hasSharePointOutput) {
      return null;
    }
    return {
      kind: 'required',
      triggeredBy: sharePointTriggers(ctx),
    };
  },
  filesReadWriteAll: (ctx) => {
    if (!ctx.hasSharePointOutput) {
      return null;
    }
    return {
      kind: 'required',
      triggeredBy: sharePointTriggers(ctx),
    };
  },
  destinationSiteLibraryAccess: (ctx) => {
    if (!ctx.hasSharePointOutput) {
      return null;
    }
    return {
      kind: 'required',
      triggeredBy: sharePointTriggers(ctx),
    };
  },

  // Fabric output
  fabricWorkspaceContributor: (ctx) => {
    if (!ctx.hasFabricOutput) {
      return null;
    }
    return {
      kind: 'required',
      triggeredBy: fabricTriggers(ctx),
    };
  },
  fabricServicePrincipalsTenantSetting: (ctx) => {
    if (!ctx.hasFabricOutput) {
      return null;
    }
    // Only fires when auth is an app-registration service-principal path.
    if (
      ctx.state.auth.mode !== 'AppRegistrationSecret' &&
      ctx.state.auth.mode !== 'AppRegistrationCertificate'
    ) {
      return null;
    }
    return {
      kind: 'required',
      triggeredBy: [...fabricTriggers(ctx), 'auth.mode'],
    };
  },
  oneLakeUrlContract: (ctx) => {
    if (!ctx.hasFabricOutput) {
      return null;
    }
    return {
      kind: 'required',
      triggeredBy: ['destinations.fact.path', 'destinations.userInfo.path'],
    };
  },

  // Local / UNC output
  outputWriteAccess: (ctx) => {
    if (!ctx.hasLocalOutput) {
      return null;
    }
    return {
      kind: 'required',
      triggeredBy: localTriggers(ctx),
    };
  },

  // Append callouts (split: fact vs user info)
  appendFactCallout: (ctx) => {
    if (ctx.state.destinations.fact.mode !== 'append') {
      return null;
    }
    return {
      kind: 'required',
      triggeredBy: ['destinations.fact.mode'],
    };
  },
  appendUserInfoCallout: (ctx) => {
    if (ctx.state.destinations.userInfo.mode !== 'append') {
      return null;
    }
    return {
      kind: 'required',
      triggeredBy: ['destinations.userInfo.mode'],
    };
  },

  // CopilotInteraction excluded
  excludeCopilotInteractionInfo: (ctx) => {
    if (ctx.state.query.excludeCopilotInteraction !== true) {
      return null;
    }
    return {
      kind: 'required',
      triggeredBy: ['query.excludeCopilotInteraction'],
    };
  },

  // Auth callouts
  webLoginDefaultInfo: (ctx) => {
    if (ctx.state.auth.mode !== 'WebLogin') {
      return null;
    }
    return { kind: 'required', triggeredBy: ['auth.mode'] };
  },
  deviceCodeRuntimeInfo: (ctx) => {
    if (ctx.state.auth.mode !== 'DeviceCode') {
      return null;
    }
    return { kind: 'required', triggeredBy: ['auth.mode'] };
  },
  appRegistrationSecretCallout: (ctx) => {
    if (ctx.state.auth.mode !== 'AppRegistrationSecret') {
      return null;
    }
    return {
      kind: 'required',
      triggeredBy: ['auth.mode', 'auth.tenantId', 'auth.clientId'],
    };
  },
  appRegistrationCertificateCallout: (ctx) => {
    if (ctx.state.auth.mode !== 'AppRegistrationCertificate') {
      return null;
    }
    return {
      kind: 'required',
      triggeredBy: [
        'auth.mode',
        'auth.tenantId',
        'auth.clientId',
        'auth.certificateThumbprint',
      ],
    };
  },
  managedIdentityCallout: (ctx) => {
    if (ctx.state.auth.mode !== 'ManagedIdentity') {
      return null;
    }
    return { kind: 'required', triggeredBy: ['auth.mode'] };
  },
};

/**
 * Evaluate whether the three M365 audit branches (Exchange / OneDrive /
 * SharePoint) are required.
 *
 *   - If `includeM365Usage` is true → required (PAX pulls the branch).
 *   - Else → does not fire.
 */
function evaluateM365Branch(ctx: ResolverContext): RulePredicateOutcome {
  if (ctx.state.query.includeM365Usage === true) {
    return { kind: 'required', triggeredBy: ['query.includeM365Usage'] };
  }
  return null;
}

function sharePointTriggers(ctx: ResolverContext): readonly string[] {
  const triggers: string[] = [];
  if (ctx.state.destinations.fact.tier === 'sharepoint') {
    triggers.push('destinations.fact.tier', 'destinations.fact.path');
  }
  if (guessUserInfoTier(ctx.state.destinations.userInfo.path) === 'sharepoint') {
    triggers.push('destinations.userInfo.path');
  }
  return triggers;
}

function fabricTriggers(ctx: ResolverContext): readonly string[] {
  const triggers: string[] = [];
  if (ctx.state.destinations.fact.tier === 'fabric') {
    triggers.push('destinations.fact.tier', 'destinations.fact.path');
  }
  if (guessUserInfoTier(ctx.state.destinations.userInfo.path) === 'fabric') {
    triggers.push('destinations.userInfo.path');
  }
  return triggers;
}

function localTriggers(ctx: ResolverContext): readonly string[] {
  const triggers: string[] = [];
  if (ctx.state.destinations.fact.tier === 'local') {
    triggers.push('destinations.fact.tier');
    if (ctx.state.destinations.fact.path) {
      triggers.push('destinations.fact.path');
    }
  }
  if (guessUserInfoTier(ctx.state.destinations.userInfo.path) === 'local') {
    triggers.push('destinations.userInfo.path');
  }
  return triggers;
}

// -----------------------------------------------------------------------------
// User info tier inference
// -----------------------------------------------------------------------------

/**
 * `MiniKitchenRecipeState` does not carry an explicit storage tier for the
 * user info destination — PAX classifies the URL shape at runtime via
 * `Get-PathTier`. The resolver uses the same URL-shape heuristic as
 * `lib/pathWarnings.ts` so its predicate outcomes line up with the renderer.
 *
 * Returns `undefined` for empty / whitespace input.
 */
function guessUserInfoTier(
  path: string | undefined,
): 'local' | 'sharepoint' | 'fabric' | undefined {
  const trimmed = (path ?? '').trim();
  if (!trimmed) {
    return undefined;
  }
  if (/^https:\/\/(?:[a-z0-9-]+-)?onelake\.dfs\.fabric\.microsoft\.com\//i.test(trimmed)) {
    return 'fabric';
  }
  if (/^https:\/\/[^/]+\.sharepoint\.com\//i.test(trimmed)) {
    return 'sharepoint';
  }
  return 'local';
}

const TIER_LABEL: Record<'local' | 'sharepoint' | 'fabric', string> = {
  local: 'a local or UNC path',
  sharepoint: 'SharePoint',
  fabric: 'Fabric / OneLake',
};

// -----------------------------------------------------------------------------
// Entry construction / merging
// -----------------------------------------------------------------------------

function buildEntry(rule: PermissionRule, outcome: RulePredicateOutcome): PermissionEntry {
  if (outcome === null) {
    // Defensive: not reachable because the caller checks outcome above.
    throw new Error('buildEntry called with null outcome');
  }
  const entry: PermissionEntry = {
    id: rule.id,
    name: rule.name,
    group: rule.group,
    appliesTo: rule.appliesTo,
    requiredBecause: outcome.reason ?? rule.requiredBecause,
    triggeredBy: dedupeTriggers(outcome.triggeredBy),
    appliesToLabel: rule.appliesToLabel,
  };
  if (rule.severity !== undefined) {
    entry.severity = rule.severity;
  }
  if (rule.notes !== undefined) {
    entry.notes = rule.notes;
  }
  return entry;
}

function mergeEntries(a: PermissionEntry, b: PermissionEntry): PermissionEntry {
  const merged: PermissionEntry = {
    ...a,
    triggeredBy: dedupeTriggers([...a.triggeredBy, ...b.triggeredBy]),
  };
  return merged;
}

function dedupeTriggers(input: readonly string[]): readonly string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const t of input) {
    if (!seen.has(t)) {
      seen.add(t);
      out.push(t);
    }
  }
  return out;
}

// -----------------------------------------------------------------------------
// Warnings + assumptions
// -----------------------------------------------------------------------------

function collectWarnings(ctx: ResolverContext): readonly string[] {
  const warnings: string[] = [];

  if (ctx.state.advanced.extraArguments && ctx.state.advanced.extraArguments.trim() !== '') {
    warnings.push(
      'Advanced switches are present and may affect permissions or prerequisites beyond the builder’s structured resolver.',
    );
  }
  for (const removed of ctx.advanced.removedSwitches) {
    const def = REMOVED_OR_UNSUPPORTED_SWITCHES.find(s => s.name === removed);
    const label = def?.userFacingName ?? `-${removed}`;
    warnings.push(
      `Removed or temporarily-disabled switch ${label} was found in advanced switches and will not be emitted by the renderer; remove it from your recipe.`,
    );
  }
  for (const unknown of ctx.advanced.unknownSwitches) {
    warnings.push(
      `Unknown advanced switch -${unknown} may require additional permissions or prerequisites not listed here.`,
    );
  }
  if (ctx.advanced.secretWarnings.length > 0) {
    warnings.push(
      'Possible secret value detected in advanced switches — review and remove before running. The builder does not collect or store secrets.',
    );
  }
  if (ctx.advanced.duplicates.length > 0) {
    for (const dup of ctx.advanced.duplicates) {
      warnings.push(
        `Advanced switch -${dup} appears more than once; the renderer keeps only the first occurrence.`,
      );
    }
  }

  const factTier = ctx.state.destinations.fact.tier;
  const userInfoTier = guessUserInfoTier(ctx.state.destinations.userInfo.path);
  if (factTier && userInfoTier && factTier !== userInfoTier) {
    warnings.push(
      `Audit output is going to ${TIER_LABEL[factTier]} but user info is going to ${TIER_LABEL[userInfoTier]}. PAX expects both outputs on the same storage tier — fix one of them.`,
    );
  }

  return warnings;
}

function collectAssumptions(
  ctx: ResolverContext,
  required: ReadonlyMap<string, PermissionEntry>,
): readonly string[] {
  const assumptions: string[] = [];

  assumptions.push(
    'The builder never reads, fetches, validates, or runs the PAX script. It only renders text. PAX supports four storage tiers — local, UNC, SharePoint, and Fabric/OneLake — and only one tier can be active per command.',
  );

  if (
    required.has('auditLogsQueryReadAll') ||
    required.has('userReadAll') ||
    required.has('organizationReadAll') ||
    required.has('groupMemberReadAll') ||
    required.has('sitesReadWriteAll') ||
    required.has('filesReadWriteAll')
  ) {
    assumptions.push(
      'The builder cannot validate Entra tenant or admin-consent state. The listed Graph permissions must already be granted to whatever identity runs PAX — either held by the signed-in user account (web login / interactive delegated auth) or consented on the app registration / managed identity the runtime uses (client-secret, certificate, or managed-identity auth).',
    );
  }

  if (ctx.hasLocalOutput) {
    assumptions.push(
      'The builder cannot validate local paths. Verify the runtime account can write to the configured directory.',
    );
  }
  if (ctx.hasSharePointOutput) {
    assumptions.push(
      'The builder cannot verify SharePoint URLs, site access, or document library permissions.',
    );
  }
  if (ctx.hasFabricOutput) {
    assumptions.push(
      'The builder cannot verify the Fabric workspace, lakehouse, schema, or table.',
    );
    assumptions.push(
      'OneLake URLs must follow PAX\u2019s expected URL contract (the item can be addressed by name <item>.Lakehouse or by GUID <itemId>): https://[<region>-]onelake.dfs.fabric.microsoft.com/<workspace>/<item>.Lakehouse[/Tables[/<schema>][/<table>]|/Files[/<sub>]]',
    );
  }
  if (ctx.hasRollup) {
    assumptions.push(
      'Rollup pipelines run through PAX’s Python helper. PAX owns the bootstrap — it auto-installs Python 3.10+ and the optional orjson package on first use — so the builder does not verify the runtime has Python pre-installed.',
    );
  }
  if (ctx.state.query.mode === 'user-info-only' || ctx.state.query.onlyUserInfo === true) {
    assumptions.push(
      'User-info-only mode suppresses the audit query and therefore the audit-side permissions (AuditLogsQuery.Read.All, unified audit logging) are not listed for this recipe.',
    );
  }
  if (ctx.state.auth.mode === 'AppRegistrationSecret') {
    assumptions.push(
      'The builder does not collect or store client secrets. The rendered command contains a <CLIENT_SECRET> placeholder — replace it in your terminal before running.',
    );
  }
  if (ctx.state.auth.mode === 'ManagedIdentity') {
    assumptions.push(
      'Managed identity auth requires the runtime to be a managed-identity-enabled Azure resource with the listed Graph permissions consented. The builder cannot verify the hosted environment.',
    );
  }

  return assumptions;
}
