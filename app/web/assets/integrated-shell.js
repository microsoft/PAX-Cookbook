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

    // Experimental Entra WAM native courier (T1-S2B). The React content iframe
    // cannot reliably reach window.chrome.webview directly, so it posts a
    // requestId-only message to this same-origin top-level shell. The shell
    // validates provenance (exact same origin, the expected iframe window, and an
    // exact { type, requestId } shape with no extra fields) and forwards ONLY the
    // bounded native envelope to the native host. It adds no field and NEVER posts
    // a result back to the iframe. Native WebMessage origin enforcement remains a
    // second boundary. In standalone/dev with no native host, it fails closed.
    var WAM_REQUEST_TYPE = 'cookbook:experimental-wam-request';

    function nativeHost() {
        try {
            var chrome = window.chrome;
            var webview = chrome && chrome.webview;
            if (webview && typeof webview.postMessage === 'function') {
                return webview;
            }
        } catch (e) {
            /* no native host available */
        }
        return null;
    }

    function isExactWamRequest(data) {
        if (!data || typeof data !== 'object') {
            return false;
        }
        var keys = Object.keys(data);
        if (keys.length !== 2) {
            return false;
        }
        if (data.type !== WAM_REQUEST_TYPE) {
            return false;
        }
        if (typeof data.requestId !== 'string' || data.requestId.length === 0) {
            return false;
        }
        return true;
    }

    function wireExperimentalWamCourier() {
        window.addEventListener('message', function (event) {
            // Exact same-origin only: no wildcard, no suffix/prefix matching.
            if (event.origin !== window.location.origin) {
                return;
            }
            // Only the expected React content iframe may drive the native host.
            if (!frame || event.source !== frame.contentWindow) {
                return;
            }
            if (!isExactWamRequest(event.data)) {
                return;
            }
            var host = nativeHost();
            if (!host) {
                return;
            }
            try {
                host.postMessage({ type: WAM_REQUEST_TYPE, requestId: event.data.requestId });
            } catch (e) {
                /* delivery failure is non-fatal; the renderer polls status */
            }
            // Deliberately never post any response or result back to React.
        }, false);
    }

    // Broker-lock re-check courier. When the React Settings surface applies a
    // sign-in-method change by locking (POST /broker/lock from inside the
    // iframe), the broker locks but this TOP-LEVEL shell -- which owns the lock
    // overlay -- does not observe it (the overlay only mounts on a shell-window
    // 423, and there is no background poll). The renderer posts a { type }-only
    // signal to this shell; the shell validates provenance (exact same origin,
    // the expected iframe window, exact 1-field shape) and asks the existing
    // lock overlay to re-check lock state now. It forwards NOTHING to the native
    // host, posts NOTHING back to the iframe, and grants no new authority: the
    // overlay re-reads /broker/lock-state and mounts only if the broker reports
    // Locked. Fails closed when the overlay module is absent.
    var BROKER_LOCK_INITIATED_TYPE = 'cookbook:broker-lock-initiated';

    function isExactBrokerLockInitiated(data) {
        if (!data || typeof data !== 'object') {
            return false;
        }
        var keys = Object.keys(data);
        if (keys.length !== 1) {
            return false;
        }
        if (data.type !== BROKER_LOCK_INITIATED_TYPE) {
            return false;
        }
        return true;
    }

    function wireBrokerLockCourier() {
        window.addEventListener('message', function (event) {
            // Exact same-origin only: no wildcard, no suffix/prefix matching.
            if (event.origin !== window.location.origin) {
                return;
            }
            // Only the expected React content iframe may drive the re-check.
            if (!frame || event.source !== frame.contentWindow) {
                return;
            }
            if (!isExactBrokerLockInitiated(event.data)) {
                return;
            }
            try {
                var overlay = window.cookbookLockOverlay;
                if (overlay && typeof overlay.recheck === 'function') {
                    overlay.recheck();
                }
            } catch (e) {
                /* absent overlay is non-fatal; the on-demand 423 path still fires */
            }
            // Deliberately never post any response or result back to React, and
            // never forward anything to the native host.
        }, false);
    }

    // ----------------------------------------------------------------
    // Work-account profile control (native-driven, experimental surface)
    // ----------------------------------------------------------------
    //
    // The native host is the sole source of the Work-account profile. It posts a
    // bounded, closed-shape presentation envelope directly to THIS top-level
    // document (window.chrome.webview 'message'); this shell revalidates it and
    // renders a fixed-size avatar + menu in the topbar ONLY when the selected
    // provider is the work account AND the app is Unlocked. The photo bytes are
    // rendered here and NEVER forwarded to the React iframe or written to any
    // browser storage. The menu reuses the existing authority: the broker lock
    // route + lock overlay ("Lock PAX Cookbook"), a bounded native intent for
    // "Use a different work account", and a bounded reveal intent to the React
    // Settings surface for "Sign-in method". A stable build never receives an
    // envelope, so nothing renders and no layout gap appears.
    var WA_PROFILE_TYPE = 'cookbook:work-account-profile';
    var WA_PROFILE_READY_TYPE = 'cookbook:work-account-profile-ready';
    var WA_PROFILE_CLEAR_TYPE = 'cookbook:work-account-profile-clear';
    var WA_DIFFERENT_ACCOUNT_TYPE = 'cookbook:work-account-different-account';
    var WA_DIFFERENT_ACCOUNT_ACK_TYPE = 'cookbook:work-account-different-account-ack';
    var WA_REVEAL_SIGN_IN_METHOD_TYPE = 'cookbook:reveal-sign-in-method';
    var WA_CONTENT_INTERACTION_TYPE = 'cookbook:work-account-profile-content-interaction';
    var WA_SESSION_PROVIDER_PATH = '/api/v1/broker/session-provider';
    var WA_LOCK_STATE_PATH = '/api/v1/broker/lock-state';
    var WA_BROKER_LOCK_PATH = '/api/v1/broker/lock';
    var WA_GATE_POLL_MS = 1500;

    var workAccountProfile = null;
    var waGateTimer = null;
    var waLatestProvider = null;
    var waLatestLockState = null;
    var waCombinedGateOpen = false;

    function isExactWorkAccountContentInteraction(data) {
        if (!data || typeof data !== 'object') {
            return false;
        }
        var keys = Object.keys(data);
        if (keys.length !== 1) {
            return false;
        }
        return data.type === WA_CONTENT_INTERACTION_TYPE;
    }

    function wireWorkAccountContentInteraction() {
        window.addEventListener('message', function (event) {
            if (event.origin !== window.location.origin) {
                return;
            }
            if (!frame || event.source !== frame.contentWindow) {
                return;
            }
            if (!isExactWorkAccountContentInteraction(event.data)) {
                return;
            }
            if (workAccountProfile && typeof workAccountProfile.dismissMenu === 'function') {
                workAccountProfile.dismissMenu();
            }
        }, false);
    }

    function postWorkAccountProfileControl(type) {
        var host = nativeHost();
        if (!host) {
            return;
        }
        try {
            host.postMessage({ type: type });
        } catch (e) {
            /* presentation-control delivery is best-effort and carries no authority */
        }
    }

    function reconcileWorkAccountGate(provenClosedTransition) {
        var open = waLatestProvider === 'work_account' && waLatestLockState === 'Unlocked';
        if (open && !waCombinedGateOpen) {
            waCombinedGateOpen = true;
            postWorkAccountProfileControl(WA_PROFILE_READY_TYPE);
            return;
        }
        if (!open && waCombinedGateOpen) {
            waCombinedGateOpen = false;
            if (workAccountProfile) {
                workAccountProfile.clear();
            }
            // Broker uncertainty still clears the local UI fail-closed, but it
            // must not destroy a just-published pending native presentation.
            if (provenClosedTransition) {
                postWorkAccountProfileControl(WA_PROFILE_CLEAR_TYPE);
            }
        }
    }

    function updateWorkAccountProvider(provider, proven) {
        waLatestProvider = provider;
        workAccountProfile.setProvider(provider);
        reconcileWorkAccountGate(
            proven && provider !== null && provider !== 'work_account'
        );
    }

    function updateWorkAccountLockState(lockState, proven) {
        waLatestLockState = lockState;
        workAccountProfile.setLockState(lockState);
        reconcileWorkAccountGate(proven && lockState === 'Locked');
    }

    function isExactDifferentAccountAck(data) {
        if (!data || typeof data !== 'object') {
            return false;
        }
        var keys = Object.keys(data);
        if (keys.length !== 2) {
            return false;
        }
        if (data.type !== WA_DIFFERENT_ACCOUNT_ACK_TYPE) {
            return false;
        }
        if (data.result !== 'cleared' && data.result !== 'failed') {
            return false;
        }
        return true;
    }

    // "Lock PAX Cookbook" — reuse the existing lock authority: idempotent lock
    // via the broker, then ask the existing overlay to re-check and mount now.
    function workAccountLock() {
        var api = window.cookbookApi;
        if (!api || typeof api.post !== 'function') {
            return;
        }
        api.post(WA_BROKER_LOCK_PATH, {}).then(function (resp) {
            if (resp && resp.ok) {
                try {
                    var overlay = window.cookbookLockOverlay;
                    if (overlay && typeof overlay.recheck === 'function') {
                        overlay.recheck();
                    }
                } catch (e) { /* overlay absent is non-fatal */ }
            }
        }).catch(function () { /* lock failure leaves the session as-is */ });
    }

    // "Use a different work account" — courier a bounded intent to the native
    // host. The native side clears the preferred account, clears the presentation
    // (a 'none' envelope arrives and clears the visible surface), and posts a
    // bounded ack. On 'cleared' the shell locks; on 'failed' it shows a generic
    // retry message. No account-selection field is ever sent.
    function workAccountDifferentAccount() {
        var host = nativeHost();
        if (!host) {
            if (workAccountProfile) {
                workAccountProfile.showMessage(
                    'PAX Cookbook could not switch work accounts just now. Please try again.'
                );
            }
            return;
        }
        try {
            host.postMessage({ type: WA_DIFFERENT_ACCOUNT_TYPE });
        } catch (e) {
            if (workAccountProfile) {
                workAccountProfile.showMessage(
                    'PAX Cookbook could not switch work accounts just now. Please try again.'
                );
            }
        }
        // The ack drives the follow-up (lock or retry message); nothing else here.
    }

    // "Sign-in method" — navigate the embedded surface to Settings and courier a
    // bounded reveal intent (message type only; NO photo, NO identity) to the
    // React iframe with an explicit exact target origin.
    function workAccountSignInMethod() {
        postSection('settings');
        if (frame && frame.contentWindow) {
            try {
                frame.contentWindow.postMessage(
                    { type: WA_REVEAL_SIGN_IN_METHOD_TYPE },
                    window.location.origin
                );
            } catch (e) {
                /* cross-document post failures are non-fatal */
            }
        }
    }

    function pollWorkAccountGate() {
        if (!workAccountProfile) {
            return;
        }
        var api = window.cookbookApi;
        if (!api || typeof api.get !== 'function') {
            return;
        }
        api.get(WA_SESSION_PROVIDER_PATH).then(function (resp) {
            var proven = !!(resp && resp.ok && resp.body &&
                typeof resp.body.selectedProvider === 'string');
            var provider = proven ? resp.body.selectedProvider : null;
            updateWorkAccountProvider(provider, proven);
        }).catch(function () {
            // Unreachable broker: fail closed by clearing the surface.
            updateWorkAccountProvider(null, false);
        });
        api.get(WA_LOCK_STATE_PATH).then(function (resp) {
            var proven = !!(resp && resp.ok && resp.body &&
                typeof resp.body.state === 'string');
            var lockState = proven ? resp.body.state : null;
            updateWorkAccountLockState(lockState, proven);
        }).catch(function () {
            updateWorkAccountLockState(null, false);
        });
    }

    function wireWorkAccountProfile() {
        var factory = window.cookbookWorkAccountProfile;
        if (!factory || typeof factory.create !== 'function') {
            return;
        }
        var mount = document.querySelector('.topbar-actions');
        if (!mount) {
            return;
        }
        workAccountProfile = factory.create({
            doc: document,
            mountTarget: mount,
            onLock: workAccountLock,
            onDifferentAccount: workAccountDifferentAccount,
            onSignInMethod: workAccountSignInMethod
        });

        // Native → top-level presentation + different-account ack channel.
        var host = nativeHost();
        if (host && typeof host.addEventListener === 'function') {
            host.addEventListener('message', function (event) {
                var data = event && event.data;
                if (!data || typeof data !== 'object') {
                    return;
                }
                if (data.type === WA_PROFILE_TYPE) {
                    // The controller revalidates the full envelope and rejects any
                    // malformed / oversized / wrong-type payload (fails to clear).
                    workAccountProfile.applyNativeMessage(data);
                    return;
                }
                if (isExactDifferentAccountAck(data)) {
                    if (data.result === 'cleared') {
                        // Native cleared the preferred account + presentation; now
                        // lock via the existing authority.
                        workAccountLock();
                    } else {
                        workAccountProfile.showMessage(
                            'PAX Cookbook could not switch work accounts just now. Please try again.'
                        );
                    }
                }
            });
        }

        // Lifecycle clearing on window teardown: drop the visible profile and
        // revoke the in-memory image handle so nothing survives the navigation.
        var teardown = function () {
            if (waGateTimer) {
                try { clearInterval(waGateTimer); } catch (e) {}
                waGateTimer = null;
            }
            if (workAccountProfile) {
                workAccountProfile.teardown();
            }
        };
        window.addEventListener('pagehide', teardown, false);
        window.addEventListener('beforeunload', teardown, false);

        // Drive the gate now and on a modest interval so a lock, a provider
        // switch, or a deprovision that flips the broker state promptly clears
        // the surface even without a fresh native envelope.
        pollWorkAccountGate();
        try {
            waGateTimer = setInterval(pollWorkAccountGate, WA_GATE_POLL_MS);
        } catch (e) { /* timers unavailable — the initial poll still ran */ }
    }

    // ----------------------------------------------------------------
    // Batch 1b Stage 3 -- Windows Hello register-before-switch enrollment
    // ----------------------------------------------------------------
    //
    // The React provider switch (Settings) cannot own the WebAuthn create()
    // ceremony: the platform-authenticator prompt must be anchored to this
    // TOP-LEVEL shell window (which owns the HWND) and must run from a real
    // top-level user gesture. The renderer couriers a correlated enrollment
    // REQUEST { type, requestId } to this shell. The shell validates provenance
    // (exact same origin, the expected iframe window, exact 2-field shape),
    // presents a bounded first-use affordance, and -- ONLY from the operator's
    // click on that affordance -- runs the EXISTING lock-overlay bootstrap
    // ceremony in enroll mode (window.cookbookHelloEnroll). It couriers ONE
    // bounded outcome { type, requestId, outcome } back to the SAME iframe with
    // an explicit exact target origin, and never leaks any credential/native
    // value. It fails closed (outcome 'failed'/'cancelled') on every non-success
    // path so the renderer can safely leave the work account selected.
    var WHE_REQUEST_TYPE = 'cookbook:windows-hello-enroll-request';
    var WHE_RESULT_TYPE  = 'cookbook:windows-hello-enroll-result';
    var WHE_OVERLAY_ID   = 'cookbook-hello-enroll';

    var activeEnrollRequestId = null;
    var enrollReadyPollTimer  = null;

    function isExactEnrollRequest(data) {
        if (!data || typeof data !== 'object') { return false; }
        var keys = Object.keys(data);
        if (keys.length !== 2) { return false; }
        if (data.type !== WHE_REQUEST_TYPE) { return false; }
        if (typeof data.requestId !== 'string' || data.requestId.length === 0) { return false; }
        return true;
    }

    // Courier exactly one bounded outcome back to the requesting iframe, with an
    // explicit exact target origin. Never posts to '*'. Idempotent per request.
    function courierEnrollResult(requestId, outcome) {
        if (!frame || !frame.contentWindow || !requestId) { return; }
        try {
            frame.contentWindow.postMessage(
                { type: WHE_RESULT_TYPE, requestId: requestId, outcome: outcome },
                window.location.origin
            );
        } catch (e) {
            /* cross-document post failure is non-fatal; the renderer watchdog fails closed */
        }
    }

    function clearEnrollReadyPoll() {
        if (enrollReadyPollTimer !== null) {
            window.clearInterval(enrollReadyPollTimer);
            enrollReadyPollTimer = null;
        }
    }

    function removeEnrollAffordance() {
        clearEnrollReadyPoll();
        var overlay = document.getElementById(WHE_OVERLAY_ID);
        if (overlay && overlay.parentNode) {
            overlay.parentNode.removeChild(overlay);
        }
    }

    // Finish the active enrollment: courier the outcome, drop the affordance,
    // and release the single-flight lock. A no-op when nothing is active.
    function finishEnroll(outcome) {
        // PHASE-2 attended diagnostic: record the bounded terminal enrollment
        // outcome (behavior-neutral; inert unless the attended marker is present).
        if (attendedDiagEnabled()) { helloAttendedDiag.enrollmentResultOutcome = outcome; }
        var requestId = activeEnrollRequestId;
        activeEnrollRequestId = null;
        removeEnrollAffordance();
        if (requestId) { courierEnrollResult(requestId, outcome); }
    }

    // ----------------------------------------------------------------
    // PHASE-1 Hello capability diagnostic recorder (cycle-01r).
    // MEASUREMENT ONLY. Entirely inert unless the native host injected the
    // read-only window.__paxHelloDiag marker (which it does only in the
    // build-gated isolated app with PAXCB_HELLO_DIAG=1). It changes no
    // switch/enrollment behavior: it observes the REAL courier/affordance,
    // runs the decisive cookbookHelloEnroll.prepare()/isReady() discriminator,
    // AUTO-CANCELS the affordance via the REAL cancel path (so no ceremony ever
    // fires and no credential is created), captures top-level secure-context +
    // UVPAA, merges the iframe's bounded report, and posts ONE bounded, PII-free
    // envelope to the native host for persistence. It never reads or forwards any
    // identity/credential material.
    // ----------------------------------------------------------------
    var helloDiag = {
        active: false,
        topLevelSecureContext: null,
        topLevelPlatformAuthenticatorAvailable: null,
        cookbookHelloEnrollPresent: null,
        cookbookHelloEnrollReadyAfterPrepare: null,
        enrollmentRequestReceived: false,
        enrollmentAffordanceOpened: false,
        probeDone: null,
        emitted: false
    };

    function diagEnabled() {
        try { return !!(window.__paxHelloDiag && window.__paxHelloDiag.enabled); }
        catch (e) { return false; }
    }

    function diagBool(v) { return v === true; }

    function diagOriginRelation(v) {
        return (v === 'same_origin' || v === 'different_origin' || v === 'unavailable')
            ? v : 'unavailable';
    }

    // ----------------------------------------------------------------
    // PHASE-2 attended Hello capability diagnostic recorder (cycle-01r).
    // MEASUREMENT ONLY. Companion to helloDiag above. Entirely inert unless the
    // native host injected the read-only window.__paxHelloAttendedDiag marker
    // (which it does only in the build-gated isolated app — no env flag). Where
    // helloDiag captures the UNATTENDED auto-drive probe (which auto-cancels the
    // affordance), this recorder observes a REAL human-driven switch: the
    // operator clicks "Use Windows Hello", the affordance opens (NOT auto-
    // cancelled), and the operator performs the gesture-dependent create()
    // ceremony. It records only bounded booleans / bounded enums — including the
    // ceremony rejection CLASS the top-level lock-overlay derives from a
    // DOMException name — and NEVER reads or forwards any identity/credential
    // material. It changes no switch/enrollment behavior.
    // ----------------------------------------------------------------
    var helloAttendedDiag = {
        active: false,
        topLevelSecureContext: null,
        topLevelPlatformAuthenticatorAvailable: null,
        enrollmentRequestReceived: false,
        enrollmentAffordanceOpened: false,
        enrollmentGestureStarted: false,
        enrollmentResultOutcome: null,
        probeDone: null,
        // attemptStarted gates every emit: the recorder persists evidence ONLY
        // after a REAL enrollment attempt began (an accepted courier request),
        // never at idle page load. safetyTimer holds the per-attempt safety net
        // so a fresh attempt can clear the prior one and re-arm.
        attemptStarted: false,
        safetyTimer: null,
        emitted: false
    };

    function attendedDiagEnabled() {
        try { return !!(window.__paxHelloAttendedDiag && window.__paxHelloAttendedDiag.enabled); }
        catch (e) { return false; }
    }

    function attendedBoundedOutcome(v) {
        return (v === 'enrolled' || v === 'cancelled' || v === 'timeout' ||
                v === 'failed' || v === 'unavailable' || v === 'transport_failure') ? v : null;
    }

    function attendedFinalProvider(v) {
        return (v === 'work_account' || v === 'windows_hello' || v === 'unavailable') ? v : 'unavailable';
    }

    // Bounded create()-ceremony facts owned by the top-level lock-overlay module,
    // which sets window.__paxHelloAttendedDiagCeremony with ONLY a boolean
    // ceremonyInvoked and a createFailureClass string derived from a DOMException
    // NAME (never the message/stack/credential). Any out-of-domain class is
    // coerced to 'unknown' here, so the on-wire evidence is always bounded.
    function attendedCeremonyFacts() {
        var c = null;
        try { c = window.__paxHelloAttendedDiagCeremony; } catch (e) { c = null; }
        var invoked = !!(c && c.ceremonyInvoked === true);
        var cls = (c && typeof c.createFailureClass === 'string') ? c.createFailureClass : null;
        var domain = { not_allowed: 1, security: 1, abort: 1, timeout: 1, constraint: 1, unknown: 1 };
        if (cls && !domain[cls]) { cls = 'unknown'; }
        return { credentialCeremonyInvoked: invoked, createFailureClass: cls };
    }

    function emitHelloAttendedDiagCapture(iframeReport) {
        var host = nativeHost();
        if (!host) { return; }
        var r = (iframeReport && typeof iframeReport === 'object') ? iframeReport : {};
        var ceremony = attendedCeremonyFacts();
        var capture = {
            schemaVersion: 'hello-attended-1',
            cycleId: 'cycle-01r-hello-capability-probe-repair',
            capturedUtc: new Date().toISOString(),
            iframeSecureContext: diagBool(r.iframeSecureContext),
            topLevelSecureContext: diagBool(helloAttendedDiag.topLevelSecureContext),
            iframePlatformAuthenticatorAvailable: diagBool(r.iframePlatformAuthenticatorAvailable),
            topLevelPlatformAuthenticatorAvailable: diagBool(helloAttendedDiag.topLevelPlatformAuthenticatorAvailable),
            originRelation: diagOriginRelation(r.originRelation),
            enrollmentRequestPosted: diagBool(r.enrollmentRequestPosted),
            enrollmentRequestReceived: diagBool(helloAttendedDiag.enrollmentRequestReceived),
            enrollmentAffordanceOpened: diagBool(helloAttendedDiag.enrollmentAffordanceOpened),
            enrollmentGestureStarted: diagBool(helloAttendedDiag.enrollmentGestureStarted),
            credentialCeremonyInvoked: ceremony.credentialCeremonyInvoked,
            createFailureClass: ceremony.createFailureClass,
            enrollmentResultOutcome: attendedBoundedOutcome(helloAttendedDiag.enrollmentResultOutcome),
            backendSelectAttempted: diagBool(r.backendSelectAttempted),
            backendSelectReason: (typeof r.backendSelectReason === 'string') ? r.backendSelectReason : null,
            backendSelectPersisted: diagBool(r.backendSelectPersisted),
            finalSelectedProvider: attendedFinalProvider(r.finalSelectedProvider)
        };
        try { host.postMessage({ type: 'cookbook:hello-attended-diag-capture', payload: capture }); }
        catch (e) { /* non-fatal: a delivery failure simply yields no evidence file */ }
    }

    function maybeEmitHelloAttendedDiag(iframeReport) {
        // Never persist evidence unless a real attended attempt actually began.
        // This is what makes an idle attended launch write NOTHING: the safety
        // net is only ever armed from beginHelloAttendedDiagAttempt().
        if (!helloAttendedDiag.attemptStarted) { return; }
        if (helloAttendedDiag.emitted) { return; }
        helloAttendedDiag.emitted = true;
        if (helloAttendedDiag.safetyTimer !== null) {
            window.clearTimeout(helloAttendedDiag.safetyTimer);
            helloAttendedDiag.safetyTimer = null;
        }
        (helloAttendedDiag.probeDone || Promise.resolve()).then(function () {
            emitHelloAttendedDiagCapture(iframeReport);
        });
    }

    // Begin a fresh attended attempt when the REAL courier accepts an enrollment
    // request. Resets the per-attempt fields and single-shot latch so a second
    // human attempt (after an earlier cancel/timeout) is captured independently,
    // and arms an attempt-scoped safety net that persists shell-side-only
    // evidence if the iframe report never arrives. Behavior-neutral: it only
    // records; it never opens an affordance or touches the ceremony.
    function beginHelloAttendedDiagAttempt() {
        if (!helloAttendedDiag.active) { return; }
        helloAttendedDiag.attemptStarted = true;
        helloAttendedDiag.emitted = false;
        helloAttendedDiag.enrollmentAffordanceOpened = false;
        helloAttendedDiag.enrollmentGestureStarted = false;
        helloAttendedDiag.enrollmentResultOutcome = null;
        if (helloAttendedDiag.safetyTimer !== null) {
            window.clearTimeout(helloAttendedDiag.safetyTimer);
            helloAttendedDiag.safetyTimer = null;
        }
        // An attended session can be long (the operator drives the gesture at
        // their own pace), so wait generously before persisting shell-side-only
        // evidence if the iframe report never arrives.
        helloAttendedDiag.safetyTimer = window.setTimeout(function () {
            helloAttendedDiag.safetyTimer = null;
            maybeEmitHelloAttendedDiag(null);
        }, 180000);
    }

    function startHelloAttendedDiagRecorder() {
        if (helloAttendedDiag.active) { return; }
        helloAttendedDiag.active = true;
        try { helloAttendedDiag.topLevelSecureContext = !!window.isSecureContext; }
        catch (e) { helloAttendedDiag.topLevelSecureContext = false; }

        helloAttendedDiag.probeDone = probeTopLevelUvpaa().then(function (v) {
            helloAttendedDiag.topLevelPlatformAuthenticatorAvailable = v;
        });

        // Receive the single bounded iframe report, merge, and persist. Exact
        // same-origin and exact source-window gated, exactly like the couriers.
        window.addEventListener('message', function (event) {
            try {
                if (event.origin !== window.location.origin) { return; }
                if (!frame || event.source !== frame.contentWindow) { return; }
                var data = event.data;
                if (!data || typeof data !== 'object' ||
                    data.type !== 'cookbook:hello-attended-iframe-report') { return; }
                maybeEmitHelloAttendedDiag(data.payload);
            } catch (e) { /* non-fatal */ }
        }, false);

        // NOTE: the safety net is intentionally NOT armed here. Arming at page
        // load caused an idle attended launch to persist a misleading null
        // capture and consume the single-shot latch, preempting the operator's
        // later real attempt. The safety net is now armed per attempt from
        // beginHelloAttendedDiagAttempt(), which the courier calls only when a
        // real enrollment request is accepted.
    }

    function probeTopLevelUvpaa() {
        return new Promise(function (resolve) {
            try {
                var pkc = window.PublicKeyCredential;
                if (pkc && typeof pkc.isUserVerifyingPlatformAuthenticatorAvailable === 'function') {
                    pkc.isUserVerifyingPlatformAuthenticatorAvailable().then(
                        function (v) { resolve(!!v); },
                        function () { resolve(false); }
                    );
                } else {
                    resolve(false);
                }
            } catch (e) { resolve(false); }
        });
    }

    // Decisive branch-A-vs-B discriminator: does the EXISTING first-use lock
    // Hello enrollment module report ready in THIS WebView2 after a background
    // prepare()? Calls prepare() (bootstrap challenge only — NEVER create()) and
    // polls isReady() for a bounded window.
    function runHelloEnrollDiscriminatorProbe() {
        return new Promise(function (resolve) {
            var enroll = null;
            try { enroll = window.cookbookHelloEnroll; } catch (e) { enroll = null; }
            var present = !!(enroll &&
                typeof enroll.enrollFromGesture === 'function' &&
                typeof enroll.prepare === 'function' &&
                typeof enroll.isReady === 'function');
            helloDiag.cookbookHelloEnrollPresent = present;
            if (!present) {
                helloDiag.cookbookHelloEnrollReadyAfterPrepare = false;
                resolve();
                return;
            }
            try { enroll.prepare(); } catch (e) {}
            var deadline = Date.now() + 4000;
            var timer = window.setInterval(function () {
                var ready = false;
                try { ready = !!enroll.isReady(); } catch (e) { ready = false; }
                if (ready) {
                    helloDiag.cookbookHelloEnrollReadyAfterPrepare = true;
                    window.clearInterval(timer);
                    resolve();
                } else if (Date.now() > deadline) {
                    helloDiag.cookbookHelloEnrollReadyAfterPrepare = false;
                    window.clearInterval(timer);
                    resolve();
                }
            }, 200);
        });
    }

    function emitHelloDiagCapture(iframeReport) {
        if (helloDiag.emitted) { /* single-shot guard handled by caller */ }
        var host = nativeHost();
        if (!host) { return; }
        var r = (iframeReport && typeof iframeReport === 'object') ? iframeReport : {};
        var capture = {
            schemaVersion: 'hello-diag-1',
            cycleId: 'cycle-01r-hello-capability-probe-repair',
            capturedUtc: new Date().toISOString(),
            iframeSecureContext: diagBool(r.iframeSecureContext),
            topLevelSecureContext: diagBool(helloDiag.topLevelSecureContext),
            iframePlatformAuthenticatorAvailable: diagBool(r.iframePlatformAuthenticatorAvailable),
            topLevelPlatformAuthenticatorAvailable: diagBool(helloDiag.topLevelPlatformAuthenticatorAvailable),
            originRelation: diagOriginRelation(r.originRelation),
            cookbookHelloEnrollPresent: diagBool(helloDiag.cookbookHelloEnrollPresent),
            cookbookHelloEnrollReadyAfterPrepare: diagBool(helloDiag.cookbookHelloEnrollReadyAfterPrepare),
            enrollmentRequestPosted: diagBool(r.enrollmentRequestPosted),
            enrollmentRequestReceived: diagBool(helloDiag.enrollmentRequestReceived),
            enrollmentAffordanceOpened: diagBool(helloDiag.enrollmentAffordanceOpened),
            backendSelectAttempted: diagBool(r.backendSelectAttempted),
            backendSelectReason: (typeof r.backendSelectReason === 'string') ? r.backendSelectReason : null,
            terminalOutcome: diagTerminalOutcome(r),
            selectedProviderStillWorkAccount: diagBool(r.selectedProviderStillWorkAccount),
            noCredentialCreated: diagBool(r.noCredentialCreated),
            // Authoritative shell-owned assertions: this recorder never invokes
            // the create() ceremony and never exercises a gesture-dependent path.
            credentialCeremonyInvoked: false,
            gestureDependentFieldsNotExercised: true
        };
        try { host.postMessage({ type: 'cookbook:hello-diag-capture', payload: capture }); }
        catch (e) { /* non-fatal: a delivery failure simply yields no evidence file */ }
    }

    // Derive the bounded terminal outcome from the MERGED shell + iframe view.
    // The shell owns the affordance-opened fact, so it — not the iframe — is the
    // authority on whether the enroll affordance was reached before auto-cancel.
    function diagTerminalOutcome(r) {
        if (r.selectedProviderStillWorkAccount === false) { return 'backend_switched'; }
        var reason = (typeof r.backendSelectReason === 'string') ? r.backendSelectReason : null;
        if (reason === 'switched') { return 'backend_switched'; }
        if (reason === 'windows_hello_platform_unavailable') { return 'platform_unavailable'; }
        if (reason === 'windows_hello_registration_repair_required') { return 'registration_repair_required'; }
        if (reason === 'transport_failure') { return 'transport_failure'; }
        if (reason === 'request_failed') { return 'request_failed'; }
        if (reason === 'cancelled') { return 'cancelled'; }
        if (reason === 'windows_hello_enrollment_required') {
            return helloDiag.enrollmentAffordanceOpened
                ? 'affordance_opened_then_cancelled'
                : 'enrollment_required_no_affordance';
        }
        if (r.backendSelectAttempted !== true) { return 'incomplete'; }
        return 'error';
    }

    function maybeEmitHelloDiag(iframeReport) {
        if (helloDiag.emitted) { return; }
        helloDiag.emitted = true;
        (helloDiag.probeDone || Promise.resolve()).then(function () {
            emitHelloDiagCapture(iframeReport);
        });
    }

    function startHelloDiagRecorder() {
        if (helloDiag.active) { return; }
        helloDiag.active = true;
        try { helloDiag.topLevelSecureContext = !!window.isSecureContext; }
        catch (e) { helloDiag.topLevelSecureContext = false; }

        helloDiag.probeDone = Promise.all([
            probeTopLevelUvpaa().then(function (v) {
                helloDiag.topLevelPlatformAuthenticatorAvailable = v;
            }),
            runHelloEnrollDiscriminatorProbe()
        ]);

        // Receive the single bounded iframe report, merge, and persist. Exact
        // same-origin and exact source-window gated, exactly like the couriers.
        window.addEventListener('message', function (event) {
            try {
                if (event.origin !== window.location.origin) { return; }
                if (!frame || event.source !== frame.contentWindow) { return; }
                var data = event.data;
                if (!data || typeof data !== 'object' ||
                    data.type !== 'cookbook:hello-diag-iframe-report') { return; }
                maybeEmitHelloDiag(data.payload);
            } catch (e) { /* non-fatal */ }
        }, false);

        // Safety net: if the React iframe never reports (e.g. the switch surface
        // failed to render or drive), still persist the shell-side evidence so
        // the failure point is captured rather than silently lost.
        window.setTimeout(function () { maybeEmitHelloDiag(null); }, 45000);
    }

    function buildEnrollAffordance() {
        var overlay = document.createElement('div');
        overlay.id = WHE_OVERLAY_ID;
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-labelledby', WHE_OVERLAY_ID + '-heading');
        overlay.setAttribute('aria-describedby', WHE_OVERLAY_ID + '-desc');
        overlay.style.cssText = [
            'position:fixed', 'inset:0', 'z-index:2147483000',
            'display:flex', 'align-items:center', 'justify-content:center',
            'background:rgba(0,0,0,0.55)', 'padding:24px'
        ].join(';');

        var panel = document.createElement('div');
        panel.style.cssText = [
            'max-width:420px', 'width:100%', 'box-sizing:border-box',
            'background:#ffffff', 'color:#1b1b1b', 'border-radius:10px',
            'padding:24px', 'box-shadow:0 12px 40px rgba(0,0,0,0.35)',
            'font-family:Segoe UI, system-ui, sans-serif'
        ].join(';');

        var heading = document.createElement('h2');
        heading.id = WHE_OVERLAY_ID + '-heading';
        heading.textContent = 'Set up Windows Hello for PAX Cookbook';
        heading.style.cssText = 'margin:0 0 12px;font-size:18px;line-height:1.3;';

        var desc = document.createElement('p');
        desc.id = WHE_OVERLAY_ID + '-desc';
        desc.textContent = 'Windows will ask you to confirm with your face, fingerprint, or PIN.';
        desc.style.cssText = 'margin:0 0 16px;font-size:14px;line-height:1.5;';

        var status = document.createElement('p');
        status.id = WHE_OVERLAY_ID + '-status';
        status.setAttribute('role', 'status');
        status.setAttribute('aria-live', 'polite');
        status.style.cssText = 'margin:0 0 16px;font-size:13px;line-height:1.4;color:#5a5a5a;min-height:18px;';
        status.textContent = 'Getting Windows Hello ready\u2026';

        var actions = document.createElement('div');
        actions.style.cssText = 'display:flex;gap:8px;justify-content:flex-end;';

        var cancelBtn = document.createElement('button');
        cancelBtn.id = WHE_OVERLAY_ID + '-cancel';
        cancelBtn.type = 'button';
        cancelBtn.textContent = 'Not now';
        cancelBtn.style.cssText = [
            'appearance:none', 'border:1px solid #c8c8c8', 'background:#f3f3f3',
            'color:#1b1b1b', 'border-radius:6px', 'padding:8px 16px',
            'font-size:14px', 'cursor:pointer'
        ].join(';');

        var primaryBtn = document.createElement('button');
        primaryBtn.id = WHE_OVERLAY_ID + '-primary';
        primaryBtn.type = 'button';
        primaryBtn.textContent = 'Preparing\u2026';
        primaryBtn.disabled = true;
        primaryBtn.style.cssText = [
            'appearance:none', 'border:1px solid #0b5fb0', 'background:#0b5fb0',
            'color:#ffffff', 'border-radius:6px', 'padding:8px 16px',
            'font-size:14px', 'cursor:pointer'
        ].join(';');

        actions.appendChild(cancelBtn);
        actions.appendChild(primaryBtn);
        panel.appendChild(heading);
        panel.appendChild(desc);
        panel.appendChild(status);
        panel.appendChild(actions);
        overlay.appendChild(panel);

        return { overlay: overlay, primaryBtn: primaryBtn, cancelBtn: cancelBtn, status: status };
    }

    function presentEnrollAffordance() {
        // Fail closed if the shell ceremony module is not present (standalone /
        // dev with no lock-overlay). The renderer then leaves work_account.
        var enroll = window.cookbookHelloEnroll;
        if (!enroll || typeof enroll.enrollFromGesture !== 'function') {
            finishEnroll('failed');
            return;
        }

        var built = buildEnrollAffordance();
        document.body.appendChild(built.overlay);
        try { built.cancelBtn.focus(); } catch (e) {}

        // PHASE-1 diagnostic: record that the REAL affordance opened, then
        // AUTO-CANCEL it via the REAL cancel path so no ceremony ever fires and
        // no credential is created. Deferred briefly so the affordance is fully
        // realized (and its cancel listener attached) before the click. Inert
        // in a normal launch.
        if (diagEnabled()) {
            helloDiag.enrollmentAffordanceOpened = true;
            window.setTimeout(function () {
                try { built.cancelBtn.click(); }
                catch (e) { try { finishEnroll('cancelled'); } catch (e2) {} }
            }, 250);
        }

        // PHASE-2 attended diagnostic: record that the REAL affordance opened, but
        // do NOT auto-cancel — the operator drives the gesture-dependent ceremony
        // themselves. Purely observational; inert without the attended marker.
        if (attendedDiagEnabled()) {
            helloAttendedDiag.enrollmentAffordanceOpened = true;
        }

        // Prepare the bootstrap challenge in the background so the primary click
        // can call navigator.credentials.create() synchronously (activation-safe).
        try { enroll.prepare(); } catch (e) {}

        // Enable the primary button ONLY once a fresh prepared challenge exists,
        // so create() runs inside the click's user-activation window. Surface a
        // bounded error if preparation fails.
        clearEnrollReadyPoll();
        enrollReadyPollTimer = window.setInterval(function () {
            if (activeEnrollRequestId === null) { clearEnrollReadyPoll(); return; }
            var ready = false;
            var st = 'pending';
            try { ready = !!enroll.isReady(); } catch (e) { ready = false; }
            try { st = enroll.status(); } catch (e) { st = 'pending'; }
            if (ready) {
                clearEnrollReadyPoll();
                built.primaryBtn.disabled = false;
                built.primaryBtn.textContent = 'Set up Windows Hello';
                built.status.textContent = 'Ready. Select "Set up Windows Hello" to continue.';
                try { built.primaryBtn.focus(); } catch (e) {}
            } else if (st === 'failed') {
                clearEnrollReadyPoll();
                built.primaryBtn.disabled = true;
                built.primaryBtn.textContent = 'Set up Windows Hello';
                built.status.textContent = 'Windows Hello could not be prepared. Select "Not now" to go back.';
            }
        }, 300);

        // Primary click == real top-level user activation. Run the ceremony
        // synchronously from here (no await before enrollFromGesture, which
        // calls create() synchronously) and courier the bounded outcome back.
        built.primaryBtn.addEventListener('click', function () {
            if (activeEnrollRequestId === null) { return; }
            if (built.primaryBtn.disabled) { return; }
            // PHASE-2 attended diagnostic: record that the operator's real gesture
            // started (behavior-neutral; inert without the attended marker).
            if (attendedDiagEnabled()) { helloAttendedDiag.enrollmentGestureStarted = true; }
            clearEnrollReadyPoll();
            built.primaryBtn.disabled = true;
            built.cancelBtn.disabled = true;
            built.status.textContent = 'Confirming it\u2019s you with Windows Hello\u2026';
            var p;
            try {
                p = enroll.enrollFromGesture();
            } catch (e) {
                finishEnroll('failed');
                return;
            }
            Promise.resolve(p).then(function (outcome) {
                var known = (outcome === 'enrolled' || outcome === 'cancelled' ||
                             outcome === 'timeout'  || outcome === 'unavailable');
                finishEnroll(known ? outcome : 'failed');
            }, function () {
                finishEnroll('failed');
            });
        });

        // Cancel / dismiss without acting -> bounded 'cancelled'; the renderer
        // leaves work_account selected. Escape mirrors the cancel button.
        built.cancelBtn.addEventListener('click', function () {
            finishEnroll('cancelled');
        });
        built.overlay.addEventListener('keydown', function (ev) {
            if (ev && ev.key === 'Escape') {
                ev.preventDefault();
                finishEnroll('cancelled');
            }
        });
    }

    function wireWindowsHelloEnrollCourier() {
        window.addEventListener('message', function (event) {
            // Exact same-origin only: no wildcard, no suffix/prefix matching.
            if (event.origin !== window.location.origin) { return; }
            // Only the expected React content iframe may drive enrollment.
            if (!frame || event.source !== frame.contentWindow) { return; }
            if (!isExactEnrollRequest(event.data)) { return; }

            // Single-flight: if an enrollment is already active, fail the
            // newcomer closed (never interrupt the in-progress ceremony).
            if (activeEnrollRequestId !== null) {
                courierEnrollResult(event.data.requestId, 'failed');
                return;
            }
            activeEnrollRequestId = event.data.requestId;
            if (diagEnabled()) { helloDiag.enrollmentRequestReceived = true; }
            if (attendedDiagEnabled()) {
                helloAttendedDiag.enrollmentRequestReceived = true;
                beginHelloAttendedDiagAttempt();
            }
            presentEnrollAffordance();
        }, false);
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
                // Experimental-channel banner. Gated PURELY on the installed
                // channel from this same runtime/version payload (releaseChannel -
                // the field the native window-title suffix also uses). Fail-safe:
                // reveal ONLY when the value is EXACTLY "experimental"; stable,
                // missing, or anything else leaves the banner empty and
                // zero-height, so a stable build shows nothing.
                var channel = (v.releaseChannel == null ? '' : String(v.releaseChannel))
                    .trim().toLowerCase();
                if (channel === 'experimental') {
                    var banner = document.getElementById('experimental-banner');
                    if (banner) {
                        banner.textContent = 'Experimental test build \u2014 not for production';
                        banner.classList.add('is-experimental');
                    }
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

        // Wire the experimental WAM native courier as soon as the iframe element
        // exists so a requestId-only message from the React surface can be
        // validated and forwarded to the native host.
        wireExperimentalWamCourier();

        // Wire the Windows Hello register-before-switch enrollment courier so a
        // correlated request from the React provider switch can present the
        // top-level ceremony affordance and return a bounded outcome.
        wireWindowsHelloEnrollCourier();

        // Wire the broker-lock re-check courier so a lock initiated from inside
        // the React Settings iframe promptly mounts the top-level lock overlay.
        wireBrokerLockCourier();

        // Wire the native-driven Work-account profile control. A stable build
        // never receives a presentation envelope, so this renders nothing.
        wireWorkAccountProfile();

        // A captured pointer interaction in the same-origin React iframe cannot
        // bubble into this document. Accept only its exact type-only courier and
        // dismiss the presentation menu without forwarding or taking authority.
        wireWorkAccountContentInteraction();

        // PHASE-1 Hello capability diagnostic recorder (cycle-01r). Inert unless
        // the native host injected the read-only window.__paxHelloDiag marker
        // (isolated build + PAXCB_HELLO_DIAG=1). Starts the top-level probes and
        // the bounded evidence merge/persist path. A normal launch never runs it.
        if (diagEnabled()) {
            startHelloDiagRecorder();
        }

        // PHASE-2 attended Hello capability diagnostic recorder (cycle-01r). Inert
        // unless the native host injected the read-only window.__paxHelloAttendedDiag
        // marker (isolated build only — no env flag). Starts the top-level probes
        // and the bounded observe-a-real-switch evidence merge/persist path. A
        // normal launch never runs it.
        if (attendedDiagEnabled()) {
            startHelloAttendedDiagRecorder();
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
