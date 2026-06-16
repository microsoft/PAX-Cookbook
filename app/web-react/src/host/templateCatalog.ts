/**
 * Read-only template catalog client.
 *
 * Reads the bundled template catalog the broker already exposes so the Pantry
 * surface can browse recipe starting points: the template list and a single
 * template's full detail body. Everything here is a GET; nothing in this module
 * writes, materializes, cooks, bakes, schedules, acquires, or mutates any
 * broker state.
 *
 * Hard boundaries:
 *   - GET only. There is intentionally no materialize / POST / PUT / DELETE
 *     helper here and one must never be added. Pantry only reflects the
 *     catalog; turning a starting point into a saved recipe is owned elsewhere.
 *   - No absolute filesystem paths or secrets are surfaced; this module exposes
 *     only the catalog display fields the broker already returns.
 *   - Auth mirrors brokerBridge / systemInfo: adopt the inline bootstrap token
 *     once and send it as `Authorization: Bearer`. No CSRF header is sent
 *     because no request here is state-changing.
 */

import { adoptBootstrapToken } from './brokerBridge';

const TOKEN_KEY = 'cookbook.sessionToken';
const TEMPLATES_PATH = '/api/v1/templates';
const DEFAULT_TIMEOUT_MS = 30000;

/** One row of the template list (broker BuildSummary projection). */
export interface TemplateSummary {
  templateId: string;
  templateVersion: string | null;
  templateSchemaVersion: number | null;
  displayName: string | null;
  shortDescription: string | null;
  category: string | null;
  minPaxScriptVersion: string | null;
  minCookbookVersion: string | null;
  manualGuidanceCount: number;
}

/** A required per-instance input declared by a template. */
export interface TemplateInput {
  field: string | null;
  kind: string | null;
  required: boolean;
  description: string | null;
}

/** One artifact a template's recipe is described as producing. */
export interface TemplateArtifact {
  kind: string | null;
  name: string | null;
  description: string | null;
}

/** One block of operator guidance carried by a template. */
export interface TemplateGuidance {
  heading: string | null;
  audience: string | null;
  body: readonly string[];
}

/** The recipe settings a template pre-fills when used as a starting point. */
export interface TemplateRecipeDefaults {
  includeM365Usage: boolean | null;
  includeUserInfo: boolean | null;
  rollup: string | null;
  dashboard: string | null;
  authMode: string | null;
}

/** The full template detail body (broker GetDetail projection). */
export interface TemplateDetail {
  templateId: string;
  templateVersion: string | null;
  templateSchemaVersion: number | null;
  displayName: string | null;
  shortDescription: string | null;
  category: string | null;
  minPaxScriptVersion: string | null;
  minCookbookVersion: string | null;
  producesSummary: string | null;
  artifacts: readonly TemplateArtifact[];
  authModes: readonly string[];
  inputs: readonly TemplateInput[];
  recipeDefaults: TemplateRecipeDefaults;
  guidance: readonly TemplateGuidance[];
  provenanceSource: string | null;
  lastReviewed: string | null;
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

function boolOrNull(value: unknown): boolean | null {
  return typeof value === 'boolean' ? value : null;
}

function arr(value: unknown): readonly unknown[] {
  return Array.isArray(value) ? value : [];
}

function stringList(value: unknown): readonly string[] {
  const out: string[] = [];
  for (const item of arr(value)) {
    if (typeof item === 'string' && item.length > 0) {
      out.push(item);
    }
  }
  return out;
}

function mapSummary(raw: Record<string, unknown>): TemplateSummary | null {
  const templateId = str(raw.templateId);
  if (!templateId) {
    return null;
  }
  return {
    templateId,
    templateVersion: str(raw.templateVersion),
    templateSchemaVersion: num(raw.templateSchemaVersion),
    displayName: str(raw.displayName),
    shortDescription: str(raw.shortDescription),
    category: str(raw.category),
    minPaxScriptVersion: str(raw.minPaxScriptVersion),
    minCookbookVersion: str(raw.minCookbookVersion),
    manualGuidanceCount: num(raw.manualGuidanceCount) ?? 0,
  };
}

/** GET /api/v1/templates — read-only list of recipe starting points. */
export async function listTemplates(): Promise<ReadResult<readonly TemplateSummary[]>> {
  const { status, data } = await getJson(TEMPLATES_PATH);
  if (status !== 200 || !data) {
    return { ok: false };
  }
  const rows = arr(data.templates);
  const templates: TemplateSummary[] = [];
  for (const row of rows) {
    const mapped = mapSummary(obj(row));
    if (mapped) {
      templates.push(mapped);
    }
  }
  return { ok: true, data: templates };
}

/** GET /api/v1/templates/{id} — read-only detail body for one starting point. */
export async function getTemplate(
  templateId: string,
): Promise<ReadResult<TemplateDetail>> {
  const { status, data } = await getJson(
    TEMPLATES_PATH + '/' + encodeURIComponent(templateId),
  );
  if (status !== 200 || !data) {
    return { ok: false };
  }
  const t = obj(data.template);
  const id = str(t.templateId);
  if (!id) {
    return { ok: false };
  }

  const produces = obj(t.produces);
  const requires = obj(t.requires);
  const recipeDefaults = obj(t.recipeDefaults);
  const ingredients = obj(recipeDefaults.ingredients);
  const m365 = obj(ingredients.m365Usage);
  const entra = obj(ingredients.entraUserData);
  const processing = obj(recipeDefaults.processing);
  const auth = obj(recipeDefaults.auth);
  const provenance = obj(t.provenance);

  const artifacts: TemplateArtifact[] = [];
  for (const a of arr(produces.artifacts)) {
    const ao = obj(a);
    artifacts.push({
      kind: str(ao.kind),
      name: str(ao.name),
      description: str(ao.description),
    });
  }

  const inputs: TemplateInput[] = [];
  for (const i of arr(requires.inputs)) {
    const io = obj(i);
    inputs.push({
      field: str(io.field),
      kind: str(io.kind),
      required: io.required === true,
      description: str(io.description),
    });
  }

  const guidance: TemplateGuidance[] = [];
  for (const g of arr(t.manualGuidance)) {
    const go = obj(g);
    guidance.push({
      heading: str(go.heading),
      audience: str(go.audience),
      body: stringList(go.body),
    });
  }

  return {
    ok: true,
    data: {
      templateId: id,
      templateVersion: str(t.templateVersion),
      templateSchemaVersion: num(t.templateSchemaVersion),
      displayName: str(t.displayName),
      shortDescription: str(t.shortDescription),
      category: str(t.category),
      minPaxScriptVersion: str(t.minPaxScriptVersion),
      minCookbookVersion: str(t.minCookbookVersion),
      producesSummary: str(produces.summary),
      artifacts,
      authModes: stringList(requires.authModes),
      inputs,
      recipeDefaults: {
        includeM365Usage: boolOrNull(m365.includeM365Usage),
        includeUserInfo: boolOrNull(entra.includeUserInfo),
        rollup: str(processing.rollup),
        dashboard: str(processing.dashboard),
        authMode: str(auth.mode),
      },
      guidance,
      provenanceSource: str(provenance.source),
      lastReviewed: str(provenance.lastReviewed),
    },
  };
}
