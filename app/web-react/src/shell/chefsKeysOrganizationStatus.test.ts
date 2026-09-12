/**
 * Cycle 04 — Chef's Keys workspace: organization authorization status surface.
 *
 * The workspace shows a RESTRAINED, READ-ONLY status line for the FUTURE
 * organization-provided keys, driven only by the broker's authorization gate.
 * These tests prove, per bounded state:
 *   1. the correct plain-language copy appears (or nothing, for not_configured);
 *   2. the surface never claims a key is available (authorization != availability);
 *   3. the surface exposes NO mutation control (no button / input / select);
 *   4. no policy path, enum token, or identifier leaks into the visible text.
 *
 * The host layer is mocked so the render is deterministic and touches no broker,
 * credential vault, registry, or network.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createElement } from 'react';
import { render, cleanup, waitFor } from '@testing-library/react';

vi.mock('../host/chefKeys', () => ({
  listChefKeys: vi.fn(),
  createChefKey: vi.fn(),
  updateChefKey: vi.fn(),
  deleteChefKey: vi.fn(),
  testChefKey: vi.fn(),
}));

vi.mock('../host/systemInfo', () => ({
  getLockState: vi.fn(),
  getSignInProtection: vi.fn(),
}));

import * as chefKeysHost from '../host/chefKeys';
import * as systemInfo from '../host/systemInfo';
import { ChefsKeysWorkspace } from './ChefsKeysWorkspace';

type OrgState = 'not_configured' | 'disabled' | 'authorized_not_provisioned' | 'unavailable';

function listResponse(orgState: OrgState | null) {
  return {
    ok: true,
    status: 200,
    data: {
      chefKeys: [],
      organizationKeys:
        orgState === null
          ? undefined
          : {
              state: orgState,
              reason: orgState,
              readOnly: true,
              certificateOnly: true,
              inventoryLoaded: false,
            },
    },
    error: null,
    reason: null,
    field: null,
    message: null,
    rawText: '',
    networkError: null,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(systemInfo.getLockState).mockResolvedValue({
    ok: true,
    data: { locked: false, state: 'unlocked', inactivityTimeoutMinutes: null },
  });
  vi.mocked(systemInfo.getSignInProtection).mockResolvedValue({
    ok: true,
    data: { passkeyRegistered: false, userVerification: null },
  });
});

afterEach(() => {
  cleanup();
});

function orgStatusEl(): HTMLElement | null {
  return document.querySelector('.dvw-keys__orgstatus');
}

async function renderWithOrgState(orgState: OrgState | null): Promise<void> {
  vi.mocked(chefKeysHost.listChefKeys).mockResolvedValue(listResponse(orgState) as never);
  render(createElement(ChefsKeysWorkspace));
  await waitFor(() => expect(chefKeysHost.listChefKeys).toHaveBeenCalled());
}

describe('ChefsKeysWorkspace: organization authorization status', () => {
  it('renders nothing for not_configured', async () => {
    await renderWithOrgState('not_configured');
    await waitFor(() => expect(orgStatusEl()).toBeNull());
  });

  it('renders nothing when the broker omits organizationKeys', async () => {
    await renderWithOrgState(null);
    // Give the effect a tick, then confirm no org-status surface exists.
    await waitFor(() => expect(chefKeysHost.listChefKeys).toHaveBeenCalled());
    expect(orgStatusEl()).toBeNull();
  });

  it('shows a quiet "turned off by policy" line for disabled, with no controls', async () => {
    await renderWithOrgState('disabled');
    const el = await waitFor(() => {
      const found = orgStatusEl();
      expect(found).not.toBeNull();
      return found!;
    });
    expect(el.textContent?.toLowerCase()).toContain('turned off');
    assertNoControls(el);
    assertNoLeaks(el);
  });

  it('shows enabled-by-policy + not-connected for authorized_not_provisioned, never claiming availability', async () => {
    await renderWithOrgState('authorized_not_provisioned');
    const el = await waitFor(() => {
      const found = orgStatusEl();
      expect(found).not.toBeNull();
      return found!;
    });
    const text = (el.textContent ?? '').toLowerCase();
    expect(text).toContain('enabled by your organization');
    expect(text).toContain('not connected yet');
    // Must NOT overclaim that a usable key exists.
    expect(text).not.toContain('available');
    expect(text).not.toContain('ready to use');
    expect(text).not.toContain('you can use');
    assertNoControls(el);
    assertNoLeaks(el);
  });

  it('shows a needs-attention + contact-admin line for unavailable, with no controls', async () => {
    await renderWithOrgState('unavailable');
    const el = await waitFor(() => {
      const found = orgStatusEl();
      expect(found).not.toBeNull();
      return found!;
    });
    const text = (el.textContent ?? '').toLowerCase();
    expect(text).toContain('needs attention');
    expect(text).toContain('contact your administrator');
    assertNoControls(el);
    assertNoLeaks(el);
  });
});

function assertNoControls(el: HTMLElement): void {
  expect(el.querySelector('button')).toBeNull();
  expect(el.querySelector('input')).toBeNull();
  expect(el.querySelector('select')).toBeNull();
  expect(el.querySelector('textarea')).toBeNull();
  expect(el.querySelector('a[href]')).toBeNull();
}

function assertNoLeaks(el: HTMLElement): void {
  const text = el.textContent ?? '';
  // No registry path / policy location.
  expect(text).not.toContain('HKEY');
  expect(text).not.toContain('SOFTWARE\\Policies');
  expect(text).not.toContain('\\');
  // No raw enum / reason-code identifier (snake_case) surfaces to the user.
  expect(text.toLowerCase()).not.toMatch(/[a-z]{2,}_[a-z]{2,}/);
  // No identifiers.
  expect(text.toLowerCase()).not.toContain('tenant');
  expect(text.toLowerCase()).not.toContain('client id');
  expect(text.toLowerCase()).not.toContain('thumbprint');
}
