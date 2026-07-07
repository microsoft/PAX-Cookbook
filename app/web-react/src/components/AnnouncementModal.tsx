import { Fragment, type ReactElement, type ReactNode } from 'react';

/** One archived What's New entry (newest-first ordering is done by the broker). */
export interface WhatsNewEntry {
  id: string;
  title: string;
  date: string | null;
  /** Per-entry "show this at startup" preference (the checkbox state). */
  showAgain: boolean;
  /** Raw Markdown body for this entry. */
  bodyMarkdown: string;
  /** filename -> data: URI, for LOCAL images shipped in this entry's own folder. */
  images: Record<string, string>;
}

interface AnnouncementModalProps {
  open: boolean;
  /** Full history, newest first. */
  entries: WhatsNewEntry[];
  /** The entry currently shown in the content pane. */
  selectedId: string | null;
  /** Select a different entry from the timeline rail. */
  onSelect: (id: string) => void;
  /** Toggle the per-entry "show this at startup" preference (persists per entry-id). */
  onShowAgainChange: (id: string, showAgain: boolean) => void;
  /** Close the browser (does NOT change any show-again preference — the checkbox does). */
  onClose: () => void;
}

/**
 * "What's New" history browser (feature D).
 *
 * A newsletter-style highlight reel, not a technical changelog. It auto-appears
 * on the first startup after an in-app update (defaulting to the NEWEST entry)
 * while that entry's "show at startup" box is checked, and is also reachable on
 * demand from the Updates page at any time. A left timeline rail lists every
 * archived entry newest-first; the content pane renders the selected entry's
 * Markdown body and its LOCAL images.
 *
 * The Markdown is rendered through a DELIBERATELY SAFE renderer: it never uses
 * dangerouslySetInnerHTML and never interprets raw HTML. Images resolve ONLY
 * against the entry's own {filename -> data:uri} map (built by the broker from
 * the sandboxed per-entry folder), so a remote URL or a ../traversal reference
 * simply is not a key and renders as its alt text — no fetch, no escape. Links
 * are allowed only for http/https/mailto schemes.
 */
export function AnnouncementModal({
  open,
  entries,
  selectedId,
  onSelect,
  onShowAgainChange,
  onClose,
}: AnnouncementModalProps): ReactElement | null {
  if (!open) {
    return null;
  }
  const selected =
    entries.find((e) => e.id === selectedId) ?? (entries.length > 0 ? entries[0] : null);

  return (
    <div
      className="whatsnew"
      role="dialog"
      aria-modal="true"
      aria-labelledby="whatsnew-title"
    >
      <div className="whatsnew__scrim" aria-hidden="true" onClick={onClose} />
      <div className="whatsnew__dialog">
        <header className="whatsnew__header">
          <span className="whatsnew__eyebrow">What's New</span>
          <button
            type="button"
            className="whatsnew__close"
            aria-label="Close What's New"
            onClick={onClose}
          >
            ×
          </button>
        </header>

        {selected === null ? (
          <div className="whatsnew__empty">
            <p>
              No highlights yet. New feature announcements will appear here after an
              update.
            </p>
          </div>
        ) : (
          <div className="whatsnew__layout">
            <nav className="whatsnew__rail" aria-label="What's New timeline">
              <ul className="whatsnew__timeline">
                {entries.map((e) => (
                  <li key={e.id}>
                    <button
                      type="button"
                      className={
                        'whatsnew__timeitem' + (e.id === selected.id ? ' is-active' : '')
                      }
                      aria-current={e.id === selected.id ? 'true' : undefined}
                      onClick={() => onSelect(e.id)}
                    >
                      <span className="whatsnew__timedate">{formatDate(e.date)}</span>
                      <span className="whatsnew__timetitle">{e.title}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </nav>

            <main className="whatsnew__content" id="whatsnew-title" tabIndex={-1}>
              {formatDate(selected.date) ? (
                <div className="whatsnew__date">{formatDate(selected.date)}</div>
              ) : null}
              <h2 className="whatsnew__title">{selected.title}</h2>
              <div className="whatsnew__body">
                {renderMarkdown(selected.bodyMarkdown, selected.images)}
              </div>
            </main>
          </div>
        )}

        <footer className="whatsnew__actions">
          {selected !== null ? (
            <label className="whatsnew__showagain">
              <input
                type="checkbox"
                className="whatsnew__showagain-box"
                checked={selected.showAgain}
                onChange={(e) => onShowAgainChange(selected.id, e.target.checked)}
              />
              <span>Show this at startup</span>
            </label>
          ) : (
            <span />
          )}
          <button
            type="button"
            className="whatsnew__btn whatsnew__btn--primary"
            onClick={onClose}
            autoFocus
          >
            Got it
          </button>
        </footer>
      </div>
    </div>
  );
}

// Friendly display date. Accepts an ISO date (YYYY-MM-DD) or datetime; returns
// "" for a missing/unparseable value (the caller then omits the date line).
function formatDate(iso: string | null): string {
  if (!iso) {
    return '';
  }
  const d = new Date(iso.length === 10 ? iso + 'T00:00:00' : iso);
  if (Number.isNaN(d.getTime())) {
    return iso;
  }
  return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

// ---------------------------------------------------------------------------
// Minimal, safe Markdown → React renderer. No HTML passthrough, no
// dangerouslySetInnerHTML. Supports headings, bold, italic, inline code,
// safe links, unordered/ordered lists, horizontal rules, and paragraphs.
// ---------------------------------------------------------------------------
const SAFE_LINK = /^(https?:|mailto:)/i;

function renderMarkdown(md: string, images: Record<string, string>): ReactNode {
  const lines = (md ?? '').replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
  const blocks: ReactNode[] = [];
  let key = 0;

  let paragraph: string[] = [];
  let list: { ordered: boolean; items: string[] } | null = null;

  const flushParagraph = () => {
    if (paragraph.length > 0) {
      const text = paragraph.join(' ').trim();
      if (text) {
        blocks.push(<p key={`p${key++}`} className="announcement__p">{renderInline(text, images)}</p>);
      }
      paragraph = [];
    }
  };
  const flushList = () => {
    if (list && list.items.length > 0) {
      const items = list.items.map((it, i) => (
        <li key={`li${key}-${i}`}>{renderInline(it, images)}</li>
      ));
      blocks.push(
        list.ordered
          ? <ol key={`ol${key++}`} className="announcement__list">{items}</ol>
          : <ul key={`ul${key++}`} className="announcement__list">{items}</ul>,
      );
      list = null;
    }
  };

  for (const raw of lines) {
    const line = raw.trimEnd();
    const trimmed = line.trim();

    if (trimmed === '') {
      flushParagraph();
      flushList();
      continue;
    }

    // Horizontal rule.
    if (/^(-{3,}|\*{3,}|_{3,})$/.test(trimmed)) {
      flushParagraph();
      flushList();
      blocks.push(<hr key={`hr${key++}`} className="announcement__hr" />);
      continue;
    }

    // Heading (#..######).
    const heading = /^(#{1,6})\s+(.*)$/.exec(trimmed);
    if (heading) {
      flushParagraph();
      flushList();
      const level = heading[1].length;
      const text = heading[2].trim();
      const cls = `announcement__h announcement__h${level}`;
      const inner = renderInline(text, images);
      switch (level) {
        case 1: blocks.push(<h3 key={`h${key++}`} className={cls}>{inner}</h3>); break;
        case 2: blocks.push(<h4 key={`h${key++}`} className={cls}>{inner}</h4>); break;
        default: blocks.push(<h5 key={`h${key++}`} className={cls}>{inner}</h5>); break;
      }
      continue;
    }

    // Ordered list item.
    const ordered = /^\d+\.\s+(.*)$/.exec(trimmed);
    if (ordered) {
      flushParagraph();
      if (!list || !list.ordered) { flushList(); list = { ordered: true, items: [] }; }
      list.items.push(ordered[1]);
      continue;
    }

    // Unordered list item.
    const unordered = /^[-*+]\s+(.*)$/.exec(trimmed);
    if (unordered) {
      flushParagraph();
      if (!list || list.ordered) { flushList(); list = { ordered: false, items: [] }; }
      list.items.push(unordered[1]);
      continue;
    }

    // Plain paragraph text.
    flushList();
    paragraph.push(trimmed);
  }
  flushParagraph();
  flushList();

  return <Fragment>{blocks}</Fragment>;
}

// Inline: **bold**, *italic* / _italic_, `code`, [text](safe-url). Everything
// else is plain (React-escaped) text. Order matters: code first (so its content
// is not further parsed), then links, then bold, then italic.
function renderInline(text: string, images: Record<string, string>): ReactNode {
  return parseImage(text, images, 0);
}

// Local, sandboxed images only. ![alt](src) renders <img> ONLY when `src` is an
// exact key in the entry's images map AND its value is a data:image URI (the
// broker builds that map from the entry's own folder). A remote URL, an unknown
// name, or a ../traversal reference is not a key -> the alt text renders instead
// (no network fetch, no path escape). Non-image spans fall through to the
// existing safe inline chain (code -> link -> bold -> italic).
function parseImage(text: string, images: Record<string, string>, k: number): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /!\[([^\]]*)\]\(([^)]+)\)/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    if (m.index > last) { out.push(...parseCode(text.slice(last, m.index), k)); k += 1000; }
    const alt = m[1] ?? '';
    const src = (m[2] ?? '').trim();
    const dataUri = Object.prototype.hasOwnProperty.call(images, src) ? images[src] : null;
    if (dataUri && /^data:image\//i.test(dataUri)) {
      out.push(<img key={`img${k++}`} className="whatsnew__img" src={dataUri} alt={alt} />);
    } else if (alt) {
      out.push(<Fragment key={`ia${k++}`}>{alt}</Fragment>);
    }
    last = m.index + m[0].length;
  }
  if (last < text.length) { out.push(...parseCode(text.slice(last), k)); }
  return out;
}

function parseCode(text: string, k: number): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /`([^`]+)`/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    if (m.index > last) { out.push(...parseLink(text.slice(last, m.index), k)); k += 100; }
    out.push(<code key={`c${k++}`} className="announcement__code">{m[1]}</code>);
    last = m.index + m[0].length;
  }
  if (last < text.length) { out.push(...parseLink(text.slice(last), k)); }
  return out;
}

function parseLink(text: string, k: number): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /\[([^\]]+)\]\(([^)]+)\)/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    if (m.index > last) { out.push(...parseBold(text.slice(last, m.index), k)); k += 100; }
    const label = m[1];
    const href = m[2].trim();
    if (SAFE_LINK.test(href)) {
      out.push(
        <a key={`a${k++}`} href={href} target="_blank" rel="noopener noreferrer">
          {parseBold(label, k)}
        </a>,
      );
    } else {
      // Unsafe/relative scheme: render the label as plain text, drop the link.
      out.push(...parseBold(label, k)); k += 100;
    }
    last = m.index + m[0].length;
  }
  if (last < text.length) { out.push(...parseBold(text.slice(last), k)); }
  return out;
}

function parseBold(text: string, k: number): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /\*\*([^*]+)\*\*/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    if (m.index > last) { out.push(...parseItalic(text.slice(last, m.index), k)); k += 100; }
    out.push(<strong key={`b${k++}`}>{parseItalic(m[1], k)}</strong>);
    last = m.index + m[0].length;
  }
  if (last < text.length) { out.push(...parseItalic(text.slice(last), k)); }
  return out;
}

function parseItalic(text: string, k: number): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /(?:\*([^*]+)\*|_([^_]+)_)/g;
  let last = 0;
  let m: RegExpExecArray | null;
  while ((m = re.exec(text)) !== null) {
    if (m.index > last) { out.push(<Fragment key={`t${k++}`}>{text.slice(last, m.index)}</Fragment>); }
    out.push(<em key={`i${k++}`}>{m[1] ?? m[2]}</em>);
    last = m.index + m[0].length;
  }
  if (last < text.length) { out.push(<Fragment key={`t${k++}`}>{text.slice(last)}</Fragment>); }
  return out;
}
