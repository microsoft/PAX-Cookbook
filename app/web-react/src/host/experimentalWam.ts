// Experimental Entra WAM renderer flow (Track 1 / T1-S2B).
//
// The renderer is a COURIER ONLY. It may ask the daemon to initiate a session or
// operation request over authenticated HTTP, it receives only an opaque
// requestId and a bounded status, and it hands the requestId to the native host
// so the native window can run WAM and deliver the bounded result to the daemon
// over native IPC. The renderer NEVER receives the native descriptor, salt,
// expected fingerprint, claims, account handle, granted scopes, or native
// result, and it NEVER submits an approval field.
//
// Windows Hello remains the primary/default action everywhere; WAM is always an
// explicit operator choice with no automatic fallback in either direction.

const TOKEN_KEY = 'cookbook.sessionToken';

// The ONLY message the renderer sends toward the native host (via the same-origin
// legacy-shell courier). It is requestId-only.
export const EXPERIMENTAL_WAM_REQUEST_TYPE = 'cookbook:experimental-wam-request';

// Windows Hello enrollment courier message types. The renderer runs INSIDE the
// same-origin iframe and the WebAuthn create() ceremony must run in the TOP-LEVEL
// shell document (which owns the native window / HWND and the existing ceremony).
// Rather than duplicate the ceremony, the renderer couriers a correlated request
// to the parent shell and awaits a single bounded outcome. The request carries
// ONLY { type, requestId }; the reply carries ONLY { type, requestId, outcome }.
export const WINDOWS_HELLO_ENROLL_REQUEST_TYPE = 'cookbook:windows-hello-enroll-request';
export const WINDOWS_HELLO_ENROLL_RESULT_TYPE = 'cookbook:windows-hello-enroll-result';

// Fire-and-forget signal the renderer posts to the top-level shell AFTER it has
// successfully asked the broker to lock. The lock overlay lives in the top-level
// shell and only mounts on a shell-window 423 (dispatched by api.js); an
// iframe-initiated POST /broker/lock returns 200 to the iframe and would
// otherwise leave the top-level shell unaware until its own next request hit a
// 423 (there is no background poll). This message tells the shell to re-check
// lock state now and present the lock screen promptly. It carries ONLY { type }
// and is delivered to the exact same origin (never '*').
export const BROKER_LOCK_INITIATED_TYPE = 'cookbook:broker-lock-initiated';

// Bounded enrollment outcomes surfaced from the parent-shell ceremony. No native
// descriptor, challenge, credential, or ceremony internals are ever exposed here.
export type WindowsHelloEnrollmentOutcome =
  | 'enrolled'
  | 'cancelled'
  | 'unavailable'
  | 'failed'
  | 'timeout'
  | 'transport_failure';

// Bounded status outcomes surfaced to business UI. Daemon/native reason internals
// are never exposed here.
export type WamFlowOutcome =
  | 'approved'
  | 'denied'
  | 'unavailable'
  | 'cancelled'
  | 'transport_failure'
  | 'unknown';

export interface WamCapability {
  available: boolean;
  providerId?: string;
}

// The bounded live configuration state served by the daemon (recomputed on every
// request). Carries NO tenant/client identifier — only the bounded state code
// plus non-identifying flags. Consumed by the Settings experience and the lock
// overlay. Any of the bounded states may appear; the UI keys on `state`.
export interface WamConfigStateInfo {
  state: string;
  providerId: string | null;
  disabled: boolean;
  configured: boolean;
  capabilityAvailable: boolean;
  verifiedUtc: string | null;
}

// The result of a Settings mutation: bounded success + reason plus the fresh
// live state after the attempt.
export interface WamActionResult {
  ok: boolean;
  reason: string | null;
  state: WamConfigStateInfo;
}

// The administrator-details disclosure (Unlocked route only): the non-secret
// tenant and public-client identifiers, or unavailable.
export interface WamAdminDetails {
  available: boolean;
  tenantId?: string;
  clientId?: string;
}

const UNAVAILABLE_STATE: WamConfigStateInfo = {
  state: 'unavailable_in_this_build',
  providerId: null,
  disabled: false,
  configured: false,
  capabilityAvailable: false,
  verifiedUtc: null,
};

function normalizeState(body: Record<string, unknown> | null): WamConfigStateInfo {
  if (!body || typeof body.state !== 'string') {
    return UNAVAILABLE_STATE;
  }
  return {
    state: body.state,
    providerId: typeof body.providerId === 'string' ? body.providerId : null,
    disabled: body.disabled === true,
    configured: body.configured === true,
    capabilityAvailable: body.capabilityAvailable === true,
    verifiedUtc: typeof body.verifiedUtc === 'string' ? body.verifiedUtc : null,
  };
}

const STATUS_POLL_INTERVAL_MS = 600;

function getToken(): string | null {
  try {
    return window.sessionStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

function buildHeaders(stateChanging: boolean): Record<string, string> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  const token = getToken();
  if (token) {
    headers.Authorization = 'Bearer ' + token;
  }
  if (stateChanging) {
    headers['X-Cookbook-Request'] = '1';
    headers['Content-Type'] = 'application/json';
  }
  return headers;
}

async function readJson(response: Response): Promise<Record<string, unknown> | null> {
  try {
    const text = await response.text();
    if (!text) {
      return null;
    }
    const parsed = JSON.parse(text) as unknown;
    return typeof parsed === 'object' && parsed !== null ? (parsed as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

// Bounded capability probe. Returns { available:false } on any failure so the UI
// fails closed and renders zero WAM footprint.
export async function getExperimentalWamCapability(signal?: AbortSignal): Promise<WamCapability> {
  try {
    const response = await fetch('/api/v1/broker/experimental/wam/capability', {
      method: 'GET',
      headers: buildHeaders(false),
      signal,
    });
    if (!response.ok) {
      return { available: false };
    }
    const body = await readJson(response);
    // Available ONLY for an explicit available:true with the exact provider id.
    // A missing, unknown, or differently-cased provider id fails closed.
    if (body && body.available === true && body.providerId === 'entra-wam') {
      return { available: true, providerId: 'entra-wam' };
    }
    return { available: false };
  } catch {
    return { available: false };
  }
}

interface InitiateResult {
  requestId: string | null;
  outcome: WamFlowOutcome | null; // set only on failure
}

// Ask the daemon to mint an opaque requestId for a session unlock. The renderer
// supplies only the purpose; there is no operation/recipe variant.
export async function initiateExperimentalWam(
  signal?: AbortSignal,
): Promise<InitiateResult> {
  const payload: Record<string, unknown> = { purpose: 'session' };
  try {
    const response = await fetch('/api/v1/broker/experimental/wam/initiate', {
      method: 'POST',
      headers: buildHeaders(true),
      body: JSON.stringify(payload),
      signal,
    });
    if (response.status === 404) {
      return { requestId: null, outcome: 'unavailable' };
    }
    if (!response.ok) {
      return { requestId: null, outcome: 'denied' };
    }
    const body = await readJson(response);
    const requestId = body && typeof body.requestId === 'string' ? body.requestId : null;
    if (!requestId) {
      return { requestId: null, outcome: 'transport_failure' };
    }
    return { requestId, outcome: null };
  } catch (err) {
    return { requestId: null, outcome: signal?.aborted ? 'cancelled' : 'transport_failure' };
  }
}

// Hand the opaque requestId to the native host through the same-origin legacy
// shell courier. Posts ONLY { type, requestId } to the parent with an explicit
// target origin. Fails closed (returns false) in standalone/dev mode where there
// is no parent host.
export function sendExperimentalWamRequestToNative(requestId: string): boolean {
  if (!requestId) {
    return false;
  }
  try {
    const parent = window.parent;
    if (!parent || parent === window) {
      // No native host (standalone browser / dev): fail closed.
      return false;
    }
    parent.postMessage(
      { type: EXPERIMENTAL_WAM_REQUEST_TYPE, requestId },
      window.location.origin,
    );
    return true;
  } catch {
    return false;
  }
}

function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal?.aborted) {
      resolve();
      return;
    }
    const timer = setTimeout(() => {
      cleanup();
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(timer);
      cleanup();
      resolve();
    };
    const cleanup = () => signal?.removeEventListener('abort', onAbort);
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

// Poll the daemon status until a terminal state, cancellation, or the provider
// becoming unavailable. Follows daemon lifecycle, not an invented UI timeout:
// Pending keeps polling; Approved/Denied/Unknown are terminal; a transient
// transport failure is retried (no arbitrary maximum-attempt count); cancellation
// stops cleanly. Unknown after a successfully minted request fails closed.
export async function waitForExperimentalWamStatus(
  requestId: string,
  signal?: AbortSignal,
): Promise<WamFlowOutcome> {
  while (!signal?.aborted) {
    let response: Response;
    try {
      response = await fetch('/api/v1/broker/experimental/wam/status', {
        method: 'POST',
        headers: buildHeaders(true),
        body: JSON.stringify({ requestId }),
        signal,
      });
    } catch {
      if (signal?.aborted) {
        return 'cancelled';
      }
      // Transport failure is bounded and retryable: poll again after a delay.
      await delay(STATUS_POLL_INTERVAL_MS, signal);
      continue;
    }

    if (signal?.aborted) {
      return 'cancelled';
    }
    if (response.status === 404) {
      return 'unavailable';
    }

    const body = await readJson(response);
    const state = body && typeof body.state === 'string' ? body.state : null;
    if (state === 'Approved') {
      return 'approved';
    }
    if (state === 'Denied') {
      return 'denied';
    }
    if (state === 'Unknown') {
      // Minted then pruned/expired, or never resolvable: fail closed.
      return 'unknown';
    }
    if (state === 'Pending') {
      // The only value that continues polling.
      await delay(STATUS_POLL_INTERVAL_MS, signal);
      continue;
    }
    // A malformed body (200 with no/invalid state) or an unexpected state
    // string is a hard fail-closed outcome — never poll forever on garbage.
    return 'transport_failure';
  }
  return 'cancelled';
}

// Orchestrate a full session-unlock WAM flow: initiate(session) -> courier ->
// poll. Returns a single bounded outcome. Never falls back to Windows Hello.
export async function authorizeSessionWithExperimentalWam(signal?: AbortSignal): Promise<WamFlowOutcome> {
  const initiated = await initiateExperimentalWam(signal);
  if (!initiated.requestId) {
    return initiated.outcome ?? 'transport_failure';
  }
  if (!sendExperimentalWamRequestToNative(initiated.requestId)) {
    return 'transport_failure';
  }
  return waitForExperimentalWamStatus(initiated.requestId, signal);
}

// ---- Settings configuration workflow (T1-S3) ---------------------------------
// Live bounded config-state + the mutation actions the Settings experience uses.
// Every function fails closed to unavailable_in_this_build so a stable/default
// build (which returns unavailable) renders no Work-account footprint.

// GET the live bounded config-state. Always answers; fails closed on any error.
export async function getWamConfigState(signal?: AbortSignal): Promise<WamConfigStateInfo> {
  try {
    const response = await fetch('/api/v1/broker/experimental/wam/config-state', {
      method: 'GET',
      headers: buildHeaders(false),
      signal,
    });
    if (!response.ok) {
      return UNAVAILABLE_STATE;
    }
    return normalizeState(await readJson(response));
  } catch {
    return UNAVAILABLE_STATE;
  }
}

async function postAction(path: string, body?: unknown, signal?: AbortSignal): Promise<WamActionResult> {
  try {
    const response = await fetch(path, {
      method: 'POST',
      headers: buildHeaders(true),
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    });
    const parsed = await readJson(response);
    const ok = response.ok && parsed?.ok === true;
    const reason = parsed && typeof parsed.reason === 'string' ? parsed.reason : ok ? null : 'request_failed';
    return { ok, reason, state: normalizeState(parsed) };
  } catch (err) {
    return {
      ok: false,
      reason: signal?.aborted ? 'cancelled' : 'transport_failure',
      state: UNAVAILABLE_STATE,
    };
  }
}

// Import a helper setup-result (the whole JSON body is the setup result). Strict
// parsing + persistence happen on the daemon; on success the state becomes
// configured_unverified — never ready.
export async function importWamSetupResult(setupResultJson: string, signal?: AbortSignal): Promise<WamActionResult> {
  try {
    const response = await fetch('/api/v1/broker/experimental/wam/import', {
      method: 'POST',
      headers: buildHeaders(true),
      body: setupResultJson,
      signal,
    });
    const parsed = await readJson(response);
    const ok = response.ok && parsed?.ok === true;
    const reason = parsed && typeof parsed.reason === 'string' ? parsed.reason : ok ? null : 'invalid_setup_result';
    return { ok, reason, state: normalizeState(parsed) };
  } catch (err) {
    return {
      ok: false,
      reason: signal?.aborted ? 'cancelled' : 'transport_failure',
      state: UNAVAILABLE_STATE,
    };
  }
}

// Run the fixed guarded helper's read-only Verify and transition to ready only on
// exact success. Requires only an Unlocked session (either selected provider);
// OAuth verification never requires Windows Hello.
export function verifyWamSetup(signal?: AbortSignal): Promise<WamActionResult> {
  return postAction('/api/v1/broker/experimental/wam/verify', undefined, signal);
}

// Local enable/disable (retains identifiers and cloud registration).
export function setWamEnabled(enabled: boolean, signal?: AbortSignal): Promise<WamActionResult> {
  return postAction(
    enabled ? '/api/v1/broker/experimental/wam/enable' : '/api/v1/broker/experimental/wam/disable',
    undefined,
    signal,
  );
}

// Remove the local configuration only (never cloud objects). Requires only an
// Unlocked session (either selected provider); never Windows Hello.
export function removeWamLocalConfig(signal?: AbortSignal): Promise<WamActionResult> {
  return postAction('/api/v1/broker/experimental/wam/remove', undefined, signal);
}

// GET the administrator-details disclosure (Unlocked only). Fails closed to
// unavailable.
export async function getWamAdminDetails(signal?: AbortSignal): Promise<WamAdminDetails> {
  try {
    const response = await fetch('/api/v1/broker/experimental/wam/admin-details', {
      method: 'GET',
      headers: buildHeaders(false),
      signal,
    });
    if (!response.ok) {
      return { available: false };
    }
    const body = await readJson(response);
    if (body && body.available === true && typeof body.tenantId === 'string' && typeof body.clientId === 'string') {
      return { available: true, tenantId: body.tenantId, clientId: body.clientId };
    }
    return { available: false };
  } catch {
    return { available: false };
  }
}

export interface WamDeprovisionPlan {
  ok: boolean;
  reason: string | null;
  planId: string | null;
  categories: Record<string, unknown> | null;
}

// Prepare (dry-run) the deprovision plan. Read-only; requires only an Unlocked
// session (either selected provider). Returns a short-lived plan id bound to the
// current configuration.
export async function prepareWamDeprovision(signal?: AbortSignal): Promise<WamDeprovisionPlan> {
  try {
    const response = await fetch('/api/v1/broker/experimental/wam/prepare-deprovision', {
      method: 'POST',
      headers: buildHeaders(true),
      signal,
    });
    const body = await readJson(response);
    const ok = response.ok && body?.ok === true;
    return {
      ok,
      reason: body && typeof body.reason === 'string' ? body.reason : ok ? null : 'request_failed',
      planId: body && typeof body.planId === 'string' ? body.planId : null,
      categories: body && typeof body.categories === 'object' ? (body.categories as Record<string, unknown>) : null,
    };
  } catch (err) {
    return { ok: false, reason: signal?.aborted ? 'cancelled' : 'transport_failure', planId: null, categories: null };
  }
}

// Execute the deprovision using an exact short-lived plan id. Removes local
// configuration only after verified cloud cleanup.
export function executeWamDeprovision(planId: string, signal?: AbortSignal): Promise<WamActionResult> {
  return postAction('/api/v1/broker/experimental/wam/execute-deprovision', { planId }, signal);
}

// ----------------------------------------------------------------------------
// Selected session-authentication provider (mutually-exclusive model).
//
// PAX Cookbook uses exactly ONE selected session provider at a time — Windows
// Hello OR the work account, never both, with no automatic fallback in either
// direction. These helpers read the bounded selected-provider status and drive
// the two explicit, atomic provider switches. All fail closed.
// ----------------------------------------------------------------------------

export type SelectedProviderId = 'windows_hello' | 'work_account' | 'recovery_required';

// Bounded selected-provider status. Carries NO tenant/client identifier or
// credential — only the selected provider, a usable flag, a bounded health code,
// and whether recovery (Setup repair) is required.
export interface SessionProviderStatus {
  selectedProvider: SelectedProviderId;
  recoveryRequired: boolean;
  usable: boolean;
  healthCode: string;
}

const RECOVERY_PROVIDER_STATUS: SessionProviderStatus = {
  selectedProvider: 'recovery_required',
  recoveryRequired: true,
  usable: false,
  healthCode: 'recovery_required',
};

function normalizeProviderStatus(body: Record<string, unknown> | null): SessionProviderStatus {
  const selected = body && typeof body.selectedProvider === 'string' ? body.selectedProvider : null;
  if (selected !== 'windows_hello' && selected !== 'work_account' && selected !== 'recovery_required') {
    return RECOVERY_PROVIDER_STATUS;
  }
  return {
    selectedProvider: selected,
    recoveryRequired: body?.recoveryRequired === true || selected === 'recovery_required',
    usable: body?.usable === true,
    healthCode: body && typeof body.healthCode === 'string' ? body.healthCode : 'unknown',
  };
}

// GET the bounded selected-provider status (lock-bypass; readable while Locked).
// Fails closed to the recovery status on any error so the UI never silently
// assumes a provider.
export async function getSessionProviderStatus(signal?: AbortSignal): Promise<SessionProviderStatus> {
  try {
    const response = await fetch('/api/v1/broker/session-provider', {
      method: 'GET',
      headers: buildHeaders(false),
      signal,
    });
    if (!response.ok) {
      return RECOVERY_PROVIDER_STATUS;
    }
    return normalizeProviderStatus(await readJson(response));
  } catch {
    return RECOVERY_PROVIDER_STATUS;
  }
}

export interface ProviderSwitchResult {
  ok: boolean;
  reason: string | null;
  selectedProvider: SelectedProviderId | null;
}

async function postProviderSwitch(
  path: string,
  signal?: AbortSignal,
  body?: unknown,
): Promise<ProviderSwitchResult> {
  try {
    const response = await fetch(path, {
      method: 'POST',
      headers: buildHeaders(true),
      body: body === undefined ? undefined : JSON.stringify(body),
      signal,
    });
    const parsed = await readJson(response);
    const ok = response.ok && parsed?.ok === true;
    const selected =
      parsed && typeof parsed.selectedProvider === 'string' &&
      (parsed.selectedProvider === 'windows_hello' || parsed.selectedProvider === 'work_account')
        ? (parsed.selectedProvider as SelectedProviderId)
        : null;
    return {
      ok,
      reason: parsed && typeof parsed.reason === 'string' ? parsed.reason : ok ? null : 'request_failed',
      selectedProvider: selected,
    };
  } catch (err) {
    return {
      ok: false,
      reason: signal?.aborted ? 'cancelled' : 'transport_failure',
      selectedProvider: null,
    };
  }
}

// Switch the selected provider to the work account. The caller MUST have already
// completed a successful native work-account authentication test in this session
// (which is what the daemon verifies). Any failure preserves the current
// selection (no partial switch).
export function selectWorkAccountProvider(signal?: AbortSignal): Promise<ProviderSwitchResult> {
  return postProviderSwitch('/api/v1/broker/session-provider/select-work-account', signal);
}

// PHASE-1 Hello capability diagnostic buffer (cycle-01r-hello-capability-probe-
// repair). MEASUREMENT ONLY. The native host injects a frozen, read-only
// window.__paxHelloDiag marker on every frame ONLY in the build-gated isolated
// app with PAXCB_HELLO_DIAG=1. Because that marker is frozen, the iframe-side
// recorder accumulates its bounded observations in a SEPARATE, mutable
// window.__paxHelloDiagIframe object. This helper returns that buffer only when
// the marker is present and enabled; a no-op (null) otherwise. It only ever
// records booleans and bounded reason strings — never identity or credential
// material — and never alters control flow.
interface HelloDiagIframeBuffer {
  platformAuthenticatorProbeRan?: boolean;
  lastPlatformAvailable?: boolean;
  backendSelectAttempted?: boolean;
  backendSelectReason?: string | null;
  enrollmentRequestPosted?: boolean;
}

function helloDiagIframeBuffer(): HelloDiagIframeBuffer | null {
  try {
    const w = window as unknown as {
      __paxHelloDiag?: { enabled?: boolean };
      __paxHelloAttendedDiag?: { enabled?: boolean };
      __paxHelloDiagIframe?: HelloDiagIframeBuffer;
    };
    // Arm the SAME bounded, behavior-neutral iframe buffer under EITHER the
    // PHASE-1 unattended marker OR the PHASE-2 attended marker. The recorded
    // values are identical bounded booleans / reason strings; only the launch
    // that injected the marker differs. Absent both markers this is a no-op.
    const armed =
      (w.__paxHelloDiag && w.__paxHelloDiag.enabled === true) ||
      (w.__paxHelloAttendedDiag && w.__paxHelloAttendedDiag.enabled === true);
    if (!armed) {
      return null;
    }
    if (!w.__paxHelloDiagIframe) {
      w.__paxHelloDiagIframe = {};
    }
    return w.__paxHelloDiagIframe;
  } catch {
    return null;
  }
}

// Probe whether this platform can perform a user-verifying platform-authenticator
// (Windows Hello) ceremony, using the SAME WebAuthn capability signal the lock
// shell uses. This is a DEVICE/PLATFORM predicate — separate from whether a PAX
// Hello credential is locally registered. Fails closed to false (treat as
// unavailable) whenever the API is missing or throws, so a switch never persists
// on an unproven platform.
export async function probePlatformAuthenticatorAvailable(): Promise<boolean> {
  let available = false;
  try {
    const pkc = (window as unknown as {
      PublicKeyCredential?: {
        isUserVerifyingPlatformAuthenticatorAvailable?: () => Promise<boolean>;
      };
    }).PublicKeyCredential;
    if (pkc && typeof pkc.isUserVerifyingPlatformAuthenticatorAvailable === 'function') {
      available = (await pkc.isUserVerifyingPlatformAuthenticatorAvailable()) === true;
    }
  } catch {
    available = false;
  }
  // PHASE-1 diagnostic: record the bounded probe outcome (behavior-neutral).
  const buf = helloDiagIframeBuffer();
  if (buf) {
    buf.platformAuthenticatorProbeRan = true;
    buf.lastPlatformAvailable = available;
  }
  return available;
}

// Switch the selected provider to Windows Hello. The current session authorizes
// the switch, but the daemon decides with two separate predicates: the caller's
// platform-availability probe (sent as the bounded `platformAvailable` boolean)
// and its own local-registration integrity check. The daemon persists Windows
// Hello ONLY when the platform is available AND a valid local registration
// exists; otherwise it returns a bounded reason and leaves the current selection
// intact (no partial switch). When it returns `windows_hello_enrollment_required`
// the caller must run the enrollment ceremony and then call again.
export function selectWindowsHelloProvider(
  platformAvailable: boolean,
  signal?: AbortSignal,
): Promise<ProviderSwitchResult> {
  return postProviderSwitch(
    '/api/v1/broker/session-provider/select-windows-hello',
    signal,
    { platformAvailable },
  ).then((result) => {
    // PHASE-1 diagnostic: record the bounded backend-select outcome. Does not
    // alter the returned result or control flow.
    const buf = helloDiagIframeBuffer();
    if (buf) {
      buf.backendSelectAttempted = true;
      buf.backendSelectReason = result.reason;
    }
    return result;
  });
}

// Upper bound on how long the renderer waits for the parent-shell enrollment
// ceremony to return a bounded outcome. The ceremony itself is watchdog-bounded
// in the shell; this is a courier-transport safety net so the renderer never
// hangs forever if the reply is lost (e.g. no native host). It intentionally
// exceeds the shell's create-ceremony timeout so a genuine user ceremony is not
// pre-empted by the courier.
const WINDOWS_HELLO_ENROLL_COURIER_TIMEOUT_MS = 150 * 1000;

function newCorrelationId(): string {
  try {
    const c = (window as unknown as { crypto?: { randomUUID?: () => string } }).crypto;
    if (c && typeof c.randomUUID === 'function') {
      return c.randomUUID();
    }
  } catch {
    // fall through to the non-crypto fallback
  }
  return 'whe-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
}

function isEnrollmentOutcome(value: unknown): value is WindowsHelloEnrollmentOutcome {
  return (
    value === 'enrolled' ||
    value === 'cancelled' ||
    value === 'unavailable' ||
    value === 'failed' ||
    value === 'timeout' ||
    value === 'transport_failure'
  );
}

// Courier a Windows Hello enrollment request to the top-level shell and await a
// single bounded outcome. The renderer NEVER runs the ceremony itself: it posts
// ONLY { type, requestId } to the parent with an explicit target origin, then
// accepts a reply ONLY when it comes from the parent window, from the exact same
// origin, with the exact result type, and with a requestId that matches the one
// minted here. Anything else is ignored. Fails closed to 'transport_failure' when
// there is no native host (standalone/dev) and to 'timeout' if no valid reply
// arrives, so the caller can safely leave work_account selected.
export function requestWindowsHelloEnrollmentFromNative(
  signal?: AbortSignal,
): Promise<WindowsHelloEnrollmentOutcome> {
  return new Promise((resolve) => {
    const parent = window.parent;
    if (!parent || parent === window) {
      resolve('transport_failure');
      return;
    }

    const requestId = newCorrelationId();
    const expectedOrigin = window.location.origin;
    let settled = false;

    const finish = (outcome: WindowsHelloEnrollmentOutcome) => {
      if (settled) {
        return;
      }
      settled = true;
      window.clearTimeout(timer);
      window.removeEventListener('message', onMessage);
      signal?.removeEventListener('abort', onAbort);
      resolve(outcome);
    };

    const onMessage = (event: MessageEvent) => {
      // Exact-origin AND exact-source (the parent shell) AND exact shape AND
      // correlation-id match. Any mismatch is ignored, never surfaced.
      if (event.origin !== expectedOrigin || event.source !== parent) {
        return;
      }
      const data = event.data as Record<string, unknown> | null;
      if (
        !data ||
        data.type !== WINDOWS_HELLO_ENROLL_RESULT_TYPE ||
        data.requestId !== requestId
      ) {
        return;
      }
      finish(isEnrollmentOutcome(data.outcome) ? data.outcome : 'failed');
    };

    const onAbort = () => finish('cancelled');

    const timer = window.setTimeout(
      () => finish('timeout'),
      WINDOWS_HELLO_ENROLL_COURIER_TIMEOUT_MS,
    );

    window.addEventListener('message', onMessage);
    if (signal) {
      if (signal.aborted) {
        finish('cancelled');
        return;
      }
      signal.addEventListener('abort', onAbort, { once: true });
    }

    try {
      parent.postMessage(
        { type: WINDOWS_HELLO_ENROLL_REQUEST_TYPE, requestId },
        expectedOrigin,
      );
      // PHASE-1 diagnostic: record that the REAL enrollment request was posted.
      const buf = helloDiagIframeBuffer();
      if (buf) {
        buf.enrollmentRequestPosted = true;
      }
    } catch {
      finish('transport_failure');
    }
  });
}

// Lock the broker session so a newly selected provider governs the next unlock.
// A provider switch is applied on the next Locked -> Unlocked cycle; this drives
// that cycle immediately. Fails closed (returns false) on any error. On success
// it also nudges the top-level shell (via the same-origin BROKER_LOCK_INITIATED
// courier) to re-check lock state and mount the lock overlay promptly, because
// the shell does not otherwise observe an iframe-initiated lock.
export async function requestBrokerLock(signal?: AbortSignal): Promise<boolean> {
  try {
    const response = await fetch('/api/v1/broker/lock', {
      method: 'POST',
      headers: buildHeaders(true),
      signal,
    });
    if (response.ok) {
      notifyShellBrokerLockInitiated();
      return true;
    }
    return false;
  } catch {
    return false;
  }
}

// Tell the top-level shell that the broker was just locked from inside the
// iframe. Best-effort and non-fatal: if the parent is unreachable (standalone /
// dev, or same window), the shell's on-demand 423 path still catches the lock
// eventually. Delivered to the exact same origin with a { type }-only payload.
function notifyShellBrokerLockInitiated(): void {
  try {
    if (typeof window === 'undefined') {
      return;
    }
    const parent = window.parent;
    if (!parent || parent === window) {
      return;
    }
    parent.postMessage({ type: BROKER_LOCK_INITIATED_TYPE }, window.location.origin);
  } catch {
    /* delivery failure is non-fatal; the shell's 423 path still catches the lock */
  }
}

