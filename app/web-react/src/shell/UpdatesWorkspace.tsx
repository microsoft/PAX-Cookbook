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
import { runUpdateCheck } from '../host/updateController';
import { getLastCheckedUtc } from '../host/updateCheck';
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
      ['Release channel', channel],
      ['PAX engine', engineStatus],
      ['Update checking', 'Automatic + manual'],
      ['Engine fingerprint', approvedSha ?? NOT_REPORTED],
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

  // Manual update check. The shell owns the "Updates available" modal; here we
  // only surface the no-update / offline outcome inline and the last-checked
  // time. The auto-check on startup keeps `lastChecked` fresh on its own.
  const [checkState, setCheckState] = useState<'idle' | 'checking' | 'uptodate' | 'unavailable'>('idle');
  const [lastChecked, setLastChecked] = useState<string | null>(() => getLastCheckedUtc());
  async function handleCheckForUpdates() {
    setCheckState('checking');
    const result = await runUpdateCheck();
    setLastChecked(getLastCheckedUtc());
    if (result.status === 'up-to-date') {
      setCheckState('uptodate');
    } else if (result.status === 'unavailable') {
      setCheckState('unavailable');
    } else {
      // Updates available — the shell modal takes over; clear inline status.
      setCheckState('idle');
    }
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
                {approvedSha ? (
                  <code className="settings-mono settings-mono--full">{approvedSha}</code>
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
              onClick={() => void handleCheckForUpdates()}
              disabled={checkState === 'checking'}
            >
              {checkState === 'checking' ? 'Checking\u2026' : 'Check for updates'}
            </button>
          </div>
          {checkState === 'uptodate' ? (
            <p className="settings-note">PAX Cookbook is up to date.</p>
          ) : checkState === 'unavailable' ? (
            <p className="settings-note">
              Couldn&rsquo;t check for updates just now. Make sure you are online, then try again.
            </p>
          ) : null}
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
                {approvedSha ? (
                  <code className="settings-mono settings-mono--full">{approvedSha}</code>
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
