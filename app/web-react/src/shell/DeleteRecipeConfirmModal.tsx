/**
 * Delete-recipe confirmation modal.
 *
 * The deliberate stop between pressing "Delete recipe" and the recipe being
 * moved to the trash. It restates — in plain language — exactly what is about
 * to happen: the recipe is moved to the trash and removed from the saved
 * recipes, and (only when it is scheduled) its schedule is removed too so it no
 * longer runs automatically. It shows only the recipe NAME — never a tenant,
 * client id, secret, path, or any other recipe field.
 *
 * Boundaries:
 *   - This component deletes nothing itself. Confirming calls the parent's
 *     onConfirm, which owns the broker delete call (and the schedule removal
 *     that must precede it for a scheduled recipe).
 *   - It runs no PAX and starts no cook. Deleting a recipe is a soft delete; it
 *     never executes anything.
 *   - It cannot double-submit: Delete is disabled while a delete is in flight,
 *     and Cancel / Escape / backdrop are inert mid-flight.
 */
import { useEffect, useId, type MouseEvent } from 'react';

interface DeleteRecipeConfirmModalProps {
  /** Display name of the saved recipe about to be deleted. */
  recipeName: string;
  /** True when the recipe has an enabled schedule that will also be removed. */
  scheduled: boolean;
  /** True while the delete (and any schedule removal) is in flight. */
  submitting: boolean;
  /** A bounded failure message to surface in the modal, or null. */
  error: string | null;
  /** Cancel / Escape / backdrop — nothing is deleted. Inert while submitting. */
  onCancel: () => void;
  /** Confirm — the parent owns the broker delete call. */
  onConfirm: () => void;
}

export function DeleteRecipeConfirmModal({
  recipeName,
  scheduled,
  submitting,
  error,
  onCancel,
  onConfirm,
}: DeleteRecipeConfirmModalProps) {
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
        className="mk-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        aria-describedby={helpId}
      >
        <header className="mk-modal__head">
          <h2 id={headingId} className="mk-modal__title">
            Delete this recipe?
          </h2>
          <p id={helpId} className="mk-modal__subtitle">
            Deleting “{recipeName}” moves it to the trash and removes it from your
            saved recipes.
          </p>
          {scheduled ? (
            <p className="mk-modal__subtitle">
              Its schedule will also be removed, so it will no longer run
              automatically.
            </p>
          ) : null}
        </header>

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
            className="mk-modal__button mk-modal__button--danger"
            onClick={handleConfirm}
            disabled={submitting}
          >
            {submitting ? 'Deleting…' : 'Delete recipe'}
          </button>
        </div>
      </div>
    </div>
  );
}
