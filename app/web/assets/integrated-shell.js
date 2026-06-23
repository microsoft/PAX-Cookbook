// Integrated shell controller.
//
// The legacy app chrome -- top bar, left navigation rail, help panel, lock
// overlay, close modal, and status rail -- stays at the top level. The main
// content area hosts a single same-origin iframe that renders the React
// Mini-Kitchen content surface in its content-only (embed) mode. The legacy
// left nav drives which section the iframe shows, so the familiar navigation
// controls the rebuilt content pane.
//
// Navigation switches the visible section by posting a message to the iframe
// rather than reloading it, so the React surface keeps its state (including an
// in-progress recipe import) as the operator moves between sections.
(function () {
    'use strict';

    var FRAME_ID = 'mk-content-frame';

    // Legacy nav hash -> React shell section id.
    var HASH_TO_SECTION = {
        '#/home': 'home',
        '#/pantry': 'pantry',
        '#/recipes': 'recipes',
        '#/bakes': 'bakes',
        '#/taste-tests': 'tastetests',
        '#/keys': 'chefskeys',
        '#/settings': 'settings',
        '#/updates': 'updates'
    };
    var DEFAULT_HASH = '#/home';

    var frame = null;
    var frameReady = false;
    var loadedSection = null;
    var desiredSection = null;

    function sectionForHash(hash) {
        return Object.prototype.hasOwnProperty.call(HASH_TO_SECTION, hash)
            ? HASH_TO_SECTION[hash]
            : null;
    }

    function buildFrameSrc(section, importId) {
        var src = 'app/index.html?embed=1&section=' + encodeURIComponent(section);
        if (importId) {
            src += '&import=' + encodeURIComponent(importId);
        }
        return src;
    }

    function updateNavCurrent(hash) {
        var items = document.querySelectorAll('.nav-item');
        for (var i = 0; i < items.length; i++) {
            var item = items[i];
            if (item.getAttribute('href') === hash) {
                item.setAttribute('aria-current', 'page');
            } else {
                item.removeAttribute('aria-current');
            }
        }
        var updateLink = document.getElementById('nav-rail-update-link');
        if (updateLink) {
            if (hash === '#/updates') {
                updateLink.setAttribute('aria-current', 'page');
            } else {
                updateLink.removeAttribute('aria-current');
            }
        }
    }

    function postSection(section) {
        if (section === loadedSection) {
            return;
        }
        if (frame && frame.contentWindow) {
            try {
                frame.contentWindow.postMessage(
                    { type: 'mk-nav', section: section },
                    window.location.origin
                );
            } catch (e) {
                /* cross-document post failures are non-fatal */
            }
        }
        loadedSection = section;
    }

    function applyHash(hash) {
        var canonical = sectionForHash(hash) ? hash : DEFAULT_HASH;
        var section = sectionForHash(canonical) || 'home';
        desiredSection = section;
        updateNavCurrent(canonical);
        if (frameReady) {
            postSection(section);
        }
    }

    function setText(id, text) {
        var el = document.getElementById(id);
        if (el) {
            el.textContent = text;
        }
    }

    // Footer status summary mirrors the same read-only rail signals in the
    // full-width strip beneath the workspace. The dot tone follows overall
    // health; the text never exposes a folder path or triggers any action.
    //
    // The latest health-derived state is remembered so the "Update available"
    // overlay (set by the embedded app's startup check) and the health state
    // never clobber each other.
    var lastFooterText = '';
    var lastFooterTone = 'ok';
    var lastWorkspaceText = 'Checking\u2026';
    var updateAvailable = false;

    function setFooterState(text, tone) {
        lastFooterText = text;
        lastFooterTone = tone || 'ok';
        renderFooter();
    }

    // Navigate the integrated shell to the Updates page via its own hash router.
    function openUpdatesSection() {
        try { window.location.hash = '#/updates'; } catch (e) { /* no-op */ }
    }

    // Render the footer status. When an update is available it takes over with a
    // distinct orange dot and a clickable "Update available" reminder that opens
    // the Updates page; otherwise it shows the latest health state. The overlay
    // persists for the whole session and is never cleared by a health poll -- a
    // restart (an applied update relaunches the app) is what resets it.
    function renderFooter() {
        var state = document.getElementById('app-footer-state');
        var dot = document.getElementById('app-footer-dot');
        if (dot) {
            dot.classList.remove('is-warning', 'is-danger', 'is-update');
        }
        if (updateAvailable) {
            if (state) {
                state.textContent = 'Update available';
                state.classList.add('app-footer-state--link');
                state.setAttribute('role', 'link');
                state.setAttribute('tabindex', '0');
                state.setAttribute('title', 'Open the Updates page');
                state.onclick = openUpdatesSection;
                state.onkeydown = function (e) {
                    if (e && (e.key === 'Enter' || e.key === ' ')) {
                        e.preventDefault();
                        openUpdatesSection();
                    }
                };
            }
            if (dot) { dot.classList.add('is-update'); }
            return;
        }
        if (state) {
            if (lastFooterText) { state.textContent = lastFooterText; }
            state.classList.remove('app-footer-state--link');
            state.removeAttribute('role');
            state.removeAttribute('tabindex');
            state.removeAttribute('title');
            state.onclick = null;
            state.onkeydown = null;
        }
        if (dot) {
            if (lastFooterTone === 'warning') { dot.classList.add('is-warning'); }
            else if (lastFooterTone === 'danger') { dot.classList.add('is-danger'); }
        }
        renderWorkspaceFooter();
    }

    // The bottom-right "Workspace:" status. When an update is available it reads
    // "Update available" and becomes a clickable link to the Updates page;
    // otherwise it shows the latest workspace-readiness text. The app/engine
    // version values next to it always reflect the installed build and are not
    // touched here.
    function renderWorkspaceFooter() {
        var el = document.getElementById('app-footer-workspace');
        if (!el) { return; }
        if (updateAvailable) {
            el.textContent = 'Update available';
            el.classList.add('app-footer-state--link');
            el.setAttribute('role', 'link');
            el.setAttribute('tabindex', '0');
            el.setAttribute('title', 'Open the Updates page');
            el.onclick = openUpdatesSection;
            el.onkeydown = function (e) {
                if (e && (e.key === 'Enter' || e.key === ' ')) {
                    e.preventDefault();
                    openUpdatesSection();
                }
            };
        } else {
            el.textContent = lastWorkspaceText;
            el.classList.remove('app-footer-state--link');
            el.removeAttribute('role');
            el.removeAttribute('tabindex');
            el.removeAttribute('title');
            el.onclick = null;
            el.onkeydown = null;
        }
    }

    // Exposed to the embedded React app (via window.parent) so its startup
    // update-check can light the footer's persistent "Update available"
    // reminder. Dismissing the startup modal does NOT clear it; only a restart
    // (after applying the update) does.
    window.paxShellSetUpdateAvailable = function (v) {
        updateAvailable = !!v;
        renderFooter();
    };

    // Footer PAX value shows the BUNDLED engine version that ships with this
    // app -- the same value the Settings cards and About section present
    // (expected.paxScriptVersion, sourced from VERSION.json). Runtime
    // acquisition state is surfaced on Home and Settings, not folded into the
    // engine version shown here.
    function derivePaxRail(resp) {
        if (!resp || !resp.ok || !resp.body) {
            return null;
        }
        var b = resp.body;
        var ver = (b.expected && b.expected.paxScriptVersion)
            ? String(b.expected.paxScriptVersion)
            : null;
        return ver;
    }

    // Workspace rail value reflects whether the app's storage is ready to use.
    // A responding broker has already resolved and created its workspace during
    // startup, so a healthy response means the workspace is ready. The rail
    // shows a plain status word -- never the underlying folder path, which is a
    // technical detail that belongs in support diagnostics, not the nav rail.
    function deriveWorkspaceRail(resp) {
        if (resp && resp.ok && resp.body &&
            typeof resp.body.workspaceFolderPath === 'string' &&
            resp.body.workspaceFolderPath.length > 0) {
            return 'Ready';
        }
        return 'Needs attention';
    }

    // Populate the footer status values. These are read-only reflections of
    // existing broker state (app version, workspace readiness, PAX engine
    // status); nothing here writes or triggers any cooking action.
    function populateNavRail() {
        var api = window.cookbookApi;
        if (!api || typeof api.get !== 'function') {
            return;
        }
        api.get('/api/v1/runtime/version').then(function (resp) {
            if (resp && resp.ok && resp.body) {
                var v = resp.body;
                var appVer = (v.cookbook && v.cookbook.version)
                    ? v.cookbook.version
                    : (v.cookbookVersion || null);
                if (appVer) {
                    setText('app-footer-app', 'App ' + String(appVer));
                }
            }
        }).catch(function () { /* footer keeps its default text on failure */ });

        api.get('/api/v1/health').then(function (resp) {
            var ready = deriveWorkspaceRail(resp) === 'Ready';
            lastWorkspaceText = deriveWorkspaceRail(resp);
            renderWorkspaceFooter();
            if (resp && resp.ok) {
                setFooterState('All systems nominal', 'ok');
            } else {
                setFooterState('Attention needed', ready ? 'warning' : 'danger');
            }
        }).catch(function () {
            lastWorkspaceText = 'Needs attention';
            renderWorkspaceFooter();
            setFooterState('Server unreachable', 'danger');
        });

        api.get('/api/v1/setup/acquire-pax/state').then(function (resp) {
            var pax = derivePaxRail(resp);
            if (pax) {
                setText('app-footer-pax', 'PAX Engine ' + pax);
            }
        }).catch(function () { /* footer keeps its default text on failure */ });
    }

    function init() {
        frame = document.getElementById(FRAME_ID);
        if (!frame) {
            return;
        }

        var search = '';
        try {
            search = window.location.search || '';
        } catch (e) {
            search = '';
        }
        var importId = null;
        try {
            importId = new URLSearchParams(search).get('import');
        } catch (e) {
            importId = null;
        }

        // A file-open handoff arrives at the top-level shell with `?import=<id>`.
        // Route the embedded surface to Recipes and forward the one-time ticket
        // into the iframe, which consumes it the same way the standalone surface
        // did. Other launches start on Home (or the requested hash).
        var startHash;
        if (importId) {
            startHash = '#/recipes';
        } else {
            startHash = sectionForHash(window.location.hash) ? window.location.hash : DEFAULT_HASH;
        }
        var startSection = sectionForHash(startHash) || 'home';
        loadedSection = startSection;
        desiredSection = startSection;

        frame.addEventListener('load', function () {
            frameReady = true;
            if (desiredSection && desiredSection !== loadedSection) {
                postSection(desiredSection);
            }
        });

        frame.src = buildFrameSrc(startSection, importId);

        // Drop the consumed import ticket from the top-level URL so a manual
        // reload does not attempt to re-open an already-consumed file.
        if (importId) {
            try {
                window.history.replaceState(null, '', window.location.pathname + startHash);
            } catch (e) {
                /* history rewrite is best-effort */
            }
        }

        if (window.location.hash !== startHash) {
            window.location.hash = startHash;
        }
        updateNavCurrent(startHash);

        window.addEventListener('hashchange', function () {
            applyHash(window.location.hash);
        });

        // A nav-rail click always re-selects its section in the embedded
        // surface, even when the hash and loaded section are unchanged. The
        // hash router only reacts to hash CHANGES and same-section posts are
        // de-duplicated, so clicking the current section (for example Recipes
        // while a recipe is open) would otherwise be inert. This delegated
        // handler posts the section with a reselect flag straight to the frame
        // so the surface can return to that section's default view (the recipe
        // list) on an explicit click. Cross-section clicks still flow through
        // the hash router; this message is additive and idempotent.
        document.addEventListener('click', function (ev) {
            var target = ev.target;
            var item = (target && target.closest) ? target.closest('.nav-item') : null;
            if (!item) {
                return;
            }
            var section = sectionForHash(item.getAttribute('href'));
            if (!section) {
                return;
            }
            if (frameReady && frame && frame.contentWindow) {
                try {
                    frame.contentWindow.postMessage(
                        { type: 'mk-nav', section: section, reselect: true },
                        window.location.origin
                    );
                } catch (e) {
                    /* cross-document post failures are non-fatal */
                }
            }
        });

        populateNavRail();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }
})();
