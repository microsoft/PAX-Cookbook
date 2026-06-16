// PAX Cookbook -- recipe list page.
//
// Page module for route '#/recipes'. Owns the DOM under #page-root for
// the duration it is mounted; on teardown, drops its listeners, signals
// in-flight responses to discard themselves, and releases its refs so
// the router can clear the container.
//
// Renders the list of active recipes returned by GET /api/v1/recipes,
// in server order (created_at DESC). Explicit, request-driven.
//
// No polling. No auto-refresh. No client-side sorting. No client-side
// filtering. No virtualization. No reactive state. No optimistic UI.
//
// User-visible state transitions are only triggered by:
//   - mount (page enters)
//   - the Reload button click
(function () {
    'use strict';

    // Page-owned IDs (built inside #page-root on mount, gone on teardown).
    var STATUS_EL_ID    = 'recipe-status';
    var TABLE_EL_ID     = 'recipe-table';
    var TBODY_EL_ID     = 'recipe-table-body';
    var BANNER_EL_ID    = 'recipe-list-banner';
    // Phase M (onboarding hardening): structured empty-state panel
    // rendered when the recipes list is empty. Built into #page-root
    // alongside the status element by mount; removed on teardown.
    // Static DOM; no fetches; no listeners beyond plain anchor clicks.
    var ONBOARDING_EL_ID = 'recipe-onboarding';

    // F2E -- Recipe Takeout import UI ids.
    //
    // The import action button lives in the page-header .page-actions
    // strip. The wizard is a single modal whose internal state moves
    // through three views: file picker, validation preview + name
    // confirmation, success. There is no separate "step indicator"
    // -- the active view is determined by which sub-panel is shown.
    var IMPORT_BTN_ID         = 'recipe-takeout-import-btn';
    var IMPORT_MODAL_ID       = 'recipe-takeout-import-modal';
    var IMPORT_FILE_INPUT_ID  = 'recipe-takeout-import-file';
    var IMPORT_NAME_INPUT_ID  = 'recipe-takeout-import-name';

    // Endpoint constants. Reading these as named constants keeps the
    // contract visible to the static smoke (which greps for the path
    // verbatim) and prevents accidental drift to the old / unused
    // /api/v1/recipes/takeout/* prefix. Two parallel endpoint pairs:
    // one for full Cookbook Recipe Takeout envelopes (cookbook class)
    // and one for Mini-Kitchen lite recipe envelopes (lite class).
    var TAKEOUT_VALIDATE_PATH = '/api/v1/recipe-takeout/validate';
    var TAKEOUT_IMPORT_PATH   = '/api/v1/recipe-takeout/import';
    var LITE_VALIDATE_PATH    = '/api/v1/recipe-lite/validate';
    var LITE_IMPORT_PATH      = '/api/v1/recipe-lite/import';

    // Envelope kind constants used to classify a parsed import file
    // into either the Cookbook class or the Mini-Kitchen lite class
    // BEFORE we post it to the broker. The broker re-validates kind
    // on every call; this client-side classification just routes the
    // POST to the right endpoint pair.
    var COOKBOOK_ENVELOPE_KIND = 'pax-cookbook.recipe-takeout';
    var LITE_ENVELOPE_KIND     = 'pax-cookbook-mini-recipe';

    // Shell-owned IDs (live in the topbar; the page reads/writes their
    // text/className but does NOT create or remove them).
    var BROKER_HOST_ID  = 'broker-host';
    var TOKEN_STATUS_ID = 'token-status';
    var LAST_FETCHED_ID = 'last-fetched';
    var RELOAD_BTN_ID   = 'reload-button';

    // Static markup for the page body. Built into #page-root by mount.
    var PAGE_TEMPLATE = [
        '<section class="page recipe-section">',
            '<header class="page-header">',
                '<div class="page-header-text">',
                    '<h1 class="page-title">Recipes<button type="button" class="help-hook" data-help-topic="recipes.intro" aria-label="Help: About recipes" title="About recipes"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h1>',
                    // Phase M: deterministic, static page-lede. Explains what
                    // a Recipe is and where it comes from. No marketing copy;
                    // no AI hype; no dynamic interpolation.
                    '<p class="page-lede">A recipe is the editable execution plan for one PAX query. Recipes are created by materializing a bundled template from the Pantry, then edited here before each run.</p>',
                '</div>',
                // F2E: page-actions strip with the single Import Recipe
                // Takeout button. The button opens an in-page modal
                // wizard; the import action posts to the F2D backend
                // import endpoint. This is the ONLY F2E-visible entry
                // point. There is intentionally no Home, Pantry, or
                // Settings placement of this action in this slice.
                '<div class="page-actions">',
                    '<button id="' + IMPORT_BTN_ID + '" type="button" class="btn-ghost">Import Recipe Takeout</button>',
                '</div>',
            '</header>',
            '<div id="recipe-status" class="recipe-status">Loading recipes</div>',
            '<div id="recipe-list-banner" class="editor-banner" hidden></div>',
            // Phase M: empty-state onboarding panel. Hidden by default;
            // revealed only when the server returns an empty recipes
            // list. Plain anchor to #/pantry -- no router preloading,
            // no in-app routing magic, no JS handlers required.
            '<aside id="recipe-onboarding" class="empty-onboarding" hidden aria-labelledby="recipe-onboarding-title">',
                '<h2 id="recipe-onboarding-title" class="empty-onboarding-title">No recipes yet. Here is how to create the first one.</h2>',
                '<p class="empty-onboarding-desc">Cookbook does not auto-create recipes, auto-discover queries, or run anything on launch. Each step below is an explicit chef action, in order.</p>',
                '<ol class="empty-onboarding-steps">',
                    '<li class="empty-onboarding-step"><strong>Open the <a class="empty-onboarding-link" href="#/pantry">Pantry</a>.</strong> The Pantry lists the audit-log templates bundled with this Cookbook install. Templates are read-only curated blueprints.</li>',
                    '<li class="empty-onboarding-step"><strong>Pick a template and materialize it.</strong> Materialization copies the template into a new editable recipe; the template itself is never executed.</li>',
                    '<li class="empty-onboarding-step"><strong>Review and edit the recipe.</strong> Adjust dates, tenant, output path, and other ingredients. Every field maps 1:1 to a PAX command-line argument.</li>',
                    '<li class="empty-onboarding-step"><strong>Click Preview.</strong> Cookbook renders the exact PAX command and argv that the next bake will use. Nothing executes until you click Bake Recipe.</li>',
                    '<li class="empty-onboarding-step"><strong>Click Bake Recipe to start.</strong> The bake (one execution of the recipe) appears in the Bakes list; its outputs are written to the recipe\u2019s output path.</li>',
                    '<li class="empty-onboarding-step"><strong>Inspect the bake.</strong> Open it from the Bakes list to see the persisted command, argv, recipe snapshot, sentinel files, and output artifacts.</li>',
                '</ol>',
                '<p class="empty-onboarding-note">If the Pantry is also empty, open <a class="empty-onboarding-link" href="#/settings">Settings</a> and review the Integrity card to confirm the broker loaded the bundled templates directory.</p>',
            '</aside>',
            '<table id="recipe-table" class="recipe-table" hidden>',
                '<thead>',
                    '<tr>',
                        '<th scope="col">Name</th>',
                        '<th scope="col">Status</th>',
                        '<th scope="col">Last baked (UTC)</th>',
                        '<th scope="col">Created (UTC)</th>',
                        '<th scope="col">Recipe ID</th>',
                        '<th scope="col">Actions</th>',
                    '</tr>',
                '</thead>',
                '<tbody id="recipe-table-body"></tbody>',
            '</table>',
        '</section>'
    ].join('');

    // Monotonic counter -- bumped on every mount AND on every teardown.
    // An in-flight fetch captures the epoch at request time and discards
    // its render if the live state's epoch has moved on.
    var nextEpoch = 1;
    var state = null;

    // Mirrors the latest cookbook:acquisitionStateChanged event from
    // pax-engine-overlay.js. When true the PAX engine is not yet
    // acquired; the per-row Bake Recipe buttons are disabled with a
    // tooltip so the operator does not have to round-trip a 409 from
    // the broker gate. Module-scoped so the value survives the
    // mount/teardown cycle.
    var acquisitionBlocked = false;

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    function byId(id) { return document.getElementById(id); }

    function setText(id, text) {
        var el = byId(id);
        if (el) { el.textContent = text; }
    }

    function setStatus(text, kind) {
        var el = byId(STATUS_EL_ID);
        if (!el) { return; }
        el.textContent = text;
        el.className = 'recipe-status' + (kind ? ' ' + kind : '');
        el.hidden = false;
    }

    function hideStatus() {
        var el = byId(STATUS_EL_ID);
        if (el) { el.hidden = true; }
    }

    function showTable(show) {
        var t = byId(TABLE_EL_ID);
        if (t) { t.hidden = !show; }
    }

    function showOnboarding(show) {
        // Phase M: reveal or hide the structured empty-state panel.
        // Plain visibility toggle -- no fade, no inline animation, no
        // tween. Visibility is bound to the data state only:
        //   server returned 0 recipes -> show
        //   anything else              -> hide
        var p = byId(ONBOARDING_EL_ID);
        if (p) { p.hidden = !show; }
    }

    function isoNowUtc() {
        return new Date().toISOString();
    }

    function cell(text, klass) {
        var td = document.createElement('td');
        td.textContent = text;
        if (klass) { td.className = klass; }
        return td;
    }

    function formatTime(value) {
        // Server emits ISO UTC strings. Shown verbatim -- no locale
        // conversion, no relative-time. Deterministic and inspectable.
        if (value === null || value === undefined || value === '') { return ''; }
        return String(value);
    }

    function renderEmpty() {
        // Phase M: when the recipes list is empty, hide the terse
        // status banner entirely and show the structured onboarding
        // panel. The panel IS the empty state -- there is no fallback
        // "No recipes yet." string for screen readers to fight with.
        showTable(false);
        hideStatus();
        showOnboarding(true);
    }

    function renderError(message) {
        showTable(false);
        showOnboarding(false);
        setStatus(message, 'error');
    }

    // ----------------------------------------------------------------
    // List-page banner (used for transient cook-trigger feedback).
    // Independent of the status element, which is the empty/error
    // signal for the table itself. Banners can be shown alongside a
    // visible table.
    // ----------------------------------------------------------------
    function hideBanner() {
        var el = byId(BANNER_EL_ID);
        if (!el) { return; }
        el.hidden = true;
        el.className = 'editor-banner';
        while (el.firstChild) { el.removeChild(el.firstChild); }
    }

    function showBanner(kind, text) {
        var el = byId(BANNER_EL_ID);
        if (!el) { return; }
        while (el.firstChild) { el.removeChild(el.firstChild); }
        el.hidden = false;
        el.className = 'editor-banner banner-' + kind;
        el.appendChild(document.createTextNode(text));
    }

    function showCookBusyBanner(activeCookId) {
        var el = byId(BANNER_EL_ID);
        if (!el) { return; }
        while (el.firstChild) { el.removeChild(el.firstChild); }
        el.hidden = false;
        el.className = 'editor-banner banner-info';
        el.appendChild(document.createTextNode('This recipe is already baking.'));
        if (activeCookId) {
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'btn-ghost banner-retry';
            btn.textContent = 'Open running bake';
            btn.addEventListener('click', function (ev) {
                if (ev) { ev.preventDefault(); }
                history.replaceState({}, '', window.location.pathname + '#/cooks/' + activeCookId);
                window.cookbookRouter.dispatch();
            });
            el.appendChild(document.createTextNode(' '));
            el.appendChild(btn);
        }
    }

    // Apply the pin/unpin toggle button's label, accessible name, and
    // pressed state for a given pinned boolean. The single control both
    // shows the current pinned state (star glyph + label) and is the
    // action to flip it -- aria-pressed makes the toggle semantics
    // explicit to assistive tech.
    function applyPinButtonLabel(btn, pinned) {
        btn.textContent = pinned ? '\u2605 Pinned' : '\u2606 Pin';
        btn.setAttribute('data-pinned', pinned ? '1' : '0');
        btn.setAttribute('aria-pressed', pinned ? 'true' : 'false');
        btn.setAttribute('aria-label', pinned ? 'Unpin recipe' : 'Pin recipe');
        btn.title = pinned ? 'Unpin recipe' : 'Pin recipe';
    }

    function actionsCell(recipeId, isPinned) {
        var td = document.createElement('td');
        td.className = 'col-actions';
        if (!recipeId) {
            td.textContent = '\u2014';
            return td;
        }
        var open = document.createElement('a');
        open.href = '#/recipes/' + recipeId;
        open.className = 'row-action row-action-open';
        open.textContent = 'Open';
        // Decorative separator -- hidden from assistive tech so screen
        // readers do not announce the middle dot between the Open link
        // and the Cook button (Phase U).
        var sep = document.createElement('span');
        sep.className = 'row-action-sep';
        sep.setAttribute('aria-hidden', 'true');
        sep.textContent = ' \u00B7 ';
        var cook = document.createElement('button');
        cook.type = 'button';
        cook.className = 'row-action row-action-cook';
        cook.textContent = 'Bake Recipe';
        cook.setAttribute('data-recipe-id', recipeId);
        if (acquisitionBlocked) {
            cook.disabled = true;
            cook.title = 'PAX engine acquisition required';
        }
        // Pin / unpin toggle. The pinned state is also reflected on the
        // row itself (recipe-row--pinned) so a pinned recipe is visible
        // at a glance; this button is the action that flips it. Pinning
        // is a pure list-ordering preference and never touches the PAX
        // engine, so it is NOT gated by acquisitionBlocked.
        var pinSep = document.createElement('span');
        pinSep.className = 'row-action-sep';
        pinSep.setAttribute('aria-hidden', 'true');
        pinSep.textContent = ' \u00B7 ';
        var pin = document.createElement('button');
        pin.type = 'button';
        pin.className = 'row-action row-action-pin';
        pin.setAttribute('data-recipe-id', recipeId);
        applyPinButtonLabel(pin, !!isPinned);
        td.appendChild(open);
        td.appendChild(sep);
        td.appendChild(cook);
        td.appendChild(pinSep);
        td.appendChild(pin);
        return td;
    }

    // Sweep the live table body once and apply the current
    // acquisitionBlocked state to every pre-rendered row-action-cook
    // button. Called by the cookbook:acquisitionStateChanged listener
    // so a state change after renderRows() still updates the
    // affordances without forcing a fresh fetch.
    function applyAcquisitionGateToRows() {
        var tbody = byId(TBODY_EL_ID);
        if (!tbody) { return; }
        var buttons = tbody.querySelectorAll('button.row-action-cook');
        for (var i = 0; i < buttons.length; i++) {
            var b = buttons[i];
            if (acquisitionBlocked) {
                b.disabled = true;
                b.title = 'PAX engine acquisition required';
            } else {
                b.disabled = false;
                b.removeAttribute('title');
            }
        }
    }

    function renderRows(recipes) {
        var tbody = byId(TBODY_EL_ID);
        if (!tbody) { return; }
        while (tbody.firstChild) { tbody.removeChild(tbody.firstChild); }

        for (var i = 0; i < recipes.length; i++) {
            var r = recipes[i] || {};
            var tr = document.createElement('tr');
            // The server returns recipes pinned-first (is_pinned DESC).
            // The row class is a subtle visual cue that this recipe sits
            // in the pinned group; the per-row toggle carries the
            // accessible state for assistive tech.
            if (r.isPinned) { tr.className = 'recipe-row--pinned'; }

            tr.appendChild(cell(String(r.name || ''),       'col-name'));
            tr.appendChild(cell(String(r.status || ''),     'col-status'));
            tr.appendChild(cell(formatTime(r.lastCookedAt), 'col-time'));
            tr.appendChild(cell(formatTime(r.createdAt),    'col-time'));
            tr.appendChild(cell(String(r.recipeId || ''),   'col-id'));
            tr.appendChild(actionsCell(r.recipeId, r.isPinned));

            tbody.appendChild(tr);
        }
        hideStatus();
        showOnboarding(false);
        showTable(true);
    }

    // ----------------------------------------------------------------
    // Cook trigger (S6A)
    //
    // The per-row Cook button POSTs /api/v1/recipes/<id>/cook. On 201,
    // the page navigates to '#/cooks/<cookId>'. On 409 recipe_busy, an
    // informational banner with an explicit "Open running cook" action
    // is shown -- the page does NOT auto-navigate (per disposition C2).
    // Other failures render a banner with the broker error code.
    //
    // No polling, no retries, no timers, no shared queue. The button
    // is disabled per-row while a POST is in flight against that row.
    // ----------------------------------------------------------------
    function doCookFromRow(recipeId, btn) {
        if (!state || !recipeId) { return; }
        if (acquisitionBlocked) {
            if (btn) { btn.disabled = true; }
            showBanner('error', 'PAX engine acquisition is required before recipes can be baked.');
            return;
        }
        var capturedEpoch = state.epoch;
        var signal = state.abortCtrl ? state.abortCtrl.signal : null;
        if (btn) { btn.disabled = true; }
        hideBanner();

        function liveBtn() { return (btn && document.body.contains(btn)) ? btn : null; }
        function reEnable() { var b = liveBtn(); if (b) { b.disabled = false; } }

        // X16C -- Bake state machine (per row).
        //   idle -> starting: the cook POST is issued; the row button
        //       is disabled.
        //   starting -> reauth_required: 401 reAuthRequired (opClass
        //       manualCook). The button STAYS disabled while we step up.
        //   reauth_required -> reauth_in_progress: Windows Hello runs
        //       via cookbookManualCookReauth.reauth().
        //   reauth_in_progress -> retrying_after_reauth: on a verified
        //       grant the cook is re-issued EXACTLY ONCE (allowReauth
        //       false on the retry so a second 401 cannot loop).
        //   * -> started: 201 navigates to the cook detail view.
        //   * -> failed: any bounded failure (including a cancelled or
        //       failed Hello) re-enables the button and surfaces a
        //       banner. A failed step-up never reads as a started cook.
        function sendCook(allowReauth) {
            window.cookbookApi.post('/api/v1/recipes/' + recipeId + '/cook', {}, { signal: signal }).then(function (resp) {
                if (!state || state.epoch !== capturedEpoch) { return; }

                if (resp.networkError) {
                    // Phase AE: cook trigger is a state-mutating call.
                    // A network failure does NOT mean no cook was started
                    // -- the POST may have committed before the socket
                    // dropped. Direct the operator to the Cooks page to
                    // verify rather than retrying blind.
                    reEnable();
                    showBanner('error',
                        'Could not reach broker: ' + resp.networkError + '. ' +
                        'A bake may or may not have been started on the server. ' +
                        'Open the Bakes page to verify before retrying.');
                    return;
                }
                if (resp.status === 201 && resp.body && resp.body.cookId) {
                    history.replaceState({}, '', window.location.pathname + '#/cooks/' + resp.body.cookId);
                    window.cookbookRouter.dispatch();
                    return;
                }
                if (resp.status === 401 && resp.body &&
                    (resp.body.code === 'reAuthRequired' || resp.body.error === 'reAuthRequired') &&
                    resp.body.opClass === 'manualCook') {
                    if (!allowReauth) {
                        // Re-auth already attempted once this Bake.
                        // Do not loop: surface a bounded failure.
                        reEnable();
                        showBanner('error', 'Windows Hello is still required. The bake did not start.');
                        return;
                    }
                    showBanner('info', 'Confirm with Windows Hello to start this bake\u2026');
                    window.cookbookManualCookReauth.reauth(recipeId).then(function (rr) {
                        if (!state || state.epoch !== capturedEpoch) { return; }
                        if (rr && rr.ok) {
                            hideBanner();
                            sendCook(false);
                            return;
                        }
                        reEnable();
                        showBanner('error', window.cookbookManualCookReauth.describe(rr));
                    });
                    return;
                }

                // Terminal non-success: re-enable the row button and
                // surface a bounded banner.
                reEnable();
                if (resp.status === 409 && resp.body && resp.body.error === 'recipe_busy') {
                    showCookBusyBanner(resp.body.cookId || '');
                    return;
                }
                if (resp.status === 409 && resp.body && resp.body.error === 'acquisitionRequired') {
                    showBanner('error', 'PAX engine acquisition is required before recipes can be baked.');
                    return;
                }
                if (resp.status === 500 && resp.body && resp.body.error === 'pax_script_integrity') {
                    showBanner('error', 'PAX integrity check failed. Re-install PAX Cookbook from the latest release.');
                    return;
                }
                if (resp.status === 412 && resp.body && resp.body.error === 'recipe_invalid') {
                    var n = (resp.body.errors && resp.body.errors.length) || 0;
                    showBanner('error', 'Recipe on disk failed validation (' + n + ' issue' + (n === 1 ? '' : 's') + '). Open the recipe and save it again.');
                    return;
                }
                if (resp.status === 404 && resp.body && resp.body.error === 'not_found') {
                    showBanner('error', 'Recipe not found.');
                    return;
                }
                if (resp.status === 400 && resp.body && resp.body.error === 'invalid_recipe_id') {
                    showBanner('error', 'Invalid recipe id.');
                    return;
                }
                var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                showBanner('error', 'Bake failed: ' + code + '.');
            });
        }

        sendCook(true);
    }

    function onTbodyClick(ev) {
        var t = ev && ev.target;
        if (!t || !t.classList) { return; }
        if (t.classList.contains('row-action-cook')) {
            ev.preventDefault();
            if (t.disabled) { return; }
            var rid = t.getAttribute('data-recipe-id');
            if (rid) { doCookFromRow(rid, t); }
            return;
        }
        if (t.classList.contains('row-action-pin')) {
            ev.preventDefault();
            if (t.disabled) { return; }
            var pinId = t.getAttribute('data-recipe-id');
            var wantPinned = t.getAttribute('data-pinned') !== '1';
            if (pinId) { doPinToggle(pinId, t, wantPinned); }
            return;
        }
    }

    // ----------------------------------------------------------------
    // Pin / unpin toggle (X11A)
    //
    // The per-row pin button POSTs /api/v1/recipes/<id>/pin or
    // /unpin. On success the list is reloaded so the broker's
    // pinned-first ordering (is_pinned DESC, created_at DESC) is
    // reflected deterministically -- the client never re-sorts on its
    // own. The button is disabled per-row while its POST is in flight.
    // Broker-lock (423) and re-auth (401) challenges are auto-routed by
    // the API helper to the lock overlay; any other failure surfaces a
    // bounded banner and the prior state is left intact (no fake
    // success, no silent failure).
    // ----------------------------------------------------------------
    function doPinToggle(recipeId, btn, wantPinned) {
        if (!state || !recipeId) { return; }
        var capturedEpoch = state.epoch;
        var signal = state.abortCtrl ? state.abortCtrl.signal : null;
        if (btn) { btn.disabled = true; }
        hideBanner();

        var path = '/api/v1/recipes/' + recipeId + (wantPinned ? '/pin' : '/unpin');
        window.cookbookApi.post(path, {}, { signal: signal }).then(function (resp) {
            if (!state || state.epoch !== capturedEpoch) { return; }
            var btnNow = btn && document.body.contains(btn) ? btn : null;
            if (btnNow) { btnNow.disabled = false; }

            if (resp.networkError) {
                showBanner('error',
                    'Could not reach broker: ' + resp.networkError + '. ' +
                    'The pin state was not changed.');
                return;
            }
            if (resp.ok) {
                // Re-fetch so the row moves into / out of the pinned
                // group in the broker's canonical order.
                loadRecipes();
                return;
            }
            if (resp.status === 404 && resp.body && resp.body.error === 'not_found') {
                showBanner('error', 'Recipe not found.');
                return;
            }
            if (resp.status === 400 && resp.body && resp.body.error === 'invalid_recipe_id') {
                showBanner('error', 'Invalid recipe id.');
                return;
            }
            var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
            showBanner('error', (wantPinned ? 'Pin' : 'Unpin') + ' failed: ' + code + '.');
        });
    }

    function loadRecipes() {
        if (!state) { return; }
        var capturedEpoch = state.epoch;
        var signal = state.abortCtrl ? state.abortCtrl.signal : null;

        var btn = byId(RELOAD_BTN_ID);
        if (btn) { btn.disabled = true; }
        setStatus('Loading recipes', '');
        hideBanner();
        showTable(false);
        showOnboarding(false);

        window.cookbookApi.get('/api/v1/recipes', { signal: signal }).then(function (resp) {
            // Discard the response if the page has been torn down or
            // remounted since the request went out.
            if (!state || state.epoch !== capturedEpoch) { return; }

            setText(LAST_FETCHED_ID, isoNowUtc());
            var btnNow = byId(RELOAD_BTN_ID);
            if (btnNow) { btnNow.disabled = false; }

            if (resp.networkError) {
                renderError('Failed to reach broker: ' + resp.networkError);
                return;
            }
            if (resp.status === 401) {
                renderError('Session token rejected (HTTP 401). Reload the app from the launcher.');
                return;
            }
            if (!resp.ok) {
                var err = (resp.body && resp.body.error) ? resp.body.error : '';
                renderError('Failed to load recipes (HTTP ' + resp.status + (err ? ': ' + err : '') + ').');
                return;
            }
            var list = (resp.body && Array.isArray(resp.body.recipes)) ? resp.body.recipes : [];
            if (list.length === 0) {
                renderEmpty();
            } else {
                renderRows(list);
            }
        });
    }

    // ----------------------------------------------------------------
    // F2E -- Recipe Takeout import wizard
    //
    // Single in-page modal. Opens when the chef clicks the page-header
    // Import Recipe Takeout button. The modal owns three views,
    // selected by which sub-panel is visible:
    //
    //   1. file picker
    //        - <input type="file" accept=".json.pax,.json.paxlite,.paxrecipe.json,.json,application/json">
    //        - FileReader reads the chosen file as text
    //        - client-side JSON parse (friendly only; not security)
    //        - classifies envelope by envelope.kind:
    //            * 'pax-cookbook.recipe-takeout' -> cookbook class
    //            * 'pax-cookbook-mini-recipe'    -> lite class
    //        - POSTs the parsed envelope to /api/v1/recipe-takeout/validate
    //          for cookbook class, or /api/v1/recipe-lite/validate for
    //          lite class. The broker re-validates kind on the wire
    //          and refuses cross-classified envelopes.
    //
    //   2. validation preview + recipe-name confirmation
    //        - shows source recipe name, suggested target recipe name,
    //          Chef's Key required status, Needs Prep status,
    //          source template if returned, warning list
    //        - an editable recipe-name input prefilled with the
    //          backend's nameSuggestion.suggestedName (blank when
    //          suggestedName is null; user must type a name)
    //        - Import button is disabled while the trimmed name is
    //          empty or while a request is in flight; clicking it POSTs
    //          {takeout: <original envelope>, targetRecipeName: <trim>}
    //          to /api/v1/recipe-takeout/import
    //
    //   3. success
    //        - shows the persisted recipe name (response.recipeName)
    //        - offers "Open Recipe" -> #/recipes/<recipeId> and
    //          a Done button that closes the modal and reloads the
    //          recipes list so the new row appears
    //
    // The wizard NEVER:
    //   - sends the selected filename as targetRecipeName
    //   - persists the envelope or filename to localStorage /
    //     sessionStorage
    //   - shows or persists the file's local OS path (browsers don't
    //     expose it anyway, and we don't try to extract it)
    //   - auto-imports on validate success (the chef must click Import)
    //   - includes any Prep Station export action (that is F2F)
    //   - includes any Help, Docs, Home, Pantry, or Settings entry
    //     points (that is F2F / F2H or out of scope entirely)
    // ----------------------------------------------------------------

    // In-memory wizard state. Lives only while the modal is mounted;
    // closeImportModal() clears it. Never written to storage.
    var importState = null;

    function closeImportModal() {
        var el = byId(IMPORT_MODAL_ID);
        if (el && el.parentNode) { el.parentNode.removeChild(el); }
        importState = null;
    }

    function openImportModal() {
        // Re-entrant guard: if a previous modal is still open (e.g. the
        // chef double-clicked the trigger), tear it down before
        // rebuilding so we never have two overlays in flight.
        closeImportModal();

        importState = {
            // Parsed envelope captured from the file picker. Reused
            // verbatim as the import endpoint's takeout payload so the
            // envelope the backend validated is exactly the envelope
            // the backend imports.
            envelope:      null,
            // Envelope class derived from envelope.kind once the file
            // is parsed: 'cookbook' or 'lite'. Controls which endpoint
            // pair we POST to (recipe-takeout/* vs recipe-lite/*) and
            // the wrapper key on the import call (takeout vs envelope).
            // null until the chef picks a file and we classify it.
            envelopeClass: null,
            // The browser-reported filename (e.g. "my-recipe.json.pax"
            // or "my-recipe.json.paxlite").
            // Shown ONLY in the selected-file display row of step 1 and
            // the file label of step 2. Never sent to the import
            // endpoint and never copied into targetRecipeName.
            selectedFile:  null,
            // Last validate-endpoint preview response (the entire 200
            // body). Used to drive the step 2 display and to source
            // nameSuggestion.suggestedName / sourceName.
            preview:       null,
            // The current view: 'file' | 'preview' | 'success'.
            view:          'file',
            // Inflight guard. While true, the Import button is disabled
            // and Escape/cancel still closes the dialog (no side effects).
            busy:          false,
            // Captured success response so the success view can render
            // the persisted recipe name and offer a route to the editor.
            result:        null
        };

        var overlay = document.createElement('div');
        overlay.id = IMPORT_MODAL_ID;
        overlay.className = 'modal-overlay';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-labelledby', IMPORT_MODAL_ID + '-title');
        overlay.addEventListener('click', function (ev) {
            // Click on the dim backdrop closes; click inside the panel
            // is ignored. Same pattern other modals use.
            if (ev && ev.target === overlay) { closeImportModal(); }
        });

        var panel = document.createElement('div');
        panel.className = 'modal-panel';
        panel.id = IMPORT_MODAL_ID + '-panel';
        panel.addEventListener('keydown', function (ev) {
            if (ev && ev.key === 'Escape' && !importState.busy) {
                ev.preventDefault();
                closeImportModal();
            }
        });

        var title = document.createElement('h2');
        title.className = 'modal-title';
        title.id = IMPORT_MODAL_ID + '-title';
        title.textContent = 'Import Recipe Takeout';
        panel.appendChild(title);

        var lede = document.createElement('p');
        lede.className = 'modal-lede';
        lede.textContent =
            'Select a PAX Cookbook recipe file (.json.pax, .json.paxlite, ' +
            '.paxrecipe.json, or .json). Cookbook will validate the package, ' +
            'let you confirm the recipe name, and then import it as a new local ' +
            'recipe. Imported recipes always start in Needs Prep until the ' +
            'recipe is reviewed in the Prep Station before the next Bake Recipe.';
        panel.appendChild(lede);

        // Body container -- each step swaps its own DOM into this slot.
        var body = document.createElement('div');
        body.className = 'modal-form recipe-takeout-import-body';
        panel.appendChild(body);

        // Status row at the bottom of the panel (validate / import
        // errors land here, near the active controls).
        var status = document.createElement('p');
        status.className = 'modal-status';
        status.setAttribute('aria-live', 'polite');
        panel.appendChild(status);

        overlay.appendChild(panel);
        document.body.appendChild(overlay);

        // Store the slot refs in importState so the step renderers can
        // swap content without re-querying the DOM each transition.
        importState.bodyEl   = body;
        importState.statusEl = status;

        renderImportFilePicker();
    }

    function setImportStatus(text, kind) {
        if (!importState || !importState.statusEl) { return; }
        var st = importState.statusEl;
        st.textContent = text || '';
        st.className = 'modal-status' + (kind ? ' modal-status-' + kind : '');
    }

    function clearImportBody() {
        if (!importState || !importState.bodyEl) { return; }
        var b = importState.bodyEl;
        while (b.firstChild) { b.removeChild(b.firstChild); }
    }

    // -------------------------- Step 1: file picker --------------------------

    function renderImportFilePicker() {
        if (!importState) { return; }
        importState.view = 'file';
        clearImportBody();
        setImportStatus('', '');
        var body = importState.bodyEl;

        var fileField = document.createElement('label');
        fileField.className = 'modal-field';
        var fileLabel = document.createElement('span');
        fileLabel.className = 'modal-field-label';
        fileLabel.textContent = 'PAX Cookbook recipe file (.json.pax, .json.paxlite, .paxrecipe.json, or .json)';
        fileField.appendChild(fileLabel);
        var fileInput = document.createElement('input');
        fileInput.type = 'file';
        fileInput.id   = IMPORT_FILE_INPUT_ID;
        // Friendly filter only -- backend revalidates contents. The
        // accept list covers the preferred .json.pax (full Cookbook
        // recipe), .json.paxlite (Mini-Kitchen lite recipe), the
        // legacy .paxrecipe.json (full Cookbook recipe, pre-rename),
        // the bare .json fallback, and the generic application/json
        // MIME hint. Envelope kind decides routing, not extension.
        fileInput.setAttribute('accept', '.json.pax,.json.paxlite,.paxrecipe.json,.json,application/json');
        fileField.appendChild(fileInput);
        body.appendChild(fileField);

        // Selected-file display row. Shows the chosen filename strictly
        // for chef context; the prose makes clear this is the FILE name,
        // not the recipe name.
        var selDisp = document.createElement('p');
        selDisp.className = 'modal-lede recipe-takeout-import-selected';
        selDisp.textContent = 'Selected file: (none)';
        body.appendChild(selDisp);

        var hint = document.createElement('p');
        hint.className = 'modal-lede';
        hint.textContent =
            'The filename is shown only so you can confirm which package ' +
            'you picked. The filename is never used as the recipe name. ' +
            'You will confirm the recipe name on the next step.';
        body.appendChild(hint);

        var actions = document.createElement('div');
        actions.className = 'modal-actions';

        var validateBtn = document.createElement('button');
        validateBtn.type = 'button';
        validateBtn.className = 'btn-primary';
        validateBtn.textContent = 'Validate package';
        validateBtn.disabled = true;
        actions.appendChild(validateBtn);

        var cancel = document.createElement('button');
        cancel.type = 'button';
        cancel.className = 'btn-ghost';
        cancel.textContent = 'Cancel';
        cancel.addEventListener('click', function () { closeImportModal(); });
        actions.appendChild(cancel);

        body.appendChild(actions);

        fileInput.addEventListener('change', function () {
            var f = (fileInput.files && fileInput.files[0]) ? fileInput.files[0] : null;
            if (!f) {
                importState.selectedFile = null;
                selDisp.textContent = 'Selected file: (none)';
                validateBtn.disabled = true;
                return;
            }
            // Friendly extension hint -- not a security gate.
            var nm = String(f.name || '');
            importState.selectedFile = nm;
            selDisp.textContent = 'Selected file: ' + nm;
            validateBtn.disabled = false;
        });

        validateBtn.addEventListener('click', function () {
            var f = (fileInput.files && fileInput.files[0]) ? fileInput.files[0] : null;
            if (!f) {
                setImportStatus('Choose a Recipe Takeout file before continuing.', 'error');
                return;
            }
            doValidateFromFile(f, validateBtn, cancel);
        });

        // Focus the file input for keyboard / SR ergonomics. Falling
        // back to the Cancel button if for some reason the input
        // cannot accept focus (defense against odd shadow-DOM cases).
        try { fileInput.focus(); }
        catch (e) { try { cancel.focus(); } catch (e2) {} }
    }

    function doValidateFromFile(file, validateBtn, cancelBtn) {
        if (!importState) { return; }
        importState.busy = true;
        if (validateBtn) { validateBtn.disabled = true; }
        if (cancelBtn)   { cancelBtn.disabled   = false; }
        setImportStatus('Reading file\u2026', '');

        var reader = new FileReader();
        reader.onerror = function () {
            if (!importState) { return; }
            importState.busy = false;
            if (validateBtn) { validateBtn.disabled = false; }
            setImportStatus('Could not read the selected file.', 'error');
        };
        reader.onload = function () {
            if (!importState) { return; }
            var text = (typeof reader.result === 'string') ? reader.result : '';
            var envelope = null;
            try {
                envelope = JSON.parse(text);
            } catch (e) {
                importState.busy = false;
                if (validateBtn) { validateBtn.disabled = false; }
                setImportStatus('This file is not a valid Recipe Takeout package.', 'error');
                return;
            }
            if (!envelope || typeof envelope !== 'object' || Array.isArray(envelope)) {
                importState.busy = false;
                if (validateBtn) { validateBtn.disabled = false; }
                setImportStatus('This file is not a valid Recipe Takeout package.', 'error');
                return;
            }
            // Classify the envelope by its 'kind' field. The broker
            // re-validates 'kind' on the wire; this client step just
            // picks which endpoint pair (recipe-takeout vs recipe-
            // lite) to POST to. Any other kind is rejected here with
            // a friendly message so the chef doesn't get a generic
            // shape error from the server. The lite contract also
            // requires schemaVersion === '1.0'; if the kind says lite
            // but the schemaVersion is missing or different, we still
            // route to the lite endpoint -- the broker returns the
            // canonical 'lite_schema_version_unsupported' error which
            // we surface as a friendly message.
            var kind = (typeof envelope.kind === 'string') ? envelope.kind : '';
            var envelopeClass = null;
            if (kind === COOKBOOK_ENVELOPE_KIND) {
                envelopeClass = 'cookbook';
            } else if (kind === LITE_ENVELOPE_KIND) {
                envelopeClass = 'lite';
            } else {
                importState.busy = false;
                if (validateBtn) { validateBtn.disabled = false; }
                setImportStatus(
                    'This file is not a recognized PAX Cookbook recipe package. ' +
                    'Expected envelope kind "' + COOKBOOK_ENVELOPE_KIND +
                    '" or "' + LITE_ENVELOPE_KIND + '".',
                    'error'
                );
                return;
            }
            // Capture the parsed envelope. The reference is what
            // step 3 sends back to the import endpoint as the
            // "takeout" payload. We do NOT round-trip the envelope
            // through JSON.stringify between validate and import --
            // the broker accepts both forms and equality is by value.
            importState.envelope      = envelope;
            importState.envelopeClass = envelopeClass;
            postValidate(envelope, validateBtn);
        };
        try {
            reader.readAsText(file);
        } catch (e) {
            importState.busy = false;
            if (validateBtn) { validateBtn.disabled = false; }
            setImportStatus('Could not read the selected file.', 'error');
        }
    }

    function postValidate(envelope, validateBtn) {
        setImportStatus('Validating package\u2026', '');
        var signal      = (state && state.abortCtrl) ? state.abortCtrl.signal : null;
        var endpoint    = (importState && importState.envelopeClass === 'lite')
                            ? LITE_VALIDATE_PATH
                            : TAKEOUT_VALIDATE_PATH;
        window.cookbookApi.post(endpoint, envelope, { signal: signal }).then(function (resp) {
            if (!importState) { return; }
            importState.busy = false;
            if (validateBtn) { validateBtn.disabled = false; }

            if (resp.networkError) {
                setImportStatus('Could not reach broker: ' + resp.networkError, 'error');
                return;
            }
            if (resp.status === 200 && resp.body && resp.body.valid === true) {
                importState.preview = resp.body;
                renderImportPreview();
                return;
            }
            // The backend returns the same error codes as the import
            // endpoint for shape/secret/JSON failures. Surface them
            // with the same friendly copy we'll use on import.
            showImportErrorFromResponse(resp, /*scope*/ 'validate');
        });
    }

    // -------------------------- Step 2: preview + name confirmation --------------------------

    function renderImportPreview() {
        if (!importState || !importState.preview) { return; }
        importState.view = 'preview';
        clearImportBody();
        setImportStatus('', '');
        var body    = importState.bodyEl;
        var preview = importState.preview;

        // Source-class banner. When the envelope is a Mini-Kitchen
        // lite recipe, the imported recipe ALWAYS lands as Needs Prep
        // regardless of warning content -- show that up front so the
        // chef knows what to expect before they confirm the name.
        if (importState.envelopeClass === 'lite') {
            var liteBanner = document.createElement('p');
            liteBanner.className = 'modal-lede recipe-takeout-import-source-banner';
            liteBanner.textContent =
                'This is a Mini-Kitchen lite recipe. It will be imported as a ' +
                'Needs Prep draft so you can finish the fields Mini-Kitchen does ' +
                'not capture (such as Chef\u2019s Key binding and output path review) ' +
                'in the Prep Station before the first Bake Recipe.';
            body.appendChild(liteBanner);
        }

        // Selected file context (filename only -- distinct from recipe name).
        if (importState.selectedFile) {
            var fileCtx = document.createElement('p');
            fileCtx.className = 'modal-lede recipe-takeout-import-selected';
            fileCtx.textContent = 'Selected file: ' + importState.selectedFile;
            body.appendChild(fileCtx);
        }

        // Source recipe name (from validate preview.recipe.name).
        var srcName = '';
        if (preview.recipe && typeof preview.recipe.name === 'string') {
            srcName = preview.recipe.name;
        }
        body.appendChild(readonlyRow('Source recipe name', srcName || '(unnamed)'));

        // Source template / provenance (informational).
        if (preview.recipe && preview.recipe.sourceTemplate) {
            body.appendChild(readonlyRow('Source template', String(preview.recipe.sourceTemplate)));
        }

        // Chef's Key required + mode.
        if (preview.chefKey) {
            var keyText = preview.chefKey.required
                ? ('Required (' + (preview.chefKey.mode || 'AppRegistration') + ')')
                : 'Not required';
            body.appendChild(readonlyRow('Chef\u2019s Key', keyText));
            if (preview.chefKey.sourceDisplayLabel) {
                body.appendChild(readonlyRow('Source key label', String(preview.chefKey.sourceDisplayLabel)));
            }
        }

        // Needs Prep status.
        var needsPrep = preview.needsPrep || {};
        var prepBits  = [];
        if (needsPrep.chefKey) { prepBits.push('Chef\u2019s Key'); }
        if (needsPrep.paths)   { prepBits.push('paths'); }
        if (needsPrep.tenant)  { prepBits.push('tenant'); }
        var prepText = (prepBits.length > 0)
            ? ('Yes \u2014 ' + prepBits.join(', '))
            : 'No \u2014 ready after import';
        body.appendChild(readonlyRow('Needs Prep', prepText));

        // Warnings list (subdued; not blocking).
        var warnings = Array.isArray(preview.warnings) ? preview.warnings : [];
        if (warnings.length > 0) {
            var wTitle = document.createElement('span');
            wTitle.className = 'modal-field-label';
            wTitle.textContent = 'Warnings';
            body.appendChild(wTitle);
            var wList = document.createElement('ul');
            wList.className = 'recipe-takeout-import-warnings';
            for (var i = 0; i < warnings.length; i++) {
                var w = warnings[i] || {};
                var li = document.createElement('li');
                var parts = [];
                if (w.code)   { parts.push(String(w.code)); }
                if (w.path)   { parts.push(String(w.path)); }
                if (w.detail) { parts.push(String(w.detail)); }
                li.textContent = parts.length > 0 ? parts.join(' \u2014 ') : 'warning';
                wList.appendChild(li);
            }
            body.appendChild(wList);
        }

        // Prep Station handoff message.
        var prepHint = document.createElement('p');
        prepHint.className = 'modal-lede';
        prepHint.textContent =
            'After import, the new recipe opens in the Prep Station for review. ' +
            'Nothing bakes until you explicitly click Bake Recipe.';
        body.appendChild(prepHint);

        // Editable recipe-name field.
        var ns         = preview.nameSuggestion || {};
        var suggested  = (typeof ns.suggestedName === 'string') ? ns.suggestedName : '';
        var sourceName = (typeof ns.sourceName    === 'string') ? ns.sourceName    : '';
        var nameField  = document.createElement('label');
        nameField.className = 'modal-field';
        var nameLabelSpan = document.createElement('span');
        nameLabelSpan.className = 'modal-field-label';
        nameLabelSpan.textContent = 'Recipe name';
        nameLabelSpan.setAttribute('for', IMPORT_NAME_INPUT_ID);
        nameField.appendChild(nameLabelSpan);
        var nameInput = document.createElement('input');
        nameInput.type = 'text';
        nameInput.id   = IMPORT_NAME_INPUT_ID;
        nameInput.maxLength = 200;
        nameInput.required  = true;
        nameInput.setAttribute('autocomplete', 'off');
        // Prefill from backend suggestion. When suggestedName is null
        // (every Name (N) through 99 collides), the input is left
        // blank and the chef MUST type a name before Import enables.
        nameInput.value = suggested ? suggested : '';
        nameField.appendChild(nameInput);

        // Suggestion explanation. Always shown so the chef understands
        // the difference between the source name and the destination
        // name. Mentions "Recipe name" -- never "filename".
        var nameHint = document.createElement('p');
        nameHint.className = 'modal-lede';
        if (suggested && sourceName && suggested !== sourceName) {
            nameHint.textContent =
                'A recipe named "' + sourceName + '" already exists in this Cookbook. ' +
                'The suggested name above adds a numeric suffix so the imported ' +
                'recipe has a unique display name. You can edit it before Import.';
        } else if (!suggested && sourceName) {
            nameHint.textContent =
                'A recipe named "' + sourceName + '" already exists in this Cookbook ' +
                'and every numbered variant up through (99) is already in use. ' +
                'Please choose a different name before Import.';
        } else if (suggested) {
            nameHint.textContent =
                'The suggested name above is taken from the package. You can edit it before Import.';
        } else {
            nameHint.textContent =
                'No suggested name is available from the package. Please type the ' +
                'name you want this recipe to appear under in your Cookbook.';
        }
        nameField.appendChild(nameHint);
        body.appendChild(nameField);

        var actions = document.createElement('div');
        actions.className = 'modal-actions';

        var importBtn = document.createElement('button');
        importBtn.type = 'button';
        importBtn.className = 'btn-primary';
        importBtn.textContent = 'Import';
        // Initial disabled state mirrors the trimmed input value.
        importBtn.disabled = !(nameInput.value && nameInput.value.trim().length > 0);
        actions.appendChild(importBtn);

        var back = document.createElement('button');
        back.type = 'button';
        back.className = 'btn-ghost';
        back.textContent = 'Choose a different file';
        back.addEventListener('click', function () {
            // Returning to step 1 resets envelope/preview so the next
            // validate call sees a clean slate. We deliberately do not
            // keep the previous envelope cached.
            importState.envelope      = null;
            importState.envelopeClass = null;
            importState.preview       = null;
            renderImportFilePicker();
        });
        actions.appendChild(back);

        var cancel = document.createElement('button');
        cancel.type = 'button';
        cancel.className = 'btn-ghost';
        cancel.textContent = 'Cancel';
        cancel.addEventListener('click', function () { closeImportModal(); });
        actions.appendChild(cancel);

        body.appendChild(actions);

        function refreshImportEnabled() {
            // Defense in depth: the backend is the source of truth for
            // name validity, but the client also disables Import while
            // the trimmed value is empty so the chef sees the gate.
            var ok = !!(nameInput.value && nameInput.value.trim().length > 0);
            importBtn.disabled = !ok || importState.busy;
        }
        nameInput.addEventListener('input', refreshImportEnabled);

        importBtn.addEventListener('click', function () {
            if (importState.busy) { return; }
            var raw     = nameInput.value || '';
            var trimmed = raw.trim();
            if (trimmed.length === 0) {
                setImportStatus('Recipe name is required.', 'error');
                try { nameInput.focus(); } catch (e) {}
                return;
            }
            doImport(trimmed, importBtn, nameInput);
        });

        // Focus order: if there's a usable name pre-filled, focus the
        // name input so the chef can edit or hit Enter to import; if
        // the input is empty (null suggestion), focus it anyway so the
        // chef can start typing immediately.
        try { nameInput.focus(); nameInput.select(); }
        catch (e) {}
    }

    function readonlyRow(labelText, value) {
        var w = document.createElement('div');
        w.className = 'modal-field';
        var span = document.createElement('span');
        span.className = 'modal-field-label';
        span.textContent = labelText;
        w.appendChild(span);
        var v = document.createElement('div');
        v.className = 'modal-readonly-value';
        v.textContent = value || '';
        w.appendChild(v);
        return w;
    }

    // -------------------------- Step 3: import + success --------------------------

    function doImport(targetRecipeName, importBtn, nameInput) {
        if (!importState || !importState.envelope) {
            setImportStatus('Validate a Recipe Takeout package before importing.', 'error');
            return;
        }
        importState.busy = true;
        if (importBtn) { importBtn.disabled = true; }
        setImportStatus('Importing recipe\u2026', '');

        // Wrapper shape differs by envelope class:
        //   cookbook -> { takeout, targetRecipeName }   POST recipe-takeout/import
        //   lite     -> { envelope, targetRecipeName }  POST recipe-lite/import
        // The broker enforces both wrapper shape and envelope class
        // on the wire; this just routes the POST.
        var isLite      = (importState.envelopeClass === 'lite');
        var endpoint    = isLite ? LITE_IMPORT_PATH : TAKEOUT_IMPORT_PATH;
        var requestBody = isLite
            ? { envelope: importState.envelope, targetRecipeName: targetRecipeName }
            : { takeout:  importState.envelope, targetRecipeName: targetRecipeName };
        var signal = (state && state.abortCtrl) ? state.abortCtrl.signal : null;
        window.cookbookApi.post(endpoint, requestBody, { signal: signal }).then(function (resp) {
            if (!importState) { return; }
            importState.busy = false;
            if (importBtn) { importBtn.disabled = false; }

            if (resp.networkError) {
                setImportStatus('Could not reach broker: ' + resp.networkError, 'error');
                return;
            }
            if (resp.status === 201 && resp.body && resp.body.ok === true && resp.body.recipeId) {
                importState.result = resp.body;
                renderImportSuccess();
                return;
            }
            // Field-scoped error handling. Each branch parks focus on
            // the most relevant control and shows a near-field message.
            var err  = (resp.body && resp.body.error) ? String(resp.body.error) : '';
            var body = resp.body || {};

            if (resp.status === 400 && err === 'recipe_name_required') {
                setImportStatus('Recipe name is required.', 'error');
                if (nameInput) { try { nameInput.focus(); } catch (e) {} }
                return;
            }
            if (resp.status === 400 && err === 'recipe_name_invalid') {
                var reason = body.reason ? String(body.reason) : '';
                var why =
                    reason === 'length'        ? 'Recipe name must be 1\u2013200 characters.' :
                    reason === 'control'       ? 'Recipe name cannot contain control characters (tab, newline, etc.).' :
                    reason === 'invalid_char'  ? 'Recipe name cannot contain any of these characters: < > : " / \\ | ? *' :
                                                 'Recipe name is not valid.';
                setImportStatus(why, 'error');
                if (nameInput) { try { nameInput.focus(); nameInput.select(); } catch (e) {} }
                return;
            }
            if (resp.status === 409 && err === 'recipe_name_conflict') {
                handleNameConflict(body, nameInput);
                return;
            }
            // Shared error path with validate (invalid_json,
            // takeout_shape_invalid, takeout_unknown_field,
            // takeout_contains_forbidden_secret_field, payload_too_large).
            showImportErrorFromResponse(resp, /*scope*/ 'import');
        });
    }

    function handleNameConflict(body, nameInput) {
        // The backend's nextSuggestion is the canonical answer for
        // "what's the next free Name (N)?" -- we never compute it on
        // the client. When the backend returns null (e.g. every
        // numbered variant through 99 collides), we tell the chef to
        // choose a different base name; we do NOT auto-submit.
        var next = (body && Object.prototype.hasOwnProperty.call(body, 'nextSuggestion'))
            ? body.nextSuggestion
            : undefined;
        var msg  = (body && body.message) ? String(body.message)
                                          : 'A recipe with that name already exists in this Cookbook.';

        if (typeof next === 'string' && next.length > 0) {
            setImportStatus(
                msg + ' Suggested: "' + next + '". Edit the Recipe name and click Import again.',
                'error'
            );
            if (nameInput) {
                try {
                    // Prefill with the next free suggestion so the chef
                    // can accept-and-import or edit-and-import. We do
                    // NOT trigger Import automatically.
                    nameInput.value = next;
                    nameInput.focus();
                    nameInput.select();
                    // Re-fire the input event so the Import button's
                    // disabled state recomputes against the new value.
                    var ev = document.createEvent('Event');
                    ev.initEvent('input', true, true);
                    nameInput.dispatchEvent(ev);
                } catch (e) {}
            }
            return;
        }
        // null or absent nextSuggestion -- the chef must invent a new
        // base name (every Name (N) through (99) is also taken).
        setImportStatus(
            msg + ' Every numbered variant through (99) is also taken. Please choose a different name.',
            'error'
        );
        if (nameInput) { try { nameInput.focus(); nameInput.select(); } catch (e) {} }
    }

    function showImportErrorFromResponse(resp, scope) {
        var status = resp.status || 0;
        var err    = (resp.body && resp.body.error) ? String(resp.body.error) : '';
        var msg    = '';
        if (status === 401 || status === 423) {
            msg = 'Sign-in required. Reload the app from the launcher.';
        } else if (err === 'invalid_json' || err === 'takeout_shape_invalid' ||
                   err === 'takeout_unknown_field' || err === 'takeout_kind_invalid' ||
                   err === 'takeout_schema_version_unsupported' ||
                   err === 'lite_shape_invalid'    || err === 'lite_unknown_field' ||
                   err === 'lite_kind_invalid') {
            msg = 'This file is not a valid PAX Cookbook recipe package.';
        } else if (err === 'lite_schema_version_unsupported') {
            msg = 'This Mini-Kitchen lite recipe was produced by a newer Mini-Kitchen than this Cookbook supports.';
        } else if (err === 'payload_too_large' || status === 413) {
            msg = 'This file is too large for import.';
        } else if (err === 'takeout_contains_forbidden_secret_field' ||
                   err === 'lite_contains_forbidden_secret_field') {
            msg = 'This package contains data Cookbook will not import for safety.';
        } else if (err === 'takeout_persist_failed' || err === 'lite_persist_failed') {
            msg = 'The recipe could not be saved. Try again, or contact your administrator.';
        } else if (err) {
            msg = (scope === 'import' ? 'Import failed' : 'Validation failed') + ': ' + err + '.';
        } else {
            msg = (scope === 'import' ? 'Import failed' : 'Validation failed') + ' (HTTP ' + status + ').';
        }
        setImportStatus(msg, 'error');
    }

    function renderImportSuccess() {
        if (!importState || !importState.result) { return; }
        importState.view = 'success';
        clearImportBody();
        setImportStatus('', '');
        var body   = importState.bodyEl;
        var result = importState.result;

        var ok = document.createElement('p');
        ok.className = 'modal-status modal-status-ok';
        ok.textContent = 'Imported "' + String(result.recipeName || '') + '" into your Cookbook.';
        body.appendChild(ok);

        var lede = document.createElement('p');
        lede.className = 'modal-lede';
        lede.textContent =
            'The recipe is in Needs Prep until you review it in the Prep Station. ' +
            'Nothing will bake until you open it and click Bake Recipe.';
        body.appendChild(lede);

        var actions = document.createElement('div');
        actions.className = 'modal-actions';

        var openBtn = document.createElement('button');
        openBtn.type = 'button';
        openBtn.className = 'btn-primary';
        openBtn.textContent = 'Open Recipe';
        openBtn.addEventListener('click', function () {
            var rid = String(result.recipeId || '');
            closeImportModal();
            if (rid) {
                // Use the existing hash-router navigation pattern that
                // the Cook button and other in-app navigations rely on.
                // The recipe editor (Prep Station) is mounted at
                // #/recipes/<id>; the router dispatch picks it up.
                history.replaceState({}, '', window.location.pathname + '#/recipes/' + rid);
                if (window.cookbookRouter && typeof window.cookbookRouter.dispatch === 'function') {
                    window.cookbookRouter.dispatch();
                }
            }
        });
        actions.appendChild(openBtn);

        var doneBtn = document.createElement('button');
        doneBtn.type = 'button';
        doneBtn.className = 'btn-ghost';
        doneBtn.textContent = 'Done';
        doneBtn.addEventListener('click', function () {
            closeImportModal();
            // Reload the recipes list so the new row appears at the
            // top (server returns created_at DESC).
            if (state) { loadRecipes(); }
        });
        actions.appendChild(doneBtn);

        body.appendChild(actions);

        try { openBtn.focus(); } catch (e) {}
    }

    // ----------------------------------------------------------------
    // Page lifecycle
    // ----------------------------------------------------------------

    function mount(container) {
        container.innerHTML = PAGE_TEMPLATE;

        state = {
            epoch:           nextEpoch++,
            reloadHandler:   null,
            tbodyClickHandler: null,
            // F2E: page-header Import Recipe Takeout button listener.
            // Tracked here so teardown can unbind it cleanly when the
            // chef navigates away mid-wizard.
            importBtnHandler:  null,
            // Phase AE: per-mount AbortController. Aborted on teardown
            // so any pending fetch issued by this page releases its
            // browser-side resources rather than running to completion
            // against an unmounted page. Epoch-based stale-drop is
            // still authoritative for response discard; AbortController
            // is purely an operational cleanup.
            abortCtrl: (typeof AbortController === 'function') ? new AbortController() : null
        };

        setText(BROKER_HOST_ID, window.location.host || '(unknown)');

        var tokenEl = byId(TOKEN_STATUS_ID);
        if (window.cookbookApi.hasToken()) {
            if (tokenEl) { tokenEl.textContent = 'present'; tokenEl.className = 'ok'; }
        } else {
            if (tokenEl) { tokenEl.textContent = 'missing'; tokenEl.className = 'miss'; }
        }

        var btn = byId(RELOAD_BTN_ID);
        if (btn) {
            state.reloadHandler = function () { loadRecipes(); };
            btn.addEventListener('click', state.reloadHandler);
            btn.disabled = false;
            if (window.PaxTopbar) { window.PaxTopbar.pageReloadBound = true; }
        }

        // Delegated click handler for per-row Cook buttons.
        var tbody = byId(TBODY_EL_ID);
        if (tbody) {
            state.tbodyClickHandler = onTbodyClick;
            tbody.addEventListener('click', state.tbodyClickHandler);
        }

        // F2E: wire the Import Recipe Takeout button. Bound here so
        // teardown can unbind it; the wizard itself manages its own
        // modal overlay lifecycle.
        var importBtn = byId(IMPORT_BTN_ID);
        if (importBtn) {
            state.importBtnHandler = function () { openImportModal(); };
            importBtn.addEventListener('click', state.importBtnHandler);
        }

        if (!window.cookbookApi.hasToken()) {
            setText(LAST_FETCHED_ID, isoNowUtc());
            renderError('No session token in this tab. Reload the app from the launcher.');
            return;
        }
        loadRecipes();
    }

    function teardown() {
        if (!state) { return; }

        // Detach the shell reload button listener bound in mount.
        var btn = byId(RELOAD_BTN_ID);
        if (btn && state.reloadHandler) {
            btn.removeEventListener('click', state.reloadHandler);
            btn.disabled = false;
        }
        if (window.PaxTopbar) { window.PaxTopbar.pageReloadBound = false; }

        var tbody = byId(TBODY_EL_ID);
        if (tbody && state.tbodyClickHandler) {
            tbody.removeEventListener('click', state.tbodyClickHandler);
        }

        // F2E: unbind the Import Recipe Takeout listener and ensure
        // the modal is torn down if the chef navigates away while the
        // wizard is open. In-memory wizard state is cleared by
        // closeImportModal() so the envelope text never lingers.
        var importBtn = byId(IMPORT_BTN_ID);
        if (importBtn && state.importBtnHandler) {
            importBtn.removeEventListener('click', state.importBtnHandler);
        }
        closeImportModal();

        // Phase AE: abort any in-flight fetch issued by this page so
        // browser-side resources release immediately. Epoch-bumping
        // (below) is still the authoritative response-discard signal.
        if (state.abortCtrl) {
            try { state.abortCtrl.abort(); } catch (e) {}
        }

        // Bump the epoch so any in-flight response is discarded by its
        // .then handler when it eventually resolves.
        nextEpoch++;

        // Drop refs. Container DOM is cleared by the router.
        state = null;
    }

    window.cookbookRecipesPage = {
        mount:    mount,
        teardown: teardown
    };

    // Stage 4: subscribe to acquisition-state changes emitted by
    // pax-engine-overlay.js. Registered once at script load so the
    // flag stays current across mount/teardown cycles. The sweep
    // self-guards against a missing tbody.
    try {
        window.addEventListener('cookbook:acquisitionStateChanged', function (ev) {
            try {
                acquisitionBlocked = !!(ev && ev.detail && ev.detail.blocked);
                applyAcquisitionGateToRows();
            } catch (e) { /* swallow */ }
        });
    } catch (e) { /* listener registration unsupported -- non-fatal */ }
})();
