import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// PAX Cookbook product shell build configuration.
//
// The bundle is served by the in-process broker's static handler from the
// install tree at app/web/app, reached over the loopback origin at /app/.
// `base` is therefore the server-absolute path '/app/' (NOT the donor's
// GitHub Pages '/PAX-Cookbook/'), so emitted asset URLs resolve correctly
// inside the WebView2 control regardless of the per-launch loopback port.
//
// Output lands in a dedicated app/web/app subtree so it sits alongside the
// existing legacy SPA (app/web/index.html + app/web/assets/*) without
// overwriting it. The native shell continues to load the legacy surface by
// default; the React shell is reachable at /app/ during the transition.
export default defineConfig({
  base: '/app/',
  plugins: [react()],
  build: {
    outDir: '../web/app',
    emptyOutDir: true,
    sourcemap: false,
    target: 'es2020',
    cssCodeSplit: true,
    assetsInlineLimit: 4096,
    rollupOptions: {
      input: 'index.html'
    }
  },
  server: {
    port: 5173
  }
});
