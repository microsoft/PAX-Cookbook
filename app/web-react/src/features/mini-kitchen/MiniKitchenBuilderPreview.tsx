/**
 * Mini-Kitchen guided recipe builder.
 *
 * This is the in-memory React Recipes surface. It owns a single controlled
 * `MiniKitchenRecipeState` and derives the rendered PAX command and required
 * permissions per change with one `useMemo` block (normalizeRecipe +
 * renderPaxCommand + resolvePermissions).
 *
 * Wired product behavior (broker-backed persistence + one gated execution path):
 *  - Save / Update persist the current state as a full Cookbook recipe through
 *    the broker's authenticated recipe routes (POST / PUT /api/v1/recipes). The
 *    state is translated to the full recipe schema first; the broker validates
 *    and owns all provenance leaves.
 *  - Import reads a `.paxlite` lite recipe through the validated importer; the
 *    result lands in memory as a `needsPrep` draft. Nothing is saved until the
 *    user clicks Save.
 *  - Export writes the current state to a scrubbed `.pax` recipe file in
 *    the browser (no network, no broker).
 *  - Bake (run) is the single execution path. It is gated behind a saved,
 *    unchanged, ready recipe and an explicit confirmation; confirming calls the
 *    one startCook bridge helper (POST /api/v1/recipes/{id}/cook) and, when the
 *    broker asks for a Windows Hello step-up, runs the browser-owned reauth
 *    ceremony and retries exactly once. The browser never spawns PAX and never
 *    fabricates a cook record — success routes to the Bakes page.
 *
 * Boundaries (intentional, unchanged):
 *  - Taste Test / Schedule remain disabled and unwired. Nothing here schedules
 *    a task or touches a credential store. Authentication inputs show
 *    placeholder/test values only — real credentials are handled by Chef's Keys.
 *  - The rendered command is a preview. The broker rebuilds the command before
 *    any bake runs.
 */
import { useEffect, useMemo, useRef, useState, type ChangeEvent, type ReactNode } from 'react';
import { createDefaultMiniKitchenRecipe } from './lib/defaultRecipe';
import { applyMatchPreset, applyPresetSelection } from './lib/builderStateTransforms';
import { normalizeRecipe } from './lib/normalizeRecipe';
import { renderPaxCommand } from './lib/commandRenderer';
import { resolvePermissions } from './lib/permissionsResolver';
import { translateLiteRecipeToFullRecipe } from './lib/translateLiteRecipeToFullRecipe';
import { buildRecipeRequestBody } from './lib/candidateToRecipeBody';
import { deriveSaveRequirements, describeSaveRequirements } from './lib/recipeSaveRequirements';
import { parseLiteRecipeJson } from './lib/recipeImporter';
import { parseFullPaxRecipeJson, FULL_PAX_RECIPE_KIND } from './lib/fullPaxRecipeImporter';
import { fullRecipeToState } from './lib/fullRecipeToState';
import { describeStartCookFailure } from './lib/startCookMessages';
import {
  createRecipe,
  updateRecipe,
  getRecipeReadiness,
  getRecipe,
  startCook,
  putScheduledTask,
  deleteScheduledTask,
  getScheduledTask,
} from '../../host/brokerBridge';
import type {
  RecipeReadinessBody,
  RecipeSummary,
} from '../../host/brokerBridge';
import {
  reauthManualCook,
  describeReauthFailure,
} from '../../host/manualCookReauth';
import { rememberPendingBakeSelect, requestShellSection } from '../../shell/shellNav';
import { setNavigationGuard, type NavIntent } from '../../shell/navigationGuard';
import { subscribePendingImport, takePendingImport } from '../../host/importHandoff';
import { ContextualHelpButton } from '../../components/ContextualHelpButton';
import type {
  AuthMode,
  DashboardTarget,
  LiteRecipeAuth,
  LiteRecipeDestinations,
  LiteRecipeIdentity,
  LiteRecipeProcessing,
  LiteRecipeQuery,
  MiniKitchenRecipeState,
  OutputCombineMode,
  PresetId,
  RollupMode,
  StorageTier,
} from './types';
import { PresetPicker } from './components/PresetPicker';
import { RecipeBasics } from './components/RecipeBasics';
import { QueryModeCard, type QueryModeSelection } from './components/QueryModeCard';
import { DataCollectionCard } from './components/DataCollectionCard';
import { DateRangeCard } from './components/DateRangeCard';
import { RollupCard } from './components/RollupCard';
import { DashboardReqTag } from './components/DashboardRequirement';
import { AuditFiltersCard } from './components/AuditFiltersCard';
import { OutputTargetCard } from './components/OutputTargetCard';
import { AuthContextCard } from './components/AuthContextCard';
import { ScheduleCard } from './components/ScheduleCard';
import { AdvancedArgsCard } from './components/AdvancedArgsCard';
import { WizardRail, type WizardStepStatus } from './components/WizardRail';
import { computeWizardStepStatus } from './lib/wizardStepStatus';
import {
  type ScheduleDraft,
  defaultScheduleDraft,
  scheduleToRecurrence,
  scheduleFromResponse,
  describeSchedule,
  decideScheduleAction,
} from './lib/builderScheduleTransforms';
import {
  GeneratedCommandPanel,
  type ReviewCommandView,
} from './components/GeneratedCommandPanel';
import { CommandTabs } from './components/CommandTabs';
import type { CommandTabDescriptor } from './components/CommandTabs';
import { WarningsPanel } from './components/WarningsPanel';
import { BlockedItemsPanel } from './components/BlockedItemsPanel';
import { PermissionsPreview } from './components/PermissionsPreview';
import { AssumptionsPanel } from './components/AssumptionsPanel';
import {
  ReviewSummaryCards,
  countReviewWarnings,
  countReviewAssumptions,
  countReviewPermissions,
} from './components/ReviewSummaryCards';
import { AdapterReadinessPanel } from './components/AdapterReadinessPanel';
import { BakeConfirmModal } from './components/BakeConfirmModal';
import { DiscardConfirmModal } from './components/DiscardConfirmModal';
import { NavGuardModal } from './components/NavGuardModal';
import { OpenRecipeConfirmModal } from './components/OpenRecipeConfirmModal';
import { computeBakeBlockReason } from './lib/bakeGate';
import './mini-kitchen.css';

const PRESET_LABELS: Record<PresetId, string> = {
  aiInOneDashboard: 'AI in One',
  aiBusinessValueDashboard: 'AI Business Value',
  m365UsageAnalyticsDashboard: 'M365 Usage Analytics',
  customAuditExport: 'Custom audit export',
  userInfoOnly: 'User info only',
  importLiteRecipeJson: 'Imported lite recipe',
  importPaxRecipeJson: 'Imported full recipe',
};

const STORAGE_TIER_LABELS: Record<StorageTier, string> = {
  local: 'Local',
  sharepoint: 'SharePoint',
  fabric: 'Fabric',
};

const AUTH_MODE_LABELS: Record<AuthMode, string> = {
  WebLogin: 'Web login',
  DeviceCode: 'Device code',
  AppRegistrationSecret: 'App reg. secret',
  AppRegistrationCertificate: 'App reg. cert',
  ManagedIdentity: 'Managed identity',
};

interface EditorStepMeta {
  n: number;
  title: string;
  intro: string;
}

/** The compact desktop editor presents the recipe as seven ordered steps. */
const EDITOR_STEPS: ReadonlyArray<EditorStepMeta> = [
  {
    n: 1,
    title: 'Basics',
    intro: 'Choose a preset or start from scratch, then name the recipe.',
  },
  {
    n: 2,
    title: 'Authentication',
    intro:
      'Pick how this recipe signs in and bind a Chef’s Key. Chef’s Keys holds your real credentials on this PC.',
  },
  {
    n: 3,
    title: 'Date Range',
    intro: 'Set the date range for the audit pull.',
  },
  {
    n: 4,
    title: 'Audit Operations',
    intro: 'Choose the Purview source, Entra scope, audit filters, and any advanced switches.',
  },
  {
    n: 5,
    title: 'Output',
    intro: 'Choose where the recipe writes its output, and the optional rollup mode.',
  },
  {
    n: 6,
    title: 'Schedule',
    intro:
      'Optionally run this recipe automatically on a schedule. A bound Chef’s Key is required.',
  },
  {
    n: 7,
    title: 'Review + Save',
    intro: 'Review the generated command and what is still needed, then save and bake.',
  },
];

/** Transient status line shown beneath the Save / Import / Export actions. */
interface ActionStatus {
  kind: 'success' | 'error' | 'info';
  text: string;
}

// Map a failed schedule registration (PUT …/scheduled-task) to a bounded,
// recipe-saved-but-not-scheduled sentence. The recipe itself is already saved
// at this point, so every message makes clear scheduling did not take — it
// never claims the recipe failed to save.
function describeScheduleFailure(error: string | null, status: number): string {
  if (status === 409 || error === 'schedule_requires_chef_key') {
    return 'The scheduled run needs a Chef\u2019s Key. Bind one and save again.';
  }
  if (status === 400 || error === 'invalid_recurrence') {
    return 'The schedule time was invalid. Adjust it and save again.';
  }
  if (status === 412 || error === 'recipe_invalid') {
    return 'The recipe could not be scheduled in its current state.';
  }
  if (status === 404) {
    return 'The recipe could not be found to schedule. Refresh and try again.';
  }
  // 500 / 502 schedule_registration_failed, network errors, anything else.
  return 'The scheduled run could not be registered with Windows Task Scheduler. Try saving again, or set up the schedule later from the recipe settings.';
}

// Fold a schedule reconcile outcome into the recipe save status. The recipe is
// saved regardless, so a scheduling hiccup is informational (never an error);
// only a clean schedule outcome keeps the success tone.
function buildSaveStatus(
  baseText: string,
  schedule: { kind: ActionStatus['kind']; text: string } | null,
): ActionStatus {
  if (!schedule) {
    return { kind: 'success', text: baseText };
  }
  const kind: ActionStatus['kind'] = schedule.kind === 'success' ? 'success' : 'info';
  return { kind, text: baseText + ' ' + schedule.text };
}

const REVIEW_TABS: readonly CommandTabDescriptor<ReviewCommandView>[] = [
  { id: 'single', label: 'Single line (recommended)' },
  { id: 'multi', label: 'Multi-line' },
];

/**
 * Peek at a picked file's envelope marker to decide which importer to run. A
 * full .pax recipe declares `kind: 'pax-cookbook-recipe'`; everything else
 * (including unreadable text) is routed to the .paxlite importer, which returns
 * a friendly error for invalid input. This only reads the marker — it never
 * applies anything.
 */
function sniffIsFullPaxRecipe(text: string): boolean {
  try {
    const parsed: unknown = JSON.parse(text);
    return (
      typeof parsed === 'object' &&
      parsed !== null &&
      !Array.isArray(parsed) &&
      (parsed as Record<string, unknown>).kind === FULL_PAX_RECIPE_KIND
    );
  } catch {
    return false;
  }
}

export function MiniKitchenBuilderPreview({
  initialOpenRecipeId,
  initialDraftState,
  initialDraftNote,
}: {
  /**
   * When the builder is opened from the Recipes workspace for a specific
   * saved recipe, this is its id. On mount the builder loads that recipe in
   * place of the blank default, skipping the unsaved-edits prompt because the
   * builder has just initialized. Read-only open path — no run, bake, or
   * schedule.
   */
  initialOpenRecipeId?: string;
  /**
   * When the builder is opened from a Pantry starting point, this is the
   * pre-filled recipe state to begin with — the same editable state a New
   * Recipe starts from, seeded with the template's safe defaults. It opens as
   * a fresh, unsaved draft (no `savedRecipeId`); nothing is persisted until the
   * user saves.
   */
  initialDraftState?: MiniKitchenRecipeState;
  /** Friendly banner shown when the editor was opened from a Pantry starting point. */
  initialDraftNote?: string;
} = {}) {
  const [state, setState] = useState<MiniKitchenRecipeState>(() =>
    initialDraftState ?? createDefaultMiniKitchenRecipe(),
  );

  // Whether a preset has been explicitly chosen (a preset card, a seeded
  // template draft, or an opened recipe). A brand-new "New recipe" starts with
  // NO preset chosen, so Step 1 highlights nothing and keeps all category
  // sections collapsed until the operator picks a starting point.
  const [presetChosen, setPresetChosen] = useState<boolean>(
    () => Boolean(initialDraftState || initialOpenRecipeId),
  );

  // When the editor was opened from a Pantry starting point, this note names
  // the source so the user knows where the pre-filled values came from. It is
  // presentation-only and never affects save or readiness.
  const [pantryDraftNote] = useState<string | null>(initialDraftNote ?? null);

  const [reviewTab, setReviewTab] = useState<ReviewCommandView>('single');

  // Which editor step is shown in the central pane. Presentation-only: the
  // recipe state and all derived data are independent of the active step.
  const [activeStep, setActiveStep] = useState<number>(1);

  const importInputRef = useRef<HTMLInputElement | null>(null);
  // Ref to the single-step content column. Changing the active step returns the
  // view to the top: the builder sits inside the shell's #shell-main column,
  // which is not its own scroll context (the document scrolls), so the effect
  // resets the content column's scrollTop — a no-op guard unless a layout change
  // makes it the scroller — and the document scroll position. Presentation-only.
  const contentRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = contentRef.current;
    if (el) {
      el.scrollTop = 0;
    }
    if (typeof window !== 'undefined') {
      window.scrollTo({ top: 0, left: 0 });
    }
  }, [activeStep]);

  // Broker-backed persistence state. `savedRecipeId` is the id the broker
  // assigned on the last successful Save; when present, Save becomes Update.
  const [savedRecipeId, setSavedRecipeId] = useState<string | null>(null);
  const [busy, setBusy] = useState<boolean>(false);
  const [, setActionStatus] = useState<ActionStatus | null>(null);

  // Schedule (X7b) — configured in the builder, but registered or removed only
  // by the save flow through the broker's scheduled-task routes after the recipe
  // is saved. Held separately from the recipe body so saving a recipe never
  // registers (or claims) a schedule on its own — the no-drift rule.
  //   - scheduleDraft: the live, editable card value.
  //   - serverSchedule: the schedule the broker last reported as registered
  //     (null when none). It is the truth for "did this recipe already have a
  //     schedule" in the save reconcile, and for the at-a-glance summary line.
  //   - scheduleNote: an optional advisory surfaced on the card (drift / could
  //     not read the current schedule).
  // scheduleBaselineRef mirrors baselineSnapshotRef for the draft so a schedule
  // change marks the builder dirty for the unsaved-edits prompt.
  const [scheduleDraft, setScheduleDraft] = useState<ScheduleDraft>(defaultScheduleDraft());
  const [serverSchedule, setServerSchedule] = useState<ScheduleDraft | null>(null);
  const [scheduleNote, setScheduleNote] = useState<string | null>(null);
  const scheduleBaselineRef = useRef<string>(JSON.stringify(defaultScheduleDraft()));

  // Adapter-backed readiness state. This is a read-only check against the local
  // broker: it asks whether the recipe could run and what is still missing,
  // without running anything. `readinessPhase` is the request lifecycle; a
  // result of any kind (including the friendly needs-setup envelope) lands in
  // `readiness`.
  const [readinessPhase, setReadinessPhase] =
    useState<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  const [readiness, setReadiness] = useState<RecipeReadinessBody | null>(null);
  const [readinessError, setReadinessError] = useState<string | null>(null);

  // Bake (run) — the single gated execution path. `bakeConfirmOpen` shows the
  // confirmation modal; `bakeSubmitting` is true only while the one startCook
  // call (and its at-most-once Windows Hello retry) is in flight; `bakeError`
  // holds a bounded failure sentence shown inside the modal. None of these ever
  // fabricate a cook record — a started bake is reported only when the broker
  // answers 201.
  const [bakeConfirmOpen, setBakeConfirmOpen] = useState<boolean>(false);
  const [bakeSubmitting, setBakeSubmitting] = useState<boolean>(false);
  const [bakeError, setBakeError] = useState<string | null>(null);

  // Discard-changes confirmation. `discardConfirmOpen` shows the in-app modal
  // that replaces the old browser confirm; the revert it gates is instant and
  // purely in-memory, so there is no submitting state to track.
  const [discardConfirmOpen, setDiscardConfirmOpen] = useState<boolean>(false);
  // Confirm before silently narrowing the data scope when the user removes
  // Entra user info from a scope that includes it. Holds the pending query
  // change until the user confirms (or cancels, leaving the toggle on).
  const [entraDeselectPending, setEntraDeselectPending] = useState<LiteRecipeQuery | null>(null);
  // A brief inline hint shown next to the Save button — used for the single
  // save requirement (a recipe name) and for a save failure. Saving an
  // incomplete recipe is allowed (draft), so there is no blocking modal.
  const [saveHint, setSaveHint] = useState<string | null>(null);

  // Open-a-different-recipe confirmation. When the builder holds unsaved edits
  // and a saved recipe is clicked, the chosen summary is parked here to show the
  // in-app open-confirm modal that replaces the old browser confirm. Confirm
  // fetches and loads it; cancel clears it and keeps the current draft.
  const [pendingOpenSummary, setPendingOpenSummary] = useState<RecipeSummary | null>(null);

  // `openingId` gates re-entrant opens: it holds the id of the saved recipe
  // currently being opened so a second open cannot start until the first
  // settles, and it is cleared when the open finishes.
  const [openingId, setOpeningId] = useState<string | null>(null);

  // Navigation guard. While the builder holds unsaved edits, any in-app
  // navigation away (the legacy nav-rail, a "Create a Chef's Key" link, the
  // Home "Open Recipes" link, etc.) is intercepted and the chosen NavIntent is
  // parked here so the unsaved-changes modal can decide whether to save, leave,
  // or stay. `isDirtyRef` mirrors the render-time `isDirty` — the exact same
  // flag the "Discard changes" button uses — so the once-registered guard reads
  // the latest value without re-registering on every keystroke.
  const [navIntent, setNavIntent] = useState<NavIntent | null>(null);
  const isDirtyRef = useRef<boolean>(false);
  useEffect(() => {
    setNavigationGuard((intent) => {
      if (!isDirtyRef.current) {
        return false; // clean — let navigation proceed with no prompt
      }
      setNavIntent(intent); // dirty — intercept; the modal owns the decision
      return true;
    });
    return () => setNavigationGuard(null);
  }, []);

  // A serialized snapshot of the recipe as it last stood at a clean point
  // (initial load, a successful save/update, or an open/import). It is the
  // baseline for the "you have unsaved edits" confirm shown before an open
  // replaces the builder contents. Kept in a ref so updating it never triggers
  // a render or invalidates readiness.
  const baselineSnapshotRef = useRef<string>('');

  // Issue C: the warnings, assumptions, and required-permissions detail panels
  // are each their own collapsible <details>, collapsed by default so a clean
  // Step 7 stays calm. The three always-visible summary cards above carry the
  // counts; clicking a card expands its matching section and scrolls to it. The
  // open state is controlled so a manual summary toggle keeps working too.
  const [warningsOpen, setWarningsOpen] = useState<boolean>(false);
  const [assumptionsOpen, setAssumptionsOpen] = useState<boolean>(false);
  const [permissionsOpen, setPermissionsOpen] = useState<boolean>(false);

  // A card click opens a section via state (async) and then scrolls to it. The
  // pending id is parked here and consumed once the open state has applied, so
  // the scroll lands after the section has actually expanded.
  const pendingReviewScrollRef = useRef<string | null>(null);

  function scrollReviewSectionIntoView(id: string) {
    const el = document.getElementById(id);
    if (!el) return;
    requestAnimationFrame(() => {
      el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    });
  }

  // Runs after the open flags change (the re-render a card click triggers).
  // Only acts when a card parked a target, so a manual summary toggle never
  // forces a scroll.
  useEffect(() => {
    const id = pendingReviewScrollRef.current;
    if (!id) return;
    pendingReviewScrollRef.current = null;
    scrollReviewSectionIntoView(id);
  }, [warningsOpen, assumptionsOpen, permissionsOpen]);

  // Open a review section and scroll to it. When it is already open, scroll
  // immediately (a no-op state set would not re-render, so the effect above
  // would not fire); otherwise park the target and let the effect scroll once
  // the section has expanded.
  function revealReviewSection(
    id: string,
    isOpen: boolean,
    setOpen: (open: boolean) => void,
  ) {
    if (isOpen) {
      scrollReviewSectionIntoView(id);
      return;
    }
    pendingReviewScrollRef.current = id;
    setOpen(true);
  }

  function openWarningsDetails() {
    revealReviewSection('mk-review-warnings', warningsOpen, setWarningsOpen);
  }
  function openAssumptionsDetails() {
    revealReviewSection('mk-review-assumptions', assumptionsOpen, setAssumptionsOpen);
  }
  function openPermissionsDetails() {
    revealReviewSection('mk-review-permissions', permissionsOpen, setPermissionsOpen);
  }

  const derived = useMemo(() => {
    const normalized = normalizeRecipe(state);
    const command = renderPaxCommand(state);
    const permissions = resolvePermissions(state);
    return { normalized, command, permissions };
  }, [state]);

  const isUserInfoOnly = state.query.mode === 'user-info-only';

  // Translate the in-memory state to a full Cookbook recipe candidate. This is
  // the same pure translator the parity harness covers; wiring it into Save is
  // what makes the recipe persistable. `needsPrep` and the compatibility
  // warnings it produces are surfaced to the user and preserved on save.
  const translation = useMemo(
    () => translateLiteRecipeToFullRecipe(state),
    [state],
  );
  const createBuild = useMemo(
    () => buildRecipeRequestBody(translation, { includeImportMetadata: true }),
    [translation],
  );
  const candidateReady = translation.ok && createBuild.ok && createBuild.body !== null;
  // Bucket A: the named recipe-content gaps that block Save. Bucket B
  // (runtime readiness) is evaluated separately and never blocks Save.
  const saveRequirements = useMemo(() => deriveSaveRequirements(state), [state]);
  const candidateSaveable = candidateReady && saveRequirements.length === 0;
  const saveLabel = 'Save recipe';

  // Any edit to the recipe invalidates a prior readiness answer, so the panel
  // returns to its idle prompt rather than showing a stale verdict.
  useEffect(() => {
    setReadinessPhase('idle');
    setReadiness(null);
    setReadinessError(null);
    // Any edit clears a stale save hint (e.g. the user fills in the name after
    // a "name required" hint).
    setSaveHint(null);
  }, [state]);

  // Record the starting state as the clean baseline for the unsaved-edits
  // prompt when the builder mounts.
  useEffect(() => {
    baselineSnapshotRef.current = JSON.stringify(state);
    // Mount-only: the baseline is re-captured explicitly after a save, update,
    // or open. Editing state must not reset it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // When opened from the Recipes workspace for a specific recipe, load it in
  // place of the blank default. The freshly set baseline above means the open
  // path sees a clean builder and skips the unsaved-edits confirm.
  useEffect(() => {
    if (!initialOpenRecipeId || initialDraftState) {
      return;
    }
    void handleOpenSavedRecipe({ recipeId: initialOpenRecipeId } as RecipeSummary);
    // Mount-only one-shot: the workspace remounts the builder when the target
    // changes, so this never needs to react to later prop or state changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Open a saved recipe back into the builder for review or editing. Read-only:
  // it fetches the stored recipe (GET /recipes/{id}) and loads it into builder
  // state. It never runs PAX, never bakes, and never schedules. If the builder
  // holds unsaved edits, it asks first via the in-app open-confirm modal; both
  // the confirmed open and the already-clean open run performOpenSavedRecipe.
  async function handleOpenSavedRecipe(summary: RecipeSummary) {
    if (busy || openingId) {
      return;
    }
    const scheduleDirty =
      JSON.stringify(scheduleDraft) !== scheduleBaselineRef.current;
    const isDirty =
      JSON.stringify(state) !== baselineSnapshotRef.current || scheduleDirty;
    if (isDirty) {
      // Unsaved edits: defer the open and let the modal decide. Confirm runs
      // performOpenSavedRecipe with this summary; cancel keeps the draft as-is.
      setPendingOpenSummary(summary);
      return;
    }
    void performOpenSavedRecipe(summary);
  }

  // Confirm/cancel for the open-a-different-recipe modal. Confirm captures the
  // pending summary before clearing it so the open never races a null state.
  function confirmOpenRecipe() {
    const summary = pendingOpenSummary;
    setPendingOpenSummary(null);
    if (summary) {
      void performOpenSavedRecipe(summary);
    }
  }

  function cancelOpenRecipe() {
    setPendingOpenSummary(null);
  }

  // The read-only open itself: fetch the stored recipe, map it into builder
  // state, and reset the clean baselines. Lifted verbatim from the old inline
  // body so the dirty path can gate it behind the open-confirm modal.
  async function performOpenSavedRecipe(summary: RecipeSummary) {
    setBusy(true);
    setOpeningId(summary.recipeId);
    setActionStatus({ kind: 'info', text: 'Opening saved recipe…' });
    try {
      const result = await getRecipe(summary.recipeId);
      if (!result.ok) {
        if (result.networkError) {
          setActionStatus({
            kind: 'error',
            text: 'Could not reach PAX Cookbook. Make sure it is running, then try again.',
          });
        } else if (result.status === 404) {
          setActionStatus({
            kind: 'error',
            text: 'That saved recipe no longer exists. Refresh the list and try again.',
          });
        } else if (result.status === 422) {
          setActionStatus({
            kind: 'error',
            text: 'That saved recipe could not be read. Its stored file looks damaged.',
          });
        } else {
          setActionStatus({
            kind: 'error',
            text: 'PAX Cookbook could not open that recipe. Try again.',
          });
        }
        return;
      }

      // The detail body carries the persisted recipe under `recipe`; its
      // identity lives in `meta`, so the authoritative id is the one we just
      // clicked, not anything inside the recipe object.
      const persisted = result.data ? result.data.recipe : undefined;
      const mapped = fullRecipeToState(persisted);
      if (!mapped.ok || !mapped.state) {
        setActionStatus({
          kind: 'error',
          text: mapped.error ?? 'That saved recipe could not be opened in the builder.',
        });
        return;
      }

      setState(mapped.state);
      setPresetChosen(true);
      setSavedRecipeId(summary.recipeId);
      // Editing `state` clears any prior readiness via the effect above; the
      // opened recipe is now the clean baseline for the unsaved-edits prompt.
      baselineSnapshotRef.current = JSON.stringify(mapped.state);
      // Load the recipe's current schedule (if any) so the Schedule card opens
      // reflecting what is actually registered. Fire-and-forget: a read failure
      // shows defaults and never blocks opening the recipe.
      void loadScheduleForRecipe(summary.recipeId);
      const openedName =
        mapped.state.identity.name && mapped.state.identity.name.length > 0
          ? `"${mapped.state.identity.name}"`
          : 'the saved recipe';
      setActionStatus({
        kind: 'success',
        text: `Opened ${openedName} in the builder for review. Nothing was run.`,
      });
    } finally {
      setBusy(false);
      setOpeningId(null);
    }
  }

  // Distill the broker's structured `validation_failed` payload into one short,
  // secret-free sentence so Save failures are diagnosable without a screenshot
  // round-trip. The broker returns an array of
  // `{ instancePath, keyword, message, params }` (see RecipeMutationRoutes
  // SerializeError). We only surface the JSON-pointer path and the keyword/
  // message — structural metadata, never the recipe field *values* — so no
  // secret or tenant data can leak into the toast. At most two errors are named.
  function describeValidationErrors(
    validationErrors: unknown[] | null,
  ): string | null {
    if (!validationErrors || validationErrors.length === 0) {
      return null;
    }
    const parts: string[] = [];
    for (const raw of validationErrors) {
      if (parts.length >= 2) {
        break;
      }
      if (!raw || typeof raw !== 'object') {
        continue;
      }
      const err = raw as Record<string, unknown>;
      const instancePath =
        typeof err.instancePath === 'string' && err.instancePath.length > 0
          ? err.instancePath
          : null;
      const keyword =
        typeof err.keyword === 'string' && err.keyword.length > 0
          ? err.keyword
          : null;
      const message =
        typeof err.message === 'string' && err.message.length > 0
          ? err.message
          : null;
      // `instancePath` is a JSON pointer (e.g. "/destinations/fact/path"); use
      // "(root)" when the failure is on the whole document. The message/keyword
      // is the schema reason (e.g. "required", "must match pattern"), not a value.
      const where = instancePath ?? '(root)';
      const reason = message ?? keyword;
      parts.push(reason ? `${where}: ${reason}` : where);
    }
    if (parts.length === 0) {
      return null;
    }
    const more =
      validationErrors.length > parts.length
        ? ` (+${validationErrors.length - parts.length} more)`
        : '';
    return `PAX Cookbook rejected the recipe — ${parts.join('; ')}${more}.`;
  }

  function describeBrokerFailure(
    error: string | null,
    message: string | null,
    validationErrors: unknown[] | null,
    networkError: string | null,
    status: number,
  ): string {
    if (networkError) {
      return 'Could not reach PAX Cookbook. Make sure it is running, then try again.';
    }
    if (status === 401) {
      return 'PAX Cookbook needs you to sign in again before it can save. Reopen the recipe and try again.';
    }
    if (status === 423) {
      return 'PAX Cookbook is locked right now. Unlock it, then try saving again.';
    }
    if (error === 'chef_key_mode_mismatch') {
      return 'This recipe\u2019s bound Chef\u2019s Key doesn\u2019t match its sign-in type. Pick a matching Chef\u2019s Key for this sign-in mode, or change the sign-in type, then save again.';
    }
    if (error === 'validation_failed') {
      // Prefer the client-side outstanding-requirements sentence when the
      // builder already knows what is missing (friendliest copy). When the
      // broker rejected something the client thought was complete (e.g. a
      // server-side rule or a stale-backend mismatch), fall back to the
      // broker's own structural error so Brian/OpenClaw can diagnose without
      // another screenshot-only loop.
      const named = describeSaveRequirements(saveRequirements);
      if (named) {
        return named;
      }
      const fromBroker = describeValidationErrors(validationErrors);
      if (fromBroker) {
        return fromBroker;
      }
      return 'PAX Cookbook could not save the recipe yet. Check the recipe details above and try again.';
    }
    if (error === 'not_found') {
      return 'That saved recipe no longer exists. Use Save to store it again as a new recipe.';
    }
    // Unmapped failure. Surface the broker's own error code and message so a
    // save rejection can be diagnosed from the screen instead of another
    // screenshot-only loop; the friendly mapped messages above already cover
    // the known cases.
    const codeLabel = error ?? (status > 0 ? `HTTP ${status}` : 'unknown error');
    const detailSuffix = message ? ` \u2014 ${message}` : '';
    return `Save failed: ${codeLabel}${detailSuffix}`;
  }

  // Reset the schedule card to a disabled default. Used whenever a fresh,
  // unsaved draft replaces the builder contents (import / file-open): a new
  // draft is never scheduled until it is saved and a schedule is configured.
  function resetScheduleForNewDraft() {
    const fresh = defaultScheduleDraft();
    setScheduleDraft(fresh);
    setServerSchedule(null);
    setScheduleNote(null);
    scheduleBaselineRef.current = JSON.stringify(fresh);
  }

  // Read a saved recipe's current schedule from the broker (GET …/scheduled-task)
  // so the card opens reflecting what is actually registered, with a drift probe.
  // Read-only: it never registers, runs, or mutates anything. On a read failure
  // it falls back to defaults and never assumes a schedule exists, so the save
  // flow can never delete a schedule it could not see.
  async function loadScheduleForRecipe(recipeId: string) {
    const res = await getScheduledTask(recipeId);
    if (res.ok && res.data) {
      const sched = res.data.schedule ?? null;
      const draft = scheduleFromResponse(sched);
      const active = sched && sched.enabled ? draft : null;
      setScheduleDraft(draft);
      setServerSchedule(active);
      scheduleBaselineRef.current = JSON.stringify(draft);
      if (active && res.data.drift) {
        setScheduleNote(
          'This recipe is scheduled, but its Windows task looks missing. Save again to re-register it.',
        );
      } else {
        setScheduleNote(null);
      }
      return;
    }
    const fresh = defaultScheduleDraft();
    setScheduleDraft(fresh);
    setServerSchedule(null);
    scheduleBaselineRef.current = JSON.stringify(fresh);
    setScheduleNote(
      'Could not read this recipe\u2019s current schedule. Configure one here to register it.',
    );
  }

  // Reconcile the recipe's OS schedule AFTER the recipe itself has been saved.
  // The recipe is already persisted, so a scheduling problem is never fatal — it
  // is reported as an advisory and the recipe stays saved. Returns the schedule
  // outcome to fold into the save status, or null when nothing was changed.
  async function reconcileSchedule(
    recipeId: string,
  ): Promise<{ kind: ActionStatus['kind']; text: string } | null> {
    const prevHadSchedule = serverSchedule !== null;
    const action = decideScheduleAction(prevHadSchedule, scheduleDraft);
    const chefKeyBound = Boolean(state.auth.chefKeyId);

    if (action === 'put') {
      // Client-side Chef's-Key gate (the broker also returns 409). Without a
      // bound key the recipe cannot run unattended, so do not attempt to
      // register — and never delete an existing schedule as a side effect.
      if (!chefKeyBound) {
        return {
          kind: 'info',
          text: 'Bind a Chef\u2019s Key, then save again to put this recipe on a schedule.',
        };
      }
      const res = await putScheduledTask(recipeId, scheduleToRecurrence(scheduleDraft));
      if (res.ok && res.data) {
        const sched = res.data.schedule ?? null;
        const draft = scheduleFromResponse(sched);
        const active = sched && sched.enabled ? draft : null;
        setScheduleDraft(draft);
        setServerSchedule(active);
        scheduleBaselineRef.current = JSON.stringify(draft);
        setScheduleNote(null);
        return { kind: 'success', text: `Scheduled \u2014 ${describeSchedule(draft)}.` };
      }
      return { kind: 'info', text: describeScheduleFailure(res.error, res.status) };
    }

    if (action === 'delete') {
      const res = await deleteScheduledTask(recipeId);
      if (res.ok) {
        const draft = { ...scheduleDraft, enabled: false };
        setScheduleDraft(draft);
        setServerSchedule(null);
        scheduleBaselineRef.current = JSON.stringify(draft);
        setScheduleNote(null);
        return { kind: 'success', text: 'Scheduling turned off.' };
      }
      return {
        kind: 'info',
        text: 'The schedule could not be turned off. Try saving again.',
      };
    }

    // 'none' — nothing to register or remove. Keep the baseline in sync so a
    // no-op save does not leave the schedule looking dirty.
    scheduleBaselineRef.current = JSON.stringify(scheduleDraft);
    return null;
  }

  // Save (create) or update the recipe. Returns true only when the recipe was
  // actually persisted, so callers like "Save and leave" can continue the
  // navigation on success and abort it on a block or failure. The ONLY
  // requirement to save is a recipe name — saving is decoupled from baking, so
  // an incomplete recipe persists as a draft and the Bake action enforces full
  // completeness separately.
  async function handleSaveOrUpdate(): Promise<boolean> {
    if (busy) {
      return false;
    }
    const hasName = (state.identity.name ?? '').trim().length > 0;
    if (!hasName) {
      setSaveHint('A recipe name is required to save.');
      return false;
    }
    if (!createBuild.body) {
      // Building the request body failed structurally (rare for a named
      // recipe). Surface a brief inline hint rather than blocking silently.
      setSaveHint('This recipe can\u2019t be saved yet. Check the recipe details above.');
      return false;
    }
    setSaveHint(null);
    setBusy(true);
    setActionStatus({ kind: 'info', text: savedRecipeId ? 'Updating…' : 'Saving…' });

    try {
      if (savedRecipeId) {
        const updateBody = buildRecipeRequestBody(translation, {
          includeImportMetadata: false,
        });
        if (!updateBody.body) {
          setSaveHint('The recipe still needs some details before it can be updated.');
          return false;
        }
        const result = await updateRecipe(savedRecipeId, updateBody.body);
        if (result.ok) {
          baselineSnapshotRef.current = JSON.stringify(state);
          const sched = await reconcileSchedule(savedRecipeId);
          setActionStatus(
            buildSaveStatus(
              'Recipe saved.',
              sched,
            ),
          );
          return true;
        }
        setSaveHint(
          describeBrokerFailure(
            result.error,
            result.message,
            result.validationErrors,
            result.networkError,
            result.status,
          ),
        );
        return false;
      }
      const result = await createRecipe(createBuild.body);
      if (result.ok) {
        const newId =
          result.data && typeof result.data.recipeId === 'string'
            ? result.data.recipeId
            : null;
        if (newId) {
          setSavedRecipeId(newId);
        }
        baselineSnapshotRef.current = JSON.stringify(state);
        const sched = newId ? await reconcileSchedule(newId) : null;
        setActionStatus(
          buildSaveStatus(
            'Recipe saved.',
            sched,
          ),
        );
        return true;
      }
      setSaveHint(
        describeBrokerFailure(
          result.error,
          result.message,
          result.validationErrors,
          result.networkError,
          result.status,
        ),
      );
      return false;
    } finally {
      setBusy(false);
    }
  }

  // UX14 R5: revert all unsaved edits back to the last saved state. This is a
  // pure in-memory undo — it restores the builder state and the schedule draft
  // to their clean baselines and calls no broker route, so the saved recipe on
  // disk is never modified, deleted, or re-saved. After the revert the state
  // matches its baseline, so isDirty falls back to false (Update returns to its
  // ghost weight and this button hides). The baselines themselves are left
  // untouched — they already hold the clean snapshot.
  function handleDiscardChanges() {
    if (busy || bakeSubmitting) {
      return;
    }
    // An empty builder baseline means there is no clean snapshot to revert to
    // (it is set on mount / open / save); treat that as nothing to discard
    // rather than opening a confirm with nothing behind it.
    if (!baselineSnapshotRef.current) {
      return;
    }
    // Open the in-app confirmation. Nothing reverts until the user confirms; the
    // builder edits stay exactly as they are while the modal is up.
    setDiscardConfirmOpen(true);
  }

  // The confirmed revert: an instant, in-memory reset of the builder and its
  // schedule draft back to the last clean baselines. Nothing on disk is read or
  // written, no broker route is called, and no PAX ever runs — this only undoes
  // unsaved edits.
  function confirmDiscardChanges() {
    // Re-check the baseline at confirm time: if it somehow went empty while the
    // modal was open, close out safely rather than parsing '' and throwing.
    if (!baselineSnapshotRef.current) {
      setDiscardConfirmOpen(false);
      return;
    }
    setState(JSON.parse(baselineSnapshotRef.current) as MiniKitchenRecipeState);
    setScheduleDraft(JSON.parse(scheduleBaselineRef.current) as ScheduleDraft);
    setActionStatus({
      kind: 'info',
      text: 'Reverted to the last saved recipe. Nothing was saved or run.',
    });
    setDiscardConfirmOpen(false);
  }

  // Cancel / Escape / backdrop — close the modal and leave every unsaved edit in
  // place.
  function cancelDiscardChanges() {
    setDiscardConfirmOpen(false);
  }

  function scrollReadinessIntoView() {
    if (typeof window === 'undefined') return;
    window.setTimeout(() => {
      document
        .getElementById('mk-readiness-heading')
        ?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }, 60);
  }

  // Ask the local broker whether this recipe could run and what is still
  // missing. Read-only: the broker validates and projects the command and
  // checks PAX engine / sign-in / destination state, but never runs anything.
  // The recipe candidate sent here is the same body Save would persist.
  async function handleCheckReadiness() {
    if (busy || readinessPhase === 'loading') {
      return;
    }
    setActiveStep(7);
    if (!candidateReady || !createBuild.body) {
      setReadinessPhase('error');
      setReadiness(null);
      setReadinessError(
        'Finish the highlighted required details above before checking readiness.',
      );
      scrollReadinessIntoView();
      return;
    }
    setReadinessPhase('loading');
    setReadiness(null);
    setReadinessError(null);
    try {
      const result = await getRecipeReadiness(createBuild.body);
      if (result.ok && result.data) {
        setReadiness(result.data);
        setReadinessPhase('loaded');
        scrollReadinessIntoView();
        return;
      }
      let message: string;
      if (result.networkError) {
        message =
          'Could not reach PAX Cookbook. Make sure it is running, then try again.';
      } else if (result.status === 401) {
        message =
          'PAX Cookbook needs you to sign in again before it can check readiness. Reopen the recipe and try again.';
      } else if (result.status === 423) {
        message =
          'PAX Cookbook is locked right now. Unlock it, then check readiness again.';
      } else {
        message =
          'PAX Cookbook could not check readiness for this recipe. Try again in a moment.';
      }
      setReadiness(null);
      setReadinessPhase('error');
      setReadinessError(message);
      scrollReadinessIntoView();
    } catch {
      setReadiness(null);
      setReadinessPhase('error');
      setReadinessError(
        'Something went wrong checking readiness. Try again in a moment.',
      );
      scrollReadinessIntoView();
    }
  }

  // The recipe is "dirty" for bake purposes whenever the in-memory state differs
  // from the last clean baseline (load / save / open). Baking always runs the
  // recipe saved on disk, so unsaved edits must block the bake.
  function isDirtyForBake(): boolean {
    return JSON.stringify(state) !== baselineSnapshotRef.current;
  }

  // The single source of truth for why Bake is unavailable, or null when it is
  // ready. The decision is the pure computeBakeBlockReason gate; the broker
  // re-checks every condition and owns the lock, same-recipe-busy, sign-in,
  // engine-SHA, and integrity gates at start time. Check readiness is an
  // optional preflight (UXR2): a saved, clean, valid recipe may bake without
  // having run it, but a readiness result that reports a problem still blocks.
  function deriveBakeBlockReason(): string | null {
    const named = describeSaveRequirements(saveRequirements);
    const saveBlockReason =
      (named ? named + ' ' : '') +
      'Finish the required details and save before you bake.';
    return computeBakeBlockReason({
      busy,
      bakeSubmitting,
      savedRecipeId,
      isDirty: isDirtyForBake(),
      candidateSaveable,
      saveBlockReason,
      readinessPhase,
      readiness,
    });
  }

  // Open the confirmation modal. The gate is re-evaluated here so a stale click
  // can never open the modal for a recipe that is no longer bakeable.
  function handleBakeClick() {
    if (deriveBakeBlockReason() !== null) {
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

  // Start the bake. `allowReauth` is true on the first attempt; on a broker
  // `reAuthRequired` (401) OR a `Locked` (423) it runs the browser-owned
  // Windows Hello step-up and retries EXACTLY ONCE with `allowReauth = false`,
  // so there is no retry loop. The step-up is the same single ceremony in both
  // cases: a verified assertion authorizes the cook AND lifts/refreshes the
  // session lock on the broker, so a timed-out session never dead-ends the bake
  // with a "locked" error — one Windows Hello prompt covers both. A started bake
  // (201) is reported only from the broker's own cookId, handed to Bakes.
  async function runBake(allowReauth: boolean): Promise<void> {
    if (!savedRecipeId) {
      return;
    }
    setBakeSubmitting(true);
    setBakeError(null);
    try {
      const result = await startCook(savedRecipeId);
      const outcome = result.outcome;
      if (outcome.kind === 'started') {
        rememberPendingBakeSelect(outcome.cookId);
        setBakeConfirmOpen(false);
        setActionStatus({
          kind: 'success',
          text: 'Bake started. Follow progress in Bakes.',
        });
        requestShellSection('bakes');
        return;
      }
      if (
        (outcome.kind === 'reauthRequired' || outcome.kind === 'locked') &&
        allowReauth
      ) {
        const reauth = await reauthManualCook(savedRecipeId);
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
    if (bakeSubmitting || savedRecipeId === null) {
      return;
    }
    void runBake(true);
  }

  // Issue 2: the full Cookbook app's primary export writes the complete,
  // runtime-oriented recipe as a `.pax` file. Export is offered from the saved
  // Recipes list (not the in-builder Step 7), so the builder keeps only the
  // import affordance below.

  function handleImportClick() {
    if (busy) {
      return;
    }
    // If the user has unsaved edits in the current draft, confirm before the
    // imported file replaces them.
    const hasUnsavedEdits = baselineSnapshotRef.current !== JSON.stringify(state);
    if (
      hasUnsavedEdits &&
      !window.confirm('Importing will replace your current settings. Continue?')
    ) {
      return;
    }
    importInputRef.current?.click();
  }

  async function handleImportFile(event: ChangeEvent<HTMLInputElement>) {
    const input = event.target;
    const file = input.files && input.files[0];
    // Reset the input so picking the same file again re-fires onChange.
    input.value = '';
    if (!file) {
      return;
    }
    let text: string;
    try {
      text = await file.text();
    } catch {
      setActionStatus({ kind: 'error', text: 'Could not read that file. Try again.' });
      return;
    }
    // Dispatch by the file's own envelope marker: a full .pax recipe carries
    // kind === 'pax-cookbook-recipe', everything else is read as a .paxlite.
    // Both importers validate untrusted text and land a draft to review.
    if (sniffIsFullPaxRecipe(text)) {
      applyImportedFullPaxText(text, file.name);
    } else {
      applyImportedLiteText(text);
    }
  }

  // Apply an imported .paxlite recipe's JSON text into the builder as a fresh
  // draft. Shared by the manual file picker and the file-open handoff (a
  // double-clicked .paxlite routed in through the import store).
  function applyImportedLiteText(text: string) {
    const result = parseLiteRecipeJson(text);
    if (!result.ok) {
      setActionStatus({
        kind: 'error',
        text: result.errors[0] ?? 'That file is not a valid PAX Cookbook recipe.',
      });
      return;
    }
    setState(result.state);
    // An imported recipe is a fresh draft until the user saves it.
    setSavedRecipeId(null);
    // The imported recipe becomes the clean baseline for the unsaved-edits prompt.
    baselineSnapshotRef.current = JSON.stringify(result.state);
    resetScheduleForNewDraft();
    const warned = result.warnings.length;
    const warnNote =
      warned > 0
        ? ` ${warned} field${warned === 1 ? '' : 's'} ${warned === 1 ? 'was' : 'were'} adjusted while importing.`
        : '';
    setActionStatus({
      kind: 'success',
      text: 'Imported the recipe. Review the steps above, then Save when it is ready.' + warnNote,
    });
  }

  // Apply an imported full .pax recipe's JSON text into the builder as a fresh
  // draft. Shared by the manual file picker and the file-open handoff (a
  // double-clicked .pax routed in through the import store). The text is
  // validated and mapped by `parseFullPaxRecipeJson`; nothing here runs PAX,
  // saves, or mutates the source file.
  function applyImportedFullPaxText(text: string, fileName: string) {
    const result = parseFullPaxRecipeJson(text);
    if (!result.ok) {
      const message =
        result.errors[0] ?? 'That file is not a valid PAX Cookbook full recipe.';
      setActionStatus({ kind: 'error', text: message });
      return;
    }
    setState(result.state);
    // An imported recipe is a fresh draft until the user saves it.
    setSavedRecipeId(null);
    // The imported recipe becomes the clean baseline for the unsaved-edits prompt.
    baselineSnapshotRef.current = JSON.stringify(result.state);
    resetScheduleForNewDraft();
    setActionStatus({
      kind: 'success',
      text: `Imported from full PAX recipe: ${fileName}. Review the details above, then Save when it is ready.`,
    });
  }

  // File-open handoff sink. When the app was launched from a double-clicked
  // recipe file, App consumes the one-time ticket and pushes the result into
  // the import store. A .paxlite and a full .pax are each applied as a draft to
  // review. Nothing here runs PAX or auto-bakes.
  useEffect(() => {
    const applyPending = () => {
      const pending = takePendingImport();
      if (!pending) {
        return;
      }
      if (pending.kind === 'paxlite') {
        applyImportedLiteText(pending.text);
      } else {
        applyImportedFullPaxText(pending.text, pending.fileName);
      }
    };
    // Apply anything already staged before this component subscribed, then
    // listen for handoffs that complete while the builder is mounted.
    applyPending();
    return subscribePendingImport(applyPending);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);


  // Presentation-only: every edit re-detects the matching preset and updates
  // the Step 1 highlight. There is no saved/imported provenance to preserve.
  function setStateMatchPreset(
    updater: (prev: MiniKitchenRecipeState) => MiniKitchenRecipeState,
  ) {
    setState(prev => applyMatchPreset(prev, updater));
  }

  function handlePresetChange(presetId: PresetId) {
    // Replace the data-scope fields with the preset's defaults but preserve the
    // operator's authentication choice (sign-in mode, Chef's Key, tenant), which
    // is independent of the data scope (see applyPresetSelection).
    setPresetChosen(true);
    setState(prev => applyPresetSelection(prev, presetId));
  }
  function handleIdentityChange(next: LiteRecipeIdentity) {
    setStateMatchPreset(prev => ({ ...prev, identity: next }));
  }
  function handleQueryModeChange(next: QueryModeSelection) {
    setStateMatchPreset(prev => ({
      ...prev,
      query: {
        ...prev.query,
        mode: next.mode,
        onlyUserInfo: next.mode === 'user-info-only' ? true : false,
        includeUserInfo:
          next.mode === 'user-info-only' ? false : next.includeUserInfo || undefined,
      },
    }));
  }
  function handleQueryChange(next: LiteRecipeQuery) {
    // If the user is REMOVING Entra user info from a scope that includes it,
    // confirm first — the change narrows the data scope to "Audit activity
    // only" and can break reports that need user info.
    if (
      state.query.includeUserInfo === true &&
      next.includeUserInfo !== true &&
      state.query.mode !== 'user-info-only'
    ) {
      setEntraDeselectPending(next);
      return;
    }
    applyQueryChange(next);
  }
  function applyQueryChange(next: LiteRecipeQuery) {
    setStateMatchPreset(prev => ({
      ...prev,
      query: { ...next, mode: prev.query.mode },
      // M365 usage and the AIBV dashboard are mutually exclusive, so turning
      // M365 on clears any dashboard target — a recipe never keeps
      // dashboard:'aibv' while M365 is enabled.
      processing:
        next.includeM365Usage === true
          ? { ...prev.processing, dashboard: undefined }
          : prev.processing,
    }));
  }
  function handleProcessingChange(next: LiteRecipeProcessing) {
    setStateMatchPreset(prev => ({ ...prev, processing: next }));
  }
  function handleRollupChange(next: RollupMode) {
    setStateMatchPreset(prev => ({
      ...prev,
      processing: {
        ...prev.processing,
        rollup: next === 'none' ? undefined : next,
        // The AIBV dashboard only applies with a rollup, so turning rollup off
        // clears the dashboard target.
        dashboard: next === 'none' ? undefined : prev.processing.dashboard,
      },
    }));
  }

  function handleDashboardChange(next: DashboardTarget) {
    setStateMatchPreset(prev => ({
      ...prev,
      processing: {
        ...prev.processing,
        // 'aio' and undefined are equivalent; store undefined for AIO to keep
        // the recipe minimal and 'aibv' for the 50-column superset.
        dashboard: next === 'aibv' ? 'aibv' : undefined,
      },
    }));
  }
  function handleCombineModeChange(next: OutputCombineMode) {
    setStateMatchPreset(prev => ({
      ...prev,
      processing: { ...prev.processing, outputCombineMode: next },
    }));
  }
  function handleDestinationsChange(next: LiteRecipeDestinations) {
    setStateMatchPreset(prev => ({ ...prev, destinations: next }));
  }
  function handleAuthChange(next: LiteRecipeAuth) {
    setStateMatchPreset(prev => ({ ...prev, auth: next }));
  }
  function handleAdvancedExtraChange(next: string) {
    setStateMatchPreset(prev => ({
      ...prev,
      advanced: { ...prev.advanced, extraArguments: next },
    }));
  }

  // The "Imported lite recipe" tile opens the same validated file picker as
  // the Import action below.

  const presetLabel =
    PRESET_LABELS[state.ingredients.preset] ?? state.ingredients.preset;
  const recipeNameText = state.identity.name.trim() || 'Untitled recipe';

  // The Audit Operations scope is rendered as read-only chips on the Review
  // step (Step 7). It is the only per-step summary still built; the others fed
  // the rail sub-lines, which the rail no longer shows.
  let scopeSummary: ReactNode;
  if (isUserInfoOnly) {
    scopeSummary = (
      <span className="mk-step__chip mk-step__chip--emerald">
        Entra: User info only
      </span>
    );
  } else {
    const includeM365 = Boolean(state.query.includeM365Usage);
    const excludeCopilot = Boolean(state.query.excludeCopilotInteraction);
    let purviewLabel: string;
    if (excludeCopilot && includeM365) {
      purviewLabel = 'Purview: M365 usage';
    } else if (excludeCopilot) {
      purviewLabel = 'Purview: No CopilotInteraction';
    } else if (includeM365) {
      purviewLabel = 'Purview: Copilot + M365 usage';
    } else {
      purviewLabel = 'Purview: CopilotInteraction';
    }

    const entraLabel = state.query.includeUserInfo
      ? 'Entra: Include with Purview'
      : 'Entra: Not included';

    const filterCount =
      (state.processing.userIds?.length ?? 0) +
      (state.processing.groupNames?.length ?? 0) +
      (state.processing.activityTypes?.length ?? 0) +
      (state.processing.agentFilter?.ids?.length ?? 0) +
      (state.processing.promptFilter ? 1 : 0);

    const rollup = state.processing.rollup;
    const rollupLabel =
      rollup === 'rollup'
        ? 'Rollup only'
        : rollup === 'rollup-plus-raw'
        ? 'Rollup + raw'
        : null;

    scopeSummary = (
      <>
        <span className="mk-step__chip mk-step__chip--blue">{purviewLabel}</span>
        <span className="mk-step__chip mk-step__chip--emerald">{entraLabel}</span>
        {rollupLabel ? (
          <span className="mk-step__chip mk-step__chip--violet">{rollupLabel}</span>
        ) : null}
        {filterCount > 0 ? (
          <span className="mk-step__chip mk-step__chip--slate">
            {filterCount} filter{filterCount === 1 ? '' : 's'}
          </span>
        ) : null}
      </>
    );
  }

  const outputLabel = STORAGE_TIER_LABELS[state.destinations.fact.tier];
  const authLabel = AUTH_MODE_LABELS[state.auth.mode];
  const activityTypeCount = state.processing.activityTypes?.length ?? 0;
  const combineEligible =
    state.query.mode === 'audit-query' &&
    !state.query.includeM365Usage &&
    activityTypeCount > 1;
  const combineDisabledByM365 =
    state.query.mode === 'audit-query' && Boolean(state.query.includeM365Usage);

  const timeStart = state.query.startDate?.trim() ?? '';
  const timeEnd = state.query.endDate?.trim() ?? '';

  const activeStepMeta = EDITOR_STEPS[activeStep - 1] ?? EDITOR_STEPS[0];

  // Bake gate + confirmation summaries, recomputed each render so the button
  // and modal always reflect the live recipe / readiness state.
  const bakeBlockReason = deriveBakeBlockReason();
  const bakeRecipeName =
    state.identity.name && state.identity.name.trim().length > 0
      ? state.identity.name.trim()
      : 'this recipe';
  const bakeAuthSummary = readiness?.auth
    ? readiness.auth.detail || readiness.auth.mode
    : 'Interactive sign-in on this PC.';
  const bakeDestinationSummary = readiness?.destination
    ? readiness.destination.detail || readiness.destination.kind
    : 'The recipe’s configured output destination.';
  const bakeCommandSummary =
    readiness?.command && readiness.command.length > 0 ? readiness.command : null;

  // ---- Review step (Step 7) derived data ----------------------------------
  // Each save-blocking requirement is owned by the wizard step where it is
  // fixed, so the Review step can link an outstanding item straight back to it.
  const STEP_FOR_REQUIREMENT: Record<string, number> = {
    name: 1, // Basics
    tenantId: 2, // Authentication
    dateRange: 3, // Date Range (both sides blank — one combined item)
    startDate: 3, // Date Range (half-filled — missing start)
    endDate: 3, // Date Range (half-filled — missing end)
    agentIds: 4, // Audit Operations
    factOutput: 5, // Output
    userInfoOutput: 5, // Output
  };
  const reviewIssues = saveRequirements.map(req => ({
    id: req.id,
    label: req.label,
    step: STEP_FOR_REQUIREMENT[req.id] ?? 1,
  }));
  const stepsNeedingAttention = new Set<number>(reviewIssues.map(issue => issue.step));
  const outstandingCount = reviewIssues.length;

  // Read-only per-step summary values for the Review step. Each reuses a label
  // or helper already derived above for the rail summaries.
  const reviewBasicsPreset =
    state.ingredients.preset === 'customAuditExport' ? 'Custom (no preset)' : presetLabel;
  const reviewChefKey = state.auth.chefKeyId ? 'Bound' : 'Not bound';
  const reviewDateRange = isUserInfoOnly
    ? 'Not used for a user-info-only pull'
    : state.query.dateMode === 'previous-day'
    ? 'Previous day (previous full UTC day)'
    : timeStart && timeEnd
    ? `${timeStart} \u2192 ${timeEnd}`
    : 'Not set';
  const reviewOutputPath = isUserInfoOnly
    ? (state.destinations.userInfo.path ?? '').trim() || 'Not set'
    : (state.destinations.fact.path ?? '').trim() || 'Not set';
  const reviewScheduleText = serverSchedule
    ? `Scheduled \u2014 ${describeSchedule(serverSchedule)}`
    : 'Not scheduled';

  // Issue C: counts for the collapsible review disclosures' badges. They reuse
  // the same helpers the summary cards use, so a card's count and its section
  // badge always match.
  const reviewWarningsCount = countReviewWarnings(
    derived.command,
    derived.permissions,
  );
  const reviewAssumptionsCount = countReviewAssumptions(
    derived.command,
    derived.permissions,
  );
  const reviewPermissionsCount = countReviewPermissions(derived.permissions);

  // Per-step rail verdict (UX14.6). The decision is a pure function so it can be
  // unit-tested independently; here the builder just gathers the live derived
  // inputs and asks for each step's status. It adds no gate — it only describes
  // what each step currently looks like (incomplete ring / valid check / error
  // warning). Step 6 Schedule is optional and never reads 'error' for being
  // unconfigured; Step 7 is 'valid' once the recipe is saveable.
  function stepStatus(n: number): WizardStepStatus {
    return computeWizardStepStatus(n, {
      stepsNeedingAttention,
      recipeName: state.identity.name,
      isUserInfoOnly,
      dateMode: state.query.dateMode,
      startDate: timeStart,
      endDate: timeEnd,
      outputPath: reviewOutputPath,
      scheduleConfigured: Boolean(serverSchedule) || scheduleDraft.enabled,
      candidateSaveable,
    });
  }

  // Why Save is unavailable, or null when it is ready — surfaced as the Save
  // button's title on hover. Saving only needs a recipe name (an incomplete
  // recipe persists as a draft), so the only reason Save is unavailable is a
  // busy in-flight action.
  const saveDisabledReason: string | null = busy
    ? 'Please wait — finishing the current action.'
    : null;

  // UX14 R4/R5: render-time "unsaved changes" flag for the pinned action bar.
  // Mirrors the exact comparison the open prompt uses (builder state OR schedule
  // draft vs their clean baselines), recomputed every render because `state` and
  // `scheduleDraft` are React state. The Update/Save and Discard buttons both
  // use it for visibility — each renders only when dirty (Update as the primary
  // button). It is intentionally broader than the bake gate's isDirtyForBake()
  // (builder-state-only): a schedule-only change reads as dirty/discardable here
  // but still does not block a bake, since the schedule is not part of the recipe
  // body that runs.
  const isDirty =
    JSON.stringify(state) !== baselineSnapshotRef.current ||
    JSON.stringify(scheduleDraft) !== scheduleBaselineRef.current;
  // Feed the once-registered navigation guard the latest dirty state without
  // re-registering each render. Writing a ref during render is render-safe (it
  // triggers no re-render) and keeps the guard in lockstep with the exact flag
  // the "Discard changes" button uses.
  isDirtyRef.current = isDirty;

  return (
    // The donor Mini-Kitchen builder CSS is scoped under `.mini-kitchen-page`;
    // this wrapper restores that scope so the donor card/field/panel styling
    // applies inside the /app/ shell content region.
    <div className="mini-kitchen-page">
      <div className="rce">
        <section
          className="rce__note"
          role="note"
          aria-label="Preview-only notice"
        >
          <p className="rce__note-line">
            Build and review a PAX recipe before anything runs.
          </p>
          <p className="rce__note-hint">
            Preview and Check readiness are safe: they do not run PAX, connect to
            a tenant, or collect credentials. Baking runs PAX only after you
            confirm a Bake; Taste Test and scheduling are still disabled in this
            build.
          </p>
        </section>

        {pantryDraftNote ? (
          <section
            className="rce__pantry-banner"
            role="note"
            aria-label="Draft starting point"
          >
            <p className="rce__pantry-banner-line">{pantryDraftNote}</p>
            <p className="rce__pantry-banner-hint">
              Nothing has been saved yet. Fill in the required details below, then
              Save the recipe to keep it.
            </p>
          </section>
        ) : null}

        <div className="mk-wizard">
          {/* Per-step status: stepStatus returns the real verdict for each step
              (incomplete ring / valid check / error warning). The Schedule step
              (6) is optional and never reads 'error' for being unconfigured. The
              active step is highlighted independently of its status. */}
          <WizardRail
            steps={EDITOR_STEPS}
            activeStep={activeStep}
            onSelect={setActiveStep}
            statusFor={stepStatus}
            badgeFor={n => (n === 6 && !scheduleDraft.enabled ? 'Optional' : null)}
            headerAccessory={<ContextualHelpButton topic="recipeBuilder" />}
          />

          <div className="mk-wizard__content" ref={contentRef}>
            <header className="mk-wizard__content-head">
              <h2 className="mk-wizard__content-title">
                Step {activeStep} — {activeStepMeta.title}
                {activeStep === 7 ? (
                  <ContextualHelpButton topic="reviewGeneratedCommand" />
                ) : null}
              </h2>
              <p className="mk-wizard__content-intro">{activeStepMeta.intro}</p>
            </header>

            <div className="mk-wizard__content-body">
              {/* Step 1 — Basics: the preset picker plus the recipe name and
                  description. The preset picker is always shown so a saved recipe
                  can switch its starting template (re-applying that preset's
                  data-scope overrides; auth is preserved). */}
              {activeStep === 1 ? (
                <>
                  <PresetPicker
                    selected={presetChosen ? state.ingredients.preset : null}
                    onSelect={handlePresetChange}
                    onImportClick={handleImportClick}
                  />
                  <RecipeBasics
                    value={state.identity}
                    onChange={handleIdentityChange}
                    nameError={null}
                  />
                </>
              ) : null}

              {/* Step 2 — Authentication: sign-in method and Chef's Key binding,
                  plus the credentials note. Auth-focused — the advanced switches
                  now live with Audit Operations (Step 4). */}
              {activeStep === 2 ? (
                <>
                  <p className="mk-chefskeys-note" role="note">
                    A <strong>Chef&rsquo;s Key</strong> is your saved Microsoft&nbsp;365
                    sign-in for this recipe. PAX Cookbook uses it to pull your audit
                    data on your behalf each time this recipe bakes. You create and
                    manage keys on the <strong>Chef&rsquo;s Keys</strong> page in the
                    left menu &mdash; each key&rsquo;s secret is kept in Windows
                    Credential Manager on this PC and never leaves your device.
                  </p>
                  <AuthContextCard
                    value={state.auth}
                    onChange={handleAuthChange}
                    onCreateChefKey={() =>
                      setNavIntent({
                        section: 'chefskeys',
                        proceed: () => requestShellSection('chefskeys'),
                        cancel: () => {},
                      })
                    }
                  />
                </>
              ) : null}

              {/* Step 3 — Date Range. */}
              {activeStep === 3 ? (
                <DateRangeCard
                  value={state.query}
                  disabled={isUserInfoOnly}
                  onChange={handleQueryChange}
                />
              ) : null}

              {/* Step 4 — Audit Operations: the densest step — the Purview query
                  mode and data collection, the audit filters, and the advanced
                  switches. The rollup control moved to Output (Step 5). */}
              {activeStep === 4 ? (
                <>
                  <QueryModeCard
                    value={{
                      mode: state.query.mode,
                      includeUserInfo: Boolean(state.query.includeUserInfo),
                    }}
                    onChange={handleQueryModeChange}
                  />
                  <DataCollectionCard
                    value={state.query}
                    disabled={isUserInfoOnly}
                    onChange={handleQueryChange}
                    activityTypes={state.processing.activityTypes}
                    onActivityTypesChange={next =>
                      setStateMatchPreset(prev => ({
                        ...prev,
                        processing: { ...prev.processing, activityTypes: next },
                      }))
                    }
                  />
                  <AuditFiltersCard
                    value={state.processing}
                    disabled={isUserInfoOnly}
                    onChange={handleProcessingChange}
                  />
                  <AdvancedArgsCard
                    value={state.advanced.extraArguments ?? ''}
                    onChange={handleAdvancedExtraChange}
                  />
                </>
              ) : null}

              {/* Step 5 — Output: the output destination, plus the optional
                  rollup control relocated here from Audit Operations. Rollup is
                  always optional and never blocks Save. */}
              {activeStep === 5 ? (
                <>
                  <OutputTargetCard
                    value={state.destinations}
                    userInfoOnly={isUserInfoOnly}
                    rollupActive={
                      state.processing.rollup === 'rollup' ||
                      state.processing.rollup === 'rollup-plus-raw'
                    }
                    combineMode={state.processing.outputCombineMode}
                    combineEligible={combineEligible}
                    combineDisabledByM365={combineDisabledByM365}
                    userInfoEligible={
                      isUserInfoOnly || Boolean(state.query.includeUserInfo)
                    }
                    onChange={handleDestinationsChange}
                    onCombineModeChange={handleCombineModeChange}
                  />
                  <RollupCard
                    value={state.processing.rollup ?? 'none'}
                    disabled={isUserInfoOnly}
                    onChange={handleRollupChange}
                  >
                  {/* Dashboard target — a subsection inside Rollup; shown only
                      with a rollup on and M365 off, mirroring the broker's
                      -Dashboard AIBV emit guard. */}
                  {(state.processing.rollup === 'rollup' ||
                    state.processing.rollup === 'rollup-plus-raw') &&
                  state.query.includeM365Usage !== true ? (
                    <div className="mk-subsection" id="mk-dashboard-target">
                      <div className="mk-subsection__head">
                        <h3 className="mk-subsection-title">Dashboard target</h3>
                        <ContextualHelpButton topic="rollupDashboard" />
                      </div>
                      <p className="mk-card__subtitle">
                        Choose the dashboard column layout the rollup produces.
                      </p>
                      <p className="mk-card__help">
                        AIBV produces the full AI Business Value superset; AI-in-One
                        is the default.
                      </p>
                      <div
                        className="mk-radio-cards"
                        role="radiogroup"
                        aria-label="Dashboard target"
                      >
                        {(
                          [
                            {
                              id: 'aio' as const,
                              title: 'AI-in-One (AIO)',
                              desc: 'The default dashboard layout.',
                              scope: 'ai-in-one' as const,
                            },
                            {
                              id: 'aibv' as const,
                              title: 'AI Business Value (AIBV)',
                              desc: 'The AI Business Value superset.',
                              scope: 'ai-business-value' as const,
                            },
                          ]
                        ).map(opt => {
                          const inputId = `mk-dashboard-${opt.id}`;
                          const selected =
                            (state.processing.dashboard ?? 'aio') === opt.id;
                          return (
                            <label
                              key={opt.id}
                              htmlFor={inputId}
                              className={
                                'mk-radio-card' +
                                (selected ? ' mk-radio-card--selected' : '')
                              }
                            >
                              <input
                                type="radio"
                                id={inputId}
                                name="mk-dashboard-target"
                                value={opt.id}
                                className="mk-radio-card__input"
                                checked={selected}
                                onChange={() => handleDashboardChange(opt.id)}
                              />
                              <span className="mk-radio-card__title">
                                {opt.title}
                                <DashboardReqTag scopes={[opt.scope]} />
                              </span>
                              <span className="mk-radio-card__desc">{opt.desc}</span>
                            </label>
                          );
                        })}
                      </div>
                    </div>
                  ) : null}
                  </RollupCard>
                </>
              ) : null}

              {/* Step 6 — Schedule (optional): the ScheduleCard, gated on a bound
                  Chef's Key. Relocated here from the former Sign-in & Context
                  step; its props and behavior are unchanged. */}
              {activeStep === 6 ? (
                <ScheduleCard
                  value={scheduleDraft}
                  onChange={setScheduleDraft}
                  hasBoundChefKey={Boolean(state.auth.chefKeyId)}
                  authMode={state.auth.mode}
                  onGoToAuthStep={() => setActiveStep(2)}
                  statusNote={scheduleNote}
                />
              ) : null}

              {/* Step 7 — Review + Save. The customer reads a plain, read-only
                  summary of every step first, then a status overview and the
                  consolidated list of anything still needed (each linking back
                  to the step that owns it). The raw command preview is tucked
                  into a collapsed accordion. Save lives only on this step; Bake
                  stays gated on bakeBlockReason. */}
              {activeStep === 7 ? (
                <>
                <section className="mk-review-status" aria-label="Recipe status">
                  <p
                    className={
                      outstandingCount > 0
                        ? 'mk-review-status__headline mk-review-status__headline--attention'
                        : 'mk-review-status__headline mk-review-status__headline--ready'
                    }
                    role="status"
                  >
                    {outstandingCount > 0
                      ? `${outstandingCount} item${outstandingCount === 1 ? '' : 's'} still needed before you can save.`
                      : candidateSaveable
                      ? 'Ready to save — every required detail is set.'
                      : 'Almost ready — finish the highlighted details below.'}
                  </p>
                  <ul className="mk-review-status__steps">
                    {EDITOR_STEPS.map(step => {
                      const isCurrent = step.n === 7;
                      const needsAttention = stepsNeedingAttention.has(step.n);
                      const tone = isCurrent
                        ? 'current'
                        : needsAttention
                        ? 'attention'
                        : 'ready';
                      const stateWord = isCurrent
                        ? 'Current step'
                        : needsAttention
                        ? 'Needs attention'
                        : 'Ready';
                      return (
                        <li key={step.n} className="mk-review-status__step">
                          <button
                            type="button"
                            className={`mk-review-status__pill mk-review-status__pill--${tone}`}
                            onClick={() => setActiveStep(step.n)}
                            aria-label={`Step ${step.n}, ${step.title}: ${stateWord}. Go to this step.`}
                          >
                            <span
                              className="mk-review-status__dot"
                              aria-hidden="true"
                            />
                            <span className="mk-review-status__step-name">
                              {step.n}. {step.title}
                            </span>
                          </button>
                        </li>
                      );
                    })}
                  </ul>
                </section>

                {reviewIssues.length > 0 ? (
                  <section
                    className="mk-review-validation"
                    role="status"
                    aria-label="Details still needed before saving"
                  >
                    <p className="mk-review-validation__title">
                      {describeSaveRequirements(saveRequirements)}
                    </p>
                    <ul className="mk-review-validation__list">
                      {reviewIssues.map(issue => (
                        <li
                          key={issue.id}
                          className="mk-review-validation__item"
                        >
                          <span className="mk-review-validation__label">
                            {issue.label}
                          </span>
                          <button
                            type="button"
                            className="mk-review-validation__jump"
                            onClick={() => setActiveStep(issue.step)}
                          >
                            Go to {EDITOR_STEPS[issue.step - 1].title}
                          </button>
                        </li>
                      ))}
                    </ul>
                  </section>
                ) : null}

                <section
                  className="mk-review-summary"
                  aria-label="Recipe summary by step"
                >
                  <h3 className="mk-review-summary__title">Recipe summary</h3>
                  <ol className="mk-review-summary__list">
                    <li className="mk-review-summary__step">
                      <div className="mk-review-summary__head">
                        <span className="mk-review-summary__step-title">
                          1. Basics
                        </span>
                        <button
                          type="button"
                          className="mk-review-summary__edit"
                          onClick={() => setActiveStep(1)}
                        >
                          Edit
                        </button>
                      </div>
                      <dl className="mk-review-summary__rows">
                        <div className="mk-review-summary__row">
                          <dt>Name</dt>
                          <dd>{recipeNameText}</dd>
                        </div>
                        <div className="mk-review-summary__row">
                          <dt>Preset</dt>
                          <dd>{reviewBasicsPreset}</dd>
                        </div>
                      </dl>
                    </li>

                    <li className="mk-review-summary__step">
                      <div className="mk-review-summary__head">
                        <span className="mk-review-summary__step-title">
                          2. Authentication
                        </span>
                        <button
                          type="button"
                          className="mk-review-summary__edit"
                          onClick={() => setActiveStep(2)}
                        >
                          Edit
                        </button>
                      </div>
                      <dl className="mk-review-summary__rows">
                        <div className="mk-review-summary__row">
                          <dt>Method</dt>
                          <dd>{authLabel}</dd>
                        </div>
                        <div className="mk-review-summary__row">
                          <dt>Chef’s Key</dt>
                          <dd>{reviewChefKey}</dd>
                        </div>
                      </dl>
                    </li>

                    <li className="mk-review-summary__step">
                      <div className="mk-review-summary__head">
                        <span className="mk-review-summary__step-title">
                          3. Date Range
                        </span>
                        <button
                          type="button"
                          className="mk-review-summary__edit"
                          onClick={() => setActiveStep(3)}
                        >
                          Edit
                        </button>
                      </div>
                      <dl className="mk-review-summary__rows">
                        <div className="mk-review-summary__row">
                          <dt>Date range</dt>
                          <dd>{reviewDateRange}</dd>
                        </div>
                      </dl>
                    </li>

                    <li className="mk-review-summary__step">
                      <div className="mk-review-summary__head">
                        <span className="mk-review-summary__step-title">
                          4. Audit Operations
                        </span>
                        <button
                          type="button"
                          className="mk-review-summary__edit"
                          onClick={() => setActiveStep(4)}
                        >
                          Edit
                        </button>
                      </div>
                      <div className="mk-review-summary__chips">{scopeSummary}</div>
                    </li>

                    <li className="mk-review-summary__step">
                      <div className="mk-review-summary__head">
                        <span className="mk-review-summary__step-title">
                          5. Output
                        </span>
                        <button
                          type="button"
                          className="mk-review-summary__edit"
                          onClick={() => setActiveStep(5)}
                        >
                          Edit
                        </button>
                      </div>
                      <dl className="mk-review-summary__rows">
                        <div className="mk-review-summary__row">
                          <dt>Output</dt>
                          <dd>{outputLabel}</dd>
                        </div>
                        <div className="mk-review-summary__row">
                          <dt>Folder</dt>
                          <dd>{reviewOutputPath}</dd>
                        </div>
                      </dl>
                    </li>

                    <li className="mk-review-summary__step">
                      <div className="mk-review-summary__head">
                        <span className="mk-review-summary__step-title">
                          6. Schedule
                        </span>
                        <button
                          type="button"
                          className="mk-review-summary__edit"
                          onClick={() => setActiveStep(6)}
                        >
                          Edit
                        </button>
                      </div>
                      <dl className="mk-review-summary__rows">
                        <div className="mk-review-summary__row">
                          <dt>Schedule</dt>
                          <dd>{reviewScheduleText}</dd>
                        </div>
                      </dl>
                    </li>
                  </ol>
                </section>

                {/* Issue C — warnings, assumptions, and required permissions are
                    each their own collapsible section, collapsed by default so a
                    clean Step 7 stays calm. The always-visible summary cards
                    above carry the counts; clicking a card expands its matching
                    section and scrolls to it. The raw-command accordion below
                    stays separate. */}
                <section
                  className="mk-review-prominent"
                  aria-label="Warnings, assumptions, and required permissions"
                >
                  <ReviewSummaryCards
                    command={derived.command}
                    permissions={derived.permissions}
                    onOpenWarnings={openWarningsDetails}
                    onOpenAssumptions={openAssumptionsDetails}
                    onOpenPermissions={openPermissionsDetails}
                  />

                  <details
                    id="mk-review-warnings"
                    className="mk-review-disclosure mk-review-disclosure--warnings"
                    open={warningsOpen}
                    onToggle={event =>
                      setWarningsOpen(
                        (event.currentTarget as HTMLDetailsElement).open,
                      )
                    }
                  >
                    <summary className="mk-review-disclosure__summary">
                      <span className="mk-review-disclosure__label">
                        Warnings &amp; blocked items
                      </span>
                      <span
                        className="mk-review-disclosure__badge"
                        aria-label={`${reviewWarningsCount} item${reviewWarningsCount === 1 ? '' : 's'}`}
                      >
                        {reviewWarningsCount}
                      </span>
                    </summary>
                    <div className="mk-review-disclosure__body">
                      <WarningsPanel
                        commandWarnings={derived.command.warnings}
                        permissionWarnings={derived.permissions.warnings}
                      />
                      <BlockedItemsPanel blocked={derived.command.blocked} />
                    </div>
                  </details>

                  <details
                    id="mk-review-assumptions"
                    className="mk-review-disclosure mk-review-disclosure--assumptions"
                    open={assumptionsOpen}
                    onToggle={event =>
                      setAssumptionsOpen(
                        (event.currentTarget as HTMLDetailsElement).open,
                      )
                    }
                  >
                    <summary className="mk-review-disclosure__summary">
                      <span className="mk-review-disclosure__label">Assumptions</span>
                      <span
                        className="mk-review-disclosure__badge"
                        aria-label={`${reviewAssumptionsCount} item${reviewAssumptionsCount === 1 ? '' : 's'}`}
                      >
                        {reviewAssumptionsCount}
                      </span>
                    </summary>
                    <div className="mk-review-disclosure__body">
                      <AssumptionsPanel
                        commandAssumptions={derived.command.assumptions}
                        permissionAssumptions={derived.permissions.assumptions}
                      />
                    </div>
                  </details>

                  <details
                    id="mk-review-permissions"
                    className="mk-review-disclosure mk-review-disclosure--permissions"
                    open={permissionsOpen}
                    onToggle={event =>
                      setPermissionsOpen(
                        (event.currentTarget as HTMLDetailsElement).open,
                      )
                    }
                  >
                    <summary className="mk-review-disclosure__summary">
                      <span className="mk-review-disclosure__label">
                        Permissions needed
                      </span>
                      <span
                        className="mk-review-disclosure__badge"
                        aria-label={`${reviewPermissionsCount} item${reviewPermissionsCount === 1 ? '' : 's'}`}
                      >
                        {reviewPermissionsCount}
                      </span>
                    </summary>
                    <div className="mk-review-disclosure__body">
                      <PermissionsPreview permissions={derived.permissions} />
                    </div>
                  </details>
                </section>

                {/* Advanced: Raw command — only the raw PAX command preview,
                    collapsed by default so the prominent warnings and
                    permissions above are what the customer reads first. */}
                <details className="mk-review-accordion">
                  <summary className="mk-review-accordion__summary">
                    Advanced: Raw command
                  </summary>
                <section className="mk-review" aria-labelledby="mk-review-heading">
                  <header className="mk-review__head">
                    <div className="mk-review__title-block">
                      <h3 id="mk-review-heading" className="mk-review__title">
                        Command preview
                      </h3>
                      {derived.command.ready ? (
                        <p className="mk-review__intro">
                          This command is a preview to help you sanity-check the
                          recipe. Check readiness confirms what is still needed
                          before a run — previewing does not run anything.
                        </p>
                      ) : (
                        <p
                          className="mk-review__intro mk-review__intro--blocked"
                          role="status"
                        >
                          {derived.command.blockers.length === 1
                            ? 'This command is not ready yet — 1 required detail still needs your input. '
                            : `This command is not ready yet — ${derived.command.blockers.length} required details still need your input. `}
                          Finish the highlighted items in the warnings above.
                        </p>
                      )}
                    </div>
                    <div className="mk-review__tabs">
                      <CommandTabs
                        groupId="mk-review-cmd"
                        tabs={REVIEW_TABS}
                        activeId={reviewTab}
                        onSelect={setReviewTab}
                        ariaLabel="Command view"
                      />
                    </div>
                  </header>

                  <div
                    className="mk-review__command"
                    id={`mk-review-cmd-panel-${reviewTab}`}
                    role="tabpanel"
                    aria-labelledby={`mk-review-cmd-tab-${reviewTab}`}
                  >
                    <GeneratedCommandPanel
                      command={derived.command}
                      activeView={reviewTab}
                    />
                  </div>

                  <footer className="mk-review__footer" role="contentinfo">
                    <p className="mk-review__footer-line">
                      This command is a preview. Previewing does not run PAX,
                      validate paths, contact tenants, or collect credentials.
                      Use Check readiness to validate the recipe. Review the
                      warnings and blocked items above before copying.
                    </p>
                  </footer>
                </section>
                </details>

                {translation.needsPrep ? (
                  <div
                    className="mk-needsprep"
                    role="status"
                    aria-label="Readiness notes for running this recipe later"
                  >
                    <p className="mk-needsprep__title">
                      You can save this recipe now. Before it can run on this PC,
                      finish these readiness items:
                    </p>
                    {translation.needsPrepReasons.length > 0 ? (
                      <ul className="mk-needsprep__list">
                        {translation.needsPrepReasons.map((reason, index) => (
                          <li key={index} className="mk-needsprep__item">
                            {reason}
                          </li>
                        ))}
                      </ul>
                    ) : null}
                  </div>
                ) : null}

                {translation.warnings.length > 0 ? (
                  <ul
                    className="mk-compat-warnings"
                    aria-label="Compatibility warnings"
                  >
                    {translation.warnings.map(note => (
                      <li
                        key={note.id}
                        className={
                          note.severity === 'warning'
                            ? 'mk-compat-warnings__item mk-compat-warnings__item--warn'
                            : 'mk-compat-warnings__item'
                        }
                      >
                        {note.message}
                      </li>
                    ))}
                  </ul>
                ) : null}

                <AdapterReadinessPanel
                  phase={readinessPhase}
                  result={readiness}
                  errorText={readinessError}
                  canCheck={candidateReady && !busy}
                  onCheck={handleCheckReadiness}
                  showTrigger={true}
                />
                </>
              ) : null}
            </div>

            {/* Persistent primary-action bar. Rendered outside the per-step
                blocks so Check readiness / Bake are present on every step, and
                pinned to the bottom of the content column (position: sticky) so
                it never scrolls away. Save is shown whenever a new recipe is
                unsaved (so it is always reachable while building) or a saved
                recipe has unsaved edits. Saving only needs a recipe name (an
                incomplete recipe persists as a draft), so the Save button is
                enabled whenever there are unsaved edits; a blank name or a save
                failure shows a brief inline hint below. Discard shows only when
                there are unsaved edits. The bake path (confirm modal + Windows
                Hello step-up + startCook) is unchanged and still enforces full
                completeness. */}
            <div
              className="mk-actionbar"
              role="group"
              aria-label="Recipe actions"
            >
              <div className="mk-actionbar__primary">
              <button
                type="button"
                className={
                  'mk-preview-boundary__btn' +
                  (isDirty ? ' mk-preview-boundary__btn--primary' : '')
                }
                onClick={() => handleSaveOrUpdate()}
                disabled={busy || !isDirty}
                aria-disabled={busy || !isDirty}
                title={
                  !busy && !isDirty
                    ? 'No unsaved changes to save.'
                    : (saveDisabledReason ?? 'Save this recipe in PAX Cookbook.')
                }
              >
                {saveLabel}
              </button>
              {isDirty ? (
                <button
                  type="button"
                  className="mk-preview-boundary__btn"
                  onClick={handleDiscardChanges}
                  disabled={busy || bakeSubmitting}
                  aria-disabled={busy || bakeSubmitting}
                  title="Discard unsaved changes and return to the last saved recipe. Nothing is run."
                >
                  Discard changes
                </button>
              ) : null}
              <button
                type="button"
                className="mk-preview-boundary__btn mk-preview-boundary__btn--bake"
                onClick={handleBakeClick}
                disabled={bakeBlockReason !== null}
                aria-disabled={bakeBlockReason !== null}
                title={bakeBlockReason ?? 'Bake runs this saved recipe on this PC.'}
              >
                Bake (run)
              </button>
              </div>
              <div
                className="mk-actionbar__nav"
                role="group"
                aria-label="Step navigation"
              >
                <button
                  type="button"
                  className="mk-preview-boundary__btn"
                  onClick={() => setActiveStep(s => Math.max(1, s - 1))}
                  disabled={activeStep === 1}
                  aria-disabled={activeStep === 1}
                  title={
                    activeStep === 1
                      ? 'You\u2019re on the first step.'
                      : 'Go to the previous step.'
                  }
                >
                  ← Back
                </button>
                <button
                  type="button"
                  className="mk-preview-boundary__btn mk-preview-boundary__btn--primary"
                  onClick={() => setActiveStep(s => Math.min(7, s + 1))}
                  disabled={activeStep === 7}
                  aria-disabled={activeStep === 7}
                  title={
                    activeStep === 7
                      ? 'You\u2019re on the last step.'
                      : 'Go to the next step.'
                  }
                >
                  Next →
                </button>
              </div>
            </div>
            {saveHint ? (
              <p className="mk-actionbar__hint" role="status">
                {saveHint}
              </p>
            ) : null}
          </div>
        </div>

        <input
          ref={importInputRef}
          type="file"
          accept=".paxlite,.pax,.json"
          className="mk-visually-hidden"
          aria-hidden="true"
          tabIndex={-1}
          onChange={handleImportFile}
        />
      </div>

      {bakeConfirmOpen ? (
        <BakeConfirmModal
          recipeName={bakeRecipeName}
          authSummary={bakeAuthSummary}
          destinationSummary={bakeDestinationSummary}
          commandSummary={bakeCommandSummary}
          submitting={bakeSubmitting}
          error={bakeError}
          onCancel={handleBakeCancel}
          onConfirm={handleConfirmBake}
        />
      ) : null}

      {navIntent ? (
        <NavGuardModal
          saving={busy}
          onSaveAndLeave={() => {
            const intent = navIntent;
            void (async () => {
              const saved = await handleSaveOrUpdate();
              setNavIntent(null);
              if (saved) {
                intent.proceed();
              } else {
                // Blocked (missing details) or failed — the save-requirements
                // dialog / error is already shown. Cancel the navigation and
                // keep the operator in the builder.
                intent.cancel();
              }
            })();
          }}
          onLeave={() => {
            const intent = navIntent;
            setNavIntent(null);
            intent.proceed();
          }}
          onStay={() => {
            const intent = navIntent;
            setNavIntent(null);
            intent.cancel();
          }}
        />
      ) : null}

      {discardConfirmOpen ? (
        <DiscardConfirmModal
          onCancel={cancelDiscardChanges}
          onConfirm={confirmDiscardChanges}
        />
      ) : null}

      {pendingOpenSummary ? (
        <OpenRecipeConfirmModal
          onCancel={cancelOpenRecipe}
          onConfirm={confirmOpenRecipe}
        />
      ) : null}

      {entraDeselectPending ? (
        <div
          className="mk-modal__backdrop"
          role="presentation"
          onClick={(e) => {
            if (e.target === e.currentTarget) {
              setEntraDeselectPending(null);
            }
          }}
        >
          <div
            className="mk-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="mk-entra-deselect-title"
          >
            <header className="mk-modal__head">
              <h2 id="mk-entra-deselect-title" className="mk-modal__title">
                Remove Entra user info?
              </h2>
              <p className="mk-modal__subtitle">
                You&rsquo;ve chosen a data scope that includes Entra user information.
                Removing Entra user info will change your data scope to &ldquo;Audit
                activity only.&rdquo;
              </p>
              <p className="mk-modal__subtitle">
                If you&rsquo;re using this data to populate a report that requires Entra
                user info, the report may not populate correctly.
              </p>
            </header>
            <div className="mk-modal__actions">
              <button
                type="button"
                className="mk-modal__button"
                onClick={() => setEntraDeselectPending(null)}
              >
                Cancel
              </button>
              <button
                type="button"
                className="mk-modal__button mk-modal__button--primary"
                onClick={() => {
                  const pending = entraDeselectPending;
                  setEntraDeselectPending(null);
                  if (pending) {
                    applyQueryChange(pending);
                  }
                }}
              >
                Continue
              </button>
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}

