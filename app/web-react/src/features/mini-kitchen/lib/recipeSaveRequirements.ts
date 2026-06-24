// Save-blocking recipe-content requirements (Bucket A).
//
// A recipe is *saveable* when the content the operator authored is complete
// enough for the broker to accept it. That is a different question from whether
// this PC is *ready to run* the recipe later (Bucket B: approved PAX engine,
// sign-in / Chef's Key, destination reachability, locks, the app-managed PAX
// script path, and so on). Bucket B never blocks Save.
//
// This helper derives a small, named list of the content gaps that the broker
// would reject on a create / update, so the UI can name them ("This recipe
// still needs: Recipe name, Start date, ...") instead of showing a vague count.
// It intentionally mirrors only the recipe-content gates, not the whole schema
// engine, and it intentionally excludes:
//   * the PAX script path (app-managed runtime detail, never recipe content),
//   * the interactive / managed-identity tenant id (resolved from sign-in at
//     run time, a readiness concern), and
//   * Chef's Key / auth-profile binding (readiness).

import type { MiniKitchenRecipeState } from '../types';

export interface SaveRequirement {
  /** Stable id for keys and tests. */
  id: string;
  /** Short human label shown in the named checklist. */
  label: string;
}

const GUID_PATTERN = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

/**
 * Compute the Bucket-A (content) gaps that would block a Save. An empty array
 * means the recipe content is complete enough to save; readiness is evaluated
 * separately and does not appear here.
 */
export function deriveSaveRequirements(state: MiniKitchenRecipeState): SaveRequirement[] {
  const reqs: SaveRequirement[] = [];

  const name = (state.identity.name ?? '').trim();
  if (!name) {
    reqs.push({ id: 'name', label: 'Recipe name' });
  }

  const isUserInfoOnly = state.query.mode === 'user-info-only';

  if (!isUserInfoOnly) {
    // Previous-day mode deliberately omits both dates (PAX queries the previous
    // full UTC day), so the start/end-date requirements do not apply — only a
    // half-filled custom range is incomplete.
    const isPreviousDay = state.query.dateMode === 'previous-day';
    if (!isPreviousDay) {
      const startDate = (state.query.startDate ?? '').trim();
      const endDate = (state.query.endDate ?? '').trim();
      if (!startDate && !endDate) {
        // Both sides blank: a single combined "Date range" gap, not two separate
        // Start date / End date items (the operator fills both, or switches to
        // Previous day). A half-filled range still names the specific missing side.
        // The label carries a short Previous-day hint so the "still needs" list
        // and the Review issues both surface the alternative.
        reqs.push({ id: 'dateRange', label: 'Date range (or switch to Previous day)' });
      } else if (!startDate) {
        reqs.push({ id: 'startDate', label: 'Start date (or use Previous day)' });
      } else if (!endDate) {
        reqs.push({ id: 'endDate', label: 'End date (or use Previous day)' });
      }
    }

    // Rollup is intentionally NOT a save requirement. Many operators run PAX
    // only to pull raw Purview audit data, so an audit recipe may legitimately
    // omit a rollup mode (processing.rollup = 'none' / absent). When rollup is
    // selected the runtime command carries -Rollup / -RollupPlusRaw; when it is
    // not, the command simply omits it (no default is invented). Rollup remains
    // forbidden for user-info-only runs, which is handled in the branch below.

    const fact = state.destinations.fact;
    const factPath = (fact.path ?? '').trim();
    if (!factPath) {
      reqs.push({ id: 'factOutput', label: 'Output folder' });
    }

    const agentFilter = state.processing.agentFilter;
    if (agentFilter && agentFilter.mode === 'ids-only') {
      const ids = (agentFilter.ids ?? []).filter(v => v.trim().length > 0);
      if (ids.length === 0) {
        reqs.push({ id: 'agentIds', label: 'Agent IDs' });
      }
    }

    // The custom filler label needs its text. This fires only when the filler
    // would actually be emitted (a rollup without the M365 dashboard) and the
    // operator picked the custom 'Fixed' mode but left the text blank. Other
    // filler modes — and a recipe with no filler at all — never appear here,
    // so a name-only draft stays saveable.
    const rollupOn =
      state.processing.rollup === 'rollup' || state.processing.rollup === 'rollup-plus-raw';
    if (
      rollupOn &&
      state.query.includeM365Usage !== true &&
      state.processing.fillerLabel === 'Fixed' &&
      (state.processing.fillerLabelText ?? '').trim().length === 0
    ) {
      reqs.push({ id: 'fillerLabelText', label: 'Custom filler label text' });
    }
  } else {
    const ui = state.destinations.userInfo;
    const uiPath = (ui.path ?? '').trim();
    // User-info-only has no audit output to co-locate beside, so a real
    // write-new / append destination path is required content.
    if (ui.mode === 'default-colocate' || !uiPath) {
      reqs.push({ id: 'userInfoOutput', label: 'User info output folder' });
    }
  }

  // The app-registration modes sign in non-interactively, so the tenant id is
  // content the operator types (and the Tenant ID field is shown for them).
  // The interactive modes (WebLogin, DeviceCode) and ManagedIdentity resolve
  // the tenant at run time, so they never appear here.
  const mode = state.auth.mode;
  const isAppReg = mode === 'AppRegistrationSecret' || mode === 'AppRegistrationCertificate';
  if (isAppReg) {
    const tenantId = (state.auth.tenantId ?? '').trim();
    if (!tenantId || !GUID_PATTERN.test(tenantId)) {
      reqs.push({ id: 'tenantId', label: 'Tenant ID' });
    }
  }

  return reqs;
}

/**
 * Render a single sentence naming the outstanding save requirements, e.g.
 * "This recipe still needs: Recipe name, Start date, End date." Returns the
 * empty string when nothing is outstanding.
 */
export function describeSaveRequirements(reqs: readonly SaveRequirement[]): string {
  if (reqs.length === 0) {
    return '';
  }
  return `This recipe still needs: ${reqs.map(r => r.label).join(', ')}.`;
}
