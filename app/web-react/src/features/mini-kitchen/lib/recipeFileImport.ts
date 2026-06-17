// Shared .pax / .paxlite FILE import helper. One place that opens a native
// file-browse dialog, reads the chosen file, decides whether it is a full
// .pax recipe or a .paxlite recipe, and parses it into the builder state.
//
// Every import entry point in the app (Home, Recipes, the builder's Step 1
// import cards) funnels through here so the behaviour is identical everywhere:
// pick a file -> parse -> pre-populate the recipe builder. This deliberately
// does NOT cover "Import Command" (pasting a command line) -- that is a
// separate feature.

import { parseLiteRecipeJson } from './recipeImporter';
import { parseFullPaxRecipeJson, FULL_PAX_RECIPE_KIND } from './fullPaxRecipeImporter';
import type { MiniKitchenRecipeState } from '../types';

export type RecipeFileImportOutcome =
  | { ok: true; state: MiniKitchenRecipeState; warnings: readonly unknown[]; fileName: string }
  | { ok: false; cancelled: true }
  | { ok: false; cancelled?: false; error: string };

const INVALID_FILE_MESSAGE =
  'This file doesn\u2019t appear to be a valid PAX recipe file.';

// Peek at a file's envelope marker: a full .pax recipe declares
// `kind: 'pax-cookbook-recipe'`; everything else is read as a .paxlite. Only
// reads the marker -- it never applies anything.
export function sniffIsFullPaxRecipe(text: string): boolean {
  try {
    const parsed: unknown = JSON.parse(text);
    return (
      typeof parsed === 'object' &&
      parsed !== null &&
      !Array.isArray(parsed) &&
      (parsed as Record<string, unknown>).kind === FULL_PAX_RECIPE_KIND
    );
  } catch {
    return false;
  }
}

// Parse already-read file text into a builder state. Routes by envelope marker
// to the full-.pax or .paxlite importer; both validate untrusted text and map
// only the fields they recognize (unmapped fields are dropped silently).
export function parseRecipeFileText(text: string, fileName: string): RecipeFileImportOutcome {
  if (sniffIsFullPaxRecipe(text)) {
    const result = parseFullPaxRecipeJson(text);
    if (result.ok) {
      return { ok: true, state: result.state, warnings: result.warnings, fileName };
    }
    return { ok: false, error: result.errors[0] ?? INVALID_FILE_MESSAGE };
  }
  const result = parseLiteRecipeJson(text);
  if (result.ok) {
    return { ok: true, state: result.state, warnings: result.warnings, fileName };
  }
  return { ok: false, error: result.errors[0] ?? INVALID_FILE_MESSAGE };
}

// Open a native Windows file-browse dialog and parse the chosen .pax/.paxlite
// file. MUST be called from within a user-gesture handler (a click) so the
// browser allows the picker to open. Resolves with the parsed builder state,
// a friendly error, or a cancellation. A fresh <input> is created per call so
// there is never a stale element or ref to depend on.
export function pickAndParseRecipeFile(): Promise<RecipeFileImportOutcome> {
  return new Promise<RecipeFileImportOutcome>(resolve => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.pax,.paxlite,.json';
    input.style.position = 'fixed';
    input.style.left = '-9999px';
    input.setAttribute('aria-hidden', 'true');

    let settled = false;
    const finish = (outcome: RecipeFileImportOutcome) => {
      if (settled) {
        return;
      }
      settled = true;
      if (input.parentNode) {
        input.parentNode.removeChild(input);
      }
      resolve(outcome);
    };

    input.onchange = () => {
      const file = input.files && input.files[0];
      if (!file) {
        finish({ ok: false, cancelled: true });
        return;
      }
      void file
        .text()
        .then(text => finish(parseRecipeFileText(text, file.name)))
        .catch(() => finish({ ok: false, error: 'Could not read that file. Try again.' }));
    };

    document.body.appendChild(input);
    input.click();
  });
}
