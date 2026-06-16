/**
 * Static permission rule catalog.
 *
 * Mini-Kitchen does not evaluate these rules in Phase 2; the resolver
 * (`lib/permissionsResolver.ts`, Phase 4) walks the normalized recipe state
 * and decides which rules fire. This file is the authoritative inventory of
 * permission entries Mini-Kitchen knows about, the structured `group` and
 * `appliesTo` categories used by the future UI, and the static copy that
 * accompanies each rule.
 *
 * `triggeredBy` lists recipe field paths whose values influence the rule.
 * It is documentation only; the resolver encodes the actual predicates per
 * rule id.
 */

import type { PermissionRule } from '../types';

export const PERMISSION_RULES: readonly PermissionRule[] = [
  // ---- Graph perms required for the audit query ---------------------------
  {
    id: 'auditLogsQueryReadAll',
    name: 'AuditLogsQuery.Read.All',
    group: 'graph',
    appliesTo: 'tenant-admin-consent',
    appliesToLabel: 'Audit log query (Graph)',
    requiredBecause:
      'PAX uses the Microsoft Graph audit log query API to pull Copilot interaction and M365 usage events.',
    triggeredBy: ['query.mode'],
    severity: 'required',
  },
  {
    id: 'unifiedAuditLogging',
    name: 'Unified audit logging enabled',
    group: 'environment',
    appliesTo: 'environment-setting',
    appliesToLabel: 'Tenant audit configuration',
    requiredBecause:
      'The audit log query API only returns data when unified audit logging is enabled at the tenant level.',
    triggeredBy: ['query.mode'],
    severity: 'required',
  },

  // ---- M365 usage bundle audit scopes -------------------------------------
  {
    id: 'exchangeAuditBranch',
    name: 'AuditLogsQuery-Exchange.Read.All',
    group: 'graph',
    appliesTo: 'tenant-admin-consent',
    appliesToLabel: 'M365 usage — Exchange audit',
    requiredBecause:
      'When -IncludeM365Usage is on, PAX queries the Exchange audit branch through Graph and requires this scope alongside AuditLogsQuery.Read.All.',
    triggeredBy: ['query.includeM365Usage'],
    severity: 'required',
  },
  {
    id: 'oneDriveAuditBranch',
    name: 'AuditLogsQuery-OneDrive.Read.All',
    group: 'graph',
    appliesTo: 'tenant-admin-consent',
    appliesToLabel: 'M365 usage — OneDrive audit',
    requiredBecause:
      'When -IncludeM365Usage is on, PAX queries the OneDrive audit branch through Graph and requires this scope alongside AuditLogsQuery.Read.All.',
    triggeredBy: ['query.includeM365Usage'],
    severity: 'required',
  },
  {
    id: 'sharePointAuditBranch',
    name: 'AuditLogsQuery-SharePoint.Read.All',
    group: 'graph',
    appliesTo: 'tenant-admin-consent',
    appliesToLabel: 'M365 usage — SharePoint audit',
    requiredBecause:
      'When -IncludeM365Usage is on, PAX queries the SharePoint audit branch through Graph and requires this scope alongside AuditLogsQuery.Read.All.',
    triggeredBy: ['query.includeM365Usage'],
    severity: 'required',
  },

  // ---- User info perms ----------------------------------------------------
  {
    id: 'userReadAll',
    name: 'User.Read.All',
    group: 'graph',
    appliesTo: 'tenant-admin-consent',
    appliesToLabel: 'Entra user info lookup',
    requiredBecause:
      'PAX calls Graph /users to resolve Entra user info when including user info, when only-user-info mode is on, when rollup runs, or when group expansion is requested.',
    triggeredBy: [
      'query.includeUserInfo',
      'query.onlyUserInfo',
      'processing.rollup',
      'processing.groupNames',
    ],
    severity: 'required',
  },
  {
    id: 'organizationReadAll',
    name: 'Organization.Read.All',
    group: 'graph',
    appliesTo: 'tenant-admin-consent',
    appliesToLabel: 'Entra organization metadata',
    requiredBecause:
      'PAX resolves tenant-level organization metadata alongside user info for the same scenarios that trigger User.Read.All.',
    triggeredBy: [
      'query.includeUserInfo',
      'query.onlyUserInfo',
      'processing.rollup',
      'processing.groupNames',
    ],
    severity: 'required',
  },

  // ---- Group expansion ----------------------------------------------------
  {
    id: 'groupMemberReadAll',
    name: 'GroupMember.Read.All',
    group: 'graph',
    appliesTo: 'tenant-admin-consent',
    appliesToLabel: 'Entra group membership expansion',
    requiredBecause:
      'When -GroupNames is set, PAX calls Graph /groups/{id}/members to expand each group into its user list.',
    triggeredBy: ['processing.groupNames'],
    severity: 'required',
  },

  // ---- Runtime / environment ---------------------------------------------
  {
    id: 'pythonRuntimeRollup',
    name: 'Python helper for rollup (PAX bootstraps)',
    group: 'runtime',
    appliesTo: 'runtime-prerequisite',
    appliesToLabel: 'Rollup aggregation pipeline',
    requiredBecause:
      'PAX runs the rollup aggregation step through a Python helper. PAX owns the bootstrap — it auto-installs Python 3.10+ and the optional orjson package on first use — so the runtime account only needs network reach to perform that bootstrap.',
    triggeredBy: ['processing.rollup'],
    severity: 'required',
  },
  {
    id: 'powerShell7',
    name: 'PowerShell 7+ runtime',
    group: 'runtime',
    appliesTo: 'runtime-prerequisite',
    appliesToLabel: 'Command execution',
    requiredBecause:
      'PAX commands run on PowerShell 7+. The runtime account must be able to launch pwsh.',
    triggeredBy: ['always'],
    severity: 'required',
  },
  {
    id: 'graphM365EndpointAccess',
    name: 'Graph + M365 endpoint reachability',
    group: 'environment',
    appliesTo: 'environment-setting',
    appliesToLabel: 'Network reachability',
    requiredBecause:
      'The runtime must be able to reach graph.microsoft.com and the M365 audit endpoints.',
    triggeredBy: ['always'],
    severity: 'required',
  },

  // ---- Output perms -------------------------------------------------------
  {
    id: 'sitesReadWriteAll',
    name: 'Sites.ReadWrite.All',
    group: 'output',
    appliesTo: 'tenant-admin-consent',
    appliesToLabel: 'SharePoint destination',
    requiredBecause:
      'PAX writes the audit or user info output to SharePoint through Graph when the SharePoint storage tier is selected.',
    triggeredBy: [
      'destinations.fact.tier',
      'destinations.userInfo.tier',
      'destinations.fact.path',
      'destinations.userInfo.path',
    ],
    severity: 'required',
  },
  {
    id: 'filesReadWriteAll',
    name: 'Files.ReadWrite.All',
    group: 'output',
    appliesTo: 'tenant-admin-consent',
    appliesToLabel: 'SharePoint destination',
    requiredBecause:
      'PAX uploads the audit or user info output through the Graph files API when the SharePoint storage tier is selected.',
    triggeredBy: [
      'destinations.fact.tier',
      'destinations.userInfo.tier',
      'destinations.fact.path',
      'destinations.userInfo.path',
    ],
    severity: 'required',
  },
  {
    id: 'destinationSiteLibraryAccess',
    name: 'Destination site / library access',
    group: 'output',
    appliesTo: 'output-location',
    appliesToLabel: 'SharePoint destination',
    requiredBecause:
      'The runtime identity must have explicit write access on the destination SharePoint document library; Graph delegated perms alone are not enough.',
    triggeredBy: [
      'destinations.fact.tier',
      'destinations.userInfo.tier',
      'destinations.fact.path',
      'destinations.userInfo.path',
    ],
    severity: 'required',
  },
  {
    id: 'fabricWorkspaceContributor',
    name: 'Fabric workspace Contributor',
    group: 'output',
    appliesTo: 'output-location',
    appliesToLabel: 'Fabric Lakehouse destination',
    requiredBecause:
      'PAX writes the audit or user info output to a Fabric Lakehouse through the OneLake DFS endpoint. The runtime identity must be a Contributor (or higher) on the Fabric workspace that owns the destination lakehouse.',
    triggeredBy: [
      'destinations.fact.tier',
      'destinations.userInfo.tier',
      'destinations.fact.path',
      'destinations.userInfo.path',
    ],
    severity: 'required',
  },
  {
    id: 'fabricServicePrincipalsTenantSetting',
    name: 'Fabric tenant setting: service principals can use Fabric APIs',
    group: 'environment',
    appliesTo: 'environment-setting',
    appliesToLabel: 'Fabric Lakehouse destination (app-registration auth)',
    requiredBecause:
      'When the runtime authenticates as a service principal (-Auth AppRegistration), the Fabric admin tenant setting "Service principals can use Fabric APIs" must include the app or a security group containing it.',
    triggeredBy: ['destinations.fact.tier', 'destinations.userInfo.tier', 'auth.mode'],
    severity: 'required',
  },
  {
    id: 'oneLakeUrlContract',
    name: 'OneLake URL contract',
    group: 'output',
    appliesTo: 'output-location',
    appliesToLabel: 'Fabric Lakehouse destination',
    requiredBecause:
      'PAX only treats URLs on the OneLake DFS endpoint as Fabric Lakehouse destinations. The URL must point at <workspace>/<item>, where the item is addressed by name (<item>.Lakehouse) or by GUID (<itemId>, no suffix) — optionally under /Tables[/<schema>][/<table>] or /Files[/<sub>].',
    triggeredBy: ['destinations.fact.path', 'destinations.userInfo.path'],
    severity: 'required',
  },
  {
    id: 'outputWriteAccess',
    name: 'Output file write access',
    group: 'output',
    appliesTo: 'output-location',
    appliesToLabel: 'Local / UNC destination',
    requiredBecause:
      'The runtime account needs write access to the configured output directory or UNC share.',
    triggeredBy: [
      'destinations.fact.tier',
      'destinations.userInfo.tier',
      'destinations.fact.path',
      'destinations.userInfo.path',
    ],
    severity: 'required',
  },

  // ---- Info / append callouts --------------------------------------------
  {
    id: 'appendFactCallout',
    name: 'Append-mode schema-stability callout (audit output)',
    group: 'info',
    appliesTo: 'informational',
    appliesToLabel: 'Append mode (audit output)',
    requiredBecause:
      'PAX append mode does not migrate schema. If column names or types change, the append will misalign. Re-export with a new file when the schema shifts.',
    triggeredBy: ['destinations.fact.mode'],
    severity: 'informational',
  },
  {
    id: 'appendUserInfoCallout',
    name: 'Append-mode schema-stability callout (user info output)',
    group: 'info',
    appliesTo: 'informational',
    appliesToLabel: 'Append mode (user info output)',
    requiredBecause:
      'PAX append mode does not migrate schema for the user info table either. If user-info columns change, the append will misalign. Re-export with a new file when the schema shifts.',
    triggeredBy: ['destinations.userInfo.mode'],
    severity: 'informational',
  },
  {
    id: 'excludeCopilotInteractionInfo',
    name: 'CopilotInteraction audit branch excluded',
    group: 'info',
    appliesTo: 'informational',
    appliesToLabel: 'Audit query (CopilotInteraction excluded)',
    requiredBecause:
      'When -ExcludeCopilotInteraction is set, PAX skips the CopilotInteraction audit branch. No additional permission is required, but the resulting audit output will not contain Copilot interaction rows.',
    triggeredBy: ['query.excludeCopilotInteraction'],
    severity: 'informational',
  },

  // ---- Auth callouts ------------------------------------------------------
  {
    id: 'webLoginDefaultInfo',
    name: 'WebLogin (default interactive) callout',
    group: 'info',
    appliesTo: 'informational',
    appliesToLabel: 'Auth: WebLogin',
    requiredBecause:
      'WebLogin will prompt you to sign in interactively when the bake runs.',
    triggeredBy: ['auth.mode'],
    severity: 'informational',
  },
  {
    id: 'deviceCodeRuntimeInfo',
    name: 'DeviceCode runtime auth callout',
    group: 'info',
    appliesTo: 'informational',
    appliesToLabel: 'Auth: DeviceCode',
    requiredBecause:
      '-Auth DeviceCode produces a device code at runtime that the operator must complete in a browser. The builder does not collect or store any Graph token.',
    triggeredBy: ['auth.mode'],
    severity: 'informational',
  },
  {
    id: 'appRegistrationSecretCallout',
    name: 'App registration (secret) credential handling',
    group: 'environment',
    appliesTo: 'environment-setting',
    appliesToLabel: 'Auth: App registration (secret)',
    requiredBecause:
      'The builder does not collect or store client secrets. The rendered command contains a <CLIENT_SECRET> placeholder. Replace it in your terminal at run time. The tenant id, client id, and app registration must already be valid.',
    triggeredBy: ['auth.mode', 'auth.tenantId', 'auth.clientId'],
    severity: 'required',
  },
  {
    id: 'appRegistrationCertificateCallout',
    name: 'App registration (certificate) credential handling',
    group: 'environment',
    appliesTo: 'local-machine',
    appliesToLabel: 'Auth: App registration (certificate)',
    requiredBecause:
      'Certificate-based app registration auth requires the configured tenant id, client id, and certificate thumbprint (-ClientCertificateThumbprint) to be valid locally. The certificate must be present in a cert store the runtime account can read.',
    triggeredBy: ['auth.mode', 'auth.tenantId', 'auth.clientId', 'auth.certificateThumbprint'],
    severity: 'required',
  },
  {
    id: 'managedIdentityCallout',
    name: 'Managed identity (hosted) callout',
    group: 'environment',
    appliesTo: 'environment-setting',
    appliesToLabel: 'Auth: Managed identity',
    requiredBecause:
      '-Auth ManagedIdentity requires the command to run on an Azure-hosted compute resource with a managed identity that has the listed Graph application permissions consented. The builder does not verify the hosted environment.',
    triggeredBy: ['auth.mode'],
    severity: 'required',
  },
] as const;

const RULES_BY_ID = new Map<string, PermissionRule>(PERMISSION_RULES.map(r => [r.id, r]));

/** Lookup a permission rule by id. */
export function getPermissionRule(id: string): PermissionRule | undefined {
  return RULES_BY_ID.get(id);
}
