/**
 * Discard-changes confirmation modal.
 *
 * The deliberate stop between pressing "Discard changes" and the builder
 * throwing away the user's unsaved edits. It restates — in plain language —
 * exactly what is about to happen: the recipe reverts to the last saved
 * version and any edits since then are lost. The revert is purely in-memory:
 * nothing on disk is touched, nothing is saved, and nothing is ever run.
 *
 * Boundaries:
 *   - This component reverts nothing itself. Confirming calls the parent's
 *     onConfirm, which owns the in-memory revert to the saved baselines.
 *   - It runs no PAX, opens no broker route, and writes no file. Discard is an
 *     instant, local state reset.
 *   - It is fully dismissable — Cancel, Escape, and a backdrop click all call
 *     onCancel and leave the builder's edits exactly as they were.
 */
import { useEffect, useId, type MouseEvent } from 'react';

interface DiscardConfirmModalProps {
  /** Cancel / Escape / backdrop — nothing reverts. */
  onCancel: () => void;
  /** Confirm — the parent owns the in-memory revert to the saved baselines. */
  onConfirm: () => void;
}

export function DiscardConfirmModal({ onCancel, onConfirm }: DiscardConfirmModalProps) {
  const headingId = useId();
  const helpId = useId();

  useEffect(() => {
    function onKey(ev: KeyboardEvent) {
      if (ev.key === 'Escape') {
        ev.preventDefault();
        onCancel();
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onCancel]);

  function handleBackdropClick(ev: MouseEvent<HTMLDivElement>) {
    if (ev.target === ev.currentTarget) {
      onCancel();
    }
  }

  return (
    <div className="mk-modal__backdrop" role="presentation" onClick={handleBackdropClick}>
      <div
        className="mk-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        aria-describedby={helpId}
      >
        <header className="mk-modal__head">
          <h2 id={headingId} className="mk-modal__title">
            Discard unsaved changes?
          </h2>
          <p id={helpId} className="mk-modal__subtitle">
            This will revert the recipe to the last saved version. Any changes you
            made since then will be lost.
          </p>
        </header>

        <div className="mk-modal__actions">
          <button type="button" className="mk-modal__button" onClick={onCancel}>
            Cancel
          </button>
          <button
            type="button"
            className="mk-modal__button mk-modal__button--danger"
            onClick={onConfirm}
          >
            Discard
          </button>
        </div>
      </div>
    </div>
  );
}
