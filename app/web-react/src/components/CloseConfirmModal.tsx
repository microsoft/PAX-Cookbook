/**
 * Shared close confirmation modal for the React product shell.
 *
 * Opened when the native shell posts `cookbook:host-close-request` (title-bar
 * X, taskbar Close, Alt+F4) or when an in-app control asks to close. Offers
 * three choices:
 *   - Minimize to tray — hide the window but keep the background broker daemon
 *                        running, so scheduled bakes and any active bake keep
 *                        going (the system-tray icon stays).
 *   - Exit            — close the window AND stop the background broker; any
 *                        running bake stops, and scheduled bakes will not fire
 *                        again until the broker next starts.
 *   - Cancel          — dismiss; nothing changes.
 *
 * When a bake is currently running the modal shows a caution and steers the
 * operator toward Minimize so a long-running bake is not interrupted. The modal
 * only reports the operator's choice through the callbacks; the caller relays
 * the decision to the native shell. Escape and the backdrop both map to the
 * safe Cancel action.
 */

import { useEffect, useRef, type ReactElement } from 'react';

interface CloseConfirmModalProps {
  open: boolean;
  onCancel: () => void;
  onMinimize: () => void;
  onCloseApp: () => void;
  /** When true, a bake is currently running; show a caution and steer to Minimize. */
  bakeRunning?: boolean;
}

export function CloseConfirmModal({
  open,
  onCancel,
  onMinimize,
  onCloseApp,
  bakeRunning = false,
}: CloseConfirmModalProps): ReactElement | null {
  const cancelRef = useRef<HTMLButtonElement | null>(null);

  useEffect(() => {
    if (!open) {
      return;
    }
    // Focus the safest action when the modal opens.
    try {
      cancelRef.current?.focus();
    } catch {
      // Non-fatal: focus is a best-effort convenience.
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' || event.key === 'Esc') {
        event.preventDefault();
        onCancel();
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [open, onCancel]);

  if (!open) {
    return null;
  }

  return (
    <div className="close-modal" role="presentation">
      <div
        className="close-modal__scrim"
        role="presentation"
        onClick={onCancel}
      />
      <div
        className="close-modal__dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="close-modal-title"
        aria-describedby="close-modal-body"
      >
        <h2 id="close-modal-title" className="close-modal__title">
          Close PAX Cookbook
        </h2>
        {bakeRunning ? (
          <p className="close-modal__warning" role="alert">
            <span aria-hidden="true">{'\u26A0'} </span>A bake is currently running.
            Exiting will cancel it.
          </p>
        ) : null}
        <p id="close-modal-body" className="close-modal__body">
          You can minimize to the system tray to keep the background broker
          running — scheduled bakes and any active bake will continue. Or you
          can exit completely, which stops the broker and any running bake.
        </p>
        <div className="close-modal__actions">
          <button
            type="button"
            className="close-modal__btn"
            onClick={onMinimize}
          >
            Minimize to tray
          </button>
          <button
            type="button"
            className="close-modal__btn close-modal__btn--danger"
            onClick={onCloseApp}
          >
            {bakeRunning ? 'Exit anyway' : 'Exit'}
          </button>
          <button
            type="button"
            ref={cancelRef}
            className="close-modal__btn"
            onClick={onCancel}
          >
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
