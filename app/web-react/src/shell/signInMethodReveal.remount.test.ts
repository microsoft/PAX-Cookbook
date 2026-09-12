/**
 * cycle-02r6r — Sign-in method reveal REMOUNT RACE reproduction + repair test.
 *
 * FALSIFYING TEST (written first, against the CURRENT public API). It reproduces
 * the attended-confirmed defect from cycle 02r6: choosing the TOP-LEVEL shell's
 * Work-account profile menu "Sign-in method" item navigates the embedded React
 * surface to Settings but the Sign-in method disclosure stays COLLAPSED.
 *
 * ROOT CAUSE under test: App.tsx onReveal (~L511-535) runs, synchronously and in
 * one handler, EXACTLY:
 *     setActiveId('settings');
 *     setNavKey((k) => k + 1);        // remounts the keyed Settings subtree
 *     requestSignInMethodReveal();    // one-shot reveal
 * The Settings subtree is rendered with key={`${active.id}:${navKey}`}
 * (App.tsx sectionKey), so the navKey bump fully REMOUNTS WorkAccountCard. The
 * one-shot reveal is delivered to the OUTGOING card's listener (or lost before
 * the async config-state load renders the disclosure), so the replacement card
 * mounts collapsed and never reveals.
 *
 * The `RevealHarness` below mirrors that App behavior faithfully (same three
 * statements, same `settings:${navKey}` remount key) while driving the REAL
 * WorkAccountCard and the REAL reveal coordinator. The heavy host module is
 * mocked at its boundary (as in WorkAccountCard.switch.test.ts) so the section
 * renders in the 'ready' + work_account state.
 *
 * Covered requirements: #1 navigate-to-Settings, #2 real navKey remount,
 * #3 reveal-during-remount not lost, #4 reveal-before-mount consumed by the new
 * card, #5 reveal-after-stable-mount works, #6 consumed at most once, #7 open,
 * #8 summary focus, #9 scrollIntoView, #10 no-reveal stays collapsed,
 * #11 an unmounting listener cannot discard the intent for its replacement.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement, useState, type ReactElement } from 'react';
import { render, fireEvent, waitFor, cleanup, act } from '@testing-library/react';

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
import { requestSignInMethodReveal, __resetSignInMethodRevealForTests } from './signInMethodReveal';

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

function disclosure(container: HTMLElement): HTMLDetailsElement | null {
  return container.querySelector('#signin-method-section');
}

// Faithful reproduction of the App.tsx reveal flow (see file header).
function RevealHarness({ initialView }: { initialView: 'home' | 'settings' }): ReactElement {
  const [view, setView] = useState<'home' | 'settings'>(initialView);
  const [navKey, setNavKey] = useState(0);
  const onReveal = () => {
    setView('settings');
    setNavKey((k) => k + 1);
    requestSignInMethodReveal();
  };
  return createElement(
    'div',
    null,
    createElement('button', { 'data-testid': 'reveal', onClick: onReveal }, 'reveal'),
    createElement('span', { 'data-testid': 'view' }, view),
    createElement('span', { 'data-testid': 'navkey' }, String(navKey)),
    view === 'settings'
      ? createElement('div', { key: `settings:${navKey}` }, createElement(WorkAccountCard))
      : createElement('div', null, 'home-content'),
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  __resetSignInMethodRevealForTests();
  vi.mocked(host.getWamConfigState).mockResolvedValue(readyConfig);
  vi.mocked(host.getSessionProviderStatus).mockResolvedValue(workAccountStatus);
  vi.mocked(host.probePlatformAuthenticatorAvailable).mockResolvedValue(true);
  // jsdom does not implement scrollIntoView; provide a no-op so the real code path runs.
  (Element.prototype as unknown as { scrollIntoView: () => void }).scrollIntoView = () => {};
});

afterEach(() => {
  cleanup();
  __resetSignInMethodRevealForTests();
  vi.restoreAllMocks();
});

describe('Sign-in method reveal — navKey remount race (cycle-02r6r)', () => {
  it('#1/#2/#3/#11 a reveal fired during the navKey remount reaches the replacement card', async () => {
    const { container, getByTestId } = render(createElement(RevealHarness, { initialView: 'settings' }));
    // The card is already mounted (user is on Settings) — this is the racing case.
    await waitFor(() => expect(disclosure(container)).not.toBeNull());
    expect(disclosure(container)!.open).toBe(false);

    fireEvent.click(getByTestId('reveal'));

    // #1 navigated to Settings, #2 the navKey bumped (subtree remounts).
    expect(getByTestId('view').textContent).toBe('settings');
    expect(getByTestId('navkey').textContent).toBe('1');

    // #3/#11 the reveal survives the remount and reaches the currently-mounted card.
    await waitFor(() => {
      const el = disclosure(container);
      expect(el).not.toBeNull();
      expect(el!.open).toBe(true);
    });
  });

  it('#4 a reveal requested before the card mounts is consumed by the newly mounted card', async () => {
    const { container, getByTestId } = render(createElement(RevealHarness, { initialView: 'home' }));
    // Card is NOT mounted yet.
    expect(disclosure(container)).toBeNull();

    fireEvent.click(getByTestId('reveal'));

    await waitFor(() => {
      const el = disclosure(container);
      expect(el).not.toBeNull();
      expect(el!.open).toBe(true);
    });
  });

  it('#5 a reveal requested after the card is stably mounted (no remount) reveals it', async () => {
    const { container } = render(createElement(WorkAccountCard));
    await waitFor(() => expect(disclosure(container)).not.toBeNull());
    expect(disclosure(container)!.open).toBe(false);

    await act(async () => {
      requestSignInMethodReveal();
    });

    await waitFor(() => expect(disclosure(container)!.open).toBe(true));
  });

  it('#6 each reveal request is consumed at most once (no re-open after a manual collapse)', async () => {
    const { container } = render(createElement(WorkAccountCard));
    await waitFor(() => expect(disclosure(container)).not.toBeNull());

    await act(async () => {
      requestSignInMethodReveal();
    });
    await waitFor(() => expect(disclosure(container)!.open).toBe(true));

    // Office worker collapses it again; NO new reveal request is made.
    await act(async () => {
      disclosure(container)!.open = false;
      await Promise.resolve();
    });
    // Let any polling-driven re-render settle; the handled request must not re-fire.
    await act(async () => {
      await Promise.resolve();
    });
    expect(disclosure(container)!.open).toBe(false);
  });

  it('#7/#8/#9 revealing sets open=true, scrolls into view, and focuses the summary', async () => {
    const scrollSpy = vi.fn();
    (Element.prototype as unknown as { scrollIntoView: unknown }).scrollIntoView = scrollSpy;
    const focused: Element[] = [];
    const focusSpy = vi
      .spyOn(HTMLElement.prototype, 'focus')
      .mockImplementation(function (this: HTMLElement) {
        focused.push(this);
      });

    const { container } = render(createElement(WorkAccountCard));
    await waitFor(() => expect(disclosure(container)).not.toBeNull());

    await act(async () => {
      requestSignInMethodReveal();
    });
    await waitFor(() => expect(disclosure(container)!.open).toBe(true));

    expect(scrollSpy).toHaveBeenCalled();
    const summary = disclosure(container)!.querySelector('summary');
    expect(summary).not.toBeNull();
    expect(focused).toContain(summary);

    focusSpy.mockRestore();
  });

  it('#10 normal Settings navigation without a reveal leaves the disclosure collapsed', async () => {
    const { container } = render(createElement(WorkAccountCard));
    await waitFor(() => expect(disclosure(container)).not.toBeNull());
    await act(async () => {
      await Promise.resolve();
    });
    expect(disclosure(container)!.open).toBe(false);
  });
});
