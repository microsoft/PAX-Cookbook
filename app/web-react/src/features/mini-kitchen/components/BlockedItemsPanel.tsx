import type {
  CommandRenderBlockedItem,
  CommandRenderBlockedKind,
} from '../lib/commandRenderer';

interface BlockedItemsPanelProps {
  blocked: readonly CommandRenderBlockedItem[];
}

/**
 * Shows tokens the renderer refused to emit (deprecated/disabled switches,
 * secret-looking values, duplicate structured switches, orphaned advanced
 * tokens). Phase 6 / D14: presentation only.
 *
 * R17 mitigation: for the `secret-value` kind, this panel displays a
 * sanitized title ("A secret-looking value was blocked") and never the
 * literal value. The renderer's `detail` field is already guaranteed not
 * to contain the verbatim secret per the `CommandRenderBlockedItem` type
 * contract, but we treat it as a sanitized label only.
 */
export function BlockedItemsPanel({ blocked }: BlockedItemsPanelProps) {
  if (blocked.length === 0) {
    return null;
  }

  return (
    <section
      className="mk-panel mk-panel--blocked"
      aria-labelledby="mk-blocked-title"
    >
      <header className="mk-panel__head">
        <div className="mk-panel__title-row">
          <h3 id="mk-blocked-title" className="mk-panel__title">
            Blocked from the command
          </h3>
          <span
            className="mk-panel__count mk-panel__count--urgent"
            aria-label={`${blocked.length} blocked items`}
          >
            {blocked.length}
          </span>
        </div>
        <p className="mk-panel__lede">
          These tokens will not appear in the generated command. PAX would either reject
          them at runtime or they would leak sensitive data. Resolve them in the builder
          before copying.
        </p>
      </header>

      <ul className="mk-blocked-list">
        {blocked.map(item => {
          const isSecret = item.kind === 'secret-value';
          return (
            <li key={item.id} className="mk-blocked-item">
              <div className="mk-blocked-item__head">
                <span
                  className={`mk-blocked-item__kind mk-blocked-item__kind--${item.kind}`}
                  aria-label={`Block kind: ${kindLabel(item.kind)}`}
                >
                  {kindLabel(item.kind)}
                </span>
                <span className="mk-blocked-item__title">
                  {isSecret
                    ? 'A secret-looking value was blocked'
                    : item.detail}
                </span>
              </div>
              <p className="mk-blocked-item__reason">{kindReason(item.kind)}</p>
              {isSecret ? (
                <p className="mk-blocked-item__safeguard">
                  PAX Cookbook does not display, copy, or store secret-looking
                  values. The rendered command uses a <code>&lt;CLIENT_SECRET&gt;</code>
                  placeholder — replace it in your terminal before running.
                </p>
              ) : null}
            </li>
          );
        })}
      </ul>
    </section>
  );
}

function kindLabel(kind: CommandRenderBlockedKind): string {
  switch (kind) {
    case 'removed-switch':
      return 'Removed switch';
    case 'secret-value':
      return 'Secret value';
    case 'duplicate-switch':
      return 'Duplicate switch';
    case 'orphan-advanced-token':
      return 'Orphan token';
  }
}

function kindReason(kind: CommandRenderBlockedKind): string {
  switch (kind) {
    case 'removed-switch':
      return 'This switch is deprecated or temporarily disabled in the target PAX version. Including it would cause PAX to exit immediately.';
    case 'secret-value':
      return 'A token in advanced switches looked like a client secret. Secrets must never appear in a shareable command.';
    case 'duplicate-switch':
      return 'The same switch was also provided through a structured field. The structured field wins; the duplicate from advanced switches was dropped.';
    case 'orphan-advanced-token':
      return 'A bare value with no preceding switch was found in advanced switches. PAX cannot bind it to a parameter, so it was dropped.';
  }
}
