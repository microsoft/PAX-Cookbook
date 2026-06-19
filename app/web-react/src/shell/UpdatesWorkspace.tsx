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
      ['Update checking', 'Not available in this build'],
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
            <span className="chip chip--local">Internal build</span>
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
            <span className="chip chip--local">Managed by PAX Cookbook</span>
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
            <span className="chip chip--local">Internal testing</span>
          </div>
          <p className="card__body">
            This build doesn’t check for updates online. To move to a newer version,
            run the PAX Cookbook installer you were given.
          </p>
          <dl className="settings-kv">
            <div className="settings-kv__row">
              <dt className="settings-kv__key">Online update check</dt>
              <dd className="settings-kv__val">Not available in this build</dd>
            </div>
            <div className="settings-kv__row">
              <dt className="settings-kv__key">How to update</dt>
              <dd className="settings-kv__val">Run the installer you were given</dd>
            </div>
          </dl>
          <p className="settings-note">
            Your saved recipes stay on this PC when you install a newer build.
          </p>
        </article>

        <article className="card settings-card">
          <div className="card__top">
            <h3 className="card__title">Before updating</h3>
          </div>
          <p className="card__body">
            A couple of things worth knowing before you install a newer internal build.
          </p>
          <ul className="upd-list">
            <li className="upd-list__item">
              Close PAX Cookbook before you run the internal release package.
            </li>
            <li className="upd-list__item">
              Your saved recipes stay on this PC and are not removed by installing a newer build.
            </li>
            <li className="upd-list__item">
              The PAX engine is re-verified by fingerprint after an update before it is used.
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
