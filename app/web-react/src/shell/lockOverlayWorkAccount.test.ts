/**
 * cycle-02r5 Batch 3 — Work-account lock overlay UI tests.
 *
 * These exercise the REAL shipped legacy shell asset
 * app/web/assets/lock-overlay.js (no reimplementation). The module is loaded as
 * a raw string via Vite's `?raw` loader and evaluated in jsdom; it self-registers
 * the `cookbook:brokerLocked` listener and exposes the diagnostics surface
 * `window.cookbookLockOverlay` ({ force, dismiss, state, recheck }). The tests
 * drive the overlay through `force('brokerLocked')` with a mocked
 * `window.cookbookApi` + native `window.chrome.webview` and assert:
 *   1. The Work-account action is a FULL-WIDTH BLUE PRIMARY button (btn-primary,
 *      never btn-ghost/link) in every state — idle, in-flight, and after a
 *      failure (retry) — while Close app stays visually secondary (btn-ghost).
 *   2. The Work-account lock screen carries provider-OWNED copy only: no
 *      'Windows Hello' / 'PIN' / 'appliance is locked' / 'before each bake'
 *      bleed, and no customer-visible 'experimental'.
 *   3. The bounded reason -> message map renders an accurate, business
 *      appropriate message per reason; a cancellation is NOT 'declined'; and an
 *      unknown/absent/malformed reason FAILS CLOSED to the generic message. On
 *      every failure the overlay stays mounted (broker stays Locked).
 *   4. The Windows Hello screen contains NO work-account action, and the
 *      recovery_required copy remains intact.
 */
/// <reference types="vite/client" />
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import moduleSrcRaw from '../../../web/assets/lock-overlay.js?raw';

const SRC: string = moduleSrcRaw;

const OVERLAY = '#cookbook-lock-overlay';
const ID_TITLE = 'cookbook-lock-overlay-title';
const ID_BODY = 'cookbook-lock-overlay-body';
const ID_STATUS = 'cookbook-lock-overlay-status';
const ID_WORKACCOUNT = 'cookbook-lock-overlay-workaccount';
const ID_TERTIARY = 'cookbook-lock-overlay-tertiary';

const SESSION_PROVIDER_PATH = '/api/v1/broker/session-provider';
const LOCK_STATE_PATH = '/api/v1/broker/lock-state';
const WAM_INIT_PATH = '/api/v1/broker/experimental/wam/initiate';
const WAM_STATUS_PATH = '/api/v1/broker/experimental/wam/status';

// Bounded reason -> expected customer message. Source of truth is the shipped
// mapWamReasonMessage() in lock-overlay.js; these strings must match verbatim.
const REASON_MESSAGE: Record<string, string> = {
  cancelled:
    'Work-account sign-in was cancelled. Select \u201cSign in with work account\u201d to try again.',
  identity_failure:
    'We couldn\u2019t verify your work account. Make sure you\u2019re using your organization account, then try again.',
  scope_failure:
    'Your work account doesn\u2019t have the permissions PAX Cookbook needs to sign you in. Contact your administrator.',
  configuration_failure:
    'Work-account sign-in isn\u2019t set up correctly on this computer. Open PAX Cookbook Setup to repair it.',
  broker_failure: 'Windows couldn\u2019t complete work-account sign-in. Try again.',
  // cycle-02r5b: connectivity wording is reserved for connectivity_failure ONLY.
  connectivity_failure:
    'We couldn\u2019t reach your organization\u2019s sign-in service. Check your connection and try again.',
  authority_registration_mismatch:
    'This work-account setup isn\u2019t compatible with the account picker. Ask your IT team to repair the sign-in setup.',
  consent_required:
    'Your organization needs to approve this sign-in permission before you can continue.',
  service_rejected:
    'Your organization couldn\u2019t complete this sign-in. Ask your IT team to check the Work-account setup.',
  unknown_failure:
    'Work-account sign-in couldn\u2019t be completed. Try again or ask your IT team for help.',
  // Legacy reason: no longer emitted by production; now shows generic try-again
  // copy (NOT connectivity), so it can never imply the network is down.
  transport_failure:
    'Work-account sign-in couldn\u2019t be completed. Try again or ask your IT team for help.',
  expired:
    'The sign-in request timed out. Select \u201cSign in with work account\u201d to try again.',
  disabled: 'Work-account sign-in is currently unavailable. Contact your administrator.',
  denied:
    'Work-account sign-in was declined. Try again, or contact your administrator if this keeps happening.',
  none: 'Work-account sign-in couldn\u2019t be completed. Try again.',
};
const GENERIC = REASON_MESSAGE.none;
// cycle-02r5b: the exact connectivity sentence, reserved for connectivity_failure.
const CONNECTIVITY_COPY =
  'We couldn\u2019t reach your organization\u2019s sign-in service. Check your connection and try again.';

interface LockOverlayApi {
  force: (kind: string) => void;
  dismiss: () => void;
  state: () => Record<string, unknown>;
  recheck: () => void;
}

// Mutable per-test mocks.
let providerBody: Record<string, unknown> | null;
let wamInitBody: Record<string, unknown> | null;
let wamStatusBody: Record<string, unknown> | null;
let postMessageSpy: ReturnType<typeof vi.fn>;

async function flush(): Promise<void> {
  for (let i = 0; i < 12; i += 1) {
    // eslint-disable-next-line no-await-in-loop
    await Promise.resolve();
  }
}

function installMocks(): void {
  (window as unknown as { cookbookApi: unknown }).cookbookApi = {
    get: (path: string) => {
      if (path === LOCK_STATE_PATH) {
        return Promise.resolve({ ok: true, body: { state: 'Unlocked' } });
      }
      if (path === SESSION_PROVIDER_PATH) {
        return Promise.resolve({ ok: true, body: providerBody });
      }
      return Promise.resolve({ ok: false, body: null });
    },
    post: (path: string) => {
      if (path === WAM_INIT_PATH) {
        return Promise.resolve({ ok: true, body: wamInitBody });
      }
      if (path === WAM_STATUS_PATH) {
        return Promise.resolve({ ok: true, body: wamStatusBody });
      }
      return Promise.resolve({ ok: false, body: null });
    },
  };
  postMessageSpy = vi.fn();
  (window as unknown as { chrome: unknown }).chrome = {
    webview: { postMessage: postMessageSpy },
  };
}

function loadApi(): LockOverlayApi {
  (0, eval)(SRC); // eslint-disable-line no-eval
  return (window as unknown as { cookbookLockOverlay: LockOverlayApi }).cookbookLockOverlay;
}

let api: LockOverlayApi;

async function mount(provider: Record<string, unknown> | null): Promise<void> {
  providerBody = provider;
  api.force('brokerLocked');
  await flush();
}

function el(id: string): HTMLElement | null {
  return document.getElementById(id);
}

function overlayText(): string {
  const o = document.querySelector(OVERLAY) as HTMLElement | null;
  return (o?.textContent || '').toLowerCase();
}

beforeEach(() => {
  document.body.innerHTML = '';
  providerBody = { selectedProvider: 'work_account', usable: true };
  wamInitBody = { requestId: 'req-1' };
  wamStatusBody = { state: 'Denied', reason: 'none' };
  installMocks();
  api = loadApi();
});

afterEach(() => {
  try {
    api.dismiss();
  } catch {
    /* ignore */
  }
  vi.restoreAllMocks();
  document.body.innerHTML = '';
});

describe('Work-account primary button', () => {
  it('renders a full-width blue PRIMARY button (btn-primary, not ghost/link)', async () => {
    await mount({ selectedProvider: 'work_account', usable: true });
    const wa = el(ID_WORKACCOUNT);
    expect(wa).not.toBeNull();
    expect(wa!.className).toContain('btn-primary');
    expect(wa!.className).not.toContain('btn-ghost');
    expect(wa!.textContent).toBe('Sign in with work account');
    // It lives in the shared actions box so it inherits the Close app metrics.
    expect(wa!.parentElement?.className).toContain('lock-overlay-actions');
  });

  it('keeps Close app present and visually SECONDARY (btn-ghost)', async () => {
    await mount({ selectedProvider: 'work_account', usable: true });
    const closeApp = el(ID_TERTIARY);
    expect(closeApp).not.toBeNull();
    expect(closeApp!.textContent).toBe('Close app');
    expect(closeApp!.className).toContain('btn-ghost');
    expect(closeApp!.className).not.toContain('btn-primary');
  });

  it('stays PRIMARY across idle, in-flight, and post-failure retry', async () => {
    await mount({ selectedProvider: 'work_account', usable: true });
    const wa = el(ID_WORKACCOUNT) as HTMLButtonElement;
    // idle
    expect(wa.className).toContain('btn-primary');
    expect(wa.disabled).toBe(false);
    expect(wa.textContent).toBe('Sign in with work account');

    // in-flight (synchronous state set by onWorkAccountClick -> setWamUi)
    wamStatusBody = { state: 'Denied', reason: 'cancelled' };
    wa.click();
    expect(wa.className).toContain('btn-primary');
    expect(wa.disabled).toBe(true);
    expect(wa.textContent).toBe('Signing in\u2026');

    // terminal failure -> retry state, still the same primary button
    await flush();
    expect(wa.className).toContain('btn-primary');
    expect(wa.className).not.toContain('btn-ghost');
    expect(wa.disabled).toBe(false);
    expect(wa.textContent).toBe('Sign in with work account');
    // Overlay is still mounted -> broker stayed Locked.
    expect((document.querySelector(OVERLAY) as HTMLElement).classList.contains('visible')).toBe(true);
  });
});

describe('Work-account provider-owned copy (no generic bleed)', () => {
  it('shows no Windows Hello / PIN / appliance-locked / before-each-bake / experimental copy', async () => {
    await mount({ selectedProvider: 'work_account', usable: true });
    const text = overlayText();
    expect(text).not.toContain('windows hello');
    expect(text).not.toMatch(/\bpin\b/);
    expect(text).not.toContain('appliance is locked');
    expect(text).not.toContain('before each bake');
    expect(text).not.toContain('before your first bake');
    expect(text).not.toContain('experimental');
  });

  it('renders the locked + single-session sign-in intent and keeps data local', async () => {
    await mount({ selectedProvider: 'work_account', usable: true });
    const body = el(ID_BODY)!.textContent || '';
    expect(body).toContain('PAX Cookbook is locked');
    expect(body).toContain('work account');
    expect(body.toLowerCase()).toContain('once per session');
    expect(body.toLowerCase()).toContain('resuming');
    expect(body).toContain('stay on this computer');
  });
});

describe('Reason -> message map (terminal failures)', () => {
  async function driveDenied(reason: unknown): Promise<string> {
    await mount({ selectedProvider: 'work_account', usable: true });
    wamStatusBody = reason === undefined ? { state: 'Denied' } : { state: 'Denied', reason };
    (el(ID_WORKACCOUNT) as HTMLButtonElement).click();
    await flush();
    return el(ID_STATUS)!.textContent || '';
  }

  for (const reason of Object.keys(REASON_MESSAGE)) {
    it(`maps reason=${reason} to its accurate message`, async () => {
      const msg = await driveDenied(reason);
      expect(msg).toBe(REASON_MESSAGE[reason]);
    });
  }

  it('never shows a cancellation as a decline', async () => {
    const msg = await driveDenied('cancelled');
    expect(msg.toLowerCase()).not.toContain('declined');
    expect(msg.toLowerCase()).toContain('cancelled');
  });

  it('shows the connectivity sentence ONLY for connectivity_failure', async () => {
    // The one reason that legitimately carries connectivity copy.
    expect(await driveDenied('connectivity_failure')).toBe(CONNECTIVITY_COPY);

    // No other reason (including the legacy transport_failure and every bounded
    // MSAL-failure class) may imply the network is down.
    for (const reason of Object.keys(REASON_MESSAGE)) {
      if (reason === 'connectivity_failure') {
        continue;
      }
      // eslint-disable-next-line no-await-in-loop
      const msg = await driveDenied(reason);
      expect(msg).not.toBe(CONNECTIVITY_COPY);
      expect(msg.toLowerCase()).not.toContain('reach your organization');
    }

    // A reached-service rejection must not borrow connectivity wording.
    expect(await driveDenied('service_rejected')).not.toContain('reach your organization');
  });

  it('maps the diagnostic MSAL-failure reasons to their accurate copy', async () => {
    expect(await driveDenied('authority_registration_mismatch')).toBe(
      REASON_MESSAGE.authority_registration_mismatch,
    );
    expect(await driveDenied('consent_required')).toBe(REASON_MESSAGE.consent_required);
    expect(await driveDenied('service_rejected')).toBe(REASON_MESSAGE.service_rejected);
    expect(await driveDenied('unknown_failure')).toBe(REASON_MESSAGE.unknown_failure);
  });

  it('fails closed to the generic message for an unknown reason', async () => {
    expect(await driveDenied('totally_unknown_reason')).toBe(GENERIC);
  });

  it('fails closed to the generic message for an absent reason', async () => {
    expect(await driveDenied(undefined)).toBe(GENERIC);
  });

  it('fails closed to the generic message for a malformed reason type', async () => {
    expect(await driveDenied(12345)).toBe(GENERIC);
  });

  it('keeps the broker Locked (overlay mounted) on a terminal failure', async () => {
    await driveDenied('denied');
    expect((document.querySelector(OVERLAY) as HTMLElement).classList.contains('visible')).toBe(true);
  });
});

describe('Windows Hello screen', () => {
  it('contains NO work-account action', async () => {
    await mount({ selectedProvider: 'windows_hello' });
    expect(el(ID_WORKACCOUNT)).toBeNull();
    const text = overlayText();
    expect(text).not.toContain('work account');
    expect(text).not.toContain('experimental');
  });

  // Re-render the Hello view in a chosen webauthn-status state. The overlay
  // derives first-run vs returning-user purely from state.statusCache.registered
  // (null/true => returning-user unlock, false => first-run setup). No preflight
  // runs in jsdom, so the test sets statusCache directly on the live state and
  // forces a re-render through the same brokerLocked path the shell uses.
  async function mountHello(registered: boolean | null): Promise<void> {
    await mount({ selectedProvider: 'windows_hello' });
    (api.state() as { statusCache: unknown }).statusCache =
      registered === null ? null : { registered };
    api.force('brokerLocked');
    await flush();
  }

  it('renders NO per-Bake language on the returning-user Hello unlock', async () => {
    await mountHello(true);
    const text = overlayText();
    expect(text).not.toContain('before each bake');
    expect(text).not.toContain('before your first bake');
    // Legitimate Hello ceremony copy stays intact.
    expect(text).toContain('fingerprint, face, or');
    expect(text).toMatch(/\bpin\b/);
    expect(text).toContain('the same way you unlock this computer');
    // Windows Security monitor hint stays intact.
    expect(text).toContain('windows security prompt');
    // Once-per-session privacy framing replaced the per-Bake claim.
    expect(text).toContain('once per session');
    expect(text).toContain('baking and resuming');
    expect(text).toContain('stay on this computer');
  });

  it('renders NO per-Bake language on the first-run Hello setup', async () => {
    await mountHello(false);
    const title = el(ID_TITLE)!.textContent || '';
    expect(title).toBe('Set up quick verification');
    const text = overlayText();
    expect(text).not.toContain('before each bake');
    expect(text).not.toContain('before your first bake');
    // First-run ceremony + local-passkey copy stays intact.
    expect(text).toContain('fingerprint, face, or');
    expect(text).toMatch(/\bpin\b/);
    expect(text).toContain('the same way you unlock this computer');
    expect(text).toContain('windows security prompt');
    expect(text).toContain('stays on this device');
    // Once-per-session privacy framing replaced the per-Bake claim.
    expect(text).toContain('once per session');
    expect(text).toContain('baking and resuming');
    expect(text).toContain('stay on this computer');
  });
});

describe('recovery_required screen', () => {
  it('keeps the repair copy intact and offers no work-account action', async () => {
    await mount({ selectedProvider: 'recovery_required' });
    expect(el(ID_WORKACCOUNT)).toBeNull();
    expect(el(ID_TITLE)!.textContent).toBe('Sign-in needs repair');
    const body = el(ID_BODY)!.textContent || '';
    expect(body).toContain('needs repair');
    expect(body).toContain('PAX Cookbook Setup');
    expect(body.toLowerCase()).not.toContain('experimental');
  });
});
