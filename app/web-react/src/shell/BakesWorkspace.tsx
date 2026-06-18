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
  listCooks,
  openFile,
  openPath,
  stopCook,
  type CookDetailBody,
  type CookOutputSummary,
  type CookSummary,
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
  IconRefresh,
} from './CookbookIllustrations';
import { downloadBakeLogByCookId, downloadBakeLogText } from './logDownload';
import {
  formatModified,
  rememberPendingSelect,
  requestShellSection,
  takePendingBakeSelect,
} from './shellNav';
import { userScrolledAway } from './bakesLogScroll';

type ListPhase = 'loading' | 'loaded' | 'error' | 'locked';
type DetailPhase = 'idle' | 'loading' | 'loaded' | 'error' | 'locked';
type LogPhase = 'idle' | 'loading' | 'available' | 'empty' | 'error' | 'locked';

// While a bake is running the history, the selected detail, AND the selected
// cook.log are quietly re-polled on this interval, so the log live-tails every
// 2-3 seconds without the user refreshing.
const AUTO_REFRESH_MS = 2500;

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

export function BakesWorkspace() {
  const [cooks, setCooks] = useState<CookSummary[]>([]);
  const [listPhase, setListPhase] = useState<ListPhase>('loading');
  // Visual-only "Clear History": empties the displayed list for this session.
  // It never deletes a cook record, log, or file from the broker — a refresh,
  // the live-tail poll, or a remount repopulates the list from the broker.
  const [historyCleared, setHistoryCleared] = useState(false);

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
      setHistoryCleared(false);
      setListPhase('loaded');
    } else if (res.status === 423) {
      setListPhase('locked');
    } else if (!background) {
      setCooks([]);
      setListPhase('error');
    }
  }, []);

  const clearHistory = useCallback(() => {
    // Visual only — empties the displayed list. No broker mutation, no record
    // or log deletion. The next load (refresh, poll, or remount) repopulates.
    setCooks([]);
    setHistoryCleared(true);
  }, []);

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
      setSelectedCookId(cookId);
      selectedCookIdRef.current = cookId;
      setCanceling(false);
      setCancelError(null);
      void loadDetail(cookId);
      void loadLog(cookId);
    },
    [loadDetail, loadLog],
  );

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

  useEffect(() => {
    void loadCooks();
  }, [loadCooks]);

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

  const refreshAll = useCallback(() => {
    void loadCooks();
    const current = selectedCookIdRef.current;
    if (current) {
      void loadDetail(current);
      void loadLog(current);
    }
  }, [loadCooks, loadDetail, loadLog]);

  const errorSummary = detail?.errorSummary ?? null;
  const detailStatus = normalizeStatus(detail?.status);
  const isFailedRun = detailStatus === 'errored' || detailStatus === 'interrupted';
  const showErrorSummary =
    isFailedRun &&
    errorSummary !== null &&
    Boolean(errorSummary.errorClass || errorSummary.errorMessage || errorSummary.closureReason);
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
        {/* Left: real bake history read from the broker */}
        <section className="dvw-card tt-pane bk-pane" aria-label="Bake history">
          <header className="tt-pane__head">
            <h2 className="tt-pane__title">Bake history</h2>
            <div className="tt-pane__head-actions">
              <button
                type="button"
                className="dvw-btn dvw-btn--ghost"
                onClick={clearHistory}
                disabled={cooks.length === 0}
                title="Clear this view only — your bake records and logs are not deleted."
              >
                Clear History
              </button>
              <button
                type="button"
                className="dvw-btn dvw-btn--icon"
                onClick={refreshAll}
                aria-label="Refresh"
              >
                <IconRefresh />
              </button>
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

          {listPhase === 'loaded' && cooks.length > 0 ? (
            <ul className="tt-list bk-history-list" role="listbox" aria-label="Bake history">
              {cooks.map(cook => {
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

          {listPhase === 'loaded' && cooks.length === 0 ? (
            <div className="tt-empty bk-empty">
              <IconClock className="tt-empty__icon" />
              <h3 className="tt-empty__title">
                {historyCleared ? 'Bake history cleared' : 'No bakes recorded yet'}
              </h3>
              <p className="tt-empty__body">
                {historyCleared
                  ? 'This view was cleared for the session. Your bake records and logs were not deleted — refresh to reload them.'
                  : 'No bakes have been recorded on this PC. This page reviews real bake history — status, outputs, and logs — when records exist. It does not start a bake: prepare recipes in Recipes and preflight them in Taste Tests.'}
              </p>
            </div>
          ) : null}
        </section>

        {/* Center: selected bake detail, outputs, and log */}
        <section className="dvw-card tt-pane tt-pane--main bk-pane" aria-label="Bake detail">
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

          {detailPhase === 'idle' ? (
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
                  state={
                    detail.recipe?.dashboard === 'aibv'
                      ? 'AI Business Value'
                      : detail.recipe?.dashboard === 'aio'
                        ? 'AI-in-One'
                        : detail.recipe?.dashboard ?? '—'
                  }
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
                    This bake&rsquo;s output files are no longer on disk.
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
                    <h3 className="bk-section__title">Error</h3>
                    {errorSummary?.errorClass ? (
                      <p className="bk-section__line">{errorSummary.errorClass}</p>
                    ) : null}
                    {errorSummary?.errorMessage ? (
                      <p className="bk-section__line tt-muted">{errorSummary.errorMessage}</p>
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
                    value={
                      detail.recipe?.dashboard === 'aibv'
                        ? 'AI Business Value'
                        : detail.recipe?.dashboard === 'aio'
                          ? 'AI-in-One'
                          : detail.recipe?.dashboard ?? '—'
                    }
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
    </div>
  );
}
