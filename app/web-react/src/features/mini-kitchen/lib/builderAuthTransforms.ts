/**
 * Pure authentication-input transforms for the Mini-Kitchen builder.
 *
 * These mirror, one-for-one, the inline `onChange` updaters the Authentication
 * card (`components/AuthContextCard.tsx`) fires when the user picks a sign-in
 * mode, binds a Chef's Key, or types a tenant id. They are factored out so the
 * card and the headless interaction simulation drive the exact same logic, and
 * so the mismatch guard has a single source of truth for the mode -> Chef's Key
 * type map. Nothing here fetches, stores a secret, or renders a command.
 */

import type { AuthMode, LiteRecipeAuth } from '../types';

/**
 * Recipe `auth.mode` -> Chef's Key `authType` (CK-1). The Authentication card
 * offers only the Chef's Keys whose type matches the selected mode. The C#
 * layer keeps its own copy (`ChefKeyModel.CkAuthTypeForRecipeMode`) for the
 * readiness and save gates; this is the frontend half of the same binding map.
 * `ManagedIdentity` (and any other value) binds to no Chef's Key.
 */
export const CK_AUTH_TYPE_FOR_MODE: Partial<Record<AuthMode, string>> = {
  WebLogin: 'WebLogin',
  DeviceCode: 'DeviceCode',
  AppRegistrationSecret: 'AppReg-Secret',
  AppRegistrationCertificate: 'AppReg-Certificate',
};

/**
 * Auth-mode radio `onChange`. Selecting a sign-in mode replaces the mode and
 * clears any bound Chef's Key: a key bound for the prior mode can never match
 * the new one, so it must not silently persist. `tenantId` and any other field
 * are preserved.
 */
export function applyAuthModeChange(value: LiteRecipeAuth, mode: AuthMode): LiteRecipeAuth {
  return { ...value, mode, chefKeyId: undefined };
}

/**
 * Chef's Key `<select>` `onChange`. An empty selection unbinds the key; any
 * other value binds it. Mode and tenant id are preserved.
 */
export function applyAuthChefKeyChange(
  value: LiteRecipeAuth,
  rawSelectValue: string,
): LiteRecipeAuth {
  return { ...value, chefKeyId: rawSelectValue === '' ? undefined : rawSelectValue };
}

/**
 * Tenant id `<input>` `onChange`. An empty field clears the tenant; any other
 * value sets it. Mode and bound Chef's Key are preserved.
 */
export function applyAuthTenantChange(value: LiteRecipeAuth, rawInput: string): LiteRecipeAuth {
  return { ...value, tenantId: rawInput === '' ? undefined : rawInput };
}

/**
 * True when a Chef's Key of `ckAuthType` matches the recipe sign-in `mode`.
 * Used by the builder to detect (and clear) a bound key that no longer matches
 * the selected mode.
 */
export function ckTypeMatchesMode(ckAuthType: string | undefined, mode: AuthMode): boolean {
  if (ckAuthType === undefined) {
    return false;
  }
  return CK_AUTH_TYPE_FOR_MODE[mode] === ckAuthType;
}
