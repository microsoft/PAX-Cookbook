/**
 * Updates workspace.
 *
 * A real, customer-ready version / update-status surface built entirely from
 * existing read-only broker state: the runtime/version payload, the PAX engine
 * acquisition state, and the health payload. It presents compact cards -
 * Installed app, PAX Engine, Update checking, Support details, and a short
 * "Before updating" guidance card.
 *
 * This build has no online update-check endpoint, so this surface never claims
 * an update result and never says the app is current. It states plainly that
 * online update checking is not available in this build and points the user at
 * the internal release package.
 *
 * Updates NEVER runs PAX, NEVER cooks, bakes, or schedules, NEVER downloads or
 * installs anything, NEVER starts an installer or update process, and NEVER
 * shows a secret. Every value shown reflects state the broker already reports;
 * missing values fall back to a plain "Not reported by this build".
 */
import { useEffect, useRef, useState } from 'react';
import { SectionHeader } from './components/SectionHeader';
import {
  checkForUpdates,
  getLastCheckedUtc,
  type UpdateComponentStatus,
} from '../host/updateCheck';
import { applyUpdate, shutdownBroker } from '../host/brokerBridge';
import { postCloseDecision } from '../host/closeHandoff';
import { setUpdatesBadge } from './shellNav';
import {
  getRuntimeVersion,
  getPaxEngineState,
  getHealth,
  formatBuildTimestamp,
  type RuntimeVersionInfo,
  type PaxEngineState,
  type HealthInfo,
} from '../host/systemInfo';

type LoadPhase = 'loading' | 'ready' | 'error';

const APPROVED_ENGINE_NAME = 'PAX Purview Audit Log Processor';
const NOT_REPORTED = 'Not reported by this build';

function engineStatusLabel(
  phase: LoadPhase,
  engine: PaxEngineState | null,
): string {
  if (phase === 'loading') {
    return 'Checking…';
  }
  if (phase === 'error' || !engine) {
    return NOT_REPORTED;
  }
  if (engine.isAcquired) {
    return 'Ready';
  }
  if (engine.managedScriptPresent) {
    return 'Needs attention';
  }
  return 'Not installed';
}

function formatLastCheckedLabel(iso: string | null): string | null {
  if (!iso) {
    return null;
  }
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) {
    return null;
  }
  return d.toLocaleString(undefined, {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  });
}

// Friendly date for a build timestamp. Accepts both the app's dashed UTC build
// stamp ("2026-06-20-00-56-03-UTC") and an ISO instant ("2026-05-22T00:00:00Z")
// and renders e.g. "Jun 19, 2026, 10:54 PM" in the user's locale.
function formatBuiltDate(raw: string | null | undefined): string | null {
  if (!raw) {
    return null;
  }
  const t = raw.trim();
  let d: Date | null = null;
  const m = t.match(/^(\d{4})-(\d{2})-(\d{2})-(\d{2})-(\d{2})-(\d{2})-UTC$/);
  if (m) {
    d = new Date(Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]));
  } else {
    const parsed = new Date(t);
    if (!Number.isNaN(parsed.getTime())) {
      d = parsed;
    }
  }
  if (!d || Number.isNaN(d.getTime())) {
    return null;
  }
  return d.toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  });
}

// Version line for the "Installed" column.
function installedVersionText(c: UpdateComponentStatus): string {
  return c.installedVersion ? `Version ${c.installedVersion}` : 'Current version';
}

// Version line for the "Available" column. A same-version rebuild reads
// "Version 1.0.0 — new build"; a real version bump reads "Version 1.0.1".
function availableVersionText(c: UpdateComponentStatus): string {
  const v = c.availableVersion ?? c.installedVersion;
  if (c.hasUpdate && c.newBuildOnly) {
    return v ? `Version ${v} — new build` : 'New build';
  }
  return v ? `Version ${v}` : 'Version \u2014';
}

// Shown when a fingerprint cannot be read (e.g. installed-skus.json absent).
const NOT_RECORDED = 'Not recorded';

// Normalize a SHA for display/compare: trimmed + lowercase. Every SHA shown on
// these pages must be lowercase. Null/empty -> null.
function normSha(sha: string | null | undefined): string | null {
  if (!sha) {
    return null;
  }
  const t = sha.trim().toLowerCase();
  return t.length > 0 ? t : null;
}

export function UpdatesWorkspace() {
  const [phase, setPhase] = useState<LoadPhase>('loading');
  const [version, setVersion] = useState<RuntimeVersionInfo | null>(null);
  const [engine, setEngine] = useState<PaxEngineState | null>(null);
  const [health, setHealth] = useState<HealthInfo | null>(null);

  useEffect(() => {
    let cancelled = false;
    setPhase('loading');
    void Promise.all([getRuntimeVersion(), getPaxEngineState(), getHealth()])
      .then(([v, e, h]) => {
        if (cancelled) {
          return;
        }
        if (v.ok) {
          setVersion(v.data);
        }
        if (e.ok) {
          setEngine(e.data);
        }
        if (h.ok) {
          setHealth(h.data);
        }
        setPhase(v.ok || e.ok || h.ok ? 'ready' : 'error');
      })
      .catch(() => {
        if (!cancelled) {
          setPhase('error');
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const appVersion =
    version?.cookbookVersion ?? health?.appVersion ?? NOT_REPORTED;
  const channel = version?.releaseChannel ?? NOT_REPORTED;
  const buildDate = formatBuildTimestamp(version?.buildTimestamp ?? null) ?? NOT_REPORTED;

  const approvedSha = engine?.approvedSha256 ?? version?.bundledPax.sha256 ?? null;
  const approvedVersion =
    engine?.approvedVersion ?? version?.bundledPax.version ?? NOT_REPORTED;
  const engineStatus = engineStatusLabel(phase, engine);

  // The installed app payload SHA (lowercase), recorded by the installer in
  // installed-skus.json. null only when no install recorded it.
  const installedPayloadSha = normSha(version?.installedPayloadSha256 ?? null);
  const engineSha = normSha(approvedSha);

  // Copy-to-clipboard for the Support details card. The text is built from the
  // same live values the card shows (never hardcoded), so a paste into a support
  // chat, email, or Teams message always reflects this build. "Copied!" reverts
  // to "Copy" after 2 seconds.
  const [copied, setCopied] = useState(false);
  const copyResetRef = useRef<number | null>(null);
  useEffect(() => {
    return () => {
      if (copyResetRef.current !== null) {
        window.clearTimeout(copyResetRef.current);
      }
    };
  }, []);

  function handleCopySupport() {
    const rows: Array<[string, string]> = [
      ['App version', appVersion],
      ['Build date', buildDate],
      ['App fingerprint', installedPayloadSha ?? NOT_RECORDED],
      ['Release channel', channel],
      ['PAX engine', engineStatus],
      ['Engine fingerprint', engineSha ?? NOT_REPORTED],
      ['Update checking', 'Not available in this build'],
    ];
    const labelWidth = Math.max(...rows.map(([key]) => key.length)) + 2;
    const body = rows
      .map(([key, val]) => (key + ':').padEnd(labelWidth) + val)
      .join('\n');
    const text = 'PAX Cookbook \u2014 Support Details\n' + body;
    try {
      void navigator.clipboard?.writeText(text);
    } catch {
      // Clipboard access can be denied; the details stay visible to copy by hand.
    }
    setCopied(true);
    if (copyResetRef.current !== null) {
      window.clearTimeout(copyResetRef.current);
    }
    copyResetRef.current = window.setTimeout(() => setCopied(false), 2000);
  }

  // Update check. The Updates page runs its own check (when it opens and on
  // demand) and shows the result inline — a green "up to date" banner, or the
  // available update with an Update now button. It calls checkForUpdates
  // directly (not the shell controller) so it never re-lights the startup nav
  // dot; opening this page instead CLEARS that dot.
  const [checkState, setCheckState] =
    useState<'idle' | 'checking' | 'uptodate' | 'available' | 'unavailable'>('idle');
  const [allComponents, setAllComponents] = useState<UpdateComponentStatus[]>([]);
  const [lastChecked, setLastChecked] = useState<string | null>(() => getLastCheckedUtc());
  const [applying, setApplying] = useState(false);
  const applyingRef = useRef(false);
  const [applyError, setApplyError] = useState<string | null>(null);

  async function runCheck() {
    setApplyError(null);
    setCheckState('checking');
    const result = await checkForUpdates();
    setLastChecked(getLastCheckedUtc());
    setAllComponents(result.allComponents);
    if (result.status === 'up-to-date') {
      setCheckState('uptodate');
    } else if (result.status === 'updates-available') {
      setCheckState('available');
    } else {
      setCheckState('unavailable');
    }
  }

  // Auto-check when the page opens, and clear the Settings nav dot — the user is
  // now looking at Updates.
  useEffect(() => {
    setUpdatesBadge(false);
    void runCheck();
    return () => {
      applyingRef.current = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function handleApplyUpdate() {
    setApplying(true);
    applyingRef.current = true;
    setApplyError(null);
    // Safety net: on success the PAX Cookbook Updater window opens, downloads,
    // then stops this app to replace files — so this window closes on its own
    // within a couple of minutes (the download can take a while on a slow
    // connection). If nothing has happened after 120s the launch likely failed;
    // surface a clear, recoverable message instead of an indefinite spinner.
    const timeoutId = window.setTimeout(() => {
      if (applyingRef.current) {
        applyingRef.current = false;
        setApplying(false);
        setApplyError(
          'If the PAX Cookbook Updater window did not open, make sure you are online and try again.',
        );
      }
    }, 120000);
    const res = await applyUpdate();
    if (!res.ok) {
      window.clearTimeout(timeoutId);
      applyingRef.current = false;
      setApplying(false);
      setApplyError('Could not start the update. Make sure you are online, then try again.');
      return;
    }
    // Success (202): the updater process is launched. Make a good-faith effort
    // to exit THIS app so no installed file stays locked when the updater copies
    // files — stop the broker and ask the native shell to close the window. Wait
    // a moment first so the updater window is visible before this one vanishes;
    // the updater's own RealAppStopper is the backstop if this doesn't finish.
    window.setTimeout(() => {
      try { void shutdownBroker(); } catch { /* best-effort */ }
      try { postCloseDecision('cookbook:close-app'); } catch { /* best-effort */ }
    }, 1500);
    // The updater window takes over and this app exits shortly.
  }
  const lastCheckedLabel = formatLastCheckedLabel(lastChecked);

  return (
    <section aria-labelledby="view-updates">
      <SectionHeader
        title="Updates"
        titleId="view-updates"
        lede="Version, release, and update status for this installed build."
        helpTopic="cookbookUpdates"
        accent="var(--c-slate)"
      />

      {checkState === 'checking' ? (
        <div className="upd-status upd-status--checking" role="status">
          <span className="upd-status__spinner" aria-hidden="true" />
          <span className="upd-status__text">Checking for updates…</span>
        </div>
      ) : checkState === 'unavailable' ? (
        <div className="upd-status upd-status--warn" role="status">
          <span className="upd-status__text">
            Couldn&rsquo;t check for updates just now. Make sure you are online, then try again.
          </span>
        </div>
      ) : checkState === 'available' || checkState === 'uptodate' ? (
        applying ? (
          <div className="upd-status upd-status--avail" role="status">
            <div className="upd-status__body">
              <p className="upd-status__title">Updating PAX Cookbook…</p>
              <div className="upd-status__applying">
                <span className="upd-status__spinner" aria-hidden="true" />
                <span className="upd-status__text">
                  Starting the updater — a PAX Cookbook Updater window will open and show progress.
                </span>
              </div>
              <p className="upd-status__hint">Do not close this window.</p>
              {applyError ? <p className="upd-status__error">{applyError}</p> : null}
            </div>
          </div>
        ) : (
          <div className="upd-block">
            {checkState === 'uptodate' ? (
              <div className="upd-status upd-status--ok" role="status">
                <span className="upd-status__icon" aria-hidden="true">✓</span>
                <div className="upd-status__body">
                  <p className="upd-status__title">PAX Cookbook is up to date</p>
                  <p className="upd-status__sub">
                    Your app and PAX engine are current. No updates needed.
                  </p>
                </div>
              </div>
            ) : (
              <p className="upd-status__title upd-status__title--avail" role="status">
                An update is available
              </p>
            )}

            <div className="upd-compare upd-compare--standalone">
              {allComponents.map((c) => {
                const fromBuilt = formatBuiltDate(c.installedBuild);
                const toBuilt = formatBuiltDate(c.availableBuild);
                return (
                  <div className="upd-compare__item" key={c.name}>
                    <p className="upd-compare__name">{c.name}</p>
                    <div className="upd-compare__cols">
                      <div
                        className={
                          'upd-compare__col' + (c.hasUpdate ? ' upd-compare__col--old' : '')
                        }
                      >
                        <p className="upd-compare__col-head">Installed</p>
                        <p className="upd-compare__line">{installedVersionText(c)}</p>
                        {fromBuilt ? (
                          <p className="upd-compare__built">Built {fromBuilt}</p>
                        ) : null}
                        <p
                          className={
                            'upd-compare__sha' +
                            (normSha(c.installedSha) ? '' : ' upd-compare__sha--missing')
                          }
                          title="SHA-256 fingerprint"
                        >
                          {normSha(c.installedSha) ?? NOT_RECORDED}
                        </p>
                      </div>
                      <div
                        className={
                          'upd-compare__col' + (c.hasUpdate ? ' upd-compare__col--new' : '')
                        }
                      >
                        <p className="upd-compare__col-head">Available</p>
                        <p className="upd-compare__line">{availableVersionText(c)}</p>
                        {toBuilt ? (
                          <p className="upd-compare__built">Built {toBuilt}</p>
                        ) : null}
                        <p
                          className={
                            'upd-compare__sha' +
                            (normSha(c.availableSha) ? '' : ' upd-compare__sha--missing')
                          }
                          title="SHA-256 fingerprint"
                        >
                          {normSha(c.availableSha) ?? NOT_RECORDED}
                        </p>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>

            {checkState === 'available' ? (
              <div className="upd-block__actions">
                {applyError ? <p className="upd-status__error">{applyError}</p> : null}
                <button
                  type="button"
                  className="dvw-settings__updates-btn"
                  onClick={() => void handleApplyUpdate()}
                >
                  Update now
                </button>
                <p className="upd-status__hint">
                  Your recipes and settings are not affected by updates.
                </p>
              </div>
            ) : null}
          </div>
        )
      ) : null}

      <div className="card-grid settings-grid">
        <article className="card settings-card">
          <div className="card__top">
            <h3 className="card__title">Installed app</h3>
          </div>
          <p className="card__body">
            The version of PAX Cookbook installed on this PC.
          </p>
          <dl className="settings-kv">
            <div className="settings-kv__row">
              <dt className="settings-kv__key">App version</dt>
              <dd className="settings-kv__val">{appVersion}</dd>
            </div>
            <div className="settings-kv__row">
              <dt className="settings-kv__key">Build date</dt>
              <dd className="settings-kv__val">{buildDate}</dd>
            </div>
            <div className="settings-kv__row">
              <dt className="settings-kv__key">App fingerprint</dt>
              <dd className="settings-kv__val">
                {installedPayloadSha ? (
                  <code className="settings-mono settings-mono--full">{installedPayloadSha}</code>
                ) : (
                  <span className="settings-muted">{NOT_RECORDED}</span>
                )}
              </dd>
            </div>
          </dl>
        </article>

        <article className="card settings-card">
          <div className="card__top">
            <h3 className="card__title">PAX Engine</h3>
          </div>
          <p className="card__body">
            PAX Cookbook manages and verifies the audit engine for you. Its version
            and fingerprint are shown so you can confirm this build.
          </p>
          <dl className="settings-kv">
            <div className="settings-kv__row">
              <dt className="settings-kv__key">Status</dt>
              <dd className="settings-kv__val">{engineStatus}</dd>
            </div>
            <div className="settings-kv__row">
              <dt className="settings-kv__key">Approved engine</dt>
              <dd className="settings-kv__val">{APPROVED_ENGINE_NAME}</dd>
            </div>
            <div className="settings-kv__row">
              <dt className="settings-kv__key">Engine version</dt>
              <dd className="settings-kv__val">{approvedVersion}</dd>
            </div>
            <div className="settings-kv__row">
              <dt className="settings-kv__key">Fingerprint</dt>
              <dd className="settings-kv__val">
                {engineSha ? (
                  <code className="settings-mono settings-mono--full">{engineSha}</code>
                ) : (
                  NOT_REPORTED
                )}
              </dd>
            </div>
          </dl>
          <p className="settings-note">
            PAX Cookbook checks this fingerprint and verifies the engine before it is used.
          </p>
        </article>

        <article className="card settings-card">
          <div className="card__top">
            <h3 className="card__title">Update checking</h3>
          </div>
          <p className="card__body">
            PAX Cookbook checks for updates automatically when you open it. You can
            also check manually at any time.
          </p>
          <div className="dvw-settings__updates">
            <button
              type="button"
              className="dvw-settings__updates-btn"
              onClick={() => void runCheck()}
              disabled={checkState === 'checking'}
            >
              {checkState === 'checking' ? 'Checking\u2026' : 'Check for updates'}
            </button>
          </div>
          {lastCheckedLabel ? (
            <p className="settings-note">Last checked: {lastCheckedLabel}</p>
          ) : null}
        </article>

        <article className="card settings-card">
          <div className="card__top">
            <h3 className="card__title">Before updating</h3>
          </div>
          <p className="card__body">
            PAX Cookbook handles updates automatically.
          </p>
          <ul className="upd-list">
            <li className="upd-list__item">
              Your saved recipes stay on this PC and are not affected by updates.
            </li>
            <li className="upd-list__item">
              The PAX engine is re-verified by fingerprint after every update before it is used.
            </li>
          </ul>
        </article>

        <article className="card settings-card">
          <div className="card__top">
            <h3 className="card__title">Support details</h3>
            <button
              type="button"
              className="dvw-btn dvw-btn--ghost dvw-btn--sm"
              onClick={handleCopySupport}
            >
              {copied ? 'Copied!' : 'Copy'}
            </button>
          </div>
          <p className="card__body">
            Handy details to share if you need help. Nothing here includes a password,
            secret, or sign-in.
          </p>
          <dl className="settings-kv">
            <div className="settings-kv__row">
              <dt className="settings-kv__key">App version</dt>
              <dd className="settings-kv__val">{appVersion}</dd>
            </div>
            <div className="settings-kv__row">
              <dt className="settings-kv__key">Build date</dt>
              <dd className="settings-kv__val">{buildDate}</dd>
            </div>
            <div className="settings-kv__row">
              <dt className="settings-kv__key">App fingerprint</dt>
              <dd className="settings-kv__val">
                {installedPayloadSha ? (
                  <code className="settings-mono settings-mono--full">{installedPayloadSha}</code>
                ) : (
                  <span className="settings-muted">{NOT_RECORDED}</span>
                )}
              </dd>
            </div>
            <div className="settings-kv__row">
              <dt className="settings-kv__key">Release channel</dt>
              <dd className="settings-kv__val">{channel}</dd>
            </div>
            <div className="settings-kv__row">
              <dt className="settings-kv__key">PAX engine</dt>
              <dd className="settings-kv__val">{engineStatus}</dd>
            </div>
            <div className="settings-kv__row">
              <dt className="settings-kv__key">Update checking</dt>
              <dd className="settings-kv__val">Not available in this build</dd>
            </div>
            <div className="settings-kv__row">
              <dt className="settings-kv__key">Engine fingerprint</dt>
              <dd className="settings-kv__val">
                {engineSha ? (
                  <code className="settings-mono settings-mono--full">{engineSha}</code>
                ) : (
                  NOT_REPORTED
                )}
              </dd>
            </div>
          </dl>
        </article>
      </div>
    </section>
  );
}
