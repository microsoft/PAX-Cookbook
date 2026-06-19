/**
 * Desktop Home dashboard.
 *
 * Replaces the old long-page Home placeholder with a compact desktop workspace
 * dashboard: a welcome header, a command row, a row of readiness status cards,
 * and three supporting panels (Recent Recipes, What needs attention, Quick
 * Tips). It reads the saved recipe list (read-only) to populate the recent and
 * attention panels; it never runs PAX, bakes, schedules, or reads a secret.
 */
import { useEffect, useMemo, useRef, useState } from 'react';
import { listRecipes, listCooks, getCook, openPath, openFile } from '../host/brokerBridge';
import type {
  RecipeSummary,
  CookSummary,
  CookDetailBody,
  CookOutputSummary,
} from '../host/brokerBridge';
import {
  requestShellSection,
  rememberPendingSelect,
  rememberPendingBakeSelect,
  rememberPendingDraft,
  rememberPendingImportCommand,
  formatModified,
} from './shellNav';
import { RecipeStatusBadge, toneForRecipeStatus } from './StatusCard';
import { listChefKeys } from '../host/chefKeys';
import { SectionHeader } from './components/SectionHeader';
import {
  IconPlus,
  IconFolder,
  IconBook,
  IconChevronRight,
  IconChevronDown,
  IconAlertCircle,
  IconCheckCircle,
  IconDownload,
  IconRefresh,
} from './CookbookIllustrations';
import { pickAndParseRecipeFile } from '../features/mini-kitchen/lib/recipeFileImport';
import { downloadBakeLogByCookId } from './logDownload';

type ListPhase = 'idle' | 'loading' | 'loaded' | 'error';

function recipeName(recipe: RecipeSummary): string {
  return typeof recipe.name === 'string' && recipe.name.trim().length > 0
    ? recipe.name.trim()
    : 'Untitled recipe';
}

const COOK_STATUS_LABELS: Record<string, string> = {
  running: 'Running',
  completed: 'Completed',
  errored: 'Failed',
  canceled: 'Canceled',
  interrupted: 'Interrupted',
  unknown: 'Unknown',
};

function normalizeCookStatus(raw: string | null | undefined): string {
  const s = (raw ?? '').toLowerCase();
  if (s === 'cancelled') {
    return 'canceled';
  }
  return Object.prototype.hasOwnProperty.call(COOK_STATUS_LABELS, s) ? s : 'unknown';
}

type BakeBadgeTone = 'success' | 'warning' | 'error' | 'running' | 'neutral';

/** Color-coded badge tone + label for a cook from its status + exit code. A
 *  completed run with a non-zero PAX exit code reads as "Completed with
 *  warnings" (amber); a clean completion is green; errored / interrupted are
 *  red; running is blue; canceled is amber. */
function bakeBadge(
  status: string | null | undefined,
  exitCode: number | null | undefined,
): { tone: BakeBadgeTone; label: string } {
  const s = normalizeCookStatus(status);
  if (s === 'running') {
    return { tone: 'running', label: 'Running' };
  }
  if (s === 'errored' || s === 'interrupted') {
    return { tone: 'error', label: 'Failed' };
  }
  if (s === 'canceled') {
    return { tone: 'warning', label: 'Canceled' };
  }
  if (s === 'completed') {
    if (typeof exitCode === 'number' && exitCode !== 0) {
      return { tone: 'warning', label: 'Completed with warnings' };
    }
    return { tone: 'success', label: 'Completed' };
  }
  return { tone: 'neutral', label: COOK_STATUS_LABELS[s] };
}

function formatBakeDuration(seconds: number | null | undefined): string | null {
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

function formatBakeBytes(bytes: number | null | undefined): string | null {
  if (bytes === null || bytes === undefined || !isFinite(bytes) || bytes < 0) {
    return null;
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

function baseName(path: string | null | undefined): string | null {
  if (!path) {
    return null;
  }
  const parts = path.split(/[\\/]/).filter(Boolean);
  return parts.length > 0 ? parts[parts.length - 1] : null;
}

function folderOf(path: string | null | undefined): string | null {
  if (!path) {
    return null;
  }
  const idx = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
  return idx > 0 ? path.slice(0, idx) : null;
}

const DASHBOARD_LABELS: Record<string, string> = {
  aio: 'AI-in-One',
  aibv: 'AI Business Value',
};

function dashboardLabel(value: string | null | undefined): string | null {
  if (!value) {
    return null;
  }
  return DASHBOARD_LABELS[value.toLowerCase()] ?? null;
}

/** "June 12, 2026 at 3:42 PM" from an ISO timestamp; null when unparseable. */
function formatBakeDateTime(iso: string | null | undefined): string | null {
  if (!iso) {
    return null;
  }
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) {
    return null;
  }
  const date = d.toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
  });
  const time = d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  return `${date} at ${time}`;
}

/** Compact relative label like "3 days ago"; null when unparseable or future. */
function relativeBakeTime(iso: string | null | undefined): string | null {
  if (!iso) {
    return null;
  }
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) {
    return null;
  }
  const deltaMs = Date.now() - d.getTime();
  if (deltaMs < 0) {
    return null;
  }
  const minutes = Math.floor(deltaMs / 60000);
  if (minutes < 1) {
    return 'just now';
  }
  if (minutes < 60) {
    return `${minutes} minute${minutes === 1 ? '' : 's'} ago`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours} hour${hours === 1 ? '' : 's'} ago`;
  }
  const days = Math.floor(hours / 24);
  if (days < 30) {
    return `${days} day${days === 1 ? '' : 's'} ago`;
  }
  const months = Math.floor(days / 30);
  return `${months} month${months === 1 ? '' : 's'} ago`;
}

/** Pick the primary output of a finished cook: prefer the dashboard "fact"
 *  CSV, then the first existing output, then the first listed output. */
function primaryOutput(
  outputs: readonly CookOutputSummary[] | null | undefined,
): CookOutputSummary | null {
  if (!outputs || outputs.length === 0) {
    return null;
  }
  const fact = outputs.find(o => (o.role ?? '').toLowerCase() === 'fact');
  if (fact) {
    return fact;
  }
  const present = outputs.find(o => o.exists);
  return present ?? outputs[0];
}

/** Choose the folder to reveal for a bake's outputs in File Explorer: prefer
 *  the Purview audit ("fact") CSV's folder, then the Entra user-info CSV's
 *  folder, then any existing output's folder, then the first output that has a
 *  path. Returns null when no output path is known. */
function bakeOutputFolder(
  outputs: readonly CookOutputSummary[] | null | undefined,
): string | null {
  if (!outputs || outputs.length === 0) {
    return null;
  }
  const byRole = (role: string) =>
    outputs.find(o => (o.role ?? '').toLowerCase() === role && o.exists && !!o.path);
  const fact = byRole('fact');
  if (fact) {
    return folderOf(fact.path);
  }
  const userInfo = byRole('userinfo');
  if (userInfo) {
    return folderOf(userInfo.path);
  }
  const present = outputs.find(o => o.exists && !!o.path);
  if (present) {
    return folderOf(present.path);
  }
  const anyPath = outputs.find(o => !!o.path);
  return anyPath ? folderOf(anyPath.path) : null;
}

function asString(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

/** "June 12, 2026" from an ISO timestamp; null when unparseable. */
function formatBakeDateShort(iso: string | null | undefined): string | null {
  if (!iso) {
    return null;
  }
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) {
    return null;
  }
  return d.toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
  });
}

/** One Recent Outputs row, derived from a recent cook's discovered outputs. */
interface RecentOutput {
  key: string;
  name: string;
  size: string | null;
  date: string | null;
  dashboard: string | null;
  folder: string | null;
  logPath: string | null;
  cookId: string;
  recipeName: string | null;
  when: string | null;
}

/** Flatten the most recent cooks' existing output files into display rows
 *  (newest first), capped so a long history can never balloon the render. */
function buildRecentOutputs(
  cooks: readonly CookSummary[],
  detailByCook: Record<string, CookDetailBody>,
): RecentOutput[] {
  const rows: RecentOutput[] = [];
  for (const cook of cooks) {
    const detail = detailByCook[cook.cookId];
    if (!detail || !detail.outputs) {
      continue;
    }
    const dash = dashboardLabel(detail.recipe?.dashboard ?? null);
    const date = formatBakeDateShort(cook.startedAt ?? cook.createdAt);
    // One folder per bake (Purview → Entra → any) and the bake's log path, shared
    // by every output row of this cook so "Open folder"/"Open log" are consistent.
    const cookFolder = bakeOutputFolder(detail.outputs.outputs);
    const cookLogPath = detail.logPath ?? null;
    for (const o of detail.outputs.outputs) {
      if (!o.exists || !o.path) {
        continue;
      }
      const name = baseName(o.path);
      if (!name) {
        continue;
      }
      rows.push({
        key: cook.cookId + ':' + o.role + ':' + name,
        name,
        size: formatBakeBytes(o.sizeBytes ?? null),
        date,
        dashboard: dash,
        folder: cookFolder,
        logPath: cookLogPath,
        cookId: cook.cookId,
        recipeName: cook.recipeName,
        when: cook.startedAt ?? cook.createdAt,
      });
      if (rows.length >= 6) {
        return rows;
      }
    }
  }
  return rows;
}

type RunTone = 'success' | 'error' | 'warning' | 'muted';

/** A recipe's last-run summary for the Recent Recipes list, cross-referencing
 *  the recipe's recorded last cook id against the loaded cook history. */
function lastRunSummary(
  recipe: RecipeSummary,
  cookById: Record<string, CookSummary>,
): { text: string; tone: RunTone } {
  const lastCookedAt = asString(recipe['lastCookedAt']);
  if (!lastCookedAt) {
    return { text: 'Never run', tone: 'muted' };
  }
  const when = formatModified(lastCookedAt);
  const lastCookId = asString(recipe['lastCookId']);
  const cook = lastCookId ? cookById[lastCookId] ?? null : null;
  if (!cook) {
    return { text: `Last run ${when}`, tone: 'muted' };
  }
  const b = bakeBadge(cook.status, cook.exitCode);
  if (b.tone === 'success') {
    return { text: `Last run ${when} — ✓ Success`, tone: 'success' };
  }
  if (b.tone === 'error') {
    return { text: `Last run ${when} — ✗ Failed`, tone: 'error' };
  }
  return { text: `Last run ${when} — ${b.label}`, tone: 'warning' };
}

/** One dynamic "what needs attention" alert. */
interface HomeAlert {
  id: string;
  tone: 'error' | 'warning' | 'info';
  title: string;
  detail: string;
  onClick: () => void;
}

// Cooking-themed Home greetings. One is chosen at random each time the Home page
// mounts — DesktopHome remounts on every navigation back to Home, so the header
// changes on each visit. useMemo with an empty dependency list picks once per
// mount and stays stable across this component's frequent re-renders, so the
// greeting never changes while the user is looking at the page.
const HOME_GREETINGS = [
  "What's cooking?",
  'Time to cook the books',
  'Ready to bake',
  "Kitchen's open",
  "Let's get baking",
  'Fresh out of the oven',
  'Order up!',
  'Mise en place',
  "Now we're cooking",
  'Recipe for success',
  'Audits are on the menu',
  'Serving up insights',
] as const;

export function DesktopHome() {
  const greeting = useMemo(
    () => HOME_GREETINGS[Math.floor(Math.random() * HOME_GREETINGS.length)],
    [],
  );
  const [recipes, setRecipes] = useState<readonly RecipeSummary[]>([]);
  const [phase, setPhase] = useState<ListPhase>('idle');
  const [cooks, setCooks] = useState<readonly CookSummary[]>([]);
  const [detailByCook, setDetailByCook] = useState<Record<string, CookDetailBody>>({});
  const [cooksPhase, setCooksPhase] = useState<ListPhase>('idle');
  const [chefKeyCount, setChefKeyCount] = useState<number | null>(null);
  const [importMenuOpen, setImportMenuOpen] = useState(false);
  const [importError, setImportError] = useState<string | null>(null);
  const importMenuRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    let cancelled = false;
    setPhase('loading');
    void listRecipes()
      .then(res => {
        if (cancelled) {
          return;
        }
        if (res.ok && res.data) {
          setRecipes(res.data.recipes ?? []);
          setPhase('loaded');
        } else {
          setPhase('error');
        }
      })
      .catch(() => {
        if (!cancelled) {
          setPhase('error');
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // Load cook history (read-only) for the Last Bake card, the per-recipe
  // last-run status, and the Recent Outputs list. The newest cooks' details
  // (output files + dashboard target) are fetched in parallel; all of it is
  // best-effort and additive — a failure just leaves a section empty.
  useEffect(() => {
    let cancelled = false;
    setCooksPhase('loading');
    void listCooks()
      .then(async res => {
        if (cancelled) {
          return;
        }
        if (!res.ok || !res.data) {
          setCooksPhase('error');
          return;
        }
        const list = res.data.cooks ?? [];
        setCooks(list);
        setCooksPhase('loaded');
        const top = list.slice(0, 5);
        const details = await Promise.all(
          top.map(c =>
            getCook(c.cookId)
              .then(d => (d.ok && d.data ? d.data : null))
              .catch(() => null),
          ),
        );
        if (cancelled) {
          return;
        }
        const map: Record<string, CookDetailBody> = {};
        for (const d of details) {
          if (d) {
            map[d.cookId] = d;
          }
        }
        setDetailByCook(map);
      })
      .catch(() => {
        if (!cancelled) {
          setCooksPhase('error');
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  // Chef's Keys count (read-only) for the "no authentication keys" alert. Left
  // null on error so the alert is suppressed rather than shown spuriously.
  useEffect(() => {
    let cancelled = false;
    void listChefKeys()
      .then(res => {
        if (!cancelled && res.ok && res.data) {
          setChefKeyCount(res.data.chefKeys.length);
        }
      })
      .catch(() => {
        /* leave chefKeyCount null */
      });
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (!importMenuOpen) {
      return;
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        setImportMenuOpen(false);
      }
    }
    function onPointerDown(event: MouseEvent) {
      const wrap = importMenuRef.current;
      if (wrap && event.target instanceof Node && !wrap.contains(event.target)) {
        setImportMenuOpen(false);
      }
    }
    document.addEventListener('keydown', onKeyDown);
    document.addEventListener('mousedown', onPointerDown);
    return () => {
      document.removeEventListener('keydown', onKeyDown);
      document.removeEventListener('mousedown', onPointerDown);
    };
  }, [importMenuOpen]);

  const cookById: Record<string, CookSummary> = {};
  for (const c of cooks) {
    cookById[c.cookId] = c;
  }
  const recipeDashboard: Record<string, string> = {};
  for (const cookId of Object.keys(detailByCook)) {
    const d = detailByCook[cookId];
    const rid = d.recipe?.recipeId ?? d.recipeId;
    const dash = d.recipe?.dashboard ?? null;
    if (rid && dash && !(rid in recipeDashboard)) {
      recipeDashboard[rid] = dash;
    }
  }
  const lastCook = cooks.length > 0 ? cooks[0] : null;
  const lastDetail = lastCook ? detailByCook[lastCook.cookId] ?? null : null;
  const recentOutputs = buildRecentOutputs(cooks, detailByCook);

  function openRecipe(recipe: RecipeSummary) {
    rememberPendingSelect(recipe.recipeId);
    requestShellSection('recipes');
  }

  // "Run again" opens the recipe in the builder (Home never starts a bake — the
  // user clicks Bake there). "View in Bakes" hands the cook to the Bakes surface.
  function runAgain(recipeId: string) {
    rememberPendingSelect(recipeId);
    requestShellSection('recipes');
  }

  function viewInBakes(cookId: string) {
    rememberPendingBakeSelect(cookId);
    requestShellSection('bakes');
  }

  // Open the Windows folder that holds a bake's output files in File Explorer
  // (best-effort: the broker opens an existing folder only, never a file).
  function openOutputFolder(path: string) {
    void openPath(path);
  }

  // Open a bake's managed cook.log in the user's default app (best-effort: the
  // broker opens only an existing allowlisted document file, never an
  // executable).
  function openLogFile(path: string) {
    void openFile(path);
  }

  function scrollToAttention() {
    const el = document.getElementById('dvw-attn-h');
    if (el) {
      el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
  }

  // Dynamic "what needs attention" alerts, surfaced only from data already
  // loaded: a failed last bake, missing auth keys, and recipes whose readiness
  // still needs work. A recipe that has simply never been baked is NOT an issue
  // (it is just a new recipe), so it never appears here. (An engine-update alert
  // is intentionally omitted — no client-side update signal is available without
  // a new broker call.)
  const alerts: HomeAlert[] = [];
  if (lastCook && bakeBadge(lastCook.status, lastCook.exitCode).tone === 'error') {
    const failedCookId = lastCook.cookId;
    alerts.push({
      id: 'failed-bake',
      tone: 'error',
      title: 'Your last bake failed',
      detail: 'Open it in Bakes to see what went wrong.',
      onClick: () => viewInBakes(failedCookId),
    });
  }
  if (chefKeyCount === 0) {
    alerts.push({
      id: 'no-keys',
      tone: 'warning',
      title: 'No authentication keys set up',
      detail: "Add a Chef's Key before you run a bake.",
      onClick: () => requestShellSection('chefskeys'),
    });
  }
  for (const r of recipes) {
    if (toneForRecipeStatus(r['status']) === 'attention') {
      alerts.push({
        id: 'attn-' + r.recipeId,
        tone: 'warning',
        title: recipeName(r),
        detail: "Open to review what's missing before a run.",
        onClick: () => openRecipe(r),
      });
    }
  }
  const attentionCount = alerts.length;

  // Open a native file-browse dialog, parse the chosen .pax/.paxlite file, and
  // open the recipe builder pre-populated with its settings. Shared by the
  // Import Recipe menu and the "Open recipe from file" link.
  async function startImport() {
    setImportMenuOpen(false);
    setImportError(null);
    const outcome = await pickAndParseRecipeFile();
    if (!outcome.ok) {
      if (outcome.cancelled) {
        return;
      }
      setImportError(outcome.error);
      return;
    }
    rememberPendingDraft({
      templateId: 'importedRecipeFile',
      templateName: 'Imported recipe',
      note: `Imported from ${outcome.fileName}. Review the settings, then Save when it is ready.`,
      state: outcome.state,
    });
    requestShellSection('recipes');
  }

  return (
    <div className="dvw dvw-home">
      <SectionHeader
        headingLevel="h1"
        title={greeting}
        helpTopic="cookbookHome"
        accent="var(--c-blue)"
        lede="Your Copilot analytics dashboard toolkit — pull data, run exports, and keep your dashboards fresh."
      />

      <div className="dvw-commandbar" role="group" aria-label="Quick actions">
        <button
          type="button"
          className="dvw-btn dvw-btn--primary"
          onClick={() => requestShellSection('recipes')}
        >
          <IconPlus className="dvw-btn__icon" />
          <span>New Recipe</span>
        </button>
        <button
          type="button"
          className="dvw-btn"
          onClick={() => requestShellSection('recipes')}
        >
          <IconFolder className="dvw-btn__icon" />
          <span>Open Recipe</span>
        </button>
        <div className="dvw-import-menu" ref={importMenuRef}>
          <button
            type="button"
            className="dvw-btn"
            aria-haspopup="menu"
            aria-expanded={importMenuOpen}
            onClick={() => setImportMenuOpen(o => !o)}
          >
            <IconFolder className="dvw-btn__icon" />
            <span>Import Recipe</span>
            <IconChevronDown className="dvw-btn__chevron" />
          </button>
          {importMenuOpen ? (
            <div className="dvw-import-menu__list" role="menu">
              <button
                type="button"
                role="menuitem"
                className="dvw-import-menu__item"
                onClick={() => { void startImport(); }}
              >
                Import PAX Cookbook .pax Recipe
              </button>
              <button
                type="button"
                role="menuitem"
                className="dvw-import-menu__item"
                onClick={() => { void startImport(); }}
              >
                Import Mini-Kitchen .paxlite Recipe
              </button>
              <button
                type="button"
                role="menuitem"
                className="dvw-import-menu__item"
                onClick={() => {
                  setImportMenuOpen(false);
                  rememberPendingImportCommand();
                  requestShellSection('recipes');
                }}
              >
                Import Command
              </button>
            </div>
          ) : null}
        </div>
      </div>

      {importError ? (
        <p
          role="alert"
          style={{ color: '#b42318', margin: '8px 0 0', fontSize: '0.875rem' }}
        >
          {importError}
        </p>
      ) : null}

      <LastBakePanel
        phase={cooksPhase}
        cook={lastCook}
        detail={lastDetail}
        onRunAgain={runAgain}
        onViewInBakes={viewInBakes}
        onOpenFolder={openOutputFolder}
        onOpenLog={openLogFile}
        onNewRecipe={() => requestShellSection('recipes')}
      />

      <SystemHealth count={attentionCount} onShowAttention={scrollToAttention} />

      <div className="dvw-home__panels">
        <div className="dvw-home__col">
          <RecentRecipesPanel
            phase={phase}
            recipes={recipes}
            cookById={cookById}
            recipeDashboard={recipeDashboard}
            onOpen={openRecipe}
            onViewAll={() => requestShellSection('recipes')}
          />
          <AttentionPanel phase={phase} alerts={alerts} />
        </div>
        <RecentOutputsPanel
          phase={cooksPhase}
          outputs={recentOutputs}
          onOpenFolder={openOutputFolder}
          onOpenLog={openLogFile}
          onViewInBakes={viewInBakes}
        />
      </div>
    </div>
  );
}

function LastBakePanel({
  phase,
  cook,
  detail,
  onRunAgain,
  onViewInBakes,
  onOpenFolder,
  onOpenLog,
  onNewRecipe,
}: {
  phase: ListPhase;
  cook: CookSummary | null;
  detail: CookDetailBody | null;
  onRunAgain: (recipeId: string) => void;
  onViewInBakes: (cookId: string) => void;
  onOpenFolder: (path: string) => void;
  onOpenLog: (path: string) => void;
  onNewRecipe: () => void;
}) {
  if (phase === 'idle' || phase === 'loading') {
    return (
      <section className="dvw-card dvw-lastbake" aria-labelledby="dvw-lastbake-h">
        <header className="dvw-card__head">
          <h2 id="dvw-lastbake-h" className="dvw-card__title">
            Last Bake
          </h2>
        </header>
        <p className="dvw-card__muted" role="status">
          Loading your most recent bake…
        </p>
      </section>
    );
  }

  if (phase === 'error') {
    return (
      <section className="dvw-card dvw-lastbake" aria-labelledby="dvw-lastbake-h">
        <header className="dvw-card__head">
          <h2 id="dvw-lastbake-h" className="dvw-card__title">
            Last Bake
          </h2>
        </header>
        <p className="dvw-card__muted" role="status">
          Your first bake results will appear here after you run a recipe.
        </p>
      </section>
    );
  }

  if (!cook) {
    return (
      <section
        className="dvw-card dvw-lastbake dvw-lastbake--empty"
        aria-labelledby="dvw-lastbake-h"
      >
        <header className="dvw-card__head">
          <h2 id="dvw-lastbake-h" className="dvw-card__title">
            Last Bake
          </h2>
        </header>
        <div className="dvw-lastbake__empty">
          <p className="dvw-card__muted">
            No bakes yet. Create a recipe and run your first export to see results
            here.
          </p>
          <button
            type="button"
            className="dvw-btn dvw-btn--primary"
            onClick={onNewRecipe}
          >
            <IconPlus className="dvw-btn__icon" />
            <span>Create or open a recipe</span>
          </button>
        </div>
      </section>
    );
  }

  const badge = bakeBadge(cook.status, cook.exitCode);
  const title =
    cook.recipeName && cook.recipeName.trim().length > 0
      ? cook.recipeName.trim()
      : 'Untitled recipe';
  const target = dashboardLabel(detail?.recipe?.dashboard ?? null);
  const when = formatBakeDateTime(cook.startedAt ?? cook.createdAt);
  const ago = relativeBakeTime(cook.startedAt ?? cook.createdAt);
  const durStr = formatBakeDuration(cook.durationSeconds);
  const out = primaryOutput(detail?.outputs?.outputs);
  const outName = baseName(out?.path);
  const outSize = formatBakeBytes(out?.sizeBytes ?? null);
  const outFolder = bakeOutputFolder(detail?.outputs?.outputs);

  return (
    <section className="dvw-card dvw-lastbake" aria-labelledby="dvw-lastbake-h">
      <header className="dvw-card__head">
        <h2 id="dvw-lastbake-h" className="dvw-card__title">
          Last Bake
        </h2>
        <span className={'dvw-bake-badge dvw-bake-badge--' + badge.tone}>
          <span className="dvw-bake-badge__dot" aria-hidden="true" />
          {badge.label}
        </span>
      </header>

      <div className="dvw-lastbake__head">
        <span className="dvw-lastbake__recipe">{title}</span>
        {target ? <span className="dvw-dash-pill">{target}</span> : null}
      </div>

      <dl className="dvw-lastbake__meta">
        {when ? (
          <div className="dvw-lastbake__metaitem">
            <dt className="dvw-lastbake__metalabel">When</dt>
            <dd className="dvw-lastbake__metavalue">
              {when}
              {ago ? <span className="dvw-lastbake__ago"> · {ago}</span> : null}
            </dd>
          </div>
        ) : null}
        {durStr ? (
          <div className="dvw-lastbake__metaitem">
            <dt className="dvw-lastbake__metalabel">Duration</dt>
            <dd className="dvw-lastbake__metavalue">Ran for {durStr}</dd>
          </div>
        ) : null}
        {outName ? (
          <div className="dvw-lastbake__metaitem">
            <dt className="dvw-lastbake__metalabel">Output</dt>
            <dd className="dvw-lastbake__metavalue">
              <span className="dvw-lastbake__file">{outName}</span>
              {outSize ? (
                <span className="dvw-lastbake__size"> — {outSize}</span>
              ) : null}
            </dd>
          </div>
        ) : null}
      </dl>

      <div className="dvw-lastbake__actions">
        <button
          type="button"
          className="dvw-btn dvw-btn--primary"
          onClick={() => onRunAgain(cook.recipeId)}
        >
          <IconRefresh className="dvw-btn__icon" />
          <span>Run again</span>
        </button>
        {outFolder ? (
          <button
            type="button"
            className="dvw-link dvw-link--icon"
            onClick={() => onOpenFolder(outFolder)}
            title="Open the folder that contains this bake's output files"
          >
            <IconFolder className="dvw-link__icon" />
            <span>Open folder</span>
          </button>
        ) : null}
        {detail?.logPath ? (
          <button
            type="button"
            className="dvw-link dvw-link--icon"
            onClick={() => onOpenLog(detail.logPath as string)}
            title="Open this bake's log file in your default app"
          >
            <IconBook className="dvw-link__icon" />
            <span>Open log</span>
          </button>
        ) : (
          <button
            type="button"
            className="dvw-link dvw-link--icon"
            onClick={() => onViewInBakes(cook.cookId)}
            title="Open this bake in Bakes to view its log"
          >
            <IconBook className="dvw-link__icon" />
            <span>Open log</span>
          </button>
        )}
        <button
          type="button"
          className="dvw-link dvw-link--icon"
          onClick={() =>
            void downloadBakeLogByCookId(
              cook.cookId,
              cook.recipeName,
              cook.startedAt ?? cook.createdAt,
            )
          }
          title="Download this bake's log file"
        >
          <IconDownload className="dvw-link__icon" />
          <span>Download log</span>
        </button>
      </div>
    </section>
  );
}

function SystemHealth({
  count,
  onShowAttention,
}: {
  count: number;
  onShowAttention: () => void;
}) {
  if (count <= 0) {
    return (
      <p className="dvw-syshealth dvw-syshealth--ok" role="status">
        <span className="dvw-syshealth__dot" aria-hidden="true" />
        All systems ready
      </p>
    );
  }
  return (
    <button
      type="button"
      className="dvw-syshealth dvw-syshealth--attention"
      onClick={onShowAttention}
    >
      <span className="dvw-syshealth__dot" aria-hidden="true" />
      {count} item{count === 1 ? '' : 's'} need{count === 1 ? 's' : ''} attention
    </button>
  );
}

function RecentRecipesPanel({
  phase,
  recipes,
  cookById,
  recipeDashboard,
  onOpen,
  onViewAll,
}: {
  phase: ListPhase;
  recipes: readonly RecipeSummary[];
  cookById: Record<string, CookSummary>;
  recipeDashboard: Record<string, string>;
  onOpen: (recipe: RecipeSummary) => void;
  onViewAll: () => void;
}) {
  return (
    <section className="dvw-card dvw-recent" aria-labelledby="dvw-recent-h">
      <header className="dvw-card__head">
        <h2 id="dvw-recent-h" className="dvw-card__title">
          Recent Recipes
        </h2>
        <button type="button" className="dvw-link" onClick={onViewAll}>
          View all
        </button>
      </header>

      {phase === 'loading' ? (
        <p className="dvw-card__muted" role="status">
          Loading recipes…
        </p>
      ) : null}

      {phase === 'error' ? (
        <p className="dvw-card__muted" role="status">
          Your recipes will appear here once you create one.
        </p>
      ) : null}

      {phase === 'loaded' && recipes.length === 0 ? (
        <p className="dvw-card__muted" role="status">
          No recipes yet. Create your first recipe to see it here.
        </p>
      ) : null}

      {phase === 'loaded' && recipes.length > 0 ? (
        <ul className="dvw-recipe-list">
          {recipes.map(recipe => {
            const tone = toneForRecipeStatus(recipe['status']);
            const dash = dashboardLabel(recipeDashboard[recipe.recipeId] ?? null);
            const lr = lastRunSummary(recipe, cookById);
            return (
              <li key={recipe.recipeId} className="dvw-recipe-row">
                <button
                  type="button"
                  className="dvw-recipe-row__main"
                  onClick={() => onOpen(recipe)}
                >
                  <span className="dvw-recipe-row__icon" aria-hidden="true">
                    <IconBook />
                  </span>
                  <span className="dvw-recipe-row__text">
                    <span className="dvw-recipe-row__head">
                      <span className="dvw-recipe-row__name">{recipeName(recipe)}</span>
                      {dash ? <span className="dvw-dash-pill">{dash}</span> : null}
                    </span>
                    <span
                      className={'dvw-recipe-row__meta dvw-recipe-row__meta--' + lr.tone}
                    >
                      {lr.text}
                    </span>
                  </span>
                </button>
                <RecipeStatusBadge tone={tone} />
              </li>
            );
          })}
        </ul>
      ) : null}
    </section>
  );
}

function AttentionPanel({
  phase,
  alerts,
}: {
  phase: ListPhase;
  alerts: readonly HomeAlert[];
}) {
  return (
    <section className="dvw-card dvw-attention" aria-labelledby="dvw-attn-h">
      <header className="dvw-card__head">
        <h2 id="dvw-attn-h" className="dvw-card__title">
          What needs attention
        </h2>
      </header>

      {phase === 'loaded' && alerts.length === 0 ? (
        <div className="dvw-attention__clear">
          <span className="dvw-attention__clear-icon" aria-hidden="true">
            <IconCheckCircle />
          </span>
          <p className="dvw-card__muted">
            Nothing needs attention right now. You're all set.
          </p>
        </div>
      ) : null}

      {phase !== 'loaded' ? (
        <p className="dvw-card__muted" role="status">
          Checking your recipes…
        </p>
      ) : null}

      {alerts.length > 0 ? (
        <ul className="dvw-attention__list">
          {alerts.map(alert => (
            <li
              key={alert.id}
              className={'dvw-attention__row dvw-attention__row--' + alert.tone}
            >
              <span className="dvw-attention__icon" aria-hidden="true">
                <IconAlertCircle />
              </span>
              <button
                type="button"
                className="dvw-attention__main"
                onClick={alert.onClick}
              >
                <span className="dvw-attention__title">{alert.title}</span>
                <span className="dvw-attention__sub">{alert.detail}</span>
              </button>
              <span className="dvw-attention__chev" aria-hidden="true">
                <IconChevronRight />
              </span>
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}

function RecentOutputsPanel({
  phase,
  outputs,
  onOpenFolder,
  onOpenLog,
  onViewInBakes,
}: {
  phase: ListPhase;
  outputs: readonly RecentOutput[];
  onOpenFolder: (path: string) => void;
  onOpenLog: (path: string) => void;
  onViewInBakes: (cookId: string) => void;
}) {
  return (
    <section className="dvw-card dvw-outputs" aria-labelledby="dvw-outputs-h">
      <header className="dvw-card__head">
        <h2 id="dvw-outputs-h" className="dvw-card__title">
          Recent Outputs
        </h2>
      </header>

      {phase === 'loading' || phase === 'idle' ? (
        <p className="dvw-card__muted" role="status">
          Loading your recent outputs…
        </p>
      ) : null}

      {phase === 'error' ? (
        <p className="dvw-card__muted" role="status">
          Your output files will appear here after your first bake.
        </p>
      ) : null}

      {phase === 'loaded' && outputs.length === 0 ? (
        <p className="dvw-card__muted" role="status">
          No output files yet. Your exports will appear here after your first bake.
        </p>
      ) : null}

      {outputs.length > 0 ? (
        <ul className="dvw-outputs__list">
          {outputs.map(output => (
            <li key={output.key} className="dvw-outputs__row">
              <div className="dvw-outputs__main">
                <span className="dvw-outputs__icon" aria-hidden="true">
                  <IconFolder />
                </span>
                <span className="dvw-outputs__text">
                  <span className="dvw-outputs__name">{output.name}</span>
                  <span className="dvw-outputs__meta">
                    {[output.size, output.date].filter(Boolean).join(' · ')}
                  </span>
                </span>
                {output.dashboard ? (
                  <span className="dvw-dash-pill">{output.dashboard}</span>
                ) : null}
              </div>
              <div className="dvw-outputs__actions">
                {output.folder ? (
                  <button
                    type="button"
                    className="dvw-link dvw-link--icon"
                    onClick={() => onOpenFolder(output.folder as string)}
                    title="Open the folder that contains this bake's output files"
                  >
                    <IconFolder className="dvw-link__icon" />
                    <span>Open folder</span>
                  </button>
                ) : null}
                <button
                  type="button"
                  className="dvw-link dvw-link--icon"
                  onClick={() =>
                    output.logPath
                      ? onOpenLog(output.logPath)
                      : onViewInBakes(output.cookId)
                  }
                  title="Open this bake's log file in your default app"
                >
                  <IconBook className="dvw-link__icon" />
                  <span>Open log</span>
                </button>
                <button
                  type="button"
                  className="dvw-link dvw-link--icon dvw-outputs__log"
                  onClick={() =>
                    void downloadBakeLogByCookId(
                      output.cookId,
                      output.recipeName,
                      output.when,
                    )
                  }
                  title="Download this bake's log file"
                >
                  <IconDownload className="dvw-link__icon" />
                  <span>Download log</span>
                </button>
              </div>
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}
