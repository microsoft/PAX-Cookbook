// Top-level Work-account profile control (EXPERIMENTAL surface, driven by native).
//
// This module renders a fixed-size avatar + menu in the TOP-LEVEL legacy shell
// topbar. It exists in every build, but it only ever mounts a visible surface
// when the NATIVE host posts a bounded Work-account profile presentation envelope
// AND the selected session provider is the work account AND the app is Unlocked.
// A stable/default build never posts that envelope, so this control renders
// nothing and leaves no layout gap.
//
// Containment doctrine (binding):
//   - The photo arrives ONLY as a bounded, closed-shape native envelope. The
//     receiver revalidates it exactly (message type, allowed state, closed field
//     set, image/jpeg content type, encoded-size ceiling, successful in-browser
//     image decode) and rejects any unknown field, wrong type, oversized payload,
//     or undecodable image.
//   - The photo bytes are rendered in the TOP-LEVEL document only. They are NEVER
//     forwarded to the React iframe and are NEVER written to localStorage,
//     sessionStorage, IndexedDB, or the Cache API. The only in-memory handle is a
//     Blob object URL, which is revoked the instant the surface clears or the
//     image is replaced.
//   - The menu reuses the EXISTING authority: "Lock PAX Cookbook" locks via the
//     existing lock route + lock overlay; "Use a different work account" couriers
//     a bounded intent to the native host and waits for its ack; "Sign-in method"
//     navigates to Settings and reveals the existing React Sign-in method section.
//     None of these actions carries a photo or identity value.
//
// The module is authored UMD-style so the pure receiver/controller can be unit-
// tested under node + jsdom while still attaching to the browser window.
(function (root, factory) {
    'use strict';
    var api = factory();
    if (typeof module !== 'undefined' && module.exports) {
        module.exports = api;
    }
    if (root) {
        root.cookbookWorkAccountProfile = api;
    }
})(typeof window !== 'undefined' ? window : this, function () {
    'use strict';

    var MESSAGE_TYPE = 'cookbook:work-account-profile';
    var LABEL_TEXT = 'Work account';
    var ACCEPTED_CONTENT_TYPE = 'image/jpeg';

    // Encoded-size ceiling: the base64 length of the raw 2 MiB fetch ceiling.
    // Mirrors the native WorkAccountProfileWindowChannel.MaxEncodedChars so the
    // shell never accepts a larger encoding than the native side would ever post.
    var MAX_RAW_BYTES = 2 * 1024 * 1024;
    var MAX_ENCODED_CHARS = Math.ceil(MAX_RAW_BYTES / 3) * 4;

    var BASE64_RE = /^[A-Za-z0-9+/]*={0,2}$/;

    function hasExactKeys(obj, allowed) {
        var keys = Object.keys(obj);
        if (keys.length !== allowed.length) {
            return false;
        }
        for (var i = 0; i < keys.length; i++) {
            if (allowed.indexOf(keys[i]) === -1) {
                return false;
            }
        }
        return true;
    }

    // Synchronous, pure structural revalidation of a native profile envelope.
    // Returns one of:
    //   { ok: true, kind: 'photo', contentType, imageBase64 }
    //   { ok: true, kind: 'initials', initials }
    //   { ok: true, kind: 'none' }
    //   { ok: false }
    // It rejects any unknown field, wrong message type, disallowed state, wrong
    // content type, non-base64 or over-ceiling image, or malformed initials.
    function validateEnvelope(data) {
        if (!data || typeof data !== 'object' || Array.isArray(data)) {
            return { ok: false };
        }
        if (data.type !== MESSAGE_TYPE) {
            return { ok: false };
        }
        // Every native envelope (photo, initials, and the 'none' clear) carries the
        // fixed label. A missing or renamed label is a malformed envelope.
        if (data.label !== LABEL_TEXT) {
            return { ok: false };
        }

        var state = data.state;
        if (state === 'photo') {
            if (!hasExactKeys(data, ['type', 'state', 'contentType', 'imageBase64', 'label'])) {
                return { ok: false };
            }
            if (data.contentType !== ACCEPTED_CONTENT_TYPE) {
                return { ok: false };
            }
            if (typeof data.imageBase64 !== 'string' ||
                data.imageBase64.length === 0 ||
                data.imageBase64.length > MAX_ENCODED_CHARS ||
                !BASE64_RE.test(data.imageBase64)) {
                return { ok: false };
            }
            return { ok: true, kind: 'photo', contentType: data.contentType, imageBase64: data.imageBase64 };
        }

        if (state === 'initials') {
            if (!hasExactKeys(data, ['type', 'state', 'initials', 'label'])) {
                return { ok: false };
            }
            if (typeof data.initials !== 'string' ||
                data.initials.length < 1 ||
                data.initials.length > 2) {
                return { ok: false };
            }
            return { ok: true, kind: 'initials', initials: data.initials };
        }

        if (state === 'none') {
            if (!hasExactKeys(data, ['type', 'state', 'label'])) {
                return { ok: false };
            }
            return { ok: true, kind: 'none' };
        }

        return { ok: false };
    }

    // Default in-browser image decoder. Converts the bounded base64 into a Blob,
    // proves it decodes as a real image, and returns a revocable object URL. Any
    // decode failure resolves to null so the caller degrades to initials/nothing.
    // Never touches storage.
    function defaultDecodeImage(imageBase64, contentType) {
        return new Promise(function (resolve) {
            var url = null;
            try {
                var binary = atob(imageBase64);
                var len = binary.length;
                var buffer = new Uint8Array(len);
                for (var i = 0; i < len; i++) {
                    buffer[i] = binary.charCodeAt(i);
                }
                var blob = new Blob([buffer], { type: contentType });
                url = URL.createObjectURL(blob);
                var img = new Image();
                img.onload = function () { resolve(url); };
                img.onerror = function () {
                    try { URL.revokeObjectURL(url); } catch (e) {}
                    resolve(null);
                };
                img.src = url;
            } catch (e) {
                if (url) { try { URL.revokeObjectURL(url); } catch (e2) {} }
                resolve(null);
            }
        });
    }

    // Creates the topbar profile controller. All environment seams (document,
    // decoder, menu-action callbacks) are injected so the controller is unit-
    // testable under jsdom.
    //
    // opts:
    //   doc                    - the document (defaults to window.document)
    //   mountTarget            - element the control is appended to (the topbar
    //                            actions group)
    //   decodeImage(b64, type) - Promise<objectUrl|null>; defaults to the browser
    //                            decoder above
    //   onLock()               - invoked for "Lock PAX Cookbook"
    //   onDifferentAccount()   - invoked for "Use a different work account"
    //   onSignInMethod()       - invoked for "Sign-in method"
    function create(opts) {
        opts = opts || {};
        var doc = opts.doc || (typeof document !== 'undefined' ? document : null);
        var decodeImage = typeof opts.decodeImage === 'function' ? opts.decodeImage : defaultDecodeImage;
        var onLock = typeof opts.onLock === 'function' ? opts.onLock : function () {};
        var onDifferentAccount = typeof opts.onDifferentAccount === 'function' ? opts.onDifferentAccount : function () {};
        var onSignInMethod = typeof opts.onSignInMethod === 'function' ? opts.onSignInMethod : function () {};

        var state = {
            provider: null,
            lockState: null,
            presentation: null, // { kind:'photo'|'initials' } once a valid envelope arrives
            objectUrl: null,
            decodeToken: 0
        };

        var root = null;
        var avatar = null;
        var button = null;
        var menu = null;
        var messageEl = null;
        var messageTimer = null;

        function onDocumentClick(ev) {
            if (!root || !menu || root.hidden || menu.hidden) { return; }
            if (ev.target && root.contains(ev.target)) { return; }
            closeMenu();
        }

        function onDocumentKeyDown(ev) {
            if (!root || !menu || !button || root.hidden || menu.hidden || ev.key !== 'Escape') { return; }
            ev.preventDefault();
            closeMenu();
            button.focus();
        }

        function ensureDom() {
            if (root || !doc || !opts.mountTarget) {
                return;
            }
            root = doc.createElement('div');
            root.id = 'work-account-profile';
            root.className = 'wa-profile';
            root.hidden = true;

            button = doc.createElement('button');
            button.type = 'button';
            button.id = 'work-account-profile-button';
            button.className = 'wa-profile__button';
            button.setAttribute('aria-haspopup', 'menu');
            button.setAttribute('aria-expanded', 'false');
            button.setAttribute('aria-controls', 'work-account-profile-menu');
            button.setAttribute('aria-label', LABEL_TEXT);
            button.title = LABEL_TEXT;

            avatar = doc.createElement('span');
            avatar.className = 'wa-profile__avatar';
            avatar.setAttribute('aria-hidden', 'true');
            button.appendChild(avatar);

            menu = doc.createElement('div');
            menu.id = 'work-account-profile-menu';
            menu.className = 'wa-profile__menu';
            menu.setAttribute('role', 'menu');
            menu.hidden = true;

            menu.appendChild(makeMenuItem('lock', 'Lock PAX Cookbook'));
            menu.appendChild(makeMenuItem('different-account', 'Use a different work account'));
            menu.appendChild(makeMenuItem('sign-in-method', 'Sign-in method'));

            messageEl = doc.createElement('div');
            messageEl.className = 'wa-profile__message';
            messageEl.setAttribute('role', 'alert');
            messageEl.hidden = true;

            button.addEventListener('click', function (ev) {
                ev.preventDefault();
                toggleMenu();
            });

            doc.addEventListener('click', onDocumentClick);
            doc.addEventListener('keydown', onDocumentKeyDown);

            root.appendChild(button);
            root.appendChild(menu);
            root.appendChild(messageEl);

            // Insert as the FIRST action so the avatar reads at the leading edge
            // of the action cluster, ahead of Help / Close App.
            if (opts.mountTarget.firstChild) {
                opts.mountTarget.insertBefore(root, opts.mountTarget.firstChild);
            } else {
                opts.mountTarget.appendChild(root);
            }
        }

        function makeMenuItem(action, text) {
            var item = doc.createElement('button');
            item.type = 'button';
            item.className = 'wa-profile__menuitem';
            item.setAttribute('role', 'menuitem');
            item.setAttribute('data-action', action);
            item.textContent = text;
            item.addEventListener('click', function (ev) {
                ev.preventDefault();
                closeMenu();
                if (action === 'lock') { onLock(); }
                else if (action === 'different-account') { onDifferentAccount(); }
                else if (action === 'sign-in-method') { onSignInMethod(); }
            });
            return item;
        }

        function toggleMenu() {
            if (!menu) { return; }
            if (menu.hidden) { openMenu(); } else { closeMenu(); }
        }

        function openMenu() {
            if (!menu || !button) { return; }
            menu.hidden = false;
            button.setAttribute('aria-expanded', 'true');
        }

        function closeMenu() {
            if (!menu || !button) { return; }
            menu.hidden = true;
            button.setAttribute('aria-expanded', 'false');
        }

        function dismissMenu() {
            closeMenu();
        }

        // Shows a bounded, transient text message (e.g. a generic retry notice
        // when a different-account request could not complete). Carries only the
        // caller-provided plain string; never a photo or identity value.
        function showMessage(text) {
            ensureDom();
            if (!messageEl) { return; }
            messageEl.textContent = String(text == null ? '' : text);
            messageEl.hidden = false;
            if (messageTimer) {
                try { clearTimeout(messageTimer); } catch (e) {}
                messageTimer = null;
            }
            if (typeof setTimeout === 'function') {
                messageTimer = setTimeout(function () {
                    if (messageEl) {
                        messageEl.hidden = true;
                        messageEl.textContent = '';
                    }
                    messageTimer = null;
                }, 6000);
            }
        }

        function hideMessage() {
            if (messageTimer) {
                try { clearTimeout(messageTimer); } catch (e) {}
                messageTimer = null;
            }
            if (messageEl) {
                messageEl.hidden = true;
                messageEl.textContent = '';
            }
        }

        function revokeObjectUrl() {
            if (state.objectUrl) {
                try {
                    if (typeof URL !== 'undefined' && URL.revokeObjectURL) {
                        URL.revokeObjectURL(state.objectUrl);
                    }
                } catch (e) {}
                state.objectUrl = null;
            }
        }

        // Whether the gate is open: work account selected AND Unlocked.
        function gateOpen() {
            return state.provider === 'work_account' && state.lockState === 'Unlocked';
        }

        // Renders (or hides) the surface based on the current gate + presentation.
        function render() {
            ensureDom();
            if (!root) { return; }

            if (!gateOpen() || !state.presentation) {
                root.hidden = true;
                closeMenu();
                return;
            }

            root.hidden = false;
            if (state.presentation.kind === 'photo' && state.objectUrl) {
                avatar.classList.add('wa-profile__avatar--photo');
                avatar.classList.remove('wa-profile__avatar--initials');
                avatar.style.backgroundImage = 'url("' + state.objectUrl + '")';
                avatar.textContent = '';
            } else if (state.presentation.kind === 'initials') {
                avatar.classList.remove('wa-profile__avatar--photo');
                avatar.classList.add('wa-profile__avatar--initials');
                avatar.style.backgroundImage = '';
                avatar.textContent = state.presentation.initials;
            }
        }

        // Immediately clears the visible profile and the in-memory image handle.
        function clear() {
            state.decodeToken++;
            state.presentation = null;
            revokeObjectUrl();
            if (avatar) {
                avatar.style.backgroundImage = '';
                avatar.textContent = '';
                avatar.classList.remove('wa-profile__avatar--photo');
                avatar.classList.remove('wa-profile__avatar--initials');
            }
            if (root) { root.hidden = true; }
            closeMenu();
            hideMessage();
        }

        // Applies a raw native message. Revalidates it; a 'none'/invalid/oversized
        // envelope clears the surface; a valid initials envelope renders initials;
        // a valid photo envelope decodes the image (async) then renders it, but
        // only if the gate is still open and no newer envelope superseded it.
        function applyNativeMessage(data) {
            var result = validateEnvelope(data);
            if (!result.ok || result.kind === 'none') {
                clear();
                return;
            }

            if (result.kind === 'initials') {
                state.decodeToken++;
                revokeObjectUrl();
                state.presentation = { kind: 'initials', initials: result.initials };
                render();
                return;
            }

            // photo: decode out-of-line; guard against races + gate changes.
            var token = ++state.decodeToken;
            var pending = decodeImage(result.imageBase64, result.contentType);
            Promise.resolve(pending).then(function (objectUrl) {
                if (token !== state.decodeToken) {
                    // Superseded by a newer envelope / clear: drop this URL.
                    if (objectUrl) { try { URL.revokeObjectURL(objectUrl); } catch (e) {} }
                    return;
                }
                if (!objectUrl) {
                    // Undecodable image: fail closed to no photo surface.
                    clear();
                    return;
                }
                revokeObjectUrl();
                state.objectUrl = objectUrl;
                state.presentation = { kind: 'photo' };
                render();
            }, function () {
                if (token === state.decodeToken) { clear(); }
            });
        }

        function setProvider(provider) {
            state.provider = provider;
            if (provider !== 'work_account') {
                clear();
            } else {
                render();
            }
        }

        function setLockState(lockState) {
            state.lockState = lockState;
            if (lockState !== 'Unlocked') {
                clear();
            } else {
                render();
            }
        }

        // Full teardown: clear the surface, revoke the object URL, and detach DOM.
        function teardown() {
            if (doc) {
                doc.removeEventListener('click', onDocumentClick);
                doc.removeEventListener('keydown', onDocumentKeyDown);
            }
            clear();
            if (root && root.parentNode) {
                root.parentNode.removeChild(root);
            }
            root = null;
            avatar = null;
            button = null;
            menu = null;
            messageEl = null;
        }

        return {
            applyNativeMessage: applyNativeMessage,
            setProvider: setProvider,
            setLockState: setLockState,
            clear: clear,
            showMessage: showMessage,
            dismissMenu: dismissMenu,
            teardown: teardown,
            // Test-only introspection helpers.
            _els: function () { return { root: root, avatar: avatar, button: button, menu: menu }; },
            _state: function () { return state; }
        };
    }

    return {
        MESSAGE_TYPE: MESSAGE_TYPE,
        LABEL_TEXT: LABEL_TEXT,
        ACCEPTED_CONTENT_TYPE: ACCEPTED_CONTENT_TYPE,
        MAX_ENCODED_CHARS: MAX_ENCODED_CHARS,
        validateEnvelope: validateEnvelope,
        create: create
    };
});
