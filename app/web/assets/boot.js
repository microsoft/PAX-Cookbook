// Bootstrap: capture the URL-fragment session token into sessionStorage and
// strip ONLY the token segment from the address bar before any further code
// runs. Any non-token route fragment present is preserved verbatim so that
// direct-link hand-offs (#/recipes/new&t=TOKEN, #/recipes/<id>&t=TOKEN) land
// the router on the intended route after the token is captured.
//
// Loaded as a classic synchronous script in <head>. By the time this IIFE
// returns, the URL has been rewritten and any subsequent code (or the user
// reading the address bar) cannot observe the token in window.location.
//
// UX-1H6 -- origin guard: Chrome and Edge reject 127.0.0.1 as a valid
// WebAuthn effective domain ("SecurityError: This is an invalid domain")
// before opening Windows Hello. The launcher steers the browser to the
// localhost form, but any operator who bookmarked or typed the 127.0.0.1
// URL directly would still land on an origin that breaks the unlock
// ceremony. The very first thing this script does is detect that case
// and history.replace(...) over to the localhost form on the same port
// while preserving the path, query, and hash (so the token fragment
// survives the redirect and is captured by the IIFE on the next load).
// Only the exact string "127.0.0.1" triggers the redirect; "localhost"
// and any other hostname are left untouched.
//
// Two accepted launcher hash shapes:
//   #t=TOKEN              -- launcher hand-off with no route (default route)
//   #<route>&t=TOKEN      -- launcher hand-off including a route fragment
//
// Anything else is treated as a pure route fragment with no token: the URL
// is left untouched and the router resolves it on its own. The token is
// never persisted to the URL bar.
//
// Scope is intentionally narrow: no globals beyond what the IIFE creates,
// no app initialization, no network calls, no router coupling.
(function () {
    'use strict';

    // UX-1H6 origin canonicalization. Runs BEFORE token capture so the
    // single round-trip survives the redirect: the launcher hands us
    // http://localhost:<port>/#t=TOKEN directly; this guard is only
    // hit if someone navigates to the 127.0.0.1 form on their own.
    try {
        if (window.location.hostname === '127.0.0.1') {
            var loc = window.location;
            var target =
                loc.protocol +
                '//localhost' +
                (loc.port ? ':' + loc.port : '') +
                loc.pathname +
                loc.search +
                loc.hash;
            window.location.replace(target);
            return;
        }
    } catch (originErr) {
        // If location is unreachable for any reason, fall through and
        // let the rest of the IIFE run. The lock-overlay surfaces a
        // diagnostic if the origin remains 127.0.0.1.
    }

    var TOKEN_KEY = 'cookbook.sessionToken';
    var STATUS_ID = 'token-status';
    var status    = 'No token';

    // PRIMARY token source: server-side injection. The broker's static
    // handler embeds an inline <script> in <head> that exposes the
    // active SessionToken as window.__cookbookBootstrapToken. This
    // path is authoritative because it does not depend on the URL
    // fragment surviving Chromium's --app= mode rewrites or on any
    // address-bar manipulation. We adopt it immediately, persist it
    // to sessionStorage, then scrub the global and remove the
    // injected <script> so the token is no longer reachable from
    // page-level introspection.
    try {
        var injected = (typeof window.__cookbookBootstrapToken === 'string')
            ? window.__cookbookBootstrapToken : null;
        if (injected && injected.length > 0) {
            sessionStorage.setItem(TOKEN_KEY, injected);
            status = 'Connected';
        }
        try { delete window.__cookbookBootstrapToken; } catch (eDel) {
            try { window.__cookbookBootstrapToken = null; } catch (eNull) {}
        }
        var injectedEl = document.getElementById('cookbook-token-bootstrap');
        if (injectedEl && injectedEl.parentNode) {
            injectedEl.parentNode.removeChild(injectedEl);
        }
    } catch (injectedErr) {
        // Fall through to fragment / sessionStorage paths.
    }

    try {
        var hash = window.location.hash;
        if (hash && hash.length > 1) {
            var raw = hash.charAt(0) === '#' ? hash.substring(1) : hash;

            // Split the hash into a route portion and an optional token
            // portion using a single specific separator. No general-purpose
            // query parsing; the only key recognised here is 't'.
            var routePart  = raw;
            var tokenValue = null;

            var sep = raw.indexOf('&t=');
            if (sep >= 0) {
                routePart  = raw.substring(0, sep);
                tokenValue = raw.substring(sep + 3);
            } else if (raw.indexOf('t=') === 0) {
                routePart  = '';
                tokenValue = raw.substring(2);
            }

            if (tokenValue !== null) {
                if (tokenValue.length > 0) {
                    sessionStorage.setItem(TOKEN_KEY, tokenValue);
                    status = 'Connected';
                }
                // Rewrite the URL to drop the token segment while
                // preserving any route fragment that was present.
                var newHash = routePart.length > 0 ? '#' + routePart : '';
                history.replaceState({}, '', window.location.pathname + newHash);
            }
            // else: no token segment in this hash. Leave the URL bar
            // alone -- the router will resolve the route on its own.
        } else {
            // No hash on this load. Surface an existing token from a prior
            // load in this tab if present.
            var existing = sessionStorage.getItem(TOKEN_KEY);
            if (existing && existing.length > 0) {
                status = 'Connected';
            }
        }
    } catch (err) {
        status = 'Sign-in error';
    }

    // Update the status indicator after DOM parse. This script runs in <head>
    // without 'defer', so the body element does not exist yet here.
    function updateStatus() {
        var el = document.getElementById(STATUS_ID);
        if (!el) { return; }
        el.textContent = status;
        // The pill's colour hook is the className: 'ok' renders green,
        // 'miss' renders red. 'Connected' is the only positive state
        // boot.js can assert at this point; every other label here
        // (no token, sign-in error, etc.) is a genuine problem and
        // stays red. broker-status.js takes over from here once
        // /api/v1/health or any other fetch settles, and may promote
        // the pill to 'Connected' on its own evidence.
        el.className = (status === 'Connected') ? 'ok' : 'miss';
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', updateStatus, { once: true });
    } else {
        updateStatus();
    }
})();
