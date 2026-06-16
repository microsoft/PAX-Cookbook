/**
 * Lite (.paxlite) recipe -> full Windows Cookbook recipe translation (UX4).
 *
 * Pure, non-executing contract layer. Maps the flat `MiniKitchenRecipeState`
 * shape that the Mini-Kitchen builder edits into a candidate for the full
 * Cookbook `recipe.schema.json` shape that the C# `PaxAdapter` projects.
 *
 * This module is the translation half of the UX4 parity contract. Its output
 * is a *candidate* — a structural projection that still needs a prep pass in
 * the full app (auth-profile binding, tenant id, path validation) before it
 * can be validated by `RecipeValidationModel` or run by the broker. It does
 * not, and must never:
 *   - run PAX or any process
 *   - call the broker, fetch, localStorage, Credential Manager, or filesystem
 *   - mint or persist secrets
 *   - decide what executes (the C# adapter remains the only execution oracle)
 *
 * The translation deliberately mirrors the decisions baked into
 * `commandRenderer.ts` so that, after projecting the candidate through the
 * C# adapter, the resulting argv is semantically equivalent to the
 * Mini-Kitchen preview (see the UX4 golden argv parity harness). Where the two
 * shapes cannot express the same thing, the difference is surfaced through
 * `warnings`, `unsupportedOrLossyFields`, or `needsPrep` rather than silently
 * dropped.
 */

import { REMOVED_OR_UNSUPPORTED_SWITCHES } from '../data/pax-switch-catalog';
import type {
  AuthMode,
  DateRangeMode,
  ExecutionMode,
  MiniKitchenRecipeState,
  PromptFilter,
  StorageTier,
} from '../types';
import { analyzeAdvancedArgs } from './advancedArgs';
import { normalizeRecipe } from './normalizeRecipe';
import { containsLikelySecret } from './secretScanner';

// -----------------------------------------------------------------------------
// Public output shape
// -----------------------------------------------------------------------------

/** Full Cookbook query mode (renamed from the lite `'audit-query' | 'user-info-only'`). */
export type FullQueryMode = 'audit' | 'userInfoOnly';

/** Full Cookbook agent-filter mode (renamed from the lite hyphenated form). */
export type FullAgentFilterMode = 'agentIds' | 'agentsOnly' | 'excludeAgents';

/** Full Cookbook rollup token (renamed from the lite lower-case form; absent = no rollup). */
export type FullRollupMode = 'Rollup' | 'RollupPlusRaw';

/** Full Cookbook destination mode (renamed from the lite `'write-new' | 'append'`). */
export type FullDestinationMode = 'outputPath' | 'append';

export interface FullCookbookIngredients {
  m365Usage: {
    includeM365Usage: boolean;
    includeCopilotInteraction?: boolean;
  };
  entraUserData: {
    includeUserInfo: boolean;
  };
}

export interface FullCookbookQuery {
  mode: FullQueryMode;
  /**
   * Audit date-range mode. `'previous-day'` deliberately omits startDate/endDate
   * so PAX queries the previous full UTC day; the broker recognizes this shape
   * and skips the date-required gate. `undefined`/`'custom'` use startDate/endDate.
   */
  dateMode?: DateRangeMode;
  startDate?: string;
  endDate?: string;
  activityTypes?: string[];
  userIds?: string[];
  groupNames?: string[];
  agentFilter?: {
    mode: FullAgentFilterMode;
    agentIds?: string[];
  };
  promptFilter?: PromptFilter;
}

export interface FullCookbookDestination {
  mode: FullDestinationMode;
  path?: string;
  appendFile?: string;
}

export interface FullCookbookAuth {
  mode: AuthMode;
  tenantId?: string;
  chefKeyId?: string;
}

/**
 * Informational bag carried alongside the candidate. Mirrors the optional
 * `importMetadata` block in `recipe.schema.json` (`source:"mini-kitchen-lite"`)
 * plus the lite-only identity fields the full schema rejects. Nothing in here
 * affects projection — it exists so the full app can preserve provenance and
 * re-surface description / tags / notes in its own UI.
 */
export interface FullCookbookImportMetadata {
  source: 'mini-kitchen-lite';
  originalIdentity: {
    description?: string;
    tags?: string[];
    notes?: string;
  };
  factTier?: StorageTier;
  scriptPathPreview?: string;
  preset?: string;
}

export interface FullCookbookRecipeCandidate {
  recipeId: string;
  recipeSchemaVersion: 1;
  paxAdapterVersion: string;
  createdAt: string;
  updatedAt: string;
  createdBy: string;
  executionMode: ExecutionMode;
  identity: { name: string };
  ingredients: FullCookbookIngredients;
  query: FullCookbookQuery;
  processing: { rollup?: FullRollupMode };
  destinations: {
    fact?: FullCookbookDestination;
    userInfo?: FullCookbookDestination;
  };
  auth: FullCookbookAuth;
  advanced?: { extraArguments: string };
  importMetadata: FullCookbookImportMetadata;
}

export type TranslationSeverity = 'info' | 'warning';

export interface TranslationNote {
  id: string;
  severity: TranslationSeverity;
  message: string;
  field?: string;
}

export interface UnsupportedOrLossyField {
  field: string;
  reason: string;
}

export interface TranslateResult {
  /**
   * `true` when a structurally complete candidate was produced. The candidate
   * may still require prep (see `needsPrep`) before it validates or runs. Only
   * `false` for an unrecognized critical enum that cannot be mapped at all.
   */
  ok: boolean;
  fullRecipeCandidate: FullCookbookRecipeCandidate | null;
  metadata: {
    recipeId: string;
    paxAdapterVersion: string;
    sourcePreset?: string;
  };
  warnings: TranslationNote[];
  needsPrep: boolean;
  needsPrepReasons: string[];
  unsupportedOrLossyFields: UnsupportedOrLossyField[];
  notesForUi: string[];
}

// -----------------------------------------------------------------------------
// Options (deterministic; pure)
// -----------------------------------------------------------------------------

export interface TranslateOptions {
  /** Server-managed ULID. The full app mints this; tests inject a fixed value. */
  recipeId?: string;
  /** PAX adapter version stamp. */
  paxAdapterVersion?: string;
  /** Creation timestamp stamp. */
  createdAt?: string;
  /** Last-updated timestamp stamp. */
  updatedAt?: string;
  /** Identity recorded for `createdBy`. */
  createdBy?: string;
}

/**
 * Deterministic defaults. A real run replaces these via {@link TranslateOptions};
 * the defaults exist only so the module stays pure (no `Date.now`, no RNG) and
 * the parity harness produces byte-stable artifacts.
 */
const DEFAULT_RECIPE_ID = '0123456789ABCDEFGHJKMNPQRS';
// Pre-save / parity-harness default only. On a real save the broker
// server-stamps `paxAdapterVersion` from the managed engine's VERSION.json
// (RecipeMutationService), so this value never reaches persisted identity; it
// only keeps the local pre-save candidate aligned with the bundled engine.
const DEFAULT_PAX_ADAPTER_VERSION = '1.11.6';
const DEFAULT_TIMESTAMP = '2026-01-01T00:00:00.000Z';
const DEFAULT_CREATED_BY = 'mini-kitchen-lite';

const RECIPE_ID_PATTERN = /^[0-9A-HJKMNP-TV-Z]{26}$/;
const GUID_PATTERN = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

// -----------------------------------------------------------------------------
// Translation
// -----------------------------------------------------------------------------

/**
 * Translate a Mini-Kitchen recipe state into a full Cookbook recipe candidate.
 *
 * Pure: same input + options always yields the same output. Runs the recipe
 * through {@link normalizeRecipe} first so the lite mutex rules (user-info-only
 * suppression, forced flags) are applied before the field-shape mapping.
 */
export function translateLiteRecipeToFullRecipe(
  recipe: MiniKitchenRecipeState,
  options: TranslateOptions = {},
): TranslateResult {
  const state = normalizeRecipe(recipe);

  const warnings: TranslationNote[] = [];
  const needsPrepReasons: string[] = [];
  const unsupportedOrLossyFields: UnsupportedOrLossyField[] = [];
  const notesForUi: string[] = [];

  const recipeId = options.recipeId ?? DEFAULT_RECIPE_ID;
  const paxAdapterVersion = options.paxAdapterVersion ?? DEFAULT_PAX_ADAPTER_VERSION;
  const createdAt = options.createdAt ?? DEFAULT_TIMESTAMP;
  const updatedAt = options.updatedAt ?? DEFAULT_TIMESTAMP;
  const createdBy = options.createdBy ?? DEFAULT_CREATED_BY;

  if (!RECIPE_ID_PATTERN.test(recipeId)) {
    warnings.push({
      id: 'recipe-id-shape',
      severity: 'warning',
      message:
        'The supplied recipeId is not a valid Crockford base32 ULID. PAX Cookbook must mint a conforming id before import.',
      field: 'recipeId',
    });
    needsPrepReasons.push('A valid ULID recipeId must be minted by PAX Cookbook.');
  }

  const isUserInfoOnly = state.query.mode === 'user-info-only';
  const fullQueryMode: FullQueryMode = isUserInfoOnly ? 'userInfoOnly' : 'audit';

  // ---- Identity ----
  const name = (state.identity.name ?? '').trim();
  if (!name) {
    warnings.push({
      id: 'identity-name-blank',
      severity: 'warning',
      message: 'Recipe name is blank. The full schema requires a non-empty identity.name.',
      field: 'identity.name',
    });
    needsPrepReasons.push('Provide a recipe name before import.');
  }

  const originalIdentity: FullCookbookImportMetadata['originalIdentity'] = {};
  const description = (state.identity.description ?? '').trim();
  const tags = (state.identity.tags ?? []).map(t => t.trim()).filter(t => t.length > 0);
  const notes = (state.identity.notes ?? '').trim();
  if (description) {
    originalIdentity.description = description;
  }
  if (tags.length > 0) {
    originalIdentity.tags = tags;
  }
  if (notes) {
    originalIdentity.notes = notes;
  }
  if (description || tags.length > 0 || notes) {
    notesForUi.push(
      'Description, tags, and notes are preserved in importMetadata.originalIdentity; the full recipe schema accepts only identity.name.',
    );
  }

  // ---- Ingredients ----
  const includeM365Usage = !isUserInfoOnly && state.query.includeM365Usage === true;
  const includeUserInfo = isUserInfoOnly || state.query.includeUserInfo === true;

  const m365Usage: FullCookbookIngredients['m365Usage'] = { includeM365Usage };
  const excludeCopilot = state.query.excludeCopilotInteraction === true;
  if (excludeCopilot) {
    if (includeM365Usage) {
      // -ExcludeCopilotInteraction is expressible as includeCopilotInteraction=false
      // only when M365 usage is on (full schema gate).
      m365Usage.includeCopilotInteraction = false;
    } else {
      // The lite renderer emits -ExcludeCopilotInteraction standalone, but the
      // full schema cannot express "exclude Copilot" without M365 usage on.
      unsupportedOrLossyFields.push({
        field: 'query.excludeCopilotInteraction',
        reason:
          'Excluding Copilot interaction without including M365 usage has no representation in the full recipe schema (includeCopilotInteraction=false requires includeM365Usage=true). The lite preview emits -ExcludeCopilotInteraction; the projected runtime command will not.',
      });
      needsPrepReasons.push(
        'Resolve exclude-Copilot-interaction: it requires M365 usage on in the full recipe.',
      );
    }
  }

  const ingredients: FullCookbookIngredients = {
    m365Usage,
    entraUserData: { includeUserInfo },
  };

  // ---- Query ----
  const query: FullCookbookQuery = { mode: fullQueryMode };
  if (!isUserInfoOnly) {
    if (state.query.dateMode === 'previous-day') {
      // Previous-day mode deliberately omits both -StartDate and -EndDate so PAX
      // queries the previous full UTC day. Carry dateMode through so the broker
      // recognizes the deliberately-dateless shape, inject NO dates, and raise no
      // "date required" prep item or warning — absence is the signal, and a
      // dateless previous-day audit recipe is complete and ready.
      query.dateMode = 'previous-day';
    } else {
      const startDate = (state.query.startDate ?? '').trim();
      const endDate = (state.query.endDate ?? '').trim();
      // Carry through whichever side is present; the both-or-neither shape below
      // governs what is flagged when a side is blank.
      if (startDate) {
        query.startDate = startDate;
      }
      if (endDate) {
        query.endDate = endDate;
      }
      if (!startDate && !endDate) {
        // Custom range with BOTH sides blank: a single combined date-range gap
        // (not two separate start/end items), steering the operator to fill both
        // dates or switch to Previous day. Mirrors the lite renderer's single
        // 'date-range-missing' blocker.
        needsPrepReasons.push(
          'Audit recipes need a date range. Set both dates for a custom range, or switch to Previous day to use the last full UTC day.',
        );
        warnings.push({
          id: 'audit-date-range-missing',
          severity: 'warning',
          message:
            'Audit shape requires a date range (query.startDate and query.endDate). Neither supplied — set both dates, or switch the date range to Previous day.',
          field: 'query.startDate',
        });
      } else if (!startDate) {
        // Half-filled custom range (end set, start blank): both-or-neither — flag
        // the missing start so PAX never silently unbounds it.
        needsPrepReasons.push(
          'Audit recipes require a start date — set one to complete the range, or switch the date range to Previous day for an automatic one-day lookback.',
        );
        warnings.push({
          id: 'audit-start-date-missing',
          severity: 'warning',
          message:
            'Audit shape requires query.startDate. None supplied — set a start date, or switch the date range to Previous day.',
          field: 'query.startDate',
        });
      } else if (!endDate) {
        // Half-filled custom range (start set, end blank): flag the missing end.
        needsPrepReasons.push(
          'Audit recipes require an end date — set one to complete the range, or switch the date range to Previous day for an automatic one-day lookback.',
        );
        warnings.push({
          id: 'audit-end-date-missing',
          severity: 'warning',
          message:
            'Audit shape requires query.endDate. None supplied — set an end date, or switch the date range to Previous day.',
          field: 'query.endDate',
        });
      }
    }

    const activityTypes = (state.processing.activityTypes ?? []).filter(v => v.trim().length > 0);
    if (activityTypes.length > 0) {
      query.activityTypes = [...activityTypes];
    }
    const userIds = (state.processing.userIds ?? []).filter(v => v.trim().length > 0);
    if (userIds.length > 0) {
      query.userIds = [...userIds];
    }
    const groupNames = (state.processing.groupNames ?? []).filter(v => v.trim().length > 0);
    if (groupNames.length > 0) {
      query.groupNames = [...groupNames];
    }

    const agentFilter = state.processing.agentFilter;
    if (agentFilter && agentFilter.mode !== 'none') {
      if (agentFilter.mode === 'ids-only') {
        const ids = (agentFilter.ids ?? []).filter(v => v.trim().length > 0);
        if (ids.length > 0) {
          query.agentFilter = { mode: 'agentIds', agentIds: [...ids] };
        } else {
          needsPrepReasons.push('Agent ID filter selected but no agent IDs supplied.');
          warnings.push({
            id: 'agent-id-filter-empty',
            severity: 'warning',
            message: 'Agent filter mode is ids-only but no agent IDs are present; the filter was dropped.',
            field: 'query.agentFilter.agentIds',
          });
        }
      } else if (agentFilter.mode === 'agents-only') {
        query.agentFilter = { mode: 'agentsOnly' };
      } else if (agentFilter.mode === 'exclude-agents') {
        query.agentFilter = { mode: 'excludeAgents' };
      }
    }

    const promptFilter: PromptFilter | undefined = state.processing.promptFilter;
    // The lite renderer omits -PromptFilter when it equals the PAX default
    // 'Null'; mirror that so the projected argv matches the preview.
    if (promptFilter !== undefined && promptFilter !== 'Null') {
      query.promptFilter = promptFilter;
    }
  } else {
    // user-info-only: audit-only fields are not carried.
    if (
      (state.processing.activityTypes ?? []).length > 0 ||
      (state.processing.userIds ?? []).length > 0 ||
      (state.processing.groupNames ?? []).length > 0 ||
      state.processing.agentFilter ||
      state.processing.promptFilter
    ) {
      notesForUi.push('User-info-only mode suppresses audit filters; they were not carried into the candidate.');
    }
  }

  // ---- Processing (rollup) ----
  const processing: { rollup?: FullRollupMode } = {};
  if (!isUserInfoOnly) {
    const rollup = state.processing.rollup;
    if (rollup === 'rollup') {
      processing.rollup = 'Rollup';
    } else if (rollup === 'rollup-plus-raw') {
      processing.rollup = 'RollupPlusRaw';
    } else {
      // Rollup is optional for audit-shape recipes. An operator who only wants
      // raw Purview audit data may legitimately omit it; the full schema and
      // both broker validators now treat processing.rollup as optional under
      // audit. We carry no rollup key (PAX simply omits -Rollup at runtime).
      // This is not a prep blocker and not a lossy projection, so we only emit
      // an informational note for the UI.
      notesForUi.push(
        'No rollup mode selected. The audit recipe will pull raw audit data without -Rollup / -RollupPlusRaw; rollup is optional.',
      );
    }

    // The lite outputCombineMode has no field in the full schema. PAX
    // auto-enables -CombineOutput under -IncludeM365Usage and under rollup, so
    // for those configurations the loss is behaviourally inert; otherwise the
    // projected runtime command will not carry -CombineOutput.
    const combineMode = state.processing.outputCombineMode ?? 'combined';
    const multiActivity = (state.processing.activityTypes ?? []).length > 1;
    const wantsCombine = combineMode === 'combined' && !includeM365Usage && multiActivity;
    if (wantsCombine) {
      const autoEnabled = processing.rollup !== undefined; // rollup auto-enables -CombineOutput
      const reason = autoEnabled
        ? 'outputCombineMode=combined has no full-schema field, but PAX auto-enables -CombineOutput under rollup, so runtime behaviour is unchanged.'
        : 'outputCombineMode=combined has no full-schema field. The lite preview emits -CombineOutput; the projected runtime command will not, and no rollup/M365 auto-enable covers this configuration.';
      unsupportedOrLossyFields.push({ field: 'processing.outputCombineMode', reason });
      if (!autoEnabled) {
        needsPrepReasons.push('Confirm combined output: -CombineOutput is not represented in the full recipe.');
      } else {
        notesForUi.push(reason);
      }
    }
  }

  // ---- Destinations ----
  const destinations: { fact?: FullCookbookDestination; userInfo?: FullCookbookDestination } = {};

  if (!isUserInfoOnly) {
    const fact = state.destinations.fact;
    const factPath = (fact.path ?? '').trim();
    if (fact.tier === 'sharepoint' || fact.tier === 'fabric') {
      notesForUi.push(
        `Fact destination tier "${fact.tier}" is preserved in importMetadata; the full recipe carries no tier field — PAX resolves the destination tier from the path shape.`,
      );
    }
    if (fact.mode === 'write-new') {
      if (factPath) {
        destinations.fact = { mode: 'outputPath', path: factPath };
      } else {
        needsPrepReasons.push('Fact write-new mode requires an output path.');
      }
    } else if (fact.mode === 'append') {
      if (factPath) {
        destinations.fact = { mode: 'append', appendFile: factPath };
      } else {
        needsPrepReasons.push('Fact append mode requires an append-file path.');
      }
    }
  }

  const userInfoDest = translateUserInfoDestination(state, {
    isUserInfoOnly,
    includeUserInfo,
    needsPrepReasons,
    warnings,
  });
  if (userInfoDest) {
    destinations.userInfo = userInfoDest;
  }

  // ---- Auth ----
  const auth: FullCookbookAuth = { mode: state.auth.mode };
  const tenantId = (state.auth.tenantId ?? '').trim();
  const chefKeyId = (state.auth.chefKeyId ?? '').trim();

  const isAppReg =
    state.auth.mode === 'AppRegistrationSecret' || state.auth.mode === 'AppRegistrationCertificate';

  if (tenantId) {
    if (GUID_PATTERN.test(tenantId)) {
      auth.tenantId = tenantId;
    } else {
      warnings.push({
        id: 'auth-tenant-not-guid',
        severity: 'warning',
        message: 'auth.tenantId is present but not a GUID; it was not carried. Supply a valid tenant id during prep.',
        field: 'auth.tenantId',
      });
      needsPrepReasons.push('Supply a valid GUID tenant id.');
    }
  } else if (isAppReg) {
    // App-registration sign-in is non-interactive, so the tenant is recipe
    // content the operator must supply. The interactive modes (WebLogin,
    // DeviceCode) resolve the tenant from sign-in at run time, so they need no
    // tenant here to save.
    needsPrepReasons.push(`Auth mode ${state.auth.mode} requires a tenant id.`);
  }

  // Carry the bound Chef's Key (CK-1) when present. The Chef's Key holds the
  // client id / certificate thumbprint and (for secret auth) the secret, which
  // lives only in Windows Credential Manager -- never in the recipe. Binding is
  // a runtime-readiness concern, not a translation requirement, so an unbound
  // App-registration recipe still produces a candidate (readiness blocks it).
  if (chefKeyId) {
    auth.chefKeyId = chefKeyId;
  }

  // ---- Advanced ----
  let advanced: { extraArguments: string } | undefined;
  const extraArguments = (state.advanced.extraArguments ?? '').trim();
  if (extraArguments) {
    const advancedResult = translateAdvancedArgs(extraArguments);
    for (const note of advancedResult.warnings) {
      warnings.push(note);
    }
    for (const lossy of advancedResult.unsupported) {
      unsupportedOrLossyFields.push(lossy);
    }
    for (const reason of advancedResult.needsPrep) {
      needsPrepReasons.push(reason);
    }
    if (advancedResult.passThrough) {
      advanced = { extraArguments };
    }
  }
  const scriptPathPreview = (state.advanced.scriptPath ?? '').trim();
  if (scriptPathPreview) {
    notesForUi.push(
      'advanced.scriptPath is preview-only; the full recipe carries no script path (the broker resolves the managed PAX engine path at runtime).',
    );
  }

  // ---- Assemble candidate ----
  const importMetadata: FullCookbookImportMetadata = {
    source: 'mini-kitchen-lite',
    originalIdentity,
  };
  if (hasFactTier(state)) {
    importMetadata.factTier = state.destinations.fact.tier;
  }
  if (scriptPathPreview) {
    importMetadata.scriptPathPreview = scriptPathPreview;
  }
  if (state.ingredients.preset) {
    importMetadata.preset = state.ingredients.preset;
  }

  const candidate: FullCookbookRecipeCandidate = {
    recipeId,
    recipeSchemaVersion: 1,
    paxAdapterVersion,
    createdAt,
    updatedAt,
    createdBy,
    executionMode: state.executionMode,
    identity: { name },
    ingredients,
    query,
    processing,
    destinations,
    auth,
    importMetadata,
  };
  if (advanced) {
    candidate.advanced = advanced;
  }

  // De-duplicate prep reasons / notes while preserving order.
  const dedupedPrep = dedupe(needsPrepReasons);
  const dedupedNotes = dedupe(notesForUi);

  return {
    ok: true,
    fullRecipeCandidate: candidate,
    metadata: {
      recipeId,
      paxAdapterVersion,
      sourcePreset: state.ingredients.preset,
    },
    warnings,
    needsPrep: dedupedPrep.length > 0,
    needsPrepReasons: dedupedPrep,
    unsupportedOrLossyFields,
    notesForUi: dedupedNotes,
  };
}

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

function hasFactTier(state: MiniKitchenRecipeState): boolean {
  return Boolean(state.destinations.fact && state.destinations.fact.tier);
}

interface UserInfoTranslateCtx {
  isUserInfoOnly: boolean;
  includeUserInfo: boolean;
  needsPrepReasons: string[];
  warnings: TranslationNote[];
}

function translateUserInfoDestination(
  state: MiniKitchenRecipeState,
  ctx: UserInfoTranslateCtx,
): FullCookbookDestination | undefined {
  const ui = state.destinations.userInfo;

  if (ui.mode === 'default-colocate') {
    if (ctx.isUserInfoOnly) {
      ctx.needsPrepReasons.push(
        'User-info-only mode has no audit output to co-locate next to; choose a write-new or append user-info path.',
      );
      return undefined;
    }
    if (!ctx.includeUserInfo) {
      return undefined;
    }
    const factPath = (state.destinations.fact.path ?? '').trim();
    if (!factPath) {
      ctx.needsPrepReasons.push(
        'Co-locate user-info output derives from the audit output path; supply an audit output path or a separate user-info path.',
      );
      return undefined;
    }
    return { mode: 'outputPath', path: deriveCoLocateUserInfoPath(factPath) };
  }

  const path = (ui.path ?? '').trim();
  if (ui.mode === 'write-new') {
    if (path) {
      return { mode: 'outputPath', path };
    }
    ctx.needsPrepReasons.push('User-info write-new mode requires an output path.');
    return undefined;
  }
  if (ui.mode === 'append') {
    if (path) {
      return { mode: 'append', appendFile: path };
    }
    ctx.needsPrepReasons.push('User-info append mode requires an append-file path.');
    return undefined;
  }
  return undefined;
}

/**
 * Derive the co-located user-info output path from a fact path. Replicates the
 * exact heuristic in `commandRenderer.ts` so the projected `-OutputPathUserInfo`
 * matches the Mini-Kitchen preview byte-for-byte.
 */
function deriveCoLocateUserInfoPath(factPath: string): string {
  const p = factPath.trim();
  if (!p) return p;
  if (p.endsWith('/') || p.endsWith('\\')) return p;
  if (/^https?:\/\//i.test(p)) return p;
  const lastSep = Math.max(p.lastIndexOf('/'), p.lastIndexOf('\\'));
  const leaf = lastSep >= 0 ? p.slice(lastSep + 1) : p;
  if (leaf.includes('.') && lastSep >= 0) {
    return p.slice(0, lastSep + 1);
  }
  return p;
}

interface AdvancedTranslateResult {
  passThrough: boolean;
  warnings: TranslationNote[];
  unsupported: UnsupportedOrLossyField[];
  needsPrep: string[];
}

/**
 * Vet the verbatim advanced-args trailer before it is carried into the
 * candidate. Refuses to pass through anything that looks like a secret or a
 * removed/unsupported switch — mirroring the C# adapter's `ScanRemovedSwitches`
 * / `ScanSecretShape` guards and the lite renderer's advanced-args blocking.
 */
function translateAdvancedArgs(extraArguments: string): AdvancedTranslateResult {
  const warnings: TranslationNote[] = [];
  const unsupported: UnsupportedOrLossyField[] = [];
  const needsPrep: string[] = [];

  if (containsLikelySecret(extraArguments)) {
    warnings.push({
      id: 'advanced-args-secret',
      severity: 'warning',
      message:
        'Advanced arguments contain a secret-shaped value; the trailer was not carried into the candidate. Secrets never belong in a recipe.',
      field: 'advanced.extraArguments',
    });
    unsupported.push({
      field: 'advanced.extraArguments',
      reason: 'Secret-shaped advanced arguments were rejected.',
    });
    needsPrep.push('Remove secret-shaped values from advanced arguments.');
    return { passThrough: false, warnings, unsupported, needsPrep };
  }

  const analysis = analyzeAdvancedArgs(extraArguments);
  for (const token of analysis.tokens) {
    if (token.isSwitch && token.switchName) {
      const removed = REMOVED_OR_UNSUPPORTED_SWITCHES.find(s => s.name === token.switchName);
      if (removed) {
        const label = removed.userFacingName ?? `-${token.switchName}`;
        warnings.push({
          id: `advanced-removed-${token.switchName}`,
          severity: 'warning',
          message: `Advanced arguments include the removed/unsupported switch ${label}; the trailer was not carried. ${removed.reason}`,
          field: 'advanced.extraArguments',
        });
        unsupported.push({
          field: 'advanced.extraArguments',
          reason: `Removed/unsupported switch ${label} present.`,
        });
        needsPrep.push(`Remove the unsupported switch ${label} from advanced arguments.`);
        return { passThrough: false, warnings, unsupported, needsPrep };
      }
    }
  }

  warnings.push({
    id: 'advanced-args-present',
    severity: 'info',
    message:
      'Advanced arguments are carried verbatim into the candidate. PAX Cookbook cannot fully validate advanced switches.',
    field: 'advanced.extraArguments',
  });
  return { passThrough: true, warnings, unsupported, needsPrep };
}

function dedupe(values: readonly string[]): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const v of values) {
    if (!seen.has(v)) {
      seen.add(v);
      out.push(v);
    }
  }
  return out;
}
