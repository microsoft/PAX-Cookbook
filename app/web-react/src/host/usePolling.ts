import { useEffect, useRef } from 'react';

/**
 * Background polling hook.
 *
 * Calls `fetchFn` once on mount and then every `intervalMs` while the component
 * is mounted; the interval is cleared automatically on unmount, so a page that
 * the user has navigated away from stops polling on its own (no wasted work on
 * hidden surfaces). The latest `fetchFn` is always used via a ref, so callers
 * can pass an inline closure without resetting the timer on every render and
 * without capturing stale state.
 *
 * The hook intentionally does no error handling — each `fetchFn` owns its own
 * "silently skip a failed cycle / merge into existing state" behavior so a
 * transient broker hiccup never clears or disrupts what the user is viewing.
 *
 * Pass a non-positive `intervalMs` to do the initial fetch only (no interval).
 */
export function usePolling(fetchFn: () => void | Promise<void>, intervalMs: number): void {
  const fnRef = useRef(fetchFn);
  fnRef.current = fetchFn;

  useEffect(() => {
    void fnRef.current();
    if (!(intervalMs > 0)) {
      return;
    }
    const id = window.setInterval(() => {
      void fnRef.current();
    }, intervalMs);
    return () => window.clearInterval(id);
  }, [intervalMs]);
}
