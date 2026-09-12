/**
 * Experimental / pilot-only Work-account Settings section.
 *
 * Renders NOTHING in a stable/default build: the live config-state route reports
 * `unavailable_in_this_build`, so this component returns null and makes no
 * further API call, leaving no layout gap, helper action, or registration
 * assumption in the built UI.
 *
 * In an experimental/pilot build it presents the Settings "Sign-in method"
 * section written for an office worker: the two mutually-exclusive sign-in
 * methods (Windows Hello and the work account) are shown as a plain,
 * hairline-separated list — one short description each, the active one marked
 * "Current method", and a single action on the inactive one. Exactly one method
 * is active at a time, never both, with no automatic fallback. The uncommon
 * organization setup/management actions live in a subordinate, collapsed
 * "For your IT team" area that keeps every existing handler and security gate.
 * The customer-facing copy never exposes the underlying architecture.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { CopyButton } from '../features/mini-kitchen/components/CopyButton';
import { usePolling } from '../host/usePolling';
import {
  getWamConfigState,
  getWamAdminDetails,
  importWamSetupResult,
  verifyWamSetup,
  setWamEnabled,
  removeWamLocalConfig,
  prepareWamDeprovision,
  executeWamDeprovision,
  getSessionProviderStatus,
  authorizeSessionWithExperimentalWam,
  selectWorkAccountProvider,
  selectWindowsHelloProvider,
  probePlatformAuthenticatorAvailable,
  requestWindowsHelloEnrollmentFromNative,
  requestBrokerLock,
  type WamConfigStateInfo,
  type WamAdminDetails,
  type WamDeprovisionPlan,
  type SessionProviderStatus,
} from '../host/experimentalWam';
import { isHelloDiagEnabled, runHelloCapabilityIframeProbe, isHelloAttendedDiagEnabled, runHelloAttendedIframeObserver } from '../host/helloCapabilityProbe';
import { subscribeSignInMethodReveal, currentSignInMethodRevealRequestId } from './signInMethodReveal';

interface ImportPreview {
  tenantId: string;
  clientId: string;
  rawJson: string;
}

function reasonText(reason: string | null): string {
  if (!reason) return '';
  const map: Record<string, string> = {
    not_configured: 'There is no configuration to act on.',
    invalid_json: 'The selected file is not a valid setup result.',
    unknown_field: 'The setup result contains unexpected fields and was rejected.',
    unsupported_result_kind: 'That file is not a Provision setup result.',
    stale_or_incomplete_verification: 'The setup result is stale or incomplete.',
    configuration_mismatch: 'The verified setup does not match the local configuration.',
    configuration_fingerprint_mismatch: 'The configuration changed; prepare the plan again.',
    consent_missing_or_revoked: 'Administrator approval is missing or was withdrawn.',
    helper_timeout: 'The setup helper timed out. Try again.',
    helper_cancelled: 'The setup helper was cancelled.',
    helper_failed: 'The setup helper reported a failure.',
    powershell_unavailable: 'PowerShell 7 was not found. Install it and try again.',
    helper_missing: 'The setup helper could not be located.',
    helper_integrity_failed: 'The setup helper failed its integrity check.',
    partial_deprovision: 'Cleanup was only partially completed. The local configuration was kept; try again.',
    invalid_or_replayed_plan: 'That deprovision plan is no longer valid. Prepare the plan again.',
    plan_expired: 'The deprovision plan expired. Prepare it again.',
    transport_failure: 'The request could not be completed. Try again.',
  };
  return map[reason] ?? 'The request could not be completed.';
}

// Office-worker message for a sign-in-method change that did NOT complete. No
// architecture term or reason-code identifier appears. `retained` is the method
// that stays selected, so a "nothing changed" outcome names it plainly.
function switchFailureMessage(
  reason: string | null,
  retained: 'windows_hello' | 'work_account',
): string {
  const retainedName = retained === 'windows_hello' ? 'Windows Hello' : 'Work account';
  const nothingChanged = `Nothing changed. ${retainedName} is still your sign-in method.`;
  switch (reason) {
    case 'cancelled':
      return nothingChanged;
    case 'timeout':
      return `That didn't finish in time. ${retainedName} is still your sign-in method.`;
    case 'windows_hello_platform_unavailable':
      return "Windows Hello isn't available right now. Check Windows Settings, then try again.";
    case 'windows_hello_registration_repair_required':
      return 'Windows Hello sign-in data for PAX Cookbook is damaged. Open PAX Cookbook Setup to repair it, then try again.';
    case 'windows_hello_enrollment_required':
      return 'To use Windows Hello, finish setting it up for PAX Cookbook, then try again.';
    case 'work_account_not_ready':
      return "Work account isn't ready yet. Your IT team can finish setting it up.";
    case 'work_account_auth_test_required':
      return `Work-account sign-in wasn't completed. ${retainedName} is still your sign-in method.`;
    default:
      return `Nothing changed. ${retainedName} is still your sign-in method.`;
  }
}

export function WorkAccountCard(): React.ReactElement | null {
  const [info, setInfo] = useState<WamConfigStateInfo | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [plan, setPlan] = useState<WamDeprovisionPlan | null>(null);
  const [deprovisionConfirmed, setDeprovisionConfirmed] = useState(false);
  const [details, setDetails] = useState<WamAdminDetails | null>(null);
  const fileRef = useRef<HTMLInputElement | null>(null);
  const busyRef = useRef(false);
  // The Sign-in method disclosure. A reveal intent from the TOP-LEVEL shell's
  // Work-account profile menu expands and focuses it (see the effect below).
  const signInDetailsRef = useRef<HTMLDetailsElement | null>(null);
  // Selected session provider (mutually-exclusive with the config state above).
  const [providerStatus, setProviderStatus] = useState<SessionProviderStatus | null>(null);
  const [switching, setSwitching] = useState(false);
  const [switchMessage, setSwitchMessage] = useState<string | null>(null);
  const [switchApplied, setSwitchApplied] = useState(false);
  // "Lock now to apply" feedback: `locking` disables the button and shows a
  // "Locking…" label while the request is in flight; `lockError` shows a plain,
  // truthful message if the lock did not take effect so the office worker can retry.
  const [locking, setLocking] = useState(false);
  const [lockError, setLockError] = useState<string | null>(null);
  // The subordinate "For your IT team" area is collapsed by default; the office
  // worker's "Set up work account" action opens it (setup is an IT task).
  const [itTeamOpen, setItTeamOpen] = useState(false);
  // Once the daemon reports the provider is unavailable in this build (every
  // stable/default build), stop probing entirely — a stable build makes at most
  // ONE bounded config-state GET and then does no further work.
  const stoppedRef = useRef(false);

  const refresh = useCallback(async () => {
    // Do not clobber the display mid-action, and never re-probe after the build
    // proved the provider unavailable.
    if (busyRef.current || stoppedRef.current) return;
    const next = await getWamConfigState();
    if (next.state === 'unavailable_in_this_build') {
      stoppedRef.current = true;
      setInfo(next);
      return;
    }
    setInfo(next);
    setProviderStatus(await getSessionProviderStatus());
  }, []);

  // Poll the live state. The interval stops automatically on unmount.
  usePolling(refresh, 2500);

  const runAction = useCallback(async (fn: () => Promise<{ ok: boolean; reason: string | null; state: WamConfigStateInfo }>) => {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy(true);
    setMessage(null);
    try {
      const r = await fn();
      setInfo(r.state);
      if (!r.ok) {
        setMessage(reasonText(r.reason));
      }
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  }, []);

  // --- Import: pick a file, preview non-secret facts, then save ---
  const onPickFile = useCallback(() => fileRef.current?.click(), []);

  const onFileChosen = useCallback(async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    setMessage(null);
    let text: string;
    try {
      text = await file.text();
    } catch {
      setMessage('The selected file could not be read.');
      return;
    }
    try {
      const parsed = JSON.parse(text) as Record<string, unknown>;
      const tenantId = typeof parsed.tenantId === 'string' ? parsed.tenantId : '';
      const clientId = typeof parsed.clientAppId === 'string' ? parsed.clientAppId : '';
      if (!tenantId || !clientId) {
        setMessage('The selected file is not a valid setup result.');
        return;
      }
      setPreview({ tenantId, clientId, rawJson: text });
    } catch {
      setMessage('The selected file is not a valid setup result.');
    }
  }, []);

  const confirmImport = useCallback(async () => {
    if (!preview) return;
    const json = preview.rawJson;
    setPreview(null);
    await runAction(() => importWamSetupResult(json));
  }, [preview, runAction]);

  // --- Deprovision: prepare a plan, then execute after explicit confirm ---
  const startDeprovision = useCallback(async () => {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy(true);
    setMessage(null);
    setDeprovisionConfirmed(false);
    try {
      const p = await prepareWamDeprovision();
      if (!p.ok || !p.planId) {
        setMessage(reasonText(p.reason));
        setPlan(null);
      } else {
        setPlan(p);
      }
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  }, []);

  const confirmDeprovision = useCallback(async () => {
    if (!plan?.planId) return;
    const id = plan.planId;
    setPlan(null);
    setDeprovisionConfirmed(false);
    await runAction(() => executeWamDeprovision(id));
  }, [plan, runAction]);

  const revealDetails = useCallback(async () => {
    if (details) return;
    const d = await getWamAdminDetails();
    setDetails(d);
  }, [details]);

  // --- Provider switch: choose the single selected sign-in method ---
  // Windows Hello -> work account requires a successful NATIVE work-account
  // authentication test in this session before the selection is persisted.
  const switchToWorkAccount = useCallback(async () => {
    if (busyRef.current || switching) return;
    setSwitching(true);
    setSwitchMessage(null);
    setSwitchApplied(false);
    try {
      const outcome = await authorizeSessionWithExperimentalWam();
      if (outcome !== 'approved') {
        setSwitchMessage("Work-account sign-in wasn't completed. Windows Hello is still your sign-in method.");
        return;
      }
      const r = await selectWorkAccountProvider();
      if (!r.ok) {
        setSwitchMessage(switchFailureMessage(r.reason, 'windows_hello'));
        return;
      }
      setSwitchApplied(true);
      setSwitchMessage('Work account is now your sign-in method. Lock or restart PAX Cookbook to start using it.');
      setProviderStatus(await getSessionProviderStatus());
    } finally {
      setSwitching(false);
    }
  }, [switching]);

  // Work account -> Windows Hello. The current session authorizes the switch,
  // but the daemon decides with two SEPARATE predicates: this renderer's
  // platform-availability probe and the daemon's own local-registration
  // integrity check. Register-before-switch: if Windows Hello is available but
  // not yet registered for PAX Cookbook, we reuse the parent-shell WebAuthn
  // enrollment ceremony FIRST and only re-select after it succeeds. The work
  // account stays selected on every non-success path — no partial switch, no
  // automatic fallback.
  const switchToWindowsHello = useCallback(async () => {
    if (busyRef.current || switching) return;
    setSwitching(true);
    setSwitchMessage(null);
    setSwitchApplied(false);
    try {
      const platformAvailable = await probePlatformAuthenticatorAvailable();
      let r = await selectWindowsHelloProvider(platformAvailable);

      // (b) Available but not registered: reuse the ceremony, then re-select.
      if (!r.ok && r.reason === 'windows_hello_enrollment_required') {
        const outcome = await requestWindowsHelloEnrollmentFromNative();
        if (outcome !== 'enrolled') {
          // Cancel/timeout/failure/unavailable/transport: leave work_account
          // selected. Map to a truthful bounded reason without persisting.
          setSwitchMessage(
            switchFailureMessage(
              outcome === 'unavailable'
                ? 'windows_hello_platform_unavailable'
                : outcome === 'failed'
                  ? 'windows_hello_enrollment_required'
                  : outcome,
              'work_account',
            ),
          );
          return;
        }
        // Enrolled: re-probe (a fresh valid registration now exists) and switch.
        const nowAvailable = await probePlatformAuthenticatorAvailable();
        r = await selectWindowsHelloProvider(nowAvailable);
      }

      if (!r.ok) {
        // (c) platform unavailable, (d) registration repair required, or any
        // other bounded reason: the daemon left work_account selected.
        setSwitchMessage(switchFailureMessage(r.reason, 'work_account'));
        return;
      }

      setSwitchApplied(true);
      setSwitchMessage('Windows Hello is now your sign-in method. Lock or restart PAX Cookbook to start using it.');
      setProviderStatus(await getSessionProviderStatus());
    } finally {
      setSwitching(false);
    }
  }, [switching]);

  // PHASE-1 Hello capability diagnostic auto-drive (cycle-01r-hello-capability-
  // probe-repair). MEASUREMENT ONLY, isolated-build + PAXCB_HELLO_DIAG=1 gated.
  // Fires at most once, only when the native host injected the read-only marker
  // AND the work account is the current provider (the exact failing precondition).
  // It drives the REAL switchToWindowsHello handler — the shell auto-cancels the
  // enroll affordance, so no credential ceremony/gesture is exercised — then
  // reports bounded observations to the shell. In every normal/stable build the
  // marker is absent, so this effect is inert.
  const helloDiagRanRef = useRef(false);
  useEffect(() => {
    if (helloDiagRanRef.current) return;
    if (!isHelloDiagEnabled()) return;
    if (providerStatus?.selectedProvider !== 'work_account') return;
    if (busyRef.current || switching) return;
    helloDiagRanRef.current = true;
    void runHelloCapabilityIframeProbe(() => switchToWindowsHello());
  }, [providerStatus, switching, switchToWindowsHello]);

  // Apply a just-made sign-in-method change by locking the session: the switch
  // takes effect on the next Locked -> Unlocked cycle, so this drives that cycle
  // now. The button gives immediate feedback (disabled + "Locking…"). On success
  // the daemon locks and the top-level shell presents the lock screen promptly
  // (requestBrokerLock nudges the shell to re-verify the lock state); the button
  // stays in its "Locking…" state so it can't be pressed twice while the overlay
  // mounts. On failure the affordance is restored with a truthful message.
  const applyByLocking = useCallback(async () => {
    if (locking) return;
    setLocking(true);
    setLockError(null);
    const ok = await requestBrokerLock();
    if (!ok) {
      setLocking(false);
      setLockError("PAX Cookbook couldn't lock just now. Please try again.");
      return;
    }
    // Success: keep `locking` set so the button remains disabled while the shell
    // mounts the lock screen over this content.
  }, [locking]);

  // PHASE-2 attended Hello capability diagnostic (cycle-01r). MEASUREMENT ONLY.
  // The "Use Windows Hello" click handler. In every normal/stable build (and any
  // launch without the isolated attended marker) this is exactly the prior
  // behavior: run the REAL `switchToWindowsHello`. In an isolated ATTENDED launch
  // it instead runs that SAME handler wrapped by the observer, which records
  // bounded post-run facts and posts them to the shell — the switch behavior is
  // identical, only observed. No auto-drive: the operator's own click triggers it.
  const onUseWindowsHelloClick = useCallback(() => {
    if (isHelloAttendedDiagEnabled()) {
      void runHelloAttendedIframeObserver(() => switchToWindowsHello());
      return;
    }
    void switchToWindowsHello();
  }, [switchToWindowsHello]);

  // "Set up work account" for the office worker: reveal the subordinate IT area,
  // where an administrator completes the organization setup.
  const openItTeam = useCallback(() => setItTeamOpen(true), []);

  // Honor a "Sign-in method" reveal intent from the TOP-LEVEL shell's
  // Work-account profile menu: expand the disclosure, bring it into view, and
  // focus its summary. Only ever fires in an experimental build where this
  // component mounts; a stable build returns null before this section renders,
  // so there is nothing to reveal.
  //
  // The App reveal handler bumps a navKey that REMOUNTS this card, and the
  // disclosure only renders after an async config load. So the intent must
  // survive both the remount and the render latency. We track the highest
  // reveal-request id we have already handled and reveal at most once per id.
  // `revealNow` runs on every new request AND whenever the disclosure first
  // renders (keyed on `info`), so a request that arrives before this card
  // mounts — or before its section renders — is still honored by exactly the
  // currently-mounted card, deterministically and without timers.
  const lastHandledRevealIdRef = useRef(0);
  const revealNow = useCallback(() => {
    const requestId = currentSignInMethodRevealRequestId();
    if (requestId === 0 || requestId <= lastHandledRevealIdRef.current) return;
    const el = signInDetailsRef.current;
    if (!el) return;
    lastHandledRevealIdRef.current = requestId;
    el.open = true;
    try {
      el.scrollIntoView({ block: 'start' });
    } catch {
      /* scrollIntoView options unsupported — non-fatal */
    }
    const summary = el.querySelector('summary');
    if (summary instanceof HTMLElement) {
      summary.focus();
    }
  }, []);
  useEffect(() => subscribeSignInMethodReveal(revealNow), [revealNow]);
  useEffect(() => {
    revealNow();
  }, [info, revealNow]);

  if (!info || info.state === 'unavailable_in_this_build') {
    return null;
  }

  const s = info.state;
  const showConfigure = s === 'not_configured' || s === 'administrator_setup_required' || s === 'invalid_configuration';
  const showImport = s === 'not_configured' || s === 'administrator_setup_required';
  const showVerify = s === 'configured_unverified' || s === 'ready' || s === 'disabled' || s === 'consent_missing_or_revoked';
  const showDisable = s === 'ready';
  const showEnable = s === 'disabled';
  const showRemove = s === 'ready' || s === 'disabled' || s === 'invalid_configuration' || s === 'consent_missing_or_revoked';
  const showDeprovision = s === 'ready' || s === 'disabled';
  const showRetry = s === 'provider_unavailable';

  const selected = providerStatus?.selectedProvider ?? null;
  const workReady = s === 'ready';

  return (
    // Collapsed by default (no `open`): the office worker expands "Sign-in method"
    // only when they want to change it, matching the Notifications section. The
    // summary reuses the section-head row and shows the current method as a chip;
    // the collapse-summary class hides the native disclosure marker (list-style +
    // ::-webkit-details-marker) and renders a single chevron instead.
    <details className="dvw-settings__section dvw-settings__collapse" id="signin-method-section" ref={signInDetailsRef}>
      <summary className="dvw-settings__head dvw-settings__collapse-summary">
        <div className="dvw-settings__head-title">
          <h3 className="dvw-keys__section-head">Sign-in method</h3>
        </div>
        {selected === 'windows_hello' ? (
          <span className="chip chip--local">Windows Hello</span>
        ) : selected === 'work_account' ? (
          <span className="chip chip--local">Work account</span>
        ) : (
          <span className="chip chip--muted">Not set</span>
        )}
      </summary>

      <p className="dvw-settings__desc">
        Choose how you unlock PAX Cookbook. You use one method at a time, and you can change it here whenever you like.
      </p>

      {selected === 'recovery_required' && (
        <p className="mk-callout mk-callout--warning" role="alert">
          Your saved sign-in method couldn&apos;t be read. Open PAX Cookbook Setup to repair it.
        </p>
      )}

      <ul className="signin-methods">
        <li className={`signin-method${selected === 'windows_hello' ? ' signin-method--current' : ''}`}>
          <div className="signin-method__body">
            <div className="signin-method__title-row">
              <span className="signin-method__title">Windows Hello</span>
              {selected === 'windows_hello' && <span className="chip chip--local">Current method</span>}
            </div>
            <p className="signin-method__desc">
              Use your face, fingerprint, or PIN to unlock PAX Cookbook on this PC.
            </p>
          </div>
          {selected === 'work_account' && (
            <div className="signin-method__action">
              <button
                type="button"
                className="dvw-btn"
                disabled={switching || busy}
                onClick={onUseWindowsHelloClick}
              >
                {switching ? 'Working\u2026' : 'Use Windows Hello'}
              </button>
            </div>
          )}
        </li>

        <li className={`signin-method${selected === 'work_account' ? ' signin-method--current' : ''}`}>
          <div className="signin-method__body">
            <div className="signin-method__title-row">
              <span className="signin-method__title">Work account</span>
              {selected === 'work_account' && <span className="chip chip--local">Current method</span>}
            </div>
            <p className="signin-method__desc">
              Use your Microsoft work account to unlock PAX Cookbook. You&apos;ll sign in once each time PAX Cookbook starts.
            </p>
          </div>
          {selected === 'windows_hello' && (
            <div className="signin-method__action">
              {workReady ? (
                <button
                  type="button"
                  className="dvw-btn"
                  disabled={switching || busy}
                  onClick={() => void switchToWorkAccount()}
                >
                  {switching ? 'Working\u2026' : 'Use work account'}
                </button>
              ) : (
                <button
                  type="button"
                  className="dvw-btn"
                  disabled={switching || busy}
                  onClick={openItTeam}
                >
                  Set up work account
                </button>
              )}
            </div>
          )}
        </li>
      </ul>

      {switchMessage && (
        <p
          className={switchApplied ? 'mk-callout mk-callout--info' : 'mk-callout mk-callout--warning'}
          role={switchApplied ? 'status' : 'alert'}
        >
          {switchMessage}
        </p>
      )}

      {switchApplied && (
        <>
          <button
            type="button"
            className="dvw-btn dvw-btn--primary signin-method__action"
            disabled={switching || locking}
            onClick={() => void applyByLocking()}
          >
            {locking ? 'Locking\u2026' : 'Lock now to apply'}
          </button>
          {lockError && (
            <p className="mk-callout mk-callout--warning" role="alert">{lockError}</p>
          )}
        </>
      )}

      {/* Subordinate, collapsed organization setup & management. Every action
          keeps its existing handler and security gate; only the labels are in
          plain language. This area is for administrators, not the everyday user. */}
      <details
        className="dvw-settings__collapse dvw-settings__subcollapse"
        data-testid="it-team-area"
        open={itTeamOpen}
        onToggle={(e) => setItTeamOpen((e.target as HTMLDetailsElement).open)}
      >
        <summary className="dvw-settings__subcollapse-summary">
          <h3 className="dvw-keys__section-head">For your IT team</h3>
        </summary>

        <p className="settings-note">
          These options set up and manage work-account sign-in for your organization. Most people won&apos;t need them.
        </p>

        {message && <p className="mk-callout mk-callout--warning" role="alert">{message}</p>}

        <div className="dvw-commandbar">
          {showConfigure && (
            <button type="button" className="dvw-btn" disabled={busy}
              onClick={() => setMessage(
                'An administrator sets up work-account sign-in for your organization: they run the provided ' +
                'setup helper, then import its result here. Nothing is created for individual people.')}>
              About work-account setup
            </button>
          )}
          {showImport && (
            <button type="button" className="dvw-btn" disabled={busy} onClick={onPickFile}>
              Import setup result
            </button>
          )}
          {showVerify && (
            <button type="button" className={s === 'configured_unverified' ? 'dvw-btn dvw-btn--primary' : 'dvw-btn'}
              disabled={busy} onClick={() => runAction(() => verifyWamSetup())}>
              {busy ? 'Checking\u2026' : 'Check organization setup'}
            </button>
          )}
          {showEnable && (
            <button type="button" className="dvw-btn" disabled={busy} onClick={() => runAction(() => setWamEnabled(true))}>
              Turn on work-account sign-in
            </button>
          )}
          {showDisable && (
            <button type="button" className="dvw-btn" disabled={busy} onClick={() => runAction(() => setWamEnabled(false))}>
              Turn off work-account sign-in
            </button>
          )}
          {showRetry && (
            <button type="button" className="dvw-btn" disabled={busy} onClick={() => void refresh()}>
              Try again
            </button>
          )}
          {showRemove && (
            <button type="button" className="dvw-btn dvw-btn--danger-ghost" disabled={busy} onClick={() => runAction(() => removeWamLocalConfig())}>
              Remove work-account setup from this PC
            </button>
          )}
          {showDeprovision && (
            <button type="button" className="dvw-btn dvw-btn--danger-ghost" disabled={busy} onClick={() => void startDeprovision()}>
              Remove organization setup
            </button>
          )}
        </div>

        <input ref={fileRef} type="file" accept="application/json,.json" style={{ display: 'none' }} onChange={onFileChosen} />

        {preview && (
          <div className="dvw-settings__inline-panel" role="dialog" aria-label="Import setup result">
            <p className="settings-note">
              Import this setup result? It records the organization setup, which must be checked before work-account sign-in can be used.
            </p>
            <dl className="dvw-settings__kv">
              <div><dt>Organization ID</dt><dd>{preview.tenantId} <CopyButton text={preview.tenantId} label="Copy organization ID" /></dd></div>
              <div><dt>Application ID</dt><dd>{preview.clientId} <CopyButton text={preview.clientId} label="Copy application ID" /></dd></div>
            </dl>
            <div className="dvw-commandbar">
              <button type="button" className="dvw-btn dvw-btn--primary" disabled={busy} onClick={() => void confirmImport()}>Save setup</button>
              <button type="button" className="dvw-btn" disabled={busy} onClick={() => setPreview(null)}>Cancel</button>
            </div>
          </div>
        )}

        {plan && (
          <div className="dvw-settings__inline-panel" role="dialog" aria-label="Remove organization setup">
            <p className="settings-note">
              This removes the work-account setup your organization created — both from this PC and from your organization.
              It does not change your Windows Hello sign-in, your recipes, or your saved data. The setup is removed from
              this PC only after the organization cleanup is confirmed.
            </p>
            <label className="dvw-settings__confirm">
              <input type="checkbox" checked={deprovisionConfirmed} onChange={(e) => setDeprovisionConfirmed(e.target.checked)} />
              <span>I understand this removes the organization setup.</span>
            </label>
            <div className="dvw-commandbar">
              <button type="button" className="dvw-btn dvw-btn--danger" disabled={busy || !deprovisionConfirmed} onClick={() => void confirmDeprovision()}>
                Remove organization setup
              </button>
              <button type="button" className="dvw-btn" disabled={busy} onClick={() => { setPlan(null); setDeprovisionConfirmed(false); }}>Cancel</button>
            </div>
          </div>
        )}

        <details className="dvw-settings__collapse dvw-settings__subcollapse" onToggle={(e) => { if ((e.target as HTMLDetailsElement).open) void revealDetails(); }}>
          <summary className="dvw-settings__subcollapse-summary"><span className="dvw-settings__head-title">Organization setup details</span></summary>
          {details?.available ? (
            <dl className="dvw-settings__kv">
              <div><dt>Organization ID</dt><dd>{details.tenantId} <CopyButton text={details.tenantId ?? ''} label="Copy organization ID" /></dd></div>
              <div><dt>Application ID</dt><dd>{details.clientId} <CopyButton text={details.clientId ?? ''} label="Copy application ID" /></dd></div>
            </dl>
          ) : (
            <p className="settings-note">No setup to show.</p>
          )}
        </details>
      </details>
    </details>
  );
}
