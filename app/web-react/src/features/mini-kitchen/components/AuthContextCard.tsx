import { useEffect, useState } from 'react';
import type { AuthMode, LiteRecipeAuth } from '../types';
import { MiniKitchenSectionCard } from './MiniKitchenSectionCard';
import { MiniKitchenField } from './MiniKitchenField';
import { DashboardReqBadge, USER_INFO_RUN_SCOPES } from './DashboardRequirement';
import { listChefKeys, type ChefKeyItem } from '../../../host/chefKeys';
import { requestShellSection } from '../../../shell/shellNav';
import {
  CK_AUTH_TYPE_FOR_MODE,
  applyAuthModeChange,
  applyAuthChefKeyChange,
  applyAuthTenantChange,
} from '../lib/builderAuthTransforms';

// Scheduling-eligibility indicator (Decision 3, informational only). Whether a
// recipe can run on a schedule depends on its auth mode and whether a Chef's Key
// is bound; this text never gates save or readiness. Exported so the Schedule
// card reuses the exact per-mode "Schedulable …" wording.
export function schedulingIndicator(
  mode: AuthMode,
  hasBoundKey: boolean,
): { text: string; tone: 'ok' | 'warn' } {
  switch (mode) {
    case 'WebLogin':
      return hasBoundKey
        ? { text: 'Schedulable \u2014 you must respond to MFA at this computer.', tone: 'ok' }
        : { text: 'Not schedulable \u2014 bind a Chef\u2019s Key to enable scheduling.', tone: 'warn' };
    case 'DeviceCode':
      return hasBoundKey
        ? { text: 'Schedulable \u2014 you\u2019ll receive a sign-in code via notification.', tone: 'ok' }
        : { text: 'Not schedulable \u2014 bind a Chef\u2019s Key to enable scheduling.', tone: 'warn' };
    case 'AppRegistrationSecret':
    case 'AppRegistrationCertificate':
      return { text: 'Schedulable \u2014 fully unattended.', tone: 'ok' };
    default:
      return { text: '', tone: 'ok' };
  }
}

interface AuthContextCardProps {
  value: LiteRecipeAuth;
  onChange: (next: LiteRecipeAuth) => void;
}

const AUTH_MODES: ReadonlyArray<{
  id: AuthMode;
  title: string;
  desc: string;
  switchHint: string;
}> = [
  {
    id: 'WebLogin',
    title: 'Web login (default)',
    desc: 'Interactive browser sign-in. PAX uses this when -Auth is omitted.',
    switchHint: 'No -Auth switch is emitted. Default behavior.',
  },
  {
    id: 'DeviceCode',
    title: 'Device code',
    desc: 'PAX prints a device-code prompt at runtime. Use on headless or scheduled runs that still need an interactive sign-in.',
    switchHint: 'Adds -Auth DeviceCode.',
  },
  {
    id: 'AppRegistrationSecret',
    title: 'App registration (client secret)',
    desc: 'Non-interactive Entra app sign-in. The rendered command embeds a placeholder for the client secret — you replace it with the real value on the machine that runs the command.',
    switchHint: 'Adds -Auth AppRegistration -TenantId <tenant> -ClientId <client> -ClientSecret <CLIENT_SECRET>.',
  },
  {
    id: 'AppRegistrationCertificate',
    title: 'App registration (certificate)',
    desc: 'Non-interactive Entra app sign-in using a certificate that already lives in the runtime account cert store.',
    switchHint: 'Adds -Auth AppRegistration -TenantId <tenant> -ClientId <client> -ClientCertificateThumbprint <thumbprint>.',
  },
];

export function AuthContextCard({ value, onChange }: AuthContextCardProps) {
  const mode = value.mode;
  const isAppReg =
    mode === 'AppRegistrationSecret' || mode === 'AppRegistrationCertificate';

  // Chef's Keys are loaded once for the binding dropdown. Metadata only --
  // listChefKeys never returns a secret (CK-1). A load failure leaves the list
  // empty; the user can still save (binding is not a save requirement) and open
  // Chef's Keys to add one.
  const [chefKeys, setChefKeys] = useState<ChefKeyItem[] | null>(null);
  const [keysFailed, setKeysFailed] = useState(false);
  // True after a mode switch removed a previously bound Chef's Key, so the
  // change is surfaced rather than silent. Cleared once the user binds a key
  // again (or the field shows a bound key).
  const [keyClearedNote, setKeyClearedNote] = useState(false);
  useEffect(() => {
    let alive = true;
    listChefKeys()
      .then(res => {
        if (!alive) return;
        if (res.ok && res.data) {
          setChefKeys(res.data.chefKeys);
        } else {
          setKeysFailed(true);
          setChefKeys([]);
        }
      })
      .catch(() => {
        if (!alive) return;
        setKeysFailed(true);
        setChefKeys([]);
      });
    return () => {
      alive = false;
    };
  }, []);

  const ckType = CK_AUTH_TYPE_FOR_MODE[mode];
  const matchingKeys = (chefKeys ?? []).filter(k => k.authType === ckType);
  const boundId = value.chefKeyId ?? '';
  const sched = schedulingIndicator(mode, boundId.length > 0);

  return (
    <MiniKitchenSectionCard
      id="mk-auth"
      title="Authentication"
      subtitle="How the eventual PAX run signs in to Microsoft Graph."
      helpText="PAX Cookbook does not test sign-in, validate tenant ids, or store secrets here. The auth mode is saved with the recipe and determines how it signs in to Microsoft Graph when the recipe runs later."
      titleBadge={<DashboardReqBadge scopes={USER_INFO_RUN_SCOPES} />}
    >
      <MiniKitchenField label="Auth mode" htmlFor="mk-auth-mode">
        <div
          className="mk-radio-cards"
          role="radiogroup"
          aria-label="Auth mode"
          id="mk-auth-mode"
        >
          {AUTH_MODES.map(m => {
            const inputId = `mk-auth-mode-${m.id}`;
            const selected = mode === m.id;
            return (
              <label
                key={m.id}
                htmlFor={inputId}
                className={'mk-radio-card' + (selected ? ' mk-radio-card--selected' : '')}
              >
                <input
                  type="radio"
                  id={inputId}
                  name="mk-auth-mode"
                  value={m.id}
                  className="mk-radio-card__input"
                  checked={selected}
                  onChange={() => {
                    // Switching mode clears any bound Chef's Key (it can no
                    // longer match the new mode). Surface a note when a key was
                    // actually removed so the change is never silent.
                    setKeyClearedNote(Boolean(value.chefKeyId));
                    onChange(applyAuthModeChange(value, m.id));
                  }}
                />
                <span className="mk-radio-card__title">{m.title}</span>
                <span className="mk-radio-card__desc">{m.desc}</span>
                <span className="mk-radio-card__switch-hint">{m.switchHint}</span>
              </label>
            );
          })}
        </div>
      </MiniKitchenField>
      <MiniKitchenField
        label="Chef's Key"
        htmlFor="mk-auth-chefkey"
        hint={
          isAppReg
            ? 'Bind a saved Chef\u2019s Key. It carries the application (client) id, certificate thumbprint, and any secret \u2014 the secret never leaves Windows. Required before this recipe is ready to run.'
            : 'Optionally bind a saved Chef\u2019s Key so this recipe can run on a schedule. Leave it on \u201Csign in manually\u201D for interactive runs.'
        }
      >
        {chefKeys === null ? (
          <p className="settings-note">Loading your Chef&apos;s Keys&hellip;</p>
        ) : (
          <>
            <select
              id="mk-auth-chefkey"
              className="mk-input"
              value={boundId}
              onChange={e => {
                setKeyClearedNote(false);
                const selectedKey =
                  matchingKeys.find(k => k.id === e.target.value) ?? null;
                onChange(applyAuthChefKeyChange(value, e.target.value, selectedKey));
              }}
            >
              <option value="">
                {isAppReg
                  ? 'Select a Chef\u2019s Key\u2026'
                  : 'No Chef\u2019s Key \u2014 sign in manually each time'}
              </option>
              {matchingKeys.map(k => (
                <option key={k.id} value={k.id}>
                  {k.displayName}
                </option>
              ))}
            </select>
            {matchingKeys.length === 0 ? (
              <p className="settings-note">
                {keysFailed
                  ? 'Could not load your Chef\u2019s Keys.'
                  : 'No matching Chef\u2019s Keys for this sign-in mode yet.'}
              </p>
            ) : null}
            {keyClearedNote && boundId.length === 0 ? (
              <p className="settings-note">
                {'Chef\u2019s Key removed \u2014 it doesn\u2019t match the selected sign-in type.'}
              </p>
            ) : null}
            <div className="mk-auth-card__create">
              <button
                type="button"
                className="mk-preview-boundary__btn"
                onClick={() => requestShellSection('chefskeys')}
              >
                + Create new Chef&apos;s Key
              </button>
            </div>
          </>
        )}
      </MiniKitchenField>
      {isAppReg ? (
        <MiniKitchenField
          label="Tenant ID"
          htmlFor="mk-auth-tenant"
          hint="Entra tenant GUID. Maps to -TenantId."
          required
        >
          <input
            id="mk-auth-tenant"
            type="text"
            className="mk-input mk-input--code"
            value={value.tenantId ?? ''}
            placeholder="00000000-0000-0000-0000-000000000000"
            autoComplete="off"
            spellCheck={false}
            aria-required={true}
            onChange={e => onChange(applyAuthTenantChange(value, e.target.value))}
          />
        </MiniKitchenField>
      ) : null}
      <p
        className={
          'mk-callout ' + (sched.tone === 'ok' ? 'mk-callout--info' : 'mk-callout--warning')
        }
      >
        {sched.text}
      </p>
      {mode === 'AppRegistrationSecret' ? (
        <p className="mk-callout mk-callout--info">
          The client secret is stored in the bound Chef&apos;s Key (Windows Credential
          Manager) and is never shown here or written into the recipe.
        </p>
      ) : null}
    </MiniKitchenSectionCard>
  );
}
