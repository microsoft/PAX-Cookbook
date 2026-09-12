/**
 * PHASE-1 Hello capability diagnostic — iframe-side orchestration
 * (cycle-01r-hello-capability-probe-repair). MEASUREMENT ONLY.
 *
 * Runs ONLY inside the build-gated isolated app when the native host injected
 * the read-only `window.__paxHelloDiag` marker (isolated build + the explicit
 * `PAXCB_HELLO_DIAG=1` one-shot flag). In a normal/attended launch the marker is
 * absent, `isHelloDiagEnabled()` is false, and none of this runs — the switch
 * surface is pristine.
 *
 * It observes the REAL `switchToWindowsHello` pipeline (which internally drives
 * the REAL platform-authenticator probe, backend select, and enrollment courier,
 * all of which self-record into `window.__paxHelloDiagIframe`), collects the
 * iframe-context predicates (secure-context, UVPAA, origin relation), asserts the
 * post-run provider/credential state, and posts ONE bounded, PII-free report to
 * the top-level shell. It NEVER calls `navigator.credentials.create()`, NEVER
 * captures any identity/credential material, and NEVER changes switch behavior.
 */
import { getSessionProviderStatus, probePlatformAuthenticatorAvailable } from './experimentalWam';

interface HelloDiagIframeBuffer {
  platformAuthenticatorProbeRan?: boolean;
  lastPlatformAvailable?: boolean;
  backendSelectAttempted?: boolean;
  backendSelectReason?: string | null;
  enrollmentRequestPosted?: boolean;
}

interface HelloDiagIframeReport {
  iframeSecureContext: boolean;
  iframePlatformAuthenticatorAvailable: boolean;
  originRelation: 'same_origin' | 'different_origin' | 'unavailable';
  backendSelectAttempted: boolean;
  backendSelectReason: string | null;
  enrollmentRequestPosted: boolean;
  selectedProviderStillWorkAccount: boolean;
  noCredentialCreated: boolean;
}

export function isHelloDiagEnabled(): boolean {
  try {
    const w = window as unknown as { __paxHelloDiag?: { enabled?: boolean } };
    return !!(w.__paxHelloDiag && w.__paxHelloDiag.enabled === true);
  } catch {
    return false;
  }
}

function readIframeBuffer(): HelloDiagIframeBuffer {
  try {
    const w = window as unknown as { __paxHelloDiagIframe?: HelloDiagIframeBuffer };
    return w.__paxHelloDiagIframe ?? {};
  } catch {
    return {};
  }
}

// The origin relationship between this iframe and the top-level shell. A
// cross-origin access to the parent's origin throws (that SecurityError is
// itself the 'different_origin' signal); a missing parent is 'unavailable'.
function computeOriginRelation(): 'same_origin' | 'different_origin' | 'unavailable' {
  try {
    const parent = window.parent;
    if (!parent || parent === window) {
      return 'unavailable';
    }
    let parentOrigin: string | null = null;
    try {
      parentOrigin = parent.location.origin;
    } catch {
      // Cross-origin parent access is blocked — that IS the different-origin case.
      return 'different_origin';
    }
    return parentOrigin === window.location.origin ? 'same_origin' : 'different_origin';
  } catch {
    return 'unavailable';
  }
}

// Read ONLY the bounded `registered` boolean from the lock-bypass status
// endpoint. Deliberately ignores credentialIds and every other field so no
// credential material is ever read into the diagnostic.
async function readRegisteredFlag(): Promise<boolean> {
  try {
    const response = await fetch('/api/v1/broker/webauthn/status', {
      method: 'GET',
      headers: { Accept: 'application/json' },
    });
    if (!response.ok) {
      return false;
    }
    const body = (await response.json()) as { registered?: unknown } | null;
    return !!(body && body.registered === true);
  } catch {
    return false;
  }
}

function postIframeReport(report: HelloDiagIframeReport): void {
  try {
    const parent = window.parent;
    if (!parent || parent === window) {
      return;
    }
    parent.postMessage(
      { type: 'cookbook:hello-diag-iframe-report', payload: report },
      window.location.origin,
    );
  } catch {
    // Non-fatal: the shell's safety-net timer still persists the shell-side view.
  }
}

// Drive the REAL switch handler once and report the bounded observations. The
// caller passes the component's actual `switchToWindowsHello` callback so the
// production code path — not a parallel harness — is exercised.
export async function runHelloCapabilityIframeProbe(
  switchToWindowsHello: () => Promise<void>,
): Promise<void> {
  let iframeSecureContext = false;
  try {
    iframeSecureContext = !!window.isSecureContext;
  } catch {
    iframeSecureContext = false;
  }

  const originRelation = computeOriginRelation();
  const iframePlatformAuthenticatorAvailable = await probePlatformAuthenticatorAvailable();
  const registeredBefore = await readRegisteredFlag();

  // Exercise the REAL switch pipeline. On the enrollment-required branch it
  // posts the enrollment request to the shell, which auto-cancels the affordance
  // (no ceremony, no create()), so this resolves cleanly leaving work_account.
  try {
    await switchToWindowsHello();
  } catch {
    // switchToWindowsHello swallows its own errors; this is belt-and-suspenders.
  }

  const buffer = readIframeBuffer();
  const providerAfter = await getSessionProviderStatus();
  const registeredAfter = await readRegisteredFlag();

  const report: HelloDiagIframeReport = {
    iframeSecureContext,
    iframePlatformAuthenticatorAvailable,
    originRelation,
    backendSelectAttempted: buffer.backendSelectAttempted === true,
    backendSelectReason:
      typeof buffer.backendSelectReason === 'string' ? buffer.backendSelectReason : null,
    enrollmentRequestPosted: buffer.enrollmentRequestPosted === true,
    selectedProviderStillWorkAccount: providerAfter.selectedProvider === 'work_account',
    // No credential existed before AND none exists after => nothing was created.
    noCredentialCreated: registeredBefore === false && registeredAfter === false,
  };

  postIframeReport(report);
}

/**
 * PHASE-2 attended Hello capability diagnostic — iframe-side observer
 * (cycle-01r-hello-capability-probe-repair). MEASUREMENT ONLY.
 *
 * Unlike the PHASE-1 auto-drive probe above (which is env-flag gated and lets
 * the shell auto-cancel the enroll affordance so no ceremony fires), this
 * observer runs when a REAL operator clicks "Use Windows Hello" in an isolated
 * ATTENDED launch. It wraps the component's actual `switchToWindowsHello`
 * callback so the production switch pipeline is exercised unchanged, then reports
 * the bounded post-run facts — INCLUDING whether the backend selection actually
 * persisted windows_hello and the final selected provider. It NEVER calls
 * `navigator.credentials.create()` (the gesture-dependent ceremony is owned by
 * the top-level shell from the operator's own click), NEVER reads identity or
 * credential material, and NEVER alters switch behavior.
 */

// The bounded class of a create() ceremony rejection, derived ONLY from the
// DOMException NAME. Never the message or stack (which can carry free text).
export type CreateFailureClass =
  | 'not_allowed'
  | 'security'
  | 'abort'
  | 'timeout'
  | 'constraint'
  | 'unknown';

// The bounded final provider a switch resolved to.
export type FinalSelectedProvider = 'work_account' | 'windows_hello' | 'unavailable';

interface HelloAttendedIframeReport {
  iframeSecureContext: boolean;
  iframePlatformAuthenticatorAvailable: boolean;
  originRelation: 'same_origin' | 'different_origin' | 'unavailable';
  backendSelectAttempted: boolean;
  backendSelectReason: string | null;
  enrollmentRequestPosted: boolean;
  backendSelectPersisted: boolean;
  finalSelectedProvider: FinalSelectedProvider;
}

// Pure, total classification of a WebAuthn create() rejection name into a
// bounded diagnostic class. Total over ALL inputs: any unrecognized, empty,
// null, or undefined name maps to 'unknown'. No PII: it inspects only the
// DOMException.name token, never the message/stack/credential. Exported so it is
// unit-testable in isolation and so the same contract can be mirrored by the
// top-level shell (which computes it from a real ceremony rejection).
export function classifyCreateFailureClass(name: string | null | undefined): CreateFailureClass {
  switch (name) {
    case 'NotAllowedError':
      return 'not_allowed';
    case 'SecurityError':
      return 'security';
    case 'AbortError':
      return 'abort';
    case 'TimeoutError':
      return 'timeout';
    case 'ConstraintError':
      return 'constraint';
    default:
      return 'unknown';
  }
}

// Pure, total classification of a session-provider status into the bounded
// diagnostic final-provider set. Anything other than the two known providers
// (including recovery_required, null, or an unexpected value) is 'unavailable'.
export function classifyFinalProvider(
  selectedProvider: string | null | undefined,
): FinalSelectedProvider {
  if (selectedProvider === 'windows_hello') {
    return 'windows_hello';
  }
  if (selectedProvider === 'work_account') {
    return 'work_account';
  }
  return 'unavailable';
}

export function isHelloAttendedDiagEnabled(): boolean {
  try {
    const w = window as unknown as { __paxHelloAttendedDiag?: { enabled?: boolean } };
    return !!(w.__paxHelloAttendedDiag && w.__paxHelloAttendedDiag.enabled === true);
  } catch {
    return false;
  }
}

function postAttendedIframeReport(report: HelloAttendedIframeReport): void {
  try {
    const parent = window.parent;
    if (!parent || parent === window) {
      return;
    }
    parent.postMessage(
      { type: 'cookbook:hello-attended-iframe-report', payload: report },
      window.location.origin,
    );
  } catch {
    // Non-fatal: the shell's safety-net timer still persists the shell-side view.
  }
}

// Wrap the REAL switch handler for a human-driven attended switch and report the
// bounded observations. The caller passes the component's actual
// `switchToWindowsHello` callback so the production code path — not a parallel
// harness — is exercised. This observer performs NO ceremony itself; the top-
// level shell owns the create() ceremony from the operator's own affordance
// click and records the ceremony-side facts separately.
export async function runHelloAttendedIframeObserver(
  switchToWindowsHello: () => Promise<void>,
): Promise<void> {
  let iframeSecureContext = false;
  try {
    iframeSecureContext = !!window.isSecureContext;
  } catch {
    iframeSecureContext = false;
  }

  const originRelation = computeOriginRelation();
  const iframePlatformAuthenticatorAvailable = await probePlatformAuthenticatorAvailable();

  // Exercise the REAL switch pipeline exactly as the button click does. On the
  // enrollment-required branch it couriers the request to the shell, which — in
  // an ATTENDED launch — presents the affordance for the operator's gesture (no
  // auto-cancel). This resolves once the operator completes or dismisses it.
  try {
    await switchToWindowsHello();
  } catch {
    // switchToWindowsHello swallows its own errors; this is belt-and-suspenders.
  }

  const buffer = readIframeBuffer();
  const providerAfter = await getSessionProviderStatus();
  const finalSelectedProvider = classifyFinalProvider(providerAfter.selectedProvider);

  const report: HelloAttendedIframeReport = {
    iframeSecureContext,
    iframePlatformAuthenticatorAvailable,
    originRelation,
    backendSelectAttempted: buffer.backendSelectAttempted === true,
    backendSelectReason:
      typeof buffer.backendSelectReason === 'string' ? buffer.backendSelectReason : null,
    enrollmentRequestPosted: buffer.enrollmentRequestPosted === true,
    // The switch persisted windows_hello iff the final selected provider is it.
    backendSelectPersisted: finalSelectedProvider === 'windows_hello',
    finalSelectedProvider,
  };

  postAttendedIframeReport(report);
}
