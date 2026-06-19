/**
 * "Recipe needs a few more details" dialog.
 *
 * Shown when the user presses Save / Update on a recipe that is not yet
 * complete. It replaces the old silent no-op: instead of a dead, disabled
 * button, every click now explains — in plain language — exactly which fields
 * are still missing and which step of the builder each one lives on, with a
 * jump straight to that step.
 *
 * Boundaries:
 *   - Presentation only. It saves nothing, runs no PAX, opens no broker route,
 *     and writes no file. "Go to step" calls the parent's onGoToStep (which
 *     changes the active builder step) and then closes; "Got it" just closes.
 *   - It is intentionally NOT dismissable by backdrop click or Escape — the
 *     user acknowledges it with a button so the missing-details message is not
 *     skipped past. Every path out (Got it / any Go to step) closes it, so the
 *     user is never trapped.
 */
import { useId } from 'react';

import type { SaveRequirement } from '../lib/recipeSaveRequirements';
import { describeRequirementStep } from '../lib/saveRequirementSteps';

interface SaveRequirementsModalProps {
  /** The outstanding Bucket-A content gaps. Empty means a structural issue. */
  requirements: readonly SaveRequirement[];
  /** Jump to a builder step (1-based). The parent also closes the dialog. */
  onGoToStep: (step: number) => void;
  /** Dismiss without navigating. */
  onClose: () => void;
}

export function SaveRequirementsModal({
  requirements,
  onGoToStep,
  onClose,
}: SaveRequirementsModalProps) {
  const headingId = useId();
  const helpId = useId();

  const items = requirements.map(req => ({
    id: req.id,
    ...describeRequirementStep(req),
  }));

  return (
    <div className="mk-modal__backdrop" role="presentation">
      <div
        className="mk-modal mk-savereq-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        aria-describedby={helpId}
      >
        <header className="mk-modal__head">
          <h2 id={headingId} className="mk-modal__title">
            Recipe needs a few more details
          </h2>
          <p id={helpId} className="mk-modal__subtitle">
            {items.length > 0
              ? 'Add the details below and the recipe is ready to save.'
              : 'This recipe has a configuration issue. Please review all steps and try again.'}
          </p>
        </header>

        {items.length > 0 ? (
          <ul className="mk-savereq-list">
            {items.map(item => (
              <li key={item.id} className="mk-savereq-item">
                <div className="mk-savereq-item__text">
                  <span className="mk-savereq-item__label">{item.label}</span>
                  <span className="mk-savereq-item__step">
                    Step {item.step}: {item.stepName}
                  </span>
                </div>
                <button
                  type="button"
                  className="mk-modal__button mk-savereq-item__go"
                  onClick={() => onGoToStep(item.step)}
                >
                  Go to step
                </button>
              </li>
            ))}
          </ul>
        ) : null}

        <div className="mk-modal__actions">
          <button
            type="button"
            className="mk-modal__button mk-modal__button--primary"
            onClick={onClose}
          >
            Got it
          </button>
        </div>
      </div>
    </div>
  );
}
