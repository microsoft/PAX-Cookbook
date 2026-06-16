/**
 * Settings workspace.
 *
 * A real, customer-ready Settings surface laid out as a compact dashboard: the
 * App information and Security sections each read as a row of at-a-glance status
 * cards, above the Notifications form and a compact About footer. The status
 * cards reflect existing broker payloads (runtime/version, PAX engine
 * acquisition, health, app-lock and Windows Hello status); none of them change a
 * setting, run PAX, cook, bake, schedule, acquire, or show a secret.
 *
 * The Notifications section (CK-4) is the one interactive part: it configures
 * opt-in Telegram notifications (bot token + chat id stored by the broker in the
 * per-user Windows Credential Manager vault). The bot token is write-only - it
 * is sent only on save and is never returned (the surface shows only whether a
 * token is configured). No tenant data is ever shown or transmitted.
 */
import { useEffect, useState } from 'react';
import { SectionHeader } from './components/SectionHeader';
import { ContextualHelpButton } from '../components/ContextualHelpButton';
import { StatusCard } from './StatusCard';
import { openShellHelp, requestShellSection } from './shellNav';
import { CopyButton } from '../features/mini-kitchen/components/CopyButton';
import {
  getRuntimeVersion,
  getPaxEngineState,
  getHealth,
  getLockState,
  getSignInProtection,
  type RuntimeVersionInfo,
  type PaxEngineState,
  type HealthInfo,
  type LockStateInfo,
  type SignInProtectionInfo,
} from '../host/systemInfo';
import {
  getNotificationSettings,
  saveNotificationSettings,
  sendTestNotification,
  resolveChatId,
  type NotificationSettings,
  type NotificationSaveRequest,
} from '../host/notifications';
import { getAutostartEnabled, setAutostartEnabled } from '../host/brokerBridge';

type LoadPhase = 'loading' | 'ready' | 'error';

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

// Read-only sign-in/lock status helpers, mirrored from Chef's Keys so the
// Security section reads identically. Status text only - never a secret,
// token, challenge, or credential value (constraint 14).
function lockStatusLabel(phase: LoadPhase, lock: LockStateInfo | null): string {
  if (phase === 'loading') {
    return 'Checking…';
  }
  if (!lock || !lock.state) {
    return NOT_REPORTED;
  }
  return lock.locked ? 'Locked' : 'Unlocked';
}

function passkeyStatusLabel(
  phase: LoadPhase,
  protect: SignInProtectionInfo | null,
): string {
  if (phase === 'loading') {
    return 'Checking…';
  }
  if (!protect) {
    return NOT_REPORTED;
  }
  return protect.passkeyRegistered ? 'Set up on this PC' : 'Not set up yet';
}

type StatusKind = 'ok' | 'err';

// CK-4 — interactive Telegram notification setup. The bot token is write-only:
// it is sent only on save and never read back (the card shows only whether a
// token is configured). Constraint 14 is preserved end to end.
function NotificationsCard() {
  const [phase, setPhase] = useState<LoadPhase>('loading');
  const [settings, setSettings] = useState<NotificationSettings | null>(null);
  const [enabled, setEnabled] = useState(false);
  const [chatId, setChatId] = useState('');
  const [botToken, setBotToken] = useState('');
  const [saving, setSaving] = useState(false);
  const [busyChatId, setBusyChatId] = useState(false);
  const [busyTest, setBusyTest] = useState(false);
  const [statusMsg, setStatusMsg] = useState<string | null>(null);
  const [statusKind, setStatusKind] = useState<StatusKind | null>(null);

  function applySettings(s: NotificationSettings) {
    setSettings(s);
    setEnabled(s.enabled);
    setChatId(s.chatId ?? '');
    setBotToken('');
  }

  useEffect(() => {
    let cancelled = false;
    setPhase('loading');
    void getNotificationSettings()
      .then((r) => {
        if (cancelled) {
          return;
        }
        if (r.ok && r.data) {
          applySettings(r.data);
          setPhase('ready');
        } else {
          setPhase('error');
        }
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

  function setStatus(kind: StatusKind, msg: string) {
    setStatusKind(kind);
    setStatusMsg(msg);
  }

  async function onSave() {
    setSaving(true);
    setStatusMsg(null);
    const body: NotificationSaveRequest = { enabled, provider: 'telegram' };
    const trimmedChat = chatId.trim();
    if (trimmedChat.length > 0) {
      body.chatId = trimmedChat;
    }
    if (botToken.length > 0) {
      body.botToken = botToken;
    }
    const r = await saveNotificationSettings(body);
    setSaving(false);
    if (r.ok && r.data) {
      applySettings(r.data);
      setStatus('ok', 'Notification settings saved.');
    } else {
      setStatus('err', r.message ?? 'The notification settings could not be saved.');
    }
  }

  async function onResolveChatId() {
    setBusyChatId(true);
    setStatusMsg(null);
    const r = await resolveChatId();
    setBusyChatId(false);
    if (r.ok && r.data?.found && r.data.chatId) {
      setChatId(String(r.data.chatId));
      setStatus('ok', 'Found your chat id. Choose Save to keep it.');
    } else if (r.ok && r.data && !r.data.found) {
      setStatus('err', r.data.message ?? 'No recent message was found. Message your bot, then try again.');
    } else {
      setStatus('err', r.message ?? 'Could not reach Telegram. Save your bot token first, then try again.');
    }
  }

  async function onSendTest() {
    setBusyTest(true);
    setStatusMsg(null);
    const r = await sendTestNotification();
    setBusyTest(false);
    if (r.ok && r.data?.ok) {
      setStatus('ok', r.data.message ?? 'Test notification sent.');
    } else {
      setStatus('err', r.data?.message ?? r.message ?? 'The test notification could not be sent.');
    }
  }

  const tokenPlaceholder = settings?.tokenSet
    ? 'A bot token is saved — leave blank to keep it'
    : 'Paste the token from BotFather';

  return (
    <section className="dvw-settings__section">
      <div className="dvw-settings__head">
        <h3 className="dvw-keys__section-head">Notifications</h3>
        {phase !== 'loading' ? (
          <span
            className={
              settings?.tokenSet ? 'chip chip--local' : 'chip chip--muted'
            }
          >
            {settings?.tokenSet ? 'Configured' : 'Not configured'}
          </span>
        ) : null}
      </div>
      <p className="dvw-settings__desc">
        Get a Telegram message when a bake finishes or fails, and receive the sign-in
        code when a bake needs a Device Code. Notifications use your own Telegram bot
        and stay off until you turn them on. Only bake details — recipe name, status,
        duration, and the output file location — are sent. Your tenant data is never
        included.
      </p>

      {phase === 'loading' ? (
        <p className="settings-note">Loading…</p>
      ) : (
        <>
          <label className="settings-toggle">
            <input
              type="checkbox"
              checked={enabled}
              onChange={(e) => setEnabled(e.target.checked)}
            />
            <span>Send notifications</span>
          </label>

          <ol className="settings-steps">
            <li>
              In Telegram, message <strong>@BotFather</strong>, send <code>/newbot</code>,
              and follow the prompts to create a bot.
            </li>
            <li>Copy the bot token BotFather gives you and paste it below.</li>
            <li>
              Open your new bot in Telegram and send it any message, then choose{' '}
              <strong>Get my Chat ID</strong>.
            </li>
            <li>Send a test, then turn notifications on.</li>
          </ol>

          <div className="settings-field">
            <label className="settings-field__label" htmlFor="tg-token">
              Bot token
            </label>
            <input
              id="tg-token"
              type="password"
              className="settings-input"
              value={botToken}
              placeholder={tokenPlaceholder}
              autoComplete="off"
              spellCheck={false}
              onChange={(e) => setBotToken(e.target.value)}
            />
            <p className="settings-note">
              {settings?.tokenSet
                ? 'Stored securely in Windows Credential Manager. Leave blank to keep the saved token.'
                : 'Stored securely in Windows Credential Manager when you save. It is never shown again.'}
            </p>
          </div>

          <div className="settings-field">
            <label className="settings-field__label" htmlFor="tg-chatid">
              Chat ID
            </label>
            <div className="settings-field__row">
              <input
                id="tg-chatid"
                type="text"
                className="settings-input"
                value={chatId}
                placeholder="e.g. 123456789"
                autoComplete="off"
                spellCheck={false}
                onChange={(e) => setChatId(e.target.value)}
              />
              <button
                type="button"
                className="dvw-btn"
                onClick={() => void onResolveChatId()}
                disabled={busyChatId}
              >
                {busyChatId ? 'Looking…' : 'Get my Chat ID'}
              </button>
            </div>
          </div>

          <div className="settings-actions">
            <button
              type="button"
              className="dvw-btn dvw-btn--primary"
              onClick={() => void onSave()}
              disabled={saving}
            >
              {saving ? 'Saving…' : 'Save'}
            </button>
            <button
              type="button"
              className="dvw-btn"
              onClick={() => void onSendTest()}
              disabled={busyTest}
            >
              {busyTest ? 'Sending…' : 'Send test notification'}
            </button>
          </div>

          {statusMsg ? (
            <p
              className={
                statusKind === 'ok'
                  ? 'settings-status settings-status--ok'
                  : 'settings-status settings-status--err'
              }
              role="status"
            >
              {statusMsg}
            </p>
          ) : null}

          {phase === 'error' ? (
            <p className="settings-status settings-status--err" role="status">
              Could not load notification settings.
            </p>
          ) : null}
        </>
      )}
    </section>
  );
}

// V2 two-process auto-start toggle. Controls the per-user HKCU Run value that
// launches the headless broker daemon at logon (read/written by the broker's
// /api/v1/settings/autostart routes). Turning it off removes the auto-start but
// does NOT stop the running broker this session; turning it on adds it back with
// no immediate effect (the app is already running). No secret is involved.
function StartupCard() {
  const [phase, setPhase] = useState<LoadPhase>('loading');
  const [enabled, setEnabled] = useState(true);
  const [saving, setSaving] = useState(false);
  const [statusMsg, setStatusMsg] = useState<string | null>(null);
  const [statusKind, setStatusKind] = useState<StatusKind | null>(null);

  useEffect(() => {
    let cancelled = false;
    setPhase('loading');
    void getAutostartEnabled()
      .then((r) => {
        if (cancelled) {
          return;
        }
        if (r.ok && r.data) {
          setEnabled(r.data.enabled);
          setPhase('ready');
        } else {
          setPhase('error');
        }
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

  async function handleToggle(next: boolean) {
    if (saving) {
      return;
    }
    const prev = enabled;
    setEnabled(next); // optimistic
    setSaving(true);
    setStatusMsg(null);
    setStatusKind(null);
    try {
      const r = await setAutostartEnabled(next);
      if (r.ok && r.data) {
        setEnabled(r.data.enabled);
        setStatusKind('ok');
        setStatusMsg(
          r.data.enabled
            ? 'PAX Cookbook will start at login.'
            : 'PAX Cookbook will not start at login.',
        );
      } else {
        setEnabled(prev); // revert
        setStatusKind('err');
        setStatusMsg('The startup setting could not be saved.');
      }
    } catch {
      setEnabled(prev); // revert
      setStatusKind('err');
      setStatusMsg('The startup setting could not be saved.');
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="dvw-settings__section">
      <div className="dvw-settings__head">
        <h3 className="dvw-keys__section-head">Startup</h3>
        <ContextualHelpButton topic="cookbookStartup" />
        {phase === 'ready' ? (
          <span className={enabled ? 'chip chip--local' : 'chip chip--muted'}>
            {enabled ? 'On' : 'Off'}
          </span>
        ) : null}
      </div>
      <p className="dvw-settings__desc">
        Controls whether PAX Cookbook runs in the background after you sign in to
        Windows so scheduled bakes can run on time.
      </p>

      {phase === 'loading' ? (
        <p className="settings-note">Loading…</p>
      ) : phase === 'error' ? (
        <p className="settings-status settings-status--err" role="status">
          The startup setting could not be read.
        </p>
      ) : (
        <>
          <label className="settings-toggle">
            <input
              type="checkbox"
              checked={enabled}
              disabled={saving}
              onChange={(e) => void handleToggle(e.target.checked)}
            />
            <span>Start PAX Cookbook at login</span>
          </label>
          <p className="settings-note">
            When enabled, PAX Cookbook runs in the background after you sign in to
            Windows, ready for scheduled bakes.
          </p>
          {statusMsg ? (
            <p
              className={
                statusKind === 'ok'
                  ? 'settings-status settings-status--ok'
                  : 'settings-status settings-status--err'
              }
              role="status"
            >
              {statusMsg}
            </p>
          ) : null}
        </>
      )}
    </section>
  );
}

export function SettingsWorkspace() {
  const [phase, setPhase] = useState<LoadPhase>('loading');
  const [version, setVersion] = useState<RuntimeVersionInfo | null>(null);
  const [engine, setEngine] = useState<PaxEngineState | null>(null);
  const [health, setHealth] = useState<HealthInfo | null>(null);
  const [lock, setLock] = useState<LockStateInfo | null>(null);
  const [protect, setProtect] = useState<SignInProtectionInfo | null>(null);

  useEffect(() => {
    let cancelled = false;
    setPhase('loading');
    void Promise.all([
      getRuntimeVersion(),
      getPaxEngineState(),
      getHealth(),
      getLockState(),
      getSignInProtection(),
    ])
      .then(([v, e, h, l, p]) => {
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
        if (l.ok) {
          setLock(l.data);
        }
        if (p.ok) {
          setProtect(p.data);
        }
        // The page is ready when any core read (version/engine/health) lands.
        // The lock/Hello reads are additive: if they fail, the Security rows
        // fall back to "Not reported" rather than failing the whole page.
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

  const approvedSha = engine?.approvedSha256 ?? version?.bundledPax.sha256 ?? null;
  const approvedVersion =
    engine?.approvedVersion ?? version?.bundledPax.version ?? NOT_REPORTED;
  const engineStatus = engineStatusLabel(phase, engine);

  const workspaceStatus =
    phase === 'loading'
      ? 'Checking…'
      : health
        ? health.workspaceReady
          ? 'Ready'
          : 'Needs attention'
        : NOT_REPORTED;

  const passkeyStatus = passkeyStatusLabel(phase, protect);
  const lockStatus = lockStatusLabel(phase, lock);
  const verification =
    protect?.userVerification && protect.userVerification.toLowerCase() === 'required'
      ? 'Windows Hello required'
      : protect?.userVerification ?? NOT_REPORTED;

  const channelDetail =
    channel !== NOT_REPORTED ? `${channel} channel` : 'Local-first build';
  const engineReady = engineStatus === 'Ready';
  const workspaceReady = workspaceStatus === 'Ready';
  const workspaceAttention = workspaceStatus === 'Needs attention';
  const passkeyReady = passkeyStatus === 'Set up on this PC';
  const verificationRequired = verification === 'Windows Hello required';
  const appUnlocked = lockStatus === 'Unlocked';

  return (
    <section aria-labelledby="view-settings">
      <SectionHeader
        title="Settings"
        titleId="view-settings"
        lede="App information, notifications, security, and the PAX engine for this app. These details are read-only unless noted."
        helpTopic="cookbookSettings"
        accent="var(--c-slate)"
      />

      <div className="dvw-settings">
        <section className="dvw-settings__section">
          <div className="dvw-settings__head">
            <h3 className="dvw-keys__section-head">App information</h3>
          </div>
          <div className="dvw-status-grid">
            <StatusCard
              title="App version"
              state={appVersion}
              detail={channelDetail}
              tone="neutral"
            />
            <StatusCard
              title="PAX engine"
              state={approvedVersion}
              detail={engineStatus}
              tone={engineReady ? 'ready' : 'neutral'}
              icon={engineReady ? 'check' : undefined}
            />
            <StatusCard
              title="Workspace"
              state={workspaceStatus}
              detail="Runs locally on this PC"
              tone={
                workspaceReady ? 'ready' : workspaceAttention ? 'attention' : 'unknown'
              }
              icon="folder"
            />
          </div>
          <div className="dvw-settings__updates">
            <span className="dvw-settings__updates-label">
              Keep PAX Cookbook current
            </span>
            <button
              type="button"
              className="dvw-settings__help-link"
              onClick={() => requestShellSection('updates')}
            >
              Check for updates
            </button>
          </div>
        </section>

        <StartupCard />

        <NotificationsCard />

        <section className="dvw-settings__section">
          <div className="dvw-settings__head">
            <h3 className="dvw-keys__section-head">Security</h3>
          </div>
          <div className="dvw-status-grid">
            <StatusCard
              title="Windows Hello"
              state={passkeyStatus}
              detail="Unlocks the app"
              tone={
                passkeyReady
                  ? 'ready'
                  : passkeyStatus === 'Not set up yet'
                    ? 'attention'
                    : 'unknown'
              }
              icon={passkeyReady ? 'check' : undefined}
            />
            <StatusCard
              title="Verification"
              state={verification}
              detail="Step-up for manual bakes"
              tone={verificationRequired ? 'ready' : 'neutral'}
            />
            <StatusCard
              title="App lock"
              state={lockStatus}
              detail="Current session"
              tone={appUnlocked ? 'ready' : 'neutral'}
              icon={appUnlocked ? 'check' : undefined}
            />
          </div>
          <p className="dvw-keys__sysnote">
            Status only — no secret, key, or token is shown.
          </p>
        </section>

        <section className="dvw-settings__section">
          <div className="dvw-settings__head">
            <h3 className="dvw-keys__section-head">About</h3>
          </div>
          <div className="dvw-settings__about">
            <div className="dvw-settings__brand">
              <span className="dvw-settings__brand-name">PAX Cookbook</span>
              <span className="dvw-settings__brand-tag">
                Local-first Microsoft Purview &amp; Copilot audit baking for Microsoft 365 admins.
              </span>
            </div>
            <div className="dvw-keys__props">
              <div className="dvw-keys__prop">
                <span className="dvw-keys__prop-label">Engine fingerprint (SHA-256)</span>
                <div className="dvw-keys__prop-value">
                  {approvedSha ? (
                    <>
                      <code className="dvw-keys__mono">{approvedSha}</code>
                      <CopyButton text={approvedSha} label="Copy engine fingerprint" />
                    </>
                  ) : (
                    <span className="dvw-keys__not-set">{NOT_REPORTED}</span>
                  )}
                </div>
              </div>
            </div>
            <div className="dvw-settings__help">
              <span className="dvw-settings__help-label">Help &amp; getting started</span>
              <button
                type="button"
                className="dvw-settings__help-link"
                onClick={openShellHelp}
              >
                Open the help panel
              </button>
            </div>
          </div>
        </section>
      </div>
    </section>
  );
}
