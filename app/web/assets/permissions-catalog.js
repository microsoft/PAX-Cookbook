// PAX Cookbook -- shared permissions catalog.
//
// Static, frozen catalog of every requirement Cookbook may surface to
// the chef when computing the Required Permissions list for a recipe.
// The resolver in permissions-resolver.js picks entries out of this
// catalog; the viewer in permissions-viewer.js renders them; the Help
// panel topic permissions.required references the same entries by id
// so display text and Help text never drift.
//
// Coverage. The catalog encodes every recipe-derivable permission the
// resolver can surface, including the recipe shapes whose UI controls
// may or may not be exposed in the editor. Entries are present for:
//
//   * Audit query (AuditLogsQuery.Read.All)
//   * M365 usage bundle (Exchange / OneDrive / SharePoint audit
//     activity permissions)
//   * User and license enrichment (User.Read.All, Organization.Read.All)
//     covering both IncludeUserInfo and OnlyUserInfo recipe shapes
//   * Group expansion (GroupMember.Read.All) for recipes that target
//     audit data by group membership
//   * Rollup runtime (PowerShell + Python) covering both Rollup and
//     RollupPlusRaw rollup modes
//   * Environment prerequisites (Unified Audit Logging enabled,
//     Microsoft Graph / Microsoft 365 endpoint access)
//   * Local or UNC output write access
//   * SharePoint output (Sites.ReadWrite.All, Files.ReadWrite.All,
//     destination site/library/folder write access)
//   * Append behavior (informational; no extra permission required)
//
// The catalog does not encode permission shapes that are intentionally
// excluded from Cookbook (EOM, OneLake / Fabric output).
//
// Each entry shape:
//   id            stable string id, dotted by convention.
//   name          short display label rendered as the card title.
//   type          one of:
//                   'graph-permission'        (Graph app role)
//                   'runtime-prerequisite'    (PowerShell, Python ...)
//                   'environment'             (Audit logging on,
//                                              Graph endpoint reachable)
//                   'output-access'           (filesystem write rights)
//                   'info-callout'            (informational only;
//                                              does NOT add a permission,
//                                              e.g. the append callout)
//   badgeLabel    short text for the card's type pill.
//   defaultReason fallback text for "Required because" when the
//                 resolver does not supply a contextual reason.
//
// The catalog is frozen at definition time. Any change here must be
// matched in the Help topic permissions.required so the two surfaces
// stay aligned.
(function () {
    'use strict';

    var TYPE_GRAPH   = 'graph-permission';
    var TYPE_RUNTIME = 'runtime-prerequisite';
    var TYPE_ENV     = 'environment';
    var TYPE_OUTPUT  = 'output-access';
    var TYPE_INFO    = 'info-callout';

    var BADGE_GRAPH   = 'Graph permission';
    var BADGE_RUNTIME = 'Runtime prerequisite';
    var BADGE_ENV     = 'Environment';
    var BADGE_OUTPUT  = 'Output access';
    var BADGE_INFO    = 'No additional permission';

    var CATALOG = Object.freeze({
        'audit-base': Object.freeze({
            id: 'audit-base',
            name: 'AuditLogsQuery.Read.All',
            type: TYPE_GRAPH,
            badgeLabel: BADGE_GRAPH,
            defaultReason: 'Required for the audit query that produces fact data.'
        }),
        'audit-exchange': Object.freeze({
            id: 'audit-exchange',
            name: 'AuditLogsQuery-Exchange.Read.All',
            type: TYPE_GRAPH,
            badgeLabel: BADGE_GRAPH,
            defaultReason: 'Required because the M365 usage bundle includes Exchange audit activity.'
        }),
        'audit-onedrive': Object.freeze({
            id: 'audit-onedrive',
            name: 'AuditLogsQuery-OneDrive.Read.All',
            type: TYPE_GRAPH,
            badgeLabel: BADGE_GRAPH,
            defaultReason: 'Required because the M365 usage bundle includes OneDrive audit activity.'
        }),
        'audit-sharepoint': Object.freeze({
            id: 'audit-sharepoint',
            name: 'AuditLogsQuery-SharePoint.Read.All',
            type: TYPE_GRAPH,
            badgeLabel: BADGE_GRAPH,
            defaultReason: 'Required because the M365 usage bundle includes SharePoint audit activity.'
        }),
        'user-read': Object.freeze({
            id: 'user-read',
            name: 'User.Read.All',
            type: TYPE_GRAPH,
            badgeLabel: BADGE_GRAPH,
            defaultReason: 'Required for user directory enrichment.'
        }),
        'org-read': Object.freeze({
            id: 'org-read',
            name: 'Organization.Read.All',
            type: TYPE_GRAPH,
            badgeLabel: BADGE_GRAPH,
            defaultReason: 'Required for license and organization enrichment.'
        }),
        'runtime-powershell': Object.freeze({
            id: 'runtime-powershell',
            name: 'PowerShell 7+',
            type: TYPE_RUNTIME,
            badgeLabel: BADGE_RUNTIME,
            defaultReason: 'Required for Cookbook\u2019s normal Graph API execution path.'
        }),
        'runtime-python': Object.freeze({
            id: 'runtime-python',
            name: 'Python',
            type: TYPE_RUNTIME,
            badgeLabel: BADGE_RUNTIME,
            defaultReason: 'Required because the recipe uses Rollup. PAX handles the Python requirement; Cookbook does not install Python directly.'
        }),
        'env-audit-enabled': Object.freeze({
            id: 'env-audit-enabled',
            name: 'Unified Audit Logging enabled',
            type: TYPE_ENV,
            badgeLabel: BADGE_ENV,
            defaultReason: 'Required so audit data exists for the tenant.'
        }),
        'env-graph-access': Object.freeze({
            id: 'env-graph-access',
            name: 'Microsoft Graph / Microsoft 365 endpoint access',
            type: TYPE_ENV,
            badgeLabel: BADGE_ENV,
            defaultReason: 'Required so PAX can query Graph and Microsoft 365 audit data.'
        }),
        'output-write-access': Object.freeze({
            id: 'output-write-access',
            name: 'Output path write access',
            type: TYPE_OUTPUT,
            badgeLabel: BADGE_OUTPUT,
            defaultReason: 'Required so PAX can write generated files.'
        }),
        'group-member-read': Object.freeze({
            id: 'group-member-read',
            name: 'GroupMember.Read.All',
            type: TYPE_GRAPH,
            badgeLabel: BADGE_GRAPH,
            defaultReason: 'Required for group expansion (read group members from Microsoft Graph).'
        }),
        'sharepoint-sites-readwrite': Object.freeze({
            id: 'sharepoint-sites-readwrite',
            name: 'Sites.ReadWrite.All',
            type: TYPE_GRAPH,
            badgeLabel: BADGE_GRAPH,
            defaultReason: 'Required so PAX can resolve the SharePoint site and drive for the output destination.'
        }),
        'sharepoint-files-readwrite': Object.freeze({
            id: 'sharepoint-files-readwrite',
            name: 'Files.ReadWrite.All',
            type: TYPE_GRAPH,
            badgeLabel: BADGE_GRAPH,
            defaultReason: 'Required so PAX can upload generated files to the SharePoint destination.'
        }),
        'sharepoint-destination-access': Object.freeze({
            id: 'sharepoint-destination-access',
            name: 'SharePoint destination library/folder write access',
            type: TYPE_OUTPUT,
            badgeLabel: BADGE_OUTPUT,
            defaultReason: 'Required because Graph permissions do not replace SharePoint site/library/folder permissions; the destination identity must have at least Member access on the target site.'
        }),
        'append-callout': Object.freeze({
            id: 'append-callout',
            name: 'Append behavior',
            type: TYPE_INFO,
            badgeLabel: BADGE_INFO,
            defaultReason: 'Append does not require additional permissions. It uses the same permissions required by the selected data, enrichment, and output options.'
        })
    });

    // Stable group order used by the viewer when grouping by type.
    var TYPE_ORDER = Object.freeze([
        TYPE_GRAPH,
        TYPE_RUNTIME,
        TYPE_ENV,
        TYPE_OUTPUT,
        TYPE_INFO
    ]);

    var TYPE_GROUP_LABELS = Object.freeze({
        'graph-permission':     'Graph permissions',
        'runtime-prerequisite': 'Runtime prerequisites',
        'environment':          'Environment',
        'output-access':        'Output access',
        'info-callout':         'Informational'
    });

    window.PaxPermissions = window.PaxPermissions || {};
    window.PaxPermissions.catalog          = CATALOG;
    window.PaxPermissions.typeOrder        = TYPE_ORDER;
    window.PaxPermissions.typeGroupLabels  = TYPE_GROUP_LABELS;
    window.PaxPermissions.TYPE_GRAPH       = TYPE_GRAPH;
    window.PaxPermissions.TYPE_RUNTIME     = TYPE_RUNTIME;
    window.PaxPermissions.TYPE_ENV         = TYPE_ENV;
    window.PaxPermissions.TYPE_OUTPUT      = TYPE_OUTPUT;
    window.PaxPermissions.TYPE_INFO        = TYPE_INFO;
})();
