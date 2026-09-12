/**
 * Sign-in method reveal coordinator (experimental Work-account surface).
 *
 * The TOP-LEVEL shell's Work-account profile menu offers a "Sign-in method"
 * item. Choosing it navigates the embedded React surface to Settings and asks
 * it to reveal (expand + focus) the EXISTING Sign-in method section rendered by
 * WorkAccountCard. The shell delivers a bounded, closed-shape navigation intent
 * (message type only — never a photo or any identity value) to this iframe; the
 * App message handler validates provenance and then calls
 * `requestSignInMethodReveal()`.
 *
 * This tiny module bridges the App-level intent to the WorkAccountCard, which
 * may mount — or REMOUNT — slightly AFTER the navigation completes. The App
 * reveal handler navigates to Settings AND bumps a navKey that remounts the
 * Settings subtree, so the section that must reveal is frequently a brand-new
 * WorkAccountCard instance that has not yet subscribed (or whose async config
 * has not yet rendered the disclosure) at the instant the intent arrives.
 *
 * To survive that remount race deterministically (no timers), the intent is a
 * monotonically increasing request id rather than a one-shot flag. Every
 * currently-subscribed card is notified on each request, and each card tracks
 * the highest request id it has already handled. Whichever card is mounted when
 * the id becomes visible — including one that mounts after the request — reveals
 * exactly once. An unmounting listener simply removes itself; it cannot consume
 * or discard an intent on behalf of its replacement, because the intent lives in
 * the shared counter, not in any single listener. It carries no payload of any
 * kind (no account, token, claim, photo, or tenant value).
 */
type RevealSubscriber = () => void;

let revealRequestId = 0;
const subscribers = new Set<RevealSubscriber>();

/** Ask the mounted Sign-in method section to reveal + focus itself. */
export function requestSignInMethodReveal(): void {
  revealRequestId += 1;
  // Snapshot so an unsubscribe during notification cannot skip a subscriber.
  for (const notify of Array.from(subscribers)) {
    notify();
  }
}

/**
 * The id of the most recent reveal request. `0` means no reveal has been
 * requested yet. A mounted card compares this against the highest id it has
 * already handled to decide whether it still owes a reveal.
 */
export function currentSignInMethodRevealRequestId(): number {
  return revealRequestId;
}

/**
 * Subscribe the Sign-in method section. The subscriber is invoked whenever a new
 * reveal is requested; it must consult `currentSignInMethodRevealRequestId()`
 * and reveal at most once per id. Returns an unsubscribe.
 */
export function subscribeSignInMethodReveal(onRevealRequested: RevealSubscriber): () => void {
  subscribers.add(onRevealRequested);
  return () => {
    subscribers.delete(onRevealRequested);
  };
}

/** Test-only reset of the module singleton state. */
export function __resetSignInMethodRevealForTests(): void {
  revealRequestId = 0;
  subscribers.clear();
}
