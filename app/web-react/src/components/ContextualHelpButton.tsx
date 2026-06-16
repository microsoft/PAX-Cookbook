import {
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
} from 'react';
import { createPortal } from 'react-dom';
import {
  CONTEXTUAL_HELP_TOPICS,
  type ContextualHelpTopicId,
} from '../data/contextual-help';

/**
 * Small click-to-open contextual help button + non-modal popover.
 *
 * Behavior (no new dependencies, no modal dialog):
 *   - Click / tap / Enter / Space toggles the popover open and closed.
 *   - Escape closes it and returns focus to the trigger.
 *   - Clicking outside closes it.
 *   - Opening one popover closes any other open popover (module-level signal).
 *   - The popover does not steal focus on open.
 *   - Position flips horizontally / vertically when near a viewport edge.
 *
 * Accessibility: the trigger is a real <button> with an accessible label,
 * aria-expanded, and aria-controls (while open). The popover uses
 * role="tooltip" but is click-activated, so it is not hover-only.
 */

const OPEN_SIGNAL = 'mk-contextual-help-open';

interface PopCoords {
  top: number;
  left: number;
}

interface ContextualHelpButtonProps {
  topic: ContextualHelpTopicId;
  /**
   * Optional accessible-name override. Defaults to the topic title, producing
   * a label like "Help: Audit output path".
   */
  label?: string;
  /** Optional extra class for alignment tweaks at the call site. */
  className?: string;
  /** Optional smaller visual size for tight inline label rows. */
  size?: 'sm' | 'md';
}

export function ContextualHelpButton({
  topic,
  label,
  className,
  size = 'md',
}: ContextualHelpButtonProps) {
  const data = CONTEXTUAL_HELP_TOPICS[topic];
  const [open, setOpen] = useState(false);
  const [coords, setCoords] = useState<PopCoords | null>(null);

  const reactId = useId();
  const popId = `${reactId}-help-pop`;

  const wrapRef = useRef<HTMLSpanElement>(null);
  const btnRef = useRef<HTMLButtonElement>(null);
  const popRef = useRef<HTMLSpanElement>(null);

  // Opening one popover closes the others.
  useEffect(() => {
    if (!open) return;
    window.dispatchEvent(new CustomEvent(OPEN_SIGNAL, { detail: reactId }));
    const onOther = (event: Event) => {
      const detail = (event as CustomEvent<string>).detail;
      if (detail !== reactId) setOpen(false);
    };
    window.addEventListener(OPEN_SIGNAL, onOther);
    return () => window.removeEventListener(OPEN_SIGNAL, onOther);
  }, [open, reactId]);

  // Outside click + Escape.
  useEffect(() => {
    if (!open) return;
    const onPointer = (event: PointerEvent) => {
      const wrap = wrapRef.current;
      if (wrap && !wrap.contains(event.target as Node)) setOpen(false);
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false);
        btnRef.current?.focus();
      }
    };
    document.addEventListener('pointerdown', onPointer, true);
    document.addEventListener('keydown', onKey, true);
    return () => {
      document.removeEventListener('pointerdown', onPointer, true);
      document.removeEventListener('keydown', onKey, true);
    };
  }, [open]);

  // Edge-aware placement. The popover is portaled to <body> with fixed
  // positioning so it can never be clipped by a card's overflow:hidden.
  // Runs before paint so it never flashes off-screen, and re-measures on
  // resize / scroll while open.
  useLayoutEffect(() => {
    if (!open) return;
    const measure = () => {
      const btn = btnRef.current;
      const pop = popRef.current;
      if (!btn || !pop) return;
      const b = btn.getBoundingClientRect();
      const vw = window.innerWidth;
      const vh = window.innerHeight;
      const popW = pop.offsetWidth;
      const popH = pop.offsetHeight;
      const margin = 8;
      const gap = 8;

      // Vertical: prefer below, flip above only when below overflows and
      // there is room above.
      let top = b.bottom + gap;
      if (top + popH + margin > vh && b.top - popH - gap > margin) {
        top = b.top - popH - gap;
      }
      top = Math.max(margin, Math.min(top, vh - popH - margin));

      // Horizontal: center on the trigger, then clamp into the viewport.
      const center = b.left + b.width / 2;
      let left = center - popW / 2;
      left = Math.max(margin, Math.min(left, vw - popW - margin));

      setCoords({ top, left });
    };
    measure();
    window.addEventListener('resize', measure);
    window.addEventListener('scroll', measure, true);
    return () => {
      window.removeEventListener('resize', measure);
      window.removeEventListener('scroll', measure, true);
    };
  }, [open]);

  const accessibleLabel = `Help: ${label ?? data.title}`;

  return (
    <span
      ref={wrapRef}
      className={`mk-cth${size === 'sm' ? ' mk-cth--sm' : ''}${
        className ? ` ${className}` : ''
      }`}
    >
      <button
        ref={btnRef}
        type="button"
        className="mk-cth__btn"
        aria-label={accessibleLabel}
        aria-expanded={open}
        aria-controls={open ? popId : undefined}
        onClick={() => setOpen(prev => !prev)}
      >
        <span className="mk-cth__glyph" aria-hidden="true">
          ?
        </span>
      </button>
      {open
        ? createPortal(
            <span
              ref={popRef}
              id={popId}
              role="tooltip"
              className="mk-cth__pop"
              style={
                coords
                  ? { top: coords.top, left: coords.left, visibility: 'visible' }
                  : { top: 0, left: 0, visibility: 'hidden' }
              }
            >
              {data.title ? (
                <span className="mk-cth__pop-title">{data.title}</span>
              ) : null}
              <span className="mk-cth__pop-body">{data.body}</span>
            </span>,
            document.body,
          )
        : null}
    </span>
  );
}
