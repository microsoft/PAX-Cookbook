/**
 * Recipes workspace.
 *
 * The default Recipes surface: a three-column desktop workspace (saved-recipes
 * list, recipe detail, readiness/advanced inspector) over a command bar. The
 * full guided builder/editor (Mini-Kitchen) is reached deliberately through
 * "Open Editor" / "New Recipe" rather than being the landing view.
 *
 * Data is read-only through the broker (list / get / readiness). The workspace
 * shell can also start the single gated Bake (run) for the selected saved recipe
 * directly, via the same confirm modal + Windows Hello step-up + single startCook
 * channel the builder uses, without opening the builder and without adding a
 * second execution channel. Creating and editing recipes still happen inside the
 * builder it mounts, which owns those broker calls; the shell does not delete,
 * rename, or schedule.
 */
import { useEffect, useMemo, useRef, useState } from 'react';
import {
  listRecipes,
  getRecipe,
  getRecipeReadiness,
  createRecipe,
  deleteRecipe,
  deleteScheduledTask,
  listCooks,
  getCook,
  startCook,
  resumeCook,
} from '../host/brokerBridge';
import type {
  RecipeSummary,
  RecipeReadinessBody,
} from '../host/brokerBridge';
import {
  reauthManualCook,
  describeReauthFailure,
} from '../host/manualCookReauth';
import { listChefKeys } from '../host/chefKeys';
import { getRuntimeVersion, getPaxEngineState } from '../host/systemInfo';
import { MiniKitchenBuilderPreview } from '../features/mini-kitchen/MiniKitchenBuilderPreview';
import {
  ImportCommandModal,
  type CommandImportSaveOutcome,
} from '../features/mini-kitchen/components/ImportCommandModal';
import { BakeConfirmModal } from '../features/mini-kitchen/components/BakeConfirmModal';
import { translateLiteRecipeToFullRecipe } from '../features/mini-kitchen/lib/translateLiteRecipeToFullRecipe';
import { buildRecipeRequestBody } from '../features/mini-kitchen/lib/candidateToRecipeBody';
import { fullRecipeToState } from '../features/mini-kitchen/lib/fullRecipeToState';
import { resolvePermissions } from '../features/mini-kitchen/lib/permissionsResolver';
import { renderPaxCommand } from '../features/mini-kitchen/lib/commandRenderer';
import { createRecipeFromPreset } from '../features/mini-kitchen/lib/defaultRecipe';
import { pickAndParseRecipeFile } from '../features/mini-kitchen/lib/recipeFileImport';
import {
  buildFullPaxRecipeExport,
  buildFullPaxRecipeFileName,
  serializeFullPaxRecipe,
} from '../features/mini-kitchen/lib/fullPaxRecipeExporter';
import { describeStartCookFailure } from '../features/mini-kitchen/lib/startCookMessages';
import { describeResumeCookFailure } from '../features/mini-kitchen/lib/resumeCookMessages';
import type {
  MiniKitchenRecipeState,
  AuthMode,
  StorageTier,
  PermissionEntry,
  PresetId,
} from '../features/mini-kitchen/types';
import {
  takePendingSelect,
  takePendingDraft,
  takePendingImportCommand,
  rememberPendingBakeSelect,
  requestShellSection,
} from './shellNav';
import type { EditorDraftSeed } from './templateToEditorDraft';
import { RecipeListPane, type ListPhase } from './RecipeListPane';
import { RecipeDetailPane } from './RecipeDetailPane';
import { DeleteRecipeConfirmModal } from './DeleteRecipeConfirmModal';
import { ResumeCheckpointModal } from './ResumeCheckpointModal';
import {
  ReadinessInspector,
  type ReadinessPhase,
  type RecipeDiagnostics,
  type DiagnosticsLastBake,
} from './ReadinessInspector';
import { AdvancedPanel } from './AdvancedPanel';
import { SectionHeader } from './components/SectionHeader';
import {
  IconPlus,
  IconArrowLeft,
  IconFolder,
  IconChevronDown,
  IconRefresh,
} from './CookbookIllustrations';

// Short, non-secret labels for the bake confirmation modal, derived from the
// recipe state the detail summary already projects (the builder's own inverse
// translator). The projection never restores a client secret or certificate
// thumbprint — those live in the Chef's Key, not the recipe — so these labels
// cannot surface a secret.
const BAKE_AUTH_LABELS: Record<AuthMode, string> = {
  WebLogin: 'Interactive web sign-in on this PC.',
  DeviceCode: 'Device-code sign-in.',
  AppRegistrationSecret: 'App registration sign-in (secret held in the Chef\u2019s Key).',
  AppRegistrationCertificate:
    'App registration sign-in (certificate held in the Chef\u2019s Key).',
  ManagedIdentity: 'Managed identity sign-in.',
};

const BAKE_TIER_LABELS: Record<StorageTier, string> = {
  local: 'Local folder',
  sharepoint: 'SharePoint',
  fabric: 'Fabric',
};

function describeBakeAuth(state: MiniKitchenRecipeState): string {
  return BAKE_AUTH_LABELS[state.auth.mode] ?? 'Interactive sign-in on this PC.';
}

function describeBakeDestination(state: MiniKitchenRecipeState): string {
  const isUserInfoOnly = state.query.mode === 'user-info-only';
  const tierLabel =
    BAKE_TIER_LABELS[state.destinations.fact.tier] ?? state.destinations.fact.tier;
  const path = isUserInfoOnly
    ? state.destinations.userInfo.path?.trim()
    : state.destinations.fact.path?.trim();
  return path ? `${tierLabel} \u2014 ${path}` : tierLabel;
}

// Read the selected recipe's persisted schedule block defensively. The full
// recipe carries an optional `schedule` object ({ enabled, recurrence, ... });
// this guards every access and returns true only for an enabled schedule. Used
// to decide whether a delete must first unregister the recipe's OS task and to
// drive the delete modal's "schedule will also be removed" note.
function isRecipeScheduled(recipe: Record<string, unknown> | null): boolean {
  if (!recipe || typeof recipe !== 'object') {
    return false;
  }
  const schedule = recipe['schedule'];
  if (!schedule || typeof schedule !== 'object') {
    return false;
  }
  return (schedule as Record<string, unknown>)['enabled'] === true;
}

// Map a failed recipe delete to one bounded, friendly sentence (never a secret
// or path). Mirrors describeCreateFailure's network / 401 / 423 / other shape.
function describeDeleteFailure(res: {
  error: string | null;
  networkError: string | null;
  status: number;
}): string {
  if (res.networkError) {
    return 'Could not reach PAX Cookbook. Make sure it is running, then try again.';
  }
  if (res.status === 401) {
    return 'PAX Cookbook needs you to sign in again before it can delete this recipe. Try again.';
  }
  if (res.status === 423) {
    return 'PAX Cookbook is locked right now. Unlock it, then try deleting again.';
  }
  if (res.status === 404) {
    return 'That recipe is no longer in PAX Cookbook. Refresh the list.';
  }
  return 'PAX Cookbook could not delete this recipe. Try again.';
}

// Map a failed schedule removal (the step that must precede deleting a
// scheduled recipe) to one bounded sentence. Every message makes clear the
// recipe was NOT deleted, so its OS task is never orphaned.
function describeScheduleRemovalFailure(res: {
  error: string | null;
  networkError: string | null;
  status: number;
}): string {
  if (res.networkError) {
    return 'Could not reach PAX Cookbook to remove this recipe\u2019s schedule, so it was not deleted. Try again.';
  }
  if (res.status === 401) {
    return 'PAX Cookbook needs you to sign in again before it can remove this recipe\u2019s schedule, so it was not deleted. Try again.';
  }
  if (res.status === 423) {
    return 'PAX Cookbook is locked right now. Unlock it, then try deleting again \u2014 the recipe was not deleted.';
  }
  return 'PAX Cookbook could not remove this recipe\u2019s schedule, so it was not deleted. Try again.';
}

type Mode =
  | { kind: 'workspace' }
  | { kind: 'editor'; recipeId: string | null; draft?: EditorDraftSeed };

export function RecipesWorkspace() {
  const [mode, setMode] = useState<Mode>({ kind: 'workspace' });

  // Import-command modal visibility.
  const [importOpen, setImportOpen] = useState(false);

  // Import Recipe dropdown (command bar) open state + outside/Escape close.
  const [importMenuOpen, setImportMenuOpen] = useState(false);
  const [importError, setImportError] = useState<string | null>(null);
  const importMenuRef = useRef<HTMLDivElement | null>(null);

  // Saved recipe list.
  const [recipes, setRecipes] = useState<readonly RecipeSummary[]>([]);
  const [listPhase, setListPhase] = useState<ListPhase>('idle');
  const [search, setSearch] = useState('');

  // Selected recipe + its detail body.
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [selectedSummary, setSelectedSummary] = useState<RecipeSummary | null>(null);
  const [detail, setDetail] = useState<Record<string, unknown> | null>(null);
  const [detailPhase, setDetailPhase] =
    useState<'idle' | 'loading' | 'loaded' | 'error'>('idle');

  // Readiness projection for the selected recipe.
  const [readinessPhase, setReadinessPhase] = useState<ReadinessPhase>('idle');
  const [readiness, setReadiness] = useState<RecipeReadinessBody | null>(null);
  const [readinessError, setReadinessError] = useState<string | null>(null);

  // Advanced details fly-in panel open/close. The open state lives here (not in
  // the inspector) so the columns layout can react and push left when it opens.
  const [advancedOpen, setAdvancedOpen] = useState(false);

  // Read-only support diagnostics for the selected recipe (Recipe ID + last
  // bake + engine fingerprint), projected from the cook-history GET routes only.
  const [diagnostics, setDiagnostics] = useState<RecipeDiagnostics | null>(null);

  // The Cookbook app version shown in the Advanced panel's Recipe Info section.
  // Fetched once on mount from the read-only runtime/version GET, fail-soft: any
  // error leaves it null and the row is omitted. This is the same value Settings
  // shows; it surfaces no secret, no path, and no new execution channel.
  const [appVersion, setAppVersion] = useState<string | null>(null);
  const [enginePath, setEnginePath] = useState<string | null>(null);

  // Friendly Chef's Key names (id → displayName) so the detail summary shows the
  // saved name instead of the raw id. Fetched once, fail-soft; only id +
  // displayName are kept (never a secret or any other key field). An empty map
  // simply leaves the summary showing the id.
  const [chefKeyNames, setChefKeyNames] =
    useState<ReadonlyMap<string, string>>(() => new Map());

  // The saved Chef's Keys (id + friendly name only) offered as the optional
  // sign-in override in the Resume modal. Populated by the same fail-soft
  // listChefKeys effect that builds chefKeyNames; only id + displayName are
  // kept (never a secret or any other key field). An empty list simply leaves
  // the dropdown at its single "use the checkpoint's saved sign-in" default.
  const [chefKeyList, setChefKeyList] =
    useState<readonly { id: string; displayName: string }[]>(() => []);

  // Bake (run) — the single gated execution path, reachable for a saved recipe
  // straight from the homepage without opening the editor. `bakeConfirmOpen`
  // shows the confirmation modal; `bakeSubmitting` is true only while the one
  // startCook call (and its at-most-once Windows Hello retry) is in flight;
  // `bakeError` holds a bounded failure sentence shown inside the modal. None of
  // these ever fabricate a cook record — a started bake is reported only when
  // the broker answers 201.
  const [bakeConfirmOpen, setBakeConfirmOpen] = useState(false);
  const [bakeSubmitting, setBakeSubmitting] = useState(false);
  const [bakeError, setBakeError] = useState<string | null>(null);

  // Resume from checkpoint — the second sanctioned cook-start entry point,
  // reachable from the command bar and the list's dashed tile. `resumeOpen`
  // shows the resume modal; `resumeSubmitting` is true only while the one
  // resumeCook call (and its at-most-once Windows Hello retry) is in flight;
  // `resumeError` holds a bounded failure sentence shown inside the modal. Like
  // a bake, a started run is reported only when the broker answers 201 — never
  // fabricated — and the Windows Hello step-up runs before the broker call.
  const [resumeOpen, setResumeOpen] = useState(false);
  const [resumeSubmitting, setResumeSubmitting] = useState(false);
  const [resumeError, setResumeError] = useState<string | null>(null);

  // Delete recipe — a soft delete (the broker moves the recipe to its trash and
  // drops it from the active list). `deleteConfirmOpen` shows the confirmation
  // modal; `deleteSubmitting` is true only while the delete (and, for a
  // scheduled recipe, the schedule removal that must precede it) is in flight;
  // `deleteError` holds a bounded failure sentence shown inside the modal.
  // Deleting runs no PAX and starts no cook.
  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false);
  const [deleteSubmitting, setDeleteSubmitting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  // Export — a pure client-side download of the selected saved recipe as a full
  // `.pax` recipe. The detail card's Export (.pax) button calls this handler; it
  // builds the file from the projected recipe state with the same proven exporter
  // lib the editor uses and triggers a browser download. It makes no broker call,
  // runs no PAX, and starts no cook. `exportStatus` is a transient inline note (a
  // scrub summary on success or a bounded sentence on failure) shown at the bottom
  // of the detail card's actions; it is simply replaced on the next action.
  const [exportStatus, setExportStatus] =
    useState<{ kind: 'success' | 'error'; text: string } | null>(null);

  // A Pantry starting point may have stashed a pre-filled recipe draft before
  // routing here. If so, open the editor on it as a fresh, unsaved recipe. This
  // is a one-shot, client-side handoff — no recipe is created until the user
  // saves in the editor.
  useEffect(() => {
    const draft = takePendingDraft();
    if (draft) {
      setMode({ kind: 'editor', recipeId: null, draft });
    }
  }, []);

  // A Home "Import Command" menu pick stashes a one-shot flag before routing
  // here; consume it on mount to open the Import Command modal.
  useEffect(() => {
    if (takePendingImportCommand()) {
      setImportOpen(true);
    }
  }, []);

  // An explicit click on the legacy nav rail's Recipes item re-selects this
  // section even when it is already active. The shell signals that click with
  // { type: 'mk-nav', section: 'recipes', reselect: true }; on it we leave the
  // builder and return to the saved-recipes list. The reselect flag gates this
  // so the programmatic Pantry-draft and file-import handoffs — which post the
  // same nav message WITHOUT reselect to open the editor — are never disturbed.
  useEffect(() => {
    const onMessage = (ev: MessageEvent) => {
      if (ev.origin !== window.location.origin) {
        return;
      }
      const data = ev.data as unknown;
      if (
        data &&
        typeof data === 'object' &&
        (data as { type?: unknown }).type === 'mk-nav' &&
        (data as { section?: unknown }).section === 'recipes' &&
        (data as { reselect?: unknown }).reselect === true
      ) {
        setMode({ kind: 'workspace' });
        if (typeof window !== 'undefined') {
          window.scrollTo({ top: 0, left: 0 });
          window.requestAnimationFrame(() => window.scrollTo({ top: 0, left: 0 }));
        }
      }
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, []);

  // Load the list once on mount, honoring any pending selection from Home.
  useEffect(() => {
    let cancelled = false;
    setListPhase('loading');
    void listRecipes()
      .then(res => {
        if (cancelled) {
          return;
        }
        if (res.ok && res.data) {
          const list = res.data.recipes ?? [];
          setRecipes(list);
          setListPhase('loaded');
          const pending = takePendingSelect();
          const initial =
            (pending && list.find(r => r.recipeId === pending)) || list[0] || null;
          if (initial) {
            void selectRecipe(initial);
          }
        } else {
          setListPhase('error');
        }
      })
      .catch(() => {
        if (!cancelled) {
          setListPhase('error');
        }
      });
    return () => {
      cancelled = true;
    };
    // selectRecipe is stable for our purposes; intentional one-shot load.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Whenever the builder closes back to the saved-recipes list, reload the list
  // so a recipe just created, updated, or deleted in the builder appears right
  // away — without waiting for a full app reload. This covers both the "Back to
  // recipes" button and the legacy nav-rail reselect, which return to the list
  // in place (no remount). The first mount is skipped: the load-once effect
  // above already fetched the list.
  const listReloadArmedRef = useRef(false);
  useEffect(() => {
    if (mode.kind !== 'workspace') {
      return;
    }
    if (!listReloadArmedRef.current) {
      listReloadArmedRef.current = true;
      return;
    }
    void reloadRecipes();
    // reloadRecipes only calls stable state setters + listRecipes; safe to omit.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode.kind]);

  // When a recipe is selected, gather read-only support diagnostics from the
  // cook-history GETs (last bake + engine fingerprint). Fail-soft: any row whose
  // data does not resolve is simply omitted, and a fetch failure never blocks
  // the inspector. GET-only history projections — no PAX, no mutation, no
  // second execution channel, and no secret (workspace path is not fetched).
  useEffect(() => {
    if (!selectedId) {
      setDiagnostics(null);
      return;
    }
    const recipeId = selectedId;
    let cancelled = false;
    // Show the Recipe ID immediately; enrich with last bake / engine as loaded.
    setDiagnostics({ recipeId, lastBake: null, engineVersion: null, appVersion });
    void (async () => {
      let lastBake: DiagnosticsLastBake | null = null;
      let latestCookId: string | null = null;
      const cooksRes = await listCooks();
      if (cancelled) {
        return;
      }
      if (cooksRes.ok && cooksRes.data) {
        // listCooks projects newest-first, so the first match is the latest cook.
        const latest =
          cooksRes.data.cooks.find(c => c.recipeId === recipeId) ?? null;
        if (latest) {
          latestCookId = latest.cookId;
          lastBake = {
            status: latest.status,
            when: latest.finishedAt ?? latest.startedAt ?? latest.createdAt,
            exitCode: latest.exitCode,
          };
        }
      }
      let engineVersion: string | null = null;
      if (latestCookId) {
        const cookRes = await getCook(latestCookId);
        if (cancelled) {
          return;
        }
        const version = cookRes.data?.engine?.version;
        if (cookRes.ok && typeof version === 'string' && version.length > 0) {
          engineVersion = version;
        }
      }
      if (cancelled) {
        return;
      }
      setDiagnostics({ recipeId, lastBake, engineVersion, appVersion });
    })();
    return () => {
      cancelled = true;
    };
  }, [selectedId, appVersion]);

  // Fetch the saved Chef's Key names once on mount so the detail summary can
  // resolve a recipe's bound key id to the friendly name the user gave it.
  // Fail-soft and cancellation-guarded like the diagnostics fetch: any error
  // simply leaves the map empty, and the summary falls back to the id. Only
  // id + displayName are read from each item — never the tenant/client id,
  // certificate thumbprint, upn, or the hasSecret flag.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const res = await listChefKeys();
        if (cancelled || !res.ok || !res.data) {
          return;
        }
        const items = res.data.chefKeys;
        if (!Array.isArray(items)) {
          return;
        }
        const map = new Map<string, string>();
        const list: { id: string; displayName: string }[] = [];
        for (const item of items) {
          const id = typeof item.id === 'string' ? item.id.trim() : '';
          const displayName =
            typeof item.displayName === 'string' ? item.displayName.trim() : '';
          if (id.length > 0 && displayName.length > 0) {
            map.set(id, displayName);
            list.push({ id, displayName });
          }
        }
        setChefKeyNames(map);
        setChefKeyList(list);
      } catch {
        // Fail-soft: leave the map empty; the summary falls back to the id.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  // Import Recipe dropdown: close on Escape or an outside click while open.
  // Mirrors the Home command bar's import menu. Pure UI state — no PAX, no cook,
  // no broker call, and no new execution channel.
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

  // Fetch the Cookbook app version once on mount for the Advanced panel's Recipe
  // Info section. Fail-soft and cancellation-guarded like the diagnostics and
  // chef-keys fetches: any error leaves it null and the row is omitted. This is
  // a read-only GET of the same version Settings shows — no secret, no path, and
  // no new execution channel.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const res = await getRuntimeVersion();
        if (cancelled || !res.ok) {
          return;
        }
        setAppVersion(res.data.cookbookVersion);
      } catch {
        // Fail-soft: leave the app version null; the Recipe Info row is omitted.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  // Fetch the managed engine path once on mount for the resume command preview.
  // Read-only GET of the same engine-state route the overlay and Settings use; no
  // secret is involved — the managed engine path is non-secret display data (it
  // already appears in Bakes output paths). Fail-soft and cancellation-guarded:
  // any error leaves it null and the modal falls back to a readable token.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const res = await getPaxEngineState();
        if (cancelled || !res.ok) {
          return;
        }
        setEnginePath(res.data.managedEnginePath);
      } catch {
        // Fail-soft: leave the engine path null; the preview uses a readable token.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  // Graph API permissions the selected recipe will require at runtime, derived
  // purely from the loaded recipe detail. `fullRecipeToState` projects the
  // persisted recipe back into builder state and `resolvePermissions` reports
  // its prerequisites; we keep only the Microsoft Graph scopes. These are scope
  // names and plain-language reasons (never a secret), recomputed only when the
  // detail changes. Computing them is cheap and pure — no broker call and no
  // PAX. The Advanced panel (the fly-in) renders and gates their display on a
  // completed readiness check, so they surface the same way its command
  // preview does.
  const graphPermissions = useMemo<readonly PermissionEntry[]>(() => {
    if (!detail) {
      return [];
    }
    const projection = fullRecipeToState(detail);
    if (!projection.ok || !projection.state) {
      return [];
    }
    return resolvePermissions(projection.state).required.filter(
      entry => entry.group === 'graph',
    );
  }, [detail]);

  async function selectRecipe(summary: RecipeSummary) {
    setSelectedId(summary.recipeId);
    setSelectedSummary(summary);
    setDetail(null);
    setDetailPhase('loading');
    // A new selection invalidates any prior readiness result.
    setReadiness(null);
    setReadinessPhase('idle');
    setReadinessError(null);

    const res = await getRecipe(summary.recipeId);
    if (res.ok && res.data) {
      setDetail(res.data.recipe ?? null);
      setDetailPhase('loaded');
    } else {
      setDetailPhase('error');
    }
  }

  async function runReadiness() {
    if (!detail) {
      return;
    }
    setReadinessPhase('loading');
    setReadinessError(null);
    const res = await getRecipeReadiness(detail);
    if (res.ok && res.data) {
      setReadiness(res.data);
      setReadinessPhase('loaded');
    } else if (res.networkError) {
      setReadinessPhase('error');
      setReadinessError(
        'Could not reach PAX Cookbook. Make sure it is running, then try again.',
      );
    } else {
      setReadinessPhase('error');
      setReadinessError('PAX Cookbook could not complete the readiness check.');
    }
  }

  // Open the bake confirmation modal for the selected saved recipe. The gate is
  // re-checked here so a stale click can never open the modal when there is no
  // loaded saved selection. The selected recipe is the persisted on-disk recipe
  // (saved + clean by definition), so its id is a valid bake target — the same
  // single startCook channel the editor uses, started from a different button.
  function handleBakeClick() {
    if (!selectedId || detailPhase !== 'loaded') {
      return;
    }
    setBakeError(null);
    setBakeConfirmOpen(true);
  }

  function handleBakeCancel() {
    if (bakeSubmitting) {
      return;
    }
    setBakeConfirmOpen(false);
    setBakeError(null);
  }

  // Start the bake through the single sanctioned channel. `allowReauth` is true
  // on the first attempt; on a broker reAuthRequired it runs the browser-owned
  // Windows Hello ceremony and retries EXACTLY ONCE with `allowReauth = false`,
  // so there is no retry loop. A started bake (201) is reported only from the
  // broker's own cookId, which is handed to the Bakes page to focus. This is
  // the same startCook helper and the same step-up the editor uses; there is no
  // second execution channel.
  async function runBake(allowReauth: boolean): Promise<void> {
    if (!selectedId) {
      return;
    }
    setBakeSubmitting(true);
    setBakeError(null);
    try {
      const result = await startCook(selectedId);
      const outcome = result.outcome;
      if (outcome.kind === 'started') {
        rememberPendingBakeSelect(outcome.cookId);
        setBakeConfirmOpen(false);
        requestShellSection('bakes');
        return;
      }
      if (
        (outcome.kind === 'reauthRequired' || outcome.kind === 'locked') &&
        allowReauth
      ) {
        const reauth = await reauthManualCook(selectedId);
        if (reauth.ok) {
          await runBake(false);
          return;
        }
        setBakeError(describeReauthFailure(reauth));
        return;
      }
      setBakeError(describeStartCookFailure(outcome));
    } finally {
      setBakeSubmitting(false);
    }
  }

  function handleConfirmBake() {
    if (bakeSubmitting || !selectedId) {
      return;
    }
    void runBake(true);
  }

  // Open the resume modal. Resume recovers an interrupted PAX run from its
  // checkpoint and needs no saved recipe, so it is always available — the modal
  // collects the checkpoint location and the broker owns every gate.
  function handleResumeClick() {
    setResumeError(null);
    setResumeOpen(true);
  }

  function handleResumeCancel() {
    if (resumeSubmitting) {
      return;
    }
    setResumeOpen(false);
    setResumeError(null);
  }

  // Start the resume through the broker's single sanctioned cook core, mirroring
  // runBake. `allowReauth` is true on the first attempt; on a broker
  // reAuthRequired it runs the browser-owned Windows Hello ceremony — keyed to
  // the resume sentinel id, the same step-up a manual bake uses — and retries
  // EXACTLY ONCE with `allowReauth = false`, so there is no retry loop. A
  // started run (201) is reported only from the broker's own cookId, which is
  // handed to the Bakes page to focus; nothing is ever fabricated. The body
  // carries no secret and no script path.
  async function runResume(
    input: { checkpointPath: string; force: boolean; chefKeyId: string | null },
    allowReauth: boolean,
  ): Promise<void> {
    setResumeSubmitting(true);
    setResumeError(null);
    try {
      const result = await resumeCook({
        checkpointPath: input.checkpointPath.trim(),
        force: input.force,
        chefKeyId:
          input.chefKeyId && input.chefKeyId.length > 0 ? input.chefKeyId : null,
      });
      const outcome = result.outcome;
      if (outcome.kind === 'started') {
        rememberPendingBakeSelect(outcome.cookId);
        setResumeOpen(false);
        requestShellSection('bakes');
        return;
      }
      if (
        (outcome.kind === 'reauthRequired' || outcome.kind === 'locked') &&
        allowReauth
      ) {
        const reauth = await reauthManualCook('__resume__');
        if (reauth.ok) {
          await runResume(input, false);
          return;
        }
        setResumeError(describeReauthFailure(reauth));
        return;
      }
      setResumeError(describeResumeCookFailure(outcome));
    } finally {
      setResumeSubmitting(false);
    }
  }

  function handleResumeConfirm(input: {
    checkpointPath: string;
    force: boolean;
    chefKeyId: string | null;
  }) {
    if (resumeSubmitting) {
      return;
    }
    void runResume(input, true);
  }

  // Open the delete confirmation modal for the selected saved recipe. The gate
  // is re-checked here so a stale click can never open the modal when there is
  // no loaded saved selection.
  function handleDeleteClick() {
    if (!selectedId || detailPhase !== 'loaded') {
      return;
    }
    setDeleteError(null);
    setDeleteConfirmOpen(true);
  }

  function handleDeleteCancel() {
    if (deleteSubmitting) {
      return;
    }
    setDeleteConfirmOpen(false);
    setDeleteError(null);
  }

  // Delete the selected recipe through the broker. A scheduled recipe owns a
  // per-user Windows Task; recipe-delete is a soft delete that does NOT cascade
  // to the scheduler, so when the recipe is scheduled its OS task is
  // unregistered FIRST (deleteScheduledTask) — if that fails the recipe is left
  // in place so its task is never orphaned. On success the selection is cleared
  // the same way a fresh selection resets it and the list is reloaded (which
  // drops the soft-deleted recipe). This runs no PAX and starts no cook.
  async function runDelete(): Promise<void> {
    if (deleteSubmitting || !selectedId) {
      return;
    }
    const recipeId = selectedId;
    setDeleteSubmitting(true);
    setDeleteError(null);
    try {
      if (isRecipeScheduled(detail)) {
        const sched = await deleteScheduledTask(recipeId);
        if (!sched.ok) {
          setDeleteError(describeScheduleRemovalFailure(sched));
          return;
        }
      }
      const res = await deleteRecipe(recipeId);
      if (res.ok) {
        setDeleteConfirmOpen(false);
        // Clear the selection the same way selectRecipe resets state, so the
        // detail / readiness / advanced panes return to their empty state. The
        // diagnostics effect clears itself when selectedId becomes null.
        setSelectedId(null);
        setSelectedSummary(null);
        setDetail(null);
        setDetailPhase('idle');
        setReadiness(null);
        setReadinessPhase('idle');
        setReadinessError(null);
        setAdvancedOpen(false);
        await reloadRecipes();
        return;
      }
      setDeleteError(describeDeleteFailure(res));
    } finally {
      setDeleteSubmitting(false);
    }
  }

  // Describe the selected saved recipe for the confirmation modal: recipe name,
  // a short sign-in label, the output destination, and the pure command preview
  // (the same renderer the builder's command preview uses). All four are derived
  // from the projected recipe state — never fetched, never a secret. If the
  // recipe cannot be projected, fall back to safe minimal summaries so the modal
  // never crashes and never invents data.
  function deriveBakeSummaries(): {
    recipeName: string;
    authSummary: string;
    destinationSummary: string;
    commandSummary: string | null;
  } {
    const recipeName =
      selectedSummary &&
      typeof selectedSummary.name === 'string' &&
      selectedSummary.name.trim().length > 0
        ? selectedSummary.name.trim()
        : 'this recipe';
    const projection = fullRecipeToState(detail);
    if (!projection.ok || !projection.state) {
      return {
        recipeName,
        authSummary: 'Interactive sign-in on this PC.',
        destinationSummary: 'The recipe\u2019s configured output destination.',
        commandSummary: null,
      };
    }
    const state = projection.state;
    const command = renderPaxCommand(state).singleLine;
    return {
      recipeName,
      authSummary: describeBakeAuth(state),
      destinationSummary: describeBakeDestination(state),
      commandSummary: command && command.length > 0 ? command : null,
    };
  }

  // Export the selected saved recipe as a full `.pax` recipe — the inverse of
  // the `.pax` importer. Pure: it projects the persisted recipe back into builder
  // state with the builder's own inverse translator, hands that to the proven
  // exporter lib, and downloads the result as a file in the browser. No broker
  // call, no PAX, no cook. The exporter scrubs the state, so the `.pax` never
  // carries a secret (it references the Chef's Key by id and the non-secret
  // tenant id only); any scrubbed field is summarized in the success note the
  // same way the editor reports it.
  function handleExportFull() {
    if (!detail || detailPhase !== 'loaded') {
      setExportStatus({ kind: 'error', text: 'Select a saved recipe to export.' });
      return;
    }
    const projection = fullRecipeToState(detail);
    if (!projection.ok || !projection.state) {
      setExportStatus({
        kind: 'error',
        text: 'This recipe needs more detail before it can be exported.',
      });
      return;
    }
    try {
      const state = projection.state;
      const exportResult = buildFullPaxRecipeExport({ state });
      if (!exportResult.ok || !exportResult.recipe) {
        setExportStatus({
          kind: 'error',
          text:
            exportResult.error ??
            'This recipe needs a few more details before it can be exported as a full .pax recipe.',
        });
        return;
      }
      const json = serializeFullPaxRecipe(exportResult.recipe);
      const fileName = buildFullPaxRecipeFileName(exportResult.recipe);
      const blob = new Blob([json], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = fileName;
      document.body.appendChild(anchor);
      anchor.click();
      document.body.removeChild(anchor);
      URL.revokeObjectURL(url);

      const scrubbed = exportResult.warnings.length;
      const scrubNote =
        scrubbed > 0
          ? ` ${scrubbed} sign-in field${scrubbed === 1 ? '' : 's'} (such as the tenant ID) ${scrubbed === 1 ? 'was' : 'were'} left out; re-enter ${scrubbed === 1 ? 'it' : 'them'} after importing.`
          : '';
      setExportStatus({
        kind: 'success',
        text:
          `Exported the full recipe “${fileName}”. Import it later to reopen the complete recipe.` +
          scrubNote,
      });
    } catch {
      setExportStatus({
        kind: 'error',
        text: 'Could not build the export file. Try again.',
      });
    }
  }

  function openEditorNew() {
    setMode({ kind: 'editor', recipeId: null });
  }

  function openEditorSelected() {
    setMode({ kind: 'editor', recipeId: selectedId });
  }

  // Open the builder pre-filled from a built-in preset as a fresh, unsaved
  // draft (recipeId: null). Pure client-side seed via createRecipeFromPreset —
  // the same end state as New Recipe: no broker write, no PAX, no cook, no
  // schedule. Drafts can never Bake (constraint 11); a recipe is created only
  // when the user saves in the editor. Shared by the "Start from a template"
  // cards and the Import Recipe dropdown.
  function openEditorFromPreset(presetId: PresetId) {
    setImportMenuOpen(false);
    setMode({
      kind: 'editor',
      recipeId: null,
      draft: {
        templateId: presetId,
        templateName: 'Recipe',
        note: 'Started from a template.',
        state: createRecipeFromPreset(presetId),
      },
    });
  }

  // Open a native file-browse dialog, parse the chosen .pax/.paxlite file, and
  // open the builder pre-populated with its settings as a fresh, unsaved draft.
  async function importRecipeFromFile() {
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
    setMode({
      kind: 'editor',
      recipeId: null,
      draft: {
        templateId: 'importedRecipeFile',
        templateName: 'Imported recipe',
        note: `Imported from ${outcome.fileName}. Review the settings, then Save when it is ready.`,
        state: outcome.state,
      },
    });
  }

  // Duplicate the selected recipe into a NEW unsaved editor draft. This is a
  // pure client-side copy: it projects the saved recipe back into builder state
  // (the projection never restores a client secret or certificate thumbprint —
  // those live in the Chef's Key, not the recipe), renames it "Copy of …", and
  // opens the editor on a fresh draft (recipeId: null). The original recipe is
  // never modified and no broker write happens until the user saves the new
  // draft. If the recipe cannot be projected, fall back to opening the editor on
  // the original so the button never dead-ends.
  function duplicateSelected() {
    if (!detail) {
      return;
    }
    const projection = fullRecipeToState(detail);
    if (!projection.ok || !projection.state) {
      openEditorSelected();
      return;
    }
    const original = projection.state;
    const originalName = (original.identity?.name ?? '').trim() || 'recipe';
    const copyName = 'Copy of ' + originalName;
    setMode({
      kind: 'editor',
      recipeId: null,
      draft: {
        templateId: 'duplicate',
        templateName: copyName,
        note:
          'Duplicated from \u201c' +
          originalName +
          '\u201d. Review the details, then save to create a new recipe.',
        state: {
          ...original,
          identity: { ...original.identity, name: copyName },
        },
      },
    });
  }

  // Reload the saved-recipe list after an import save and optionally re-select
  // a recipe by id (e.g. the one just created).
  async function reloadRecipes(selectId?: string) {
    setListPhase('loading');
    const res = await listRecipes();
    if (res.ok && res.data) {
      const list = res.data.recipes ?? [];
      setRecipes(list);
      setListPhase('loaded');
      if (selectId) {
        const match = list.find(r => r.recipeId === selectId);
        if (match) {
          void selectRecipe(match);
        }
      }
    } else {
      setListPhase('error');
    }
  }

  function describeCreateFailure(
    error: string | null,
    networkError: string | null,
    status: number,
  ): string {
    if (networkError) {
      return 'Could not reach PAX Cookbook. Make sure it is running, then try again.';
    }
    if (status === 401) {
      return 'PAX Cookbook needs you to sign in again before it can save. Try again.';
    }
    if (status === 423) {
      return 'PAX Cookbook is locked right now. Unlock it, then try saving again.';
    }
    if (error === 'validation_failed') {
      return 'PAX Cookbook could not save the recipe yet. Review the details, or load it without saving to finish it in the editor.';
    }
    return 'PAX Cookbook could not save the recipe. Try again, or load it without saving to finish it in the editor.';
  }

  // Persist an imported-command recipe through the broker's authenticated
  // create route. Pure handoff: nothing here runs PAX or the pasted command.
  async function createImportedRecipe(
    name: string,
    state: MiniKitchenRecipeState,
  ): Promise<{ ok: boolean; message: string; newId: string | null }> {
    const named: MiniKitchenRecipeState = {
      ...state,
      identity: { ...state.identity, name },
    };
    const translation = translateLiteRecipeToFullRecipe(named);
    const build = buildRecipeRequestBody(translation, { includeImportMetadata: true });
    if (!build.body) {
      return {
        ok: false,
        message: 'The recipe still needs some details before it can be saved.',
        newId: null,
      };
    }
    const res = await createRecipe(build.body);
    if (res.ok) {
      const newId =
        res.data && typeof res.data.recipeId === 'string' ? res.data.recipeId : null;
      return { ok: true, message: 'Saved to PAX Cookbook.', newId };
    }
    return {
      ok: false,
      message: describeCreateFailure(res.error, res.networkError, res.status),
      newId: null,
    };
  }

  async function handleImportSave(
    name: string,
    state: MiniKitchenRecipeState,
  ): Promise<CommandImportSaveOutcome> {
    const result = await createImportedRecipe(name, state);
    if (result.ok) {
      setImportOpen(false);
      await reloadRecipes(result.newId ?? undefined);
    }
    return { ok: result.ok, message: result.message };
  }

  async function handleImportSaveAndLoad(
    name: string,
    state: MiniKitchenRecipeState,
  ): Promise<CommandImportSaveOutcome> {
    const result = await createImportedRecipe(name, state);
    if (result.ok) {
      setImportOpen(false);
      if (result.newId) {
        setMode({ kind: 'editor', recipeId: result.newId });
      } else {
        await reloadRecipes();
      }
    }
    return { ok: result.ok, message: result.message };
  }

  function handleImportLoadOnly(name: string, state: MiniKitchenRecipeState) {
    setImportOpen(false);
    setMode({
      kind: 'editor',
      recipeId: null,
      draft: {
        templateId: 'imported-command',
        templateName: name,
        note: 'Imported from pasted command. Review the details, then save the recipe.',
        state: { ...state, identity: { ...state.identity, name } },
      },
    });
  }

  if (mode.kind === 'editor') {
    return (
      <div className="dvw dvw-recipes dvw-recipes--editor">
        <div className="dvw-editor-bar">
          <button
            type="button"
            className="dvw-btn dvw-btn--ghost"
            onClick={() => setMode({ kind: 'workspace' })}
          >
            <IconArrowLeft className="dvw-btn__icon" />
            <span>Back to recipes</span>
          </button>
          <span className="dvw-editor-bar__title">
            {mode.recipeId ? 'Edit recipe' : 'New recipe'}
          </span>
          <img
            className="dvw-editor-bar__art"
            src="/images/pax-cookbook-recipe-book.png"
            alt=""
          />
        </div>
        <MiniKitchenBuilderPreview
          initialOpenRecipeId={mode.recipeId ?? undefined}
          initialDraftState={mode.draft?.state}
          initialDraftNote={mode.draft?.note}
        />
      </div>
    );
  }

  const canCheck = selectedId !== null && detailPhase === 'loaded';
  // Bake is available under the same condition as readiness: a saved recipe
  // whose detail has loaded. The selected recipe is the persisted on-disk recipe
  // — saved + clean by definition — so a draft can never reach this button.
  const canBake = selectedId !== null && detailPhase === 'loaded';
  // Whether the selected recipe has an enabled schedule. Drives the delete
  // modal's "schedule will also be removed" note and the delete flow's
  // unschedule-first step.
  const isScheduledForSelected = isRecipeScheduled(detail);
  // Friendly name for the delete confirmation modal (never a secret or path).
  const deleteRecipeName =
    selectedSummary &&
    typeof selectedSummary.name === 'string' &&
    selectedSummary.name.trim().length > 0
      ? selectedSummary.name.trim()
      : 'this recipe';
  // Only project/render the modal summaries while the modal is actually open.
  const bakeSummaries = bakeConfirmOpen ? deriveBakeSummaries() : null;

  return (
    <div className="dvw dvw-recipes">
      <SectionHeader
        headingLevel="h1"
        title="Recipes"
        helpTopic="cookbookRecipes"
        accent="var(--c-blue)"
        lede="Create, organize, and review saved recipes — use Check readiness to confirm what is still needed before a run."
      />

      <div className="dvw-commandbar" role="group" aria-label="Recipe actions">
        <button
          type="button"
          className="dvw-btn dvw-btn--primary"
          onClick={openEditorNew}
        >
          <IconPlus className="dvw-btn__icon" />
          <span>New Recipe</span>
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
                onClick={() => { void importRecipeFromFile(); }}
              >
                Import PAX Cookbook .pax Recipe
              </button>
              <button
                type="button"
                role="menuitem"
                className="dvw-import-menu__item"
                onClick={() => { void importRecipeFromFile(); }}
              >
                Import Mini-Kitchen .paxlite Recipe
              </button>
              <button
                type="button"
                role="menuitem"
                className="dvw-import-menu__item"
                onClick={() => {
                  setImportMenuOpen(false);
                  setImportOpen(true);
                }}
              >
                Import Command
              </button>
            </div>
          ) : null}
        </div>
        <button
          type="button"
          className="dvw-btn"
          onClick={handleResumeClick}
        >
          <IconRefresh className="dvw-btn__icon" />
          <span>Resume from checkpoint</span>
        </button>
      </div>

      {importError ? (
        <p
          role="alert"
          style={{ color: '#b42318', margin: '8px 0 0', fontSize: '0.875rem' }}
        >
          {importError}
        </p>
      ) : null}

      <div className="dvw-recipes__layout">
        <div className="dvw-recipes__cols">
          <RecipeListPane
            phase={listPhase}
            recipes={recipes}
            selectedId={selectedId}
            search={search}
            onSearch={setSearch}
            onSelect={summary => void selectRecipe(summary)}
            onEditRecipe={recipeId => setMode({ kind: 'editor', recipeId })}
            onPickPreset={openEditorFromPreset}
            onResume={handleResumeClick}
            onImportCommand={() => setImportOpen(true)}
            onImportFromFile={() => { void importRecipeFromFile(); }}
          />
          <RecipeDetailPane
            phase={detailPhase}
            summary={selectedSummary}
            recipe={detail}
            onOpenEditor={openEditorSelected}
            onDuplicate={duplicateSelected}
            onDelete={handleDeleteClick}
            onOpenAdvanced={() => setAdvancedOpen(prev => !prev)}
            advancedOpen={advancedOpen}
            onBake={handleBakeClick}
            canBake={canBake}
            chefKeyNames={chefKeyNames}
            onExportFull={handleExportFull}
            exportStatus={exportStatus}
          />
          <ReadinessInspector
            phase={readinessPhase}
            result={readiness}
            errorText={readinessError}
            canCheck={canCheck}
            onCheck={runReadiness}
          />
        </div>
        <AdvancedPanel
          open={advancedOpen}
          onClose={() => setAdvancedOpen(false)}
          result={readiness}
          phase={readinessPhase}
          diagnostics={diagnostics}
          graphPermissions={graphPermissions}
        />
      </div>

      {importOpen && (
        <div className="mini-kitchen-page">
          <ImportCommandModal
            existingRecipes={recipes}
            onClose={() => setImportOpen(false)}
            onSave={handleImportSave}
            onSaveAndLoad={handleImportSaveAndLoad}
            onLoadOnly={handleImportLoadOnly}
          />
        </div>
      )}

      {bakeConfirmOpen && bakeSummaries && (
        <div className="mini-kitchen-page">
          <BakeConfirmModal
            recipeName={bakeSummaries.recipeName}
            authSummary={bakeSummaries.authSummary}
            destinationSummary={bakeSummaries.destinationSummary}
            commandSummary={bakeSummaries.commandSummary}
            submitting={bakeSubmitting}
            error={bakeError}
            onCancel={handleBakeCancel}
            onConfirm={handleConfirmBake}
          />
        </div>
      )}

      {resumeOpen && (
        <div className="mini-kitchen-page">
          <ResumeCheckpointModal
            chefKeys={chefKeyList}
            submitting={resumeSubmitting}
            error={resumeError}
            enginePath={enginePath}
            onCancel={handleResumeCancel}
            onConfirm={handleResumeConfirm}
            onNavigate={() => {
              // Close the resume dialog cleanly, then hand the shell over to
              // Chef's Keys. The modal only asks; the navigation lives here.
              handleResumeCancel();
              requestShellSection('chefskeys');
            }}
          />
        </div>
      )}

      {deleteConfirmOpen && (
        <div className="mini-kitchen-page">
          <DeleteRecipeConfirmModal
            recipeName={deleteRecipeName}
            scheduled={isScheduledForSelected}
            submitting={deleteSubmitting}
            error={deleteError}
            onCancel={handleDeleteCancel}
            onConfirm={() => void runDelete()}
          />
        </div>
      )}
    </div>
  );
}
