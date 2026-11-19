/**
 * Mini-Kitchen command renderer (Phase 3).
 *
 * Produces a deterministic command preview from a Mini-Kitchen recipe state.
 * The renderer is argv-first (master plan Decision D11): it normalizes the
 * recipe, builds a single canonical `argv: string[]`, then derives the
 * PowerShell 7 multiline and single-line forms from that same argv.
 *
 * Multiline output uses PowerShell backtick (`` ` ``) line continuations.
 * The POSIX `\` continuation is never emitted. The renderer guarantees no
 * trailing whitespace after any backtick (master plan Risk R14).
 *
 * The renderer does not:
 *   - run PAX
 *   - connect to a tenant
 *   - validate local paths
 *   - collect, store, or emit secrets
 *   - emit removed/unsupported switches
 */

import type {
  AgentFilterMode,
  MiniKitchenRecipeState,
  PromptFilter,
  RollupMode,
  WarningEntry,
} from '../types';
import { ADVANCED_ARGUMENTS_ERROR, parseAdvancedArguments, type PaxParameter } from './advancedArgs';
import { normalizeRecipe } from './normalizeRecipe';

// -----------------------------------------------------------------------------
// Public types
// -----------------------------------------------------------------------------

/** Why the renderer refused to emit a token or token group. */
export type CommandRenderBlockedKind =
  | 'removed-switch'
  | 'secret-value'
  | 'duplicate-switch'
  | 'orphan-advanced-token';

export interface CommandRenderBlockedItem {
  id: string;
  kind: CommandRenderBlockedKind;
  /** Switch name or descriptive label. Never includes a secret value verbatim. */
  detail: string;
}

/**
 * A reason the rendered command is NOT yet ready to copy and run verbatim.
 * Each blocker corresponds to a required value or switch that is missing for
 * the *current* configuration, or to a placeholder that must be replaced
 * before the command will run. The set of blockers is config-aware: e.g. a
 * start/end date is only required in audit-query mode, a certificate
 * thumbprint only for AppRegistration (certificate) auth, etc. The `ready`
 * flag on `RenderedCommand` is exactly `blockers.length === 0`.
 */
export interface CommandReadinessBlocker {
  id: string;
  message: string;
  field?: string;
}

export interface RenderedCommand {
  shell: 'pwsh';
  parameters: readonly PaxParameter[];
  argv: readonly string[];
  multiline: string;
  singleLine: string;
  setupSnippets: readonly string[];
  warnings: readonly WarningEntry[];
  blocked: readonly CommandRenderBlockedItem[];
  assumptions: readonly string[];
  explanation: readonly string[];
  /**
   * Config-aware readiness: `true` only when no required value/switch is
   * missing and no placeholder remains, so the displayed command can be
   * copied and run verbatim. Drives the Review section's helper copy.
   */
  ready: boolean;
  /** The specific reasons `ready` is `false`. Empty when `ready` is `true`. */
  blockers: readonly CommandReadinessBlocker[];
}

// -----------------------------------------------------------------------------
// Constants
// -----------------------------------------------------------------------------

/** Placeholder used when the recipe has no PAX script path. */
export const PAX_SCRIPT_PATH_PLACEHOLDER = '<PATH_TO_PAX.ps1>';

/**
 * Reference shown for the script that PAX Cookbook runs. The app resolves and
 * runs the managed PAX engine itself, so the preview names the managed engine
 * instead of a user-supplied `.ps1` path.
 */
export const MANAGED_PAX_ENGINE_LABEL = '(managed PAX engine)';

/** Placeholder used when AppRegistrationSecret auth is selected. */
export const CLIENT_SECRET_PLACEHOLDER = '<CLIENT_SECRET>';

const MULTILINE_SEPARATOR = ' `\n  ';
const SWITCH_TOKEN = /^-[A-Za-z][A-Za-z0-9_-]*$/;

// -----------------------------------------------------------------------------
// Quoting / rendering primitives
// -----------------------------------------------------------------------------

/**
 * Quote a single value for PowerShell. Rules:
 *  - Empty string             -> `""`
 *  - Plain ASCII identifier   -> returned as-is (`Bypass`, `NoProfile`)
 *  - PowerShell switch tokens -> returned as-is (`-NoProfile`)
 *  - ISO date / datetime      -> returned as-is (PAX's `[datetime]` binder
 *                                accepts unquoted YYYY-MM-DD without issue)
 *  - Anything else            -> double-quoted with PS-safe escapes
 *                                (`` ` `` -> ` `` `, `"` -> `""`, `$` -> `` `$ ``)
 */
export function quotePowerShellArg(value: string): string {
  if (value.includes('\0')) throw new Error('A parameter contains an unsupported null character.');
  const parts: string[] = [];
  let text = '';
  for (const character of value) {
    const code = character.charCodeAt(0);
    if (code >= 0x2018 && code <= 0x201f) {
      parts.push(`'${text.replace(/'/g, "''")}'`, `[char]${code}`);
      text = '';
    } else text += character;
  }
  parts.push(`'${text.replace(/'/g, "''")}'`);
  return parts.length === 1 ? parts[0]! : `(${parts.join('+')})`;
}

export function serializePaxValue(parameter: PaxParameter): string {
  if (parameter.type === 'string[]' && Array.isArray(parameter.value)) {
    return `[string[]]@(${parameter.value.map(quotePowerShellArg).join(',')})`;
  }
  if (parameter.type === 'string' && typeof parameter.value === 'string') return quotePowerShellArg(parameter.value);
  if (parameter.type === 'bool' && typeof parameter.value === 'boolean') return parameter.value ? '$true' : '$false';
  if ((parameter.type === 'int' || parameter.type === 'double') && typeof parameter.value === 'number' && Number.isFinite(parameter.value)) {
    return `[${parameter.type}]${parameter.value}`;
  }
  throw new Error('Unsupported parameter type.');
}

export function renderTypedPaxCommand(parameters: readonly PaxParameter[], scriptLabel = MANAGED_PAX_ENGINE_LABEL): string {
  const assignments = parameters.map(parameter => `${quotePowerShellArg(parameter.name)}=${serializePaxValue(parameter)}`).join(';');
  return `$paxParameters=@{${assignments}}; & ${quotePowerShellArg(scriptLabel)} @paxParameters`;
}

/**
 * Derive the PowerShell 7 single-line form from `argv`. Each argv element is
 * quoted independently and joined with single spaces.
 *
 * Some argv slots may already be pre-rendered PowerShell value forms — for
 * example, a comma-joined-and-pre-quoted array literal for `-ActivityTypes`,
 * `-UserIds`, `-GroupNames`, or `-AgentId`. The internal builder marks those
 * slots in `rawIndices` so the renderer leaves them untouched (re-quoting a
 * pre-rendered array literal as a single string would break PowerShell
 * `[string[]]` parameter binding). Public callers that do not have a
 * `rawIndices` set simply omit it; everything is then quoted normally.
 */
export function renderPowerShellSingleLine(
  argv: readonly string[],
  rawIndices?: ReadonlySet<number>,
): string {
  return argv.map((tok, i) => (rawIndices && rawIndices.has(i) ? tok : quotePowerShellArg(tok))).join(' ');
}

/**
 * Derive the PowerShell 7 multiline form from `argv`. Uses backtick line
 * continuations. Groups each switch (`-Name`) with its trailing value tokens
 * onto a single line. The first token starts the command (no leading indent);
 * subsequent lines are indented two spaces. No trailing whitespace appears
 * after a backtick.
 *
 * Slots flagged in `rawIndices` (see `renderPowerShellSingleLine`) are
 * emitted verbatim — used by array-switch values that are already a valid
 * PowerShell array literal.
 */
export function renderPowerShellMultiline(
  argv: readonly string[],
  rawIndices?: ReadonlySet<number>,
): string {
  if (argv.length === 0) {
    return '';
  }
  const groups = groupArgvForMultiline(argv);
  // groupArgvForMultiline preserves argv order; rebuild a position map so the
  // per-token quoting decision still sees the original argv index.
  let cursor = 0;
  const renderedGroups: string[] = [];
  for (const g of groups) {
    const parts: string[] = [];
    for (let j = 0; j < g.length; j++, cursor++) {
      const tok = g[j]!;
      parts.push(rawIndices && rawIndices.has(cursor) ? tok : quotePowerShellArg(tok));
    }
    renderedGroups.push(parts.join(' '));
  }
  return renderedGroups.join(MULTILINE_SEPARATOR);
}

/**
 * Heuristic grouping for multiline rendering: any token starting with `-`
 * begins a new logical line; subsequent non-`-` tokens belong to that line.
 * The first token (`pwsh`) always starts its own group.
 */
function groupArgvForMultiline(argv: readonly string[]): string[][] {
  const groups: string[][] = [];
  let current: string[] = [];
  for (let i = 0; i < argv.length; i++) {
    const tok = argv[i]!;
    if (i === 0) {
      current = [tok];
      groups.push(current);
      continue;
    }
    if (SWITCH_TOKEN.test(tok)) {
      current = [tok];
      groups.push(current);
    } else {
      current.push(tok);
    }
  }
  return groups;
}

// -----------------------------------------------------------------------------
// Argv builder
// -----------------------------------------------------------------------------

interface BuildContext {
  parameters: PaxParameter[];
  argv: string[];
  /**
   * Indices into `argv` whose values are pre-rendered PowerShell literals
   * (e.g. a comma-joined-and-pre-quoted array literal for a `[string[]]`
   * PAX parameter). The renderer must not re-quote these slots; they are
   * already valid PowerShell value syntax.
   */
  rawIndices: Set<number>;
  warnings: WarningEntry[];
  blocked: CommandRenderBlockedItem[];
  assumptions: string[];
  explanation: string[];
  setupSnippets: string[];
  emittedSwitches: Set<string>;
  blockers: CommandReadinessBlocker[];
}

function pushSwitch(ctx: BuildContext, name: string, ...values: string[]): void {
  ctx.parameters.push({ name, type: 'string', value: values[0] ?? '' });
  ctx.argv.push(`-${name}`, ...values);
  ctx.emittedSwitches.add(name);
}

function pushFlag(ctx: BuildContext, name: string): void {
  ctx.parameters.push({ name, type: 'bool', value: true });
  ctx.argv.push(`-${name}`);
  ctx.emittedSwitches.add(name);
}

/**
 * Emit a `-Name 'a','b','c'`-shaped argv pair for a PAX parameter typed as
 * `[string[]]`. PowerShell binds array params from a single comma-joined
 * argument token; emitting separate argv tokens (`-Name a b c`) would let
 * the second and later values fall through to `$RemainingArgs` instead of
 * being bound to the parameter.
 *
 * Each value is quoted via `quotePowerShellArg` and joined with `,`. The
 * resulting single value token is recorded in `ctx.rawIndices` so the
 * downstream renderers do not re-quote the already-valid literal.
 */
function pushArraySwitch(ctx: BuildContext, name: string, values: readonly string[]): void {
  if (values.length === 0) {
    return;
  }
  ctx.parameters.push({ name, type: 'string[]', value: [...values] });
  const quoted = values.map(quotePowerShellArg).join(',');
  ctx.argv.push(`-${name}`);
  ctx.argv.push(quoted);
  ctx.rawIndices.add(ctx.argv.length - 1);
  ctx.emittedSwitches.add(name);
}

function warn(
  ctx: BuildContext,
  id: string,
  severity: WarningEntry['severity'],
  message: string,
  field?: string,
): void {
  ctx.warnings.push(field === undefined ? { id, severity, message } : { id, severity, message, field });
}

function block(
  ctx: BuildContext,
  id: string,
  kind: CommandRenderBlockedKind,
  detail: string,
): void {
  ctx.blocked.push({ id, kind, detail });
}

/**
 * Emit a `warning`-severity entry AND record it as a readiness blocker. Use
 * this (instead of plain `warn(..., 'warning', ...)`) only for conditions
 * that make the command unsafe to run verbatim: a required value/switch that
 * is missing for the current configuration, or a placeholder that must be
 * replaced. Caveat-style warnings that do not stop the command from running
 * (e.g. "we can't validate this path") must stay as plain `warn` calls so
 * they never flip the command's `ready` flag.
 */
function warnBlocking(
  ctx: BuildContext,
  id: string,
  message: string,
  field?: string,
): void {
  warn(ctx, id, 'warning', message, field);
  ctx.blockers.push(field === undefined ? { id, message } : { id, message, field });
}

/**
 * Build the canonical argv for a recipe. Public for callers that want just
 * the tokens (lite recipe `commandPreview.argv`, fixtures, etc.). The result
 * is the same array that the rendered strings are derived from.
 */
export function buildPaxArgv(recipe: MiniKitchenRecipeState): readonly string[] {
  return renderPaxCommand(recipe).argv;
}

// -----------------------------------------------------------------------------
// Main renderer
// -----------------------------------------------------------------------------

/**
 * Render a deterministic command preview from a Mini-Kitchen recipe state.
 * Pure function; safe to call on every keystroke.
 */
export function renderPaxCommand(recipe: MiniKitchenRecipeState): RenderedCommand {
  const state = normalizeRecipe(recipe);

  const ctx: BuildContext = {
    parameters: [],
    argv: [],
    rawIndices: new Set<number>(),
    warnings: [],
    blocked: [],
    assumptions: [],
    explanation: [],
    setupSnippets: [],
    emittedSwitches: new Set(),
    blockers: [],
  };

  emitBaseCommand(ctx);
  emitDateRange(ctx, state);
  emitDataCollection(ctx, state);
  emitActivityFilters(ctx, state);
  emitAgentFilter(ctx, state);
  emitPromptFilter(ctx, state);
  emitRollup(ctx, state);
  emitFactOutput(ctx, state);
  emitCombineOutput(ctx, state);
  emitUserInfoOutput(ctx, state);
  emitAgent365Output(ctx, state);
  emitDeidentify(ctx, state);
  emitAuthContext(ctx, state);
  emitAdvancedArgs(ctx, state);

  emitGlobalAssumptions(ctx);
  emitExplanation(ctx, state);

  let argv: string[] = [];
  let singleLine = '';
  let multiline = '';
  if (ctx.blocked.length === 0) {
    try {
      const expression = renderTypedPaxCommand(ctx.parameters);
      argv = ['pwsh', '-NoProfile', '-NoLogo', '-Command', expression];
      singleLine = `pwsh -NoProfile -NoLogo -Command ${quotePowerShellArg(expression)}`;
      multiline = singleLine;
    } catch {
      block(ctx, 'literal-invalid', 'secret-value', 'A parameter cannot be rendered as literal data.');
      warnBlocking(ctx, 'literal-invalid', 'A parameter contains an unsupported value.');
    }
  }

  return {
    shell: 'pwsh',
    parameters: ctx.blocked.length === 0 ? ctx.parameters : [],
    argv,
    multiline,
    singleLine,
    setupSnippets: ctx.setupSnippets,
    warnings: ctx.warnings,
    blocked: ctx.blocked,
    assumptions: ctx.assumptions,
    explanation: ctx.explanation,
    ready: ctx.blockers.length === 0 && ctx.blocked.length === 0,
    blockers: ctx.blockers,
  };
}

// -----------------------------------------------------------------------------
// Section emitters
// -----------------------------------------------------------------------------

function emitBaseCommand(ctx: BuildContext): void {
  // PAX Cookbook resolves and runs the managed PAX engine itself. The preview
  // names the managed engine instead of a user-supplied `.ps1` path, and any
  // imported script path is kept only as silent `.paxlite` provenance.
  ctx.argv.push('pwsh', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', MANAGED_PAX_ENGINE_LABEL);
}

function emitDateRange(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  if (state.query.mode !== 'audit-query') {
    return;
  }
  // Previous-day mode: absence of the date switches IS the signal. Omitting
  // both -StartDate and -EndDate makes PAX query the previous full UTC day.
  // This is a valid, ready configuration — it emits nothing and adds zero
  // blockers (readiness derives from blockers.length === 0).
  if (state.query.dateMode === 'previous-day') {
    ctx.assumptions.push(
      'No date range set: PAX queries the previous full UTC day (yesterday 00:00 UTC through today 00:00 UTC). Ideal for scheduled daily or append runs.',
    );
    return;
  }
  const start = (state.query.startDate ?? '').trim();
  const end = (state.query.endDate ?? '').trim();
  // Custom range, both sides blank: a single incomplete-range blocker (not two),
  // steering the user to fill both dates or switch to Previous day. PAX would
  // otherwise fill a missing side with `*` (unbounded) — a footgun we block.
  if (!start && !end) {
    warnBlocking(
      ctx,
      'date-range-missing',
      'Set both a start and end date, or switch the date range to Previous day to automatically pull the last full UTC day — ideal for scheduled or append runs.',
      'query.startDate',
    );
    return;
  }
  // Custom range, exactly one side: emit the present side and block the missing
  // one (both-or-neither — never let PAX silently unbound the missing side).
  if (start) {
    pushSwitch(ctx, 'StartDate', start);
  } else {
    warnBlocking(
      ctx,
      'start-date-missing',
      'Set a start date to complete the range, or clear both dates and switch to Previous day for an automatic one-day lookback.',
      'query.startDate',
    );
  }
  if (end) {
    pushSwitch(ctx, 'EndDate', end);
  } else {
    warnBlocking(
      ctx,
      'end-date-missing',
      'Set an end date to complete the range, or clear both dates and switch to Previous day for an automatic one-day lookback.',
      'query.endDate',
    );
  }
}

function emitDataCollection(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  if (state.query.mode === 'user-info-only') {
    pushFlag(ctx, 'OnlyUserInfo');
    warn(
      ctx,
      'user-info-only-suppresses',
      'info',
      'User-info-only mode suppresses audit date range, audit filters, rollup, and audit output switches.',
    );
    return;
  }
  if (state.query.mode === 'agent365-only') {
    pushFlag(ctx, 'OnlyAgent365Info');
    warn(
      ctx,
      'agent365-only-suppresses',
      'info',
      'Microsoft Agent 365 only mode exports the catalog and suppresses the audit query, audit filters, rollup, user info, and audit output switches.',
    );
    return;
  }
  if (state.query.includeM365Usage === true) {
    pushFlag(ctx, 'IncludeM365Usage');
  }
  // -ExcludeCopilotInteraction is standalone in PAX v1.11.3. Emit whenever
  // the recipe state requests it, independent of -IncludeM365Usage. The
  // OnlyUserInfo branch above already early-returns, which matches PAX's
  // mutex between -OnlyUserInfo and -ExcludeCopilotInteraction.
  if (state.query.excludeCopilotInteraction === true) {
    pushFlag(ctx, 'ExcludeCopilotInteraction');
  }
  // Bring your own directory: -UserInfoFile supplies the directory from a file
  // and already implies user info, so PAX does not also emit -IncludeUserInfo.
  // It cannot be combined with -GroupNames (a command-readiness blocker).
  const userInfoFile = (state.query.userInfoFile ?? '').trim();
  const groupNames = state.processing.groupNames ?? [];
  if (userInfoFile) {
    if (groupNames.length > 0) {
      warnBlocking(
        ctx,
        'byod-groupnames-conflict',
        'Bring your own directory and group filtering can’t be used together. Remove the group names in Step 2, or clear the directory file.',
        'query.userInfoFile',
      );
    }
    pushSwitch(ctx, 'UserInfoFile', userInfoFile);
  } else if (state.query.includeUserInfo === true) {
    pushFlag(ctx, 'IncludeUserInfo');
  }
  // Microsoft Agent 365 catalog alongside the audit run.
  if (state.query.includeAgent365Info === true) {
    pushFlag(ctx, 'IncludeAgent365Info');
  }
}

function emitActivityFilters(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  if (state.query.mode !== 'audit-query') {
    return;
  }
  const activityTypes = state.processing.activityTypes ?? [];
  if (activityTypes.length > 0) {
    pushArraySwitch(ctx, 'ActivityTypes', activityTypes);
  }
  const userIds = state.processing.userIds ?? [];
  if (userIds.length > 0) {
    pushArraySwitch(ctx, 'UserIds', userIds);
  }
  const groupNames = state.processing.groupNames ?? [];
  if (groupNames.length > 0) {
    pushArraySwitch(ctx, 'GroupNames', groupNames);
  }
}

function emitAgentFilter(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  if (state.query.mode !== 'audit-query') {
    return;
  }
  const filter = state.processing.agentFilter;
  if (!filter) {
    return;
  }
  const mode: AgentFilterMode = filter.mode;
  if (mode === 'ids-only') {
    const ids = filter.ids ?? [];
    if (ids.length === 0) {
      warnBlocking(
        ctx,
        'agent-id-filter-no-ids',
        'Add one or more agent IDs to the agent filter, or change the filter mode.',
        'processing.agentFilter.ids',
      );
      return;
    }
    pushArraySwitch(ctx, 'AgentId', ids);
    return;
  }
  if (mode === 'agents-only') {
    pushFlag(ctx, 'AgentsOnly');
    return;
  }
  if (mode === 'exclude-agents') {
    pushFlag(ctx, 'ExcludeAgents');
    return;
  }
  // 'none' -> emit nothing
}

function emitPromptFilter(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  if (state.query.mode !== 'audit-query') {
    return;
  }
  const value: PromptFilter | undefined = state.processing.promptFilter;
  if (value === undefined) {
    return;
  }
  // PAX v1.11.3 defaults -PromptFilter to 'Null'; omit when the value matches
  // the default so the rendered command stays compact.
  if (value === 'Null') {
    return;
  }
  pushSwitch(ctx, 'PromptFilter', value);
}

function emitRollup(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  if (state.query.mode !== 'audit-query') {
    return;
  }
  const rollup: RollupMode | undefined = state.processing.rollup;
  if (rollup === 'rollup') {
    pushFlag(ctx, 'Rollup');
    ctx.assumptions.push('Rollup is enabled. PAX auto-installs Python 3.10+ and orjson on first use. The builder does not validate the runtime.');
  } else if (rollup === 'rollup-plus-raw') {
    pushFlag(ctx, 'RollupPlusRaw');
    ctx.assumptions.push('RollupPlusRaw is enabled. PAX auto-installs Python 3.10+ and orjson on first use. The builder does not validate the runtime.');
  }

  // Mirror the broker (PaxAdapter.cs): -Dashboard AIBV is emitted only with a
  // rollup, never alongside -IncludeM365Usage, and only when the recipe selected
  // AIBV. AIO is PAX's default and is omitted. The dashboard match is
  // case-insensitive so the preview agrees with the broker (CiEq) for a recipe
  // whose persisted value differs in case.
  const rollupOn = rollup === 'rollup' || rollup === 'rollup-plus-raw';
  if (
    rollupOn &&
    state.query.includeM365Usage !== true &&
    (state.processing.dashboard ?? '').toLowerCase() === 'aibv'
  ) {
    pushSwitch(ctx, 'Dashboard', 'AIBV');
  }

  // Mirror the broker (PaxAdapter.cs): -FillerLabel is rollup-only and is never
  // emitted alongside -IncludeM365Usage. Blank/default omits the switch; 'Fixed'
  // also emits -FillerLabelText. An empty 'Fixed' label is a readiness blocker.
  const fillerLabel = state.processing.fillerLabel;
  if (rollupOn && state.query.includeM365Usage !== true && fillerLabel) {
    pushSwitch(ctx, 'FillerLabel', fillerLabel);
    if (fillerLabel === 'Fixed') {
      const text = (state.processing.fillerLabelText ?? '').trim();
      if (text.length > 0) {
        pushSwitch(ctx, 'FillerLabelText', text);
      } else {
        warnBlocking(
          ctx,
          'filler-label-text-missing',
          'Custom filler label is selected but no label text was entered. Enter the text or choose a different filler.',
          'processing.fillerLabelText',
        );
      }
    }
  }
}

/**
 * Emit -Deidentify. The de-identify flag is engine-wide: it anonymizes the raw
 * audit + EntraUsers output and threads --deidentify into the rollup processor,
 * so it is valid in every run shape (audit, rollup, and user-info-only).
 */
function emitDeidentify(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  if (state.processing.deidentify === true) {
    pushFlag(ctx, 'Deidentify');
  }
}

function emitFactOutput(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  if (state.query.mode !== 'audit-query') {
    return;
  }
  const fact = state.destinations.fact;
  const tier = fact.tier;
  // Mini-Kitchen renders text only and cannot reach any storage tier from the
  // browser, so "cannot validate path" notices are stated as assumptions —
  // not warnings — and never count against the Warnings pill.
  if (tier === 'fabric') {
    ctx.assumptions.push(
      'The builder cannot validate Fabric Lakehouse paths. Confirm the runtime identity is at least Contributor on the destination workspace before running.',
    );
  } else if (tier === 'sharepoint') {
    ctx.assumptions.push(
      'The builder cannot validate SharePoint paths. Confirm the runtime identity has write access to the destination document library before running.',
    );
  }
  const path = (fact.path ?? '').trim();
  if (fact.mode === 'write-new') {
    if (path) {
      pushSwitch(ctx, 'OutputPath', path);
    } else {
      warnBlocking(
        ctx,
        'fact-output-path-missing',
        'Add an audit output path in Step 4 (write-new mode).',
        'destinations.fact.path',
      );
    }
  } else if (fact.mode === 'append') {
    if (path) {
      pushSwitch(ctx, 'AppendFile', path);
      warn(
        ctx,
        'append-fact-info',
        'info',
        'Append mode merges into an existing audit output file. Verify the existing file schema matches what PAX will write.',
        'destinations.fact.path',
      );
    } else {
      warnBlocking(
        ctx,
        'fact-append-path-missing',
        'Add an audit append-file path in Step 4 (append mode).',
        'destinations.fact.path',
      );
    }
  }
}

function emitCombineOutput(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  if (state.query.mode !== 'audit-query') {
    return;
  }
  const advanced = parseAdvancedArguments(state.advanced.extraArguments ?? '');
  if (!advanced.error && advanced.parameters.some(parameter => parameter.name === 'CombineOutput')) return;
  // PAX v1.11.3 auto-enables -CombineOutput when -IncludeM365Usage is bound
  // (line 1716-1722) and again when -Rollup / -RollupPlusRaw is bound
  // (line 2243-2249). Emitting the switch in those cases would be redundant;
  // we leave the resolution to PAX. The UI also disables the combine/separate
  // control when -IncludeM365Usage is on so the user cannot pick 'separate'
  // in a configuration where PAX would override.
  if (state.query.includeM365Usage === true) {
    return;
  }
  // -CombineOutput is "only relevant with multiple activity types" per PAX
  // v1.11.3 (-OnlyUserInfo mutex banner, line 1855). With at most one
  // activity type we never emit the switch.
  const activityTypes = state.processing.activityTypes ?? [];
  if (activityTypes.length <= 1) {
    return;
  }
  // Default to combined when undefined so older lite recipes that did not
  // carry this field render as -CombineOutput-on. Only emit the switch when
  // combined is selected; separate is the absence of the switch.
  const mode = state.processing.outputCombineMode ?? 'combined';
  if (mode === 'combined') {
    pushFlag(ctx, 'CombineOutput');
  }
}

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

function emitAgent365Output(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  const active =
    state.query.mode === 'agent365-only' || state.query.includeAgent365Info === true;
  if (!active) {
    return;
  }
  const a = state.destinations.agent365;
  const path = (a.path ?? '').trim();
  if (a.mode === 'default-colocate') {
    if (state.query.mode === 'agent365-only') {
      warnBlocking(
        ctx,
        'agent365-only-colocate-invalid',
        'Microsoft Agent 365 only mode has no audit output to place the catalog beside. Switch the Agent 365 output to "Write a separate file" and add a path in Step 4.',
        'destinations.agent365.path',
      );
      return;
    }
    // Co-locate alongside the audit output: PAX co-locates the catalog by
    // default when -IncludeAgent365Info is passed without a destination switch,
    // so nothing is emitted here.
    return;
  }
  if (a.mode === 'write-new') {
    if (path) {
      pushSwitch(ctx, 'OutputPathAgent365Info', path);
    } else {
      warnBlocking(
        ctx,
        'agent365-output-path-missing',
        'Add an Agent 365 catalog output path in Step 4 (write-new mode).',
        'destinations.agent365.path',
      );
    }
  } else if (a.mode === 'append') {
    if (path) {
      pushSwitch(ctx, 'AppendAgent365Info', path);
      warn(
        ctx,
        'append-agent365-info',
        'info',
        'Append mode merges into an existing Agent 365 catalog file. Verify the existing file schema matches what PAX will write.',
      );
    } else {
      warnBlocking(
        ctx,
        'agent365-append-path-missing',
        'Add an Agent 365 append-file path in Step 4 (append mode).',
        'destinations.agent365.path',
      );
    }
  }
}

function emitUserInfoOutput(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  // Agent-365-only runs produce no Entra user-info stream.
  if (state.query.mode === 'agent365-only') {
    return;
  }
  const ui = state.destinations.userInfo;
  if (ui.mode === 'default-colocate') {
    if (state.query.mode === 'user-info-only') {
      warnBlocking(
        ctx,
        'user-info-only-colocate-invalid',
        'OnlyUserInfo mode has no audit output to co-locate next to. Switch user-info output to "Write a separate user-info file" and add a path in Step 4.',
        'destinations.userInfo.path',
      );
      return;
    }
    if (state.query.includeUserInfo !== true) {
      return;
    }
    // PAX v1.11.3 (~line 2954) aborts when -IncludeUserInfo is bound without
    // -OutputPathUserInfo or -AppendUserInfo. Co-locate satisfies that
    // pre-check by emitting -OutputPathUserInfo pointing at the audit
    // output's directory, so PAX writes the EntraUsers CSV alongside the fact.
    const factPath = (state.destinations.fact.path ?? '').trim();
    if (!factPath) {
      warnBlocking(
        ctx,
        'user-info-colocate-fact-path-missing',
        'Co-locate mode derives -OutputPathUserInfo from the audit output path. Add an audit output path in Step 4, or switch user-info output to "Write a separate user-info file".',
        'destinations.userInfo.path',
      );
      return;
    }
    pushSwitch(ctx, 'OutputPathUserInfo', deriveCoLocateUserInfoPath(factPath));
    return;
  }
  const path = (ui.path ?? '').trim();
  if (ui.mode === 'write-new') {
    if (path) {
      pushSwitch(ctx, 'OutputPathUserInfo', path);
    } else {
      warnBlocking(
        ctx,
        'user-info-output-path-missing',
        'Add a user-info output path in Step 4 (write-new mode).',
        'destinations.userInfo.path',
      );
    }
  } else if (ui.mode === 'append') {
    if (path) {
      pushSwitch(ctx, 'AppendUserInfo', path);
      warn(
        ctx,
        'append-user-info-info',
        'info',
        'Append mode merges into an existing user-info file. Verify the existing file schema matches what PAX will write.',
        'destinations.userInfo.path',
      );
    } else {
      warnBlocking(
        ctx,
        'user-info-append-path-missing',
        'Add a user-info append-file path in Step 4 (append mode).',
        'destinations.userInfo.path',
      );
    }
  }
}

function emitAuthContext(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  const auth = state.auth;
  const tenantId = (auth.tenantId ?? '').trim();
  const clientId = (auth.clientId ?? '').trim();
  const thumbprint = (auth.certificateThumbprint ?? '').trim();

  // -Auth emission. PAX v1.11.3 defaults to WebLogin when -Auth is omitted, so
  // Mini-Kitchen emits the switch only when a non-default mode is selected.
  // AppRegistrationSecret and AppRegistrationCertificate both map to the
  // `AppRegistration` enum value — the companion switch (secret vs cert) tells
  // PAX which credential to use.
  if (auth.mode === 'AppRegistrationSecret' || auth.mode === 'AppRegistrationCertificate') {
    pushSwitch(ctx, 'Auth', 'AppRegistration');
  } else if (auth.mode === 'DeviceCode') {
    pushSwitch(ctx, 'Auth', 'DeviceCode');
  } else if (auth.mode === 'ManagedIdentity') {
    pushSwitch(ctx, 'Auth', 'ManagedIdentity');
  }

  if (tenantId) {
    pushSwitch(ctx, 'TenantId', tenantId);
  }
  if (clientId) {
    pushSwitch(ctx, 'ClientId', clientId);
  }

  if (auth.mode === 'AppRegistrationCertificate') {
    if (thumbprint) {
      pushSwitch(ctx, 'ClientCertificateThumbprint', thumbprint);
    } else {
      warnBlocking(
        ctx,
        'auth-cert-thumbprint-missing',
        'Add the certificate thumbprint in Step 4 for AppRegistration (certificate) auth.',
        'auth.certificateThumbprint',
      );
    }
  }

  if (auth.mode === 'AppRegistrationSecret') {
    warnBlocking(
      ctx,
      'auth-secret-placeholder',
      "Select a bound Chef's Key. Credentials are supplied only by PAX Cookbook, never by a copied command.",
      'auth.mode',
    );
  }

  if (auth.mode === 'ManagedIdentity') {
    ctx.assumptions.push(
      'Managed identity requires an appropriate hosted execution environment. The builder does not validate identity availability.',
    );
  }
}

// -----------------------------------------------------------------------------
// Advanced args integration
// -----------------------------------------------------------------------------

function emitAdvancedArgs(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  const parsed = parseAdvancedArguments(state.advanced.extraArguments ?? '');
  if (parsed.error || parsed.parameters.some(parameter => ctx.emittedSwitches.has(parameter.name))) {
    block(ctx, 'advanced-invalid', 'orphan-advanced-token', ADVANCED_ARGUMENTS_ERROR);
    warnBlocking(ctx, 'advanced-invalid', ADVANCED_ARGUMENTS_ERROR, 'advanced.extraArguments');
    return;
  }
  ctx.parameters.push(...parsed.parameters);
}

/**
 * Strip a single layer of surrounding `'…'` or `"…"` from an advanced-args
 * token, if present. Inner content is returned unchanged; argv stores logical
 * (unquoted) values, and the renderer re-quotes them per PowerShell rules.
 */

// -----------------------------------------------------------------------------
// Global assumptions + explanation
// -----------------------------------------------------------------------------

function emitGlobalAssumptions(ctx: BuildContext): void {
  ctx.assumptions.push('The builder builds the command text only. The machine that runs PAX is responsible for validating paths and tenant access.');
}

function emitExplanation(ctx: BuildContext, state: MiniKitchenRecipeState): void {
  ctx.explanation.push('Runs the managed PAX engine with PowerShell 7.');
  if (state.query.mode === 'user-info-only') {
    ctx.explanation.push('Pulls Entra user info only; the audit query is skipped.');
  } else {
    if (state.query.dateMode === 'previous-day') {
      ctx.explanation.push('Queries the previous full UTC day (no -StartDate/-EndDate); ideal for scheduled daily or append runs.');
    } else if (state.query.startDate || state.query.endDate) {
      ctx.explanation.push('Uses the selected audit date range.');
    }
    if (state.query.includeM365Usage === true) {
      if (state.query.excludeCopilotInteraction === true) {
        ctx.explanation.push('Includes the broader M365 audit usage bundle and excludes the CopilotInteraction branch.');
      } else {
        ctx.explanation.push('Includes the broader M365 audit usage bundle alongside Copilot interactions.');
      }
    } else if (state.query.excludeCopilotInteraction === true) {
      ctx.explanation.push('Excludes the CopilotInteraction branch from the audit pull.');
    }
    if (state.query.includeUserInfo === true) {
      ctx.explanation.push('Includes Entra user information.');
    }
    if (state.processing.rollup === 'rollup') {
      ctx.explanation.push('Rollup is enabled; PAX bootstraps its Python helper on first use.');
    } else if (state.processing.rollup === 'rollup-plus-raw') {
      ctx.explanation.push('Emits both rollup and raw rows; PAX bootstraps its Python helper on first use.');
    }
    if (state.destinations.fact.mode === 'write-new') {
      ctx.explanation.push('Writes a new audit output file.');
    } else if (state.destinations.fact.mode === 'append') {
      ctx.explanation.push('Appends to an existing audit output file.');
    }
  }
}
