// PAX Cookbook -- tiny hash router.
//
// Owns:
//   - parsing window.location.hash into one of a small, hard-coded set
//     of routes
//   - asking the matching page module to mount() into #page-root
//   - asking the previous page module to teardown() before swapping
//
// Does NOT own:
//   - any visual state (CSS classes other than aria-current on .nav-item)
//   - any data fetches (pages own their own fetches)
//   - any history beyond replaceState normalisation of the URL bar
//
// What this router intentionally is NOT, by design and by user contract:
//   - a navigation framework
//   - a route registry with metadata
//   - middleware / guards / transition pipelines / lifecycle observers
//   - a page cache / keepalive / hidden retained state
//   - an event bus
//   - an async routing system
//
// Adding a route = one explicit branch in resolveRoute() below. That
// is the entire extension story.

(function () {
    'use strict';

    var DEFAULT_ROUTE = '#/home';
    var PAGE_ROOT_ID  = 'page-root';

    // Alias table: legacy URL fragments that map onto a single redesigned
    // route. Aliases are rewritten into their canonical target BEFORE
    // resolution so the active nav highlight tracks the new label
    // ('Bakes', 'Chef's Keys') even when the operator landed on the
    // legacy fragment from a bookmark, an external launcher, or a
    // historic deep link. The rewrite is silent (replaceState) so no
    // hashchange ping-pong is generated. Each entry is an explicit
    // string-pair or a /regex/ + replacement function pair -- there is
    // intentionally no general-purpose rewrite engine.
    function rewriteAlias(bareHash) {
        if (bareHash === '#/cooks')         { return '#/bakes'; }
        if (bareHash === '#/auth-profiles') { return '#/keys';  }
        var mCookAlias = /^#\/cooks\/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$/.exec(bareHash);
        if (mCookAlias) { return '#/bakes/' + mCookAlias[1].toLowerCase(); }
        return null;
    }

    // Route resolution.
    //
    // Each branch returns { canonical, pageGetter, params } or null.
    // First match wins. Adding a route = one explicit branch here.
    //
    // The pageGetter is invoked at dispatch time, not parse time, so
    // script load order is not a hard ordering constraint.
    //
    // Every page module must expose:
    //   .mount(container, params)  -- builds DOM into container
    //   .teardown()                -- drops listeners, refs, in-flight work
    function resolveRoute(bareHash) {
        if (bareHash === '#/home') {
            return {
                canonical:  '#/home',
                pageGetter: function () { return window.cookbookHomePage; },
                params:     {}
            };
        }
        if (bareHash === '#/recipes') {
            return {
                canonical:  '#/recipes',
                pageGetter: function () { return window.cookbookRecipesPage; },
                params:     {}
            };
        }
        if (bareHash === '#/recipes/new') {
            return {
                canonical:  '#/recipes/new',
                pageGetter: function () { return window.cookbookRecipeEditorPage; },
                params:     { recipeId: null }
            };
        }
        var m = /^#\/recipes\/([A-Za-z0-9_\-]+)$/.exec(bareHash);
        if (m) {
            return {
                canonical:  '#/recipes/' + m[1],
                pageGetter: function () { return window.cookbookRecipeEditorPage; },
                params:     { recipeId: m[1] }
            };
        }
        // Bakes (renamed surface for the cooks list/detail). The legacy
        // '#/cooks' and '#/cooks/<uuid>' fragments alias into these
        // branches via rewriteAlias() above, so existing bookmarks and
        // launcher hand-offs remain stable.
        var mBake = /^#\/bakes\/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$/.exec(bareHash);
        if (mBake) {
            return {
                canonical:  '#/bakes/' + mBake[1].toLowerCase(),
                pageGetter: function () { return window.cookbookCookViewPage; },
                params:     { cookId: mBake[1].toLowerCase() }
            };
        }
        if (bareHash === '#/bakes') {
            return {
                canonical:  '#/bakes',
                pageGetter: function () { return window.cookbookCooksListPage; },
                params:     {}
            };
        }
        // Taste Tests: navigation slot exists in the rail; the dedicated
        // page module renders only a calm 'coming soon' placeholder
        // for this checkpoint. The full Taste Test workflow is
        // explicitly deferred to a later UX slice.
        if (bareHash === '#/taste-tests') {
            return {
                canonical:  '#/taste-tests',
                pageGetter: function () { return window.cookbookTasteTestsPage; },
                params:     {}
            };
        }
        // Pantry list view (#/pantry) and bundled-template detail view
        // (#/pantry/<templateId>). One module owns both states. The
        // templateId pattern mirrors the server-side
        // $Script:TemplateIdPattern from app/broker/Routes/TemplateValidator.ps1
        // (lowercase ASCII; hyphenated; bounded length). The detail
        // route only PARSES the id from the URL -- it does not verify
        // existence; the page module handles the not-found case via the
        // server's 404 response.
        if (bareHash === '#/pantry') {
            return {
                canonical:  '#/pantry',
                pageGetter: function () { return window.cookbookPantryPage; },
                params:     { templateId: null }
            };
        }
        var mTpl = /^#\/pantry\/([a-z][a-z0-9\-]{1,62}[a-z0-9])$/.exec(bareHash);
        if (mTpl) {
            return {
                canonical:  '#/pantry/' + mTpl[1],
                pageGetter: function () { return window.cookbookPantryPage; },
                params:     { templateId: mTpl[1] }
            };
        }
        if (bareHash === '#/settings') {
            return {
                canonical:  '#/settings',
                pageGetter: function () { return window.cookbookSettingsPage; },
                params:     {}
            };
        }
        // Chef's Keys (renamed surface for the auth-profiles page).
        // The legacy '#/auth-profiles' fragment aliases into '#/keys'
        // via rewriteAlias() above.
        if (bareHash === '#/keys') {
            return {
                canonical:  '#/keys',
                pageGetter: function () { return window.cookbookAuthProfilesPage; },
                params:     {}
            };
        }
        return null;
    }

    // Module-scoped state. Exactly one page is mounted at a time.
    var container    = null;
    var currentRoute = null;
    var currentPage  = null;

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    function normalize(hash) {
        if (!hash || hash === '#' || hash === '#/') {
            return DEFAULT_ROUTE;
        }
        // Strip any query string portion of the hash (not used in M1,
        // but inert if a future caller appends '?foo=bar').
        var bare  = hash.split('?')[0];
        // Apply the legacy-alias rewrite (e.g. '#/cooks' -> '#/bakes')
        // BEFORE resolution so the active nav highlight, dispatched
        // page, and canonical URL bar all reflect the redesigned label
        // instead of the legacy fragment.
        var aliased = rewriteAlias(bare);
        if (aliased) { bare = aliased; }
        var match = resolveRoute(bare);
        return match ? match.canonical : null;
    }

    function setHashSilently(newHash) {
        // replaceState (vs. assigning location.hash) so we do not push
        // history entries and do not fire a hashchange event.
        var url = window.location.pathname + window.location.search + newHash;
        history.replaceState(null, '', url);
    }

    function updateNavCurrent(route) {
        // A nav-rail item is current when the active route matches its
        // href exactly, OR when the active route is a path under it
        // (e.g. nav 'Recipes' (#/recipes) stays current for #/recipes/new
        // and #/recipes/<id>).
        var items = document.querySelectorAll('.nav-item');
        for (var i = 0; i < items.length; i++) {
            var item = items[i];
            var href = item.getAttribute('href');
            var isCurrent = href && (route === href || route.indexOf(href + '/') === 0);
            if (isCurrent) {
                item.setAttribute('aria-current', 'page');
            } else {
                item.removeAttribute('aria-current');
            }
        }
    }

    function clearContainer() {
        if (!container) { return; }
        while (container.firstChild) {
            container.removeChild(container.firstChild);
        }
    }

    function teardownCurrent() {
        if (currentPage && typeof currentPage.teardown === 'function') {
            try {
                currentPage.teardown();
            } catch (err) {
                console.error('cookbookRouter: teardown error', err);
            }
        }
        currentPage  = null;
        currentRoute = null;
        clearContainer();
    }

    // ----------------------------------------------------------------
    // Dispatch
    // ----------------------------------------------------------------

    function dispatch() {
        if (!container) { return; }

        var requested = normalize(window.location.hash);

        if (requested === null) {
            // Unknown hash. Rewrite the URL bar to the default route
            // and continue dispatching to the default. (replaceState
            // does not fire hashchange, so we fall through directly
            // instead of awaiting an event.)
            requested = DEFAULT_ROUTE;
            setHashSilently(requested);
        } else if (window.location.hash !== requested) {
            // Empty or partial hash on initial load -- normalise the URL
            // bar so it reflects the dispatched route.
            setHashSilently(requested);
        }

        if (requested === currentRoute && currentPage) {
            // Same route as already mounted -- nothing to do.
            return;
        }

        teardownCurrent();

        var match = resolveRoute(requested);
        var page = match ? match.pageGetter() : null;
        if (!page || typeof page.mount !== 'function') {
            console.error('cookbookRouter: page module missing or invalid for ' + requested);
            return;
        }

        try {
            page.mount(container, match.params);
        } catch (err) {
            console.error('cookbookRouter: mount error for ' + requested, err);
            return;
        }

        currentRoute = requested;
        currentPage  = page;
        updateNavCurrent(requested);
        focusPageRoot();
    }

    // Phase U: keyboard / SR ergonomics. After every successful route
    // dispatch we move focus to the page root so that:
    //   1) Screen readers announce the new page contents.
    //   2) The user does not have to tab through the nav rail again
    //      to reach the page body.
    //
    // The page root is given tabindex="-1" by index.html so it can
    // receive programmatic focus without entering the natural tab
    // order. We do NOT scroll: the browser's default scroll on focus
    // would be disruptive for in-place updates (e.g. cook-view auto-
    // refresh). preventScroll keeps the operator's viewport stable.
    function focusPageRoot() {
        if (!container) { return; }
        try {
            container.focus({ preventScroll: true });
        } catch (_e) {
            // Some older user-agents reject the options bag; fall back
            // to the no-argument form, which is still bounded because
            // the focus target is just #page-root.
            try { container.focus(); } catch (_e2) { /* ignore */ }
        }
    }

    // ----------------------------------------------------------------
    // Boot
    // ----------------------------------------------------------------

    function init() {
        container = document.getElementById(PAGE_ROOT_ID);
        if (!container) {
            console.error('cookbookRouter: #' + PAGE_ROOT_ID + ' not found');
            return;
        }
        window.addEventListener('hashchange', dispatch);
        dispatch();
    }

    // Minimal external surface. No register() API by design -- the
    // resolveRoute() function is the source of truth and lives inside
    // this file.
    window.cookbookRouter = {
        dispatch: dispatch
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }
})();
