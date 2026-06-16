import { PAX_REPO_URL } from '../data/mini-kitchen-constants';

interface PaxDownloadButtonProps {
  /**
   * Visual emphasis. `primary` renders a filled call-to-action button;
   * `subtle` renders an outlined button for denser contexts.
   */
  variant?: 'primary' | 'subtle';
  /** Optional override for the visible label. */
  label?: string;
  /** Optional extra class names appended to the button. */
  className?: string;
}

/**
 * Prominent "Download PAX script" button that opens the official PAX
 * repository in a new tab. Mini-Kitchen never bundles, fetches, or runs the
 * PAX script — this is a plain outbound link styled as a button so users can
 * always find where to get PAX from the places that reference its path.
 */
export function PaxDownloadButton({
  variant = 'primary',
  label = 'Download PAX script',
  className,
}: PaxDownloadButtonProps) {
  const classes = [
    'mk-pax-download',
    `mk-pax-download--${variant}`,
    className,
  ]
    .filter(Boolean)
    .join(' ');

  return (
    <a
      className={classes}
      href={PAX_REPO_URL}
      target="_blank"
      rel="noopener noreferrer"
    >
      <svg
        className="mk-pax-download__icon"
        viewBox="0 0 16 16"
        width="16"
        height="16"
        aria-hidden="true"
        focusable="false"
      >
        <path
          fill="currentColor"
          d="M8 1a.75.75 0 0 1 .75.75v6.69l1.97-1.97a.75.75 0 1 1 1.06 1.06l-3.25 3.25a.75.75 0 0 1-1.06 0L4.22 8.53a.75.75 0 0 1 1.06-1.06l1.97 1.97V1.75A.75.75 0 0 1 8 1ZM2.75 11a.75.75 0 0 1 .75.75v1.5c0 .138.112.25.25.25h8.5a.25.25 0 0 0 .25-.25v-1.5a.75.75 0 0 1 1.5 0v1.5A1.75 1.75 0 0 1 12.25 15h-8.5A1.75 1.75 0 0 1 2 13.25v-1.5a.75.75 0 0 1 .75-.75Z"
        />
      </svg>
      <span className="mk-pax-download__label">{label}</span>
    </a>
  );
}
