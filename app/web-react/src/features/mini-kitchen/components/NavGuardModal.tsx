/**
 * Unsaved-changes navigation guard modal.
 *
 * The deliberate stop shown when the operator tries to navigate away from the
 * recipe builder (the left nav-rail, a "Create a Chef's Key" link, or any other
 * in-app destination) while the builder has unsaved edits. It restates plainly
 * what is at stake and offers three exits:
 *   - Save and leave — save the recipe first, then continue to the destination.
 *     If the recipe cannot be saved yet (missing required details), the parent
 *     surfaces the save-requirements dialog and the navigation is cancelled.
 *   - Leave without saving — continue to the destination, discarding the edits.
 *   - Stay here — close the dialog and remain exactly where the operator was.
 *
 * Boundaries:
 *   - This component navigates nothing and saves nothing itself; it only relays
 *     the operator's choice to the parent, which owns the save flow and the
 *     actual section switch.
 *   - It runs no PAX, opens no broker route, and writes no file.
 *   - It is fully dismissable — Stay here, Escape, and a backdrop click all keep
 *     the builder and its edits exactly as they were.
 */
import { useEffect, useId, type MouseEvent } from 'react';

interface NavGuardModalProps {
  /** True while a save triggered by "Save and leave" is in flight. */
  saving: boolean;
  /** Save the recipe, then leave on success (parent decides). */
  onSaveAndLeave: () => void;
  /** Leave immediately, discarding unsaved edits. */
  onLeave: () => void;
  /** Stay — close the dialog and keep every unsaved edit in place. */
  onStay: () => void;
}

export function NavGuardModal({ saving, onSaveAndLeave, onLeave, onStay }: NavGuardModalProps) {
  const headingId = useId();
  const helpId = useId();

  useEffect(() => {
    function onKey(ev: KeyboardEvent) {
      if (ev.key === 'Escape' && !saving) {
        ev.preventDefault();
        onStay();
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onStay, saving]);

  function handleBackdropClick(ev: MouseEvent<HTMLDivElement>) {
    if (ev.target === ev.currentTarget && !saving) {
      onStay();
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
            You have unsaved changes
          </h2>
          <p id={helpId} className="mk-modal__subtitle">
            Your recipe changes haven&rsquo;t been saved yet. If you leave now, your
            changes will be lost.
          </p>
        </header>

        <div className="mk-modal__actions mk-modal__actions--wrap">
          <button
            type="button"
            className="mk-modal__button"
            onClick={onStay}
            disabled={saving}
          >
            Stay here
          </button>
          <button
            type="button"
            className="mk-modal__button mk-modal__button--danger"
            onClick={onLeave}
            disabled={saving}
          >
            Leave without saving
          </button>
          <button
            type="button"
            className="mk-modal__button mk-modal__button--primary"
            onClick={onSaveAndLeave}
            disabled={saving}
          >
            {saving ? 'Saving…' : 'Save and leave'}
          </button>
        </div>
      </div>
    </div>
  );
}
