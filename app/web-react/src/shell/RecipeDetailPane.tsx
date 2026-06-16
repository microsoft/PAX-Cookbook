/**
 * Recipe detail pane for the Recipes workspace (center column).
 *
 * Read-only summary of the selected recipe in plain language. It defensively
 * reads a handful of well-known fields from the persisted recipe and shows only
 * the ones that resolve, so it never invents data.
 *
 * Its action area is arranged in three tiers: a primary "Bake (run)" hero; a
 * secondary peer row of equal-weight "Open Editor" / "Export" / "Duplicate"
 * controls (the Export (.pax) button delegates the actual export to the
 * workspace's pure handler); and a low-emphasis utility row with the
 * Advanced fly-in toggle and a danger-styled "Delete recipe" text link. A
 * transient export status note renders at the bottom of the actions block.
 *
 * Every action delegates to the workspace — Bake to the gated bake flow
 * (confirm modal + Windows Hello step-up + single startCook), Delete to the
 * workspace's delete flow (which removes any schedule first), and Export to the
 * workspace's client-side download handlers. The pane itself makes no broker
 * call: it runs no PAX and does not delete, rename, schedule, or export
 * directly.
 */
import type { ReactNode } from 'react';
import type { RecipeSummary } from '../host/brokerBridge';
import { formatModified } from './shellNav';
import { fullRecipeToState } from '../features/mini-kitchen/lib/fullRecipeToState';
import type {
  MiniKitchenRecipeState,
  AuthMode,
  RollupMode,
  StorageTier,
} from '../features/mini-kitchen/types';
import { RecipeStatusBadge, toneForRecipeStatus } from './StatusCard';
import {
  IconBook,
  IconCode,
  IconCopy,
  IconTrash,
  IconList,
  IconCalendar,
  IconFolder,
  IconKey,
  IconClock,
  IconInfoCircle,
  IconDownload,
} from './CookbookIllustrations';

interface RecipeDetailPaneProps {
  phase: 'idle' | 'loading' | 'loaded' | 'error';
  summary: RecipeSummary | null;
  recipe: Record<string, unknown> | null;
  onOpenEditor: () => void;
  onDuplicate: () => void;
  /**
   * Open the workspace's delete-confirmation flow for this recipe. The pane
   * never deletes directly; the workspace owns the broker call (and removes any
   * schedule first).
   */
  onDelete: () => void;
  /**
   * Toggles the Advanced details fly-in panel (command preview, permissions,
   * and support details) open or closed. The panel lives in the workspace so
   * it can push the columns left; this button only triggers it.
   */
  onOpenAdvanced: () => void;
  /**
   * True while the Advanced fly-in panel is open; drives the button's
   * active/selected (pressed) visual state.
   */
  advancedOpen: boolean;
  /** Start a gated bake of this saved recipe (the single startCook channel). */
  onBake: () => void;
  /** True when a saved recipe is selected and its detail has loaded. */
  canBake: boolean;
  /**
   * id → friendly Chef's Key name, so the summary shows the saved name instead
   * of the raw id. Only id + displayName are carried here (never a secret or
   * any other key field); an empty map simply falls back to the id.
   */
  chefKeyNames: ReadonlyMap<string, string>;
  /**
   * Build and download the selected recipe as a full `.pax` recipe. Delegated
   * to the workspace's pure export handler; the pane never builds the file.
   */
  onExportFull: () => void;
  /**
   * Transient inline note shown at the bottom of the actions block after an
   * export (a scrub summary on success or a bounded sentence on failure), or
   * null when there is nothing to show. Rendering it last keeps the action
   * buttons from reflowing when it appears or disappears.
   */
  exportStatus: { kind: 'success' | 'error'; text: string } | null;
}

/** Read a string at the first path that resolves to a non-empty string. */
function pick(recipe: Record<string, unknown> | null, paths: string[][]): string {
  if (!recipe) {
    return '';
  }
  for (const path of paths) {
    let cursor: unknown = recipe;
    let ok = true;
    for (const key of path) {
      if (cursor && typeof cursor === 'object' && key in (cursor as object)) {
        cursor = (cursor as Record<string, unknown>)[key];
      } else {
        ok = false;
        break;
      }
    }
    if (ok && typeof cursor === 'string' && cursor.trim().length > 0) {
      return cursor.trim();
    }
    if (ok && Array.isArray(cursor) && cursor.length > 0) {
      const labels = cursor.filter(v => typeof v === 'string') as string[];
      if (labels.length > 0) {
        return labels.join(', ');
      }
    }
  }
  return '';
}

interface DetailRow {
  icon: (p: { className?: string }) => ReactNode;
  label: string;
  value: string;
}

function buildRows(
  summary: RecipeSummary | null,
  recipe: Record<string, unknown> | null,
): DetailRow[] {
  const rows: DetailRow[] = [];

  const scope = pick(recipe, [
    ['query', 'activityTypes'],
    ['scope', 'activityTypes'],
    ['scope'],
    ['activityTypes'],
  ]);
  if (scope) {
    rows.push({ icon: IconList, label: 'Scope / Activity types', value: scope });
  }

  const dateRange = pick(recipe, [
    ['query', 'dateRange', 'label'],
    ['dateRange', 'label'],
    ['query', 'dateRange'],
    ['dateRange'],
  ]);
  if (dateRange) {
    rows.push({ icon: IconCalendar, label: 'Date range', value: dateRange });
  }

  const output = pick(recipe, [
    ['output', 'path'],
    ['destination', 'path'],
    ['destinations', 'local', 'path'],
    ['outputPath'],
  ]);
  if (output) {
    rows.push({ icon: IconFolder, label: 'Output destination', value: output });
  }

  const auth = pick(recipe, [
    ['auth', 'mode'],
    ['authentication', 'mode'],
    ['authMode'],
  ]);
  if (auth) {
    rows.push({ icon: IconKey, label: 'Auth mode', value: auth });
  }

  const updated = formatModified(summary?.['updatedAt']);
  if (updated) {
    rows.push({ icon: IconClock, label: 'Last updated', value: updated });
  }

  return rows;
}

// Display labels mirroring the Mini-Kitchen Review step (Step 7), so a saved
// recipe reads the same on the homepage summary as it does in the editor. These
// are presentation-only; the field MAPPING comes from fullRecipeToState (the
// builder's own inverse translator), never re-derived here.
const AUTH_MODE_LABELS: Record<AuthMode, string> = {
  WebLogin: 'Web login',
  DeviceCode: 'Device code',
  AppRegistrationSecret: 'App reg. secret',
  AppRegistrationCertificate: 'App reg. cert',
  ManagedIdentity: 'Managed identity',
};

const STORAGE_TIER_LABELS: Record<StorageTier, string> = {
  local: 'Local',
  sharepoint: 'SharePoint',
  fabric: 'Fabric',
};

const ROLLUP_LABELS: Record<RollupMode, string> = {
  none: '',
  rollup: 'Rollup only',
  'rollup-plus-raw': 'Rollup + raw',
};

// Short weekday names indexed 0 = Sun … 6 = Sat, matching the persisted
// `daysOfWeek` encoding the broker registrar uses (X7).
const SCHEDULE_DAY_SHORT: ReadonlyArray<string> = [
  'Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat',
];

/** Zero-pad an hour/minute to two digits for an HH:MM time label. */
function pad2(n: number): string {
  return n < 10 ? '0' + n : String(n);
}

/**
 * Read the recipe's persisted schedule block (X7) defensively and describe the
 * actual recurrence. The full recipe carries an optional `schedule` object
 * ({ enabled, recurrence: { kind, hour, minute, daysOfWeek? }, ... }); the
 * builder state does not, so this reads the raw recipe. It guards every nested
 * access, never throws, and never fetches — the block has no next-fire time, so
 * none is shown. Examples: "Daily at 14:30", "Weekly Mon/Wed/Fri at 09:00".
 */
function describeSchedule(recipe: Record<string, unknown> | null): string {
  if (!recipe || typeof recipe !== 'object') {
    return 'Not scheduled';
  }
  const schedule = recipe['schedule'];
  if (!schedule || typeof schedule !== 'object') {
    return 'Not scheduled';
  }
  if ((schedule as Record<string, unknown>)['enabled'] !== true) {
    return 'Not scheduled';
  }
  const recurrence = (schedule as Record<string, unknown>)['recurrence'];
  if (!recurrence || typeof recurrence !== 'object') {
    return 'Scheduled';
  }
  const rec = recurrence as Record<string, unknown>;
  const kindRaw = rec['kind'];
  const kind = typeof kindRaw === 'string' ? kindRaw : '';

  // Build an HH:MM time only when hour and minute are valid in-range integers;
  // otherwise the time is absent and we fall back to a kind-only label.
  const hourRaw = rec['hour'];
  const minuteRaw = rec['minute'];
  const hasTime =
    typeof hourRaw === 'number' &&
    Number.isInteger(hourRaw) &&
    hourRaw >= 0 &&
    hourRaw <= 23 &&
    typeof minuteRaw === 'number' &&
    Number.isInteger(minuteRaw) &&
    minuteRaw >= 0 &&
    minuteRaw <= 59;
  if (!hasTime) {
    return kind.length > 0 ? `Scheduled \u2014 ${kind}` : 'Scheduled';
  }
  const time = `${pad2(hourRaw as number)}:${pad2(minuteRaw as number)}`;

  if (kind === 'daily') {
    return `Daily at ${time}`;
  }
  if (kind === 'weekly') {
    const daysRaw = rec['daysOfWeek'];
    const dayNames: string[] = [];
    if (Array.isArray(daysRaw)) {
      // Render in Sun-first week order regardless of how the days were stored.
      for (let d = 0; d <= 6; d++) {
        if (daysRaw.includes(d)) {
          dayNames.push(SCHEDULE_DAY_SHORT[d]);
        }
      }
    }
    const days = dayNames.join('/');
    return days.length > 0 ? `Weekly ${days} at ${time}` : `Weekly at ${time}`;
  }
  return kind.length > 0 ? `Scheduled \u2014 ${kind}` : 'Scheduled';
}

/**
 * Human-readable data scope for the summary, mirroring the editor Step 7 scope
 * chips (Purview base label + activity types + user-info).
 */
function describeScope(
  state: MiniKitchenRecipeState,
  isUserInfoOnly: boolean,
): string {
  if (isUserInfoOnly) {
    return 'User info only';
  }
  const includeM365 = Boolean(state.query.includeM365Usage);
  const excludeCopilot = Boolean(state.query.excludeCopilotInteraction);
  let base: string;
  if (excludeCopilot && includeM365) {
    base = 'M365 usage';
  } else if (excludeCopilot) {
    base = 'No CopilotInteraction';
  } else if (includeM365) {
    base = 'Copilot + M365 usage';
  } else {
    base = 'CopilotInteraction';
  }
  const parts: string[] = [base];
  const activityTypes = state.processing.activityTypes ?? [];
  if (activityTypes.length > 0) {
    parts.push(activityTypes.join(', '));
  }
  if (state.query.includeUserInfo) {
    parts.push('User info included');
  }
  return parts.join(' \u00b7 ');
}

/**
 * Project the reconstructed builder state into the same per-step summary the
 * editor's Review step shows. Each row is added only when its value resolves,
 * so a saved recipe is described at a glance without opening the editor and
 * without inventing data. The recipe name is shown in the card header, so it is
 * not duplicated as a row here. Pure: no fetch, no PAX, display only.
 */
function summaryRowsFromState(
  state: MiniKitchenRecipeState,
  recipe: Record<string, unknown> | null,
  chefKeyNames: ReadonlyMap<string, string>,
): DetailRow[] {
  const rows: DetailRow[] = [];
  const isUserInfoOnly = state.query.mode === 'user-info-only';

  rows.push({
    icon: IconKey,
    label: 'Authentication',
    value: AUTH_MODE_LABELS[state.auth.mode] ?? state.auth.mode,
  });

  const chefKeyId = state.auth.chefKeyId?.trim();
  if (chefKeyId) {
    // Show the friendly Chef's Key name the user saved, resolved from the
    // chef-keys list. Only id + displayName are ever read here — never the
    // tenant/client id, certificate thumbprint, or upn. If the name has not
    // loaded yet or the key was deleted, fall back to the id so the row is
    // never blank.
    const friendly = chefKeyNames.get(chefKeyId);
    const chefKeyLabel =
      friendly && friendly.trim().length > 0 ? friendly.trim() : chefKeyId;
    rows.push({ icon: IconKey, label: 'Chef\u2019s Key', value: chefKeyLabel });
  }

  if (isUserInfoOnly) {
    rows.push({
      icon: IconCalendar,
      label: 'Date range',
      value: 'Not used for a user-info-only pull',
    });
  } else {
    const start = state.query.startDate?.trim() ?? '';
    const end = state.query.endDate?.trim() ?? '';
    if (start && end) {
      rows.push({
        icon: IconCalendar,
        label: 'Date range',
        value: `${start} \u2192 ${end}`,
      });
    }
  }

  const scope = describeScope(state, isUserInfoOnly);
  if (scope) {
    rows.push({ icon: IconList, label: 'Audit operations', value: scope });
  }

  const tierLabel =
    STORAGE_TIER_LABELS[state.destinations.fact.tier] ??
    state.destinations.fact.tier;
  const outPath = isUserInfoOnly
    ? state.destinations.userInfo.path?.trim()
    : state.destinations.fact.path?.trim();
  rows.push({
    icon: IconFolder,
    label: 'Output',
    value: outPath ? `${tierLabel} \u2014 ${outPath}` : tierLabel,
  });

  const rollupLabel = ROLLUP_LABELS[state.processing.rollup ?? 'none'];
  if (rollupLabel) {
    rows.push({ icon: IconList, label: 'Rollup', value: rollupLabel });
  }

  rows.push({
    icon: IconClock,
    label: 'Schedule',
    value: describeSchedule(recipe),
  });

  return rows;
}

export function RecipeDetailPane({
  phase,
  summary,
  recipe,
  onOpenEditor,
  onDuplicate,
  onDelete,
  onOpenAdvanced,
  advancedOpen,
  onBake,
  canBake,
  chefKeyNames,
  onExportFull,
  exportStatus,
}: RecipeDetailPaneProps) {
  if (phase === 'idle' || (!summary && phase !== 'loading')) {
    return (
      <section className="dvw-card dvw-detail dvw-detail--empty" aria-live="polite">
        <span className="dvw-detail__empty-icon" aria-hidden="true">
          <IconBook />
        </span>
        <p className="dvw-card__muted">
          Select a recipe to see its details, or create a new one.
        </p>
      </section>
    );
  }

  if (phase === 'loading') {
    return (
      <section className="dvw-card dvw-detail" aria-busy="true">
        <p className="dvw-card__muted" role="status">
          Loading recipe…
        </p>
      </section>
    );
  }

  if (phase === 'error') {
    return (
      <section className="dvw-card dvw-detail">
        <p className="dvw-card__muted" role="status">
          That recipe could not be opened. Refresh the list and try again.
        </p>
      </section>
    );
  }

  const name =
    summary && typeof summary.name === 'string' && summary.name.trim().length > 0
      ? summary.name.trim()
      : 'Untitled recipe';
  const tone = toneForRecipeStatus(summary?.['status']);
  // Project the persisted recipe through the builder's own inverse translator
  // so the homepage summary matches the editor's Review step exactly. If the
  // recipe cannot be reconstructed, fall back to the defensive field picks so
  // the card still shows what it can without crashing.
  const projection = fullRecipeToState(recipe);
  const rows =
    projection.ok && projection.state
      ? summaryRowsFromState(projection.state, recipe, chefKeyNames)
      : buildRows(summary, recipe);

  return (
    <section className="dvw-card dvw-detail" aria-labelledby="dvw-detail-h">
      <header className="dvw-detail__head">
        <span className="dvw-detail__icon" aria-hidden="true">
          <IconBook />
        </span>
        <h2 id="dvw-detail-h" className="dvw-detail__name">
          {name}
        </h2>
        <RecipeStatusBadge tone={tone} />
      </header>

      {rows.length > 0 ? (
        <dl className="dvw-detail__rows">
          {rows.map(row => {
            const Icon = row.icon;
            return (
              <div key={row.label} className="dvw-detail__row">
                <span className="dvw-detail__row-icon" aria-hidden="true">
                  <Icon />
                </span>
                <dt className="dvw-detail__row-label">{row.label}</dt>
                <dd className="dvw-detail__row-value">{row.value}</dd>
              </div>
            );
          })}
        </dl>
      ) : (
        <p className="dvw-card__muted">
          Open the editor to view and change every detail of this recipe.
        </p>
      )}

      <div className="dvw-detail__actions">
        <button
          type="button"
          className="dvw-btn dvw-btn--bake dvw-detail__bake"
          onClick={onBake}
          disabled={!canBake}
          aria-disabled={!canBake}
          title={
            canBake
              ? 'Bake runs this saved recipe on this PC.'
              : 'Select a saved recipe to bake.'
          }
        >
          <span>Bake (run)</span>
        </button>

        <div className="dvw-detail__secondary">
          <button
            type="button"
            className="dvw-btn dvw-detail__secondary-btn"
            onClick={onOpenEditor}
          >
            <IconCode className="dvw-btn__icon" />
            <span>Open Editor</span>
          </button>
          <button
            type="button"
            className="dvw-btn dvw-detail__secondary-btn"
            onClick={onExportFull}
          >
            <IconDownload className="dvw-btn__icon" />
            <span>Export (.pax)</span>
          </button>
          <button
            type="button"
            className="dvw-btn dvw-detail__secondary-btn"
            onClick={onDuplicate}
          >
            <IconCopy className="dvw-btn__icon" />
            <span>Duplicate</span>
          </button>
        </div>

        <div className="dvw-detail__utility">
          <button
            type="button"
            className={
              'dvw-detail__util-link' +
              (advancedOpen ? ' dvw-detail__util-link--active' : '')
            }
            onClick={onOpenAdvanced}
            aria-pressed={advancedOpen}
          >
            <IconInfoCircle className="dvw-detail__util-icon" />
            <span>Advanced</span>
          </button>
          <button
            type="button"
            className="dvw-detail__delete-link"
            onClick={onDelete}
          >
            <IconTrash className="dvw-detail__util-icon" />
            <span>Delete recipe</span>
          </button>
        </div>

        {exportStatus ? (
          <p
            className={
              'dvw-export-status' +
              (exportStatus.kind === 'error' ? ' dvw-export-status--error' : '')
            }
            role={exportStatus.kind === 'error' ? 'alert' : 'status'}
          >
            {exportStatus.text}
          </p>
        ) : null}
      </div>

      <div className="dvw-detail__tip">
        <span className="dvw-detail__tip-icon" aria-hidden="true">
          <IconInfoCircle />
        </span>
        <p className="dvw-detail__tip-text">
          Tip: Use Check Readiness to verify permissions and output folder before
          running.
        </p>
      </div>
    </section>
  );
}
