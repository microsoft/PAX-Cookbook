import { TARGET_PAX_VERSION_DEFAULT } from '../data/mini-kitchen-constants';

const PAX_REPO_URL = 'https://github.com/microsoft/pax';

/**
 * Compact target-PAX pill rendered in the builder section header strip,
 * paired with a small link to the official PAX repository. The other
 * compatibility metadata (switch catalog, schema versions) still travels
 * with exported lite recipes but is no longer surfaced in the UI.
 */
export function VersionPanel() {
  return (
    <div
      className="mk-version-strip"
      role="group"
      aria-label="Supported PAX script version"
    >
      <span className="mk-version-strip__pill">
        Supports PAX Script v{TARGET_PAX_VERSION_DEFAULT}
      </span>
      <a
        className="mk-version-strip__repo-link"
        href={PAX_REPO_URL}
        target="_blank"
        rel="noopener noreferrer"
      >
        View the PAX project
      </a>
    </div>
  );
}
