/**
 * Bake confirmation modal.
 *
 * The deliberate stop between pressing "Bake (run)" and the broker actually
 * recording a cook. It restates — in plain language — exactly what is about to
 * happen: which recipe runs, how it signs in, where its output lands, and the
 * one safe, non-secret command preview the broker already projects. It makes
 * the consequence unmistakable ("This will run PAX on this PC.") and draws the
 * line against the surfaces that never run anything.
 *
 * Boundaries:
 *   - This component starts nothing itself. Confirming calls the parent's
 *     onConfirm, which owns the single startCook bridge call.
 *   - It shows only the broker's already-redacted command projection (or a safe
 *     note when none is available). It never renders a secret, token, or key.
 *   - It cannot double-submit: Confirm is disabled while a start is in flight,
 *     Enter is swallowed during submit, and Cancel/Escape are inert mid-flight.
 */
import { useEffect, useId, type MouseEvent } from 'react';

interface BakeConfirmModalProps {
  /** Display name of the saved recipe about to run. */
  recipeName: string;
  /** One-line sign-in / auth-mode summary (no secrets). */
  authSummary: string;
  /** One-line output-destination summary. */
  destinationSummary: string;
  /** The broker's already-redacted command projection, or null for a safe note. */
  commandSummary: string | null;
  /** True while the startCook call is in flight. */
  submitting: boolean;
  /** A bounded failure message to surface in the modal, or null. */
  error: string | null;
  /** Cancel / Escape / backdrop — nothing starts. Inert while submitting. */
  onCancel: () => void;
  /** Confirm — the parent owns the single startCook call. */
  onConfirm: () => void;
}

export function BakeConfirmModal({
  recipeName,
  authSummary,
  destinationSummary,
  commandSummary,
  submitting,
  error,
  onCancel,
  onConfirm,
}: BakeConfirmModalProps) {
  const headingId = useId();
  const helpId = useId();

  useEffect(() => {
    function onKey(ev: KeyboardEvent) {
      if (ev.key === 'Escape' && !submitting) {
        ev.preventDefault();
        onCancel();
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onCancel, submitting]);

  function handleBackdropClick(ev: MouseEvent<HTMLDivElement>) {
    if (ev.target === ev.currentTarget && !submitting) {
      onCancel();
    }
  }

  function handleConfirm() {
    if (submitting) {
      return;
    }
    onConfirm();
  }

  return (
    <div className="mk-modal__backdrop" role="presentation" onClick={handleBackdropClick}>
      <div
        className="mk-modal mk-bake-confirm"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        aria-describedby={helpId}
      >
        <header className="mk-modal__head">
          <h2 id={headingId} className="mk-modal__title">
            Bake “{recipeName}”?
          </h2>
          <p id={helpId} className="mk-modal__subtitle">
            This will run PAX on this PC. Taste Tests, imports, and previews never
            run PAX — baking does.
          </p>
        </header>

        <dl className="mk-bake-confirm__facts">
          <div className="mk-bake-confirm__fact">
            <dt className="mk-bake-confirm__term">Recipe</dt>
            <dd className="mk-bake-confirm__value">{recipeName}</dd>
          </div>
          <div className="mk-bake-confirm__fact">
            <dt className="mk-bake-confirm__term">Sign-in</dt>
            <dd className="mk-bake-confirm__value">{authSummary}</dd>
          </div>
          <div className="mk-bake-confirm__fact">
            <dt className="mk-bake-confirm__term">Output</dt>
            <dd className="mk-bake-confirm__value">{destinationSummary}</dd>
          </div>
          <div className="mk-bake-confirm__fact">
            <dt className="mk-bake-confirm__term">Command</dt>
            <dd className="mk-bake-confirm__value">
              {commandSummary ? (
                <code className="mk-bake-confirm__command">{commandSummary}</code>
              ) : (
                <span className="mk-bake-confirm__command-note">
                  PAX Cookbook runs the saved recipe’s command. No secrets are
                  included.
                </span>
              )}
            </dd>
          </div>
        </dl>

        <p className="mk-bake-confirm__consequence">
          Baking runs the recipe saved on disk — not your unsaved edits. Follow
          progress on the Bakes page once it starts.
        </p>

        {error ? (
          <p className="mk-modal__error" role="alert">
            {error}
          </p>
        ) : null}

        <div className="mk-modal__actions">
          <button
            type="button"
            className="mk-modal__button"
            onClick={onCancel}
            disabled={submitting}
          >
            Cancel
          </button>
          <button
            type="button"
            className="mk-modal__button mk-modal__button--primary"
            onClick={handleConfirm}
            disabled={submitting}
          >
            {submitting ? 'Starting…' : 'Confirm Bake'}
          </button>
        </div>
      </div>
    </div>
  );
}
