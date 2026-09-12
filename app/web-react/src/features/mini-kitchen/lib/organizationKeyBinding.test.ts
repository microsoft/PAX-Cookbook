/**
 * Cycle 14s — organization key binding: pure transforms and lossless round-trip.
 *
 * Proves the Recipe carries the OPAQUE organization identifier and NOTHING else,
 * that the personal and organization bindings are mutually exclusive in both
 * directions, and that translate / import / export / scrub all preserve exactly
 * that one reference. Nothing here fetches, spawns, signs, or renders a command.
 */
import { describe, it, expect } from 'vitest';
import {
  applyAuthModeChange,
  applyAuthChefKeyChange,
  applyAuthOrganizationKeyChange,
} from './builderAuthTransforms';
import { translateLiteRecipeToFullRecipe } from './translateLiteRecipeToFullRecipe';
import { fullRecipeToState } from './fullRecipeToState';
import { scrubSavedRecipeState } from './recipeScrub';
import { createDefaultMiniKitchenRecipe } from './defaultRecipe';
import type { LiteRecipeAuth, MiniKitchenRecipeState } from '../types';

const ORG_ID = 'contoso.managed_key-1';

function organizationState(): MiniKitchenRecipeState {
  const state = createDefaultMiniKitchenRecipe();
  return {
    ...state,
    identity: { ...state.identity, name: 'Organization bound' },
    auth: { mode: 'AppRegistrationCertificate', organizationKeyId: ORG_ID },
    destinations: {
      ...state.destinations,
      fact: { ...state.destinations.fact, mode: 'write-new', path: 'C:\\PAX\\audit.csv' },
    },
  };
}

describe('builderAuthTransforms: organization binding', () => {
  // 12 — selecting an organization key clears the personal binding.
  it('binds only the opaque id and clears any personal Chef\u2019s Key', () => {
    const value: LiteRecipeAuth = {
      mode: 'AppRegistrationCertificate',
      chefKeyId: 'personal-1',
      tenantId: 'tenant-1',
    };
    const next = applyAuthOrganizationKeyChange(value, ORG_ID);

    expect(next.organizationKeyId).toBe(ORG_ID);
    expect(next.chefKeyId).toBeUndefined();
    expect(next.mode).toBe('AppRegistrationCertificate');
    expect(next.tenantId).toBe('tenant-1');
  });

  it('unbinds on an empty selection', () => {
    const next = applyAuthOrganizationKeyChange(
      { mode: 'AppRegistrationCertificate', organizationKeyId: ORG_ID },
      '',
    );
    expect(next.organizationKeyId).toBeUndefined();
    expect(next.chefKeyId).toBeUndefined();
  });

  // 11 — selecting a personal key clears the organization binding.
  it('clears the organization binding when a personal key is bound', () => {
    const next = applyAuthChefKeyChange(
      { mode: 'AppRegistrationCertificate', organizationKeyId: ORG_ID },
      'personal-1',
      null,
    );
    expect(next.chefKeyId).toBe('personal-1');
    expect(next.organizationKeyId).toBeUndefined();
  });

  it('keeps the organization binding only while the mode stays certificate-only', () => {
    const bound: LiteRecipeAuth = {
      mode: 'AppRegistrationCertificate',
      organizationKeyId: ORG_ID,
    };
    expect(applyAuthModeChange(bound, 'AppRegistrationCertificate').organizationKeyId).toBe(ORG_ID);
    for (const mode of ['WebLogin', 'DeviceCode', 'AppRegistrationSecret'] as const) {
      expect(applyAuthModeChange(bound, mode).organizationKeyId).toBeUndefined();
    }
  });
});

describe('organization binding: translate / round-trip / scrub', () => {
  // 15 — the recipe persists the opaque id and nothing else.
  it('translates to an auth block carrying only mode and the opaque id', () => {
    const candidate = translateLiteRecipeToFullRecipe(organizationState()).fullRecipeCandidate!;
    const auth = candidate.auth as unknown as Record<string, unknown>;

    expect(auth.organizationKeyId).toBe(ORG_ID);
    expect(auth.chefKeyId).toBeUndefined();
    expect(Object.keys(auth).sort()).toEqual(['mode', 'organizationKeyId']);

    const json = JSON.stringify(candidate);
    for (const forbidden of ['thumbprint', 'Thumbprint', 'certificateSha256', 'clientSecret']) {
      expect(json).not.toContain(forbidden);
    }
  });

  it('never emits both bindings when a personal key is also present', () => {
    const state = organizationState();
    state.auth = {
      mode: 'AppRegistrationCertificate',
      chefKeyId: 'personal-1',
      organizationKeyId: ORG_ID,
    };
    const candidate = translateLiteRecipeToFullRecipe(state).fullRecipeCandidate!;
    const auth = candidate.auth as unknown as Record<string, unknown>;
    expect(auth.chefKeyId).toBe('personal-1');
    expect(auth.organizationKeyId).toBeUndefined();
  });

  // 16 — export -> import round-trips the opaque id, and only the opaque id.
  it('round-trips the opaque id through the full recipe and back', () => {
    const candidate = translateLiteRecipeToFullRecipe(organizationState()).fullRecipeCandidate!;
    const back = fullRecipeToState(candidate as unknown as Record<string, unknown>);

    expect(back.ok).toBe(true);
    expect(back.state!.auth.organizationKeyId).toBe(ORG_ID);
    expect(back.state!.auth.chefKeyId).toBeUndefined();
    expect(back.state!.auth.mode).toBe('AppRegistrationCertificate');
  });

  // 16 — the export scrub preserves the reference (it is not a secret).
  it('preserves the opaque id through the export scrub', () => {
    const { state, warnings } = scrubSavedRecipeState(organizationState());
    expect(state.auth.organizationKeyId).toBe(ORG_ID);
    expect(warnings.map(w => w.path)).not.toContain('state.auth.organizationKeyId');
  });
});
