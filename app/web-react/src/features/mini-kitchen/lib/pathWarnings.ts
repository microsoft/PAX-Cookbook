/**
 * Path warning detector.
 *
 * Mini-Kitchen runs in the browser and cannot validate any path the user
 * types into a destination field. This module returns advisory messages
 * for common destination shapes so the UI can show the right caveats.
 *
 * Phase 2 returns warning strings only; the UI decides presentation.
 */

export type PathWarningSeverity = 'info' | 'warning';

export interface PathWarning {
  id: string;
  severity: PathWarningSeverity;
  message: string;
}

const WINDOWS_LOCAL_PATH = /^[A-Za-z]:[\\/]/;
const UNC_PATH = /^\\\\[^\\]+\\/;
const POSIX_LOCAL_PATH = /^\/(?:[^/].*)?$/;
const HOME_PATH = /^~[\\/]?/;
const SHAREPOINT_URL = /^https:\/\/[^/]+\.sharepoint\.com\//i;
// PAX (v1.11.6+) treats URLs on the OneLake DFS endpoint as Fabric Lakehouse
// destinations (optionally with a regional prefix like `<region>-onelake.dfs...`).
// The item segment may be addressed by NAME (`<item>.Lakehouse`, suffix required)
// or by GUID (`<itemId>`, no suffix) — the two canonical OneLake DFS forms. Any
// other Fabric-looking URL is rejected by the tier resolver, so a generic
// `*.fabric.microsoft.com` URL is downgraded to a heads-up warning.
const ONELAKE_LAKEHOUSE_URL =
  /^https:\/\/(?:[a-z0-9-]+-)?onelake\.dfs\.fabric\.microsoft\.com\/[^/]+\/(?:[^/]+\.Lakehouse|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?:\/(?:Tables|Files)(?:\/.*)?)?\/?$/i;
const ONELAKE_HOST = /^https:\/\/(?:[a-z0-9-]+-)?onelake\.dfs\.fabric\.microsoft\.com\//i;
const FABRIC_PORTAL_URL = /^https:\/\/[^/]+\.fabric\.microsoft\.com\//i;
const ABFSS_URL = /^abfss:\/\//i;

/**
 * Inspect a destination path string and return advisory warnings. Returns an
 * empty list for empty input.
 */
export function detectPathWarnings(path: string | undefined): readonly PathWarning[] {
  const out: PathWarning[] = [];
  const trimmed = (path ?? '').trim();
  if (!trimmed) {
    return out;
  }

  if (WINDOWS_LOCAL_PATH.test(trimmed) || POSIX_LOCAL_PATH.test(trimmed) || HOME_PATH.test(trimmed)) {
    out.push({
      id: 'local-path',
      severity: 'warning',
      message: 'The builder cannot validate this local path.',
    });
    out.push({
      id: 'local-path-machine-specific',
      severity: 'info',
      message: 'Local paths are machine-specific; verify the runtime account can write here.',
    });
  }

  if (UNC_PATH.test(trimmed)) {
    out.push({
      id: 'unc-path',
      severity: 'warning',
      message: 'The builder cannot validate this UNC path. Verify reachability from the runtime.',
    });
  }

  if (SHAREPOINT_URL.test(trimmed)) {
    out.push({
      id: 'sharepoint-url',
      severity: 'warning',
      message:
        'The builder cannot verify SharePoint URLs, site access, or document library permissions.',
    });
  }

  if (ONELAKE_HOST.test(trimmed)) {
    out.push({
      id: 'fabric-onelake-url',
      severity: 'warning',
      message:
        'The builder cannot verify the OneLake workspace, lakehouse, schema, or table.',
    });
    out.push({
      id: 'fabric-onelake-identity',
      severity: 'info',
      message:
        'The runtime identity must be a Contributor (or higher) on the Fabric workspace that owns the destination lakehouse.',
    });
    if (!ONELAKE_LAKEHOUSE_URL.test(trimmed)) {
      out.push({
        id: 'fabric-onelake-shape',
        severity: 'warning',
        message:
          'Provide a full OneLake Lakehouse URL. The item can be addressed by name (<item>.Lakehouse) or by GUID (<itemId>, no suffix): https://[<region>-]onelake.dfs.fabric.microsoft.com/<workspace>/<item>.Lakehouse[/Tables[/<schema>][/<table>]|/Files[/<sub>]]',
      });
    }
  } else if (FABRIC_PORTAL_URL.test(trimmed)) {
    out.push({
      id: 'fabric-portal-url',
      severity: 'warning',
      message:
        'This looks like a Fabric portal URL, not a OneLake destination. PAX only accepts OneLake Lakehouse URLs on the onelake.dfs.fabric.microsoft.com host.',
    });
  } else if (ABFSS_URL.test(trimmed)) {
    out.push({
      id: 'fabric-abfss-url',
      severity: 'warning',
      message:
        'PAX does not accept abfss:// URLs. Use the HTTPS OneLake form: https://[<region>-]onelake.dfs.fabric.microsoft.com/...',
    });
  }

  return out;
}

/** Convenience: does the path produce any warnings? */
export function hasPathWarnings(path: string | undefined): boolean {
  return detectPathWarnings(path).length > 0;
}
