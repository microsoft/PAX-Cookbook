/**
 * Open-a-different-recipe confirmation modal.
 *
 * The deliberate stop between clicking a saved recipe and the builder
 * replacing the user's current, unsaved work with it. It restates — in plain
 * language — exactly what is about to happen: the builder loads the chosen
 * recipe and any unsaved edits in the current draft are lost. Loading a recipe
 * is purely a read: it fetches the stored recipe and maps it into builder
 * state. Nothing is saved, scheduled, or run.
 *
 * Boundaries:
 *   - This component opens nothing itself. Confirming calls the parent's
 *     onConfirm, which owns the read-only fetch (GET /recipes/{id}) and the
 *     in-memory load into builder state.
 *   - It runs no PAX, opens no broker route, and writes no file.
 *   - It is fully dismissable — Cancel, Escape, and a backdrop click all call
 *     onCancel and leave the builder's current edits exactly as they were.
 */
import { useEffect, useId, type MouseEvent } from 'react';

interface OpenRecipeConfirmModalProps {
  /** Cancel / Escape / backdrop — nothing opens, the current draft stays. */
  onCancel: () => void;
  /** Confirm — the parent owns the read-only fetch and load into the builder. */
  onConfirm: () => void;
}

export function OpenRecipeConfirmModal({ onCancel, onConfirm }: OpenRecipeConfirmModalProps) {
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
            Open a different recipe?
          </h2>
          <p id={helpId} className="mk-modal__subtitle">
            You have unsaved changes that will be lost if you switch recipes.
          </p>
        </header>

        <div className="mk-modal__actions">
          <button type="button" className="mk-modal__button" onClick={onCancel}>
            Cancel
          </button>
          <button
            type="button"
            className="mk-modal__button mk-modal__button--primary"
            onClick={onConfirm}
          >
            Open recipe
          </button>
        </div>
      </div>
    </div>
  );
}
