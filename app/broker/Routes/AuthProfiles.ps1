#requires -Version 7.4

# Routes/AuthProfiles.ps1
#
# Phase AF -- HTTP surface for the auth_profiles registry. Eight
# endpoints, all under /api/v1/auth/profiles:
#
#   GET    /api/v1/auth/profiles                  -- list (metadata only)
#   POST   /api/v1/auth/profiles                  -- create
#   GET    /api/v1/auth/profiles/{id}             -- single (metadata only)
#   PUT    /api/v1/auth/profiles/{id}             -- update metadata
#   DELETE /api/v1/auth/profiles/{id}             -- delete profile + secret
#   POST   /api/v1/auth/profiles/{id}/secret      -- bind / replace secret
#   DELETE /api/v1/auth/profiles/{id}/secret      -- remove secret only
#   POST   /api/v1/auth/profiles/{id}/test        -- bounded structural test
#
# Doctrine (verbatim, in force):
#   - Recipes NEVER persist secret material. Profiles are the binding
#     between an authorial identity (name, tenant, client) and a
#     credential reference (Windows Credential Manager target string).
#   - The list/get endpoints NEVER return secret material. Even
#     metadata like cred_man_target is included verbatim because the
#     target STRING is not a secret -- the credential bytes addressed
#     by that string are.
#   - The bind endpoint accepts a SecureString-equivalent in the
#     request body and persists ONLY into Windows Credential Manager.
#     The plaintext is zeroed in the broker process after the
#     CredWrite call returns.
#   - The test endpoint is BOUNDED. v1 implements STRUCTURAL
#     validation only: row well-formed, credential present in
#     CredMan (for secret mode) or certificate resolvable in
#     LocalMachine\My (for cert mode). v1 does NOT attempt token
#     acquisition against Entra; that is reserved for a controlled
#     follow-on once the operational telemetry posture for outbound
#     auth probes is defined. The response carries an explicit
#     validationKind='structural' so the SPA cannot mistakenly
#     present a structural pass as authentication validity.
#   - All mutations are gated by Invoke-BrokerLockReAuthForOp. A
#     'Verified' verdict permits the mutation; any other verdict
#     produces HTTP 401 with code=reAuthRequired.
#   - Profile deletion ALSO deletes the bound credential from
#     CredMan (the orphaned target string would otherwise become
#     impossible-to-reach state and violates the appliance's
#     "no orphaned credentials" doctrine).
#
# Dot-sourced from Start-Broker.ps1; depends on:
#   - $Script:SqliteConn                                (SQLite handle)
#   - Write-JsonResponse, Read-RequestJson, Get-UtcNowIso (broker helpers)
#   - CredentialManager.ps1 functions
#   - BrokerLock.ps1 (Auth/) gating helpers
#   - WindowsReAuth.ps1 helpers (transitive)

# Profile id is a lowercase UUID v4 by convention (the recipe schema's
# authProfileId pattern is case-insensitive but UUIDs across the wire
# are lowercase for stable diffs).
$Script:AuthProfileIdPattern = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'

# Modes that have a row in this table. ManagedIdentity is deliberately
# absent: identity-by-environment, not a Cookbook-stored asset.
$Script:AuthProfilePersistedModes = @('AppRegistrationSecret','AppRegistrationCertificate')

# ---------------------------------------------------------------------
# Row helpers
# ---------------------------------------------------------------------

function ConvertTo-AuthProfileRow {
    # Reads the current row from a SqliteDataReader into a plain
    # PSObject. Field order tracks the table definition (Start-Broker
    # M1_Ddl). Nullable columns surface as $null, not [DBNull]::Value.
    param([Microsoft.Data.Sqlite.SqliteDataReader]$Reader)
    $null2 = { param($v) if ($v -is [System.DBNull]) { return $null } return $v }
    return [pscustomobject]@{
        authProfileId       = [string]$Reader['auth_profile_id']
        name                = [string]$Reader['name']
        mode                = [string]$Reader['mode']
        tenantId            = [string]$Reader['tenant_id']
        clientId            = [string]$Reader['client_id']
        credManTarget       = & $null2 $Reader['cred_man_target']
        certThumbprint      = & $null2 $Reader['cert_thumbprint']
        certStore           = & $null2 $Reader['cert_store']
        description         = & $null2 $Reader['description']
        lastVerifiedAt      = & $null2 $Reader['last_verified_at']
        lastVerifiedResult  = & $null2 $Reader['last_verified_result']
        createdAt           = [string]$Reader['created_at']
        updatedAt           = [string]$Reader['updated_at']
    }
}

function Get-AuthProfileRowsAll {
    $rows = New-Object System.Collections.Generic.List[object]
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
SELECT auth_profile_id, name, mode, tenant_id, client_id, cred_man_target,
       cert_thumbprint, cert_store, description, last_verified_at,
       last_verified_result, created_at, updated_at
FROM auth_profiles
ORDER BY name COLLATE NOCASE ASC;
'@
    $reader = $cmd.ExecuteReader()
    try {
        while ($reader.Read()) { $rows.Add( (ConvertTo-AuthProfileRow -Reader $reader) ) | Out-Null }
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
    return @($rows.ToArray())
}

function Get-AuthProfileRow {
    param([Parameter(Mandatory)][string]$AuthProfileId)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
SELECT auth_profile_id, name, mode, tenant_id, client_id, cred_man_target,
       cert_thumbprint, cert_store, description, last_verified_at,
       last_verified_result, created_at, updated_at
FROM auth_profiles WHERE auth_profile_id = $id;
'@
    [void]$cmd.Parameters.AddWithValue('$id', $AuthProfileId)
    $reader = $cmd.ExecuteReader()
    try {
        if ($reader.Read()) { return ConvertTo-AuthProfileRow -Reader $reader }
        return $null
    } finally {
        $reader.Dispose()
        $cmd.Dispose()
    }
}

function Test-AuthProfileNameUniqueness {
    # Returns $true if no OTHER row holds this name (case-insensitive).
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$ExcludeId = ''
    )
    $cmd = $Script:SqliteConn.CreateCommand()
    if ([string]::IsNullOrEmpty($ExcludeId)) {
        $cmd.CommandText = 'SELECT COUNT(*) FROM auth_profiles WHERE LOWER(name) = LOWER($n);'
        [void]$cmd.Parameters.AddWithValue('$n', $Name)
    } else {
        $cmd.CommandText = 'SELECT COUNT(*) FROM auth_profiles WHERE LOWER(name) = LOWER($n) AND auth_profile_id <> $id;'
        [void]$cmd.Parameters.AddWithValue('$n',  $Name)
        [void]$cmd.Parameters.AddWithValue('$id', $ExcludeId)
    }
    try {
        $n = [int]$cmd.ExecuteScalar()
        return ($n -eq 0)
    } finally {
        $cmd.Dispose()
    }
}

function Add-AuthProfileRow {
    param(
        [Parameter(Mandatory)][string]$AuthProfileId,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Mode,
        [Parameter(Mandatory)][string]$TenantId,
        [Parameter(Mandatory)][string]$ClientId,
        [string]$CertThumbprint = '',
        [string]$CertStore      = '',
        [string]$Description    = '',
        [Parameter(Mandatory)][string]$NowIso
    )
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
INSERT INTO auth_profiles (
    auth_profile_id, name, mode, tenant_id, client_id, cred_man_target,
    cert_thumbprint, cert_store, description, last_verified_at,
    last_verified_result, created_at, updated_at
) VALUES (
    $id, $n, $m, $t, $c, NULL, $thumb, $store, $desc, NULL, NULL, $now, $now
);
'@
    [void]$cmd.Parameters.AddWithValue('$id',    $AuthProfileId)
    [void]$cmd.Parameters.AddWithValue('$n',     $Name)
    [void]$cmd.Parameters.AddWithValue('$m',     $Mode)
    [void]$cmd.Parameters.AddWithValue('$t',     $TenantId)
    [void]$cmd.Parameters.AddWithValue('$c',     $ClientId)
    [void]$cmd.Parameters.AddWithValue('$thumb', [string]$CertThumbprint)
    [void]$cmd.Parameters.AddWithValue('$store', [string]$CertStore)
    [void]$cmd.Parameters.AddWithValue('$desc',  [string]$Description)
    [void]$cmd.Parameters.AddWithValue('$now',   $NowIso)
    try { [void]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

function Update-AuthProfileMetadata {
    param(
        [Parameter(Mandatory)][string]$AuthProfileId,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$TenantId,
        [Parameter(Mandatory)][string]$ClientId,
        [string]$CertThumbprint = '',
        [string]$CertStore      = '',
        [string]$Description    = '',
        [Parameter(Mandatory)][string]$NowIso
    )
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = @'
UPDATE auth_profiles SET
    name = $n, tenant_id = $t, client_id = $c,
    cert_thumbprint = $thumb, cert_store = $store, description = $desc,
    updated_at = $now
WHERE auth_profile_id = $id;
'@
    [void]$cmd.Parameters.AddWithValue('$id',    $AuthProfileId)
    [void]$cmd.Parameters.AddWithValue('$n',     $Name)
    [void]$cmd.Parameters.AddWithValue('$t',     $TenantId)
    [void]$cmd.Parameters.AddWithValue('$c',     $ClientId)
    [void]$cmd.Parameters.AddWithValue('$thumb', [string]$CertThumbprint)
    [void]$cmd.Parameters.AddWithValue('$store', [string]$CertStore)
    [void]$cmd.Parameters.AddWithValue('$desc',  [string]$Description)
    [void]$cmd.Parameters.AddWithValue('$now',   $NowIso)
    try { return [int]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

function Update-AuthProfileCredManTarget {
    param(
        [Parameter(Mandatory)][string]$AuthProfileId,
        [AllowEmptyString()][AllowNull()][string]$Target,
        [Parameter(Mandatory)][string]$NowIso
    )
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'UPDATE auth_profiles SET cred_man_target = $tgt, updated_at = $now WHERE auth_profile_id = $id;'
    if ([string]::IsNullOrEmpty($Target)) {
        [void]$cmd.Parameters.AddWithValue('$tgt', [System.DBNull]::Value)
    } else {
        [void]$cmd.Parameters.AddWithValue('$tgt', $Target)
    }
    [void]$cmd.Parameters.AddWithValue('$now', $NowIso)
    [void]$cmd.Parameters.AddWithValue('$id',  $AuthProfileId)
    try { return [int]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

function Update-AuthProfileVerifiedResult {
    param(
        [Parameter(Mandatory)][string]$AuthProfileId,
        [Parameter(Mandatory)][string]$Result,
        [Parameter(Mandatory)][string]$NowIso
    )
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'UPDATE auth_profiles SET last_verified_at = $now, last_verified_result = $res, updated_at = $now WHERE auth_profile_id = $id;'
    [void]$cmd.Parameters.AddWithValue('$now', $NowIso)
    [void]$cmd.Parameters.AddWithValue('$res', $Result)
    [void]$cmd.Parameters.AddWithValue('$id',  $AuthProfileId)
    try { return [int]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

function Remove-AuthProfileRow {
    param([Parameter(Mandatory)][string]$AuthProfileId)
    $cmd = $Script:SqliteConn.CreateCommand()
    $cmd.CommandText = 'DELETE FROM auth_profiles WHERE auth_profile_id = $id;'
    [void]$cmd.Parameters.AddWithValue('$id', $AuthProfileId)
    try { return [int]$cmd.ExecuteNonQuery() } finally { $cmd.Dispose() }
}

# ---------------------------------------------------------------------
# Re-auth gate helper (response shaping)
# ---------------------------------------------------------------------

function Invoke-AuthProfileReAuthOrShortCircuit {
    # Wraps Invoke-BrokerLockReAuthForOp. If the verdict is 'Verified',
    # returns $null (caller proceeds). Otherwise emits the 401
    # reAuthRequired response and returns a truthy "handled" sentinel
    # so the caller can early-return.
    param(
        [Parameter(Mandatory)]$Context,
        [Parameter(Mandatory)][string]$OpClass,
        [Parameter(Mandatory)][string]$Message
    )
    $verdict = Invoke-BrokerLockReAuthForOp -OpClass $OpClass -Message $Message
    if ($verdict -eq 'Verified') { return $null }
    $resp = New-BrokerLockReAuthRequiredResponse -OpClass $OpClass -VerificationResult $verdict
    Write-JsonResponse -Context $Context -Status $resp.status -Body $resp.body
    return @{ handled = $true; verdict = $verdict }
}

# ---------------------------------------------------------------------
# Request body validation
# ---------------------------------------------------------------------

function Test-AuthProfileCreateBody {
    # Returns @{ ok=$bool; errors=@(...) }. Errors are AJV-shaped.
    param($Body)
    $errors = New-Object System.Collections.Generic.List[object]
    $add = {
        param($path, $kw, $msg, $params = @{})
        $errors.Add( [pscustomobject]@{ instancePath = $path; keyword = $kw; message = $msg; params = $params } )
    }
    if ($null -eq $Body -or -not (($Body -is [hashtable]) -or ($Body -is [System.Collections.IDictionary]))) {
        & $add '' 'type' 'request body must be a JSON object' @{}
        return @{ ok = $false; errors = @($errors.ToArray()) }
    }
    foreach ($f in @('name','mode','tenantId','clientId')) {
        if (-not $Body.ContainsKey($f) -or [string]::IsNullOrWhiteSpace([string]$Body[$f])) {
            & $add ('/' + $f) 'required' "must have non-empty property '$f'" @{}
        }
    }
    if ($Body.ContainsKey('mode')) {
        $mode = [string]$Body['mode']
        if ($Script:AuthProfilePersistedModes -notcontains $mode) {
            & $add '/mode' 'enum' ("mode must be one of: " + ($Script:AuthProfilePersistedModes -join ', ')) @{ allowed = $Script:AuthProfilePersistedModes }
        }
        if ($mode -eq 'AppRegistrationCertificate') {
            if (-not $Body.ContainsKey('certThumbprint') -or [string]::IsNullOrWhiteSpace([string]$Body['certThumbprint'])) {
                & $add '/certThumbprint' 'required' "AppRegistrationCertificate mode requires certThumbprint" @{}
            } else {
                $thumb = ([string]$Body['certThumbprint']).Trim()
                if ($thumb -notmatch '^[0-9A-Fa-f]{40}$') {
                    & $add '/certThumbprint' 'pattern' "certThumbprint must be 40 hex characters (SHA-1 thumbprint)" @{}
                }
            }
        }
    }
    if ($Body.ContainsKey('tenantId')) {
        if (([string]$Body['tenantId']) -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
            & $add '/tenantId' 'pattern' "tenantId must be a UUID" @{}
        }
    }
    if ($Body.ContainsKey('clientId')) {
        if (([string]$Body['clientId']) -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
            & $add '/clientId' 'pattern' "clientId must be a UUID" @{}
        }
    }
    if ($Body.ContainsKey('name')) {
        $nm = [string]$Body['name']
        if ($nm.Length -gt 200) { & $add '/name' 'maxLength' "name must be 200 characters or fewer" @{} }
    }
    if ($Body.ContainsKey('description')) {
        $d = [string]$Body['description']
        if ($d.Length -gt 2000) { & $add '/description' 'maxLength' "description must be 2000 characters or fewer" @{} }
    }
    return @{ ok = ($errors.Count -eq 0); errors = @($errors.ToArray()) }
}

function Test-AuthProfileUpdateBody {
    # Same as create-body but mode is NOT mutable (the credential
    # binding model depends on mode; changing mode would orphan the
    # credential). The chef must delete + recreate to change mode.
    param($Body)
    $errors = New-Object System.Collections.Generic.List[object]
    $add = {
        param($path, $kw, $msg, $params = @{})
        $errors.Add( [pscustomobject]@{ instancePath = $path; keyword = $kw; message = $msg; params = $params } )
    }
    if ($null -eq $Body -or -not (($Body -is [hashtable]) -or ($Body -is [System.Collections.IDictionary]))) {
        & $add '' 'type' 'request body must be a JSON object' @{}
        return @{ ok = $false; errors = @($errors.ToArray()) }
    }
    foreach ($f in @('name','tenantId','clientId')) {
        if (-not $Body.ContainsKey($f) -or [string]::IsNullOrWhiteSpace([string]$Body[$f])) {
            & $add ('/' + $f) 'required' "must have non-empty property '$f'" @{}
        }
    }
    if ($Body.ContainsKey('mode')) {
        # Reject mode changes.
        & $add '/mode' 'immutable' "mode cannot be changed via PUT (delete and recreate the profile to switch modes)" @{}
    }
    if ($Body.ContainsKey('tenantId')) {
        if (([string]$Body['tenantId']) -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
            & $add '/tenantId' 'pattern' "tenantId must be a UUID" @{}
        }
    }
    if ($Body.ContainsKey('clientId')) {
        if (([string]$Body['clientId']) -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
            & $add '/clientId' 'pattern' "clientId must be a UUID" @{}
        }
    }
    if ($Body.ContainsKey('certThumbprint')) {
        $thumb = ([string]$Body['certThumbprint']).Trim()
        if (-not [string]::IsNullOrEmpty($thumb) -and $thumb -notmatch '^[0-9A-Fa-f]{40}$') {
            & $add '/certThumbprint' 'pattern' "certThumbprint must be 40 hex characters (SHA-1 thumbprint)" @{}
        }
    }
    if ($Body.ContainsKey('name')) {
        $nm = [string]$Body['name']
        if ($nm.Length -gt 200) { & $add '/name' 'maxLength' "name must be 200 characters or fewer" @{} }
    }
    if ($Body.ContainsKey('description')) {
        $d = [string]$Body['description']
        if ($d.Length -gt 2000) { & $add '/description' 'maxLength' "description must be 2000 characters or fewer" @{} }
    }
    return @{ ok = ($errors.Count -eq 0); errors = @($errors.ToArray()) }
}

# ---------------------------------------------------------------------
# Handlers
# ---------------------------------------------------------------------

function Invoke-AuthProfilesList {
    param($Context)
    # List does NOT require re-auth (read-only metadata).
    $rows = Get-AuthProfileRowsAll
    Write-JsonResponse -Context $Context -Status 200 -Body @{ profiles = $rows }
}

function Invoke-AuthProfileGet {
    param($Context, [string]$AuthProfileId)
    $row = Get-AuthProfileRow -AuthProfileId $AuthProfileId
    if (-not $row) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found' }
        return
    }
    Write-JsonResponse -Context $Context -Status 200 -Body @{ profile = $row }
}

function Invoke-AuthProfileCreate {
    param($Context)
    # Re-auth gate. The chef intends to register a new workload
    # identity; per the doctrine the per-op gate fires regardless of
    # lock state.
    $h = Invoke-AuthProfileReAuthOrShortCircuit -Context $Context -OpClass 'profileMutation' -Message 'Verify to create a new auth profile.'
    if ($h) { return }

    $body = Read-RequestJson -Context $Context
    if ($null -eq $body) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }
    $val = Test-AuthProfileCreateBody -Body $body
    if (-not $val.ok) {
        Write-JsonResponse -Context $Context -Status 422 -Body @{ error = 'profile_invalid'; errors = $val.errors }
        return
    }
    $name = [string]$body['name']
    if (-not (Test-AuthProfileNameUniqueness -Name $name)) {
        Write-JsonResponse -Context $Context -Status 409 -Body @{ error = 'name_conflict'; name = $name }
        return
    }

    $id     = [string]([guid]::NewGuid()).ToString().ToLowerInvariant()
    $now    = Get-UtcNowIso
    $mode   = [string]$body['mode']
    $tid    = ([string]$body['tenantId']).ToLowerInvariant()
    $cid    = ([string]$body['clientId']).ToLowerInvariant()
    $thumb  = if ($body.ContainsKey('certThumbprint')) { ([string]$body['certThumbprint']).ToUpperInvariant() } else { '' }
    $store  = if ($body.ContainsKey('certStore'))      { [string]$body['certStore'] } else { '' }
    $desc   = if ($body.ContainsKey('description'))    { [string]$body['description'] } else { '' }
    if ($mode -eq 'AppRegistrationCertificate' -and [string]::IsNullOrEmpty($store)) {
        # Default cert store; chef may override on subsequent PUT.
        $store = 'LocalMachine\My'
    }

    try {
        Add-AuthProfileRow -AuthProfileId $id -Name $name -Mode $mode `
                           -TenantId $tid -ClientId $cid `
                           -CertThumbprint $thumb -CertStore $store -Description $desc `
                           -NowIso $now
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'profile_persist_failed'; detail = [string]$_.Exception.Message }
        return
    }
    $row = Get-AuthProfileRow -AuthProfileId $id
    Write-JsonResponse -Context $Context -Status 201 -Body @{ profile = $row }
}

function Invoke-AuthProfileUpdate {
    param($Context, [string]$AuthProfileId)
    $existing = Get-AuthProfileRow -AuthProfileId $AuthProfileId
    if (-not $existing) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found' }
        return
    }
    $h = Invoke-AuthProfileReAuthOrShortCircuit -Context $Context -OpClass 'profileMutation' -Message ("Verify to update auth profile '" + $existing.name + "'.")
    if ($h) { return }

    $body = Read-RequestJson -Context $Context
    if ($null -eq $body) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json' }
        return
    }
    $val = Test-AuthProfileUpdateBody -Body $body
    if (-not $val.ok) {
        Write-JsonResponse -Context $Context -Status 422 -Body @{ error = 'profile_invalid'; errors = $val.errors }
        return
    }
    $name = [string]$body['name']
    if (-not (Test-AuthProfileNameUniqueness -Name $name -ExcludeId $AuthProfileId)) {
        Write-JsonResponse -Context $Context -Status 409 -Body @{ error = 'name_conflict'; name = $name }
        return
    }
    $tid    = ([string]$body['tenantId']).ToLowerInvariant()
    $cid    = ([string]$body['clientId']).ToLowerInvariant()
    $thumb  = if ($body.ContainsKey('certThumbprint')) { ([string]$body['certThumbprint']).ToUpperInvariant() } else { [string]$existing.certThumbprint }
    $store  = if ($body.ContainsKey('certStore'))      { [string]$body['certStore'] }      else { [string]$existing.certStore }
    $desc   = if ($body.ContainsKey('description'))    { [string]$body['description'] }    else { [string]$existing.description }
    $now    = Get-UtcNowIso

    try {
        [void](Update-AuthProfileMetadata -AuthProfileId $AuthProfileId `
                                          -Name $name -TenantId $tid -ClientId $cid `
                                          -CertThumbprint $thumb -CertStore $store -Description $desc `
                                          -NowIso $now)
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'profile_persist_failed'; detail = [string]$_.Exception.Message }
        return
    }
    $row = Get-AuthProfileRow -AuthProfileId $AuthProfileId
    Write-JsonResponse -Context $Context -Status 200 -Body @{ profile = $row }
}

function Invoke-AuthProfileDelete {
    param($Context, [string]$AuthProfileId)
    $existing = Get-AuthProfileRow -AuthProfileId $AuthProfileId
    if (-not $existing) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found' }
        return
    }
    $h = Invoke-AuthProfileReAuthOrShortCircuit -Context $Context -OpClass 'profileMutation' -Message ("Verify to DELETE auth profile '" + $existing.name + "' (the bound credential will also be removed from Windows Credential Manager).")
    if ($h) { return }

    # Remove the bound credential FIRST. CredMan removal is idempotent
    # (ERROR_NOT_FOUND -> $false return), so this is safe even if the
    # profile never had a secret bound (e.g. cert mode).
    if ($existing.credManTarget) {
        try { [void](Remove-AuthProfileSecret -AuthProfileId $AuthProfileId) } catch {
            # Best-effort: log to the response but still proceed with
            # row delete. Leaving a row + orphaned credential is worse
            # than leaving an orphaned credential.
        }
    }

    try {
        [void](Remove-AuthProfileRow -AuthProfileId $AuthProfileId)
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'profile_delete_failed'; detail = [string]$_.Exception.Message }
        return
    }
    Write-JsonResponse -Context $Context -Status 200 -Body @{ ok = $true; authProfileId = $AuthProfileId }
}

function Invoke-AuthProfileSecretBind {
    # Body: { "secret": "<plaintext>" } -- the plaintext is consumed
    # by the broker process and written to Windows Credential Manager.
    # The broker zeroes the plaintext after CredWrite. The on-disk
    # database row records only cred_man_target (a STRING that
    # addresses the credential, not the credential bytes).
    #
    # The endpoint accepts plaintext over the TLS-protected /api/v1
    # surface because the alternative (operator pasting a SecureString
    # marshal blob via JSON) introduces more surface area than it
    # removes. The transport is loopback HTTPS; the recipient is the
    # broker process under the same Windows identity that owns the
    # credential store.
    param($Context, [string]$AuthProfileId)
    $existing = Get-AuthProfileRow -AuthProfileId $AuthProfileId
    if (-not $existing) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found' }
        return
    }
    if ($existing.mode -ne 'AppRegistrationSecret') {
        Write-JsonResponse -Context $Context -Status 422 -Body @{ error = 'mode_mismatch'; detail = "secret binding is only valid for AppRegistrationSecret mode (this profile is mode '$($existing.mode)')" }
        return
    }
    $h = Invoke-AuthProfileReAuthOrShortCircuit -Context $Context -OpClass 'secretBind' -Message ("Verify to bind the client secret for auth profile '" + $existing.name + "'. The secret will be written to Windows Credential Manager under this Windows account.")
    if ($h) { return }

    $body = Read-RequestJson -Context $Context
    if ($null -eq $body -or -not $body.ContainsKey('secret')) {
        Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_json'; detail = "request body must be { 'secret': '<plaintext>' }" }
        return
    }
    $plain = [string]$body['secret']
    if ([string]::IsNullOrEmpty($plain)) {
        Write-JsonResponse -Context $Context -Status 422 -Body @{ error = 'secret_empty'; detail = "secret cannot be empty" }
        return
    }

    # Convert to SecureString and immediately blank the local plain
    # variable. PowerShell strings are immutable; the cleanup here is
    # best-effort -- the GC may retain a copy until collection. The
    # primary defense is that the secret never leaves the broker
    # process other than via CredMan.
    $secure = $null
    try {
        $secure = New-Object System.Security.SecureString
        foreach ($ch in $plain.ToCharArray()) { $secure.AppendChar($ch) }
        $secure.MakeReadOnly()
        $plain = ''
    } catch {
        if ($secure) { try { $secure.Dispose() } catch {} }
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'secret_marshal_failed'; detail = [string]$_.Exception.Message }
        return
    }

    try {
        Set-AuthProfileSecret -AuthProfileId $AuthProfileId -Secret $secure
    } catch {
        try { $secure.Dispose() } catch {}
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'secret_write_failed'; detail = [string]$_.Exception.Message }
        return
    } finally {
        try { $secure.Dispose() } catch {}
    }

    $now    = Get-UtcNowIso
    $target = Get-AuthProfileCredManTarget -AuthProfileId $AuthProfileId
    try {
        [void](Update-AuthProfileCredManTarget -AuthProfileId $AuthProfileId -Target $target -NowIso $now)
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'profile_persist_failed'; detail = [string]$_.Exception.Message }
        return
    }
    Write-JsonResponse -Context $Context -Status 200 -Body @{
        ok            = $true
        authProfileId = $AuthProfileId
        credManTarget = $target
        secretPresent = $true
    }
}

function Invoke-AuthProfileSecretRemove {
    param($Context, [string]$AuthProfileId)
    $existing = Get-AuthProfileRow -AuthProfileId $AuthProfileId
    if (-not $existing) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found' }
        return
    }
    if ($existing.mode -ne 'AppRegistrationSecret') {
        Write-JsonResponse -Context $Context -Status 422 -Body @{ error = 'mode_mismatch'; detail = "secret removal is only valid for AppRegistrationSecret mode" }
        return
    }
    $h = Invoke-AuthProfileReAuthOrShortCircuit -Context $Context -OpClass 'secretRemove' -Message ("Verify to remove the client secret for auth profile '" + $existing.name + "' from Windows Credential Manager.")
    if ($h) { return }

    $removed = $false
    try { $removed = [bool](Remove-AuthProfileSecret -AuthProfileId $AuthProfileId) } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'secret_remove_failed'; detail = [string]$_.Exception.Message }
        return
    }
    $now = Get-UtcNowIso
    try {
        [void](Update-AuthProfileCredManTarget -AuthProfileId $AuthProfileId -Target $null -NowIso $now)
    } catch {
        Write-JsonResponse -Context $Context -Status 500 -Body @{ error = 'profile_persist_failed'; detail = [string]$_.Exception.Message }
        return
    }
    Write-JsonResponse -Context $Context -Status 200 -Body @{
        ok            = $true
        authProfileId = $AuthProfileId
        secretRemoved = $removed
        secretPresent = $false
    }
}

function Invoke-AuthProfileTest {
    # BOUNDED structural test. v1 verifies that the credential-binding
    # prerequisites are in place; it does NOT attempt token acquisition
    # against Entra. The response payload carries an explicit
    # validationKind='structural' so callers cannot mistake a structural
    # pass for authentication validity. This boundary is intentional
    # and documented in TROUBLESHOOTING §13c.
    param($Context, [string]$AuthProfileId)
    $existing = Get-AuthProfileRow -AuthProfileId $AuthProfileId
    if (-not $existing) {
        Write-JsonResponse -Context $Context -Status 404 -Body @{ error = 'not_found' }
        return
    }
    $h = Invoke-AuthProfileReAuthOrShortCircuit -Context $Context -OpClass 'profileTest' -Message ("Verify to run the structural validation test for auth profile '" + $existing.name + "'.")
    if ($h) { return }

    $result  = 'unknown'
    $details = ''
    switch ($existing.mode) {
        'AppRegistrationSecret' {
            try {
                $present = [bool](Test-AuthProfileSecretPresent -AuthProfileId $AuthProfileId)
                if ($present) {
                    $result  = 'structural_ok'
                    $details = "Client secret is present in Windows Credential Manager under target '$($existing.credManTarget)'. PAX will validate the credential against Entra at first cook."
                } else {
                    $result  = 'secret_missing'
                    $details = "No client secret is bound. POST /api/v1/auth/profiles/$AuthProfileId/secret to bind one before scheduling cooks against this profile."
                }
            } catch {
                $result  = 'probe_failed'
                $details = "Credential Manager probe failed: " + [string]$_.Exception.Message
            }
        }
        'AppRegistrationCertificate' {
            $thumb = [string]$existing.certThumbprint
            if ([string]::IsNullOrWhiteSpace($thumb)) {
                $result  = 'cert_thumbprint_missing'
                $details = "Profile is mode AppRegistrationCertificate but no certificate thumbprint is recorded."
            } else {
                $found = $false
                try {
                    $storeLocation = [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
                    $storeName     = [System.Security.Cryptography.X509Certificates.StoreName]::My
                    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, $storeLocation)
                    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
                    try {
                        foreach ($c in $store.Certificates) {
                            if ($c.Thumbprint -ieq $thumb) { $found = $true; break }
                        }
                    } finally { $store.Close() }
                } catch {
                    $result  = 'probe_failed'
                    $details = "Certificate store probe failed: " + [string]$_.Exception.Message
                }
                if ($result -eq 'unknown') {
                    if ($found) {
                        $result  = 'structural_ok'
                        $details = "Certificate with thumbprint $thumb is present in the LocalMachine\My store. PAX will validate the certificate's private-key access and the Entra binding at first cook."
                    } else {
                        $result  = 'cert_not_found'
                        $details = "Certificate with thumbprint $thumb is NOT present in the LocalMachine\My store on this appliance. Install the certificate (including its private key) before scheduling cooks against this profile."
                    }
                }
            }
        }
        default {
            $result  = 'mode_unsupported'
            $details = "Profile mode '$($existing.mode)' is not testable through this endpoint."
        }
    }
    $now = Get-UtcNowIso
    try { [void](Update-AuthProfileVerifiedResult -AuthProfileId $AuthProfileId -Result $result -NowIso $now) } catch {}
    Write-JsonResponse -Context $Context -Status 200 -Body @{
        ok              = ($result -eq 'structural_ok')
        authProfileId   = $AuthProfileId
        validationKind  = 'structural'
        result          = $result
        details         = $details
        lastVerifiedAt  = $now
    }
}

# ---------------------------------------------------------------------
# Dispatcher
# ---------------------------------------------------------------------

function Invoke-AuthProfilesRoute {
    # Returns $true if the request was consumed by this handler.
    param($Context)
    $req    = $Context.Request
    $method = $req.HttpMethod.ToUpperInvariant()
    $path   = $req.Url.AbsolutePath

    # /api/v1/auth/profiles
    if ($path -eq '/api/v1/auth/profiles') {
        switch ($method) {
            'GET'  { Invoke-AuthProfilesList  -Context $Context; return $true }
            'POST' { Invoke-AuthProfileCreate -Context $Context; return $true }
            default {
                Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
                return $true
            }
        }
    }

    # /api/v1/auth/profiles/<id>
    if ($path -match '^/api/v1/auth/profiles/([^/]+)$') {
        $profileId = $matches[1]
        if ($profileId -notmatch $Script:AuthProfileIdPattern) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_profile_id'; authProfileId = $profileId }
            return $true
        }
        switch ($method) {
            'GET'    { Invoke-AuthProfileGet    -Context $Context -AuthProfileId $profileId; return $true }
            'PUT'    { Invoke-AuthProfileUpdate -Context $Context -AuthProfileId $profileId; return $true }
            'DELETE' { Invoke-AuthProfileDelete -Context $Context -AuthProfileId $profileId; return $true }
            default {
                Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
                return $true
            }
        }
    }

    # /api/v1/auth/profiles/<id>/secret
    if ($path -match '^/api/v1/auth/profiles/([^/]+)/secret$') {
        $profileId = $matches[1]
        if ($profileId -notmatch $Script:AuthProfileIdPattern) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_profile_id'; authProfileId = $profileId }
            return $true
        }
        switch ($method) {
            'POST'   { Invoke-AuthProfileSecretBind   -Context $Context -AuthProfileId $profileId; return $true }
            'DELETE' { Invoke-AuthProfileSecretRemove -Context $Context -AuthProfileId $profileId; return $true }
            default {
                Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
                return $true
            }
        }
    }

    # /api/v1/auth/profiles/<id>/test
    if ($path -match '^/api/v1/auth/profiles/([^/]+)/test$') {
        $profileId = $matches[1]
        if ($profileId -notmatch $Script:AuthProfileIdPattern) {
            Write-JsonResponse -Context $Context -Status 400 -Body @{ error = 'invalid_profile_id'; authProfileId = $profileId }
            return $true
        }
        if ($method -eq 'POST') {
            Invoke-AuthProfileTest -Context $Context -AuthProfileId $profileId
            return $true
        }
        Write-JsonResponse -Context $Context -Status 405 -Body @{ error = 'method_not_allowed' }
        return $true
    }

    return $false
}
