# PAX Cookbook — Native Windows Sign-In: Customer Administrator Setup

PAX Cookbook can unlock its broker session using **native Windows account
sign-in** (the Windows Web Account Manager) against **your own** Microsoft Entra
tenant. This document is for the tenant administrator who sets that up.

Each customer **owns the registration in their own tenant**. The registration
uses the sign-in audience **"Accounts in any organizational directory and
personal Microsoft accounts" (`AzureADandPersonalMicrosoftAccount`)**, which is
the audience the native Windows account picker requires. This is a **picker
compatibility choice, not an authorization grant**: PAX Cookbook validates every
sign-in and unlocks the session only for **your one configured tenant**. No
sign-in ever leaves the native Windows experience for a web browser. See
**Section 2** and the **External sign-in footprint** note below for exactly what
this audience does and does not mean.

---

## 1. Prerequisites

- A Microsoft Entra tenant you administer.
- An administrator account able to create app registrations **and** grant
  tenant-wide admin consent — for example **Global Administrator**, or
  **Privileged Role Administrator**, or the combination of **Cloud Application
  Administrator** and **Application Administrator**.
- Azure CLI installed and signed in to that tenant:

  ```
  az login --tenant <your-tenant-id>
  ```

  If your tenant has no Azure subscription, add `--allow-no-subscriptions`.

- Windows 10/11 devices that are joined or registered to your tenant (so native
  Windows sign-in can broker the account).

---

## 2. What gets created, and the exact permission

Run the guided helper:

```
pwsh -File tools/admin/New-PaxCookbookEntraWamSetup.ps1 -Action Provision
```

It creates **one** registration in your tenant:

- A **public-client registration** — the app the user signs in with. It has a
  native Windows broker redirect, **no exposed API**, and **no credential**. It
  requests one delegated Microsoft Graph permission, **`User.Read`**. Its
  sign-in audience is **`AzureADandPersonalMicrosoftAccount`** ("Accounts in any
  organizational directory and personal Microsoft accounts"), because the native
  Windows account picker is only offered for that audience.

It then creates a service principal for it and grants **one tenant-wide
administrator consent for exactly Microsoft Graph `User.Read`**. There is no
second registration, no custom exposed API, and no `access_as_user` scope.

Although the registration's audience is broad enough for the native picker, PAX
Cookbook's **authorization is single-configured-tenant**: it accepts a sign-in
only from the one tenant you configure and rejects every other organization and
all personal Microsoft accounts in-process. The setup result the app imports
carries a `single-configured-tenant` authorization marker, and PAX Cookbook
**refuses to import any result that does not assert it**.

**Exact permission meaning.** `User.Read` is a delegated sign-in permission that
lets the signed-in user read **their own basic profile**. PAX Cookbook uses it
only to assert *"this user signed in"* so it can unlock the local session. PAX
Cookbook is token-free: it **never calls Microsoft Graph with the sign-in token
and never stores a token**. No client secret, certificate, or federated
credential is created.

The helper prints the only values PAX Cookbook needs (all non-secret
configuration, not secrets):

- tenant id
- public-client application id

Pass `-OutputPath <file>` to also write them to a file you choose. These values
are configuration; they are never written into PAX Cookbook's own program files.

### External sign-in footprint

Because the registration audience accepts personal Microsoft accounts and
accounts from any organizational directory, users **outside your tenant** may
see this application in the Windows account picker. If such a user attempts to
sign in, Microsoft may create a **consumer or foreign-tenant service principal**
for the app in **their own** directory — an object the registration owner
**cannot prevent or remove**. This is an unavoidable consequence of a
picker-compatible audience.

This footprint grants those users **no access to PAX Cookbook**: every sign-in is
validated in-process and the session unlocks only for your one configured
tenant. The footprint is disclosed here so you can make an informed choice before
provisioning. To preview the exact proposed change and this disclosure **without
modifying anything**, run the read-only preflight:

```
pwsh -File tools/admin/New-PaxCookbookEntraWamSetup.ps1 -Action Preflight
```

The preflight performs reads only and prints a migration plan; it makes no
directory change and emits no tenant, client, application, user, or secret.

---

## 3. Conditional Access and MFA

Because sign-in uses standard Microsoft Entra interactive authentication through
the Windows broker, your **Conditional Access policies and MFA requirements
apply normally**. If a policy requires MFA, a compliant device, or a managed
device, the user satisfies it during the native sign-in exactly as they would for
any other Entra application. PAX Cookbook adds no bypass and stores no credential.

---

## 4. What the user sees (native Windows sign-in)

- On the **first** sign-in after setup, the user may see a single native
  Windows account prompt. Because tenant-wide admin consent is already in place,
  a separate "Permissions requested" page is not expected.
- On **subsequent** sign-ins, the session unlock is silent.
- A separate **"Allow my organization to manage this device"** prompt is a
  Windows *device-registration* experience, not a PAX Cookbook permission. It
  appears when the device is not yet registered to your tenant and is governed by
  your device policies; it is unrelated to the `User.Read` sign-in.

**One sign-in per session.** The user authenticates **once per broker session**.
Starting a Bake or resuming one does **not** prompt for identity again. A locked
action requires the user to unlock first and then explicitly reconfirm the
action; unlocking never starts or resumes work on its own.

---

## 5. Revocation

To revoke access without removing the registration:

- Remove the tenant-wide consent (the Microsoft Graph `User.Read` admin grant)
  for the public client, or
- Disable the public-client service principal (set it to not allow sign-in), or
- Use Conditional Access to block the application.

Any of these stops native sign-in immediately. Recovery is an explicit provider
change in Setup Repair (see section 8) — there is no automatic switch.

---

## 6. Replacing or recovering the configuration

If the registration changes (for example a planned rotation), re-run the helper
to provision a fresh registration, then update PAX Cookbook's configuration
values to the new tenant id / client id. PAX Cookbook **fails closed** if the
configuration is missing, incomplete, or points at a different tenant than the
account signing in — it will report that administrator setup is required rather
than sign in against the wrong tenant.

---

## 7. Complete deprovisioning

To remove everything this setup created:

```
pwsh -File tools/admin/New-PaxCookbookEntraWamSetup.ps1 -Action Deprovision
```

The helper removes **only** the registration it created (identified by an
ownership tag it stamped on it) together with its service principal and its
tenant-wide consent. It does not touch any other object. After deprovisioning,
switch PAX Cookbook back to Windows Hello through Setup Repair or Settings
(section 8).

---

## 8. Mutually-exclusive providers and recovery

PAX Cookbook uses **exactly one** selected session provider at a time — **Windows
Hello OR the work account**, never both, with **no automatic fallback** in either
direction. Setup selects the initial provider; Settings switches it atomically
from an authenticated session; and **Setup Repair** recovers or explicitly
replaces a broken provider **before the app unlocks**.

When the work account is the selected provider, the lock screen shows only native
work-account sign-in and Windows Hello is absent — so sign-in works on VMs,
terminal servers, and policy-restricted machines where Hello is unavailable. If
native sign-in becomes unavailable (revoked consent, deleted registration, or
missing configuration), the lock screen shows a bounded recovery state and the
administrator repairs the work account or explicitly switches to Windows Hello in
Setup Repair. Nothing silently falls back to the other provider.

---

## 9. Summary of guarantees

- One customer-owned registration whose sign-in audience is picker-compatible
  (`AzureADandPersonalMicrosoftAccount`), with **single-configured-tenant
  authorization**: sign-in is accepted only from your one configured tenant and
  every other organization and all personal accounts are rejected in-process.
- External footprint disclosed: because the audience is broad, outside users may
  see the app in the picker and may cause a foreign-tenant service principal in
  their own directory that the owner cannot remove; this grants them no access.
- Native Windows sign-in only; no web-browser sign-in.
- One delegated Microsoft Graph `User.Read` permission (basic profile); no custom
  exposed API and no `access_as_user` scope.
- No client secret, certificate, or federated credential.
- Token-free: PAX Cookbook never calls Microsoft Graph with the sign-in token and
  stores no token.
- One sign-in per session; no identity prompt for Bake or Resume.
- Conditional Access and MFA apply normally.
- Fails closed on missing or wrong-tenant configuration.
- Providers are mutually exclusive (Windows Hello OR work account); Setup selects,
  Settings switches, Setup Repair recovers before unlock; no automatic fallback.

---

## 10. Machine-policy detection (read-only; no capability activation)

PAX Cookbook can *detect* a machine-wide administrative policy so it can present
the correct experience on a managed device. Detection is **read-only** and does
**not** turn on any capability. It never installs the Windows service, never
provisions or manages a Chef's Key, and never changes the sign-in provider.
Presence of managed policy only changes how the app *describes* itself; the
underlying managed capabilities remain out of scope until implemented.

### Authoritative source

The single authoritative source is the machine hive under
`HKEY_LOCAL_MACHINE\SOFTWARE\Policies\PAXCookbook`. Only an administrator (or
device-management tooling) can write there. The per-user hive and environment or
command-line inputs are **never** consulted as a policy source. Detection opens
this key **read-only**; it never creates, writes, or deletes any value or subkey.

### The three policy dimensions

When configured, all of the following are read from the key above:

| Value | Type | Meaning |
| --- | --- | --- |
| `PolicySchemaVersion` | `REG_DWORD` | Must be `1`; any other value is treated as unsupported. |
| `DesktopAccess` | `REG_SZ` | `self_service` (personal) or `organization_managed`. |
| `ManagedChefKeys` | `REG_SZ` | `disabled` or `enabled` (detection only). |
| `WindowsService` | `REG_SZ` | `disabled` or `enabled` (detection only). |

### Fail-closed semantics

- **Absence / default:** if the key is missing or contains none of the four
  values, the device is treated as ordinary personal self-service — the safe
  default. No managed capability is implied.
- **Invalid:** partial, wrong-typed, oversized, unsupported-schema, unknown-value,
  or self-contradictory policy resolves to a bounded *needs-attention* state with
  every managed capability forced off. Detection never guesses a corrected value.
- **Untrusted or unreadable:** if the source cannot be trusted or read
  (for example access denied), detection fails closed to the same off state.

Only a complete, correctly typed, schema-1, `organization_managed` policy can
report a managed capability as enabled, and even then this cycle performs
**detection only** — it does not activate the Windows service or managed keys.

### Normal-user limitations

A standard (non-administrator) user cannot author or alter this policy; it is a
device-wide administrative control. On an unmanaged device the app simply reports
personal self-service. This section documents detection only and is **not** a
deployment guide for the managed capabilities themselves.


## Managed Chef's Keys — authorization status (Cycle 4)

This cycle adds a small, **read-only authorization/status** signal on top of the
machine-policy detection above. It answers exactly one question: does the
administrator-authored machine policy **authorize** a *future*
organization-provided Chef's Keys inventory to exist?

- **Additive and read-only.** Personal Chef's Keys (stored per-user in Windows
  Credential Manager) are unchanged. The status object lists no key, exposes no
  identifier or secret, and adds no control the user can act on.
- **Certificate-only; secrets prohibited.** A future organization key origin is
  constrained to an app-registration **certificate**. Organization-origin client
  secrets are not permitted, and personal write routes continue to reject any
  organization/origin field.
- **Authorization is not availability.** When policy authorizes the inventory,
  the reported state is *authorized but not provisioned*: no managed key
  inventory is connected, nothing is usable yet, and the app never claims a key
  is available. This cycle activates nothing — no inventory source, no
  certificate discovery, no private-key access, no service, no key use.
- **Fail-closed.** A missing/self-service policy reports *not configured*; an
  organization policy with managed keys turned off reports *disabled*; any
  invalid, untrusted, or unreadable policy reports *needs attention*. Only a
  complete `organization_managed`, managed-keys-`enabled` policy authorizes.
- **Normal-user limitation.** A standard (non-administrator) user cannot author
  or change this policy and cannot mutate organization keys; the surface is
  status-only.


## Managed Chef's Keys — read-only ProgramData inventory source (Cycle 6)

When the machine policy **authorizes** organization-managed Chef's Keys (see
Cycle 4 above), the app now consults a single, fixed, **read-only** location for
a future organization-provided key *inventory manifest*. This cycle only reads
and trust-validates that location; it activates no certificate, private key,
service, or key use.

### Authoritative location

The one and only inventory path is derived internally as:

```
%ProgramData%\PAXCookbook\ManagedChefKeys\organization-key-inventory.json
```

(equivalently `Environment.GetFolderPath(CommonApplicationData)` joined with the
fixed `PAXCookbook\ManagedChefKeys\organization-key-inventory.json` segments).
The path is **not** configurable. Environment variables, command-line inputs,
the per-user profile, and any registry value are **never** consulted to relocate
it. The app opens this file **read-only**; it never creates, writes, moves,
deletes, or repairs the file, its parent directories, or their permissions.

### Trust requirements

Before any byte is parsed, the source enforces all of the following and fails
closed if any check does not pass:

- **Path containment.** The resolved file must sit exactly at the fixed path
  under `%ProgramData%`; nothing outside that folder is ever read.
- **No reparse points.** If the file, or any directory from `%ProgramData%`
  down to the file's parent, is a junction, symlink, or other reparse point,
  the source refuses it.
- **Administrative ownership and ACLs.** The file and its parent directory must
  be owned by, and grant write-capable access only to, trusted system principals
  (Local System and the built-in Administrators group). If any other principal
  has a write-capable grant, the file is treated as untrusted.
- **Bounded size and stable read.** Oversized files are rejected, and the file
  must not change between the pre-read check and the read.
- **Strict UTF-8.** The bytes must decode as strict UTF-8 (a leading UTF-8 byte
  order mark is tolerated); anything else is rejected as invalid content.

### Reported states

- **Absent (normal).** On an ordinary device the folder or file simply does not
  exist, and the app reports *authorized but not provisioned* — the expected,
  safe default. Nothing is provisioned or usable.
- **Untrusted.** If the path, reparse, ownership, or ACL checks fail, the app
  reports the location as untrusted and reads nothing further.
- **Unavailable.** If the location cannot be read (for example access denied),
  the app fails closed to an unavailable state.
- **Invalid.** If a trusted file is present but its bytes are not valid content,
  the app reports *needs attention*; it never guesses a corrected value.
- **Provisioned.** Only a present, contained, reparse-free, admin-owned,
  correctly sized, stable, strict-UTF-8 file whose content the existing parser
  accepts is surfaced as a provisioned inventory. Even then this cycle performs
  **detection only** — no certificate discovery, no private-key access, no
  service, and no key use.

### Normal-user and deployment limitations

This section documents **detection only**. Placing or provisioning the inventory
file is a future, administrator-owned, elevated workflow and is deliberately out
of scope here. A standard (non-administrator) user cannot create the location or
weaken its permissions, and this document is **not** a deployment guide for the
managed inventory itself.


## Managed Chef's Keys — future elevated provisioning helper contract (Cycle 7)

Cycle 6 defined how the app **reads and trust-validates** the managed inventory.
Cycle 7 defines only the **contract shape** for the *future* elevated helper that
would one day place, replace, roll back, or remove that inventory. It is a
paper design expressed as versioned data contracts, pure validators, and a
pure, side-effect-free operation **planner**.

> **No active helper exists.** This cycle ships **no** elevated component, **no**
> installer step, and **no** runtime call site. Nothing here elevates, starts a
> process, touches `%ProgramData%`, changes an ACL or the registry, installs a
> service, or reaches the network. The planner computes immutable *plans* or
> *rejections* from **synthetic inputs only**; it never executes them. This
> document remains **detection-and-design only** and is **not** a provisioning
> guide.

### Bounded elevated verb surface (design)

A future helper would expose a **small, fixed set of symbolic verbs** —
*plan*, *apply*, *verify*, *rollback*, *remove* — and nothing else. Each verb is
a closed enumeration value; there is no free-form command, script path, service
name, or executable argument anywhere in the contract. Requests are carried as a
**bounded, versioned JSON envelope** (a hard maximum size, a required schema
version, a strict allow-list of known fields, and an explicit closed set of
prohibited fields such as secrets, arbitrary paths, commands, service names, and
cloud identifiers). Any oversized, malformed, unknown-field, prohibited-field,
wrong-type, or duplicate-property request is **rejected before** any planning.

### ProgramData artifacts and ownership ledger (design)

The design names only the same fixed, non-configurable managed folder used in
Cycle 6, and a **fixed, closed set of owned file names** (the inventory, an
ownership ledger, and transient temp/transaction markers). Ownership is proven by
a versioned **ownership ledger** carrying a product ownership marker, the managed
feature id, a monotonic **generation** counter, the current (and previous)
inventory content hashes, and an explicit **transaction state**. The validators
reject any ledger with the wrong marker, wrong feature id, wrong owned-file set,
a non-monotonic or invalid generation, an invalid hash, or an invalid state.

### Atomic replacement, rollback, and idempotence (design)

The planner emits **symbolic action steps only** (enumerated action kinds against
enumerated artifact kinds — e.g. *write temp*, *validate temp*, *replace*,
*stamp ACL*, *restore previous generation*, *remove-if-empty*). No step contains
a path string, command line, or hash literal. The design is:

- **Atomic replacement.** New content is staged to a temp artifact, validated,
  then swapped in; a generation increment records the change in the ledger.
- **Rollback.** A rollback is only planned when ownership is proven and a real
  previous generation exists; otherwise it is rejected.
- **Idempotence.** If the proposed content already matches the current trusted
  content (by hash), the planner reports a **no-op / up-to-date** plan that would
  mutate nothing.
- **Recovery.** A ledger caught mid-transaction plans a bounded recovery only
  when ownership is proven; a partial transaction without proven ownership is
  rejected.

### Foreign-file protection and trust gating (design)

Ownership can **never** be established by the caller. The planner treats any
un-owned or unexpected sibling artifact as **foreign** and refuses to overwrite
or remove it; a present-but-unproven inventory or a ledger/inventory disagreement
is a rejection, not a repair. Every mutating plan additionally requires a
**trusted environment** (admin ownership, protected DACL, no reparse point, path
containment) mirroring the Cycle 6 ACL model: write-capable access is limited to
**Local System (`S-1-5-18`)** and the **built-in Administrators group
(`S-1-5-32-544`)**; broad principals (Everyone, Authenticated Users, Users,
Interactive) are **never** granted write, the DACL must be protected, and only
well-known SID strings are accepted (no localized account names).

### Normal-user and deployment limitations

As with every prior cycle, this is **design only**. A standard (non-administrator)
user cannot create, own, or weaken the managed location, and no part of this
contract lets any caller — elevated or not — manufacture ownership or force a
foreign file to be overwritten. There is still **no** deployment procedure here.

### Cycle 8 amendment — one previous-generation backup and reversible swap (design)

Cycle 8 amends the design by naming **exactly one** additional owned artifact —
a single **previous-generation inventory backup** — so the fixed owned-file set is
now the inventory, the ownership ledger, and this one prev backup (plus the same
transient temp/transaction markers). There is **no** second backup generation and
**no** unbounded history: at most one generation is retained behind the current
one. The ledger's `previousGeneration`/`previousInventorySha256` fields govern
whether the prev backup file is expected, and ownership at generation ≥ 2 is proven
only when the prev backup is **present, parses, and hash-matches** the ledger.

- **Backups come from proven current bytes.** An apply always stages the prev
  backup from the ownership-proven **current** owned inventory bytes on the
  machine, never from the caller's request.
- **Rollback is a reversible swap through a new monotonic generation.** Rollback
  exchanges the roles of the current inventory and the prev backup by writing a
  **new** generation `N+1` (the restored prior bytes become the new current, the
  swapped-out bytes become the new backup). Generation numbers are never
  decremented or reused, so a rollback-of-a-rollback simply swaps back at `N+2`.
- **Crash windows fail closed.** A crash mid-transaction is either recovered to a
  fully valid quiescent generation or, where the committed on-disk artifacts
  cannot prove ownership, **rejected (fail-closed stop)** — never reported as
  success and never left corrupt.

The elevated executor that performs these planned actions is **implemented but
compile-gated and NOT invoked** in this cycle: it exists only in a build compiled
with the managed-inventory provisioning switch, its verb is **rejected by the
default product build** before any machine access, and the gated dispatch
recognizes only the five closed operations (*plan, apply, verify, rollback,
remove*) and stops with a bounded "live execution not enabled this cycle" code
without ever constructing the executor, touching an ACL, or requesting elevation.
