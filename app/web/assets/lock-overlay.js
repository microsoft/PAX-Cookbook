// PAX Cookbook -- Lock + Re-Auth overlay (Phase AF).
//
// Owns exactly ONE full-viewport modal element (#cookbook-lock-overlay)
// and listens to TWO window-scoped events dispatched by api.js:
//
//   'cookbook:brokerLocked'    -> broker returned 423 with body code
//                                 'brokerLocked'. Shows the unlock
//                                 prompt with a single CTA that POSTs
//                                 /api/v1/broker/unlock. Sticky:
//                                 stays mounted until the unlock POST
//                                 succeeds (state == Unlocked).
//
//   'cookbook:reAuthRequired'  -> broker returned 401 with body code
//                                 'reAuthRequired'. Shows the per-op
//                                 re-auth verdict and an explanation
//                                 message. NOT sticky: dismisses on
//                                 user acknowledgement. The chef
//                                 re-issues the original gated action
//                                 themselves (the SPA does not retry).
//
// Doctrine (verbatim, in force):
//   - The SPA NEVER collects, hashes, compares, or proxies the Windows
//     password. The unlock CTA POSTs an empty body to the broker; the
//     broker calls Windows Hello / PIN itself and returns a verdict.
//   - The overlay NEVER stores any "unlocked once" hint in storage.
//     A page reload starts from /broker/lock-state.
//   - Unlock is BROKER-scoped (per-process) not browser-scoped.
//   - The overlay is the only writer of body.cookbook-lock-active so
//     CSS selectors can dim the underlying page while it is up.
//   - We do not silence other pages' fetches while the overlay is up;
//     they will keep getting 423/401 and they will trigger more
//     overlay events, which is harmless (the overlay coalesces them).
//
// Architectural boundary: this module knows about
//   POST /api/v1/broker/unlock
//   GET  /api/v1/broker/lock-state
// and nothing else. It does NOT inspect recipe state, cooks, or any
// other domain object.

(function () {
    'use strict';

    var OVERLAY_ID    = 'cookbook-lock-overlay';
    var BODY_CLASS    = 'cookbook-lock-active';

    // Module-scoped state. A second event firing while the overlay is
    // already up does NOT remount it -- we just refresh the message.
    // Two events of the same kind coalesce to one render.
    //
    // UX-1H3 fields (lastDiagnostics, lastUnlockAttemptId) carry the
    // most recent unlock attempt's structured trace so the operator
    // can inspect, in the Support details disclosure, exactly which
    // path was selected, why, and what (if anything) failed -- the
    // primary button NEVER silently routes to broker-owned Hello, so
    // every failure has to be explainable on the surface.
    var state = {
        mounted:               false,
        kind:                  null,      // 'brokerLocked' | 'reAuthRequired'
        message:               null,
        opClass:               null,
        verificationResult:    null,
        attemptedMethod:       null,
        attemptedPath:         null,
        unlockInFlight:        false,
        lastShownUtc:          null,
        lastUnlockAttemptId:   null,
        lastDiagnostics:       null,
        lastFailureMessage:    null,
        // UX-1H5 -- two-stage activation-safe bootstrap. preparedBootstrap
        // holds the normalized PublicKeyCredentialCreationOptions shape
        // together with its issue timestamp so an unlock click can call
        // navigator.credentials.create() IMMEDIATELY from the click
        // handler with no intervening fetch / await / DOM rebuild that
        // could lose transient user activation on Chromium / Windows
        // Hello (which UX-1H4 manual testing identified as the suspected
        // root cause of the "no Hello UI ever appears" failure mode).
        preparedBootstrap:       null,     // { publicKey, issuedAtMs, expiresAtMs, prepareAttemptId, optsRaw, challengeByteLength, userIdByteLength, pubKeyCredParamsAlgs }
        preparedBootstrapStatus: 'idle',   // 'idle' | 'pending' | 'ready' | 'failed'
        preparedBootstrapError:  null,
        statusCache:             null,     // last /webauthn/status body, populated by preflight
        lastProbeResult:         null      // most recent diagnostic probe result (UX-1H5 Part C)
    };

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
        panel.className = 'lock-overlay-panel';

        var title = document.createElement('h2');
        title.id = OVERLAY_ID + '-title';
        title.className = 'lock-overlay-title';
        panel.appendChild(title);

        var body = document.createElement('div');
        body.id = OVERLAY_ID + '-body';
        body.className = 'lock-overlay-body';
        panel.appendChild(body);

        var actions = document.createElement('div');
        actions.className = 'lock-overlay-actions';

        var primary = document.createElement('button');
        primary.type = 'button';
        primary.className = 'btn-primary lock-overlay-primary';
        primary.id = OVERLAY_ID + '-primary';
        actions.appendChild(primary);

        var secondary = document.createElement('button');
        secondary.type = 'button';
        secondary.className = 'btn-ghost lock-overlay-secondary';
        secondary.id = OVERLAY_ID + '-secondary';
        secondary.textContent = 'Close';
        actions.appendChild(secondary);

        // Tertiary action: invoke the managed Close App flow (window
        // close + broker shutdown) without first unlocking. Delegates
        // to window.CookbookCloseApp.open() which renders its own
        // modal on top of this overlay (higher z-index). If the
        // operator cancels that modal, this lock overlay is still
        // mounted underneath and remains visible -- no restore work.
        var tertiary = document.createElement('button');
        tertiary.type = 'button';
        tertiary.className = 'btn-ghost lock-overlay-tertiary';
        tertiary.id = OVERLAY_ID + '-tertiary';
        tertiary.textContent = 'Close app and stop server';
        actions.appendChild(tertiary);

        panel.appendChild(actions);

        var status = document.createElement('p');
        status.className = 'lock-overlay-status';
        status.id = OVERLAY_ID + '-status';
        status.setAttribute('aria-live', 'polite');
        panel.appendChild(status);

        overlay.appendChild(panel);
        return overlay;
    }

    function getOverlay() {
        var el = document.getElementById(OVERLAY_ID);
        if (el) { return el; }
        el = buildOverlayDOM();
        document.body.appendChild(el);
        return el;
    }

    // ----------------------------------------------------------------
    // Rendering
    // ----------------------------------------------------------------

    function renderLockedView() {
        var overlay = getOverlay();
        var title   = document.getElementById(OVERLAY_ID + '-title');
        var body    = document.getElementById(OVERLAY_ID + '-body');
        var primary = document.getElementById(OVERLAY_ID + '-primary');
        var secondary = document.getElementById(OVERLAY_ID + '-secondary');
        var status  = document.getElementById(OVERLAY_ID + '-status');

        title.textContent = 'PAX Cookbook is locked';

        // Empty + repopulate body so coalesced events refresh content.
        while (body.firstChild) { body.removeChild(body.firstChild); }
        var p1 = document.createElement('p');
        p1.textContent = state.message ||
            'Unlock to continue working in Cookbook.';
        body.appendChild(p1);

        // UX-1H7 -- first-run passkey explanation. The current
        // broker /webauthn/status response is cached on
        // state.statusCache by the preflight path. When registered
        // is explicitly false (i.e. status was successfully fetched
        // and the appliance has no credential yet), this is the
        // bootstrap-register-unlock entry path and the operator
        // needs to know they are about to CREATE a local passkey,
        // not unlock one that already exists. The copy is
        // explicitly local-device-only and avoids Google Password
        // Manager / passkey-sync language: this passkey stays on
        // the operator's machine.
        var sc = state.statusCache;
        var isFirstRun = !!(sc && sc.registered === false);
        if (isFirstRun) {
            var pPasskey = document.createElement('p');
            pPasskey.className = 'lock-overlay-fine';
            pPasskey.textContent =
                'Cookbook will create a local Windows Hello passkey for this ' +
                'appliance on this device. This stays on your machine and is ' +
                'used only to unlock the local Cookbook session.';
            body.appendChild(pPasskey);

            var pChromeHint = document.createElement('p');
            pChromeHint.className = 'lock-overlay-fine';
            pChromeHint.textContent =
                'Chrome may ask where to save the passkey. Choose Windows ' +
                'Hello / this Windows device for the intended local ' +
                'device-bound flow.';
            body.appendChild(pChromeHint);
        }

        var p2 = document.createElement('p');
        p2.className = 'lock-overlay-fine';
        p2.textContent =
            'For your privacy, Cookbook pauses access when it has been idle ' +
            'or manually locked. Your recipes, settings, and workspace stay ' +
            'on this computer.';
        body.appendChild(p2);

        // Support / diagnostic details collapsed behind a "Support
        // details" disclosure so the lock surface is operator-friendly
        // by default. Copy is plain English; the raw endpoint name is
        // preserved verbatim for support handoff. The disclosure is
        // mounted whenever EITHER the broker reported an attempted
        // endpoint OR the SPA recorded an unlock-attempt diagnostic
        // (UX-1H3); both conditions get their own section inside.
        var hasEndpoint    = !!(state.attemptedMethod && state.attemptedPath);
        var hasDiagnostics = !!state.lastDiagnostics;

        // UX-1H6 -- the Copy diagnostics button moves out of the deep
        // troubleshooting nest and into the main lock box so the
        // operator does not have to discover it. It only renders on
        // a failed attempt: a recorded errorName, a known
        // terminal-failure phase, OR an explicit user-visible
        // failure message produced by the unlock flow.
        var failDiag = state.lastDiagnostics || null;
        // UX-1H10 -- bootstrap_create_pending_timeout was removed
        // from this list. A pending create() promise that has not
        // resolved within the watchdog window is NOT a terminal
        // failure: the ceremony is still legitimately in flight and
        // may succeed (Brian's UX-1H9 manual test produced a
        // successful unlock 5-10s after the watchdog had already
        // fired). The watchdog now records a non-error progress
        // phase (bootstrap_create_pending_slow) instead.
        var failPhases = [
            'bootstrap_create_sync_throw',
            'bootstrap_create_rejected',
            'bootstrap_create_rejected_after_watchdog',
            'browser_ceremony_failed',
            'status_failed',
            'status_network_error',
            'webauthn_unsupported'
        ];
        var hasFailureDiagnostics =
            !!state.lastFailureMessage ||
            (failDiag && (
                !!failDiag.errorName ||
                (failDiag.phase && failPhases.indexOf(failDiag.phase) !== -1)
            )) ||
            state.preparedBootstrapStatus === 'failed';

        if (hasFailureDiagnostics) {
            var copyHint = document.createElement('p');
            copyHint.className = 'lock-overlay-fine';
            copyHint.style.marginTop = '0.75em';
            copyHint.textContent =
                'Unlock did not complete. Click "Copy diagnostics" below and ' +
                'paste the result so the failure can be analyzed.';
            body.appendChild(copyHint);

            var copyBtnMain = document.createElement('button');
            copyBtnMain.type = 'button';
            copyBtnMain.className = 'btn-secondary lock-overlay-copy-diag-main';
            copyBtnMain.id = OVERLAY_ID + '-copy-diagnostics-main';
            copyBtnMain.textContent = 'Copy diagnostics';
            copyBtnMain.style.marginTop = '0.25em';
            copyBtnMain.style.display   = 'block';
            copyBtnMain.addEventListener('click', function (ev) {
                if (ev && typeof ev.preventDefault === 'function') { ev.preventDefault(); }
                copyDiagnosticsToClipboard(copyBtnMain);
            });
            body.appendChild(copyBtnMain);
        }

        if (hasEndpoint || hasDiagnostics) {
            var details = document.createElement('details');
            details.className = 'lock-overlay-details';
            var summary = document.createElement('summary');
            summary.textContent = 'Support details';
            details.appendChild(summary);

            if (hasEndpoint) {
                var dp1 = document.createElement('p');
                dp1.className = 'lock-overlay-fine';
                dp1.textContent =
                    'Cookbook is currently locked. Unlocking with Windows ' +
                    'Hello will reopen access to your recipes and settings.';
                details.appendChild(dp1);

                var dp2 = document.createElement('p');
                dp2.className = 'lock-overlay-fine';
                dp2.textContent =
                    'If you have more than one Cookbook tab open, unlocking one ' +
                    'tab unlocks the others connected to the same running copy ' +
                    'of Cookbook.';
                details.appendChild(dp2);

                var dp3 = document.createElement('p');
                dp3.className = 'lock-overlay-fine';
                dp3.textContent =
                    'Closing this browser tab does not shut down Cookbook. To ' +
                    'fully stop Cookbook, close the Cookbook terminal window.';
                details.appendChild(dp3);

                var dp4 = document.createElement('p');
                dp4.className = 'lock-overlay-fine';
                dp4.textContent =
                    'The last thing Cookbook tried to load before showing this ' +
                    'screen was:';
                details.appendChild(dp4);

                var dp5 = document.createElement('p');
                dp5.className = 'lock-overlay-fine lock-overlay-endpoint';
                dp5.textContent = state.attemptedMethod + ' ' + state.attemptedPath;
                details.appendChild(dp5);

                var dp6 = document.createElement('p');
                dp6.className = 'lock-overlay-fine';
                dp6.textContent =
                    'Share these details only if someone is helping ' +
                    'troubleshoot Cookbook.';
                details.appendChild(dp6);
            }

            // UX-1H3 -- structured unlock-attempt diagnostic block.
            // Renders the last attempt's selected path, failure
            // reason, and an EXPLICIT "Use legacy Windows Hello
            // fallback" button. The button is the ONLY way to
            // invoke the broker-owned terminal Hello path: the
            // primary button never routes here silently.
            if (hasDiagnostics) {
                var diag = state.lastDiagnostics || {};

                var hd = document.createElement('h3');
                hd.className = 'lock-overlay-fine';
                hd.textContent = 'Last unlock attempt';
                hd.style.marginTop = '0.75em';
                details.appendChild(hd);

                var rows = [
                    ['Attempt id',                diag.attemptId || state.lastUnlockAttemptId || '\u2014'],
                    ['Started (UTC)',             diag.startedUtc || '\u2014'],
                    ['Phase reached',             diag.phase || '\u2014'],
                    ['Selected path',             diag.selectedPath || '\u2014'],
                    ['Browser API',               diag.browserApi || '\u2014'],
                    ['Endpoint',                  diag.endpoint || '\u2014'],
                    ['WebAuthn supported',        (diag.webauthnSupported === null || typeof diag.webauthnSupported === 'undefined') ? '\u2014' : String(diag.webauthnSupported)],
                    ['Status fetch ok',           (diag.statusFetchOk === null || typeof diag.statusFetchOk === 'undefined') ? '\u2014' : String(diag.statusFetchOk)],
                    ['Status HTTP code',          (diag.statusFetchStatus === null || typeof diag.statusFetchStatus === 'undefined') ? '\u2014' : String(diag.statusFetchStatus)],
                    ['Credential registered',     (diag.registered === null || typeof diag.registered === 'undefined') ? '\u2014' : String(diag.registered)],
                    ['Fallback policy',           diag.fallbackPolicy || 'no_silent_fallback'],
                    ['Invoked broker-owned path', (diag.willInvokeBrokerOwnedUnlock === null || typeof diag.willInvokeBrokerOwnedUnlock === 'undefined') ? '\u2014' : String(diag.willInvokeBrokerOwnedUnlock)],
                    ['Failure detail',            diag.resultDetail || '\u2014'],
                    ['Error name',                diag.errorName || '\u2014'],
                    ['Error message',             diag.errorMessage || '\u2014'],
                    ['Error stack (first line)',  diag.errorStackFirstLine || '\u2014'],
                    ['Error before create()',     (diag.errorOccurredBeforeCreate === null || typeof diag.errorOccurredBeforeCreate === 'undefined') ? '\u2014' : String(diag.errorOccurredBeforeCreate)],
                    // UX-1H4 -- browser ceremony runtime context.
                    ['Origin',                    diag.locationOrigin   || '\u2014'],
                    ['Protocol',                  diag.locationProtocol || '\u2014'],
                    ['Hostname',                  diag.locationHostname || '\u2014'],
                    ['Secure context',            (diag.isSecureContext === null || typeof diag.isSecureContext === 'undefined') ? '\u2014' : String(diag.isSecureContext)],
                    ['Document visibility',       diag.documentVisibilityState || '\u2014'],
                    ['Document hasFocus',         (diag.documentHasFocus === null || typeof diag.documentHasFocus === 'undefined') ? '\u2014' : String(diag.documentHasFocus)],
                    ['User agent',                diag.userAgent || '\u2014'],
                    ['PublicKeyCredential',       (diag.publicKeyCredentialExists === null || typeof diag.publicKeyCredentialExists === 'undefined') ? '\u2014' : String(diag.publicKeyCredentialExists)],
                    ['has isUVPAA fn',            (diag.hasIsUVPAAFunction === null || typeof diag.hasIsUVPAAFunction === 'undefined') ? '\u2014' : String(diag.hasIsUVPAAFunction)],
                    ['isUVPAA result',            (diag.isUVPAAResult === null || typeof diag.isUVPAAResult === 'undefined') ? '\u2014' : String(diag.isUVPAAResult)],
                    ['has cond. mediation fn',    (diag.hasConditionalMediation === null || typeof diag.hasConditionalMediation === 'undefined') ? '\u2014' : String(diag.hasConditionalMediation)],
                    ['Challenge bytes',           (diag.challengeByteLength === null || typeof diag.challengeByteLength === 'undefined') ? '\u2014' : String(diag.challengeByteLength)],
                    ['user.id bytes',             (diag.userIdByteLength    === null || typeof diag.userIdByteLength    === 'undefined') ? '\u2014' : String(diag.userIdByteLength)],
                    ['pubKeyCredParams algs',     diag.pubKeyCredParamsAlgs ? JSON.stringify(diag.pubKeyCredParamsAlgs) : '\u2014'],
                    ['authenticatorSelection',    diag.authenticatorSelection ? JSON.stringify(diag.authenticatorSelection) : '\u2014'],
                    ['timeout (ms)',              (diag.timeoutMs === null || typeof diag.timeoutMs === 'undefined') ? '\u2014' : String(diag.timeoutMs)],
                    ['rp.id (sent)',              diag.rpId  || '(omitted -- uses effective domain)'],
                    ['rp.name',                   diag.rpName || '\u2014'],
                    ['attestation',               diag.attestation || '\u2014'],
                    ['excludeCredentials count',  (diag.excludeCredentialsCount === null || typeof diag.excludeCredentialsCount === 'undefined') ? '\u2014' : String(diag.excludeCredentialsCount)]
                ];
                for (var ri = 0; ri < rows.length; ri++) {
                    var rp = document.createElement('p');
                    rp.className = 'lock-overlay-fine lock-overlay-endpoint';
                    rp.textContent = rows[ri][0] + ': ' + rows[ri][1];
                    details.appendChild(rp);
                }

                // UX-1H6 -- the Browser Windows Hello diagnostics
                // ladder is now nested behind an additional
                // disclosure inside Support details (two clicks away
                // by default). The Copy diagnostics button moved to
                // the main lock box above this disclosure and only
                // renders on failure, so this section now ONLY hosts
                // the probe controls and the last-probe-result
                // readout.
                var probeDetails = document.createElement('details');
                probeDetails.className = 'lock-overlay-details lock-overlay-probe-details';
                probeDetails.id = OVERLAY_ID + '-probe-disclosure';
                var probeSummary = document.createElement('summary');
                probeSummary.textContent = 'Advanced browser WebAuthn probes';
                probeDetails.appendChild(probeSummary);

                var probeIntro = document.createElement('p');
                probeIntro.className = 'lock-overlay-fine';
                probeIntro.textContent =
                    'These probes each ask Windows Hello to create a credential with ' +
                    'progressively fewer constraints. They are click-triggered and request ' +
                    'a fresh challenge from the broker. A successful probe creates an ' +
                    'authenticator credential on this device that is NOT redeemed for ' +
                    'unlock; this may leave orphaned authenticator state. Use sparingly. ' +
                    'After running a probe, click "Copy diagnostics" and paste the result ' +
                    'so the option set can be adopted as the primary path.';
                probeDetails.appendChild(probeIntro);

                var probeDefs = [
                    { name: 'current_options',                 label: 'Probe 1 \u2014 Current Cookbook options (platform / UV required / residentKey discouraged)' },
                    { name: 'omit_authenticator_attachment',   label: 'Probe 2 \u2014 Omit authenticatorAttachment' },
                    { name: 'minimal_authenticator_selection', label: 'Probe 3 \u2014 Minimal authenticatorSelection (UV only)' },
                    { name: 'minimal_create_options',          label: 'Probe 4 \u2014 Minimal create options (no authenticatorSelection field)' },
                    { name: 'alternative_user_id',             label: 'Probe 5 \u2014 Alternative user.id (locally randomized)' }
                ];
                for (var pi = 0; pi < probeDefs.length; pi++) {
                    (function (def) {
                        var btn = document.createElement('button');
                        btn.type = 'button';
                        btn.className = 'btn-secondary lock-overlay-webauthn-probe';
                        btn.id = OVERLAY_ID + '-probe-' + def.name;
                        btn.textContent = def.label;
                        btn.style.marginTop = '0.25em';
                        btn.style.display   = 'block';
                        btn.addEventListener('click', function (ev) {
                            if (ev && typeof ev.preventDefault === 'function') { ev.preventDefault(); }
                            runDiagProbe(def.name);
                        });
                        probeDetails.appendChild(btn);
                    })(probeDefs[pi]);
                }

                if (state.lastProbeResult) {
                    var lpr = state.lastProbeResult;
                    var lprHd = document.createElement('p');
                    lprHd.className = 'lock-overlay-fine';
                    lprHd.textContent = 'Last probe result:';
                    lprHd.style.marginTop = '0.5em';
                    probeDetails.appendChild(lprHd);
                    var lprFields = [
                        ['Probe',                       lpr.probeName  || '\u2014'],
                        ['Started (UTC)',               lpr.startedUtc || '\u2014'],
                        ['Outcome',                     lpr.outcome    || '\u2014'],
                        ['Elapsed (ms)',                (typeof lpr.elapsedMs === 'number') ? String(lpr.elapsedMs) : '\u2014'],
                        ['Error name',                  lpr.errorName    || '\u2014'],
                        ['Error message',               lpr.errorMessage || '\u2014'],
                        ['Error stack 1st line',        lpr.errorStackFirstLine || '\u2014'],
                        ['UI opened (best-effort)',     (lpr.uiOpened === null || typeof lpr.uiOpened === 'undefined') ? '\u2014' : String(lpr.uiOpened)],
                        ['Credential returned',         (lpr.credentialReturned === null || typeof lpr.credentialReturned === 'undefined') ? '\u2014' : String(lpr.credentialReturned)],
                        ['Credential type',             lpr.credentialType || '\u2014'],
                        ['attestationObject bytes',     (typeof lpr.attestationObjectBytes === 'number') ? String(lpr.attestationObjectBytes) : '\u2014'],
                        ['clientDataJSON bytes',        (typeof lpr.clientDataJsonBytes  === 'number') ? String(lpr.clientDataJsonBytes)  : '\u2014'],
                        ['userActivation isActive',     (lpr.userActivationIsActive === null || typeof lpr.userActivationIsActive === 'undefined') ? '\u2014' : String(lpr.userActivationIsActive)],
                        ['userActivation hasBeenActive',(lpr.userActivationHasBeenActive === null || typeof lpr.userActivationHasBeenActive === 'undefined') ? '\u2014' : String(lpr.userActivationHasBeenActive)],
                        ['Options summary',             lpr.optionsSummary || '\u2014']
                    ];
                    for (var lpi = 0; lpi < lprFields.length; lpi++) {
                        var lprP = document.createElement('p');
                        lprP.className = 'lock-overlay-fine lock-overlay-endpoint';
                        lprP.textContent = lprFields[lpi][0] + ': ' + lprFields[lpi][1];
                        probeDetails.appendChild(lprP);
                    }
                }

                details.appendChild(probeDetails);
            }

            body.appendChild(details);
        }

        // Primary button copy. The label is identical for first-run
        // and steady-state because the unlock route is the same in
        // both cases (no separate bootstrap UX in v1). The 'Try
        // again' / failure label is set by onPrimaryClick when a
        // verdict comes back non-Verified.
        var primaryText;
        if (state.unlockInFlight) {
            primaryText = 'Authenticating\u2026';
        } else if (state.lastFailureMessage) {
            primaryText = 'Try again';
        } else {
            primaryText = 'Authenticate';
        }
        primary.textContent = primaryText;
        primary.disabled = !!state.unlockInFlight;
        secondary.style.display = 'none'; // locked view has no "Close" -- unlock is the only exit

        status.textContent = '';
        return primary;
    }

    function renderReAuthView() {
        var overlay = getOverlay();
        var title   = document.getElementById(OVERLAY_ID + '-title');
        var body    = document.getElementById(OVERLAY_ID + '-body');
        var primary = document.getElementById(OVERLAY_ID + '-primary');
        var secondary = document.getElementById(OVERLAY_ID + '-secondary');
        var status  = document.getElementById(OVERLAY_ID + '-status');

        title.textContent = 'Verification was required';

        while (body.firstChild) { body.removeChild(body.firstChild); }
        var p1 = document.createElement('p');
        p1.textContent = state.message ||
            'The broker required a fresh Windows Hello verdict for this action. Try the action again to be prompted.';
        body.appendChild(p1);

        if (state.opClass) {
            var p2 = document.createElement('p');
            p2.className = 'lock-overlay-fine';
            p2.textContent = 'Operation: ' + state.opClass;
            body.appendChild(p2);
        }
        if (state.verificationResult && state.verificationResult !== 'Verified') {
            var p3 = document.createElement('p');
            p3.className = 'lock-overlay-fine';
            p3.textContent = 'Verification result: ' + state.verificationResult;
            body.appendChild(p3);
        }

        primary.textContent = 'OK';
        primary.disabled = false;
        secondary.style.display = 'none';

        status.textContent = '';
        return primary;
    }

    function mount() {
        if (state.mounted) { return; }
        var overlay = getOverlay();
        overlay.classList.add('visible');
        document.body.classList.add(BODY_CLASS);
        state.mounted = true;
        state.lastShownUtc = new Date().toISOString();
        wireButtons();
        // Move focus to the primary button so SR / keyboard users
        // land on the action.
        var primary = document.getElementById(OVERLAY_ID + '-primary');
        if (primary) {
            try { primary.focus(); } catch (e) {}
        }
        // Kick off the bootstrap preflight in the background as soon
        // as the lock overlay shows. By the time the operator clicks
        // the primary button, the prepared options shape is already
        // in memory, so the click can call navigator.credentials.create()
        // synchronously with no preceding fetch / await chain. That
        // preserves the browser's transient user activation across the
        // call into the platform authenticator, which is what makes the
        // Windows Hello prompt appear. preflightBootstrap() is idempotent
        // and a no-op when the cached status already says registered=true.
        if (state.kind === 'brokerLocked') {
            try { preflightBootstrap(); } catch (ePf) {}
        }
    }

    function unmount() {
        var overlay = document.getElementById(OVERLAY_ID);
        if (overlay) { overlay.classList.remove('visible'); }
        document.body.classList.remove(BODY_CLASS);
        state.mounted = false;
        state.kind    = null;
        state.message = null;
        state.opClass = null;
        state.verificationResult = null;
        state.attemptedMethod    = null;
        state.attemptedPath      = null;
        state.unlockInFlight     = false;
        // UX-1H3 diagnostics survive unmount because the overlay can
        // get remounted by a follow-up 423 from a different page
        // module and the operator may want to scroll back through
        // the last failure they saw. They are reset on the NEXT
        // primary click (start of performUnlock).
    }

    // ----------------------------------------------------------------
    // Button wiring
    // ----------------------------------------------------------------

    function wireButtons() {
        var primary = document.getElementById(OVERLAY_ID + '-primary');
        var secondary = document.getElementById(OVERLAY_ID + '-secondary');
        var tertiary = document.getElementById(OVERLAY_ID + '-tertiary');
        if (primary && !primary._cookbookWired) {
            primary.addEventListener('click', onPrimaryClick);
            primary._cookbookWired = true;
        }
        if (secondary && !secondary._cookbookWired) {
            secondary.addEventListener('click', unmount);
            secondary._cookbookWired = true;
        }
        if (tertiary && !tertiary._cookbookWired) {
            tertiary.addEventListener('click', function (ev) {
                if (ev && typeof ev.preventDefault === 'function') { ev.preventDefault(); }
                if (window.CookbookCloseApp && typeof window.CookbookCloseApp.open === 'function') {
                    window.CookbookCloseApp.open();
                }
            });
            tertiary._cookbookWired = true;
        }
    }

    function onPrimaryClick(ev) {
        if (ev && typeof ev.preventDefault === 'function') { ev.preventDefault(); }
        if (state.kind !== 'brokerLocked') {
            if (state.kind === 'reAuthRequired') { unmount(); }
            return;
        }
        if (state.unlockInFlight) { return; }
        // Hold the operator's gesture context in the browser window.
        // The primary button is BROWSER-OWNED ONLY. Both the
        // steady-state ceremony (navigator.credentials.get) AND the
        // bootstrap ceremony (navigator.credentials.create) are rendered
        // by the browser process, so the Windows Security prompt appears
        // anchored to the same window as the Cookbook tab. There is NO
        // silent path from this click into the broker-owned terminal
        // Hello; if browser-owned Hello cannot complete, the overlay
        // surfaces a diagnostic + an explicit "Use legacy Windows Hello
        // fallback" button inside Support details.
        try { window.focus(); } catch (e) {}

        // Activation-safe two-stage bootstrap path. If we already have a
        // prepared bootstrap challenge from the preflight that ran on
        // overlay mount, we MUST call navigator.credentials.create()
        // immediately from this click handler, with no fetch / await /
        // DOM rebuild in between, so the browser's transient user
        // activation is preserved across the call into the platform
        // authenticator. A status-then-challenge-then-create promise
        // chain is the activation-loss boundary this two-stage flow
        // removes.
        if (state.preparedBootstrap && isPreparedBootstrapFresh()) {
            performUnlockFromPrepared(state.preparedBootstrap);
            return;
        }

        // No fresh prepared challenge. If cached status says the
        // workspace already has a registered credential, take the
        // steady-state /unlock-challenge path via performUnlock. The
        // steady-state flow uses navigator.credentials.get, which the
        // browser treats more permissively than create, so it runs the
        // fetch chain inline.
        if (state.statusCache && state.statusCache.registered === true) {
            performUnlock();
            return;
        }

        // Bootstrap path but no fresh prepared challenge. Kick off
        // preflight (idempotent) and ask the operator for a second
        // click. We do NOT call create() here because the activation
        // would have to survive the challenge fetch promise.
        var wasRetry             = !!state.lastUnlockAttemptId;
        var registeredAtRetrySnp = (state.statusCache && typeof state.statusCache.registered === 'boolean')
                                       ? state.statusCache.registered
                                       : null;
        var locationHostnameSnp  = (window.location && window.location.hostname) || null;
        state.lastUnlockAttemptId = newUnlockAttemptId();
        state.lastDiagnostics     = null;
        state.lastFailureMessage  = null;
        recordDiag({
            webauthnSupported:      webauthnSupported(),
            selectedPath:           'browser_webauthn_bootstrap_create',
            browserApi:             'navigator.credentials.create',
            endpoint:               '/api/v1/broker/webauthn/bootstrap-register-unlock',
            phase:                  'bootstrap_preflight_required_by_click',
            isRetry:                wasRetry,
            registeredAtRetry:      registeredAtRetrySnp,
            locationHostname:       locationHostnameSnp,
            legacyFallbackInvoked:  false
        });
        logUnlock(state.lastDiagnostics);
        preflightBootstrap(); // fire-and-forget; updates state.preparedBootstrap when ready
        setUnlockUi(
            'Preparing Windows Hello\u2026 the button will say "Continue with Windows Hello" when ready.',
            true,
            'Preparing\u2026'
        );
    }

    // ----------------------------------------------------------------
    // /broker/unlock orchestration
    // ----------------------------------------------------------------

    // UX-1H -- byte conversion helpers for WebAuthn payload exchange.
    // The broker speaks base64url (challenge, credentialId,
    // clientDataJSON, authenticatorData, signature). The browser's
    // WebAuthn API uses ArrayBuffer. publicKeySpki is base64 (no -url
    // variant) to keep parity with how SPKI is conventionally encoded
    // (PEM body == standard base64). All conversions are pure,
    // dependency-free, and never log or store any value.
    function b64uToArrayBuffer(b64u) {
        var s = String(b64u || '').replace(/-/g, '+').replace(/_/g, '/');
        var pad = s.length % 4;
        if (pad === 2) { s += '=='; }
        else if (pad === 3) { s += '='; }
        else if (pad !== 0 && pad !== 1) { throw new Error('webauthn: invalid base64url length'); }
        var bin = window.atob(s);
        var out = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
        return out.buffer;
    }
    function arrayBufferToB64u(buf) {
        var bytes = (buf instanceof Uint8Array) ? buf : new Uint8Array(buf);
        var s = '';
        for (var i = 0; i < bytes.length; i++) { s += String.fromCharCode(bytes[i]); }
        return window.btoa(s).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
    }
    function arrayBufferToB64(buf) {
        var bytes = (buf instanceof Uint8Array) ? buf : new Uint8Array(buf);
        var s = '';
        for (var i = 0; i < bytes.length; i++) { s += String.fromCharCode(bytes[i]); }
        return window.btoa(s);
    }

    // ----------------------------------------------------------------
    // UX-1H3 -- unlock-attempt diagnostics
    // ----------------------------------------------------------------
    //
    // Every operator click on the primary unlock button gets a fresh
    // attemptId of the form 'UX1H3-<ms-since-epoch>-<random-4-chars>'.
    // The id is:
    //   - logged to console.log under '[PAX Cookbook Unlock]' so the
    //     operator can correlate browser-side trace with broker-side
    //     trace by id;
    //   - appended to every unlock-related fetch as ?attempt=<id> so
    //     the broker records the same id on its end (the SPA API
    //     helper does not expose custom request headers, so a query
    //     param is the cleanest way to thread the id);
    //   - persisted in state.lastUnlockAttemptId and surfaced inside
    //     the Support details disclosure so the operator can see the
    //     full structured diagnostic of the LAST attempt.
    //
    // The diagnostic record (state.lastDiagnostics) is a flat
    // hashmap of stable string fields documented in
    // _temp\phase_ux1h3_verification\UX1H3_*_REPORT.md. The recorder
    // does NOT proxy or persist any WebAuthn payload bytes -- only
    // path decisions and stable failure reasons.
    function newUnlockAttemptId() {
        var r = '';
        try {
            var arr = new Uint8Array(3);
            (window.crypto || window.msCrypto).getRandomValues(arr);
            for (var i = 0; i < arr.length; i++) {
                var h = arr[i].toString(16);
                if (h.length < 2) { h = '0' + h; }
                r += h;
            }
            r = r.slice(0, 4);
        } catch (e) {
            r = Math.random().toString(36).slice(2, 6);
        }
        return 'UX1H3-' + Date.now() + '-' + r;
    }

    function logUnlock(diag) {
        // Single, structured console line per state transition. The
        // operator can collect these from the browser devtools
        // console under the '[PAX Cookbook Unlock]' prefix.
        try {
            // eslint-disable-next-line no-console
            console.log('[PAX Cookbook Unlock]', diag);
        } catch (e) {}
    }

    function recordDiag(updates) {
        if (!state.lastDiagnostics) {
            state.lastDiagnostics = {
                attemptId:                 state.lastUnlockAttemptId || null,
                startedUtc:                new Date().toISOString(),
                webauthnSupported:         null,
                statusFetchOk:             null,
                statusFetchStatus:         null,
                registered:                null,
                selectedPath:              null,
                endpoint:                  null,
                browserApi:                null,
                phase:                     null,
                errorName:                 null,
                errorMessage:              null,
                errorStackFirstLine:       null,
                errorOccurredBeforeCreate: null,
                fallbackPolicy:            'no_silent_fallback',
                willInvokeBrokerOwnedUnlock: false,
                resultOk:                  null,
                resultDetail:              null,
                // UX-1H4 -- bootstrap-specific runtime context
                // captured immediately before navigator.credentials.create
                // so an operator can see exactly what state the
                // browser ceremony was attempted from.
                locationOrigin:            null,
                locationProtocol:          null,
                locationHostname:          null,
                isSecureContext:           null,
                documentVisibilityState:   null,
                documentHasFocus:          null,
                userAgent:                 null,
                publicKeyCredentialExists: null,
                hasIsUVPAAFunction:        null,
                isUVPAAResult:             null,
                hasConditionalMediation:   null,
                challengeByteLength:       null,
                userIdByteLength:          null,
                pubKeyCredParamsAlgs:      null,
                authenticatorSelection:    null,
                timeoutMs:                 null,
                rpId:                      null,
                rpName:                    null,
                attestation:               null,
                excludeCredentialsCount:   null,
                excludeCredentialsIdLens:  null,
                // UX-1H5 -- two-stage activation-safe context.
                userActivationIsActive:      null,
                userActivationHasBeenActive: null,
                bootstrapPreparedFlag:       null,
                preparedIssuedAtUtc:         null,
                preparedExpiresAtUtc:        null,
                usedPreparedChallenge:       null,
                createWatchdogElapsedMs:     null,
                createElapsedMs:             null
            };
        }
        if (updates && typeof updates === 'object') {
            for (var k in updates) {
                if (Object.prototype.hasOwnProperty.call(updates, k)) {
                    state.lastDiagnostics[k] = updates[k];
                }
            }
        }
        return state.lastDiagnostics;
    }

    function withAttempt(path) {
        // Threads the current attempt id onto an unlock endpoint URL
        // as a query parameter so the broker can correlate. The
        // broker is free to ignore the parameter; nothing about
        // body-signed clientDataJSON depends on it.
        var id = state.lastUnlockAttemptId;
        if (!id || typeof path !== 'string' || path.length === 0) { return path; }
        var sep = (path.indexOf('?') >= 0) ? '&' : '?';
        return path + sep + 'attempt=' + encodeURIComponent(id);
    }

    function webauthnSupported() {
        // Both the API and the platform-authenticator predicate must
        // be present. Older Edge / Chromium-on-Win10 may expose the
        // API but lack the predicate; we treat that as "not
        // supported" and surface a diagnostic + the explicit legacy
        // fallback button. UX-1H3 forbids silent re-routing to
        // broker-owned Hello: an operator who needs the legacy path
        // has to click for it.
        try {
            return !!(window.PublicKeyCredential &&
                      window.navigator &&
                      window.navigator.credentials &&
                      typeof window.navigator.credentials.create === 'function' &&
                      typeof window.navigator.credentials.get    === 'function');
        } catch (e) {
            return false;
        }
    }

    function setUnlockUi(stateText, btnDisabled, btnText) {
        var primary = document.getElementById(OVERLAY_ID + '-primary');
        var status  = document.getElementById(OVERLAY_ID + '-status');
        if (primary) {
            primary.disabled    = !!btnDisabled;
            primary.textContent = btnText;
        }
        if (status && stateText !== null && typeof stateText !== 'undefined') {
            status.textContent = stateText;
        }
    }

    function finishUnlockSuccess() {
        // Dispatch the brokerUnlocked event and reload so every page
        // module re-runs its initial load with a clean session state.
        // The session token persists in sessionStorage across the
        // reload (boot.js' "no hash present but existing token"
        // branch picks it back up) and the boot-time /broker/lock-state
        // probe will see Unlocked and not re-mount the overlay.
        try {
            window.dispatchEvent(new CustomEvent('cookbook:brokerUnlocked', {
                detail: { timestampUtc: new Date().toISOString() }
            }));
        } catch (e) {}
        setUnlockUi('Unlocked. Refreshing...', true, 'Unlocked');
        setTimeout(function () {
            try { window.location.reload(); } catch (e) { unmount(); }
        }, 250);
    }

    function surfaceUnlockFailure(resp) {
        // Common "non-success" rendering for both WebAuthn and
        // legacy paths. Mirrors the V1.S18 / pre-UX-1H behavior:
        // keep the overlay up, surface the verdict, re-enable the
        // button so the operator can retry.
        var verdict = (resp && resp.body && resp.body.verificationResult)
            ? resp.body.verificationResult
            : 'Unknown';
        var msg = (resp && resp.body && resp.body.message)
            ? resp.body.message
            : 'Verification did not succeed.';
        setUnlockUi(msg + ' (verdict: ' + verdict + ')', false, 'Try again');
    }

    // Browser-owned WebAuthn unlock attempt (steady-state path:
    // a credential is already registered for this workspace).
    // Returns a Promise that resolves to { ok: true, resp } on
    // success, or { ok: false, reason } on any failure. The
    // 'user_cancelled' reason is reserved for NotAllowedError /
    // AbortError, which is how the Windows Security dialog
    // reports the operator dismissing or vetoing the prompt.
    // UX-1H3: NEITHER user_cancelled NOR technical failures
    // silently re-route to broker-owned Hello. The caller
    // (performUnlock) surfaces a diagnostic + the explicit
    // legacy fallback button. The reason vocab below is what
    // ends up in state.lastDiagnostics.resultDetail / errorMessage.
    function tryWebAuthnUnlock(statusBody) {
        if (!webauthnSupported())                  { return Promise.resolve({ ok: false, reason: 'webauthn_unsupported' }); }
        if (!statusBody || !statusBody.registered) { return Promise.resolve({ ok: false, reason: 'no_credential' });        }

        return window.cookbookApi.post(withAttempt('/api/v1/broker/webauthn/unlock-challenge'), {}).then(function (chResp) {
            if (!chResp || !chResp.ok || !chResp.body || !chResp.body.challenge) {
                return { ok: false, reason: 'challenge_failed' };
            }
            var opts = chResp.body;
            var allowCredentials = (statusBody.credentialIds || []).map(function (id) {
                return { type: 'public-key', id: b64uToArrayBuffer(id), transports: ['internal'] };
            });
            var getOpts = {
                publicKey: {
                    challenge:        b64uToArrayBuffer(opts.challenge),
                    allowCredentials: allowCredentials,
                    userVerification: 'required',
                    timeout:          (opts.timeoutMs || 60000)
                    // rp.id intentionally omitted -- the browser
                    // defaults to the effective domain of the page
                    // origin (either 127.0.0.1 or localhost
                    // depending on which the launcher opened).
                }
            };
            setUnlockUi('Opening Windows Hello\u2026', true, 'Verifying\u2026');
            return window.navigator.credentials.get(getOpts).then(function (assertion) {
                if (!assertion || !assertion.response) {
                    return { ok: false, reason: 'no_assertion' };
                }
                setUnlockUi('Verifying with Windows Hello\u2026', true, 'Verifying\u2026');
                var body = {
                    credentialId:      arrayBufferToB64u(assertion.rawId),
                    clientDataJSON:    arrayBufferToB64u(assertion.response.clientDataJSON),
                    authenticatorData: arrayBufferToB64u(assertion.response.authenticatorData),
                    signature:         arrayBufferToB64u(assertion.response.signature),
                    challenge:         opts.challenge
                };
                return window.cookbookApi.post(withAttempt('/api/v1/broker/webauthn/unlock'), body).then(function (resp) {
                    if (resp && resp.ok && resp.body && resp.body.state === 'Unlocked') {
                        return { ok: true, resp: resp };
                    }
                    return { ok: false, reason: 'broker_rejected', resp: resp };
                }, function () {
                    return { ok: false, reason: 'broker_network_error' };
                });
            }, function (err) {
                // navigator.credentials.get rejection. NotAllowedError
                // and AbortError mean the operator cancelled the
                // platform-authenticator ceremony (closed the
                // Windows Security dialog or hit Esc); everything
                // else is a technical failure (RP-ID rejection,
                // platform-authenticator unavailable, transport
                // issues). UX-1H3: neither category silently falls
                // back to broker-owned Hello. performUnlock()
                // surfaces a diagnostic and an explicit fallback
                // button instead.
                var name = (err && err.name) ? err.name : 'unknown';
                if (name === 'NotAllowedError' || name === 'AbortError') {
                    return { ok: false, reason: 'user_cancelled', errName: name };
                }
                return { ok: false, reason: 'navigator_get_failed:' + name };
            });
        }, function () {
            return { ok: false, reason: 'challenge_network_error' };
        });
    }

    // UX-1H2 -- browser-owned WebAuthn BOOTSTRAP unlock. The very
    // first unlock on a fresh workspace (no credential registered
    // yet) takes this path: a single browser-owned Windows Hello
    // ceremony enrolls a new credential AND transitions the broker
    // Locked -> Unlocked atomically, via the LOCK-BYPASS endpoint
    // /api/v1/broker/webauthn/bootstrap-register-unlock. This
    // eliminates the legacy "broker-owned Hello on the wrong
    // monitor for the first unlock" UX entirely.
    //
    // The broker verifies clientDataJSON (type='webauthn.create',
    // challenge, origin), authenticatorData (UP+UV flags set), and
    // SPKI (ES256 only) before persisting the credential. See
    // app/broker/Routes/BrokerWebAuthn.ps1 ::
    // Invoke-BrokerWebAuthnBootstrapRegisterUnlock.
    //
    // UX-1H4 -- this function is now heavily instrumented around the
    // navigator.credentials.create call. The browser ceremony has
    // historically failed to even open the Windows Hello prompt for
    // operators on this build, with the only externally-visible
    // symptom being a repeated bootstrap-register-challenge fetch on
    // the broker side. The instrumentation below captures:
    //   - exactly which sub-phase the ceremony is in when it
    //     stops (status fetch, challenge fetch, option normalize,
    //     pre-create, sync-throw, async-reject, resolve, post),
    //   - exactly what the runtime context looked like immediately
    //     before create() (origin, secure context, focus, UA,
    //     UVPAA availability, byte lengths, the publicKey option
    //     shape, the chosen authenticatorSelection),
    //   - exactly what the browser-side error object looked like
    //     (.name, .message, first line of .stack) and whether the
    //     error occurred BEFORE create() was reached or AFTER.
    //
    // The instrumentation is logged to console.log('[PAX Cookbook
    // Unlock]', ...) AND mirrored into state.lastDiagnostics so the
    // Support details disclosure surfaces it to a non-DevTools
    // operator.
    //
    // Returns { ok, resp } or { ok, reason } with the same
    // user_cancelled convention as tryWebAuthnUnlock.
    function tryWebAuthnBootstrap() {
        if (!webauthnSupported()) { return Promise.resolve({ ok: false, reason: 'webauthn_unsupported' }); }

        recordDiag({ phase: 'bootstrap_status_fetch_start' });
        logUnlock(state.lastDiagnostics);

        recordDiag({ phase: 'bootstrap_challenge_fetch_start' });
        logUnlock(state.lastDiagnostics);
        return window.cookbookApi.post(withAttempt('/api/v1/broker/webauthn/bootstrap-register-challenge'), {}).then(function (chResp) {
            recordDiag({
                phase:             'bootstrap_challenge_fetch_done',
                statusFetchStatus: (chResp ? chResp.status : null)
            });
            logUnlock(state.lastDiagnostics);
            if (!chResp || !chResp.ok || !chResp.body || !chResp.body.challenge) {
                recordDiag({
                    phase:                     'bootstrap_create_sync_throw',
                    errorName:                 'challenge_failed',
                    errorMessage:              'bootstrap-register-challenge returned no body or no challenge',
                    errorOccurredBeforeCreate: true
                });
                logUnlock(state.lastDiagnostics);
                return { ok: false, reason: 'challenge_failed' };
            }
            var opts = chResp.body;

            // ----------------------------------------------------------
            // UX-1H4 Part C -- normalize and validate publicKey options
            // ----------------------------------------------------------
            recordDiag({ phase: 'bootstrap_options_normalize_start' });
            logUnlock(state.lastDiagnostics);

            var challengeBuf, userIdBuf;
            try {
                challengeBuf = b64uToArrayBuffer(opts.challenge);
                userIdBuf    = b64uToArrayBuffer(opts.userIdBase64Url || opts.challenge);
            } catch (eConv) {
                recordDiag({
                    phase:                     'bootstrap_create_sync_throw',
                    errorName:                 (eConv && eConv.name)    || 'b64u_conversion_failed',
                    errorMessage:              (eConv && eConv.message) || 'failed to decode challenge or user.id',
                    errorOccurredBeforeCreate: true
                });
                logUnlock(state.lastDiagnostics);
                return { ok: false, reason: 'b64u_conversion_failed' };
            }
            var challengeBytes = challengeBuf.byteLength;
            var userIdBytes    = userIdBuf.byteLength;
            if (challengeBytes < 16) {
                recordDiag({
                    phase:                     'bootstrap_create_sync_throw',
                    errorName:                 'challenge_too_short',
                    errorMessage:              ('challenge byte length ' + challengeBytes + ' is below the WebAuthn minimum of 16'),
                    errorOccurredBeforeCreate: true,
                    challengeByteLength:       challengeBytes
                });
                logUnlock(state.lastDiagnostics);
                return { ok: false, reason: 'challenge_too_short' };
            }
            if (userIdBytes < 1 || userIdBytes > 64) {
                recordDiag({
                    phase:                     'bootstrap_create_sync_throw',
                    errorName:                 'user_id_byte_length_out_of_range',
                    errorMessage:              ('user.id byte length ' + userIdBytes + ' is outside the WebAuthn-required 1..64 range'),
                    errorOccurredBeforeCreate: true,
                    userIdByteLength:          userIdBytes
                });
                logUnlock(state.lastDiagnostics);
                return { ok: false, reason: 'user_id_byte_length_out_of_range' };
            }

            // pubKeyCredParams: keep only well-formed entries with a
            // numeric `alg`. The default ES256 entry is the minimum
            // viable set for Windows Hello. Defensive against the
            // broker accidentally serializing alg as a string.
            var pkcp = [];
            if (Array.isArray(opts.pubKeyCredParams)) {
                for (var i = 0; i < opts.pubKeyCredParams.length; i++) {
                    var p = opts.pubKeyCredParams[i];
                    if (p && p.type === 'public-key' && typeof p.alg === 'number') {
                        pkcp.push({ type: 'public-key', alg: p.alg });
                    }
                }
            }
            if (pkcp.length === 0) { pkcp = [{ type: 'public-key', alg: -7 }]; }
            var pkcpAlgs = pkcp.map(function (p) { return p.alg; });

            // UX-1H4 -- authenticatorSelection simplified.
            // Previously residentKey was 'preferred', which in some
            // Chromium / Windows Hello combinations was observed to
            // cause create() to reject immediately without showing
            // the Windows Hello dialog (the symptom Brian reported).
            // The Cookbook flow does NOT require a discoverable
            // (resident) credential -- the broker always supplies
            // the credentialId via /webauthn/status and the SPA
            // passes it through allowCredentials on the unlock-get
            // path. So 'discouraged' is both safe and the most
            // universally compatible setting. requireResidentKey is
            // intentionally omitted (deprecated legacy field).
            var authSel = {
                authenticatorAttachment: 'platform',
                userVerification:        'required',
                residentKey:             'discouraged'
            };

            // rp.id intentionally omitted. The broker does not return
            // an rpId, and on localhost / 127.0.0.1 the browser
            // defaults to the effective domain of the page origin --
            // which is exactly what we want (the broker's
            // Test-WebAuthnOriginAcceptable accepts both
            // http://127.0.0.1:<port> and http://localhost:<port>).
            // Setting rp.id explicitly here would only narrow that
            // accepted set and risk an opaque NotAllowedError.
            var publicKey = {
                challenge:              challengeBuf,
                rp:                     { name: (opts.rpName || 'PAX Cookbook') },
                user: {
                    id:          userIdBuf,
                    name:        (opts.userName        || 'cookbook'),
                    displayName: (opts.userDisplayName || 'Cookbook')
                },
                pubKeyCredParams:       pkcp,
                authenticatorSelection: authSel,
                timeout:                (opts.timeoutMs || 60000),
                attestation:            'none'
            };
            // Bootstrap path: by definition the workspace has no
            // registered credential, so excludeCredentials must be
            // empty (not omitted -- the spec allows an empty array
            // and it is the most explicit signal to the platform
            // authenticator that there are no duplicates to dedupe
            // against).
            publicKey.excludeCredentials = [];

            recordDiag({
                phase:                    'bootstrap_options_normalize_done',
                challengeByteLength:      challengeBytes,
                userIdByteLength:         userIdBytes,
                pubKeyCredParamsAlgs:     pkcpAlgs,
                authenticatorSelection:   authSel,
                timeoutMs:                publicKey.timeout,
                rpId:                     (opts.rpId || null),
                rpName:                   publicKey.rp.name,
                attestation:              publicKey.attestation,
                excludeCredentialsCount:  publicKey.excludeCredentials.length,
                excludeCredentialsIdLens: []
            });
            logUnlock(state.lastDiagnostics);

            // ----------------------------------------------------------
            // UX-1H4 Part B + D -- pre-create capture + activation hint
            // ----------------------------------------------------------
            //
            // The diagnostic capture below is logged from inside the
            // same promise-resolution microtask that received the
            // challenge response. Chromium preserves transient user
            // activation across an .then() chain provided we do NOT
            // schedule any setTimeout / requestAnimationFrame / extra
            // await between fetch and create. We also call
            // window.focus() as a belt-and-braces hint; it is not
            // load-bearing.
            var isUVPAAFn = (window.PublicKeyCredential &&
                             typeof window.PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable === 'function');
            var hasCondMedFn = (window.PublicKeyCredential &&
                                typeof window.PublicKeyCredential.isConditionalMediationAvailable === 'function');

            // Probe isUVPAA before create() but do NOT await it; we
            // log the boolean result asynchronously so the value
            // appears alongside bootstrap_create_resolved /
            // bootstrap_create_rejected. Awaiting it would consume
            // user activation.
            if (isUVPAAFn) {
                try {
                    window.PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable().then(function (v) {
                        recordDiag({ isUVPAAResult: !!v });
                        logUnlock(state.lastDiagnostics);
                    }, function () {
                        recordDiag({ isUVPAAResult: 'rejected' });
                        logUnlock(state.lastDiagnostics);
                    });
                } catch (eU) { /* noop */ }
            }

            var preCreateDiag = {
                phase:                     'bootstrap_pre_create',
                locationOrigin:            (window.location && window.location.origin)   || null,
                locationProtocol:          (window.location && window.location.protocol) || null,
                locationHostname:          (window.location && window.location.hostname) || null,
                isSecureContext:           !!window.isSecureContext,
                documentVisibilityState:   (document && document.visibilityState) || null,
                documentHasFocus:          (function () { try { return !!document.hasFocus(); } catch (e) { return null; } })(),
                userAgent:                 (window.navigator && window.navigator.userAgent) || null,
                publicKeyCredentialExists: !!window.PublicKeyCredential,
                hasIsUVPAAFunction:        !!isUVPAAFn,
                hasConditionalMediation:   !!hasCondMedFn
            };
            recordDiag(preCreateDiag);
            logUnlock(state.lastDiagnostics);

            try { window.focus(); } catch (eF) {}

            // Keep the status text update minimal; setUnlockUi does
            // not re-render the overlay (it only writes button label
            // + status text), so user activation is preserved.
            setUnlockUi('Setting up Windows Hello for this browser\u2026', true, 'Setting up\u2026');

            // ----------------------------------------------------------
            // The actual create() call. Wrapped in try/catch so we
            // can distinguish a synchronous throw (e.g. TypeError on
            // a malformed options object) from an asynchronous
            // rejection (e.g. NotAllowedError after the platform
            // authenticator rejected the ceremony).
            // ----------------------------------------------------------
            var createPromise;
            try {
                createPromise = window.navigator.credentials.create({ publicKey: publicKey });
            } catch (eSync) {
                recordDiag({
                    phase:                     'bootstrap_create_sync_throw',
                    errorName:                 (eSync && eSync.name)    || 'sync_throw',
                    errorMessage:              (eSync && eSync.message) || 'navigator.credentials.create threw synchronously',
                    errorStackFirstLine:       (eSync && eSync.stack)
                                                ? String(eSync.stack).split('\n')[0]
                                                : null,
                    errorOccurredBeforeCreate: false
                });
                logUnlock(state.lastDiagnostics);
                return { ok: false, reason: 'navigator_create_sync_throw:' + ((eSync && eSync.name) || 'unknown') };
            }

            return createPromise.then(function (cred) {
                recordDiag({ phase: 'bootstrap_create_resolved' });
                logUnlock(state.lastDiagnostics);
                if (!cred || !cred.response) { return { ok: false, reason: 'no_credential' }; }
                setUnlockUi('Verifying with Windows Hello\u2026', true, 'Verifying\u2026');

                var spkiB64      = '';
                var alg          = -7;
                var authDataB64u = '';
                try {
                    if (typeof cred.response.getPublicKey !== 'function') {
                        return { ok: false, reason: 'public_key_unavailable' };
                    }
                    var spki = cred.response.getPublicKey();
                    if (!spki) { return { ok: false, reason: 'public_key_empty' }; }
                    spkiB64 = arrayBufferToB64(spki);

                    if (typeof cred.response.getPublicKeyAlgorithm === 'function') {
                        alg = cred.response.getPublicKeyAlgorithm();
                    }

                    if (typeof cred.response.getAuthenticatorData !== 'function') {
                        return { ok: false, reason: 'authdata_unavailable' };
                    }
                    var ad = cred.response.getAuthenticatorData();
                    if (!ad) { return { ok: false, reason: 'authdata_empty' }; }
                    authDataB64u = arrayBufferToB64u(ad);
                } catch (e) {
                    return { ok: false, reason: 'authdata_or_pubkey_extract_failed' };
                }
                if (alg !== -7) {
                    return { ok: false, reason: 'unsupported_alg' };
                }
                var body = {
                    credentialId:      arrayBufferToB64u(cred.rawId),
                    publicKeySpki:     spkiB64,
                    alg:               alg,
                    clientDataJSON:    arrayBufferToB64u(cred.response.clientDataJSON),
                    authenticatorData: authDataB64u,
                    challenge:         opts.challenge
                };
                recordDiag({ phase: 'bootstrap_unlock_post_start' });
                logUnlock(state.lastDiagnostics);
                return window.cookbookApi.post(withAttempt('/api/v1/broker/webauthn/bootstrap-register-unlock'), body).then(function (resp) {
                    recordDiag({ phase: 'bootstrap_unlock_post_done' });
                    logUnlock(state.lastDiagnostics);
                    if (resp && resp.ok && resp.body && resp.body.state === 'Unlocked') {
                        return { ok: true, resp: resp };
                    }
                    return { ok: false, reason: 'broker_rejected', resp: resp };
                }, function () {
                    return { ok: false, reason: 'broker_network_error' };
                });
            }, function (err) {
                // navigator.credentials.create rejection. UX-1H3:
                // technical failures do NOT silently fall back;
                // performUnlock() surfaces a diagnostic and an
                // explicit fallback button. UX-1H4: capture the
                // full error shape so the operator can see EXACTLY
                // why the platform authenticator rejected (or never
                // displayed) the Windows Hello dialog.
                var name = (err && err.name) ? err.name : 'unknown';
                recordDiag({
                    phase:                     'bootstrap_create_rejected',
                    errorName:                 name,
                    errorMessage:              (err && err.message) ? err.message : null,
                    errorStackFirstLine:       (err && err.stack)
                                                ? String(err.stack).split('\n')[0]
                                                : null,
                    errorOccurredBeforeCreate: false
                });
                logUnlock(state.lastDiagnostics);
                if (name === 'NotAllowedError' || name === 'AbortError') {
                    return { ok: false, reason: 'user_cancelled', errName: name };
                }
                return { ok: false, reason: 'navigator_create_failed:' + name };
            });
        }, function (err) {
            recordDiag({
                phase:                     'bootstrap_create_sync_throw',
                errorName:                 'challenge_network_error',
                errorMessage:              (err && err.message) ? err.message : null,
                errorOccurredBeforeCreate: true
            });
            logUnlock(state.lastDiagnostics);
            return { ok: false, reason: 'challenge_network_error' };
        });
    }

    // ----------------------------------------------------------------
    // UX-1H5 -- activation-safe two-stage bootstrap preflight
    // ----------------------------------------------------------------
    //
    // The legacy bootstrap path (tryWebAuthnBootstrap above) fetches
    // the bootstrap challenge AFTER the operator's primary-button
    // click, then awaits a promise chain (status fetch + challenge
    // fetch + b64u conversions + DOM updates) before reaching
    // navigator.credentials.create(). On the build that UX-1H4
    // documented, no Windows Hello UI surface ever appears: the
    // browser appears to drop transient user activation across that
    // promise chain on this Chromium / Windows Hello combination.
    //
    // The activation-safe approach (UX-1H5):
    //
    //   1. preflightBootstrap() runs in the background on overlay
    //      mount. It fetches /webauthn/status, decides whether a
    //      bootstrap ceremony is required (registered=false), and if
    //      so fetches /webauthn/bootstrap-register-challenge and
    //      normalizes the response into a full
    //      PublicKeyCredentialCreationOptions object. The prepared
    //      options are stored in state.preparedBootstrap.
    //
    //   2. onPrimaryClick(), if a fresh prepared options object
    //      exists, calls navigator.credentials.create() RIGHT NOW
    //      from inside the click handler -- no fetch in between, no
    //      await, no DOM rebuild. This preserves transient user
    //      activation across the call into the platform
    //      authenticator.
    //
    //   3. If no fresh prepared options exist at click time, the
    //      click handler triggers preflight (idempotent) and asks
    //      the operator to click again. The button text changes to
    //      "Continue with Windows Hello" once preflight succeeds.
    //
    // Challenge freshness budget: PREPARED_TTL_MS. The broker's
    // Issue-WebAuthnChallenge.ps1 issues bootstrap challenges with a
    // 300-second lifetime; we use 240 seconds as the SPA-side budget
    // so the broker would still accept the challenge even with a few
    // seconds of clock skew.
    var PREPARED_TTL_MS = 240 * 1000;

    function newPrepareAttemptId() {
        var r;
        try {
            var arr = new Uint8Array(3);
            (window.crypto || window.msCrypto).getRandomValues(arr);
            r = '';
            for (var i = 0; i < arr.length; i++) {
                var h = arr[i].toString(16);
                if (h.length < 2) { h = '0' + h; }
                r += h;
            }
            r = r.slice(0, 4);
        } catch (e) {
            r = Math.random().toString(36).slice(2, 6);
        }
        return 'UX1H5-prepare-' + Date.now() + '-' + r;
    }

    function isPreparedBootstrapFresh() {
        var p = state.preparedBootstrap;
        if (!p || !p.issuedAtMs) { return false; }
        return (Date.now() - p.issuedAtMs) < PREPARED_TTL_MS;
    }

    function normalizeBootstrapOptions(opts) {
        // Translates the broker's flat JSON body into the WebAuthn
        // PublicKeyCredentialCreationOptions shape with ArrayBuffer
        // fields. Returns { publicKey, challengeByteLength,
        // userIdByteLength, pubKeyCredParamsAlgs } or throws on
        // malformed input. Does NOT include rp.id -- the browser
        // defaults to the effective domain of the page origin
        // (127.0.0.1 or localhost) which is what the broker
        // validates against.
        var challengeBuf = b64uToArrayBuffer(opts.challenge);
        var userIdBuf    = b64uToArrayBuffer(opts.userIdBase64Url || opts.challenge);
        if (challengeBuf.byteLength < 16) {
            throw new Error('challenge byte length ' + challengeBuf.byteLength + ' < 16');
        }
        if (userIdBuf.byteLength < 1 || userIdBuf.byteLength > 64) {
            throw new Error('user.id byte length ' + userIdBuf.byteLength + ' outside 1..64');
        }
        var pkcp = [];
        if (Array.isArray(opts.pubKeyCredParams)) {
            for (var i = 0; i < opts.pubKeyCredParams.length; i++) {
                var p = opts.pubKeyCredParams[i];
                if (p && p.type === 'public-key' && typeof p.alg === 'number') {
                    pkcp.push({ type: 'public-key', alg: p.alg });
                }
            }
        }
        if (pkcp.length === 0) { pkcp = [{ type: 'public-key', alg: -7 }]; }
        var publicKey = {
            challenge: challengeBuf,
            rp:        { name: (opts.rpName || 'PAX Cookbook') },
            user: {
                id:          userIdBuf,
                name:        (opts.userName        || 'cookbook'),
                displayName: (opts.userDisplayName || 'Cookbook')
            },
            pubKeyCredParams:       pkcp,
            authenticatorSelection: {
                authenticatorAttachment: 'platform',
                userVerification:        'required',
                residentKey:             'discouraged'
            },
            timeout:            (opts.timeoutMs || 60000),
            attestation:        'none',
            excludeCredentials: []
        };
        return {
            publicKey:            publicKey,
            challengeByteLength:  challengeBuf.byteLength,
            userIdByteLength:     userIdBuf.byteLength,
            pubKeyCredParamsAlgs: pkcp.map(function (q) { return q.alg; })
        };
    }

    function preflightBootstrap() {
        // Idempotent. If a prepared bootstrap is already fresh OR
        // preflight is in flight, this is a no-op. Does NOT touch
        // the visible primary button on entry -- the success/failure
        // paths repaint it via repaintPreparedReady().
        if (state.preparedBootstrap && isPreparedBootstrapFresh()) { return; }
        if (state.preparedBootstrapStatus === 'pending')           { return; }
        if (!webauthnSupported()) {
            state.preparedBootstrapStatus = 'failed';
            state.preparedBootstrapError  = 'webauthn_unsupported';
            return;
        }
        if (!window.cookbookApi || typeof window.cookbookApi.post !== 'function') {
            state.preparedBootstrapStatus = 'failed';
            state.preparedBootstrapError  = 'cookbookApi_unavailable';
            return;
        }

        state.preparedBootstrapStatus = 'pending';
        state.preparedBootstrapError  = null;
        var prepareId    = newPrepareAttemptId();
        var attemptParam = '?attempt=' + encodeURIComponent(prepareId);

        window.cookbookApi.get('/api/v1/broker/webauthn/status' + attemptParam).then(function (statusResp) {
            var statusBody = (statusResp && statusResp.ok && statusResp.body) ? statusResp.body : null;
            state.statusCache = statusBody;
            if (!statusBody) {
                state.preparedBootstrapStatus = 'failed';
                state.preparedBootstrapError  = 'status_no_body';
                repaintPreparedReady();
                return;
            }
            if (statusBody.registered === true) {
                state.preparedBootstrapStatus = 'idle';
                state.preparedBootstrapError  = null;
                logUnlock({ phase: 'preflight_skipped_already_registered', prepareAttemptId: prepareId });
                return;
            }
            return window.cookbookApi.post(
                '/api/v1/broker/webauthn/bootstrap-register-challenge' + attemptParam,
                {}
            ).then(function (chResp) {
                if (!chResp || !chResp.ok || !chResp.body || !chResp.body.challenge) {
                    state.preparedBootstrapStatus = 'failed';
                    state.preparedBootstrapError  = 'challenge_failed';
                    repaintPreparedReady();
                    return;
                }
                var optsRaw = chResp.body;
                var norm;
                try {
                    norm = normalizeBootstrapOptions(optsRaw);
                } catch (eN) {
                    state.preparedBootstrapStatus = 'failed';
                    state.preparedBootstrapError  = (eN && eN.message) ? eN.message : 'normalize_failed';
                    repaintPreparedReady();
                    return;
                }
                var nowMs = Date.now();
                state.preparedBootstrap = {
                    publicKey:            norm.publicKey,
                    issuedAtMs:           nowMs,
                    expiresAtMs:          nowMs + PREPARED_TTL_MS,
                    prepareAttemptId:     prepareId,
                    optsRaw:              optsRaw,
                    challengeByteLength:  norm.challengeByteLength,
                    userIdByteLength:     norm.userIdByteLength,
                    pubKeyCredParamsAlgs: norm.pubKeyCredParamsAlgs
                };
                state.preparedBootstrapStatus = 'ready';
                state.preparedBootstrapError  = null;
                logUnlock({
                    phase:               'preflight_ready',
                    prepareAttemptId:    prepareId,
                    challengeByteLength: norm.challengeByteLength,
                    userIdByteLength:    norm.userIdByteLength,
                    expiresAtUtc:        new Date(state.preparedBootstrap.expiresAtMs).toISOString()
                });
                repaintPreparedReady();
            }, function (err) {
                state.preparedBootstrapStatus = 'failed';
                state.preparedBootstrapError  = (err && err.message) ? err.message : 'challenge_network_error';
                repaintPreparedReady();
            });
        }, function (err) {
            state.preparedBootstrapStatus = 'failed';
            state.preparedBootstrapError  = (err && err.message) ? err.message : 'status_network_error';
            repaintPreparedReady();
        });
    }

    function repaintPreparedReady() {
        // Called from preflight success/failure paths. Updates the
        // primary button + status line ONLY (does not re-render the
        // entire locked view which would clobber any open Support
        // details disclosure state).
        if (!state.mounted || state.kind !== 'brokerLocked') { return; }
        if (state.unlockInFlight) { return; }
        var primary = document.getElementById(OVERLAY_ID + '-primary');
        var status  = document.getElementById(OVERLAY_ID + '-status');
        // UX-1H7 -- first-run vs steady-state copy. Mirrors the
        // logic in renderLockedView. statusCache may be null if the
        // preflight is still in-flight; treat that as "unknown" and
        // fall back to the steady-state label.
        var sc          = state.statusCache;
        var isFirstRun  = !!(sc && sc.registered === false);
        var readyLabel  = isFirstRun ? 'Create Windows Hello passkey' : 'Continue with Windows Hello';
        var readyHint   = isFirstRun
            ? 'Ready to create your local Windows Hello passkey. Click "Create Windows Hello passkey".'
            : 'Ready to call Windows Hello. Click "Continue with Windows Hello".';
        if (state.preparedBootstrapStatus === 'ready') {
            if (primary) {
                primary.disabled    = false;
                primary.textContent = readyLabel;
            }
            if (status) {
                status.textContent = readyHint;
            }
        } else if (state.preparedBootstrapStatus === 'failed') {
            if (primary) {
                primary.disabled    = false;
                primary.textContent = 'Retry preparation';
            }
            if (status) {
                status.textContent = 'Windows Hello setup could not be prepared (' +
                                     (state.preparedBootstrapError || 'unknown') +
                                     '). Click "Retry preparation".';
            }
        }
    }

    function performUnlockFromPrepared(prepared) {
        // Called SYNCHRONOUSLY from onPrimaryClick when a fresh
        // prepared bootstrap exists. Calls navigator.credentials.create()
        // BEFORE any await so Chromium sees the call inside the same
        // user-activation window as the click. Discards the prepared
        // challenge after first use whether create() resolves, rejects,
        // or hits the watchdog so a follow-up click forces fresh
        // preflight rather than reusing a half-consumed challenge.
        if (state.unlockInFlight) { return; }

        // UX-1H7 -- snapshot retry-context flags BEFORE
        // newUnlockAttemptId() / lastDiagnostics clear. A non-null
        // lastUnlockAttemptId at entry means this click follows at
        // least one prior attempt; record that explicitly along
        // with the no-silent-fallback contract flag.
        var wasRetry = !!state.lastUnlockAttemptId;

        state.unlockInFlight       = true;
        state.lastUnlockAttemptId  = newUnlockAttemptId();
        state.lastDiagnostics      = null;
        state.lastFailureMessage   = null;

        var ua = (window.navigator && window.navigator.userActivation) || null;
        recordDiag({
            webauthnSupported:           true,
            statusFetchOk:               (state.statusCache ? true : null),
            registered:                  (state.statusCache ? !!state.statusCache.registered : null),
            selectedPath:                'browser_webauthn_bootstrap_create',
            endpoint:                    '/api/v1/broker/webauthn/bootstrap-register-unlock',
            browserApi:                  'navigator.credentials.create',
            phase:                       'bootstrap_pre_create_activation_safe',
            bootstrapPreparedFlag:       true,
            preparedIssuedAtUtc:         new Date(prepared.issuedAtMs).toISOString(),
            preparedExpiresAtUtc:        new Date(prepared.expiresAtMs).toISOString(),
            usedPreparedChallenge:       true,
            challengeByteLength:         prepared.challengeByteLength,
            userIdByteLength:            prepared.userIdByteLength,
            pubKeyCredParamsAlgs:        prepared.pubKeyCredParamsAlgs,
            authenticatorSelection:      prepared.publicKey.authenticatorSelection,
            timeoutMs:                   prepared.publicKey.timeout,
            rpId:                        (prepared.optsRaw && prepared.optsRaw.rpId) || null,
            rpName:                      prepared.publicKey.rp.name,
            attestation:                 prepared.publicKey.attestation,
            excludeCredentialsCount:     prepared.publicKey.excludeCredentials.length,
            excludeCredentialsIdLens:    [],
            locationOrigin:              (window.location && window.location.origin)   || null,
            locationProtocol:            (window.location && window.location.protocol) || null,
            locationHostname:            (window.location && window.location.hostname) || null,
            isSecureContext:             !!window.isSecureContext,
            documentVisibilityState:     (document && document.visibilityState) || null,
            documentHasFocus:            (function () { try { return !!document.hasFocus(); } catch (e) { return null; } })(),
            userAgent:                   (window.navigator && window.navigator.userAgent) || null,
            publicKeyCredentialExists:   !!window.PublicKeyCredential,
            hasIsUVPAAFunction:          !!(window.PublicKeyCredential &&
                                            typeof window.PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable === 'function'),
            hasConditionalMediation:     !!(window.PublicKeyCredential &&
                                            typeof window.PublicKeyCredential.isConditionalMediationAvailable === 'function'),
            userActivationIsActive:      ua ? !!ua.isActive : null,
            userActivationHasBeenActive: ua ? !!ua.hasBeenActive : null,
            // UX-1H7 -- retry-context flags. isRetry is true when
            // the click follows at least one prior attempt in this
            // overlay mount; legacyFallbackInvoked is structurally
            // false here because performUnlockFromPrepared is the
            // browser-owned path. The broker-owned terminal Hello
            // is invoked ONLY by the explicit fallback button.
            isRetry:                     wasRetry,
            legacyFallbackInvoked:       false
        });
        logUnlock(state.lastDiagnostics);

        setUnlockUi('Opening Windows Hello\u2026', true, 'Verifying\u2026');

        var startMs = Date.now();
        var createPromise;
        try {
            createPromise = window.navigator.credentials.create({ publicKey: prepared.publicKey });
        } catch (eSync) {
            recordDiag({
                phase:                     'bootstrap_create_sync_throw',
                errorName:                 (eSync && eSync.name)    || 'sync_throw',
                errorMessage:              (eSync && eSync.message) || 'navigator.credentials.create threw synchronously',
                errorStackFirstLine:       (eSync && eSync.stack)   ? String(eSync.stack).split('\n')[0] : null,
                errorOccurredBeforeCreate: false,
                createElapsedMs:           (Date.now() - startMs)
            });
            logUnlock(state.lastDiagnostics);
            state.preparedBootstrap        = null;
            state.preparedBootstrapStatus  = 'idle';
            state.unlockInFlight           = false;
            state.lastFailureMessage       = 'Windows Hello did not open (sync throw: ' +
                                             ((eSync && eSync.name) || 'unknown') +
                                             '). Click "Copy diagnostics" below, then click "Retry preparation".';
            renderLockedView();
            setUnlockUi(state.lastFailureMessage, false, 'Retry preparation');
            return;
        }

        // Watchdog: 30 s. UX-1H10 -- this watchdog is now strictly a
        // non-error progress notice. A pending create() promise is
        // NOT a failure: the Windows Hello ceremony may legitimately
        // take many seconds (operator walked away, biometric prompt
        // waiting on touch, etc.) and ultimately succeed. Brian's
        // UX-1H9 manual test produced a successful unlock 5-10s
        // after a 10s watchdog had already classified the attempt
        // as failed.
        //
        // The watchdog now:
        //   - records phase 'bootstrap_create_pending_slow' (NOT in
        //     the failPhases array so Copy Diagnostics main button
        //     does not arm),
        //   - records resultDetail 'create_still_pending_after_watchdog',
        //   - records createWatchdogElapsedMs for support,
        //   - updates ONLY the status text to a friendly hint,
        //   - does NOT set state.lastFailureMessage,
        //   - does NOT re-enable the primary button (create promise
        //     is still in flight, button remains disabled with the
        //     'Verifying...' label),
        //   - does NOT clear preparedBootstrap,
        //   - does NOT flip unlockInFlight,
        //   - does NOT auto-fall-back to broker-owned Hello.
        //
        // Late resolve: the resolved_after_watchdog branch overrides
        // the status text via setUnlockUi('Verifying with Windows
        // Hello...') and proceeds to POST bootstrap-register-unlock.
        // Late reject: the rejected_after_watchdog branch arms
        // Copy Diagnostics via state.lastFailureMessage and the
        // existing failPhases-driven render path.
        var watchdogFired = false;
        var watchdogTimer = setTimeout(function () {
            if (watchdogFired) { return; }
            watchdogFired = true;
            recordDiag({
                phase:                   'bootstrap_create_pending_slow',
                createWatchdogElapsedMs: (Date.now() - startMs),
                resultDetail:            'create_still_pending_after_watchdog'
            });
            logUnlock(state.lastDiagnostics);
            setUnlockUi(
                'Windows Hello is still waiting for a response. ' +
                'Finish the Windows Hello prompt if it is open.',
                true,
                'Verifying\u2026'
            );
        }, 30 * 1000);

        return createPromise.then(function (cred) {
            if (watchdogFired) {
                recordDiag({
                    phase:           'bootstrap_create_resolved_after_watchdog',
                    createElapsedMs: (Date.now() - startMs)
                });
            } else {
                clearTimeout(watchdogTimer);
                recordDiag({
                    phase:           'bootstrap_create_resolved',
                    createElapsedMs: (Date.now() - startMs)
                });
            }
            logUnlock(state.lastDiagnostics);
            if (!cred || !cred.response) { return finishBootstrapFailure('no_credential', null); }
            setUnlockUi('Verifying with Windows Hello\u2026', true, 'Verifying\u2026');

            var spkiB64      = '';
            var alg          = -7;
            var authDataB64u = '';
            try {
                if (typeof cred.response.getPublicKey !== 'function') {
                    return finishBootstrapFailure('public_key_unavailable', null);
                }
                var spki = cred.response.getPublicKey();
                if (!spki) { return finishBootstrapFailure('public_key_empty', null); }
                spkiB64 = arrayBufferToB64(spki);
                if (typeof cred.response.getPublicKeyAlgorithm === 'function') {
                    alg = cred.response.getPublicKeyAlgorithm();
                }
                if (typeof cred.response.getAuthenticatorData !== 'function') {
                    return finishBootstrapFailure('authdata_unavailable', null);
                }
                var ad = cred.response.getAuthenticatorData();
                if (!ad) { return finishBootstrapFailure('authdata_empty', null); }
                authDataB64u = arrayBufferToB64u(ad);
            } catch (e) {
                return finishBootstrapFailure('authdata_or_pubkey_extract_failed', null);
            }
            if (alg !== -7) { return finishBootstrapFailure('unsupported_alg', null); }

            var body = {
                credentialId:      arrayBufferToB64u(cred.rawId),
                publicKeySpki:     spkiB64,
                alg:               alg,
                clientDataJSON:    arrayBufferToB64u(cred.response.clientDataJSON),
                authenticatorData: authDataB64u,
                challenge:         prepared.optsRaw.challenge
            };
            recordDiag({ phase: 'bootstrap_unlock_post_start' });
            logUnlock(state.lastDiagnostics);
            // Discard the prepared challenge now -- we used it once.
            state.preparedBootstrap       = null;
            state.preparedBootstrapStatus = 'idle';

            return window.cookbookApi.post(withAttempt('/api/v1/broker/webauthn/bootstrap-register-unlock'), body).then(function (resp) {
                recordDiag({ phase: 'bootstrap_unlock_post_done' });
                logUnlock(state.lastDiagnostics);
                if (resp && resp.ok && resp.body && resp.body.state === 'Unlocked') {
                    recordDiag({ phase: 'browser_ceremony_success', resultOk: true });
                    logUnlock(state.lastDiagnostics);
                    setUnlockUi('Unlocking Cookbook\u2026', true, 'Unlocking\u2026');
                    state.unlockInFlight = false;
                    finishUnlockSuccess();
                    return;
                }
                return finishBootstrapFailure('broker_rejected', resp);
            }, function () {
                return finishBootstrapFailure('broker_network_error', null);
            });
        }, function (err) {
            if (watchdogFired) {
                recordDiag({
                    phase:                     'bootstrap_create_rejected_after_watchdog',
                    errorName:                 (err && err.name) ? err.name : 'unknown',
                    errorMessage:              (err && err.message) ? err.message : null,
                    errorStackFirstLine:       (err && err.stack) ? String(err.stack).split('\n')[0] : null,
                    errorOccurredBeforeCreate: false,
                    createElapsedMs:           (Date.now() - startMs)
                });
            } else {
                clearTimeout(watchdogTimer);
                recordDiag({
                    phase:                     'bootstrap_create_rejected',
                    errorName:                 (err && err.name) ? err.name : 'unknown',
                    errorMessage:              (err && err.message) ? err.message : null,
                    errorStackFirstLine:       (err && err.stack) ? String(err.stack).split('\n')[0] : null,
                    errorOccurredBeforeCreate: false,
                    createElapsedMs:           (Date.now() - startMs)
                });
            }
            logUnlock(state.lastDiagnostics);
            var name = (err && err.name) ? err.name : 'unknown';
            state.preparedBootstrap       = null;
            state.preparedBootstrapStatus = 'idle';
            state.unlockInFlight          = false;
            if (name === 'NotAllowedError' || name === 'AbortError') {
                state.lastFailureMessage = 'Windows Hello was cancelled or dismissed. Click "Retry preparation".';
            } else {
                state.lastFailureMessage = 'Windows Hello create() rejected (' + name + '). ' +
                                           'Click "Copy diagnostics" below, then click "Retry preparation".';
            }
            renderLockedView();
            setUnlockUi(state.lastFailureMessage, false, 'Retry preparation');
        });
    }

    function finishBootstrapFailure(reason, resp) {
        state.unlockInFlight          = false;
        state.preparedBootstrap       = null;
        state.preparedBootstrapStatus = 'idle';

        // Pull every piece of the broker's structured rejection
        // response onto the diagnostic surface. UX-1H7 contract:
        // the broker emits { ok:false, code, message, stage,
        // attemptId } for every failed gate and a paired
        // 'BROWSER-OWNED WEBAUTHN bootstrap-register-unlock
        // REJECTED' audit line. UX-1H8 contract: the broker
        // additionally emits HTTP 500 with body
        // { ok:false, code:'internal_exception', stage:'unknown',
        //   message, attemptId, exceptionType, exceptionMessage,
        //   checkpoint } for ANY unhandled exception, so the
        // diagnostic block never carries a blank stage/code/
        // message again. If the response body is missing or
        // non-JSON on a 500 (the dispatcher-level fallback that
        // would re-introduce an opaque 500), we synthesise
        // explicit 'http_500_no_json' marker fields so the
        // diagnostic block still surfaces actionable evidence.
        var respBody     = (resp && resp.body) ? resp.body : null;
        var respStatus   = (resp && typeof resp.status === 'number') ? resp.status : null;
        var respOk       = (resp && typeof resp.ok === 'boolean') ? resp.ok : null;
        var respState    = (respBody && typeof respBody.state === 'string') ? respBody.state : null;
        var brokerStage  = (respBody && typeof respBody.stage   === 'string') ? respBody.stage   : null;
        var brokerCode   = (respBody && typeof respBody.code    === 'string') ? respBody.code    : null;
        var brokerMsg    = (respBody && typeof respBody.message === 'string') ? respBody.message : null;
        var brokerAttId  = (respBody && typeof respBody.attemptId === 'string') ? respBody.attemptId : null;
        var brokerExcType  = (respBody && typeof respBody.exceptionType    === 'string') ? respBody.exceptionType    : null;
        var brokerExcMsg   = (respBody && typeof respBody.exceptionMessage === 'string') ? respBody.exceptionMessage : null;
        var brokerExcCkpt  = (respBody && typeof respBody.checkpoint       === 'string') ? respBody.checkpoint       : null;
        var legacyVerd   = (respBody && respBody.verificationResult) ? respBody.verificationResult : null;

        // Dispatcher-level naked 500 (Write-BootstrapException
        // itself failed, or some pre-handler hook threw before
        // the top-level catch could fire). The client refuses to
        // leave the diagnostic block blank: synthesize explicit
        // 'http_500_no_json' marker values so operator triage can
        // still see WHAT shape the failure took.
        if (respStatus === 500 && respBody === null) {
            if (!brokerStage)   { brokerStage   = 'unknown'; }
            if (!brokerCode)    { brokerCode    = 'http_500_no_json'; }
            if (!brokerMsg)     { brokerMsg     = 'Broker returned HTTP 500 without a structured JSON body.'; }
            if (!brokerExcType) { brokerExcType = 'unknown'; }
            if (!brokerExcMsg)  { brokerExcMsg  = '<broker returned no JSON body>'; }
            if (!brokerExcCkpt) { brokerExcCkpt = 'unknown'; }
        }

        var isInternalException = (brokerCode === 'internal_exception');

        var detailParts = [reason];
        if (brokerStage) { detailParts.push('stage=' + brokerStage); }
        if (brokerCode)  { detailParts.push('code='  + brokerCode);  }
        if (brokerExcType && isInternalException) { detailParts.push('excType=' + brokerExcType); }
        if (brokerExcCkpt && isInternalException) { detailParts.push('checkpoint=' + brokerExcCkpt); }
        if (legacyVerd)  { detailParts.push('verdict=' + legacyVerd); }

        recordDiag({
            phase:                       'browser_ceremony_failed',
            resultOk:                    false,
            resultDetail:                detailParts.join(' '),
            bootstrapUnlockHttpStatus:   respStatus,
            bootstrapUnlockResponseOk:   respOk,
            bootstrapUnlockResponseState:respState,
            brokerRejectStatus:          respStatus,
            brokerRejectStage:           brokerStage,
            brokerRejectCode:            brokerCode,
            brokerRejectMessage:         brokerMsg,
            brokerRejectAttemptId:       brokerAttId,
            brokerExceptionType:         brokerExcType,
            brokerExceptionMessage:      brokerExcMsg,
            brokerExceptionCheckpoint:   brokerExcCkpt
        });
        logUnlock(state.lastDiagnostics);

        // Operator-visible failure message. The spec-mandated copy
        // does NOT reveal stage/code in the headline (those are in
        // the copied diagnostics block) so the overlay stays
        // legible; the headline directs to Copy diagnostics and
        // Retry preparation, which are the only two next steps
        // that ever matter here. UX-1H8 -- on a 500 internal
        // exception the headline shifts to make it explicit that
        // this is a broker-side bug, not a Hello/passkey problem.
        if (isInternalException) {
            state.lastFailureMessage =
                'Cookbook hit an internal error while verifying the Windows Hello passkey. ' +
                'Click "Copy diagnostics" below, then click "Retry preparation".';
        } else {
            state.lastFailureMessage =
                'Windows Hello completed, but Cookbook could not verify the passkey. ' +
                'Click "Copy diagnostics" below, then click "Retry preparation".';
        }
        renderLockedView();
        setUnlockUi(state.lastFailureMessage, false, 'Retry preparation');
    }

    // Legacy broker-owned unlock -- now reserved as an EXPLICIT
    // operator-initiated fallback (UX-1H3). The primary unlock
    // button NEVER routes here automatically: a click on the
    // "Use legacy Windows Hello fallback" button inside the
    // Support details disclosure is the only way to invoke this
    // path. Returns { ok, resp } / { ok, resp:null }.
    function performBrokerOwnedUnlock() {
        return window.cookbookApi.post(withAttempt('/api/v1/broker/unlock'), {}).then(function (resp) {
            if (resp && resp.ok && resp.body && resp.body.state === 'Unlocked') {
                return { ok: true, resp: resp };
            }
            return { ok: false, resp: resp };
        }, function () {
            return { ok: false, resp: null };
        });
    }

    function brokerOwnedFallback() {
        // UX-1H3 -- this function is ONLY reachable via the explicit
        // "Use legacy Windows Hello fallback" button rendered inside
        // the Support details disclosure. It MUST NEVER be called
        // from a silent technical-failure path in performUnlock:
        // doing so re-introduces the wrong-monitor / terminal-Hello
        // bug that UX-1H3 exists to eliminate. The Write-Host log
        // line on the broker side ("LEGACY BROKER-OWNED WINDOWS
        // HELLO PATH INVOKED") is the canonical audit signal that
        // this code ran.
        recordDiag({
            selectedPath:                'legacy_broker_owned_explicit',
            endpoint:                    '/api/v1/broker/unlock',
            browserApi:                  'none',
            phase:                       'broker_owned_invoke',
            willInvokeBrokerOwnedUnlock: true
        });
        logUnlock(state.lastDiagnostics);
        setUnlockUi('Using Windows Hello fallback\u2026', true, 'Verifying\u2026');
        state.unlockInFlight = true;
        return performBrokerOwnedUnlock().then(function (bResult) {
            state.unlockInFlight = false;
            if (bResult.ok) {
                recordDiag({ phase: 'broker_owned_success', resultOk: true });
                logUnlock(state.lastDiagnostics);
                setUnlockUi('Unlocking Cookbook\u2026', true, 'Unlocking\u2026');
                finishUnlockSuccess();
            } else {
                var verdict = (bResult.resp && bResult.resp.body && bResult.resp.body.verificationResult) || 'Unknown';
                var brokerMsg = (bResult.resp && bResult.resp.body && bResult.resp.body.message) || 'Verification did not succeed.';
                recordDiag({
                    phase:        'broker_owned_failed',
                    resultOk:     false,
                    resultDetail: ('verdict=' + verdict)
                });
                state.lastFailureMessage = brokerMsg + ' (verdict: ' + verdict + ')';
                logUnlock(state.lastDiagnostics);
                renderLockedView();
                setUnlockUi(state.lastFailureMessage, false, 'Unlock with Windows Hello');
            }
        });
    }

    function performUnlock() {
        // UX-1H3 orchestration -- BROWSER-OWNED ONLY ON THE PRIMARY
        // BUTTON. There is NO silent path from the primary button
        // into the broker-owned terminal Hello. Every failure mode
        // that previously fell through to broker-owned Hello now
        // surfaces a structured diagnostic in the Support details
        // disclosure AND re-enables the primary button so the
        // operator can retry browser-owned Hello. The legacy
        // broker-owned path is reachable only via the explicit
        // "Use legacy Windows Hello fallback" button rendered by
        // renderLockedView() when state.lastDiagnostics is present.
        //
        //   GET /webauthn/status
        //     |
        //     +-- status fetch fails (network or non-2xx)
        //     |       -> surface 'Status check failed' on the
        //     |          overlay, re-enable primary, NO FALLBACK.
        //     |
        //     +-- webauthn NOT supported in this browser
        //     |       -> surface 'Browser does not support
        //     |          WebAuthn', re-enable primary, NO FALLBACK.
        //     |          Support details exposes the explicit
        //     |          legacy fallback button.
        //     |
        //     +-- registered=true  AND webauthn supported
        //     |       -> tryWebAuthnUnlock         (steady-state)
        //     |
        //     +-- registered=false AND webauthn supported
        //             -> tryWebAuthnBootstrap      (first run)
        //
        // On user cancel (NotAllowedError / AbortError) from either
        // browser-owned ceremony we do NOT fall back. On technical
        // failure (RP-ID rejection, public-key extraction failed,
        // broker rejected, network glitch, etc.) we do NOT fall
        // back -- the operator sees the failure reason in the
        // Support details and can either retry browser-owned Hello
        // OR explicitly click the legacy fallback button.
        if (state.unlockInFlight) { return; }
        if (!window.cookbookApi || typeof window.cookbookApi.post !== 'function') { return; }

        // Fresh attempt id + cleared diagnostic record for this click.
        state.lastUnlockAttemptId = newUnlockAttemptId();
        state.lastDiagnostics     = null;
        state.lastFailureMessage  = null;
        recordDiag({
            webauthnSupported: webauthnSupported(),
            phase:             'start'
        });
        logUnlock(state.lastDiagnostics);

        state.unlockInFlight = true;
        setUnlockUi(null, true, 'Verifying\u2026');

        window.cookbookApi.get(withAttempt('/api/v1/broker/webauthn/status')).then(function (statusResp) {
            var statusBody = (statusResp && statusResp.ok && statusResp.body) ? statusResp.body : null;
            recordDiag({
                statusFetchOk:     !!(statusResp && statusResp.ok),
                statusFetchStatus: (statusResp ? statusResp.status : null),
                registered:        statusBody ? !!statusBody.registered : null,
                phase:             'status_received'
            });
            logUnlock(state.lastDiagnostics);

            // Failure mode 1: status fetch returned a non-2xx or
            // empty body. UX-1H3: do NOT auto-fall-back. Surface
            // the failure + diagnostic and re-enable the primary
            // button. The operator can retry OR explicitly click
            // the legacy fallback button.
            if (!statusBody) {
                recordDiag({
                    phase:        'status_failed',
                    selectedPath: 'none_status_failed',
                    errorMessage: 'webauthn/status returned no body'
                });
                logUnlock(state.lastDiagnostics);
                state.unlockInFlight     = false;
                state.lastFailureMessage = 'Could not reach the broker\u2019s WebAuthn status endpoint. Click "Copy diagnostics" below, or open Support details for the Windows Hello fallback.';
                renderLockedView();
                setUnlockUi(state.lastFailureMessage, false, 'Unlock with Windows Hello');
                return;
            }

            // Failure mode 2: browser does not expose a usable
            // WebAuthn surface. UX-1H3: do NOT auto-fall-back.
            // Surface the explicit fallback option through the
            // Support details disclosure.
            if (!webauthnSupported()) {
                recordDiag({
                    phase:        'webauthn_unsupported',
                    selectedPath: 'none_webauthn_unsupported',
                    errorMessage: 'navigator.credentials.create/get not available'
                });
                logUnlock(state.lastDiagnostics);
                state.unlockInFlight     = false;
                state.lastFailureMessage = 'This browser does not support WebAuthn / Windows Hello. Open Support details to use the Windows Hello fallback.';
                renderLockedView();
                setUnlockUi(state.lastFailureMessage, false, 'Unlock with Windows Hello');
                return;
            }

            // Steady-state vs bootstrap selection.
            var selectedPath = statusBody.registered ? 'browser_webauthn_get' : 'browser_webauthn_bootstrap_create';
            var endpoint     = statusBody.registered
                ? '/api/v1/broker/webauthn/unlock'
                : '/api/v1/broker/webauthn/bootstrap-register-unlock';
            var browserApi   = statusBody.registered ? 'navigator.credentials.get' : 'navigator.credentials.create';
            recordDiag({
                selectedPath: selectedPath,
                endpoint:     endpoint,
                browserApi:   browserApi,
                phase:        'browser_ceremony_starting'
            });
            logUnlock(state.lastDiagnostics);

            var primary = statusBody.registered
                ? tryWebAuthnUnlock(statusBody)
                : tryWebAuthnBootstrap();

            primary.then(function (result) {
                if (result.ok) {
                    recordDiag({ phase: 'browser_ceremony_success', resultOk: true });
                    logUnlock(state.lastDiagnostics);
                    setUnlockUi('Unlocking Cookbook\u2026', true, 'Unlocking\u2026');
                    state.unlockInFlight = false;
                    finishUnlockSuccess();
                    return;
                }
                if (result.reason === 'user_cancelled') {
                    recordDiag({
                        phase:        'user_cancelled',
                        resultOk:     false,
                        resultDetail: 'user_cancelled',
                        errorName:    (result.errName || null),
                        errorMessage: 'Windows Hello dialog cancelled or dismissed by operator'
                    });
                    logUnlock(state.lastDiagnostics);
                    state.unlockInFlight = false;
                    setUnlockUi(
                        'Windows Hello was cancelled. Try again when you\u2019re ready.',
                        false,
                        'Unlock with Windows Hello'
                    );
                    return;
                }

                // Technical failure (RP-ID rejection, public-key
                // extraction failed, broker rejected, network
                // glitch, unsupported alg, etc.). UX-1H3: do NOT
                // auto-fall-back. Surface the failure reason on
                // the overlay + diagnostic in Support details +
                // re-enable the primary button.
                var brokerRespVerdict = null;
                if (result.resp && result.resp.body && result.resp.body.verificationResult) {
                    brokerRespVerdict = result.resp.body.verificationResult;
                }
                recordDiag({
                    phase:        'browser_ceremony_failed',
                    resultOk:     false,
                    resultDetail: (result.reason || 'unknown'),
                    errorMessage: ('Browser-owned WebAuthn could not complete: ' + (result.reason || 'unknown') + (brokerRespVerdict ? (' (broker verdict: ' + brokerRespVerdict + ')') : ''))
                });
                logUnlock(state.lastDiagnostics);
                state.unlockInFlight     = false;
                state.lastFailureMessage = 'Browser-owned Windows Hello could not complete. Click "Copy diagnostics" below, or open Support details for the Windows Hello fallback.';
                renderLockedView();
                setUnlockUi(state.lastFailureMessage, false, 'Unlock with Windows Hello');
            });
        }, function (err) {
            // Failure mode 3: webauthn/status network promise
            // rejected outright. UX-1H3: do NOT auto-fall-back.
            recordDiag({
                phase:        'status_network_error',
                selectedPath: 'none_status_network_error',
                errorMessage: ((err && err.message) ? err.message : 'webauthn/status fetch rejected')
            });
            logUnlock(state.lastDiagnostics);
            state.unlockInFlight     = false;
            state.lastFailureMessage = 'Could not reach the broker. Click "Copy diagnostics" below, or open Support details for the Windows Hello fallback.';
            renderLockedView();
            setUnlockUi(state.lastFailureMessage, false, 'Unlock with Windows Hello');
        });
    }

    // ----------------------------------------------------------------
    // Event handlers
    // ----------------------------------------------------------------

    function onBrokerLocked(ev) {
        var d = (ev && ev.detail) ? ev.detail : {};
        // If a reAuthRequired overlay is already up, a 423 supersedes
        // it (the broker is now locked harder than just per-op gate).
        state.kind            = 'brokerLocked';
        state.message         = d.message         || null;
        state.attemptedMethod = d.attemptedMethod || null;
        state.attemptedPath   = d.attemptedPath   || null;
        renderLockedView();
        mount();
    }

    function onReAuthRequired(ev) {
        var d = (ev && ev.detail) ? ev.detail : {};
        // X16C -- the manual-cook step-up is owned inline by the Bake
        // button flow (manual-cook-reauth.js drives navigator.credentials
        // .get and retries the cook once). The generic overlay must NOT
        // intercept that op class, or it would race the Bake flow and
        // show a second, conflicting prompt. Every other op class still
        // surfaces the per-op verdict overlay here.
        if (d.opClass === 'manualCook') { return; }
        // If a brokerLocked overlay is already up, don't downgrade --
        // the chef must clear the lock first; the per-op verdict is
        // surfaced after the unlock succeeds and the chef retries.
        if (state.kind === 'brokerLocked') { return; }
        state.kind               = 'reAuthRequired';
        state.message            = d.message            || null;
        state.opClass            = d.opClass            || null;
        state.verificationResult = d.verificationResult || null;
        renderReAuthView();
        mount();
    }

    // ----------------------------------------------------------------
    // Boot-time lock-state probe
    // ----------------------------------------------------------------

    // One-shot probe of /api/v1/broker/lock-state at boot so the SPA
    // shows the overlay BEFORE the first page-driven fetch hits 423.
    // /broker/lock-state is allowlisted when locked, so this poll is
    // safe even when the broker just rebooted.
    function probeLockStateOnce() {
        if (!window.cookbookApi || typeof window.cookbookApi.get !== 'function') { return; }
        window.cookbookApi.get('/api/v1/broker/lock-state').then(function (resp) {
            if (resp && resp.ok && resp.body && resp.body.state === 'Locked') {
                onBrokerLocked({ detail: { code: 'brokerLocked', message: null } });
                return;
            }
            if (resp && resp.ok && resp.body && resp.body.state === 'Unlocked') {
                try {
                    window.dispatchEvent(new CustomEvent('cookbook:brokerUnlocked', {
                        detail: { timestampUtc: new Date().toISOString() }
                    }));
                } catch (e) {}
            }
            // If unknown / non-2xx, do nothing -- a real 423 / 401
            // will mount the overlay on demand.
        });
    }

    // ----------------------------------------------------------------
    // UX-1H5 -- Copy diagnostics + WebAuthn probe ladder
    // ----------------------------------------------------------------
    //
    // formatDiagnosticBlock() serializes the current diagnostic
    // surface (state.lastDiagnostics + prepared bootstrap metadata
    // + last probe result) into a plain-text block the operator can
    // copy to the clipboard and paste back as evidence.
    //
    // runDiagProbe(name) is click-triggered. It fetches a fresh
    // bootstrap challenge from the broker, builds probe-specific
    // PublicKeyCredentialCreationOptions, calls
    // navigator.credentials.create(), and records the outcome into
    // state.lastProbeResult. It does NOT post the result to
    // /bootstrap-register-unlock -- any returned credential is
    // intentionally discarded (which may leave orphaned authenticator
    // state on this device; the Support details panel warns about
    // this). No raw clientDataJSON / attestationObject / SPKI bytes
    // are logged or persisted -- only byte lengths and stable metadata.

    function formatDiagnosticBlock() {
        var d = state.lastDiagnostics || {};
        var p = state.preparedBootstrap;
        var lines = [
            '=== PAX Cookbook unlock diagnostics ===',
            'expected script version: lock-overlay.js?v=uxr19',
            'collected at: ' + new Date().toISOString(),
            '',
            '--- last unlock attempt ---',
            'attemptId: '                  + (d.attemptId  || ''),
            'startedUtc: '                 + (d.startedUtc || ''),
            'phase: '                      + (d.phase      || ''),
            'selectedPath: '               + (d.selectedPath || ''),
            'browserApi: '                 + (d.browserApi || ''),
            'endpoint: '                   + (d.endpoint   || ''),
            'webauthnSupported: '          + String(d.webauthnSupported),
            'statusFetchOk: '              + String(d.statusFetchOk),
            'statusFetchStatus: '          + String(d.statusFetchStatus),
            'registered: '                 + String(d.registered),
            'fallbackPolicy: '             + (d.fallbackPolicy || ''),
            'willInvokeBrokerOwnedUnlock: '+ String(d.willInvokeBrokerOwnedUnlock),
            'resultOk: '                   + String(d.resultOk),
            'resultDetail: '               + (d.resultDetail || ''),
            'errorName: '                  + (d.errorName    || ''),
            'errorMessage: '               + (d.errorMessage || ''),
            'errorStackFirstLine: '        + (d.errorStackFirstLine || ''),
            'errorOccurredBeforeCreate: '  + String(d.errorOccurredBeforeCreate),
            'createElapsedMs: '            + String(d.createElapsedMs),
            'createWatchdogElapsedMs: '    + String(d.createWatchdogElapsedMs),
            '',
            '--- broker rejection (bootstrap-register-unlock) ---',
            'bootstrapUnlockHttpStatus: '  + String(d.bootstrapUnlockHttpStatus),
            'bootstrapUnlockResponseOk: '  + String(d.bootstrapUnlockResponseOk),
            'bootstrapUnlockResponseState: '+ (d.bootstrapUnlockResponseState || ''),
            'brokerRejectStatus: '         + String(d.brokerRejectStatus),
            'brokerRejectStage: '          + (d.brokerRejectStage   || ''),
            'brokerRejectCode: '           + (d.brokerRejectCode    || ''),
            'brokerRejectMessage: '        + (d.brokerRejectMessage || ''),
            'brokerRejectAttemptId: '      + (d.brokerRejectAttemptId || ''),
            'brokerExceptionType: '        + (d.brokerExceptionType       || ''),
            'brokerExceptionMessage: '     + (d.brokerExceptionMessage    || ''),
            'brokerExceptionCheckpoint: '  + (d.brokerExceptionCheckpoint || ''),
            '',
            '--- browser context ---',
            'locationOrigin: '             + (d.locationOrigin   || ''),
            'locationProtocol: '           + (d.locationProtocol || ''),
            'locationHostname: '           + (d.locationHostname || ''),
            'isSecureContext: '            + String(d.isSecureContext),
            'documentVisibilityState: '    + (d.documentVisibilityState || ''),
            'documentHasFocus: '           + String(d.documentHasFocus),
            'userAgent: '                  + (d.userAgent || ''),
            'publicKeyCredentialExists: '  + String(d.publicKeyCredentialExists),
            'hasIsUVPAAFunction: '         + String(d.hasIsUVPAAFunction),
            'isUVPAAResult: '              + String(d.isUVPAAResult),
            'hasConditionalMediation: '    + String(d.hasConditionalMediation),
            'userActivationIsActive: '     + String(d.userActivationIsActive),
            'userActivationHasBeenActive: '+ String(d.userActivationHasBeenActive),
            '',
            '--- bootstrap options used in last attempt ---',
            'bootstrapPreparedFlag: '      + String(d.bootstrapPreparedFlag),
            'preparedIssuedAtUtc: '        + (d.preparedIssuedAtUtc  || ''),
            'preparedExpiresAtUtc: '       + (d.preparedExpiresAtUtc || ''),
            'usedPreparedChallenge: '      + String(d.usedPreparedChallenge),
            'challengeByteLength: '        + String(d.challengeByteLength),
            'userIdByteLength: '           + String(d.userIdByteLength),
            'pubKeyCredParamsAlgs: '       + (d.pubKeyCredParamsAlgs   ? JSON.stringify(d.pubKeyCredParamsAlgs)   : ''),
            'authenticatorSelection: '     + (d.authenticatorSelection ? JSON.stringify(d.authenticatorSelection) : ''),
            'timeoutMs: '                  + String(d.timeoutMs),
            'rpId: '                       + (d.rpId   || '(omitted)'),
            'rpName: '                     + (d.rpName || ''),
            'attestation: '                + (d.attestation || ''),
            'excludeCredentialsCount: '    + String(d.excludeCredentialsCount),
            ''
        ];

        lines.push('--- currently prepared bootstrap (in memory) ---');
        lines.push('preparedBootstrapStatus: ' + state.preparedBootstrapStatus);
        if (p) {
            lines.push('prepareAttemptId: '     + (p.prepareAttemptId || ''));
            lines.push('preparedAtUtc: '        + new Date(p.issuedAtMs).toISOString());
            lines.push('preparedExpiresAtUtc: ' + new Date(p.expiresAtMs).toISOString());
            lines.push('challengeByteLength: '  + String(p.challengeByteLength));
            lines.push('userIdByteLength: '     + String(p.userIdByteLength));
            lines.push('pubKeyCredParamsAlgs: ' + JSON.stringify(p.pubKeyCredParamsAlgs));
        } else {
            lines.push('preparedBootstrapError: ' + (state.preparedBootstrapError || ''));
        }
        lines.push('');

        if (state.lastProbeResult) {
            var r = state.lastProbeResult;
            lines.push('--- last probe ---');
            lines.push('probeName: '                  + (r.probeName  || ''));
            lines.push('startedUtc: '                 + (r.startedUtc || ''));
            lines.push('outcome: '                    + (r.outcome    || ''));
            lines.push('elapsedMs: '                  + String(r.elapsedMs));
            lines.push('errorName: '                  + (r.errorName    || ''));
            lines.push('errorMessage: '               + (r.errorMessage || ''));
            lines.push('errorStackFirstLine: '        + (r.errorStackFirstLine || ''));
            lines.push('uiOpened: '                   + String(r.uiOpened));
            lines.push('credentialReturned: '         + String(r.credentialReturned));
            lines.push('credentialType: '             + (r.credentialType || ''));
            lines.push('attestationObjectBytes: '     + String(r.attestationObjectBytes));
            lines.push('clientDataJsonBytes: '        + String(r.clientDataJsonBytes));
            lines.push('userActivationIsActive: '     + String(r.userActivationIsActive));
            lines.push('userActivationHasBeenActive: '+ String(r.userActivationHasBeenActive));
            lines.push('optionsSummary: '             + (r.optionsSummary || ''));
            lines.push('');
        }

        return lines.join('\n');
    }

    function copyDiagnosticsToClipboard(triggerBtn) {
        var block = formatDiagnosticBlock();
        var setBtnText = function (msg) {
            if (triggerBtn) { triggerBtn.textContent = msg; }
            setTimeout(function () {
                if (triggerBtn) { triggerBtn.textContent = 'Copy diagnostics'; }
            }, 2500);
        };
        try {
            if (window.navigator.clipboard && typeof window.navigator.clipboard.writeText === 'function') {
                window.navigator.clipboard.writeText(block).then(function () {
                    setBtnText('Copied!');
                }, function () {
                    fallbackCopy(block, setBtnText);
                });
                return;
            }
        } catch (e) {}
        fallbackCopy(block, setBtnText);
    }

    function fallbackCopy(text, setBtnText) {
        try {
            var ta = document.createElement('textarea');
            ta.value = text;
            ta.setAttribute('readonly', '');
            ta.style.position = 'absolute';
            ta.style.left     = '-9999px';
            document.body.appendChild(ta);
            ta.select();
            document.execCommand('copy');
            document.body.removeChild(ta);
            setBtnText('Copied (fallback)');
        } catch (e) {
            setBtnText('Copy failed - see console');
            try { /* eslint-disable-next-line no-console */ console.log('[PAX Cookbook Diagnostics]\n' + text); } catch (e2) {}
        }
    }

    function probeOptionsFor(name, rawOpts) {
        // Returns { publicKey, optionsSummary } for the named probe.
        // Each probe normalizes the challenge into an ArrayBuffer
        // and sets user.id similarly. Probes 2-4 progressively
        // strip authenticator constraints; probe 5 randomizes the
        // user.id locally to bypass any TPM-local credential
        // collision with prior bootstrap attempts.
        var challengeBuf = b64uToArrayBuffer(rawOpts.challenge);
        var userIdSource = rawOpts.userIdBase64Url || rawOpts.challenge;
        var userIdBuf;
        var userIdNote = '';
        if (name === 'alternative_user_id') {
            var randBytes = new Uint8Array(16);
            (window.crypto || window.msCrypto).getRandomValues(randBytes);
            userIdBuf = randBytes.buffer;
            userIdNote = ' (randomized locally, 16 bytes)';
        } else {
            userIdBuf = b64uToArrayBuffer(userIdSource);
        }
        var publicKey = {
            challenge: challengeBuf,
            rp:        { name: (rawOpts.rpName || 'PAX Cookbook') },
            user: {
                id:          userIdBuf,
                name:        (rawOpts.userName        || 'cookbook'),
                displayName: (rawOpts.userDisplayName || 'Cookbook')
            },
            pubKeyCredParams:   [{ type: 'public-key', alg: -7 }],
            timeout:            (rawOpts.timeoutMs || 60000),
            attestation:        'none',
            excludeCredentials: []
        };
        var summary;
        switch (name) {
            case 'current_options':
                publicKey.authenticatorSelection = {
                    authenticatorAttachment: 'platform',
                    userVerification:        'required',
                    residentKey:             'discouraged'
                };
                summary = 'authSel={attachment:platform, uv:required, residentKey:discouraged}, attestation:none' + userIdNote;
                break;
            case 'omit_authenticator_attachment':
                publicKey.authenticatorSelection = {
                    userVerification: 'required',
                    residentKey:      'discouraged'
                };
                summary = 'authSel={uv:required, residentKey:discouraged} (no attachment), attestation:none' + userIdNote;
                break;
            case 'minimal_authenticator_selection':
                publicKey.authenticatorSelection = { userVerification: 'required' };
                summary = 'authSel={uv:required} only, attestation:none' + userIdNote;
                break;
            case 'minimal_create_options':
                // Intentionally NO authenticatorSelection field.
                summary = 'no authenticatorSelection field at all, attestation:none' + userIdNote;
                break;
            case 'alternative_user_id':
                publicKey.authenticatorSelection = { userVerification: 'required' };
                summary = 'authSel={uv:required}, attestation:none, user.id RANDOMIZED LOCALLY (16 bytes)';
                break;
            default:
                publicKey.authenticatorSelection = { userVerification: 'required' };
                summary = 'unknown probe -- defaulted to uv:required only';
        }
        return { publicKey: publicKey, optionsSummary: summary };
    }

    function runDiagProbe(probeName) {
        // Click-triggered probe. Fetches a fresh bootstrap challenge,
        // builds the probe-specific options, calls create() with a
        // 10-second watchdog, records the outcome. Does NOT POST to
        // /bootstrap-register-unlock.
        if (state.unlockInFlight) { return; }
        if (!webauthnSupported()) {
            state.lastProbeResult = {
                probeName: probeName, startedUtc: new Date().toISOString(),
                outcome: 'webauthn_unsupported', elapsedMs: 0,
                errorName: '', errorMessage: '', errorStackFirstLine: '',
                uiOpened: false, credentialReturned: false,
                credentialType: '', attestationObjectBytes: null, clientDataJsonBytes: null,
                userActivationIsActive: null, userActivationHasBeenActive: null,
                optionsSummary: ''
            };
            renderLockedView();
            return;
        }
        if (!window.cookbookApi || typeof window.cookbookApi.post !== 'function') { return; }

        var probeAttemptId = 'UX1H5-probe-' + Date.now() + '-' + Math.random().toString(36).slice(2, 6);
        var attemptParam   = '?attempt=' + encodeURIComponent(probeAttemptId);

        state.unlockInFlight = true;
        setUnlockUi('Running diagnostic probe: ' + probeName + '\u2026', true, 'Probe running\u2026');

        window.cookbookApi.post(
            '/api/v1/broker/webauthn/bootstrap-register-challenge' + attemptParam, {}
        ).then(function (chResp) {
            if (!chResp || !chResp.ok || !chResp.body || !chResp.body.challenge) {
                state.unlockInFlight = false;
                state.lastProbeResult = {
                    probeName: probeName, startedUtc: new Date().toISOString(),
                    outcome: 'challenge_failed', elapsedMs: 0,
                    errorName: '', errorMessage: '', errorStackFirstLine: '',
                    uiOpened: false, credentialReturned: false,
                    credentialType: '', attestationObjectBytes: null, clientDataJsonBytes: null,
                    userActivationIsActive: null, userActivationHasBeenActive: null,
                    optionsSummary: ''
                };
                renderLockedView();
                return;
            }
            var built;
            try {
                built = probeOptionsFor(probeName, chResp.body);
            } catch (eOpt) {
                state.unlockInFlight = false;
                state.lastProbeResult = {
                    probeName: probeName, startedUtc: new Date().toISOString(),
                    outcome: 'options_build_failed', elapsedMs: 0,
                    errorName:    (eOpt && eOpt.name)    || '',
                    errorMessage: (eOpt && eOpt.message) || '',
                    errorStackFirstLine: (eOpt && eOpt.stack) ? String(eOpt.stack).split('\n')[0] : '',
                    uiOpened: false, credentialReturned: false,
                    credentialType: '', attestationObjectBytes: null, clientDataJsonBytes: null,
                    userActivationIsActive: null, userActivationHasBeenActive: null,
                    optionsSummary: ''
                };
                renderLockedView();
                return;
            }
            var ua = (window.navigator && window.navigator.userActivation) || null;
            var probeStart = Date.now();
            var probeRec = {
                probeName:                   probeName,
                startedUtc:                  new Date(probeStart).toISOString(),
                optionsSummary:              built.optionsSummary,
                userActivationIsActive:      ua ? !!ua.isActive : null,
                userActivationHasBeenActive: ua ? !!ua.hasBeenActive : null,
                outcome:                     null,
                elapsedMs:                   null,
                errorName:                   null,
                errorMessage:                null,
                errorStackFirstLine:         null,
                uiOpened:                    null,
                credentialReturned:          null,
                credentialType:              null,
                attestationObjectBytes:      null,
                clientDataJsonBytes:         null
            };
            try { window.focus(); } catch (eF) {}
            try { /* eslint-disable-next-line no-console */
                console.log('[PAX Cookbook Unlock]', {
                    phase: 'probe_pre_create',
                    probeName: probeName,
                    optionsSummary: built.optionsSummary,
                    probeAttemptId: probeAttemptId
                });
            } catch (e) {}

            var probeCreatePromise;
            try {
                probeCreatePromise = window.navigator.credentials.create({ publicKey: built.publicKey });
            } catch (eSync) {
                probeRec.outcome             = 'sync_throw';
                probeRec.elapsedMs           = Date.now() - probeStart;
                probeRec.errorName           = (eSync && eSync.name)    || 'sync_throw';
                probeRec.errorMessage        = (eSync && eSync.message) || '';
                probeRec.errorStackFirstLine = (eSync && eSync.stack)   ? String(eSync.stack).split('\n')[0] : '';
                probeRec.uiOpened            = false;
                probeRec.credentialReturned  = false;
                state.lastProbeResult = probeRec;
                state.unlockInFlight  = false;
                renderLockedView();
                return;
            }

            var probeTimedOut = false;
            var probeTimer = setTimeout(function () {
                if (probeTimedOut) { return; }
                probeTimedOut             = true;
                probeRec.outcome          = 'pending_timeout_10s';
                probeRec.elapsedMs        = Date.now() - probeStart;
                probeRec.uiOpened         = false;
                probeRec.credentialReturned = false;
                state.lastProbeResult     = probeRec;
                state.unlockInFlight      = false;
                renderLockedView();
            }, 10 * 1000);

            probeCreatePromise.then(function (cred) {
                if (probeTimedOut) {
                    probeRec.outcome = 'resolved_after_watchdog';
                } else {
                    clearTimeout(probeTimer);
                    probeRec.outcome = 'resolved';
                }
                probeRec.elapsedMs           = Date.now() - probeStart;
                probeRec.uiOpened            = true;
                probeRec.credentialReturned  = !!cred;
                probeRec.credentialType      = (cred && cred.type) ? cred.type : null;
                try {
                    if (cred && cred.response && typeof cred.response.attestationObject !== 'undefined') {
                        var ao = cred.response.attestationObject;
                        probeRec.attestationObjectBytes = ao ? ao.byteLength : 0;
                    }
                    if (cred && cred.response && typeof cred.response.clientDataJSON !== 'undefined') {
                        var cd = cred.response.clientDataJSON;
                        probeRec.clientDataJsonBytes = cd ? cd.byteLength : 0;
                    }
                } catch (eMeta) {}
                state.lastProbeResult = probeRec;
                state.unlockInFlight  = false;
                renderLockedView();
            }, function (err) {
                if (probeTimedOut) {
                    probeRec.outcome = 'rejected_after_watchdog';
                } else {
                    clearTimeout(probeTimer);
                    probeRec.outcome = 'rejected';
                }
                probeRec.elapsedMs           = Date.now() - probeStart;
                probeRec.errorName           = (err && err.name)    || 'unknown';
                probeRec.errorMessage        = (err && err.message) || '';
                probeRec.errorStackFirstLine = (err && err.stack)   ? String(err.stack).split('\n')[0] : '';
                probeRec.uiOpened            = false;
                probeRec.credentialReturned  = false;
                state.lastProbeResult = probeRec;
                state.unlockInFlight  = false;
                renderLockedView();
            });
        }, function () {
            state.unlockInFlight = false;
            state.lastProbeResult = {
                probeName: probeName, startedUtc: new Date().toISOString(),
                outcome: 'challenge_network_error', elapsedMs: 0,
                errorName: '', errorMessage: '', errorStackFirstLine: '',
                uiOpened: false, credentialReturned: false,
                credentialType: '', attestationObjectBytes: null, clientDataJsonBytes: null,
                userActivationIsActive: null, userActivationHasBeenActive: null,
                optionsSummary: ''
            };
            renderLockedView();
        });
    }

    // ----------------------------------------------------------------
    // Boot
    // ----------------------------------------------------------------

    function init() {
        try { window.addEventListener('cookbook:brokerLocked',   onBrokerLocked); } catch (e) {}
        try { window.addEventListener('cookbook:reAuthRequired', onReAuthRequired); } catch (e) {}
        if (document.readyState === 'complete' || document.readyState === 'interactive') {
            probeLockStateOnce();
        } else {
            document.addEventListener('DOMContentLoaded', probeLockStateOnce, { once: true });
        }
    }

    // Diagnostics-only surface. NOT meant for page modules.
    window.cookbookLockOverlay = {
        state:    function () { return state; },
        force:    function (kind) {
            if (kind === 'brokerLocked')   { onBrokerLocked({ detail: { code: 'brokerLocked' } }); }
            if (kind === 'reAuthRequired') { onReAuthRequired({ detail: { code: 'reAuthRequired' } }); }
        },
        dismiss:  function () { unmount(); }
    };

    init();
})();
