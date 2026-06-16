interface AssumptionsPanelProps {
  commandAssumptions: readonly string[];
  permissionAssumptions: readonly string[];
}

/**
 * Merges renderer assumptions and resolver assumptions into a single list.
 * Phase 6 / D14: presentation only. Dedupe by exact message string —
 * unchanged ordering otherwise.
 */
export function AssumptionsPanel({
  commandAssumptions,
  permissionAssumptions,
}: AssumptionsPanelProps) {
  const seen = new Set<string>();
  const merged: string[] = [];
  for (const a of commandAssumptions) {
    if (!seen.has(a)) {
      seen.add(a);
      merged.push(a);
    }
  }
  for (const a of permissionAssumptions) {
    if (!seen.has(a)) {
      seen.add(a);
      merged.push(a);
    }
  }

  return (
    <section
      className="mk-panel mk-panel--assumptions"
      aria-labelledby="mk-assume-title"
    >
      <header className="mk-panel__head">
        <div className="mk-panel__title-row">
          <h3 id="mk-assume-title" className="mk-panel__title">
            Assumptions
          </h3>
          {merged.length > 0 ? (
            <span
              className="mk-panel__count"
              aria-label={`${merged.length} assumptions`}
            >
              {merged.length}
            </span>
          ) : null}
        </div>
        <p className="mk-panel__lede">
          Default behavior the builder is assuming on your behalf. The command will
          execute under these assumptions unless you override them in the builder.
        </p>
      </header>

      {merged.length === 0 ? (
        <p className="mk-empty">
          No assumptions surfaced for the current selections.
        </p>
      ) : (
        <ul className="mk-assumption-list">
          {merged.map((line, idx) => (
            <li key={idx} className="mk-assumption">
              {line}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
