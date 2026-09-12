import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  EXPERIMENTAL_WAM_REQUEST_TYPE,
  authorizeSessionWithExperimentalWam,
  getExperimentalWamCapability,
  initiateExperimentalWam,
  sendExperimentalWamRequestToNative,
  waitForExperimentalWamStatus,
  WINDOWS_HELLO_ENROLL_REQUEST_TYPE,
  WINDOWS_HELLO_ENROLL_RESULT_TYPE,
  requestWindowsHelloEnrollmentFromNative,
  requestBrokerLock,
  BROKER_LOCK_INITIATED_TYPE,
} from './experimentalWam';

// Security-sensitive renderer flow (T1-S2B). These prove the renderer is a
// courier only: it posts ONLY a requestId to the native host, never submits an
// approval field, and fails closed on denial/unknown/transport/cancellation.

interface FakeResponse {
  ok: boolean;
  status: number;
  text: () => Promise<string>;
}

function jsonResponse(status: number, body: unknown): FakeResponse {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: async () => (body === undefined ? '' : JSON.stringify(body)),
  };
}

function fetchMock() {
  return vi.fn() as unknown as ReturnType<typeof vi.fn> & typeof fetch;
}

// Collect every request body posted through fetch so we can assert no approval
// field ever leaves the renderer.
function bodiesOf(mock: ReturnType<typeof vi.fn>): string[] {
  return mock.mock.calls
    .map((c) => (c[1] as RequestInit | undefined)?.body)
    .filter((b): b is string => typeof b === 'string');
}

const APPROVAL_FIELDS = ['category', 'scopeValid', 'identityValid', 'accountBindingFingerprint', 'approved', 'salt', 'fingerprint'];

beforeEach(() => {
  window.sessionStorage.setItem('cookbook.sessionToken', 'test-token');
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.useRealTimers();
  window.sessionStorage.clear();
});

describe('capability', () => {
  it('reports available only for an explicit available:true + providerId', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { available: true, providerId: 'entra-wam' }));
    vi.stubGlobal('fetch', m);
    expect(await getExperimentalWamCapability()).toEqual({ available: true, providerId: 'entra-wam' });
  });

  it('fails closed to unavailable on {available:false}, non-200, and network error', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { available: false }))
      .mockResolvedValueOnce(jsonResponse(404, { error: 'experimental_wam_not_enabled' }))
      .mockRejectedValueOnce(new Error('boom'));
    vi.stubGlobal('fetch', m);
    expect(await getExperimentalWamCapability()).toEqual({ available: false });
    expect(await getExperimentalWamCapability()).toEqual({ available: false });
    expect(await getExperimentalWamCapability()).toEqual({ available: false });
  });

  it('requires the provider id to be exactly "entra-wam" (missing, wrong, or wrong-case fails closed)', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { available: true }))                       // missing providerId
      .mockResolvedValueOnce(jsonResponse(200, { available: true, providerId: 'Entra-WAM' })) // wrong case
      .mockResolvedValueOnce(jsonResponse(200, { available: true, providerId: 'other' }))     // different id
      .mockResolvedValueOnce(jsonResponse(200, { available: true, providerId: 'entra-wam' })); // exact
    vi.stubGlobal('fetch', m);
    expect(await getExperimentalWamCapability()).toEqual({ available: false });
    expect(await getExperimentalWamCapability()).toEqual({ available: false });
    expect(await getExperimentalWamCapability()).toEqual({ available: false });
    expect(await getExperimentalWamCapability()).toEqual({ available: true, providerId: 'entra-wam' });
  });
});

describe('initiate', () => {
  it('session initiation posts only purpose (no recipeId, no approval fields)', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { requestId: 'req-1' }));
    vi.stubGlobal('fetch', m);
    const result = await initiateExperimentalWam();
    expect(result.requestId).toBe('req-1');
    const body = JSON.parse(bodiesOf(m)[0]);
    expect(body).toEqual({ purpose: 'session' });
  });

  it('404 maps to unavailable and grants no requestId', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(404, { error: 'experimental_wam_not_enabled' }));
    vi.stubGlobal('fetch', m);
    expect(await initiateExperimentalWam()).toEqual({ requestId: null, outcome: 'unavailable' });
  });
});

describe('native courier send', () => {
  it('posts ONLY { type, requestId } to the parent with the exact origin', () => {
    const post = vi.fn();
    const fakeParent = { postMessage: post } as unknown as Window;
    Object.defineProperty(window, 'parent', { value: fakeParent, configurable: true });

    expect(sendExperimentalWamRequestToNative('req-9')).toBe(true);
    expect(post).toHaveBeenCalledTimes(1);
    const [message, origin] = post.mock.calls[0];
    expect(message).toEqual({ type: EXPERIMENTAL_WAM_REQUEST_TYPE, requestId: 'req-9' });
    expect(Object.keys(message)).toEqual(['type', 'requestId']);
    expect(origin).toBe(window.location.origin);

    Object.defineProperty(window, 'parent', { value: window, configurable: true });
  });

  it('fails closed (returns false, posts nothing) when there is no native host parent', () => {
    Object.defineProperty(window, 'parent', { value: window, configurable: true });
    expect(sendExperimentalWamRequestToNative('req-9')).toBe(false);
  });
});

describe('requestBrokerLock notifies the top-level shell', () => {
  it('on a 2xx lock, posts ONLY { type } to the parent with the exact origin', async () => {
    const post = vi.fn();
    Object.defineProperty(window, 'parent', {
      value: { postMessage: post } as unknown as Window,
      configurable: true,
    });
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { state: 'Locked' }));
    vi.stubGlobal('fetch', m);

    expect(await requestBrokerLock()).toBe(true);
    expect(post).toHaveBeenCalledTimes(1);
    const [message, origin] = post.mock.calls[0];
    expect(message).toEqual({ type: BROKER_LOCK_INITIATED_TYPE });
    expect(Object.keys(message)).toEqual(['type']);
    expect(origin).toBe(window.location.origin);

    Object.defineProperty(window, 'parent', { value: window, configurable: true });
  });

  it('does NOT notify on a non-2xx lock response', async () => {
    const post = vi.fn();
    Object.defineProperty(window, 'parent', {
      value: { postMessage: post } as unknown as Window,
      configurable: true,
    });
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(423, { code: 'brokerLocked' }));
    vi.stubGlobal('fetch', m);

    expect(await requestBrokerLock()).toBe(false);
    expect(post).not.toHaveBeenCalled();

    Object.defineProperty(window, 'parent', { value: window, configurable: true });
  });

  it('does NOT throw or notify when there is no separate parent window', async () => {
    Object.defineProperty(window, 'parent', { value: window, configurable: true });
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { state: 'Locked' }));
    vi.stubGlobal('fetch', m);
    expect(await requestBrokerLock()).toBe(true);
  });
});

describe('status polling follows daemon lifecycle', () => {
  it('Pending then Approved completes once', async () => {
    vi.useFakeTimers();
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { state: 'Pending' }))
      .mockResolvedValueOnce(jsonResponse(200, { state: 'Approved' }));
    vi.stubGlobal('fetch', m);
    const p = waitForExperimentalWamStatus('req-1');
    await vi.advanceTimersByTimeAsync(700);
    expect(await p).toBe('approved');
    expect(m).toHaveBeenCalledTimes(2);
  });

  it('Denied grants nothing', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { state: 'Denied' }));
    vi.stubGlobal('fetch', m);
    expect(await waitForExperimentalWamStatus('req-1')).toBe('denied');
  });

  it('Unknown fails closed', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { state: 'Unknown' }));
    vi.stubGlobal('fetch', m);
    expect(await waitForExperimentalWamStatus('req-1')).toBe('unknown');
  });

  it('404 mid-flight maps to unavailable', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(404, { error: 'experimental_wam_not_enabled' }));
    vi.stubGlobal('fetch', m);
    expect(await waitForExperimentalWamStatus('req-1')).toBe('unavailable');
  });

  it('a malformed body (200 with no state) fails closed instead of polling forever', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { junk: 1 }));
    vi.stubGlobal('fetch', m);
    expect(await waitForExperimentalWamStatus('req-1')).toBe('transport_failure');
    expect(m).toHaveBeenCalledTimes(1); // did NOT keep polling
  });

  it('an unexpected state string fails closed instead of polling forever', async () => {
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { state: 'Weird' }));
    vi.stubGlobal('fetch', m);
    expect(await waitForExperimentalWamStatus('req-1')).toBe('transport_failure');
    expect(m).toHaveBeenCalledTimes(1);
  });

  it('transport failure is retried (no max-attempt cap) then resolves', async () => {
    vi.useFakeTimers();
    const m = fetchMock();
    m.mockRejectedValueOnce(new Error('net'))
      .mockRejectedValueOnce(new Error('net'))
      .mockResolvedValueOnce(jsonResponse(200, { state: 'Approved' }));
    vi.stubGlobal('fetch', m);
    const p = waitForExperimentalWamStatus('req-1');
    await vi.advanceTimersByTimeAsync(700);
    await vi.advanceTimersByTimeAsync(700);
    expect(await p).toBe('approved');
    expect(m).toHaveBeenCalledTimes(3);
  });

  it('cancellation via AbortSignal stops polling and returns cancelled', async () => {
    vi.useFakeTimers();
    const m = fetchMock();
    m.mockResolvedValue(jsonResponse(200, { state: 'Pending' }));
    vi.stubGlobal('fetch', m);
    const controller = new AbortController();
    const p = waitForExperimentalWamStatus('req-1', controller.signal);
    await vi.advanceTimersByTimeAsync(700);
    controller.abort();
    await vi.advanceTimersByTimeAsync(700);
    expect(await p).toBe('cancelled');
  });
});

describe('full flows never submit an approval field', () => {
  it('session flow approves and posts only requestId; no approval field in any request', async () => {
    const post = vi.fn();
    Object.defineProperty(window, 'parent', { value: { postMessage: post } as unknown as Window, configurable: true });
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { requestId: 'req-1' })) // initiate
      .mockResolvedValueOnce(jsonResponse(200, { state: 'Approved' })); // status
    vi.stubGlobal('fetch', m);

    expect(await authorizeSessionWithExperimentalWam()).toBe('approved');
    const [message] = post.mock.calls[0];
    expect(message).toEqual({ type: EXPERIMENTAL_WAM_REQUEST_TYPE, requestId: 'req-1' });
    for (const body of bodiesOf(m)) {
      for (const forbidden of APPROVAL_FIELDS) {
        expect(body).not.toContain(forbidden);
      }
    }
    Object.defineProperty(window, 'parent', { value: window, configurable: true });
  });

  it('a flow with no native host fails closed as transport_failure (never throws, never approves)', async () => {
    Object.defineProperty(window, 'parent', { value: window, configurable: true });
    const m = fetchMock();
    m.mockResolvedValueOnce(jsonResponse(200, { requestId: 'req-1' }));
    vi.stubGlobal('fetch', m);
    expect(await authorizeSessionWithExperimentalWam()).toBe('transport_failure');
  });
});

// Windows Hello enrollment courier (Batch 1b). The renderer NEVER runs the
// ceremony itself: it couriers a correlated request to the top-level shell and
// accepts a single bounded outcome ONLY from the parent window, from the exact
// same origin, with the exact result type, and with a matching requestId.
describe('windows hello enrollment courier', () => {
  function stubParent(post: ReturnType<typeof vi.fn>): Window {
    const fakeParent = { postMessage: post } as unknown as Window;
    Object.defineProperty(window, 'parent', { value: fakeParent, configurable: true });
    return fakeParent;
  }

  afterEach(() => {
    Object.defineProperty(window, 'parent', { value: window, configurable: true });
  });

  it('posts ONLY { type, requestId } to the parent with the exact origin', async () => {
    const post = vi.fn();
    stubParent(post);
    // Do not resolve — just assert the outbound message shape, then abort.
    const controller = new AbortController();
    const p = requestWindowsHelloEnrollmentFromNative(controller.signal);
    expect(post).toHaveBeenCalledTimes(1);
    const [message, origin] = post.mock.calls[0];
    expect(message.type).toBe(WINDOWS_HELLO_ENROLL_REQUEST_TYPE);
    expect(typeof message.requestId).toBe('string');
    expect(Object.keys(message)).toEqual(['type', 'requestId']);
    expect(origin).toBe(window.location.origin);
    controller.abort();
    expect(await p).toBe('cancelled');
  });

  it('fails closed to transport_failure when there is no native host parent', async () => {
    Object.defineProperty(window, 'parent', { value: window, configurable: true });
    expect(await requestWindowsHelloEnrollmentFromNative()).toBe('transport_failure');
  });

  it('resolves the bounded outcome from a correlated exact-origin parent reply', async () => {
    const post = vi.fn();
    const parent = stubParent(post);
    const p = requestWindowsHelloEnrollmentFromNative();
    const requestId = post.mock.calls[0][0].requestId as string;
    window.dispatchEvent(
      new MessageEvent('message', {
        data: { type: WINDOWS_HELLO_ENROLL_RESULT_TYPE, requestId, outcome: 'enrolled' },
        origin: window.location.origin,
        source: parent,
      }),
    );
    expect(await p).toBe('enrolled');
  });

  it('ignores replies with a wrong origin, wrong source, or wrong requestId', async () => {
    vi.useFakeTimers();
    const post = vi.fn();
    const parent = stubParent(post);
    const p = requestWindowsHelloEnrollmentFromNative();
    const requestId = post.mock.calls[0][0].requestId as string;

    // Wrong origin — ignored.
    window.dispatchEvent(new MessageEvent('message', {
      data: { type: WINDOWS_HELLO_ENROLL_RESULT_TYPE, requestId, outcome: 'enrolled' },
      origin: 'https://evil.example',
      source: parent,
    }));
    // Wrong source (not the parent) — ignored.
    window.dispatchEvent(new MessageEvent('message', {
      data: { type: WINDOWS_HELLO_ENROLL_RESULT_TYPE, requestId, outcome: 'enrolled' },
      origin: window.location.origin,
      source: {} as Window,
    }));
    // Wrong requestId — ignored.
    window.dispatchEvent(new MessageEvent('message', {
      data: { type: WINDOWS_HELLO_ENROLL_RESULT_TYPE, requestId: 'other', outcome: 'enrolled' },
      origin: window.location.origin,
      source: parent,
    }));

    // None settled it; the transport watchdog fires instead.
    await vi.advanceTimersByTimeAsync(150 * 1000);
    expect(await p).toBe('timeout');
  });

  it('maps an unrecognized outcome from a valid reply to failed (fail closed)', async () => {
    const post = vi.fn();
    const parent = stubParent(post);
    const p = requestWindowsHelloEnrollmentFromNative();
    const requestId = post.mock.calls[0][0].requestId as string;
    window.dispatchEvent(new MessageEvent('message', {
      data: { type: WINDOWS_HELLO_ENROLL_RESULT_TYPE, requestId, outcome: 'nonsense' },
      origin: window.location.origin,
      source: parent,
    }));
    expect(await p).toBe('failed');
  });

  it('ignores a replayed/duplicate reply after the request has already settled', async () => {
    const post = vi.fn();
    const parent = stubParent(post);
    const p = requestWindowsHelloEnrollmentFromNative();
    const requestId = post.mock.calls[0][0].requestId as string;

    // First valid reply settles the request to 'enrolled'.
    window.dispatchEvent(new MessageEvent('message', {
      data: { type: WINDOWS_HELLO_ENROLL_RESULT_TYPE, requestId, outcome: 'enrolled' },
      origin: window.location.origin,
      source: parent,
    }));
    expect(await p).toBe('enrolled');

    // A replayed reply with the SAME requestId but a DIFFERENT outcome arrives
    // after settle. The single-shot promise cannot re-settle, so the duplicate
    // cannot flip the resolved outcome or trigger a second switch. The listener
    // is gone, so this is an inert no-op (and must not throw).
    expect(() =>
      window.dispatchEvent(new MessageEvent('message', {
        data: { type: WINDOWS_HELLO_ENROLL_RESULT_TYPE, requestId, outcome: 'failed' },
        origin: window.location.origin,
        source: parent,
      })),
    ).not.toThrow();
    expect(await p).toBe('enrolled');
  });
});
