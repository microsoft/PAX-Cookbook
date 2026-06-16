/**
 * Notification settings broker client (CK-4).
 *
 * A dedicated, narrow channel for the Settings → Notifications surface. It is
 * kept SEPARATE from brokerBridge (whose contract explicitly forbids
 * notification, credential, and Credential Manager calls and any route outside
 * the recipes and cooks routes) and from systemInfo (GET-only), so neither of
 * those modules' boundaries is broken.
 *
 * It talks ONLY to the broker's authenticated `/api/v1/settings/notifications*`
 * routes. The bot token + chat id are stored in the per-user Windows Credential
 * Manager vault by the broker; PAX Cookbook never stores them itself, and no
 * external bot is referenced here.
 *
 * Authentication mirrors brokerBridge / chefKeys exactly: adopt the
 * broker-injected bootstrap token once, send it as `Authorization: Bearer`, and
 * add the `X-Cookbook-Request: 1` CSRF header + `Content-Type: application/json`
 * on every state-changing request (PUT / POST).
 *
 * Constraint 14: the Telegram bot token is WRITE-ONLY. It is sent only on save
 * and is never returned by any route; the settings response carries a
 * `tokenSet` boolean, never the token value. This module never reads, stores,
 * caches, or logs a token value.
 */

import { adoptBootstrapToken } from './brokerBridge';

const TOKEN_KEY = 'cookbook.sessionToken';
const NOTIFICATIONS_PATH = '/api/v1/settings/notifications';
const DEFAULT_TIMEOUT_MS = 30000;

/** Secret-free notification settings as projected by the broker. */
export interface NotificationSettings {
  enabled: boolean;
  chatId: string | null;
  chatIdSet: boolean;
  /** True when a bot token is stored. The token value is NEVER returned. */
  tokenSet: boolean;
  provider: string;
}

export interface NotificationSaveResult extends NotificationSettings {
  saved?: boolean;
}

/**
 * Save payload. `botToken` is write-only and is only ever set on this outbound
 * shape; it never appears in a response type. A blank/omitted `botToken` keeps
 * the existing stored token; an omitted `chatId` keeps the existing chat id.
 */
export interface NotificationSaveRequest {
  enabled: boolean;
  chatId?: string;
  botToken?: string;
  provider?: string;
}

export interface NotificationTestResult {
  ok: boolean;
  status: string;
  message: string | null;
}

export interface ResolveChatIdResult {
  found: boolean;
  chatId?: string | null;
  message?: string | null;
  error?: string | null;
}

export interface NotificationResponse<T> {
  /** `true` iff the HTTP status was 2xx. */
  ok: boolean;
  /** Integer status code, or 0 on a network error. */
  status: number;
  /** Parsed JSON body when the response was JSON, else `null`. */
  data: T | null;
  /** Broker error code when present. */
  error: string | null;
  /** Specific validation reason when present. */
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

export interface NotificationRequestOptions {
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
  method: 'GET' | 'POST' | 'PUT',
  path: string,
  body: unknown,
  options: NotificationRequestOptions,
): Promise<NotificationResponse<T>> {
  adoptBootstrapToken();

  const stateChanging = method === 'POST' || method === 'PUT';
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

/** GET /api/v1/settings/notifications — secret-free settings (never the token). */
export function getNotificationSettings(
  options: NotificationRequestOptions = {},
): Promise<NotificationResponse<NotificationSettings>> {
  return request<NotificationSettings>('GET', NOTIFICATIONS_PATH, undefined, options);
}

/** PUT /api/v1/settings/notifications — save enabled + chatId + optional write-only token. */
export function saveNotificationSettings(
  body: NotificationSaveRequest,
  options: NotificationRequestOptions = {},
): Promise<NotificationResponse<NotificationSaveResult>> {
  return request<NotificationSaveResult>('PUT', NOTIFICATIONS_PATH, body, options);
}

/** POST /api/v1/settings/notifications/test — send a real test message (token read server-side). */
export function sendTestNotification(
  options: NotificationRequestOptions = {},
): Promise<NotificationResponse<NotificationTestResult>> {
  return request<NotificationTestResult>('POST', NOTIFICATIONS_PATH + '/test', undefined, options);
}

/** POST /api/v1/settings/notifications/resolve-chat-id — auto-discover the chat id via getUpdates. */
export function resolveChatId(
  options: NotificationRequestOptions = {},
): Promise<NotificationResponse<ResolveChatIdResult>> {
  return request<ResolveChatIdResult>('POST', NOTIFICATIONS_PATH + '/resolve-chat-id', undefined, options);
}
