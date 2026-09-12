/**
 * Batch 1c — office-worker copy contract for the "Sign-in method" surface.
 *
 * The everyday, non-technical sign-in surface (WorkAccountCard's primary body,
 * i.e. everything OUTSIDE the collapsed "For your IT team" area) must speak only
 * in plain, task-oriented language. This test renders the surface across its
 * bounded states and asserts:
 *   1. the authorized customer copy is present, and
 *   2. none of the prohibited engineering / identity / architecture terms leak
 *      into the customer-visible text.
 *
 * The subordinate "For your IT team" details node (data-testid="it-team-area")
 * is administrator-facing and is deliberately excluded from the scan — it may
 * legitimately show organization/application identifiers and setup controls.
 *
 * DESIGN NOTE — "fingerprint": the prohibited list guards against the
 * ARCHITECTURAL fingerprint (the SHA-256 engine/config fingerprint), which must
 * never surface to an office worker. The word also appears legitimately as a
 * BIOMETRIC in the authorized Windows Hello description ("face, fingerprint, or
 * PIN"). The two meanings are distinct, so the biometric term is intentionally
 * NOT enforced as prohibited here; the architectural fingerprint (a hex hash)
 * does not and must not appear on this surface at all.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createElement } from 'react';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';

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
import type { WamConfigStateInfo, SessionProviderStatus } from '../host/experimentalWam';
import { WorkAccountCard } from './WorkAccountCard';

function config(state: string): WamConfigStateInfo {
  return {
    state,
    providerId: null,
    disabled: false,
    configured: state !== 'not_configured',
    capabilityAvailable: true,
    verifiedUtc: null,
  };
}

function status(selected: SessionProviderStatus['selectedProvider']): SessionProviderStatus {
  return {
    selectedProvider: selected,
    recoveryRequired: selected === 'recovery_required',
    usable: selected !== 'recovery_required',
    healthCode: 'ok',
  };
}

// Prohibited engineering / identity / architecture vocabulary that must never
// appear in the everyday sign-in surface. NOTE: the biometric word "fingerprint"
// is intentionally absent (see the DESIGN NOTE in the file header).
const PROHIBITED = [
  'oauth',
  'wam',
  'broker',
  'provider state',
  'session',
  'acquisition',
  'scope',
  'microsoft graph',
  'user.read',
  'delegated permission',
  'tenant',
  'token',
  'claim',
  'client id',
  'credential',
  'provisioning',
  'deprovision',
  'provenance',
  'unverified',
  'capability',
];

// Render, then return ONLY the customer-facing text: the section's textContent
// with the subordinate "For your IT team" area removed. The Sign-in method
// surface is a collapsed <details> (not an ARIA landmark), so it is located by
// its heading and we climb to the enclosing <details>.
function customerText(): string {
  const heading = screen.getByRole('heading', { name: /^Sign-in method$/i });
  const section = heading.closest('details') as HTMLElement;
  const clone = section.cloneNode(true) as HTMLElement;
  clone.querySelector('[data-testid="it-team-area"]')?.remove();
  return (clone.textContent ?? '').toLowerCase();
}

function assertNoProhibited(text: string): void {
  for (const term of PROHIBITED) {
    expect(text, `prohibited term "${term}" leaked into the office-worker surface`).not.toContain(term);
  }
  // No raw reason-code / internal identifier (snake_case) may surface either.
  expect(text, 'a raw snake_case identifier leaked into the office-worker surface').not.toMatch(
    /[a-z]{2,}_[a-z]{2,}/,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(host.probePlatformAuthenticatorAvailable).mockResolvedValue(true);
});

afterEach(() => {
  cleanup();
});

describe('WorkAccountCard: office-worker "Sign-in method" copy contract', () => {
  it('shows the authorized heading and both method descriptions (Windows Hello active)', async () => {
    vi.mocked(host.getWamConfigState).mockResolvedValue(config('ready'));
    vi.mocked(host.getSessionProviderStatus).mockResolvedValue(status('windows_hello'));

    render(createElement(WorkAccountCard));
    await screen.findByRole('heading', { name: /^Sign-in method$/i });

    expect(
      screen.getByText('Use your face, fingerprint, or PIN to unlock PAX Cookbook on this PC.'),
    ).toBeTruthy();
    expect(
      screen.getByText(
        "Use your Microsoft work account to unlock PAX Cookbook. You'll sign in once each time PAX Cookbook starts.",
      ),
    ).toBeTruthy();
    // The active method is labelled "Current method"; the inactive one offers a
    // single plain action.
    expect(screen.getByText('Current method')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Use work account' })).toBeTruthy();

    assertNoProhibited(customerText());
  });

  it('offers "Use Windows Hello" when the work account is active, no prohibited terms', async () => {
    vi.mocked(host.getWamConfigState).mockResolvedValue(config('ready'));
    vi.mocked(host.getSessionProviderStatus).mockResolvedValue(status('work_account'));

    render(createElement(WorkAccountCard));
    await screen.findByRole('button', { name: 'Use Windows Hello' });

    assertNoProhibited(customerText());
  });

  it('offers "Set up work account" (no prohibited terms) when work account is not ready', async () => {
    vi.mocked(host.getWamConfigState).mockResolvedValue(config('not_configured'));
    vi.mocked(host.getSessionProviderStatus).mockResolvedValue(status('windows_hello'));

    render(createElement(WorkAccountCard));
    await screen.findByRole('button', { name: 'Set up work account' });

    assertNoProhibited(customerText());
  });

  it('shows a plain repair message (no prohibited terms) when the method needs recovery', async () => {
    vi.mocked(host.getWamConfigState).mockResolvedValue(config('ready'));
    vi.mocked(host.getSessionProviderStatus).mockResolvedValue(status('recovery_required'));

    render(createElement(WorkAccountCard));
    await screen.findByText(/Your saved sign-in method couldn't be read/);

    assertNoProhibited(customerText());
  });

  it('keeps switch-failure messages in plain language (no prohibited terms)', async () => {
    vi.mocked(host.getWamConfigState).mockResolvedValue(config('ready'));
    vi.mocked(host.getSessionProviderStatus).mockResolvedValue(status('work_account'));
    // Available-but-not-registered, then the enrollment ceremony is cancelled:
    // the office worker sees "Nothing changed. Work account is still your
    // sign-in method." — a bounded, plain-language outcome.
    vi.mocked(host.selectWindowsHelloProvider).mockResolvedValue({
      ok: false,
      reason: 'windows_hello_enrollment_required',
      selectedProvider: null,
    });
    vi.mocked(host.requestWindowsHelloEnrollmentFromNative).mockResolvedValue('cancelled');

    render(createElement(WorkAccountCard));
    const button = await screen.findByRole('button', { name: 'Use Windows Hello' });
    fireEvent.click(button);

    await screen.findByText('Nothing changed. Work account is still your sign-in method.');

    assertNoProhibited(customerText());
  });
});
