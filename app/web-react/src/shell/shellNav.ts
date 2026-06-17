/**
 * Cross-frame navigation helpers for the embedded desktop workspace surfaces.
 *
 * The React surface runs inside the legacy shell's content iframe. The legacy
 * shell owns the left nav and routes sections by URL hash (#/home, #/recipes,
 * …) and re-renders the iframe accordingly. These helpers let an embedded
 * surface (Home / Recipes) ask the shell to switch sections, open the legacy
 * help panel, or import a file — without duplicating the shell's chrome.
 *
 * Everything is same-origin (broker-served), so reaching the parent frame is
 * permitted; every access is guarded so a standalone (non-embedded) render is a
 * harmless no-op.
 */

import type { EditorDraftSeed } from './templateToEditorDraft';

export type ShellSectionId =
  | 'home'
  | 'pantry'
  | 'recipes'
  | 'bakes'
  | 'tastetests'
  | 'chefskeys'
  | 'settings'
  | 'updates';

const SECTION_HASH: Record<ShellSectionId, string> = {
  home: '#/home',
  pantry: '#/pantry',
  recipes: '#/recipes',
  bakes: '#/bakes',
  tastetests: '#/taste-tests',
  chefskeys: '#/keys',
  settings: '#/settings',
  updates: '#/updates',
};

/** Key used to hand a selected recipe from Home to the Recipes workspace. */
export const PENDING_SELECT_KEY = 'cookbook.pendingSelectRecipe';

/** Key used to hand a Pantry-seeded recipe draft to the Recipes editor. */
export const PENDING_DRAFT_KEY = 'cookbook.pendingRecipeDraft';

/** Key used to ask the Recipes workspace to open the Import Command modal. */
export const PENDING_IMPORT_COMMAND_KEY = 'cookbook.pendingImportCommand';

/** Key used to hand a just-started cook from the editor to the Bakes surface. */
export const PENDING_BAKE_SELECT_KEY = 'cookbook.pendingSelectBake';

function isEmbedded(): boolean {
  try {
    return typeof window !== 'undefined' && window.parent !== window;
  } catch {
    return false;
  }
}

/** Ask the legacy shell to switch to another section via its hash router. */
export function requestShellSection(section: ShellSectionId): void {
  if (!isEmbedded()) {
    return;
  }
  try {
    window.parent.location.hash = SECTION_HASH[section];
  } catch {
    // Cross-origin or detached parent — nothing we can safely do.
  }
}

/** Open the legacy shell's help panel by activating its existing button. */
export function openShellHelp(): void {
  if (!isEmbedded()) {
    return;
  }
  try {
    const doc = window.parent.document;
    const btn = doc.getElementById('help-button') as HTMLElement | null;
    if (btn) {
      btn.click();
    }
  } catch {
    // Same-origin access blocked — leave help to the topbar button.
  }
}

/**
 * Cross-surface intent: ask the Pantry to open the bundled PAX Cookbook User
 * Guide. The flag lives on the top window so it is shared (same-origin) between
 * the legacy shell's help panel and the embedded React surfaces; the Pantry
 * reads and clears it on mount / re-selection. Pure in-memory hint — no storage,
 * no network. Falling back to the local window keeps a standalone render safe.
 */
const PANTRY_DOC_INTENT_KEY = '__paxPantryDocIntent';

function intentHost(): Record<string, unknown> | null {
  try {
    return (window.top ?? window) as unknown as Record<string, unknown>;
  } catch {
    return window as unknown as Record<string, unknown>;
  }
}

/** Navigate to the Pantry and request that it open the User Guide document. */
export function requestPantryUserGuide(): void {
  const host = intentHost();
  if (host) {
    try {
      host[PANTRY_DOC_INTENT_KEY] = 'userguide';
    } catch {
      // ignore — Pantry simply lands on its default view
    }
  }
  requestShellSection('pantry');
}

/** Read and clear the one-shot Pantry doc intent. */
export function takePantryDocIntent(): boolean {
  const host = intentHost();
  if (!host) {
    return false;
  }
  try {
    if (host[PANTRY_DOC_INTENT_KEY] === 'userguide') {
      delete host[PANTRY_DOC_INTENT_KEY];
      return true;
    }
  } catch {
    // ignore
  }
  return false;
}

/** Remember which recipe the Recipes workspace should pre-select on load. */
export function rememberPendingSelect(recipeId: string): void {
  try {
    window.sessionStorage.setItem(PENDING_SELECT_KEY, recipeId);
  } catch {
    // Storage disabled — selection simply will not pre-seed.
  }
}

/** Read and clear the pending recipe selection (one-shot). */
export function takePendingSelect(): string | null {
  try {
    const value = window.sessionStorage.getItem(PENDING_SELECT_KEY);
    if (value) {
      window.sessionStorage.removeItem(PENDING_SELECT_KEY);
    }
    return value && value.length > 0 ? value : null;
  } catch {
    return null;
  }
}

/**
 * Remember which cook the Bakes surface should focus on its next load. Set by
 * the editor immediately after the broker records a bake (HTTP 201) and read
 * once by the Bakes surface so a freshly started bake lands selected. This is a
 * pure client-side handoff: the cook record itself comes from the broker, never
 * from this value.
 */
export function rememberPendingBakeSelect(cookId: string): void {
  try {
    window.sessionStorage.setItem(PENDING_BAKE_SELECT_KEY, cookId);
  } catch {
    // Storage disabled — the bake simply will not pre-focus.
  }
}

/** Read and clear the pending bake selection (one-shot). */
export function takePendingBakeSelect(): string | null {
  try {
    const value = window.sessionStorage.getItem(PENDING_BAKE_SELECT_KEY);
    if (value) {
      window.sessionStorage.removeItem(PENDING_BAKE_SELECT_KEY);
    }
    return value && value.length > 0 ? value : null;
  } catch {
    return null;
  }
}

/**
 * Stash a Pantry-seeded recipe draft for the Recipes editor to pick up on its
 * next mount. Client-side only (sessionStorage): nothing is sent to the broker
 * and no recipe is persisted. The draft is an in-memory editor state the user
 * still has to complete and save themselves.
 */
export function rememberPendingDraft(seed: EditorDraftSeed): void {
  try {
    window.sessionStorage.setItem(PENDING_DRAFT_KEY, JSON.stringify(seed));
  } catch {
    // Storage disabled — the draft simply will not pre-seed the editor.
  }
}

/** Read and clear the pending recipe draft (one-shot). */
export function takePendingDraft(): EditorDraftSeed | null {
  try {
    const raw = window.sessionStorage.getItem(PENDING_DRAFT_KEY);
    if (raw) {
      window.sessionStorage.removeItem(PENDING_DRAFT_KEY);
    }
    if (!raw) {
      return null;
    }
    const parsed = JSON.parse(raw) as unknown;
    if (
      parsed &&
      typeof parsed === 'object' &&
      typeof (parsed as { templateId?: unknown }).templateId === 'string' &&
      typeof (parsed as { note?: unknown }).note === 'string' &&
      (parsed as { state?: unknown }).state &&
      typeof (parsed as { state?: unknown }).state === 'object'
    ) {
      return parsed as EditorDraftSeed;
    }
    return null;
  } catch {
    return null;
  }
}

export function rememberPendingImportCommand(): void {
  try {
    window.sessionStorage.setItem(PENDING_IMPORT_COMMAND_KEY, '1');
  } catch {
    // Storage disabled — the import-command request simply will not carry over.
  }
}

export function takePendingImportCommand(): boolean {
  try {
    const raw = window.sessionStorage.getItem(PENDING_IMPORT_COMMAND_KEY);
    if (raw === '1') {
      window.sessionStorage.removeItem(PENDING_IMPORT_COMMAND_KEY);
      return true;
    }
  } catch {
    // Storage disabled.
  }
  return false;
}

/** Short local date for "Modified …" lines, or '' when unparseable. */
export function formatModified(value: unknown): string {
  if (typeof value !== 'string' || value.length === 0) {
    return '';
  }
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return '';
  }
  return parsed.toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  });
}
