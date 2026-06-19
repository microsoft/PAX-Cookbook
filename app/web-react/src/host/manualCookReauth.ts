/**
 * Manual-cook step-up re-auth (browser-owned Windows Hello / WebAuthn ceremony).
 *
 * This is the React port of the legacy `manual-cook-reauth.js`. It owns the one
 * thing the broker cannot do for itself: turn a broker `401 reAuthRequired`
 * (opClass `manualCook`) into a single-use, recipe-bound authorization grant so
 * exactly one Bake can proceed. The Bake flow calls `reauthManualCook()` inline
 * and retries the cook exactly once on success — there is no fabricated success
 * and no automatic retry loop.
 *
 * Endpoints (all token + CSRF gated; NOT lock-bypass):
 *   GET  /api/v1/broker/webauthn/status              -> { registered, credentialIds }
 *   POST /api/v1/broker/reauth/manual-cook/challenge -> { challenge, timeoutMs }
 *   POST /api/v1/broker/reauth/manual-cook/verify    -> { ok:true } on grant
 *
 * Doctrine (unchanged from the legacy helper):
 *   - `navigator.credentials.get` is the only credential API used. The browser
 *     owns Windows Hello; the broker only verifies the ES256 assertion. The SPA
 *     never collects, hashes, or proxies the Windows password or PIN.
 *   - `userVerification` is always `'required'`.
 *   - `rp.id` is intentionally omitted so the browser defaults to the page
 *     origin (127.0.0.1 or localhost, whichever the launcher opened).
 *   - Byte conversions are pure and never log or persist any payload.
 *   - A user cancel (NotAllowedError / AbortError) is a bounded
 *     `user_cancelled` reason, never a success.
 *   - The returned promise NEVER rejects: every failure resolves to
 *     `{ ok: false, reason }` so a caller cannot mistake an exception for a
 *     started cook.
 */

import { adoptBootstrapToken } from './brokerBridge';

const TOKEN_KEY = 'cookbook.sessionToken';

/** Stable, bounded reasons a step-up can fail with. */
export type ReauthReason =
  | 'webauthn_unsupported'
  | 'bad_recipe_id'
  | 'status_failed'
  | 'no_credential'
  | 'challenge_failed'
  | 'no_assertion'
  | 'user_cancelled'
  | 'verify_rejected'
  | 'verify_network_error'
  | 'challenge_network_error'
  | 'status_network_error'
  | string; // navigator_get_failed:<name>

export interface ReauthResult {
  ok: boolean;
  reason?: ReauthReason;
  status?: number;
}

interface BrokerJson {
  ok: boolean;
  status: number;
  body: Record<string, unknown> | null;
}

/** Whether this device can complete a WebAuthn assertion in the app window. */
export function isWebAuthnSupported(): boolean {
  try {
    return !!(
      typeof window !== 'undefined' &&
      window.PublicKeyCredential &&
      window.navigator &&
      window.navigator.credentials &&
      typeof window.navigator.credentials.get === 'function'
    );
  } catch {
    return false;
  }
}

function getToken(): string | null {
  if (typeof window === 'undefined') {
    return null;
  }
  try {
    const token = window.sessionStorage.getItem(TOKEN_KEY);
    return token && token.length > 0 ? token : null;
  } catch {
    return null;
  }
}

// base64url (broker wire format) -> ArrayBuffer (WebAuthn API).
function b64uToArrayBuffer(b64u: string): ArrayBuffer {
  let s = String(b64u || '').replace(/-/g, '+').replace(/_/g, '/');
  const pad = s.length % 4;
  if (pad === 2) {
    s += '==';
  } else if (pad === 3) {
    s += '=';
  } else if (pad !== 0 && pad !== 1) {
    throw new Error('manualCook: invalid base64url length');
  }
  const bin = window.atob(s);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) {
    out[i] = bin.charCodeAt(i);
  }
  return out.buffer;
}

// ArrayBuffer (WebAuthn API) -> base64url (broker wire format).
function arrayBufferToB64u(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf);
  let s = '';
  for (let i = 0; i < bytes.length; i++) {
    s += String.fromCharCode(bytes[i]);
  }
  return window.btoa(s).replace(/=+$/g, '').replace(/\+/g, '-').replace(/\//g, '_');
}

async function brokerGet(path: string): Promise<BrokerJson> {
  adoptBootstrapToken();
  const headers: Record<string, string> = { Accept: 'application/json' };
  const token = getToken();
  if (token) {
    headers.Authorization = 'Bearer ' + token;
  }
  const response = await fetch(path, { method: 'GET', headers });
  const body = await readJsonBody(response);
  return { ok: response.ok, status: response.status, body };
}

async function brokerPost(path: string, payload: unknown): Promise<BrokerJson> {
  adoptBootstrapToken();
  const headers: Record<string, string> = {
    Accept: 'application/json',
    'X-Cookbook-Request': '1',
    'Content-Type': 'application/json',
  };
  const token = getToken();
  if (token) {
    headers.Authorization = 'Bearer ' + token;
  }
  const response = await fetch(path, {
    method: 'POST',
    headers,
    body: JSON.stringify(payload ?? {}),
  });
  const body = await readJsonBody(response);
  return { ok: response.ok, status: response.status, body };
}

async function readJsonBody(response: Response): Promise<Record<string, unknown> | null> {
  let text = '';
  try {
    text = await response.text();
  } catch {
    return null;
  }
  const contentType = response.headers.get('Content-Type') ?? '';
  if (contentType.indexOf('application/json') < 0 || text.length === 0) {
    return null;
  }
  try {
    const parsed = JSON.parse(text) as unknown;
    return parsed && typeof parsed === 'object' ? (parsed as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/**
 * Run the full step-up ceremony for one recipe. Resolves to `{ ok: true }` on a
 * granted authorization, or `{ ok: false, reason }` on any failure. Never
 * rejects.
 */
export async function reauthManualCook(recipeId: string): Promise<ReauthResult> {
  if (!isWebAuthnSupported()) {
    return { ok: false, reason: 'webauthn_unsupported' };
  }
  if (!recipeId || typeof recipeId !== 'string') {
    return { ok: false, reason: 'bad_recipe_id' };
  }

  // 1. WebAuthn status — is a credential enrolled for this workspace?
  let status: BrokerJson;
  try {
    status = await brokerGet('/api/v1/broker/webauthn/status');
  } catch {
    return { ok: false, reason: 'status_network_error' };
  }
  if (!status.ok || !status.body) {
    return { ok: false, reason: 'status_failed', status: status.status };
  }
  if (!status.body.registered) {
    return { ok: false, reason: 'no_credential', status: status.status };
  }
  const credentialIds = Array.isArray(status.body.credentialIds)
    ? (status.body.credentialIds as string[])
    : [];

  // 2. Fresh challenge.
  let challenge: BrokerJson;
  try {
    challenge = await brokerPost('/api/v1/broker/reauth/manual-cook/challenge', {});
  } catch {
    return { ok: false, reason: 'challenge_network_error' };
  }
  const challengeB64u =
    challenge.body && typeof challenge.body.challenge === 'string'
      ? (challenge.body.challenge as string)
      : null;
  if (!challenge.ok || !challengeB64u) {
    return { ok: false, reason: 'challenge_failed', status: challenge.status };
  }
  const timeoutMs =
    challenge.body && typeof challenge.body.timeoutMs === 'number'
      ? (challenge.body.timeoutMs as number)
      : 60000;

  // 3. Browser-owned Windows Hello assertion.
  const allowCredentials = credentialIds.map(id => ({
    type: 'public-key' as const,
    id: b64uToArrayBuffer(id),
    transports: ['internal'] as AuthenticatorTransport[],
  }));
  let assertion: PublicKeyCredential | null;
  try {
    const credential = await window.navigator.credentials.get({
      publicKey: {
        challenge: b64uToArrayBuffer(challengeB64u),
        allowCredentials,
        userVerification: 'required',
        timeout: timeoutMs,
        // rp.id intentionally omitted — the browser defaults to the page origin.
      },
    });
    assertion = credential as PublicKeyCredential | null;
  } catch (err) {
    const name = err && typeof err === 'object' && 'name' in err
      ? String((err as { name?: unknown }).name ?? 'unknown')
      : 'unknown';
    if (name === 'NotAllowedError' || name === 'AbortError') {
      return { ok: false, reason: 'user_cancelled' };
    }
    return { ok: false, reason: 'navigator_get_failed:' + name };
  }
  const assertionResponse = assertion?.response as AuthenticatorAssertionResponse | undefined;
  if (!assertion || !assertionResponse) {
    return { ok: false, reason: 'no_assertion' };
  }

  // 4. Broker verifies the ES256 assertion and grants the single-use,
  //    recipe-bound authorization.
  const verifyBody = {
    credentialId: arrayBufferToB64u(assertion.rawId),
    clientDataJSON: arrayBufferToB64u(assertionResponse.clientDataJSON),
    authenticatorData: arrayBufferToB64u(assertionResponse.authenticatorData),
    signature: arrayBufferToB64u(assertionResponse.signature),
    challenge: challengeB64u,
    recipeId,
  };
  let verify: BrokerJson;
  try {
    verify = await brokerPost('/api/v1/broker/reauth/manual-cook/verify', verifyBody);
  } catch {
    return { ok: false, reason: 'verify_network_error' };
  }
  if (verify.ok && verify.body && verify.body.ok === true) {
    return { ok: true };
  }
  return { ok: false, reason: 'verify_rejected', status: verify.status };
}

/**
 * Map a bounded `{ ok:false }` step-up result to a short, operator-facing
 * sentence. Every sentence states plainly that the bake did not start so a
 * failed step-up can never read as a silent success.
 */
export function describeReauthFailure(result: ReauthResult): string {
  if (!result || result.ok) {
    return '';
  }
  switch (result.reason) {
    case 'webauthn_unsupported':
      return 'This device can\'t confirm it\'s you in the app window. The bake did not start.';
    case 'no_credential':
      return 'You haven\'t set up identity confirmation yet. Unlock the app once to set it up, then try again. The bake did not start.';
    case 'user_cancelled':
      return 'The identity check was cancelled. The bake did not start.';
    case 'verify_rejected':
      return 'We couldn\'t confirm it\'s you. The bake did not start.';
    case 'status_failed':
    case 'status_network_error':
    case 'challenge_failed':
    case 'challenge_network_error':
    case 'verify_network_error':
      return 'Couldn\'t reach PAX Cookbook to confirm it\'s you. The bake did not start.';
    default:
      return 'The identity check couldn\'t be completed. The bake did not start.';
  }
}
