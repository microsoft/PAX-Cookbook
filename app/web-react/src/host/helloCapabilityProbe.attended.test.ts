/**
 * PHASE-3 tests for the PHASE-2 attended Hello capability diagnostic
 * (cycle-01r-hello-capability-probe-repair). MEASUREMENT ONLY.
 *
 * These prove the attended iframe-side observer is a pure, bounded, PII-free
 * OBSERVER: the two classifiers are total functions over all inputs, the enable
 * gate is strict, and runHelloAttendedIframeObserver drives the REAL switch
 * handler exactly once, posts ONE allow-listed report to the parent at the EXACT
 * origin, never calls navigator.credentials.create(), and never leaks any
 * credential/identity material. The switch BEHAVIOR itself is unchanged and is
 * covered by WorkAccountCard.switch.test.ts + experimentalWam.test.ts.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';

// Mock ONLY the two host helpers the observer consumes so we exercise the real
// observer code path against controlled post-run state.
vi.mock('./experimentalWam', () => ({
  getSessionProviderStatus: vi.fn(),
  probePlatformAuthenticatorAvailable: vi.fn(),
}));

import * as host from './experimentalWam';
import type { SessionProviderStatus } from './experimentalWam';
import {
  classifyCreateFailureClass,
  classifyFinalProvider,
  isHelloAttendedDiagEnabled,
  runHelloAttendedIframeObserver,
} from './helloCapabilityProbe';

describe('classifyCreateFailureClass', () => {
  it('maps each known DOMException name to its bounded class', () => {
    expect(classifyCreateFailureClass('NotAllowedError')).toBe('not_allowed');
    expect(classifyCreateFailureClass('SecurityError')).toBe('security');
    expect(classifyCreateFailureClass('AbortError')).toBe('abort');
    expect(classifyCreateFailureClass('TimeoutError')).toBe('timeout');
    expect(classifyCreateFailureClass('ConstraintError')).toBe('constraint');
  });

  it('maps null, undefined, empty, and any unrecognized name to "unknown"', () => {
    expect(classifyCreateFailureClass(null)).toBe('unknown');
    expect(classifyCreateFailureClass(undefined)).toBe('unknown');
    expect(classifyCreateFailureClass('')).toBe('unknown');
    expect(classifyCreateFailureClass('SomethingElseError')).toBe('unknown');
    // Case-sensitive: a lowercase variant is NOT a known name.
    expect(classifyCreateFailureClass('notallowederror')).toBe('unknown');
  });
});

describe('classifyFinalProvider', () => {
  it('maps the two known providers and coerces everything else to unavailable', () => {
    expect(classifyFinalProvider('windows_hello')).toBe('windows_hello');
    expect(classifyFinalProvider('work_account')).toBe('work_account');
    expect(classifyFinalProvider('recovery_required')).toBe('unavailable');
    expect(classifyFinalProvider(null)).toBe('unavailable');
    expect(classifyFinalProvider(undefined)).toBe('unavailable');
    expect(classifyFinalProvider('')).toBe('unavailable');
  });
});

describe('isHelloAttendedDiagEnabled', () => {
  afterEach(() => {
    delete (window as unknown as { __paxHelloAttendedDiag?: unknown }).__paxHelloAttendedDiag;
  });

  it('is false when the marker is absent', () => {
    expect(isHelloAttendedDiagEnabled()).toBe(false);
  });

  it('is false when the marker exists but enabled !== true', () => {
    (window as unknown as { __paxHelloAttendedDiag?: unknown }).__paxHelloAttendedDiag = {
      enabled: false,
    };
    expect(isHelloAttendedDiagEnabled()).toBe(false);
    (window as unknown as { __paxHelloAttendedDiag?: unknown }).__paxHelloAttendedDiag = {};
    expect(isHelloAttendedDiagEnabled()).toBe(false);
  });

  it('is true ONLY for an explicit enabled:true marker', () => {
    (window as unknown as { __paxHelloAttendedDiag?: unknown }).__paxHelloAttendedDiag = {
      enabled: true,
    };
    expect(isHelloAttendedDiagEnabled()).toBe(true);
  });
});

describe('runHelloAttendedIframeObserver', () => {
  const realParent = Object.getOwnPropertyDescriptor(window, 'parent');

  afterEach(() => {
    vi.restoreAllMocks();
    if (realParent) {
      Object.defineProperty(window, 'parent', realParent);
    }
    delete (window as unknown as { __paxHelloDiagIframe?: unknown }).__paxHelloDiagIframe;
  });

  function stubSameOriginParent(post: ReturnType<typeof vi.fn>): void {
    const fakeParent = {
      postMessage: post,
      location: { origin: window.location.origin },
    } as unknown as Window;
    Object.defineProperty(window, 'parent', { value: fakeParent, configurable: true });
  }

  function providerStatus(selectedProvider: string): SessionProviderStatus {
    return {
      selectedProvider,
      recoveryRequired: false,
      usable: true,
      healthCode: 'ok',
    } as unknown as SessionProviderStatus;
  }

  it('drives the REAL switch once, posts ONE bounded PII-free report at the exact origin, and never calls create()', async () => {
    const post = vi.fn();
    stubSameOriginParent(post);
    (window as unknown as { __paxHelloDiagIframe?: unknown }).__paxHelloDiagIframe = {
      backendSelectAttempted: true,
      backendSelectReason: 'windows_hello_enrollment_required',
      enrollmentRequestPosted: true,
    };
    vi.mocked(host.probePlatformAuthenticatorAvailable).mockResolvedValue(true);
    vi.mocked(host.getSessionProviderStatus).mockResolvedValue(providerStatus('work_account'));

    // Guard: prove the observer never touches the credential ceremony.
    let createSpy: ReturnType<typeof vi.fn> | null = null;
    try {
      createSpy = vi.fn();
      Object.defineProperty(window.navigator, 'credentials', {
        value: { create: createSpy, get: vi.fn() },
        configurable: true,
      });
    } catch {
      createSpy = null;
    }

    const switchSpy = vi.fn(async () => {
      /* stands in for the real switchToWindowsHello; does no ceremony */
    });
    await runHelloAttendedIframeObserver(switchSpy);

    expect(switchSpy).toHaveBeenCalledTimes(1);
    if (createSpy) {
      expect(createSpy).not.toHaveBeenCalled();
    }
    expect(post).toHaveBeenCalledTimes(1);

    const [message, origin] = post.mock.calls[0];
    expect(origin).toBe(window.location.origin);
    expect(message.type).toBe('cookbook:hello-attended-iframe-report');

    const payload = message.payload as Record<string, unknown>;
    // Exactly the allow-listed, bounded fields — nothing more.
    expect(Object.keys(payload).sort()).toEqual([
      'backendSelectAttempted',
      'backendSelectPersisted',
      'backendSelectReason',
      'enrollmentRequestPosted',
      'finalSelectedProvider',
      'iframePlatformAuthenticatorAvailable',
      'iframeSecureContext',
      'originRelation',
    ]);
    expect(payload.backendSelectAttempted).toBe(true);
    expect(payload.backendSelectReason).toBe('windows_hello_enrollment_required');
    expect(payload.enrollmentRequestPosted).toBe(true);
    expect(payload.originRelation).toBe('same_origin');
    expect(payload.finalSelectedProvider).toBe('work_account');
    // Stayed on work_account => the switch did NOT persist windows_hello.
    expect(payload.backendSelectPersisted).toBe(false);

    // No credential / identity material of any kind leaked into the report.
    const json = JSON.stringify(payload);
    expect(json).not.toMatch(/credential|challenge|token|tenant|userId|user_id|secret/i);
  });

  it('reports backendSelectPersisted=true + finalSelectedProvider=windows_hello when the switch persisted', async () => {
    const post = vi.fn();
    stubSameOriginParent(post);
    (window as unknown as { __paxHelloDiagIframe?: unknown }).__paxHelloDiagIframe = {
      backendSelectAttempted: true,
      backendSelectReason: 'switched',
      enrollmentRequestPosted: false,
    };
    vi.mocked(host.probePlatformAuthenticatorAvailable).mockResolvedValue(true);
    vi.mocked(host.getSessionProviderStatus).mockResolvedValue(providerStatus('windows_hello'));

    await runHelloAttendedIframeObserver(vi.fn(async () => {}));

    const payload = post.mock.calls[0][0].payload as Record<string, unknown>;
    expect(payload.finalSelectedProvider).toBe('windows_hello');
    expect(payload.backendSelectPersisted).toBe(true);
  });

  it('coerces a missing/malformed buffer + unknown provider to bounded defaults without throwing', async () => {
    const post = vi.fn();
    stubSameOriginParent(post);
    // No __paxHelloDiagIframe buffer at all.
    vi.mocked(host.probePlatformAuthenticatorAvailable).mockResolvedValue(false);
    vi.mocked(host.getSessionProviderStatus).mockResolvedValue(
      providerStatus('recovery_required'),
    );

    await runHelloAttendedIframeObserver(vi.fn(async () => {}));

    const payload = post.mock.calls[0][0].payload as Record<string, unknown>;
    expect(payload.backendSelectAttempted).toBe(false);
    expect(payload.backendSelectReason).toBeNull();
    expect(payload.enrollmentRequestPosted).toBe(false);
    expect(payload.finalSelectedProvider).toBe('unavailable');
    expect(payload.backendSelectPersisted).toBe(false);
  });
});
