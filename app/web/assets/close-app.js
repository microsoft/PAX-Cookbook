// PAX Cookbook -- Close confirmation modal.
//
// Wires a single 3-choice dialog that every close path shares:
//   - Cancel:            dismiss; nothing changes; the app stays visible.
//   - Minimize to tray:  ask the shell to hide the window to the tray; the
//                        Cookbook server keeps running so bakes continue.
//   - Close app:         ask the shell to stop the local Cookbook server and
//                        exit the process.
//
// The native shell (WebViewShell) hosts this page inside a WebView2 control.
// It intercepts every window-close request -- the title-bar X, the taskbar
// right-click Close, and Alt+F4 -- and asks this modal to open by posting the
// host message "cookbook:host-close-request". The modal's buttons send the
// operator's choice back to the shell over the same WebView2 message channel:
//   - Minimize to tray -> "cookbook:minimize-to-tray"
//   - Close app        -> "cookbook:close-app"
// The shell performs the actual hide or shutdown; this script never tears the
// process down on its own.
//
// When the page runs without the native host bridge (for example, opened in a
// plain browser during development), Close app falls back to window.close()
// and Minimize is a no-op, so the modal still behaves sensibly.
//
// No state in sessionStorage / localStorage. The modal is purely modal.

(function () {
    'use strict';

    var refs = null;
    var lastFocus = null;

    function byId(id) { return document.getElementById(id); }

    function hasHostBridge() {
        return !!(window.chrome && window.chrome.webview &&
                  typeof window.chrome.webview.postMessage === 'function');
    }

    function postToHost(message) {
        if (!hasHostBridge()) { return false; }
        try {
            window.chrome.webview.postMessage(message);
            return true;
        } catch (e) {
            return false;
        }
    }

    function captureRefs() {
        var modal       = byId('close-app-modal');
        var scrim       = byId('close-app-modal-scrim');
        var btnOpen     = byId('close-app-button');
        var btnCancel   = byId('close-app-cancel');
        var btnMinimize = byId('close-app-minimize');
        var btnConfirm  = byId('close-app-confirm');
        if (!modal || !scrim || !btnOpen || !btnCancel || !btnMinimize || !btnConfirm) {
            return false;
        }
        refs = {
            modal:       modal,
            scrim:       scrim,
            btnOpen:     btnOpen,
            btnCancel:   btnCancel,
            btnMinimize: btnMinimize,
            btnConfirm:  btnConfirm
        };
        return true;
    }

    function openModal() {
        if (!refs) { return; }
        if (!refs.modal.hidden) { return; }
        lastFocus = document.activeElement;
        refs.modal.hidden = false;
        refs.scrim.hidden = false;
        // Default focus on Cancel -- the safest action.
        try { refs.btnCancel.focus(); } catch (e) {}
    }

    function closeModal() {
        if (!refs) { return; }
        if (refs.modal.hidden) { return; }
        refs.modal.hidden = true;
        refs.scrim.hidden = true;
        if (lastFocus && typeof lastFocus.focus === 'function') {
            try { lastFocus.focus(); } catch (e) {}
        }
        lastFocus = null;
    }

    function tryCloseWindow() {
        // Fallback path only -- used when the native host bridge is absent.
        // In the shell, the host performs the close after the message below.
        try { window.close(); } catch (e) {}
    }

    function onMinimize() {
        closeModal();
        // Ask the shell to hide the window to the tray. The server keeps
        // running so any bakes in progress continue. Without the bridge there
        // is nothing to minimize, so this is a no-op outside the shell.
        postToHost('cookbook:minimize-to-tray');
    }

    function onCloseApp() {
        // Ask the shell to stop the local server and exit. The shell tears the
        // window down, so we leave the modal in place to avoid a visible flash.
        if (!postToHost('cookbook:close-app')) {
            closeModal();
            tryCloseWindow();
        }
    }

    function onKeyDown(ev) {
        if (!refs || refs.modal.hidden) { return; }
        if (ev.key === 'Escape' || ev.key === 'Esc') {
            ev.preventDefault();
            closeModal();
        }
    }

    function onHostMessage(ev) {
        var data = ev && ev.data;
        if (typeof data !== 'string') { return; }
        if (data === 'cookbook:host-close-request') {
            openModal();
        }
    }

    function init() {
        if (!captureRefs()) { return; }
        refs.btnOpen.addEventListener('click', function (ev) {
            ev.preventDefault();
            openModal();
        });
        refs.btnCancel.addEventListener('click', function (ev) {
            ev.preventDefault();
            closeModal();
        });
        refs.btnMinimize.addEventListener('click', function (ev) {
            ev.preventDefault();
            onMinimize();
        });
        refs.btnConfirm.addEventListener('click', function (ev) {
            ev.preventDefault();
            onCloseApp();
        });
        refs.scrim.addEventListener('click', function () {
            closeModal();
        });
        document.addEventListener('keydown', onKeyDown, false);

        // Listen for the shell's intercepted window-close request so the
        // native X / taskbar Close / Alt+F4 all open this same modal.
        if (window.chrome && window.chrome.webview &&
            typeof window.chrome.webview.addEventListener === 'function') {
            window.chrome.webview.addEventListener('message', onHostMessage);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }

    // Public API. Lets other modules (e.g. lock-overlay.js) trigger
    // the same managed-close confirmation flow without having to
    // duplicate the modal markup or shutdown wiring. The lock overlay
    // stays mounted underneath via z-index; if the operator cancels,
    // the auth surface is still visible without any restore work.
    window.CookbookCloseApp = {
        open: function () { openModal(); },
        close: function () { closeModal(); }
    };
})();
