/**
 * Chef's Keys broker client (CK-1).
 *
 * A dedicated, narrow channel for the Chef's Keys management surface. It is kept
 * SEPARATE from brokerBridge (whose contract forbids credential / Credential
 * Manager calls and any route outside the recipes and cooks routes) and from
 * systemInfo (GET-only), so neither of those modules' boundaries is broken.
 *
 * It talks ONLY to the broker's authenticated `/api/v1/chef-keys*` routes, which
 * are backed entirely by the Windows Credential Manager. PAX Cookbook never
 * stores credential material itself.
 *
 * Authentication mirrors brokerBridge exactly: adopt the broker-injected
 * bootstrap token once, send it as `Authorization: Bearer`, and add the
 * `X-Cookbook-Request: 1` CSRF header + `Content-Type: application/json` on every
 * state-changing request (POST / PUT / DELETE).
 *
 * Constraint 14: a client secret is WRITE-ONLY. It is sent only on create /
 * update and is never returned by any route; list / detail responses carry a
 * `hasSecret` boolean, never the secret value. This module never reads, stores,
 * caches, or logs a secret value.
 */

import { adoptBootstrapToken } from './brokerBridge';

const TOKEN_KEY = 'cookbook.sessionToken';
const CHEF_KEYS_PATH = '/api/v1/chef-keys';
const DEFAULT_TIMEOUT_MS = 30000;

export type ChefKeyAuthType =
  | 'WebLogin'
  | 'DeviceCode'
  | 'AppReg-Certificate'
  | 'AppReg-Secret';

/** A Chef's Key as projected by the broker. NEVER carries a secret value. */
export interface ChefKeyItem {
  id: string;
  authType: string;
  displayName: string;
  tenantId: string | null;
  clientId: string | null;
  certThumbprint: string | null;
  upn: string | null;
  hasSecret: boolean;
}

export interface ChefKeyListBody {
  chefKeys: ChefKeyItem[];
}

export interface ChefKeyDetailBody {
  chefKey: ChefKeyItem;
}

export interface ChefKeyCreateBody {
  id: string;
  chefKey: ChefKeyItem;
}

export interface ChefKeyDeleteBody {
  id: string;
  deleted: boolean;
}

export interface ChefKeyTestCheck {
  name: string;
  ok: boolean;
  detail: string;
}

export interface ChefKeyTestBody {
  ok: boolean;
  status: string;
  reason: string;
  authType: string;
  graphConnectivityTested: boolean;
  checks: ChefKeyTestCheck[];
}

/**
 * Write payload for create / update. `clientSecret` is write-only and is only
 * ever set on this outbound request shape; it never appears in a response type.
 * On update, an omitted/blank `clientSecret` keeps the existing stored secret.
 */
export interface ChefKeyWriteRequest {
  authType: ChefKeyAuthType;
  displayName: string;
  tenantId?: string;
  clientId?: string;
  certThumbprint?: string;
  upn?: string;
  clientSecret?: string;
}

export interface ChefKeyResponse<T> {
  /** `true` iff the HTTP status was 2xx. */
  ok: boolean;
  /** Integer status code, or 0 on a network error. */
  status: number;
  /** Parsed JSON body when the response was JSON, else `null`. */
  data: T | null;
  /** Broker error code (e.g. `validation_failed`, `not_found`) when present. */
  error: string | null;
  /** Specific validation reason (e.g. `invalid_upn`) when present. */
  reason: string | null;
  /** Offending field name for a validation failure, when present. */
  field: string | null;
  /** Human-readable message when present. */
  message: string | null;
  /** Raw response text for diagnostics. */
  rawText: string;
  /** Non-null only when fetch threw, timed out, or was cancelled. */
  networkError: string | null;
}

export interface ChefKeyRequestOptions {
  signal?: AbortSignal;
  timeoutMs?: number;
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

async function safeText(response: Response): Promise<string> {
  try {
    return await response.text();
  } catch {
    return '';
  }
}

function describeError(err: unknown): string {
  if (err && typeof err === 'object' && 'message' in err) {
    const message = (err as { message?: unknown }).message;
    if (typeof message === 'string' && message.length > 0) {
      return message;
    }
  }
  return 'network_error';
}

async function request<T>(
  method: 'GET' | 'POST' | 'PUT' | 'DELETE',
  path: string,
  body: unknown,
  options: ChefKeyRequestOptions,
): Promise<ChefKeyResponse<T>> {
  adoptBootstrapToken();

  const stateChanging = method === 'POST' || method === 'PUT' || method === 'DELETE';
  const controller = new AbortController();
  const timeoutMs =
    typeof options.timeoutMs === 'number' ? options.timeoutMs : DEFAULT_TIMEOUT_MS;
  const timer = setTimeout(() => controller.abort(), timeoutMs);

  if (options.signal) {
    if (options.signal.aborted) {
      controller.abort();
    } else {
      options.signal.addEventListener('abort', () => controller.abort(), { once: true });
    }
  }

  const init: RequestInit = {
    method,
    headers: buildHeaders(stateChanging),
    signal: controller.signal,
    // Always fetch the Chef's Keys list fresh; never a cached GET.
    cache: 'no-store',
  };
  if ((method === 'POST' || method === 'PUT') && body !== undefined) {
    init.body = JSON.stringify(body);
  }

  let response: Response;
  try {
    response = await fetch(path, init);
  } catch (err) {
    clearTimeout(timer);
    const aborted = options.signal?.aborted ?? false;
    return {
      ok: false,
      status: 0,
      data: null,
      error: null,
      reason: null,
      field: null,
      message: null,
      rawText: '',
      networkError: aborted ? 'cancelled' : describeError(err),
    };
  }
  clearTimeout(timer);

  const rawText = await safeText(response);
  let data: T | null = null;
  let errorCode: string | null = null;
  let reason: string | null = null;
  let field: string | null = null;
  let message: string | null = null;

  const contentType = response.headers.get('Content-Type') ?? '';
  if (contentType.indexOf('application/json') >= 0 && rawText.length > 0) {
    try {
      const parsed = JSON.parse(rawText) as unknown;
      data = parsed as T;
      if (parsed && typeof parsed === 'object') {
        const bag = parsed as Record<string, unknown>;
        if (typeof bag.error === 'string') {
          errorCode = bag.error;
        }
        if (typeof bag.reason === 'string') {
          reason = bag.reason;
        }
        if (typeof bag.field === 'string') {
          field = bag.field;
        }
        if (typeof bag.message === 'string') {
          message = bag.message;
        }
      }
    } catch {
      data = null;
    }
  }

  return {
    ok: response.ok,
    status: response.status,
    data: response.ok ? data : null,
    error: errorCode,
    reason,
    field,
    message,
    rawText,
    networkError: null,
  };
}

/** GET /api/v1/chef-keys — list every saved Chef's Key (metadata only). */
export function listChefKeys(
  options: ChefKeyRequestOptions = {},
): Promise<ChefKeyResponse<ChefKeyListBody>> {
  return request<ChefKeyListBody>('GET', CHEF_KEYS_PATH, undefined, options);
}

/** GET /api/v1/chef-keys/{id} — fetch one Chef's Key's detail (metadata only). */
export function getChefKey(
  id: string,
  options: ChefKeyRequestOptions = {},
): Promise<ChefKeyResponse<ChefKeyDetailBody>> {
  return request<ChefKeyDetailBody>(
    'GET',
    CHEF_KEYS_PATH + '/' + encodeURIComponent(id),
    undefined,
    options,
  );
}

/** POST /api/v1/chef-keys — create a Chef's Key (the broker generates the id). */
export function createChefKey(
  body: ChefKeyWriteRequest,
  options: ChefKeyRequestOptions = {},
): Promise<ChefKeyResponse<ChefKeyCreateBody>> {
  return request<ChefKeyCreateBody>('POST', CHEF_KEYS_PATH, body, options);
}

/** PUT /api/v1/chef-keys/{id} — update metadata; blank secret keeps the existing one. */
export function updateChefKey(
  id: string,
  body: ChefKeyWriteRequest,
  options: ChefKeyRequestOptions = {},
): Promise<ChefKeyResponse<ChefKeyDetailBody>> {
  return request<ChefKeyDetailBody>(
    'PUT',
    CHEF_KEYS_PATH + '/' + encodeURIComponent(id),
    body,
    options,
  );
}

/** DELETE /api/v1/chef-keys/{id} — remove the Chef's Key from the vault. */
export function deleteChefKey(
  id: string,
  options: ChefKeyRequestOptions = {},
): Promise<ChefKeyResponse<ChefKeyDeleteBody>> {
  return request<ChefKeyDeleteBody>(
    'DELETE',
    CHEF_KEYS_PATH + '/' + encodeURIComponent(id),
    undefined,
    options,
  );
}

/** POST /api/v1/chef-keys/{id}/test — local/structural validation only. */
export function testChefKey(
  id: string,
  options: ChefKeyRequestOptions = {},
): Promise<ChefKeyResponse<ChefKeyTestBody>> {
  return request<ChefKeyTestBody>(
    'POST',
    CHEF_KEYS_PATH + '/' + encodeURIComponent(id) + '/test',
    undefined,
    options,
  );
}
