import type {
  PermissionEntry,
  PermissionGroup,
  PermissionsReport,
} from '../types';

interface PermissionsPreviewProps {
  permissions: PermissionsReport;
}

const GROUP_ORDER: readonly PermissionGroup[] = [
  'graph',
  'runtime',
  'environment',
  'output',
];

/**
 * Groups whose title bar shows a per-group count chip. Only Graph permissions
 * are tallied for the operator, so runtime prerequisites, environment
 * requirements, and output access are listed without a summary number on their
 * title bars.
 */
const GROUPS_WITH_COUNT: ReadonlySet<PermissionGroup> = new Set([
  'graph',
]);

const GROUP_LABELS: Record<PermissionGroup, string> = {
  graph: 'Graph permissions',
  runtime: 'Runtime prerequisites',
  environment: 'Environment requirements',
  output: 'Output access',
  info: 'Informational callouts',
};

const GROUP_DESCRIPTIONS: Record<PermissionGroup, string> = {
  graph:
    'Application or delegated Microsoft Graph scopes the chosen auth mode must already hold (tenant admin consent).',
  runtime:
    'Software, modules, or interpreters the operator machine must have installed before running the command.',
  environment:
    'Tenant- or operator-controlled toggles that must already be enabled.',
  output:
    'Write access to the destination of the audit data and user info outputs.',
  info: 'Mechanics and caveats PAX Cookbook wants visible — they do not by themselves imply an additional grant.',
};

/**
 * Right-rail permissions preview. Phase 6 / D14: presentation only — reads
 * `permissions.required` directly from the resolver's report. Entries are
 * bucketed by their `group` field; ordering within a bucket is preserved
 * from the resolver. No re-derivation.
 */
export function PermissionsPreview({ permissions }: PermissionsPreviewProps) {
  const requiredByGroup = bucket(permissions.required);
  const requiredCount = permissions.required.length;
  const graphCount = permissions.required.filter(
    entry => entry.group === 'graph',
  ).length;

  return (
    <section
      className="mk-panel mk-panel--perms"
      aria-labelledby="mk-perms-title"
    >
      <header className="mk-panel__head">
        <div className="mk-panel__title-row">
          <h3 id="mk-perms-title" className="mk-panel__title">
            Permissions preview
          </h3>
          <span
            className="mk-panel__count"
            aria-label={`${graphCount} required Graph permission${graphCount === 1 ? '' : 's'}`}
          >
            {graphCount}
          </span>
        </div>
        <p className="mk-panel__lede">
          Required at runtime by the rendered command. PAX Cookbook has not checked your
          tenant — these are derived from the recipe shape, not from a live consent
          state.
        </p>
      </header>

      {requiredCount === 0 ? (
        <p className="mk-empty">
          No additional permissions surfaced for the current selections.
        </p>
      ) : (
        <div className="mk-perm-groups">
          {GROUP_ORDER.map(group => {
            const entries = requiredByGroup.get(group);
            if (!entries || entries.length === 0) return null;
            return (
              <PermissionGroupBlock
                key={`req-${group}`}
                group={group}
                entries={entries}
              />
            );
          })}
        </div>
      )}
    </section>
  );
}

interface PermissionGroupBlockProps {
  group: PermissionGroup;
  entries: readonly PermissionEntry[];
}

function PermissionGroupBlock({
  group,
  entries,
}: PermissionGroupBlockProps) {
  return (
    <section
      className={`mk-perm-group mk-perm-group--${group}`}
      aria-label={GROUP_LABELS[group]}
    >
      <header className="mk-perm-group__head">
        <h5 className="mk-perm-group__title">{GROUP_LABELS[group]}</h5>
        {GROUPS_WITH_COUNT.has(group) ? (
          <span className="mk-perm-group__count" aria-hidden="true">
            {entries.length}
          </span>
        ) : null}
      </header>
      <p className="mk-perm-group__desc">{GROUP_DESCRIPTIONS[group]}</p>
      <ul className="mk-perm-list">
        {entries.map(entry => (
          <li key={entry.id} className="mk-perm-item">
            <div className="mk-perm-item__head">
              <span className="mk-perm-item__name">{entry.name}</span>
              {entry.severity ? (
                <span
                  className={`mk-perm-item__severity mk-perm-item__severity--${entry.severity}`}
                >
                  {entry.severity}
                </span>
              ) : null}
            </div>
            <p className="mk-perm-item__because">{entry.requiredBecause}</p>
            <p className="mk-perm-item__applies">
              <span className="mk-perm-item__applies-key">applies to:</span>{' '}
              {entry.appliesToLabel ?? entry.appliesTo}
            </p>
            {entry.notes && entry.notes.length > 0 ? (
              <ul className="mk-perm-item__notes">
                {entry.notes.map((note, idx) => (
                  <li key={idx}>{note}</li>
                ))}
              </ul>
            ) : null}
          </li>
        ))}
      </ul>
    </section>
  );
}

function bucket(entries: readonly PermissionEntry[]): Map<PermissionGroup, PermissionEntry[]> {
  const result = new Map<PermissionGroup, PermissionEntry[]>();
  for (const entry of entries) {
    const list = result.get(entry.group) ?? [];
    list.push(entry);
    result.set(entry.group, list);
  }
  return result;
}
