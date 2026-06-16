/**
 * Pure builder-state transforms for the Mini-Kitchen builder.
 *
 * These are the extracted cores of two `MiniKitchenBuilderPreview` reducers:
 *
 *  - `applyMatchPreset` is the pure body of `setStateMatchPreset` (apply the
 *    updater, then re-detect the Step-1 preset highlight). The component now
 *    calls `setState(prev => applyMatchPreset(prev, updater))`, so its behavior
 *    is unchanged; extracting it lets the headless interaction simulation drive
 *    the exact same transform the UI fires.
 *
 *  - `applyPresetSelection` is the pure body of `handlePresetChange`, with the
 *    auth-preservation fix described below.
 *
 * Nothing here fetches, persists, reads a secret, or renders a command.
 */

import type { MiniKitchenRecipeState, PresetId } from '../types';
import { createRecipeFromPreset, detectMatchingPreset } from './defaultRecipe';

/**
 * Apply an updater to the recipe state, then re-detect the Step-1 preset
 * highlight from the result. Identical to the inline reducer the component used
 * inside `setStateMatchPreset`.
 */
export function applyMatchPreset(
  prev: MiniKitchenRecipeState,
  updater: (prev: MiniKitchenRecipeState) => MiniKitchenRecipeState,
): MiniKitchenRecipeState {
  const next = updater(prev);
  const matched = detectMatchingPreset(next);
  if (matched === next.ingredients.preset) {
    return next;
  }
  return {
    ...next,
    ingredients: { ...next.ingredients, preset: matched },
  };
}

/**
 * Switch the Step-1 starting point.
 *
 * Selecting a preset card replaces the data-scope fields (query, processing,
 * destinations, name) with that preset's defaults. The authentication choice --
 * sign-in mode, bound Chef's Key, and tenant id -- is independent of the data
 * scope (preset auto-detection ignores auth entirely), so it is carried across
 * the switch.
 *
 * Without preserving `auth`, configuring an App-registration sign-in and then
 * (re)selecting a preset card -- which the picker fires even when re-clicking
 * the already-selected card -- silently reverted auth to Web login with no
 * Chef's Key and no tenant. That is the recipe shape observed on disk
 * (`auth` keys = `[mode]` only), and the reason a recipe the operator built as
 * an App-registration recipe ran an interactive Web-login sign-in.
 */
export function applyPresetSelection(
  prev: MiniKitchenRecipeState,
  presetId: PresetId,
): MiniKitchenRecipeState {
  const fromPreset = createRecipeFromPreset(presetId);
  return { ...fromPreset, auth: prev.auth };
}
