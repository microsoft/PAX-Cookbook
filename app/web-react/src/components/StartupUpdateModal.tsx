import type { ReactElement } from 'react';

interface StartupUpdateModalProps {
  open: boolean;
  /** Go to the Updates page (full version comparison + Update now). */
  onViewUpdates: () => void;
  /** Dismiss until the next app restart and go Home. */
  onNotNow: () => void;
}

/**
 * Startup "updates available" alert.
 *
 * Shown once on startup, AFTER Windows Hello unlock, when the auto-check found
 * one or more updates. Deliberately GENERIC — it never names the app or the
 * engine; the full breakdown (components, versions, fingerprints) lives on the
 * Updates page. The backdrop is heavily blurred so nothing behind it is
 * readable.
 */
export function StartupUpdateModal({
  open,
  onViewUpdates,
  onNotNow,
}: StartupUpdateModalProps): ReactElement | null {
  if (!open) {
    return null;
  }
  return (
    <div
      className="startup-update"
      role="dialog"
      aria-modal="true"
      aria-labelledby="startup-update-title"
    >
      <div className="startup-update__scrim" aria-hidden="true" />
      <div className="startup-update__dialog">
        <h2 id="startup-update-title" className="startup-update__title">
          Updates are available
        </h2>
        <p className="startup-update__body">
          One or more PAX Cookbook components have updates ready to install.
        </p>
        <div className="startup-update__actions">
          <button
            type="button"
            className="startup-update__btn"
            onClick={onNotNow}
          >
            Not now
          </button>
          <button
            type="button"
            className="startup-update__btn startup-update__btn--primary"
            onClick={onViewUpdates}
            autoFocus
          >
            View updates
          </button>
        </div>
      </div>
    </div>
  );
}
