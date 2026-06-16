// PAX Cookbook -- Topbar broker-status pill (Phase AE).
//
// Truthful, bounded visibility into broker reachability. This module
// owns exactly ONE topbar element (#broker-status) and reads exactly
// TWO event types dispatched by api.js:
//
//   'cookbook:api-result'    every settled HTTP request
//   'cookbook:unauthorized'  401 specifically
//
// It also performs a SINGLE one-shot probe of /api/v1/health at boot
// (the broker route is unauthenticated by design -- see Start-Broker
// Get-HealthPayload doctrine). After that single probe, the pill is
// updated ONLY from real fetch results that the SPA already had to
// issue for some other reason. There is NO background polling, NO
// timer, NO heartbeat, NO ping. This module never synthesizes
// reachability evidence it has not actually observed.
//
// State vocabulary (Phase AE: six terminal labels; Phase AF adds 'locked'
// as a seventh, slotted between 'unauthorized' and 'unreachable' in
// the severity ordering):
//
//   'unknown'       no fetch result has settled yet, and the boot
//                   probe has not completed (or failed in a way that
//                   does not entitle us to claim 'unreachable').
//                   This is the initial label; it is replaced as
//                   soon as evidence arrives.
//
//   'ok'            most recent settled fetch returned 2xx AND the
//                   most recent /api/v1/health snapshot reported
//                   status='ok' (or no health snapshot has ever
//                   contradicted that). The broker is up, serving,
//                   and has no recent-error queue contents.
//
//   'degraded'      most recent /api/v1/health snapshot reported
//                   status='degraded' (recentErrors non-empty OR
//                   recentErrorOverflowCount > 0). HTTP traffic
//                   continues to succeed. The broker is doing its
//                   job but has recorded that something went wrong;
//                   the recentErrors array on /health enumerates
//                   what is currently visible. See OPERATOR_GUIDE.md
//                   section 11.8 for the per-error contract.
//
//   'locked'        Phase AF. The broker is up and reachable but is
//                   refusing mutating routes pending Windows Hello
//                   re-auth. Set when /api/v1/broker/lock-state or
//                   any 423 response reports state=Locked. Cleared
//                   when /api/v1/broker/unlock succeeds (or a fresh
//                   /broker/lock-state probe reports Unlocked).
//                   Sticky -- not cleared by an unrelated 2xx fetch
//                   because allow-listed routes (/health,
//                   /broker/lock-state) still return 200 while
//                   locked.
//
//   'unreachable'   most recent settled fetch returned a networkError
//                   (broker did not respond, connection refused,
//                   request timed out). No HTTP-status traffic is
//                   currently succeeding. Sticky until the next
//                   successful fetch flips it back.
//
//   'unauthorized'  any settled fetch returned HTTP 401. Sticky
//                   until either the page reload re-issues a fresh
//                   token, or any subsequent fetch succeeds. The
//                   chef is told to relaunch from the launcher;
//                   the SPA cannot re-issue its own session token.
//
//   'stale'         a state transition rule, not a primary label.
//                   When the page is hidden by 'visibilitychange'
//                   for longer than STALE_AFTER_MS, the pill steps
//                   from its current label back to 'unknown' so
//                   the next user action triggers fresh evidence.
//                   We never PROMOTE to 'ok' on visibility-restore.
//
// Truthful ambiguity is acceptable; fabricated certainty is not.
// When in doubt the pill says 'unknown' rather than guessing.
//
// Token-status pill ('present' / 'missing' / 'rejected'). The
// 'rejected' label is added here in Phase AE: api.js dispatches
// 'cookbook:unauthorized' on every 401, and this module flips the
// existing #token-status element to 'rejected' so the chef sees
// the same truth in every tab regardless of which page surfaced
// the 401.
(function () {
    'use strict';

    var STATUS_EL_ID  = 'broker-status';
    var TOKEN_EL_ID   = 'token-status';
    var STALE_AFTER_MS = 5 * 60 * 1000;  // 5 minutes hidden -> step to 'unknown'

    // Most recent observed broker state. Plain object so it is easy
    // to inspect from devtools. Never written to storage; never sent
    // anywhere.
    var observed = {
        label:                     'unknown',
        lastUpdateUtc:             null,
        lastSuccessfulFetchUtc:    null,
        lastNetworkErrorUtc:       null,
        lastNetworkErrorMessage:   null,
        lastUnauthorizedUtc:       null,
        // Phase AH.C3 -- bounded broker-supplied reason string from
        // the most recent 401 response body (e.g.
        // 'session_token_not_recognized'). null if no 401 has been
        // observed yet, or if the 401 body did not include a reason.
        // Never invented client-side; only forwarded from the broker.
        lastUnauthorizedReason:    null,
        lastLockedUtc:             null,
        lastUnlockedUtc:           null,
        lastHealthSnapshot:        null,   // { status, recentErrorCount, recentErrorOverflowCount }
        // Phase AH.C3 -- observational broker-session evidence
        // captured at the boot probe of /api/v1/health (unauth). The
        // SPA never re-probes /health on its own; this value is fixed
        // for the lifetime of the SPA session. It is forensic
        // ambient information shown in the pill title -- it does NOT
        // authorize anything and is NEVER reconciled or refreshed.
        bootObservedBrokerSessionId:           null,
        bootObservedBrokerSessionStartedAtUtc: null,
        bootObservedBrokerStartupClass:        null,
        hiddenSinceUtc:            null
    };

    function setLabel(label, titleHint) {
        var el = document.getElementById(STATUS_EL_ID);
        if (!el) { return; }
        el.textContent = friendlyServerText(label);
        el.className = label;  // CSS hook: .ok / .degraded / .unreachable / .unauthorized / .unknown
        if (titleHint) {
            el.title = titleHint;
        }
        observed.label = label;
        observed.lastUpdateUtc = new Date().toISOString();
    }

    // Render the broker-status pill's textContent in human-readable
    // terms. The CSS hook (el.className) keeps the raw status string
    // so existing colour rules still apply.
    function friendlyServerText(label) {
        switch (label) {
            case 'ok':            return 'Running';
            case 'degraded':      return 'Running (degraded)';
            case 'unreachable':   return 'Offline';
            case 'unauthorized':  return 'Sign-in expired';
            case 'locked':        return 'Locked';
            case 'unknown':       return 'Unknown';
            default:              return label ? String(label) : 'Unknown';
        }
    }

    function setTokenLabel(label) {
        var el = document.getElementById(TOKEN_EL_ID);
        if (!el) { return; }
        el.textContent = friendlyTokenText(label);
        // Use existing class hooks: ok / miss (already styled). Map
        // 'rejected' onto the same negative-state class as 'missing'
        // so existing CSS continues to work without a redesign.
        if (label === 'present') {
            el.className = 'ok';
        } else {
            el.className = 'miss';
        }
    }

    // Render the token-status pill's textContent in human-readable
    // terms while preserving the .ok / .miss CSS hook on the element.
    function friendlyTokenText(label) {
        switch (label) {
            case 'present':  return 'Connected';
            case 'missing':  return 'No token';
            case 'rejected': return 'Sign-in expired';
            case 'checking': return 'Checking';
            default:         return label ? String(label) : 'Unknown';
        }
    }

    // Decide the new pill label given the freshest evidence we have.
    // Order of precedence reflects severity:
    //   unauthorized > locked > unreachable > degraded > ok > unknown
    // A successful HTTP fetch resets unauthorized/unreachable. A
    // health snapshot of 'degraded' overrides 'ok' but never trumps
    // a still-active unauthorized/locked/unreachable signal.
    function recompute(reason) {
        // Sticky unauthorized: do not silently fall back from 'unauthorized'
        // to 'ok' on a subsequent 200, UNLESS we have explicit evidence
        // the token is now accepted again (i.e. a successful request
        // happened AFTER the last 401).
        var unauthAfterSuccess =
            observed.lastUnauthorizedUtc &&
            (!observed.lastSuccessfulFetchUtc ||
             observed.lastUnauthorizedUtc > observed.lastSuccessfulFetchUtc);
        if (unauthAfterSuccess) {
            // Phase AH.C3 -- surface the bounded broker-supplied reason
            // when available. The wording is observational and explicit
            // about the restart-boundary: the broker may or may not be
            // the same broker the SPA bootstrapped against, but in
            // either case the SPA has no path to re-acquire authority
            // on its own. Relaunching from the launcher is the only
            // truthful instruction. No silent retry. No reconnect.
            var unauthMsg = 'Broker rejected the session token (HTTP 401).';
            if (observed.lastUnauthorizedReason) {
                unauthMsg += ' Reason: ' + observed.lastUnauthorizedReason + '.';
            }
            if (observed.bootObservedBrokerSessionId) {
                unauthMsg += ' SPA bootstrapped against broker session ' +
                    observed.bootObservedBrokerSessionId + '.';
            }
            unauthMsg += ' Relaunch PAX Cookbook from the launcher to obtain a fresh token.';
            setLabel('unauthorized', unauthMsg);
            return;
        }

        // Sticky locked: cleared ONLY by an explicit
        // 'cookbook:brokerUnlocked' event (the lock-overlay module
        // dispatches this on a 200 from /broker/unlock or on a fresh
        // /broker/lock-state probe that reports Unlocked). We do NOT
        // unlock on an unrelated 2xx because allow-listed routes
        // (/health, /broker/lock-state) keep returning 200 while the
        // broker is locked.
        var lockedAfterUnlock =
            observed.lastLockedUtc &&
            (!observed.lastUnlockedUtc ||
             observed.lastLockedUtc > observed.lastUnlockedUtc);
        if (lockedAfterUnlock) {
            setLabel('locked',
                'Broker is up but locked. Verify with Windows Hello / PIN to unlock; mutating routes will return HTTP 423 until then.');
            return;
        }

        var unreachable =
            observed.lastNetworkErrorUtc &&
            (!observed.lastSuccessfulFetchUtc ||
             observed.lastNetworkErrorUtc > observed.lastSuccessfulFetchUtc);
        if (unreachable) {
            var msg = observed.lastNetworkErrorMessage || 'broker did not respond';
            setLabel('unreachable',
                'Broker did not respond to the most recent request (' + msg + '). The SPA is showing the last data it successfully fetched; it is not live.');
            return;
        }

        if (observed.lastHealthSnapshot && observed.lastHealthSnapshot.status === 'degraded') {
            var snap = observed.lastHealthSnapshot;
            var count = (typeof snap.recentErrorCount === 'number') ? snap.recentErrorCount : 0;
            var over  = (typeof snap.recentErrorOverflowCount === 'number') ? snap.recentErrorOverflowCount : 0;
            setLabel('degraded',
                'Broker is up but reports degraded state: ' + count + ' recent error(s) visible, ' + over + ' displaced. See /api/v1/health for the per-error breakdown.');
            return;
        }

        if (observed.lastSuccessfulFetchUtc) {
            setLabel('ok',
                'Broker is reachable and reported no degraded state on the most recent health probe.');
            return;
        }

        setLabel('unknown',
            'No broker traffic has settled yet. The first fetch will set this pill from real evidence.');
        // Reason is informational only -- never put it into the pill
        // text. The pill is meant to be glanceable.
        void reason;
    }

    function onApiResult(ev) {
        var d = (ev && ev.detail) ? ev.detail : null;
        if (!d) { return; }
        if (d.ok) {
            observed.lastSuccessfulFetchUtc = d.timestampUtc;
        } else if (d.networkError) {
            observed.lastNetworkErrorUtc     = d.timestampUtc;
            observed.lastNetworkErrorMessage = d.networkError;
        } else if (d.status === 401) {
            observed.lastUnauthorizedUtc = d.timestampUtc;
        }
        // Note: non-401 HTTP errors (404, 409, 422, 500, etc.) are
        // *application-level* outcomes, not broker-reachability
        // outcomes. They DO count as a successful round-trip for the
        // purposes of the broker-status pill -- the broker responded.
        // The page surfaces the application error in its own banner.
        if (!d.ok && !d.networkError && d.status !== 401) {
            observed.lastSuccessfulFetchUtc = d.timestampUtc;
        }
        recompute('api-result');
    }

    function onUnauthorized(ev) {
        var d = (ev && ev.detail) ? ev.detail : null;
        observed.lastUnauthorizedUtc = (d && d.timestampUtc) ? d.timestampUtc : new Date().toISOString();
        // Phase AH.C3 -- record the bounded broker-supplied reason
        // string (e.g. 'session_token_not_recognized') so recompute()
        // can surface it in the pill title hint.
        if (d && typeof d.unauthorizedReason === 'string' && d.unauthorizedReason.length > 0) {
            observed.lastUnauthorizedReason = d.unauthorizedReason;
        }
        setTokenLabel('rejected');
        recompute('unauthorized');
    }

    // Phase AF: locked / unlocked lifecycle. Pure observers; we do
    // not initiate any HTTP calls -- the lock-overlay module owns
    // those.
    function onBrokerLocked(ev) {
        var d = (ev && ev.detail) ? ev.detail : null;
        observed.lastLockedUtc = (d && d.timestampUtc) ? d.timestampUtc : new Date().toISOString();
        recompute('brokerLocked');
    }

    function onBrokerUnlocked(ev) {
        var d = (ev && ev.detail) ? ev.detail : null;
        observed.lastUnlockedUtc = (d && d.timestampUtc) ? d.timestampUtc : new Date().toISOString();
        recompute('brokerUnlocked');
    }

    // One-shot boot probe of /api/v1/health. Unauthenticated by
    // design (broker permits this single route without bearer). Uses
    // the standard cookbookApi.get so the result also flows through
    // the announceResult event path. Honors the timeout. We do this
    // exactly once; subsequent updates come from real traffic.
    function probeHealthOnce() {
        if (!window.cookbookApi || typeof window.cookbookApi.get !== 'function') {
            return;
        }
        window.cookbookApi.get('/api/v1/health').then(function (resp) {
            if (resp && resp.ok && resp.body) {
                observed.lastHealthSnapshot = {
                    status:                    resp.body.status || null,
                    recentErrorCount:          resp.body.recentErrorCount || 0,
                    recentErrorOverflowCount:  resp.body.recentErrorOverflowCount || 0
                };
                // Phase AH.C3 -- one-shot capture of the broker
                // session evidence served on /api/v1/health. The SPA
                // records what it bootstrapped against; it does NOT
                // re-poll, NOT compare across requests, NOT branch on
                // it. The value is purely forensic and surfaces only
                // in the pill title on the unauthorized path. The SPA
                // never makes restoration decisions from this value.
                if (resp.body.brokerSession && typeof resp.body.brokerSession === 'object') {
                    var bs = resp.body.brokerSession;
                    if (typeof bs.sessionId === 'string') {
                        observed.bootObservedBrokerSessionId = bs.sessionId;
                    }
                    if (typeof bs.startedAtUtc === 'string') {
                        observed.bootObservedBrokerSessionStartedAtUtc = bs.startedAtUtc;
                    }
                    if (typeof bs.startupClassification === 'string') {
                        observed.bootObservedBrokerStartupClass = bs.startupClassification;
                    }
                }
                recompute('boot-probe');
            }
            // On networkError or non-2xx, onApiResult has already
            // updated the pill via the event path.
        });
    }

    // Visibility-change handling: if the tab is hidden for longer
    // than STALE_AFTER_MS, the next visibility-restore steps the
    // pill back to 'unknown' so the chef knows the displayed
    // evidence may be cold. We do not auto-probe on restore; the
    // chef's next interaction (Reload button, navigation) will
    // produce fresh evidence on its own.
    function onVisibilityChange() {
        if (document.hidden) {
            observed.hiddenSinceUtc = new Date().toISOString();
            return;
        }
        if (!observed.hiddenSinceUtc) { return; }
        var hiddenStart = new Date(observed.hiddenSinceUtc).getTime();
        var now = new Date().getTime();
        observed.hiddenSinceUtc = null;
        if (isFinite(hiddenStart) && (now - hiddenStart) > STALE_AFTER_MS) {
            // Step current evidence aside; we will not pretend it is
            // still current. The next real fetch will repopulate.
            observed.lastSuccessfulFetchUtc = null;
            observed.lastHealthSnapshot     = null;
            recompute('stale-after-hidden');
        }
    }

    function init() {
        try { window.addEventListener('cookbook:api-result',     onApiResult); } catch (e) {}
        try { window.addEventListener('cookbook:unauthorized',   onUnauthorized); } catch (e) {}
        try { window.addEventListener('cookbook:brokerLocked',   onBrokerLocked); } catch (e) {}
        try { window.addEventListener('cookbook:brokerUnlocked', onBrokerUnlocked); } catch (e) {}
        try { document.addEventListener('visibilitychange', onVisibilityChange); } catch (e) {}
        recompute('init');
        // Probe runs after DOMContentLoaded to give other defer
        // scripts (including boot.js's tokenStatus repaint) a chance
        // to settle first.
        if (document.readyState === 'complete' || document.readyState === 'interactive') {
            probeHealthOnce();
        } else {
            document.addEventListener('DOMContentLoaded', probeHealthOnce, { once: true });
        }
    }

    // Expose for diagnostics + verification harness. NOT for page
    // modules to call; pages remain decoupled.
    window.cookbookBrokerStatus = {
        observed: function () { return observed; },
        // Allow the verification harness (or devtools) to force a
        // recompute after manually mutating observed{}. Not used by
        // page modules.
        recompute: function () { recompute('external'); }
    };

    init();
})();
