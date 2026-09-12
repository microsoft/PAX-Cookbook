/**
 * Cycle 04 — host contract for the organization Chef's Keys authorization status.
 *
 * `listChefKeys()` must surface the additive, read-only `organizationKeys`
 * status object emitted by the broker's managed-keys authorization gate WITHOUT
 * disturbing the existing personal `chefKeys` array, and must remain fully
 * backward compatible with a broker response that omits the field. The status is
 * status ONLY: a bounded `state`, an opaque `reason`, the constant read-only /
 * certificate-only markers, an `inventoryLoaded` flag (true ONLY when
 * provisioned), and a bounded non-negative `entryCount` present ONLY in the
 * provisioned state. It carries no key, identifier, or secret.
 *
 * The host layer talks to the broker over global `fetch`, so these tests stub
 * `fetch` and assert the parsed projection.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { listChefKeys, type OrganizationKeysStatus } from './chefKeys';

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('chefKeys host contract: organization authorization status', () => {
  it('surfaces the authorized-not-provisioned status alongside the personal array', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        jsonResponse({
          chefKeys: [],
          organizationKeys: {
            state: 'authorized_not_provisioned',
            reason: 'authorized_not_provisioned',
            readOnly: true,
            certificateOnly: true,
            inventoryLoaded: false,
          },
        }),
      ),
    );

    const res = await listChefKeys();

    expect(res.ok).toBe(true);
    expect(res.data).not.toBeNull();
    const org = res.data!.organizationKeys as OrganizationKeysStatus;
    expect(org.state).toBe('authorized_not_provisioned');
    expect(org.readOnly).toBe(true);
    expect(org.certificateOnly).toBe(true);
    // Authorization is never availability.
    expect(org.inventoryLoaded).toBe(false);
    // The personal array is unchanged and independent.
    expect(res.data!.chefKeys).toEqual([]);
  });

  it('carries no key, identifier, or secret in the organization status', async () => {
    const orgStatus = {
      state: 'authorized_not_provisioned',
      reason: 'authorized_not_provisioned',
      readOnly: true,
      certificateOnly: true,
      inventoryLoaded: false,
    };
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(jsonResponse({ chefKeys: [], organizationKeys: orgStatus })),
    );

    const res = await listChefKeys();
    const org = res.data!.organizationKeys as unknown as Record<string, unknown>;

    // A closed, bounded key set — nothing that could be an inventory/identifier/secret.
    expect(Object.keys(org).sort()).toEqual(
      ['certificateOnly', 'inventoryLoaded', 'readOnly', 'reason', 'state'].sort(),
    );
    for (const forbidden of [
      'keys',
      'items',
      'count',
      'clientSecret',
      'secret',
      'thumbprint',
      'certThumbprint',
      'tenantId',
      'clientId',
      'upn',
      'privateKey',
      'token',
    ]) {
      expect(org).not.toHaveProperty(forbidden);
    }
  });

  it.each([
    ['not_configured'],
    ['disabled'],
    ['authorized_not_provisioned'],
    ['unavailable'],
    ['untrusted'],
    ['invalid'],
  ])('accepts the bounded non-provisioned state %s with inventoryLoaded:false and no entryCount', async (state) => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        jsonResponse({
          chefKeys: [],
          organizationKeys: {
            state,
            reason: state,
            readOnly: true,
            certificateOnly: true,
            inventoryLoaded: false,
          },
        }),
      ),
    );

    const res = await listChefKeys();
    const org = res.data!.organizationKeys as OrganizationKeysStatus;
    expect(org.state).toBe(state);
    // A non-provisioned state never asserts availability and carries no count.
    expect(org.inventoryLoaded).toBe(false);
    expect(org.entryCount).toBeUndefined();
  });

  it('surfaces the authorized_provisioned state with inventoryLoaded:true and a bounded entryCount', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        jsonResponse({
          chefKeys: [],
          organizationKeys: {
            state: 'authorized_provisioned',
            reason: 'provisioned',
            readOnly: true,
            certificateOnly: true,
            inventoryLoaded: true,
            entryCount: 3,
          },
        }),
      ),
    );

    const res = await listChefKeys();
    const org = res.data!.organizationKeys as OrganizationKeysStatus;
    expect(org.state).toBe('authorized_provisioned');
    expect(org.readOnly).toBe(true);
    expect(org.certificateOnly).toBe(true);
    // Availability is asserted ONLY in the provisioned state.
    expect(org.inventoryLoaded).toBe(true);
    expect(org.entryCount).toBe(3);
    // The bounded count is the ONLY additive field; still no key/identifier/secret.
    const raw = res.data!.organizationKeys as unknown as Record<string, unknown>;
    expect(Object.keys(raw).sort()).toEqual(
      ['certificateOnly', 'entryCount', 'inventoryLoaded', 'readOnly', 'reason', 'state'].sort(),
    );
    for (const forbidden of [
      'keys',
      'items',
      'clientSecret',
      'secret',
      'thumbprint',
      'certThumbprint',
      'tenantId',
      'clientId',
      'upn',
      'privateKey',
      'token',
      'organizationKeyId',
      'displayName',
      'path',
    ]) {
      expect(raw).not.toHaveProperty(forbidden);
    }
  });

  it('remains backward compatible when the broker omits organizationKeys', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(jsonResponse({ chefKeys: [] })),
    );

    const res = await listChefKeys();
    expect(res.ok).toBe(true);
    expect(res.data!.chefKeys).toEqual([]);
    expect(res.data!.organizationKeys).toBeUndefined();
  });
});
