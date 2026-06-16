import { CopyButton } from './CopyButton';

interface SetupSnippetsPanelProps {
  setupSnippets: readonly string[];
}

/**
 * Displays renderer-produced setup snippets (e.g. the
 * `$env:GRAPH_CLIENT_SECRET = '<paste locally before running PAX>'` line
 * for AppRegistrationSecret auth). Each snippet is copyable verbatim.
 *
 * Presentation only — the renderer is responsible for ensuring no live
 * secret is ever placed in `setupSnippets`. We trust the upstream guard.
 */
export function SetupSnippetsPanel({ setupSnippets }: SetupSnippetsPanelProps) {
  if (setupSnippets.length === 0) {
    return (
      <section className="mk-panel mk-panel--setup" aria-labelledby="mk-setup-title">
        <header className="mk-panel__head">
          <h3 id="mk-setup-title" className="mk-panel__title">
            Setup snippets
          </h3>
        </header>
        <p className="mk-empty">
          No setup snippet required for the selected options.
        </p>
      </section>
    );
  }

  return (
    <section className="mk-panel mk-panel--setup" aria-labelledby="mk-setup-title">
      <header className="mk-panel__head">
        <div className="mk-panel__title-row">
          <h3 id="mk-setup-title" className="mk-panel__title">
            Setup snippets
          </h3>
          <span className="mk-panel__count" aria-label={`${setupSnippets.length} snippets`}>
            {setupSnippets.length}
          </span>
        </div>
        <p className="mk-panel__lede">
          Run these in the same PowerShell session before the generated command.
          Placeholders only — PAX Cookbook does not collect, store, or read secrets.
        </p>
      </header>

      <ol className="mk-setup-list">
        {setupSnippets.map((snippet, idx) => (
          <li key={idx} className="mk-setup-list__item">
            <div className="mk-codeblock-meta">
              <span className="mk-codeblock-meta__label">Snippet {idx + 1}</span>
              <CopyButton
                text={snippet}
                label={`Copy setup snippet ${idx + 1}`}
              />
            </div>
            <pre
              className="mk-codeblock"
              tabIndex={0}
              aria-label={`Setup snippet ${idx + 1}`}
            >
              <code>{snippet}</code>
            </pre>
          </li>
        ))}
      </ol>
    </section>
  );
}
