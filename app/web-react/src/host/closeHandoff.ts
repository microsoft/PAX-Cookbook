/**
 * Native close-handoff client for the React product shell.
 *
 * The native WebView2 shell (WebViewShell) intercepts every window-close
 * request — the title-bar X, the taskbar right-click Close, and Alt+F4 — and,
 * instead of exiting, posts the host message `cookbook:host-close-request` to
 * whichever web surface is loaded. The legacy SPA answers that message with its
 * own close modal; this module gives the React `/app/` surface the same
 * handshake so closing from the product shell also shows the shared
 * three-choice confirmation instead of silently doing nothing.
 *
 * The operator's decision is sent back to the shell over the same channel:
 *   - Minimize to tray -> `cookbook:minimize-to-tray` (hide window, keep server)
 *   - Close app        -> `cookbook:close-app`        (stop server, exit)
 * The shell performs the actual hide or shutdown; nothing here tears the
 * process down on its own. When the page runs without the native host bridge
 * (for example in a plain browser during development) the bridge calls are
 * no-ops, so the modal still opens and closes sensibly.
 */

const HOST_CLOSE_REQUEST = 'cookbook:host-close-request';

export type CloseDecision = 'cookbook:minimize-to-tray' | 'cookbook:close-app';

interface WebViewBridge {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
  removeEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}

function getBridge(): WebViewBridge | null {
  if (typeof window === 'undefined') {
    return null;
  }
  const chrome = (window as unknown as { chrome?: { webview?: unknown } }).chrome;
  const webview = chrome && chrome.webview ? (chrome.webview as Partial<WebViewBridge>) : null;
  if (
    webview &&
    typeof webview.postMessage === 'function' &&
    typeof webview.addEventListener === 'function' &&
    typeof webview.removeEventListener === 'function'
  ) {
    return webview as WebViewBridge;
  }
  return null;
}

/** Whether the native WebView2 host bridge is present. */
export function hasHostBridge(): boolean {
  return getBridge() !== null;
}

/**
 * Send the operator's close decision back to the native shell. Returns false
 * when no host bridge is present (e.g. a plain dev browser).
 */
export function postCloseDecision(decision: CloseDecision): boolean {
  const bridge = getBridge();
  if (!bridge) {
    return false;
  }
  try {
    bridge.postMessage(decision);
    return true;
  } catch {
    return false;
  }
}

/**
 * Subscribe to the shell's intercepted window-close request so the native X /
 * taskbar Close / Alt+F4 all open the React close modal. Returns an unsubscribe
 * function. A no-op (returns an empty cleanup) when no host bridge is present.
 */
export function subscribeHostCloseRequest(onRequest: () => void): () => void {
  const bridge = getBridge();
  if (!bridge) {
    return () => {};
  }
  const listener = (event: { data: unknown }) => {
    if (typeof event.data === 'string' && event.data === HOST_CLOSE_REQUEST) {
      onRequest();
    }
  };
  bridge.addEventListener('message', listener);
  return () => {
    try {
      bridge.removeEventListener('message', listener);
    } catch {
      // Best-effort cleanup.
    }
  };
}
