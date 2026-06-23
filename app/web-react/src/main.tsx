import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './styles/tokens.css';
import './styles/shell.css';
import './styles/contextual-help.css';
import './shell/workspace.css';

const rootEl = document.getElementById('root');
if (!rootEl) {
  throw new Error('Root element #root not found in index.html');
}

// Post-load repaint safety net. After an in-app update reloads the page, the
// WebView2 (Chromium) compositor can present a stale frame: text falls back to
// grayscale antialiasing (looks jagged) and :has()-driven layouts have not
// settled, until something forces a recomposite. The native host nudges a real
// resize, and this is the belt-and-suspenders client counterpart: promote #root
// to a compositor layer for one frame, then drop it, which forces Chromium to
// repaint the de-composited content (restoring ClearType) and re-resolve layout.
function nudgeRepaint(): void {
  const root = document.getElementById('root');
  if (!root) {
    return;
  }
  root.style.transform = 'translateZ(0)';
  void root.offsetHeight;
  requestAnimationFrame(() => {
    root.style.transform = '';
    void root.offsetHeight;
  });
}
requestAnimationFrame(() => requestAnimationFrame(nudgeRepaint));
window.addEventListener('pageshow', () => nudgeRepaint());

ReactDOM.createRoot(rootEl).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
