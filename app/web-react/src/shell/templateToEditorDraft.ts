/**
 * Pantry starting point -> recipe editor draft mapping.
 *
 * `templateToEditorDraft()` turns a read-only Pantry template detail into a
 * fresh, in-memory recipe draft for the Recipes editor. It is a pure function:
 * it reads the template detail, never writes to it, and performs no network,
 * persistence, or execution. The draft it produces is the same editable
 * `MiniKitchenRecipeState` a New Recipe would start from, pre-filled only with
 * the safe, template-declared defaults (what to collect, the processing mode,
 * and the sign-in method).
 *
 * It deliberately does NOT invent the per-run details a user must own: no
 * tenant id, no client id, no secret or certificate thumbprint, no date range,
 * and no output path. Those are left blank so the editor's normal save
 * requirements ask the user to provide them. A recipe is only created when the
 * user saves in the editor; nothing here saves, materializes, cooks, or
 * schedules anything.
 */
import type {
  MiniKitchenRecipeState,
  RollupMode,
  DashboardTarget,
  AuthMode,
} from '../features/mini-kitchen/types';
import {
  createDefaultMiniKitchenRecipe,
  detectMatchingPreset,
} from '../features/mini-kitchen/lib/defaultRecipe';
import { normalizeRecipe } from '../features/mini-kitchen/lib/normalizeRecipe';
import type { TemplateDetail } from '../host/templateCatalog';

/** A recipe draft seeded from a Pantry starting point, ready for the editor. */
export interface EditorDraftSeed {
  templateId: string;
  templateName: string;
  /** Friendly banner shown in the editor so the source is clear. */
  note: string;
  /** The editable recipe state the editor opens with. */
  state: MiniKitchenRecipeState;
}

/** Result of mapping a template detail to an editor draft. */
export interface TemplateDraftResult {
  ok: boolean;
  /** When `ok` is false, a short, friendly reason for the disabled action. */
  reason: string | null;
  seed: EditorDraftSeed | null;
}

const KNOWN_AUTH_MODES: readonly AuthMode[] = [
  'WebLogin',
  'DeviceCode',
  'AppRegistrationSecret',
  'AppRegistrationCertificate',
  'ManagedIdentity',
];

/**
 * Map a template's declared sign-in method onto a known `AuthMode`.
 *  - `undefined` return: the template did not declare one — keep the editor
 *    default (WebLogin) and do not require a tenant id.
 *  - `null` return: the template declared a method this build does not model —
 *    the template is not mappable.
 */
function mapAuthMode(raw: string | null): AuthMode | null | undefined {
  if (raw === null) {
    return undefined;
  }
  const match = KNOWN_AUTH_MODES.find(
    m => m.toLowerCase() === raw.trim().toLowerCase(),
  );
  return match ?? null;
}

/**
 * Map a template's declared processing mode onto a known `RollupMode`.
 *  - `undefined`: not declared — leave the editor default.
 *  - `null`: declared but unrecognized — not mappable.
 */
function mapRollup(raw: string | null): RollupMode | null | undefined {
  if (raw === null) {
    return undefined;
  }
  switch (raw.trim().toLowerCase()) {
    case 'none':
      return 'none';
    case 'rollup':
      return 'rollup';
    case 'rollup-plus-raw':
    case 'rollupplusraw':
    case 'rollup plus raw':
      return 'rollup-plus-raw';
    default:
      return null;
  }
}

/**
 * Map a template's declared dashboard column target onto a known
 * `DashboardTarget`.
 *  - `undefined`: not declared, or declared with a value this build does not
 *    model — leave the editor default (AI-in-One). Unknown values are omitted
 *    rather than treated as fatal, so an unfamiliar dashboard never blocks a
 *    draft from opening.
 */
function mapDashboard(raw: string | null): DashboardTarget | undefined {
  if (raw === null) {
    return undefined;
  }
  switch (raw.trim().toLowerCase()) {
    case 'aibv':
      return 'aibv';
    case 'aio':
      return 'aio';
    default:
      return undefined;
  }
}

/**
 * Build a recipe editor draft from a Pantry template detail. Pure and
 * defensive: missing fields fall back to the editor defaults rather than
 * throwing, and the source `detail` is never mutated.
 */
export function templateToEditorDraft(detail: TemplateDetail): TemplateDraftResult {
  if (
    !detail ||
    typeof detail.templateId !== 'string' ||
    detail.templateId.length === 0
  ) {
    return {
      ok: false,
      reason: 'This starting point is missing an identifier, so it can’t open a draft.',
      seed: null,
    };
  }

  const defaults = detail.recipeDefaults ?? {
    includeM365Usage: null,
    includeUserInfo: null,
    rollup: null,
    dashboard: null,
    authMode: null,
  };

  const auth = mapAuthMode(defaults.authMode ?? null);
  if (auth === null) {
    return {
      ok: false,
      reason: 'This starting point uses a sign-in method this build can’t pre-fill yet.',
      seed: null,
    };
  }

  const rollup = mapRollup(defaults.rollup ?? null);
  if (rollup === null) {
    return {
      ok: false,
      reason: 'This starting point uses a processing mode this build can’t pre-fill yet.',
      seed: null,
    };
  }

  const dashboard = mapDashboard(defaults.dashboard ?? null);

  const templateName =
    typeof detail.displayName === 'string' && detail.displayName.length > 0
      ? detail.displayName
      : detail.templateId;

  // Start from the same blank draft a New Recipe uses, then layer on only the
  // template's safe, declared defaults. Per-run details (tenant id, dates,
  // output path, secrets) are intentionally left blank.
  const base = createDefaultMiniKitchenRecipe();
  const seedState: MiniKitchenRecipeState = {
    ...base,
    identity: { ...base.identity, name: templateName },
    query: {
      ...base.query,
      ...(typeof defaults.includeM365Usage === 'boolean'
        ? { includeM365Usage: defaults.includeM365Usage }
        : {}),
      ...(typeof defaults.includeUserInfo === 'boolean'
        ? { includeUserInfo: defaults.includeUserInfo }
        : {}),
    },
    processing: {
      ...base.processing,
      ...(rollup !== undefined ? { rollup } : {}),
      ...(dashboard !== undefined ? { dashboard } : {}),
    },
    auth: {
      ...base.auth,
      ...(auth !== undefined ? { mode: auth } : {}),
    },
  };

  const normalized = normalizeRecipe(seedState);

  // Align the Step 1 preset highlight with the recipe the template produced.
  // The blank base draft starts on `customAuditExport`; after the template's
  // collection, processing, and dashboard defaults are layered on, re-detect
  // which preset card this recipe matches so the editor opens with the right
  // one selected. Falls back to `customAuditExport` when nothing matches.
  const withDetectedPreset: MiniKitchenRecipeState = {
    ...normalized,
    ingredients: {
      ...normalized.ingredients,
      preset: detectMatchingPreset(normalized),
    },
  };

  return {
    ok: true,
    reason: null,
    seed: {
      templateId: detail.templateId,
      templateName,
      note: `Started from Pantry: ${templateName}. Complete the required details, then save the recipe.`,
      state: withDetectedPreset,
    },
  };
}

/** True when a starting point can open an editor draft in this build. */
export function canStartFromTemplate(detail: TemplateDetail | null): boolean {
  if (!detail) {
    return false;
  }
  return templateToEditorDraft(detail).ok;
}
