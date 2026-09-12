/**
 * Cycle 14s — Mini-Kitchen Chef's Key selector: the organization group.
 *
 * Cycle 14r proved the immutable PAX engine can select a certificate ONLY by
 * SHA-1 thumbprint, so organization-bound Cook cannot work. These tests prove the
 * SAFE half only:
 *   1. personal and organization keys are rendered as SEPARATE groups;
 *   2. only the DISPLAY NAME is visible — the opaque id is never visible text;
 *   3. an ineligible entry is disabled with exactly "Not ready on this PC";
 *   4. the two bindings are MUTUALLY EXCLUSIVE in both directions;
 *   5. the recipe stores ONLY the opaque organizationKeyId;
 *   6. the group is omitted entirely for a non-certificate mode or an empty list;
 *   7. the group is READ-ONLY — no add / edit / delete / test / repair control;
 *   8. the verbatim not-yet-runnable copy is surfaced when one is bound.
 *
 * The host layer is mocked, so this touches no broker, credential vault,
 * certificate store, registry, or network, and it never runs PAX or a Bake.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createElement } from 'react';
import { render, cleanup, waitFor, fireEvent, screen } from '@testing-library/react';

vi.mock('../../../host/chefKeys', () => ({
  listChefKeys: vi.fn(),
}));

import * as chefKeysHost from '../../../host/chefKeys';
import type { OrganizationKeySelectorItem } from '../../../host/chefKeys';
import type { LiteRecipeAuth } from '../types';
import {
  AuthContextCard,
  ORG_GROUP_LABEL,
  ORG_NOT_READY_LABEL,
  ORG_OPTION_PREFIX,
} from './AuthContextCard';

const ELIGIBLE_ID = 'contoso.managed_key-1';
const INELIGIBLE_ID = 'contoso.managed_key-2';

function listResponse(
  chefKeys: unknown[],
  selectableKeys: OrganizationKeySelectorItem[] | undefined,
) {
  return {
    ok: true,
    status: 200,
    data: {
      chefKeys,
      organizationKeys:
        selectableKeys === undefined
          ? undefined
          : {
              state: 'authorized_provisioned',
              reason: 'provisioned',
              readOnly: true,
              certificateOnly: true,
              inventoryLoaded: true,
              entryCount: selectableKeys.length,
              selectableKeys,
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

const PERSONAL_KEY = {
  id: 'personal-key-1',
  authType: 'AppReg-Certificate',
  displayName: 'My Personal Key',
  tenantId: null,
  clientId: null,
  certThumbprint: null,
  upn: null,
  hasSecret: false,
};

const TWO_ORG_KEYS: OrganizationKeySelectorItem[] = [
  { organizationKeyId: ELIGIBLE_ID, displayName: 'Contoso Managed Key', eligible: true },
  { organizationKeyId: INELIGIBLE_ID, displayName: 'Fabrikam Managed Key', eligible: false },
];

beforeEach(() => {
  vi.clearAllMocks();
});

afterEach(() => {
  cleanup();
});

/** `'omitted'` means the broker returned no selector array at all. */
type SelectorArg = OrganizationKeySelectorItem[] | 'omitted';

async function renderCard(
  value: LiteRecipeAuth,
  selectableKeys: SelectorArg = TWO_ORG_KEYS,
  chefKeys: unknown[] = [PERSONAL_KEY],
): Promise<{ onChange: ReturnType<typeof vi.fn>; select: HTMLSelectElement }> {
  vi.mocked(chefKeysHost.listChefKeys).mockResolvedValue(
    listResponse(chefKeys, selectableKeys === 'omitted' ? undefined : selectableKeys) as never,
  );
  const onChange = vi.fn();
  render(
    createElement(AuthContextCard, {
      value,
      onChange,
      onCreateChefKey: vi.fn(),
    }),
  );
  const select = await waitFor(() => {
    const found = document.getElementById('mk-auth-chefkey');
    expect(found).not.toBeNull();
    return found as HTMLSelectElement;
  });
  return { onChange, select };
}

function certificateAuth(overrides: Partial<LiteRecipeAuth> = {}): LiteRecipeAuth {
  return { mode: 'AppRegistrationCertificate', ...overrides };
}

describe('AuthContextCard: organization key group', () => {
  // 8 — personal and organization keys are separate groups.
  it('renders the organization keys in their own labelled group, separate from personal keys', async () => {
    const { select } = await renderCard(certificateAuth());

    const group = select.querySelector('optgroup');
    expect(group).not.toBeNull();
    expect(group!.getAttribute('label')).toBe(ORG_GROUP_LABEL);
    expect(ORG_GROUP_LABEL).toBe('Provided by your organization');

    // The personal option is a DIRECT child of the select, not of the group.
    const personal = Array.from(select.querySelectorAll('option')).find(
      o => o.value === PERSONAL_KEY.id,
    );
    expect(personal).toBeDefined();
    expect(personal!.parentElement!.tagName.toLowerCase()).toBe('select');

    const organizationOptions = Array.from(group!.querySelectorAll('option'));
    expect(organizationOptions).toHaveLength(2);
  });

  // 9 + 10 — display name only; the opaque id is never visible text.
  it('renders only the display name and never the opaque identifier as visible text', async () => {
    const { select } = await renderCard(certificateAuth());

    const eligible = select.querySelector(
      `option[value="${ORG_OPTION_PREFIX}${ELIGIBLE_ID}"]`,
    ) as HTMLOptionElement;
    expect(eligible.textContent).toBe('Contoso Managed Key');

    // The identifier exists only as an option VALUE, never as rendered text.
    expect(document.body.textContent).not.toContain(ELIGIBLE_ID);
    expect(document.body.textContent).not.toContain(INELIGIBLE_ID);
    expect(document.body.textContent).not.toContain(ORG_OPTION_PREFIX);
  });

  // 13 — an ineligible entry cannot be selected.
  it('disables an ineligible entry with exactly the approved label', async () => {
    const { select } = await renderCard(certificateAuth());

    const ineligible = select.querySelector(
      `option[value="${ORG_OPTION_PREFIX}${INELIGIBLE_ID}"]`,
    ) as HTMLOptionElement;
    expect(ineligible.disabled).toBe(true);
    expect(ineligible.textContent).toContain(ORG_NOT_READY_LABEL);
    expect(ORG_NOT_READY_LABEL).toBe('Not ready on this PC');

    const eligible = select.querySelector(
      `option[value="${ORG_OPTION_PREFIX}${ELIGIBLE_ID}"]`,
    ) as HTMLOptionElement;
    expect(eligible.disabled).toBe(false);
  });

  // 12 + 15 — selecting an organization key clears the personal binding and
  // stores ONLY the opaque identifier.
  it('stores only the opaque organization id and clears any personal binding', async () => {
    const { onChange, select } = await renderCard(
      certificateAuth({ chefKeyId: PERSONAL_KEY.id, tenantId: 'tenant-value' }),
    );

    fireEvent.change(select, { target: { value: ORG_OPTION_PREFIX + ELIGIBLE_ID } });

    expect(onChange).toHaveBeenCalledTimes(1);
    const next = onChange.mock.calls[0][0] as LiteRecipeAuth;
    expect(next.organizationKeyId).toBe(ELIGIBLE_ID);
    expect(next.chefKeyId).toBeUndefined();
    expect(next.mode).toBe('AppRegistrationCertificate');
    // Nothing else is copied out of the organization entry.
    expect(Object.keys(next).sort()).toEqual(
      ['chefKeyId', 'mode', 'organizationKeyId', 'tenantId'].sort(),
    );
    expect(next.tenantId).toBe('tenant-value');
    expect(JSON.stringify(next)).not.toContain('Contoso');
  });

  // 11 — selecting a personal key clears the organization binding.
  it('clears the organization binding when a personal key is selected', async () => {
    const { onChange, select } = await renderCard(
      certificateAuth({ organizationKeyId: ELIGIBLE_ID }),
    );

    fireEvent.change(select, { target: { value: PERSONAL_KEY.id } });

    const next = onChange.mock.calls[0][0] as LiteRecipeAuth;
    expect(next.chefKeyId).toBe(PERSONAL_KEY.id);
    expect(next.organizationKeyId).toBeUndefined();
  });

  it('reflects a bound organization key as the selected option', async () => {
    const { select } = await renderCard(certificateAuth({ organizationKeyId: ELIGIBLE_ID }));
    expect(select.value).toBe(ORG_OPTION_PREFIX + ELIGIBLE_ID);
  });

  // 6 (UI half) — the group is omitted entirely when it does not apply.
  it('renders no organization group for a non-certificate sign-in mode', async () => {
    await renderCard({ mode: 'AppRegistrationSecret' });
    expect(document.querySelector('optgroup')).toBeNull();
    expect(document.body.textContent).not.toContain(ORG_GROUP_LABEL);
  });

  it('renders no organization group when the broker lists nothing selectable', async () => {
    await renderCard(certificateAuth(), []);
    expect(document.querySelector('optgroup')).toBeNull();
    expect(document.body.textContent).not.toContain(ORG_GROUP_LABEL);
  });

  it('renders no organization group when the broker omits the selector entirely', async () => {
    await renderCard(certificateAuth(), 'omitted');
    expect(document.querySelector('optgroup')).toBeNull();
  });

  // 14 — the organization group is strictly read-only.
  it('offers no organization add, edit, delete, test, or repair control', async () => {
    const { select } = await renderCard(certificateAuth({ organizationKeyId: ELIGIBLE_ID }));

    const group = select.querySelector('optgroup')!;
    expect(group.querySelectorAll('button, input, a, textarea')).toHaveLength(0);

    // The only button on the card remains the personal "create" action.
    const buttons = Array.from(document.querySelectorAll('button')).map(b => b.textContent ?? '');
    expect(buttons).toEqual(["+ Create new Chef's Key"]);
    for (const forbidden of ['Edit', 'Delete', 'Remove', 'Test', 'Repair', 'Refresh', 'Install']) {
      expect(document.body.textContent).not.toContain(forbidden);
    }
  });

  // 18 — the verbatim not-yet-runnable copy, and nothing about engine internals.
  it('surfaces the verbatim not-yet-runnable copy for a bound organization key', async () => {
    const { select } = await renderCard(certificateAuth({ organizationKeyId: ELIGIBLE_ID }));

    const note = screen.getByTestId('mk-auth-org-not-runnable');
    expect(note.textContent).toContain(
      'This organization-provided certificate is not yet available for Bakes.',
    );
    expect(note.textContent).toContain(
      'PAX Cookbook is waiting for a fail-closed engine certificate selector.',
    );

    // The organization copy never names an engine internal, never says
    // misconfigured, and never tells the customer to repair or reselect.
    const organizationCopy =
      (note.textContent ?? '') + (select.querySelector('optgroup')?.textContent ?? '');
    for (const forbidden of [
      'SHA-1',
      'SHA1',
      'SHA-256',
      'SHA256',
      'thumbprint',
      'Thumbprint',
      'store fallback',
      'misconfigured',
      'reselect',
      'repair',
    ]) {
      expect(organizationCopy).not.toContain(forbidden);
    }
  });

  // 24 — personal behaviour is unchanged.
  it('leaves the personal binding path unchanged', async () => {
    const { onChange, select } = await renderCard(certificateAuth(), 'omitted');

    fireEvent.change(select, { target: { value: PERSONAL_KEY.id } });
    const next = onChange.mock.calls[0][0] as LiteRecipeAuth;
    expect(next.chefKeyId).toBe(PERSONAL_KEY.id);
    expect(next.organizationKeyId).toBeUndefined();
    expect(screen.queryByTestId('mk-auth-org-not-runnable')).toBeNull();
  });
});
