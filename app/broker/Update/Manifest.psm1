#requires -Version 7.4

# Manifest.psm1
#
# Update-manifest fetch and validation.
#
# Exposes:
#   Test-UpdateManifestUrl       - URL syntax validation (HTTPS-only, with
#                                  loopback HTTP allowed for tests). Returns
#                                  [hashtable] { ok = $true } or
#                                  { ok = $false; error = '<token>'; message = '<text>' }.
#
#   Get-UpdateManifest           - Synchronously fetches the manifest from
#                                  the configured URL. Single attempt, no
#                                  retry, no redirect-follow. Returns the
#                                  parsed manifest body on success, or a
#                                  structured error object on failure.
#
#   Test-UpdateManifestSchema    - Structural validation of a parsed
#                                  manifest body. Rejects unknown top-level
#                                  fields, missing required fields, type
#                                  drift, and unsupported schemaVersion.
#                                  Returns { ok = $true } or
#                                  { ok = $false; error; message }.
#
# This module is the ONLY surface in the broker that performs outbound
# HTTPS. The choke-point is Get-UpdateManifest. Package downloads live in
# Package.psm1; everything else in the broker is loopback or filesystem.
#
# Forbidden by design (do NOT add):
#   - automatic retry / backoff loops
#   - redirect-following
#   - credentialed requests
#   - cookie persistence
#   - background polling / scheduled fetch
#   - User-Agent customization that varies per request
#   - any telemetry POST
#   - any non-manifest GET against any URL

Set-StrictMode -Version Latest

# Hard ceiling on the manifest body size. Real manifests are < 4 KB.
# A 64 KB ceiling allows comfortable headroom while preventing a
# misconfigured / hostile endpoint from streaming megabytes.
$Script:MaxManifestBytes  = 64 * 1024

# Timeout for the manifest fetch. Manifest is tiny; 30 s covers slow
# corporate proxies without becoming a UX hazard.
$Script:ManifestTimeoutSec = 30

# Supported manifest schema versions. The remote manifest's
# schemaVersion field MUST be in this set or the manifest is rejected.
$Script:SupportedManifestSchemaVersions = @(1)

# Allowed top-level keys on a remote manifest. Any other top-level key
# causes a hard rejection (no silent ignore, no forward-compat sloppiness).
$Script:AllowedManifestTopKeys = @(
    'schemaVersion',
    'channel',
    'releaseTimestamp',
    'latestCookbook',
    'includedPaxScript',
    'compatibility'
)

# Allowed nested keys per object. Same hard-reject rule applies.
$Script:AllowedLatestCookbookKeys    = @('version', 'packageUrl', 'sha256', 'releaseNotesUrl')
$Script:AllowedIncludedPaxScriptKeys = @(
    'name',
    'version',
    'relativePath',
    'sha256',
    'acquisitionPolicy',
    'exportEnabled',
    'engineManifestUrl',
    'engineManifestTrustAnchorThumbprint',
    'manifestSignaturePolicy'
)
$Script:AllowedCompatibilityKeys     = @('minCookbookVersionForPaxScript', 'minimumCompatibleInstallerVersion')

# Acquisition-policy allow-list. "external" is the only value accepted
# on v1 builds; "embedded" is permitted ONLY for the Phase 12 historical
# artifact and triggers a legacy_historical_artifact_detected warning.
$Script:AllowedAcquisitionPolicies         = @('external', 'embedded')
$Script:DefaultAcquisitionPolicy           = 'embedded'

# manifestSignaturePolicy controls whether detached signature
# verification runs at runtime. "required" (default) is the production
# value; "internal-test-bypass" is the internal-test unsigned build
# value (only allowed when the build was invoked with both
# -InternalTestProfile AND -InternalTestUnsigned). When the policy is
# "internal-test-bypass", engineManifestTrustAnchorThumbprint is the
# ONLY non-40-hex value accepted: literal JSON null. Placeholder
# strings remain rejected in every profile.
$Script:AllowedManifestSignaturePolicies   = @('required', 'internal-test-bypass')
$Script:DefaultManifestSignaturePolicy     = 'required'

# ---------------------------------------------------------------------
# URL validation
# ---------------------------------------------------------------------
function Test-UpdateManifestUrl {
    param([string]$Url)

    if ([string]::IsNullOrWhiteSpace($Url)) {
        return @{ ok = $false; error = 'not_configured'; message = 'No update manifest URL is configured.' }
    }

    # Reject template placeholders that ship in repo manifests.
    if ($Url -match '^<[A-Z0-9_]+>$') {
        return @{ ok = $false; error = 'placeholder_url'; message = 'Update manifest URL is a release placeholder.' }
    }

    $uri = $null
    try {
        $uri = [Uri]::new($Url, [UriKind]::Absolute)
    } catch {
        return @{ ok = $false; error = 'malformed_url'; message = 'Update manifest URL is not a well-formed absolute URI.' }
    }

    if (-not $uri.IsAbsoluteUri) {
        return @{ ok = $false; error = 'malformed_url'; message = 'Update manifest URL is not absolute.' }
    }

    $scheme = $uri.Scheme.ToLowerInvariant()
    if ($scheme -eq 'https') {
        return @{ ok = $true; uri = $uri }
    }
    if ($scheme -eq 'http' -and $uri.IsLoopback) {
        # Loopback HTTP is permitted for local verification harnesses
        # only. Production manifests must be HTTPS.
        return @{ ok = $true; uri = $uri }
    }

    return @{ ok = $false; error = 'forbidden_scheme'; message = ('Update manifest URL must use HTTPS (got "' + $scheme + '").') }
}

# ---------------------------------------------------------------------
# Manifest fetch (the choke-point for outbound HTTPS)
# ---------------------------------------------------------------------
function Get-UpdateManifest {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$CookbookVersion
    )

    $urlCheck = Test-UpdateManifestUrl -Url $Url
    if (-not $urlCheck.ok) {
        return $urlCheck
    }

    $userAgent = 'PAXCookbook/' + $CookbookVersion + ' (manifest-check)'

    # Single deterministic GET. No retry. No redirect follow. Tight
    # timeout. The TLS-version selection is whatever .NET picks for the
    # current host; we do not pin a specific protocol to avoid drifting
    # from OS-level allowed ciphers.
    $response = $null
    try {
        $response = Invoke-WebRequest `
            -Uri $urlCheck.uri.AbsoluteUri `
            -Method GET `
            -Headers @{ 'Accept' = 'application/json' } `
            -UserAgent $userAgent `
            -UseBasicParsing `
            -MaximumRedirection 0 `
            -TimeoutSec $Script:ManifestTimeoutSec `
            -ErrorAction Stop
    } catch {
        return @{
            ok      = $false
            error   = 'fetch_failed'
            message = ('Manifest fetch failed: ' + $_.Exception.Message)
        }
    }

    if (-not $response) {
        return @{ ok = $false; error = 'fetch_failed'; message = 'Manifest fetch returned no response.' }
    }

    # Reject suspiciously large responses. The manifest should be tiny.
    $rawBytes = $response.RawContentLength
    if ($null -ne $rawBytes -and $rawBytes -gt $Script:MaxManifestBytes) {
        return @{
            ok      = $false
            error   = 'manifest_too_large'
            message = ('Manifest exceeds ' + $Script:MaxManifestBytes + ' bytes.')
        }
    }

    $text = [string]$response.Content
    if ($null -ne $text -and $text.Length -gt $Script:MaxManifestBytes) {
        return @{
            ok      = $false
            error   = 'manifest_too_large'
            message = ('Manifest exceeds ' + $Script:MaxManifestBytes + ' bytes.')
        }
    }

    $body = $null
    try {
        $body = $text | ConvertFrom-Json -ErrorAction Stop -AsHashtable
    } catch {
        return @{
            ok      = $false
            error   = 'manifest_unparseable'
            message = ('Manifest is not valid JSON: ' + $_.Exception.Message)
        }
    }

    if (-not ($body -is [hashtable])) {
        return @{
            ok      = $false
            error   = 'manifest_not_object'
            message = 'Manifest top-level value is not a JSON object.'
        }
    }

    return @{ ok = $true; body = $body; rawText = $text; sourceUrl = $urlCheck.uri.AbsoluteUri }
}

# ---------------------------------------------------------------------
# Schema validation
# ---------------------------------------------------------------------
function Test-Hashtable {
    param($Value)
    return ($Value -is [hashtable] -or $Value -is [System.Collections.Specialized.OrderedDictionary])
}

function Test-RequiredString {
    param($Value)
    return ($Value -is [string] -and -not [string]::IsNullOrWhiteSpace($Value))
}

function Get-HashtableKeys {
    param($Value)
    if ($Value -is [hashtable]) { return [string[]]@($Value.Keys) }
    if ($Value -is [System.Collections.Specialized.OrderedDictionary]) { return [string[]]@($Value.Keys) }
    return @()
}

function Test-UpdateManifestSchema {
    param([Parameter(Mandatory)]$Manifest)

    if (-not (Test-Hashtable $Manifest)) {
        return @{ ok = $false; error = 'manifest_not_object'; message = 'Manifest is not a JSON object.' }
    }

    $topKeys = Get-HashtableKeys $Manifest
    foreach ($k in $topKeys) {
        if ($Script:AllowedManifestTopKeys -notcontains $k) {
            return @{ ok = $false; error = 'unknown_field'; message = ('Manifest has an unknown top-level field: "' + $k + '".') }
        }
    }

    # schemaVersion -- required, integer, must be supported.
    if (-not $Manifest.Contains('schemaVersion')) {
        return @{ ok = $false; error = 'missing_field'; message = 'Manifest is missing "schemaVersion".' }
    }
    $sv = $Manifest['schemaVersion']
    if (-not ($sv -is [int] -or $sv -is [long])) {
        return @{ ok = $false; error = 'type_mismatch'; message = '"schemaVersion" must be an integer.' }
    }
    if ($Script:SupportedManifestSchemaVersions -notcontains [int]$sv) {
        return @{
            ok      = $false
            error   = 'unsupported_schema_version'
            message = ('Manifest schemaVersion ' + $sv + ' is not supported by this Cookbook build.')
        }
    }

    # channel -- required string.
    if (-not (Test-RequiredString $Manifest['channel'])) {
        return @{ ok = $false; error = 'missing_field'; message = 'Manifest is missing "channel".' }
    }

    # latestCookbook -- required object with mandated fields.
    if (-not $Manifest.Contains('latestCookbook')) {
        return @{ ok = $false; error = 'missing_field'; message = 'Manifest is missing "latestCookbook".' }
    }
    $lc = $Manifest['latestCookbook']
    if (-not (Test-Hashtable $lc)) {
        return @{ ok = $false; error = 'type_mismatch'; message = '"latestCookbook" must be a JSON object.' }
    }
    foreach ($k in (Get-HashtableKeys $lc)) {
        if ($Script:AllowedLatestCookbookKeys -notcontains $k) {
            return @{ ok = $false; error = 'unknown_field'; message = ('"latestCookbook" has an unknown field: "' + $k + '".') }
        }
    }
    if (-not (Test-RequiredString $lc['version'])) {
        return @{ ok = $false; error = 'missing_field'; message = '"latestCookbook.version" is required.' }
    }
    if (-not (Test-RequiredString $lc['packageUrl'])) {
        return @{ ok = $false; error = 'missing_field'; message = '"latestCookbook.packageUrl" is required.' }
    }
    if (-not (Test-RequiredString $lc['sha256'])) {
        return @{ ok = $false; error = 'missing_field'; message = '"latestCookbook.sha256" is required.' }
    }
    if ($lc['sha256'] -notmatch '^[0-9A-Fa-f]{64}$') {
        return @{ ok = $false; error = 'invalid_sha256'; message = '"latestCookbook.sha256" must be a 64-character hex string.' }
    }
    # packageUrl must pass the same syntax test as the manifest URL.
    $pkgUrlCheck = Test-UpdateManifestUrl -Url ([string]$lc['packageUrl'])
    if (-not $pkgUrlCheck.ok) {
        return @{ ok = $false; error = ('package_url_' + $pkgUrlCheck.error); message = ('Manifest "latestCookbook.packageUrl" rejected: ' + $pkgUrlCheck.message) }
    }

    # includedPaxScript -- required object, mandated fields.
    if (-not $Manifest.Contains('includedPaxScript')) {
        return @{ ok = $false; error = 'missing_field'; message = 'Manifest is missing "includedPaxScript".' }
    }
    $ip = $Manifest['includedPaxScript']
    if (-not (Test-Hashtable $ip)) {
        return @{ ok = $false; error = 'type_mismatch'; message = '"includedPaxScript" must be a JSON object.' }
    }
    foreach ($k in (Get-HashtableKeys $ip)) {
        if ($Script:AllowedIncludedPaxScriptKeys -notcontains $k) {
            return @{ ok = $false; error = 'unknown_field'; message = ('"includedPaxScript" has an unknown field: "' + $k + '".') }
        }
    }
    if (-not (Test-RequiredString $ip['version'])) {
        return @{ ok = $false; error = 'missing_field'; message = '"includedPaxScript.version" is required.' }
    }
    if (-not (Test-RequiredString $ip['sha256'])) {
        return @{ ok = $false; error = 'missing_field'; message = '"includedPaxScript.sha256" is required.' }
    }
    if ($ip['sha256'] -notmatch '^[0-9A-Fa-f]{64}$') {
        return @{ ok = $false; error = 'invalid_sha256'; message = '"includedPaxScript.sha256" must be a 64-character hex string.' }
    }

    # acquisitionPolicy -- optional in v1 for forward-compat with manifests
    # missing the field (treated as legacy "embedded"). When present, must
    # be one of the allow-listed values. "embedded" is accepted but emits
    # a structured warning so the caller can surface that a Phase 12
    # historical artifact is in play.
    $warnings = @()
    $effectivePolicy = $Script:DefaultAcquisitionPolicy
    if ($ip.Contains('acquisitionPolicy')) {
        $rawPolicy = $ip['acquisitionPolicy']
        if (-not (Test-RequiredString $rawPolicy)) {
            return @{ ok = $false; error = 'type_mismatch'; message = '"includedPaxScript.acquisitionPolicy" must be a non-empty string.' }
        }
        if ($Script:AllowedAcquisitionPolicies -notcontains [string]$rawPolicy) {
            return @{
                ok      = $false
                error   = 'invalid_acquisition_policy'
                message = ('"includedPaxScript.acquisitionPolicy" must be one of: ' + ($Script:AllowedAcquisitionPolicies -join ', ') + '. Got "' + [string]$rawPolicy + '".')
            }
        }
        $effectivePolicy = [string]$rawPolicy
        if ($effectivePolicy -eq 'embedded') {
            $warnings += 'legacy_historical_artifact_detected'
        }
    } else {
        $warnings += 'legacy_historical_artifact_detected'
    }

    # exportEnabled -- optional boolean. Strings ("true" / "false") are
    # rejected; the field is either a true JSON boolean or absent.
    if ($ip.Contains('exportEnabled')) {
        $rawExport = $ip['exportEnabled']
        if ($rawExport -isnot [bool]) {
            return @{ ok = $false; error = 'type_mismatch'; message = '"includedPaxScript.exportEnabled" must be a JSON boolean.' }
        }
    }

    # manifestSignaturePolicy -- optional in v1 for forward-compat with
    # manifests missing the field (defaults to "required"). When present,
    # must be one of the allow-listed enum values. Production builds
    # require "required"; "internal-test-bypass" is the internal-test
    # unsigned build value and is only set by the release pipeline when
    # both -InternalTestProfile and -InternalTestUnsigned are passed.
    $effectiveSignaturePolicy = $Script:DefaultManifestSignaturePolicy
    if ($ip.Contains('manifestSignaturePolicy')) {
        $rawSigPolicy = $ip['manifestSignaturePolicy']
        if (-not (Test-RequiredString $rawSigPolicy)) {
            return @{ ok = $false; error = 'type_mismatch'; message = '"includedPaxScript.manifestSignaturePolicy" must be a non-empty string.' }
        }
        if ($Script:AllowedManifestSignaturePolicies -notcontains [string]$rawSigPolicy) {
            return @{
                ok      = $false
                error   = 'invalid_manifest_signature_policy'
                message = ('"includedPaxScript.manifestSignaturePolicy" must be one of: ' + ($Script:AllowedManifestSignaturePolicies -join ', ') + '. Got "' + [string]$rawSigPolicy + '".')
            }
        }
        $effectiveSignaturePolicy = [string]$rawSigPolicy
    }

    # engineManifestUrl -- required when acquisitionPolicy == "external".
    # Reuses Test-UpdateManifestUrl so HTTPS-only / loopback-HTTP-only
    # rules apply identically to the package URL.
    if ($effectivePolicy -eq 'external') {
        if (-not $ip.Contains('engineManifestUrl') -or -not (Test-RequiredString $ip['engineManifestUrl'])) {
            return @{ ok = $false; error = 'missing_field'; message = '"includedPaxScript.engineManifestUrl" is required when acquisitionPolicy is "external".' }
        }
        $emuCheck = Test-UpdateManifestUrl -Url ([string]$ip['engineManifestUrl'])
        if (-not $emuCheck.ok) {
            return @{
                ok      = $false
                error   = ('engine_manifest_url_' + $emuCheck.error)
                message = ('"includedPaxScript.engineManifestUrl" rejected: ' + $emuCheck.message)
            }
        }
    } elseif ($ip.Contains('engineManifestUrl') -and -not [string]::IsNullOrWhiteSpace([string]$ip['engineManifestUrl'])) {
        $emuCheck = Test-UpdateManifestUrl -Url ([string]$ip['engineManifestUrl'])
        if (-not $emuCheck.ok) {
            return @{
                ok      = $false
                error   = ('engine_manifest_url_' + $emuCheck.error)
                message = ('"includedPaxScript.engineManifestUrl" rejected: ' + $emuCheck.message)
            }
        }
    }

    # engineManifestTrustAnchorThumbprint -- required when
    # acquisitionPolicy == "external". 40-character hex SHA-1 thumbprint.
    # When manifestSignaturePolicy is "internal-test-bypass", the field
    # MAY be literal JSON null (the ONLY non-40-hex value accepted, and
    # only in that profile). Placeholder strings are still rejected.
    # We do not depend on Trust.psm1 here to keep the validator
    # self-contained; the canonical normalizer is Format-Thumbprint and
    # consumers that need the normalized form should call it directly.
    if ($effectivePolicy -eq 'external') {
        $thumbPresent = $ip.Contains('engineManifestTrustAnchorThumbprint')
        $thumbValue   = if ($thumbPresent) { $ip['engineManifestTrustAnchorThumbprint'] } else { $null }
        $thumbIsNull  = ($null -eq $thumbValue)
        if ($effectiveSignaturePolicy -eq 'internal-test-bypass') {
            if (-not $thumbPresent) {
                return @{ ok = $false; error = 'missing_field'; message = '"includedPaxScript.engineManifestTrustAnchorThumbprint" must be present (value null is permitted) when manifestSignaturePolicy is "internal-test-bypass".' }
            }
            if (-not $thumbIsNull) {
                $tpRaw   = [string]$thumbValue
                if ([string]::IsNullOrWhiteSpace($tpRaw)) {
                    return @{ ok = $false; error = 'invalid_trust_anchor_thumbprint'; message = '"includedPaxScript.engineManifestTrustAnchorThumbprint" must be a 40-character hex SHA-1 thumbprint or literal null.' }
                }
                $tpClean = ($tpRaw -replace '[^0-9A-Fa-f]', '')
                if ($tpClean.Length -ne 40) {
                    return @{
                        ok      = $false
                        error   = 'invalid_trust_anchor_thumbprint'
                        message = '"includedPaxScript.engineManifestTrustAnchorThumbprint" must be a 40-character hex SHA-1 thumbprint or literal null under internal-test-bypass.'
                    }
                }
            }
        } else {
            if (-not $thumbPresent -or $thumbIsNull) {
                return @{ ok = $false; error = 'missing_field'; message = '"includedPaxScript.engineManifestTrustAnchorThumbprint" is required when acquisitionPolicy is "external".' }
            }
            $tpRaw = [string]$thumbValue
            if ([string]::IsNullOrWhiteSpace($tpRaw)) {
                return @{ ok = $false; error = 'missing_field'; message = '"includedPaxScript.engineManifestTrustAnchorThumbprint" is required when acquisitionPolicy is "external".' }
            }
            $tpClean = ($tpRaw -replace '[^0-9A-Fa-f]', '')
            if ($tpClean.Length -ne 40) {
                return @{
                    ok      = $false
                    error   = 'invalid_trust_anchor_thumbprint'
                    message = '"includedPaxScript.engineManifestTrustAnchorThumbprint" must be a 40-character hex SHA-1 thumbprint.'
                }
            }
        }
    } elseif ($ip.Contains('engineManifestTrustAnchorThumbprint') -and
              ($null -ne $ip['engineManifestTrustAnchorThumbprint']) -and
              -not [string]::IsNullOrWhiteSpace([string]$ip['engineManifestTrustAnchorThumbprint'])) {
        $tpRaw   = [string]$ip['engineManifestTrustAnchorThumbprint']
        $tpClean = ($tpRaw -replace '[^0-9A-Fa-f]', '')
        if ($tpClean.Length -ne 40) {
            return @{
                ok      = $false
                error   = 'invalid_trust_anchor_thumbprint'
                message = '"includedPaxScript.engineManifestTrustAnchorThumbprint" must be a 40-character hex SHA-1 thumbprint.'
            }
        }
    }

    # compatibility -- optional, but if present must be the right shape.
    if ($Manifest.Contains('compatibility')) {
        $cp = $Manifest['compatibility']
        if (-not (Test-Hashtable $cp)) {
            return @{ ok = $false; error = 'type_mismatch'; message = '"compatibility" must be a JSON object.' }
        }
        foreach ($k in (Get-HashtableKeys $cp)) {
            if ($Script:AllowedCompatibilityKeys -notcontains $k) {
                return @{ ok = $false; error = 'unknown_field'; message = ('"compatibility" has an unknown field: "' + $k + '".') }
            }
        }
    }

    return @{ ok = $true; warnings = @($warnings); effectiveAcquisitionPolicy = $effectivePolicy; effectiveManifestSignaturePolicy = $effectiveSignaturePolicy }
}

# ---------------------------------------------------------------------
# Version-comparison helper
# ---------------------------------------------------------------------
function Compare-CookbookVersion {
    # Returns -1, 0, or +1 like .NET Comparer<T>.
    # Both inputs are dotted version strings; falls back to ordinal
    # string compare if either side is not parseable.
    param(
        [Parameter(Mandatory)][string]$Left,
        [Parameter(Mandatory)][string]$Right
    )
    $leftVer  = $null
    $rightVer = $null
    if ([System.Version]::TryParse($Left, [ref]$leftVer) -and [System.Version]::TryParse($Right, [ref]$rightVer)) {
        return $leftVer.CompareTo($rightVer)
    }
    return [string]::CompareOrdinal($Left, $Right)
}

Export-ModuleMember -Function `
    Test-UpdateManifestUrl, `
    Get-UpdateManifest, `
    Test-UpdateManifestSchema, `
    Compare-CookbookVersion
