/**
 * Update-available modal (React product shell).
 *
 * Shown when an update check (the startup auto-check or a manual check from
 * Settings / Updates) finds a newer published build. It lists what will update
 * in plain language and offers two choices:
 *   - Update now   — hands off to the broker, which launches the installer to
 *                    download + apply the update; the app then closes to finish.
 *   - Update later — dismiss for the rest of this session (no nagging until the
 *                    next app restart).
 *
 * The modal applies nothing itself; it only relays the choice. Escape and the
 * backdrop map to "Update later" (unless an apply is already in flight).
 */
import { useEffect, type ReactElement } from 'react';
import type { UpdateComponent } from '../host/updateCheck';

interface UpdateAvailableModalProps {
  open: boolean;
  components: readonly UpdateComponent[];
  applying: boolean;
  error: string | null;
  onUpdateNow: () => void;
  onUpdateLater: () => void;
}

function formatBuild(b: string | null | undefined): string | null {
  if (!b) {
    return null;
  }
  const d = new Date(b);
  if (Number.isNaN(d.getTime())) {
    return b;
  }
  return d.toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  });
}

function describeComponent(c: UpdateComponent): string {
  if (c.newBuildOnly) {
    const from = formatBuild(c.fromBuild);
    const to = formatBuild(c.toBuild);
    if (from && to) {
      return `${c.name} \u2014 new build available (${from} \u2192 ${to})`;
    }
    return `${c.name} \u2014 new build available`;
  }
  if (!c.fromVersion || !c.toVersion) {
    return `${c.name} \u2014 update available`;
  }
  return `${c.name} \u2014 version ${c.fromVersion} \u2192 ${c.toVersion}`;
}

export function UpdateAvailableModal({
  open,
  components,
  applying,
  error,
  onUpdateNow,
  onUpdateLater,
}: UpdateAvailableModalProps): ReactElement | null {
  useEffect(() => {
    if (!open) {
      return;
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if ((event.key === 'Escape' || event.key === 'Esc') && !applying) {
        event.preventDefault();
        onUpdateLater();
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [open, applying, onUpdateLater]);

  if (!open) {
    return null;
  }

  return (
    <div className="close-modal" role="presentation">
      <div
        className="close-modal__scrim"
        role="presentation"
        onClick={applying ? undefined : onUpdateLater}
      />
      <div
        className="close-modal__dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="update-modal-title"
        aria-describedby="update-modal-body"
      >
        <h2 id="update-modal-title" className="close-modal__title">
          Updates available
        </h2>
        <div id="update-modal-body" className="close-modal__body">
          <p>The following updates are ready to install:</p>
          <ul className="dvw-attention__list" style={{ marginTop: 'var(--sp-2)' }}>
            {components.map(c => (
              <li key={c.name} className="dvw-attention__row">
                <span className="dvw-attention__title">{describeComponent(c)}</span>
              </li>
            ))}
          </ul>
          <p style={{ marginTop: 'var(--sp-3)' }}>
            PAX Cookbook will download and apply the update, then close so you can
            reopen it. Your saved recipes and history are not affected.
          </p>
        </div>
        {error ? (
          <p className="close-modal__warning" role="alert">
            {error}
          </p>
        ) : null}
        <div className="close-modal__actions">
          <button
            type="button"
            className="close-modal__btn"
            onClick={onUpdateLater}
            disabled={applying}
          >
            Update later
          </button>
          <button
            type="button"
            className="close-modal__btn close-modal__btn--danger"
            onClick={onUpdateNow}
            disabled={applying}
          >
            {applying ? 'Updating\u2026' : 'Update now'}
          </button>
        </div>
      </div>
    </div>
  );
}
