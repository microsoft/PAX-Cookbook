# =====================================================================
# Engine\ManifestSchema.psm1
# =====================================================================
#
# Stage 1 (Phase 13) -- read-only schema definition for the signed
# "approved-engine" manifest that the PAX team publishes to the
# distribution host referenced by VERSION.json.paxScript.engineManifestUrl.
#
# This module is INTENTIONALLY data + helpers only:
#   * It exports the schema constants (allowed top-level fields,
#     allowed per-script fields, allow-listed status values, etc.).
#   * It exports a Test-ApprovedEngineManifestSchema helper that
#     validates a hashtable against the schema. The helper is a pure
#     function with no I/O, no HTTP, no file writes, and no signature
#     verification.
#
# It MUST NOT:
#   * Fetch the manifest.
#   * Verify the manifest signature (Trust.psm1 / Signature.psm1 own
#     that, plumbed through in Stage 2).
#   * Read or write to disk.
#   * Mutate any PAX script bytes (see §4.5 immutability contract).
#
# Stage 2+ adds the fetch, signature-verify, entry-select and
# acquisition wiring. See Engine\Acquisition.psm1 (stubbed in Stage 1).

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Schema version the broker understands. The manifest's "schemaVersion"
# integer must match (or be downgrade-compatible with) this value. The
# allow-list is intentionally explicit -- Stage 2+ may expand it after
# review.
$Script:SupportedManifestSchemaVersions = @(1)

# Allowed top-level keys on the signed approved-engine manifest.
# Per plan §7.4.
$Script:AllowedManifestTopLevelKeys = @(
    'schemaVersion',
    'manifestVersion',
    'channel',
    'generatedAtUtc',
    'signingKeyId',
    'scripts'
)

# Allowed per-script entry keys. Per plan §7.4.
$Script:AllowedManifestScriptEntryKeys = @(
    'name',
    'version',
    'sha256',
    'downloadUrl',
    'status',
    'minCookbookVersion',
    'maxCookbookVersion',
    'releaseNotesUrl'
)

# Required per-script entry keys (subset of allowed). releaseNotesUrl
# is optional per §7.4.
$Script:RequiredManifestScriptEntryKeys = @(
    'name',
    'version',
    'sha256',
    'downloadUrl',
    'status',
    'minCookbookVersion',
    'maxCookbookVersion'
)

# Allow-listed status values for a script entry. Stage 2+ uses this to
# decide selectability (approved = selectable; deprecated = selectable
# with warning; withdrawn = not selectable).
$Script:AllowedManifestScriptStatuses = @(
    'approved',
    'deprecated',
    'withdrawn'
)

function Test-IsHashtableLike {
    param($Value)
    if ($null -eq $Value) { return $false }
    if ($Value -is [System.Collections.IDictionary]) { return $true }
    return $false
}

function Get-HashtableKeysSafe {
    param($Value)
    if ($null -eq $Value) { return @() }
    if ($Value -is [System.Collections.IDictionary]) { return @($Value.Keys) }
    return @()
}

function Test-NonEmptyString {
    param($Value)
    if ($null -eq $Value) { return $false }
    if ($Value -isnot [string]) { return $false }
    return -not [string]::IsNullOrWhiteSpace($Value)
}

function Test-IsHttpsUrl {
    param($Value)
    if (-not (Test-NonEmptyString $Value)) { return $false }
    $uri = $null
    if (-not [System.Uri]::TryCreate([string]$Value, [System.UriKind]::Absolute, [ref]$uri)) { return $false }
    return ($uri.Scheme -eq 'https')
}

function Test-IsSha256Hex {
    param($Value)
    if (-not (Test-NonEmptyString $Value)) { return $false }
    return ([string]$Value) -match '^[0-9A-Fa-f]{64}$'
}

function Test-ApprovedEngineManifestSchema {
    <#
    .SYNOPSIS
        Validates a parsed approved-engine manifest hashtable against
        the §7.4 schema. Returns @{ ok = $true; ... } or
        @{ ok = $false; error = <token>; message = <text> }.

    .DESCRIPTION
        Pure validator. No I/O, no HTTP, no signature verification.
        Reject-unknown is enforced at top level and per script entry.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Manifest
    )

    if (-not (Test-IsHashtableLike $Manifest)) {
        return @{ ok = $false; error = 'type_mismatch'; message = 'Approved-engine manifest must be a JSON object.' }
    }

    foreach ($k in (Get-HashtableKeysSafe $Manifest)) {
        if ($Script:AllowedManifestTopLevelKeys -notcontains $k) {
            return @{ ok = $false; error = 'unknown_field'; message = ('Top-level key "' + $k + '" is not allowed.') }
        }
    }

    if (-not $Manifest.Contains('schemaVersion')) {
        return @{ ok = $false; error = 'missing_field'; message = '"schemaVersion" is required.' }
    }
    $sv = $Manifest['schemaVersion']
    if ($sv -isnot [int] -and $sv -isnot [long]) {
        return @{ ok = $false; error = 'type_mismatch'; message = '"schemaVersion" must be an integer.' }
    }
    if ($Script:SupportedManifestSchemaVersions -notcontains [int]$sv) {
        return @{
            ok      = $false
            error   = 'unsupported_schema_version'
            message = ('"schemaVersion" = ' + [string]$sv + ' is not supported. Supported: ' + ($Script:SupportedManifestSchemaVersions -join ', '))
        }
    }

    if (-not $Manifest.Contains('manifestVersion') -or
        ($Manifest['manifestVersion'] -isnot [int] -and
         $Manifest['manifestVersion'] -isnot [long] -and
         -not (Test-NonEmptyString $Manifest['manifestVersion']))) {
        return @{ ok = $false; error = 'missing_field'; message = '"manifestVersion" is required (integer or non-empty string).' }
    }

    if (-not $Manifest.Contains('channel') -or -not (Test-NonEmptyString $Manifest['channel'])) {
        return @{ ok = $false; error = 'missing_field'; message = '"channel" is required.' }
    }

    if (-not $Manifest.Contains('generatedAtUtc') -or -not (Test-NonEmptyString $Manifest['generatedAtUtc'])) {
        return @{ ok = $false; error = 'missing_field'; message = '"generatedAtUtc" is required.' }
    }

    if (-not $Manifest.Contains('signingKeyId') -or -not (Test-NonEmptyString $Manifest['signingKeyId'])) {
        return @{ ok = $false; error = 'missing_field'; message = '"signingKeyId" is required.' }
    }

    if (-not $Manifest.Contains('scripts')) {
        return @{ ok = $false; error = 'missing_field'; message = '"scripts" is required.' }
    }
    $scripts = $Manifest['scripts']
    if ($null -eq $scripts -or -not ($scripts -is [System.Collections.IEnumerable]) -or $scripts -is [string]) {
        return @{ ok = $false; error = 'type_mismatch'; message = '"scripts" must be an array.' }
    }

    $idx = -1
    foreach ($entry in $scripts) {
        $idx++
        if (-not (Test-IsHashtableLike $entry)) {
            return @{ ok = $false; error = 'type_mismatch'; message = ('scripts[' + $idx + '] must be a JSON object.') }
        }
        foreach ($k in (Get-HashtableKeysSafe $entry)) {
            if ($Script:AllowedManifestScriptEntryKeys -notcontains $k) {
                return @{ ok = $false; error = 'unknown_field'; message = ('scripts[' + $idx + '] has unknown field "' + $k + '".') }
            }
        }
        foreach ($req in $Script:RequiredManifestScriptEntryKeys) {
            if (-not $entry.Contains($req)) {
                return @{ ok = $false; error = 'missing_field'; message = ('scripts[' + $idx + '] is missing required field "' + $req + '".') }
            }
        }
        if (-not (Test-NonEmptyString $entry['name'])) {
            return @{ ok = $false; error = 'type_mismatch'; message = ('scripts[' + $idx + '].name must be a non-empty string.') }
        }
        if (-not (Test-NonEmptyString $entry['version'])) {
            return @{ ok = $false; error = 'type_mismatch'; message = ('scripts[' + $idx + '].version must be a non-empty string.') }
        }
        if (-not (Test-IsSha256Hex $entry['sha256'])) {
            return @{ ok = $false; error = 'invalid_sha256'; message = ('scripts[' + $idx + '].sha256 must be a 64-character hex string.') }
        }
        if (-not (Test-IsHttpsUrl $entry['downloadUrl'])) {
            return @{ ok = $false; error = 'invalid_download_url'; message = ('scripts[' + $idx + '].downloadUrl must be an https:// URL.') }
        }
        if ($Script:AllowedManifestScriptStatuses -notcontains [string]$entry['status']) {
            return @{
                ok      = $false
                error   = 'invalid_status'
                message = ('scripts[' + $idx + '].status must be one of: ' + ($Script:AllowedManifestScriptStatuses -join ', '))
            }
        }
        if (-not (Test-NonEmptyString $entry['minCookbookVersion'])) {
            return @{ ok = $false; error = 'type_mismatch'; message = ('scripts[' + $idx + '].minCookbookVersion must be a non-empty string.') }
        }
        if (-not (Test-NonEmptyString $entry['maxCookbookVersion'])) {
            return @{ ok = $false; error = 'type_mismatch'; message = ('scripts[' + $idx + '].maxCookbookVersion must be a non-empty string.') }
        }
        if ($entry.Contains('releaseNotesUrl') -and $null -ne $entry['releaseNotesUrl'] -and
            -not (Test-IsHttpsUrl $entry['releaseNotesUrl'])) {
            return @{ ok = $false; error = 'invalid_release_notes_url'; message = ('scripts[' + $idx + '].releaseNotesUrl must be an https:// URL when present.') }
        }
    }

    return @{ ok = $true; entryCount = $idx + 1 }
}

Export-ModuleMember -Function @(
    'Test-ApprovedEngineManifestSchema',
    'Test-IsHashtableLike',
    'Get-HashtableKeysSafe',
    'Test-NonEmptyString',
    'Test-IsHttpsUrl',
    'Test-IsSha256Hex'
) -Variable @(
    'SupportedManifestSchemaVersions',
    'AllowedManifestTopLevelKeys',
    'AllowedManifestScriptEntryKeys',
    'RequiredManifestScriptEntryKeys',
    'AllowedManifestScriptStatuses'
)
