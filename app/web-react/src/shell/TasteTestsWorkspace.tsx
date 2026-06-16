/**
 * Taste Tests — a customer-ready preflight / rehearsal surface.
 *
 * Taste Tests let someone pick a saved recipe and run a safe preflight check
 * before they ever bake it: it confirms the recipe is filled in, that the PAX
 * engine is ready, that the sign-in / Chef's Key context is in place, and that
 * the output destination is set — then summarizes what (if anything) still
 * needs attention. It is a safety / rehearsal capability, not execution.
 *
 * Taste Tests NEVER run PAX and NEVER bake a recipe. The check is read-only: it
 * reuses the broker's existing, non-executing readiness projection
 * (`getRecipeReadiness`), the same one the Recipes surface uses. No process is
 * started, no cook/bake row is written, and no secret is read or displayed.
 * When a recipe is not ready, the surface explains the gaps and points the user
 * at the right place to fix them (Edit recipe, Chef's Keys, Settings).
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  getRecipe,
  getRecipeReadiness,
  listRecipes,
  type RecipeReadinessBody,
  type RecipeSummary,
} from '../host/brokerBridge';
import { SectionHeader } from './components/SectionHeader';
import {
  IconAlertCircle,
  IconBook,
  IconCheckCircle,
  IconInfoCircle,
  IconKey,
  IconPencil,
  IconRefresh,
  IconSearch,
  IconShieldCheck,
  IconTarget,
} from './CookbookIllustrations';
import { RecipeStatusBadge, toneForRecipeStatus } from './StatusCard';
import {
  formatModified,
  rememberPendingSelect,
  requestShellSection,
} from './shellNav';

/** Bounded fallback used whenever a readiness signal is absent in this build. */
const NOT_REPORTED = 'Not reported by this build';

type ListPhase = 'loading' | 'loaded' | 'error';
type TestPhase = 'idle' | 'detail-loading' | 'detail-error' | 'running' | 'done' | 'error';

/** One row of the preflight checklist, derived from the readiness body. */
interface ChecklistItem {
  id: string;
  label: string;
  state: 'ready' | 'attention' | 'unknown';
  detail: string;
}

function recipeName(summary: RecipeSummary): string {
  const raw = typeof summary.name === 'string' ? summary.name.trim() : '';
  return raw.length > 0 ? raw : 'Untitled recipe';
}

/** Map the overall readiness verdict onto a single taste-test outcome. */
function outcomeFor(phase: TestPhase, readiness: RecipeReadinessBody | null): {
  tone: 'ready' | 'attention' | 'unknown' | 'error';
  title: string;
  detail: string;
} {
  if (phase === 'detail-error') {
    return {
      tone: 'unknown',
      title: 'Cannot taste-test yet',
      detail:
        'This recipe could not be opened, so there is nothing to preflight. Open it in Recipes to check that it saved correctly.',
    };
  }
  if (phase === 'error') {
    return {
      tone: 'error',
      title: 'Could not finish the taste test',
      detail:
        'PAX Cookbook could not complete the preflight check. Make sure PAX Cookbook is running, then try again.',
    };
  }
  if (phase !== 'done' || !readiness) {
    return { tone: 'unknown', title: 'Not tasted yet', detail: '' };
  }
  if (readiness.status === 'ready') {
    return {
      tone: 'ready',
      title: 'Ready to bake',
      detail:
        readiness.summary ||
        'Everything this recipe needs looks ready. Nothing ran during this check.',
    };
  }
  return {
    tone: 'attention',
    title: 'Needs attention before baking',
    detail:
      readiness.summary ||
      'A few things still need attention before this recipe is ready to bake.',
  };
}

/** Build the preflight checklist rows from the readiness body. */
function checklistFor(readiness: RecipeReadinessBody): ChecklistItem[] {
  const items: ChecklistItem[] = [];

  // 1. Recipe content — can the broker even project a command from it?
  items.push({
    id: 'content',
    label: 'Recipe details',
    state: readiness.canPreview ? 'ready' : 'attention',
    detail: readiness.canPreview
      ? 'The recipe is filled in enough to preview its command.'
      : 'Some recipe details are still missing. Open the recipe to finish filling it in.',
  });

  // 2. PAX engine.
  if (readiness.engine) {
    items.push({
      id: 'engine',
      label: 'PAX engine',
      state: readiness.engine.isAcquired ? 'ready' : 'attention',
      detail: readiness.engine.isAcquired
        ? `The PAX engine is ready (${readiness.engine.state}).`
        : `The PAX engine is not ready yet (${readiness.engine.state}).`,
    });
  } else {
    items.push({
      id: 'engine',
      label: 'PAX engine',
      state: 'unknown',
      detail: NOT_REPORTED,
    });
  }

  // 3. Sign-in / Chef's Key context.
  if (readiness.auth) {
    items.push({
      id: 'auth',
      label: 'Sign-in & Chef\u2019s Keys',
      state: readiness.auth.ready ? 'ready' : 'attention',
      detail: readiness.auth.detail || (readiness.auth.ready
        ? `Sign-in is ready (${readiness.auth.mode}).`
        : `Sign-in still needs setup (${readiness.auth.mode}).`),
    });
  } else {
    items.push({
      id: 'auth',
      label: 'Sign-in & Chef\u2019s Keys',
      state: 'unknown',
      detail: NOT_REPORTED,
    });
  }

  // 4. Output / destination.
  if (readiness.destination) {
    items.push({
      id: 'destination',
      label: 'Output destination',
      state: readiness.destination.ready ? 'ready' : 'attention',
      detail: readiness.destination.detail || (readiness.destination.ready
        ? `The output destination is set (${readiness.destination.kind}).`
        : `The output destination still needs attention (${readiness.destination.kind}).`),
    });
  } else {
    items.push({
      id: 'destination',
      label: 'Output destination',
      state: 'unknown',
      detail: NOT_REPORTED,
    });
  }

  // 5. Permissions / remaining requirements.
  const reqs = readiness.requirements ?? [];
  if (reqs.length > 0) {
    const met = reqs.filter(r => r.met).length;
    items.push({
      id: 'requirements',
      label: 'Run requirements',
      state: met === reqs.length ? 'ready' : 'attention',
      detail:
        met === reqs.length
          ? `All ${reqs.length} run requirements are satisfied.`
          : `${met} of ${reqs.length} run requirements are satisfied.`,
    });
  } else {
    items.push({
      id: 'requirements',
      label: 'Run requirements',
      state: 'unknown',
      detail: NOT_REPORTED,
    });
  }

  return items;
}

export function TasteTestsWorkspace() {
  const [listPhase, setListPhase] = useState<ListPhase>('loading');
  const [recipes, setRecipes] = useState<RecipeSummary[]>([]);
  const [search, setSearch] = useState('');

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [selectedSummary, setSelectedSummary] = useState<RecipeSummary | null>(null);
  const [detail, setDetail] = useState<Record<string, unknown> | null>(null);

  const [testPhase, setTestPhase] = useState<TestPhase>('idle');
  const [readiness, setReadiness] = useState<RecipeReadinessBody | null>(null);

  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const loadRecipes = useCallback(async () => {
    setListPhase('loading');
    const res = await listRecipes();
    if (!mounted.current) {
      return;
    }
    if (res.ok && res.data) {
      setRecipes(res.data.recipes ?? []);
      setListPhase('loaded');
    } else {
      setRecipes([]);
      setListPhase('error');
    }
  }, []);

  useEffect(() => {
    void loadRecipes();
  }, [loadRecipes]);

  const selectRecipe = useCallback(async (summary: RecipeSummary) => {
    setSelectedId(summary.recipeId);
    setSelectedSummary(summary);
    setDetail(null);
    setReadiness(null);
    setTestPhase('detail-loading');
    const res = await getRecipe(summary.recipeId);
    if (!mounted.current) {
      return;
    }
    if (res.ok && res.data) {
      setDetail(res.data.recipe ?? null);
      setTestPhase('idle');
    } else {
      setDetail(null);
      setTestPhase('detail-error');
    }
  }, []);

  const runTasteTest = useCallback(async () => {
    if (!detail) {
      return;
    }
    setTestPhase('running');
    setReadiness(null);
    const res = await getRecipeReadiness(detail);
    if (!mounted.current) {
      return;
    }
    if (res.ok && res.data) {
      setReadiness(res.data);
      setTestPhase('done');
    } else {
      setReadiness(null);
      setTestPhase('error');
    }
  }, [detail]);

  const editRecipe = useCallback(() => {
    if (selectedId) {
      rememberPendingSelect(selectedId);
    }
    requestShellSection('recipes');
  }, [selectedId]);

  const openChefsKeys = useCallback(() => requestShellSection('chefskeys'), []);
  const openSettings = useCallback(() => requestShellSection('settings'), []);
  const openRecipes = useCallback(() => requestShellSection('recipes'), []);

  const visible = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) {
      return recipes;
    }
    return recipes.filter(r => recipeName(r).toLowerCase().includes(q));
  }, [recipes, search]);

  const checklist = useMemo(
    () => (testPhase === 'done' && readiness ? checklistFor(readiness) : []),
    [testPhase, readiness],
  );
  const outcome = outcomeFor(testPhase, readiness);
  const canRunTest = selectedId !== null && testPhase !== 'detail-loading' && detail !== null;

  return (
    <div className="dvw dvw-tastetests">
      <SectionHeader
        headingLevel="h1"
        title="Taste Tests"
        helpTopic="cookbookTasteTests"
        accent="var(--c-green)"
        lede="Preflight a saved recipe before you bake it. A taste test checks that everything is ready — it never runs PAX and never bakes."
      />

      <div className="dvw-tastetests__cols">
        {/* Left: saved recipe picker */}
        <section className="dvw-card tt-pane" aria-label="Saved recipes">
          <header className="tt-pane__head">
            <h2 className="tt-pane__title">Saved recipes</h2>
            <button
              type="button"
              className="dvw-btn dvw-btn--icon"
              onClick={() => void loadRecipes()}
              aria-label="Refresh saved recipes"
            >
              <IconRefresh />
            </button>
          </header>

          <div className="tt-search">
            <IconSearch className="tt-search__icon" />
            <input
              type="search"
              className="tt-search__input"
              placeholder="Search recipes"
              value={search}
              onChange={e => setSearch(e.target.value)}
              aria-label="Search saved recipes"
            />
          </div>

          {listPhase === 'loading' ? (
            <p className="tt-muted" role="status">
              Loading your saved recipes…
            </p>
          ) : null}

          {listPhase === 'error' ? (
            <div className="tt-muted" role="alert">
              <p>Could not reach PAX Cookbook. Make sure it is running, then try again.</p>
              <button type="button" className="dvw-btn" onClick={() => void loadRecipes()}>
                <IconRefresh className="dvw-btn__icon" />
                <span>Try again</span>
              </button>
            </div>
          ) : null}

          {listPhase === 'loaded' && recipes.length === 0 ? (
            <div className="tt-muted" role="status">
              <p>No saved recipes yet. Create one in Recipes, then come back to taste-test it.</p>
              <button type="button" className="dvw-btn" onClick={openRecipes}>
                <IconBook className="dvw-btn__icon" />
                <span>Open Recipes</span>
              </button>
            </div>
          ) : null}

          {listPhase === 'loaded' && recipes.length > 0 && visible.length === 0 ? (
            <p className="tt-muted" role="status">
              No recipes match “{search.trim()}”.
            </p>
          ) : null}

          {visible.length > 0 ? (
            <ul className="tt-list" role="listbox" aria-label="Saved recipes">
              {visible.map(recipe => {
                const selected = recipe.recipeId === selectedId;
                const tone = toneForRecipeStatus(recipe['status']);
                const modified = formatModified(recipe['updatedAt']);
                return (
                  <li key={recipe.recipeId} className="tt-list__item">
                    <button
                      type="button"
                      role="option"
                      aria-selected={selected}
                      className={'tt-list__row' + (selected ? ' tt-list__row--selected' : '')}
                      onClick={() => void selectRecipe(recipe)}
                    >
                      <span className="tt-list__icon" aria-hidden="true">
                        <IconBook />
                      </span>
                      <span className="tt-list__text">
                        <span className="tt-list__name">{recipeName(recipe)}</span>
                        <span className="tt-list__meta">
                          {modified ? `Modified ${modified}` : 'Saved recipe'}
                        </span>
                      </span>
                      <RecipeStatusBadge tone={tone} />
                    </button>
                  </li>
                );
              })}
            </ul>
          ) : null}
        </section>

        {/* Center: selected recipe + taste-test result */}
        <section className="dvw-card tt-pane tt-pane--main" aria-label="Taste test">
          {!selectedSummary ? (
            <div className="tt-empty">
              <IconShieldCheck className="tt-empty__icon" />
              <h2 className="tt-empty__title">Pick a recipe to taste-test</h2>
              <p className="tt-empty__body">
                Choose a saved recipe on the left, then run a taste test. A taste test is a safe
                preflight: it checks the recipe and its surroundings without running PAX and without
                baking anything.
              </p>
            </div>
          ) : (
            <>
              <header className="tt-result__head">
                <div className="tt-result__heading">
                  <h2 className="tt-result__title">{recipeName(selectedSummary)}</h2>
                  <RecipeStatusBadge tone={toneForRecipeStatus(selectedSummary['status'])} />
                </div>
                <button
                  type="button"
                  className="dvw-btn dvw-btn--primary"
                  onClick={() => void runTasteTest()}
                  disabled={!canRunTest || testPhase === 'running'}
                >
                  <IconShieldCheck className="dvw-btn__icon" />
                  <span>{testPhase === 'running' ? 'Tasting…' : 'Run taste test'}</span>
                </button>
              </header>

              <p className="tt-result__note">
                A taste test never runs PAX and never bakes. Nothing runs during this check.
              </p>

              {testPhase === 'detail-loading' ? (
                <p className="tt-muted" role="status">
                  Opening the recipe…
                </p>
              ) : null}

              {testPhase === 'idle' && detail ? (
                <p className="tt-muted" role="status">
                  Ready when you are. Select “Run taste test” to preflight this recipe.
                </p>
              ) : null}

              {testPhase === 'running' ? (
                <p className="tt-muted" role="status">
                  Checking this recipe… nothing is running.
                </p>
              ) : null}

              {testPhase === 'done' || testPhase === 'error' || testPhase === 'detail-error' ? (
                <div className={`tt-verdict tt-verdict--${outcome.tone}`} role="status">
                  <span className="tt-verdict__icon" aria-hidden="true">
                    {outcome.tone === 'ready' ? (
                      <IconCheckCircle />
                    ) : outcome.tone === 'attention' ? (
                      <IconAlertCircle />
                    ) : (
                      <IconInfoCircle />
                    )}
                  </span>
                  <span className="tt-verdict__text">
                    <span className="tt-verdict__title">{outcome.title}</span>
                    {outcome.detail ? (
                      <span className="tt-verdict__detail">{outcome.detail}</span>
                    ) : null}
                  </span>
                </div>
              ) : null}

              {testPhase === 'done' && readiness ? (
                <div className="tt-checklist">
                  <h3 className="tt-checklist__title">Preflight checklist</h3>
                  <ul className="tt-checklist__rows">
                    {checklist.map(item => (
                      <li key={item.id} className={`tt-check tt-check--${item.state}`}>
                        <span className="tt-check__icon" aria-hidden="true">
                          {item.state === 'ready' ? (
                            <IconCheckCircle />
                          ) : item.state === 'attention' ? (
                            <IconAlertCircle />
                          ) : (
                            <IconInfoCircle />
                          )}
                        </span>
                        <span className="tt-check__text">
                          <span className="tt-check__label">{item.label}</span>
                          <span className="tt-check__detail">{item.detail}</span>
                        </span>
                        <span className="tt-check__state">
                          {item.state === 'ready'
                            ? 'Ready'
                            : item.state === 'attention'
                              ? 'Needs attention'
                              : 'Not reported'}
                        </span>
                      </li>
                    ))}
                  </ul>

                  {readiness.needsPrep.length > 0 ? (
                    <div className="tt-notes">
                      <h4 className="tt-notes__title">Still needed before baking</h4>
                      <ul className="tt-notes__list">
                        {readiness.needsPrep.map((n, i) => (
                          <li key={`prep-${i}`}>{n}</li>
                        ))}
                      </ul>
                    </div>
                  ) : null}

                  {readiness.warnings.length > 0 ? (
                    <div className="tt-notes">
                      <h4 className="tt-notes__title">Worth a look</h4>
                      <ul className="tt-notes__list">
                        {readiness.warnings.map((w, i) => (
                          <li key={`warn-${i}`}>{w}</li>
                        ))}
                      </ul>
                    </div>
                  ) : null}
                </div>
              ) : null}
            </>
          )}
        </section>

        {/* Right: what to do next */}
        <section className="dvw-card tt-pane tt-pane--next" aria-label="Next steps">
          <h2 className="tt-pane__title">Next steps</h2>
          <p className="tt-muted">
            Taste tests are a safety check. When something needs attention, fix it in the right
            place, then taste-test again.
          </p>
          <div className="tt-actions">
            <button
              type="button"
              className="dvw-btn"
              onClick={editRecipe}
              disabled={!selectedId}
            >
              <IconPencil className="dvw-btn__icon" />
              <span>Edit recipe</span>
            </button>
            <button type="button" className="dvw-btn" onClick={openChefsKeys}>
              <IconKey className="dvw-btn__icon" />
              <span>Check Chef’s Keys</span>
            </button>
            <button type="button" className="dvw-btn" onClick={openSettings}>
              <IconTarget className="dvw-btn__icon" />
              <span>Open Settings</span>
            </button>
            <button type="button" className="dvw-btn" onClick={openRecipes}>
              <IconBook className="dvw-btn__icon" />
              <span>Open Recipes</span>
            </button>
          </div>
        </section>
      </div>
    </div>
  );
}
