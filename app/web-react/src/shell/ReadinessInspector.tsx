/**
 * Right-column readiness inspector for the Recipes workspace.
 *
 * The readiness card runs the broker's read-only readiness projection (it never
 * runs PAX) and lists each requirement in plain language. The Advanced details
 * fly-in panel (command preview, permissions, and support details) is opened
 * from the detail pane's action row, not from here.
 */
import type { RecipeReadinessBody } from '../host/brokerBridge';
import {
  ChefHatAccent,
  IconShieldCheck,
  IconRefresh,
  IconCheckCircle,
  IconAlertCircle,
} from './CookbookIllustrations';

export type ReadinessPhase = 'idle' | 'loading' | 'loaded' | 'error';

/** Last recorded bake for the selected recipe (read-only cook history). */
export interface DiagnosticsLastBake {
  status: string;
  when: string | null;
  exitCode: number | null;
}

/**
 * Read-only support diagnostics for the selected recipe. Sourced from the
 * recipe id, the cook-history GET routes, and the runtime-version GET (app
 * version) — no secrets, and no new execution or mutation channel. Rows whose
 * data does not resolve are omitted.
 */
export interface RecipeDiagnostics {
  recipeId: string;
  lastBake: DiagnosticsLastBake | null;
  engineVersion: string | null;
  appVersion: string | null;
}

interface ReadinessInspectorProps {
  phase: ReadinessPhase;
  result: RecipeReadinessBody | null;
  errorText: string | null;
  canCheck: boolean;
  onCheck: () => void;
}

export function ReadinessInspector({
  phase,
  result,
  errorText,
  canCheck,
  onCheck,
}: ReadinessInspectorProps) {
  const loading = phase === 'loading';
  const requirements = result?.requirements ?? [];

  return (
    <aside className="dvw-inspector" aria-label="Readiness">
      <section className="dvw-card dvw-readiness" aria-labelledby="dvw-readiness-h">
        <header className="dvw-card__head">
          <h2 id="dvw-readiness-h" className="dvw-card__title">
            Readiness
          </h2>
          <span className="dvw-readiness__hat" aria-hidden="true">
            <ChefHatAccent />
          </span>
        </header>

        {phase === 'idle' ? (
          <p className="dvw-card__muted">
            Run a readiness check to confirm the engine, sign-in, output folder,
            and permissions. Nothing runs.
          </p>
        ) : null}

        {phase === 'error' ? (
          <p className="dvw-readiness__error" role="alert">
            {errorText ?? 'PAX Cookbook could not complete the readiness check.'}
          </p>
        ) : null}

        {phase === 'loaded' && result ? (
          <>
            {result.summary ? (
              <p
                className={
                  'dvw-readiness__verdict dvw-readiness__verdict--' +
                  (result.status === 'ready' ? 'ready' : 'pending')
                }
                role="status"
              >
                {result.summary}
              </p>
            ) : null}
            <ul className="dvw-readiness__rows">
              {requirements.map(req => (
                <li
                  key={req.id}
                  className={
                    'dvw-readiness__row dvw-readiness__row--' +
                    (req.met ? 'met' : 'unmet')
                  }
                >
                  <span className="dvw-readiness__row-icon" aria-hidden="true">
                    {req.met ? <IconCheckCircle /> : <IconAlertCircle />}
                  </span>
                  <span className="dvw-readiness__row-text">
                    <span className="dvw-readiness__row-label">{req.label}</span>
                    <span className="dvw-readiness__row-detail">{req.detail}</span>
                  </span>
                  <span className="dvw-readiness__row-state">
                    {req.met ? 'Ready' : 'Needs attention'}
                  </span>
                </li>
              ))}
            </ul>
          </>
        ) : null}

        <button
          type="button"
          className="dvw-btn dvw-btn--primary dvw-readiness__run"
          onClick={onCheck}
          disabled={!canCheck || loading}
          aria-disabled={!canCheck || loading}
        >
          <IconRefresh className="dvw-btn__icon" />
          <span>{loading ? 'Checking…' : 'Run readiness check'}</span>
        </button>
        {!canCheck ? (
          <p className="dvw-readiness__hint">Select a recipe to check its readiness.</p>
        ) : null}

        <p className="dvw-readiness__note">
          <IconShieldCheck className="dvw-readiness__note-icon" />
          <span>Nothing runs during this check. Baking runs PAX only after you confirm a Bake.</span>
        </p>
      </section>
    </aside>
  );
}
