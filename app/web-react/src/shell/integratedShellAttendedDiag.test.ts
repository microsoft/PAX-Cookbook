/**
 * PHASE-2b (fix) tests for the TOP-LEVEL shell attended Hello capability
 * diagnostic recorder (cycle-01r-hello-capability-probe-repair). MEASUREMENT
 * ONLY. These prove the IDLE-PREEMPT repair directly against the REAL shell code
 * in app/web/assets/integrated-shell.js — no reimplementation, no fork.
 *
 * The shell is a legacy top-level IIFE that is normally "NOT reachable from
 * vitest/jsdom" (see WorkAccountCard.switch.test.ts). To exercise the recorder
 * faithfully we load the ACTUAL source file, evaluate it inside jsdom with the
 * bare surfaces it needs (a #mk-content-frame element, the read-only
 * window.__paxHelloAttendedDiag marker the native host injects, and a
 * window.chrome.webview.postMessage native-host spy), and drive it exactly the
 * way the runtime does — same-origin, exact-source messages plus the real enroll
 * courier — under fake timers.
 *
 * What the repair must guarantee (and what these tests lock in):
 *   1. IDLE launch persists NOTHING. The 180s safety net is no longer armed at
 *      page load, so an attended launch that never sees a real attempt writes no
 *      capture — even long after 180s.
 *   2. A REAL accepted enrollment request arms an attempt-scoped safety net that
 *      DOES persist shell-side-only evidence if the iframe report never arrives.
 *   3. A real iframe report yields exactly ONE merged capture.
 *   4. A SECOND human attempt after an earlier one is captured independently
 *      (the single-shot latch is re-armable per attempt).
 *   5. The exact same-origin / exact-source-window gating on the report listener
 *      is preserved — a foreign origin or foreign source window never emits.
 *
 * NONE of these tests touch navigator.credentials.create(); the recorder is a
 * pure observer and the switch/enrollment behavior is unchanged.
 */
/// <reference types="vite/client" />
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
// Load the REAL legacy shell asset as a raw string via Vite's `?raw` loader so
// these tests exercise the shipped recorder rather than a reimplementation. This
// avoids any Node built-in imports (the project intentionally has no @types/node);
// the `?raw` module type comes from the vite/client reference above.
import shellSrcRaw from '../../../web/assets/integrated-shell.js?raw';

const SHELL_SRC: string = shellSrcRaw;

const ATTENDED_CAPTURE_TYPE = 'cookbook:hello-attended-diag-capture';
const ENROLL_REQUEST_TYPE = 'cookbook:windows-hello-enroll-request';
const IFRAME_REPORT_TYPE = 'cookbook:hello-attended-iframe-report';
const FRAME_ID = 'mk-content-frame';
const SAFETY_NET_MS = 180000;

type HostMessage = { type?: string; payload?: Record<string, unknown> };

interface Harness {
  frameWindow: Window;
  hostPosts: HostMessage[];
  attendedCaptures: HostMessage[];
}

let harness: Harness;

/** Flush the recorder's probe-promise microtask chain (no timers involved). */
async function flushMicrotasks(): Promise<void> {
  for (let i = 0; i < 6; i += 1) {
    // eslint-disable-next-line no-await-in-loop
    await Promise.resolve();
  }
}

/** Build a same-origin, exact-source message event the way the runtime posts. */
function postFromFrame(data: unknown, opts?: { origin?: string; source?: unknown }): void {
  const origin = opts?.origin ?? window.location.origin;
  const source = (opts && 'source' in opts ? opts.source : harness.frameWindow) as unknown;
  const evt = new MessageEvent('message', { data });
  // jsdom's MessageEvent constructor does not preserve the `source` (and can
  // normalize `origin`) from the init dict, so pin both as own properties to
  // faithfully reproduce a real cross-document post from the content iframe.
  Object.defineProperty(evt, 'origin', { value: origin, configurable: true });
  Object.defineProperty(evt, 'source', { value: source, configurable: true });
  window.dispatchEvent(evt);
}

/** Deliver a valid, single-flight-clean enrollment request → begins an attempt. */
function beginAttempt(requestId: string): void {
  postFromFrame({ type: ENROLL_REQUEST_TYPE, requestId });
}

/** Deliver the bounded iframe report that the React observer would post. */
function deliverIframeReport(
  payload: Record<string, unknown>,
  opts?: { origin?: string; source?: unknown },
): void {
  postFromFrame({ type: IFRAME_REPORT_TYPE, payload }, opts);
}

function bootShell(attendedEnabled: boolean): void {
  // A fresh content-frame per boot so each evaluated IIFE gates on its own
  // frame identity — prior evals' listeners bail on the source check. jsdom's
  // native iframe.contentWindow identity is not stable across reads, so we pin a
  // fake window object as contentWindow and use that exact object as the message
  // source; this reproduces the runtime's exact-source gating deterministically.
  const frame = document.createElement('iframe');
  frame.id = FRAME_ID;
  document.body.appendChild(frame);
  const frameWindow = { postMessage: () => {} } as unknown as Window;
  Object.defineProperty(frame, 'contentWindow', {
    value: frameWindow,
    configurable: true,
  });

  const hostPosts: HostMessage[] = [];
  const attendedCaptures: HostMessage[] = [];
  (window as unknown as { chrome?: unknown }).chrome = {
    webview: {
      postMessage: (msg: HostMessage) => {
        hostPosts.push(msg);
        if (msg && msg.type === ATTENDED_CAPTURE_TYPE) {
          attendedCaptures.push(msg);
        }
      },
    },
  };

  if (attendedEnabled) {
    (window as unknown as { __paxHelloAttendedDiag?: unknown }).__paxHelloAttendedDiag = {
      enabled: true,
    };
  }

  harness = { frameWindow, hostPosts, attendedCaptures };

  // Indirect eval runs the source in global scope against the jsdom window;
  // document.readyState is already 'complete', so init() runs synchronously and
  // (when enabled) starts the attended recorder immediately.
  (0, eval)(SHELL_SRC); // eslint-disable-line no-eval
}

describe('integrated-shell attended Hello diagnostic recorder (IDLE-PREEMPT repair)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.clearAllTimers();
    vi.useRealTimers();
    document.body.innerHTML = '';
    delete (window as unknown as { __paxHelloAttendedDiag?: unknown }).__paxHelloAttendedDiag;
    delete (window as unknown as { __paxHelloAttendedDiagCeremony?: unknown })
      .__paxHelloAttendedDiagCeremony;
    delete (window as unknown as { chrome?: unknown }).chrome;
    delete (window as unknown as { cookbookHelloEnroll?: unknown }).cookbookHelloEnroll;
  });

  it('is fully inert without the attended marker (no recorder, no capture ever)', async () => {
    bootShell(false);
    // Even a real enroll request + a report + a very long wait persist nothing.
    beginAttempt('req-noflag');
    deliverIframeReport({ iframeSecureContext: true });
    await vi.advanceTimersByTimeAsync(SAFETY_NET_MS + 60000);
    await flushMicrotasks();
    expect(harness.attendedCaptures).toHaveLength(0);
  });

  it('IDLE attended launch persists NOTHING — the safety net is NOT armed at page load', async () => {
    bootShell(true);
    // No attempt is ever started. Advance well past the old 180s page-load
    // safety net (which was the bug) plus a large margin.
    await vi.advanceTimersByTimeAsync(SAFETY_NET_MS + 120000);
    await flushMicrotasks();
    expect(harness.attendedCaptures).toHaveLength(0);
  });

  it('a REAL accepted enrollment request arms an attempt-scoped safety net that persists once', async () => {
    bootShell(true);

    // Before any attempt: idle for a bit — still nothing.
    await vi.advanceTimersByTimeAsync(60000);
    expect(harness.attendedCaptures).toHaveLength(0);

    beginAttempt('req-1');

    // Just before the safety net fires: still nothing.
    await vi.advanceTimersByTimeAsync(SAFETY_NET_MS - 1);
    await flushMicrotasks();
    expect(harness.attendedCaptures).toHaveLength(0);

    // The attempt-scoped net fires and persists exactly one shell-side capture.
    await vi.advanceTimersByTimeAsync(1);
    await flushMicrotasks();
    expect(harness.attendedCaptures).toHaveLength(1);

    const cap = harness.attendedCaptures[0].payload as Record<string, unknown>;
    expect(cap.schemaVersion).toBe('hello-attended-1');
    expect(cap.cycleId).toBe('cycle-01r-hello-capability-probe-repair');
    expect(cap.enrollmentRequestReceived).toBe(true);
    // Null-report safety-net capture => iframe-side fields fall back to false.
    expect(cap.iframeSecureContext).toBe(false);
    expect(cap.iframePlatformAuthenticatorAvailable).toBe(false);
    expect(cap.originRelation).toBe('unavailable');
    // No credential/identity material of any kind. (The schema intentionally
    // carries the bounded boolean field `credentialCeremonyInvoked`, so the leak
    // scan targets identity/secret TOKENS rather than that allow-listed name.)
    expect(JSON.stringify(cap)).not.toMatch(
      /challenge|token|tenant|userId|user_id|secret|password|principal/i,
    );
  });

  it('a real iframe report after an attempt yields exactly ONE merged capture (net cleared)', async () => {
    bootShell(true);
    beginAttempt('req-1');
    deliverIframeReport({
      iframeSecureContext: true,
      iframePlatformAuthenticatorAvailable: true,
      originRelation: 'same_origin',
      enrollmentRequestPosted: true,
      backendSelectAttempted: true,
      backendSelectReason: 'windows_hello_enrollment_required',
      backendSelectPersisted: false,
      finalSelectedProvider: 'work_account',
    });
    await flushMicrotasks();

    expect(harness.attendedCaptures).toHaveLength(1);
    const cap = harness.attendedCaptures[0].payload as Record<string, unknown>;
    expect(cap.iframeSecureContext).toBe(true);
    expect(cap.iframePlatformAuthenticatorAvailable).toBe(true);
    expect(cap.originRelation).toBe('same_origin');
    expect(cap.enrollmentRequestPosted).toBe(true);
    expect(cap.finalSelectedProvider).toBe('work_account');
    expect(cap.enrollmentRequestReceived).toBe(true);

    // The report cleared the safety net: advancing past 180s adds NO duplicate.
    await vi.advanceTimersByTimeAsync(SAFETY_NET_MS + 1);
    await flushMicrotasks();
    expect(harness.attendedCaptures).toHaveLength(1);
  });

  it('a SECOND human attempt after the first is captured independently (latch re-arms)', async () => {
    bootShell(true);

    // Attempt 1 completes with a report (single-flight lock released via the
    // real finishEnroll path — no enroll module present => outcome "failed").
    beginAttempt('req-1');
    deliverIframeReport({ finalSelectedProvider: 'work_account', originRelation: 'same_origin' });
    await flushMicrotasks();
    expect(harness.attendedCaptures).toHaveLength(1);

    // Attempt 2: a fresh accepted request re-arms the latch + safety net.
    beginAttempt('req-2');
    deliverIframeReport({ finalSelectedProvider: 'windows_hello', originRelation: 'same_origin' });
    await flushMicrotasks();

    expect(harness.attendedCaptures).toHaveLength(2);
    const first = harness.attendedCaptures[0].payload as Record<string, unknown>;
    const second = harness.attendedCaptures[1].payload as Record<string, unknown>;
    expect(first.finalSelectedProvider).toBe('work_account');
    expect(second.finalSelectedProvider).toBe('windows_hello');
  });

  it('preserves exact same-origin / exact-source gating on the report listener', async () => {
    bootShell(true);
    beginAttempt('req-1');

    // Foreign origin: ignored.
    deliverIframeReport(
      { finalSelectedProvider: 'windows_hello' },
      { origin: 'https://evil.example' },
    );
    await flushMicrotasks();
    expect(harness.attendedCaptures).toHaveLength(0);

    // Foreign source window: ignored.
    deliverIframeReport(
      { finalSelectedProvider: 'windows_hello' },
      { source: {} as unknown as Window },
    );
    await flushMicrotasks();
    expect(harness.attendedCaptures).toHaveLength(0);

    // Correct origin + source: emits exactly one.
    deliverIframeReport({ finalSelectedProvider: 'work_account', originRelation: 'same_origin' });
    await flushMicrotasks();
    expect(harness.attendedCaptures).toHaveLength(1);
  });
});
