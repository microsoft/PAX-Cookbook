/**
 * Recipe normalizer.
 *
 * `normalizeRecipe()` enforces shape-level rules over the editable recipe
 * state: clears fields that are suppressed by the current query mode,
 * forces fields that are implied by the current query mode, and drops
 * dependent fields when their parent flag is off.
 *
 * Phase 2 covers shape correctness only. Command rendering, permissions,
 * and warning emission live in their own modules.
 *
 * Mutex/clearing rules implemented here (validated against PAX v1.11.3):
 *   - `query.mode === 'user-info-only'`:
 *       * `query.includeUserInfo` forced to `true`.
 *       * `query.onlyUserInfo` forced to `true`.
 *       * Audit-only `query` fields cleared (startDate, endDate,
 *         includeM365Usage, excludeCopilotInteraction). PAX v1.11.3
 *         `-OnlyUserInfo` rejects every audit-side switch in its
 *         mutex block (lines 1792–1879).
 *       * Audit-only `processing` fields cleared (activityTypes, userIds,
 *         groupNames, agentFilter, promptFilter, rollup).
 *   - `query.onlyUserInfo === true`:
 *       * `query.includeUserInfo` forced to `true`.
 *       * `query.includeM365Usage` cleared.
 *       * `query.excludeCopilotInteraction` cleared.
 *
 * Intentionally NOT a rule (validated against PAX v1.11.3):
 *   - `query.excludeCopilotInteraction` is **standalone**. PAX accepts
 *     `-ExcludeCopilotInteraction` independently of `-IncludeM365Usage`.
 *     The normalizer must not silently clear it just because
 *     `includeM365Usage` is falsy — the renderer will emit the switch
 *     whenever the recipe state requests it (except inside OnlyUserInfo
 *     mode, where it is mutex-cleared above). Any UI-level preset
 *     behavior that hides the option belongs in the UI layer, not here.
 *
 * Mutex pairs enforced by single-field enums in the type definitions
 * (`RollupMode`, `OutputMode`, `UserInfoOutputMode`) are not re-checked here.
 */

import type { MiniKitchenRecipeState } from '../types';

export function normalizeRecipe(state: MiniKitchenRecipeState): MiniKitchenRecipeState {
  const next: MiniKitchenRecipeState = {
    identity: { ...state.identity },
    ingredients: { ...state.ingredients },
    query: { ...state.query },
    processing: { ...state.processing },
    destinations: {
      fact: { ...state.destinations.fact },
      userInfo: { ...state.destinations.userInfo },
    },
    auth: { ...state.auth },
    executionMode: state.executionMode,
    advanced: { ...state.advanced },
  };

  if (next.query.mode === 'user-info-only') {
    next.query.includeUserInfo = true;
    next.query.onlyUserInfo = true;
    delete next.query.dateMode;
    delete next.query.startDate;
    delete next.query.endDate;
    delete next.query.includeM365Usage;
    delete next.query.excludeCopilotInteraction;
    delete next.processing.activityTypes;
    delete next.processing.userIds;
    delete next.processing.groupNames;
    delete next.processing.agentFilter;
    delete next.processing.promptFilter;
    delete next.processing.rollup;
    delete next.processing.dashboard;
    delete next.processing.outputCombineMode;
  } else {
    if (next.query.onlyUserInfo === true) {
      next.query.includeUserInfo = true;
      delete next.query.includeM365Usage;
      delete next.query.excludeCopilotInteraction;
    }
    // Previous-day mode clears any stale custom dates so they never resurface
    // if the user later switches back to Custom range.
    if (next.query.dateMode === 'previous-day') {
      delete next.query.startDate;
      delete next.query.endDate;
    }
    // ExcludeCopilotInteraction is standalone in PAX v1.11.3 — do not clear
    // it when IncludeM365Usage is falsy.
  }

  return next;
}
