// PAX Cookbook -- shared permissions resolver.
//
// Pure function: takes a recipe-shaped object (or a flat options
// object) and returns the cumulative, deduplicated list of
// requirements the recipe needs, in catalog terms. The Required
// Permissions viewer in the recipe editor calls this on every form
// input change; the Help panel references the same catalog ids via
// permissions-catalog.js.
//
// Doctrine:
//
//   * No DOM access. No network. No window mutation beyond exposing
//     the PaxPermissions.resolve entry point. Output depends ONLY on
//     input.
//
//   * The resolver computes permissions for whatever recipe shape it
//     receives, even if a given option is not currently exposed by a
//     dedicated UI control. Saved or custom recipes can carry shapes
//     the editor does not yet author directly, and the rail must
//     reflect them when present.
//
//   * Shapes the resolver covers:
//       - includeM365Usage              (M365 usage bundle)
//       - includeUserInfo               (user/license enrichment)
//       - onlyUserInfo                  (user-info-only mode, no audit)
//       - groupNames (array, non-empty) (group expansion)
//       - rollup in {Rollup, RollupPlusRaw} (rollup post-processor)
//       - destinations.fact.path        (local/UNC vs SharePoint URL)
//       - destinations.fact.appendBehavior (informational append)
//
//   * Returns a flat, insertion-ordered array. The viewer groups by
//     type when rendering; the resolver does not.
//
// Output entry shape:
//   {
//     id:               catalog id
//     name:             permission/prerequisite display name
//     type:             catalog type ('graph-permission',
//                       'runtime-prerequisite', 'environment',
//                       'output-access', 'info-callout')
//     badgeLabel:       short pill text
//     requiredBecause:  contextual sentence, supplied by the resolver
//     triggeredBy:      array of human-readable trigger reasons that
//                       caused this entry to be present (dedup'd)
//   }
(function () {
    'use strict';

    if (!window.PaxPermissions || !window.PaxPermissions.catalog) {
        // Catalog must be loaded first. permissions-catalog.js is wired
        // ahead of this file in index.html; if it is missing the page
        // is broken and there is nothing useful to do here.
        return;
    }

    var CATALOG = window.PaxPermissions.catalog;

    // -----------------------------------------------------------------
    // SharePoint URL detection. Matches host names whose effective TLD
    // is sharepoint.<region>, covering the commercial cloud and the
    // government / sovereign clouds. The check is case-insensitive and
    // requires the path component to start with '/' so plain hostname
    // values without a path are still recognized as SharePoint URLs.
    // -----------------------------------------------------------------
    var SHAREPOINT_HOST_PATTERN =
        /^https?:\/\/[a-z0-9-]+(?:\.[a-z0-9-]+)*\.sharepoint(?:\.com|\.us|\.de|\.cn)(?:[\/?#]|$)/i;

    function isSharePointOutputPath(path) {
        if (typeof path !== 'string' || path.length === 0) { return false; }
        return SHAREPOINT_HOST_PATTERN.test(path);
    }

    // -----------------------------------------------------------------
    // Recipe-shape normalization.
    //
    // Accepts:
    //   - a full schema-shaped recipe object (from extractPayloadFromDom,
    //     /api/v1/recipes/<id> response, or a template), OR
    //   - a flat options object with the same leaf names
    //     ({ m365Usage, includeUserInfo, onlyUserInfo, groupNames,
    //        rollup, outputPath, appendBehavior }).
    //
    // The 'rollup' value is normalized to a string:
    //   '' / 'none'      -- no rollup
    //   'Rollup'         -- standard rollup mode
    //   'RollupPlusRaw'  -- rollup that also preserves raw output
    //
    // Returns a flat options struct the rule engine consumes.
    // -----------------------------------------------------------------
    function normalizeRecipe(recipe) {
        var out = {
            m365Usage:       false,
            includeUserInfo: false,
            onlyUserInfo:    false,
            groupExpansion:  false,
            rollupMode:      '',
            outputPath:      '',
            appendBehavior:  ''
        };
        if (!recipe || typeof recipe !== 'object') { return out; }

        // Flat-options short-circuit (used by unit tests / smoke
        // harnesses). Accepts both boolean 'rollup' (legacy) and the
        // newer string 'rollup' that names the rollup mode directly.
        var isFlatOptions = (
            typeof recipe.m365Usage === 'boolean' ||
            typeof recipe.includeUserInfo === 'boolean' ||
            typeof recipe.onlyUserInfo === 'boolean' ||
            typeof recipe.rollup === 'boolean' ||
            typeof recipe.rollup === 'string' ||
            Array.isArray(recipe.groupNames)
        );
        if (isFlatOptions) {
            out.m365Usage       = !!recipe.m365Usage;
            out.includeUserInfo = !!recipe.includeUserInfo;
            out.onlyUserInfo    = !!recipe.onlyUserInfo;
            if (Array.isArray(recipe.groupNames)) {
                out.groupExpansion = recipe.groupNames.length > 0;
            } else if (typeof recipe.groupExpansion === 'boolean') {
                out.groupExpansion = recipe.groupExpansion;
            }
            if (typeof recipe.rollup === 'string') {
                if (recipe.rollup === 'Rollup' || recipe.rollup === 'RollupPlusRaw') {
                    out.rollupMode = recipe.rollup;
                }
            } else if (recipe.rollup === true) {
                out.rollupMode = 'Rollup';
            }
            out.outputPath      = String(recipe.outputPath || '');
            out.appendBehavior  = String(recipe.appendBehavior || '');
            return out;
        }

        // Full recipe shape.
        if (recipe.ingredients) {
            if (recipe.ingredients.m365Usage) {
                out.m365Usage = !!recipe.ingredients.m365Usage.includeM365Usage;
            }
            if (recipe.ingredients.entraUserData) {
                out.includeUserInfo = !!recipe.ingredients.entraUserData.includeUserInfo;
                if (typeof recipe.ingredients.entraUserData.onlyUserInfo === 'boolean') {
                    out.onlyUserInfo = recipe.ingredients.entraUserData.onlyUserInfo;
                }
            }
            // Legacy alias: pre-S26 drafts placed groupNames under
            // ingredients. The authoritative S26 location is
            // recipe.query.groupNames; the schema enforces the
            // schema-validated location. Both are honored here so
            // older drafts continue to surface the group-expansion
            // permission card after the SPA updates to S26.
            if (Array.isArray(recipe.ingredients.groupNames) &&
                recipe.ingredients.groupNames.length > 0) {
                out.groupExpansion = true;
            }
        }
        // V1.S26 onlyUserInfo signal: query.mode='userInfoOnly' is
        // the new authoritative discriminator for Shape 3 (the legacy
        // entraUserData.onlyUserInfo flag is no longer carried under
        // the schema). When set, the rail must suppress all
        // audit-only permission cards.
        if (recipe.query) {
            if (recipe.query.mode === 'userInfoOnly') {
                out.onlyUserInfo = true;
            }
            if (Array.isArray(recipe.query.groupNames) &&
                recipe.query.groupNames.length > 0) {
                out.groupExpansion = true;
            }
        }
        if (recipe.processing && typeof recipe.processing.rollup === 'string') {
            if (recipe.processing.rollup === 'Rollup' ||
                recipe.processing.rollup === 'RollupPlusRaw') {
                out.rollupMode = recipe.processing.rollup;
            }
        }
        if (recipe.destinations && recipe.destinations.fact) {
            out.outputPath     = String(recipe.destinations.fact.path || '');
            // V1.S26: destinations.fact.mode is the authoritative
            // append signal. Recipes carrying only the legacy
            // appendBehavior alias ('fresh'|'append') still flow
            // through. mode='append' is normalized to 'append'
            // (matches the legacy alias) so downstream rules need
            // only one branch.
            var factMode = recipe.destinations.fact.mode;
            if (factMode === 'append') {
                out.appendBehavior = 'append';
            } else if (factMode === 'outputPath') {
                out.appendBehavior = '';
            } else {
                out.appendBehavior = String(recipe.destinations.fact.appendBehavior || '');
            }
        }
        return out;
    }

    // -----------------------------------------------------------------
    // Rule engine.
    //
    // Each rule encodes a single semantic relationship between recipe
    // shape and required permissions. Rules are additive: a single
    // catalog entry can be added by multiple rules, which the
    // deduplication accumulator collapses into one card with merged
    // "Triggered by" reasons.
    // -----------------------------------------------------------------
    function resolveRequiredPermissions(recipe) {
        var r = normalizeRecipe(recipe);
        var isRollup       = (r.rollupMode === 'Rollup' || r.rollupMode === 'RollupPlusRaw');
        var sharePointOut  = isSharePointOutputPath(r.outputPath);
        var rollupLabel    = isRollup ? r.rollupMode : '';

        // Dedup accumulator (ordered Map). Stores cards by catalog id.
        // First reason set wins for "Required because"; subsequent
        // triggers append to triggeredBy.
        var byId = new Map();

        function add(id, requiredBecause, trigger) {
            var def = CATALOG[id];
            if (!def) { return; }
            if (byId.has(id)) {
                var existing = byId.get(id);
                if (existing.triggeredBy.indexOf(trigger) < 0) {
                    existing.triggeredBy.push(trigger);
                }
                return;
            }
            byId.set(id, {
                id:              def.id,
                name:            def.name,
                type:            def.type,
                badgeLabel:      def.badgeLabel,
                requiredBecause: requiredBecause || def.defaultReason,
                triggeredBy:     [trigger]
            });
        }

        // Audit query. Required for every recipe that runs the normal
        // audit query, suppressed for user-info-only recipes that skip
        // the audit query entirely.
        if (!r.onlyUserInfo) {
            add(
                'audit-base',
                'Required for the audit query that produces fact data.',
                'Audit query'
            );
            add(
                'env-audit-enabled',
                'Required so audit data exists for the tenant.',
                'Audit query'
            );
        }

        // M365 usage bundle. Adds the three granular audit permissions
        // alongside AuditLogsQuery.Read.All. Suppressed if the recipe is
        // user-info-only (no audit query runs at all).
        if (r.m365Usage && !r.onlyUserInfo) {
            add(
                'audit-exchange',
                'Required because the M365 usage bundle includes Exchange audit activity.',
                'M365 usage bundle selected'
            );
            add(
                'audit-onedrive',
                'Required because the M365 usage bundle includes OneDrive audit activity.',
                'M365 usage bundle selected'
            );
            add(
                'audit-sharepoint',
                'Required because the M365 usage bundle includes SharePoint audit activity.',
                'M365 usage bundle selected'
            );
        }

        // User and license enrichment. IncludeUserInfo and OnlyUserInfo
        // both add User.Read.All + Organization.Read.All.
        if (r.includeUserInfo) {
            add(
                'user-read',
                'Required because user directory enrichment is enabled.',
                'Include user info selected'
            );
            add(
                'org-read',
                'Required because user/license enrichment is enabled.',
                'Include user info selected'
            );
        }
        if (r.onlyUserInfo) {
            add(
                'user-read',
                'Required because user-info-only mode enriches user directory data.',
                'User-info-only mode'
            );
            add(
                'org-read',
                'Required because user-info-only mode enriches license / organization data.',
                'User-info-only mode'
            );
        }

        // CopilotInteraction-only rollup (Rollup or RollupPlusRaw) on a
        // non-M365-bundle, non-user-info-only run also requires
        // User.Read.All + Organization.Read.All, because the rollup
        // post-processor needs user/license context to label rolled-up
        // rows. M365 usage bundle rollup runs do NOT trigger this rule.
        if (isRollup && !r.m365Usage && !r.onlyUserInfo) {
            add(
                'user-read',
                'Required because CopilotInteraction-only rollup needs user info to label rolled-up rows.',
                'CopilotInteraction-only ' + rollupLabel
            );
            add(
                'org-read',
                'Required because CopilotInteraction-only rollup needs user/license enrichment.',
                'CopilotInteraction-only ' + rollupLabel
            );
        }

        // Group expansion. Adds GroupMember.Read.All on top of the
        // directory enrichment pair, so the group filter can read each
        // group's members from Microsoft Graph.
        if (r.groupExpansion) {
            add(
                'user-read',
                'Required because group expansion enriches user directory data for the resolved members.',
                'Group expansion'
            );
            add(
                'org-read',
                'Required because group expansion enriches license / organization data for the resolved members.',
                'Group expansion'
            );
            add(
                'group-member-read',
                'Required so PAX can resolve the members of each named group.',
                'Group expansion'
            );
        }

        // Rollup runtime. Both Rollup and RollupPlusRaw require Python
        // for the rollup post-processor; PAX manages the runtime and
        // Cookbook does not install Python directly.
        if (isRollup) {
            add(
                'runtime-python',
                'Required because the recipe uses ' + rollupLabel + '. PAX handles the Python requirement; Cookbook does not install Python directly.',
                rollupLabel + ' selected'
            );
        }

        // Core runtime. PowerShell 7+ and Microsoft Graph / Microsoft
        // 365 endpoint access are required for every Cookbook recipe,
        // including user-info-only runs (the Graph calls still
        // happen).
        add(
            'runtime-powershell',
            'Required for Cookbook\u2019s normal Graph API execution path.',
            'Every recipe'
        );
        add(
            'env-graph-access',
            'Required so PAX can query Graph and Microsoft 365 audit data.',
            'Every recipe'
        );

        // Output destination.
        //   * SharePoint URL: adds Sites.ReadWrite.All +
        //     Files.ReadWrite.All Graph permissions AND a destination
        //     site/library/folder write-access reminder.
        //   * Local or UNC path: adds the local output-write-access
        //     entry only.
        if (sharePointOut) {
            add(
                'sharepoint-sites-readwrite',
                'Required so PAX can resolve the SharePoint site and drive for the output destination.',
                'SharePoint output URL'
            );
            add(
                'sharepoint-files-readwrite',
                'Required so PAX can upload generated files to the SharePoint destination.',
                'SharePoint output URL'
            );
            add(
                'sharepoint-destination-access',
                'Required because Graph permissions do not replace SharePoint site / library / folder permissions; the destination identity must have at least Member access on the target site.',
                'SharePoint output URL'
            );
        } else {
            add(
                'output-write-access',
                'Required so PAX can write generated files to the output path.',
                'Local or UNC output path'
            );
        }

        // Append behavior. Permission-neutral: when append is selected,
        // render an informational callout that reuses the permissions
        // already added above.
        if (r.appendBehavior === 'append') {
            add(
                'append-callout',
                'Append does not require additional permissions. It uses the same permissions required by the selected data, enrichment, and output options.',
                'Append behavior selected'
            );
        }

        // Preserve insertion order. The viewer groups by type when
        // rendering, so the order here is mostly cosmetic for the
        // ungrouped case (e.g. unit tests).
        var arr = [];
        byId.forEach(function (v) { arr.push(v); });
        return arr;
    }

    window.PaxPermissions = window.PaxPermissions || {};
    window.PaxPermissions.resolve           = resolveRequiredPermissions;
    window.PaxPermissions.normalizeRecipe   = normalizeRecipe;
    window.PaxPermissions.isSharePointPath  = isSharePointOutputPath;
})();
