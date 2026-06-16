/**
 * Browse button — a compact "path picker" unit that sits inline to the right of
 * a path text input and opens the OS-native file/folder dialog through the
 * broker.
 *
 * It is a thin, calm wrapper over the browsePath bridge call: a real
 * <button type="button"> with a small folder/file glyph and a "Browse…" label.
 * On click it asks the broker to open the native picker and, only when the user
 * actually chooses a path, hands that path back through onSelect. It starts
 * nothing, submits nothing, and shows no secret — it returns a path the user
 * explicitly picked and leaves what to do with it entirely to the caller.
 *
 * Because the broker's request stays open while the native dialog is up, the
 * button reflects that in-flight state: it disables itself and swaps the glyph
 * for a spinner until the dialog is dismissed. A non-OK answer (e.g. a 423 lock
 * the global overlay already handles, or a 500) is met calmly — the button just
 * does nothing rather than surfacing a raw error.
 */
import { useState } from 'react';
import { browsePath, type BrowsePathRequest } from '../host/brokerBridge';

interface BrowsePathButtonProps {
  mode: 'file' | 'folder';
  title?: string;
  filters?: Array<{ name: string; extensions: string[] }>;
  initialDirectory?: string;
  onSelect: (path: string) => void;
  disabled?: boolean;
}

function PickerIcon({ mode }: { mode: 'file' | 'folder' }) {
  if (mode === 'folder') {
    return (
      <svg
        width="14"
        height="14"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        <path d="M3 7a2 2 0 0 1 2-2h3.5l2 2H19a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
      </svg>
    );
  }
  return (
    <svg
      width="14"
      height="14"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z" />
      <path d="M14 3v5h5" />
    </svg>
  );
}

export function BrowsePathButton({
  mode,
  title,
  filters,
  initialDirectory,
  onSelect,
  disabled,
}: BrowsePathButtonProps) {
  const [busy, setBusy] = useState(false);

  async function handleClick() {
    if (busy || disabled) {
      return;
    }
    setBusy(true);
    try {
      const request: BrowsePathRequest = { mode, title, filters, initialDirectory };
      const result = await browsePath(request);
      // Only a real, non-cancelled choice surfaces. A cancel or a non-OK answer
      // (e.g. a 423 lock the global overlay owns, or a 500) is left calm.
      if (result.ok && !result.cancelled && result.path && result.path.length > 0) {
        onSelect(result.path);
      }
    } finally {
      setBusy(false);
    }
  }

  const isDisabled = Boolean(disabled) || busy;

  return (
    <button
      type="button"
      className="mk-browse-btn"
      onClick={() => void handleClick()}
      disabled={isDisabled}
      aria-busy={busy}
    >
      {busy ? (
        <span className="mk-browse-btn__spinner" aria-hidden="true" />
      ) : (
        <span className="mk-browse-btn__icon" aria-hidden="true">
          <PickerIcon mode={mode} />
        </span>
      )}
      <span className="mk-browse-btn__label">Browse&hellip;</span>
    </button>
  );
}
