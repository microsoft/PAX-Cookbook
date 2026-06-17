import { useEffect, useRef, useState, type ReactNode } from 'react';
import { SectionHeader } from './components/SectionHeader';
import { DashboardRepoPills } from '../features/mini-kitchen/components/DashboardRepoPills';
import {
  getPantryRepo,
  getPantryRepoContents,
  fetchPantryDownload,
  type PantryRepoResult,
  type PantryContentItem,
} from '../host/brokerBridge';
import { takePantryDocIntent } from './shellNav';

interface PantryResource {
  id: string;
  name: string;
  short: string;
  owner: string;
  repo: string;
  url: string;
  githubOnly?: boolean;
  /** The PAX Cookbook User Guide entry — rendered in-app by PantryDocViewer
   *  (online-first PDF with a bundled offline fallback) rather than the repo
   *  reader. */
  isUserGuide?: boolean;
}

/** Curated resources surfaced in the Pantry. Read-only — each opens its public
 *  GitHub repository in a native in-app reader (rendered README + repo stats),
 *  with a persistent open-on-GitHub action. */
const PANTRY_RESOURCES: ReadonlyArray<PantryResource> = [
  {
    id: 'user-guide',
    name: '\u{1F4D6} PAX Cookbook User Guide',
    short: 'The complete guide \u2014 installation, recipes, bakes, scheduling, and every feature. Opens right here in the app.',
    owner: 'microsoft',
    repo: 'PAX-Cookbook',
    url: 'https://github.com/microsoft/PAX-Cookbook/blob/main/docs/PAX-Cookbook-User-Guide.md',
    githubOnly: true,
    isUserGuide: true,
  },
  {
    id: 'pax-cookbook',
    name: 'PAX Cookbook',
    short: 'The PAX Cookbook app itself \u2014 source code, releases, and documentation on GitHub.',
    owner: 'microsoft',
    repo: 'PAX-Cookbook',
    url: 'https://github.com/microsoft/PAX-Cookbook',
    githubOnly: true,
  },
  {
    id: 'ai-in-one',
    name: 'AI-in-One Dashboard',
    short: 'Copilot adoption & interaction trends for the AI-in-One Power BI template.',
    owner: 'microsoft',
    repo: 'AI-in-One-Dashboard',
    url: 'https://github.com/microsoft/AI-in-One-Dashboard',
  },
  {
    id: 'ai-business-value',
    name: 'AI Business Value Dashboard',
    short: 'A 50-column AI Business Value superset for ROI and productivity modeling.',
    owner: 'Keithland89',
    repo: 'AI-Business-Value-Dashboard',
    url: 'https://github.com/Keithland89/AI-Business-Value-Dashboard',
  },
  {
    id: 'm365-usage',
    name: 'M365 Usage Analytics Dashboard',
    short: 'Broad M365 usage (Exchange, OneDrive, SharePoint, Teams) plus Copilot.',
    owner: 'microsoft',
    repo: 'M365UsageAnalytics',
    url: 'https://github.com/microsoft/M365UsageAnalytics',
  },
  {
    id: 'pax-script',
    name: 'PAX Script',
    short: 'The PAX Purview Audit Log Processor — the engine behind every bake.',
    owner: 'microsoft',
    repo: 'pax',
    url: 'https://github.com/microsoft/pax',
    githubOnly: true,
  },
];

/** Format an ISO timestamp as a short, friendly date (e.g. "Nov 15, 2025"). */
function formatUpdated(iso: string | null | undefined): string | null {
  if (!iso) {
    return null;
  }
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) {
    return null;
  }
  return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

/** Format a byte count as a compact human size (e.g. "512 KB", "1.6 MB"). */
function formatBytes(bytes: number | null | undefined): string {
  if (typeof bytes !== 'number' || !Number.isFinite(bytes) || bytes <= 0) {
    return '';
  }
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let i = 0;
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024;
    i += 1;
  }
  const text = i === 0 ? String(Math.round(value)) : value.toFixed(value < 10 ? 1 : 0);
  return text + ' ' + units[i];
}

/** A small document glyph for a download row (themeable via currentColor). */
function FileGlyph() {
  return (
    <svg
      width="16"
      height="16"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
      <path d="M14 2v6h6" />
    </svg>
  );
}

const ICON_SVG_PROPS = {
  width: 16,
  height: 16,
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.7,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
  'aria-hidden': true,
};

/** A right-pointing chevron; rotated 90° via CSS when its folder is expanded. */
function ChevronIcon() {
  return (
    <svg {...ICON_SVG_PROPS} width={12} height={12}>
      <path d="m9 18 6-6-6-6" />
    </svg>
  );
}

function FolderIcon() {
  return (
    <svg {...ICON_SVG_PROPS} style={{ color: 'var(--c-blue)' }}>
      <path d="M4 20a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2Z" />
    </svg>
  );
}

/** Power BI templates (.pbit/.pbix) — a bar-chart glyph in the Bakes amber. */
function PowerBiIcon() {
  return (
    <svg {...ICON_SVG_PROPS} style={{ color: 'var(--c-amber)' }}>
      <path d="M3 21h18" />
      <rect x="5" y="10" width="3.5" height="8" rx="0.5" />
      <rect x="10.25" y="6" width="3.5" height="12" rx="0.5" />
      <rect x="15.5" y="3" width="3.5" height="15" rx="0.5" />
    </svg>
  );
}

/** PowerShell scripts (.ps1) — a terminal prompt glyph in the workflow blue. */
function PowerShellIcon() {
  return (
    <svg {...ICON_SVG_PROPS} style={{ color: 'var(--c-blue)' }}>
      <rect x="3" y="4" width="18" height="16" rx="2" />
      <path d="m7 9 3 3-3 3" />
      <path d="M13 15h4" />
    </svg>
  );
}

/** Markdown / docs (.md) — a lined document glyph (inherits muted color). */
function DocIcon() {
  return (
    <svg {...ICON_SVG_PROPS}>
      <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
      <path d="M14 2v6h6" />
      <path d="M8 13h8" />
      <path d="M8 17h5" />
    </svg>
  );
}

/** Data files (.json/.csv/.xlsx) — a small table grid (inherits muted color). */
function DataIcon() {
  return (
    <svg {...ICON_SVG_PROPS}>
      <rect x="3" y="4" width="18" height="16" rx="2" />
      <path d="M3 10h18" />
      <path d="M9 4v16" />
    </svg>
  );
}

/** Pick a file-type icon by extension. Power BI + PowerShell get accent colors;
 *  docs/data get themed glyphs; everything else gets the generic document. */
function fileIconFor(name: string): ReactNode {
  const lower = name.toLowerCase();
  if (lower.endsWith('.pbit') || lower.endsWith('.pbix')) {
    return <PowerBiIcon />;
  }
  if (lower.endsWith('.ps1')) {
    return <PowerShellIcon />;
  }
  if (lower.endsWith('.md')) {
    return <DocIcon />;
  }
  if (lower.endsWith('.json') || lower.endsWith('.csv') || lower.endsWith('.xlsx')) {
    return <DataIcon />;
  }
  return <FileGlyph />;
}

/** Sort a directory listing GitHub-style: folders first, then files, each group
 *  alphabetical (case-insensitive). */
function sortItems(items: PantryContentItem[]): PantryContentItem[] {
  return [...items].sort((a, b) => {
    if (a.type !== b.type) {
      return a.type === 'dir' ? -1 : 1;
    }
    return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
  });
}

/**
 * Lazily-expanded file-explorer tree for the selected repository. The root
 * listing loads on mount; each folder fetches its children the first time it is
 * expanded and caches them, so collapse/re-expand never re-fetches. Files are
 * <a> links the shell hands to the OS browser (the broker already validated the
 * download URL to a trusted GitHub host). Mounted with key={resourceId} so the
 * whole tree state resets when a different Pantry resource is selected.
 */
function PantryFileTree({
  owner,
  repo,
  onFileOpen,
}: {
  owner: string;
  repo: string;
  onFileOpen: (item: PantryContentItem) => void;
}) {
  const [rootItems, setRootItems] = useState<PantryContentItem[] | null>(null);
  const [rootPhase, setRootPhase] = useState<'loading' | 'loaded' | 'error'>('loading');
  const [rootError, setRootError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [collapsed, setCollapsed] = useState(false);
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());
  const [childrenByPath, setChildrenByPath] = useState<Record<string, PantryContentItem[]>>({});
  const [childPhase, setChildPhase] = useState<Record<string, 'loading' | 'error'>>({});

  useEffect(() => {
    let cancelled = false;
    const controller = new AbortController();
    setRootPhase('loading');
    setRootError(null);
    getPantryRepoContents(owner, repo, '', { signal: controller.signal, timeoutMs: 20000 })
      .then(result => {
        if (cancelled) {
          return;
        }
        if (result.ok && result.items) {
          setRootItems(sortItems(result.items));
          setRootPhase('loaded');
        } else {
          setRootItems(null);
          setRootError(result.error ?? 'Could not load the file list.');
          setRootPhase('error');
        }
      })
      .catch(() => {
        if (cancelled) {
          return;
        }
        setRootItems(null);
        setRootError('Could not load the file list.');
        setRootPhase('error');
      });
    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [owner, repo, reloadKey]);

  function loadFolder(path: string) {
    setChildPhase(prev => ({ ...prev, [path]: 'loading' }));
    getPantryRepoContents(owner, repo, path, { timeoutMs: 20000 })
      .then(result => {
        if (result.ok && result.items) {
          const items = sortItems(result.items);
          setChildrenByPath(prev => ({ ...prev, [path]: items }));
          setChildPhase(prev => {
            const next = { ...prev };
            delete next[path];
            return next;
          });
        } else {
          setChildPhase(prev => ({ ...prev, [path]: 'error' }));
        }
      })
      .catch(() => {
        setChildPhase(prev => ({ ...prev, [path]: 'error' }));
      });
  }

  function toggleDir(path: string) {
    const isOpen = expanded.has(path);
    const next = new Set(expanded);
    if (isOpen) {
      next.delete(path);
    } else {
      next.add(path);
      if (!(path in childrenByPath) && childPhase[path] !== 'loading') {
        loadFolder(path);
      }
    }
    setExpanded(next);
  }

  function renderRows(items: PantryContentItem[], depth: number): ReactNode[] {
    const rows: ReactNode[] = [];
    const indent = (d: number) => ({ paddingLeft: 8 + d * 18 });
    for (const item of items) {
      if (item.type === 'dir') {
        const isOpen = expanded.has(item.path);
        rows.push(
          <li key={item.path} className="pantry-files__row pantry-files__row--dir">
            <button
              type="button"
              className="pantry-files__rowbtn"
              style={indent(depth)}
              onClick={() => toggleDir(item.path)}
              aria-expanded={isOpen}
            >
              <span
                className={
                  'pantry-files__chevron' + (isOpen ? ' pantry-files__chevron--open' : '')
                }
                aria-hidden="true"
              >
                <ChevronIcon />
              </span>
              <span className="pantry-files__icon" aria-hidden="true">
                <FolderIcon />
              </span>
              <span className="pantry-files__name">{item.name}</span>
              {childPhase[item.path] === 'loading' ? (
                <span className="pantry-files__spinner" aria-hidden="true" />
              ) : null}
            </button>
          </li>,
        );
        if (isOpen) {
          if (childPhase[item.path] === 'error') {
            rows.push(
              <li key={item.path + '::error'} className="pantry-files__msg" style={indent(depth + 1)}>
                Couldn't load this folder.{' '}
                <button
                  type="button"
                  className="pantry-files__retry"
                  onClick={() => loadFolder(item.path)}
                >
                  Retry
                </button>
              </li>,
            );
          } else {
            const kids = childrenByPath[item.path];
            if (kids && kids.length === 0) {
              rows.push(
                <li key={item.path + '::empty'} className="pantry-files__msg" style={indent(depth + 1)}>
                  Empty folder
                </li>,
              );
            } else if (kids) {
              rows.push(...renderRows(kids, depth + 1));
            }
          }
        }
      } else {
        const sizeText = formatBytes(item.size);
        const inner = (
          <>
            <span className="pantry-files__chevron pantry-files__chevron--spacer" aria-hidden="true" />
            <span className="pantry-files__icon" aria-hidden="true">
              {fileIconFor(item.name)}
            </span>
            <span className="pantry-files__name">{item.name}</span>
            {sizeText ? <span className="pantry-files__size">{sizeText}</span> : null}
          </>
        );
        if (item.downloadUrl) {
          rows.push(
            <li key={item.path} className="pantry-files__row pantry-files__row--file">
              <button
                type="button"
                className="pantry-files__rowbtn"
                style={indent(depth)}
                onClick={() => onFileOpen(item)}
                title={`Preview ${item.name}`}
              >
                {inner}
              </button>
            </li>,
          );
        } else {
          rows.push(
            <li
              key={item.path}
              className="pantry-files__row pantry-files__row--file pantry-files__row--disabled"
            >
              <span className="pantry-files__rowbtn" style={indent(depth)}>
                {inner}
              </span>
            </li>,
          );
        }
      }
    }
    return rows;
  }

  return (
    <section className="pantry-files" aria-label="Files">
      <button
        type="button"
        className="pantry-files__header"
        onClick={() => setCollapsed(c => !c)}
        aria-expanded={!collapsed}
      >
        <span
          className={'pantry-files__toggle' + (!collapsed ? ' pantry-files__toggle--open' : '')}
          aria-hidden="true"
        >
          <ChevronIcon />
        </span>
        <span className="pantry-files__htitle">Files</span>
      </button>
      {!collapsed ? (
        rootPhase === 'loading' ? (
          <div className="pantry-files__tree pantry-files__tree--status" role="status">
            <span className="pantry-files__spinner" aria-hidden="true" />
            <span className="pantry-files__statustext">Loading files…</span>
          </div>
        ) : rootPhase === 'error' ? (
          <div className="pantry-files__tree pantry-files__tree--status" role="status">
            <span className="pantry-files__statustext">{rootError}</span>
            <button
              type="button"
              className="pantry-files__retry"
              onClick={() => setReloadKey(k => k + 1)}
            >
              Retry
            </button>
          </div>
        ) : rootItems && rootItems.length > 0 ? (
          <ul className="pantry-files__tree">{renderRows(rootItems, 0)}</ul>
        ) : (
          <div className="pantry-files__tree pantry-files__tree--status" role="status">
            <span className="pantry-files__statustext">
              This repository has no files to browse.
            </span>
          </div>
        )
      ) : null}
    </section>
  );
}

/** File extensions the in-app preview can render, grouped by how each is shown.
 *  Images / video / audio load directly from the raw GitHub URL; PDFs, HTML, and
 *  text/code are fetched through the broker download proxy first. */
type PreviewKind = 'image' | 'pdf' | 'video' | 'audio' | 'html' | 'text' | 'unsupported';

const PREVIEW_EXT: Record<string, PreviewKind> = (() => {
  const map: Record<string, PreviewKind> = {};
  const add = (kind: PreviewKind, exts: string[]) => {
    for (const e of exts) {
      map[e] = kind;
    }
  };
  add('image', ['png', 'jpg', 'jpeg', 'gif', 'svg', 'webp', 'ico', 'bmp']);
  add('pdf', ['pdf']);
  add('video', ['mp4', 'webm', 'mov', 'avi', 'mkv']);
  add('audio', ['mp3', 'wav', 'ogg', 'flac', 'aac', 'm4a']);
  add('html', ['html', 'htm']);
  add('text', [
    'ps1', 'py', 'json', 'md', 'txt', 'csv', 'xml', 'yml', 'yaml', 'toml', 'ini',
    'cfg', 'conf', 'sh', 'bat', 'cmd', 'ts', 'js', 'css', 'sql', 'r', 'rb', 'java',
    'cs', 'cpp', 'c', 'h', 'go', 'rs', 'swift', 'kt', 'scala', 'lua', 'pl', 'php',
  ]);
  return map;
})();

/** Classify a filename for preview by its extension. */
function previewKindFor(name: string): PreviewKind {
  const dot = name.lastIndexOf('.');
  if (dot < 0 || dot === name.length - 1) {
    return 'unsupported';
  }
  return PREVIEW_EXT[name.slice(dot + 1).toLowerCase()] ?? 'unsupported';
}

/** True when a file has a renderable in-app preview (vs. download-only). */
function isPreviewable(name: string): boolean {
  return previewKindFor(name) !== 'unsupported';
}

/** The lowercased extension of a filename including the leading dot, or null
 *  when there is none (or it is a dotfile such as ".gitignore"). */
function fileExtWithDot(name: string): string | null {
  const dot = name.lastIndexOf('.');
  if (dot <= 0 || dot === name.length - 1) {
    return null;
  }
  return name.slice(dot).toLowerCase();
}

type SaveResult = 'saved' | 'cancelled' | 'error';

// The File System Access API (showSaveFilePicker) is not in the standard TS DOM
// lib; declare the minimal surface this module uses. It is present in WebView2 /
// Chromium; a blob + anchor fallback covers anywhere it is missing.
interface PantrySaveWritable extends WritableStream<Uint8Array> {
  write(data: Blob | BufferSource): Promise<void>;
  close(): Promise<void>;
  abort(reason?: unknown): Promise<void>;
}
interface PantrySaveHandle {
  createWritable(): Promise<PantrySaveWritable>;
}
interface PantrySavePickerOptions {
  suggestedName?: string;
  types?: Array<{ description?: string; accept: Record<string, string[]> }>;
}
declare global {
  interface Window {
    showSaveFilePicker?: (options?: PantrySavePickerOptions) => Promise<PantrySaveHandle>;
  }
}

/**
 * Download a Pantry file through the broker proxy and write it to disk. Prefers
 * the native Save As dialog (showSaveFilePicker) so the user chooses the
 * location, streaming the response straight to the chosen file; if the picker is
 * unavailable it falls back to a blob + anchor download to the default folder. A
 * cancelled dialog resolves to 'cancelled' (no error). The bearer token rides in
 * the proxy fetch header only; the file bytes come from GitHub via the broker.
 */
async function savePantryFileAs(item: PantryContentItem): Promise<SaveResult> {
  const url = item.downloadUrl;
  if (!url) {
    return 'error';
  }

  // Show the native Save As dialog first (when available) so a cancel costs no
  // download; a cancelled dialog surfaces as a benign 'cancelled'.
  let handle: PantrySaveHandle | null = null;
  const picker = typeof window !== 'undefined' ? window.showSaveFilePicker : undefined;
  if (typeof picker === 'function') {
    const options: PantrySavePickerOptions = { suggestedName: item.name };
    const ext = fileExtWithDot(item.name);
    if (ext) {
      options.types = [
        {
          description: ext.slice(1).toUpperCase() + ' file',
          accept: { 'application/octet-stream': [ext] },
        },
      ];
    }
    try {
      handle = await picker(options);
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') {
        return 'cancelled';
      }
      handle = null; // picker failed for another reason — fall back to a blob download
    }
  }

  let resp: Response;
  try {
    resp = await fetchPantryDownload(url, { timeoutMs: 300000 });
  } catch {
    return 'error';
  }
  if (!resp.ok) {
    return 'error';
  }

  try {
    if (handle) {
      const writable = await handle.createWritable();
      try {
        if (resp.body) {
          await resp.body.pipeTo(writable);
        } else {
          await writable.write(await resp.blob());
          await writable.close();
        }
      } catch (writeErr) {
        try {
          await writable.abort();
        } catch {
          // best-effort cleanup of the partial file
        }
        throw writeErr;
      }
    } else {
      const blob = await resp.blob();
      const objectUrl = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = objectUrl;
      anchor.download = item.name;
      document.body.appendChild(anchor);
      anchor.click();
      document.body.removeChild(anchor);
      URL.revokeObjectURL(objectUrl);
    }
    return 'saved';
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') {
      return 'cancelled';
    }
    return 'error';
  }
}

/** A scrollable code view with a sticky line-number gutter. The gutter and the
 *  code are each a single text node (so even a large file stays cheap to
 *  render); the gutter stays pinned at the left while long lines scroll
 *  horizontally. Content is rendered as text, never as HTML. */
function PantryCodeView({ text }: { text: string }) {
  const normalized = text.split('\r\n').join('\n').split('\r').join('\n');
  const lines = normalized.split('\n');
  const gutterNumbers: number[] = [];
  for (let i = 1; i <= lines.length; i += 1) {
    gutterNumbers.push(i);
  }
  return (
    <div className="pantry-preview__code">
      <pre className="pantry-preview__gutter" aria-hidden="true">
        {gutterNumbers.join('\n')}
      </pre>
      <pre className="pantry-preview__codetext">{normalized}</pre>
    </div>
  );
}

/**
 * In-app file preview. Replaces the repo view (README + file tree) with a
 * toolbar (Back to repo / filename / Open in browser) over a type-specific
 * renderer. Images, video, and audio load directly from the trusted raw GitHub
 * URL the broker validated; PDFs, HTML, and text/code are fetched through the
 * broker's authenticated download proxy. HTML renders in a script-sandboxed,
 * opaque-origin iframe (no same-origin access to the app), and text/code render
 * as auto-escaped text — the preview never injects HTML into the app origin.
 */
function PantryFilePreview({
  item,
  onBack,
}: {
  item: PantryContentItem;
  onBack: () => void;
}) {
  const kind = previewKindFor(item.name);
  const url = item.downloadUrl;
  const needsFetch = kind === 'pdf' || kind === 'text' || kind === 'html';

  const [phase, setPhase] = useState<'idle' | 'loading' | 'loaded' | 'error'>(
    needsFetch ? 'loading' : 'idle',
  );
  const [text, setText] = useState<string | null>(null);
  const [blobUrl, setBlobUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [imgDims, setImgDims] = useState<{ w: number; h: number } | null>(null);
  const [saveState, setSaveState] = useState<'idle' | 'saving' | 'saved' | 'error'>('idle');
  const saveMounted = useRef(true);
  useEffect(
    () => () => {
      saveMounted.current = false;
    },
    [],
  );

  useEffect(() => {
    if (!needsFetch || !url) {
      return;
    }
    let cancelled = false;
    const controller = new AbortController();
    let createdBlobUrl: string | null = null;
    setPhase('loading');
    setError(null);
    setText(null);
    setBlobUrl(null);
    fetchPantryDownload(url, { signal: controller.signal, timeoutMs: 30000 })
      .then(async resp => {
        if (!resp.ok) {
          let message = 'This file could not be loaded.';
          try {
            const body = (await resp.json()) as { error?: unknown };
            if (body && typeof body.error === 'string') {
              message = body.error;
            }
          } catch {
            // Non-JSON error body — keep the generic message.
          }
          if (!cancelled) {
            setError(message);
            setPhase('error');
          }
          return;
        }
        if (kind === 'pdf') {
          const buffer = await resp.arrayBuffer();
          if (cancelled) {
            return;
          }
          createdBlobUrl = URL.createObjectURL(new Blob([buffer], { type: 'application/pdf' }));
          setBlobUrl(createdBlobUrl);
          setPhase('loaded');
        } else {
          const body = await resp.text();
          if (cancelled) {
            return;
          }
          setText(body);
          setPhase('loaded');
        }
      })
      .catch(() => {
        if (!cancelled) {
          setError('This file could not be loaded.');
          setPhase('error');
        }
      });
    return () => {
      cancelled = true;
      controller.abort();
      if (createdBlobUrl) {
        URL.revokeObjectURL(createdBlobUrl);
      }
    };
  }, [url, kind, needsFetch]);

  const sizeText = formatBytes(item.size);
  const lineCount = kind === 'text' && text !== null ? text.split('\n').length : null;
  const metaParts: string[] = [];
  if (imgDims) {
    metaParts.push(imgDims.w + ' × ' + imgDims.h);
  }
  if (lineCount !== null) {
    metaParts.push(lineCount.toLocaleString() + ' lines');
  }
  if (sizeText) {
    metaParts.push(sizeText);
  }
  const metaText = metaParts.join(' · ');

  async function onSaveAsClick() {
    if (!url || saveState === 'saving') {
      return;
    }
    setSaveState('saving');
    const result = await savePantryFileAs(item);
    if (!saveMounted.current) {
      return;
    }
    if (result === 'cancelled') {
      setSaveState('idle');
      return;
    }
    setSaveState(result === 'saved' ? 'saved' : 'error');
    window.setTimeout(
      () => {
        if (saveMounted.current) {
          setSaveState('idle');
        }
      },
      result === 'saved' ? 2500 : 4000,
    );
  }

  function renderBody() {
    if (!url) {
      return <div className="pantry-preview__status">This file can't be previewed.</div>;
    }
    if (needsFetch && phase === 'loading') {
      return (
        <div className="pantry-preview__status">
          <span className="pantry-preview__spinner" aria-hidden="true" />
          <span>Loading preview…</span>
        </div>
      );
    }
    if (needsFetch && phase === 'error') {
      return (
        <div className="pantry-preview__status">
          <span>{error ?? 'This file could not be loaded.'}</span>
        </div>
      );
    }
    switch (kind) {
      case 'image':
        return (
          <div className="pantry-preview__center">
            <img
              className="pantry-preview__image"
              src={url}
              alt={item.name}
              onLoad={e =>
                setImgDims({
                  w: e.currentTarget.naturalWidth,
                  h: e.currentTarget.naturalHeight,
                })
              }
            />
          </div>
        );
      case 'video':
        return (
          <div className="pantry-preview__center">
            <video className="pantry-preview__media" src={url} controls />
          </div>
        );
      case 'audio':
        return (
          <div className="pantry-preview__center">
            <audio className="pantry-preview__audio" src={url} controls />
          </div>
        );
      case 'pdf':
        return blobUrl ? (
          <iframe className="pantry-preview__frame" src={blobUrl} title={item.name} />
        ) : null;
      case 'html':
        return text !== null ? (
          <iframe
            className="pantry-preview__frame"
            sandbox="allow-scripts"
            srcDoc={text}
            title={item.name}
          />
        ) : null;
      case 'text':
        return text !== null ? <PantryCodeView text={text} /> : null;
      default:
        return (
          <div className="pantry-preview__status pantry-preview__unsupported">
            <p className="pantry-preview__unsupported-title">
              This file type can't be previewed.
            </p>
            <p className="pantry-preview__hint">Use “Save As” or “Open in browser” to download it.</p>
          </div>
        );
    }
  }

  return (
    <div className="pantry-preview">
      <div className="pantry-preview__toolbar">
        <button type="button" className="pantry-preview__back" onClick={onBack}>
          <span aria-hidden="true">←</span> Back to repo
        </button>
        <div className="pantry-preview__heading">
          <span className="pantry-preview__filename" title={item.name}>
            {item.name}
          </span>
          {metaText ? <span className="pantry-preview__meta">{metaText}</span> : null}
        </div>
        <div className="pantry-preview__actions">
          {url ? (
            <button
              type="button"
              className="pantry-preview__btn pantry-preview__btn--primary"
              onClick={onSaveAsClick}
              disabled={saveState === 'saving'}
            >
              {saveState === 'saving' ? (
                <>
                  <span className="pantry-preview__btn-spinner" aria-hidden="true" />
                  Saving…
                </>
              ) : saveState === 'saved' ? (
                'Saved ✓'
              ) : saveState === 'error' ? (
                'Save failed'
              ) : (
                'Save As…'
              )}
            </button>
          ) : null}
          {url ? (
            <a
              className="pantry-preview__btn"
              href={url}
              target="_blank"
              rel="noopener noreferrer"
            >
              Open in browser
              <span aria-hidden="true"> ↗</span>
            </a>
          ) : null}
        </div>
      </div>
      <div className="pantry-preview__body">{renderBody()}</div>
    </div>
  );
}

/**
 * Pantry workspace — a curated reader for the PAX dashboards and the PAX engine.
 * A 15% selector panel on the left chooses a resource; an 85% detail pane on the
 * right shows the repository natively: a metadata header (stars / forks /
 * language / license / last updated + topic tags) and the GitHub-rendered
 * README.
 *
 * The README + metadata are fetched through the broker's read-only
 * GET /api/v1/pantry/repo route, which calls GitHub's public REST API
 * server-side (api.github.com only, parameterized by owner/repo). The README
 * HTML GitHub returns is already server-sanitized by its markup pipeline, so it
 * renders directly in a markdown container. A persistent "Open on GitHub" action
 * opens the repo in the OS browser. The page is read-only and non-executing: it
 * never runs PAX or touches a recipe; the only broker call is the repo reader.
 */
/** A muted cupboard / pantry-shelf glyph for the empty-state splash. */
function PantryShelfIcon() {
  return (
    <svg
      width="56"
      height="56"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.4"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <rect x="3.5" y="3" width="17" height="18" rx="2" />
      <path d="M3.5 9h17" />
      <path d="M3.5 15h17" />
      <rect x="6" y="5" width="2.6" height="4" rx="0.6" />
      <rect x="10" y="5.4" width="2.6" height="3.6" rx="0.6" />
      <rect x="6.5" y="11" width="2.6" height="4" rx="0.6" />
      <rect x="11" y="11.4" width="3" height="3.6" rx="0.6" />
    </svg>
  );
}

const USER_GUIDE_ONLINE_PDF =
  'https://raw.githubusercontent.com/microsoft/PAX-Cookbook/main/docs/PAX-Cookbook-User-Guide.pdf';
// The bundled offline copy ships in the SPA static root (vite base is '/app/'),
// so it is always available even with no network.
const USER_GUIDE_LOCAL_PDF = '/app/PAX-Cookbook-User-Guide.pdf';
const USER_GUIDE_BROWSER_URL =
  'https://github.com/microsoft/PAX-Cookbook/blob/main/docs/PAX-Cookbook-User-Guide.md';
const USER_GUIDE_ONLINE_TIMEOUT_MS = 10000;

/**
 * The PAX Cookbook User Guide, shown in the embedded viewer. It always tries the
 * latest copy from GitHub first (through the broker's download proxy, ~10s
 * budget); if that cannot be reached in time it falls back to the copy bundled
 * with the install, so the guide is always available — even offline. The only
 * external link is the explicit "Open in browser" action.
 */
function PantryDocViewer({ onBack }: { onBack: () => void }) {
  const [phase, setPhase] = useState<'loading' | 'loaded' | 'error'>('loading');
  const [blobUrl, setBlobUrl] = useState<string | null>(null);
  const [source, setSource] = useState<'online' | 'offline' | null>(null);

  useEffect(() => {
    let cancelled = false;
    let created: string | null = null;
    setPhase('loading');
    setBlobUrl(null);
    setSource(null);

    async function tryOnline(): Promise<ArrayBuffer | null> {
      try {
        const resp = await fetchPantryDownload(USER_GUIDE_ONLINE_PDF, {
          timeoutMs: USER_GUIDE_ONLINE_TIMEOUT_MS,
        });
        if (!resp.ok) {
          return null;
        }
        return await resp.arrayBuffer();
      } catch {
        return null;
      }
    }

    async function tryLocal(): Promise<ArrayBuffer | null> {
      try {
        const resp = await fetch(USER_GUIDE_LOCAL_PDF, { cache: 'no-store' });
        if (!resp.ok) {
          return null;
        }
        return await resp.arrayBuffer();
      } catch {
        return null;
      }
    }

    void (async () => {
      let buffer = await tryOnline();
      let src: 'online' | 'offline' = 'online';
      if (!buffer) {
        buffer = await tryLocal();
        src = 'offline';
      }
      if (cancelled) {
        return;
      }
      if (!buffer) {
        setPhase('error');
        return;
      }
      created = URL.createObjectURL(new Blob([buffer], { type: 'application/pdf' }));
      setBlobUrl(created);
      setSource(src);
      setPhase('loaded');
    })();

    return () => {
      cancelled = true;
      if (created) {
        URL.revokeObjectURL(created);
      }
    };
  }, []);

  return (
    <div className="pantry-preview">
      <div className="pantry-preview__toolbar">
        <button type="button" className="pantry-preview__back" onClick={onBack}>
          <span aria-hidden="true">←</span> Back
        </button>
        <div className="pantry-preview__heading">
          <span className="pantry-preview__filename" title="PAX Cookbook User Guide">
            PAX Cookbook User Guide
          </span>
          {source === 'offline' ? (
            <span className="pantry-preview__meta">
              Offline copy — couldn't reach the latest version
            </span>
          ) : source === 'online' ? (
            <span className="pantry-preview__meta">Latest version</span>
          ) : null}
        </div>
        <div className="pantry-preview__actions">
          <a
            className="pantry-preview__btn"
            href={USER_GUIDE_BROWSER_URL}
            target="_blank"
            rel="noopener noreferrer"
          >
            Open in browser
            <span aria-hidden="true"> ↗</span>
          </a>
        </div>
      </div>
      <div className="pantry-preview__body">
        {phase === 'loading' ? (
          <div className="pantry-preview__status">
            <span className="pantry-preview__spinner" aria-hidden="true" />
            <span>Loading the User Guide…</span>
          </div>
        ) : phase === 'error' ? (
          <div className="pantry-preview__status">
            <span>
              The User Guide could not be loaded right now. Use “Open in browser”
              to read it on GitHub.
            </span>
          </div>
        ) : blobUrl ? (
          <iframe
            className="pantry-preview__frame"
            src={blobUrl}
            title="PAX Cookbook User Guide"
          />
        ) : null}
      </div>
    </div>
  );
}

export function PantryWorkspace() {
  const [online, setOnline] = useState(
    typeof navigator === 'undefined' ? true : navigator.onLine,
  );
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [previewFile, setPreviewFile] = useState<PantryContentItem | null>(null);
  const [saveToast, setSaveToast] = useState<{ kind: 'saving' | 'saved' | 'error'; name: string } | null>(
    null,
  );
  const saveToastTimer = useRef<number | null>(null);
  const [phase, setPhase] = useState<'loading' | 'loaded' | 'error'>('loading');
  const [repo, setRepo] = useState<PantryRepoResult | null>(null);
  const [errorText, setErrorText] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const readmeRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    function update() {
      setOnline(navigator.onLine);
    }
    window.addEventListener('online', update);
    window.addEventListener('offline', update);
    return () => {
      window.removeEventListener('online', update);
      window.removeEventListener('offline', update);
    };
  }, []);

  // An explicit click on the legacy nav rail's Pantry item re-selects this
  // section even when it is already active. The shell signals that click with
  // { type: 'mk-nav', section: 'pantry', reselect: true }; on it we return the
  // Pantry to its default view — clear the selected resource (back to the
  // splash), close any open file preview, and scroll to the top. The reselect
  // flag gates this so the programmatic section posts that have no reselect flag
  // are never disturbed.
  // A help-panel / Settings request to open the User Guide is handed off via a
  // one-shot intent flag on the top window. Honour it on first mount so landing
  // on the Pantry from those entry points lands directly on the guide.
  useEffect(() => {
    if (takePantryDocIntent()) {
      setPreviewFile(null);
      setSelectedId('user-guide');
    }
  }, []);

  useEffect(() => {
    const onMessage = (ev: MessageEvent) => {
      if (ev.origin !== window.location.origin) {
        return;
      }
      const data = ev.data as unknown;
      if (
        data &&
        typeof data === 'object' &&
        (data as { type?: unknown }).type === 'mk-nav' &&
        (data as { section?: unknown }).section === 'pantry'
      ) {
        // A pending "open the User Guide" intent (from the help panel or the
        // Settings link) wins over the default reselect-to-splash behaviour.
        if (takePantryDocIntent()) {
          setPreviewFile(null);
          setSelectedId('user-guide');
          if (typeof window !== 'undefined') {
            window.scrollTo({ top: 0, left: 0 });
          }
          return;
        }
        if ((data as { reselect?: unknown }).reselect === true) {
          setSelectedId(null);
          setPreviewFile(null);
          if (typeof window !== 'undefined') {
            window.scrollTo({ top: 0, left: 0 });
            window.requestAnimationFrame(() => window.scrollTo({ top: 0, left: 0 }));
          }
        }
      }
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, []);

  // Fetch the selected repository's metadata + README through the read-only
  // broker route. Re-runs on selection change, when connectivity returns, or on
  // an explicit Retry. A stale request is abandoned so a fast reselect never
  // paints an earlier repository.
  useEffect(() => {
    if (!online || selectedId === null) {
      return;
    }
    const resource = PANTRY_RESOURCES.find(r => r.id === selectedId);
    if (!resource || resource.isUserGuide) {
      return;
    }
    let cancelled = false;
    const controller = new AbortController();
    setPhase('loading');
    setErrorText(null);
    getPantryRepo(resource.owner, resource.repo, {
      signal: controller.signal,
      timeoutMs: 20000,
    })
      .then(result => {
        if (cancelled) {
          return;
        }
        if (result.ok) {
          setRepo(result);
          setPhase('loaded');
        } else {
          setRepo(null);
          setErrorText(result.error ?? 'This repository could not be loaded.');
          setPhase('error');
        }
      })
      .catch(() => {
        if (cancelled) {
          return;
        }
        setRepo(null);
        setErrorText('This repository could not be loaded. Open it on GitHub instead.');
        setPhase('error');
      });
    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [selectedId, online, reloadKey]);

  const selected =
    selectedId === null
      ? null
      : PANTRY_RESOURCES.find(r => r.id === selectedId) ?? null;
  const updated = formatUpdated(repo?.updatedAt);

  // Safety net for README images: any image that still fails to load after the
  // broker's server-side relative-URL rewrite is hidden, so no broken-image
  // icon ever appears in the rendered README.
  useEffect(() => {
    const container = readmeRef.current;
    if (!container) {
      return;
    }
    const imgs = container.querySelectorAll('img');
    imgs.forEach(img => {
      img.onerror = () => {
        img.style.display = 'none';
      };
      // An image that already failed before the handler attached (cached error)
      // is hidden immediately.
      if (img.complete && img.naturalWidth === 0) {
        img.style.display = 'none';
      }
    });
  }, [repo?.readmeHtml]);

  // Save As (download) for a Pantry file. A previewable file opens the preview
  // (whose toolbar has its own Save As button); a non-previewable file is
  // downloaded straight from the tree, with a small status toast.
  async function runSaveAs(item: PantryContentItem) {
    if (saveToastTimer.current !== null) {
      window.clearTimeout(saveToastTimer.current);
      saveToastTimer.current = null;
    }
    setSaveToast({ kind: 'saving', name: item.name });
    const result = await savePantryFileAs(item);
    if (result === 'cancelled') {
      setSaveToast(null);
      return;
    }
    setSaveToast({ kind: result === 'saved' ? 'saved' : 'error', name: item.name });
    saveToastTimer.current = window.setTimeout(
      () => {
        setSaveToast(null);
        saveToastTimer.current = null;
      },
      result === 'saved' ? 2500 : 4000,
    );
  }

  function handleFileOpen(item: PantryContentItem) {
    if (isPreviewable(item.name)) {
      setPreviewFile(item);
    } else {
      void runSaveAs(item);
    }
  }

  useEffect(
    () => () => {
      if (saveToastTimer.current !== null) {
        window.clearTimeout(saveToastTimer.current);
      }
    },
    [],
  );

  return (
    <section aria-labelledby="view-pantry">
      <SectionHeader
        title="Pantry"
        titleId="view-pantry"
        lede="Resources and tools for your dashboards."
        helpTopic="cookbookPantry"
        accent="var(--c-teal)"
      />

      {!online ? (
        <div className="pantry-offline" role="status">
          You're offline. Pantry resources require an internet connection.
        </div>
      ) : null}

      <div className="pantry-viewer">
        <aside className="pantry-selector" aria-label="Dashboard resources">
          {PANTRY_RESOURCES.map(res => {
            const active = res.id === selectedId;
            return (
              <div
                key={res.id}
                className={
                  'pantry-card' + (active ? ' pantry-card--active' : '')
                }
              >
                <button
                  type="button"
                  className="pantry-card__select"
                  onClick={() => {
                    setPreviewFile(null);
                    setSelectedId(res.id);
                  }}
                  aria-pressed={active}
                >
                  <span className="pantry-card__name">{res.name}</span>
                  <span className="pantry-card__short">{res.short}</span>
                </button>
                <DashboardRepoPills url={res.url} githubOnly={res.githubOnly} />
              </div>
            );
          })}
        </aside>

        <div className="pantry-stage" aria-live="polite">
          {selected === null ? (
            <div className="pantry-splash">
              <span className="pantry-splash__icon" aria-hidden="true">
                <PantryShelfIcon />
              </span>
              <h2 className="pantry-splash__title">Welcome to the Pantry</h2>
              <p className="pantry-splash__text">
                Pick a resource from the sidebar to browse its files, read the
                docs, and download what you need.
              </p>
            </div>
          ) : selected.isUserGuide ? (
            <PantryDocViewer onBack={() => setSelectedId(null)} />
          ) : previewFile !== null ? (
            <PantryFilePreview item={previewFile} onBack={() => setPreviewFile(null)} />
          ) : (
            <>
          <div className="pantry-stage__toolbar">
            <h2 className="pantry-stage__title">{selected.name}</h2>
            <a
              className="pantry-stage__open"
              href={selected.url}
              target="_blank"
              rel="noopener noreferrer"
              title={`Open ${selected.name} on GitHub (opens in your browser)`}
            >
              Open on GitHub
              <span aria-hidden="true"> ↗</span>
            </a>
          </div>
          <div className="pantry-stage__viewer">
            {!online ? (
              <div className="pantry-viewer-status" role="status">
                <p className="pantry-viewer-status__title">You're offline</p>
                <p className="pantry-viewer-status__body">
                  Pantry resources need an internet connection. Reconnect to load{' '}
                  {selected.name}, or open it on GitHub.
                </p>
                <div className="pantry-viewer-status__actions">
                  <a
                    className="pantry-stage__open"
                    href={selected.url}
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    Open on GitHub
                    <span aria-hidden="true"> ↗</span>
                  </a>
                </div>
              </div>
            ) : phase === 'loading' ? (
              <div className="pantry-viewer-status" role="status">
                <span className="pantry-viewer-status__spinner" aria-hidden="true" />
                <p className="pantry-viewer-status__body">Loading {selected.name}…</p>
              </div>
            ) : phase === 'error' ? (
              <div className="pantry-viewer-status" role="status">
                <p className="pantry-viewer-status__title">Couldn't load this repository</p>
                <p className="pantry-viewer-status__body">{errorText}</p>
                <div className="pantry-viewer-status__actions">
                  <button
                    type="button"
                    className="pantry-stage__cta-btn"
                    onClick={() => setReloadKey(k => k + 1)}
                  >
                    Retry
                  </button>
                  <a
                    className="pantry-stage__open"
                    href={selected.url}
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    Open on GitHub
                    <span aria-hidden="true"> ↗</span>
                  </a>
                </div>
              </div>
            ) : repo ? (
              <article className="pantry-repo">
                {repo.description ? (
                  <p className="pantry-repo__desc">{repo.description}</p>
                ) : null}
                <div className="pantry-repo__meta">
                  <span className="pantry-repo__stat" title="Stars">
                    ⭐ {(repo.stars ?? 0).toLocaleString()}
                  </span>
                  <span className="pantry-repo__stat" title="Forks">
                    🍴 {(repo.forks ?? 0).toLocaleString()}
                  </span>
                  {repo.language ? (
                    <span className="pantry-repo__stat">{repo.language}</span>
                  ) : null}
                  {repo.license ? (
                    <span className="pantry-repo__stat">{repo.license}</span>
                  ) : null}
                  {updated ? (
                    <span className="pantry-repo__stat">Updated {updated}</span>
                  ) : null}
                </div>
                {repo.topics && repo.topics.length > 0 ? (
                  <div className="pantry-repo__topics">
                    {repo.topics.map(topic => (
                      <span key={topic} className="pantry-topic">
                        {topic}
                      </span>
                    ))}
                  </div>
                ) : null}
                <PantryFileTree
                  key={selected.id}
                  owner={selected.owner}
                  repo={selected.repo}
                  onFileOpen={handleFileOpen}
                />
                {repo.readmeHtml ? (
                  <div
                    ref={readmeRef}
                    className="pantry-readme markdown-body"
                    dangerouslySetInnerHTML={{ __html: repo.readmeHtml }}
                  />
                ) : (
                  <p className="pantry-repo__no-readme">
                    This repository doesn't have a README to display. Open it on
                    GitHub to see its contents.
                  </p>
                )}
              </article>
            ) : null}
          </div>
            </>
          )}
        </div>
      </div>
      {saveToast ? (
        <div
          className={'pantry-save-toast pantry-save-toast--' + saveToast.kind}
          role="status"
        >
          {saveToast.kind === 'saving' ? (
            <span className="pantry-save-toast__spinner" aria-hidden="true" />
          ) : null}
          <span className="pantry-save-toast__text">
            {saveToast.kind === 'saving'
              ? 'Saving ' + saveToast.name + '…'
              : saveToast.kind === 'saved'
                ? 'Saved ' + saveToast.name
                : "Couldn't save " + saveToast.name}
          </span>
        </div>
      ) : null}
    </section>
  );
}
