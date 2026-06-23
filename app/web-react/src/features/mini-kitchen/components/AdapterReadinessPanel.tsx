import type { RecipeReadinessBody } from '../../../host/brokerBridge';

interface AdapterReadinessPanelProps {
  /** Request lifecycle for the readiness check. */
  phase: 'idle' | 'loading' | 'loaded' | 'error';
  /** The readiness envelope the broker returned (any status), or null. */
  result: RecipeReadinessBody | null;
  /** Friendly message when the request itself failed (network / 401 / 423). */
  errorText: string | null;
  /** Whether the recipe candidate is complete enough to check. */
  canCheck: boolean;
  /** Run the read-only readiness check against the local app. */
  onCheck: () => void;
  /**
   * Whether to render the panel's own "Check readiness" trigger button. The
   * builder relocates the trigger to the bottom action bar and passes false so
   * the panel shows only the heading, intro, and result.
   */
  showTrigger?: boolean;
}

/**
 * Adapter-backed readiness panel.
 *
 * This is the authoritative, non-executing readiness view for the Mini-Kitchen
 * builder. Unlike the browser command preview above (which is composed locally
 * for teaching), this panel asks the local PAX Cookbook app to validate the
 * recipe, project the real command, and report what is still missing before a
 * run would be possible — PAX engine installed, sign-in / Chef's Key, and an
 * output destination. It never runs PAX, never starts a bake, and never reads a
 * credential; baking runs PAX only from the builder's gated Bake action.
 *
 * Everything here is plain English. The raw command, arguments, and any
 * diagnostic detail live in a collapsed "support details" disclosure so the
 * primary view never shows a raw HTTP error or a wall of JSON.
 */
export function AdapterReadinessPanel({
  phase,
  result,
  errorText,
  canCheck,
  onCheck,
  showTrigger = true,
}: AdapterReadinessPanelProps) {
  const loading = phase === 'loading';
  const buttonLabel = loading
    ? 'Checking…'
    : phase === 'loaded' || phase === 'error'
      ? 'Run Check Readiness again'
      : 'Run Check Readiness';

  return (
    <section
      className="mk-readiness"
      aria-labelledby="mk-readiness-heading"
    >
      <header className="mk-readiness__head">
        <h2 id="mk-readiness-heading" className="mk-readiness__title">
          Check Readiness
        </h2>
        <p className="mk-readiness__intro">
          Check Readiness verifies that your recipe is complete and ready to
          run. It confirms your PAX engine is available, your authentication is
          configured, your output destination is accessible, and all required
          recipe fields are filled in. Run it before your first bake to catch
          any issues. Nothing runs during this check.
        </p>
      </header>

      {showTrigger ? (
        <div className="mk-readiness__actions" role="group">
          <button
            type="button"
            className="mk-readiness__btn mk-readiness__btn--primary"
            onClick={onCheck}
            disabled={!canCheck || loading}
            aria-disabled={!canCheck || loading}
          >
            {buttonLabel}
          </button>
          {!canCheck ? (
            <span className="mk-readiness__hint">
              Finish the highlighted required details above first.
            </span>
          ) : null}
        </div>
      ) : null}

      {phase === 'error' && errorText ? (
        <p className="mk-readiness__message mk-readiness__message--error" role="alert">
          {errorText}
        </p>
      ) : null}

      {phase === 'loaded' && result ? (
        <ReadinessResult result={result} />
      ) : null}
    </section>
  );
}

function ReadinessResult({ result }: { result: RecipeReadinessBody }) {
  const tone = result.status === 'ready' ? 'ready' : 'pending';
  return (
    <div className="mk-readiness__result">
      <p
        className={'mk-readiness__verdict mk-readiness__verdict--' + tone}
        role="status"
      >
        {result.summary}
      </p>

      {result.requirements.length > 0 ? (
        <ul className="mk-readiness__reqs" aria-label="Readiness requirements">
          {result.requirements.map(req => (
            <li
              key={req.id}
              className={
                req.met
                  ? 'mk-readiness__req mk-readiness__req--met'
                  : 'mk-readiness__req mk-readiness__req--unmet'
              }
            >
              <span className="mk-readiness__req-mark" aria-hidden="true">
                {req.met ? '✓' : '•'}
              </span>
              <span className="mk-readiness__req-text">
                <span className="mk-readiness__req-label">{req.label}</span>
                <span className="mk-readiness__req-detail">{req.detail}</span>
              </span>
            </li>
          ))}
        </ul>
      ) : null}

      {result.errors.length > 0 ? (
        <ul className="mk-readiness__notes" aria-label="What still needs setup">
          {result.errors.map((note, index) => (
            <li key={'e' + index} className="mk-readiness__note">
              {note}
            </li>
          ))}
        </ul>
      ) : null}

      {result.warnings.length > 0 ? (
        <ul className="mk-readiness__notes" aria-label="Readiness notes">
          {result.warnings.map((note, index) => (
            <li key={'w' + index} className="mk-readiness__note">
              {note}
            </li>
          ))}
        </ul>
      ) : null}

      <p className="mk-readiness__footnote">
        This readiness check does not run PAX. Baking runs PAX only after you
        confirm a Bake.
      </p>
    </div>
  );
}
