# PAX Cookbook Administrator Setup

For IT administrators preparing Work account sign-in for their organization.
The intended release is [v2.0.0-exp.6](https://github.com/microsoft/PAX-Cookbook/releases/tag/v2.0.0-exp.6).
This reference does not claim that release or its administrator asset is already published.
When available, download `PAX_Cookbook_Admin_Setup.zip` from that canonical release and unzip it into a dedicated local folder.

The archive contains only `New-PaxCookbookEntraWamSetup.ps1` and this `README.md`.
The helper's raw source bytes are unchanged by packaging. No real identifiers,
tokens, credentials, logs, or generated configuration/results are included in the archive.
Setup never installs or detects this administrator ZIP; it is a separate IT download,
not an additional runtime prerequisite. Existing product helper delivery is separate.
There is no wrapper: the two direct helper actions below retain their own visible
plans, interactive confirmations, and result files in your PowerShell terminal.

## Prerequisites

Use your own PowerShell 7.4 or later terminal (the helper declares
`#requires -Version 7.4`), with Azure CLI already installed. Change directory to
the unzipped folder. Sign in to the intended tenant, replacing the placeholder:

```powershell
az login --tenant '<your-tenant-id>' --allow-no-subscriptions
az account show --only-show-errors -o json
```

Check that the displayed account and tenant are the expected administrative
context before proceeding. Your role and tenant policy must permit application
creation/configuration and tenant-wide administrator consent. The helper's
`Get-Account` only parses Azure CLI account JSON; it does not validate your role.
An Azure CLI login or successful account query does not prove those permissions.

## Provision

Run this once for a new configuration:

```powershell
& '.\New-PaxCookbookEntraWamSetup.ps1' -Action Provision -DisplayNamePrefix 'PAX Cookbook Sign-In' -SetupResultPath '.\provision-result.json'
```

Provision displays its plan before asking to create the registration. Creation
and administrator consent have separate explicit `Confirm-Step` prompts, both
defaulting to No. The `Confirmed` switch does not bypass these prompts. Canceling
creation makes no tenant mutation. Declining consent leaves a created
configuration but cannot produce acceptable verification for distribution.

Do not rerun Provision to obtain missing consent: it creates another registration,
so rerunning can produce duplicates. Have an authorized administrator correct the
existing configuration and grant the missing permission using your organization's
approved administration process, then run Verify below.

Optionally add `-OutputPath '.\configuration-values.json'` to the Provision call
for configuration values. That export uses `clientId`, not `clientAppId`, and is
not a verified setup import. Do not distribute it or the Provision result as a
replacement for the Verify result.

## Verify And Distribute

Keep the same display-name prefix and the same Azure CLI session tenant:

```powershell
& '.\New-PaxCookbookEntraWamSetup.ps1' -Action Verify -DisplayNamePrefix 'PAX Cookbook Sign-In' -SetupResultPath '.\verified-setup.json'
```

Verify is read-only with respect to Entra configuration. It requires exactly one
registration matching the prefix and the helper's ownership tag. Every structural
check in `structuralVerification.checks` must pass, including
`allPrincipalsUserReadGrant`; the result must also have
`grantResult.allPrincipalsUserRead` equal to `true`. Review the newly written JSON:
require `resultKind` equal to `verify` and `structuralVerification.success` equal
to `true`, with `configFingerprint`, `verifiedUtc`,
`tenantId`, and `clientAppId`, not cancellation, refusal, or an earlier result.
Exit code 0 alone is not success and is not evidence of administrator consent.

Distribute only the successful `verified-setup.json` through your organization's
trusted IT channel. The fingerprint binds configuration values; it is not a
signature or proof of authenticity. End users import the trusted file and then
complete the actual native **Test sign-in** on their own PC. Import never grants
a native sign-in pass. Keep tenant-specific outputs out of the public toolkit.

`-Action Preflight` is a read-only migration assessment of an existing registration's
audience. It is not a provisioning plan and does not replace Provision or Verify.

## Optional Removal

In the same intended tenant, inspect the removal plan first:

```powershell
& '.\New-PaxCookbookEntraWamSetup.ps1' -Action Deprovision -DisplayNamePrefix 'PAX Cookbook Sign-In' -PlanOnly -SetupResultPath '.\removal-plan.json'
```

Inspect every planned target carefully. Prefix wildcard matching can select
multiple owned registrations. Only when those targets are intended, run the
interactive removal and answer its confirmation prompts yourself:

```powershell
& '.\New-PaxCookbookEntraWamSetup.ps1' -Action Deprovision -DisplayNamePrefix 'PAX Cookbook Sign-In' -SetupResultPath '.\removal-result.json'
```

The code deletes the application's grants and then the application. It relies on
Entra's associated-service-principal behavior rather than explicitly deleting
every service principal. Its final absence check covers matching owned
registrations only, not universal tenant footprint or external-tenant artifacts.
Uninstalling PAX Cookbook does not replace this distinct administrator action.

## Technical Reference

Native Microsoft work-account sign-in using a customer-owned registration that shows the native Windows account picker. It requests delegated Microsoft Graph User.Read only; a tenant administrator approves it and no secret or certificate is created. PAX Cookbook uses your work-account permission only to sign you in and show your profile picture; it does not use this sign-in to read audit or directory data. Because the registration shows the standard picker, other accounts (including personal or external accounts) may appear, but only your configured organization can unlock PAX Cookbook.

The helper's preserved header contains older token-free / never-Graph wording.
Current product policy permits one bounded exception: the WAM-owning native
process may use the sign-in token to fetch only the signed-in user's own profile
photo via `GET https://graph.microsoft.com/v1.0/me/photo/$value`. The token never
leaves that process, and is not used for audit collection or directory export.
This clarification does not edit the helper bytes or broaden its permissions.