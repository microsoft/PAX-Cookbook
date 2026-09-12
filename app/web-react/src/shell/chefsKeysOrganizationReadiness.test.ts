/**
 * Cycle 13 — Chef's Keys workspace: organization certificate readiness UX.
 *
 * The provisioned organization-managed status block projects the Cycle 12
 * readiness aggregate as BOUNDED, customer-safe sentences. These tests prove:
 *   1. every readiness outcome maps to its exact approved sentence;
 *   2. a zero count is omitted entirely (never rendered as "0");
 *   3. no raw enum token, identifier, reference, thumbprint, subject, issuer,
 *      serial, store, or key-container detail ever reaches the rendered text;
 *   4. the organization block stays read-only (no button / link / input);
 *   5. private-key availability is described in DESKTOP terms only, with the
 *      service caveat shown solely when a private key is unavailable.
 *
 * The host layer is mocked so the render is deterministic and touches no broker,
 * credential vault, certificate store, registry, or network.
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

type OrgStatus = Record<string, unknown>;

function listResponse(organizationKeys: OrgStatus | undefined) {
  return {
    ok: true,
    status: 200,
    data: { chefKeys: [], organizationKeys },
    error: null,
    reason: null,
    field: null,
    message: null,
    rawText: '',
    networkError: null,
  };
}

/** A provisioned aggregate with every count defaulted to a clean zero. */
function provisioned(overrides: OrgStatus): OrgStatus {
  return {
    state: 'authorized_provisioned',
    reason: 'provisioned',
    readOnly: true,
    certificateOnly: true,
    inventoryLoaded: true,
    entryCount: 1,
    resolvedMetadataCount: 0,
    notFoundCount: 0,
    ambiguousCount: 0,
    referenceMissingCount: 0,
    disabledCount: 0,
    catalogUnavailable: false,
    usableCount: 0,
    notYetValidCount: 0,
    expiredCount: 0,
    clientAuthNotAllowedCount: 0,
    digitalSignatureNotAllowedCount: 0,
    unsupportedKeyAlgorithmCount: 0,
    privateKeyUnavailableCount: 0,
    usabilityInvalidCount: 0,
    usabilityUnavailable: false,
    ...overrides,
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

async function renderOrg(organizationKeys: OrgStatus | undefined): Promise<HTMLElement> {
  vi.mocked(chefKeysHost.listChefKeys).mockResolvedValue(listResponse(organizationKeys) as never);
  render(createElement(ChefsKeysWorkspace));
  return waitFor(() => {
    const found = orgStatusEl();
    expect(found).not.toBeNull();
    return found!;
  });
}

/** Renders and settles the workspace without requiring an organization block. */
async function renderShell(organizationKeys: OrgStatus | undefined): Promise<void> {
  vi.mocked(chefKeysHost.listChefKeys).mockResolvedValue(listResponse(organizationKeys) as never);
  render(createElement(ChefsKeysWorkspace));
  await waitFor(() => {
    expect(document.querySelector('.dvw-keys__intro')).not.toBeNull();
  });
  await waitFor(() => {
    expect(vi.mocked(chefKeysHost.listChefKeys)).toHaveBeenCalled();
  });
}

/** Typographic apostrophes render as U+2019; compare against plain text. */
function textOf(el: HTMLElement): string {
  return (el.textContent ?? '').replace(/\u2019/g, "'");
}

async function orgTextFor(organizationKeys: OrgStatus): Promise<string> {
  return textOf(await renderOrg(organizationKeys));
}

/** Raw status tokens and identifier-shaped fragments that must never render. */
const FORBIDDEN_TOKENS = [
  'resolved_metadata_only',
  'client_auth_not_allowed',
  'digital_signature_not_allowed',
  'private_key_unavailable',
  'unsupported_key_algorithm',
  'not_yet_valid',
  'reference_missing',
  'authorized_provisioned',
  'authorized_not_provisioned',
  'not_configured',
  'org-key-',
  'tenant-ref',
  'client-ref',
  'thumbprint',
  'fingerprint',
  'subject',
  'issuer',
  'serial',
  'sha256',
  'EKU',
  'OID',
  'ACL',
  'LocalMachine',
  'CurrentUser',
  'StoreName',
  'key container',
];

function expectNoLeakage(text: string): void {
  for (const token of FORBIDDEN_TOKENS) {
    expect(text.toLowerCase()).not.toContain(token.toLowerCase());
  }
}

describe('ChefsKeysWorkspace: organization certificate readiness', () => {
  // 1 - policy has not authorized organization-provided keys at all.
  it('renders no organization block when organization keys are not configured', async () => {
    await renderShell({
      state: 'not_configured',
      reason: 'not_configured',
      readOnly: true,
      certificateOnly: true,
      inventoryLoaded: false,
    });
    expect(orgStatusEl()).toBeNull();
  });

  it('renders no organization block when the broker omits organization keys', async () => {
    await renderShell(undefined);
    expect(orgStatusEl()).toBeNull();
  });

  it('states that organization-provided keys are turned off by policy', async () => {
    const text = await orgTextFor({
      state: 'disabled',
      reason: 'disabled_by_policy',
      readOnly: true,
      certificateOnly: true,
      inventoryLoaded: false,
    });
    expect(text).toContain(
      "Organization-provided keys are turned off by your organization's policy.",
    );
    expectNoLeakage(text);
  });

  // 2 - authorized by policy, but no managed inventory is connected yet.
  it('states that an authorized policy has no inventory connected yet', async () => {
    const text = await orgTextFor({
      state: 'authorized_not_provisioned',
      reason: 'not_provisioned',
      readOnly: true,
      certificateOnly: true,
      inventoryLoaded: false,
    });
    expect(text).toContain(
      "Organization-provided keys are enabled by your organization's policy.",
    );
    expect(text).toContain(
      'A managed key inventory is not connected yet, so there is nothing to use here for now.',
    );
    expectNoLeakage(text);
  });

  // 3 - the inventory itself could not be read.
  it('states that organization key policy needs attention when the inventory is unavailable', async () => {
    const text = await orgTextFor({
      state: 'unavailable',
      reason: 'inventory_unavailable',
      readOnly: true,
      certificateOnly: true,
      inventoryLoaded: false,
    });
    expect(text).toContain('Organization key policy needs attention.');
    expect(text).toContain('Contact your administrator.');
    expectNoLeakage(text);
  });

  // 4 - untrusted or invalid inventory, with no parser or exception text.
  it.each(['untrusted', 'invalid'])(
    'states that organization key settings need attention when the inventory is %s',
    async (state) => {
      const text = await orgTextFor({
        state,
        reason: 'inventory_rejected',
        readOnly: true,
        certificateOnly: true,
        inventoryLoaded: false,
      });
      expect(text).toContain('Organization key settings need attention.');
      expect(text).toContain('Contact your administrator.');
      expectNoLeakage(text);
    },
  );

  // 5 - readiness could not be evaluated on this PC.
  it.each([
    ['catalogUnavailable', { catalogUnavailable: true }],
    ['usabilityUnavailable', { usabilityUnavailable: true }],
  ])('reports readiness as unchecked when %s', async (_label, overrides) => {
    const el = await renderOrg(provisioned({ entryCount: 2, ...overrides }));
    const text = textOf(el);
    expect(el.getAttribute('data-org-readiness')).toBe('unchecked');
    expect(text).toContain('Certificate readiness could not be checked on this PC.');
    expectNoLeakage(text);
  });

  // 6, 7, 8, 9, 10, 11, 12, 15, 16 - one attention reason per aggregate, shown
  // only when its count is greater than zero, in singular and plural form.
  const REASON_CASES: ReadonlyArray<[string, string, string]> = [
    ['referenceMissingCount', 'One entry does not name a certificate yet.', '2 entries do not name a certificate yet.'],
    ['notFoundCount', 'One certificate was not found on this PC.', '2 certificates were not found on this PC.'],
    [
      'ambiguousCount',
      'One entry matches more than one certificate on this PC.',
      '2 entries match more than one certificate on this PC.',
    ],
    ['notYetValidCount', 'One certificate is not valid yet.', '2 certificates are not valid yet.'],
    ['expiredCount', 'One certificate has expired.', '2 certificates have expired.'],
    [
      'clientAuthNotAllowedCount',
      'The certificate does not explicitly allow client authentication.',
      '2 certificates do not explicitly allow client authentication.',
    ],
    [
      'digitalSignatureNotAllowedCount',
      'The certificate does not explicitly allow digital signatures.',
      '2 certificates do not explicitly allow digital signatures.',
    ],
    [
      'privateKeyUnavailableCount',
      "The certificate's private key is not available to this Windows user.",
      '2 certificates have a private key that is not available to this Windows user.',
    ],
    [
      'unsupportedKeyAlgorithmCount',
      'One certificate uses a key type PAX Cookbook does not support.',
      '2 certificates use a key type PAX Cookbook does not support.',
    ],
    ['usabilityInvalidCount', 'One certificate could not be read.', '2 certificates could not be read.'],
    [
      'disabledCount',
      'One entry is turned off by your organization.',
      '2 entries are turned off by your organization.',
    ],
  ];

  it.each(REASON_CASES)(
    'renders %s only when it is greater than zero, in the matching singular or plural form',
    async (field, singular, plural) => {
      const zero = await orgTextFor(
        provisioned({ entryCount: 4, resolvedMetadataCount: 4, usableCount: 4 }),
      );
      expect(zero).not.toContain(singular);
      expect(zero).not.toContain(plural);
      cleanup();

      const one = await orgTextFor(
        provisioned({ entryCount: 4, resolvedMetadataCount: 4, [field]: 1 }),
      );
      expect(one).toContain(singular);
      expect(one).not.toContain(plural);
      expectNoLeakage(one);
      cleanup();

      const two = await orgTextFor(
        provisioned({ entryCount: 4, resolvedMetadataCount: 4, [field]: 2 }),
      );
      expect(two).toContain(plural);
      expect(two).not.toContain(singular);
      expectNoLeakage(two);
    },
  );

  // 13 - private-key wording is desktop-scoped and identifier-free.
  it('describes an unavailable private key in desktop terms only, with no certificate identifier', async () => {
    const el = await renderOrg(
      provisioned({ entryCount: 1, resolvedMetadataCount: 1, privateKeyUnavailableCount: 1 }),
    );
    const text = textOf(el);
    expect(text).toContain(
      "The certificate's private key is not available to this Windows user.",
    );
    expect(text).toContain(
      'A service may use a separately permissioned key. Desktop availability does not confirm service availability.',
    );
    expectNoLeakage(text);
  });

  // 14 - the service caveat appears only alongside an unavailable private key.
  it('omits the service caveat when no private key is unavailable', async () => {
    const text = await orgTextFor(
      provisioned({ entryCount: 3, resolvedMetadataCount: 3, expiredCount: 1, usableCount: 2 }),
    );
    expect(text).toContain('One certificate has expired.');
    expect(text).not.toContain('A service may use a separately permissioned key.');
    expect(text).not.toContain('Desktop availability does not confirm service availability.');
  });

  // 17 - everything resolved and usable on this PC.
  it('reports that organization-provided certificates are ready on this PC', async () => {
    const el = await renderOrg(
      provisioned({ entryCount: 2, resolvedMetadataCount: 2, usableCount: 2 }),
    );
    const text = textOf(el);
    expect(el.getAttribute('data-org-readiness')).toBe('ready');
    expect(text).toContain('Organization-provided certificates are ready on this PC.');
    expect(text).not.toContain('not ready for desktop use');
    expect(el.querySelector('.dvw-keys__orgstatus-reasons')).toBeNull();
    expectNoLeakage(text);
  });

  // 18 - metadata resolved, but the certificate is not usable here.
  it('reports partial readiness when certificates resolve but are not desktop-ready', async () => {
    const el = await renderOrg(
      provisioned({ entryCount: 1, resolvedMetadataCount: 1, expiredCount: 1 }),
    );
    const text = textOf(el);
    expect(el.getAttribute('data-org-readiness')).toBe('partial');
    expect(text).toContain(
      'Organization-provided certificates were found, but some are not ready for desktop use.',
    );
    expect(text).toContain('One certificate has expired.');
    expectNoLeakage(text);
  });

  it('reports that nothing is ready when no entry resolves to a usable certificate', async () => {
    const el = await renderOrg(provisioned({ entryCount: 2 }));
    const text = textOf(el);
    expect(el.getAttribute('data-org-readiness')).toBe('none');
    expect(text).toContain(
      'Organization-provided certificates are listed, but none are ready to use on this PC.',
    );
    expectNoLeakage(text);
  });

  it('reports an empty inventory when the organization has listed nothing', async () => {
    const el = await renderOrg(provisioned({ entryCount: 0 }));
    const text = textOf(el);
    expect(el.getAttribute('data-org-readiness')).toBe('empty');
    expect(text).toContain('Your organization has not listed any certificates yet.');
    expectNoLeakage(text);
  });

  // 19 - several outcomes at once still produce one bounded block.
  it('summarizes mixed outcomes in a single flat attention list', async () => {
    const el = await renderOrg(
      provisioned({
        entryCount: 7,
        resolvedMetadataCount: 5,
        notFoundCount: 2,
        expiredCount: 1,
        privateKeyUnavailableCount: 3,
        usableCount: 1,
      }),
    );
    const text = textOf(el);
    expect(el.getAttribute('data-org-readiness')).toBe('partial');
    const list = el.querySelector('.dvw-keys__orgstatus-reasons');
    expect(list).not.toBeNull();
    expect(list!.querySelectorAll(':scope > li').length).toBe(3);
    expect(list!.querySelectorAll('ul, ol').length).toBe(0);
    expect(text).toContain('2 certificates were not found on this PC.');
    expect(text).toContain('One certificate has expired.');
    expect(text).toContain(
      '3 certificates have a private key that is not available to this Windows user.',
    );
    expect(text).toContain(
      'A service may use a separately permissioned key. Desktop availability does not confirm service availability.',
    );
    expectNoLeakage(text);
  });

  // 20 - a zero count is omitted entirely, never rendered as "0".
  it('never renders a zero count', async () => {
    const el = await renderOrg(
      provisioned({ entryCount: 3, resolvedMetadataCount: 3, usableCount: 3 }),
    );
    const text = textOf(el);
    expect(text).not.toMatch(/(^|\D)0(\D|$)/);
    expect(el.querySelector('.dvw-keys__orgstatus-reasons')).toBeNull();
  });

  // 21 + 22 - no raw enum token, identifier, reference, or store detail leaks.
  it('never leaks a raw status token, identifier, reference, or store detail', async () => {
    const el = await renderOrg(
      provisioned({
        entryCount: 9,
        resolvedMetadataCount: 8,
        notFoundCount: 1,
        ambiguousCount: 1,
        referenceMissingCount: 1,
        disabledCount: 1,
        usableCount: 1,
        notYetValidCount: 1,
        expiredCount: 1,
        clientAuthNotAllowedCount: 1,
        digitalSignatureNotAllowedCount: 1,
        unsupportedKeyAlgorithmCount: 1,
        privateKeyUnavailableCount: 1,
        usabilityInvalidCount: 1,
      }),
    );
    expectNoLeakage(textOf(el));
    expect(textOf(el)).not.toContain('reason');
    // The only machine-readable hooks are the two bounded data attributes.
    expect(el.querySelectorAll('[title], [aria-label]').length).toBe(0);
  });

  // 23 - the organization block is read-only: no control of any kind.
  it('offers no control in the organization block', async () => {
    const el = await renderOrg(
      provisioned({ entryCount: 4, resolvedMetadataCount: 3, expiredCount: 2, usableCount: 1 }),
    );
    expect(el.querySelectorAll('button').length).toBe(0);
    expect(el.querySelectorAll('a').length).toBe(0);
    expect(el.querySelectorAll('input').length).toBe(0);
    expect(el.querySelectorAll('select, textarea, [role="button"]').length).toBe(0);
    const text = textOf(el).toLowerCase();
    for (const verb of ['fix', 'repair', 'import', 'refresh', 'retry', 'override']) {
      expect(text).not.toContain(verb);
    }
  });

  // 24 - the personal Chef's Keys surface is untouched by this projection.
  it('leaves the personal Chef\u2019s Keys controls unchanged', async () => {
    const el = await renderOrg(
      provisioned({ entryCount: 2, resolvedMetadataCount: 2, usableCount: 2 }),
    );
    const addButton = Array.from(document.querySelectorAll('button')).find(
      (b) => (b.textContent ?? '').includes("Add a Chef's Key"),
    );
    expect(addButton).toBeDefined();
    expect(el.contains(addButton!)).toBe(false);
    expect(document.querySelector('.dvw-keys')).not.toBeNull();
    expect(document.querySelector('.dvw-keys__sysinfo')).not.toBeNull();
  });

  // No readiness claim ever extends past this PC.
  it('never claims Bake, unattended, service, or authentication readiness', async () => {
    const el = await renderOrg(
      provisioned({ entryCount: 2, resolvedMetadataCount: 2, usableCount: 2 }),
    );
    const text = textOf(el).toLowerCase();
    for (const claim of [
      'ready for bake',
      'unattended',
      'service ready',
      'fully configured',
      'authentication verified',
    ]) {
      expect(text).not.toContain(claim);
    }
  });
});
