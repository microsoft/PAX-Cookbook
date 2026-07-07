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
// Experimental channel discovery: the GitHub releases API. Production (stable)
// never touches this URL; only a build whose installed channel is exactly
// 'experimental' queries it (see resolveChannel — anything else is stable).
const RELEASES_API_URL =
  'https://api.github.com/repos/microsoft/PAX-Cookbook/releases';
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
  /** Full SHA-256 fingerprint of the installed/available component (verification). */
  installedSha: string | null;
  availableSha: string | null;
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
 *
 * Channel-aware: the installed channel (from runtime/version) selects the
 * discovery source. 'stable' (and anything that is NOT exactly 'experimental')
 * reads main/versions.json exactly as before. 'experimental' reads the newest
 * GitHub pre-release's attached versions.json instead. The comparison logic is
 * identical for both — an experimental build keeps the plain cookbook version
 * in versions.json (only its payload SHA moves per pre-release), so the SHA
 * compare already handles it and parseVersion/isNewer never see a release tag.
 */
export async function checkForUpdates(): Promise<UpdateCheckResult> {
  // Runtime + engine state first: the runtime channel decides the source, and
  // both branches need these values anyway. Fetch order does not affect the
  // result (independent reads).
  const [ver, eng] = await Promise.all([getRuntimeVersion(), getPaxEngineState()]);
  const channel = resolveChannel(ver.ok && ver.data ? ver.data.releaseChannel : null);

  let remote: unknown;
  try {
    remote =
      channel === 'experimental'
        ? await fetchExperimentalManifest()
        : await fetchStableManifest();
  } catch {
    return { status: 'unavailable', components: [], allComponents: [], checkedAtUtc: null };
  }
  if (remote == null || typeof remote !== 'object') {
    return { status: 'unavailable', components: [], allComponents: [], checkedAtUtc: null };
  }

  const checkedAtUtc = new Date().toISOString();
  setLastCheckedUtc(checkedAtUtc);

  return buildUpdateResult(remote, ver, eng, checkedAtUtc);
}

/**
 * Fail-safe channel resolution: the experimental discovery path runs ONLY when
 * the installed channel is EXACTLY 'experimental'. Missing, malformed,
 * 'unknown', 'stable', or any other value collapses to 'stable' so a production
 * build can never accidentally follow the pre-release path.
 */
function resolveChannel(raw: string | null | undefined): 'stable' | 'experimental' {
  const c = typeof raw === 'string' ? raw.trim().toLowerCase() : '';
  return c === 'experimental' ? 'experimental' : 'stable';
}

/** Stable manifest: the fixed versions.json on main (production, unchanged). */
async function fetchStableManifest(): Promise<unknown> {
  const res = await fetch(`${VERSIONS_URL}?cb=${Date.now()}`, {
    method: 'GET',
    cache: 'no-store',
  });
  if (!res.ok) {
    return null;
  }
  return await res.json();
}

/**
 * Experimental manifest: the newest GitHub PRE-RELEASE, selected by created_at
 * (descending) — NOT by version parsing — then that release's attached
 * versions.json asset (which carries the payload SHA gate). Returns null on any
 * miss so the caller reports 'unavailable' and stays silent.
 */
async function fetchExperimentalManifest(): Promise<unknown> {
  const res = await fetch(`${RELEASES_API_URL}?per_page=100&cb=${Date.now()}`, {
    method: 'GET',
    cache: 'no-store',
    headers: { Accept: 'application/vnd.github+json' },
  });
  if (!res.ok) {
    return null;
  }
  const releases = await res.json();
  if (!Array.isArray(releases)) {
    return null;
  }
  const prereleases = releases.filter(
    (r) =>
      r &&
      typeof r === 'object' &&
      (r as { prerelease?: unknown }).prerelease === true &&
      (r as { draft?: unknown }).draft !== true,
  );
  if (prereleases.length === 0) {
    return null;
  }
  // Newest first by created_at. This — not isNewer/parseVersion — is how the
  // experimental channel decides WHICH release to compare against.
  prereleases.sort(
    (a, b) =>
      Date.parse((b as { created_at?: string }).created_at ?? '') -
      Date.parse((a as { created_at?: string }).created_at ?? ''),
  );
  const newest = prereleases[0] as { assets?: unknown };
  const assets = Array.isArray(newest.assets) ? newest.assets : [];
  const manifestAsset = assets.find(
    (a) => a && typeof a === 'object' && (a as { name?: unknown }).name === 'versions.json',
  ) as { browser_download_url?: unknown } | undefined;
  if (!manifestAsset || typeof manifestAsset.browser_download_url !== 'string') {
    return null;
  }
  const mres = await fetch(`${manifestAsset.browser_download_url}?cb=${Date.now()}`, {
    method: 'GET',
    cache: 'no-store',
  });
  if (!mres.ok) {
    return null;
  }
  return await mres.json();
}

/**
 * Compare a resolved manifest against installed runtime/engine state and build
 * the result. Shared verbatim by both channels — the only channel difference is
 * where `remote` came from (see fetchStableManifest / fetchExperimentalManifest).
 */
function buildUpdateResult(
  remote: unknown,
  ver: Awaited<ReturnType<typeof getRuntimeVersion>>,
  eng: Awaited<ReturnType<typeof getPaxEngineState>>,
  checkedAtUtc: string,
): UpdateCheckResult {
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

  // App update? A version bump, or — at the SAME version — a different payload
  // SHA. If the installed payload SHA is UNKNOWN (installed-skus.json missing or
  // empty), we cannot verify the build is current, so we report an update
  // available rather than silently claiming "up to date" (which also self-heals:
  // applying the update makes the installer record the SHA). All SHA compares
  // are case-insensitive.
  const appVersionSame =
    !isNewer(remoteApp, installedApp) && sameVersion(remoteApp, installedApp);
  const appNewBuildOnly =
    appVersionSame &&
    !!remotePayloadSha &&
    (!installedPayloadSha ||
      installedPayloadSha.toLowerCase() !== remotePayloadSha.toLowerCase());
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
      installedSha: installedPayloadSha,
      availableSha: remotePayloadSha,
      hasUpdate: appHasUpdate,
      newBuildOnly: appNewBuildOnly,
    },
    {
      name: 'PAX engine',
      installedVersion: installedEngineVer,
      availableVersion: remoteEngineVer,
      installedBuild: null,
      availableBuild: null,
      installedSha: installedEngineSha,
      availableSha: remoteEngineSha,
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
