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
import { getExperimentalUpdateManifest } from './brokerBridge';

const VERSIONS_URL =
  'https://raw.githubusercontent.com/microsoft/PAX-Cookbook/main/versions.json';
// Experimental channel discovery now runs SERVER-SIDE through the broker
// (/api/v1/updates/experimental-manifest) — the broker calls the GitHub
// Releases API from its own process, never the WebView2 renderer. This matches
// the Pantry pattern and sidesteps browser-context CORS, corporate-proxy
// behavior, and api.github.com's per-IP rate limit. The stable channel below
// still reads main/versions.json directly (unchanged).
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
  /**
   * A specific, user-facing reason when status is 'unavailable' (e.g. a GitHub
   * rate limit or an unexpected response). Undefined for a successful check.
   * The Updates page shows this instead of the generic "make sure you're
   * online" message when present.
   */
  detail?: string;
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

interface ParsedVersion {
  core: number[];
  prerelease: { channel: 'exp' | 'internal'; ordinal: number } | null;
}

function parseVersion(v: string | null | undefined): ParsedVersion | null {
  if (!v || typeof v !== 'string') {
    return null;
  }
  const trimmed = v.trim();
  const match = /^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-(exp|internal)\.([1-9]\d*))?$/.exec(trimmed);
  if (!match) {
    return null;
  }
  return {
    core: [match[1], match[2], match[3], match[4] ?? '0'].map(value => parseInt(value, 10)),
    prerelease: match[5]
      ? {
          channel: match[5] as 'exp' | 'internal',
          ordinal: parseInt(match[6], 10),
        }
      : null,
  };
}

/** True only when `remote` is a strictly newer supported release-contract version. */
export function isNewerVersion(remote: string | null | undefined, installed: string | null | undefined): boolean {
  const r = parseVersion(remote);
  const i = parseVersion(installed);
  if (!r || !i) {
    return false; // unknown either side — never prompt on a guess
  }
  const len = Math.max(r.core.length, i.core.length);
  for (let k = 0; k < len; k++) {
    const rv = r.core[k] ?? 0;
    const iv = i.core[k] ?? 0;
    if (rv > iv) return true;
    if (rv < iv) return false;
  }
  if (!r.prerelease && i.prerelease) return true;
  if (r.prerelease && !i.prerelease) return false;
  if (!r.prerelease || !i.prerelease) return false;
  if (r.prerelease.channel !== i.prerelease.channel) return false;
  return r.prerelease.ordinal > i.prerelease.ordinal;
}

/** True only when both parse to the same supported release-contract version. */
export function sameVersion(a: string | null | undefined, b: string | null | undefined): boolean {
  const pa = parseVersion(a);
  const pb = parseVersion(b);
  if (!pa || !pb) {
    return false;
  }
  const len = Math.max(pa.core.length, pb.core.length);
  for (let k = 0; k < len; k++) {
    if ((pa.core[k] ?? 0) !== (pb.core[k] ?? 0)) {
      return false;
    }
  }
  if (!pa.prerelease || !pb.prerelease) return pa.prerelease === pb.prerelease;
  return pa.prerelease.channel === pb.prerelease.channel &&
    pa.prerelease.ordinal === pb.prerelease.ordinal;
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
 * discovery source. Stable reads main/versions.json, experimental reads the
 * newest GitHub pre-release's attached versions.json, and internal refuses
 * update discovery entirely.
 */
function unavailable(detail?: string): UpdateCheckResult {
  return { status: 'unavailable', components: [], allComponents: [], checkedAtUtc: null, detail };
}

export async function checkForUpdates(): Promise<UpdateCheckResult> {
  // Runtime + engine state first: the runtime channel decides the source, and
  // both branches need these values anyway. Fetch order does not affect the
  // result (independent reads).
  const [ver, eng] = await Promise.all([getRuntimeVersion(), getPaxEngineState()]);
  const channel = resolveChannel(ver.ok && ver.data ? ver.data.releaseChannel : null);

  if (channel === 'internal') {
    return unavailable('Local validation builds do not check production updates.');
  }

  let remote: unknown;
  if (channel === 'experimental') {
    // Experimental discovery runs SERVER-SIDE through the broker — never a
    // browser-direct api.github.com fetch — so it is not subject to WebView
    // CORS, corporate proxy behavior, or api.github.com's per-IP rate limit.
    // The broker returns a specific state so a failure can be explained rather
    // than shown as a generic "make sure you're online".
    const exp = await getExperimentalUpdateManifest();
    if (exp.state === 'ok' && exp.manifestJson) {
      try {
        remote = JSON.parse(exp.manifestJson);
      } catch {
        return unavailable('The pre-release update information was unreadable.');
      }
    } else if (exp.state === 'no_prerelease') {
      return unavailable('No pre-release builds are currently published.');
    } else {
      return unavailable(
        exp.detail ?? 'Couldn\u2019t check for pre-release updates just now.',
      );
    }
    if (remote == null || typeof remote !== 'object') {
      return unavailable('The pre-release update information was unreadable.');
    }
  } else {
    // Stable channel — UNCHANGED: read the fixed main/versions.json directly.
    try {
      remote = await fetchStableManifest();
    } catch {
      return { status: 'unavailable', components: [], allComponents: [], checkedAtUtc: null };
    }
    if (remote == null || typeof remote !== 'object') {
      return { status: 'unavailable', components: [], allComponents: [], checkedAtUtc: null };
    }
  }

  const checkedAtUtc = new Date().toISOString();
  setLastCheckedUtc(checkedAtUtc);

  return buildUpdateResult(remote, ver, eng, checkedAtUtc);
}

/**
 * Fail-safe channel resolution: exact internal and experimental values retain
 * their isolated routes. Missing, malformed, or unknown values collapse to
 * stable so a production build never follows a non-production route by guess.
 */
function resolveChannel(raw: string | null | undefined): 'stable' | 'experimental' | 'internal' {
  const c = typeof raw === 'string' ? raw.trim().toLowerCase() : '';
  if (c === 'experimental') return 'experimental';
  if (c === 'internal') return 'internal';
  return 'stable';
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
    !isNewerVersion(remoteApp, installedApp) && sameVersion(remoteApp, installedApp);
  const appNewBuildOnly =
    appVersionSame &&
    !!remotePayloadSha &&
    (!installedPayloadSha ||
      installedPayloadSha.toLowerCase() !== remotePayloadSha.toLowerCase());
  const appHasUpdate = isNewerVersion(remoteApp, installedApp) || appNewBuildOnly;

  // Engine update? Only once the engine is actually acquired — on a fresh
  // install it is not yet acquired, and the engine is immutable per release
  // anyway (its SHA always matches the published one), so this is rare.
  const engineAcquired = eng.ok && eng.data ? eng.data.isAcquired : false;
  const engineNewBuildOnly =
    engineAcquired &&
    !isNewerVersion(remoteEngineVer, installedEngineVer) &&
    sameVersion(remoteEngineVer, installedEngineVer) &&
    !!remoteEngineSha &&
    !!installedEngineSha &&
    remoteEngineSha.toLowerCase() !== installedEngineSha.toLowerCase();
  const engineHasUpdate =
    engineAcquired &&
    (isNewerVersion(remoteEngineVer, installedEngineVer) || engineNewBuildOnly);

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
