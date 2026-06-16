import { useCallback, useRef } from 'react';

export interface CommandTabDescriptor<TId extends string = string> {
  id: TId;
  label: string;
  /**
   * Optional small numeric badge rendered next to the label (e.g. line count
   * for multiline, token count for argv).
   */
  count?: number;
}

interface CommandTabsProps<TId extends string> {
  /** Stable id used to namespace the `id`/`aria-controls` pairings. */
  groupId: string;
  /** Tab descriptors in display order. */
  tabs: readonly CommandTabDescriptor<TId>[];
  activeId: TId;
  onSelect: (id: TId) => void;
  /** Accessible label for the tablist. */
  ariaLabel?: string;
}

/**
 * Keyboard-accessible segmented control for the command preview.
 *
 * - ARIA: `role="tablist"` + `role="tab"` per item + `aria-selected`.
 * - Keyboard: ←/→ rotate, Home/End jump to first/last.
 * - Visual: amber underline on the active pill.
 *
 * Presentation only — owns no command state, never re-invokes the renderer.
 */
export function CommandTabs<TId extends string>({
  groupId,
  tabs,
  activeId,
  onSelect,
  ariaLabel = 'Command view',
}: CommandTabsProps<TId>) {
  const listRef = useRef<HTMLDivElement | null>(null);

  const focusTab = useCallback(
    (id: TId) => {
      const list = listRef.current;
      if (!list) return;
      const next = list.querySelector<HTMLButtonElement>(
        `[data-mk-tab="${id}"]`,
      );
      next?.focus();
    },
    [],
  );

  function handleKeyDown(event: React.KeyboardEvent<HTMLDivElement>) {
    if (tabs.length === 0) return;
    const idx = tabs.findIndex(t => t.id === activeId);
    if (idx < 0) return;

    let nextIdx = idx;
    switch (event.key) {
      case 'ArrowRight':
        nextIdx = (idx + 1) % tabs.length;
        break;
      case 'ArrowLeft':
        nextIdx = (idx - 1 + tabs.length) % tabs.length;
        break;
      case 'Home':
        nextIdx = 0;
        break;
      case 'End':
        nextIdx = tabs.length - 1;
        break;
      default:
        return;
    }

    event.preventDefault();
    const nextId = tabs[nextIdx].id;
    onSelect(nextId);
    focusTab(nextId);
  }

  return (
    <div
      ref={listRef}
      role="tablist"
      aria-label={ariaLabel}
      className="mk-tabs"
      onKeyDown={handleKeyDown}
    >
      {tabs.map(tab => {
        const active = tab.id === activeId;
        const tabId = `${groupId}-tab-${tab.id}`;
        const panelId = `${groupId}-panel-${tab.id}`;
        return (
          <button
            key={tab.id}
            id={tabId}
            type="button"
            role="tab"
            aria-selected={active}
            aria-controls={panelId}
            tabIndex={active ? 0 : -1}
            data-mk-tab={tab.id}
            className={`mk-tab${active ? ' mk-tab--active' : ''}`}
            onClick={() => onSelect(tab.id)}
          >
            <span className="mk-tab__label">{tab.label}</span>
            {typeof tab.count === 'number' ? (
              <span className="mk-tab__count" aria-hidden="true">
                {tab.count}
              </span>
            ) : null}
          </button>
        );
      })}
    </div>
  );
}
