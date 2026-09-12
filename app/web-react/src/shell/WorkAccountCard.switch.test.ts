/**
 * Batch 1b Stage 3 — register-before-switch enrollment orchestration.
 *
 * These tests exercise the React side of the Work account -> Windows Hello
 * provider switch (WorkAccountCard.switchToWindowsHello). The WebAuthn ceremony
 * itself lives in the TOP-LEVEL shell (app/web/assets/integrated-shell.js ->
 * app/web/assets/lock-overlay.js) and is NOT reachable from vitest/jsdom, so the
 * ceremony is mocked at its React boundary — requestWindowsHelloEnrollmentFromNative,
 * the courier that carries the correlated enroll request to the parent shell and
 * returns the bounded outcome. The exact-origin / requestId-correlation guards on
 * that courier are proven separately in experimentalWam.test.ts.
 *
 * Covered requirements:
 *   #1 request -> ceremony success -> ATOMIC select (re-select only after 'enrolled').
 *   #2 ceremony cancel/timeout/failure/unavailable -> work_account retained, NO flip.
 *   #5 provider persisted ONLY after enrollment success (strict call ordering).
 * Plus the already-registered fast path (a): a direct successful select with NO
 * enrollment courier at all.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createElement } from 'react';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';

// Mock the entire host module so every provider-switch dependency is a spy.
vi.mock('../host/experimentalWam', () => ({
  getWamConfigState: vi.fn(),
  getWamAdminDetails: vi.fn(),
  importWamSetupResult: vi.fn(),
  verifyWamSetup: vi.fn(),
  setWamEnabled: vi.fn(),
  removeWamLocalConfig: vi.fn(),
  prepareWamDeprovision: vi.fn(),
  executeWamDeprovision: vi.fn(),
  getSessionProviderStatus: vi.fn(),
  authorizeSessionWithExperimentalWam: vi.fn(),
  selectWorkAccountProvider: vi.fn(),
  selectWindowsHelloProvider: vi.fn(),
  probePlatformAuthenticatorAvailable: vi.fn(),
  requestWindowsHelloEnrollmentFromNative: vi.fn(),
  requestBrokerLock: vi.fn(),
}));

import * as host from '../host/experimentalWam';
import type {
  WamConfigStateInfo,
  SessionProviderStatus,
  ProviderSwitchResult,
  WindowsHelloEnrollmentOutcome,
} from '../host/experimentalWam';
import { WorkAccountCard } from './WorkAccountCard';

const readyConfig: WamConfigStateInfo = {
  state: 'ready',
  providerId: null,
  disabled: false,
  configured: true,
  capabilityAvailable: true,
  verifiedUtc: null,
};

const workAccountStatus: SessionProviderStatus = {
  selectedProvider: 'work_account',
  recoveryRequired: false,
  usable: true,
  healthCode: 'ok',
};

const enrollmentRequired: ProviderSwitchResult = {
  ok: false,
  reason: 'windows_hello_enrollment_required',
  selectedProvider: null,
};

const selectSucceeded: ProviderSwitchResult = {
  ok: true,
  reason: null,
  selectedProvider: 'windows_hello',
};

beforeEach(() => {
  vi.clearAllMocks();
  // Config-state 'ready' + selected provider 'work_account' => the section
  // renders and offers the inactive-method "Use Windows Hello" action.
  vi.mocked(host.getWamConfigState).mockResolvedValue(readyConfig);
  vi.mocked(host.getSessionProviderStatus).mockResolvedValue(workAccountStatus);
  // Platform (device) predicate is available by default; the daemon still owns
  // the SEPARATE local-registration predicate via selectWindowsHelloProvider.
  vi.mocked(host.probePlatformAuthenticatorAvailable).mockResolvedValue(true);
});

afterEach(() => {
  cleanup();
});

async function renderCardAndGetSwitchButton(): Promise<HTMLElement> {
  render(createElement(WorkAccountCard));
  return await screen.findByRole('button', { name: /Use Windows Hello/ });
}

describe('WorkAccountCard: Work account -> Windows Hello register-before-switch', () => {
  it('(a) already registered: a single successful select, no enrollment courier', async () => {
    vi.mocked(host.selectWindowsHelloProvider).mockResolvedValue(selectSucceeded);

    const button = await renderCardAndGetSwitchButton();
    fireEvent.click(button);

    await screen.findByText(/Windows Hello is now your sign-in method/);
    expect(host.selectWindowsHelloProvider).toHaveBeenCalledTimes(1);
    // No ceremony was requested because registration already existed.
    expect(host.requestWindowsHelloEnrollmentFromNative).not.toHaveBeenCalled();
  });

  it('#1 enrolled outcome -> re-selects and applies (registration before flip)', async () => {
    vi.mocked(host.selectWindowsHelloProvider)
      .mockResolvedValueOnce(enrollmentRequired) // (b) available but not registered
      .mockResolvedValueOnce(selectSucceeded); // after enrollment persists
    vi.mocked(host.requestWindowsHelloEnrollmentFromNative).mockResolvedValue('enrolled');

    const button = await renderCardAndGetSwitchButton();
    fireEvent.click(button);

    await screen.findByText(/Windows Hello is now your sign-in method/);
    expect(host.requestWindowsHelloEnrollmentFromNative).toHaveBeenCalledTimes(1);
    // Exactly two selects: the initial probe-select that reported
    // enrollment_required, then the post-enrollment flip.
    expect(host.selectWindowsHelloProvider).toHaveBeenCalledTimes(2);
    // The platform predicate was re-probed after enrollment before the flip:
    // once for the initial select, once for the post-enrollment select.
    expect(host.probePlatformAuthenticatorAvailable).toHaveBeenCalledTimes(2);
  });

  it('#5 strict ordering: the flip (persist) happens ONLY after "enrolled"', async () => {
    const order: string[] = [];
    vi.mocked(host.selectWindowsHelloProvider).mockImplementation(async () => {
      const priorSelects = order.filter((x) => x === 'select').length;
      order.push('select');
      return priorSelects === 0 ? enrollmentRequired : selectSucceeded;
    });
    vi.mocked(host.requestWindowsHelloEnrollmentFromNative).mockImplementation(async () => {
      order.push('enroll');
      return 'enrolled';
    });

    const button = await renderCardAndGetSwitchButton();
    fireEvent.click(button);

    await waitFor(() => expect(order).toEqual(['select', 'enroll', 'select']));
    // The provider-flipping select is strictly the LAST step, after 'enroll'.
    expect(order.lastIndexOf('enroll')).toBeLessThan(order.lastIndexOf('select'));
  });

  const nonSuccessCases: Array<{ outcome: WindowsHelloEnrollmentOutcome; text: RegExp }> = [
    { outcome: 'cancelled', text: /Nothing changed\. Work account is still your sign-in method\./ },
    { outcome: 'timeout', text: /That didn't finish in time/ },
    { outcome: 'failed', text: /finish setting it up for PAX Cookbook/ },
    { outcome: 'unavailable', text: /Windows Hello isn't available right now/ },
  ];

  for (const { outcome, text } of nonSuccessCases) {
    it(`#2 enrollment ${outcome} -> work account retained, no partial write / no flip`, async () => {
      vi.mocked(host.selectWindowsHelloProvider).mockResolvedValue(enrollmentRequired);
      vi.mocked(host.requestWindowsHelloEnrollmentFromNative).mockResolvedValue(outcome);

      const button = await renderCardAndGetSwitchButton();
      fireEvent.click(button);

      await screen.findByText(text);
      // The courier ran once; the provider was NEVER flipped (only the initial
      // probe-select ran — no second, provider-changing select).
      expect(host.requestWindowsHelloEnrollmentFromNative).toHaveBeenCalledTimes(1);
      expect(host.selectWindowsHelloProvider).toHaveBeenCalledTimes(1);
      // No success message was shown.
      expect(screen.queryByText(/Windows Hello is now your sign-in method/)).toBeNull();
    });
  }

  it('#2 enrollment transport_failure -> work account retained, no flip', async () => {
    vi.mocked(host.selectWindowsHelloProvider).mockResolvedValue(enrollmentRequired);
    vi.mocked(host.requestWindowsHelloEnrollmentFromNative).mockResolvedValue('transport_failure');

    const button = await renderCardAndGetSwitchButton();
    fireEvent.click(button);

    // A transport failure carrying the enrollment outcome is a non-success: the
    // work account is retained with a bounded "Nothing changed" message.
    await screen.findByText(/Nothing changed\. Work account is still your sign-in method\./);
    expect(host.requestWindowsHelloEnrollmentFromNative).toHaveBeenCalledTimes(1);
    // Only the initial probe-select ran; the provider was never flipped.
    expect(host.selectWindowsHelloProvider).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(/Windows Hello is now your sign-in method/)).toBeNull();
  });

  it('(c) initial select platform_unavailable -> work account retained, NO enrollment courier', async () => {
    vi.mocked(host.selectWindowsHelloProvider).mockResolvedValue({
      ok: false,
      reason: 'windows_hello_platform_unavailable',
      selectedProvider: null,
    });

    const button = await renderCardAndGetSwitchButton();
    fireEvent.click(button);

    await screen.findByText(/Windows Hello isn't available right now/);
    // A non-enrollment reason must NOT trigger the ceremony courier at all.
    expect(host.requestWindowsHelloEnrollmentFromNative).not.toHaveBeenCalled();
    // Exactly the one probe-select; no provider-changing second select.
    expect(host.selectWindowsHelloProvider).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(/Windows Hello is now your sign-in method/)).toBeNull();
  });

  it('malformed initial select result -> fail closed to work account, NO courier, NO flip', async () => {
    // An unrecognized/garbage reason on a not-ok result must fall through the
    // default arm and leave work_account selected without any ceremony.
    vi.mocked(host.selectWindowsHelloProvider).mockResolvedValue({
      ok: false,
      reason: 'totally_unrecognized_reason',
      selectedProvider: null,
    } as unknown as ProviderSwitchResult);

    const button = await renderCardAndGetSwitchButton();
    fireEvent.click(button);

    await screen.findByText(/Nothing changed\. Work account is still your sign-in method\./);
    expect(host.requestWindowsHelloEnrollmentFromNative).not.toHaveBeenCalled();
    expect(host.selectWindowsHelloProvider).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(/Windows Hello is now your sign-in method/)).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// Sign-in method UX repair (cycle-01r-signin-ux-repair).
//   DEFECT #2 — the section is a collapsed-by-default <details> with a
//               current-method chip in its summary (not an always-open block).
//   DEFECT #3 — "Lock now to apply" actually drives the broker lock, gives
//               in-flight feedback (disabled + "Locking\u2026"), and reports a
//               truthful, retryable message when the lock does not take effect.
// ---------------------------------------------------------------------------
describe('WorkAccountCard: Sign-in method collapse + "Lock now to apply"', () => {
  it('DEFECT#2: renders a collapsed <details> whose summary shows the current-method chip', async () => {
    render(createElement(WorkAccountCard));
    const heading = await screen.findByRole('heading', { name: /^Sign-in method$/i });
    const details = heading.closest('details') as HTMLDetailsElement;
    expect(details).not.toBeNull();
    expect(details.classList.contains('dvw-settings__collapse')).toBe(true);
    // Collapsed by default: no `open` attribute.
    expect(details.open).toBe(false);
    const summary = details.querySelector('summary');
    expect(summary).not.toBeNull();
    expect(summary?.classList.contains('dvw-settings__collapse-summary')).toBe(true);
    // The current method (work_account by default here) is surfaced as a chip.
    expect(summary?.textContent).toContain('Sign-in method');
    expect(summary?.textContent).toContain('Work account');
    expect(summary?.querySelector('.chip')).not.toBeNull();
  });

  it('DEFECT#3: "Lock now to apply" drives the broker lock and shows in-flight feedback', async () => {
    vi.mocked(host.selectWindowsHelloProvider).mockResolvedValue(selectSucceeded);
    // Initialized to a definite no-op so its type is always callable (avoids the
    // closure-assignment `never` narrowing); reassigned to the real resolver the
    // first time the mocked lock request runs.
    let resolveLock: (v: boolean) => void = () => undefined;
    vi.mocked(host.requestBrokerLock).mockImplementation(
      () =>
        new Promise<boolean>((resolve) => {
          resolveLock = resolve;
        }),
    );

    const button = await renderCardAndGetSwitchButton();
    fireEvent.click(button);
    await screen.findByText(/Windows Hello is now your sign-in method/);

    const lockButton = await screen.findByRole('button', { name: /Lock now to apply/ });
    expect((lockButton as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(lockButton);

    // In-flight: the button relabels to "Locking\u2026" and disables so it can't
    // be pressed twice while the shell mounts the lock screen.
    const locking = await screen.findByRole('button', { name: /Locking/ });
    expect((locking as HTMLButtonElement).disabled).toBe(true);
    expect(host.requestBrokerLock).toHaveBeenCalledTimes(1);

    // Resolve the lock so no promise is left dangling; the button stays in its
    // "Locking\u2026" state because the shell now owns presenting the overlay.
    resolveLock(true);
    await waitFor(() =>
      expect((screen.getByRole('button', { name: /Locking/ }) as HTMLButtonElement).disabled).toBe(
        true,
      ),
    );
  });

  it('DEFECT#3: a failed lock restores the affordance with a truthful retry message', async () => {
    vi.mocked(host.selectWindowsHelloProvider).mockResolvedValue(selectSucceeded);
    vi.mocked(host.requestBrokerLock).mockResolvedValue(false);

    const button = await renderCardAndGetSwitchButton();
    fireEvent.click(button);
    await screen.findByText(/Windows Hello is now your sign-in method/);

    const lockButton = await screen.findByRole('button', { name: /Lock now to apply/ });
    fireEvent.click(lockButton);

    await screen.findByText(/couldn't lock just now/i);
    expect(host.requestBrokerLock).toHaveBeenCalledTimes(1);
    // The affordance is restored (not stuck in "Locking\u2026") so the office
    // worker can retry.
    const restored = await screen.findByRole('button', { name: /Lock now to apply/ });
    expect((restored as HTMLButtonElement).disabled).toBe(false);
  });

  // cycle-01r follow-up: the two NESTED collapsible summaries ("For your IT
  // team" and "Organization setup details") are SUB-dropdowns of the "Sign-in
  // method" section, not peers of it / of About / Help. They carry the distinct
  // subordinate treatment: the summary uses dvw-settings__subcollapse-summary
  // (LEFT tree-style expander + small sentence-case sub heading, no far-right
  // chevron), and the <details> carries dvw-settings__subcollapse (indent + left
  // nesting rail) alongside dvw-settings__collapse for block layout.
  it('cycle-01r: nested "For your IT team" is a subordinate sub-dropdown, not a peer section', async () => {
    render(createElement(WorkAccountCard));
    const heading = await screen.findByRole('heading', { name: /^For your IT team$/i });
    const summary = heading.closest('summary') as HTMLElement;
    expect(summary).not.toBeNull();
    // Sub-dropdown summary treatment (not the parent's flex head).
    expect(summary.classList.contains('dvw-settings__subcollapse-summary')).toBe(true);
    expect(summary.classList.contains('dvw-settings__head')).toBe(false);
    // The nested <details> carries the indent + left-rail subcollapse class.
    const details = summary.closest('details') as HTMLElement;
    expect(details.classList.contains('dvw-settings__subcollapse')).toBe(true);
    expect(details.classList.contains('dvw-settings__collapse')).toBe(true);
    // Its title is the (now de-emphasized) section head.
    expect(summary.querySelector('.dvw-keys__section-head')).not.toBeNull();
  });

  it('cycle-01r: nested "Organization setup details" is a subordinate sub-dropdown with a wrapped title', async () => {
    render(createElement(WorkAccountCard));
    const summary = await screen.findByText('Organization setup details');
    const summaryEl = summary.closest('summary') as HTMLElement;
    expect(summaryEl).not.toBeNull();
    // Sub-dropdown summary treatment (not the parent's flex head).
    expect(summaryEl.classList.contains('dvw-settings__subcollapse-summary')).toBe(true);
    expect(summaryEl.classList.contains('dvw-settings__head')).toBe(false);
    // The nested <details> carries the indent + left-rail subcollapse class;
    // nested inside "For your IT team", so the rail/indent compounds to show a
    // third level.
    const detailsEl = summaryEl.closest('details') as HTMLElement;
    expect(detailsEl.classList.contains('dvw-settings__subcollapse')).toBe(true);
    expect(detailsEl.classList.contains('dvw-settings__collapse')).toBe(true);
    // The bare label stays wrapped in .dvw-settings__head-title (now the
    // de-emphasized sub heading).
    const title = summaryEl.querySelector('.dvw-settings__head-title');
    expect(title).not.toBeNull();
    expect(title?.textContent).toBe('Organization setup details');
  });
});
