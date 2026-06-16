/**
 * Chef's Keys workspace.
 *
 * The cookbook-branded surface for managing local sign-in and key context for
 * recipes that need Microsoft 365 data. PAX Cookbook never stores credential
 * material itself: every Chef's Key is created, read, updated, and deleted in
 * the Windows Credential Manager per-user vault through the broker's
 * authenticated `/api/v1/chef-keys*` routes.
 *
 * This surface lists saved Chef's Keys and lets the user add, edit, test, and
 * delete them for the four sign-in types (Web sign-in, Device code, App
 * registration with a secret, App registration with a certificate). It keeps
 * the educational context about what stays on this PC and how Chef's Keys
 * relates to recipe readiness, plus the live app-lock and Windows Hello status.
 *
 * Constraint 14: a client secret is WRITE-ONLY. It is entered when creating or
 * editing a key and is never displayed again - the list shows only whether a
 * secret is set. No secret value is ever read back, rendered, or logged. The
 * certificate option stores only a thumbprint reference; the certificate and
 * its private key stay in the Windows certificate store.
 *
 * Hard boundaries: no cook / bake / run, no PAX invocation, and no Microsoft
 * Graph call - the Test action performs local/structural validation only.
 */
import {
  useCallback,
  useEffect,
  useId,
  useState,
  type FormEvent,
  type MouseEvent,
} from 'react';
import { SectionHeader } from './components/SectionHeader';
import {
  getLockState,
  getSignInProtection,
  type LockStateInfo,
  type SignInProtectionInfo,
} from '../host/systemInfo';
import {
  listChefKeys,
  createChefKey,
  updateChefKey,
  deleteChefKey,
  testChefKey,
  type ChefKeyItem,
  type ChefKeyAuthType,
  type ChefKeyWriteRequest,
  type ChefKeyResponse,
  type ChefKeyTestBody,
} from '../host/chefKeys';
import { CopyButton } from '../features/mini-kitchen/components/CopyButton';
import {
  IconKey,
  IconPlus,
  IconPencil,
  IconTrash,
  IconShieldCheck,
  IconCheckCircle,
  IconAlertCircle,
  IconX,
} from './CookbookIllustrations';

interface FormState {
  mode: 'closed' | 'create' | 'edit';
  editingId: string | null;
  authType: ChefKeyAuthType;
  displayName: string;
  tenantId: string;
  clientId: string;
  certThumbprint: string;
  upn: string;
  clientSecret: string;
}

const CLOSED_FORM: FormState = {
  mode: 'closed',
  editingId: null,
  authType: 'WebLogin',
  displayName: '',
  tenantId: '',
  clientId: '',
  certThumbprint: '',
  upn: '',
  clientSecret: '',
};

const AUTH_TYPE_OPTIONS: ReadonlyArray<{ value: ChefKeyAuthType; label: string }> = [
  { value: 'WebLogin', label: 'Web sign-in' },
  { value: 'DeviceCode', label: 'Device code' },
  { value: 'AppReg-Secret', label: 'App registration with a secret' },
  { value: 'AppReg-Certificate', label: 'App registration with a certificate' },
];

function authTypeLabel(authType: string): string {
  const match = AUTH_TYPE_OPTIONS.find((o) => o.value === authType);
  return match ? match.label : authType;
}

// Short badge labels for the list rows, where the full sign-in-type name is too
// long to sit beside a name in a narrow column. The detail pane shows the full
// label from authTypeLabel.
const AUTH_TYPE_SHORT: Record<ChefKeyAuthType, string> = {
  WebLogin: 'Web sign-in',
  DeviceCode: 'Device code',
  'AppReg-Secret': 'App \u00b7 Secret',
  'AppReg-Certificate': 'App \u00b7 Cert',
};

function authTypeShort(authType: string): string {
  return AUTH_TYPE_SHORT[normalizeAuthType(authType)];
}

// Auth-type pill class: each sign-in type maps to a tinted chip modifier so the
// badge color reinforces the sign-in type at a glance.
const AUTH_TYPE_CHIP: Record<ChefKeyAuthType, string> = {
  WebLogin: 'chip--web',
  DeviceCode: 'chip--device',
  'AppReg-Secret': 'chip--secret',
  'AppReg-Certificate': 'chip--cert',
};

function authTypeChip(authType: string): string {
  return AUTH_TYPE_CHIP[normalizeAuthType(authType)];
}

function normalizeAuthType(authType: string): ChefKeyAuthType {
  const match = AUTH_TYPE_OPTIONS.find((o) => o.value === authType);
  return match ? match.value : 'WebLogin';
}

function usesUpn(authType: ChefKeyAuthType): boolean {
  return authType === 'WebLogin' || authType === 'DeviceCode';
}

function usesAppReg(authType: ChefKeyAuthType): boolean {
  return authType === 'AppReg-Secret' || authType === 'AppReg-Certificate';
}

function buildWriteRequest(form: FormState): ChefKeyWriteRequest {
  const req: ChefKeyWriteRequest = {
    authType: form.authType,
    displayName: form.displayName.trim(),
  };
  if (usesUpn(form.authType)) {
    if (form.upn.trim()) {
      req.upn = form.upn.trim();
    }
    if (form.tenantId.trim()) {
      req.tenantId = form.tenantId.trim();
    }
    return req;
  }
  if (form.tenantId.trim()) {
    req.tenantId = form.tenantId.trim();
  }
  if (form.clientId.trim()) {
    req.clientId = form.clientId.trim();
  }
  if (form.authType === 'AppReg-Certificate') {
    if (form.certThumbprint.trim()) {
      req.certThumbprint = form.certThumbprint.trim();
    }
  } else if (form.clientSecret.length > 0) {
    req.clientSecret = form.clientSecret;
  }
  return req;
}

function formatError(res: ChefKeyResponse<unknown>): string {
  if (res.networkError) {
    return 'Could not reach PAX Cookbook. Please try again.';
  }
  if (res.message) {
    return res.message;
  }
  if (res.error) {
    return res.error;
  }
  return 'Something went wrong. Please try again.';
}

type LoadPhase = 'loading' | 'ready' | 'error';

const NOT_REPORTED = 'Not reported by this build';

function lockStatusLabel(phase: LoadPhase, lock: LockStateInfo | null): string {
  if (phase === 'loading') {
    return 'Checking…';
  }
  if (!lock || !lock.state) {
    return NOT_REPORTED;
  }
  return lock.locked ? 'Locked' : 'Unlocked';
}

function passkeyStatusLabel(
  phase: LoadPhase,
  protect: SignInProtectionInfo | null,
): string {
  if (phase === 'loading') {
    return 'Checking…';
  }
  if (!protect) {
    return NOT_REPORTED;
  }
  return protect.passkeyRegistered ? 'Set up on this PC' : 'Not set up yet';
}

// One-line secondary identifier for a key's list row: the sign-in name for
// interactive keys, or the secret/certificate status for app registrations.
function rowMeta(item: ChefKeyItem): string {
  if (usesUpn(normalizeAuthType(item.authType))) {
    return item.upn && item.upn.trim().length > 0 ? item.upn.trim() : 'Interactive sign-in';
  }
  if (item.hasSecret) {
    return 'Secret stored';
  }
  if (item.certThumbprint && item.certThumbprint.trim().length > 0) {
    return 'Certificate';
  }
  return 'Setup incomplete';
}

/**
 * One detail field: a label with either a monospace value plus a Copy button, or
 * a muted "Not set". Only non-secret identifiers (sign-in name, tenant id, client
 * id, certificate thumbprint) are ever passed here; a client secret value is
 * never given to this component - the secret's set/none status is shown
 * separately and the value itself is never read back or rendered (constraint 14).
 */
function KeyField({
  label,
  value,
  copyLabel,
}: {
  label: string;
  value: string | null;
  copyLabel: string;
}) {
  const shown = value && value.trim().length > 0 ? value.trim() : '';
  return (
    <div className="dvw-keys__prop">
      <span className="dvw-keys__prop-label">{label}</span>
      <div className="dvw-keys__prop-value">
        {shown ? (
          <>
            <code className="dvw-keys__mono">{shown}</code>
            <CopyButton text={shown} label={copyLabel} />
          </>
        ) : (
          <span className="dvw-keys__not-set">Not set</span>
        )}
      </div>
    </div>
  );
}

export function ChefsKeysWorkspace() {
  const [phase, setPhase] = useState<LoadPhase>('loading');
  const [lock, setLock] = useState<LockStateInfo | null>(null);
  const [protect, setProtect] = useState<SignInProtectionInfo | null>(null);

  useEffect(() => {
    let cancelled = false;
    setPhase('loading');
    void Promise.all([getLockState(), getSignInProtection()])
      .then(([l, p]) => {
        if (cancelled) {
          return;
        }
        if (l.ok) {
          setLock(l.data);
        }
        if (p.ok) {
          setProtect(p.data);
        }
        setPhase(l.ok || p.ok ? 'ready' : 'error');
      })
      .catch(() => {
        if (!cancelled) {
          setPhase('error');
        }
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const [keys, setKeys] = useState<ChefKeyItem[] | null>(null);
  const [keysPhase, setKeysPhase] = useState<LoadPhase>('loading');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [form, setForm] = useState<FormState>(CLOSED_FORM);
  const [formBusy, setFormBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [testFor, setTestFor] = useState<string | null>(null);
  const [testBody, setTestBody] = useState<ChefKeyTestBody | null>(null);
  const [testError, setTestError] = useState<string | null>(null);
  const formTitleId = useId();

  const loadKeys = useCallback(async () => {
    setKeysPhase('loading');
    const res = await listChefKeys();
    if (res.ok && res.data) {
      setKeys(res.data.chefKeys);
      setKeysPhase('ready');
    } else {
      setKeys(null);
      setKeysPhase('error');
    }
  }, []);

  useEffect(() => {
    void loadKeys();
  }, [loadKeys]);

  // The add/edit form is a modal overlay. Escape closes it through the same
  // path as Cancel and the backdrop, and is inert while a save is in flight.
  useEffect(() => {
    if (form.mode === 'closed') {
      return;
    }
    function onKey(ev: KeyboardEvent) {
      if (ev.key === 'Escape' && !formBusy) {
        ev.preventDefault();
        setForm(CLOSED_FORM);
        setFormError(null);
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [form.mode, formBusy]);

  // When the add/edit modal opens, move focus into the dialog (the display-name
  // field) so keyboard users land inside it rather than on the trigger behind
  // the backdrop, matching the app's other input modals.
  useEffect(() => {
    if (form.mode === 'closed') {
      return;
    }
    document.getElementById('ck-displayName')?.focus();
  }, [form.mode]);

  function openCreate() {
    setFormError(null);
    setForm({ ...CLOSED_FORM, mode: 'create' });
  }

  function openEdit(item: ChefKeyItem) {
    setFormError(null);
    setForm({
      mode: 'edit',
      editingId: item.id,
      authType: normalizeAuthType(item.authType),
      displayName: item.displayName,
      tenantId: item.tenantId ?? '',
      clientId: item.clientId ?? '',
      certThumbprint: item.certThumbprint ?? '',
      upn: item.upn ?? '',
      clientSecret: '',
    });
  }

  function closeForm() {
    setForm(CLOSED_FORM);
    setFormError(null);
  }

  // Clicking the dimmed area outside the modal closes the form, mirroring
  // Cancel/Escape. Inert while a save is in flight.
  function handleFormBackdrop(event: MouseEvent<HTMLDivElement>) {
    if (event.target === event.currentTarget && !formBusy) {
      closeForm();
    }
  }

  async function submitForm(event: FormEvent) {
    event.preventDefault();
    setFormBusy(true);
    setFormError(null);
    const body = buildWriteRequest(form);
    const res =
      form.mode === 'edit' && form.editingId
        ? await updateChefKey(form.editingId, body)
        : await createChefKey(body);
    setFormBusy(false);
    if (res.ok) {
      closeForm();
      void loadKeys();
    } else {
      setFormError(formatError(res));
    }
  }

  async function runTest(id: string) {
    setTestFor(id);
    setTestBody(null);
    setTestError(null);
    setBusyId(id);
    const res = await testChefKey(id);
    setBusyId(null);
    if (res.ok && res.data) {
      setTestBody(res.data);
    } else {
      setTestError(formatError(res));
    }
  }

  async function doDelete(id: string) {
    setBusyId(id);
    setListError(null);
    const res = await deleteChefKey(id);
    setBusyId(null);
    if (res.ok) {
      setConfirmDeleteId(null);
      if (testFor === id) {
        setTestFor(null);
        setTestBody(null);
        setTestError(null);
      }
      void loadKeys();
    } else {
      setListError(formatError(res));
    }
  }

  // The selected key resolves from the live list, so when the selected key is
  // deleted (and the list reloads without it) this naturally falls back to null
  // and the detail pane shows the placeholder hint again.
  const selectedItem =
    selectedId && keys ? keys.find((k) => k.id === selectedId) ?? null : null;

  const lockStatus = lockStatusLabel(phase, lock);
  const passkeyStatus = passkeyStatusLabel(phase, protect);
  const verification =
    protect?.userVerification && protect.userVerification.toLowerCase() === 'required'
      ? 'Windows Hello required'
      : protect?.userVerification ?? NOT_REPORTED;

  // Global sign-in status for this PC, shown at the bottom of the right pane in
  // both the selected-key and empty states. Status text only - no key value,
  // token, or secret (constraint 14).
  const systemInfo = (
    <div className="dvw-keys__section dvw-keys__sysinfo">
      <h3 className="dvw-keys__section-head dvw-keys__section-head--muted">This PC</h3>
      <div className="dvw-keys__props">
        <div className="dvw-keys__prop">
          <span className="dvw-keys__prop-label">Windows Hello unlock</span>
          <div className="dvw-keys__prop-value">
            <span className="dvw-keys__sysval">{passkeyStatus}</span>
          </div>
        </div>
        <div className="dvw-keys__prop">
          <span className="dvw-keys__prop-label">Verification</span>
          <div className="dvw-keys__prop-value">
            <span className="dvw-keys__sysval">{verification}</span>
          </div>
        </div>
        <div className="dvw-keys__prop">
          <span className="dvw-keys__prop-label">App lock</span>
          <div className="dvw-keys__prop-value">
            <span className="dvw-keys__sysval">{lockStatus}</span>
          </div>
        </div>
      </div>
      <p className="dvw-keys__sysnote">
        Status details only. No key value, token, or secret is shown.
      </p>
    </div>
  );

  return (
    <section aria-labelledby="view-chefskeys">
      <SectionHeader
        title="Chef's Keys"
        titleId="view-chefskeys"
        lede="Local sign-in and key context for recipes that need Microsoft 365 data."
        helpTopic="cookbookChefsKeys"
        accent="var(--c-purple)"
      />

      <div className="dvw-keys">
        <div className="dvw-commandbar" role="group" aria-label="Chef's Keys actions">
          <button
            type="button"
            className="dvw-btn dvw-btn--primary"
            onClick={openCreate}
            disabled={form.mode !== 'closed'}
          >
            <IconPlus className="dvw-btn__icon" />
            <span>Add a Chef's Key</span>
          </button>
        </div>

        <div className="dvw-keys__cols">
          <section className="dvw-card dvw-list" aria-labelledby="dvw-keys-list-h">
            <header className="dvw-card__head">
              <h2 id="dvw-keys-list-h" className="dvw-card__title">
                Your Chef's Keys
              </h2>
            </header>

            {keysPhase === 'loading' ? (
              <p className="dvw-card__muted" role="status">
                Loading your Chef's Keys…
              </p>
            ) : keysPhase === 'error' ? (
              <p className="dvw-card__muted" role="status">
                Could not load your Chef's Keys.{' '}
                <button type="button" className="dvw-link" onClick={() => void loadKeys()}>
                  Try again
                </button>
              </p>
            ) : keys && keys.length > 0 ? (
              <ul className="dvw-list__rows" role="listbox" aria-label="Saved Chef's Keys">
                {keys.map((item) => {
                  const selected = item.id === selectedId;
                  return (
                    <li className="dvw-list__item" key={item.id}>
                      <button
                        type="button"
                        role="option"
                        aria-selected={selected}
                        className={
                          'dvw-list__row' + (selected ? ' dvw-list__row--selected' : '')
                        }
                        onClick={() => setSelectedId(item.id)}
                      >
                        <span className="dvw-list__row-icon" aria-hidden="true">
                          <IconKey />
                        </span>
                        <span className="dvw-list__row-text">
                          <span className="dvw-list__row-name">
                            {item.displayName || '(no name)'}
                          </span>
                          <span className="dvw-list__row-meta">{rowMeta(item)}</span>
                        </span>
                        <span className={`chip ${authTypeChip(item.authType)} dvw-keys__row-badge`}>
                          {authTypeShort(item.authType)}
                        </span>
                      </button>
                    </li>
                  );
                })}
              </ul>
            ) : (
              <p className="dvw-card__muted" role="status">
                No Chef's Keys yet. Add one to get started.
              </p>
            )}
          </section>

          <section className="dvw-card dvw-detail dvw-keys__right">
          {selectedItem ? (
            <>
              <div className="dvw-keys__toolbar">
                <div className="dvw-keys__toolbar-titles">
                  <h2 className="dvw-keys__detail-title">
                    {selectedItem.displayName || '(no name)'}
                  </h2>
                  <span className={`chip ${authTypeChip(selectedItem.authType)} dvw-keys__detail-badge`}>
                    {authTypeLabel(selectedItem.authType)}
                  </span>
                </div>
                <div
                  className="dvw-keys__toolbar-actions"
                  role="group"
                  aria-label="Chef's Key actions"
                >
                  <button
                    type="button"
                    className="dvw-btn"
                    onClick={() => void runTest(selectedItem.id)}
                    disabled={busyId === selectedItem.id}
                  >
                    <IconShieldCheck className="dvw-btn__icon" />
                    <span>Test</span>
                  </button>
                  <button
                    type="button"
                    className="dvw-btn"
                    onClick={() => openEdit(selectedItem)}
                    disabled={form.mode !== 'closed'}
                  >
                    <IconPencil className="dvw-btn__icon" />
                    <span>Edit</span>
                  </button>
                  <button
                    type="button"
                    className="dvw-btn dvw-btn--danger-ghost"
                    onClick={() => {
                      setListError(null);
                      setConfirmDeleteId(selectedItem.id);
                    }}
                  >
                    <IconTrash className="dvw-btn__icon" />
                    <span>Delete</span>
                  </button>
                </div>
              </div>

              {listError ? (
                <p className="dvw-keys__error" role="alert">
                  {listError}
                </p>
              ) : null}

              {confirmDeleteId === selectedItem.id ? (
                <div
                  className="dvw-keys__confirm"
                  role="alertdialog"
                  aria-label="Confirm delete"
                >
                  <span>
                    Remove {selectedItem.displayName || selectedItem.id}? It will be deleted
                    from Windows-backed storage.
                  </span>
                  <div className="dvw-keys__confirm-actions">
                    <button
                      type="button"
                      className="dvw-btn dvw-btn--danger"
                      onClick={() => void doDelete(selectedItem.id)}
                      disabled={busyId === selectedItem.id}
                    >
                      {busyId === selectedItem.id ? 'Deleting…' : 'Delete'}
                    </button>
                    <button
                      type="button"
                      className="dvw-btn dvw-btn--ghost"
                      onClick={() => setConfirmDeleteId(null)}
                      disabled={busyId === selectedItem.id}
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              ) : null}

              <div className="dvw-keys__section">
                <h3 className="dvw-keys__section-head">Properties</h3>
                <div className="dvw-keys__props">
                {usesUpn(normalizeAuthType(selectedItem.authType)) ? (
                  <>
                    <KeyField
                      label="Sign-in name (UPN)"
                      value={selectedItem.upn}
                      copyLabel="Copy sign-in name"
                    />
                    <KeyField
                      label="Tenant ID"
                      value={selectedItem.tenantId}
                      copyLabel="Copy tenant ID"
                    />
                  </>
                ) : (
                  <>
                    <KeyField
                      label="Tenant ID"
                      value={selectedItem.tenantId}
                      copyLabel="Copy tenant ID"
                    />
                    <KeyField
                      label="Application (client) ID"
                      value={selectedItem.clientId}
                      copyLabel="Copy application (client) ID"
                    />
                    {normalizeAuthType(selectedItem.authType) === 'AppReg-Certificate' ? (
                      <KeyField
                        label="Certificate thumbprint"
                        value={selectedItem.certThumbprint}
                        copyLabel="Copy certificate thumbprint"
                      />
                    ) : (
                      <div className="dvw-keys__prop">
                        <span className="dvw-keys__prop-label">Client secret</span>
                        <div className="dvw-keys__prop-value">
                          <span
                            className={
                              'dvw-keys__secret ' +
                              (selectedItem.hasSecret
                                ? 'dvw-keys__secret--set'
                                : 'dvw-keys__secret--none')
                            }
                          >
                            {selectedItem.hasSecret ? 'Set' : 'None'}
                          </span>
                        </div>
                      </div>
                    )}
                  </>
                )}
                </div>
              </div>

              {testFor === selectedItem.id && (testBody || testError) ? (
                <div className="dvw-keys__section">
                  <h3 className="dvw-keys__section-head">Test results</h3>
                  {testError ? (
                    <div className="dvw-keys__check-row dvw-keys__check-row--fail">
                      <IconAlertCircle className="dvw-keys__check-icon" aria-hidden="true" />
                      <span className="dvw-keys__check-text">{testError}</span>
                    </div>
                  ) : testBody ? (
                    <>
                      <p
                        className={
                          'dvw-keys__test-summary ' +
                          (testBody.ok
                            ? 'dvw-keys__test-summary--pass'
                            : 'dvw-keys__test-summary--fail')
                        }
                      >
                        {testBody.ok ? 'Checks passed' : 'Checks failed'} — {testBody.reason}
                      </p>
                      <div className="dvw-keys__checks" role="list">
                        {testBody.checks.map((c) => (
                          <div
                            key={c.name}
                            role="listitem"
                            className={
                              'dvw-keys__check-row ' +
                              (c.ok
                                ? 'dvw-keys__check-row--ok'
                                : 'dvw-keys__check-row--fail')
                            }
                          >
                            {c.ok ? (
                              <IconCheckCircle
                                className="dvw-keys__check-icon"
                                aria-hidden="true"
                              />
                            ) : (
                              <IconX className="dvw-keys__check-icon" aria-hidden="true" />
                            )}
                            <span className="dvw-keys__check-text">{c.detail}</span>
                          </div>
                        ))}
                      </div>
                    </>
                  ) : null}
                </div>
              ) : null}

              {systemInfo}
            </>
          ) : (
            <>
              <div className="dvw-keys__hint">
                <span className="dvw-detail__empty-icon" aria-hidden="true">
                  <IconKey />
                </span>
                <p className="dvw-card__muted">
                  Select a Chef's Key to view its details.
                </p>
              </div>

              {systemInfo}
            </>
          )}
          </section>
        </div>
        {form.mode !== 'closed' ? (
          <div className="mini-kitchen-page">
            <div
              className="mk-modal__backdrop"
              role="presentation"
              onClick={handleFormBackdrop}
            >
              <div
                className="mk-modal dvw-keys__form-modal"
                role="dialog"
                aria-modal="true"
                aria-labelledby={formTitleId}
              >
                <header className="mk-modal__head">
                  <h2 id={formTitleId} className="mk-modal__title">
                    {form.mode === 'edit' ? "Edit Chef's Key" : "New Chef's Key"}
                  </h2>
                </header>

                <form className="keys-form" onSubmit={submitForm}>
                  <div className="mk-field">
                    <label className="mk-field__label" htmlFor="ck-authType">
                      <span className="mk-field__label-text">Sign-in type</span>
                    </label>
                    <div className="mk-field__control">
                      <select
                        id="ck-authType"
                        className="mk-input"
                        value={form.authType}
                        disabled={form.mode === 'edit'}
                        onChange={(e) =>
                          setForm((f) => ({ ...f, authType: normalizeAuthType(e.target.value) }))
                        }
                      >
                        {AUTH_TYPE_OPTIONS.map((o) => (
                          <option key={o.value} value={o.value}>
                            {o.label}
                          </option>
                        ))}
                      </select>
                    </div>
                    {form.mode === 'edit' ? (
                      <p className="mk-field__note">
                        The sign-in type can't be changed. Delete and re-create to switch types.
                      </p>
                    ) : null}
                  </div>

                  <div className="mk-field">
                    <label className="mk-field__label" htmlFor="ck-displayName">
                      <span className="mk-field__label-text">Display name</span>
                    </label>
                    <div className="mk-field__control">
                      <input
                        id="ck-displayName"
                        className="mk-input"
                        type="text"
                        maxLength={120}
                        value={form.displayName}
                        onChange={(e) => setForm((f) => ({ ...f, displayName: e.target.value }))}
                      />
                    </div>
                  </div>

                  {usesUpn(form.authType) ? (
                    <>
                      <div className="mk-field">
                        <label className="mk-field__label" htmlFor="ck-upn">
                          <span className="mk-field__label-text">Sign-in name (UPN)</span>
                        </label>
                        <div className="mk-field__control">
                          <input
                            id="ck-upn"
                            className="mk-input"
                            type="text"
                            placeholder="user@domain.com"
                            value={form.upn}
                            onChange={(e) => setForm((f) => ({ ...f, upn: e.target.value }))}
                          />
                        </div>
                      </div>
                      <div className="mk-field">
                        <label className="mk-field__label" htmlFor="ck-tenantId">
                          <span className="mk-field__label-text">Tenant ID</span>
                          <span className="mk-field__optional">optional</span>
                        </label>
                        <div className="mk-field__control">
                          <input
                            id="ck-tenantId"
                            className="mk-input"
                            type="text"
                            value={form.tenantId}
                            onChange={(e) => setForm((f) => ({ ...f, tenantId: e.target.value }))}
                          />
                        </div>
                      </div>
                    </>
                  ) : null}

                  {usesAppReg(form.authType) ? (
                    <>
                      <div className="mk-field">
                        <label className="mk-field__label" htmlFor="ck-tenantId">
                          <span className="mk-field__label-text">Tenant ID</span>
                        </label>
                        <div className="mk-field__control">
                          <input
                            id="ck-tenantId"
                            className="mk-input"
                            type="text"
                            value={form.tenantId}
                            onChange={(e) => setForm((f) => ({ ...f, tenantId: e.target.value }))}
                          />
                        </div>
                      </div>
                      <div className="mk-field">
                        <label className="mk-field__label" htmlFor="ck-clientId">
                          <span className="mk-field__label-text">Application (client) ID</span>
                        </label>
                        <div className="mk-field__control">
                          <input
                            id="ck-clientId"
                            className="mk-input"
                            type="text"
                            value={form.clientId}
                            onChange={(e) => setForm((f) => ({ ...f, clientId: e.target.value }))}
                          />
                        </div>
                      </div>
                    </>
                  ) : null}

                  {form.authType === 'AppReg-Certificate' ? (
                    <div className="mk-field">
                      <label className="mk-field__label" htmlFor="ck-thumb">
                        <span className="mk-field__label-text">Certificate thumbprint</span>
                      </label>
                      <div className="mk-field__control">
                        <input
                          id="ck-thumb"
                          className="mk-input"
                          type="text"
                          value={form.certThumbprint}
                          onChange={(e) => setForm((f) => ({ ...f, certThumbprint: e.target.value }))}
                        />
                      </div>
                      <p className="mk-field__note">
                        The certificate and its private key stay in the Windows certificate store. PAX
                        finds it automatically in either the current-user (CurrentUser\My) or the
                        machine (LocalMachine\My) store. Only the thumbprint is saved here.
                      </p>
                    </div>
                  ) : null}

                  {form.authType === 'AppReg-Secret' ? (
                    <div className="mk-field">
                      <label className="mk-field__label" htmlFor="ck-secret">
                        <span className="mk-field__label-text">Client secret</span>
                      </label>
                      <div className="mk-field__control">
                        <input
                          id="ck-secret"
                          className="mk-input"
                          type="password"
                          autoComplete="off"
                          placeholder={form.mode === 'edit' ? 'Leave blank to keep the current secret' : ''}
                          value={form.clientSecret}
                          onChange={(e) => setForm((f) => ({ ...f, clientSecret: e.target.value }))}
                        />
                      </div>
                      <p className="mk-field__note">
                        The secret is stored in Windows-backed storage and is never shown again.
                      </p>
                    </div>
                  ) : null}

                  {formError ? (
                    <p className="keys-form__error" role="alert">
                      {formError}
                    </p>
                  ) : null}

                  <div className="mk-modal__actions">
                    <button
                      type="button"
                      className="dvw-btn dvw-btn--ghost"
                      onClick={closeForm}
                      disabled={formBusy}
                    >
                      Cancel
                    </button>
                    <button type="submit" className="dvw-btn dvw-btn--primary" disabled={formBusy}>
                      {formBusy ? 'Saving…' : form.mode === 'edit' ? 'Save changes' : "Save Chef's Key"}
                    </button>
                  </div>
                </form>
              </div>
            </div>
          </div>
        ) : null}
      </div>
    </section>
  );
}
