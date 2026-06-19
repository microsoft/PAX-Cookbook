/**
 * PAX Cookbook product-shell chrome.
 *
 * Renders the app shell — header, left navigation, main content region, and a
 * status rail — inside the native WebView2 host. In production the React
 * surface runs embedded (`?embed=1`) inside the legacy shell, which supplies
 * the outer chrome; this file's standalone header, nav, and rail are used only
 * when the surface is opened on its own. Navigation switches the active view in
 * local React state. Home and Recipes are live; cooking actions are disabled in
 * this build.
 */
import { Fragment, useEffect, useRef, useState, type CSSProperties, type ReactElement } from 'react';
import { SHELL_SECTIONS } from './shell/sections';
import { consultNavigationGuard } from './shell/navigationGuard';
import { shellSectionHash, type ShellSectionId } from './shell/shellNav';
import {
  clearImportTicketFromUrl,
  consumeImport,
  fetchLockState,
  readImportTicketId,
  setPendingImport,
} from './host/importHandoff';
import { parseLiteRecipeJson } from './features/mini-kitchen/lib/recipeImporter';
import { parseFullPaxRecipeJson } from './features/mini-kitchen/lib/fullPaxRecipeImporter';
import {
  hasHostBridge,
  postCloseDecision,
  subscribeHostCloseRequest,
} from './host/closeHandoff';
import { listCooks, shutdownBroker } from './host/brokerBridge';
import { CloseConfirmModal } from './components/CloseConfirmModal';

// Embedded content-only mode. When the React surface is hosted inside the
// legacy shell's main-content iframe, it is loaded with `?embed=1` so it
// renders only the active section (no React header, sidebar, or status rail)
// and lets the legacy left nav drive which section is shown. An optional
// `section` param selects the initial view to match the legacy nav target.
interface EmbedConfig {
  embed: boolean;
  section: string | null;
}

function readEmbedConfig(): EmbedConfig {
  try {
    const params = new URLSearchParams(window.location.search);
    return { embed: params.get('embed') === '1', section: params.get('section') };
  } catch {
    return { embed: false, section: null };
  }
}

const EMBED_CONFIG = readEmbedConfig();

function accentStyle(accent: string): CSSProperties {
  return { ['--accent' as string]: accent } as CSSProperties;
}

type ImportBannerState =
  | { phase: 'idle' }
  | { phase: 'checking' }
  | { phase: 'locked' }
  | { phase: 'consuming' }
  | { phase: 'done'; fileName: string; source: 'paxlite' | 'pax' }
  | { phase: 'invalid'; fileName: string; message: string }
  | { phase: 'pax-unsupported-version'; fileName: string; version: string }
  | { phase: 'error'; message: string };

const LOCK_POLL_INTERVAL_MS = 1000;

function App() {
  const initialSectionId =
    EMBED_CONFIG.section && SHELL_SECTIONS.some((s) => s.id === EMBED_CONFIG.section)
      ? EMBED_CONFIG.section
      : SHELL_SECTIONS[0].id;
  const [activeId, setActiveId] = useState<string>(initialSectionId);
  // Bumped on every navigation (legacy nav message or local sidebar click).
  // The active section is keyed by it, so each nav click — including a click on
  // the section already shown — fully remounts the page and re-runs its data
  // fetches. This is what makes a just-saved recipe (or any changed data) show
  // up immediately on the target page instead of stale data from a prior visit.
  const [navKey, setNavKey] = useState(0);
  const active = SHELL_SECTIONS.find((s) => s.id === activeId) ?? SHELL_SECTIONS[0];
  const sectionKey = `${active.id}:${navKey}`;

  // Kept current for the cross-frame `mk-nav` handler (registered once on
  // mount) so a guarded "stay" can restore the legacy hash to the section that
  // is actually shown.
  const activeIdRef = useRef(activeId);
  activeIdRef.current = activeId;
  // One-shot: the section id of an echo `mk-nav` to ignore. Set when a guarded
  // "stay" restores the legacy hash (which re-posts `mk-nav` for that section);
  // consuming it keeps the builder mounted instead of remounting it.
  const suppressNavSectionRef = useRef<string | null>(null);

  // File-open import handoff. When the Windows app launched (or re-activated)
  // PAX Cookbook from a double-clicked .paxlite / .pax file, it navigated here
  // with `?import=<ticket id>`. Wait for the broker lock to read Unlocked (the
  // normal Windows Hello / lock ceremony stays on the legacy shell), then
  // consume the one-time ticket and route the recipe into the builder. Both a
  // .paxlite and a full .pax land as a draft to review. Nothing here runs PAX,
  // auto-bakes, or fabricates a success.
  const [importBanner, setImportBanner] = useState<ImportBannerState>({ phase: 'idle' });
  const importStartedRef = useRef(false);

  // Native close handshake. The WebView2 shell intercepts the title-bar X,
  // taskbar Close, and Alt+F4 and posts `cookbook:host-close-request` instead
  // of exiting; this opens the shared close modal so the React surface matches
  // the legacy shell's behavior. The operator's choice is relayed back to the
  // shell, which performs the actual hide or shutdown.
  const [closeModalOpen, setCloseModalOpen] = useState(false);

  // When the close modal opens we check whether a bake is currently running so
  // the modal can caution the operator and steer them to Minimize instead of an
  // exit that could interrupt it. A locked or unreachable broker simply leaves
  // this false (no false caution).
  const [bakeRunning, setBakeRunning] = useState(false);

  useEffect(() => {
    return subscribeHostCloseRequest(() => {
      setCloseModalOpen(true);
      setBakeRunning(false);
      void (async () => {
        try {
          const response = await listCooks();
          const running = (response.data?.cooks ?? []).some(
            (cook) => cook.status === 'running',
          );
          setBakeRunning(running);
        } catch {
          // Non-fatal: leave bakeRunning false if the check fails.
          setBakeRunning(false);
        }
      })();
    });
  }, []);

  // Embedded navigation. While running inside the legacy shell's content
  // iframe, the legacy left nav posts `{ type: 'mk-nav', section }` to switch
  // the visible view, so the old navigation controls the new content pane.
  useEffect(() => {
    if (!EMBED_CONFIG.embed) {
      return;
    }
    const onMessage = (ev: MessageEvent) => {
      const data = ev.data as unknown;
      if (
        data &&
        typeof data === 'object' &&
        (data as { type?: unknown }).type === 'mk-nav' &&
        typeof (data as { section?: unknown }).section === 'string'
      ) {
        const next = (data as { section: string }).section;
        if (!SHELL_SECTIONS.some((s) => s.id === next)) {
          return;
        }
        // Consume the one-shot echo a guarded "stay" produces when it restores
        // the legacy hash to the section we are still on.
        if (suppressNavSectionRef.current === next) {
          suppressNavSectionRef.current = null;
          return;
        }
        const goToSection = () => {
          setActiveId(next);
          setNavKey((k) => k + 1);
        };
        const intercepted = consultNavigationGuard({
          section: next,
          proceed: goToSection,
          cancel: () => {
            // Restore the legacy hash + nav-rail highlight to the section still
            // shown, and ignore the echo `mk-nav` that restore re-posts.
            const current = activeIdRef.current;
            suppressNavSectionRef.current = current;
            try {
              if (window.parent && window.parent !== window) {
                window.parent.location.hash = shellSectionHash(current as ShellSectionId);
              }
            } catch {
              // Cross-origin or detached parent — nothing we can safely do.
            }
            // Safety net: if the echo never arrives, do not leave a stale
            // suppression that would swallow a later genuine navigation.
            window.setTimeout(() => {
              if (suppressNavSectionRef.current === current) {
                suppressNavSectionRef.current = null;
              }
            }, 500);
          },
        });
        if (intercepted) {
          return;
        }
        goToSection();
      }
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, []);

  useEffect(() => {
    if (importStartedRef.current) {
      return;
    }
    const ticketId = readImportTicketId();
    if (!ticketId) {
      return;
    }
    importStartedRef.current = true;

    let cancelled = false;
    let timer: number | undefined;

    setActiveId('recipes');
    setImportBanner({ phase: 'checking' });

    const attempt = async (): Promise<void> => {
      if (cancelled) {
        return;
      }
      const lockState = await fetchLockState();
      if (cancelled) {
        return;
      }
      if (lockState !== 'Unlocked') {
        setImportBanner({ phase: 'locked' });
        timer = window.setTimeout(() => void attempt(), LOCK_POLL_INTERVAL_MS);
        return;
      }

      setImportBanner({ phase: 'consuming' });
      const outcome = await consumeImport(ticketId);
      if (cancelled) {
        return;
      }

      switch (outcome.status) {
        case 'paxlite': {
          // Validate the recipe text here, before declaring success. An
          // unreadable file must surface a bounded invalid-file message rather
          // than silently failing in the builder and leaving the previous (or
          // default) recipe on screen, which looks like a valid import.
          const parsed = parseLiteRecipeJson(outcome.text);
          if (!parsed.ok) {
            const message =
              parsed.errors.length > 0
                ? parsed.errors[0]
                : 'PAX Cookbook could not read this recipe file.';
            setImportBanner({ phase: 'invalid', fileName: outcome.fileName, message });
            clearImportTicketFromUrl();
            break;
          }
          setPendingImport({ kind: 'paxlite', fileName: outcome.fileName, text: outcome.text });
          setImportBanner({ phase: 'done', fileName: outcome.fileName, source: 'paxlite' });
          clearImportTicketFromUrl();
          break;
        }
        case 'pax': {
          // A full .pax recipe. Validate it here before declaring success: a
          // malformed file shows a bounded invalid message, an envelope this
          // build does not understand shows a clear unsupported-version
          // message, and a valid file lands in the builder as a draft to
          // review. Nothing is saved and nothing runs.
          const result = parseFullPaxRecipeJson(outcome.text);
          if (!result.ok) {
            if (result.reason === 'unsupported-version') {
              setImportBanner({
                phase: 'pax-unsupported-version',
                fileName: outcome.fileName,
                version: result.version,
              });
            } else {
              const message =
                result.errors.length > 0
                  ? result.errors[0]
                  : 'PAX Cookbook could not read this recipe file.';
              setImportBanner({ phase: 'invalid', fileName: outcome.fileName, message });
            }
            clearImportTicketFromUrl();
            break;
          }
          setPendingImport({ kind: 'pax', fileName: outcome.fileName, text: outcome.text });
          setImportBanner({ phase: 'done', fileName: outcome.fileName, source: 'pax' });
          clearImportTicketFromUrl();
          break;
        }
        case 'locked':
          setImportBanner({ phase: 'locked' });
          timer = window.setTimeout(() => void attempt(), LOCK_POLL_INTERVAL_MS);
          break;
        case 'expired':
          setImportBanner({
            phase: 'error',
            message: 'This recipe file link expired. Open the file again to import it.',
          });
          clearImportTicketFromUrl();
          break;
        case 'too-large':
          setImportBanner({
            phase: 'error',
            message: 'That recipe file is too large to import. Nothing was imported.',
          });
          clearImportTicketFromUrl();
          break;
        case 'not-found':
          setImportBanner({
            phase: 'error',
            message: 'That recipe file is no longer available to import.',
          });
          clearImportTicketFromUrl();
          break;
        default:
          setImportBanner({ phase: 'error', message: outcome.message });
          clearImportTicketFromUrl();
          break;
      }
    };

    void attempt();

    return () => {
      cancelled = true;
      if (timer !== undefined) {
        window.clearTimeout(timer);
      }
    };
  }, []);

  const dismissBanner = () => setImportBanner({ phase: 'idle' });

  const handleCloseCancel = () => setCloseModalOpen(false);
  const handleCloseMinimize = () => {
    setCloseModalOpen(false);
    postCloseDecision('cookbook:minimize-to-tray');
  };
  const handleCloseApp = () => {
    // Leave the modal up to avoid a visible flash; the shell tears the window
    // down once it receives the decision.
    //
    // "Exit" must stop EVERYTHING — including a separate background broker
    // daemon this window may be ATTACHED to (closing the window alone leaves the
    // daemon serving). Ask the broker to shut down first, then tell the native
    // shell to close the window. The request is best-effort and time-bounded so
    // a slow, unreachable, or already-stopped broker never blocks the exit.
    const closeWindow = () => postCloseDecision('cookbook:close-app');
    if (!hasHostBridge()) {
      closeWindow();
      return;
    }
    const timeout = new Promise<void>(resolve => window.setTimeout(resolve, 2500));
    void Promise.race([
      shutdownBroker().then(
        () => undefined,
        () => undefined,
      ),
      timeout,
    ]).finally(closeWindow);
  };

  // Content-only surface for the legacy shell iframe: render just the active
  // section, the import banner, and the close modal. The legacy shell supplies
  // its own header, left nav, and status rail at the top level.
  if (EMBED_CONFIG.embed) {
    return (
      <div className="app app--embed">
        <main className="app-main" id="shell-main" style={accentStyle(active.accent)}>
          <img
            className="app-main__decor"
            src="/images/pax-cookbook-mixing-bowl.png"
            alt=""
            aria-hidden="true"
          />
          <div className="app-main__content">
            <ImportHandoffBanner state={importBanner} onDismiss={dismissBanner} />
            <Fragment key={sectionKey}>{active.body}</Fragment>
          </div>
        </main>

        <CloseConfirmModal
          open={closeModalOpen}
          onCancel={handleCloseCancel}
          onMinimize={handleCloseMinimize}
          onCloseApp={handleCloseApp}
          bakeRunning={bakeRunning}
        />
      </div>
    );
  }

  return (
    <div className="app">
      <a className="shell-skip" href="#shell-main">
        Skip to content
      </a>

      <header className="app-header">
        <div className="app-brand">
          <picture>
            <source
              srcSet="/images/pax-cookbook-logo-horizontal-white.png"
              media="(prefers-color-scheme: dark)"
            />
            <img
              className="app-brand__logo"
              src="/images/pax-cookbook-logo-horizontal-blue.png"
              alt="PAX Cookbook"
            />
          </picture>
          <span className="app-brand__sub">Product shell</span>
        </div>
        <div className="app-header__spacer" />
        <span className="shell-badge">
          <span className="shell-badge__dot" aria-hidden="true" />
          Internal build
        </span>
      </header>

      <div className="app-body">
        <nav className="app-sidebar" aria-label="Kitchen sections">
          <span className="app-nav__heading" id="nav-heading">
            Kitchen
          </span>
          {SHELL_SECTIONS.filter((section) => !section.hideFromNav).map((section) => (
            <button
              key={section.id}
              type="button"
              className="nav-item"
              style={accentStyle(section.accent)}
              aria-current={section.id === activeId ? 'page' : undefined}
              onClick={() => {
                const next = section.id;
                const goToSection = () => {
                  setActiveId(next);
                  setNavKey((k) => k + 1);
                };
                const intercepted = consultNavigationGuard({
                  section: next,
                  proceed: goToSection,
                  cancel: () => {
                    // Standalone chrome: nothing to restore (no legacy hash).
                  },
                });
                if (!intercepted) {
                  goToSection();
                }
              }}
            >
              <span className="nav-item__swatch" aria-hidden="true" />
              <span className="nav-item__label">{section.label}</span>
            </button>
          ))}
        </nav>

        <main className="app-main" id="shell-main" style={accentStyle(active.accent)}>
          <img
            className="app-main__decor"
            src="/images/pax-cookbook-mixing-bowl.png"
            alt=""
            aria-hidden="true"
          />
          <div className="app-main__content">
            <ImportHandoffBanner state={importBanner} onDismiss={dismissBanner} />
            <Fragment key={sectionKey}>{active.body}</Fragment>
          </div>
        </main>

        <aside className="app-rail" aria-label="Kitchen status">
          <div className="rail-card">
            <h2 className="rail-card__title">Kitchen status</h2>
            <div className="rail-row">
              <span className="rail-row__key">Mode</span>
              <span className="rail-row__val">Internal build</span>
            </div>
            <div className="rail-row">
              <span className="rail-row__key">Cooking</span>
              <span className="rail-row__val">Available in Recipes</span>
            </div>
            <div className="rail-row">
              <span className="rail-row__key">Saved recipes</span>
              <span className="rail-row__val">—</span>
            </div>
          </div>
          <div className="rail-card">
            <h2 className="rail-card__title">About this build</h2>
            <p className="rail-note">
              In Recipes you can build, save, import, export, and check the readiness of recipes.
              To run a recipe, open it in Recipes, choose Edit, then use Bake (run) - it runs PAX on
              this PC only after you confirm and complete Windows Hello. Taste Tests check readiness
              only and never run PAX; Scheduling is coming in a later build.
            </p>
          </div>
        </aside>
      </div>

      <CloseConfirmModal
        open={closeModalOpen}
        onCancel={handleCloseCancel}
        onMinimize={handleCloseMinimize}
        onCloseApp={handleCloseApp}
        bakeRunning={bakeRunning}
      />
    </div>
  );
}

interface ImportHandoffBannerProps {
  state: ImportBannerState;
  onDismiss: () => void;
}

function ImportHandoffBanner({ state, onDismiss }: ImportHandoffBannerProps): ReactElement | null {
  if (state.phase === 'idle') {
    return null;
  }

  let tone: 'info' | 'success' | 'warn' | 'error' = 'info';
  let title = '';
  let detail = '';
  let dismissible = false;

  switch (state.phase) {
    case 'checking':
      title = 'Opening recipe file…';
      detail = 'Getting your kitchen ready.';
      break;
    case 'locked':
      title = 'Waiting for sign-in';
      detail = 'Unlock PAX Cookbook to finish opening this recipe file.';
      break;
    case 'consuming':
      title = 'Importing recipe…';
      detail = 'Reading the recipe file.';
      break;
    case 'done':
      tone = 'success';
      if (state.source === 'pax') {
        title = 'Full PAX recipe ready to review';
        detail = `Imported from full PAX recipe: ${state.fileName}. Review the details below, then save the recipe to your cookbook.`;
      } else {
        title = 'Recipe ready to review';
        detail = `Imported ${state.fileName} as a draft below. Review it, then save it to your cookbook.`;
      }
      dismissible = true;
      break;
    case 'invalid':
      tone = 'error';
      title = 'This recipe file can’t be opened';
      detail = `${state.fileName} couldn’t be read. ${state.message} Nothing was imported.`;
      dismissible = true;
      break;
    case 'pax-unsupported-version':
      tone = 'warn';
      title = 'This PAX recipe needs a newer PAX Cookbook';
      detail = `${state.fileName} is a full PAX recipe saved with schema version ${state.version}, which this build of PAX Cookbook can’t open. Update PAX Cookbook to import it. Nothing was imported or changed.`;
      dismissible = true;
      break;
    case 'error':
      tone = 'error';
      title = 'Couldn’t open the recipe file';
      detail = state.message;
      dismissible = true;
      break;
  }

  return (
    <div className={`import-banner import-banner--${tone}`} role="status" aria-live="polite">
      <div className="import-banner__text">
        <span className="import-banner__title">{title}</span>
        <span className="import-banner__detail">{detail}</span>
      </div>
      {dismissible ? (
        <button
          type="button"
          className="import-banner__dismiss"
          onClick={onDismiss}
          aria-label="Dismiss"
        >
          ×
        </button>
      ) : null}
    </div>
  );
}

export default App;
