// PAX Cookbook -- cook view page.
//
// Page module for route '#/cooks/<cookId>'. Owns the DOM under
// #page-root for the duration it is mounted; on teardown, drops its
// listeners and refs.
//
// Scope (intentional, hard-bounded):
//   - Stale-on-mount fetch of GET /api/v1/cooks/<cookId>
//   - Render of the persisted cook row + sentinels + artifacts
//   - Stop button (POST /api/v1/cooks/<cookId>/stop) when status === 'running'
//   - Explicit Refresh button (re-fetches the same GET)
//   - One-shot historical hydration of cook.log via
//     GET /api/v1/cooks/<cookId>/log into the Console card. The
//     response body is rendered append-only as a single immutable
//     text node and terminated by a positional seam marker.
//     Hydration fires ONCE per page mount, immediately after the
//     first successful cook-detail load. Refresh does not re-hydrate.
//   - One-shot opening of the SPECIALIZED cook-view live-tail
//     WebSocket at /api/v1/cooks/<cookId>/log/ws after hydration,
//     ONLY if the cook is in status 'running' at mount time. The
//     socket carries OPAQUE UTF-8 text frames carrying raw cook.log
//     bytes appended after the moment of upgrade. Frames are
//     rendered append-only as text nodes after the seam marker.
//     The socket is opened at most once per page mount; the broker
//     closes it when the cook becomes terminal.
//
// NOT in scope:
//   - reuse of the broker /ws hub. The cook-view live-tail uses a
//     DIFFERENT, specialized endpoint at /api/v1/cooks/<id>/log/ws
//     and is INTENTIONALLY architecturally distinct from /ws. The
//     two systems solve different problems; do not unify them.
//   - any subscribe / unsubscribe / ping / pong protocol on the
//     cook-view live-tail socket. The client never sends. The wire
//     format is opaque UTF-8 text bytes; no JSON envelope, no typed
//     frames, no semantic interpretation, no JSON.parse of frame
//     bodies, no kind classification.
//   - reconnect, retry, backoff, disconnect banner. On disconnect
//     the user navigates away and back to re-mount.
//   - polling, timers, scheduled refresh, debounce, throttle
//   - terminal emulation, ANSI parsing, cursor movement, stdin
//   - global cooks dashboard / cross-recipe history
//   - hydration / live deduplication, diff, replay arrays, frame
//     storage, cursor offsets, range requests, partial fetches
//   - logfile semantic interpretation. The cook.log body is rendered
//     OPAQUELY: no stripping of '[STDERR] ' prefixes, no marker
//     normalization, no prefix reclassification, no line splitting,
//     no per-line typing. Whatever bytes the broker returns are the
//     bytes the user sees.
//
// Three rules apply to the Console card:
//   1. STDOUT IS OPAQUE TEXT. No progress inference, no phase detection,
//      no error classification from stream content, no colorize. The
//      Console card renders the cook.log response body as one opaque
//      blob.
//   2. NO TERMINAL EMULATION. No ANSI parsing, no VT100, no pseudo-tty,
//      no resize, no interactivity. Hydration content is materialized
//      as a Text node via document.createTextNode; the seam marker
//      is a structural element with no textual content.
//   3. APPEND-ONLY RENDERING. Once a node is appended to the Console,
//      it is immutable: never re-written, never removed, never diffed.
//      The hydration text node is appended exactly once. The seam
//      marker is appended exactly once. Live-tail frames are appended
//      as additional immutable text nodes AFTER the seam, never
//      editing existing ones.

(function () {
    'use strict';

    var PAGE_TEMPLATE = [
        '<nav class="breadcrumb" aria-label="Breadcrumb">',
            '<a href="#/recipes" class="breadcrumb-link">Recipes</a>',
            '<span class="breadcrumb-sep" aria-hidden="true"> / </span>',
            '<span id="cook-breadcrumb-trailing">Bake</span>',
        '</nav>',
        '<section class="page cook-view">',
            '<header class="page-header cook-page-header">',
                '<div class="page-header-text">',
                    '<h1 class="page-title">Bake<button type="button" class="help-hook" data-help-topic="cooks.detail" aria-label="Help: About the Bake detail page" title="About the Bake detail page"><svg class="help-hook-icon" aria-hidden="true" focusable="false"><use href="#icon-question"></use></svg></button></h1>',
                '</div>',
                '<div class="cook-actions editor-actions">',
                    '<button type="button" id="cook-refresh-button" class="btn-ghost">Refresh</button>',
                    '<button type="button" id="cook-readiness-button" class="btn-ghost" hidden>Check readiness</button>',
                    '<button type="button" id="cook-stop-button" class="btn-primary" hidden>Stop</button>',
                    '<button type="button" id="cook-resume-button" class="btn-primary" hidden>Resume</button>',
                '</div>',
            '</header>',
            '<div id="cook-banner" class="editor-banner" hidden></div>',
            '<div id="cook-body" class="cook-body" hidden>',
                '<section class="cook-card" aria-labelledby="cook-card-status-h">',
                    '<h2 id="cook-card-status-h" class="cook-card-title">Status</h2>',
                    '<div class="cook-status-bar">',
                        '<span id="cook-status-chip" class="cook-status-chip status-unknown"></span>',
                        '<span id="cook-status-detail" class="cook-status-detail"></span>',
                    '</div>',
                    '<dl id="cook-meta" class="cook-meta"></dl>',
                '</section>',
                // V1.S06d: Scheduled-run evidence card. Visible only
                // when the cook was reconciled from a scheduled-task
                // wrapper folder (trigger='scheduled'). The card
                // surfaces the wrapper envelope JSON fields verbatim
                // -- the SPA never recomputes a wrapper field from
                // the live recipe or from PAX. Hidden for manual /
                // resume cooks.
                '<section id="cook-card-scheduled" class="cook-card" aria-labelledby="cook-card-scheduled-h" hidden>',
                    '<h2 id="cook-card-scheduled-h" class="cook-card-title">Scheduled run</h2>',
                    '<dl id="cook-scheduled-meta" class="cook-meta"></dl>',
                '</section>',
                // V1.S04: advisory readiness card for Resume.
                // Visible only when the cook is interrupted+resumable;
                // populated only after the operator clicks "Check
                // readiness". Readiness is informational and does NOT
                // grant resume; the broker re-checks resumability
                // and all start-time gates at the resume endpoint.
                '<section id="cook-readiness-card" class="cook-card editor-readiness" aria-labelledby="cook-card-readiness-h" hidden>',
                    '<h2 id="cook-card-readiness-h" class="cook-card-title">Readiness</h2>',
                    '<div class="editor-readiness-body">',
                        '<p class="editor-readiness-placeholder">Click “Check readiness” to run advisory pre-flight checks for this resume. Readiness is informational only; PAX remains authoritative.</p>',
                    '</div>',
                '</section>',
                '<section class="cook-card" aria-labelledby="cook-card-provenance-h">',
                    '<h2 id="cook-card-provenance-h" class="cook-card-title">Provenance</h2>',
                    '<dl id="cook-provenance" class="cook-provenance-grid"></dl>',
                '</section>',
                '<section class="cook-card" aria-labelledby="cook-card-summary-h">',
                    '<h2 id="cook-card-summary-h" class="cook-card-title">Summary &amp; metrics</h2>',
                    '<div id="cook-summary-metrics" class="cook-summary-metrics"></div>',
                '</section>',
                '<section class="cook-card" aria-labelledby="cook-card-command-h">',
                    '<h2 id="cook-card-command-h" class="cook-card-title">Command</h2>',
                    '<div id="cook-command" class="cook-command"></div>',
                '</section>',
                '<section class="cook-card cook-console-card" aria-labelledby="cook-card-console-h">',
                    '<h2 id="cook-card-console-h" class="cook-card-title">Console</h2>',
                    '<pre id="cook-console" class="cook-console" tabindex="0" role="region" aria-label="Bake log output (scrollable; press Tab to enter, arrow keys to scroll)"></pre>',
                '</section>',
                '<section class="cook-card" aria-labelledby="cook-card-sentinels-h">',
                    '<h2 id="cook-card-sentinels-h" class="cook-card-title">Sentinels</h2>',
                    '<div id="cook-sentinels" class="cook-sentinels"></div>',
                '</section>',
                '<section class="cook-card" aria-labelledby="cook-card-outputs-h">',
                    '<h2 id="cook-card-outputs-h" class="cook-card-title">Outputs</h2>',
                    '<div id="cook-artifacts" class="cook-artifacts"></div>',
                '</section>',
                '<section class="cook-card" aria-labelledby="cook-card-folder-h">',
                    '<h2 id="cook-card-folder-h" class="cook-card-title">Bake folder</h2>',
                    '<div id="cook-folder-artifacts" class="cook-folder-files"></div>',
                '</section>',
                '<section class="cook-card" aria-labelledby="cook-card-snapshot-h">',
                    '<h2 id="cook-card-snapshot-h" class="cook-card-title">Recipe snapshot</h2>',
                    '<div id="cook-recipe-snapshot" class="cook-recipe-snapshot"></div>',
                '</section>',
            '</div>',
        '</section>'
    ].join('');

    // ----------------------------------------------------------------
    // Page-scoped state
    //
    // Stale-drop pattern: every async op captures `var mySession = state`
    // at the moment it dispatches. When the promise resolves, it checks
    // `state !== mySession` and drops if the page has been re-mounted
    // (the user navigated away and back, or to a different cookId).
    // teardown() sets `state = null`, which is the canonical stale
    // sentinel.
    // ----------------------------------------------------------------
    var state    = null;
    var pageRoot = null;

    // Mirrors the latest cookbook:acquisitionStateChanged event from
    // pax-engine-overlay.js. When true the PAX engine is not yet
    // acquired; resuming a cook would round-trip a 409 from the
    // broker gate so we refuse locally and surface a tooltip on the
    // disabled Resume button. Module-scoped so the value survives
    // mount/teardown cycles.
    var acquisitionBlocked = false;

    // ----------------------------------------------------------------
    // DOM helpers (local-only, no global utility coupling)
    // ----------------------------------------------------------------

    function byId(id) {
        return pageRoot ? pageRoot.querySelector('#' + id) : null;
    }

    function clearChildren(el) {
        if (!el) { return; }
        while (el.firstChild) { el.removeChild(el.firstChild); }
    }

    function hideBanner() {
        var el = byId('cook-banner');
        if (!el) { return; }
        el.hidden = true;
        el.className = 'editor-banner';
        clearChildren(el);
    }

    function showBanner(kind, text) {
        var el = byId('cook-banner');
        if (!el) { return; }
        el.hidden = false;
        el.className = 'editor-banner banner-' + kind;
        clearChildren(el);
        el.appendChild(document.createTextNode(text));
    }

    // ----------------------------------------------------------------
    // Console card -- opaque text append helpers.
    //
    // appendConsoleText writes a SINGLE immutable text node into the
    // Console pre. It is used for BOTH historical hydration (whole
    // cook.log body from GET /log) and live-tail frames (individual
    // chunks from the /log/ws socket). Both sources are opaque UTF-8
    // text; the helper does not distinguish them. The seam marker
    // appended between them carries no replay / dedup / sync semantics
    // and exists purely as a positional anchor in the DOM.
    //
    // Append-only doctrine: no node is ever mutated after being
    // appended. No string is ever rebuilt. No content is ever
    // reinterpreted (no stripping of '[STDERR] ', no prefix
    // classification, no marker normalization, no JSON.parse of frame
    // payloads). Whatever the broker emits is what the user sees,
    // byte for byte.
    //
    // Scroll follow-tail-only-if-at-bottom: if the user has scrolled
    // up away from the live edge, appends do NOT yank the viewport to
    // the bottom. We sample the at-bottom state BEFORE appending and
    // only re-anchor after the append if the user was already pinned
    // to the bottom (within a small sub-pixel threshold).
    // ----------------------------------------------------------------

    var SCROLL_AT_BOTTOM_THRESHOLD = 4;

    function consoleEl() {
        return byId('cook-console');
    }

    function wasConsoleAtBottom() {
        var el = consoleEl();
        if (!el) { return false; }
        return (el.scrollHeight - el.clientHeight - el.scrollTop)
            <= SCROLL_AT_BOTTOM_THRESHOLD;
    }

    function scrollConsoleToBottom() {
        var el = consoleEl();
        if (!el) { return; }
        el.scrollTop = el.scrollHeight;
    }

    function appendConsoleText(rawText) {
        var el = consoleEl();
        if (!el) { return; }
        var wasAtBottom = wasConsoleAtBottom();
        el.appendChild(document.createTextNode(rawText));
        if (wasAtBottom) { scrollConsoleToBottom(); }
    }

    function appendSeamMarker() {
        var el = consoleEl();
        if (!el) { return; }
        var seam = document.createElement('div');
        seam.className = 'cook-console-seam';
        seam.setAttribute('aria-hidden', 'true');
        el.appendChild(seam);
    }

    function appendConsoleNotice(kind, text) {
        var el = consoleEl();
        if (!el) { return; }
        var notice = document.createElement('div');
        notice.className = 'cook-console-notice cook-console-notice-' + kind;
        notice.textContent = text;
        el.appendChild(notice);
    }

    // ----------------------------------------------------------------
    // Rendering
    // ----------------------------------------------------------------

    function setStatusChip(status) {
        var chip = byId('cook-status-chip');
        if (!chip) { return; }
        var s = (status === null || status === undefined) ? '' : String(status);
        chip.textContent = s ? s : '(unknown)';
        chip.className = 'cook-status-chip status-' + (s ? s : 'unknown');
    }

    function metaPair(label, value) {
        var dt = document.createElement('dt');
        dt.className = 'cook-meta-label';
        dt.textContent = label;
        var dd = document.createElement('dd');
        dd.className = 'cook-meta-value';
        if (value === null || value === undefined || value === '') {
            dd.textContent = '\u2014';
            dd.classList.add('cook-meta-empty');
        } else {
            dd.textContent = String(value);
        }
        return [dt, dd];
    }

    function renderMeta(cook, recipeName) {
        var dl = byId('cook-meta');
        if (!dl) { return; }
        clearChildren(dl);
        var rows = [
            ['Recipe name',      recipeName],
            ['Bake ID',          cook.cookId],
            ['Recipe ID',        cook.recipeId],
            ['Trigger',          cook.trigger],
            ['Started (UTC)',    cook.startedAt],
            ['Finished (UTC)',   cook.finishedAt],
            ['Duration (sec)',   cook.durationSeconds],
            ['Exit code',        cook.exitCode],
            ['Error class',      cook.errorClass],
            ['Error message',    cook.errorMessage],
            ['PID',              cook.pid],
            ['Bake folder',      cook.cookFolder],
            ['PAX script',       cook.paxScriptPath],
            ['Adapter version',  cook.paxScriptVersion],
            ['Created (UTC)',    cook.createdAt],
            ['Updated (UTC)',    cook.updatedAt]
        ];
        for (var i = 0; i < rows.length; i++) {
            var pair = metaPair(rows[i][0], rows[i][1]);
            dl.appendChild(pair[0]);
            dl.appendChild(pair[1]);
        }
    }

    // V1.S06d -- scheduled-run evidence card. The card is shown only
    // when the broker reports trigger='scheduled' AND returned a
    // non-null wrapperEnvelopes object. For manual/resume cooks the
    // card stays hidden. Field accessors are all null-tolerant.
    function renderScheduledRun(cook, wrapperEnvelopes) {
        var card = byId('cook-card-scheduled');
        var dl   = byId('cook-scheduled-meta');
        if (!card || !dl) { return; }
        clearChildren(dl);
        var isScheduled = (cook && String(cook.trigger || '') === 'scheduled');
        if (!isScheduled || !wrapperEnvelopes) {
            card.hidden = true;
            return;
        }
        var started  = wrapperEnvelopes.started  || null;
        var finished = wrapperEnvelopes.finished || null;
        var refused  = wrapperEnvelopes.refused  || null;
        function pick(o, k) {
            if (!o) { return null; }
            try {
                var v = o[k];
                if (v === undefined || v === null) { return null; }
                if (typeof v === 'object') {
                    // For nested wrapper objects (e.g. refused.detail),
                    // stringify so the meta cell can render verbatim
                    // without the SPA imposing a schema.
                    try { return JSON.stringify(v); } catch (e) { return String(v); }
                }
                return v;
            } catch (e) { return null; }
        }
        var rows = [
            ['Scheduled task ID',           pick(started, 'scheduledTaskId')],
            ['Windows task name',           pick(started, 'windowsTaskName')],
            ['Recipe projection hash',      pick(started, 'recipeProjectionHash')],
            ['Chef\u2019s Key (at fire)',   pick(started, 'authProfileId')],
            ['Auth mode (at fire)',         pick(started, 'authMode')],
            ['Wrapper host',                pick(started, 'wrapperHost')],
            ['Wrapper user',                pick(started, 'wrapperUser')],
            ['Execution mode',              pick(started, 'executionMode')],
            ['PAX script (at fire)',        pick(started, 'paxScriptPath')],
            ['PAX script version (at fire)', pick(started, 'paxScriptVersion')],
            ['Wrapper started (UTC)',       pick(started,  'startedAt')],
            ['Wrapper finished (UTC)',      pick(finished, 'finishedAt')],
            ['Wrapper outcome',             pick(finished, 'wrapperOutcome')],
            ['Wrapper reason',              pick(finished, 'wrapperReason')],
            ['PAX exit code',               pick(finished, 'paxExitCode')],
            ['Duration (ms)',               pick(finished, 'durationMs')],
            ['PID (wrapper-recorded)',      pick(finished, 'pidValue')]
        ];
        if (refused) {
            rows.push(['Refused at (UTC)',  pick(refused, 'refusedAt')]);
            rows.push(['Refusal reason',    pick(refused, 'reason')]);
            rows.push(['Refusal detail',    pick(refused, 'detail')]);
        }
        for (var i = 0; i < rows.length; i++) {
            var pair = metaPair(rows[i][0], rows[i][1]);
            dl.appendChild(pair[0]);
            dl.appendChild(pair[1]);
        }
        card.hidden = false;
    }

    function renderSummaryMetrics(paxSummary, metrics, metricSummary, summaryPath) {
        // Surfaces three things, all defensively:
        //   1. Metric chips for safe, universally-named keys from
        //      either pax-summary.json or the parsed metrics file
        //      (rowCount, outputCount, warningCount, errorCount,
        //       durationSeconds). Only chips that resolved to a finite
        //      numeric appear; missing ones are omitted, NOT shown
        //      as zero.
        //   2. The pax-summary.json content as a collapsible JSON
        //      block, or an explicit "not emitted" / "parse failed"
        //      empty state.
        //   3. The metrics file content as a collapsible JSON block
        //      with the discovered file path, or an explicit "not
        //      emitted" / "parse failed" empty state.
        //
        // Nothing here computes business semantics from the JSON --
        // we render exactly what the broker returned. The cook never
        // fails when these files are absent or malformed.
        var box = byId('cook-summary-metrics');
        if (!box) { return; }
        clearChildren(box);

        // ---- Metric chips ----
        var chipRow = document.createElement('div');
        chipRow.className = 'cook-summary-chips';
        var ms = metricSummary || {};
        var chips = [
            ['Rows produced',  ms.rowCount,        'cook-summary-chip-rows'],
            ['Output files',   ms.outputCount,     'cook-summary-chip-outputs'],
            ['Warnings',       ms.warningCount,    'cook-summary-chip-warnings'],
            ['Errors',         ms.errorCount,      'cook-summary-chip-errors'],
            ['Duration (sec)', ms.durationSeconds, 'cook-summary-chip-duration']
        ];
        var chipCount = 0;
        for (var i = 0; i < chips.length; i++) {
            var v = chips[i][1];
            if (v === null || v === undefined) { continue; }
            chipCount++;
            var chip = document.createElement('div');
            chip.className = 'cook-summary-chip ' + chips[i][2];
            var lbl = document.createElement('span');
            lbl.className = 'cook-summary-chip-label';
            lbl.textContent = chips[i][0];
            var val = document.createElement('span');
            val.className = 'cook-summary-chip-value';
            val.textContent = String(v);
            chip.appendChild(lbl);
            chip.appendChild(val);
            chipRow.appendChild(chip);
        }
        if (chipCount === 0) {
            var none = document.createElement('p');
            none.className = 'cook-empty';
            none.textContent = 'No metric summary available for this cook.';
            box.appendChild(none);
        } else {
            box.appendChild(chipRow);
        }

        // ---- pax-summary.json ----
        var sBlock = document.createElement('div');
        sBlock.className = 'cook-summary-block';
        var sH = document.createElement('h3');
        sH.className = 'cook-summary-section-title';
        sH.textContent = 'pax-summary.json';
        sBlock.appendChild(sH);
        if (!paxSummary) {
            var sNone = document.createElement('p');
            sNone.className = 'cook-empty';
            sNone.textContent = 'Not emitted for this cook.';
            sBlock.appendChild(sNone);
        } else if (paxSummary._parseError) {
            var sErr = document.createElement('p');
            sErr.className = 'cook-summary-error';
            sErr.textContent = 'pax-summary.json failed to parse: '
                + String(paxSummary._parseError);
            sBlock.appendChild(sErr);
        } else {
            var sDet = document.createElement('details');
            sDet.className = 'cook-summary-details';
            var sSum = document.createElement('summary');
            sSum.className = 'cook-summary-disclosure';
            sSum.textContent = 'Show pax-summary.json';
            sDet.appendChild(sSum);
            var sPre = document.createElement('pre');
            sPre.className = 'cook-summary-body';
            try {
                sPre.textContent = JSON.stringify(paxSummary, null, 2);
            } catch (e) {
                sPre.textContent = String(paxSummary);
            }
            sDet.appendChild(sPre);
            sBlock.appendChild(sDet);
        }
        box.appendChild(sBlock);

        // ---- metrics JSON ----
        var mBlock = document.createElement('div');
        mBlock.className = 'cook-summary-block';
        var mH = document.createElement('h3');
        mH.className = 'cook-summary-section-title';
        mH.textContent = 'Metrics JSON';
        mBlock.appendChild(mH);
        if (!metrics) {
            var mNone = document.createElement('p');
            mNone.className = 'cook-empty';
            mNone.textContent = 'Not emitted for this cook.';
            mBlock.appendChild(mNone);
        } else if (metrics._parseError) {
            if (metrics._path) {
                var mErrPath = document.createElement('p');
                mErrPath.className = 'cook-summary-path';
                mErrPath.textContent = 'Path: ' + String(metrics._path);
                mBlock.appendChild(mErrPath);
            }
            var mErr = document.createElement('p');
            mErr.className = 'cook-summary-error';
            mErr.textContent = 'Metrics file failed to parse: '
                + String(metrics._parseError);
            mBlock.appendChild(mErr);
        } else {
            if (metrics._path) {
                var mPathRow = document.createElement('p');
                mPathRow.className = 'cook-summary-path';
                var mPathLbl = document.createElement('span');
                mPathLbl.className = 'cook-summary-path-label';
                mPathLbl.textContent = 'Path: ';
                mPathRow.appendChild(mPathLbl);
                var mPathVal = document.createElement('code');
                mPathVal.className = 'cook-summary-path-value';
                mPathVal.textContent = String(metrics._path);
                mPathRow.appendChild(mPathVal);
                mBlock.appendChild(mPathRow);
            }
            var mDet = document.createElement('details');
            mDet.className = 'cook-summary-details';
            var mSum = document.createElement('summary');
            mSum.className = 'cook-summary-disclosure';
            mSum.textContent = 'Show metrics JSON';
            mDet.appendChild(mSum);
            var mPre = document.createElement('pre');
            mPre.className = 'cook-summary-body';
            try {
                // Strip the synthetic _path key from the rendered
                // payload so the operator sees only what was on disk.
                var rendered = {};
                for (var k in metrics) {
                    if (!Object.prototype.hasOwnProperty.call(metrics, k)) { continue; }
                    if (k === '_path') { continue; }
                    rendered[k] = metrics[k];
                }
                mPre.textContent = JSON.stringify(rendered, null, 2);
            } catch (e) {
                mPre.textContent = String(metrics);
            }
            mDet.appendChild(mPre);
            mBlock.appendChild(mDet);
        }
        box.appendChild(mBlock);

        // ---- summary_path provenance (persisted on cooks row) ----
        if (summaryPath) {
            var spRow = document.createElement('p');
            spRow.className = 'cook-summary-path cook-summary-persisted-path';
            var spLbl = document.createElement('span');
            spLbl.className = 'cook-summary-path-label';
            spLbl.textContent = 'Recorded summary path (cooks.summary_path): ';
            spRow.appendChild(spLbl);
            var spVal = document.createElement('code');
            spVal.className = 'cook-summary-path-value';
            spVal.textContent = String(summaryPath);
            spRow.appendChild(spVal);
            box.appendChild(spRow);
        }
    }

    function renderDiscoveredFiles(discoveredOutputs) {
        // Append a 'Discovered output files' table to the existing
        // Outputs card showing additional output-shaped files in the
        // cook folder and around the fact destination that the broker
        // observed at GET time. Stable empty state when none.
        var box = byId('cook-artifacts');
        if (!box) { return; }
        var rows = Array.isArray(discoveredOutputs) ? discoveredOutputs : [];
        if (rows.length === 0) { return; }
        var hdr = document.createElement('h3');
        hdr.className = 'cook-summary-section-title cook-discovered-title';
        hdr.textContent = 'Discovered output files';
        box.appendChild(hdr);
        var table = document.createElement('table');
        table.className = 'cook-artifact-table cook-discovered-table';
        var thead = document.createElement('thead');
        var thr   = document.createElement('tr');
        var hdrs  = ['Name', 'Kind', 'Source', 'Path', 'Size (bytes)', 'Modified (UTC)'];
        for (var h = 0; h < hdrs.length; h++) {
            var th = document.createElement('th');
            th.textContent = hdrs[h];
            thr.appendChild(th);
        }
        thead.appendChild(thr);
        table.appendChild(thead);
        var tbody = document.createElement('tbody');
        for (var i = 0; i < rows.length; i++) {
            var r = rows[i] || {};
            var tr = document.createElement('tr');
            var cells = [
                r.name,
                r.kind,
                r.source,
                r.path,
                (r.sizeBytes === null || r.sizeBytes === undefined) ? null : String(r.sizeBytes),
                r.modifiedAt
            ];
            for (var c = 0; c < cells.length; c++) {
                var td = document.createElement('td');
                var v = cells[c];
                if (v === null || v === undefined || v === '') {
                    td.textContent = '\u2014';
                    td.classList.add('cook-artifact-empty');
                } else {
                    td.textContent = String(v);
                }
                tr.appendChild(td);
            }
            tbody.appendChild(tr);
        }
        table.appendChild(tbody);
        box.appendChild(table);
    }

    function renderSentinels(sentinels) {
        var box = byId('cook-sentinels');
        if (!box) { return; }
        clearChildren(box);
        var names = ['started.json', 'finished.json', 'interrupted.json'];
        var found = 0;
        for (var i = 0; i < names.length; i++) {
            var name = names[i];
            if (!sentinels || !Object.prototype.hasOwnProperty.call(sentinels, name)) { continue; }
            found++;
            var details = document.createElement('details');
            details.className = 'cook-sentinel';
            var summary = document.createElement('summary');
            summary.className = 'cook-sentinel-name';
            summary.textContent = name;
            details.appendChild(summary);
            var pre = document.createElement('pre');
            pre.className = 'cook-sentinel-body';
            var body = sentinels[name];
            try {
                // textContent assignment only -- no innerHTML anywhere
                // in this file. Sentinels are persisted JSON and may
                // contain user-controlled fields, so this is deliberate.
                pre.textContent = JSON.stringify(body, null, 2);
            } catch (e) {
                pre.textContent = String(body);
            }
            details.appendChild(pre);
            box.appendChild(details);
        }
        if (found === 0) {
            var p = document.createElement('p');
            p.className = 'cook-empty';
            p.textContent = 'No sentinels written yet.';
            box.appendChild(p);
        }
    }

    function renderArtifacts(outputs) {
        // Output-file references from cook_artifacts joined with a
        // fresh existence/size check by the broker. Cookbook surfaces
        // references and visibility only -- the contents of these
        // files are NEVER fetched, parsed, or interpreted here.
        var box = byId('cook-artifacts');
        if (!box) { return; }
        clearChildren(box);
        var rows = Array.isArray(outputs) ? outputs : [];
        // V1.S06d -- for scheduled-task cooks the PAX engine log is
        // the single most operationally interesting artifact; surface
        // it first when present. We sort a SHALLOW copy so the
        // upstream array is untouched. The sort is a stable
        // partition: pax_log rows preserve their relative order, and
        // non-pax_log rows preserve theirs.
        if (rows.length > 1) {
            var sorted = rows.slice();
            sorted.sort(function (a, b) {
                var aLog = (a && a.stream === 'pax_log') ? 0 : 1;
                var bLog = (b && b.stream === 'pax_log') ? 0 : 1;
                return aLog - bLog;
            });
            rows = sorted;
        }
        if (rows.length === 0) {
            var p = document.createElement('p');
            p.className = 'cook-empty';
            p.textContent = 'No outputs recorded yet.';
            box.appendChild(p);
            return;
        }
        var table = document.createElement('table');
        table.className = 'cook-artifact-table';
        var thead = document.createElement('thead');
        var thr   = document.createElement('tr');
        var hdrs  = [
            'Stream', 'Kind', 'Tier', 'Location',
            'Recorded size', 'Current size', 'On disk', 'Modified (UTC)', 'Created (UTC)'
        ];
        for (var h = 0; h < hdrs.length; h++) {
            var th = document.createElement('th');
            th.textContent = hdrs[h];
            thr.appendChild(th);
        }
        thead.appendChild(thr);
        table.appendChild(thead);
        var tbody = document.createElement('tbody');
        for (var i = 0; i < rows.length; i++) {
            var r = rows[i] || {};
            var tr = document.createElement('tr');
            var cells = [
                r.stream,
                r.artifactKind,
                r.tier,
                r.location,
                (r.recordedSizeBytes === null || r.recordedSizeBytes === undefined) ? null : String(r.recordedSizeBytes),
                (r.currentSizeBytes  === null || r.currentSizeBytes  === undefined) ? null : String(r.currentSizeBytes),
                (r.existsOnDisk ? 'yes' : 'no'),
                r.modifiedAt,
                r.createdAt
            ];
            for (var c = 0; c < cells.length; c++) {
                var td = document.createElement('td');
                var v = cells[c];
                if (v === null || v === undefined || v === '') {
                    td.textContent = '\u2014';
                    td.classList.add('cook-artifact-empty');
                } else {
                    td.textContent = String(v);
                }
                tr.appendChild(td);
            }
            // Visual signal: missing file gets a row class so CSS can
            // dim/strike it. Pure CSS hint; no behavior change.
            if (!r.existsOnDisk) {
                tr.classList.add('cook-artifact-row-missing');
            }
            tbody.appendChild(tr);
        }
        table.appendChild(tbody);
        box.appendChild(table);
    }

    function renderProvenance(context, cook, recipeName) {
        // Per-cook provenance evidence sourced from cook-context.json
        // (createdBy + host) and the cooks row (trigger). Every field
        // below is read straight from the persisted execution
        // snapshot. NEVER read from the current recipes row -- the
        // recipe may have been edited or deleted since this cook ran.
        var dl = byId('cook-provenance');
        if (!dl) { return; }
        clearChildren(dl);
        var createdBy = (context && context.createdBy) ? context.createdBy : {};
        var bundledPax = (context && context.bundledPax) ? context.bundledPax : {};
        var rows = [
            ['Recipe name',          recipeName],
            ['Recipe ID',            cook ? cook.recipeId : null],
            ['Cookbook version',     createdBy.cookbookVersion],
            ['Bundled PAX version',  createdBy.bundledPaxVersion],
            ['Release channel',      createdBy.releaseChannel],
            ['Bundled PAX path',     bundledPax.path],
            ['Bundled PAX sha256',   bundledPax.sha256],
            ['Schema version',       context ? context.schemaVersion : null],
            ['Trigger',              cook ? cook.trigger : null],
            ['Host',                 context ? context.host : null],
            ['Context recorded at',  context ? context.createdAt : null]
        ];
        for (var i = 0; i < rows.length; i++) {
            var pair = metaPair(rows[i][0], rows[i][1]);
            dl.appendChild(pair[0]);
            dl.appendChild(pair[1]);
        }
        if (!context) {
            var p = document.createElement('p');
            p.className = 'cook-empty';
            p.textContent = 'No cook-context.json on disk for this cook (older broker or pre-spawn failure).';
            dl.parentNode.appendChild(p);
        }
    }

    function renderArgvList(items, className) {
        // Ordered list of opaque argv tokens. Each token is rendered
        // verbatim as a text node inside a mono <li>. No shell
        // escaping, no quoting, no concatenation -- the argv array
        // IS the wire-level invocation.
        var ol = document.createElement('ol');
        ol.className = className;
        if (!Array.isArray(items)) { return ol; }
        for (var i = 0; i < items.length; i++) {
            var li = document.createElement('li');
            li.className = 'cook-command-token';
            li.textContent = (items[i] === null || items[i] === undefined) ? '' : String(items[i]);
            ol.appendChild(li);
        }
        return ol;
    }

    function renderCommand(command) {
        // The AUTHORITATIVE persisted invocation evidence: paxArgv
        // (the token list passed to the PAX script), spawnArgv (the
        // pwsh.exe wrapper command line that actually launched the
        // subprocess), the extraArguments verbatim string from the
        // recipe, the resolved paxScriptPath, and the human-form
        // paxCommand string from command.txt. These come from
        // <CookFolder>/command-argv.json and <CookFolder>/command.txt
        // -- frozen at cook-start by the same Get-PaxInvocationPlan
        // call that drove the actual spawn. NEVER reconstructed from
        // the current recipe row.
        var box = byId('cook-command');
        if (!box) { return; }
        clearChildren(box);
        if (!command || (!command.paxArgv && !command.spawnArgv && !command.paxCommand)) {
            var pNone = document.createElement('p');
            pNone.className = 'cook-empty';
            pNone.textContent = 'No command-argv.json / command.txt on disk for this cook.';
            box.appendChild(pNone);
            return;
        }

        // Source-of-truth label: which file each field came from.
        var src = document.createElement('p');
        src.className = 'cook-command-source';
        src.textContent = 'Projection source: ' + (command.projectionSource
            || 'frozen at cook-start by Get-PaxInvocationPlan');
        box.appendChild(src);

        // paxCommand (string from command.txt) -- the human-readable
        // approximation. Authoritative argv is the list below; this
        // string is for operator scanning only.
        if (command.paxCommand) {
            var lbl1 = document.createElement('h3');
            lbl1.className = 'cook-command-label';
            lbl1.textContent = 'paxCommand (command.txt)';
            box.appendChild(lbl1);
            var pre = document.createElement('pre');
            pre.className = 'cook-command-text';
            pre.textContent = String(command.paxCommand);
            box.appendChild(pre);
        }

        // paxArgv -- the authoritative argv passed to the PAX script.
        var lbl2 = document.createElement('h3');
        lbl2.className = 'cook-command-label';
        lbl2.textContent = 'paxArgv (command-argv.json)';
        box.appendChild(lbl2);
        box.appendChild(renderArgvList(command.paxArgv, 'cook-command-argv-list'));

        // spawnArgv -- the pwsh wrapper that actually launched the
        // child process. Useful for diagnosing host / runtime issues.
        var lbl3 = document.createElement('h3');
        lbl3.className = 'cook-command-label';
        lbl3.textContent = 'spawnArgv (command-argv.json)';
        box.appendChild(lbl3);
        box.appendChild(renderArgvList(command.spawnArgv, 'cook-command-argv-list'));

        // Extras: resolved paxScriptPath + verbatim extraArguments
        // string. Both come from command-argv.json.
        var dl = document.createElement('dl');
        dl.className = 'cook-command-meta';
        var pairs = [
            ['PAX script path',     command.paxScriptPath],
            ['Extra arguments',     command.extraArguments]
        ];
        for (var i = 0; i < pairs.length; i++) {
            var pair = metaPair(pairs[i][0], pairs[i][1]);
            dl.appendChild(pair[0]);
            dl.appendChild(pair[1]);
        }
        box.appendChild(dl);
    }

    function renderFolderArtifacts(folderArtifacts) {
        // Filesystem-authoritative references to the metadata files
        // the supervisor wrote into the cook folder. Cookbook
        // surfaces the path + existence + size only; never reads
        // content here. (The parsed JSON for the key files is
        // delivered inline elsewhere on the page via the dedicated
        // cards above.)
        var box = byId('cook-folder-artifacts');
        if (!box) { return; }
        clearChildren(box);
        var rows = Array.isArray(folderArtifacts) ? folderArtifacts : [];
        if (rows.length === 0) {
            var p = document.createElement('p');
            p.className = 'cook-empty';
            p.textContent = 'No cook folder recorded for this cook.';
            box.appendChild(p);
            return;
        }
        var table = document.createElement('table');
        table.className = 'cook-folder-table';
        var thead = document.createElement('thead');
        var thr   = document.createElement('tr');
        var hdrs  = ['File', 'Path', 'On disk', 'Size (bytes)', 'Modified (UTC)'];
        for (var h = 0; h < hdrs.length; h++) {
            var th = document.createElement('th');
            th.textContent = hdrs[h];
            thr.appendChild(th);
        }
        thead.appendChild(thr);
        table.appendChild(thead);
        var tbody = document.createElement('tbody');
        for (var i = 0; i < rows.length; i++) {
            var r = rows[i] || {};
            var tr = document.createElement('tr');
            var cells = [
                r.name,
                r.path,
                (r.exists ? 'yes' : 'no'),
                (r.sizeBytes === null || r.sizeBytes === undefined) ? null : String(r.sizeBytes),
                r.modifiedAt
            ];
            for (var c = 0; c < cells.length; c++) {
                var td = document.createElement('td');
                var v = cells[c];
                if (v === null || v === undefined || v === '') {
                    td.textContent = '\u2014';
                    td.classList.add('cook-artifact-empty');
                } else {
                    td.textContent = String(v);
                }
                tr.appendChild(td);
            }
            if (!r.exists) {
                tr.classList.add('cook-artifact-row-missing');
            }
            tbody.appendChild(tr);
        }
        table.appendChild(tbody);
        box.appendChild(table);
    }

    function renderRecipeSnapshot(snapshot, cookFolder) {
        // The frozen recipe-snapshot.json for this cook, surfaced as
        // a collapsed inspectable blob. The snapshot is authoritative
        // for "what recipe was executed" and is intentionally
        // independent of the current recipes row. The displayed text
        // is JSON.stringify of the parsed object -- never the result
        // of any field synthesis or normalization.
        var box = byId('cook-recipe-snapshot');
        if (!box) { return; }
        clearChildren(box);

        // Always surface the on-disk path reference, even if the file
        // is missing. This is a key piece of operational evidence.
        var pathRow = document.createElement('p');
        pathRow.className = 'cook-snapshot-path';
        var pathLbl = document.createElement('span');
        pathLbl.className = 'cook-snapshot-path-label';
        pathLbl.textContent = 'Path: ';
        pathRow.appendChild(pathLbl);
        var pathVal = document.createElement('code');
        pathVal.className = 'cook-snapshot-path-value';
        pathVal.textContent = cookFolder
            ? (cookFolder.replace(/[\\\/]+$/, '') + '\\recipe-snapshot.json')
            : '\u2014';
        pathRow.appendChild(pathVal);
        box.appendChild(pathRow);

        if (!snapshot) {
            var p = document.createElement('p');
            p.className = 'cook-empty';
            p.textContent = 'No recipe-snapshot.json on disk for this cook.';
            box.appendChild(p);
            return;
        }
        if (snapshot._parseError) {
            var perr = document.createElement('p');
            perr.className = 'cook-snapshot-error';
            perr.textContent = 'recipe-snapshot.json failed to parse: ' + String(snapshot._parseError);
            box.appendChild(perr);
            return;
        }
        var details = document.createElement('details');
        details.className = 'cook-snapshot-details';
        var summary = document.createElement('summary');
        summary.className = 'cook-snapshot-summary';
        summary.textContent = 'Show recipe snapshot JSON';
        details.appendChild(summary);
        var pre = document.createElement('pre');
        pre.className = 'cook-snapshot-body';
        try {
            pre.textContent = JSON.stringify(snapshot, null, 2);
        } catch (e) {
            pre.textContent = String(snapshot);
        }
        details.appendChild(pre);
        box.appendChild(details);
    }

    function setStatusDetail(cook) {
        var el = byId('cook-status-detail');
        if (!el) { return; }
        var bits = [];
        if (cook.status === 'errored' || cook.status === 'interrupted') {
            if (cook.errorClass)   { bits.push(cook.errorClass); }
            if (cook.errorMessage) { bits.push(cook.errorMessage); }
        }
        if (cook.status === 'completed' && cook.exitCode !== null && cook.exitCode !== undefined) {
            bits.push('exit ' + cook.exitCode);
        }
        // V1.S03 -- interrupted cooks that carry a checkpoint are
        // resumable. The badge is rendered inline next to the
        // errorClass / errorMessage so the operator sees in one
        // glance both WHY the cook ended and that a Resume action
        // is available. The chip itself stays 'interrupted'; only
        // the derived suffix changes.
        if (cook.status === 'interrupted' && state && state.resumable === true) {
            bits.push('resumable');
        }
        el.textContent = bits.join(' \u2014 ');
    }

    function applyButtonStates() {
        if (!state) { return; }
        var stop    = byId('cook-stop-button');
        var resume  = byId('cook-resume-button');
        var refresh = byId('cook-refresh-button');
        var readiness = byId('cook-readiness-button');
        var readinessCard = byId('cook-readiness-card');
        var isRunning  = state.lastStatus === 'running';
        var isResumable = state.lastStatus === 'interrupted' && state.resumable === true;
        if (stop) {
            // Stop button is visible iff the persisted status is running,
            // OR a stop POST is in flight (so the "Stopping..." label is
            // visible until the user refreshes and sees terminal state).
            stop.hidden   = !isRunning && !state.stopInFlight;
            stop.disabled = state.stopInFlight || state.loadInFlight || !isRunning;
            stop.textContent = state.stopInFlight ? 'Stopping\u2026' : 'Stop';
        }
        if (resume) {
            // V1.S03 -- Resume button is visible iff the persisted
            // status is 'interrupted' AND the broker has confirmed
            // a checkpoint file is present on disk (body.resumable
            // is the derived boolean from Test-CookResumable),
            // OR a resume POST is in flight (so the "Resuming..."
            // label stays visible until the navigate-to-new-cook
            // hand-off completes). Resumability is a per-load
            // truth; we never speculate, we only mirror what the
            // /api/v1/cooks/<id> GET told us.
            resume.hidden   = !isResumable && !state.resumeInFlight;
            resume.disabled = state.resumeInFlight || state.loadInFlight || state.stopInFlight || state.readinessInFlight || (!isResumable) || acquisitionBlocked;
            resume.textContent = state.resumeInFlight ? 'Resuming\u2026' : 'Resume';
            if (acquisitionBlocked && !resume.hidden) {
                resume.title = 'PAX engine acquisition required';
            } else {
                resume.removeAttribute('title');
            }
        }
        // V1.S04 -- readiness button and card mirror Resume's
        // visibility. There is no point pre-flighting a cook that
        // cannot be resumed.
        if (readiness) {
            readiness.hidden   = !isResumable;
            readiness.disabled = state.loadInFlight || state.stopInFlight || state.resumeInFlight || state.readinessInFlight || (!isResumable);
            readiness.textContent = state.readinessInFlight ? 'Checking\u2026' : 'Check readiness';
        }
        if (readinessCard) {
            readinessCard.hidden = !isResumable;
        }
        if (refresh) {
            refresh.disabled = state.loadInFlight;
        }
    }

    function setBreadcrumb(cookId) {
        var el = byId('cook-breadcrumb-trailing');
        if (!el) { return; }
        var shortId = cookId ? cookId.substring(0, 8) : '';
        el.textContent = shortId ? ('Cook ' + shortId) : 'Cook';
    }

    function renderCook(body) {
        if (!state) { return; }
        var cook         = (body && body.cook)              ? body.cook              : {};
        var sentinels    = (body && body.sentinels)         ? body.sentinels         : {};
        var outputs      = (body && body.outputs)           ? body.outputs           : [];
        var ctx          = (body && body.context)           ? body.context           : null;
        var command      = (body && body.command)           ? body.command           : null;
        var snapshot     = (body && body.recipeSnapshot)    ? body.recipeSnapshot    : null;
        var recipeName   = (body && body.recipeName)        ? body.recipeName        : null;
        var folderArts   = (body && body.folderArtifacts)   ? body.folderArtifacts   : [];
        var paxSummary   = (body && body.paxSummary)        ? body.paxSummary        : null;
        var metrics      = (body && body.metrics)           ? body.metrics           : null;
        var discovered   = (body && body.discoveredOutputs) ? body.discoveredOutputs : [];
        var metricSum    = (body && body.metricSummary)     ? body.metricSummary     : null;
        // V1.S06d -- the broker returns this only for scheduled-task
        // cooks (trigger='scheduled') whose wrapper folder is still
        // on disk. For manual / resume cooks it is null; for
        // scheduled cooks whose folder was deleted out from under
        // the broker it is also null. renderScheduledRun() handles
        // both shapes by hiding the card.
        var wrapperEnvs  = (body && body.wrapperEnvelopes)  ? body.wrapperEnvelopes  : null;

        state.lastStatus = cook.status ? String(cook.status) : '';

        // V1.S03 -- mirror the resumability projection from the
        // /api/v1/cooks/<id> response body. The broker computes
        // these via Test-CookResumable; the SPA never re-derives
        // them. resumable is a strict bool; resumeReason is the
        // refusal code (e.g. 'checkpoint_missing') when resumable
        // is false; checkpointPath and parentCookId are advisory
        // fields surfaced for operator diagnostics. closureReason
        // is rendered in the Status card for terminal cooks.
        state.resumable       = !!(body && body.resumable);
        state.resumeReason    = (body && body.resumeReason)    ? String(body.resumeReason)    : '';
        state.checkpointPath  = (body && body.checkpointPath)  ? String(body.checkpointPath)  : '';
        state.parentCookId    = (body && body.parentCookId)    ? String(body.parentCookId)    : '';
        state.closureReason   = (body && body.closureReason)   ? String(body.closureReason)   : '';

        var bodyEl = byId('cook-body');
        if (bodyEl) { bodyEl.hidden = false; }

        setBreadcrumb(cook.cookId || state.cookId);
        setStatusChip(cook.status);
        setStatusDetail(cook);
        renderMeta(cook, recipeName);
        renderScheduledRun(cook, wrapperEnvs);
        renderProvenance(ctx, cook, recipeName);
        renderSummaryMetrics(paxSummary, metrics, metricSum, cook.summaryPath);
        renderCommand(command);
        renderSentinels(sentinels);
        renderArtifacts(outputs);
        renderDiscoveredFiles(discovered);
        renderFolderArtifacts(folderArts);
        renderRecipeSnapshot(snapshot, cook.cookFolder);

        // If the persisted status is terminal, any in-flight stop
        // request is implicitly resolved.
        if (state.lastStatus !== 'running') {
            state.stopInFlight = false;
        }
        // V1.S03 -- if the persisted status flipped away from
        // 'interrupted' (e.g. the operator already resumed it from
        // another tab and we're now seeing the child), any
        // in-flight resume request is implicitly resolved. The
        // navigate-to-new-cook hand-off in doResume() also clears
        // the flag on success; this is the belt-and-braces clear
        // for the refresh-after-resume edge case.
        if (state.lastStatus !== 'interrupted') {
            state.resumeInFlight = false;
        }
        applyButtonStates();
    }

    // ----------------------------------------------------------------
    // Fetch / stop
    // ----------------------------------------------------------------

    function doLoad() {
        if (!state || state.loadInFlight) { return; }
        var mySession = state;
        var cookId    = state.cookId;
        state.loadInFlight = true;
        hideBanner();
        applyButtonStates();

        window.cookbookApi.get('/api/v1/cooks/' + encodeURIComponent(cookId), { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (state !== mySession) { return; }
            state.loadInFlight = false;

            if (resp.networkError) {
                showBanner('error', 'Could not reach broker: ' + resp.networkError + '.');
                applyButtonStates();
                return;
            }
            if (resp.status === 400 && resp.body && resp.body.error === 'invalid_cook_id') {
                showBanner('error', 'Invalid bake id in URL.');
                applyButtonStates();
                return;
            }
            if (resp.status === 404) {
                showBanner('error', 'Bake not found.');
                applyButtonStates();
                return;
            }
            if (!resp.ok || !resp.body || !resp.body.cook) {
                var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                showBanner('error', 'Failed to load bake: ' + code + '.');
                applyButtonStates();
                return;
            }
            renderCook(resp.body);
            if (!state.logHydrated) { doLoadLog(); }
            doOpenTail();
        });
    }

    function doLoadLog() {
        if (!state || state.logHydrated) { return; }
        state.logHydrated = true;
        var mySession = state;
        var cookId    = state.cookId;

        window.cookbookApi.get('/api/v1/cooks/' + encodeURIComponent(cookId) + '/log', { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (state !== mySession) { return; }

            if (resp.networkError) {
                appendConsoleNotice('error', '(could not reach broker: ' + resp.networkError + ')');
                return;
            }
            if (resp.status === 200) {
                var rawText = (resp.rawText === null || resp.rawText === undefined) ? '' : resp.rawText;
                appendConsoleText(rawText);
                appendSeamMarker();
                return;
            }
            if (resp.status === 404) {
                var which = (resp.body && resp.body.error) ? String(resp.body.error) : 'not_found';
                if (which === 'log_unavailable') {
                    appendConsoleNotice('unavailable', '(no log available for this bake)');
                } else {
                    appendConsoleNotice('error', '(bake not found)');
                }
                return;
            }
            if (resp.status === 400 && resp.body && resp.body.error === 'invalid_cook_id') {
                appendConsoleNotice('error', '(invalid bake id)');
                return;
            }
            if (resp.status === 401) {
                appendConsoleNotice('error', '(unauthorized: session token missing or rejected)');
                return;
            }
            var code = (resp.body && resp.body.error) ? String(resp.body.error) : ('HTTP ' + resp.status);
            appendConsoleNotice('error', '(could not load log: ' + code + ')');
        });
    }

    // ----------------------------------------------------------------
    // Live-tail WebSocket -- specialized cook-view transport.
    //
    // Connects to /api/v1/cooks/<cookId>/log/ws?t=<sessionToken>. The
    // server emits opaque UTF-8 TEXT frames; each frame body is appended
    // verbatim as a new immutable text node after the hydration seam.
    // The client never sends. There is no subscribe / unsubscribe / ping
    // / pong protocol. There is no JSON envelope. There is no replay.
    //
    // Lifecycle (per page mount, at most ONE socket):
    //   gate:  status === 'running' AND no prior attempt this mount
    //   open:  construct WebSocket; record state.cookSocket
    //   onopen:    tailSubscribed = true
    //   onmessage: appendConsoleText(event.data)
    //   onerror:   tailErrored = true (onclose follows)
    //   onclose:   tailClosed = true; cookSocket = null; notice
    //
    // teardown() closes the socket on page-mount end.
    //
    // No reconnect, no retry, no backoff, no fallback. If the socket
    // dies, the user must navigate away and back.
    //
    // Streaming rule (Phase AE): distinguish carefully between
    //   stream disconnected / cook reached terminal state /
    //   broker unreachable / cook stalled / no new output yet.
    // Do NOT flatten those states into a single 'disconnected' label.
    // The onclose handler below maps close codes to truthful notices
    // so the operator can tell which state actually occurred.
    // ----------------------------------------------------------------

    var TAIL_TOKEN_KEY = 'cookbook.sessionToken';

    function doOpenTail() {
        if (!state) { return; }
        // Single-shot gate: one socket per mount, regardless of
        // success/failure outcome.
        if (state.cookSocket || state.tailClosed || state.tailErrored) { return; }
        // No live tail for terminal cooks; the hydrated log is complete.
        if (state.lastStatus !== 'running') { return; }

        var token = null;
        try { token = sessionStorage.getItem(TAIL_TOKEN_KEY); } catch (e) { token = null; }
        if (!token) {
            state.tailErrored = true;
            appendConsoleNotice('error', '(no session token; live tail unavailable)');
            return;
        }

        var proto = (window.location.protocol === 'https:') ? 'wss:' : 'ws:';
        var url   = proto + '//' + window.location.host
                  + '/api/v1/cooks/' + encodeURIComponent(state.cookId)
                  + '/log/ws?t=' + encodeURIComponent(token);

        var mySession = state;
        var ws;
        try {
            ws = new WebSocket(url);
        } catch (e) {
            state.tailErrored = true;
            appendConsoleNotice('error', '(could not open live tail)');
            return;
        }
        state.cookSocket = ws;

        ws.onopen = function () {
            if (state !== mySession) { return; }
            state.tailSubscribed = true;
        };
        ws.onmessage = function (event) {
            if (state !== mySession) { return; }
            var data = event ? event.data : null;
            if (typeof data === 'string' && data.length > 0) {
                appendConsoleText(data);
            }
            // Non-string frames (Blob / ArrayBuffer) are silently
            // ignored. The server contract is text-only; receiving
            // anything else is a protocol error we choose not to
            // surface to the user.
        };
        ws.onerror = function () {
            if (state !== mySession) { return; }
            state.tailErrored = true;
            // onclose will fire next; the notice is appended there so
            // we render exactly one closure notice.
        };
        ws.onclose = function (event) {
            if (state !== mySession) { return; }
            var code = (event && typeof event.code === 'number') ? event.code : 0;
            var wasSubscribed = state.tailSubscribed;
            state.tailClosed = true;
            state.cookSocket = null;
            // Phase AE close-code vocabulary. The four cases below
            // map to distinct operational realities; flattening them
            // into one 'disconnected' label hides truth from the
            // operator. The browser-supplied close code is the only
            // signal available here (no JSON envelope on this socket).
            if (code === 1000) {
                // Clean server-side close. The broker's cook-log
                // tailer closes the socket with code 1000 the moment
                // the cook reaches a terminal state (succeeded,
                // failed, stopped). The hydrated console is now
                // complete; no further output will arrive.
                appendConsoleNotice('terminal', '(bake reached terminal state \u2014 live tail closed)');
            } else if (wasSubscribed && code === 1006) {
                // 1006 = abnormal closure with no Close frame. The
                // socket was open and we received frames, then the
                // stream died without a terminal marker. This is the
                // canonical broker-side interruption / process kill /
                // network drop case. The cook itself may or may not
                // still be running; reload the page to re-fetch
                // status.
                state.tailErrored = true;
                appendConsoleNotice('error', '(live tail interrupted \u2014 broker stream ended without terminal marker; reload to re-check bake status)');
            } else if (wasSubscribed) {
                // Any other non-1000 close after a successful
                // subscription. Surface the raw code so the operator
                // (or TROUBLESHOOTING.md) can decode it.
                state.tailErrored = true;
                appendConsoleNotice('error', '(live tail disconnected \u2014 abnormal close code ' + code + '; navigate away and back to reattempt)');
            } else {
                // onopen never fired -- the socket failed before
                // subscription completed (token rejection, broker
                // refused, network unreachable). The hydrated log,
                // if any, is still authoritative.
                state.tailErrored = true;
                appendConsoleNotice('error', '(could not open live tail \u2014 close code ' + code + ')');
            }
        };
    }

    function doStop() {
        if (!state || state.stopInFlight || state.loadInFlight) { return; }
        if (state.lastStatus !== 'running') { return; }
        var mySession = state;
        var cookId    = state.cookId;
        state.stopInFlight = true;
        hideBanner();
        applyButtonStates();

        window.cookbookApi.post('/api/v1/cooks/' + encodeURIComponent(cookId) + '/stop', {}, { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (state !== mySession) { return; }

            if (resp.networkError) {
                state.stopInFlight = false;
                // Phase AE: stop is a state-mutating call. A network
                // failure does NOT mean the stop request was rejected
                // -- the POST may have reached the broker and the
                // cook may already have been signalled before the
                // socket dropped. Tell the truth and direct the
                // operator to refresh rather than retry blind.
                showBanner('error',
                    'Could not reach broker: ' + resp.networkError + '. ' +
                    'The stop request may or may not have been received by the broker. ' +
                    'Click Refresh to re-check the bake status before retrying.');
                applyButtonStates();
                return;
            }
            if (resp.status === 400 && resp.body && resp.body.error === 'invalid_cook_id') {
                state.stopInFlight = false;
                showBanner('error', 'Invalid bake id.');
                applyButtonStates();
                return;
            }
            if (resp.status === 404 && resp.body && resp.body.error === 'cook_not_active') {
                // The broker already drove the cook to a terminal state
                // between our GET and the stop POST. Re-fetch the row.
                state.stopInFlight = false;
                applyButtonStates();
                doLoad();
                return;
            }
            if (!resp.ok) {
                state.stopInFlight = false;
                var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                showBanner('error', 'Stop failed: ' + code + '.');
                applyButtonStates();
                return;
            }
            // 202 Accepted. The button stays "Stopping..." until the
            // user explicitly clicks Refresh and sees a terminal status
            // in the persisted row. No implicit poll, no timer.
            showBanner('info', 'Stop requested. Click Refresh to see the terminal state once it is recorded.');
            applyButtonStates();
        });
    }

    // ----------------------------------------------------------------
    // V1.S03 -- Resume
    //
    // Resume is a state-mutating POST that asks the broker to spawn
    // a NEW cook (new cookId, new folder) that re-attaches to the
    // parent's checkpoint file. The current page mirrors an
    // interrupted parent; on 201 we navigate to the child so the
    // operator sees the resume run start immediately. The broker
    // contract guarantees: 201 only on a fully-spawned child; any
    // refusal (409 / 410 / 412 / 500 / 507) leaves NO new row, NO
    // new folder, NO secret in broker memory. The SPA never tries
    // to "finish" a resume that the broker refused.
    // ----------------------------------------------------------------

    function doResume() {
        if (!state || state.resumeInFlight || state.stopInFlight || state.loadInFlight || state.readinessInFlight) { return; }
        if (state.lastStatus !== 'interrupted' || state.resumable !== true) { return; }
        if (acquisitionBlocked) {
            showBanner('error', 'PAX engine acquisition is required before bakes can be resumed.');
            return;
        }
        // V1.S04 -- advisory readiness gate. Only a present payload
        // with summary.blocked > 0 refuses the resume; warnings,
        // not_checked, and never-ran do NOT block.
        if (state.latestReadiness && state.latestReadiness.summary && (state.latestReadiness.summary.blocked|0) > 0) {
            var n = (state.latestReadiness.summary.blocked|0);
            showBanner('error', 'Readiness reports ' + n + ' blocker' + (n === 1 ? '' : 's') + '. Resolve them, then re-check readiness or refresh to clear the panel.');
            return;
        }
        var mySession = state;
        var cookId    = state.cookId;
        state.resumeInFlight = true;
        hideBanner();
        applyButtonStates();

        window.cookbookApi.post('/api/v1/cooks/' + encodeURIComponent(cookId) + '/resume', {}, { signal: state.abortCtrl ? state.abortCtrl.signal : null }).then(function (resp) {
            if (state !== mySession) { return; }

            if (resp.networkError) {
                state.resumeInFlight = false;
                // Resume is a state-mutating call. A network failure
                // does NOT mean the resume request was rejected --
                // the POST may have reached the broker and a child
                // cook may already exist before the socket dropped.
                // Tell the truth and direct the operator to refresh
                // (or visit the Cooks page) rather than retry blind.
                showBanner('error',
                    'Could not reach broker: ' + resp.networkError + '. ' +
                    'The resume request may or may not have been received by the broker. ' +
                    'Click Refresh to re-check this bake, then open the Bakes page to look for a newly spawned child bake before retrying.');
                applyButtonStates();
                return;
            }
            if (resp.status === 201 && resp.body && resp.body.cookId) {
                // Success. Navigate to the child cook detail view.
                // The router teardown will clear resumeInFlight on
                // its own by tearing down state entirely; we do not
                // need to clear it here.
                history.replaceState({}, '', window.location.pathname + '#/cooks/' + resp.body.cookId);
                window.cookbookRouter.dispatch();
                return;
            }
            state.resumeInFlight = false;
            if (resp.status === 400 && resp.body && resp.body.error === 'invalid_cook_id') {
                showBanner('error', 'Invalid bake id.');
                applyButtonStates();
                return;
            }
            if (resp.status === 404 && resp.body && resp.body.error === 'not_found') {
                showBanner('error', 'Bake not found.');
                applyButtonStates();
                return;
            }
            if ((resp.status === 409 || resp.status === 410) && resp.body && resp.body.error === 'not_resumable') {
                // The broker's resumability re-check at the gate
                // disagreed with the snapshot we rendered. Re-fetch
                // the cook so the operator sees the current truth
                // (the Resume button will hide itself once state
                // mirrors the new resumeReason).
                var why = (resp.body && resp.body.reason) ? String(resp.body.reason) : 'unknown';
                var human = (resp.body && resp.body.humanReason) ? String(resp.body.humanReason) : '';
                var msg = 'Cannot resume: ' + (human || why) + '.';
                showBanner('error', msg + ' Click Refresh to see the current state.');
                applyButtonStates();
                doLoad();
                return;
            }
            if (resp.status === 412 && resp.body && resp.body.error === 'recipe_invalid') {
                showBanner('error', 'Cannot resume: the parent recipe snapshot is invalid for resume (executionMode must be local-manual).');
                applyButtonStates();
                return;
            }
            if (resp.status === 409 && resp.body && resp.body.error === 'recipe_busy') {
                showBanner('error', 'Cannot resume: the recipe is currently busy with another bake.');
                applyButtonStates();
                return;
            }
            if (resp.status === 500 && resp.body && resp.body.error === 'pax_script_integrity') {
                showBanner('error', 'PAX integrity check failed. Re-install PAX Cookbook from the latest release.');
                applyButtonStates();
                return;
            }
            if (resp.status === 507 && resp.body && resp.body.error === 'insufficient_disk_space') {
                showBanner('error', 'Cannot resume: insufficient disk space on the workspace drive.');
                applyButtonStates();
                return;
            }
            if (resp.status === 400 && resp.body && resp.body.error === 'workspace_path_too_long') {
                showBanner('error', 'Cannot resume: workspace path is too long for a new bake folder.');
                applyButtonStates();
                return;
            }
            if (resp.status === 500 && resp.body && resp.body.error === 'cook_resume_internal') {
                showBanner('error', 'Resume failed internally on the broker. Check the broker log and TROUBLESHOOTING for cook_resume_internal.');
                applyButtonStates();
                return;
            }
            var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
            showBanner('error', 'Resume failed: ' + code + '.');
            applyButtonStates();
        });
    }

    // ----------------------------------------------------------------
    // Event wiring
    // ----------------------------------------------------------------

    function onRefreshClick(ev) {
        if (ev) { ev.preventDefault(); }
        // V1.S04 -- a manual refresh invalidates any previously
        // displayed readiness result. The cook row may have changed,
        // and even if it has not, the operator is explicitly asking
        // for a current view; surfacing a stale readiness contradicts
        // that intent.
        if (state) {
            state.latestReadiness = null;
            renderReadinessInitial();
        }
        doLoad();
    }

    function onStopClick(ev) {
        if (ev) { ev.preventDefault(); }
        doStop();
    }

    function onResumeClick(ev) {
        if (ev) { ev.preventDefault(); }
        doResume();
    }

    function onReadinessClick(ev) {
        if (ev) { ev.preventDefault(); }
        doReadiness();
    }

    // ----------------------------------------------------------------
    // V1.S04 -- Readiness rail (resume side)
    //
    // Mirrors the editor's readiness rail, scoped to a resume action.
    // The button is only visible when the cook is interrupted and
    // resumable; the readiness card is hidden otherwise. Clicking the
    // button POSTs /api/v1/cooks/readiness with { cookId } (the
    // broker locates the parent recipe from the cook row). The
    // result populates the readiness card in place.
    //
    // Refresh and Resume both invalidate any prior readiness payload:
    // refresh because the cook may have changed; resume because
    // navigation tears down the page anyway. No auto-run, no polling.
    // ----------------------------------------------------------------
    function getReadinessBody() {
        var card = byId('cook-readiness-card');
        return card ? card.querySelector('.editor-readiness-body') : null;
    }

    function renderReadinessInitial() {
        var body = getReadinessBody();
        if (!body) { return; }
        var p = document.createElement('p');
        p.className = 'editor-readiness-placeholder';
        p.textContent = 'Click \u201cCheck readiness\u201d to run advisory pre-flight checks for this resume. Readiness is informational only; PAX remains authoritative.';
        clearChildren(body);
        body.appendChild(p);
    }

    function renderReadinessInFlight() {
        var body = getReadinessBody();
        if (!body) { return; }
        var p = document.createElement('p');
        p.className = 'editor-readiness-status';
        p.textContent = 'Checking readiness\u2026';
        clearChildren(body);
        body.appendChild(p);
    }

    function renderReadinessTransportError(detail) {
        var body = getReadinessBody();
        if (!body) { return; }
        var h = document.createElement('p');
        h.className = 'readiness-error-heading';
        h.textContent = 'Readiness check failed.';
        var d = document.createElement('p');
        d.className = 'readiness-error-detail';
        d.textContent = detail || '';
        clearChildren(body);
        body.appendChild(h);
        body.appendChild(d);
    }

    function renderReadinessSuccess(result) {
        var body = getReadinessBody();
        if (!body) { return; }
        var overall = (result && result.status) ? String(result.status) : 'unknown';
        var summary = (result && result.summary) ? result.summary : { blocked: 0, warning: 0, ok: 0, notChecked: 0 };

        var header = document.createElement('div');
        header.className = 'readiness-summary';
        var chip = document.createElement('span');
        chip.className = 'readiness-status-chip readiness-status-' + overall;
        chip.textContent = overall.replace('_', ' ');
        header.appendChild(chip);
        var tally = document.createElement('span');
        tally.className = 'readiness-tally';
        tally.textContent =
            ' blockers: ' + (summary.blocked|0) +
            ' \u00b7 warnings: ' + (summary.warning|0) +
            ' \u00b7 ok: ' + (summary.ok|0) +
            ' \u00b7 not checked: ' + (summary.notChecked|0);
        header.appendChild(tally);

        var disclaimer = document.createElement('p');
        disclaimer.className = 'readiness-disclaimer';
        disclaimer.textContent =
            'A green result does not guarantee a successful resume. PAX validates auth, reachability, and destination at cook time and is authoritative.';

        var list = document.createElement('ul');
        list.className = 'readiness-check-list';
        var checks = (result && result.checks) ? result.checks : [];
        for (var i = 0; i < checks.length; i++) {
            var c = checks[i] || {};
            var li = document.createElement('li');
            li.className = 'readiness-check readiness-check-' + (c.status || 'unknown');

            var head = document.createElement('div');
            head.className = 'readiness-check-head';
            var s = document.createElement('span');
            s.className = 'readiness-status-chip readiness-status-' + (c.status || 'unknown');
            s.textContent = String(c.status || 'unknown').replace('_', ' ');
            head.appendChild(s);
            var lbl = document.createElement('span');
            lbl.className = 'readiness-check-label';
            lbl.textContent = String(c.label || c.id || '');
            head.appendChild(lbl);
            var scope = document.createElement('span');
            scope.className = 'readiness-check-scope';
            scope.textContent = String(c.scope || '');
            head.appendChild(scope);
            li.appendChild(head);

            if (c.detail) {
                var det = document.createElement('p');
                det.className = 'readiness-check-detail';
                det.textContent = String(c.detail);
                li.appendChild(det);
            }
            if (c.remediation) {
                var rem = document.createElement('p');
                rem.className = 'readiness-check-remediation';
                rem.textContent = 'Remediation: ' + String(c.remediation);
                li.appendChild(rem);
            }
            list.appendChild(li);
        }

        var ts = document.createElement('p');
        ts.className = 'readiness-timestamp';
        ts.textContent = 'Generated at ' + String(result.generatedAtUtc || '') + ' (UTC).';

        clearChildren(body);
        body.appendChild(header);
        body.appendChild(disclaimer);
        body.appendChild(list);
        body.appendChild(ts);
    }

    function doReadiness() {
        if (!state) { return; }
        if (state.lastStatus !== 'interrupted' || state.resumable !== true) { return; }
        if (state.readinessInFlight || state.loadInFlight || state.stopInFlight || state.resumeInFlight) { return; }
        var mySession = state;
        var mySeq     = ++state.readinessSeq;

        state.readinessInFlight = true;
        applyButtonStates();
        renderReadinessInFlight();

        window.cookbookApi.post(
            '/api/v1/cooks/readiness',
            { cookId: state.cookId },
            { signal: state.abortCtrl ? state.abortCtrl.signal : null }
        ).then(function (resp) {
            if (state !== mySession || mySeq !== state.readinessSeq) { return; }

            if (resp.networkError) {
                renderReadinessTransportError('Could not reach broker: ' + resp.networkError + '.');
                state.readinessInFlight = false;
                state.latestReadiness   = null;
                applyButtonStates();
                return;
            }
            if (!resp.ok || !resp.body) {
                var code = (resp.body && resp.body.error) ? resp.body.error : ('HTTP ' + resp.status);
                renderReadinessTransportError('Readiness check failed: ' + code + '.');
                state.readinessInFlight = false;
                state.latestReadiness   = null;
                applyButtonStates();
                return;
            }

            state.latestReadiness   = resp.body;
            state.readinessInFlight = false;
            renderReadinessSuccess(resp.body);
            applyButtonStates();
        });
    }

    // ----------------------------------------------------------------
    // Lifecycle
    // ----------------------------------------------------------------

    function mount(container, params) {
        var cookId = (params && params.cookId) ? String(params.cookId) : '';
        container.innerHTML = PAGE_TEMPLATE;
        pageRoot = container.querySelector('.cook-view');

        state = {
            cookId:         cookId,
            lastStatus:     '',
            loadInFlight:   false,
            stopInFlight:   false,
            resumeInFlight: false,
            // V1.S03 -- resumability projection mirrored from the
            // /api/v1/cooks/<id> GET response body. The broker is
            // authoritative; the SPA NEVER re-derives these.
            resumable:      false,
            resumeReason:   '',
            checkpointPath: '',
            parentCookId:   '',
            closureReason:  '',
            // V1.S04 -- advisory readiness for resume.
            readinessInFlight: false,
            readinessSeq:      0,
            latestReadiness:   null,
            logHydrated:    false,
            cookSocket:     null,
            tailSubscribed: false,
            tailClosed:     false,
            tailErrored:    false,
            refreshHandler: null,
            stopHandler:    null,
            resumeHandler:  null,
            readinessHandler: null,
            // Phase AE: per-mount AbortController for HTTP fetches
            // (not the WebSocket; the cook log socket has its own
            // close path in teardown).
            abortCtrl:      (typeof AbortController === 'function') ? new AbortController() : null
        };

        setBreadcrumb(cookId);

        var refresh = byId('cook-refresh-button');
        var stop    = byId('cook-stop-button');
        var resume  = byId('cook-resume-button');
        var readiness = byId('cook-readiness-button');
        if (refresh) {
            state.refreshHandler = onRefreshClick;
            refresh.addEventListener('click', state.refreshHandler);
        }
        if (stop) {
            state.stopHandler = onStopClick;
            stop.addEventListener('click', state.stopHandler);
        }
        if (resume) {
            state.resumeHandler = onResumeClick;
            resume.addEventListener('click', state.resumeHandler);
        }
        if (readiness) {
            state.readinessHandler = onReadinessClick;
            readiness.addEventListener('click', state.readinessHandler);
        }

        applyButtonStates();
        doLoad();
    }

    function teardown() {
        if (!state) { return; }
        var refresh = byId('cook-refresh-button');
        var stop    = byId('cook-stop-button');
        var resume  = byId('cook-resume-button');
        var readiness = byId('cook-readiness-button');
        if (refresh && state.refreshHandler) {
            refresh.removeEventListener('click', state.refreshHandler);
        }
        if (stop && state.stopHandler) {
            stop.removeEventListener('click', state.stopHandler);
        }
        if (resume && state.resumeHandler) {
            resume.removeEventListener('click', state.resumeHandler);
        }
        if (readiness && state.readinessHandler) {
            readiness.removeEventListener('click', state.readinessHandler);
        }
        if (state.cookSocket) {
            try { state.cookSocket.close(1000, 'page-teardown'); } catch (e) {}
            state.cookSocket = null;
        }
        // Phase AE: abort in-flight HTTP fetches.
        if (state.abortCtrl) {
            try { state.abortCtrl.abort(); } catch (e) {}
        }
        state    = null;
        pageRoot = null;
    }

    window.cookbookCookViewPage = {
        mount:    mount,
        teardown: teardown
    };

    // Stage 4: subscribe to acquisition-state changes emitted by
    // pax-engine-overlay.js. Registered once at script load so the
    // flag stays current across mount/teardown cycles. The handler
    // calls applyButtonStates() which self-guards against !state
    // and a missing pageRoot.
    try {
        window.addEventListener('cookbook:acquisitionStateChanged', function (ev) {
            try {
                acquisitionBlocked = !!(ev && ev.detail && ev.detail.blocked);
                applyButtonStates();
            } catch (e) { /* swallow */ }
        });
    } catch (e) { /* listener registration unsupported -- non-fatal */ }
})();
