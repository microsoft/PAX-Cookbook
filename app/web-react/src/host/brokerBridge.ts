/**
 * Persistence-only broker bridge for the React Recipes surface.
 *
 * This module is the single, narrow channel through which the React shell talks
 * to the local broker. It exposes two families and nothing else:
 *
 *   - Recipe persistence — list, get, create, update, delete, and readiness against the
 *     broker's authenticated recipe routes. The readiness call is a read-only,
 *     non-executing projection: it asks the broker whether a recipe could run
 *     and what is still missing, and the broker answers without invoking PAX,
 *     spawning a process, creating a cook/bake, writing a row, or reading a
 *     secret.
 *   - Cook history (read-only) — listCooks, getCook, and getCookLog against the
 *     broker's GET cook-read routes. These project already-recorded cook
 *     history / detail / log for the Bakes surface. They are GET-only: they
 *     never start, stop, cancel, or retry a cook, never invoke PAX, never write
 *     a row, and never read a secret. The log read is addressed by cookId only
 *     (never an arbitrary filesystem path) and returns the broker's managed,
 *     redacted cook.log as text.
 *   - Cook start — the broker's single execution mechanism, reached through two
 *     sanctioned entry points this module exposes: startCook (the manual bake,
 *     POST /api/v1/recipes/{id}/cook, no body) and resumeCook (a one-time
 *     resume-from-checkpoint recovery run, POST /api/v1/resume-cook). Neither is
 *     a separate execution channel: both funnel into the broker's one sanctioned
 *     cook core (engine SHA re-verify, child spawn, log, status, stop/cancel).
 *     Each carries no command text, secret, or script path (resumeCook carries
 *     only a checkpoint path + force + optional Chef's Key id), spawns nothing
 *     in the browser, lets the broker own every gate (token, CSRF, lock, Windows
 *     Hello reauth, validation, engine, busy, integrity), returns a typed
 *     outcome, and never fabricates a cook record. The browser-owned Windows
 *     Hello step-up both may require lives in its own module (manualCookReauth),
 *     not here.
 *   - Cook stop / cancel (X6) — stopCook posts to the broker's authenticated
 *     POST /api/v1/cooks/{id}/stop|kill. It is lifecycle control, not a second
 *     execution channel: it spawns nothing, and only the broker terminates a
 *     running cook it supervises.
 *   - Schedule configuration (X7) — putScheduledTask / deleteScheduledTask /
 *     getScheduledTask against the broker's authenticated, recipe-scoped
 *     PUT/DELETE/GET /api/v1/recipes/{id}/scheduled-task sub-routes. They let
 *     the user configure a per-user Windows Task Scheduler task for a saved
 *     recipe entirely from the app UI. They carry only a recurrence + ids —
 *     never a secret — and they do NOT execute PAX or add a second execution
 *     channel: a scheduled run still flows through the broker's single
 *     sanctioned cook core (the headless one-shot the broker registers), never
 *     from the browser. The broker owns every gate (token, CSRF, lock,
 *     Chef's Key requirement, recurrence validation, OS task registration).
 *   - Native path picker (non-executing) — browsePath posts to the broker's
 *     authenticated POST /api/v1/browse-path and returns only the single local
 *     path the user chose in an OS-native file/folder dialog the broker opens.
 *     It is NOT an execution channel: it spawns nothing, runs no PAX, writes no
 *     row, and carries no secret or command — only a picker mode and optional
 *     title / filters / starting directory. The chosen path is a value the user
 *     picked; nothing is auto-submitted.
 *
 * This module deliberately does NOT expose, and must never grow, any of the
 * following:
 *
 *   - any execution call beyond the sanctioned cook-start entry points
 *     (startCook, resumeCook) and the X6 stop/cancel control above: no retry /
 *     pause / taste-test / dry-run / re-run helper, and no other cook-start
 *     helper under any name (runRecipe, bakeRecipe, cookRecipe, executeRecipe,
 *     startProcess, …) — every execution still funnels into the broker's one
 *     sanctioned cook core
 *   - a broker notification channel, or any scheduler / task-registration
 *     surface OTHER than the recipe-scoped scheduled-task CONFIGURATION
 *     sub-routes described above (those configure a per-user OS task through
 *     the broker and never execute PAX)
 *   - auth-profile, credential, secret, or Credential Manager calls
 *   - any route outside `/api/v1/recipes*`, the read-only `/api/v1/cooks*`
 *     history projections plus the X6 `/api/v1/cooks/{id}/stop|kill` control,
 *     the resume-cook recovery route `/api/v1/resume-cook`, and the
 *     non-executing `/api/v1/browse-path` native picker relay
 *
 * Authentication mirrors the legacy shell exactly: the broker injects an inline
 * bootstrap token into the served HTML, this module adopts it into
 * sessionStorage once (scrubbing the global and the inline element), then sends
 * it as `Authorization: Bearer` on every request. State-changing requests
 * (create / update) additionally carry the `X-Cookbook-Request: 1` CSRF header
 * and `Content-Type: application/json`, exactly as the legacy `api.js` helper
 * does. No request is ever made to an unauthenticated route, and no secret is
 * ever read from or written to the browser.
 */

const TOKEN_KEY = 'cookbook.sessionToken';
const BOOTSTRAP_GLOBAL = '__cookbookBootstrapToken';
const BOOTSTRAP_ELEMENT_ID = 'cookbook-token-bootstrap';
const RECIPES_PATH = '/api/v1/recipes';
const RECIPE_READINESS_PATH = '/api/v1/recipes/readiness';
const COOKS_PATH = '/api/v1/cooks';
const DEFAULT_TIMEOUT_MS = 30000;

export interface BrokerResponse<T> {
  /** `true` iff the HTTP status was 2xx. */
  ok: boolean;
  /** Integer status code, or 0 on a network error. */
  status: number;
  /** Parsed JSON body when the response was JSON, else `null`. */
  data: T | null;
  /** Broker error code (e.g. `validation_failed`, `not_found`) when present. */
  error: string | null;
  /** Human-readable broker message accompanying an error code, when present. */
  message: string | null;
  /** Structured validation errors returned with a `validation_failed` response. */
  validationErrors: unknown[] | null;
  /** Raw response text, for surfacing in the UI when JSON parsing was not possible. */
  rawText: string;
  /** Non-null only when fetch threw, timed out, or was cancelled. */
  networkError: string | null;
}

export interface RecipeSummary {
  recipeId: string;
  name: string;
  [key: string]: unknown;
}

export interface RecipeListBody {
  recipes: RecipeSummary[];
}

export interface RecipeDetailBody {
  recipeId: string;
  recipe: Record<string, unknown>;
}

/**
 * Body of a successful DELETE /api/v1/recipes/{id}. The broker soft-deletes the
 * recipe (moves the file to its trash and stamps a deletion time); `trashPath`
 * is a local filesystem path and is never surfaced in the UI.
 */
export interface RecipeDeleteBody {
  recipeId: string;
  deletedAt: string;
  trashPath: string;
}

/** One run requirement the readiness projection evaluated. */
export interface ReadinessRequirement {
  id: string;
  label: string;
  met: boolean;
  detail: string;
}

/**
 * Body of a `/api/v1/recipes/readiness` response. The broker always answers
 * HTTP 200 with this envelope (even when the recipe is not yet complete), so
 * the builder never has to surface a raw HTTP error in its primary readiness
 * UI. `canRun` is an informational readiness signal only: readiness never
 * itself authorizes a bake, and this field is not consumed by the UI to enable
 * or disable the Bake control. The single gated Bake (run) flow — confirmation
 * plus Windows Hello/WebAuthn step-up — is the only path that runs PAX.
 */
export interface RecipeReadinessBody {
  ok: boolean;
  status: string;
  summary: string;
  canPreview: boolean;
  canRun: boolean;
  requirements: ReadinessRequirement[];
  needsPrep: string[];
  warnings: string[];
  errors: string[];
  engine: { isAcquired: boolean; state: string } | null;
  auth: { mode: string; ready: boolean; detail: string } | null;
  destination: { kind: string; ready: boolean; detail: string } | null;
  recipeId: string | null;
  command: string | null;
  argv: string[];
  extraArguments: string | null;
  details: unknown;
}

export interface RecipeRequestOptions {
  signal?: AbortSignal;
  timeoutMs?: number;
}

let bootstrapAdopted = false;

/**
 * Adopt the broker-injected bootstrap token into sessionStorage exactly once.
 * Mirrors the legacy `boot.js`: reads `window.__cookbookBootstrapToken`, copies
 * a non-empty value into sessionStorage under `cookbook.sessionToken`, then
 * deletes the global and removes the inline `#cookbook-token-bootstrap` element
 * so the raw token does not linger in the DOM or on `window`.
 */
export function adoptBootstrapToken(): void {
  if (bootstrapAdopted) {
    return;
  }
  bootstrapAdopted = true;
  if (typeof window === 'undefined') {
    return;
  }
  try {
    const holder = window as unknown as Record<string, unknown>;
    const injected = holder[BOOTSTRAP_GLOBAL];
    if (typeof injected === 'string' && injected.length > 0) {
      try {
        window.sessionStorage.setItem(TOKEN_KEY, injected);
      } catch {
        // Private-mode tabs / quota errors: leave the global in place is not
        // an option (we still scrub it below); requests will simply be
        // unauthenticated and the broker will answer 401.
      }
    }
    if (BOOTSTRAP_GLOBAL in holder) {
      delete holder[BOOTSTRAP_GLOBAL];
    }
    if (typeof document !== 'undefined') {
      const el = document.getElementById(BOOTSTRAP_ELEMENT_ID);
      if (el && el.parentNode) {
        el.parentNode.removeChild(el);
      }
    }
  } catch {
    // Never let token adoption throw into app startup.
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

async function request<T>(
  method: 'GET' | 'POST' | 'PUT' | 'DELETE',
  path: string,
  body: unknown,
  options: RecipeRequestOptions,
): Promise<BrokerResponse<T>> {
  adoptBootstrapToken();

  // POST / PUT / DELETE are state-changing and must carry the CSRF header
  // (X-Cookbook-Request) the broker requires, or the broker answers 403
  // csrf_required. A DELETE carries no body but still needs the header.
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
    // Always hit the broker fresh; never serve a cached list/detail. Prevents a
    // just-saved recipe (or any changed data) from being masked by a stale
    // cached GET on the next navigation.
    cache: 'no-store',
  };
  if (stateChanging && body !== undefined) {
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
      message: null,
      validationErrors: null,
      rawText: '',
      networkError: aborted ? 'cancelled' : describeError(err),
    };
  }
  clearTimeout(timer);

  const rawText = await safeText(response);
  let data: T | null = null;
  let errorCode: string | null = null;
  let errorMessage: string | null = null;
  let validationErrors: unknown[] | null = null;

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
        if (typeof bag.message === 'string') {
          errorMessage = bag.message;
        }
        if (Array.isArray(bag.errors)) {
          validationErrors = bag.errors as unknown[];
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
    message: errorMessage,
    validationErrors,
    rawText,
    networkError: null,
  };
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

// -----------------------------------------------------------------------------
// public recipe-persistence API (the recipe-route calls this surface makes)
// -----------------------------------------------------------------------------

/** GET /api/v1/recipes — list the active (non-deleted) recipes. */
export function listRecipes(
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<RecipeListBody>> {
  return request<RecipeListBody>('GET', RECIPES_PATH, undefined, options);
}

/** GET /api/v1/recipes/{id} — fetch one recipe's detail. */
export function getRecipe(
  recipeId: string,
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<RecipeDetailBody>> {
  return request<RecipeDetailBody>(
    'GET',
    RECIPES_PATH + '/' + encodeURIComponent(recipeId),
    undefined,
    options,
  );
}

/** POST /api/v1/recipes — create a new recipe from a schema-shaped body. */
export function createRecipe(
  body: Record<string, unknown>,
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<RecipeDetailBody>> {
  return request<RecipeDetailBody>('POST', RECIPES_PATH, body, options);
}

/** PUT /api/v1/recipes/{id} — replace the body of an existing recipe. */
export function updateRecipe(
  recipeId: string,
  body: Record<string, unknown>,
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<RecipeDetailBody>> {
  return request<RecipeDetailBody>(
    'PUT',
    RECIPES_PATH + '/' + encodeURIComponent(recipeId),
    body,
    options,
  );
}

/**
 * DELETE /api/v1/recipes/{id} — soft-delete a recipe. The broker moves the
 * recipe file to its trash, stamps a deletion time, and drops it from the
 * active list, answering { recipeId, deletedAt, trashPath }. It deliberately
 * does NOT touch scheduler, cook, or notification state, so a scheduled
 * recipe's OS task must be unregistered separately (deleteScheduledTask) before
 * this is called. It runs no PAX and reads no secret.
 */
export function deleteRecipe(
  recipeId: string,
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<RecipeDeleteBody>> {
  return request<RecipeDeleteBody>(
    'DELETE',
    RECIPES_PATH + '/' + encodeURIComponent(recipeId),
    undefined,
    options,
  );
}

/**
 * POST /api/v1/recipes/readiness — ask the broker whether a recipe could run
 * and what is still missing. Read-only / non-executing: the broker runs the
 * same validate + command-projection pipeline as preview and layers PAX engine,
 * sign-in / Chef's Key, and destination requirement state on top. It never
 * invokes PAX, creates a cook/bake, writes a recipe, or reads a secret.
 */
export function getRecipeReadiness(
  body: Record<string, unknown>,
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<RecipeReadinessBody>> {
  return request<RecipeReadinessBody>('POST', RECIPE_READINESS_PATH, body, options);
}

// -----------------------------------------------------------------------------
// schedule-configuration API (X7) — recipe-scoped scheduled-task sub-routes
//
// putScheduledTask / deleteScheduledTask / getScheduledTask configure a
// per-user Windows Task Scheduler task for a saved recipe through the broker's
// authenticated PUT/DELETE/GET /api/v1/recipes/{id}/scheduled-task sub-routes.
// They carry only a recurrence + ids (never a secret) and never execute PAX:
// the scheduled run itself still flows through the broker's single sanctioned
// cook core. The broker owns recurrence validation, the bound-Chef's-Key
// requirement, OS task registration, and rollback on registrar failure.
// -----------------------------------------------------------------------------

const SCHEDULED_TASK_SEGMENT = '/scheduled-task';

function scheduledTaskPath(recipeId: string): string {
  return RECIPES_PATH + '/' + encodeURIComponent(recipeId) + SCHEDULED_TASK_SEGMENT;
}

/** Recurrence accepted by PUT …/scheduled-task (daily, or weekly with days). */
export interface ScheduledTaskRecurrence {
  kind: 'daily' | 'weekly';
  /** Hour of day, 0-23. */
  hour: number;
  /** Minute of hour, 0-59. */
  minute: number;
  /** Days of week (0-6) for a weekly recurrence; omitted for daily. */
  daysOfWeek?: number[];
}

/** The schedule projection echoed by the scheduled-task routes (secret-free). */
export interface ScheduledTaskSchedule {
  enabled: boolean;
  scheduledTaskId: string;
  recurrence: {
    kind: string;
    hour: number;
    minute: number;
    daysOfWeek?: number[];
  };
  updatedAt: string;
}

/**
 * Body of PUT/DELETE/GET /api/v1/recipes/{id}/scheduled-task. `schedule` is the
 * projected schedule block (null after a DELETE); `osTask` / `drift` are echoed
 * by GET. The index signature tolerates additional/optional broker fields.
 */
export interface ScheduledTaskBody {
  recipeId: string;
  schedule: ScheduledTaskSchedule | null;
  osTask?: {
    present?: boolean;
    [key: string]: unknown;
  };
  drift?: boolean;
  [key: string]: unknown;
}

/**
 * PUT /api/v1/recipes/{id}/scheduled-task — register or replace the recipe's
 * schedule with the given recurrence. The broker validates the recurrence,
 * requires a bound Chef's Key, writes the recipe's schedule block, and
 * registers the per-user OS task (rolling back on registrar failure).
 */
export function putScheduledTask(
  recipeId: string,
  recurrence: ScheduledTaskRecurrence,
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<ScheduledTaskBody>> {
  return request<ScheduledTaskBody>(
    'PUT',
    scheduledTaskPath(recipeId),
    { recurrence },
    options,
  );
}

/**
 * DELETE /api/v1/recipes/{id}/scheduled-task — remove the recipe's schedule and
 * unregister its OS task. Idempotent: deleting an unscheduled recipe succeeds
 * with `schedule: null`.
 */
export function deleteScheduledTask(
  recipeId: string,
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<ScheduledTaskBody>> {
  return request<ScheduledTaskBody>(
    'DELETE',
    scheduledTaskPath(recipeId),
    undefined,
    options,
  );
}

/**
 * GET /api/v1/recipes/{id}/scheduled-task — read the recipe's current schedule
 * plus the broker's OS-task presence / drift probe. Read-only: it never
 * registers, runs, or mutates anything.
 */
export function getScheduledTask(
  recipeId: string,
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<ScheduledTaskBody>> {
  return request<ScheduledTaskBody>(
    'GET',
    scheduledTaskPath(recipeId),
    undefined,
    options,
  );
}

// -----------------------------------------------------------------------------
// read-only cook-history API (the Bakes surface)
//
// listCooks / getCook / getCookLog are GET-only projections of already-recorded
// cook history. They never start, stop, cancel, or retry a cook, never invoke
// PAX, never write a row, and never read a secret. getCookLog is addressed by
// cookId only — never an arbitrary path — and returns the broker's managed,
// redacted cook.log as text.
// -----------------------------------------------------------------------------

/** One row of cook history as projected by GET /api/v1/cooks. */
export interface CookSummary {
  cookId: string;
  recipeId: string;
  recipeName: string | null;
  status: string;
  trigger: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  durationSeconds: number | null;
  exitCode: number | null;
  closureReason: string | null;
  errorClass: string | null;
  createdAt: string;
  updatedAt: string | null;
}

export interface CookListBody {
  cooks: CookSummary[];
}

/** Recipe snapshot summary echoed in a cook detail. */
export interface CookRecipeSummary {
  recipeId: string | null;
  name: string | null;
  authMode: string | null;
  dashboard?: string | null;
  destinations: Record<string, unknown> | null;
}

/** Engine / PAX fingerprint recorded for a cook (no secret). */
export interface CookEngineFingerprint {
  version: string | null;
  sha256: string | null;
  path: string | null;
}

/** One discovered output destination — metadata only (path / size / existence). */
export interface CookOutputSummary {
  role: string;
  path: string | null;
  exists: boolean;
  sizeBytes: number | null;
}

export interface CookOutputsBody {
  schemaVersion?: number;
  discoveredAt?: string | null;
  outputs: CookOutputSummary[];
}

/** No-secret readiness snapshot echoed in a cook detail. */
export interface CookReadinessSummary {
  status: string;
  engineStatus?: string;
  engineSha256?: string | null;
  authMode?: string;
  chefKeyBound?: boolean;
  requirements?: string[];
  warnings?: string[];
  errors?: string[];
  [key: string]: unknown;
}

export interface CookErrorSummary {
  errorClass: string | null;
  errorMessage: string | null;
  closureReason: string | null;
}

/** Body of GET /api/v1/cooks/{cookId}. */
export interface CookDetailBody {
  cookId: string;
  recipeId: string;
  status: string;
  trigger: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  durationSeconds: number | null;
  exitCode: number | null;
  closureReason: string | null;
  commandRedacted: string | null;
  recipe: CookRecipeSummary | null;
  engine: CookEngineFingerprint | null;
  readiness: CookReadinessSummary | null;
  outputs: CookOutputsBody | null;
  /**
   * Absolute on-disk path to this bake's managed, broker-redacted cook.log,
   * present ONLY when the log file exists. The "Open log" buttons pass it to
   * `openFile` to open it in the user's default app. Null/absent when no log
   * file is present yet.
   */
  logPath?: string | null;
  errorSummary: CookErrorSummary | null;
  /**
   * Non-secret resume context, present only for a `trigger: "resume"` cook. The
   * broker projects the checkpoint folder / .json path the run was resumed
   * from, the force flag, and the bound Chef's Key id (an id only — never a
   * secret). Null or absent for manual and scheduled cooks.
   */
  checkpoint: { path: string; force: boolean; chefKeyId: string | null } | null;
  createdAt: string;
  updatedAt: string | null;
}

/** Result of GET /api/v1/cooks/{cookId}/log. Text, never JSON, on success. */
export interface CookLogResult {
  ok: boolean;
  status: number;
  /** `true` only when the broker returned the managed cook.log (HTTP 200). */
  available: boolean;
  text: string;
  /** Broker error code on a 4xx (`cook_not_found`, `cook_log_not_found`, `invalid_cook_id`). */
  error: string | null;
  /** `true` when the broker is locked (HTTP 423). */
  locked: boolean;
  networkError: string | null;
}

/** GET /api/v1/cooks — list recorded cook history, newest first. */
export function listCooks(
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<CookListBody>> {
  return request<CookListBody>('GET', COOKS_PATH, undefined, options);
}

/** GET /api/v1/cooks/{cookId} — fetch one cook's recorded detail. */
export function getCook(
  cookId: string,
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<CookDetailBody>> {
  return request<CookDetailBody>(
    'GET',
    COOKS_PATH + '/' + encodeURIComponent(cookId),
    undefined,
    options,
  );
}

/**
 * GET /api/v1/cooks/{cookId}/log — read the broker's managed, redacted cook.log
 * as text. Addressed by cookId only (never an arbitrary path). Read-only: it
 * never starts, mutates, or deletes anything. A missing log resolves to a clean
 * `available: false` result rather than throwing.
 */
export async function getCookLog(
  cookId: string,
  options: RecipeRequestOptions = {},
): Promise<CookLogResult> {
  adoptBootstrapToken();

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

  const path = COOKS_PATH + '/' + encodeURIComponent(cookId) + '/log';
  let response: Response;
  try {
    response = await fetch(path, {
      method: 'GET',
      headers: buildHeaders(false),
      signal: controller.signal,
    });
  } catch (err) {
    clearTimeout(timer);
    const aborted = options.signal?.aborted ?? false;
    return {
      ok: false,
      status: 0,
      available: false,
      text: '',
      error: null,
      locked: false,
      networkError: aborted ? 'cancelled' : describeError(err),
    };
  }
  clearTimeout(timer);

  const rawText = await safeText(response);
  const contentType = response.headers.get('Content-Type') ?? '';

  if (response.ok) {
    return {
      ok: true,
      status: response.status,
      available: true,
      text: rawText,
      error: null,
      locked: false,
      networkError: null,
    };
  }

  // Non-2xx: the broker answers JSON with an `error` code. Surface it without
  // ever treating the body as log text.
  let errorCode: string | null = null;
  if (contentType.indexOf('application/json') >= 0 && rawText.length > 0) {
    try {
      const parsed = JSON.parse(rawText) as Record<string, unknown>;
      if (typeof parsed.error === 'string') {
        errorCode = parsed.error;
      }
    } catch {
      errorCode = null;
    }
  }
  return {
    ok: false,
    status: response.status,
    available: false,
    text: '',
    error: errorCode,
    locked: response.status === 423,
    networkError: null,
  };
}

/** Result of GET /api/v1/pantry/repo?owner=…&repo=… — combined repo metadata
 *  and the GitHub-rendered README HTML. */
export interface PantryRepoResult {
  ok: boolean;
  owner?: string;
  repo?: string;
  description?: string | null;
  stars?: number;
  forks?: number;
  language?: string | null;
  license?: string | null;
  updatedAt?: string | null;
  topics?: string[];
  defaultBranch?: string | null;
  /** GitHub-rendered, server-sanitized README HTML (empty when the repo has none). */
  readmeHtml?: string;
  htmlUrl?: string;
  /** Present (with ok: false) when the repo could not be loaded. */
  error?: string;
}

const PANTRY_REPO_PATH = '/api/v1/pantry/repo';

/**
 * GET /api/v1/pantry/repo?owner=<owner>&repo=<repo>. The broker calls GitHub's
 * public REST API server-side for the repo metadata + rendered README HTML and
 * returns one combined JSON document. Authenticated like every other read — the
 * bearer token rides in the Authorization header, never in the URL. The broker
 * only ever contacts api.github.com (parameterized by the validated
 * owner/repo); a missing repo, rate limit, or network failure resolves to a
 * clean `{ ok: false, error }` result rather than throwing.
 */
export async function getPantryRepo(
  owner: string,
  repo: string,
  options: RecipeRequestOptions = {},
): Promise<PantryRepoResult> {
  adoptBootstrapToken();

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

  const path =
    PANTRY_REPO_PATH +
    '?owner=' + encodeURIComponent(owner) +
    '&repo=' + encodeURIComponent(repo);
  let response: Response;
  try {
    response = await fetch(path, {
      method: 'GET',
      headers: buildHeaders(false),
      signal: controller.signal,
    });
  } catch {
    clearTimeout(timer);
    const aborted = options.signal?.aborted ?? false;
    return {
      ok: false,
      error: aborted ? 'Loading was cancelled.' : 'Unable to reach PAX Cookbook.',
    };
  }
  clearTimeout(timer);

  if (response.status === 423) {
    return {
      ok: false,
      error: 'The appliance is locked. Unlock it to view Pantry resources.',
    };
  }

  const rawText = await safeText(response);
  let parsed: Record<string, unknown> | null = null;
  try {
    parsed = JSON.parse(rawText) as Record<string, unknown>;
  } catch {
    parsed = null;
  }

  if (!parsed || typeof parsed.ok !== 'boolean') {
    return { ok: false, error: 'PAX Cookbook returned an unexpected response.' };
  }

  // The broker owns the response shape; pass it through as the typed result.
  return parsed as unknown as PantryRepoResult;
}

/** One entry in a Pantry repository directory listing (a file or a folder). */
export interface PantryContentItem {
  name: string;
  path: string;
  type: 'file' | 'dir';
  size: number;
  /** A trusted GitHub https download URL for a file; null for a folder or an
   *  unsafe value the broker dropped. */
  downloadUrl: string | null;
  sha: string;
}

/** Result of GET /api/v1/pantry/repo-contents?owner=…&repo=…&path=…. */
export interface PantryContentsResult {
  ok: boolean;
  owner?: string;
  repo?: string;
  path?: string;
  items?: PantryContentItem[];
  error?: string;
}

const PANTRY_CONTENTS_PATH = '/api/v1/pantry/repo-contents';

/**
 * GET /api/v1/pantry/repo-contents?owner=<owner>&repo=<repo>&path=<path>. The
 * broker returns one directory listing from GitHub's Contents API for the file
 * explorer tree. `path` is optional (omitted / empty = repo root). Authenticated
 * like every other read — the bearer token rides in the Authorization header,
 * never the URL. The broker only ever contacts api.github.com (parameterized by
 * the validated owner/repo/path); a missing path, rate limit, or network
 * failure resolves to a clean `{ ok: false, error }` result rather than throwing.
 */
export async function getPantryRepoContents(
  owner: string,
  repo: string,
  path = '',
  options: RecipeRequestOptions = {},
): Promise<PantryContentsResult> {
  adoptBootstrapToken();

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

  const requestPath =
    PANTRY_CONTENTS_PATH +
    '?owner=' + encodeURIComponent(owner) +
    '&repo=' + encodeURIComponent(repo) +
    '&path=' + encodeURIComponent(path);
  let response: Response;
  try {
    response = await fetch(requestPath, {
      method: 'GET',
      headers: buildHeaders(false),
      signal: controller.signal,
    });
  } catch {
    clearTimeout(timer);
    const aborted = options.signal?.aborted ?? false;
    return {
      ok: false,
      error: aborted ? 'Loading was cancelled.' : 'Unable to reach PAX Cookbook.',
    };
  }
  clearTimeout(timer);

  if (response.status === 423) {
    return {
      ok: false,
      error: 'The appliance is locked. Unlock it to view Pantry resources.',
    };
  }

  const rawText = await safeText(response);
  let parsed: Record<string, unknown> | null = null;
  try {
    parsed = JSON.parse(rawText) as Record<string, unknown>;
  } catch {
    parsed = null;
  }

  if (!parsed || typeof parsed.ok !== 'boolean') {
    return { ok: false, error: 'PAX Cookbook returned an unexpected response.' };
  }

  return parsed as unknown as PantryContentsResult;
}

const PANTRY_DOWNLOAD_PATH = '/api/v1/pantry/download';

/**
 * Build the same-origin broker proxy URL that streams a single Pantry file from
 * GitHub. `downloadUrl` is a trusted GitHub https URL the broker already
 * validated when it listed the directory; the broker re-validates it
 * server-side before fetching, so this is never an arbitrary-URL surface.
 */
export function pantryDownloadProxyUrl(downloadUrl: string): string {
  return PANTRY_DOWNLOAD_PATH + '?url=' + encodeURIComponent(downloadUrl);
}

/**
 * GET the broker's file download proxy for a Pantry file and return the raw
 * Response so the caller can read it as text, a blob, or a stream (the in-app
 * preview and Save As). Authenticated like every other read — the bearer token
 * rides in the Authorization header, never the URL. The abort timer bounds only
 * time-to-headers: it is cleared the moment the Response resolves, so a large
 * file streams to completion without being cut off (the broker enforces its own
 * 5-minute upstream ceiling). A caller-supplied signal still aborts an
 * in-progress stream.
 */
export async function fetchPantryDownload(
  downloadUrl: string,
  options: RecipeRequestOptions = {},
): Promise<Response> {
  adoptBootstrapToken();

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

  try {
    return await fetch(pantryDownloadProxyUrl(downloadUrl), {
      method: 'GET',
      headers: buildHeaders(false),
      signal: controller.signal,
    });
  } finally {
    clearTimeout(timer);
  }
}

// -----------------------------------------------------------------------------
// bake start — the SINGLE execution channel
//
// startCook is the one and only execution call this module exposes. It posts to
// the broker's authenticated POST /api/v1/recipes/{id}/cook route with no body
// and lets the broker own every gate (token, CSRF, lock, manual-cook reauth,
// validation, engine acquisition, same-recipe busy, PAX integrity). It carries
// no command text or script path, spawns nothing in the browser, and never
// fabricates a cook record: the returned outcome reflects only what the broker
// actually answered. The Windows Hello step-up a manual bake may require lives
// in the separate manualCookReauth module, not here.
// -----------------------------------------------------------------------------

/** Discriminated outcome of a bake (cook-start) attempt. */
export type StartCookOutcome =
  | { kind: 'started'; cookId: string; status: string | null; cookFolder: string | null }
  | { kind: 'reauthRequired'; recipeId: string }
  | { kind: 'unauthorized' }
  | { kind: 'forbidden' }
  | { kind: 'locked' }
  | { kind: 'engineSetupRequired' }
  | { kind: 'recipeBusy'; cookId: string | null }
  | { kind: 'validationFailed'; issueCount: number }
  | { kind: 'notFound' }
  | { kind: 'invalidRecipeId' }
  | { kind: 'appAuthUnsupported'; authMode: string | null }
  | { kind: 'integrityFailed' }
  | { kind: 'diskSpace' }
  | { kind: 'network'; detail: string }
  | { kind: 'error'; code: string | null; status: number };

export interface StartCookResult {
  outcome: StartCookOutcome;
  /** Integer HTTP status, or 0 on a network error. */
  status: number;
}

function readString(bag: Record<string, unknown> | null, key: string): string | null {
  if (!bag) {
    return null;
  }
  const value = bag[key];
  return typeof value === 'string' && value.length > 0 ? value : null;
}

function mapStartCookOutcome(
  recipeId: string,
  status: number,
  bag: Record<string, unknown> | null,
  errorCode: string | null,
): StartCookOutcome {
  if (status === 201) {
    const cookId = readString(bag, 'cookId');
    if (cookId) {
      return {
        kind: 'started',
        cookId,
        status: readString(bag, 'status'),
        cookFolder: readString(bag, 'cookFolder'),
      };
    }
    // A 2xx without a cookId is not a usable start — treat it as a bounded error
    // rather than fabricating a record.
    return { kind: 'error', code: errorCode, status };
  }
  if (status === 401) {
    const code = readString(bag, 'code');
    if (code === 'reAuthRequired' || errorCode === 'reAuthRequired') {
      return { kind: 'reauthRequired', recipeId };
    }
    return { kind: 'unauthorized' };
  }
  if (status === 403) {
    return { kind: 'forbidden' };
  }
  if (status === 423) {
    return { kind: 'locked' };
  }
  if (status === 409) {
    if (errorCode === 'recipe_busy') {
      return { kind: 'recipeBusy', cookId: readString(bag, 'cookId') };
    }
    // acquisitionRequired (or any other 409) means the PAX engine still needs
    // setup before a bake can run.
    return { kind: 'engineSetupRequired' };
  }
  if (status === 412) {
    const errors = bag && Array.isArray(bag.errors) ? (bag.errors as unknown[]) : [];
    return { kind: 'validationFailed', issueCount: errors.length };
  }
  if (status === 404) {
    return { kind: 'notFound' };
  }
  if (status === 400) {
    if (errorCode === 'invalid_recipe_id') {
      return { kind: 'invalidRecipeId' };
    }
    return { kind: 'error', code: errorCode, status };
  }
  if (status === 501) {
    return { kind: 'appAuthUnsupported', authMode: readString(bag, 'authMode') };
  }
  if (status === 507) {
    return { kind: 'diskSpace' };
  }
  if (errorCode === 'pax_script_integrity') {
    return { kind: 'integrityFailed' };
  }
  return { kind: 'error', code: errorCode, status };
}

/**
 * POST /api/v1/recipes/{id}/cook — start a manual bake of an already-saved
 * recipe. No request body: the broker reads the recipe id from the path, runs
 * its full gate ladder, and either records a cook (201) or refuses with a typed
 * error. The browser never spawns PAX and never invents a cook record.
 */
export async function startCook(
  recipeId: string,
  options: RecipeRequestOptions = {},
): Promise<StartCookResult> {
  adoptBootstrapToken();

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

  const path = RECIPES_PATH + '/' + encodeURIComponent(recipeId) + '/cook';
  let response: Response;
  try {
    response = await fetch(path, {
      method: 'POST',
      headers: buildHeaders(true),
      signal: controller.signal,
    });
  } catch (err) {
    clearTimeout(timer);
    const aborted = options.signal?.aborted ?? false;
    return {
      outcome: { kind: 'network', detail: aborted ? 'cancelled' : describeError(err) },
      status: 0,
    };
  }
  clearTimeout(timer);

  const rawText = await safeText(response);
  const contentType = response.headers.get('Content-Type') ?? '';
  let bag: Record<string, unknown> | null = null;
  let errorCode: string | null = null;
  if (contentType.indexOf('application/json') >= 0 && rawText.length > 0) {
    try {
      const parsed = JSON.parse(rawText) as unknown;
      if (parsed && typeof parsed === 'object') {
        bag = parsed as Record<string, unknown>;
        if (typeof bag.error === 'string') {
          errorCode = bag.error;
        }
      }
    } catch {
      bag = null;
    }
  }

  return {
    outcome: mapStartCookOutcome(recipeId, response.status, bag, errorCode),
    status: response.status,
  };
}

// -----------------------------------------------------------------------------
// resume start — the second sanctioned cook-start entry point
//
// resumeCook posts to the broker's authenticated POST /api/v1/resume-cook route
// with a small, non-secret body (the checkpoint folder / .json path, a force
// flag, and an optional Chef's Key id) and lets the broker own every gate
// (token, CSRF, lock, manual-cook reauth, engine acquisition, disk, path,
// integrity). It is NOT a second execution channel: like the scheduled-run
// entry point it flows through the broker's single sanctioned cook core, never
// spawning PAX in the browser and never fabricating a cook record. The body
// carries no secret and no script path — the engine is the managed engine and
// the Chef's Key id resolves the bound sign-in server-side. The Windows Hello
// step-up a resume requires lives in the separate manualCookReauth module
// (keyed to the resume sentinel), not here. The returned outcome mirrors
// startCook so the UI can run the same reauth-retry-once ceremony.
// -----------------------------------------------------------------------------

/** Body of a successful POST /api/v1/resume-cook (201). */
export interface ResumeCookBody {
  cookId?: string;
  cookFolder?: string;
  trigger?: string;
  checkpoint?: { path: string; force: boolean };
}

/** Request body for resumeCook. Carries no secret and no script path. */
export interface ResumeCookRequest {
  checkpointPath: string;
  force: boolean;
  chefKeyId: string | null;
}

/** Discriminated outcome of a resume (cook-start) attempt. Mirrors StartCookOutcome. */
export type ResumeCookOutcome =
  | { kind: 'started'; cookId: string; status: string | null; cookFolder: string | null }
  | { kind: 'reauthRequired' }
  | { kind: 'unauthorized' }
  | { kind: 'forbidden' }
  | { kind: 'locked' }
  | { kind: 'engineSetupRequired' }
  | { kind: 'invalidCheckpointPath' }
  | { kind: 'pathTooLong' }
  | { kind: 'chefKeyProblem'; code: string | null }
  | { kind: 'integrityFailed' }
  | { kind: 'diskSpace' }
  | { kind: 'network'; detail: string }
  | { kind: 'error'; code: string | null; status: number };

export interface ResumeCookResult {
  outcome: ResumeCookOutcome;
  /** Integer HTTP status, or 0 on a network error. */
  status: number;
}

function mapResumeCookOutcome(
  status: number,
  bag: Record<string, unknown> | null,
  errorCode: string | null,
): ResumeCookOutcome {
  if (status === 201) {
    const cookId = readString(bag, 'cookId');
    if (cookId) {
      return {
        kind: 'started',
        cookId,
        status: readString(bag, 'status'),
        cookFolder: readString(bag, 'cookFolder'),
      };
    }
    // A 2xx without a cookId is not a usable start — treat it as a bounded error
    // rather than fabricating a record.
    return { kind: 'error', code: errorCode, status };
  }
  if (status === 401) {
    const code = readString(bag, 'code');
    if (code === 'reAuthRequired' || errorCode === 'reAuthRequired') {
      return { kind: 'reauthRequired' };
    }
    return { kind: 'unauthorized' };
  }
  if (status === 403) {
    return { kind: 'forbidden' };
  }
  if (status === 423) {
    return { kind: 'locked' };
  }
  if (status === 409) {
    // acquisitionRequired (or any other 409) means the PAX engine still needs
    // setup before a resume can run.
    return { kind: 'engineSetupRequired' };
  }
  if (status === 412) {
    if (
      errorCode === 'chefKeyNotFound' ||
      errorCode === 'chefKeyModeMismatch' ||
      errorCode === 'chefKeySecretMissing'
    ) {
      return { kind: 'chefKeyProblem', code: errorCode };
    }
    return { kind: 'error', code: errorCode, status };
  }
  if (status === 507) {
    return { kind: 'diskSpace' };
  }
  if (status === 400) {
    if (errorCode === 'invalid_checkpoint_path') {
      return { kind: 'invalidCheckpointPath' };
    }
    if (errorCode === 'workspace_path_too_long') {
      return { kind: 'pathTooLong' };
    }
    return { kind: 'error', code: errorCode, status };
  }
  if (errorCode === 'pax_script_integrity') {
    return { kind: 'integrityFailed' };
  }
  return { kind: 'error', code: errorCode, status };
}

/**
 * POST /api/v1/resume-cook — resume an interrupted PAX run from its checkpoint.
 * The body names the checkpoint folder (or .json) to resume, whether to use the
 * most recent checkpoint without prompting, and an optional Chef's Key id to
 * sign in with (an id only, never a secret). The broker runs its full gate
 * ladder and either records a resume cook (201) or refuses with a typed error.
 * The browser never spawns PAX and never invents a cook record.
 */
export async function resumeCook(
  body: ResumeCookRequest,
  options: RecipeRequestOptions = {},
): Promise<ResumeCookResult> {
  adoptBootstrapToken();

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

  let response: Response;
  try {
    response = await fetch('/api/v1/resume-cook', {
      method: 'POST',
      headers: buildHeaders(true),
      body: JSON.stringify(body),
      signal: controller.signal,
    });
  } catch (err) {
    clearTimeout(timer);
    const aborted = options.signal?.aborted ?? false;
    return {
      outcome: { kind: 'network', detail: aborted ? 'cancelled' : describeError(err) },
      status: 0,
    };
  }
  clearTimeout(timer);

  const rawText = await safeText(response);
  const contentType = response.headers.get('Content-Type') ?? '';
  let bag: Record<string, unknown> | null = null;
  let errorCode: string | null = null;
  if (contentType.indexOf('application/json') >= 0 && rawText.length > 0) {
    try {
      const parsed = JSON.parse(rawText) as unknown;
      if (parsed && typeof parsed === 'object') {
        bag = parsed as Record<string, unknown>;
        if (typeof bag.error === 'string') {
          errorCode = bag.error;
        }
      }
    } catch {
      bag = null;
    }
  }

  return {
    outcome: mapResumeCookOutcome(response.status, bag, errorCode),
    status: response.status,
  };
}

// -----------------------------------------------------------------------------
// browse path — OS-native file/folder picker relay (non-executing)
//
// browsePath posts to the broker's authenticated POST /api/v1/browse-path route
// and asks the broker to open the OS-native picker on the user's behalf,
// returning only the single local path the user explicitly chose (or nothing if
// they cancelled). It is NOT an execution channel and never touches PAX: it
// spawns no process, writes no row, reads no secret, and carries no command
// text — just a picker mode and an optional title / filters / starting
// directory. The browser opens no dialog itself; the broker owns the picker and
// every gate (token, CSRF, lock). The returned path is a value the user picked —
// surfacing it is the caller's choice, never an auto-submit.
//
// Unlike the recipe calls, this request deliberately waits on a human: the
// broker's fetch stays open while the native dialog is up. So it uses a far
// longer (but still bounded) default timeout, sized for a person browsing the
// filesystem rather than a fast broker round-trip.
// -----------------------------------------------------------------------------

const BROWSE_PICKER_TIMEOUT_MS = 600000;

export interface BrowsePathRequest {
  /** `file` opens a file picker; `folder` opens a folder picker. */
  mode: 'file' | 'folder';
  /** Optional dialog title shown on the native picker. */
  title?: string;
  /** Optional file-type filters (each a display name + bare extensions). */
  filters?: Array<{ name: string; extensions: string[] }>;
  /** Optional directory the picker should open in. */
  initialDirectory?: string;
}

export interface BrowsePathResult {
  /** `true` only when the broker answered 200. */
  ok: boolean;
  /** The chosen local path, or null when cancelled / not answered. */
  path: string | null;
  /** `true` when the user dismissed the picker without choosing. */
  cancelled: boolean;
  /** Integer HTTP status (e.g. 200, 423 locked, 500), or 0 on a network error. */
  status: number;
}

/**
 * POST /api/v1/browse-path — relay an OS-native file/folder picker through the
 * broker and return the single path the user chose. The body names the picker
 * mode and optional title / filters / starting directory; it carries no secret
 * and no command. A 200 yields `{ path, cancelled }` (path is null on cancel);
 * any non-200 (e.g. 423 locked, 500) yields a calm `ok:false` with no path, and
 * a network error yields status 0. The browser opens no dialog and runs nothing.
 */
export async function browsePath(
  body: BrowsePathRequest,
  options: RecipeRequestOptions = {},
): Promise<BrowsePathResult> {
  adoptBootstrapToken();

  const controller = new AbortController();
  const timeoutMs =
    typeof options.timeoutMs === 'number' ? options.timeoutMs : BROWSE_PICKER_TIMEOUT_MS;
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  if (options.signal) {
    if (options.signal.aborted) {
      controller.abort();
    } else {
      options.signal.addEventListener('abort', () => controller.abort(), { once: true });
    }
  }

  let response: Response;
  try {
    response = await fetch('/api/v1/browse-path', {
      method: 'POST',
      headers: buildHeaders(true),
      body: JSON.stringify(body),
      signal: controller.signal,
    });
  } catch {
    clearTimeout(timer);
    return { ok: false, path: null, cancelled: false, status: 0 };
  }
  clearTimeout(timer);

  if (response.status !== 200) {
    return { ok: false, path: null, cancelled: false, status: response.status };
  }

  const rawText = await safeText(response);
  const contentType = response.headers.get('Content-Type') ?? '';
  let path: string | null = null;
  let cancelled = false;
  if (contentType.indexOf('application/json') >= 0 && rawText.length > 0) {
    try {
      const parsed = JSON.parse(rawText) as unknown;
      if (parsed && typeof parsed === 'object') {
        const bag = parsed as Record<string, unknown>;
        if (typeof bag.path === 'string' && bag.path.length > 0) {
          path = bag.path;
        }
        cancelled = bag.cancelled === true;
      }
    } catch {
      path = null;
      cancelled = false;
    }
  }

  return { ok: true, path, cancelled, status: 200 };
}

// -----------------------------------------------------------------------------
// bake stop — the single sanctioned cancel control (X6)
//
// stopCook posts to the broker's authenticated POST /api/v1/cooks/{id}/stop
// route (the /kill route maps to the same broker handler) with no body, and lets
// the broker own every gate (token, CSRF, lock) AND the kill. The browser never
// kills anything itself. The broker accepts the cancel with 202, kills the
// supervised process tree, and transitions the cook to 'canceled' asynchronously;
// the caller relies on the existing Bakes poll to observe that terminal status.
// The returned result reflects only what the broker actually answered.
// -----------------------------------------------------------------------------

/** Outcome of POST /api/v1/cooks/{cookId}/stop (X6 — Stop bake). */
export interface StopCookResult {
  /** `true` only when the broker accepted the cancel (HTTP 202/200). */
  ok: boolean;
  /** Integer HTTP status, or 0 on a network error. */
  status: number;
  /** Broker error code on a 4xx (`cook_not_found`, `cook_not_running`, `cook_not_supervised`). */
  error: string | null;
  networkError: string | null;
}

/**
 * POST /api/v1/cooks/{cookId}/stop — request cancellation of a running bake. No
 * request body: the broker reads the cook id from the path, kills the supervised
 * process tree, and transitions the cook to 'canceled'. The browser spawns and
 * kills nothing; a 202 means the cancel was requested, not that the row has
 * already flipped (the Bakes poll observes the terminal status).
 */
export async function stopCook(
  cookId: string,
  options: RecipeRequestOptions = {},
): Promise<StopCookResult> {
  const res = await request<Record<string, unknown>>(
    'POST',
    COOKS_PATH + '/' + encodeURIComponent(cookId) + '/stop',
    undefined,
    options,
  );
  return {
    ok: res.status === 202 || res.status === 200,
    status: res.status,
    error: res.error,
    networkError: res.networkError,
  };
}

// -----------------------------------------------------------------------------
// Settings → Startup (V2 two-process auto-start toggle).
//
// Two authenticated routes (Bearer for GET, Bearer + CSRF for POST, enforced by
// the broker) that read / write the per-user HKCU Run value auto-starting the
// headless broker daemon at logon. No secret is involved — the broker writes a
// launch command line for its own signed exe. Toggling off removes the Run
// value but does NOT stop the currently-running broker (it just won't auto-start
// next login); toggling on adds it (no immediate effect — the app is already
// running). The installer still creates the value on install (default on); the
// uninstaller still removes it regardless of this toggle.
// -----------------------------------------------------------------------------

const AUTOSTART_PATH = '/api/v1/settings/autostart';

/** Body of GET /api/v1/settings/autostart. */
export interface AutostartState {
  enabled: boolean;
}

/** Body of POST /api/v1/settings/autostart. */
export interface AutostartSetResult {
  ok: boolean;
  enabled: boolean;
}

/** GET /api/v1/settings/autostart — read whether the headless broker auto-starts at logon. */
export function getAutostartEnabled(
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<AutostartState>> {
  return request<AutostartState>('GET', AUTOSTART_PATH, undefined, options);
}

/** POST /api/v1/settings/autostart — enable/disable auto-start at logon (HKCU Run value). */
export function setAutostartEnabled(
  enabled: boolean,
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<AutostartSetResult>> {
  return request<AutostartSetResult>('POST', AUTOSTART_PATH, { enabled }, options);
}

const OPEN_PATH_PATH = '/api/v1/open-path';

/** Body of POST /api/v1/open-path. */
export interface OpenPathResult {
  ok: boolean;
  opened?: string;
}

/**
 * POST /api/v1/open-path — open the Windows folder containing an output file in
 * File Explorer. The broker opens a FOLDER only (never executes a file) and the
 * target must already exist. Used by the "Open folder" buttons (Last Bake,
 * Bakes, etc.). Bearer + CSRF + lock gated like every other route.
 */
export function openPath(
  path: string,
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<OpenPathResult>> {
  return request<OpenPathResult>('POST', OPEN_PATH_PATH, { path }, options);
}

const OPEN_FILE_PATH = '/api/v1/open-file';

/** Body of POST /api/v1/open-file. */
export interface OpenFileResult {
  ok: boolean;
  opened?: string;
}

/**
 * POST /api/v1/open-file — open an output or log FILE in the user's default app.
 * The broker opens only inert document types from a closed allowlist
 * (.log/.txt/.csv/.json/.xml/.html/.pdf) — never an executable / script — via
 * the shell's registered default handler, and the file must already exist. Used
 * by the "Open log" buttons (Last Bake, Bakes detail). Bearer + CSRF + lock
 * gated like every other route.
 */
export function openFile(
  path: string,
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<OpenFileResult>> {
  return request<OpenFileResult>('POST', OPEN_FILE_PATH, { path }, options);
}

const SHUTDOWN_PATH = '/api/v1/shutdown';

/** Body of POST /api/v1/shutdown. */
export interface ShutdownResult {
  ok: boolean;
}

/**
 * POST /api/v1/shutdown — ask the broker to shut down gracefully. Used by the
 * close dialog's "Exit" so exiting stops EVERYTHING, including a separate
 * background broker daemon that an attached window does not own (closing the
 * window alone would leave the daemon serving). The broker returns 200 first
 * and then stops itself (the daemon ends its tray loop; a combined window's
 * in-process host stops). Bearer + CSRF + lock gated like every other route; a
 * locked session cannot shut the broker down (it is not on the lock allow-list).
 */
export function shutdownBroker(
  options: RecipeRequestOptions = {},
): Promise<BrokerResponse<ShutdownResult>> {
  return request<ShutdownResult>('POST', SHUTDOWN_PATH, undefined, options);
}
