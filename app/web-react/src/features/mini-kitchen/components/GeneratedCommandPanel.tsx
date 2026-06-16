import { useMemo, useState } from 'react';
import type { RenderedCommand } from '../lib/commandRenderer';
import { tokenizePowerShell } from '../lib/highlightPowerShell';
import { CommandTabs } from './CommandTabs';
import type { CommandTabDescriptor } from './CommandTabs';
import { CopyButton } from './CopyButton';

type CommandTabId = 'single' | 'multi' | 'argv' | 'explanation';
export type ReviewCommandView = 'single' | 'multi';

interface GeneratedCommandPanelProps {
  command: RenderedCommand;
  /**
   * When provided, the panel runs in "review section" controlled mode:
   * the parent owns the active view and renders the tab strip in the
   * section header. The panel renders only the code block + copy button
   * for that view, with no internal header or tabs. Argv and explanation
   * views are not rendered in this mode — they live in a separate
   * disclosure beside the review section.
   *
   * When omitted, the panel runs in legacy uncontrolled mode (its own
   * header, its own pill tabs, all four views).
   */
  activeView?: ReviewCommandView;
}

/**
 * Renders the rendered PAX command. Two modes:
 *
 * - Uncontrolled (legacy): self-managed pill tabs across single-line,
 *   multiline, argv, and explanation views.
 * - Controlled (`activeView` provided): renders only the code block for
 *   the selected single-line / multiline view; parent provides the tabs.
 *
 * Presentation only — reads from the renderer output object.
 */
export function GeneratedCommandPanel({ command, activeView }: GeneratedCommandPanelProps) {
  const controlled = activeView !== undefined;
  const [internalTab, setInternalTab] = useState<CommandTabId>('single');

  const activeTab: CommandTabId = controlled ? activeView : internalTab;

  const argvText = useMemo(() => command.argv.join(' '), [command.argv]);
  const multilineLineCount = useMemo(
    () => command.multiline.split('\n').length,
    [command.multiline],
  );

  if (controlled) {
    return (
      <div className="mk-cmd-view" data-mk-cmd-view={activeTab}>
        {activeTab === 'single' ? (
          <SingleLineView text={command.singleLine} />
        ) : (
          <MultilineView text={command.multiline} />
        )}
      </div>
    );
  }

  const tabs: readonly CommandTabDescriptor<CommandTabId>[] = [
    { id: 'single', label: 'Single-line' },
    { id: 'multi', label: 'Multi-line', count: multilineLineCount },
    { id: 'argv', label: 'Argv', count: command.argv.length },
    {
      id: 'explanation',
      label: 'Explanation',
      count: command.explanation.length,
    },
  ];

  const groupId = 'mk-cmd';
  const panelId = `${groupId}-panel-${activeTab}`;
  const tabId = `${groupId}-tab-${activeTab}`;

  return (
    <section className="mk-panel mk-panel--command" aria-labelledby="mk-cmd-title">
      <header className="mk-panel__head">
        <div className="mk-panel__title-row">
          <h3 id="mk-cmd-title" className="mk-panel__title">
            Generated command
          </h3>
          <span className="mk-panel__sub">
            PowerShell 7 · {command.argv.length} tokens
          </span>
        </div>
        <p className="mk-panel__lede">
          PAX Cookbook composes the command from your selections. It does not run PAX or
          contact your tenant.
        </p>
      </header>

      <CommandTabs
        groupId={groupId}
        tabs={tabs}
        activeId={activeTab}
        onSelect={setInternalTab}
        ariaLabel="Command view"
      />

      <div
        id={panelId}
        role="tabpanel"
        aria-labelledby={tabId}
        tabIndex={0}
        className="mk-tabpanel"
      >
        {activeTab === 'single' ? (
          <SingleLineView text={command.singleLine} />
        ) : activeTab === 'multi' ? (
          <MultilineView text={command.multiline} />
        ) : activeTab === 'argv' ? (
          <ArgvView argv={command.argv} joined={argvText} />
        ) : (
          <ExplanationView lines={command.explanation} />
        )}
      </div>
    </section>
  );
}

function HighlightedCommand({ text }: { text: string }) {
  const tokens = useMemo(() => tokenizePowerShell(text), [text]);
  return (
    <>
      {tokens.map((tok, idx) =>
        tok.kind === 'whitespace' ? (
          tok.text
        ) : (
          <span key={idx} className={`mk-cmd-tok mk-cmd-tok--${tok.kind}`}>
            {tok.text}
          </span>
        ),
      )}
    </>
  );
}

function SingleLineView({ text }: { text: string }) {
  return (
    <div className="mk-codeblock-wrap">
      <div className="mk-codeblock-meta">
        <span className="mk-codeblock-meta__label">Single-line</span>
        <CopyButton text={text} label="Copy single-line command" variant="primary" />
      </div>
      <pre className="mk-codeblock" tabIndex={0} aria-label="Single-line command">
        <code>
          <HighlightedCommand text={text} />
        </code>
      </pre>
    </div>
  );
}

function MultilineView({ text }: { text: string }) {
  return (
    <div className="mk-codeblock-wrap">
      <div className="mk-codeblock-meta">
        <span className="mk-codeblock-meta__label">Multi-line</span>
        <CopyButton text={text} label="Copy multi-line command" variant="primary" />
      </div>
      <pre className="mk-codeblock" tabIndex={0} aria-label="Multi-line command">
        <code>
          <HighlightedCommand text={text} />
        </code>
      </pre>
      <p className="mk-codeblock-note">
        If pasting into a terminal loses backtick continuations, switch to
        single-line.
      </p>
    </div>
  );
}

function ArgvView({
  argv,
  joined,
}: {
  argv: readonly string[];
  joined: string;
}) {
  return (
    <div className="mk-codeblock-wrap">
      <div className="mk-codeblock-meta">
        <span className="mk-codeblock-meta__label">
          Argv — canonical token list ({argv.length})
        </span>
        <CopyButton text={joined} label="Copy tokens" />
      </div>
      <ol className="mk-argv-list">
        {argv.map((token, index) => (
          <li key={`${index}-${token}`} className="mk-argv-list__item">
            <span className="mk-argv-list__index" aria-hidden="true">
              {index + 1}
            </span>
            <code className="mk-argv-list__token">{token}</code>
          </li>
        ))}
      </ol>
      <p className="mk-codeblock-note">
        Argv is the canonical truth. Both the single-line and multiline forms above are
        derived from these tokens.
      </p>
    </div>
  );
}

function ExplanationView({ lines }: { lines: readonly string[] }) {
  if (lines.length === 0) {
    return (
      <p className="mk-empty">No explanation lines produced for the current selections.</p>
    );
  }
  return (
    <div className="mk-explanation-wrap">
      <ul className="mk-explanation">
        {lines.map((line, idx) => (
          <li key={idx} className="mk-explanation__item">
            {line}
          </li>
        ))}
      </ul>
      <p className="mk-codeblock-note">
        Plain-language summary of what the rendered command will do at runtime. It does
        not claim PAX Cookbook verified any of these steps.
      </p>
    </div>
  );
}

/**
 * Argv + explanation views for use in the review section's "more views"
 * disclosure. Presentation only — reads `command.argv` and
 * `command.explanation` from the renderer output.
 */
export function ArgvAndExplanationViews({ command }: { command: RenderedCommand }) {
  const argvText = useMemo(() => command.argv.join(' '), [command.argv]);
  return (
    <div className="mk-cmd-extra">
      <ArgvView argv={command.argv} joined={argvText} />
      <ExplanationView lines={command.explanation} />
    </div>
  );
}
