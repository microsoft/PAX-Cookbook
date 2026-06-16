/**
 * Resume-from-checkpoint modal.
 *
 * The deliberate stop between choosing to recover an interrupted run and the
 * broker actually recording a resume cook. It collects only what the broker's
 * resume contract needs — the checkpoint folder (or .json) an interrupted run
 * left behind, whether to use the most recent checkpoint without prompting, and
 * an optional Chef's Key to sign in with — and shows a faithful, copy-pasteable
 * preview of the PAX -Resume command the broker will run.
 *
 * Boundaries:
 *   - This component starts nothing itself. Confirming calls the parent's
 *     onConfirm, which owns the single resumeCook bridge call and the Windows
 *     Hello step-up that precedes it.
 *   - It collects NO script path and NO secret. The command preview is a
 *     faithful, copy-pasteable equivalent of the PAX -Resume command: the engine
 *     is the managed engine path (non-secret — it already appears on Bakes output
 *     rows) and any chosen Chef's Key is named beneath the command, never inlined
 *     as a secret. It never renders a client id, certificate thumbprint, token,
 *     or secret, and it is not the broker's literal internal spawn array.
 *   - It cannot double-submit: Resume is disabled while a start is in flight and
 *     until a checkpoint path is entered, and Cancel/Escape/backdrop are inert
 *     mid-flight.
 */
import { useEffect, useId, useRef, useState, type MouseEvent } from 'react';
import { BrowsePathButton } from './BrowsePathButton';

interface ResumeCheckpointModalProps {
  /** Saved Chef's Keys offered as an optional sign-in override (id + name only). */
  chefKeys: readonly { id: string; displayName: string }[];
  /** True while the resumeCook call (and its at-most-once Hello retry) is in flight. */
  submitting: boolean;
  /** A bounded failure message to surface in the modal, or null. */
  error: string | null;
  /** Non-secret managed engine path for the copy-pasteable command preview, or null. */
  enginePath: string | null;
  /** Cancel / Escape / backdrop — nothing starts. Inert while submitting. */
  onCancel: () => void;
  /** Confirm — the parent owns the single resumeCook call. */
  onConfirm: (input: {
    checkpointPath: string;
    force: boolean;
    chefKeyId: string | null;
  }) => void;
  /**
   * Leave for Chef's Keys — offered behind an in-modal confirmation. The parent
   * closes this modal and navigates; this component only asks. Inert while a
   * resume is in flight.
   */
  onNavigate: () => void;
}

export function ResumeCheckpointModal({
  chefKeys,
  submitting,
  error,
  enginePath,
  onCancel,
  onConfirm,
  onNavigate,
}: ResumeCheckpointModalProps) {
  const headingId = useId();
  const helpId = useId();
  const pathId = useId();
  const keyId = useId();

  const [checkpointPath, setCheckpointPath] = useState('');
  const [force, setForce] = useState(false);
  const [chefKeyId, setChefKeyId] = useState('');
  // An in-modal step that confirms leaving for Chef's Keys, shown in place of
  // the resume form so the dialog never stacks a second backdrop.
  const [confirmNav, setConfirmNav] = useState(false);
  const confirmNavButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    function onKey(ev: KeyboardEvent) {
      if (ev.key === 'Escape' && !submitting) {
        ev.preventDefault();
        if (confirmNav) {
          // Escape backs out of the in-modal confirm to the form, rather than
          // closing the whole dialog.
          setConfirmNav(false);
        } else {
          onCancel();
        }
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onCancel, submitting, confirmNav]);

  // Move focus to the confirm's primary action when the leave-for-keys step
  // appears, so the keyboard lands somewhere sensible.
  useEffect(() => {
    if (confirmNav) {
      confirmNavButtonRef.current?.focus();
    }
  }, [confirmNav]);

  function handleBackdropClick(ev: MouseEvent<HTMLDivElement>) {
    if (ev.target === ev.currentTarget && !submitting) {
      onCancel();
    }
  }

  const trimmedPath = checkpointPath.trim();
  const canSubmit = trimmedPath.length > 0 && !submitting;

  function handleConfirm() {
    if (!canSubmit) {
      return;
    }
    onConfirm({
      checkpointPath: trimmedPath,
      force,
      chefKeyId: chefKeyId.length > 0 ? chefKeyId : null,
    });
  }

  // Faithful, copy-pasteable, secret-free preview of the PAX -Resume command the
  // broker will run. A .json target resumes that exact checkpoint file; any other
  // path is treated as the output folder to auto-discover the most recent
  // checkpoint in. The engine is the managed engine path (non-secret); a readable
  // token stands in until it loads. Any chosen Chef's Key is named beneath the
  // command, never inlined as a secret — this is an honest preview of the command,
  // not the broker's literal internal spawn array.
  const isJsonTarget = trimmedPath.toLowerCase().endsWith('.json');
  const previewTarget =
    trimmedPath.length > 0 ? trimmedPath : '<output folder or checkpoint .json>';
  const previewCore = isJsonTarget
    ? '-Resume "' + previewTarget + '"'
    : '-Resume -OutputPath "' + previewTarget + '"';
  const engineToken =
    enginePath && enginePath.length > 0 ? enginePath : '<managed engine path>';
  const commandPreview =
    'pwsh -NoProfile -ExecutionPolicy Bypass -File "' +
    engineToken +
    '" ' +
    previewCore +
    (force ? ' -Force' : '');
  const selectedKey =
    chefKeyId.length > 0 ? chefKeys.find(k => k.id === chefKeyId) ?? null : null;
  const signInLabel = selectedKey ? 'Signs in with ' + selectedKey.displayName : '';

  return (
    <div className="mk-modal__backdrop" role="presentation" onClick={handleBackdropClick}>
      <div
        className="mk-modal mk-resume-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby={headingId}
        aria-describedby={helpId}
      >
        <header className="mk-modal__head">
          <div className="mk-resume__titlerow">
            <span className="mk-resume__icon" aria-hidden="true">
              <svg
                width="18"
                height="18"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
                strokeLinecap="round"
                strokeLinejoin="round"
              >
                <path d="M3 12a9 9 0 1 0 3-6.7L3 8" />
                <path d="M3 3v5h5" />
              </svg>
            </span>
            <h2 id={headingId} className="mk-modal__title">
              Resume an interrupted run
            </h2>
          </div>
          <p id={helpId} className="mk-modal__subtitle">
            Point PAX Cookbook at the output folder — or the checkpoint .json —
            left by an interrupted run. Cookbook runs PAX -Resume as a real bake
            you can watch on the Bakes page.
          </p>
        </header>

        {confirmNav ? (
          <div className="mk-resume__confirm">
            <p className="mk-resume__confirm-msg">
              This will close the resume dialog and take you to Chef&rsquo;s
              Keys. You can return here after setting up your keys.
            </p>
            <div className="mk-modal__actions">
              <button
                type="button"
                className="mk-modal__button"
                onClick={() => setConfirmNav(false)}
              >
                Cancel
              </button>
              <button
                ref={confirmNavButtonRef}
                type="button"
                className="mk-modal__button mk-modal__button--primary"
                onClick={onNavigate}
              >
                Go to Chef&rsquo;s Keys
              </button>
            </div>
          </div>
        ) : (
          <>
            <div className="mk-resume__field">
              <div className="mk-modal__form">
                <label className="mk-modal__label" htmlFor={pathId}>
                  Checkpoint folder or .json file
                </label>
                <div className="mk-resume__path-row">
                  <input
                    id={pathId}
                    type="text"
                    className="mk-modal__input"
                    value={checkpointPath}
                    onChange={e => setCheckpointPath(e.target.value)}
                    placeholder={'C:\\PAXOut  or  .pax_checkpoint_*.json path'}
                    autoComplete="off"
                    spellCheck={false}
                    disabled={submitting}
                  />
                  <BrowsePathButton
                    mode="folder"
                    title="Select the interrupted run's output folder"
                    onSelect={path => setCheckpointPath(path)}
                    disabled={submitting}
                  />
                </div>
                <p className="mk-modal__hint">
                  Paste the output folder an interrupted run wrote to, or the exact
                  checkpoint .json inside it.
                </p>
              </div>
            </div>

            <label className="mk-resume__check">
              <input
                type="checkbox"
                checked={force}
                onChange={e => setForce(e.target.checked)}
                disabled={submitting}
              />
              <span>Use the most recent checkpoint without prompting</span>
            </label>

            <div className="mk-modal__form">
              <label className="mk-modal__label" htmlFor={keyId}>
                Sign-in
              </label>
              <select
                id={keyId}
                className="mk-modal__input"
                value={chefKeyId}
                onChange={e => setChefKeyId(e.target.value)}
                disabled={submitting}
              >
                <option value="">Use checkpoint&rsquo;s saved sign-in</option>
                {chefKeys.map(key => (
                  <option key={key.id} value={key.id}>
                    {key.displayName}
                  </option>
                ))}
              </select>
              <button
                type="button"
                className="mk-resume__manage"
                onClick={() => {
                  if (!submitting) {
                    setConfirmNav(true);
                  }
                }}
                disabled={submitting}
              >
                Manage Chef&rsquo;s Keys →
              </button>
            </div>

            <details className="mk-resume__preview">
              <summary className="mk-resume__preview-summary">Command preview</summary>
              <div className="mk-resume__preview-body">
                <code className="mk-resume__command">{commandPreview}</code>
                {signInLabel ? (
                  <span className="mk-resume__signin">{signInLabel}</span>
                ) : null}
              </div>
            </details>

            <p className="mk-resume__note">
              No script path or sign-in secrets needed — PAX Cookbook manages the
              engine, and Chef&rsquo;s Keys handle sign-in.
            </p>

            <p className="mk-resume__note">
              PAX automatically restores the dashboard target (AI-in-One or AI
              Business Value) from the checkpoint.
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
                disabled={!canSubmit}
              >
                {submitting ? 'Resuming…' : 'Resume Bake'}
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
