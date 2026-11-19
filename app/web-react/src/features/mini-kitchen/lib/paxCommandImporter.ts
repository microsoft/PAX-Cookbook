/**
 * Reverse parser: a pasted PAX command line -> editable recipe state.
 *
 * This is the inverse of `commandRenderer.ts`. It takes a pasted PAX command
 * (optionally wrapped in a `pwsh -NoProfile -File ...` invocation), tokenizes
 * it the same way the Advanced Arguments box does, and maps each recognized
 * switch back onto the editable recipe state so the result can be reviewed and
 * loaded into the builder as a draft.
 *
 * It never runs the command, never spawns PowerShell, never reads or writes a
 * file, and never persists anything. It only reads switches.
 *
 * Design rules (kept deliberately aligned with the forward renderer):
 *   - Reuse `tokenizeAdvancedArgs` so quoting/splitting matches the rest of the
 *     builder.
 *   - Reuse the switch catalog (`getSwitchDefinition`,
 *     `REMOVED_OR_UNSUPPORTED_SWITCHES`) so the allowed/removed lists never
 *     drift from the renderer. Switch names are resolved case-insensitively
 *     because PowerShell switch names are case-insensitive.
 *   - Unknown switches are DROPPED and reported as "not applied" — never
 *     applied blindly.
 *   - Removed/unsupported switches are DROPPED and reported with their
 *     user-facing label (never the internal identifier).
 *   - Known switches that have no guided field (operator knobs, Force, etc.)
 *     are preserved verbatim into `advanced.extraArguments` so nothing is lost.
 *   - A detected script path (`-File`, `-f`, or a bare `*.ps1` token) is shown
 *     only as command provenance. PAX Cookbook runs recipes with its managed,
 *     verified PAX engine, so the pasted path never becomes the recipe's engine
 *     path and never counts as a recognized PAX switch on its own.
 *   - Secrets are rejected up front. The parser never echoes a secret value.
 *   - Combine-output follows the SAME rule the builder/renderer use.
 */

import {
  PAX_SWITCH_CATALOG,
  REMOVED_OR_UNSUPPORTED_SWITCHES,
} from '../data/pax-switch-catalog';
import type {
  AuthMode,
  MiniKitchenRecipeState,
  OutputCombineMode,
  PaxSwitchDefinition,
  PromptFilter,
  RollupMode,
  StorageTier,
} from '../types';
import { ADVANCED_OPTIONS, parseAdvancedArguments, tokenizeAdvancedArgs, type PaxParameter } from './advancedArgs';
import { createDefaultMiniKitchenRecipe } from './defaultRecipe';
import { normalizeRecipe } from './normalizeRecipe';
import { scanForSecrets } from './secretScanner';

/** One recognized switch that was mapped onto a recipe field. */
export interface AppliedSwitch {
  /** Switch name without the leading hyphen, e.g. `"StartDate"`. */
  switchName: string;
  /** Friendly label for the recipe field this switch updated. */
  fieldLabel: string;
  /** Safe, human-readable summary of what was applied (never a secret). */
  detail: string;
}

/** A switch that was parsed but intentionally not applied. */
export interface NotAppliedSwitch {
  /** Safe display label. For removed switches this is the user-facing name. */
  label: string;
  /** Why it was dropped. */
  kind: 'unknown' | 'unsupported';
}

export interface PaxCommandParseResult {
  /** True when the text was recognized as a PAX command and produced state. */
  ok: boolean;
  /**
   * When `ok` is false, a friendly reason (empty input, no recognized PAX
   * switches, or a secret was detected). Never contains a secret value.
   */
  rejectedReason?: string;
  /** The resulting recipe state. Present only when `ok` is true. */
  state?: MiniKitchenRecipeState;
  /** Recognized switches and the fields they updated, in input order. */
  applied: readonly AppliedSwitch[];
  /** Switches that were parsed but dropped (unknown or unsupported). */
  notApplied: readonly NotAppliedSwitch[];
  /** Informational notes (e.g. a detected non-local storage tier). */
  notes: readonly string[];
}

// -----------------------------------------------------------------------------
// Public entry point
// -----------------------------------------------------------------------------

export function parsePaxCommand(input: string): PaxCommandParseResult {
  const text = (input ?? '').trim();

  if (text.length > 131072 || text.includes('\0')) return reject('The command is too large or contains unsupported characters.');

  if (!text) {
    return reject('Paste a PAX command above, then run the import.');
  }

  // Reject obvious credential shapes before doing anything else. We keep only
  // strong signals (keyword=value pairs and bearer tokens) so legitimate
  // GUIDs, certificate thumbprints, and long file paths are not flagged.
  const secretHits = scanForSecrets(text).filter(
    m => m.id.endsWith('-pair') || m.id === 'bearer-token',
  );
  if (secretHits.length > 0) {
    return reject(
      'This command appears to contain a secret (such as a client secret, password, or token). ' +
        'PAX Cookbook never stores secrets — remove the secret value and source it from an ' +
        'environment variable at runtime, then import again.',
    );
  }

  // Normalize PowerShell line continuations (backtick + newline) and stray
  // newlines into spaces so the tokenizer sees one flat command line.
  const flattened = text
    .replace(/`\r?\n/g, ' ')
    .replace(/\r?\n/g, ' ')
    .trim();

  let typed: readonly PaxParameter[] | undefined;
  if (/\s-Command\b/i.test(flattened)) {
    try { typed = parseTypedProjection(text); }
    catch { return reject('This command is not a supported literal PAX command. Import a .pax or .paxlite recipe instead.'); }
  }
  const typedByName = new Map(typed?.map(parameter => [parameter.name, parameter]));
  const isAdvanced = (parameter: PaxParameter) => ADVANCED_OPTIONS.some(([name]) => name === parameter.name) && !(parameter.name === 'CombineOutput' && parameter.value === true);
  const tokens = typed
    ? typed.filter(parameter => !isAdvanced(parameter)).map(parameter => ({ raw: `-${parameter.name}`, isSwitch: true, switchName: parameter.name }))
    : tokenizeAdvancedArgs(flattened);

  const state = createDefaultMiniKitchenRecipe();
  // Clear the default-stamped name; command import names happen in the modal.
  state.identity.name = '';

  const applied: AppliedSwitch[] = [];
  const notApplied: NotAppliedSwitch[] = [];
  const notes: string[] = [];
  const advancedTokens: string[] = typed?.filter(isAdvanced).map(parameter => `-${parameter.name}:${typeof parameter.value === 'boolean' ? (parameter.value ? '$true' : '$false') : parameter.value}`) ?? [];

  let recognizedCount = advancedTokens.length;
  let sawClientSecret = false;
  let sawScriptPath = false;
  // Track raw flags so dependent decisions (auth mode, combine output) can be
  // resolved after the full pass.
  let sawCombineOutput = false;
  let authEnum: string | undefined;
  let sawCertThumbprint = false;
  const seen = new Set<string>();

  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i]!;

    // --- Invocation wrappers: skip host pieces, note the script path. --------
    if (!token.isSwitch) {
      const bare = unquote(token.raw);
      if (/\.ps1$/i.test(bare)) {
        // Provenance only — never the recipe's engine path, never a recognized
        // switch on its own.
        sawScriptPath = true;
      }
      // Anything else here (pwsh, &, stray values) is ignored.
      continue;
    }

    const name = token.switchName ?? token.raw.slice(1);
    if (seen.has(name.toLowerCase())) return reject('Duplicate command options are not supported.');
    seen.add(name.toLowerCase());

    // PowerShell host switches that wrap the PAX invocation.
    if (HOST_FLAG_SWITCHES.has(name.toLowerCase())) {
      continue;
    }
    if (HOST_VALUE_SWITCHES.has(name.toLowerCase())) {
      // Consume one value token (e.g. -ExecutionPolicy Bypass).
      i = skipValueRun(tokens, i).end;
      continue;
    }
    if (FILE_SWITCHES.has(name.toLowerCase())) {
      // Consume the path value but keep it only as provenance.
      const run = collectValueRun(tokens, i);
      if (run.tokens.length > 0) {
        sawScriptPath = true;
      }
      i = run.end;
      continue;
    }

    // --- Removed / unsupported PAX switches. ---------------------------------
    const removed = REMOVED_OR_UNSUPPORTED_SWITCHES.find(
      s => s.name.toLowerCase() === name.toLowerCase(),
    );
    if (removed) {
      notApplied.push({ label: removed.userFacingName ?? removed.name, kind: 'unsupported' });
      const def = resolveSwitchDefinition(removed.name);
      if (def && def.kind !== 'flag') {
        i = skipValueRun(tokens, i).end;
      }
      continue;
    }

    // --- Secret-bearing switches: reject and never store/echo the value. -----
    if (SECRET_SWITCHES.has(name.toLowerCase())) {
      sawClientSecret = true;
      // Advance past the value token(s) without recording them anywhere.
      i = collectValueRun(tokens, i).end;
      continue;
    }

    // --- Known PAX switches. -------------------------------------------------
    const def = resolveSwitchDefinition(name);
    if (!def) {
      notApplied.push({ label: name, kind: 'unknown' });
      // We cannot know the arity of an unknown switch; leave following tokens
      // for the loop to skip as stray values.
      continue;
    }

    recognizedCount++;

    // Value run for non-flag switches.
    const typedParameter = typedByName.get(def.name);
    const run = typed || def.kind === 'flag' ? null : collectValueRun(tokens, i);
    const rawValue = typedParameter && typeof typedParameter.value === 'string'
      ? `'${typedParameter.value.replace(/'/g, "''")}'`
      : run ? joinRaw(run.tokens) : '';
    const listValue = () => typedParameter?.type === 'string[]' ? [...typedParameter.value as readonly string[]] : splitListValue(rawValue);
    if (run) {
      i = run.end;
    }

    switch (def.name) {
      case 'StartDate': {
        const v = unquote(rawValue);
        state.query.startDate = v;
        applied.push(mk('StartDate', 'Start date (Step 3)', v));
        break;
      }
      case 'EndDate': {
        const v = unquote(rawValue);
        state.query.endDate = v;
        applied.push(mk('EndDate', 'End date (Step 3)', v));
        break;
      }
      case 'IncludeM365Usage': {
        state.query.includeM365Usage = true;
        applied.push(mk('IncludeM365Usage', 'M365 usage bundle (Step 3)', 'On'));
        break;
      }
      case 'ExcludeCopilotInteraction': {
        state.query.excludeCopilotInteraction = true;
        applied.push(mk('ExcludeCopilotInteraction', 'Exclude Copilot interaction (Step 3)', 'On'));
        break;
      }
      case 'IncludeUserInfo': {
        state.query.includeUserInfo = true;
        applied.push(mk('IncludeUserInfo', 'Include user info (Step 3)', 'On'));
        break;
      }
      case 'UserInfoFile': {
        const v = unquote(rawValue);
        state.query.userInfoFile = v;
        state.query.includeUserInfo = true;
        noteTier(notes, detectTier(v));
        applied.push(mk('UserInfoFile', 'Bring your own directory (Step 3)', `Directory file -> ${v}`));
        break;
      }
      case 'OnlyUserInfo': {
        state.query.mode = 'user-info-only';
        state.query.includeUserInfo = true;
        state.query.onlyUserInfo = true;
        state.ingredients = { preset: 'userInfoOnly' };
        applied.push(mk('OnlyUserInfo', 'Data scope (Step 3)', 'User info only'));
        break;
      }
      case 'IncludeAgent365Info': {
        state.query.includeAgent365Info = true;
        applied.push(mk('IncludeAgent365Info', 'Microsoft Agent 365 (Step 3)', 'On'));
        break;
      }
      case 'OnlyAgent365Info': {
        state.query.mode = 'agent365-only';
        state.query.onlyAgent365Info = true;
        state.ingredients = { preset: 'agent365CatalogOnly' };
        applied.push(mk('OnlyAgent365Info', 'Data scope (Step 3)', 'Microsoft Agent 365 only'));
        break;
      }
      case 'ActivityTypes': {
        const list = listValue();
        state.processing.activityTypes = list;
        applied.push(mk('ActivityTypes', 'Activity types (Step 3)', list.join(', ')));
        break;
      }
      case 'UserIds': {
        const list = listValue();
        state.processing.userIds = list;
        applied.push(mk('UserIds', 'User IDs (Step 3)', list.join(', ')));
        break;
      }
      case 'GroupNames': {
        const list = listValue();
        state.processing.groupNames = list;
        applied.push(mk('GroupNames', 'Group names (Step 3)', list.join(', ')));
        break;
      }
      case 'AgentId': {
        const list = listValue();
        state.processing.agentFilter = { mode: 'ids-only', ids: list };
        applied.push(mk('AgentId', 'Agent filter (Step 3)', `Specific IDs: ${list.join(', ')}`));
        break;
      }
      case 'AgentsOnly': {
        state.processing.agentFilter = { mode: 'agents-only' };
        applied.push(mk('AgentsOnly', 'Agent filter (Step 3)', 'Agents only'));
        break;
      }
      case 'ExcludeAgents': {
        state.processing.agentFilter = { mode: 'exclude-agents' };
        applied.push(mk('ExcludeAgents', 'Agent filter (Step 3)', 'Exclude agents'));
        break;
      }
      case 'PromptFilter': {
        const v = unquote(rawValue);
        if (isPromptFilter(v)) {
          state.processing.promptFilter = v;
          applied.push(mk('PromptFilter', 'Prompt filter (Step 3)', v));
        } else {
          notApplied.push({ label: `PromptFilter ${v}`.trim(), kind: 'unknown' });
          recognizedCount--;
        }
        break;
      }
      case 'Rollup': {
        state.processing.rollup = 'rollup' as RollupMode;
        applied.push(mk('Rollup', 'Rollup (Step 3)', 'Rollup'));
        break;
      }
      case 'RollupPlusRaw': {
        state.processing.rollup = 'rollup-plus-raw' as RollupMode;
        applied.push(mk('RollupPlusRaw', 'Rollup (Step 3)', 'Rollup plus raw'));
        break;
      }
      case 'CombineOutput': {
        sawCombineOutput = true;
        applied.push(mk('CombineOutput', 'Output combine mode (Step 4)', 'Combined'));
        break;
      }
      case 'Deidentify': {
        state.processing.deidentify = true;
        applied.push(mk(def.name, 'Deidentify', 'On'));
        break;
      }
      case 'Dashboard': {
        const value = unquote(rawValue).toLowerCase();
        if (value !== 'aio' && value !== 'aibv') return reject('Select a supported dashboard value.');
        state.processing.dashboard = value;
        applied.push(mk(def.name, 'Dashboard', value));
        break;
      }
      case 'FillerLabel': {
        const value = unquote(rawValue);
        if (value !== 'Self' && value !== 'RepeatManager' && value !== 'Fixed') return reject('Select a supported filler label.');
        state.processing.fillerLabel = value;
        applied.push(mk(def.name, 'Filler label', value));
        break;
      }
      case 'FillerLabelText': {
        state.processing.fillerLabelText = unquote(rawValue);
        applied.push(mk(def.name, 'Filler label text', 'Imported'));
        break;
      }
      case 'OutputPath': {
        const v = unquote(rawValue);
        const tier = detectTier(v);
        state.destinations.fact = { mode: 'write-new', tier, path: v };
        noteTier(notes, tier);
        applied.push(mk('OutputPath', 'Audit output (Step 4)', `Write new -> ${v}`));
        break;
      }
      case 'AppendFile': {
        const v = unquote(rawValue);
        const tier = detectTier(v);
        state.destinations.fact = { mode: 'append', tier, path: v };
        noteTier(notes, tier);
        applied.push(mk('AppendFile', 'Audit output (Step 4)', `Append -> ${v}`));
        break;
      }
      case 'OutputPathUserInfo': {
        const v = unquote(rawValue);
        state.destinations.userInfo = { mode: 'write-new', path: v };
        applied.push(mk('OutputPathUserInfo', 'User-info output (Step 4)', `Write new -> ${v}`));
        break;
      }
      case 'AppendUserInfo': {
        const v = unquote(rawValue);
        state.destinations.userInfo = { mode: 'append', path: v };
        applied.push(mk('AppendUserInfo', 'User-info output (Step 4)', `Append -> ${v}`));
        break;
      }
      case 'OutputPathAgent365Info': {
        const v = unquote(rawValue);
        const tier = detectTier(v);
        state.destinations.agent365 = { mode: 'write-new', path: v };
        noteTier(notes, tier);
        applied.push(mk('OutputPathAgent365Info', 'Agent 365 output (Step 4)', `Write new -> ${v}`));
        break;
      }
      case 'AppendAgent365Info': {
        const v = unquote(rawValue);
        const tier = detectTier(v);
        state.destinations.agent365 = { mode: 'append', path: v };
        noteTier(notes, tier);
        applied.push(mk('AppendAgent365Info', 'Agent 365 output (Step 4)', `Append -> ${v}`));
        break;
      }
      case 'Auth': {
        authEnum = unquote(rawValue);
        // Resolved after the full pass (depends on the cert thumbprint).
        break;
      }
      case 'TenantId': {
        const v = unquote(rawValue);
        state.auth.tenantId = v;
        applied.push(mk('TenantId', 'Authentication (Step 4)', `Tenant ID ${v}`));
        break;
      }
      case 'ClientId': {
        const v = unquote(rawValue);
        state.auth.clientId = v;
        applied.push(mk('ClientId', 'Authentication (Step 4)', `Client ID ${v}`));
        break;
      }
      case 'ClientCertificateThumbprint': {
        const v = unquote(rawValue);
        state.auth.certificateThumbprint = v;
        sawCertThumbprint = true;
        applied.push(mk('ClientCertificateThumbprint', 'Authentication (Step 4)', 'Certificate thumbprint set'));
        break;
      }
      default: {
        // Known operator knob with no guided field — preserve verbatim into
        // Advanced arguments so nothing is lost on round-trip.
        advancedTokens.push(`-${def.name}`);
        if (rawValue) {
          advancedTokens.push(rawValue);
        }
        applied.push(
          mk(def.name, 'Advanced arguments (Step 5)', rawValue ? `${def.name} ${rawValue}` : `${def.name} (on)`),
        );
        break;
      }
    }
  }

  if (sawClientSecret) {
    return reject(
      'This command includes a client secret. PAX Cookbook never stores secrets — remove the ' +
        '-ClientSecret value and source it from an environment variable at runtime, then import again.',
    );
  }

  if (notApplied.length > 0) return reject('Unsupported command options were found. Remove them before importing.');
  if (recognizedCount === 0) {
    return {
      ok: false,
      rejectedReason:
        'PAX Cookbook did not recognize any PAX switches in that text. Double-check that you ' +
        'pasted a PAX command line (script path plus -Switch values).',
      applied: [],
      notApplied,
      notes: [],
    };
  }

  // Surface the detected script path as provenance only — it is never the
  // recipe's engine path. Shown after the real switches so it reads as context.
  if (sawScriptPath) {
    applied.push(
      mk(
        'File',
        'Command provenance',
        'Script path detected; PAX Cookbook uses the managed, verified PAX engine.',
      ),
    );
    notes.push(
      'A script path was detected and kept only as command provenance. PAX Cookbook runs recipes ' +
        'with its managed, verified PAX engine, so the pasted path is not used.',
    );
  }

  // Resolve auth mode now that we know whether a cert thumbprint was present.
  resolveAuth(state, authEnum, sawCertThumbprint, advancedTokens, applied);

  // Resolve combine output using the existing builder rule: with at most one
  // activity type, combined is the default; the -CombineOutput flag forces
  // combined; otherwise multiple activity types without the flag means
  // separate.
  state.processing.outputCombineMode = resolveCombineMode(
    sawCombineOutput,
    state.processing.activityTypes ?? [],
    state.query.includeM365Usage === true,
  );

  if (advancedTokens.length > 0) {
    const existing = (state.advanced.extraArguments ?? '').trim();
    state.advanced.extraArguments = [existing, advancedTokens.join(' ')]
      .filter(Boolean)
      .join(' ')
      .trim();
  }

  // Previous-day post-pass: an audit command with neither -StartDate nor
  // -EndDate is the deliberate "previous full UTC day" configuration (absence
  // is the signal). Mark it so the builder shows Previous day rather than an
  // empty, blocking custom range. User-info-only commands don't use dates.
  const sawStartDate = applied.some(a => a.switchName === 'StartDate');
  const sawEndDate = applied.some(a => a.switchName === 'EndDate');
  if (state.query.mode === 'audit-query' && !sawStartDate && !sawEndDate) {
    state.query.dateMode = 'previous-day';
    delete state.query.startDate;
    delete state.query.endDate;
    notes.push(
      'No -StartDate or -EndDate in the command — imported as Previous day mode (PAX queries the previous full UTC day).',
    );
  }

  const normalized = normalizeRecipe(state);
  const advancedError = parseAdvancedArguments(normalized.advanced.extraArguments ?? '').error;
  if (advancedError) return reject(advancedError);

  return {
    ok: true,
    state: normalized,
    applied,
    notApplied,
    notes: dedupe(notes),
  };
}

function parseTypedProjection(command: string): readonly PaxParameter[] {
  let source = command;
  let position = 0;
  const fail = (): never => { throw new Error('Invalid literal command.'); };
  const take = (literal: string) => {
    if (!source.startsWith(literal, position)) fail();
    position += literal.length;
  };
  const quoted = (): string => {
    take("'");
    let value = '';
    while (position < source.length) {
      const character = source[position++]!;
      if (character !== "'") value += character;
      else if (source[position] === "'") { value += "'"; position++; }
      else return value;
    }
    return fail();
  };
  const stringValue = (): string => {
    if (source[position] !== '(') return quoted();
    take('(');
    let value = quoted();
    while (source[position] === '+') {
      take('+[char]');
      const number = /^\d+/.exec(source.slice(position))?.[0] ?? fail();
      position += number.length;
      const code = Number(number);
      if (code < 8216 || code > 8223) fail();
      take('+');
      value += String.fromCharCode(code) + quoted();
    }
    take(')');
    return value;
  };
  take('pwsh -NoProfile -NoLogo -Command ');
  const expression = stringValue();
  if (position !== source.length) fail();
  source = expression;
  position = 0;
  take('$paxParameters=@{');
  const parameters: PaxParameter[] = [];
  const names = new Set<string>();
  while (source[position] !== '}') {
    if (parameters.length >= 128) fail();
    const name = quoted();
    if (names.has(name.toLowerCase())) fail();
    names.add(name.toLowerCase());
    take('=');
    let parameter: PaxParameter;
    if (source.startsWith('[string[]]@(', position)) {
      take('[string[]]@(');
      const value: string[] = [];
      while (source[position] !== ')') {
        value.push(stringValue());
        if (source[position] !== ',') break;
        take(',');
      }
      take(')');
      parameter = { name, type: 'string[]', value };
    } else if (source.startsWith('$true', position) || source.startsWith('$false', position)) {
      const value = source.startsWith('$true', position);
      take(value ? '$true' : '$false');
      parameter = { name, type: 'bool', value };
    } else if (source[position] === '[') {
      const type = source.startsWith('[int]', position) ? 'int' : 'double';
      take(`[${type}]`);
      const literal = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?/.exec(source.slice(position))?.[0] ?? fail();
      position += literal.length;
      parameter = { name, type, value: Number(literal) };
    } else parameter = { name, type: 'string', value: stringValue() };
    const advanced = ADVANCED_OPTIONS.find(([optionName]) => optionName === name);
    const definition = resolveSwitchDefinition(name);
    if (advanced) {
      if (parameter.type !== advanced[1] || parseAdvancedArguments(`-${name}:${parameter.value}`).error) fail();
    } else if (!definition || definition.name !== name ||
      (definition.kind === 'flag' ? parameter.type !== 'bool' || parameter.value !== true :
        definition.kind === 'string-list' ? parameter.type !== 'string[]' : parameter.type !== 'string')) fail();
    parameters.push(parameter);
    if (source[position] !== ';') break;
    take(';');
  }
  take('}; & ');
  stringValue();
  take(' @paxParameters');
  if (position !== source.length) fail();
  return parameters;
}

// -----------------------------------------------------------------------------
// Auth resolution
// -----------------------------------------------------------------------------

function resolveAuth(
  state: MiniKitchenRecipeState,
  authEnum: string | undefined,
  sawCertThumbprint: boolean,
  advancedTokens: string[],
  applied: AppliedSwitch[],
): void {
  if (authEnum === undefined) {
    // No -Auth switch. If a thumbprint was supplied, the app registration is
    // certificate-based; otherwise leave the default WebLogin.
    if (sawCertThumbprint) {
      state.auth.mode = 'AppRegistrationCertificate';
    }
    return;
  }

  const value = authEnum.trim();
  const lc = value.toLowerCase();
  let mode: AuthMode | undefined;
  if (lc === 'weblogin') mode = 'WebLogin';
  else if (lc === 'devicecode') mode = 'DeviceCode';
  else if (lc === 'managedidentity') mode = 'ManagedIdentity';
  else if (lc === 'appregistration') {
    mode = sawCertThumbprint ? 'AppRegistrationCertificate' : 'AppRegistrationSecret';
  }

  if (mode) {
    state.auth.mode = mode;
    applied.push(mk('Auth', 'Authentication (Step 4)', authModeLabel(mode)));
    return;
  }

  // A valid PAX enum value that the builder has no guided control for
  // (e.g. Credential, Silent). Preserve it verbatim in Advanced arguments.
  advancedTokens.push('-Auth', value);
  applied.push(mk('Auth', 'Advanced arguments (Step 5)', `Auth ${value}`));
}

function authModeLabel(mode: AuthMode): string {
  switch (mode) {
    case 'WebLogin':
      return 'Web login';
    case 'DeviceCode':
      return 'Device code';
    case 'ManagedIdentity':
      return 'Managed identity';
    case 'AppRegistrationSecret':
      return 'App registration (secret)';
    case 'AppRegistrationCertificate':
      return 'App registration (certificate)';
    default:
      return mode;
  }
}

// -----------------------------------------------------------------------------
// Combine-output resolution (mirrors commandRenderer combine-output rule)
// -----------------------------------------------------------------------------

function resolveCombineMode(
  sawFlag: boolean,
  activityTypes: readonly string[],
  includeM365Usage: boolean,
): OutputCombineMode {
  if (sawFlag) {
    return 'combined';
  }
  // PAX auto-combines when the M365 usage bundle is bound.
  if (includeM365Usage) {
    return 'combined';
  }
  // With more than one activity type and no flag, separate files were chosen.
  if (activityTypes.length > 1) {
    return 'separate';
  }
  // One activity type (or none): combined is the default.
  return 'combined';
}

// -----------------------------------------------------------------------------
// Tier detection
// -----------------------------------------------------------------------------

function detectTier(path: string): StorageTier {
  const p = path.toLowerCase();
  if (
    p.includes('sharepoint.com') ||
    p.includes('-my.sharepoint') ||
    p.includes('/personal/') ||
    p.includes('/sites/')
  ) {
    return 'sharepoint';
  }
  if (
    p.includes('onelake') ||
    p.includes('fabric.microsoft.com') ||
    p.includes('.lakehouse') ||
    p.includes('/lakehouse/')
  ) {
    return 'fabric';
  }
  return 'local';
}

function noteTier(notes: string[], tier: StorageTier): void {
  if (tier === 'sharepoint') {
    notes.push('Detected a SharePoint output path and set the storage tier to SharePoint.');
  } else if (tier === 'fabric') {
    notes.push('Detected a Fabric output path and set the storage tier to Fabric.');
  }
}

// -----------------------------------------------------------------------------
// Switch catalog (case-insensitive resolution)
// -----------------------------------------------------------------------------

/**
 * Resolve a switch definition by name, case-insensitively. PowerShell switch
 * names are case-insensitive, so a pasted `-startdate` must resolve to the
 * `StartDate` definition. Returns `undefined` for unknown names.
 */
function resolveSwitchDefinition(name: string): PaxSwitchDefinition | undefined {
  const lc = name.toLowerCase();
  return PAX_SWITCH_CATALOG.find(s => s.name.toLowerCase() === lc);
}

// -----------------------------------------------------------------------------
// Token helpers
// -----------------------------------------------------------------------------

const HOST_FLAG_SWITCHES = new Set([
  'noprofile',
  'nologo',
  'noninteractive',
  'mta',
  'sta',
  'noexit',
]);

const HOST_VALUE_SWITCHES = new Set([
  'executionpolicy',
  'windowstyle',
  'version',
  'outputformat',
  'inputformat',
  'configurationname',
]);

const FILE_SWITCHES = new Set(['file', 'f']);

/** Switches whose value is a credential — rejected, never stored or echoed. */
const SECRET_SWITCHES = new Set(['clientsecret', 'password', 'pwd', 'secret']);

interface ValueRun {
  tokens: { raw: string }[];
  /** Index of the LAST consumed token (loop should continue from end). */
  end: number;
}

/** Collect the run of non-switch value tokens following index `i`. */
function collectValueRun(
  tokens: readonly { raw: string; isSwitch: boolean }[],
  i: number,
): ValueRun {
  const out: { raw: string }[] = [];
  let j = i + 1;
  while (j < tokens.length && !tokens[j]!.isSwitch) {
    out.push({ raw: tokens[j]!.raw });
    j++;
  }
  return { tokens: out, end: j - 1 < i ? i : j - 1 };
}

/** Like {@link collectValueRun} but only consumes a single value token. */
function skipValueRun(
  tokens: readonly { raw: string; isSwitch: boolean }[],
  i: number,
): ValueRun {
  if (i + 1 < tokens.length && !tokens[i + 1]!.isSwitch) {
    return { tokens: [{ raw: tokens[i + 1]!.raw }], end: i + 1 };
  }
  return { tokens: [], end: i };
}

function joinRaw(parts: readonly { raw: string }[]): string {
  return parts.map(p => p.raw).join(' ').trim();
}

/** Strip a single layer of matching surrounding quotes and unescape doubles. */
function unquote(value: string): string {
  const v = value.trim();
  if (v.length >= 2) {
    const first = v[0];
    const last = v[v.length - 1];
    if (first === '"' && last === '"') {
      return v.slice(1, -1).replace(/""/g, '"');
    }
    if (first === "'" && last === "'") {
      return v.slice(1, -1).replace(/''/g, "'");
    }
  }
  return v;
}

/** Split a comma-joined list value, respecting quoted segments. */
function splitListValue(value: string): string[] {
  const items: string[] = [];
  let current = '';
  let quote: '"' | "'" | null = null;
  for (let i = 0; i < value.length; i++) {
    const ch = value[i]!;
    if (quote) {
      if (ch === quote) {
        quote = null;
      }
      current += ch;
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      current += ch;
      continue;
    }
    if (ch === ',') {
      items.push(current);
      current = '';
      continue;
    }
    current += ch;
  }
  items.push(current);
  return items
    .map(item => unquote(item.trim()))
    .map(item => item.trim())
    .filter(item => item.length > 0);
}

function isPromptFilter(value: string): value is PromptFilter {
  return value === 'Prompt' || value === 'Response' || value === 'Both' || value === 'Null';
}

function mk(switchName: string, fieldLabel: string, detail: string): AppliedSwitch {
  return { switchName, fieldLabel, detail };
}

function reject(reason: string): PaxCommandParseResult {
  return { ok: false, rejectedReason: reason, applied: [], notApplied: [], notes: [] };
}

function dedupe(values: readonly string[]): string[] {
  return Array.from(new Set(values));
}
