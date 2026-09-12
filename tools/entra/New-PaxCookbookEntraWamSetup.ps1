#requires -Version 7.4

# =====================================================================
# New-PaxCookbookEntraWamSetup.ps1   Guided, admin-run provisioning of the
# native Windows (WAM) sign-in registration PAX Cookbook uses, owned by a
# single customer tenant.
#
# WHAT THIS SCRIPT DOES
#
#   PAX Cookbook signs a user in once per broker session using native
#   Windows account authentication. That sign-in needs one registration
#   in the customer's own Microsoft Entra tenant:
#
#     * A single public-client registration  the application the user signs
#       in with. It has a native Windows (WAM) broker redirect, no exposed
#       API, no custom scope, and no credential. It requests exactly the
#       delegated Microsoft Graph User.Read permission. Its sign-in audience
#       is AzureADandPersonalMicrosoftAccount, which the native Windows
#       account picker requires; PAX Cookbook itself still authorizes exactly
#       the ONE configured tenant (see SIGN-IN AUDIENCE AND EXTERNAL FOOTPRINT).
#
#   PAX Cookbook is token-free: it uses the sign-in only to unlock the
#   session. User.Read is a delegated permission that allows reading the
#   signed-in user's basic profile; PAX Cookbook never calls Microsoft Graph
#   with the sign-in token and never stores a token.
#
# WHAT THIS SCRIPT CREATES (Provision)
#
#     1. One public-client registration with a native Windows (WAM) broker
#        redirect (ms-appx-web://microsoft.aad.brokerplugin/{client-id}) and
#        sign-in audience AzureADandPersonalMicrosoftAccount (the audience the
#        native Windows account picker requires).
#     2. Delegated Microsoft Graph User.Read on that registration.
#     3. The service principal for the registration.
#     4. One tenant-wide administrator consent for exactly User.Read, so
#        users are not prompted individually.
#
#   It creates no client secret, no certificate, no federated credential,
#   no custom exposed scope, no application permission, and no second Graph
#   permission. The registration is owned by, and this script configures,
#   exactly one tenant.
#
# SIGN-IN AUDIENCE AND EXTERNAL FOOTPRINT
#
#   The registration's sign-in audience is AzureADandPersonalMicrosoftAccount
#   because the native Windows account picker is only offered for that
#   audience. This is an audience choice on the picker, NOT an authorization
#   grant: PAX Cookbook validates every sign-in in-process and unlocks the
#   session only for the ONE configured tenant. Accounts from other
#   organizations and personal Microsoft accounts are rejected.
#
#   Honest disclosure: because the audience is broad, users outside the
#   configured tenant may see this application in the Windows account picker
#   and, by attempting sign-in, may cause a consumer or foreign-tenant service
#   principal to be created in their own directory that the registration owner
#   cannot prevent or remove. This does not grant those users any access to
#   PAX Cookbook; it is an unavoidable consequence of a picker-compatible
#   audience and is disclosed so the owner can make an informed choice.
#
# WHAT THIS SCRIPT OUTPUTS
#
#   Only the non-secret configuration values PAX Cookbook needs:
#     * the tenant id
#     * the public-client application id
#   These are configuration, not secrets. They are printed and, if you
#   pass -OutputPath, written to a file you choose. They are never written
#   into the product's own files.
#
# PREFLIGHT
#
#   Run with -Action Preflight for a READ-ONLY migration check. It performs
#   only reads (no create, update, PATCH, POST, DELETE, or consent), locates
#   the one owned registration, prints a human-readable migration plan, and
#   (when -SetupResultPath is given) writes a fully redacted machine result
#   containing only booleans, constant strings, and a UTC timestamp  never a
#   tenant, client, application, object id, user, token, or secret.
#
# DEPROVISION
#
#   Run with -Action Deprovision to remove exactly the one registration
#   this script created (identified by the ownership tag it stamps on it),
#   its service principal, and its tenant-wide consent. It removes nothing
#   it did not create.
#
# REQUIREMENTS
#
#   * Azure CLI signed in as a tenant administrator able to create app
#     registrations and grant tenant-wide admin consent
#     (for example Global Administrator, Privileged Role Administrator, or
#     Cloud Application Administrator + Application Administrator).
#   * You will be shown each change and asked to confirm. Every prompt
#     defaults to No.
# =====================================================================

[CmdletBinding()]
param(
    [ValidateSet('Provision', 'Deprovision', 'Verify', 'Preflight')]
    [string]$Action = 'Provision',

    # The display-name prefix used for the two registrations. Also used to
    # locate them for Verify/Deprovision, together with the ownership tag.
    [string]$DisplayNamePrefix = 'PAX Cookbook Sign-In',

    # Optional path to write the non-secret configuration values to.
    [string]$OutputPath,

    # Optional path to write the machine-readable, schema-versioned setup result
    # to. When set, the script emits a strict result document (kind =
    # pax-cookbook-wam-setup-result) that PAX Cookbook's guarded helper runner
    # reads back. Only non-secret facts are written; no token, credential,
    # identity, raw Graph body, or customer name.
    [string]$SetupResultPath,

    # Deprovision plan-only (dry run): report the helper-owned deletion plan
    # without deleting anything.
    [switch]$PlanOnly,

    # Machine mode: the caller (PAX Cookbook) has already collected explicit
    # confirmation in its own UI, so skip the interactive prompts. Applies to
    # Deprovision execution launched by the product.
    [switch]$Confirmed
)

$ErrorActionPreference = 'Stop'

# Positive-ownership marker stamped on every registration this script creates.
# Deprovision only touches registrations that carry this exact tag AND match
# the display-name prefix, so it never removes anything it did not create.
$OwnershipTag = 'pax-cookbook-wam-provisioner'
$GraphAppId = '00000003-0000-0000-c000-000000000000'
# Microsoft Graph delegated User.Read permission id (well-known, tenant-invariant).
$GraphUserReadId = 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'

# Strict result schema shared by Provision, Verify, and Deprovision. PAX
# Cookbook's guarded runner parses this exact family (kind + resultKind).
$ResultSchemaVersion = 1
$ResultKind = 'pax-cookbook-wam-setup-result'
$ProviderId = 'entra-wam'
$ProviderVersion = '1'

# Active durable configuration schema version (one-registration model). Must
# match the product's ExperimentalWamOptions.SchemaVersion so the configuration
# fingerprint is byte-identical on both sides.
$ConfigSchemaVersion = 2

# Authorization model marker written on every result document. The registration
# audience is picker-compatible (broad), but PAX Cookbook authorizes exactly the
# one configured tenant. The product REQUIRES this marker on import and rejects
# any result that does not positively assert it.
$AuthorizationModel = 'single-configured-tenant'

# Constant, non-identifying disclosure strings reused by the Provision plan and
# by the read-only Preflight result. They contain no tenant, client, or user id.
$PreflightProposedMutation = 'Set the registration signInAudience to AzureADandPersonalMicrosoftAccount so the native Windows account picker is offered. No secret, certificate, credential, custom scope, application permission, redirect URI, or Graph permission is added or changed. PAX Cookbook authorization stays restricted to exactly the one configured tenant.'
$PreflightRollbackPlan = 'Set the registration signInAudience back to AzureADMyOrg. No other property is changed by this migration, so no further rollback is required.'
$ExternalFootprintDisclosure = 'Because the picker-compatible audience (AzureADandPersonalMicrosoftAccount) accepts personal Microsoft accounts and accounts from any organizational directory, users outside the configured tenant may see this application in the Windows account picker and, by attempting sign-in, may cause a consumer or foreign-tenant service principal to be created in their own directory that the owner cannot prevent or remove. This grants them no access: PAX Cookbook validates every sign-in in-process and unlocks only for the one configured tenant.'

# Non-reversible fingerprint that binds verification to the exact one-registration
# configuration, byte-identical to the product's ExperimentalWamVerificationStore
# .ComputeFingerprint: sha256 hex of
# lower(trim(tenant))|lower(trim(client))|provider|schemaVersion.
function Get-ConfigFingerprint([string]$Tenant, [string]$Client) {
    $material = '{0}|{1}|{2}|{3}' -f $Tenant.Trim().ToLowerInvariant(), $Client.Trim().ToLowerInvariant(), $ProviderId, $ConfigSchemaVersion
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($material)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return (-join ($hash | ForEach-Object { $_.ToString('x2') }))
}

# Atomic write of a machine-readable result document. Only non-secret facts.
function Write-SetupResult([object]$Result) {
    if (-not $SetupResultPath) { return }
    $json = $Result | ConvertTo-Json -Depth 8
    $sourcePath = Join-Path ([System.IO.Path]::GetTempPath()) ("pax-setup-result-{0}.tmp" -f [Guid]::NewGuid().ToString('N'))
    try {
        Set-Content -LiteralPath $sourcePath -Value $json -Encoding utf8
        $p1Path = Join-Path $PSScriptRoot '..\guards\Invoke-P1ArtifactPersistencePathPolicy.ps1'
        if (Test-Path -LiteralPath $p1Path -PathType Leaf) {
            $p1Arguments = @(
                '-NoProfile', '-File', $p1Path,
                '-Mode', 'Persist',
                '-DestinationPath', $SetupResultPath,
                '-SourcePath', $sourcePath,
                '-ExpectedSourceSha256', '7B5257D14BC4D590AEC60BC08A9FB21A72721B93A3B8739AF9CDD77BAFEE5D57'
            )
            & ([Environment]::ProcessPath) @p1Arguments *> $null
            if ($LASTEXITCODE -ne 0) { throw 'Setup result persistence failed.' }
        } else {
            $destinationPath = [IO.Path]::GetFullPath($SetupResultPath)
            $destinationDirectory = [IO.Path]::GetDirectoryName($destinationPath)
            if (-not [IO.Directory]::Exists($destinationDirectory)) {
                [IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
            }
            $stagingPath = Join-Path $destinationDirectory ([IO.Path]::GetRandomFileName())
            try {
                [IO.File]::Copy($sourcePath, $stagingPath, $false)
                [IO.File]::Move($stagingPath, $destinationPath, $true)
            } finally {
                Remove-Item -LiteralPath $stagingPath -Force -ErrorAction SilentlyContinue
            }
        }
    } finally {
        Remove-Item -LiteralPath $sourcePath -Force -ErrorAction SilentlyContinue
    }
}

# True only when every structural (non-consent) check passed.
function Test-StructuralOk([object]$Checks) {
    return (
        $Checks.applicationExists -and
        $Checks.servicePrincipalExists -and
        $Checks.pickerCompatibleAudience -and
        $Checks.wamRedirectExact -and
        $Checks.graphUserReadOnly -and
        $Checks.noAppPermission -and
        $Checks.noCustomScope -and
        $Checks.noCredential -and
        $Checks.noUnexpectedRedirect
    )
}

function Write-Section([string]$text) {
    Write-Host ''
    Write-Host "== $text ==" -ForegroundColor Cyan
}

# Confirmation that always defaults to No. Returns $true only on an explicit yes.
function Confirm-Step([string]$question) {
    $answer = Read-Host "$question [y/N]"
    return ($answer -eq 'y' -or $answer -eq 'Y' -or $answer -eq 'yes')
}

function Get-Account {
    $json = (az account show --only-show-errors -o json 2>$null | Out-String).Trim()
    if (-not $json.StartsWith('{')) {
        throw 'Azure CLI is not signed in. Run "az login --tenant <your-tenant-id>" as a tenant administrator first.'
    }
    return $json | ConvertFrom-Json
}

function Invoke-GraphGet([string]$url) {
    $json = (az rest --method GET --url $url --only-show-errors -o json | Out-String).Trim()
    if (-not $json.StartsWith('{') -and -not $json.StartsWith('[')) { return $null }
    return $json | ConvertFrom-Json
}

function Get-AppByObjectId([string]$objectId) {
    $json = (az ad app show --id $objectId --only-show-errors -o json 2>$null | Out-String).Trim()
    if (-not $json.StartsWith('{')) { return $null }
    return $json | ConvertFrom-Json
}

# Find the registrations this script owns: display-name prefix AND ownership tag.
function Find-OwnedApps([string]$prefix) {
    $json = (az ad app list --all --only-show-errors -o json | Out-String).Trim()
    if (-not $json.StartsWith('[')) { return @() }
    $all = $json | ConvertFrom-Json
    return @($all | Where-Object {
        $_.displayName -like "$prefix*" -and (@($_.tags) -contains $OwnershipTag)
    })
}

function Get-Grants([string]$spId) {
    $r = Invoke-GraphGet "https://graph.microsoft.com/v1.0/servicePrincipals/$spId/oauth2PermissionGrants"
    if ($null -eq $r) { return @() }
    return @($r.value)
}

# ---------------------------------------------------------------------
# PROVISION
# ---------------------------------------------------------------------
function Invoke-Provision {
    $acct = Get-Account
    $tenant = $acct.tenantId

    Write-Section 'Plan'
    Write-Host "Tenant:               $tenant"
    Write-Host "Signed in as:         $($acct.user.name)"
    Write-Host ''
    Write-Host 'This will create, in the tenant above:'
    Write-Host "  1. A public-client registration '$DisplayNamePrefix' with a native"
    Write-Host '     Windows (WAM) broker redirect and sign-in audience'
    Write-Host '     AzureADandPersonalMicrosoftAccount (native account-picker compatible).'
    Write-Host '  2. Delegated Microsoft Graph User.Read on that registration.'
    Write-Host '  3. A service principal for the registration.'
    Write-Host '  4. One tenant-wide administrator consent for exactly User.Read.'
    Write-Host ''
    Write-Host 'No client secret, certificate, federated credential, custom scope, or'
    Write-Host 'application permission is created. The registration is owned by, and this'
    Write-Host 'script configures, exactly one tenant. PAX Cookbook authorizes exactly that'
    Write-Host 'one configured tenant; the picker-compatible audience is not an'
    Write-Host 'authorization grant.'
    Write-Host ''
    Write-Host 'External sign-in footprint:' -ForegroundColor Yellow
    Write-Host $ExternalFootprintDisclosure
    Write-Host ''
    if (-not (Confirm-Step 'Proceed with creating these objects?')) {
        Write-Host 'No changes made.' -ForegroundColor Yellow
        return
    }

    # --- Public-client registration (one registration) ---
    Write-Section 'Creating the sign-in registration'
    $app = (az ad app create --display-name $DisplayNamePrefix --sign-in-audience 'AzureADandPersonalMicrosoftAccount' --only-show-errors -o json | Out-String).Trim() | ConvertFrom-Json
    $cliAppId = $app.appId
    $cliObjId = $app.id
    Start-Sleep -Seconds 3

    $wamRedirect = "ms-appx-web://microsoft.aad.brokerplugin/$cliAppId"
    $appPatch = [ordered]@{
        tags                   = @($OwnershipTag)
        publicClient           = @{ redirectUris = @($wamRedirect) }
        requiredResourceAccess = @(
            [ordered]@{
                resourceAppId  = $GraphAppId
                resourceAccess = @( [ordered]@{ id = $GraphUserReadId; type = 'Scope' } )
            }
        )
    } | ConvertTo-Json -Depth 8
    $appPatchFile = Join-Path ([System.IO.Path]::GetTempPath()) 'pax_app_patch.json'
    Set-Content -Path $appPatchFile -Value $appPatch -Encoding utf8
    az rest --method PATCH --url "https://graph.microsoft.com/v1.0/applications/$cliObjId" --headers 'Content-Type=application/json' --body "@$appPatchFile" --only-show-errors | Out-Null
    Remove-Item $appPatchFile -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3

    # --- Service principal ---
    Write-Section 'Creating service principal'
    $cliSp = (az ad sp create --id $cliAppId --only-show-errors -o json | Out-String).Trim() | ConvertFrom-Json
    Start-Sleep -Seconds 2
    $graphSp = (az ad sp show --id $GraphAppId --only-show-errors -o json | Out-String).Trim() | ConvertFrom-Json

    # --- Tenant-wide administrator consent for exactly Graph User.Read ---
    Write-Section 'Granting tenant-wide administrator consent'
    Write-Host 'This grants exactly one permission (Microsoft Graph User.Read) for all'
    Write-Host 'users in the tenant, so they are not prompted to consent individually.'
    if (-not (Confirm-Step 'Grant tenant-wide consent for User.Read now?')) {
        Write-Host 'Registration created, but tenant-wide consent was NOT granted.' -ForegroundColor Yellow
        Write-Host 'Users will be prompted to consent individually until an administrator consents.'
    }
    else {
        $grantBody = [ordered]@{
            clientId    = $cliSp.id
            consentType = 'AllPrincipals'
            resourceId  = $graphSp.id
            scope       = 'User.Read'
        } | ConvertTo-Json
        $grantFile = Join-Path ([System.IO.Path]::GetTempPath()) 'pax_grant.json'
        Set-Content -Path $grantFile -Value $grantBody -Encoding utf8
        az rest --method POST --url 'https://graph.microsoft.com/v1.0/oauth2PermissionGrants' --headers 'Content-Type=application/json' --body "@$grantFile" --only-show-errors | Out-Null
        Remove-Item $grantFile -ErrorAction SilentlyContinue
    }

    # --- Verify + output ---
    $verify = Test-Structure -Tenant $tenant -ClientAppId $cliAppId -ClientSpId $cliSp.id -GraphSpId $graphSp.id
    Write-Section 'Verification'
    $verify | Format-List | Out-String | Write-Host

    Write-Section 'PAX Cookbook configuration values (non-secret)'
    $config = [ordered]@{
        tenantId   = $tenant
        clientId   = $cliAppId
        providerId = 'entra-wam'
    }
    $config | Format-List | Out-String | Write-Host
    Write-Host 'PAX Cookbook reads these from its experimental runtime configuration:'
    Write-Host '  PAXCOOKBOOK_EXPERIMENTAL_WAM_ENABLED  = 1'
    Write-Host '  PAXCOOKBOOK_EXPERIMENTAL_WAM_PROVIDER = entra-wam'
    Write-Host "  PAXCOOKBOOK_EXPERIMENTAL_WAM_TENANT   = $tenant"
    Write-Host "  PAXCOOKBOOK_EXPERIMENTAL_WAM_CLIENT   = $cliAppId"

    if ($OutputPath) {
        $sourcePath = Join-Path ([System.IO.Path]::GetTempPath()) ("pax-wam-config-{0}.tmp" -f [Guid]::NewGuid().ToString('N'))
        try {
            $config | ConvertTo-Json | Set-Content -LiteralPath $sourcePath -Encoding utf8
            $p1Path = Join-Path $PSScriptRoot '..\guards\Invoke-P1ArtifactPersistencePathPolicy.ps1'
            if (Test-Path -LiteralPath $p1Path -PathType Leaf) {
                $p1Arguments = @(
                    '-NoProfile', '-File', $p1Path,
                    '-Mode', 'Persist',
                    '-DestinationPath', $OutputPath,
                    '-SourcePath', $sourcePath,
                    '-ExpectedSourceSha256', '7B5257D14BC4D590AEC60BC08A9FB21A72721B93A3B8739AF9CDD77BAFEE5D57'
                )
                & ([Environment]::ProcessPath) @p1Arguments *> $null
                if ($LASTEXITCODE -ne 0) { throw 'Configuration value persistence failed.' }
            } else {
                $destinationPath = [IO.Path]::GetFullPath($OutputPath)
                $destinationDirectory = [IO.Path]::GetDirectoryName($destinationPath)
                if (-not [IO.Directory]::Exists($destinationDirectory)) {
                    [IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
                }
                $stagingPath = Join-Path $destinationDirectory ([IO.Path]::GetRandomFileName())
                try {
                    [IO.File]::Copy($sourcePath, $stagingPath, $false)
                    [IO.File]::Move($stagingPath, $destinationPath, $true)
                } finally {
                    Remove-Item -LiteralPath $stagingPath -Force -ErrorAction SilentlyContinue
                }
            }
        } finally {
            Remove-Item -LiteralPath $sourcePath -Force -ErrorAction SilentlyContinue
        }
        Write-Host ''
        Write-Host "Configuration values written to: $OutputPath"
    }

    # Machine-readable Provision result (non-secret only).
    $provisionResult = [ordered]@{
        schemaVersion    = $ResultSchemaVersion
        kind             = $ResultKind
        resultKind       = 'provision'
        providerId       = $ProviderId
        providerVersion  = $ProviderVersion
        authorizationModel = $AuthorizationModel
        tenantId         = $tenant
        clientAppId      = $cliAppId
        structuralVerification = [ordered]@{
            success = [bool](Test-StructuralOk $verify)
            checks  = $verify
        }
        verifiedUtc      = (Get-Date).ToUniversalTime().ToString('o')
        helper           = [ordered]@{ ownershipTag = $OwnershipTag; name = 'New-PaxCookbookEntraWamSetup' }
    }
    Write-SetupResult $provisionResult
}

# ---------------------------------------------------------------------
# VERIFY (pure read; returns a structural summary)
# ---------------------------------------------------------------------
function Test-Structure {
    param(
        [string]$Tenant, [string]$ClientAppId, [string]$ClientSpId, [string]$GraphSpId
    )
    $cli = Get-AppByObjectId $ClientAppId
    $rra = @($cli.requiredResourceAccess)

    # Exactly Microsoft Graph delegated User.Read, and nothing else.
    $graphBlocks = @($rra | Where-Object { $_.resourceAppId -eq $GraphAppId })
    $graphAccess = @(); foreach ($b in $graphBlocks) { foreach ($a in @($b.resourceAccess)) { $graphAccess += $a } }
    $delegated = @($graphAccess | Where-Object { $_.type -eq 'Scope' })
    $appPerms = @($graphAccess | Where-Object { $_.type -eq 'Role' })
    $userReadOnly = (($rra.Count -eq 1) -and ($graphBlocks.Count -eq 1) -and ($delegated.Count -eq 1) -and ($delegated[0].id -eq $GraphUserReadId))

    # No custom exposed API / scope.
    $customScopes = 0; if ($cli.api -and $cli.api.oauth2PermissionScopes) { $customScopes = @($cli.api.oauth2PermissionScopes).Count }

    # No credential.
    $cred = 0; if ($cli.passwordCredentials) { $cred += @($cli.passwordCredentials).Count }; if ($cli.keyCredentials) { $cred += @($cli.keyCredentials).Count }

    # Exact native WAM redirect, and no web/SPA redirect.
    $redirects = @(); if ($cli.publicClient -and $cli.publicClient.redirectUris) { $redirects = @($cli.publicClient.redirectUris) }
    $wamExpected = "ms-appx-web://microsoft.aad.brokerplugin/$($cli.appId)"
    $wamRedirectExact = (($redirects.Count -eq 1) -and ($redirects[0] -eq $wamExpected))
    $webRedirects = 0
    if ($cli.web -and $cli.web.redirectUris) { $webRedirects += @($cli.web.redirectUris).Count }
    if ($cli.spa -and $cli.spa.redirectUris) { $webRedirects += @($cli.spa.redirectUris).Count }

    # Exact tenant-wide AllPrincipals Graph User.Read grant.
    $grantOk = $false
    foreach ($g in (Get-Grants $ClientSpId)) {
        $sc = @(); if ($g.scope) { $sc = ($g.scope.Trim() -split '\s+') }
        if ($g.consentType -eq 'AllPrincipals' -and $g.resourceId -eq $GraphSpId -and ($sc -contains 'User.Read')) { $grantOk = $true }
    }

    return [ordered]@{
        applicationExists          = ($null -ne $cli)
        servicePrincipalExists     = (-not [string]::IsNullOrEmpty($ClientSpId))
        pickerCompatibleAudience   = ($cli.signInAudience -eq 'AzureADandPersonalMicrosoftAccount')
        wamRedirectExact           = $wamRedirectExact
        graphUserReadOnly          = $userReadOnly
        noAppPermission            = ($appPerms.Count -eq 0)
        noCustomScope              = ($customScopes -eq 0)
        noCredential               = ($cred -eq 0)
        noUnexpectedRedirect       = ($webRedirects -eq 0)
        allPrincipalsUserReadGrant = $grantOk
    }
}

function Invoke-Verify {
    $acct = Get-Account
    $tenant = $acct.tenantId
    $owned = @(Find-OwnedApps $DisplayNamePrefix)

    if ($owned.Count -ne 1) {
        # Registration missing or ambiguous: emit a read-only result that the
        # product maps to a non-ready state (never a false ready).
        Write-Host "Could not positively identify exactly one PAX Cookbook sign-in registration in tenant $tenant." -ForegroundColor Yellow
        Write-SetupResult ([ordered]@{
            schemaVersion    = $ResultSchemaVersion
            kind             = $ResultKind
            resultKind       = 'verify'
            providerId       = $ProviderId
            providerVersion  = $ProviderVersion
            authorizationModel = $AuthorizationModel
            tenantId         = $tenant
            clientAppId      = ($owned | Select-Object -First 1 -ExpandProperty appId)
            structuralVerification = [ordered]@{ success = $false; checks = @{} }
            grantResult      = [ordered]@{ allPrincipalsUserRead = $false }
            configFingerprint = ''
            verifiedUtc      = (Get-Date).ToUniversalTime().ToString('o')
        })
        return
    }

    $cliAppId = $owned[0].appId
    $cliSpId = ((az ad sp show --id $cliAppId --only-show-errors -o json 2>$null | Out-String).Trim() | ConvertFrom-Json).id
    $graphSpId = ((az ad sp show --id $GraphAppId --only-show-errors -o json 2>$null | Out-String).Trim() | ConvertFrom-Json).id

    $checks = Test-Structure -Tenant $tenant -ClientAppId $cliAppId -ClientSpId $cliSpId -GraphSpId $graphSpId
    $structuralOk = [bool](Test-StructuralOk $checks)
    $grantOk = [bool]$checks.allPrincipalsUserReadGrant
    $fingerprint = Get-ConfigFingerprint $tenant $cliAppId

    Write-Section 'Verification (read-only)'
    $checks | Format-List | Out-String | Write-Host

    Write-SetupResult ([ordered]@{
        schemaVersion    = $ResultSchemaVersion
        kind             = $ResultKind
        resultKind       = 'verify'
        providerId       = $ProviderId
        providerVersion  = $ProviderVersion
        authorizationModel = $AuthorizationModel
        tenantId         = $tenant
        clientAppId      = $cliAppId
        structuralVerification = [ordered]@{ success = $structuralOk; checks = $checks }
        grantResult      = [ordered]@{ allPrincipalsUserRead = $grantOk }
        configFingerprint = $fingerprint
        verifiedUtc      = (Get-Date).ToUniversalTime().ToString('o')
    })
}

# ---------------------------------------------------------------------
# PREFLIGHT (read-only migration check; performs no create/update/delete/consent)
# ---------------------------------------------------------------------
function Invoke-Preflight {
    # Pure reads only: az account show, az ad app list/show, az ad sp show, and
    # az rest --method GET (via Test-Structure). This action never writes to the
    # directory and never emits a tenant, client, application, object id, user,
    # token, or secret. Its machine result is fully redacted.
    $acct = Get-Account
    $tenant = $acct.tenantId
    $owned = @(Find-OwnedApps $DisplayNamePrefix)

    Write-Section 'Preflight (read-only migration check)'

    if ($owned.Count -ne 1) {
        Write-Host "Could not positively identify exactly one PAX Cookbook sign-in registration in tenant $tenant." -ForegroundColor Yellow
        Write-Host 'No migration plan can be produced until exactly one owned registration exists.'
        Write-SetupResult ([ordered]@{
            schemaVersion                 = $ResultSchemaVersion
            kind                          = $ResultKind
            resultKind                    = 'preflight'
            providerId                    = $ProviderId
            providerVersion               = $ProviderVersion
            authorizationModel            = $AuthorizationModel
            ownershipVerified             = $false
            currentAudienceIsSingleTenant = $false
            wamRedirectExact              = $false
            graphUserReadOnly             = $false
            noAppPermission               = $false
            noCustomScope                 = $false
            noCredential                  = $false
            servicePrincipalExists        = $false
            identifierUriPreconditionOk   = $false
            proposedMutation              = $PreflightProposedMutation
            rollbackPlan                  = $PreflightRollbackPlan
            externalFootprintDisclosure   = $ExternalFootprintDisclosure
            verifiedUtc                   = (Get-Date).ToUniversalTime().ToString('o')
        })
        return
    }

    $cliAppId = $owned[0].appId
    $cli = Get-AppByObjectId $cliAppId
    $cliSpId = ((az ad sp show --id $cliAppId --only-show-errors -o json 2>$null | Out-String).Trim() | ConvertFrom-Json).id
    $graphSpId = ((az ad sp show --id $GraphAppId --only-show-errors -o json 2>$null | Out-String).Trim() | ConvertFrom-Json).id

    $checks = Test-Structure -Tenant $tenant -ClientAppId $cliAppId -ClientSpId $cliSpId -GraphSpId $graphSpId

    $currentAudienceIsSingleTenant = ($cli.signInAudience -eq 'AzureADMyOrg')
    $servicePrincipalExists = (-not [string]::IsNullOrEmpty($cliSpId))
    $identifierUris = @(); if ($cli.identifierUris) { $identifierUris = @($cli.identifierUris) }
    $identifierUriPreconditionOk = ($identifierUris.Count -eq 0)

    Write-Host 'Found exactly one owned registration.'
    Write-Host ''
    Write-Host 'Current state (read-only):'
    Write-Host "  Sign-in audience is still single-tenant (AzureADMyOrg): $currentAudienceIsSingleTenant"
    Write-Host "  Native WAM redirect exact:                            $([bool]$checks.wamRedirectExact)"
    Write-Host "  Graph delegated User.Read only:                       $([bool]$checks.graphUserReadOnly)"
    Write-Host "  No application permission:                            $([bool]$checks.noAppPermission)"
    Write-Host "  No custom scope:                                      $([bool]$checks.noCustomScope)"
    Write-Host "  No credential:                                        $([bool]$checks.noCredential)"
    Write-Host "  Service principal exists:                             $servicePrincipalExists"
    Write-Host "  Identifier-URI precondition met:                      $identifierUriPreconditionOk"
    Write-Host ''
    Write-Host 'Proposed migration:'
    Write-Host "  $PreflightProposedMutation"
    Write-Host ''
    Write-Host 'Rollback:'
    Write-Host "  $PreflightRollbackPlan"
    Write-Host ''
    Write-Host 'External sign-in footprint:' -ForegroundColor Yellow
    Write-Host "  $ExternalFootprintDisclosure"

    Write-SetupResult ([ordered]@{
        schemaVersion                 = $ResultSchemaVersion
        kind                          = $ResultKind
        resultKind                    = 'preflight'
        providerId                    = $ProviderId
        providerVersion               = $ProviderVersion
        authorizationModel            = $AuthorizationModel
        ownershipVerified             = $true
        currentAudienceIsSingleTenant = $currentAudienceIsSingleTenant
        wamRedirectExact              = [bool]$checks.wamRedirectExact
        graphUserReadOnly             = [bool]$checks.graphUserReadOnly
        noAppPermission               = [bool]$checks.noAppPermission
        noCustomScope                 = [bool]$checks.noCustomScope
        noCredential                  = [bool]$checks.noCredential
        servicePrincipalExists        = $servicePrincipalExists
        identifierUriPreconditionOk   = $identifierUriPreconditionOk
        proposedMutation              = $PreflightProposedMutation
        rollbackPlan                  = $PreflightRollbackPlan
        externalFootprintDisclosure   = $ExternalFootprintDisclosure
        verifiedUtc                   = (Get-Date).ToUniversalTime().ToString('o')
    })
}

# ---------------------------------------------------------------------
# DEPROVISION (removes only registrations this script created)
# ---------------------------------------------------------------------
function Invoke-Deprovision {
    $acct = Get-Account
    $tenant = $acct.tenantId
    $owned = Find-OwnedApps $DisplayNamePrefix

    # Plan-only (dry run): report the helper-owned deletion plan; delete nothing.
    if ($PlanOnly) {
        Write-Section 'Deprovision plan (dry run)'
        foreach ($a in $owned) { Write-Host "  $($a.displayName)  (appId $($a.appId))" }
        Write-SetupResult ([ordered]@{
            schemaVersion   = $ResultSchemaVersion
            kind            = $ResultKind
            resultKind      = 'deprovision_plan'
            providerId      = $ProviderId
            providerVersion = $ProviderVersion
            tenantId        = $tenant
            ownership       = [ordered]@{ tag = $OwnershipTag; prefix = $DisplayNamePrefix; allOwned = $true }
            objectCategories = [ordered]@{
                registration      = ($owned.Count -eq 1)
                servicePrincipals = $owned.Count
                tenantWideGrant   = $true
            }
            verifiedUtc     = (Get-Date).ToUniversalTime().ToString('o')
        })
        return
    }

    if ($owned.Count -eq 0) {
        Write-Host "Nothing to remove: no registrations with the ownership tag and prefix were found in tenant $tenant." -ForegroundColor Yellow
        Write-SetupResult ([ordered]@{
            schemaVersion   = $ResultSchemaVersion
            kind            = $ResultKind
            resultKind      = 'deprovision'
            providerId      = $ProviderId
            providerVersion = $ProviderVersion
            tenantId        = $tenant
            deletedCategories = [ordered]@{ registration = $false; servicePrincipals = 0; tenantWideGrant = $false }
            absenceVerified = $true
            partialFailure  = @()
            verifiedUtc     = (Get-Date).ToUniversalTime().ToString('o')
        })
        return
    }

    Write-Section 'Plan'
    Write-Host "Tenant: $tenant"
    Write-Host 'The following registrations created by this script will be deleted, along'
    Write-Host 'with their service principals and their tenant-wide consent:'
    foreach ($a in $owned) { Write-Host "  $($a.displayName)  (appId $($a.appId))" }
    Write-Host ''
    Write-Host 'No other object will be touched.'
    if (-not $Confirmed) {
        if (-not (Confirm-Step 'Delete the registrations listed above?')) {
            Write-Host 'No changes made.' -ForegroundColor Yellow
            return
        }
    }

    $deletedRegistration = $false
    foreach ($a in $owned) {
        # Remove tenant-wide grants owned by this app's service principal first.
        $spJson = (az ad sp show --id $a.appId --only-show-errors -o json 2>$null | Out-String).Trim()
        if ($spJson.StartsWith('{')) {
            $sp = $spJson | ConvertFrom-Json
            foreach ($g in (Get-Grants $sp.id)) {
                az rest --method DELETE --url "https://graph.microsoft.com/v1.0/oauth2PermissionGrants/$($g.id)" --only-show-errors | Out-Null
            }
        }
        az ad app delete --id $a.appId --only-show-errors | Out-Null
        $deletedRegistration = $true
        Write-Host "Deleted $($a.displayName)."
    }
    Start-Sleep -Seconds 3

    $remaining = Find-OwnedApps $DisplayNamePrefix
    $absenceVerified = ($remaining.Count -eq 0)
    if ($absenceVerified) {
        Write-Host 'Deprovision complete. All registrations created by this script were removed.' -ForegroundColor Green
    }
    else {
        Write-Host 'Some registrations could not be removed; re-run Deprovision or remove them manually.' -ForegroundColor Yellow
    }

    $partial = @()
    if (-not $absenceVerified) { foreach ($r in $remaining) { $partial += 'registration' } }

    Write-SetupResult ([ordered]@{
        schemaVersion   = $ResultSchemaVersion
        kind            = $ResultKind
        resultKind      = 'deprovision'
        providerId      = $ProviderId
        providerVersion = $ProviderVersion
        tenantId        = $tenant
        deletedCategories = [ordered]@{
            registration      = $deletedRegistration
            servicePrincipals = $owned.Count
            tenantWideGrant   = $true
        }
        absenceVerified = $absenceVerified
        partialFailure  = $partial
        verifiedUtc     = (Get-Date).ToUniversalTime().ToString('o')
    })
}

switch ($Action) {
    'Provision' { Invoke-Provision }
    'Deprovision' { Invoke-Deprovision }
    'Verify' { Invoke-Verify }
    'Preflight' { Invoke-Preflight }
}
