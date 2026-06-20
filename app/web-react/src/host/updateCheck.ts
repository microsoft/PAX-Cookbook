/**
 * Self-update check (client side).
 *
 * Fetches the published `versions.json` from the PAX Cookbook GitHub repo
 * (cache-busted) and compares it against the versions this build reports, so the
 * app can offer an in-app update. The actual apply is handed off to the broker
 * (`applyUpdate` -> the installer); this module only DECIDES whether an update
 * is available and in what user-facing terms.
 *
 * Read-only and resilient: any failure to reach GitHub (offline, firewall,
 * rate-limit, malformed body) resolves to `status: 'unavailable'` so the
 * auto-check on startup silently skips with no error and no modal. The engine is
 * immutable per release, so in practice the app version is what moves.
 */
import { getRuntimeVersion, getPaxEngineState } from './systemInfo';

const VERSIONS_URL =
  'https://raw.githubusercontent.com/microsoft/PAX-Cookbook/main/versions.json';
const LAST_CHECK_KEY = 'pax.updates.lastCheckedUtc';

export interface UpdateComponent {
  /** Friendly name, e.g. "PAX Cookbook app" or "PAX engine". */
  name: string;
  fromVersion: string | null;
  toVersion: string | null;
  /** True when only the build changed (same version number). */
  newBuildOnly: boolean;
  /** Installed build timestamp (shown for a same-version rebuild). */
  fromBuild?: string | null;
  /** Available build timestamp (shown for a same-version rebuild). */
  toBuild?: string | null;
}

export interface UpdateCheckResult {
  status: 'up-to-date' | 'updates-available' | 'unavailable';
  components: UpdateComponent[];
  /**
   * Both components (app + engine) ALWAYS, each flagged with whether it has an
   * update, so the Updates page can show the full picture every time.
   */
  allComponents: UpdateComponentStatus[];
  /** ISO timestamp of when the check reached GitHub (null when unavailable). */
  checkedAtUtc: string | null;
}

// Per-component status shown on the Updates page whether or not it has an
// update. installed/available build dates are populated for the app only (the
// engine ships in the payload and has no separate build stamp).
export interface UpdateComponentStatus {
  name: string;
  installedVersion: string | null;
  availableVersion: string | null;
  installedBuild: string | null;
  availableBuild: string | null;
  hasUpdate: boolean;
  newBuildOnly: boolean;
}

function parseVersion(v: string | null | undefined): number[] | null {
  if (!v || typeof v !== 'string') {
    return null;
  }
  const trimmed = v.trim();
  if (!/^\d+(\.\d+)*$/.test(trimmed)) {
    return null;
  }
  return trimmed.split('.').map(n => parseInt(n, 10));
}

/** True only when `remote` is a strictly-newer dotted version than `installed`. */
function isNewer(remote: string | null | undefined, installed: string | null | undefined): boolean {
  const r = parseVersion(remote);
  const i = parseVersion(installed);
  if (!r || !i) {
    return false; // unknown either side — never prompt on a guess
  }
  const len = Math.max(r.length, i.length);
  for (let k = 0; k < len; k++) {
    const rv = r[k] ?? 0;
    const iv = i[k] ?? 0;
    if (rv > iv) return true;
    if (rv < iv) return false;
  }
  return false;
}

/** True only when both parse to the same dotted version. */
function sameVersion(a: string | null | undefined, b: string | null | undefined): boolean {
  const pa = parseVersion(a);
  const pb = parseVersion(b);
  if (!pa || !pb) {
    return false;
  }
  const len = Math.max(pa.length, pb.length);
  for (let k = 0; k < len; k++) {
    if ((pa[k] ?? 0) !== (pb[k] ?? 0)) {
      return false;
    }
  }
  return true;
}

export function getLastCheckedUtc(): string | null {
  try {
    const v = window.localStorage.getItem(LAST_CHECK_KEY);
    return v && v.trim() ? v : null;
  } catch {
    return null;
  }
}

function setLastCheckedUtc(iso: string): void {
  try {
    window.localStorage.setItem(LAST_CHECK_KEY, iso);
  } catch {
    /* storage disabled — the timestamp simply will not persist */
  }
}

/**
 * Check GitHub for a newer release and compare against the installed build.
 * Never throws — failures resolve to `status: 'unavailable'`.
 */
export async function checkForUpdates(): Promise<UpdateCheckResult> {
  let remote: unknown;
  try {
    const res = await fetch(`${VERSIONS_URL}?cb=${Date.now()}`, {
      method: 'GET',
      cache: 'no-store',
    });
    if (!res.ok) {
      return { status: 'unavailable', components: [], allComponents: [], checkedAtUtc: null };
    }
    remote = await res.json();
  } catch {
    return { status: 'unavailable', components: [], allComponents: [], checkedAtUtc: null };
  }

  const checkedAtUtc = new Date().toISOString();
  setLastCheckedUtc(checkedAtUtc);

  const current =
    remote && typeof remote === 'object'
      ? ((remote as { current?: Record<string, unknown> }).current ?? {})
      : {};
  const engine =
    current && typeof current.engine === 'object'
      ? (current.engine as Record<string, unknown>)
      : {};
  const payload =
    current && typeof current.payload === 'object'
      ? (current.payload as Record<string, unknown>)
      : {};
  const remoteApp = typeof current.version === 'string' ? current.version : null;
  const remoteBuiltAt = typeof current.builtAtUtc === 'string' ? current.builtAtUtc : null;
  const remotePayloadSha = typeof payload.sha256 === 'string' ? payload.sha256 : null;
  const remoteEngineVer = typeof engine.version === 'string' ? engine.version : null;
  const remoteEngineSha = typeof engine.sha256 === 'string' ? engine.sha256 : null;

  const [ver, eng] = await Promise.all([getRuntimeVersion(), getPaxEngineState()]);
  const installedApp = ver.ok && ver.data ? ver.data.cookbookVersion : null;
  const installedBuildTs = ver.ok && ver.data ? ver.data.buildTimestamp : null;
  const installedPayloadSha = ver.ok && ver.data ? ver.data.installedPayloadSha256 : null;
  const installedEngineVer =
    (eng.ok && eng.data ? eng.data.approvedVersion : null) ??
    (ver.ok && ver.data ? ver.data.bundledPax.version : null);
  const installedEngineSha =
    (eng.ok && eng.data ? eng.data.approvedSha256 : null) ??
    (ver.ok && ver.data ? ver.data.bundledPax.sha256 : null);

  // Diagnostics — visible in the dev console so a mis-compare can be traced
  // (the values that drive "installed" vs "remote"). No secrets are logged.
  try {
    console.info('[pax-update-check]', {
      installedApp,
      remoteApp,
      installedPayloadSha,
      remotePayloadSha,
      installedBuildTs,
      remoteBuiltAt,
      installedEngineVer,
      remoteEngineVer,
      installedEngineSha,
      remoteEngineSha,
    });
  } catch {
    /* console may be unavailable */
  }

  // App update? A version bump, or a same-version rebuild detected via the
  // installer-recorded payload SHA.
  const appNewBuildOnly =
    !isNewer(remoteApp, installedApp) &&
    sameVersion(remoteApp, installedApp) &&
    !!installedPayloadSha &&
    !!remotePayloadSha &&
    installedPayloadSha.toLowerCase() !== remotePayloadSha.toLowerCase();
  const appHasUpdate = isNewer(remoteApp, installedApp) || appNewBuildOnly;

  // Engine update? Only once the engine is actually acquired — on a fresh
  // install it is not yet acquired, and the engine is immutable per release
  // anyway (its SHA always matches the published one), so this is rare.
  const engineAcquired = eng.ok && eng.data ? eng.data.isAcquired : false;
  const engineNewBuildOnly =
    engineAcquired &&
    !isNewer(remoteEngineVer, installedEngineVer) &&
    sameVersion(remoteEngineVer, installedEngineVer) &&
    !!remoteEngineSha &&
    !!installedEngineSha &&
    remoteEngineSha.toLowerCase() !== installedEngineSha.toLowerCase();
  const engineHasUpdate =
    engineAcquired &&
    (isNewer(remoteEngineVer, installedEngineVer) || engineNewBuildOnly);

  const components: UpdateComponent[] = [];
  if (appHasUpdate) {
    components.push({
      name: 'PAX Cookbook app',
      fromVersion: installedApp,
      toVersion: remoteApp,
      newBuildOnly: appNewBuildOnly,
      fromBuild: installedBuildTs,
      toBuild: remoteBuiltAt,
    });
  }
  if (engineHasUpdate) {
    components.push({
      name: 'PAX engine',
      fromVersion: installedEngineVer,
      toVersion: remoteEngineVer,
      newBuildOnly: engineNewBuildOnly,
    });
  }

  // Always present both components for the full-picture comparison view.
  const allComponents: UpdateComponentStatus[] = [
    {
      name: 'PAX Cookbook app',
      installedVersion: installedApp,
      availableVersion: remoteApp,
      installedBuild: installedBuildTs,
      availableBuild: remoteBuiltAt,
      hasUpdate: appHasUpdate,
      newBuildOnly: appNewBuildOnly,
    },
    {
      name: 'PAX engine',
      installedVersion: installedEngineVer,
      availableVersion: remoteEngineVer,
      installedBuild: null,
      availableBuild: null,
      hasUpdate: engineHasUpdate,
      newBuildOnly: engineNewBuildOnly,
    },
  ];

  return {
    status: components.length > 0 ? 'updates-available' : 'up-to-date',
    components,
    allComponents,
    checkedAtUtc,
  };
}
