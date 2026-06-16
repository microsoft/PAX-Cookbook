// PAX Cookbook -- runs (cooks) list page.
//
// Page module for route '#/cooks'. Owns the DOM under #page-root for
// the duration it is mounted; on teardown, drops its listeners, signals
// in-flight responses to discard themselves, and releases its refs so
// the router can clear the container.
//
// Renders the operator-facing run history: one row per cook, in server
// order (COALESCE(started_at, created_at) DESC). The columns surface
// the persisted run identity --
//
//     recipe name           (from frozen recipe-snapshot.json)
//     lifecycle state       (authoritative status column from cooks row)
//     created / started /   (timestamps owned by the cook row, never
//       finished / duration   inferred from filesystem)
//     terminal outcome      (exit code + error class from cooks row)
//     bundled PAX version   (from cook-context.json createdBy)
//     Cookbook version      (from cook-context.json createdBy)
//     cook id               (with link to details view)
//
// No polling, no auto-refresh, no client-side sort, no client-side
// filter, no virtualization. Refresh is triggered ONLY by mount and
// by the topbar Reload button. The page renders what the broker
// returned -- it does NOT compute, infer, or aggregate any field.
(function () {
    'use strict';

    var STATUS_EL_ID    = 'cooks-status';
    var TABLE_EL_ID     = 'cooks-table';
    var TBODY_EL_ID     = 'cooks-table-body';
    var BANNER_EL_ID    = 'cooks-list-banner';
    // Structured empty-state panel rendered when the cooks list is
    // empty. Built into #page-root alongside the status element by
    // mount; removed on teardown.
    var ONBOARDING_EL_ID = 'cooks-onboarding';

    var BROKER_HOST_ID  = 'broker-host';
    var TOKEN_STATUS_ID = 'token-status';
    var LAST_FETCHED_ID = 'last-fetched';
    var RELOAD_BTN_ID   = 'reload-button';

    var PAGE_TEMPLATE = [
        '<section class="page cooks-section">',
            '<header class="page-header">',
                '<div class="page-header-text">',
                    '<h1 class="page-title">Bakes<button type="button" class="help-hook" data-help-topic="cooks.intro" aria-label="Help: About bakes" title="About bakes"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h1>',
                    // Deterministic, static page lede. Explains what a bake
                    // is and what gets recorded.
                    '<p class="page-lede">A bake is one execution of a recipe. Cookbook records the frozen command, recipe snapshot, sentinels, and output artifacts for every bake \u2014 nothing is reconstructed after the fact.</p>',
                '</div>',
            '</header>',
            '<div id="cooks-status" class="cooks-status">Loading bakes</div>',
            '<div id="cooks-list-banner" class="editor-banner" hidden></div>',
            // Empty-state onboarding panel. Hidden by default;
            // revealed only when the server returns an empty bake list.
            '<aside id="cooks-onboarding" class="empty-onboarding" hidden aria-labelledby="cooks-onboarding-title">',
                '<h2 id="cooks-onboarding-title" class="empty-onboarding-title">No bakes yet. Here is how to start the first one.</h2>',
                '<p class="empty-onboarding-desc">Cookbook does not bake anything automatically. A bake only starts when the chef opens a recipe and starts it from the recipe page.</p>',
                '<ol class="empty-onboarding-steps">',
                    '<li class="empty-onboarding-step"><strong>Open the <a class="empty-onboarding-link" href="#/recipes">Recipes</a> list.</strong> If no recipes exist, create one by materializing a template from the Pantry first.</li>',
                    '<li class="empty-onboarding-step"><strong>Click an existing recipe to open it.</strong> Or use the per-row Cook action once a recipe has been saved.</li>',
                    '<li class="empty-onboarding-step"><strong>Click Preview.</strong> Confirm the exact PAX command and argv before anything executes.</li>',
                    '<li class="empty-onboarding-step"><strong>Click Cook to start.</strong> The bake appears in this list as soon as the broker accepts the request.</li>',
                    '<li class="empty-onboarding-step"><strong>Open the bake to inspect.</strong> Each bake row links to a detail page with the frozen command, recipe snapshot, sentinels, and output artifacts on disk.</li>',
                '</ol>',
                '<p class="empty-onboarding-note">If bakes you expected to see are missing, open <a class="empty-onboarding-link" href="#/settings">Settings</a> to confirm the broker, PAX integrity, and database paths are healthy.</p>',
            '</aside>',
            '<table id="cooks-table" class="cooks-table" hidden>',
                '<thead>',
                    '<tr>',
                        '<th scope="col">Recipe</th>',
                        '<th scope="col">Status</th>',
                        '<th scope="col">Created (UTC)</th>',
                        '<th scope="col">Started (UTC)</th>',
                        '<th scope="col">Finished (UTC)</th>',
                        '<th scope="col">Duration</th>',
                        '<th scope="col">Outcome</th>',
                        '<th scope="col">Rows</th>',
                        '<th scope="col">Artifacts</th>',
                        '<th scope="col">Bundled PAX</th>',
                        '<th scope="col">Cookbook</th>',
                        '<th scope="col">Bake</th>',
                    '</tr>',
                '</thead>',
                '<tbody id="cooks-table-body"></tbody>',
            '</table>',
        '</section>'
    ].join('');

    var nextEpoch = 1;
    var state = null;

    function byId(id) { return document.getElementById(id); }

    function setText(id, text) {
        var el = byId(id);
        if (el) { el.textContent = text; }
    }

    function setStatus(text, kind) {
        var el = byId(STATUS_EL_ID);
        if (!el) { return; }
        el.textContent = text;
        el.className = 'cooks-status' + (kind ? ' ' + kind : '');
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
        // Plain visibility toggle for the empty-state panel.
        var p = byId(ONBOARDING_EL_ID);
        if (p) { p.hidden = !show; }
    }

    function isoNowUtc() {
        return new Date().toISOString();
    }

    function cell(text, klass) {
        var td = document.createElement('td');
        td.textContent = (text === null || text === undefined || text === '') ? '\u2014' : String(text);
        if (klass) { td.className = klass; }
        return td;
    }

    function formatTime(value) {
        if (value === null || value === undefined || value === '') { return '\u2014'; }
        return String(value);
    }

    function formatDuration(seconds) {
        // Duration is owned by the cook row's duration_seconds column,
        // computed once at cook-finish. Never recomputed client-side.
        if (seconds === null || seconds === undefined || seconds === '') { return '\u2014'; }
        var n = Number(seconds);
        if (!isFinite(n)) { return '\u2014'; }
        return n.toFixed(3) + ' s';
    }

    function formatCount(value) {
        // Renders integer counts (row count, artifact count). null/
        // undefined render as em-dash; anything else is coerced to a
        // string. Zero renders as '0', NOT as em-dash, so an explicit
        // zero is distinguishable from absent.
        if (value === null || value === undefined) { return '\u2014'; }
        var n = Number(value);
        if (!isFinite(n)) { return '\u2014'; }
        return String(n);
    }

    function formatOutcome(cook) {
        // Terminal outcome surface: shows the authoritative exit_code
        // and error_class from the cooks row. For running cooks the
        // outcome is intentionally empty -- lifecycle is the source of
        // truth and is shown in its own column.
        if (!cook) { return '\u2014'; }
        if (cook.status === 'running') { return '\u2014'; }
        var bits = [];
        if (cook.exitCode !== null && cook.exitCode !== undefined) {
            bits.push('exit=' + cook.exitCode);
        }
        if (cook.errorClass) {
            bits.push(String(cook.errorClass));
        }
        if (bits.length === 0) { return '\u2014'; }
        return bits.join(' / ');
    }

    function statusCell(cook) {
        var td = document.createElement('td');
        td.className = 'cooks-col-status';
        var s = (cook && cook.status) ? String(cook.status) : '';
        var chip = document.createElement('span');
        chip.className = 'cook-status-chip status-' + (s ? s : 'unknown');
        chip.textContent = s ? s : '(unknown)';
        td.appendChild(chip);
        // V1.S03 -- resumability marker for interrupted cooks.
        // The chip itself stays 'interrupted'; the marker is a
        // separate inline badge so the operator sees in the list
        // view that a Resume action is available without opening
        // the cook. Truth is supplied by the broker via
        // /api/v1/cooks (Test-CookResumable); we never re-derive.
        if (s === 'interrupted' && cook && cook.resumable === true) {
            var marker = document.createElement('span');
            marker.className = 'cooks-resumable-marker';
            marker.textContent = 'resumable';
            td.appendChild(document.createTextNode(' '));
            td.appendChild(marker);
        }
        return td;
    }

    function cookIdCell(cookId) {
        var td = document.createElement('td');
        td.className = 'cooks-col-cookid';
        if (!cookId) {
            td.textContent = '\u2014';
            return td;
        }
        var a = document.createElement('a');
        a.href = '#/cooks/' + cookId;
        a.className = 'cooks-row-open';
        a.textContent = cookId;
        td.appendChild(a);
        return td;
    }

    // V1.S06d -- the Recipe cell carries a small trigger chip
    // prepended to the recipe name so the operator can tell at a
    // glance whether a row was a manual run, a Resume of an
    // interrupted run, or a scheduled-task run reconciled from a
    // wrapper folder. The chip is purely informational; the cookId
    // and status columns remain authoritative. Per the V1.S06d
    // doctrine no new column is introduced -- the chip lives
    // inline with the recipe name.
    function recipeCell(cook) {
        var td = document.createElement('td');
        td.className = 'cooks-col-recipe';
        var trigger = cook && cook.trigger ? String(cook.trigger) : '';
        if (trigger === 'scheduled' || trigger === 'resume') {
            var chip = document.createElement('span');
            chip.className = 'cook-trigger-chip cook-trigger-' + trigger;
            chip.textContent = trigger;
            td.appendChild(chip);
            td.appendChild(document.createTextNode(' '));
        }
        var name = cook && cook.recipeName ? String(cook.recipeName) : '\u2014';
        td.appendChild(document.createTextNode(name));
        return td;
    }

    function renderEmpty() {
        // When the cooks list is empty, suppress the terse status line
        // and show the structured onboarding panel instead.
        showTable(false);
        hideStatus();
        showOnboarding(true);
    }

    function renderError(message) {
        showTable(false);
        showOnboarding(false);
        setStatus(message, 'error');
    }

    // Friendly state for builds that do not yet expose a bake-history
    // read endpoint. GET /api/v1/cooks is unmapped in this build and the
    // broker answers 501 not_implemented_x4. Rather than surface the raw
    // HTTP error as a red failure banner, present a calm "not available
    // yet" panel with the technical detail demoted to a muted support line.
    function renderNotReady(resp) {
        showTable(false);
        showOnboarding(false);
        var el = byId(STATUS_EL_ID);
        if (!el) { return; }
        while (el.firstChild) { el.removeChild(el.firstChild); }
        el.className = 'cooks-status not-ready';
        el.hidden = false;

        var title = document.createElement('div');
        title.className = 'cooks-not-ready-title';
        title.textContent = 'Bake history is not available in this build yet.';
        el.appendChild(title);

        var body = document.createElement('div');
        body.className = 'cooks-not-ready-body';
        body.textContent = 'Bakes will appear here after you run a recipe.';
        el.appendChild(body);

        // Discoverability: a chef landing on the Bakes page with nothing
        // to show needs to know where a bake actually starts. Point them
        // at the Recipes list and name the exact action (Bake Recipe).
        // This does not start a bake and does not fabricate history.
        var cta = document.createElement('div');
        cta.className = 'cooks-not-ready-cta';
        cta.appendChild(document.createTextNode('To start a bake, open a saved recipe and choose Bake Recipe. '));
        var ctaLink = document.createElement('a');
        ctaLink.className = 'cooks-not-ready-link';
        ctaLink.href = '#/recipes';
        ctaLink.textContent = 'Go to Recipes';
        cta.appendChild(ctaLink);
        el.appendChild(cta);

        var err = (resp && resp.body && resp.body.error) ? String(resp.body.error) : 'not_implemented_x4';
        var status = (resp && resp.status) ? resp.status : 501;
        var detail = document.createElement('div');
        detail.className = 'cooks-not-ready-detail';
        detail.textContent = 'Support detail: HTTP ' + status + ' ' + err;
        el.appendChild(detail);
    }

    function hideBanner() {
        var el = byId(BANNER_EL_ID);
        if (!el) { return; }
        el.hidden = true;
        el.className = 'editor-banner';
        while (el.firstChild) { el.removeChild(el.firstChild); }
    }

    function renderRows(cooks) {
        var tbody = byId(TBODY_EL_ID);
        if (!tbody) { return; }
        while (tbody.firstChild) { tbody.removeChild(tbody.firstChild); }

        for (var i = 0; i < cooks.length; i++) {
            var c = cooks[i] || {};
            var tr = document.createElement('tr');

            tr.appendChild(recipeCell(c));
            tr.appendChild(statusCell(c));
            tr.appendChild(cell(formatTime(c.createdAt),   'cooks-col-time'));
            tr.appendChild(cell(formatTime(c.startedAt),   'cooks-col-time'));
            tr.appendChild(cell(formatTime(c.finishedAt),  'cooks-col-time'));
            tr.appendChild(cell(formatDuration(c.durationSeconds), 'cooks-col-duration'));
            tr.appendChild(cell(formatOutcome(c),          'cooks-col-outcome'));
            tr.appendChild(cell(formatCount(c.rowCount),       'cooks-col-rows'));
            tr.appendChild(cell(formatCount(c.artifactCount),  'cooks-col-artifacts'));
            tr.appendChild(cell(c.bundledPaxVersion,       'cooks-col-pax'));
            tr.appendChild(cell(c.cookbookVersion,         'cooks-col-cookbook'));
            tr.appendChild(cookIdCell(c.cookId));

            tbody.appendChild(tr);
        }
        hideStatus();
        showOnboarding(false);
        showTable(true);
    }

    function loadCooks() {
        if (!state) { return; }
        var capturedEpoch = state.epoch;
        var signal = state.abortCtrl ? state.abortCtrl.signal : null;

        var btn = byId(RELOAD_BTN_ID);
        if (btn) { btn.disabled = true; }
        setStatus('Loading bakes', '');
        hideBanner();
        showTable(false);
        showOnboarding(false);

        window.cookbookApi.get('/api/v1/cooks', { signal: signal }).then(function (resp) {
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
            if (resp.status === 501) {
                renderNotReady(resp);
                return;
            }
            if (!resp.ok) {
                var err = (resp.body && resp.body.error) ? resp.body.error : '';
                renderError('Failed to load bakes (HTTP ' + resp.status + (err ? ': ' + err : '') + ').');
                return;
            }
            var list = (resp.body && Array.isArray(resp.body.cooks)) ? resp.body.cooks : [];
            if (list.length === 0) {
                renderEmpty();
            } else {
                renderRows(list);
            }
        });
    }

    function mount(container) {
        container.innerHTML = PAGE_TEMPLATE;

        state = {
            epoch:         nextEpoch++,
            reloadHandler: null,
            // Phase AE: per-mount AbortController; aborted on teardown
            // to release in-flight fetches.
            abortCtrl:     (typeof AbortController === 'function') ? new AbortController() : null
        };

        setText(BROKER_HOST_ID, window.location.host || '(unknown)');

        var tokenEl = byId(TOKEN_STATUS_ID);
        if (window.cookbookApi.hasToken()) {
            if (tokenEl) { tokenEl.textContent = 'Connected'; tokenEl.className = 'ok'; }
        } else {
            if (tokenEl) { tokenEl.textContent = 'No token'; tokenEl.className = 'miss'; }
        }

        var btn = byId(RELOAD_BTN_ID);
        if (btn) {
            state.reloadHandler = function () { loadCooks(); };
            btn.addEventListener('click', state.reloadHandler);
            btn.disabled = false;
            if (window.PaxTopbar) { window.PaxTopbar.pageReloadBound = true; }
        }

        if (!window.cookbookApi.hasToken()) {
            setText(LAST_FETCHED_ID, isoNowUtc());
            renderError('No session token in this tab. Reload the app from the launcher.');
            return;
        }
        loadCooks();
    }

    function teardown() {
        if (!state) { return; }

        var btn = byId(RELOAD_BTN_ID);
        if (btn && state.reloadHandler) {
            btn.removeEventListener('click', state.reloadHandler);
            btn.disabled = false;
        }
        if (window.PaxTopbar) { window.PaxTopbar.pageReloadBound = false; }

        // Phase AE: abort in-flight fetches before bumping epoch.
        if (state.abortCtrl) {
            try { state.abortCtrl.abort(); } catch (e) {}
        }

        nextEpoch++;
        state = null;
    }

    window.cookbookCooksListPage = {
        mount:    mount,
        teardown: teardown
    };
})();
