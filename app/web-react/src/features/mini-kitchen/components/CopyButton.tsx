import { useEffect, useRef, useState } from 'react';

interface CopyButtonProps {
  /**
   * Text to copy to the clipboard verbatim. The renderer is responsible for
   * blocking secret-looking values upstream; this primitive trusts its
   * caller and copies the string as-is.
   */
  text: string;
  /**
   * Accessible label describing what will be copied (e.g.
   * "Copy single-line command"). Shown next to the icon.
   */
  label: string;
  /** Optional visual variant. Defaults to `ghost`. */
  variant?: 'ghost' | 'primary';
  /** Optional className appended to the button. */
  className?: string;
  /**
   * Optional descriptive id for an associated status region. When omitted,
   * the button renders its own inline `aria-live` status.
   */
  statusId?: string;
}

type CopyStatus = 'idle' | 'copied' | 'error';

/**
 * Mini-Kitchen Clipboard API copy button.
 *
 * Phase 6 / D14: presentation only. Trusts the caller to pass a renderer- or
 * resolver-derived string. Never copies an empty value. Shows "Copied" on
 * success and a plain-text failure message on rejection — no silent fail.
 */
export function CopyButton({
  text,
  label,
  variant = 'ghost',
  className,
  statusId,
}: CopyButtonProps) {
  const [status, setStatus] = useState<CopyStatus>('idle');
  const timerRef = useRef<number | null>(null);

  useEffect(() => {
    return () => {
      if (timerRef.current !== null) {
        window.clearTimeout(timerRef.current);
      }
    };
  }, []);

  const empty = text.trim().length === 0;

  function scheduleReset(ms: number) {
    if (timerRef.current !== null) {
      window.clearTimeout(timerRef.current);
    }
    timerRef.current = window.setTimeout(() => setStatus('idle'), ms);
  }

  async function handleClick() {
    if (empty) return;
    try {
      const clipboard = navigator?.clipboard;
      if (!clipboard || typeof clipboard.writeText !== 'function') {
        throw new Error('Clipboard API unavailable');
      }
      await clipboard.writeText(text);
      setStatus('copied');
      scheduleReset(1600);
    } catch {
      setStatus('error');
      scheduleReset(3500);
    }
  }

  const baseClass = `mk-copy mk-copy--${variant}`;
  const composedClass = className ? `${baseClass} ${className}` : baseClass;

  let statusText = '';
  if (status === 'copied') statusText = 'Copied';
  else if (status === 'error') statusText = 'Copy failed — select the text manually.';

  return (
    <span className="mk-copy-wrap">
      <button
        type="button"
        className={composedClass}
        onClick={handleClick}
        disabled={empty}
        aria-label={label}
        title={label}
        aria-describedby={statusId}
      >
        <span aria-hidden="true" className="mk-copy__icon">
          {status === 'copied' ? '✓' : '⎘'}
        </span>
        <span className="mk-copy__label">
          {status === 'copied' ? 'Copied' : 'Copy'}
        </span>
      </button>
      {statusId ? null : (
        <span className="mk-copy__status" role="status" aria-live="polite">
          {statusText}
        </span>
      )}
    </span>
  );
}
