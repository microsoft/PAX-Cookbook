Set-StrictMode -Version Latest

# RecipeTakeoutSanitizer.psm1
#
# Builds a sanitized Recipe Takeout envelope from a source
# recipe hashtable. Pure helper module: no I/O, no broker
# routes, no external services, no PAX invocation.
#
# Authority: recipe-takeout.schema.json (v1) and the
# Decision Lock K-1..K-15. Stripped categories are listed in
# $Script:TakeoutExcludedCategories. Forbidden field-name
# lists are in $Script:TakeoutForbiddenSecretFields and
# $Script:TakeoutForbiddenArtifactFields.

# ---------------------------------------------------------
# Constants
# ---------------------------------------------------------

$Script:TakeoutSchemaVersion = 1
$Script:TakeoutKindConstant  = 'pax-cookbook.recipe-takeout'

$Script:AppRegistrationModes = @(
    'AppRegistrationSecret',
    'AppRegistrationCertificate'
)

$Script:TakeoutExcludedCategories = @(
    'chef_key_secrets',
    'chef_key_binding_authProfileId',
    'credential_manager_target_names',
    'access_tokens',
    'tenant_audit_output',
    'bake_records',
    'cooks_folder_contents',
    'cookbook_sqlite',
    'logs',
    'runtime_lock_state',
    'update_trust_files',
    'source_recipeId_as_active_id'
)

# Forbidden secret-property names. Compared case-insensitively.
$Script:TakeoutForbiddenSecretFields = @(
    'clientSecret',
    'secret',
    'password',
    'passphrase',
    'accessToken',
    'refreshToken',
    'bearerToken',
    'idToken',
    'apiKey',
    'api_key',
    'connectionString',
    'credentialTargetName',
    'certificateBase64',
    'certificatePfx',
    'privateKey'
)

# Forbidden artifact-property names. Compared case-insensitively.
$Script:TakeoutForbiddenArtifactFields = @(
    'cookContext',
    'cookLog',
    'bakeOutputs',
    'bakeResults',
    'databaseRows',
    'tenantAuditData',
    'windowsCredentialManager',
    'logs',
    'sqliteDump',
    'cookbookSqlite',
    'runtimeLockState'
)

# Stale-date threshold. K-3 / security model section "Category C".
$Script:TakeoutStaleDateRangeDays = 90

# ---------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------

function ConvertTo-TakeoutHashtable {
    # Defensive deep-copy of an input object into a fresh
    # hashtable / array tree. Does NOT mutate input. Drops
    # property types that are not safe to serialize.
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [hashtable]) {
        $copy = @{}
        foreach ($k in $Value.Keys) {
            $copy[[string]$k] = ConvertTo-TakeoutHashtable -Value $Value[$k]
        }
        return $copy
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $copy = @{}
        foreach ($k in $Value.Keys) {
            $copy[[string]$k] = ConvertTo-TakeoutHashtable -Value $Value[$k]
        }
        return $copy
    }
    if ($Value -is [System.Management.Automation.PSCustomObject] -or
        $Value -is [pscustomobject]) {
        $copy = @{}
        foreach ($p in $Value.PSObject.Properties) {
            $copy[[string]$p.Name] = ConvertTo-TakeoutHashtable -Value $p.Value
        }
        return $copy
    }
    if ($Value -is [string]) { return [string]$Value }
    if ($Value -is [bool])   { return [bool]$Value }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) {
        return $Value
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        $arr = New-Object System.Collections.ArrayList
        foreach ($item in $Value) {
            [void]$arr.Add( (ConvertTo-TakeoutHashtable -Value $item) )
        }
        return ,@($arr.ToArray())
    }
    return [string]$Value
}

function Test-TakeoutNodeForForbiddenName {
    # Walks a value tree; returns the first offending property
    # name found in $Forbidden. Comparison is case-insensitive.
    param(
        $Value,
        [string[]]$Forbidden
    )
    if ($null -eq $Value) { return $null }
    if ($Value -is [hashtable] -or $Value -is [System.Collections.IDictionary]) {
        foreach ($k in $Value.Keys) {
            $kn = [string]$k
            foreach ($f in $Forbidden) {
                if ([string]::Equals($kn, $f, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $kn
                }
            }
            $hit = Test-TakeoutNodeForForbiddenName -Value $Value[$k] -Forbidden $Forbidden
            if ($null -ne $hit) { return $hit }
        }
        return $null
    }
    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        foreach ($item in $Value) {
            $hit = Test-TakeoutNodeForForbiddenName -Value $item -Forbidden $Forbidden
            if ($null -ne $hit) { return $hit }
        }
    }
    return $null
}

function Get-TakeoutAuthBlock {
    param([hashtable]$Recipe)
    if (-not $Recipe.ContainsKey('auth')) { return @{} }
    $auth = $Recipe['auth']
    if ($null -eq $auth) { return @{} }
    if ($auth -is [hashtable]) { return $auth }
    return @{}
}

function Get-TakeoutDestinationPaths {
    param([hashtable]$Recipe)
    $paths = New-Object System.Collections.ArrayList
    if (-not $Recipe.ContainsKey('destinations')) { return @() }
    $destinations = $Recipe['destinations']
    if ($null -eq $destinations -or -not ($destinations -is [hashtable])) {
        return @()
    }
    foreach ($k in @('fact','userInfo')) {
        if (-not $destinations.ContainsKey($k)) { continue }
        $d = $destinations[$k]
        if ($null -eq $d -or -not ($d -is [hashtable])) { continue }
        foreach ($pf in @('path','appendFile','outputPath')) {
            if ($d.ContainsKey($pf)) {
                $v = [string]$d[$pf]
                if (-not [string]::IsNullOrWhiteSpace($v)) {
                    [void]$paths.Add($v)
                }
            }
        }
    }
    return ,@($paths.ToArray())
}

# ---------------------------------------------------------
# Exported helpers
# ---------------------------------------------------------

function Get-RecipeTakeoutWorkspaceFingerprint {
    # K-9: 8 lowercase hex chars of SHA-256(installRootPath).
    # Caller supplies the path; the helper itself is path-agnostic.
    param([Parameter(Mandatory)][string]$WorkspacePath)
    if ([string]::IsNullOrWhiteSpace($WorkspacePath)) { return $null }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($WorkspacePath)
    $sha   = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($bytes)
    } finally {
        $sha.Dispose()
    }
    $hex = -join ($hash | ForEach-Object { $_.ToString('x2') })
    return $hex.Substring(0, 8)
}

function Get-RecipeTakeoutChefKeyDisplayLabel {
    # K-2: sanitize a Chef's Key display label. Trim, strip
    # control characters, cap at 200 chars. Returns $null if
    # the resulting label is empty.
    param($Label)
    if ($null -eq $Label) { return $null }
    $s = [string]$Label
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $s.ToCharArray()) {
        if ([char]::IsControl($ch)) { continue }
        [void]$sb.Append($ch)
    }
    $clean = $sb.ToString().Trim()
    if ([string]::IsNullOrWhiteSpace($clean)) { return $null }
    if ($clean.Length -gt 200) {
        $clean = $clean.Substring(0, 200)
    }
    return $clean
}

function Test-RecipeTakeoutForbiddenFieldName {
    # Defense-in-depth scan. Returns $null if clean, otherwise
    # returns the first offending property name (string).
    param($Tree)
    return Test-TakeoutNodeForForbiddenName -Value $Tree -Forbidden $Script:TakeoutForbiddenSecretFields
}

function Test-RecipeTakeoutForbiddenSecretValue {
    # Companion to Test-RecipeTakeoutForbiddenFieldName.
    # Field-name check catches the structured-data leakage path;
    # this function scans for obvious secret value shapes that
    # could have hidden inside permitted string fields. Returns
    # $null if clean, otherwise a short tag describing the hit.
    param($Tree)
    $secretPatterns = @(
        @{ tag = 'jwt';                pattern = '(?i)\beyJ[0-9A-Za-z_-]{8,}\.[0-9A-Za-z_-]{8,}\.[0-9A-Za-z_-]{8,}\b' },
        @{ tag = 'pem_private_key';    pattern = '(?i)-----BEGIN[A-Z ]*PRIVATE KEY-----' },
        @{ tag = 'aws_access_key_id';  pattern = '\bAKIA[0-9A-Z]{16}\b' }
    )
    function Test-Local-Value {
        param($Val)
        if ($null -eq $Val) { return $null }
        if ($Val -is [string]) {
            foreach ($p in $secretPatterns) {
                if ([regex]::IsMatch($Val, $p.pattern)) { return $p.tag }
            }
            return $null
        }
        if ($Val -is [hashtable] -or $Val -is [System.Collections.IDictionary]) {
            foreach ($k in $Val.Keys) {
                $hit = Test-Local-Value -Val $Val[$k]
                if ($null -ne $hit) { return $hit }
            }
            return $null
        }
        if ($Val -is [System.Collections.IEnumerable]) {
            foreach ($item in $Val) {
                $hit = Test-Local-Value -Val $item
                if ($null -ne $hit) { return $hit }
            }
            return $null
        }
        return $null
    }
    return (Test-Local-Value -Val $Tree)
}

function Test-RecipeTakeoutWarningPathLocalAbsolute {
    param([string]$PathValue)
    if ([string]::IsNullOrWhiteSpace($PathValue)) { return $false }
    # Windows drive letter root, e.g. C:\foo
    if ([regex]::IsMatch($PathValue, '^[A-Za-z]:[\\/]')) { return $true }
    # POSIX root, e.g. /etc/foo. Excluded if already classified
    # as a UNC by the caller.
    if ($PathValue.StartsWith('\\')) { return $false }
    if ($PathValue.StartsWith('/'))  { return $true }
    return $false
}

function Test-RecipeTakeoutWarningPathUnc {
    param([string]$PathValue)
    if ([string]::IsNullOrWhiteSpace($PathValue)) { return $false }
    return $PathValue.StartsWith('\\')
}

function Test-RecipeTakeoutWarningPathUserSpecific {
    param([string]$PathValue)
    if ([string]::IsNullOrWhiteSpace($PathValue)) { return $false }
    $envTokens = @('%USERPROFILE%','%LOCALAPPDATA%','%APPDATA%','%HOMEDRIVE%','%HOMEPATH%','%TEMP%','%TMP%')
    foreach ($t in $envTokens) {
        if ($PathValue.IndexOf($t, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    }
    if ($PathValue.StartsWith('~')) { return $true }
    return $false
}

function Test-RecipeTakeoutWarningTenantIdPresent {
    param([hashtable]$Recipe)
    $auth = Get-TakeoutAuthBlock -Recipe $Recipe
    if (-not $auth.ContainsKey('tenantId')) { return $false }
    $v = [string]$auth['tenantId']
    return -not [string]::IsNullOrWhiteSpace($v)
}

function Test-RecipeTakeoutWarningUserFilterPresent {
    param([hashtable]$Recipe)
    if (-not $Recipe.ContainsKey('query')) { return $false }
    $q = $Recipe['query']
    if (-not ($q -is [hashtable])) { return $false }
    if (-not $q.ContainsKey('userIds')) { return $false }
    $u = $q['userIds']
    if ($null -eq $u) { return $false }
    if ($u -is [System.Collections.IEnumerable] -and -not ($u -is [string])) {
        foreach ($x in $u) { return $true }
        return $false
    }
    return -not [string]::IsNullOrWhiteSpace([string]$u)
}

function Test-RecipeTakeoutWarningGroupFilterPresent {
    param([hashtable]$Recipe)
    if (-not $Recipe.ContainsKey('query')) { return $false }
    $q = $Recipe['query']
    if (-not ($q -is [hashtable])) { return $false }
    if (-not $q.ContainsKey('groupNames')) { return $false }
    $g = $q['groupNames']
    if ($null -eq $g) { return $false }
    if ($g -is [System.Collections.IEnumerable] -and -not ($g -is [string])) {
        foreach ($x in $g) { return $true }
        return $false
    }
    return -not [string]::IsNullOrWhiteSpace([string]$g)
}

function Test-RecipeTakeoutWarningAgentFilterPresent {
    param([hashtable]$Recipe)
    if (-not $Recipe.ContainsKey('query')) { return $false }
    $q = $Recipe['query']
    if (-not ($q -is [hashtable])) { return $false }
    if (-not $q.ContainsKey('agentFilter')) { return $false }
    $af = $q['agentFilter']
    if (-not ($af -is [hashtable])) { return $false }
    if (-not $af.ContainsKey('agentIds')) { return $false }
    $ids = $af['agentIds']
    if ($null -eq $ids) { return $false }
    if ($ids -is [System.Collections.IEnumerable] -and -not ($ids -is [string])) {
        foreach ($x in $ids) { return $true }
        return $false
    }
    return -not [string]::IsNullOrWhiteSpace([string]$ids)
}

function Test-RecipeTakeoutWarningExtraArgumentsPresent {
    param([hashtable]$Recipe)
    if (-not $Recipe.ContainsKey('advanced')) { return $false }
    $a = $Recipe['advanced']
    if (-not ($a -is [hashtable])) { return $false }
    if (-not $a.ContainsKey('extraArguments')) { return $false }
    $v = [string]$a['extraArguments']
    return -not [string]::IsNullOrWhiteSpace($v)
}

function Test-RecipeTakeoutWarningDateRangeStale {
    param(
        [hashtable]$Recipe,
        [datetime]$ExportedAtUtc
    )
    if (-not $Recipe.ContainsKey('query')) { return $false }
    $q = $Recipe['query']
    if (-not ($q -is [hashtable])) { return $false }
    if (-not $q.ContainsKey('endDate')) { return $false }
    $endStr = [string]$q['endDate']
    if ([string]::IsNullOrWhiteSpace($endStr)) { return $false }
    $endDt = [datetime]::MinValue
    if (-not [datetime]::TryParse($endStr, [ref]$endDt)) { return $false }
    $endUtc = $endDt.ToUniversalTime()
    $exportUtc = $ExportedAtUtc.ToUniversalTime()
    $delta = $exportUtc - $endUtc
    return ($delta.TotalDays -gt $Script:TakeoutStaleDateRangeDays)
}

function Get-RecipeTakeoutWarnings {
    param(
        [Parameter(Mandatory)][hashtable]$Recipe,
        [Parameter(Mandatory)][datetime]$ExportedAtUtc
    )
    $warnings = New-Object System.Collections.ArrayList

    # Path warnings (one warning per matching path, with JSON pointer).
    if ($Recipe.ContainsKey('destinations')) {
        $destinations = $Recipe['destinations']
        if ($destinations -is [hashtable]) {
            foreach ($k in @('fact','userInfo')) {
                if (-not $destinations.ContainsKey($k)) { continue }
                $d = $destinations[$k]
                if (-not ($d -is [hashtable])) { continue }
                foreach ($pf in @('path','appendFile','outputPath')) {
                    if (-not $d.ContainsKey($pf)) { continue }
                    $v = [string]$d[$pf]
                    if ([string]::IsNullOrWhiteSpace($v)) { continue }
                    $pointer = "/destinations/$k/$pf"
                    if (Test-RecipeTakeoutWarningPathUnc -PathValue $v) {
                        [void]$warnings.Add(@{ code = 'path_unc_review_recommended'; path = $pointer })
                    }
                    elseif (Test-RecipeTakeoutWarningPathLocalAbsolute -PathValue $v) {
                        [void]$warnings.Add(@{ code = 'path_local_absolute_needs_review'; path = $pointer })
                    }
                    if (Test-RecipeTakeoutWarningPathUserSpecific -PathValue $v) {
                        [void]$warnings.Add(@{ code = 'path_user_specific_review_recommended'; path = $pointer })
                    }
                }
            }
        }
    }

    if (Test-RecipeTakeoutWarningTenantIdPresent -Recipe $Recipe) {
        [void]$warnings.Add(@{ code = 'tenant_id_present_review_recommended'; path = '/auth/tenantId' })
    }
    if (Test-RecipeTakeoutWarningUserFilterPresent -Recipe $Recipe) {
        [void]$warnings.Add(@{ code = 'user_filter_values_present_review_recommended'; path = '/query/userIds' })
    }
    if (Test-RecipeTakeoutWarningGroupFilterPresent -Recipe $Recipe) {
        [void]$warnings.Add(@{ code = 'group_filter_values_present_review_recommended'; path = '/query/groupNames' })
    }
    if (Test-RecipeTakeoutWarningAgentFilterPresent -Recipe $Recipe) {
        [void]$warnings.Add(@{ code = 'agent_filter_values_present_review_recommended'; path = '/query/agentFilter/agentIds' })
    }
    if (Test-RecipeTakeoutWarningExtraArgumentsPresent -Recipe $Recipe) {
        [void]$warnings.Add(@{ code = 'extra_arguments_present_review_recommended'; path = '/advanced/extraArguments' })
    }
    if (Test-RecipeTakeoutWarningDateRangeStale -Recipe $Recipe -ExportedAtUtc $ExportedAtUtc) {
        [void]$warnings.Add(@{ code = 'date_range_may_be_stale'; path = '/query/endDate' })
    }

    $auth = Get-TakeoutAuthBlock -Recipe $Recipe
    if ($auth.ContainsKey('mode')) {
        $mode = [string]$auth['mode']
        if ($Script:AppRegistrationModes -contains $mode) {
            [void]$warnings.Add(@{ code = 'chef_key_required_select_local_binding'; path = '/auth/authProfileId' })
        }
    }

    return ,@($warnings.ToArray())
}

function Get-RecipeTakeoutEnvelope {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Recipe,
        [Parameter(Mandatory)][datetime]$ExportedAtUtc,
        $CookbookVersion       = $null,
        $BundledPaxVersion     = $null,
        $ReleaseChannel        = $null,
        $WorkspaceInstallPath  = $null,
        $ChefKeySourceLabel    = $null
    )

    # 1. Defensive deep-copy so caller's object is not touched.
    $copy = ConvertTo-TakeoutHashtable -Value $Recipe
    if (-not ($copy -is [hashtable])) {
        throw "Get-RecipeTakeoutEnvelope: Recipe must convert to a hashtable; got $($copy.GetType().FullName)"
    }

    # 2. Capture source metadata BEFORE we mutate the copy.
    $sourceId        = if ($copy.ContainsKey('recipeId')) { [string]$copy['recipeId'] } else { $null }
    $sourceName      = $null
    if ($copy.ContainsKey('identity') -and ($copy['identity'] -is [hashtable]) -and $copy['identity'].ContainsKey('name')) {
        $sourceName = [string]$copy['identity']['name']
    }
    $sourceCreatedAt = if ($copy.ContainsKey('createdAt')) { [string]$copy['createdAt'] } else { $null }
    $sourceUpdatedAt = if ($copy.ContainsKey('updatedAt')) { [string]$copy['updatedAt'] } else { $null }
    $sourceTemplate  = $null
    if ($copy.ContainsKey('createdBy') -and ($copy['createdBy'] -is [hashtable]) -and $copy['createdBy'].ContainsKey('fromTemplate')) {
        $ft = $copy['createdBy']['fromTemplate']
        if ($ft -is [hashtable]) { $sourceTemplate = $ft }
    }

    # 3. Build sanitized recipe payload (allow-list, not strip-list).
    $payload = @{}
    $authoredKeys = @(
        'recipeSchemaVersion','paxAdapterVersion','identity','ingredients',
        'query','processing','destinations','auth','advanced','executionMode',
        'createdBy'
    )
    foreach ($k in $authoredKeys) {
        if ($copy.ContainsKey($k)) {
            $payload[$k] = $copy[$k]
        }
    }

    # 4. Strip Chef's Key binding from the exported recipe payload.
    if ($payload.ContainsKey('auth') -and ($payload['auth'] -is [hashtable])) {
        if ($payload['auth'].ContainsKey('authProfileId')) {
            [void]$payload['auth'].Remove('authProfileId')
        }
    }

    # 5. Build chefKey requirement block.
    $authMode = $null
    if ($payload.ContainsKey('auth') -and ($payload['auth'] -is [hashtable]) -and $payload['auth'].ContainsKey('mode')) {
        $authMode = [string]$payload['auth']['mode']
    }
    $chefKey = @{}
    if ($Script:AppRegistrationModes -contains $authMode) {
        $chefKey['requirement'] = 'required'
        $chefKey['mode']        = $authMode
    } else {
        $chefKey['requirement'] = 'none'
    }
    $cleanLabel = Get-RecipeTakeoutChefKeyDisplayLabel -Label $ChefKeySourceLabel
    if (-not [string]::IsNullOrWhiteSpace($cleanLabel)) {
        $chefKey['sourceDisplayLabel'] = $cleanLabel
    }

    # 6. Build sourceRecipe metadata.
    $sourceRecipe = @{}
    if ($null -ne $sourceId)        { $sourceRecipe['id']        = $sourceId }
    if ($null -ne $sourceName)      { $sourceRecipe['name']      = $sourceName }
    if ($null -ne $sourceCreatedAt) { $sourceRecipe['createdAt'] = $sourceCreatedAt }
    if ($null -ne $sourceUpdatedAt) { $sourceRecipe['updatedAt'] = $sourceUpdatedAt }
    if ($null -ne $sourceTemplate)  { $sourceRecipe['sourceTemplate'] = $sourceTemplate }

    # 7. Build exportedBy block.
    $exportedBy = @{}
    if (-not [string]::IsNullOrWhiteSpace([string]$CookbookVersion))   { $exportedBy['cookbookVersion']   = [string]$CookbookVersion }
    if (-not [string]::IsNullOrWhiteSpace([string]$BundledPaxVersion)) { $exportedBy['bundledPaxVersion'] = [string]$BundledPaxVersion }
    if (-not [string]::IsNullOrWhiteSpace([string]$ReleaseChannel))    { $exportedBy['releaseChannel']    = [string]$ReleaseChannel }
    if (-not [string]::IsNullOrWhiteSpace([string]$WorkspaceInstallPath)) {
        $fp = Get-RecipeTakeoutWorkspaceFingerprint -WorkspacePath ([string]$WorkspaceInstallPath)
        if ($null -ne $fp) { $exportedBy['workspaceFingerprint'] = $fp }
    }

    # 8. Build warnings.
    $warnings = Get-RecipeTakeoutWarnings -Recipe $payload -ExportedAtUtc $ExportedAtUtc

    # 9. Build the envelope.
    $envelope = [ordered]@{
        takeoutSchemaVersion = $Script:TakeoutSchemaVersion
        kind                 = $Script:TakeoutKindConstant
        exportedAtUtc        = $ExportedAtUtc.ToUniversalTime().ToString('o')
    }
    if ($exportedBy.Count -gt 0)   { $envelope['exportedBy']   = $exportedBy }
    $envelope['recipe']   = $payload
    $envelope['chefKey']  = $chefKey
    if ($sourceRecipe.Count -gt 0) { $envelope['sourceRecipe'] = $sourceRecipe }
    $envelope['warnings'] = $warnings
    $envelope['excluded'] = @($Script:TakeoutExcludedCategories)

    # 10. Defense-in-depth scans. These should never fire on a
    # legitimate recipe shape; if they do, the source recipe
    # would already have failed save-time validation.
    $offendingSecret = Test-RecipeTakeoutForbiddenFieldName -Tree $envelope
    if ($null -ne $offendingSecret) {
        throw "Get-RecipeTakeoutEnvelope: refusing to export envelope; forbidden secret field name found at any depth: '$offendingSecret'"
    }
    $offendingArtifact = Test-TakeoutNodeForForbiddenName -Value $envelope -Forbidden $Script:TakeoutForbiddenArtifactFields
    if ($null -ne $offendingArtifact) {
        throw "Get-RecipeTakeoutEnvelope: refusing to export envelope; forbidden artifact field name found at any depth: '$offendingArtifact'"
    }
    $obviousSecretValue = Test-RecipeTakeoutForbiddenSecretValue -Tree $envelope
    if ($null -ne $obviousSecretValue) {
        throw "Get-RecipeTakeoutEnvelope: refusing to export envelope; obvious secret value pattern detected: '$obviousSecretValue'"
    }

    return $envelope
}

Export-ModuleMember -Function @(
    'Get-RecipeTakeoutEnvelope',
    'Get-RecipeTakeoutWorkspaceFingerprint',
    'Get-RecipeTakeoutChefKeyDisplayLabel',
    'Test-RecipeTakeoutForbiddenFieldName',
    'Test-RecipeTakeoutForbiddenSecretValue',
    'Get-RecipeTakeoutWarnings',
    'Test-RecipeTakeoutWarningPathLocalAbsolute',
    'Test-RecipeTakeoutWarningPathUnc',
    'Test-RecipeTakeoutWarningPathUserSpecific',
    'Test-RecipeTakeoutWarningTenantIdPresent',
    'Test-RecipeTakeoutWarningUserFilterPresent',
    'Test-RecipeTakeoutWarningGroupFilterPresent',
    'Test-RecipeTakeoutWarningAgentFilterPresent',
    'Test-RecipeTakeoutWarningExtraArgumentsPresent',
    'Test-RecipeTakeoutWarningDateRangeStale'
)
