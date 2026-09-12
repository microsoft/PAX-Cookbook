/**
 * Sign-in method reveal coordinator — unit tests (monotonic request-id design).
 *
 * The coordinator bridges the App-level reveal intent (delivered by the
 * TOP-LEVEL shell's Work-account profile menu) to the WorkAccountCard Sign-in
 * method section. Because the App reveal handler REMOUNTS the Settings subtree
 * (navKey bump), the intent is a monotonically increasing request id rather than
 * a one-shot flag: every currently-subscribed card is notified on each request,
 * a late-mounting card can read the current id and reveal itself, and each id is
 * handled at most once. The signal carries no payload of any kind.
 *
 * Behavioral coverage of the remount race at the COMPONENT level (against the
 * real WorkAccountCard) lives in signInMethodReveal.remount.test.ts.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  requestSignInMethodReveal,
  subscribeSignInMethodReveal,
  currentSignInMethodRevealRequestId,
  __resetSignInMethodRevealForTests,
} from './signInMethodReveal';

afterEach(() => {
  __resetSignInMethodRevealForTests();
});

describe('signInMethodReveal coordinator', () => {
  it('starts with request id 0 (no reveal requested yet)', () => {
    expect(currentSignInMethodRevealRequestId()).toBe(0);
  });

  it('increments the request id and notifies attached subscribers on each request', () => {
    const listener = vi.fn();
    subscribeSignInMethodReveal(listener);

    requestSignInMethodReveal();
    expect(currentSignInMethodRevealRequestId()).toBe(1);
    expect(listener).toHaveBeenCalledTimes(1);

    requestSignInMethodReveal();
    expect(currentSignInMethodRevealRequestId()).toBe(2);
    expect(listener).toHaveBeenCalledTimes(2);
  });

  it('notifies every currently-mounted subscriber (fan-out, not one-shot)', () => {
    const first = vi.fn();
    const second = vi.fn();
    subscribeSignInMethodReveal(first);
    subscribeSignInMethodReveal(second);

    requestSignInMethodReveal();

    expect(first).toHaveBeenCalledTimes(1);
    expect(second).toHaveBeenCalledTimes(1);
  });

  it('exposes the current id so a subscriber that attaches AFTER a request can still reveal', () => {
    // A reveal is requested while no card is mounted (e.g. before/around remount).
    requestSignInMethodReveal();
    expect(currentSignInMethodRevealRequestId()).toBe(1);

    // Subscribing does NOT auto-fire; the mounted card reveals from the current
    // id via its own effect. Here we confirm the id remains observable so a
    // late subscriber is never starved.
    let seen = -1;
    subscribeSignInMethodReveal(() => {
      seen = currentSignInMethodRevealRequestId();
    });
    expect(seen).toBe(-1);
    expect(currentSignInMethodRevealRequestId()).toBe(1);
  });

  it('does not notify a subscriber after it unsubscribes', () => {
    const listener = vi.fn();
    const unsubscribe = subscribeSignInMethodReveal(listener);
    unsubscribe();
    requestSignInMethodReveal();
    expect(listener).not.toHaveBeenCalled();
    // The shared id still advances so a replacement subscriber is not starved.
    expect(currentSignInMethodRevealRequestId()).toBe(1);
  });

  it('an unsubscribe during notification does not skip other subscribers', () => {
    const calls: string[] = [];
    let unsubscribeSecond: (() => void) | null = null;
    subscribeSignInMethodReveal(() => {
      calls.push('first');
      // The first subscriber tears down the second mid-notification.
      unsubscribeSecond?.();
    });
    unsubscribeSecond = subscribeSignInMethodReveal(() => {
      calls.push('second');
    });

    requestSignInMethodReveal();

    // Both subscribers present at request time are notified from the snapshot.
    expect(calls).toContain('first');
    expect(calls).toContain('second');
  });

  it('__resetSignInMethodRevealForTests clears id and subscribers', () => {
    const listener = vi.fn();
    subscribeSignInMethodReveal(listener);
    requestSignInMethodReveal();
    expect(currentSignInMethodRevealRequestId()).toBe(1);

    __resetSignInMethodRevealForTests();
    expect(currentSignInMethodRevealRequestId()).toBe(0);

    requestSignInMethodReveal();
    // The old listener was cleared by reset, so it is not notified again.
    expect(listener).toHaveBeenCalledTimes(1);
  });
});
