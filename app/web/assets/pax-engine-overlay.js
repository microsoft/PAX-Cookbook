// PAX Cookbook -- PAX Engine Acquisition overlay (Phase 13 Stage 4).
//
// Owns exactly ONE full-viewport modal element
// (#cookbook-pax-engine-overlay) and listens to ONE window-scoped
// event dispatched by api.js:
//
//   'cookbook:acquisitionRequired' -> broker returned 409 with
//                                     body.code = 'acquisitionRequired'.
//                                     Mounts the overlay (sticky)
//                                     and starts polling /state at
//                                     2s intervals until the state
//                                     reaches 'acquired' (or the
//                                     policy is 'embedded' / state
//                                     is 'embedded-legacy', in which
//                                     case the overlay never had to
//                                     be mounted to begin with).
//
// Also runs a one-shot boot probe of
//   GET /api/v1/setup/acquire-pax/state
// at DOMContentLoaded so the overlay is mounted BEFORE the first
// page-driven cook attempt hits the 409 gate. The /state endpoint
// is read-only and reflects the policy + install-state.json snapshot
// computed by Engine\Acquisition.psm1::Get-PaxAcquisitionState.
//
// Doctrine:
//   - The overlay's "Choose PAX script from this computer" button opens
//     a browser file picker (Stage 4B). The picked file is read into an
//     ArrayBuffer in the browser, then POSTed as raw
//     application/octet-stream to /api/v1/setup/acquire-pax/upload-bytes.
//     Client-side validation (size <= 4 MiB, extension .ps1, optional
//     SubtleCrypto SHA-256) is purely a UX fast-fail and never
//     substitutes for the broker's authoritative chain of signed
//     manifest + approved-only + SHA-256 + byte-equal staged write.
//     The original picked file is never mutated and never re-read
//     after the buffer is captured.
//   - The overlay NEVER inspects, hashes, or proxies the PAX engine
//     bytes for trust purposes. The download action POSTs an empty
//     body; the broker fetches the manifest entry, validates, and
//     writes the canonical script. The local-file action POSTs the
//     picked bytes; the broker hashes them, matches against the
//     manifest, and writes the canonical script. The overlay only
//     re-reads /state.
//   - The overlay dispatches 'cookbook:acquisitionStateChanged' on
//     every successful /state read so page modules
//     (recipe-editor.js, cook-view.js, cooks-list.js, recipe-list.js,
//     home.js) can update their cook/resume/schedule button
//     affordances without each owning their own /state poller.
//   - The "Not now" button dismisses the modal into limited app mode
//     without acquiring the engine. It is a purely client-side
//     dismissal: it stops the /state poll and unmounts the overlay,
//     but does NOT relax the acquisitionRequired gate. The next cook/
//     bake attempt fires 'cookbook:acquisitionRequired', which clears
//     the dismissal and re-opens the modal. The modal banner is
//     strictly session-scoped: it shows only the live progress and
//     result of the action the operator just took. A failure stored by
//     a previous session is never promoted into the banner -- it stays
//     in Support details.
//
// Architectural boundary: this module knows about
//   GET  /api/v1/setup/acquire-pax/state
//   POST /api/v1/setup/acquire-pax/download
//   POST /api/v1/setup/acquire-pax/upload-bytes
// and nothing else.

(function () {
    'use strict';

    var OVERLAY_ID  = 'cookbook-pax-engine-overlay';
    var BODY_CLASS  = 'cookbook-pax-engine-active';
    var POLL_MS     = 2000;
    var MAX_UPLOAD_BYTES = 4 * 1024 * 1024;

    // Module-scoped state. A second 'cookbook:acquisitionRequired'
    // event firing while the overlay is already up does NOT remount
    // it -- the in-flight poll loop is the single source of truth.
    var state = {
        mounted:           false,
        lastStateBody:     null,
        lastFetchUtc:      null,
        lastFetchError:    null,
        downloadInFlight:  false,
        uploadInFlight:    false,
        pollHandle:        null,
        lastUploadError:   null,
        lastUploadErrorCode: null,
        lastOperation:     null,
        dismissed:         false,
        showSupport:       false,
        // The only message shown in the modal banner. It is set ONLY by
        // a button the operator clicks in THIS session (download or
        // choose-from-computer) and is cleared the moment a new action
        // starts. On open it is null, so the modal never surfaces a
        // failure from a previous session -- those stay in Support
        // details. {tone:'info'|'error', text:'...'} or null.
        sessionMessage:    null
    };

    // ----------------------------------------------------------------
    // Pure helpers
    // ----------------------------------------------------------------

    function isAcquiredBody(body) {
        if (!body || typeof body !== 'object') { return false; }
        if (body.policy === 'embedded') { return true; }
        if (body.state === 'embedded-legacy') { return true; }
        if (body.state === 'acquired' && body.isAcquired === true) { return true; }
        return false;
    }

    // Reads the broker's most recent attempt error code (if any) from a
    // /state body. Used only to populate Support details -- it is NEVER
    // promoted into the visible banner on its own, so a failure recorded
    // in a previous session can never auto-appear when the modal opens.
    function lastAttemptErrorCode(body) {
        if (!body || typeof body !== 'object') { return null; }
        var inst = body.installState;
        if (!inst || typeof inst !== 'object') { return null; }
        var err = inst.lastAttemptError;
        if (!err || typeof err !== 'object') { return null; }
        return (typeof err.error === 'string') ? err.error : null;
    }

    // Maps a raw acquisition failure to a short, plain-English
    // message for office testers. Every raw diagnostic (failure
    // code, URL, port, status, hash) stays in Support details; this
    // text is the only thing shown in the modal body banner. The
    // context ('download' vs 'local') chooses between the two
    // failure stories the tester can act on.
    function humanizeAcquisitionError(code, context) {
        var c = (typeof code === 'string') ? code.toLowerCase() : '';
        if (c.indexOf('manifest_fetch') !== -1
            || c.indexOf('manifest_unavailable') !== -1
            || c.indexOf('network') !== -1
            || c.indexOf('connection') !== -1
            || c.indexOf('refused') !== -1
            || c.indexOf('timeout') !== -1) {
            if (context === 'local') {
                return 'We couldn\u2019t check that this PAX script is approved because the test approval list is not available. Try again later, or ask the test coordinator for the approved PAX script setup.';
            }
            return 'We couldn\u2019t download the PAX script because the test download source is not running. Try again later, or choose a PAX script from this computer.';
        }
        if (c.indexOf('too_large') !== -1 || c.indexOf('size') !== -1) {
            return 'That file is too large to be a valid PAX script.';
        }
        if (c.indexOf('read') !== -1 || c.indexOf('io_') !== -1 || c.indexOf('access') !== -1) {
            return 'We couldn\u2019t read that file. Check that you have access to it and try again.';
        }
        if (c.indexOf('not_approved') !== -1 || c.indexOf('hash') !== -1
            || c.indexOf('sha') !== -1 || c.indexOf('mismatch') !== -1
            || c.indexOf('approved') !== -1 || c.indexOf('extension') !== -1
            || c.indexOf('schema') !== -1 || c.indexOf('signature') !== -1
            || c.indexOf('invalid') !== -1) {
            return 'That file is not an approved PAX script for this version of PAX Cookbook. Please choose the approved PAX script.';
        }
        if (context === 'local') {
            return 'We couldn\u2019t add that PAX script. Open Support details below for the technical reason.';
        }
        return 'We couldn\u2019t set up the PAX script. Try again later, or open Support details below for the technical reason.';
    }

    function safeStringifyJson(value) {
        try { return JSON.stringify(value, null, 2); } catch (e) { return String(value); }
    }

    // Internal-test (non-customer-facing) detection. The banner shows
    // ONLY when the broker's /state body reports
    // manifestSignaturePolicy === 'internal-test-bypass'. Every other
    // value -- 'required', null, missing, empty, or any unexpected
    // string -- suppresses the banner. The banner is informational
    // copy: it adds no action and changes no acquisition behavior.
    function isInternalTestBypass(body) {
        return !!(body
            && typeof body === 'object'
            && typeof body.manifestSignaturePolicy === 'string'
            && body.manifestSignaturePolicy === 'internal-test-bypass');
    }

    // ----------------------------------------------------------------
    // DOM construction
    // ----------------------------------------------------------------

    function buildOverlayDOM() {
        var overlay = document.createElement('div');
        overlay.id = OVERLAY_ID;
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-labelledby', OVERLAY_ID + '-title');
        overlay.setAttribute('aria-describedby', OVERLAY_ID + '-body');

        var panel = document.createElement('div');
        panel.className = 'lock-overlay-panel pax-engine-overlay-panel';

        var title = document.createElement('h2');
        title.id = OVERLAY_ID + '-title';
        title.className = 'lock-overlay-title';
        title.textContent = 'PAX script needed';
        panel.appendChild(title);

        var body = document.createElement('div');
        body.id = OVERLAY_ID + '-body';
        body.className = 'lock-overlay-body';
        panel.appendChild(body);

        var bannerSlot = document.createElement('div');
        bannerSlot.className = 'pax-engine-overlay-banner-slot';
        panel.appendChild(bannerSlot);

        var actions = document.createElement('div');
        actions.className = 'lock-overlay-actions pax-engine-overlay-actions';

        var btnDownload = document.createElement('button');
        btnDownload.type = 'button';
        btnDownload.className = 'btn-primary lock-overlay-primary';
        btnDownload.id = OVERLAY_ID + '-download';
        btnDownload.textContent = 'Download PAX script';
        actions.appendChild(btnDownload);

        var btnLocal = document.createElement('button');
        btnLocal.type = 'button';
        btnLocal.className = 'btn-ghost lock-overlay-secondary';
        btnLocal.id = OVERLAY_ID + '-local';
        btnLocal.textContent = 'Choose PAX script from this computer';
        actions.appendChild(btnLocal);

        var btnCancel = document.createElement('button');
        btnCancel.type = 'button';
        btnCancel.className = 'btn-ghost lock-overlay-secondary';
        btnCancel.id = OVERLAY_ID + '-cancel';
        btnCancel.textContent = 'Not now';
        actions.appendChild(btnCancel);

        panel.appendChild(actions);

        // Hidden file input -- the visible "Choose PAX script from this
        // computer" button proxies a click into this input so the
        // browser's native picker pops with the .ps1 accept filter.
        // The input is removed from the tab order (tabindex=-1) and
        // hidden via inline style so it never appears in the
        // overlay's visible layout.
        var fileInput = document.createElement('input');
        fileInput.type = 'file';
        fileInput.id = OVERLAY_ID + '-file-input';
        fileInput.accept = '.ps1';
        fileInput.setAttribute('tabindex', '-1');
        fileInput.setAttribute('aria-hidden', 'true');
        fileInput.style.position = 'absolute';
        fileInput.style.left = '-9999px';
        fileInput.style.width = '1px';
        fileInput.style.height = '1px';
        fileInput.style.opacity = '0';
        panel.appendChild(fileInput);

        var support = document.createElement('details');
        support.className = 'pax-engine-overlay-support';
        support.id = OVERLAY_ID + '-support';
        var summary = document.createElement('summary');
        summary.textContent = 'Support details';
        support.appendChild(summary);
        var pre = document.createElement('pre');
        pre.className = 'pax-engine-overlay-support-pre';
        pre.id = OVERLAY_ID + '-support-pre';
        support.appendChild(pre);
        panel.appendChild(support);

        overlay.appendChild(panel);

        // Wire button handlers AFTER the panel is composed so click
        // handlers can call into renderState() to refresh disabled
        // states.
        btnDownload.addEventListener('click', onClickDownload);
        btnCancel.addEventListener('click', onClickDismiss);
        btnLocal.addEventListener('click', onClickLocal);
        fileInput.addEventListener('change', onFileChosen);

        return overlay;
    }

    function mount() {
        if (state.mounted) { renderState(); return; }
        if (document.getElementById(OVERLAY_ID)) {
            state.mounted = true;
            renderState();
            return;
        }
        var overlay = buildOverlayDOM();
        document.body.appendChild(overlay);
        try { document.body.classList.add(BODY_CLASS); } catch (e) {}
        state.mounted = true;
        renderState();
    }

    function unmount() {
        var el = document.getElementById(OVERLAY_ID);
        if (el && el.parentNode) { try { el.parentNode.removeChild(el); } catch (e) {} }
        try { document.body.classList.remove(BODY_CLASS); } catch (e) {}
        state.mounted = false;
    }

    // ----------------------------------------------------------------
    // Render
    // ----------------------------------------------------------------

    function renderState() {
        if (!state.mounted) { return; }
        var bodyEl = document.getElementById(OVERLAY_ID + '-body');
        var bannerSlot = document.getElementById(OVERLAY_ID + '-body').parentNode.querySelector('.pax-engine-overlay-banner-slot');
        var btnDownload = document.getElementById(OVERLAY_ID + '-download');
        var btnLocal    = document.getElementById(OVERLAY_ID + '-local');
        var btnCancel   = document.getElementById(OVERLAY_ID + '-cancel');
        var supportPre  = document.getElementById(OVERLAY_ID + '-support-pre');
        if (!bodyEl || !btnDownload || !btnLocal || !btnCancel || !supportPre) { return; }

        var s = state.lastStateBody;

        var lines = [];
        lines.push('PAX Cookbook needs the PAX script before it can run a bake. You can add it now, or continue and do it later.');
        lines.push('Status: PAX script not added yet');
        lines.push('Downloading will get the latest approved PAX script from the official Microsoft GitHub repository.');
        if (s && s.expected && s.expected.paxScriptVersion) {
            lines.push('Expected PAX script version: ' + s.expected.paxScriptVersion);
        }
        bodyEl.innerHTML = '';
        for (var i = 0; i < lines.length; i++) {
            var p = document.createElement('p');
            p.textContent = lines[i];
            bodyEl.appendChild(p);
        }

        // Banner.
        bannerSlot.innerHTML = '';
        // Internal-test (non-customer-facing) banner. Rendered above
        // any last-attempt banner and ONLY under the
        // internal-test-bypass manifest-signature policy.
        if (isInternalTestBypass(s)) {
            var ntDiv = document.createElement('div');
            ntDiv.className = 'pax-engine-overlay-banner pax-engine-overlay-banner-nonproduction';
            ntDiv.setAttribute('role', 'status');
            ntDiv.setAttribute('aria-live', 'polite');
            var ntTitle = document.createElement('p');
            ntTitle.className = 'pax-engine-overlay-banner-nonproduction-title';
            ntTitle.textContent = 'Test build';
            ntDiv.appendChild(ntTitle);
            var ntBody = document.createElement('p');
            ntBody.className = 'pax-engine-overlay-banner-nonproduction-body';
            ntBody.textContent = 'This test build uses a test approval list for the PAX script. Production builds will use Microsoft-approved signing.';
            ntDiv.appendChild(ntBody);
            bannerSlot.appendChild(ntDiv);
        }
        // Visible banner. This is strictly session-scoped: it shows the
        // live progress of an in-flight action the operator just started,
        // or the plain-English result of the action they just finished.
        // It is NEVER derived from the broker's stored last-attempt error,
        // so opening the modal -- or a background /state poll -- can never
        // resurface a failure from a previous session. Raw diagnostics
        // (including any previous attempt) live in Support details below.
        var visibleErr = null;
        if (state.downloadInFlight) {
            visibleErr = { tone: 'info', text: 'Downloading PAX script\u2026' };
        } else if (state.uploadInFlight) {
            visibleErr = { tone: 'info', text: 'Checking PAX script\u2026' };
        } else if (state.sessionMessage) {
            visibleErr = state.sessionMessage;
        }
        if (visibleErr) {
            var div = document.createElement('div');
            div.className = 'pax-engine-overlay-banner pax-engine-overlay-banner-' + visibleErr.tone;
            div.setAttribute('role', 'status');
            div.setAttribute('aria-live', 'polite');
            div.textContent = visibleErr.text;
            bannerSlot.appendChild(div);
        }

        // Button states.
        var inflight = state.downloadInFlight || state.uploadInFlight;
        btnDownload.disabled = inflight;
        btnDownload.textContent = state.downloadInFlight ? 'Downloading\u2026' : 'Download PAX script';
        btnLocal.disabled = inflight;
        btnLocal.textContent = state.uploadInFlight ? 'Checking\u2026' : 'Choose PAX script from this computer';
        // "Not now" dismisses the modal into limited mode. It stays
        // available unless an acquisition action is mid-flight; it does
        // NOT acquire the engine and the acquisitionRequired gate stays
        // enforced, so any cook/bake attempt re-opens this modal.
        btnCancel.disabled = inflight;
        btnCancel.textContent = 'Not now';

        // Support details. The raw broker /state body (including any
        // previous attempt's lastAttemptError) is recorded here for
        // troubleshooting only -- it is intentionally NOT promoted into
        // the visible banner.
        var payload = {
            note:                'Support details may include the most recent previous attempt. These technical details are for troubleshooting only.',
            sessionMessage:      state.sessionMessage,
            lastFetchUtc:        state.lastFetchUtc,
            lastFetchError:      state.lastFetchError,
            lastUploadError:     state.lastUploadError,
            lastUploadErrorCode: state.lastUploadErrorCode,
            lastOperation:       state.lastOperation,
            previousAttemptError: lastAttemptErrorCode(s),
            stateBody:           s
        };
        supportPre.textContent = safeStringifyJson(payload);
    }

    // ----------------------------------------------------------------
    // /state probing
    // ----------------------------------------------------------------

    function emitStateChanged(body, source) {
        try {
            window.dispatchEvent(new CustomEvent('cookbook:acquisitionStateChanged', {
                detail: {
                    timestampUtc: new Date().toISOString(),
                    source:       source || 'overlay',
                    blocked:      !isAcquiredBody(body),
                    state:        body ? body.state  : null,
                    policy:       body ? body.policy : null,
                    body:         body || null
                }
            }));
        } catch (e) {}
    }

    function fetchStateOnce() {
        if (!window.cookbookApi || typeof window.cookbookApi.get !== 'function') {
            return Promise.resolve({ ok: false });
        }
        return window.cookbookApi.get('/api/v1/setup/acquire-pax/state').then(function (resp) {
            state.lastFetchUtc = new Date().toISOString();
            if (resp && resp.ok && resp.body && typeof resp.body === 'object') {
                state.lastStateBody  = resp.body;
                state.lastFetchError = null;
                emitStateChanged(resp.body, 'probe');
                if (isAcquiredBody(resp.body)) {
                    stopPolling();
                    if (state.mounted) { unmount(); }
                } else {
                    // Respect an operator's "Not now" dismissal: a
                    // background poll must not yank the modal back up.
                    // Only an explicit acquisitionRequired gate (a cook/
                    // bake attempt) re-opens it.
                    if (!state.mounted && !state.dismissed) { mount(); }
                    if (state.mounted) { renderState(); }
                }
                return resp;
            }
            // Non-2xx, body missing, or non-object body.
            state.lastFetchError = resp ? ('HTTP ' + resp.status) : 'no_response';
            if (state.mounted) { renderState(); }
            return resp || { ok: false };
        }, function (err) {
            state.lastFetchUtc   = new Date().toISOString();
            state.lastFetchError = (err && err.message) ? err.message : String(err);
            if (state.mounted) { renderState(); }
            return { ok: false };
        });
    }

    function startPolling() {
        if (state.pollHandle !== null) { return; }
        state.pollHandle = setInterval(function () { fetchStateOnce(); }, POLL_MS);
    }

    function stopPolling() {
        if (state.pollHandle !== null) {
            try { clearInterval(state.pollHandle); } catch (e) {}
            state.pollHandle = null;
        }
    }

    // ----------------------------------------------------------------
    // Button handlers
    // ----------------------------------------------------------------

    function onClickDownload() {
        if (state.downloadInFlight || state.uploadInFlight) { return; }
        if (!window.cookbookApi || typeof window.cookbookApi.post !== 'function') { return; }
        state.downloadInFlight = true;
        state.lastOperation = 'download';
        state.lastUploadError = null;
        state.lastUploadErrorCode = null;
        // Clear any prior result so the operator sees a fresh
        // "Downloading PAX script…" progress line for THIS click.
        state.sessionMessage = null;
        renderState();
        window.cookbookApi.post('/api/v1/setup/acquire-pax/download', {}).then(function (resp) {
            state.downloadInFlight = false;
            // The download response carries an installState block in
            // its body whether it succeeded or failed. On success the
            // re-fetch below flips the modal to acquired (it unmounts).
            // On failure we surface a fresh, download-scoped plain-English
            // result; the raw reason stays in Support details.
            applyDownloadResult(resp);
            fetchStateOnce();
        }, function () {
            state.downloadInFlight = false;
            state.sessionMessage = { tone: 'error', text: humanizeAcquisitionError('network', 'download') };
            fetchStateOnce();
        });
    }

    // Translates a /download response into the session banner. On a
    // successful acquisition there is nothing to show (the modal
    // unmounts); otherwise a plain-English, download-scoped error is
    // surfaced for the operator.
    function applyDownloadResult(resp) {
        if (resp && resp.ok && resp.body && isAcquiredBody(resp.body)) {
            state.sessionMessage = null;
            return;
        }
        var code = null;
        if (resp && resp.body && typeof resp.body === 'object') {
            if (typeof resp.body.error === 'string') {
                code = resp.body.error;
            } else {
                code = lastAttemptErrorCode(resp.body);
            }
        }
        state.sessionMessage = { tone: 'error', text: humanizeAcquisitionError(code || 'network', 'download') };
    }

    // "Not now" / continue-without-PAX-script. Dismisses the modal into
    // limited app mode WITHOUT acquiring the engine. The acquisitionRequired
    // gate stays fully enforced: any cook/bake attempt fires the
    // acquisitionRequired event, which re-opens this modal. This never
    // fakes engine availability and never persists across a gated action.
    function onClickDismiss() {
        if (state.downloadInFlight || state.uploadInFlight) { return; }
        state.dismissed = true;
        state.sessionMessage = null;
        stopPolling();
        unmount();
    }

    // ----------------------------------------------------------------
    // Local-file upload (Stage 4B)
    // ----------------------------------------------------------------

    function onClickLocal() {
        if (state.uploadInFlight || state.downloadInFlight) { return; }
        state.lastOperation = 'local';
        state.lastUploadError = null;
        state.lastUploadErrorCode = null;
        // Clear any prior result (including a previous download error) so
        // the operator never sees a stale download message after choosing
        // a local file. If they cancel the picker, the banner simply
        // stays empty.
        state.sessionMessage = null;
        renderState();
        var fileInput = document.getElementById(OVERLAY_ID + '-file-input');
        if (!fileInput) { return; }
        // Clear previous selection so picking the same file twice still
        // fires the change event.
        try { fileInput.value = ''; } catch (e) {}
        try { fileInput.click(); } catch (e) {
            state.lastUploadError = 'unable to open file picker: ' + ((e && e.message) ? e.message : String(e));
            state.lastUploadErrorCode = 'read_failed';
            state.sessionMessage = { tone: 'error', text: humanizeAcquisitionError('read', 'local') };
            renderState();
        }
    }

    function computeSha256Hex(arrayBuffer) {
        try {
            if (!window.crypto || !window.crypto.subtle || typeof window.crypto.subtle.digest !== 'function') {
                return Promise.resolve(null);
            }
        } catch (e) { return Promise.resolve(null); }
        return window.crypto.subtle.digest('SHA-256', arrayBuffer).then(function (digest) {
            var bytes = new Uint8Array(digest);
            var hex = '';
            for (var i = 0; i < bytes.length; i++) {
                var h = bytes[i].toString(16);
                if (h.length === 1) { h = '0' + h; }
                hex += h;
            }
            return hex.toUpperCase();
        }, function () { return null; });
    }

    function onFileChosen(ev) {
        var fileInput = (ev && ev.target) ? ev.target : document.getElementById(OVERLAY_ID + '-file-input');
        if (!fileInput || !fileInput.files || fileInput.files.length === 0) { return; }
        var file = fileInput.files[0];
        state.lastOperation = 'local';
        state.lastUploadError = null;
        state.lastUploadErrorCode = null;
        state.sessionMessage = null;
        // Client-side validation: bound size + extension. These are UX
        // fast-fails ONLY -- the broker re-validates and is the
        // authoritative trust boundary.
        if (file.size > MAX_UPLOAD_BYTES) {
            state.lastUploadError = 'selected file is ' + file.size + ' bytes, exceeds 4 MiB cap';
            state.lastUploadErrorCode = 'too_large';
            state.sessionMessage = { tone: 'error', text: humanizeAcquisitionError('too_large', 'local') };
            renderState();
            return;
        }
        var nameLower = (file.name || '').toLowerCase();
        if (nameLower.length < 4 || nameLower.lastIndexOf('.ps1') !== nameLower.length - 4) {
            state.lastUploadError = 'selected file does not have a .ps1 extension';
            state.lastUploadErrorCode = 'not_approved';
            state.sessionMessage = { tone: 'error', text: humanizeAcquisitionError('not_approved', 'local') };
            renderState();
            return;
        }
        if (!window.cookbookApi || typeof window.cookbookApi.postBytes !== 'function') {
            state.lastUploadError = 'cookbookApi.postBytes is unavailable';
            state.lastUploadErrorCode = 'read_failed';
            state.sessionMessage = { tone: 'error', text: humanizeAcquisitionError('read', 'local') };
            renderState();
            return;
        }
        state.uploadInFlight = true;
        state.lastUploadError = null;
        state.lastUploadErrorCode = null;
        state.sessionMessage = null;
        renderState();
        file.arrayBuffer().then(function (buf) {
            return computeSha256Hex(buf).then(function (clientSha) {
                var headers = {
                    'X-PAX-Filename':  file.name,
                    'X-PAX-File-Size': String(buf.byteLength)
                };
                if (clientSha) { headers['X-PAX-Client-SHA256'] = clientSha; }
                return window.cookbookApi.postBytes(
                    '/api/v1/setup/acquire-pax/upload-bytes',
                    buf,
                    'application/octet-stream',
                    headers
                );
            });
        }).then(function (resp) {
            state.uploadInFlight = false;
            if (!resp || !resp.ok) {
                var msg = 'upload failed';
                if (resp && resp.body && typeof resp.body === 'object') {
                    if (typeof resp.body.error === 'string')   { msg = resp.body.error; }
                    if (typeof resp.body.message === 'string') { msg += ': ' + resp.body.message; }
                } else if (resp && resp.networkError) {
                    msg = resp.networkError;
                } else if (resp) {
                    msg = 'HTTP ' + resp.status;
                }
                state.lastUploadError = msg;
                var code;
                if (resp && resp.body && typeof resp.body === 'object' && typeof resp.body.error === 'string') {
                    code = resp.body.error;
                } else if (resp && resp.networkError) {
                    code = 'network';
                } else {
                    code = 'upload_failed';
                }
                state.lastUploadErrorCode = code;
                // Fresh, local-scoped plain-English result for THIS
                // selection. Raw detail goes to Support details.
                state.sessionMessage = { tone: 'error', text: humanizeAcquisitionError(code, 'local') };
            } else {
                state.lastUploadError = null;
                state.lastUploadErrorCode = null;
                state.sessionMessage = null;
            }
            fetchStateOnce();
        }, function (err) {
            state.uploadInFlight = false;
            state.lastUploadError = (err && err.message) ? err.message : String(err);
            state.lastUploadErrorCode = 'read_failed';
            state.sessionMessage = { tone: 'error', text: humanizeAcquisitionError('read', 'local') };
            renderState();
        });
    }

    // ----------------------------------------------------------------
    // Event wiring
    // ----------------------------------------------------------------

    function onAcquisitionRequired(ev) {
        // The 409 event carries the gate's own 'details' snapshot but
        // its shape (flat hashtable) differs from /state's nested
        // shape (installState.*, canonicalScript.*). Rather than
        // teach the overlay both shapes, we ignore the event payload
        // and re-fetch /state -- a single source of truth.
        //
        // An explicit gate (a cook/bake attempt) always overrides a
        // prior "Not now" dismissal: clear the flag so the modal
        // re-opens. The acquisitionRequired gate is the only thing that
        // can bring the modal back after the operator dismissed it.
        state.dismissed = false;
        if (!state.mounted) { mount(); }
        fetchStateOnce();
        startPolling();
    }

    function init() {
        try { window.addEventListener('cookbook:acquisitionRequired', onAcquisitionRequired); } catch (e) {}
        if (document.readyState === 'complete' || document.readyState === 'interactive') {
            probeStateOnce();
        } else {
            document.addEventListener('DOMContentLoaded', probeStateOnce, { once: true });
        }
    }

    // One-shot boot probe. If the engine is already acquired (or the
    // policy is embedded), the overlay never mounts; otherwise it
    // mounts and the poll loop takes over.
    function probeStateOnce() {
        fetchStateOnce().then(function () {
            if (state.mounted) { startPolling(); }
        });
    }

    // Diagnostics-only surface. NOT meant for page modules.
    window.cookbookPaxEngineOverlay = {
        state:   function () { return state; },
        refresh: function () { return fetchStateOnce(); },
        force:   function () { mount(); fetchStateOnce(); startPolling(); },
        dismiss: function () { stopPolling(); unmount(); }
    };

    init();
})();
