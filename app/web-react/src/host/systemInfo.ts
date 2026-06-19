/**
 * Read-only system information client.
 *
 * Reads existing, unauthenticated-or-bearer broker state for display on the
 * Settings surface: the runtime/version payload, the PAX engine acquisition
 * state, and the health payload. Everything here is a GET; nothing in this
 * module writes, cooks, bakes, schedules, acquires, downloads, uploads, or
 * mutates any broker state.
 *
 * Hard boundaries:
 *   - GET only. There is intentionally no POST/PUT/DELETE helper here and one
 *     must never be added. The Settings surface only reflects state.
 *   - No absolute filesystem paths, no secrets, and no PAX script path are
 *     surfaced by callers; this module exposes only the read-only display
 *     fields the broker already returns for status.
 *   - Auth mirrors brokerBridge: adopt the inline bootstrap token once and send
 *     it as `Authorization: Bearer`. No CSRF header is sent because no request
 *     here is state-changing.
 */

import { adoptBootstrapToken } from './brokerBridge';

const TOKEN_KEY = 'cookbook.sessionToken';
const RUNTIME_VERSION_PATH = '/api/v1/runtime/version';
const ENGINE_STATE_PATH = '/api/v1/setup/acquire-pax/state';
const HEALTH_PATH = '/api/v1/health';
const LOCK_STATE_PATH = '/api/v1/broker/lock-state';
const WEBAUTHN_STATUS_PATH = '/api/v1/broker/webauthn/status';
const DEFAULT_TIMEOUT_MS = 30000;

/** Read-only runtime/version fields used by Settings. */
export interface RuntimeVersionInfo {
  cookbookVersion: string | null;
  releaseChannel: string | null;
  buildTimestamp: string | null;
  bundledPax: {
    version: string | null;
    sha256: string | null;
    integrity: string | null;
  };
  runtime: {
    brokerPort: number | null;
    transport: string | null;
  };
}

/** Read-only PAX engine acquisition-state fields used by Settings. */
export interface PaxEngineState {
  state: string | null;
  isAcquired: boolean;
  managedScriptPresent: boolean;
  approvedVersion: string | null;
  approvedSha256: string | null;
  installedVersion: string | null;
  installedSource: string | null;
  validatedAtUtc: string | null;
  /** Non-secret managed engine path the broker already reports, for command-preview display. */
  managedEnginePath: string | null;
}

/** Read-only health fields used by Settings. */
export interface HealthInfo {
  ok: boolean;
  status: string | null;
  appVersion: string | null;
  runtimeKind: string | null;
  workspaceReady: boolean;
}

/**
 * Read-only app-lock fields used by Chef's Keys.
 *
 * Sourced from GET /api/v1/broker/lock-state. These are status flags only -
 * never a secret, token, challenge, or credential value.
 */
export interface LockStateInfo {
  locked: boolean;
  state: string | null;
  inactivityTimeoutMinutes: number | null;
}

/**
 * Read-only sign-in-protection fields used by Chef's Keys.
 *
 * Sourced from GET /api/v1/broker/webauthn/status. Only the non-secret
 * registration flag and verification policy are surfaced; the raw credential
 * identifiers, public keys, challenges, and origins are intentionally NOT
 * read or projected by this helper.
 */
export interface SignInProtectionInfo {
  passkeyRegistered: boolean;
  userVerification: string | null;
}

export type ReadResult<T> =
  | { ok: true; data: T }
  | { ok: false };

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

function buildHeaders(): Record<string, string> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  const token = getToken();
  if (token) {
    headers.Authorization = 'Bearer ' + token;
  }
  return headers;
}

async function getJson(
  path: string,
): Promise<{ status: number; data: Record<string, unknown> | null }> {
  adoptBootstrapToken();

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), DEFAULT_TIMEOUT_MS);

  let response: Response;
  try {
    response = await fetch(path, {
      method: 'GET',
      headers: buildHeaders(),
      signal: controller.signal,
    });
  } catch {
    clearTimeout(timer);
    return { status: 0, data: null };
  }
  clearTimeout(timer);

  let data: Record<string, unknown> | null = null;
  try {
    const raw = await response.text();
    if (raw.length > 0) {
      const parsed = JSON.parse(raw) as unknown;
      if (parsed && typeof parsed === 'object') {
        data = parsed as Record<string, unknown>;
      }
    }
  } catch {
    data = null;
  }

  return { status: response.status, data };
}

function str(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value : null;
}

function num(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function obj(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : {};
}

/**
 * Render the build stamp for display. Build-Setup.ps1 writes it as
 * "YYYY-MM-DD-HH-MM-SS-UTC"; this returns a compact "YYYY-MM-DD HH:MM" that
 * fits on one line in the Build status card (the card detail already notes it
 * is UTC). Unrecognized shapes pass through unchanged; null/empty returns null.
 */
export function formatBuildTimestamp(raw: string | null): string | null {
  if (!raw) {
    return null;
  }
  const trimmed = raw.trim();
  if (trimmed.length === 0) {
    return null;
  }
  const parts = trimmed.split('-');
  if (parts.length === 7 && parts[6].toUpperCase() === 'UTC' && parts[0].length === 4) {
    const [y, mo, d, h, mi] = parts;
    return `${y}-${mo}-${d} ${h}:${mi}`;
  }
  return trimmed;
}

/** GET /api/v1/runtime/version — read-only version display. */
export async function getRuntimeVersion(): Promise<ReadResult<RuntimeVersionInfo>> {
  const { status, data } = await getJson(RUNTIME_VERSION_PATH);
  if (status !== 200 || !data) {
    return { ok: false };
  }
  const pax = obj(data.bundledPax);
  const runtime = obj(data.runtime);
  return {
    ok: true,
    data: {
      cookbookVersion: str(data.cookbookVersion),
      releaseChannel: str(data.releaseChannel),
      buildTimestamp: str(data.buildTimestamp),
      bundledPax: {
        version: str(pax.version),
        sha256: str(pax.sha256),
        integrity: str(pax.integrity),
      },
      runtime: {
        brokerPort: num(runtime.brokerPort),
        transport: str(runtime.transport),
      },
    },
  };
}

/** GET /api/v1/setup/acquire-pax/state — read-only engine status display. */
export async function getPaxEngineState(): Promise<ReadResult<PaxEngineState>> {
  const { status, data } = await getJson(ENGINE_STATE_PATH);
  if (status !== 200 || !data) {
    return { ok: false };
  }
  const expected = obj(data.expected);
  const canonical = obj(data.canonicalScript);
  const install = obj(data.installState);
  return {
    ok: true,
    data: {
      state: str(data.state),
      isAcquired: data.isAcquired === true,
      managedScriptPresent: canonical.present === true,
      approvedVersion: str(expected.paxScriptVersion),
      approvedSha256: str(expected.paxScriptSha256),
      installedVersion: str(install.version),
      installedSource: str(install.source),
      validatedAtUtc: str(install.validatedAtUtc),
      // The route already returns the canonical (managed) engine path; surface it
      // as non-secret display data for the resume command preview.
      managedEnginePath: str(canonical.path),
    },
  };
}

/** GET /api/v1/health — read-only health display. */
export async function getHealth(): Promise<ReadResult<HealthInfo>> {
  const { status, data } = await getJson(HEALTH_PATH);
  if (!data) {
    return { ok: false };
  }
  const okFlag = data.ok === true && status === 200;
  return {
    ok: true,
    data: {
      ok: okFlag,
      status: str(data.status),
      appVersion: str(data.version),
      runtimeKind: str(data.runtime),
      workspaceReady: okFlag,
    },
  };
}

/**
 * GET /api/v1/broker/lock-state — read-only app-lock status display.
 *
 * Surfaces only the lock state and the inactivity timeout. No challenge,
 * token, credential, or secret is read or returned.
 */
export async function getLockState(): Promise<ReadResult<LockStateInfo>> {
  const { status, data } = await getJson(LOCK_STATE_PATH);
  if (status !== 200 || !data) {
    return { ok: false };
  }
  const state = str(data.state);
  return {
    ok: true,
    data: {
      locked: state !== 'Unlocked',
      state,
      inactivityTimeoutMinutes: num(data.inactivityTimeoutMinutes),
    },
  };
}

/**
 * GET /api/v1/broker/webauthn/status — read-only sign-in-protection display.
 *
 * Surfaces only the non-secret registration flag and the verification policy.
 * The raw credential identifiers, public keys, accepted origins, and challenges
 * the endpoint also returns are intentionally NOT read here.
 */
export async function getSignInProtection(): Promise<ReadResult<SignInProtectionInfo>> {
  const { status, data } = await getJson(WEBAUTHN_STATUS_PATH);
  if (status !== 200 || !data) {
    return { ok: false };
  }
  return {
    ok: true,
    data: {
      passkeyRegistered: data.registered === true,
      userVerification: str(data.userVerification),
    },
  };
}
