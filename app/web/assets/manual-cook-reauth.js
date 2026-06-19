// PAX Cookbook -- Manual-cook step-up re-auth helper (X16C).
//
// Owns the browser-side Windows Hello / WebAuthn ceremony that turns a
// broker 401 'reAuthRequired' (opClass 'manualCook') into a single-use,
// recipe-bound authorization grant so one Bake can proceed. The Bake
// button flow (recipe-editor.js and recipe-list.js) calls reauth()
// inline and retries the cook exactly once on success. There is no
// path that fabricates success and no automatic retry loop.
//
// Endpoints (all token + CSRF gated; NOT lock-bypass):
//   GET  /api/v1/broker/webauthn/status              -> registered + credentialIds
//   POST /api/v1/broker/reauth/manual-cook/challenge -> { challenge, timeoutMs }
//   POST /api/v1/broker/reauth/manual-cook/verify    -> { ok:true } on grant
//
// Doctrine:
//   - navigator.credentials.get is the only credential API used. The
//     browser owns Windows Hello; the broker only verifies the ES256
//     assertion. The SPA never collects, hashes, or proxies the
//     Windows password or PIN.
//   - userVerification is always 'required'.
//   - Byte conversions are pure and never log or persist any payload.
//   - A user cancel (NotAllowedError / AbortError) is reported as a
//     bounded 'user_cancelled' reason, never as success.

(function () {
    'use strict';

    function webauthnSupported() {
        try {
            return !!(window.PublicKeyCredential &&
                      window.navigator &&
                      window.navigator.credentials &&
                      typeof window.navigator.credentials.get === 'function');
        } catch (e) {
            return false;
        }
    }

    // base64url (broker wire format) -> ArrayBuffer (WebAuthn API).
    function b64uToArrayBuffer(b64u) {
        var s = String(b64u || '').replace(/-/g, '+').replace(/_/g, '/');
        var pad = s.length % 4;
        if (pad === 2) { s += '=='; }
        else if (pad === 3) { s += '='; }
        else if (pad !== 0 && pad !== 1) { throw new Error('manualCook: invalid base64url length'); }
        var bin = window.atob(s);
        var out = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
        return out.buffer;
    }

    // ArrayBuffer (WebAuthn API) -> base64url (broker wire format).
    function arrayBufferToB64u(buf) {
        var bytes = (buf instanceof Uint8Array) ? buf : new Uint8Array(buf);
        var s = '';
        for (var i = 0; i < bytes.length; i++) { s += String.fromCharCode(bytes[i]); }
        return window.btoa(s).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
    }

    // Runs the full step-up ceremony for one recipe. Resolves to one of:
    //   { ok: true }
    //   { ok: false, reason: <stable-string>, status?: <int>, body?: <obj> }
    // Stable reasons: 'webauthn_unsupported', 'bad_recipe_id',
    //   'status_failed', 'no_credential', 'challenge_failed',
    //   'no_assertion', 'user_cancelled', 'navigator_get_failed:<name>',
    //   'verify_rejected', 'verify_network_error',
    //   'challenge_network_error', 'status_network_error'.
    // The promise never rejects: every failure is a resolved
    // { ok: false } so the caller cannot mistake an exception for a
    // started cook.
    function reauth(recipeId) {
        if (!webauthnSupported()) {
            return Promise.resolve({ ok: false, reason: 'webauthn_unsupported' });
        }
        if (!recipeId || typeof recipeId !== 'string') {
            return Promise.resolve({ ok: false, reason: 'bad_recipe_id' });
        }
        var api = window.cookbookApi;
        if (!api || typeof api.get !== 'function' || typeof api.post !== 'function') {
            return Promise.resolve({ ok: false, reason: 'status_failed' });
        }

        return api.get('/api/v1/broker/webauthn/status').then(function (stResp) {
            if (!stResp || !stResp.ok || !stResp.body) {
                return { ok: false, reason: 'status_failed', status: stResp ? stResp.status : 0, body: stResp ? stResp.body : null };
            }
            if (!stResp.body.registered) {
                return { ok: false, reason: 'no_credential', status: stResp.status, body: stResp.body };
            }
            var credentialIds = stResp.body.credentialIds || [];

            return api.post('/api/v1/broker/reauth/manual-cook/challenge', {}).then(function (chResp) {
                if (!chResp || !chResp.ok || !chResp.body || !chResp.body.challenge) {
                    return { ok: false, reason: 'challenge_failed', status: chResp ? chResp.status : 0, body: chResp ? chResp.body : null };
                }
                var challengeB64u = chResp.body.challenge;
                var allowCredentials = credentialIds.map(function (id) {
                    return { type: 'public-key', id: b64uToArrayBuffer(id), transports: ['internal'] };
                });
                var getOpts = {
                    publicKey: {
                        challenge:        b64uToArrayBuffer(challengeB64u),
                        allowCredentials: allowCredentials,
                        userVerification: 'required',
                        timeout:          (chResp.body.timeoutMs || 60000)
                        // rp.id intentionally omitted -- the browser
                        // defaults to the effective domain of the page
                        // origin (127.0.0.1 or localhost, depending on
                        // which the launcher opened).
                    }
                };
                return window.navigator.credentials.get(getOpts).then(function (assertion) {
                    if (!assertion || !assertion.response) {
                        return { ok: false, reason: 'no_assertion' };
                    }
                    var verifyBody = {
                        credentialId:      arrayBufferToB64u(assertion.rawId),
                        clientDataJSON:    arrayBufferToB64u(assertion.response.clientDataJSON),
                        authenticatorData: arrayBufferToB64u(assertion.response.authenticatorData),
                        signature:         arrayBufferToB64u(assertion.response.signature),
                        challenge:         challengeB64u,
                        recipeId:          recipeId
                    };
                    return api.post('/api/v1/broker/reauth/manual-cook/verify', verifyBody).then(function (vResp) {
                        if (vResp && vResp.ok && vResp.body && vResp.body.ok === true) {
                            return { ok: true };
                        }
                        return { ok: false, reason: 'verify_rejected', status: vResp ? vResp.status : 0, body: vResp ? vResp.body : null };
                    }, function () {
                        return { ok: false, reason: 'verify_network_error' };
                    });
                }, function (err) {
                    // navigator.credentials.get rejection. NotAllowedError
                    // and AbortError mean the operator dismissed or vetoed
                    // the Windows Security dialog; everything else is a
                    // technical failure. Neither path fabricates success.
                    var name = (err && err.name) ? err.name : 'unknown';
                    if (name === 'NotAllowedError' || name === 'AbortError') {
                        return { ok: false, reason: 'user_cancelled' };
                    }
                    return { ok: false, reason: 'navigator_get_failed:' + name };
                });
            }, function () {
                return { ok: false, reason: 'challenge_network_error' };
            });
        }, function () {
            return { ok: false, reason: 'status_network_error' };
        });
    }

    // Maps a bounded { ok:false } result to a short, operator-facing
    // sentence. Always states plainly that the bake did not start so a
    // failed step-up can never read as a silent success.
    function describe(result) {
        if (!result || result.ok) { return ''; }
        switch (result.reason) {
            case 'webauthn_unsupported':
                return 'This device can\'t confirm it\'s you in the app window. The bake did not start.';
            case 'no_credential':
                return 'You haven\'t set up identity confirmation yet. Unlock the app once to set it up, then try again. The bake did not start.';
            case 'user_cancelled':
                return 'The identity check was cancelled. The bake did not start.';
            case 'verify_rejected':
                return 'We couldn\'t confirm it\'s you. The bake did not start.';
            case 'status_failed':
            case 'status_network_error':
            case 'challenge_failed':
            case 'challenge_network_error':
            case 'verify_network_error':
                return 'Couldn\'t reach PAX Cookbook to confirm it\'s you. The bake did not start.';
            default:
                return 'The identity check couldn\'t be completed. The bake did not start.';
        }
    }

    window.cookbookManualCookReauth = {
        isSupported: webauthnSupported,
        reauth:      reauth,
        describe:    describe
    };
})();
