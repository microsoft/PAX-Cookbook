/**
 * Static catalog of fact-destination storage targets.
 *
 * Mini-Kitchen does not deploy or validate any of these targets at runtime;
 * this catalog feeds the destination card UI and the permissions resolver.
 *
 * Status semantics:
 *   - `supported`                          : emitable from Mini-Kitchen today.
 *                                            Tier-specific runtime caveats are
 *                                            surfaced through `warnings` and
 *                                            `notes` (e.g. workspace access).
 *   - `catalog-gated`                      : emitable, but extra permissions
 *                                            and out-of-band setup apply that
 *                                            Mini-Kitchen cannot verify.
 *   - `not-available-for-selected-version` : not emitable; Mini-Kitchen
 *                                            surfaces the option only as a
 *                                            "use the full PAX Cookbook" hint.
 *
 * PAX v1.11.3 auto-detects the storage tier (Local / SharePoint / Fabric)
 * from the URL shape passed to the same `-OutputPath` / `-AppendFile` /
 * `-OutputPathUserInfo` / `-AppendUserInfo` switches via the `Get-PathTier`
 * helper. There are no Fabric-specific switches. Mini-Kitchen does not
 * validate workspace/lakehouse/schema/table access; the runtime identity is
 * responsible for write access at execution time.
 */

import type { StorageTargetDefinition } from '../types';

export const STORAGE_TARGETS: readonly StorageTargetDefinition[] = [
  {
    id: 'local',
    name: 'Local CSV files',
    status: 'supported',
    tierDetection: 'local-path',
    switches: ['OutputPath', 'AppendFile', 'OutputPathUserInfo', 'AppendUserInfo'],
    permissions: ['outputWriteAccess'],
    warnings: [
      'The builder cannot validate local paths.',
      'Use an absolute path that the runtime account can write to.',
    ],
    notes: [
      'Default storage tier for new recipes.',
      'Works on Windows, macOS, and Linux runtimes.',
    ],
  },
  {
    id: 'sharepoint',
    name: 'SharePoint CSV files',
    status: 'supported',
    tierDetection: 'url-auto-detected',
    switches: ['OutputPath', 'AppendFile', 'OutputPathUserInfo', 'AppendUserInfo'],
    permissions: [
      'sitesReadWriteAll',
      'filesReadWriteAll',
      'destinationSiteLibraryAccess',
    ],
    warnings: [
      'The builder cannot verify the SharePoint URL or confirm site access.',
      'The runtime identity must have write access to the destination document library.',
      'Provide a full HTTPS SharePoint URL to a .csv file.',
    ],
    notes: [
      'PAX auto-detects the SharePoint tier from the URL shape; the same OutputPath / AppendFile / OutputPathUserInfo / AppendUserInfo switches are used.',
    ],
  },
  {
    id: 'fabric',
    name: 'Fabric Lakehouse (OneLake)',
    status: 'supported',
    tierDetection: 'url-auto-detected',
    switches: ['OutputPath', 'AppendFile', 'OutputPathUserInfo', 'AppendUserInfo'],
    permissions: [
      'fabricWorkspaceContributor',
      'fabricServicePrincipalsTenantSetting',
      'oneLakeUrlContract',
    ],
    warnings: [
      'The builder cannot verify the OneLake URL, the workspace, the lakehouse, or schema/table existence.',
      'The runtime identity must be a Contributor (or higher) on the Fabric workspace that owns the destination lakehouse.',
      'Provide a full HTTPS OneLake URL pointing at a Lakehouse (optionally under /Tables[/<schema>][/<table>] or /Files[/<sub>]).',
    ],
    notes: [
      'PAX auto-detects Fabric Lakehouse destinations from the URL shape on the same OutputPath / AppendFile / OutputPathUserInfo / AppendUserInfo switches — there are no Fabric-specific switches.',
      'All bound output destinations in one PAX run must resolve to the same storage tier (no mixed Local / SharePoint / Fabric in one invocation).',
      'When using `-Auth AppRegistration`, the Fabric admin tenant setting "Service principals can use Fabric APIs" must include the app or its security group.',
    ],
  },
] as const;

const TARGETS_BY_ID = new Map<string, StorageTargetDefinition>(
  STORAGE_TARGETS.map(t => [t.id, t]),
);

/** Lookup a storage target definition by id. */
export function getStorageTarget(id: string): StorageTargetDefinition | undefined {
  return TARGETS_BY_ID.get(id);
}
