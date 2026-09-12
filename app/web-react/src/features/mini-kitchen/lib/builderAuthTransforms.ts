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
import type { ChefKeyItem } from '../../../host/chefKeys';

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
 * the new one, so it must not silently persist. The organization binding is
 * certificate-only, so it is cleared for every mode except
 * `AppRegistrationCertificate`. `tenantId` and any other field are preserved.
 */
export function applyAuthModeChange(value: LiteRecipeAuth, mode: AuthMode): LiteRecipeAuth {
  return {
    ...value,
    mode,
    chefKeyId: undefined,
    organizationKeyId:
      mode === 'AppRegistrationCertificate' ? value.organizationKeyId : undefined,
  };
}

/**
 * Chef's Key `<select>` `onChange`. An empty selection unbinds the key; any
 * other value binds it. Binding a PERSONAL key always CLEARS any organization
 * binding: a recipe may reference at most one key. When a key is bound and it
 * carries a stored tenant id, the recipe tenant id is auto-filled from the key so
 * the operator never has to re-enter a value the key already holds (the client id
 * and any secret stay in the key / Windows Credential Manager and are resolved at
 * run time). Unbinding, or binding a key with no stored tenant id, leaves the
 * existing tenant id and mode untouched.
 */
export function applyAuthChefKeyChange(
  value: LiteRecipeAuth,
  rawSelectValue: string,
  selectedKey?: ChefKeyItem | null,
): LiteRecipeAuth {
  const chefKeyId = rawSelectValue === '' ? undefined : rawSelectValue;
  const tenantId =
    selectedKey && selectedKey.tenantId ? selectedKey.tenantId : value.tenantId;
  return { ...value, chefKeyId, tenantId, organizationKeyId: undefined };
}

/**
 * Organization-key `<optgroup>` selection. Binding an organization key stores the
 * OPAQUE identifier and NOTHING else, and always CLEARS any personal Chef's Key
 * binding. The sign-in mode stays `AppRegistrationCertificate` and the tenant id
 * is untouched: no tenant/client reference, certificate reference, or thumbprint
 * is ever copied out of the organization entry. An empty selection unbinds.
 *
 * Selecting an organization key NEVER makes the recipe runnable -- the broker
 * reports `organization_key_not_yet_runnable` and refuses before Cook/PAX
 * preparation.
 */
export function applyAuthOrganizationKeyChange(
  value: LiteRecipeAuth,
  rawOrganizationKeyId: string,
): LiteRecipeAuth {
  const organizationKeyId = rawOrganizationKeyId === '' ? undefined : rawOrganizationKeyId;
  return { ...value, organizationKeyId, chefKeyId: undefined };
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
