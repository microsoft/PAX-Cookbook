/**
 * In-app navigation guard registry.
 *
 * A surface that holds unsaved work (the recipe builder) registers a guard
 * while it is dirty. The shell consults the guard before switching sections —
 * including section switches that originate from the legacy shell's left
 * nav-rail, which reach React as an `mk-nav` message. If the guard intercepts,
 * it shows its own confirmation and decides whether to proceed.
 *
 * This is a module-level registry on purpose: the shell's cross-frame `mk-nav`
 * handler and the builder live in different component trees, so a shared module
 * lets them coordinate without threading a callback through every layer. Only
 * one guard is ever active at a time (the builder registers on mount and clears
 * on unmount), which matches the single-editor reality of the app.
 *
 * Pure coordination: no fetch, no PAX, no navigation of its own. It only relays
 * the shell's intent to the registered guard and reports whether the guard took
 * ownership of the decision.
 */

export interface NavIntent {
  /** Target section id the user asked to navigate to. */
  section: string;
  /** Continue the navigation (the guard chose to leave). */
  proceed: () => void;
  /** Abandon the navigation and stay put (the guard chose to stay). */
  cancel: () => void;
}

/**
 * A registered guard. Returns `true` when it intercepts the navigation (the
 * caller must NOT navigate now and must defer to the intent's proceed/cancel);
 * `false` when navigation may happen immediately.
 */
export type NavigationGuard = (intent: NavIntent) => boolean;

let activeGuard: NavigationGuard | null = null;

/** Register (or clear, with null) the active navigation guard. */
export function setNavigationGuard(guard: NavigationGuard | null): void {
  activeGuard = guard;
}

/**
 * Consult the active guard. Returns `true` when the guard intercepted the
 * navigation (the caller must wait for the intent's proceed/cancel); `false`
 * when navigation may proceed immediately. A throwing guard never traps the
 * user — it is treated as "allow".
 */
export function consultNavigationGuard(intent: NavIntent): boolean {
  if (!activeGuard) {
    return false;
  }
  try {
    return activeGuard(intent);
  } catch {
    return false;
  }
}
