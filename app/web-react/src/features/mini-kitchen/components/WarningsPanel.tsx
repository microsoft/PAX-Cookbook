import type { WarningEntry } from '../types';

interface WarningsPanelProps {
  commandWarnings: readonly WarningEntry[];
  permissionWarnings: readonly string[];
}

interface DisplayWarning {
  id: string;
  severity: 'info' | 'warning' | 'error';
  message: string;
  field?: string;
}

/**
 * Surfaces every renderer warning and every permissions-resolver warning
 * next to the generated command. Phase 6 / D14: presentation only — no
 * warning is invented here. Sources:
 *
 *   - `command.warnings: WarningEntry[]` (renderer)
 *   - `permissions.warnings: string[]` (resolver)
 *
 * The two streams are deduped by message text. Permission warnings inherit
 * `warning` severity because the resolver does not currently classify them.
 */
export function WarningsPanel({
  commandWarnings,
  permissionWarnings,
}: WarningsPanelProps) {
  const seen = new Set<string>();
  const merged: DisplayWarning[] = [];

  for (const w of commandWarnings) {
    if (seen.has(w.message)) continue;
    seen.add(w.message);
    merged.push({
      id: `cmd-${w.id}`,
      severity: w.severity,
      message: w.message,
      field: w.field,
    });
  }
  for (const message of permissionWarnings) {
    if (seen.has(message)) continue;
    seen.add(message);
    merged.push({
      id: `perm-${message.slice(0, 32)}`,
      severity: 'warning',
      message,
    });
  }

  if (merged.length === 0) {
    return (
      <section className="mk-panel mk-panel--warnings" aria-labelledby="mk-warn-title">
        <header className="mk-panel__head">
          <h3 id="mk-warn-title" className="mk-panel__title">
            Warnings
          </h3>
        </header>
        <p className="mk-empty">No warnings for the current selections.</p>
      </section>
    );
  }

  return (
    <section className="mk-panel mk-panel--warnings" aria-labelledby="mk-warn-title">
      <header className="mk-panel__head">
        <div className="mk-panel__title-row">
          <h3 id="mk-warn-title" className="mk-panel__title">
            Warnings
          </h3>
          <span className="mk-panel__count" aria-label={`${merged.length} warnings`}>
            {merged.length}
          </span>
        </div>
        <p className="mk-panel__lede">
          Read these before copying. PAX Cookbook cannot validate paths, tenants, or
          permissions.
        </p>
      </header>

      <ul className="mk-warning-list">
        {merged.map(item => (
          <li
            key={item.id}
            className={`mk-warning mk-warning--${item.severity}`}
          >
            <span
              className="mk-warning__icon"
              aria-label={severityLabel(item.severity)}
            >
              {severityGlyph(item.severity)}
            </span>
            <div className="mk-warning__body">
              <p className="mk-warning__message">{item.message}</p>
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}

function severityGlyph(severity: 'info' | 'warning' | 'error'): string {
  if (severity === 'error') return '⛔';
  if (severity === 'warning') return '⚠';
  return 'ℹ';
}

function severityLabel(severity: 'info' | 'warning' | 'error'): string {
  if (severity === 'error') return 'Error';
  if (severity === 'warning') return 'Warning';
  return 'Info';
}
