/**
 * File-open import handoff client.
 *
 * When the user double-clicks a .paxlite / .pax file, the Windows app stages it
 * as a one-time, locally-staged ticket and navigates the WebView to
 * `/app/index.html?import=<id>` — but only after the broker lock is Unlocked
 * (i.e. the normal Windows Hello / lock ceremony has completed on the legacy
 * shell). This module reads that ticket id, confirms the lock is open, consumes
 * the ticket through the authenticated, CSRF-protected, lock-gated broker
 * routes, and publishes the result to a tiny in-memory store the Recipes
 * builder subscribes to.
 *
 * Hard boundaries (mirroring the broker side):
 *   - The absolute file path is NEVER received here. The broker returns only
 *     id / kind / fileName and, for .paxlite and .pax, the file TEXT for the
 *     in-browser importers.
 *   - Both .paxlite and .pax are validated in the browser before they are
 *     applied; the broker only reads and hands off the file text and never
 *     interprets, runs, or mutates it.
 *   - Nothing here runs PAX, auto-bakes, or fabricates a success. A consumed
 *     recipe file lands in the builder as a draft for the user to review.
 *   - Auth mirrors brokerBridge exactly: adopt the inline bootstrap token once,
 *     send it as `Authorization: Bearer`, and add the CSRF header on the POST.
 */

import { adoptBootstrapToken } from './brokerBridge';

const TOKEN_KEY = 'cookbook.sessionToken';
const LOCK_STATE_PATH = '/api/v1/broker/lock-state';
const IMPORT_HANDOFF_PENDING_PATH = '/api/v1/import-handoff/pending';
const IMPORT_HANDOFF_CONSUME_PATH = '/api/v1/import-handoff/consume';
const IMPORT_QUERY_PARAM = 'import';
const DEFAULT_TIMEOUT_MS = 30000;

/** A consumed import waiting for the Recipes builder to apply it. */
export type PendingImport =
  | { kind: 'paxlite'; fileName: string; text: string }
  | { kind: 'pax'; fileName: string; text: string };

export type ConsumeOutcome =
  | { status: 'paxlite'; fileName: string; text: string }
  | { status: 'pax'; fileName: string; text: string }
  | { status: 'locked' }
  | { status: 'not-found' }
  | { status: 'expired' }
  | { status: 'too-large' }
  | { status: 'error'; message: string };

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

async function fetchJson(
  method: 'GET' | 'POST',
  path: string,
  body: unknown,
): Promise<{ status: number; data: Record<string, unknown> | null }> {
  adoptBootstrapToken();

  const stateChanging = method === 'POST';
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), DEFAULT_TIMEOUT_MS);

  const init: RequestInit = {
    method,
    headers: buildHeaders(stateChanging),
    signal: controller.signal,
  };
  if (stateChanging && body !== undefined) {
    init.body = JSON.stringify(body);
  }

  let response: Response;
  try {
    response = await fetch(path, init);
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

/**
 * Read the import ticket id from the current URL's `?import=` query, if any.
 * Returns null when the app was not launched via a file-open handoff.
 */
export function readImportTicketId(): string | null {
  if (typeof window === 'undefined') {
    return null;
  }
  try {
    const params = new URLSearchParams(window.location.search);
    const id = params.get(IMPORT_QUERY_PARAM);
    return id && id.length > 0 ? id : null;
  } catch {
    return null;
  }
}

/**
 * Remove the `?import=` parameter from the address bar without reloading, so a
 * manual refresh does not attempt to re-consume an already-consumed ticket.
 */
export function clearImportTicketFromUrl(): void {
  if (typeof window === 'undefined' || typeof window.history === 'undefined') {
    return;
  }
  try {
    const url = new URL(window.location.href);
    if (url.searchParams.has(IMPORT_QUERY_PARAM)) {
      url.searchParams.delete(IMPORT_QUERY_PARAM);
      window.history.replaceState(null, '', url.toString());
    }
  } catch {
    // Best-effort cosmetic cleanup.
  }
}

/**
 * Read the current broker lock state. Returns the broker's `state` string
 * ("Locked" / "Unlocked") or null when it could not be read.
 */
export async function fetchLockState(): Promise<string | null> {
  const { status, data } = await fetchJson('GET', LOCK_STATE_PATH, undefined);
  if (status !== 200 || !data) {
    return null;
  }
  const state = data.state;
  return typeof state === 'string' ? state : null;
}

/** Whether the broker reports a pending import ticket (id / kind / fileName only). */
export async function hasPendingImport(): Promise<boolean> {
  const { status, data } = await fetchJson('GET', IMPORT_HANDOFF_PENDING_PATH, undefined);
  if (status !== 200 || !data) {
    return false;
  }
  return Array.isArray(data.pending) && data.pending.length > 0;
}

/**
 * Consume a staged import ticket one time. The broker enforces auth, CSRF, and
 * the lock gate; a 423 means the kitchen is still locked.
 */
export async function consumeImport(id: string): Promise<ConsumeOutcome> {
  const { status, data } = await fetchJson('POST', IMPORT_HANDOFF_CONSUME_PATH, { id });

  if (status === 423) {
    return { status: 'locked' };
  }
  if (status === 404) {
    return { status: 'not-found' };
  }
  if (status === 410) {
    return { status: 'expired' };
  }
  if (status === 413) {
    return { status: 'too-large' };
  }
  if (status !== 200 || !data) {
    return { status: 'error', message: 'The recipe file could not be opened.' };
  }

  const kind = typeof data.kind === 'string' ? data.kind : '';
  const fileName = typeof data.fileName === 'string' ? data.fileName : 'recipe';

  if (kind === 'paxlite' && typeof data.text === 'string') {
    return { status: 'paxlite', fileName, text: data.text };
  }
  if (kind === 'pax' && typeof data.text === 'string') {
    return { status: 'pax', fileName, text: data.text };
  }

  return { status: 'error', message: 'The recipe file could not be opened.' };
}

// ---------------------------------------------------------------------------
// Pending-import store — a minimal pub/sub the Recipes builder subscribes to so
// a consumed import is applied exactly once, regardless of which shell section
// is mounted when the handoff completes.
// ---------------------------------------------------------------------------

let pendingImport: PendingImport | null = null;
const listeners = new Set<() => void>();

function emit(): void {
  for (const listener of listeners) {
    listener();
  }
}

export function setPendingImport(next: PendingImport): void {
  pendingImport = next;
  emit();
}

/** Read the current pending import without clearing it (for useSyncExternalStore). */
export function getPendingImport(): PendingImport | null {
  return pendingImport;
}

/** Take and clear the pending import so it is applied exactly once. */
export function takePendingImport(): PendingImport | null {
  const current = pendingImport;
  pendingImport = null;
  if (current) {
    emit();
  }
  return current;
}

export function subscribePendingImport(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}
