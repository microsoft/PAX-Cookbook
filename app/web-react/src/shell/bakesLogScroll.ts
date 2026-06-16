/**
 * Pure helpers for the Bakes log auto-scroll ("tail -f") behavior.
 *
 * Extracted so the scroll-away decision is unit-testable without a DOM. The
 * component owns the side effects (reading the live element metrics, pinning
 * the <pre> to the bottom); this module only decides, from a set of scroll
 * metrics, whether the user has scrolled away from the bottom far enough that
 * auto-scroll should turn itself off.
 *
 * No data is read here -- the metrics are plain numbers from the log viewport.
 */

/**
 * How far (in pixels) the viewport may sit above the very bottom and still be
 * treated as "at the bottom". A programmatic pin-to-bottom lands at distance 0,
 * so it never trips the scroll-away check; only a genuine upward user scroll
 * pushes the distance past this threshold.
 */
export const NEAR_BOTTOM_PX = 24;

export interface LogScrollMetrics {
  scrollTop: number;
  scrollHeight: number;
  clientHeight: number;
}

/** Pixels between the current viewport bottom and the end of the content. */
export function distanceFromBottom(m: LogScrollMetrics): number {
  return m.scrollHeight - m.scrollTop - m.clientHeight;
}

/**
 * True when the viewport is currently pinned at (or within the threshold of)
 * the bottom of the content.
 */
export function isNearBottom(
  m: LogScrollMetrics,
  nearBottomPx: number = NEAR_BOTTOM_PX,
): boolean {
  return distanceFromBottom(m) <= nearBottomPx;
}

/**
 * True when a scroll event represents the user moving away from the bottom
 * (i.e. scrolling up to read earlier output). The caller uses this, while
 * auto-scroll is on, to turn auto-scroll off so the live tail stops yanking the
 * view back down.
 */
export function userScrolledAway(
  m: LogScrollMetrics,
  nearBottomPx: number = NEAR_BOTTOM_PX,
): boolean {
  return !isNearBottom(m, nearBottomPx);
}
