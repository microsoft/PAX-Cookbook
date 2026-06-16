// PAX Cookbook  thin HTTP helper.
//
// Reads the session token captured by boot.js from sessionStorage and
// attaches it as an Authorization: Bearer header on every request.
//
// Loaded as a classic deferred script; exposes a single global namespace
// window.cookbookApi with one method per HTTP verb that pages currently
// need. New verbs are added when a page genuinely needs them, not in
// advance.
//
// Return shape (every method): a Promise resolving to:
//     { ok, status, body, rawText, networkError }
//   ok            true iff HTTP status was 2xx
//   status        integer status code, or 0 on networkError
//   body          parsed JSON if Content-Type is application/json, else null
//   rawText       the response text (for surfacing in error UI)
//   networkError  null on HTTP success/failure; non-null only when fetch
//                 threw, the request timed out, or it was caller-cancelled
//
// Phase AE additions (operational resilience):
//   - Per-request AbortController with a bounded default timeout
//     (DEFAULT_TIMEOUT_MS). A hung broker can no longer leave the SPA
//     in indefinite "Loading...".
//   - Caller-supplied AbortSignal (opts.signal). Page modules pass a
//     per-mount signal here and abort it on teardown so pending
//     requests are released rather than running to completion against
//     an unmounted page.
//   - Every settled request announces itself via a window-scoped
//     CustomEvent so the topbar status pill (broker-status.js) can
//     surface truthful broker reachability without polling. Two
//     events are dispatched:
//        'cookbook:api-result'   detail = { ok, status, networkError,
//                                            timedOut, timestampUtc }
//        'cookbook:unauthorized' detail = { timestampUtc, unauthorizedReason }
//                                          (only on 401; unauthorizedReason
//                                          is the bounded broker-supplied
//                                          reason string forwarded verbatim,
//                                          or null if the body did not
//                                          carry one)
//     Caller-initiated cancellations (opts.signal aborted) do NOT
//     announce, because the page knows it cancelled itself and a
//     stale "unreachable" pulse would be a lie.
//
// No retry. No cache. No event bus subscription inside api.js. No
// interceptors. The events are FIRE-AND-FORGET signals; api.js never
// reads them back. Pages continue to consume the resolved Promise.
(function () {
    'use strict';

    var TOKEN_KEY          = 'cookbook.sessionToken';

    // Default per-request timeout. Bounded so the UI never hangs on a
    // broker that accepted the TCP but never replied. Pages with
    // legitimately long-running calls (none today; the broker is
    // synchronous and local) may override via opts.timeoutMs.
    var DEFAULT_TIMEOUT_MS = 30000;

    function getToken() {
        try {
            var t = sessionStorage.getItem(TOKEN_KEY);
            return (t && t.length > 0) ? t : null;
        } catch (e) {
            return null;
        }
    }

    function buildHeaders(opts) {
        var h = { 'Accept': 'application/json' };
        var token = getToken();
        if (token) { h['Authorization'] = 'Bearer ' + token; }
        if (opts && opts.stateful) { h['X-Cookbook-Request'] = '1'; }
        if (opts && opts.jsonBody)  { h['Content-Type']        = 'application/json'; }
        return h;
    }

    function mergeOpts(internal, caller) {
        var out = { stateful: false, jsonBody: false, signal: null, timeoutMs: DEFAULT_TIMEOUT_MS };
        if (internal) {
            if (typeof internal.stateful  === 'boolean') { out.stateful  = internal.stateful;  }
            if (typeof internal.jsonBody  === 'boolean') { out.jsonBody  = internal.jsonBody;  }
        }
        if (caller) {
            if (caller.signal)                          { out.signal    = caller.signal;    }
            if (typeof caller.timeoutMs === 'number')   { out.timeoutMs = caller.timeoutMs; }
        }
        return out;
    }

    function announceResult(detail) {
        try {
            window.dispatchEvent(new CustomEvent('cookbook:api-result', { detail: detail }));
        } catch (e) {}
        if (detail.status === 401) {
            try {
                // Phase AH.C3 -- forward the bounded broker-supplied
                // reason vocab (e.g. 'session_token_not_recognized')
                // so broker-status.js can surface the truth in the
                // pill title. The SPA never invents reasons; if the
                // broker did not provide one (older brokers, network
                // proxy stripped the body), the field is null and
                // the pill falls back to its generic 401 wording.
                window.dispatchEvent(new CustomEvent('cookbook:unauthorized', {
                    detail: {
                        timestampUtc:       detail.timestampUtc,
                        unauthorizedReason: detail.unauthorizedReason || null
                    }
                }));
            } catch (e) {}
        }
    }

    // Phase AF: broker lock-state + per-op re-auth events. These are
    // dispatched ONLY for response bodies whose code field carries the
    // documented sentinel. Pages never have to inspect 401/423
    // response bodies themselves; the lock-overlay module owns the
    // entire UI response.
    //
    //   'cookbook:brokerLocked'   detail = { code, message, attemptedMethod, attemptedPath, timestampUtc }
    //   'cookbook:reAuthRequired' detail = { code, opClass, verificationResult, message, timestampUtc }
    //
    // The detail shape mirrors the broker's response body 1:1 plus a
    // timestampUtc for the overlay to display.
    function announceAuthChallenge(status, body, timestampUtc) {
        if (!body || typeof body !== 'object' || typeof body.code !== 'string') { return; }
        if (status === 423 && body.code === 'brokerLocked') {
            try {
                window.dispatchEvent(new CustomEvent('cookbook:brokerLocked', {
                    detail: {
                        code:            body.code,
                        message:         body.message         || null,
                        attemptedMethod: body.attemptedMethod || null,
                        attemptedPath:   body.attemptedPath   || null,
                        timestampUtc:    timestampUtc
                    }
                }));
            } catch (e) {}
            return;
        }
        if (status === 401 && body.code === 'reAuthRequired') {
            try {
                window.dispatchEvent(new CustomEvent('cookbook:reAuthRequired', {
                    detail: {
                        code:               body.code,
                        opClass:            body.opClass            || null,
                        verificationResult: body.verificationResult || null,
                        message:            body.message            || null,
                        timestampUtc:       timestampUtc
                    }
                }));
            } catch (e) {}
            return;
        }
        // Phase 13 Stage 4 -- acquisition gate refusal. Dispatched
        // for HTTP 409 responses whose body.code is
        // 'acquisitionRequired' so pax-engine-overlay.js can render
        // the engine-acquisition flow without each call site having
        // to inspect 409 bodies. detail.details is the full state
        // snapshot from Get-PaxAcquisitionState (policy, state,
        // pending, source, version, sha256, manifest fields,
        // canonicalScript* fields, installStatePath) which the
        // overlay's support-info expander renders verbatim.
        if (status === 409 && body.code === 'acquisitionRequired') {
            try {
                window.dispatchEvent(new CustomEvent('cookbook:acquisitionRequired', {
                    detail: {
                        code:             body.code,
                        endpoint:         body.endpoint         || null,
                        state:            body.state            || null,
                        isLegacyEmbedded: !!body.isLegacyEmbedded,
                        message:          body.message          || null,
                        details:          body.details          || null,
                        timestampUtc:     timestampUtc
                    }
                }));
            } catch (e) {}
            return;
        }
    }

    // Wire timeout + caller-signal linkage to a fresh internal
    // AbortController. Returns { controller, dispose, didTimeout() }.
    // dispose() must be called from BOTH the success and failure paths
    // so the timeout never fires after the request has settled.
    function newAbortPlumbing(callerSignal, timeoutMs) {
        var hasController = (typeof AbortController === 'function');
        if (!hasController) {
            return {
                controller:  null,
                dispose:     function () {},
                didTimeout:  function () { return false; }
            };
        }
        var ctrl = new AbortController();
        var timedOut = false;
        var handle = null;
        if (timeoutMs && timeoutMs > 0) {
            handle = setTimeout(function () {
                timedOut = true;
                try { ctrl.abort(); } catch (e) {}
            }, timeoutMs);
        }
        var onCallerAbort = null;
        if (callerSignal) {
            if (callerSignal.aborted) {
                try { ctrl.abort(); } catch (e) {}
            } else {
                onCallerAbort = function () {
                    try { ctrl.abort(); } catch (e) {}
                };
                try { callerSignal.addEventListener('abort', onCallerAbort); } catch (e) {}
            }
        }
        return {
            controller: ctrl,
            dispose: function () {
                if (handle !== null) {
                    try { clearTimeout(handle); } catch (e) {}
                    handle = null;
                }
                if (onCallerAbort && callerSignal) {
                    try { callerSignal.removeEventListener('abort', onCallerAbort); } catch (e) {}
                    onCallerAbort = null;
                }
            },
            didTimeout: function () { return timedOut; }
        };
    }

    function execute(method, path, opts, body) {
        var merged = mergeOpts(opts, opts);
        // Internal opts (stateful/jsonBody) live on opts; caller opts
        // (signal/timeoutMs) ALSO live on opts. mergeOpts treats both as
        // the same object intentionally -- there is no second arg.
        var plumb = newAbortPlumbing(merged.signal, merged.timeoutMs);
        var req = {
            method:      method,
            headers:     buildHeaders(merged),
            credentials: 'omit',
            cache:       'no-store'
        };
        if (plumb.controller) { req.signal = plumb.controller.signal; }
        if (body !== undefined && body !== null) {
            req.body = (typeof body === 'string') ? body : JSON.stringify(body);
        }
        return fetch(path, req).then(function (resp) {
            plumb.dispose();
            return resp.text().then(function (text) {
                var ct   = resp.headers.get('Content-Type') || '';
                var parsed = null;
                if (ct.indexOf('application/json') === 0 && text.length > 0) {
                    try { parsed = JSON.parse(text); } catch (e) { parsed = null; }
                }
                var result = {
                    ok:           resp.ok,
                    status:       resp.status,
                    body:         parsed,
                    rawText:      text,
                    networkError: null
                };
                var nowIso = new Date().toISOString();
                // Phase AH.C3 -- extract the bounded broker-supplied
                // reason from a 401 body so broker-status.js can show
                // it in the pill title hint. The reason vocabulary is
                // owned by the broker; the SPA only forwards what it
                // received.
                var unauthorizedReason = null;
                if (resp.status === 401 && parsed && typeof parsed.reason === 'string') {
                    unauthorizedReason = parsed.reason;
                }
                announceResult({
                    ok:                 result.ok,
                    status:             result.status,
                    networkError:       null,
                    timedOut:           false,
                    timestampUtc:       nowIso,
                    unauthorizedReason: unauthorizedReason
                });
                announceAuthChallenge(result.status, parsed, nowIso);
                return result;
            });
        }).catch(function (err) {
            plumb.dispose();
            var didTimeout = plumb.didTimeout();
            var callerCancelled = !!(merged.signal && merged.signal.aborted && !didTimeout);
            var msg;
            if (didTimeout) {
                msg = 'request timed out after ' + Math.floor((merged.timeoutMs || 0) / 1000) + 's';
            } else if (callerCancelled) {
                msg = 'request cancelled';
            } else if (err && err.name === 'AbortError') {
                msg = 'request aborted';
            } else {
                msg = (err && err.message) ? err.message : String(err);
            }
            var result = {
                ok:           false,
                status:       0,
                body:         null,
                rawText:      '',
                networkError: msg
            };
            // Caller-initiated cancellations do not signal broker
            // reachability -- the page intentionally walked away. All
            // other failure modes (real network error, fetch threw,
            // timeout) are honest broker-reachability evidence.
            if (!callerCancelled) {
                announceResult({
                    ok:           false,
                    status:       0,
                    networkError: msg,
                    timedOut:     didTimeout,
                    timestampUtc: new Date().toISOString()
                });
            }
            return result;
        });
    }

    window.cookbookApi = {
        hasToken: function () { return getToken() !== null; },
        get:      function (path, callerOpts) {
            return execute('GET',  path, mergeOpts({ stateful: false }, callerOpts));
        },
        post:     function (path, body, callerOpts) {
            return execute('POST', path, mergeOpts({ stateful: true, jsonBody: true }, callerOpts), body);
        },
        put:      function (path, body, callerOpts) {
            return execute('PUT',  path, mergeOpts({ stateful: true, jsonBody: true }, callerOpts), body);
        },
        // del: bearer-authenticated DELETE for the bounded set of
        // routes that genuinely remove server-side state (Phase AF:
        // auth profile delete, secret remove). The body argument is
        // optional and is sent only if non-null -- broker DELETE
        // handlers do not require a body but the API helper supports
        // it so a caller never has to drop down to fetch() to add
        // one. Stateful is implicit; broker dispatch treats any
        // mutating verb as stateful.
        del:      function (path, body, callerOpts) {
            return execute('DELETE', path, mergeOpts({ stateful: true, jsonBody: (body !== undefined && body !== null) }, callerOpts), body);
        },
        // postBytes: bearer-authenticated POST that sends a raw byte
        // body (ArrayBuffer or Uint8Array) WITHOUT JSON serialization
        // and WITHOUT mutating the bytes in any way. Used by the
        // first-run acquisition overlay to upload the operator's
        // selected PAX script as application/octet-stream so the
        // broker can run its signed-manifest + approved-only +
        // SHA-256 chain against the bytes the user actually picked.
        // Caller supplies the Content-Type explicitly (no implicit
        // application/json) and may add extra request headers
        // (e.g. X-PAX-Filename, X-PAX-Client-SHA256) via the
        // extraHeaders argument; Bearer + X-Cookbook-Request (CSRF)
        // are added on top by buildHeaders() with stateful:true.
        // Returns the same shape as get/post/put.
        postBytes: function (path, bytes, contentType, extraHeaders, callerOpts) {
            var merged = mergeOpts({ stateful: true }, callerOpts);
            var plumb = newAbortPlumbing(merged.signal, merged.timeoutMs);
            var headers = buildHeaders(merged);
            if (typeof contentType === 'string' && contentType.length > 0) {
                headers['Content-Type'] = contentType;
            }
            if (extraHeaders && typeof extraHeaders === 'object') {
                for (var k in extraHeaders) {
                    if (Object.prototype.hasOwnProperty.call(extraHeaders, k)) {
                        var v = extraHeaders[k];
                        if (v !== undefined && v !== null) {
                            headers[k] = String(v);
                        }
                    }
                }
            }
            var req = {
                method:      'POST',
                headers:     headers,
                credentials: 'omit',
                cache:       'no-store',
                body:        bytes
            };
            if (plumb.controller) { req.signal = plumb.controller.signal; }
            return fetch(path, req).then(function (resp) {
                plumb.dispose();
                return resp.text().then(function (text) {
                    var ct = resp.headers.get('Content-Type') || '';
                    var parsed = null;
                    if (ct.indexOf('application/json') === 0 && text.length > 0) {
                        try { parsed = JSON.parse(text); } catch (e) { parsed = null; }
                    }
                    var result = {
                        ok:           resp.ok,
                        status:       resp.status,
                        body:         parsed,
                        rawText:      text,
                        networkError: null
                    };
                    var nowIso = new Date().toISOString();
                    var unauthorizedReason = null;
                    if (resp.status === 401 && parsed && typeof parsed.reason === 'string') {
                        unauthorizedReason = parsed.reason;
                    }
                    announceResult({
                        ok:                 result.ok,
                        status:             result.status,
                        networkError:       null,
                        timedOut:           false,
                        timestampUtc:       nowIso,
                        unauthorizedReason: unauthorizedReason
                    });
                    announceAuthChallenge(result.status, parsed, nowIso);
                    return result;
                });
            }).catch(function (err) {
                plumb.dispose();
                var didTimeout = plumb.didTimeout();
                var callerCancelled = !!(merged.signal && merged.signal.aborted && !didTimeout);
                var msg;
                if (didTimeout) {
                    msg = 'request timed out after ' + Math.floor((merged.timeoutMs || 0) / 1000) + 's';
                } else if (callerCancelled) {
                    msg = 'request cancelled';
                } else if (err && err.name === 'AbortError') {
                    msg = 'request aborted';
                } else {
                    msg = (err && err.message) ? err.message : String(err);
                }
                var result = {
                    ok:           false,
                    status:       0,
                    body:         null,
                    rawText:      '',
                    networkError: msg
                };
                if (!callerCancelled) {
                    announceResult({
                        ok:           false,
                        status:       0,
                        networkError: msg,
                        timedOut:     didTimeout,
                        timestampUtc: new Date().toISOString()
                    });
                }
                return result;
            });
        },
        // getBlob: bearer-authenticated GET that resolves to a Blob plus
        // a server-supplied filename parsed from Content-Disposition.
        // Used only for the bounded export surfaces (Phase V: bundled
        // PAX script download). Returns:
        //     { ok, status, blob, filename, rawText, networkError }
        // - blob:     the response body as a Blob on success, else null
        // - filename: the filename= value from Content-Disposition, or
        //             null if the header is absent / unparseable
        // - rawText:  populated only on non-2xx so error UIs can show
        //             the server's plain-text or JSON error body
        // No retry. No cache. No interceptors. Mirrors the discipline
        // of the other verbs. Honors opts.signal and opts.timeoutMs.
        getBlob: function (path, callerOpts) {
            var merged = mergeOpts({ stateful: false }, callerOpts);
            var plumb = newAbortPlumbing(merged.signal, merged.timeoutMs);
            var req = {
                method:      'GET',
                headers:     buildHeaders(merged),
                credentials: 'omit',
                cache:       'no-store'
            };
            if (plumb.controller) { req.signal = plumb.controller.signal; }
            return fetch(path, req).then(function (resp) {
                plumb.dispose();
                if (!resp.ok) {
                    return resp.text().then(function (text) {
                        var result = {
                            ok:           false,
                            status:       resp.status,
                            blob:         null,
                            filename:     null,
                            rawText:      text,
                            networkError: null
                        };
                        announceResult({
                            ok:           false,
                            status:       resp.status,
                            networkError: null,
                            timedOut:     false,
                            timestampUtc: new Date().toISOString()
                        });
                        return result;
                    });
                }
                return resp.blob().then(function (blob) {
                    var cd = resp.headers.get('Content-Disposition') || '';
                    // Match: filename="..." with the contents anchored
                    // to a single attribute. The broker always quotes
                    // the value, so we accept the quoted form only.
                    var m = /filename\s*=\s*"([^"]+)"/i.exec(cd);
                    var filename = (m && m[1]) ? m[1] : null;
                    var result = {
                        ok:           true,
                        status:       resp.status,
                        blob:         blob,
                        filename:     filename,
                        rawText:      '',
                        networkError: null
                    };
                    announceResult({
                        ok:           true,
                        status:       resp.status,
                        networkError: null,
                        timedOut:     false,
                        timestampUtc: new Date().toISOString()
                    });
                    return result;
                });
            }).catch(function (err) {
                plumb.dispose();
                var didTimeout = plumb.didTimeout();
                var callerCancelled = !!(merged.signal && merged.signal.aborted && !didTimeout);
                var msg;
                if (didTimeout) {
                    msg = 'request timed out after ' + Math.floor((merged.timeoutMs || 0) / 1000) + 's';
                } else if (callerCancelled) {
                    msg = 'request cancelled';
                } else if (err && err.name === 'AbortError') {
                    msg = 'request aborted';
                } else {
                    msg = (err && err.message) ? err.message : String(err);
                }
                var result = {
                    ok:           false,
                    status:       0,
                    blob:         null,
                    filename:     null,
                    rawText:      '',
                    networkError: msg
                };
                if (!callerCancelled) {
                    announceResult({
                        ok:           false,
                        status:       0,
                        networkError: msg,
                        timedOut:     didTimeout,
                        timestampUtc: new Date().toISOString()
                    });
                }
                return result;
            });
        }
    };
})();
