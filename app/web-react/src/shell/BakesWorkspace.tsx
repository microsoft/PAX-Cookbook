/**
 * Bakes — a customer-ready bake history / results surface.
 *
 * Bakes is where someone reviews recipes that have been baked: the history of
 * recorded bakes on the left, and the selected bake's detail, analytics,
 * outputs, and log in the center. The history, detail, and log
 * are read straight from the local broker's read-only cook-read routes
 * (GET /api/v1/cooks, /api/v1/cooks/{id}, /api/v1/cooks/{id}/log), so every row
 * shown is a real recorded bake — the surface never fabricates a record.
 *
 * This page reviews history only. It does not start a bake: it exposes no
 * Bake / Run / Cook / Start / Stop / Cancel / Retry action, calls no execution
 * route, starts no process, mutates no record, and reads no secret. The command
 * is shown only in the broker's redacted form, the log is read by cookId (never
 * an arbitrary path), and output destinations are reported as metadata only —
 * role, path, size, and whether the file exists — never their contents.
 *
 * Bakes NEVER runs PAX, NEVER starts or schedules a bake, NEVER creates a bake
 * row, and exposes no execution action.
 */
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  getCook,
  getCookLog,
  getRecipe,
  listCooks,
  listUpcomingScheduled,
  skipNextScheduledBake,
  deleteScheduledTask,
  openFile,
  openPath,
  stopCook,
  type CookDetailBody,
  type CookOutputSummary,
  type CookSummary,
  type RecipeDetailBody,
  type UpcomingScheduledBake,
} from '../host/brokerBridge';
import { SectionHeader } from './components/SectionHeader';
import { StatusCard } from './StatusCard';
import {
  IconAlertCircle,
  IconCheckCircle,
  IconClock,
  IconCode,
  IconCopy,
  IconDownload,
  IconList,
} from './CookbookIllustrations';
import { downloadBakeLogByCookId, downloadBakeLogText } from './logDownload';
import {
  formatModified,
  rememberPendingSelect,
  requestShellSection,
  takePendingBakeSelect,
} from './shellNav';
import { userScrolledAway } from './bakesLogScroll';
import { usePolling } from '../host/usePolling';

type ListPhase = 'loading' | 'loaded' | 'error' | 'locked';
type DetailPhase = 'idle' | 'loading' | 'loaded' | 'error' | 'locked';
type LogPhase = 'idle' | 'loading' | 'available' | 'empty' | 'error' | 'locked';

// While a bake is running the history, the selected detail, AND the selected
// cook.log are quietly re-polled on this interval, so the log live-tails every
// 2-3 seconds without the user refreshing.
const AUTO_REFRESH_MS = 2500;

// "Clear History" persists as a cutoff timestamp stored locally on this PC, so
// bakes recorded up to that moment stay hidden across refreshes and restarts.
// It never deletes a record, log, or file: "Restore History" removes the cutoff
// and a newer bake reappears on its own.
const HISTORY_CLEARED_AT_KEY = 'pax.bakes.historyClearedAt';

function readHistoryClearedAt(): string | null {
  try {
    const v = window.localStorage.getItem(HISTORY_CLEARED_AT_KEY);
    return v && v.trim() ? v : null;
  } catch {
    return null;
  }
}

function writeHistoryClearedAt(value: string | null): void {
  try {
    if (value) {
      window.localStorage.setItem(HISTORY_CLEARED_AT_KEY, value);
    } else {
      window.localStorage.removeItem(HISTORY_CLEARED_AT_KEY);
    }
  } catch {
    /* localStorage unavailable — the cutoff stays in memory for this session. */
  }
}

const STATUS_LABELS: Record<string, string> = {
  running: 'Running',
  completed: 'Completed',
  errored: 'Failed',
  canceled: 'Canceled',
  interrupted: 'Interrupted',
  unknown: 'Unknown',
};

function normalizeStatus(raw: string | null | undefined): string {
  const s = (raw ?? '').toLowerCase();
  if (s === 'cancelled') {
    return 'canceled';
  }
  return Object.prototype.hasOwnProperty.call(STATUS_LABELS, s) ? s : 'unknown';
}

function statusLabel(raw: string | null | undefined): string {
  return STATUS_LABELS[normalizeStatus(raw)];
}

function StatusGlyph({ status }: { status: string }) {
  const s = normalizeStatus(status);
  if (s === 'completed') {
    return <IconCheckCircle />;
  }
  if (s === 'errored' || s === 'interrupted') {
    return <IconAlertCircle />;
  }
  return <IconClock />;
}

function formatDuration(seconds: number | null): string | null {
  if (seconds === null || seconds === undefined || !isFinite(seconds) || seconds < 0) {
    return null;
  }
  if (seconds < 1) {
    return '<1s';
  }
  const total = Math.round(seconds);
  if (total < 60) {
    return `${total}s`;
  }
  const minutes = Math.floor(total / 60);
  const remSeconds = total % 60;
  if (minutes < 60) {
    return remSeconds ? `${minutes}m ${remSeconds}s` : `${minutes}m`;
  }
  const hours = Math.floor(minutes / 60);
  const remMinutes = minutes % 60;
  return remMinutes ? `${hours}h ${remMinutes}m` : `${hours}h`;
}

function formatBytes(bytes: number | null): string {
  if (bytes === null || bytes === undefined || !isFinite(bytes) || bytes < 0) {
    return 'size unknown';
  }
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let index = 0;
  while (value >= 1024 && index < units.length - 1) {
    value /= 1024;
    index += 1;
  }
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[index]}`;
}

// Bounded, deliberately-heuristic scan of the already-redacted cook.log text for
// lines that mention an error or a warning, used only to surface a quick "what to
// look at" hint on the detail view. It reads the same redacted log already shown
// in the viewer (no new data is exposed), classifies each non-empty line, and
// caps each bucket at 100 lines so a huge log can never balloon the render.
function scanLogIssues(text: string): { errors: string[]; warnings: string[] } {
  const errors: string[] = [];
  const warnings: string[] = [];
  if (!text) {
    return { errors, warnings };
  }
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line) continue;
    if (/(\[error\]|^error\b|\berror:|\bexception\b|\bfailed\b)/i.test(line)) {
      if (errors.length < 100) errors.push(line);
    } else if (/(\[warn(?:ing)?\]|^warn(?:ing)?\b|\bwarning:)/i.test(line)) {
      if (warnings.length < 100) warnings.push(line);
    }
  }
  return { errors, warnings };
}

function MetaRow({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="bk-meta__row">
      <span className="bk-meta__label">{label}</span>
      <span className="bk-meta__value">{value}</span>
    </div>
  );
}

// Short, read-only descriptions for the known PAX process exit codes, surfaced
// beside the numeric exit code on a finished Bake. Returns null when the code
// has no annotation (success, or an unrecognized code).
function paxExitCodeNote(code: number | null): string | null {
  switch (code) {
    case 10:
      return 'Completeness/result limit reached';
    case 20:
      return 'Circuit breaker tripped';
    case 30:
      return 'Entra user directory collection failed or partial';
    default:
      return null;
  }
}

// A cook's trigger is "scheduled" (Windows Task Scheduler), "resume" (recovered
// from a checkpoint), or "manual" (null is treated as manual). Scheduled and
// resume cooks each get a visually distinct chip; manual cooks get a neutral one.
function isScheduledTrigger(trigger: string | null | undefined): boolean {
  return (trigger ?? '').toLowerCase() === 'scheduled';
}

function isResumeTrigger(trigger: string | null | undefined): boolean {
  return (trigger ?? '').toLowerCase() === 'resume';
}

function TriggerChip({ trigger }: { trigger: string | null | undefined }) {
  if (isResumeTrigger(trigger)) {
    return <span className="bk-trigger bk-trigger--resume">Resume</span>;
  }
  const scheduled = isScheduledTrigger(trigger);
  return (
    <span className={'bk-trigger' + (scheduled ? ' bk-trigger--scheduled' : '')}>
      {scheduled ? 'Scheduled' : 'Manual'}
    </span>
  );
}

function OutputRow({ output }: { output: CookOutputSummary }) {
  const canOpen = output.exists && !!output.path && output.path.trim().length > 0;
  return (
    <li className="bk-output">
      <span className="bk-output__role">{output.role}</span>
      <span className="bk-output__path">{output.path ?? '(no path recorded)'}</span>
      <span className="bk-output__meta">
        {output.exists ? 'Present' : 'Not found'} · {formatBytes(output.sizeBytes)}
      </span>
      {canOpen ? (
        <button
          type="button"
          className="bk-output__open"
          onClick={() => void openPath(output.path as string)}
          title="Open the folder that contains this file"
        >
          Open folder
        </button>
      ) : null}
    </li>
  );
}

// A presentational collapsible panel (native <details>/<summary>) used to group
// the bake detail into Run summary / Output files / Errors & warnings / Advanced
// sections. Purely layout — it owns no state and reads no data.
function CollapsibleSection({
  title,
  defaultOpen = false,
  children,
}: {
  title: string;
  defaultOpen?: boolean;
  children: ReactNode;
}) {
  return (
    <details className="bk-collapse" {...(defaultOpen ? { open: true } : {})}>
      <summary className="bk-collapse__summary">{title}</summary>
      <div className="bk-collapse__body">{children}</div>
    </details>
  );
}

// Maps a normalized cook status + the heuristic issue tone onto a single
// presentational tone used by the status banner and the left-list accent bar.
// A completed run is "success" unless the issue scan found warnings (warning)
// or errors (error); a canceled run reads as a warning; running/errored/
// interrupted map directly; anything else is neutral. Presentation only.
type BakeTone = 'success' | 'warning' | 'error' | 'running' | 'neutral';

function bakeTone(statusNorm: string, issue: 'clean' | 'warn' | 'error'): BakeTone {
  if (statusNorm === 'running') return 'running';
  if (statusNorm === 'errored' || statusNorm === 'interrupted') return 'error';
  if (statusNorm === 'completed') return issue === 'error' ? 'error' : issue === 'warn' ? 'warning' : 'success';
  if (statusNorm === 'canceled') return 'warning';
  return 'neutral';
}

// The color-coded headline banner across the top of a bake's detail — the
// visual anchor that tells the reader at a glance how the run went. Purely
// presentational: the caller supplies the tone and copy.
function StatusBanner({ tone, headline, detail }: { tone: BakeTone; headline: string; detail: string }) {
  return (
    <div className={'bk-banner bk-banner--' + tone} role="status">
      <span className="bk-banner__headline">{headline}</span>
      {detail ? <span className="bk-banner__detail">{detail}</span> : null}
    </div>
  );
}

// Derives the banner headline + sub-line from already-computed primitives, so
// the case logic stays out of the JSX and is null-safe by construction (every
// argument is a plain value, never the nullable detail object). Reads no data.
function bakeBannerCopy(
  tone: BakeTone,
  statusNorm: string,
  statusText: string,
  durStr: string | null,
  warnCount: number,
  exitCode: number | null,
  errorClass: string | null,
): { headline: string; detail: string } {
  if (tone === 'success') {
    return { headline: 'Completed successfully', detail: durStr ? `Ran in ${durStr}` : '' };
  }
  if (tone === 'warning' && statusNorm === 'completed') {
    return {
      headline: 'Completed with warnings',
      detail: `${warnCount} warning(s)` + (durStr ? ` · ${durStr}` : ''),
    };
  }
  if (tone === 'warning' && statusNorm === 'canceled') {
    return { headline: 'Canceled', detail: durStr ? `After ${durStr}` : '' };
  }
  if (tone === 'error') {
    return {
      headline: 'Failed',
      detail: (exitCode !== null ? `Exit code ${exitCode}` : '') + (errorClass ? ` · ${errorClass}` : ''),
    };
  }
  if (tone === 'running') {
    return { headline: 'Running', detail: durStr ? `Elapsed ${durStr}` : 'In progress' };
  }
  return { headline: statusText, detail: '' };
}

// ---------------------------------------------------------------------------
// Scheduled-bake (Upcoming bakes) presentation helpers.
// ---------------------------------------------------------------------------

const WEEKDAY_NAMES = [
  'Sunday',
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
];

type ScheduleRecurrence = UpcomingScheduledBake['recurrence'];

// A clock time ("7:00 AM") in the user's locale from an hour/minute pair.
function formatClockTime(hour: number, minute: number): string {
  const d = new Date();
  d.setHours(hour, minute, 0, 0);
  return d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
}

// Short schedule badge — "Daily" / "Weekly" / "Scheduled".
function scheduleBadge(kind: string): string {
  const k = (kind ?? '').toLowerCase();
  if (k === 'daily') {
    return 'Daily';
  }
  if (k === 'weekly') {
    return 'Weekly';
  }
  return 'Scheduled';
}

// Frequency sentence — "Every day at 7:00 AM" / "Every Monday, Friday at 9:00 AM".
function formatFrequency(rec: ScheduleRecurrence): string {
  const time = formatClockTime(rec.hour, rec.minute);
  const k = (rec.kind ?? '').toLowerCase();
  if (k === 'daily') {
    return `Every day at ${time}`;
  }
  if (k === 'weekly') {
    const days = (rec.daysOfWeek ?? []).filter((d) => d >= 0 && d <= 6);
    if (days.length === 0 || days.length === 7) {
      return `Every day at ${time}`;
    }
    const names = days
      .slice()
      .sort((a, b) => a - b)
      .map((d) => WEEKDAY_NAMES[d]);
    return `Every ${names.join(', ')} at ${time}`;
  }
  return time;
}

// Friendly next-run label — "Today at 7:00 AM" / "Tomorrow at 7:00 AM" /
// "Monday, Jun 22 at 7:00 AM".
function formatNextRun(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) {
    return iso;
  }
  const time = d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  const now = new Date();
  const tomorrow = new Date(now);
  tomorrow.setDate(now.getDate() + 1);
  if (d.toDateString() === now.toDateString()) {
    return `Today at ${time}`;
  }
  if (d.toDateString() === tomorrow.toDateString()) {
    return `Tomorrow at ${time}`;
  }
  const date = d.toLocaleDateString(undefined, {
    weekday: 'long',
    month: 'short',
    day: 'numeric',
  });
  return `${date} at ${time}`;
}

const AUTH_MODE_FRIENDLY: Record<string, string> = {
  devicecode: 'Device code sign-in',
  appregistration: 'Entra ID app registration',
  managedidentity: 'Managed identity',
  agent365: 'Agent 365',
  interactive: 'Interactive sign-in',
};

function friendlyAuthMode(raw: string): string {
  const key = raw.toLowerCase().replace(/[^a-z0-9]/g, '');
  return AUTH_MODE_FRIENDLY[key] ?? raw;
}

// Best-effort "where results are saved" from a saved recipe document. Falls back
// to a safe generic line when the destination field is absent or unrecognized.
function extractOutputDestination(recipe: Record<string, unknown> | undefined): string {
  if (!recipe) {
    return 'As configured in the recipe';
  }
  const destinations = recipe.destinations as
    | { fact?: { path?: unknown }; userInfo?: { path?: unknown } }
    | undefined;
  const factPath = destinations?.fact?.path;
  if (typeof factPath === 'string' && factPath.trim().length > 0) {
    return factPath.trim();
  }
  const single = recipe.outputPath ?? recipe.destination;
  if (typeof single === 'string' && single.trim().length > 0) {
    return single.trim();
  }
  return 'As configured in the recipe';
}

// Best-effort "which sign-in is used" from a saved recipe document.
function extractSignIn(recipe: Record<string, unknown> | undefined): string {
  if (!recipe) {
    return 'As configured in the recipe';
  }
  const mode = recipe.authMode;
  if (typeof mode === 'string' && mode.trim().length > 0) {
    return friendlyAuthMode(mode.trim());
  }
  return 'As configured in the recipe';
}

// The Power BI dashboard layout a bake's data targets, read from the recipe's
// template/processing selection. Null for Custom / Entra-only recipes.
function extractRecipeDashboard(recipe: Record<string, unknown> | undefined): string | null {
  const proc = recipe?.processing as { dashboard?: unknown } | undefined;
  const d = proc?.dashboard;
  return typeof d === 'string' && d.trim().length > 0 ? d.trim() : null;
}

function resolveDashboardLabel(raw: string | null | undefined): string {
  if (raw === 'aibv') {
    return 'AI Business Value';
  }
  if (raw === 'aio') {
    return 'AI-in-One';
  }
  return raw && raw.length > 0 ? raw : '\u2014';
}

// Maps known broker error codes to plain-language explanations with a likely
// cause and a suggested next step. Unknown codes fall back to the raw value.
const FRIENDLY_ERROR_MAP: Record<string, { title: string; help: string }> = {
  spawn_failed: {
    title: "PAX Cookbook couldn't start the script engine.",
    help: "This usually means PowerShell 7 isn't installed or can't be found. Try reinstalling PAX Cookbook.",
  },
  broker_exited: {
    title: 'The background process stopped unexpectedly during the bake.',
    help: 'Try running the bake again. If this keeps happening, open the log file below and share it with support.',
  },
  engine_verify_failed: {
    title: "The PAX engine couldn't be verified before the bake.",
    help: 'The engine fingerprint did not match the approved version. Update or reinstall PAX Cookbook, then try again.',
  },
  engine_missing: {
    title: 'The PAX engine is not set up on this PC yet.',
    help: 'Open Settings to finish setting up the engine, then run the bake again.',
  },
  disk_space: {
    title: 'There was not enough free disk space to run the bake.',
    help: 'Free up space on this drive, then run the bake again.',
  },
  timeout: {
    title: 'The bake ran longer than allowed and was stopped.',
    help: 'Try a smaller date range or fewer activity types, then run again.',
  },
  canceled: {
    title: 'The bake was stopped before it finished.',
    help: 'Start the bake again when you are ready.',
  },
  auth_failed: {
    title: "PAX couldn't sign in to your Microsoft 365 environment.",
    help: 'Check the recipe\u2019s sign-in details (or its Chef\u2019s Key), then run the bake again.',
  },
};

function friendlyError(code: string | null | undefined): { title: string; help: string } | null {
  if (!code) {
    return null;
  }
  const key = code.trim().toLowerCase().replace(/[^a-z0-9]+/g, '_');
  return FRIENDLY_ERROR_MAP[key] ?? null;
}

export function BakesWorkspace() {
  const [cooks, setCooks] = useState<CookSummary[]>([]);
  const [listPhase, setListPhase] = useState<ListPhase>('loading');
  // Persisted, non-destructive "Clear History": a cutoff timestamp (stored
  // locally on this PC) hides bakes recorded up to that moment across refreshes
  // and restarts. It never deletes a cook record, log, or file — "Restore
  // History" removes the cutoff, and a newer bake reappears on its own.
  const [clearedAt, setClearedAt] = useState<string | null>(() => readHistoryClearedAt());

  const [selectedCookId, setSelectedCookId] = useState<string | null>(null);
  const [detail, setDetail] = useState<CookDetailBody | null>(null);
  const [detailPhase, setDetailPhase] = useState<DetailPhase>('idle');

  // X6 — Stop bake control. `canceling` disables the button and shows "Stopping…"
  // while a stop request is in flight, and stays set after a 202 until the poll
  // flips the row to a terminal status (which removes the button). `cancelError`
  // surfaces a bounded inline message on a 404 / 409 / locked / network refusal.
  const [canceling, setCanceling] = useState(false);
  const [cancelError, setCancelError] = useState<string | null>(null);

  const [logText, setLogText] = useState('');
  const [logPhase, setLogPhase] = useState<LogPhase>('idle');
  const [logCopied, setLogCopied] = useState(false);

  // Transient "Copied" state for the engine-fingerprint copy button in Run
  // configuration. The SHA-256 is a non-secret engine fingerprint (already
  // shown in full today); copying it reads/exposes no tenant data or secret.
  const [shaCopied, setShaCopied] = useState(false);

  // Live-tail ("tail -f") of the cook.log viewer. When auto-scroll is on, each
  // log update pins the <pre> to the bottom; a user scroll away from the bottom
  // turns it off so they can read earlier output. Checked by default.
  const [autoScroll, setAutoScroll] = useState(true);
  const logPreRef = useRef<HTMLPreElement | null>(null);

  // --- Upcoming bakes (scheduled) ---
  const [upcoming, setUpcoming] = useState<UpcomingScheduledBake[]>([]);
  const [upcomingPhase, setUpcomingPhase] =
    useState<'loading' | 'loaded' | 'error' | 'locked'>('loading');
  // The selected scheduled bake. Mutually exclusive with selectedCookId — only
  // one detail (history OR schedule) is shown at a time.
  const [selectedSchedule, setSelectedSchedule] = useState<UpcomingScheduledBake | null>(null);
  const selectedScheduleRef = useRef<string | null>(null);
  // The selected schedule's recipe document (for Output + Sign-in).
  const [scheduleRecipe, setScheduleRecipe] = useState<RecipeDetailBody | null>(null);
  // The selected bake's live recipe (so the Dashboard target reflects the recipe
  // config even when the cook snapshot did not capture it).
  const [bakeRecipe, setBakeRecipe] = useState<RecipeDetailBody | null>(null);
  const bakeRecipeIdRef = useRef<string | null>(null);
  // Transient confirmation toast after "Skip next bake".
  const [skipToast, setSkipToast] = useState<string | null>(null);
  const [skipBusy, setSkipBusy] = useState(false);
  // "Cancel all future bakes" confirmation modal target + in-flight flag.
  const [cancelTarget, setCancelTarget] = useState<UpcomingScheduledBake | null>(null);
  const [cancelBusy, setCancelBusy] = useState(false);

  const mounted = useRef(true);
  const selectedCookIdRef = useRef<string | null>(null);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  useEffect(() => {
    selectedCookIdRef.current = selectedCookId;
  }, [selectedCookId]);

  const loadCooks = useCallback(async (background = false) => {
    if (!background) {
      setListPhase('loading');
    }
    const res = await listCooks();
    if (!mounted.current) {
      return;
    }
    if (res.ok && res.data) {
      setCooks(Array.isArray(res.data.cooks) ? res.data.cooks : []);
      setListPhase('loaded');
    } else if (res.status === 423) {
      setListPhase('locked');
    } else if (!background) {
      setCooks([]);
      setListPhase('error');
    }
  }, []);

  const clearHistory = useCallback(() => {
    // Persisted, non-destructive — records a cutoff timestamp so bakes recorded
    // up to now stay hidden across refreshes and restarts. No broker mutation,
    // no record or log deletion. "Restore History" or a newer bake brings the
    // list back.
    const cutoff = new Date().toISOString();
    setClearedAt(cutoff);
    writeHistoryClearedAt(cutoff);
  }, []);

  const restoreHistory = useCallback(() => {
    setClearedAt(null);
    writeHistoryClearedAt(null);
  }, []);

  // Apply the persisted clear cutoff to the broker's full list. Rows recorded
  // after the cutoff (newer bakes) reappear on their own, and any row we cannot
  // date is kept rather than hidden.
  const visibleCooks = useMemo(() => {
    if (!clearedAt) {
      return cooks;
    }
    const cutoff = new Date(clearedAt).getTime();
    if (!isFinite(cutoff)) {
      return cooks;
    }
    return cooks.filter(cook => {
      const t = new Date(cook.createdAt).getTime();
      return !isFinite(t) || t > cutoff;
    });
  }, [cooks, clearedAt]);
  const historyHidden = clearedAt !== null && cooks.length > visibleCooks.length;

  const loadDetail = useCallback(async (cookId: string, background = false) => {
    if (!background) {
      setDetailPhase('loading');
    }
    const res = await getCook(cookId);
    if (!mounted.current || selectedCookIdRef.current !== cookId) {
      return;
    }
    if (res.ok && res.data) {
      setDetail(res.data);
      setDetailPhase('loaded');
    } else if (res.status === 423) {
      setDetailPhase('locked');
    } else if (!background) {
      setDetail(null);
      setDetailPhase('error');
    }
  }, []);

  // Loads the broker's managed cook.log for a cook. A foreground load (quiet =
  // false) shows the 'loading' phase and resets transient copy state. A quiet
  // load (quiet = true) is used by the live-tail poll and the terminal-transition
  // reload: it never flips to 'loading' and only replaces the displayed text when
  // real content is available, so a growing log refreshes in place without
  // flicker and a transient empty/locked/error poll never clobbers content that
  // is already shown. Either way only the broker-managed, redacted cook.log is
  // read (addressed by cookId) -- never an output file or any tenant data.
  const loadLog = useCallback(async (cookId: string, quiet = false) => {
    if (!quiet) {
      setLogPhase('loading');
      setLogCopied(false);
    }
    const res = await getCookLog(cookId);
    if (!mounted.current || selectedCookIdRef.current !== cookId) {
      return;
    }
    if (res.available) {
      const text = res.text ?? '';
      if (text.trim().length === 0) {
        if (!quiet) {
          setLogText('');
          setLogPhase('empty');
        }
      } else {
        setLogText(text);
        setLogPhase('available');
      }
    } else if (res.locked) {
      if (!quiet) {
        setLogText('');
        setLogPhase('locked');
      }
    } else if (res.error === 'cook_log_not_found' || res.error === 'cook_not_found') {
      if (!quiet) {
        setLogText('');
        setLogPhase('empty');
      }
    } else if (!quiet) {
      setLogText('');
      setLogPhase('error');
    }
  }, []);

  const selectCook = useCallback(
    (cookId: string) => {
      // A history detail replaces any scheduled detail (only one shows).
      setSelectedSchedule(null);
      selectedScheduleRef.current = null;
      setSelectedCookId(cookId);
      selectedCookIdRef.current = cookId;
      setCanceling(false);
      setCancelError(null);
      void loadDetail(cookId);
      void loadLog(cookId);
    },
    [loadDetail, loadLog],
  );

  const loadUpcoming = useCallback(async (background = false) => {
    if (!background) {
      setUpcomingPhase('loading');
    }
    const res = await listUpcomingScheduled();
    if (!mounted.current) {
      return;
    }
    if (res.ok && res.data) {
      setUpcoming(Array.isArray(res.data.scheduled) ? res.data.scheduled : []);
      setUpcomingPhase('loaded');
    } else if (res.status === 423) {
      setUpcomingPhase('locked');
    } else if (!background) {
      setUpcoming([]);
      setUpcomingPhase('error');
    }
  }, []);

  // Select a scheduled bake — replaces any history detail and loads the recipe
  // document so the panel can show its output destination + sign-in.
  const selectSchedule = useCallback((entry: UpcomingScheduledBake) => {
    setSelectedCookId(null);
    selectedCookIdRef.current = null;
    setDetail(null);
    setDetailPhase('idle');
    setSelectedSchedule(entry);
    selectedScheduleRef.current = entry.recipeId;
    setScheduleRecipe(null);
    void (async () => {
      const res = await getRecipe(entry.recipeId);
      if (!mounted.current || selectedScheduleRef.current !== entry.recipeId) {
        return;
      }
      if (res.ok && res.data) {
        setScheduleRecipe(res.data);
      }
    })();
  }, []);

  // "Skip next bake" — skip only the next occurrence; the schedule stays active.
  const skipNext = useCallback(
    async (entry: UpcomingScheduledBake) => {
      setSkipBusy(true);
      const res = await skipNextScheduledBake(entry.recipeId);
      if (!mounted.current) {
        return;
      }
      setSkipBusy(false);
      setSkipToast(
        res.ok
          ? 'Next bake skipped. The schedule will resume after that.'
          : 'Could not skip the next bake. Try again.',
      );
      window.setTimeout(() => {
        if (mounted.current) {
          setSkipToast(null);
        }
      }, 4000);
      if (res.ok) {
        void loadUpcoming(true);
      }
    },
    [loadUpcoming],
  );

  // "Cancel all future bakes" (confirmed) — remove the whole schedule + OS task.
  const confirmCancelAll = useCallback(async () => {
    const entry = cancelTarget;
    if (!entry) {
      return;
    }
    setCancelBusy(true);
    const res = await deleteScheduledTask(entry.recipeId);
    if (!mounted.current) {
      return;
    }
    setCancelBusy(false);
    setCancelTarget(null);
    if (res.ok) {
      setSelectedSchedule((cur) => (cur && cur.recipeId === entry.recipeId ? null : cur));
      if (selectedScheduleRef.current === entry.recipeId) {
        selectedScheduleRef.current = null;
      }
      void loadUpcoming(true);
    }
  }, [cancelTarget, loadUpcoming]);

  // "Open recipe" — open the recipe in the builder for editing.
  const openScheduleRecipe = useCallback((recipeId: string) => {
    rememberPendingSelect(recipeId);
    requestShellSection('recipes');
  }, []);

  const copyLog = useCallback(async () => {
    if (!logText) {
      return;
    }
    try {
      await navigator.clipboard.writeText(logText);
      if (!mounted.current) {
        return;
      }
      setLogCopied(true);
      window.setTimeout(() => {
        if (mounted.current) {
          setLogCopied(false);
        }
      }, 2000);
    } catch {
      if (mounted.current) {
        setLogCopied(false);
      }
    }
  }, [logText]);

  // Copy the selected bake's full PAX engine SHA-256 fingerprint to the
  // clipboard (the detail view shows an abbreviated form). The fingerprint is a
  // non-secret integrity hash already projected by the broker; this reads no
  // tenant data, no secret, and starts nothing. Mirrors copyLog's pattern.
  const copyEngineSha = useCallback(async () => {
    const sha = detail?.engine?.sha256;
    if (!sha) {
      return;
    }
    try {
      await navigator.clipboard.writeText(sha);
      if (!mounted.current) {
        return;
      }
      setShaCopied(true);
      window.setTimeout(() => {
        if (mounted.current) {
          setShaCopied(false);
        }
      }, 2000);
    } catch {
      if (mounted.current) {
        setShaCopied(false);
      }
    }
  }, [detail]);

  // X6 — request cancellation of the selected running bake. Confirms first, then
  // posts to the broker stop route (which owns the kill of the supervised
  // process tree); the browser kills nothing. On a 202 the button stays disabled
  // ("Stopping…") and a quiet reload is kicked so the transition shows promptly —
  // the existing 2.5s poll observes the terminal 'canceled' status and removes
  // the button. A 4xx / locked / network refusal re-enables it with a bounded
  // inline message.
  const stopBake = useCallback(async () => {
    const cookId = selectedCookIdRef.current;
    if (!cookId || canceling) {
      return;
    }
    const confirmed = window.confirm(
      'Stop this bake? The run will be canceled and cannot be resumed.',
    );
    if (!confirmed) {
      return;
    }
    setCancelError(null);
    setCanceling(true);
    const res = await stopCook(cookId);
    if (!mounted.current || selectedCookIdRef.current !== cookId) {
      return;
    }
    if (res.ok) {
      void loadDetail(cookId, true);
      void loadLog(cookId, true);
      return;
    }
    setCanceling(false);
    if (res.status === 409 && res.error === 'cook_not_running') {
      setCancelError('This bake has already finished.');
    } else if (res.status === 409 && res.error === 'cook_not_supervised') {
      setCancelError(
        'This bake is not being supervised by the running app and cannot be stopped here.',
      );
    } else if (res.status === 404) {
      setCancelError('This bake could not be found.');
    } else if (res.status === 423) {
      setCancelError('PAX Cookbook is locked. Unlock it to stop this bake.');
    } else {
      setCancelError('Could not stop this bake. Refresh and try again.');
    }
  }, [canceling, loadDetail, loadLog]);

  // Steady background polling so the history list and the upcoming-bakes list
  // stay current even when nothing is running (a scheduled bake can start and
  // finish without the user touching anything). The first call of each is a
  // foreground load (shows the loading state); every later call is a quiet,
  // merge-in-place background refresh that never disrupts the selection, scroll,
  // or a failed cycle. The faster 2.5s live-tail poll below stays in charge of
  // an actively running bake's detail + log.
  const cooksFirstRef = useRef(true);
  const upcomingFirstRef = useRef(true);
  const pollCooks = useCallback(async () => {
    const background = !cooksFirstRef.current;
    cooksFirstRef.current = false;
    await loadCooks(background);
  }, [loadCooks]);
  const pollUpcoming = useCallback(async () => {
    const background = !upcomingFirstRef.current;
    upcomingFirstRef.current = false;
    await loadUpcoming(background);
  }, [loadUpcoming]);
  usePolling(pollCooks, 15000);
  usePolling(pollUpcoming, 60000);

  // Fetch the selected bake's live recipe so the Dashboard target always
  // reflects the recipe configuration, even when the cook snapshot did not
  // capture it (e.g. a bake that failed early). Fetched once per distinct recipe.
  useEffect(() => {
    const rid = detail?.recipeId ?? null;
    if (!rid) {
      setBakeRecipe(null);
      bakeRecipeIdRef.current = null;
      return;
    }
    if (bakeRecipeIdRef.current === rid) {
      return;
    }
    bakeRecipeIdRef.current = rid;
    setBakeRecipe(null);
    void (async () => {
      const res = await getRecipe(rid);
      if (!mounted.current || bakeRecipeIdRef.current !== rid) {
        return;
      }
      if (res.ok && res.data) {
        setBakeRecipe(res.data);
      }
    })();
  }, [detail?.recipeId]);

  // One-shot: when the editor has just started a bake it stashes the new
  // cookId, then routes here. Once the history list has loaded, focus that
  // cook so the freshly started bake lands selected. The detail/log still come
  // from the broker by id — this only chooses what to show first.
  const pendingBakeHandled = useRef(false);
  useEffect(() => {
    if (pendingBakeHandled.current || listPhase !== 'loaded') {
      return;
    }
    pendingBakeHandled.current = true;
    const pending = takePendingBakeSelect();
    if (pending) {
      selectCook(pending);
    }
  }, [listPhase, selectCook]);

  // Auto-refresh only while a bake is still running, so an in-progress record,
  // its live detail, AND its cook.log stay current without polling a settled
  // history. The selected cook's log is quietly re-fetched each tick so it
  // live-tails as the broker streams the child's stdout into it.
  const hasRunning = useMemo(
    () =>
      cooks.some(cook => normalizeStatus(cook.status) === 'running') ||
      (detail !== null && normalizeStatus(detail.status) === 'running'),
    [cooks, detail],
  );
  useEffect(() => {
    if (!hasRunning) {
      return;
    }
    const id = window.setInterval(() => {
      void loadCooks(true);
      const current = selectedCookIdRef.current;
      if (current) {
        void loadDetail(current, true);
        void loadLog(current, true);
      }
    }, AUTO_REFRESH_MS);
    return () => window.clearInterval(id);
  }, [hasRunning, loadCooks, loadDetail, loadLog]);

  // When the selected cook finishes (running -> terminal), reload its log once
  // so the final, complete cook.log replaces any header-only / mid-run content
  // the live tail last captured. Gated by the last-acted status per cookId, so
  // it fires exactly once per transition and never on a fresh selection of an
  // already-terminal cook (selectCook already loads that log).
  const lastLoggedStatusRef = useRef<{ cookId: string | null; status: string }>({
    cookId: null,
    status: '',
  });
  useEffect(() => {
    const current = selectedCookId;
    if (!current || !detail || detail.cookId !== current) {
      return;
    }
    const status = normalizeStatus(detail.status);
    const prev = lastLoggedStatusRef.current;
    const wasRunningSameCook = prev.cookId === current && prev.status === 'running';
    const isTerminal =
      status === 'completed' ||
      status === 'errored' ||
      status === 'interrupted' ||
      status === 'canceled';
    if (wasRunningSameCook && isTerminal) {
      void loadLog(current, true);
    }
    lastLoggedStatusRef.current = { cookId: current, status };
  }, [detail, selectedCookId, loadLog]);

  // Keep the log pinned to the bottom whenever auto-scroll is on and the log is
  // showing: on each content update (the 2.5s live-tail poll and the initial /
  // terminal-transition load) and when the user re-enables auto-scroll. A direct
  // scrollTop assignment lands exactly at the bottom, so the onScroll handler
  // never mistakes it for a user scroll. Only the managed cook.log viewport is
  // moved -- no data is read.
  useEffect(() => {
    if (!autoScroll || logPhase !== 'available') {
      return;
    }
    const el = logPreRef.current;
    if (!el) {
      return;
    }
    el.scrollTop = el.scrollHeight;
  }, [logText, autoScroll, logPhase]);

  // When the user scrolls the log away from the bottom while auto-scroll is on,
  // turn it off so the live tail stops yanking the view back down. The
  // programmatic pin above always lands at the bottom, so it never trips this.
  const handleLogScroll = useCallback(() => {
    if (!autoScroll) {
      return;
    }
    const el = logPreRef.current;
    if (!el) {
      return;
    }
    if (
      userScrolledAway({
        scrollTop: el.scrollTop,
        scrollHeight: el.scrollHeight,
        clientHeight: el.clientHeight,
      })
    ) {
      setAutoScroll(false);
    }
  }, [autoScroll]);

  const errorSummary = detail?.errorSummary ?? null;
  const detailStatus = normalizeStatus(detail?.status);
  const isFailedRun = detailStatus === 'errored' || detailStatus === 'interrupted';
  const showErrorSummary =
    isFailedRun &&
    errorSummary !== null &&
    Boolean(errorSummary.errorClass || errorSummary.errorMessage || errorSummary.closureReason);
  const friendlyErr =
    friendlyError(errorSummary?.errorClass) ?? friendlyError(errorSummary?.closureReason);
  const allOutputs = detail?.outputs?.outputs ?? [];
  // Only show output rows for files that actually exist on disk, so the Output
  // files section reflects what this bake really produced — e.g. no phantom
  // user-info row for an audit-only bake, and no fact row for an Entra-only one.
  // Existence is the broker-provided signal (outputs.json carries it per file).
  const outputs = allOutputs.filter(o => o.exists);
  const showCompletedNoData = detailStatus === 'completed' && allOutputs.length === 0;
  const readiness = detail?.readiness ?? null;
  const requirements = readiness?.requirements ?? [];
  const logIssues = scanLogIssues(logText);
  const hasErrors = isFailedRun || showErrorSummary || logIssues.errors.length > 0;
  const hasWarnings = logIssues.warnings.length > 0 || (readiness?.warnings?.length ?? 0) > 0;
  const issueTone = hasErrors ? 'error' : hasWarnings ? 'warn' : 'clean';

  // Presentation derivations for the status banner + metric cards. All read
  // existing fields/helpers (nothing fabricated) and use optional chaining so
  // they are safe to compute even before a bake is selected (detail null).
  const tone = bakeTone(detailStatus, issueTone);
  const durStr = formatDuration(detail?.durationSeconds ?? null);
  const warnCount = logIssues.warnings.length + (readiness?.warnings?.length ?? 0);
  const bannerCopy = bakeBannerCopy(
    tone,
    detailStatus,
    statusLabel(detail?.status),
    durStr,
    warnCount,
    detail?.exitCode ?? null,
    errorSummary?.errorClass ?? null,
  );

  return (
    <div className="dvw-bakes">
      <SectionHeader
        headingLevel="h1"
        title="Bakes"
        helpTopic="cookbookBakes"
        accent="var(--c-amber)"
        lede="Review recorded bakes — their status, outputs, and logs. This page does not start a bake."
      />

      <div className="dvw-bakes__cols">
        <div className="bk-left-col">
        {/* Left: real bake history read from the broker */}
        <section className="dvw-card tt-pane bk-pane" aria-label="Bake history">
          <header className="tt-pane__head">
            <h2 className="tt-pane__title">Bake history</h2>
            <div className="tt-pane__head-actions">
              {clearedAt !== null ? (
                <button
                  type="button"
                  className="bk-history-btn"
                  onClick={restoreHistory}
                  title="Bring back the bakes hidden by Clear History."
                >
                  Restore History
                </button>
              ) : visibleCooks.length > 0 ? (
                <button
                  type="button"
                  className="bk-history-btn"
                  onClick={clearHistory}
                  title="Clear this view only — your bake records and logs are not deleted."
                >
                  Clear History
                </button>
              ) : null}
            </div>
          </header>

          {listPhase === 'loading' ? (
            <div className="bk-status-block" role="status">
              <p className="tt-muted">Loading bake history…</p>
            </div>
          ) : null}

          {listPhase === 'error' ? (
            <div className="bk-status-block" role="status">
              <p className="tt-muted">
                Bake history couldn’t be loaded right now. Try refreshing.
              </p>
            </div>
          ) : null}

          {listPhase === 'locked' ? (
            <div className="bk-status-block" role="status">
              <p className="tt-muted">
                PAX Cookbook is locked. Unlock it to review bake history.
              </p>
            </div>
          ) : null}

          {listPhase === 'loaded' && visibleCooks.length > 0 ? (
            <ul className="tt-list bk-history-list" role="listbox" aria-label="Bake history">
              {visibleCooks.map(cook => {
                const selected = cook.cookId === selectedCookId;
                const finished = formatModified(cook.finishedAt ?? cook.createdAt);
                const name = isResumeTrigger(cook.trigger)
                  ? 'Resume from checkpoint'
                  : cook.recipeName ?? cook.recipeId;
                const status = normalizeStatus(cook.status);
                const rowTone = bakeTone(status, 'clean');
                return (
                  <li key={cook.cookId} className="tt-list__item">
                    <button
                      type="button"
                      role="option"
                      aria-selected={selected}
                      className={
                        'tt-list__row bk-row--' +
                        rowTone +
                        (selected ? ' tt-list__row--selected' : '')
                      }
                      onClick={() => selectCook(cook.cookId)}
                    >
                      <span className={'tt-list__icon bk-status--' + status} aria-hidden="true">
                        <StatusGlyph status={cook.status} />
                      </span>
                      <span className="tt-list__text">
                        <span className="tt-list__name">{name}</span>
                        <span className="tt-list__meta">
                          {statusLabel(cook.status)}
                          {finished ? ` · ${finished}` : ''}
                          {isScheduledTrigger(cook.trigger) || isResumeTrigger(cook.trigger) ? (
                            <>
                              {' · '}
                              <TriggerChip trigger={cook.trigger} />
                            </>
                          ) : null}
                        </span>
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          ) : null}

          {listPhase === 'loaded' && visibleCooks.length === 0 ? (
            <div className="tt-empty bk-empty">
              <IconClock className="tt-empty__icon" />
              <h3 className="tt-empty__title">
                {historyHidden ? 'Bake history cleared' : 'No bakes recorded yet'}
              </h3>
              <p className="tt-empty__body">
                {historyHidden
                  ? 'This view is cleared. Your bake records and logs were not deleted — choose Restore History to bring them back, and any new bake will appear here on its own.'
                  : 'No bakes have been recorded on this PC. This page reviews real bake history — status, outputs, and logs — when records exist. It does not start a bake: prepare recipes in Recipes and preflight them in Taste Tests.'}
              </p>
              {historyHidden ? (
                <button
                  type="button"
                  className="bk-empty__link"
                  onClick={restoreHistory}
                >
                  Restore History
                </button>
              ) : (
                <button
                  type="button"
                  className="bk-empty__link"
                  onClick={() => requestShellSection('recipes')}
                >
                  Open Recipes →
                </button>
              )}
            </div>
          ) : null}
        </section>

        {/* Upcoming scheduled bakes */}
        <section className="dvw-card tt-pane bk-pane bk-upcoming" aria-label="Upcoming bakes">
          <header className="tt-pane__head">
            <h2 className="tt-pane__title">Upcoming bakes</h2>
          </header>

          {upcomingPhase === 'loading' ? (
            <div className="bk-status-block" role="status">
              <p className="tt-muted">Loading scheduled bakes…</p>
            </div>
          ) : null}

          {upcomingPhase === 'error' ? (
            <div className="bk-status-block" role="status">
              <p className="tt-muted">Scheduled bakes couldn’t be loaded right now.</p>
            </div>
          ) : null}

          {upcomingPhase === 'locked' ? (
            <div className="bk-status-block" role="status">
              <p className="tt-muted">PAX Cookbook is locked. Unlock it to see scheduled bakes.</p>
            </div>
          ) : null}

          {upcomingPhase === 'loaded' && upcoming.length > 0 ? (
            <ul className="tt-list bk-upcoming-list" role="listbox" aria-label="Upcoming bakes">
              {upcoming.map((entry) => {
                const selected = selectedSchedule?.recipeId === entry.recipeId;
                return (
                  <li key={entry.recipeId} className="tt-list__item">
                    <button
                      type="button"
                      role="option"
                      aria-selected={selected}
                      className={'tt-list__row' + (selected ? ' tt-list__row--selected' : '')}
                      onClick={() => selectSchedule(entry)}
                    >
                      <span className="tt-list__icon bk-status--scheduled" aria-hidden="true">
                        <IconClock />
                      </span>
                      <span className="tt-list__text">
                        <span className="tt-list__name">{entry.name || entry.recipeId}</span>
                        <span className="tt-list__meta">{formatNextRun(entry.nextRunAt)}</span>
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          ) : null}

          {upcomingPhase === 'loaded' && upcoming.length === 0 ? (
            <div className="tt-empty bk-empty">
              <IconClock className="tt-empty__icon" />
              <p className="tt-empty__body">
                No upcoming bakes scheduled.
                <br />
                Set up a schedule from the recipe builder to automate your bakes.
              </p>
            </div>
          ) : null}
        </section>
        </div>

        {/* Center: selected bake detail, outputs, and log */}
        <section className="dvw-card tt-pane tt-pane--main bk-pane" aria-label="Bake detail">
          {selectedSchedule ? (
            <div className="bk-detail bk-sched-detail">
              <header className="bk-detail__head">
                <div className="bk-detail__title-row">
                  <h2 className="tt-result__title">
                    {selectedSchedule.name || selectedSchedule.recipeId}
                  </h2>
                  <span
                    className={
                      'bk-sched-badge bk-sched-badge--' +
                      (selectedSchedule.recurrence.kind || '').toLowerCase()
                    }
                  >
                    {scheduleBadge(selectedSchedule.recurrence.kind)}
                  </span>
                </div>
              </header>

              <div className="bk-meta">
                <MetaRow label="Next run" value={formatNextRun(selectedSchedule.nextRunAt)} />
                <MetaRow label="Frequency" value={formatFrequency(selectedSchedule.recurrence)} />
                <MetaRow label="Output" value={extractOutputDestination(scheduleRecipe?.recipe)} />
                <MetaRow label="Sign-in" value={extractSignIn(scheduleRecipe?.recipe)} />
              </div>

              <div className="bk-sched-actions">
                <button
                  type="button"
                  className="dvw-btn dvw-btn--ghost"
                  onClick={() => void skipNext(selectedSchedule)}
                  disabled={skipBusy}
                >
                  {skipBusy ? 'Skipping…' : 'Skip next bake'}
                </button>
                <button
                  type="button"
                  className="dvw-btn dvw-btn--danger-ghost"
                  onClick={() => setCancelTarget(selectedSchedule)}
                >
                  Cancel all future bakes
                </button>
                <button
                  type="button"
                  className="dvw-btn"
                  onClick={() => openScheduleRecipe(selectedSchedule.recipeId)}
                >
                  <IconCode className="dvw-btn__icon" />
                  <span>Open recipe</span>
                </button>
              </div>
            </div>
          ) : null}
          {detailPhase === 'loading' ? (
            <div className="bk-status-block" role="status">
              <p className="tt-muted">Loading bake detail…</p>
            </div>
          ) : null}

          {detailPhase === 'error' ? (
            <div className="bk-status-block" role="status">
              <p className="tt-muted">Could not load this bake's detail. Refresh and try again.</p>
            </div>
          ) : null}

          {detailPhase === 'locked' ? (
            <div className="bk-status-block" role="status">
              <p className="tt-muted">PAX Cookbook is locked. Unlock it to review this bake.</p>
            </div>
          ) : null}

          {detailPhase === 'idle' && !selectedSchedule ? (
            <div className="tt-empty bk-empty">
              <IconList className="tt-empty__icon" />
              <h2 className="tt-empty__title">No bake selected</h2>
              <p className="tt-empty__body">
                Select a bake from the history to review its status, outputs, and log. This page
                does not start a bake.
              </p>
            </div>
          ) : null}

          {detailPhase === 'loaded' && detail ? (
            <div className="bk-detail">
              <header className="bk-detail__head">
                <div className="bk-detail__title-row">
                  <h2 className="tt-result__title">
                    {isResumeTrigger(detail.trigger)
                      ? 'Resume from checkpoint'
                      : detail.recipe?.name ?? detail.recipeId}
                  </h2>
                  {(detail.recipe?.recipeId ?? detail.recipeId) ? (
                    <button
                      type="button"
                      className="dvw-btn bk-detail__open-recipe"
                      onClick={() => {
                        const rid = detail.recipe?.recipeId ?? detail.recipeId;
                        if (rid) {
                          rememberPendingSelect(rid);
                          requestShellSection('recipes');
                        }
                      }}
                    >
                      <IconCode className="dvw-btn__icon" />
                      <span>Open Recipe</span>
                    </button>
                  ) : null}
                </div>
                <span className={'bk-status bk-status--' + normalizeStatus(detail.status)}>
                  <StatusGlyph status={detail.status} />
                  <span>{statusLabel(detail.status)}</span>
                </span>
              </header>

              {detailStatus === 'running' ? (
                <div className="bk-detail__actions">
                  <button
                    type="button"
                    className="dvw-btn dvw-btn--danger-ghost"
                    onClick={stopBake}
                    disabled={canceling}
                  >
                    {canceling ? 'Stopping…' : 'Stop bake'}
                  </button>
                  {cancelError ? (
                    <p className="bk-detail__cancel-error" role="status">
                      {cancelError}
                    </p>
                  ) : null}
                </div>
              ) : null}

              <StatusBanner tone={tone} headline={bannerCopy.headline} detail={bannerCopy.detail} />

              <div className="bk-metrics">
                <StatusCard
                  title="Duration"
                  state={durStr ?? '—'}
                  detail={detail.startedAt ? 'Start to finish' : 'Not started'}
                  tone="neutral"
                  icon="clock"
                />
                <StatusCard
                  title="Exit code"
                  state={detail.exitCode === null ? '—' : String(detail.exitCode)}
                  detail={
                    detail.exitCode === null
                      ? 'Not recorded'
                      : paxExitCodeNote(detail.exitCode) ??
                        (detail.exitCode === 0 ? 'Success' : 'Non-zero')
                  }
                  tone={
                    detail.exitCode === 0 ? 'ready' : detail.exitCode === null ? 'neutral' : 'attention'
                  }
                  icon={detail.exitCode === 0 ? 'check' : 'alert'}
                />
                <StatusCard
                  title="Output files"
                  state={String(outputs.length)}
                  detail={
                    outputs.length
                      ? formatBytes(outputs.reduce((s, o) => s + (o.sizeBytes ?? 0), 0)) + ' total'
                      : 'No files recorded'
                  }
                  tone="neutral"
                  icon="folder"
                />
                <StatusCard
                  title="Dashboard target"
                  titleHelp={
                    <span
                      className="bk-help-tip"
                      role="img"
                      aria-label="About the dashboard target"
                      title="The Power BI dashboard this bake's data is designed for. This value comes from the recipe's template selection. Custom and Entra-only recipes don't have a dashboard target."
                    >
                      ?
                    </span>
                  }
                  state={resolveDashboardLabel(
                    detail.recipe?.dashboard ?? extractRecipeDashboard(bakeRecipe?.recipe),
                  )}
                  detail="Power BI target"
                  tone="neutral"
                  icon="check"
                />
              </div>

              <div className="bk-section">
                <h3 className="bk-section__title">Output files</h3>
                {outputs.length > 0 ? (
                  <>
                    <ul className="bk-outputs">
                      {outputs.map((output, idx) => (
                        <OutputRow key={idx} output={output} />
                      ))}
                    </ul>
                    <p className="bk-outputs__total tt-muted">
                      Total: {formatBytes(outputs.reduce((s, o) => s + (o.sizeBytes ?? 0), 0))} across{' '}
                      {outputs.length} file(s)
                    </p>
                  </>
                ) : allOutputs.length > 0 ? (
                  <p className="tt-muted">
                    Output files were not found on disk. This may be because the bake did not
                    complete successfully, or the files were moved or deleted.
                  </p>
                ) : (
                  <p className="tt-muted">No output destinations recorded for this bake.</p>
                )}
                {detail.logPath ? (
                  <ul className="bk-outputs bk-outputs--log">
                    <li className="bk-output">
                      <span className="bk-output__role">log</span>
                      <span className="bk-output__path">{detail.logPath}</span>
                      <span className="bk-output__meta">Present</span>
                      <button
                        type="button"
                        className="bk-output__open"
                        onClick={() => void openFile(detail.logPath as string)}
                        title="Open this bake's log file in your default app"
                      >
                        Open log
                      </button>
                      <button
                        type="button"
                        className="bk-output__open"
                        onClick={() =>
                          void downloadBakeLogByCookId(
                            detail.cookId,
                            detail.recipe?.name ?? null,
                            detail.startedAt ?? detail.createdAt ?? null,
                          )
                        }
                        title="Download this bake's log file"
                      >
                        Download log
                      </button>
                    </li>
                  </ul>
                ) : null}
                <p className="bk-outputs__note tt-muted">
                  Output destinations are shown as metadata only — role, path, size, and whether
                  the file exists. Their contents are never read here.
                </p>
              </div>

              <CollapsibleSection title="Errors & warnings" defaultOpen={issueTone !== 'clean'}>
                <p className={'bk-issues bk-issues--' + issueTone}>
                  {issueTone === 'clean'
                    ? 'Clean run — no errors or warnings detected.'
                    : issueTone === 'warn'
                      ? `${logIssues.warnings.length + (readiness?.warnings?.length ?? 0)} warning(s) detected.`
                      : 'This run reported errors.' +
                        (logIssues.errors.length
                          ? ` ${logIssues.errors.length} error line(s) in the log.`
                          : '')}
                </p>

                {showCompletedNoData ? (
                  <p className="bk-section__line tt-muted">
                    Completed — no data was produced by this bake.
                  </p>
                ) : null}

                {showErrorSummary ? (
                  <div className="bk-section bk-section--error">
                    <h3 className="bk-section__title">What went wrong</h3>
                    {friendlyErr ? (
                      <>
                        <p className="bk-section__line">{friendlyErr.title}</p>
                        <p className="bk-section__line tt-muted">{friendlyErr.help}</p>
                      </>
                    ) : (
                      <>
                        {errorSummary?.errorClass ? (
                          <p className="bk-section__line">{errorSummary.errorClass}</p>
                        ) : null}
                        {errorSummary?.errorMessage ? (
                          <p className="bk-section__line tt-muted">{errorSummary.errorMessage}</p>
                        ) : null}
                      </>
                    )}
                    {errorSummary?.errorClass || errorSummary?.closureReason ? (
                      <p className="bk-section__line tt-muted">
                        Error code:{' '}
                        <code>{errorSummary?.errorClass ?? errorSummary?.closureReason}</code>
                        {detail.logPath
                          ? ' \u2014 open or download the log below to share with support.'
                          : ''}
                      </p>
                    ) : null}
                  </div>
                ) : null}

                {readiness?.warnings?.length ? (
                  <div className="bk-section">
                    <h3 className="bk-section__title">Readiness warnings at start</h3>
                    <ul className="bk-reqs">
                      {readiness.warnings.map((warn, idx) => (
                        <li key={idx} className="bk-reqs__item">
                          {warn}
                        </li>
                      ))}
                    </ul>
                  </div>
                ) : null}

                {logIssues.errors.length || logIssues.warnings.length ? (
                  <details className="bk-collapse bk-collapse--nested">
                    <summary className="bk-collapse__summary">
                      Log lines mentioning errors / warnings (heuristic)
                    </summary>
                    <div className="bk-collapse__body">
                      <ul className="bk-issues__lines">
                        {logIssues.errors.map((line, idx) => (
                          <li key={'e' + idx}>{line}</li>
                        ))}
                        {logIssues.warnings.map((line, idx) => (
                          <li key={'w' + idx}>{line}</li>
                        ))}
                      </ul>
                      <p className="tt-muted">
                        Heuristic scan of the redacted run log — may include lines that mention
                        'error'/'warning' in passing.
                      </p>
                    </div>
                  </details>
                ) : null}
              </CollapsibleSection>

              <CollapsibleSection title="Run configuration">
                <div className="bk-meta">
                  <MetaRow label="Recipe" value={detail.recipe?.name ?? detail.recipeId} />
                  {detail.trigger ? (
                    <MetaRow label="Trigger" value={<TriggerChip trigger={detail.trigger} />} />
                  ) : null}
                  <MetaRow
                    label="Sign-in mode"
                    value={readiness?.authMode ?? detail.recipe?.authMode ?? '—'}
                  />
                  <MetaRow
                    label="Dashboard target"
                    value={resolveDashboardLabel(
                      detail.recipe?.dashboard ?? extractRecipeDashboard(bakeRecipe?.recipe),
                    )}
                  />
                  <MetaRow
                    label="Started"
                    value={detail.startedAt ? formatModified(detail.startedAt) : '—'}
                  />
                  <MetaRow
                    label="Finished"
                    value={detail.finishedAt ? formatModified(detail.finishedAt) : '—'}
                  />
                  {readiness ? (
                    <MetaRow label="Readiness at start" value={readiness.status} />
                  ) : null}
                  {readiness?.engineStatus ? (
                    <MetaRow label="Engine readiness" value={readiness.engineStatus} />
                  ) : null}
                  {typeof readiness?.chefKeyBound === 'boolean' ? (
                    <MetaRow label="Sign-in bound" value={readiness.chefKeyBound ? 'Yes' : 'No'} />
                  ) : null}
                  {detail.closureReason ? (
                    <MetaRow label="Closure" value={detail.closureReason} />
                  ) : null}
                  <MetaRow label="PAX engine version" value={detail.engine?.version ?? '—'} />
                  {detail.engine?.sha256 ? (
                    <MetaRow
                      label="Engine fingerprint"
                      value={
                        <span className="bk-engine-fp">
                          <code className="bk-inline-code">
                            {detail.engine.sha256.slice(0, 12)}…
                          </code>
                          <button
                            type="button"
                            className="bk-engine-fp__copy"
                            onClick={() => void copyEngineSha()}
                          >
                            {shaCopied ? 'Copied' : 'Copy'}
                          </button>
                        </span>
                      }
                    />
                  ) : null}
                  {detail.engine?.path ? (
                    <MetaRow
                      label="Engine path"
                      value={<span className="bk-path">{detail.engine.path}</span>}
                    />
                  ) : null}
                </div>

                {requirements.length > 0 ? (
                  <ul className="bk-reqs">
                    {requirements.map((req, idx) => (
                      <li key={idx} className="bk-reqs__item">
                        {req}
                      </li>
                    ))}
                  </ul>
                ) : null}

                <div className="bk-section">
                  <h3 className="bk-section__title">
                    <IconCode className="bk-section__icon" /> Command (redacted)
                  </h3>
                  <code className="bk-code">
                    {detail.commandRedacted ?? '(no command recorded)'}
                  </code>
                  <p className="bk-section__line tt-muted">
                    Granular run arguments (date range, rollup, output path) are shown in the
                    redacted command above; values that look like secrets are removed.
                  </p>
                </div>

                <p className="bk-future tt-muted">
                  Not captured in the current bake record (future broker enhancement): records
                  processed, Entra directory user count and partial/complete status, and Graph API
                  paging / retry counts. Surfacing these would require the broker to record
                  additional run metadata.
                </p>
              </CollapsibleSection>

              {detail.checkpoint ? (
                <CollapsibleSection title="Checkpoint">
                  <div className="bk-meta">
                    <MetaRow
                      label="Path"
                      value={<span className="bk-path">{detail.checkpoint.path}</span>}
                    />
                    <MetaRow label="Force" value={detail.checkpoint.force ? 'Yes' : 'No'} />
                  </div>
                </CollapsibleSection>
              ) : null}

              <div className="bk-section bk-log">
                <header className="bk-log__head">
                  <h3 className="bk-section__title">Bake log</h3>
                  {logPhase === 'available' ? (
                    <div className="bk-log__controls">
                      <label className="bk-log__autoscroll">
                        <input
                          type="checkbox"
                          checked={autoScroll}
                          onChange={e => setAutoScroll(e.target.checked)}
                        />
                        <span>Auto-scroll</span>
                      </label>
                      <button
                        type="button"
                        className="dvw-btn dvw-btn--ghost"
                        onClick={() => void copyLog()}
                      >
                        <IconCopy className="dvw-btn__icon" />
                        <span>{logCopied ? 'Copied' : 'Copy log'}</span>
                      </button>
                      <button
                        type="button"
                        className="dvw-btn dvw-btn--ghost"
                        onClick={() =>
                          void downloadBakeLogText(
                            logText,
                            detail?.recipe?.name ?? null,
                            detail?.startedAt ?? detail?.createdAt ?? null,
                          )
                        }
                      >
                        <IconDownload className="dvw-btn__icon" />
                        <span>Download log</span>
                      </button>
                    </div>
                  ) : null}
                </header>
                {logPhase === 'loading' ? <p className="tt-muted">Loading log…</p> : null}
                {logPhase === 'available' ? (
                  <pre
                    className="bk-log__pre"
                    tabIndex={0}
                    ref={logPreRef}
                    onScroll={handleLogScroll}
                  >
                    {logText}
                  </pre>
                ) : null}
                {logPhase === 'empty' ? (
                  <p className="tt-muted">No log was recorded for this bake.</p>
                ) : null}
                {logPhase === 'locked' ? (
                  <p className="tt-muted">PAX Cookbook is locked. Unlock it to read this log.</p>
                ) : null}
                {logPhase === 'error' ? (
                  <p className="tt-muted">Could not read this bake's log. Refresh and try again.</p>
                ) : null}
              </div>
            </div>
          ) : null}
        </section>
      </div>

      {skipToast ? (
        <div className="bk-toast" role="status" aria-live="polite">
          {skipToast}
        </div>
      ) : null}

      {cancelTarget ? (
        <div className="close-modal" role="presentation">
          <div
            className="close-modal__scrim"
            role="presentation"
            onClick={() => (cancelBusy ? undefined : setCancelTarget(null))}
          />
          <div
            className="close-modal__dialog"
            role="dialog"
            aria-modal="true"
            aria-labelledby="cancel-sched-title"
            aria-describedby="cancel-sched-body"
          >
            <h2 id="cancel-sched-title" className="close-modal__title">
              Cancel all future bakes?
            </h2>
            <p id="cancel-sched-body" className="close-modal__body">
              This will cancel all future scheduled bakes for{' '}
              <strong>{cancelTarget.name || cancelTarget.recipeId}</strong>. You can set up a
              new schedule anytime from the recipe builder.
            </p>
            <div className="close-modal__actions">
              <button
                type="button"
                className="close-modal__btn"
                onClick={() => setCancelTarget(null)}
                disabled={cancelBusy}
              >
                Keep schedule
              </button>
              <button
                type="button"
                className="close-modal__btn close-modal__btn--danger"
                onClick={() => void confirmCancelAll()}
                disabled={cancelBusy}
              >
                {cancelBusy ? 'Canceling…' : 'Cancel schedule'}
              </button>
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}
