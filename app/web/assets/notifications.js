// PAX Cookbook -- In-app notification toasts/banners (V1 Notifications N4).
//
// Surfaces the durable JSONL notification log in the web UI. The broker
// route GET /api/v1/notifications replays the records written by the
// notification core (manual-bake supervisor and scheduled-bake reconcile
// funnels) for the current UTC day; this module renders them as accessible
// in-app toasts (bake completed) and persistent banners (bake failed /
// stopped).
//
// Hard contract -- best-effort and non-blocking:
//   - This module is PURELY PRESENTATIONAL. Every entry point is wrapped so
//     a failure here can never throw into, block, or fail bake
//     finalization, scheduled reconcile, cook history, broker startup, or
//     app navigation. On any error it silently does nothing.
//   - The JSONL (replayed via the local broker endpoint) is the single
//     source of truth. This module never writes anything back: there is no
//     acknowledge endpoint, no history page, no persistence. Dismiss is
//     session-local DOM removal only.
//
// Privacy / safe rendering:
//   - All text is placed via textContent / createTextNode -- NEVER
//     innerHTML. Operator-authored values (recipeName) are therefore inert.
//   - Only three fields are ever shown: a fixed status label, the recipe
//     name, and the fixed message string. Paths, URLs, IDs, tenant/user,
//     auth/token/secret material, stack traces, command lines, exit codes,
//     and every other field are never rendered.
//
// Accessibility:
//   - completed/info  -> role="status", aria-live="polite"   (transient)
//   - errored/stopped -> role="alert",  aria-live="assertive" (persistent)
//   - Each notification has a keyboard-focusable dismiss button with a
//     visible focus ring (CSS) and a descriptive aria-label.
//   - Status is conveyed by a text label ("Completed" / "Failed" /
//     "Stopped"), never by color alone.
//
// Delivery channel: JSONL replay only. Live WebSocket push is intentionally
// deferred (see the N4 report) -- the existing per-cook WebSocket carries
// cook-scoped stdout/stderr/finished semantics and adding a global
// notification frame would touch shared transport behavior. Replay from the
// durable log fully satisfies this slice with no transport risk.

(function () {
    'use strict';

    var REGION_ID    = 'cookbook-notifications';
    var ENDPOINT     = '/api/v1/notifications';
    var AUTO_DISMISS_MS = 6000;   // transient (completed) auto-dismiss window

    // eventIds already rendered in this browser session. Prevents a double
    // render if init() is ever called more than once. Session-local only;
    // never persisted.
    var shownEventIds = Object.create(null);

    // Map a notification status to its presentation. Status is the
    // authoritative driver; severity from the record is used only as a
    // fallback for the visual kind. Unknown statuses are not rendered.
    function presentationFor(status, severity) {
        if (status === 'completed') {
            return { label: 'Completed', kind: 'info',    persistent: false, assertive: false };
        }
        if (status === 'errored') {
            return { label: 'Failed',    kind: 'error',   persistent: true,  assertive: true };
        }
        if (status === 'interrupted') {
            return { label: 'Stopped',   kind: 'warning', persistent: true,  assertive: true };
        }
        // Defensive fallback on severity only; still requires a known
        // severity to render anything.
        if (severity === 'error')   { return { label: 'Failed',  kind: 'error',   persistent: true,  assertive: true }; }
        if (severity === 'warning') { return { label: 'Stopped', kind: 'warning', persistent: true,  assertive: true }; }
        if (severity === 'info')    { return { label: 'Completed', kind: 'info',  persistent: false, assertive: false }; }
        return null;
    }

    // Resolve (or lazily create) the fixed positioning region appended to
    // <body>. The region itself is a passive container; each toast carries
    // its own live-region role so screen readers announce per-event.
    function ensureRegion() {
        var region = document.getElementById(REGION_ID);
        if (region) { return region; }
        if (!document.body) { return null; }
        region = document.createElement('div');
        region.id = REGION_ID;
        region.className = 'notifications-region';
        // The container is decorative chrome; individual toasts own the
        // semantics. Hide the empty container from the a11y tree.
        region.setAttribute('aria-hidden', 'false');
        document.body.appendChild(region);
        return region;
    }

    // Remove a single notification node. Idempotent; safe to call from the
    // auto-dismiss timer and the dismiss button both.
    function dismiss(node, timerId) {
        try {
            if (timerId) { clearTimeout(timerId); }
            if (node && node.parentNode) { node.parentNode.removeChild(node); }
        } catch (e) { /* best-effort */ }
    }

    // Build one notification node from a record using only safe DOM APIs.
    function buildNotificationNode(record) {
        var status   = (record && typeof record.status === 'string')   ? record.status   : null;
        var severity = (record && typeof record.severity === 'string') ? record.severity : null;
        var present  = presentationFor(status, severity);
        if (!present) { return null; }

        var card = document.createElement('div');
        card.className = 'notification notification-' + present.kind;
        card.setAttribute('role', present.assertive ? 'alert' : 'status');
        card.setAttribute('aria-live', present.assertive ? 'assertive' : 'polite');

        var body = document.createElement('div');
        body.className = 'notification-body';

        // Status label -- text, never color-only.
        var label = document.createElement('span');
        label.className = 'notification-label';
        label.appendChild(document.createTextNode(present.label));
        body.appendChild(label);

        // Recipe name -- operator-authored; placed as inert text.
        var recipeName = (record && typeof record.recipeName === 'string' && record.recipeName.length > 0)
            ? record.recipeName : null;
        if (recipeName) {
            var nameEl = document.createElement('span');
            nameEl.className = 'notification-recipe';
            nameEl.appendChild(document.createTextNode(recipeName));
            body.appendChild(nameEl);
        }

        // Message -- fixed broker-authored string from the record.
        var message = (record && typeof record.message === 'string' && record.message.length > 0)
            ? record.message : null;
        if (message) {
            var msgEl = document.createElement('p');
            msgEl.className = 'notification-message';
            msgEl.appendChild(document.createTextNode(message));
            body.appendChild(msgEl);
        }

        card.appendChild(body);

        // Keyboard-focusable dismiss button.
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'notification-dismiss';
        var dismissText = 'Dismiss ' + present.label.toLowerCase() + ' notification';
        if (recipeName) { dismissText += ' for ' + recipeName; }
        btn.setAttribute('aria-label', dismissText);
        btn.appendChild(document.createTextNode('\u00D7'));   // multiplication sign as a close glyph
        card.appendChild(btn);

        var timerId = null;
        btn.addEventListener('click', function () { dismiss(card, timerId); });

        // Transient (completed/info) notifications auto-dismiss; persistent
        // (failed/stopped) notifications stay until the operator dismisses.
        if (!present.persistent) {
            timerId = setTimeout(function () { dismiss(card, timerId); }, AUTO_DISMISS_MS);
        }

        return card;
    }

    // Render a list of records into the region (oldest first so the newest
    // sits at the top of the visual stack via CSS column-reverse).
    function render(records) {
        if (!Array.isArray(records) || records.length === 0) { return; }
        var region = ensureRegion();
        if (!region) { return; }
        for (var i = 0; i < records.length; i++) {
            var rec = records[i];
            if (!rec || typeof rec !== 'object') { continue; }
            var eventId = (typeof rec.eventId === 'string') ? rec.eventId : null;
            if (eventId) {
                if (shownEventIds[eventId]) { continue; }
                shownEventIds[eventId] = true;
            }
            var node = buildNotificationNode(rec);
            if (node) { region.appendChild(node); }
        }
    }

    // Fetch today's notifications from the local broker and render them.
    // Best-effort: any failure (no API helper, network error, non-2xx,
    // malformed body) results in nothing being shown.
    function loadAndRender() {
        try {
            if (!window.cookbookApi || typeof window.cookbookApi.get !== 'function') { return; }
            if (typeof window.cookbookApi.hasToken === 'function' && !window.cookbookApi.hasToken()) {
                return;   // not signed in yet; nothing to replay
            }
            window.cookbookApi.get(ENDPOINT).then(function (result) {
                try {
                    if (!result || !result.ok || !result.body) { return; }
                    var list = result.body.notifications;
                    render(list);
                } catch (eInner) { /* best-effort */ }
            }).catch(function () { /* best-effort: swallow */ });
        } catch (eOuter) { /* best-effort */ }
    }

    function init() {
        try { loadAndRender(); } catch (e) { /* best-effort */ }
    }

    function teardown() {
        try {
            var region = document.getElementById(REGION_ID);
            if (region && region.parentNode) { region.parentNode.removeChild(region); }
        } catch (e) { /* best-effort */ }
    }

    // Public surface (small; mainly for testability). The module
    // self-initializes on DOM ready; nothing else needs to call init().
    window.cookbookNotifications = {
        init:     init,
        teardown: teardown
    };

    // Global mount: run once after the DOM is parsed, independent of the
    // hash router so the notifications surface is present on every route
    // without coupling to any page module's lifecycle.
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }
})();
