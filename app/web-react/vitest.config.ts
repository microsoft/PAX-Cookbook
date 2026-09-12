import { defineConfig } from 'vitest/config';

// Minimal, conventional Vitest setup for the security-sensitive experimental WAM
// renderer flow/state helpers (T1-S2B Phase 6). jsdom provides window/fetch
// surfaces; production code and the Vite build are unaffected (test-only).
export default defineConfig({
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.ts'],
    globals: false,
  },
});
