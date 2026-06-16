# =====================================================================
# Engine\Acquisition.psm1
# =====================================================================
#
# State helpers, policy resolution, approved-engine manifest verify,
# and compatible-entry selection for the external-PAX-engine flow.
#
# Stage 2 implements the read-only / verify-only surface:
#   * Install-state JSON path resolution, read, and merge-write.
#   * VERSION.json -> acquisition policy resolution.
#   * Composite acquisition-state synthesis (no PAX byte access).
#   * Approved-engine manifest schema + signature + trust-anchor
#     verification (wraps Update\Signature.psm1 + Update\Trust.psm1
#     + Engine\ManifestSchema.psm1).
#   * Compatible-entry selection honoring status + min/maxCookbookVersion.
#
# Stage 2 leaves three functions as Stage 3+ stubs that still throw
# NotImplemented:
#   * Get-PaxScriptByDownload   (HTTPS fetch + SHA-256 verify)
#   * Get-PaxScriptByLocalFile  (operator-selected file ingest)
#   * Set-PaxScriptActivated    (byte-preserving write to canonical path)
# Those three are the only entry points that touch PAX script bytes;
# they remain stubbed until the Stage 3+ work item lands.
#
# ---------------------------------------------------------------------
# PAX SCRIPT IMMUTABILITY CONTRACT (plan §4.5) -- ABSOLUTE
# ---------------------------------------------------------------------
# When Stage 3+ fills in the remaining bodies below, the following
# rules are inviolate:
#
#   1. The acquired PAX script bytes MUST be hash-verified against an
#      approved-manifest entry BEFORE being written to the canonical
#      managed path.
#
#   2. Once validated, the bytes MUST be copied to the canonical path
#      BYTE-FOR-BYTE UNCHANGED. The on-disk file's SHA-256 after
#      activation MUST equal the SHA-256 computed before activation.
#
#   3. Stage 3+ MUST use byte-preserving file APIs when materializing
#      the script. The only acceptable primitives are:
#         - [System.IO.File]::WriteAllBytes($path, $bytes)
#         - [System.IO.File]::Copy($src, $dst, [bool]$overwrite)
#         - System.IO.FileStream with FileMode.Create / Write
#      The following primitives MUST NOT be used to write PAX script
#      bytes (they re-encode, normalize line endings, or add a BOM):
#         - Set-Content / Add-Content
#         - Out-File
#         - [System.IO.File]::WriteAllText
#         - [System.IO.File]::WriteAllLines
#         - StreamWriter on a file path
#         - Any "ConvertTo-Json | Set-Content" round-trip
#
#   4. VERSION.json.paxScript.sha256 MUST NEVER be adjusted to make a
#      mutated script "match". If a candidate's SHA does not match an
#      approved-manifest entry, acquisition fails and the canonical
#      path is left untouched.
#
#   5. The installer / broker have six and only six allowed operations
#      on PAX script bytes (per plan §4.5): Acquire, Validate,
#      Verify SHA-256 exactly, Copy bytes UNCHANGED, Re-hash,
#      Invoke as standalone. Anything else is forbidden.
#
# The Stage 2 helpers in this module observe rule (5) trivially: none
# of them read or write PAX script bytes. Install-state.json is a
# sidecar metadata document and is NOT in scope of §4.5; text APIs
# (ConvertTo-Json / Set-Content -Encoding utf8) are explicitly
# permitted for that document.
#
# Reviewers of any future PR that touches this file MUST verify that
# none of the above rules are weakened.
# ---------------------------------------------------------------------

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Side-load Update\Signature.psm1 into Acquisition's module session
# state so Test-ApprovedEngineManifest can resolve Test-PackageSignature
# directly. The broker boot path imports Update\Trust.psm1 which itself
# nested-imports Signature.psm1, but nested-module exports do not bubble
# up to sibling modules like this one, so we import explicitly here.
$Script:SignatureModulePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Update\Signature.psm1'
if (Test-Path -LiteralPath $Script:SignatureModulePath -PathType Leaf) {
    try { Import-Module -Force $Script:SignatureModulePath -ErrorAction Stop | Out-Null } catch { }
}

# Side-load Update\Manifest.psm1 so Get-ApprovedEngineManifestPackage
# can call Test-UpdateManifestUrl directly. Same rationale as above:
# nested-module exports from Trust.psm1 do not bubble through Acquisition's
# session state.
$Script:ManifestModulePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Update\Manifest.psm1'
if (Test-Path -LiteralPath $Script:ManifestModulePath -PathType Leaf) {
    try { Import-Module -Force $Script:ManifestModulePath -ErrorAction Stop | Out-Null } catch { }
}

# Approved-engine manifest fetch caps. Mirror the Update\Manifest.psm1
# convention (HTTPS-only, single GET, no retry, no redirect). 64 KB body
# cap matches Update\Manifest.psm1's $Script:MaxManifestBytes. The .sig
# envelope is much smaller; a 16 KB cap is generous.
$Script:ApprovedEngineManifestMaxBytes  = 64 * 1024
$Script:ApprovedEngineSignatureMaxBytes = 16 * 1024
$Script:ApprovedEngineFetchTimeoutSec   = 30

# PAX script byte fetch caps. The PAX script file is larger than the
# manifest envelope -- a few hundred KB today, with room to grow. Cap
# at 4 MB so a runaway redirect or wrong-URL cannot fill the disk.
# Timeout is longer than the manifest timeout because a real GET can
# include TLS handshake plus a slow first-hop on operator networks.
$Script:PaxScriptMaxBytes        = 4 * 1024 * 1024
$Script:PaxScriptFetchTimeoutSec = 60

# Acquisition state tokens surfaced to callers (and ultimately to the
# SPA). Stage 2+ populates / transitions between these.
$Script:AcquisitionStateAcquired           = 'acquired'
$Script:AcquisitionStateAcquisitionPending = 'acquisition_pending'
$Script:AcquisitionStateInFlight           = 'in_flight'
$Script:AcquisitionStateFailed             = 'failed'

# Acquisition source tokens. v1 active enum is closed to
# download | local-file | automation (or null when no acquisition
# attempt has completed). Earlier doctrine sources ('automation-path',
# 'sideload') are not part of the active production surface.
$Script:AcquisitionSourceDownload       = 'download'
$Script:AcquisitionSourceLocalFile      = 'local-file'
$Script:AcquisitionSourceAutomation     = 'automation'

# Manifest signature policy default. Broker may overwrite this at
# startup from VERSION.json.paxScript.manifestSignaturePolicy. The
# value gates one step inside Test-ApprovedEngineManifest: the
# detached signature verification. Schema validation, approved-only
# selection, and triple SHA-256 matching never depend on it.
if (-not (Get-Variable -Scope Script -Name PaxManifestSignaturePolicy -ErrorAction SilentlyContinue)) {
    $Script:PaxManifestSignaturePolicy = 'required'
}

function Format-PaxThumbprint {
    # Normalize a SHA-1 cert thumbprint to bare uppercase hex (no
    # colons, no whitespace). Returns $null when the input is not a
    # plausible 40-hex thumbprint. Private helper -- not exported.
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $clean = ($Value -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($clean.Length -ne 40) { return $null }
    return $clean
}

function ConvertTo-PaxManifestStringDates {
    # ConvertFrom-Json -AsHashtable silently coerces ISO-8601 strings
    # to [datetime], which breaks the schema validator's string-typed
    # field checks. Stringify any [datetime] values back to a stable
    # 'yyyy-MM-ddTHH:mm:ssZ' form so the validator sees what was on
    # the wire. Private helper -- not exported.
    param($Manifest)
    if ($null -eq $Manifest -or -not ($Manifest -is [System.Collections.IDictionary])) { return }
    foreach ($key in @($Manifest.Keys)) {
        $v = $Manifest[$key]
        if ($v -is [datetime]) {
            $Manifest[$key] = ([datetime]$v).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        } elseif ($v -is [datetimeoffset]) {
            $Manifest[$key] = ([datetimeoffset]$v).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
        }
    }
}

# =====================================================================
# State accessors (Stage 2)
# =====================================================================

function Get-PaxAcquisitionInstallStatePath {
    <#
    .SYNOPSIS
        Returns the absolute path to %LOCALAPPDATA%\PAXCookbook\install-state.json.
        This mirrors the installer-side Get-InstallStateFilePath helper so the
        broker and installer share a single install-state document. Honors the
        LOCALAPPDATA environment variable first (which the installer can
        redirect via -InstallRoot in sandboxed test runs), then falls back to
        the canonical LocalApplicationData special folder for production
        Windows installs where the env var may be unset.
    #>
    [CmdletBinding()] param()
    $localAppData = $env:LOCALAPPDATA
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    }
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw 'install_state_path_unavailable: LocalApplicationData is not set in this environment.'
    }
    return (Join-Path (Join-Path $localAppData 'PAXCookbook') 'install-state.json')
}

function Read-PaxAcquisitionInstallState {
    <#
    .SYNOPSIS
        Reads install-state.json and returns the parsed paxAcquisition
        block (or $null when the file, top-level document, or block is absent
        or unparseable). Read-only and side-effect free.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)][string]$StatePath
    )
    if ([string]::IsNullOrWhiteSpace($StatePath)) {
        $StatePath = Get-PaxAcquisitionInstallStatePath
    }
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        return $null
    }
    try {
        $raw = Get-Content -LiteralPath $StatePath -Raw -ErrorAction Stop
    } catch {
        return $null
    }
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    try {
        $doc = $raw | ConvertFrom-Json -AsHashtable -Depth 12 -ErrorAction Stop
    } catch {
        return $null
    }
    if ($null -eq $doc -or -not ($doc -is [System.Collections.IDictionary])) { return $null }
    if (-not $doc.Contains('paxAcquisition')) { return $null }
    $block = $doc['paxAcquisition']
    if ($null -eq $block -or -not ($block -is [System.Collections.IDictionary])) { return $null }
    return $block
}

function Get-PaxAcquisitionState {
    <#
    .SYNOPSIS
        Synthesizes the current PAX-script acquisition state from
        VERSION.json (policy) + install-state.json (paxAcquisition block)
        + canonical-path presence. Pure read-only: this function NEVER
        touches PAX script bytes (no SHA-256 of the script file, no
        content read; only Test-Path presence). PAX script integrity is
        validated separately at broker startup.

        Returns a hashtable:
            @{
                policy           = 'embedded' | 'external'
                state            = 'embedded-legacy' | 'acquired' |
                                   'acquisition_pending' | 'in_flight' |
                                   'failed' | 'not-required'
                isAcquired       = $true | $false
                pending          = $true | $false
                source           = '<token>' | $null
                version          = '<string>' | $null
                sha256           = '<hex>' | $null
                manifestId       = '<string>' | $null
                manifestHash     = '<hex>' | $null
                manifestVersion  = '<string>' | $null
                validatedAtUtc   = '<iso8601>' | $null
                activatedAtUtc   = '<iso8601>' | $null
                lastAttemptError = '<string>' | $null
                canonicalScriptPath        = '<path>' | $null
                canonicalScriptPresent     = $true | $false
                installStatePath           = '<path>'
            }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$VersionFilePath,
        [Parameter(Mandatory = $false)][string]$CanonicalScriptPath,
        [Parameter(Mandatory = $false)][string]$StatePath
    )

    $policy = Resolve-PaxAcquisitionPolicy -VersionFilePath $VersionFilePath

    if ([string]::IsNullOrWhiteSpace($StatePath)) {
        $StatePath = Get-PaxAcquisitionInstallStatePath
    }
    $block = Read-PaxAcquisitionInstallState -StatePath $StatePath

    $canonicalPresent = $false
    if (-not [string]::IsNullOrWhiteSpace($CanonicalScriptPath)) {
        $canonicalPresent = [bool](Test-Path -LiteralPath $CanonicalScriptPath -PathType Leaf)
    }

    $result = [ordered]@{
        policy                 = $policy
        state                  = $null
        isAcquired             = $false
        pending                = $false
        source                 = $null
        version                = $null
        sha256                 = $null
        manifestId             = $null
        manifestHash           = $null
        manifestVersion        = $null
        validatedAtUtc         = $null
        activatedAtUtc         = $null
        lastAttemptError       = $null
        canonicalScriptPath    = $CanonicalScriptPath
        canonicalScriptPresent = $canonicalPresent
        installStatePath       = $StatePath
    }

    if ($null -ne $block) {
        foreach ($k in @('pending','source','version','sha256','manifestId','manifestHash','manifestVersion','validatedAtUtc','activatedAtUtc','lastAttemptError')) {
            if ($block.Contains($k)) { $result[$k] = $block[$k] }
        }
    }

    if ($policy -eq 'embedded') {
        # Legacy / bundled path: PAX script ships inside the cookbook ZIP and
        # is validated at broker startup. Acquisition flow does not apply.
        $result.state      = 'embedded-legacy'
        $result.isAcquired = $canonicalPresent
        $result.pending    = $false
        return $result
    }

    # policy = 'external'
    if ($null -eq $block) {
        $result.state      = $Script:AcquisitionStateAcquisitionPending
        $result.isAcquired = $false
        $result.pending    = $true
        return $result
    }

    $pendingFlag = $false
    if ($block.Contains('pending') -and $null -ne $block['pending']) {
        $pendingFlag = [bool]$block['pending']
    }
    $hasActivatedAt = $block.Contains('activatedAtUtc') -and -not [string]::IsNullOrWhiteSpace([string]$block['activatedAtUtc'])
    $hasLastError   = $block.Contains('lastAttemptError') -and -not [string]::IsNullOrWhiteSpace([string]$block['lastAttemptError'])

    if (-not $pendingFlag -and $hasActivatedAt -and $canonicalPresent) {
        $result.state      = $Script:AcquisitionStateAcquired
        $result.isAcquired = $true
    } elseif ($pendingFlag -and $hasLastError) {
        $result.state      = $Script:AcquisitionStateFailed
        $result.isAcquired = $false
    } elseif ($pendingFlag) {
        $result.state      = $Script:AcquisitionStateAcquisitionPending
        $result.isAcquired = $false
    } else {
        # Block present but inconclusive — treat as pending so the SPA prompts.
        $result.state      = $Script:AcquisitionStateAcquisitionPending
        $result.isAcquired = $canonicalPresent
    }

    return $result
}

# =====================================================================
# Policy and selection (Stage 2)
# =====================================================================

function Resolve-PaxAcquisitionPolicy {
    <#
    .SYNOPSIS
        Reads VERSION.json.paxScript.acquisitionPolicy. Returns
        "external" or "embedded". Missing field => "embedded" (legacy
        / bundled). Invalid value throws structured error.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$VersionFilePath)

    if (-not (Test-Path -LiteralPath $VersionFilePath -PathType Leaf)) {
        throw ('version_file_missing: ' + $VersionFilePath)
    }
    try {
        $raw = Get-Content -LiteralPath $VersionFilePath -Raw -ErrorAction Stop
    } catch {
        throw ('version_file_read_failed: ' + $_.Exception.Message)
    }
    if ([string]::IsNullOrWhiteSpace($raw)) {
        throw 'version_file_empty: VERSION.json is empty.'
    }
    try {
        $doc = $raw | ConvertFrom-Json -AsHashtable -Depth 12 -ErrorAction Stop
    } catch {
        throw ('version_file_parse_failed: ' + $_.Exception.Message)
    }
    if ($null -eq $doc -or -not ($doc -is [System.Collections.IDictionary])) {
        throw 'version_file_invalid: top-level value is not an object.'
    }
    if (-not $doc.Contains('paxScript')) {
        return 'embedded'
    }
    $pax = $doc['paxScript']
    if ($null -eq $pax -or -not ($pax -is [System.Collections.IDictionary])) {
        return 'embedded'
    }
    if (-not $pax.Contains('acquisitionPolicy')) {
        return 'embedded'
    }
    $value = [string]$pax['acquisitionPolicy']
    if ($value -eq 'external' -or $value -eq 'embedded') {
        return $value
    }
    throw ('invalid_acquisition_policy: "' + $value + '" is not one of: external, embedded.')
}

function Test-ApprovedEngineManifest {
    <#
    .SYNOPSIS
        Verifies an approved-engine manifest end-to-end:
            * signature envelope verifies against the body via
              Update\Signature.psm1::Test-PackageSignature
              (RSA-PKCS1v15-SHA256 over the body file's SHA-256),
            * the cert embedded in the envelope chains by thumbprint
              to the caller-supplied pinned trust anchor
              (engineManifestTrustAnchorThumbprint from VERSION.json),
            * the body parses as JSON,
            * the body matches the §7.4 schema
              (Engine\ManifestSchema.psm1::Test-ApprovedEngineManifestSchema).
        Returns @{
            ok                 = $true
            manifest           = <parsed hashtable>
            manifestId         = '<signingKeyId>:<manifestVersion>'
            manifestHash       = '<sha256 hex of body file>'
            manifestVersion    = '<string>'
            channel            = '<string>'
            entryCount         = <int>
            signatureVerified  = $true
            certThumbprint     = '<uppercase 40-hex>'
            certSubject        = '<x500 subject>'
        }
        On failure returns @{ ok = $false; error = '<token>'; message = '<text>'; ... }.

        This function performs no network I/O. It does not download
        the manifest body or signature; callers pass already-fetched
        file paths. PAX script bytes are NEVER touched here.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ManifestBodyPath,
        [Parameter(Mandatory = $false)][string]$ManifestSignaturePath,
        [Parameter(Mandatory = $false)][string]$ExpectedTrustAnchorThumbprint,
        [Parameter(Mandatory = $false)]
        [ValidateSet('required','internal-test-bypass')]
        [string]$SignaturePolicy
    )

    if ([string]::IsNullOrWhiteSpace($SignaturePolicy)) {
        $SignaturePolicy = if ([string]::IsNullOrWhiteSpace($Script:PaxManifestSignaturePolicy)) { 'required' } else { [string]$Script:PaxManifestSignaturePolicy }
    }

    if (-not (Test-Path -LiteralPath $ManifestBodyPath -PathType Leaf)) {
        return @{ ok = $false; error = 'manifest_body_missing'; message = ('Manifest body file does not exist: ' + $ManifestBodyPath) }
    }

    $normalizedAnchor = $null
    if ($SignaturePolicy -ne 'internal-test-bypass') {
        if ([string]::IsNullOrWhiteSpace($ManifestSignaturePath) -or -not (Test-Path -LiteralPath $ManifestSignaturePath -PathType Leaf)) {
            return @{ ok = $false; error = 'manifest_signature_missing'; message = ('Manifest signature envelope does not exist: ' + $ManifestSignaturePath) }
        }
        $normalizedAnchor = Format-PaxThumbprint -Value $ExpectedTrustAnchorThumbprint
        if ([string]::IsNullOrWhiteSpace($normalizedAnchor)) {
            return @{ ok = $false; error = 'invalid_trust_anchor'; message = 'ExpectedTrustAnchorThumbprint must be a 40-character hex SHA-1 thumbprint.' }
        }
    }

    # Parse + schema-check body BEFORE invoking signature crypto so callers
    # see structural errors first. Signature crypto is still gated below.
    try {
        $rawBody = Get-Content -LiteralPath $ManifestBodyPath -Raw -ErrorAction Stop
    } catch {
        return @{ ok = $false; error = 'manifest_body_read_failed'; message = $_.Exception.Message }
    }
    if ([string]::IsNullOrWhiteSpace($rawBody)) {
        return @{ ok = $false; error = 'manifest_body_empty'; message = 'Manifest body file is empty.' }
    }
    try {
        $manifest = $rawBody | ConvertFrom-Json -AsHashtable -Depth 12 -ErrorAction Stop
    } catch {
        return @{ ok = $false; error = 'manifest_body_parse_failed'; message = $_.Exception.Message }
    }
    ConvertTo-PaxManifestStringDates -Manifest $manifest
    $schema = Test-ApprovedEngineManifestSchema -Manifest $manifest
    if (-not $schema.ok) {
        return @{ ok = $false; error = ('manifest_schema_' + $schema.error); message = $schema.message }
    }

    if ($SignaturePolicy -eq 'internal-test-bypass') {
        Write-Verbose '[Acquisition] manifest_signature_verification_skipped (manifestSignaturePolicy=internal-test-bypass)'

        $manifestHash = ((Get-FileHash -LiteralPath $ManifestBodyPath -Algorithm SHA256).Hash).ToUpperInvariant()
        $manifestId   = ([string]$manifest['signingKeyId']) + ':' + ([string]$manifest['manifestVersion'])

        return @{
            ok                = $true
            manifest          = $manifest
            manifestId        = $manifestId
            manifestHash      = $manifestHash
            manifestVersion   = [string]$manifest['manifestVersion']
            channel           = [string]$manifest['channel']
            entryCount        = [int]$schema.entryCount
            signatureVerified = $false
            certThumbprint    = $null
            certSubject       = $null
            signaturePolicy   = 'internal-test-bypass'
        }
    }

    if (-not (Get-Command -Name Test-PackageSignature -ErrorAction SilentlyContinue)) {
        return @{ ok = $false; error = 'signature_module_unavailable'; message = 'Update\Signature.psm1::Test-PackageSignature is not loaded.' }
    }
    $sig = Test-PackageSignature -PackagePath $ManifestBodyPath -EnvelopePath $ManifestSignaturePath
    if ($null -eq $sig -or -not $sig.ok) {
        return @{
            ok                = $false
            error             = ('signature_' + ($(if ($null -ne $sig -and $sig.error) { $sig.error } else { 'unknown' })))
            message           = ($(if ($null -ne $sig -and $sig.errorMessage) { [string]$sig.errorMessage } else { 'Manifest signature verification failed.' }))
            certThumbprint    = ($(if ($null -ne $sig) { $sig.certThumbprint } else { $null }))
            signatureVerified = $false
        }
    }

    $sigThumb = [string]$sig.certThumbprint
    $sigThumbNormalized = Format-PaxThumbprint -Value $sigThumb
    if (-not [string]::IsNullOrWhiteSpace($sigThumbNormalized)) { $sigThumb = $sigThumbNormalized }
    if ($sigThumb -ne $normalizedAnchor) {
        return @{
            ok                 = $false
            error              = 'trust_anchor_mismatch'
            message            = ('Manifest signer thumbprint ' + $sigThumb + ' does not match pinned trust anchor ' + $normalizedAnchor + '.')
            certThumbprint     = $sigThumb
            expectedThumbprint = $normalizedAnchor
            signatureVerified  = $true
        }
    }

    $manifestHash = ((Get-FileHash -LiteralPath $ManifestBodyPath -Algorithm SHA256).Hash).ToUpperInvariant()
    $manifestId   = ([string]$manifest['signingKeyId']) + ':' + ([string]$manifest['manifestVersion'])

    return @{
        ok                = $true
        manifest          = $manifest
        manifestId        = $manifestId
        manifestHash      = $manifestHash
        manifestVersion   = [string]$manifest['manifestVersion']
        channel           = [string]$manifest['channel']
        entryCount        = [int]$schema.entryCount
        signatureVerified = $true
        certThumbprint    = $sigThumb
        certSubject       = [string]$sig.certSubject
        signaturePolicy   = 'required'
    }
}

function Select-CompatibleEngineEntry {
    <#
    .SYNOPSIS
        Given a verified manifest hashtable and the running cookbook
        version, selects the script entry the operator should be
        offered.

        APPROVED-ONLY SELECTION CONTRACT (v1, plan §6.2):
            * Only entries with status == 'approved' are selectable.
            * Entries with status == 'deprecated' remain VALID in the
              manifest schema (historical entries may exist on the
              wire) but are NEVER selected by this helper. If only
              deprecated entries are otherwise compatible, the result
              is no_compatible_engine.
            * Entries with status == 'withdrawn' are never selected.
            * The optional -TargetVersion / -TargetSha256 filters do
              NOT widen the approved-only contract: a target match
              against a deprecated or withdrawn entry still returns
              no_compatible_engine.

        Any future change that allows deprecated entries to be
        selected MUST be expressed as an explicit caller opt-in (e.g.
        an -AllowDeprecated switch) -- not as an silent fallback when
        approved is unavailable. The schema's continued acceptance of
        'deprecated' as a status MUST NOT be read as permission to
        select such entries here.

        Optional -TargetVersion / -TargetSha256 filter to a specific
        approved entry (e.g. when re-acquiring a previously-validated
        script).

        Returns @{
            ok       = $true
            entry    = <selected entry hashtable>
            warnings = @()                # reserved; always empty for v1
        }
        or @{
            ok                  = $false
            error               = '<token>'
            message             = '<text>'
            candidatesEvaluated = <int>
        }.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Manifest,
        [Parameter(Mandatory = $true)][string]$CookbookVersion,
        [Parameter(Mandatory = $false)][string]$TargetVersion,
        [Parameter(Mandatory = $false)][string]$TargetSha256
    )

    if ($null -eq $Manifest -or -not ($Manifest -is [System.Collections.IDictionary])) {
        return @{ ok = $false; error = 'invalid_manifest'; message = 'Manifest argument is not a hashtable.'; candidatesEvaluated = 0 }
    }
    if (-not $Manifest.Contains('scripts')) {
        return @{ ok = $false; error = 'missing_scripts'; message = 'Manifest has no "scripts" array.'; candidatesEvaluated = 0 }
    }
    $scripts = $Manifest['scripts']
    if ($null -eq $scripts -or -not ($scripts -is [System.Collections.IEnumerable]) -or $scripts -is [string]) {
        return @{ ok = $false; error = 'invalid_scripts'; message = '"scripts" is not an array.'; candidatesEvaluated = 0 }
    }

    $cookbookVer = $null
    try { $cookbookVer = [version]$CookbookVersion } catch {
        return @{ ok = $false; error = 'invalid_cookbook_version'; message = ('CookbookVersion "' + $CookbookVersion + '" is not a parseable version.'); candidatesEvaluated = 0 }
    }

    $targetSha = $null
    if (-not [string]::IsNullOrWhiteSpace($TargetSha256)) {
        $targetSha = $TargetSha256.ToUpperInvariant()
    }

    $candidates = 0
    $best       = $null
    $bestVer    = $null

    foreach ($entry in $scripts) {
        $candidates++
        if ($null -eq $entry -or -not ($entry -is [System.Collections.IDictionary])) { continue }
        $status = [string]$entry['status']
        # Approved-only selection. Deprecated remains schema-valid but is
        # ineligible for selection in v1; withdrawn is never eligible.
        if ($status -ne 'approved') { continue }

        $entryVerStr = [string]$entry['version']
        if (-not [string]::IsNullOrWhiteSpace($TargetVersion) -and $entryVerStr -ne $TargetVersion) { continue }
        $entrySha = ([string]$entry['sha256']).ToUpperInvariant()
        if ($null -ne $targetSha -and $entrySha -ne $targetSha) { continue }

        $minStr = [string]$entry['minCookbookVersion']
        $maxStr = [string]$entry['maxCookbookVersion']
        $minVer = $null; $maxVer = $null
        try { $minVer = [version]$minStr } catch { continue }
        try { $maxVer = [version]$maxStr } catch { continue }
        if ($cookbookVer -lt $minVer) { continue }
        if ($cookbookVer -gt $maxVer) { continue }

        $entryVer = $null
        try { $entryVer = [version]$entryVerStr } catch { continue }

        # Highest version wins among approved candidates.
        if ($null -eq $best -or $entryVer -gt $bestVer) {
            $best    = $entry
            $bestVer = $entryVer
        }
    }

    if ($null -eq $best) {
        return @{
            ok                  = $false
            error               = 'no_compatible_engine'
            message             = ('No approved script entry is compatible with cookbook version ' + $CookbookVersion + '.')
            candidatesEvaluated = $candidates
        }
    }

    return @{
        ok       = $true
        entry    = $best
        warnings = @()
    }
}

function Get-ApprovedEngineManifestPackage {
    <#
    .SYNOPSIS
        Fetch an approved-engine manifest body and its detached .sig
        envelope from HTTPS (or loopback HTTP for the smoke harness) and
        write both to a caller-provided work directory. The two returned
        file paths are suitable for handing directly to
        Test-ApprovedEngineManifest.

    .DESCRIPTION
        STAGE 2A scope: manifest layer only. This helper:
          * Validates both URLs through Test-UpdateManifestUrl (HTTPS
            required; loopback HTTP allowed for the test harness).
          * Issues a single GET each (manifest + .sig). No retry, no
            redirect (MaximumRedirection = 0), no credentials, no
            cookies.
          * Caps responses (64 KB manifest, 16 KB signature).
          * Uses a 30-second TimeoutSec to match Update\Manifest.psm1.
          * Writes both responses to the work directory using
            [System.IO.File]::WriteAllBytes so the on-disk bytes
            exactly match the wire bytes (Test-PackageSignature
            recomputes SHA-256 over the body file, so byte
            preservation here is required).

        The signature URL defaults to '<ManifestUrl>.sig' (sibling
        convention) when -SignatureUrl is omitted; callers may pass an
        explicit URL if the distribution publishes the envelope at a
        different absolute path.

        SCOPE LIMIT (plan §4.5): this function NEVER fetches PAX
        script bytes. Only the manifest JSON and its detached .sig
        envelope are touched.

    .OUTPUTS
        On success:
            @{
                ok                    = $true
                manifestBodyPath      = <abs path>
                manifestSignaturePath = <abs path>
                manifestUrl           = <abs url>
                signatureUrl          = <abs url>
                manifestBytes         = <int>
                signatureBytes        = <int>
                workDirectory         = <abs path>
            }
        On failure:
            @{ ok = $false; error = '<token>'; message = '<text>'; ... }
        Error tokens (closed set):
            invalid_manifest_url, invalid_signature_url,
            work_directory_create_failed,
            manifest_fetch_failed, signature_fetch_failed,
            manifest_too_large,   signature_too_large,
            manifest_write_failed, signature_write_failed
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ManifestUrl,
        [Parameter(Mandatory = $true)][string]$WorkDirectory,
        [Parameter(Mandatory = $false)][string]$SignatureUrl,
        [Parameter(Mandatory = $false)][string]$CookbookVersion
    )

    # --- validate manifest URL (HTTPS or loopback HTTP) ---
    $urlCheck = Test-UpdateManifestUrl -Url $ManifestUrl
    if (-not $urlCheck.ok) {
        return @{
            ok                 = $false
            error              = 'invalid_manifest_url'
            message            = ('ManifestUrl "' + $ManifestUrl + '" failed validation: ' + [string]$urlCheck.error)
            urlValidationError = [string]$urlCheck.error
        }
    }

    # --- compute / validate signature URL (defaults to '<manifest>.sig') ---
    if ([string]::IsNullOrWhiteSpace($SignatureUrl)) {
        $SignatureUrl = $ManifestUrl + '.sig'
    }
    $sigCheck = Test-UpdateManifestUrl -Url $SignatureUrl
    if (-not $sigCheck.ok) {
        return @{
            ok                 = $false
            error              = 'invalid_signature_url'
            message            = ('SignatureUrl "' + $SignatureUrl + '" failed validation: ' + [string]$sigCheck.error)
            urlValidationError = [string]$sigCheck.error
        }
    }

    # --- ensure work directory exists ---
    try {
        if (-not (Test-Path -LiteralPath $WorkDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
        }
    } catch {
        return @{
            ok      = $false
            error   = 'work_directory_create_failed'
            message = ('Failed to create work directory "' + $WorkDirectory + '": ' + $_.Exception.Message)
        }
    }

    $cbVer = if ([string]::IsNullOrWhiteSpace($CookbookVersion)) { 'unknown' } else { $CookbookVersion }
    $ua    = 'PAXCookbook/' + $cbVer + ' (engine-manifest-check)'
    $hdrs  = @{ Accept = 'application/json' }

    $bodyPath = Join-Path $WorkDirectory 'engine-manifest.json'
    $sigPath  = Join-Path $WorkDirectory 'engine-manifest.json.sig'

    # --- fetch manifest body ---
    $resp = $null
    try {
        $resp = Invoke-WebRequest `
            -Uri                $ManifestUrl `
            -UseBasicParsing `
            -MaximumRedirection 0 `
            -TimeoutSec         $Script:ApprovedEngineFetchTimeoutSec `
            -Method             GET `
            -Headers            $hdrs `
            -UserAgent          $ua `
            -ErrorAction        Stop
    } catch {
        return @{
            ok      = $false
            error   = 'manifest_fetch_failed'
            message = ('GET ' + $ManifestUrl + ' failed: ' + $_.Exception.Message)
        }
    }

    $bodyBytes = Get-ApprovedEngineFetchResponseBytes -Response $resp
    if ($null -eq $bodyBytes) {
        return @{ ok = $false; error = 'manifest_fetch_failed'; message = ('GET ' + $ManifestUrl + ' returned no body bytes.') }
    }
    if ($bodyBytes.Length -gt $Script:ApprovedEngineManifestMaxBytes) {
        return @{
            ok        = $false
            error     = 'manifest_too_large'
            message   = ('Manifest body ' + $bodyBytes.Length + ' bytes exceeds cap of ' + $Script:ApprovedEngineManifestMaxBytes + ' bytes.')
            byteCount = $bodyBytes.Length
            byteCap   = $Script:ApprovedEngineManifestMaxBytes
        }
    }
    try {
        [System.IO.File]::WriteAllBytes($bodyPath, $bodyBytes)
    } catch {
        return @{
            ok      = $false
            error   = 'manifest_write_failed'
            message = ('Failed to persist manifest body to "' + $bodyPath + '": ' + $_.Exception.Message)
        }
    }

    # --- fetch detached signature envelope ---
    $sresp = $null
    try {
        $sresp = Invoke-WebRequest `
            -Uri                $SignatureUrl `
            -UseBasicParsing `
            -MaximumRedirection 0 `
            -TimeoutSec         $Script:ApprovedEngineFetchTimeoutSec `
            -Method             GET `
            -Headers            $hdrs `
            -UserAgent          $ua `
            -ErrorAction        Stop
    } catch {
        return @{
            ok      = $false
            error   = 'signature_fetch_failed'
            message = ('GET ' + $SignatureUrl + ' failed: ' + $_.Exception.Message)
        }
    }

    $sigBytes = Get-ApprovedEngineFetchResponseBytes -Response $sresp
    if ($null -eq $sigBytes) {
        return @{ ok = $false; error = 'signature_fetch_failed'; message = ('GET ' + $SignatureUrl + ' returned no body bytes.') }
    }
    if ($sigBytes.Length -gt $Script:ApprovedEngineSignatureMaxBytes) {
        return @{
            ok        = $false
            error     = 'signature_too_large'
            message   = ('Signature body ' + $sigBytes.Length + ' bytes exceeds cap of ' + $Script:ApprovedEngineSignatureMaxBytes + ' bytes.')
            byteCount = $sigBytes.Length
            byteCap   = $Script:ApprovedEngineSignatureMaxBytes
        }
    }
    try {
        [System.IO.File]::WriteAllBytes($sigPath, $sigBytes)
    } catch {
        return @{
            ok      = $false
            error   = 'signature_write_failed'
            message = ('Failed to persist signature envelope to "' + $sigPath + '": ' + $_.Exception.Message)
        }
    }

    return @{
        ok                    = $true
        manifestBodyPath      = $bodyPath
        manifestSignaturePath = $sigPath
        manifestUrl           = $ManifestUrl
        signatureUrl          = $SignatureUrl
        manifestBytes         = $bodyBytes.Length
        signatureBytes        = $sigBytes.Length
        workDirectory         = $WorkDirectory
    }
}

function Get-ApprovedEngineFetchResponseBytes {
    # Private helper. Extract the wire bytes from an Invoke-WebRequest
    # response, preferring RawContentStream so a UTF-8 BOM (or other
    # encoding quirk in the decoded .Content string) cannot perturb
    # the SHA-256 that Test-PackageSignature later recomputes over the
    # on-disk body. Returns $null if no bytes can be recovered.
    param($Response)
    if ($null -eq $Response) { return $null }
    try {
        $raw = $Response.RawContentStream
        if ($null -ne $raw) {
            try { $raw.Position = 0 } catch { }
            $ms = New-Object System.IO.MemoryStream
            try {
                $raw.CopyTo($ms)
                return $ms.ToArray()
            } finally { $ms.Dispose() }
        }
    } catch { }
    try {
        $c = $Response.Content
        if ($c -is [byte[]]) { return [byte[]]$c }
        if ($c -is [string]) { return [System.Text.Encoding]::UTF8.GetBytes([string]$c) }
    } catch { }
    return $null
}

# =====================================================================
# Acquire (Stage 2)
# =====================================================================
#
# All acquire-* functions return validated bytes IN MEMORY plus the
# entry that matched. They DO NOT write to the canonical path. The
# Activate step (below) writes byte-for-byte UNCHANGED.

function Get-PaxScriptByDownload {
    <#
    .SYNOPSIS
        Downloads the entry.downloadUrl, verifies SHA-256 matches the
        approved-manifest entry (and the optional VERSION.json pin),
        writes the EXACT wire bytes UNCHANGED to a staging file under
        the work directory, re-hashes the staged file, and returns
        the staged path. DOES NOT write to the canonical script path.
    .NOTES
        Plan section 4.5 immutability: bytes are persisted ONLY via
        [System.IO.File]::WriteAllBytes. No text APIs. No encoding
        normalization. SHA-256 is computed over the exact wire bytes
        BEFORE persistence and re-computed over the on-disk staged
        file AFTER persistence; both must equal entry.sha256 verbatim.
        Single GET, no retry, no redirect, no cookies, no credentials.
    .OUTPUTS
        Success: @{ ok=$true; stagedPath; sha256; version; source='download';
                    entry; bytes; downloadUrl; workDirectory }
        Failure: @{ ok=$false; error=<token>; message=<text>; ... }
        Error tokens (closed set):
            invalid_entry, invalid_download_url,
            work_directory_create_failed,
            script_fetch_failed, script_too_large, script_write_failed,
            script_hash_mismatch, version_hash_mismatch,
            post_write_hash_mismatch
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Entry,
        [Parameter(Mandatory = $true)][string]$WorkDirectory,
        [Parameter(Mandatory = $false)][string]$ExpectedVersionSha256,
        [Parameter(Mandatory = $false)][string]$CookbookVersion
    )

    if ($null -eq $Entry -or -not ($Entry -is [System.Collections.IDictionary])) {
        return @{ ok = $false; error = 'invalid_entry'; message = 'Entry argument is not a hashtable.' }
    }
    $entryUrl = [string]$Entry['downloadUrl']
    $entrySha = ([string]$Entry['sha256']).ToUpperInvariant()
    $entryVer = [string]$Entry['version']
    if ([string]::IsNullOrWhiteSpace($entryUrl) -or
        [string]::IsNullOrWhiteSpace($entrySha) -or
        [string]::IsNullOrWhiteSpace($entryVer)) {
        return @{ ok = $false; error = 'invalid_entry'; message = 'Entry must carry non-empty downloadUrl, sha256, and version.' }
    }
    if ($entrySha -notmatch '^[0-9A-F]{64}$') {
        return @{ ok = $false; error = 'invalid_entry'; message = 'Entry.sha256 is not a 64-character hex string.' }
    }

    # Cross-check the manifest entry against the VERSION.json pin
    # (if supplied) BEFORE any network I/O. A mismatch here means the
    # caller's VERSION.json pin and the approved-manifest entry
    # disagree -- refuse rather than paper over by downloading.
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersionSha256)) {
        $verSha = $ExpectedVersionSha256.ToUpperInvariant()
        if ($verSha -ne $entrySha) {
            return @{
                ok             = $false
                error          = 'version_hash_mismatch'
                message        = ('VERSION.json paxScript.sha256 ' + $verSha + ' does not match approved entry sha256 ' + $entrySha + '.')
                entrySha256    = $entrySha
                expectedSha256 = $verSha
            }
        }
    }

    # Validate the download URL through the same HTTPS-only check the
    # manifest fetcher uses (HTTPS, or loopback HTTP for tests).
    $urlCheck = Test-UpdateManifestUrl -Url $entryUrl
    if (-not $urlCheck.ok) {
        return @{
            ok                 = $false
            error              = 'invalid_download_url'
            message            = ('Entry.downloadUrl "' + $entryUrl + '" failed validation: ' + [string]$urlCheck.error)
            urlValidationError = [string]$urlCheck.error
        }
    }

    try {
        if (-not (Test-Path -LiteralPath $WorkDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
        }
    } catch {
        return @{
            ok      = $false
            error   = 'work_directory_create_failed'
            message = ('Failed to create work directory "' + $WorkDirectory + '": ' + $_.Exception.Message)
        }
    }

    $cbVer = if ([string]::IsNullOrWhiteSpace($CookbookVersion)) { 'unknown' } else { $CookbookVersion }
    $ua    = 'PAXCookbook/' + $cbVer + ' (pax-script-download)'
    $hdrs  = @{ Accept = 'text/plain, application/octet-stream;q=0.8, */*;q=0.5' }

    $resp = $null
    try {
        $resp = Invoke-WebRequest `
            -Uri                $entryUrl `
            -UseBasicParsing `
            -MaximumRedirection 0 `
            -TimeoutSec         $Script:PaxScriptFetchTimeoutSec `
            -Method             GET `
            -Headers            $hdrs `
            -UserAgent          $ua `
            -ErrorAction        Stop
    } catch {
        return @{
            ok      = $false
            error   = 'script_fetch_failed'
            message = ('GET ' + $entryUrl + ' failed: ' + $_.Exception.Message)
        }
    }

    $bytes = Get-ApprovedEngineFetchResponseBytes -Response $resp
    if ($null -eq $bytes) {
        return @{ ok = $false; error = 'script_fetch_failed'; message = ('GET ' + $entryUrl + ' returned no body bytes.') }
    }
    if ($bytes.Length -le 0) {
        return @{ ok = $false; error = 'script_fetch_failed'; message = ('GET ' + $entryUrl + ' returned a zero-length body.') }
    }
    if ($bytes.Length -gt $Script:PaxScriptMaxBytes) {
        return @{
            ok        = $false
            error     = 'script_too_large'
            message   = ('Downloaded PAX script body ' + $bytes.Length + ' bytes exceeds cap of ' + $Script:PaxScriptMaxBytes + ' bytes.')
            byteCount = $bytes.Length
            byteCap   = $Script:PaxScriptMaxBytes
        }
    }

    # SHA-256 over the exact wire bytes BEFORE any persistence.
    $sha256 = $null
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha.ComputeHash($bytes)
            $sb = New-Object System.Text.StringBuilder
            foreach ($b in $hashBytes) { [void]$sb.Append($b.ToString('X2')) }
            $sha256 = $sb.ToString()
        } finally { $sha.Dispose() }
    } catch {
        return @{ ok = $false; error = 'script_fetch_failed'; message = ('SHA-256 over downloaded bytes failed: ' + $_.Exception.Message) }
    }
    if ($sha256 -ne $entrySha) {
        return @{
            ok             = $false
            error          = 'script_hash_mismatch'
            message        = ('Downloaded bytes SHA-256 ' + $sha256 + ' does not match approved entry ' + $entrySha + '.')
            actualSha256   = $sha256
            expectedSha256 = $entrySha
        }
    }

    # Persist bytes UNCHANGED to a per-call staging file. NEVER write
    # to the canonical path here; activation is a separate step.
    $stagedName = 'staged-download-' + ([Guid]::NewGuid().ToString('N')) + '.ps1'
    $stagedPath = Join-Path $WorkDirectory $stagedName
    try {
        [System.IO.File]::WriteAllBytes($stagedPath, $bytes)
    } catch {
        return @{
            ok      = $false
            error   = 'script_write_failed'
            message = ('Failed to write staged file "' + $stagedPath + '": ' + $_.Exception.Message)
        }
    }

    $reHash = $null
    try {
        $reHash = ((Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash).ToUpperInvariant()
    } catch {
        try { Remove-Item -LiteralPath $stagedPath -Force -ErrorAction SilentlyContinue } catch { }
        return @{
            ok      = $false
            error   = 'post_write_hash_mismatch'
            message = ('Failed to re-hash staged file "' + $stagedPath + '": ' + $_.Exception.Message)
        }
    }
    if ($reHash -ne $entrySha) {
        try { Remove-Item -LiteralPath $stagedPath -Force -ErrorAction SilentlyContinue } catch { }
        return @{
            ok           = $false
            error        = 'post_write_hash_mismatch'
            message      = ('Staged file SHA-256 ' + $reHash + ' does not match pre-write hash ' + $entrySha + '.')
            postWriteSha = $reHash
            preWriteSha  = $entrySha
        }
    }

    return @{
        ok            = $true
        stagedPath    = $stagedPath
        sha256        = $entrySha
        version       = $entryVer
        source        = $Script:AcquisitionSourceDownload
        entry         = $Entry
        bytes         = $bytes
        downloadUrl   = $entryUrl
        workDirectory = $WorkDirectory
    }
}

function Get-PaxScriptByLocalFile {
    <#
    .SYNOPSIS
        Reads bytes from an operator-supplied local .ps1 path WITHOUT
        modifying that file, verifies SHA-256 matches exactly one
        approved manifest entry compatible with the running cookbook,
        copies the bytes UNCHANGED to a staging file under the work
        directory, and returns the staged path. The original file is
        NEVER mutated, deleted, or moved by this helper.
    .NOTES
        Plan section 4.5 immutability: original-file reads use
        [System.IO.File]::ReadAllBytes; the staging write uses
        [System.IO.File]::WriteAllBytes. No text APIs. No encoding
        normalization.
    .OUTPUTS
        Success: @{ ok=$true; stagedPath; sha256; version; source='local-file';
                    entry; bytes; originalPath; originalSha256; workDirectory }
        Failure: @{ ok=$false; error=<token>; message=<text>; ... }
        Error tokens (closed set):
            file_missing, invalid_extension, read_failed,
            invalid_manifest, hash_not_approved, no_compatible_engine,
            work_directory_create_failed,
            staging_write_failed, post_write_hash_mismatch
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$LocalFilePath,
        [Parameter(Mandatory = $true)] $Manifest,
        [Parameter(Mandatory = $true)][string]$WorkDirectory,
        [Parameter(Mandatory = $true)][string]$CookbookVersion,
        [Parameter(Mandatory = $false)][string]$TargetVersion,
        [Parameter(Mandatory = $false)][string]$ExpectedVersionSha256
    )

    if ([string]::IsNullOrWhiteSpace($LocalFilePath)) {
        return @{ ok = $false; error = 'file_missing'; message = 'LocalFilePath is empty.' }
    }
    if (-not (Test-Path -LiteralPath $LocalFilePath -PathType Leaf)) {
        return @{ ok = $false; error = 'file_missing'; message = ('Local file does not exist: ' + $LocalFilePath) }
    }
    if (-not ($LocalFilePath.ToLowerInvariant().EndsWith('.ps1'))) {
        return @{ ok = $false; error = 'invalid_extension'; message = ('Local file extension is not .ps1: ' + $LocalFilePath) }
    }
    if ($null -eq $Manifest -or -not ($Manifest -is [System.Collections.IDictionary])) {
        return @{ ok = $false; error = 'invalid_manifest'; message = 'Manifest argument is not a hashtable.' }
    }
    if ([string]::IsNullOrWhiteSpace($CookbookVersion)) {
        return @{ ok = $false; error = 'invalid_manifest'; message = 'CookbookVersion is required.' }
    }

    # Read bytes UNCHANGED from the operator's original file.
    $bytes = $null
    try {
        $bytes = [System.IO.File]::ReadAllBytes($LocalFilePath)
    } catch {
        return @{ ok = $false; error = 'read_failed'; message = ('Failed to read local file "' + $LocalFilePath + '": ' + $_.Exception.Message) }
    }
    if ($null -eq $bytes -or $bytes.Length -le 0) {
        return @{ ok = $false; error = 'read_failed'; message = ('Local file is empty: ' + $LocalFilePath) }
    }
    if ($bytes.Length -gt $Script:PaxScriptMaxBytes) {
        return @{
            ok        = $false
            error     = 'read_failed'
            message   = ('Local file ' + $bytes.Length + ' bytes exceeds cap of ' + $Script:PaxScriptMaxBytes + ' bytes.')
            byteCount = $bytes.Length
            byteCap   = $Script:PaxScriptMaxBytes
        }
    }

    $sha256 = $null
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha.ComputeHash($bytes)
            $sb = New-Object System.Text.StringBuilder
            foreach ($b in $hashBytes) { [void]$sb.Append($b.ToString('X2')) }
            $sha256 = $sb.ToString()
        } finally { $sha.Dispose() }
    } catch {
        return @{ ok = $false; error = 'read_failed'; message = ('SHA-256 over local bytes failed: ' + $_.Exception.Message) }
    }

    # Cross-check against the VERSION.json pin first (if supplied).
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersionSha256)) {
        $verSha = $ExpectedVersionSha256.ToUpperInvariant()
        if ($verSha -ne $sha256) {
            return @{
                ok             = $false
                error          = 'hash_not_approved'
                message        = ('Local file SHA-256 ' + $sha256 + ' does not match VERSION.json paxScript.sha256 ' + $verSha + '.')
                localSha256    = $sha256
                expectedSha256 = $verSha
            }
        }
    }

    # Select the approved entry whose sha256 matches the local file.
    # The approved-only contract in Select-CompatibleEngineEntry
    # filters deprecated / withdrawn entries before any TargetSha256
    # match, so a deprecated entry whose sha equals the local file
    # still returns no_compatible_engine.
    $selectArgs = @{
        Manifest        = $Manifest
        CookbookVersion = $CookbookVersion
        TargetSha256    = $sha256
    }
    if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) {
        $selectArgs['TargetVersion'] = $TargetVersion
    }
    $sel = Select-CompatibleEngineEntry @selectArgs
    if ($null -eq $sel -or -not $sel.ok) {
        $reason  = if ($null -ne $sel -and $sel.error)   { [string]$sel.error }   else { 'no_compatible_engine' }
        $msgText = if ($null -ne $sel -and $sel.message) { [string]$sel.message } else { 'Select-CompatibleEngineEntry rejected the local file hash.' }
        if ($reason -eq 'no_compatible_engine') {
            return @{
                ok          = $false
                error       = 'hash_not_approved'
                message     = ('Local file SHA-256 ' + $sha256 + ' does not match any approved entry compatible with cookbook ' + $CookbookVersion + '.')
                localSha256 = $sha256
                selectError = $reason
            }
        }
        return @{
            ok          = $false
            error       = $reason
            message     = $msgText
            localSha256 = $sha256
        }
    }

    $entry = $sel.entry

    try {
        if (-not (Test-Path -LiteralPath $WorkDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
        }
    } catch {
        return @{
            ok      = $false
            error   = 'work_directory_create_failed'
            message = ('Failed to create work directory "' + $WorkDirectory + '": ' + $_.Exception.Message)
        }
    }

    # Stage a per-call copy. NEVER mutate the operator's original.
    $stagedName = 'staged-localfile-' + ([Guid]::NewGuid().ToString('N')) + '.ps1'
    $stagedPath = Join-Path $WorkDirectory $stagedName
    try {
        [System.IO.File]::WriteAllBytes($stagedPath, $bytes)
    } catch {
        return @{
            ok      = $false
            error   = 'staging_write_failed'
            message = ('Failed to write staged file "' + $stagedPath + '": ' + $_.Exception.Message)
        }
    }

    $reHash = $null
    try {
        $reHash = ((Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash).ToUpperInvariant()
    } catch {
        try { Remove-Item -LiteralPath $stagedPath -Force -ErrorAction SilentlyContinue } catch { }
        return @{
            ok      = $false
            error   = 'post_write_hash_mismatch'
            message = ('Failed to re-hash staged file "' + $stagedPath + '": ' + $_.Exception.Message)
        }
    }
    if ($reHash -ne $sha256) {
        try { Remove-Item -LiteralPath $stagedPath -Force -ErrorAction SilentlyContinue } catch { }
        return @{
            ok           = $false
            error        = 'post_write_hash_mismatch'
            message      = ('Staged file SHA-256 ' + $reHash + ' does not match pre-write hash ' + $sha256 + '.')
            postWriteSha = $reHash
            preWriteSha  = $sha256
        }
    }

    return @{
        ok             = $true
        stagedPath     = $stagedPath
        sha256         = $sha256
        version        = [string]$entry['version']
        source         = $Script:AcquisitionSourceLocalFile
        entry          = $entry
        bytes          = $bytes
        originalPath   = $LocalFilePath
        originalSha256 = $sha256
        workDirectory  = $WorkDirectory
    }
}

function Get-PaxScriptByUploadBytes {
    <#
    .SYNOPSIS
        Validates a PAX script byte array supplied directly by the
        broker request body (SPA browser-picker upload), verifies
        SHA-256 against exactly one approved manifest entry compatible
        with the running cookbook, and stages the bytes UNCHANGED to
        a per-call staging file under the work directory. The bytes
        argument is treated as already-detached from any operator-side
        file -- this helper does NOT read from or write to the
        operator's local disk.
    .NOTES
        Plan section 4.5 immutability: this helper uses ONLY
        [System.IO.File]::WriteAllBytes for the staging write. No
        text APIs. No encoding normalization. No line-ending
        rewriting. The byte array crossed the loopback HTTP boundary
        from the browser already, and is never decoded as text
        anywhere in the broker.
    .OUTPUTS
        Success: @{ ok=$true; stagedPath; sha256; version; source='local-file';
                    entry; bytes; originalPath=$null; originalSha256;
                    workDirectory; clientFilenameHint; clientReportedSha256 }
        Failure: @{ ok=$false; error=<token>; message=<text>; ... }
        Error tokens (closed set):
            empty_body, payload_too_large,
            invalid_manifest, hash_not_approved, no_compatible_engine,
            work_directory_create_failed,
            staging_write_failed, post_write_hash_mismatch,
            client_sha_mismatch
    .DESCRIPTION
        The ClientReportedSha256 parameter is ADVISORY ONLY. The
        helper computes its own SHA over the actual bytes and uses
        the manifest entry's sha256 as the sole authority. The
        client-reported hash is compared as a fast-fail UX
        improvement so a wire-corrupted upload fails before the
        full manifest fetch; it can never relax server validation.
        ClientReportedFilename is preserved into the success and
        failure result hashtables for telemetry only and MUST NOT
        be used for path construction by any caller.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [byte[]]$Bytes,
        [Parameter(Mandatory = $true)] $Manifest,
        [Parameter(Mandatory = $true)][string]$WorkDirectory,
        [Parameter(Mandatory = $true)][string]$CookbookVersion,
        [Parameter(Mandatory = $false)][string]$TargetVersion,
        [Parameter(Mandatory = $false)][string]$ExpectedVersionSha256,
        [Parameter(Mandatory = $false)][string]$ClientReportedSha256,
        [Parameter(Mandatory = $false)][string]$ClientReportedFilename
    )

    if ($null -eq $Bytes -or $Bytes.Length -le 0) {
        return @{ ok = $false; error = 'empty_body'; message = 'Upload body is empty.' }
    }
    if ($Bytes.Length -gt $Script:PaxScriptMaxBytes) {
        return @{
            ok        = $false
            error     = 'payload_too_large'
            message   = ('Uploaded byte body ' + $Bytes.Length + ' bytes exceeds cap of ' + $Script:PaxScriptMaxBytes + ' bytes.')
            byteCount = $Bytes.Length
            byteCap   = $Script:PaxScriptMaxBytes
        }
    }
    if ($null -eq $Manifest -or -not ($Manifest -is [System.Collections.IDictionary])) {
        return @{ ok = $false; error = 'invalid_manifest'; message = 'Manifest argument is not a hashtable.' }
    }
    if ([string]::IsNullOrWhiteSpace($CookbookVersion)) {
        return @{ ok = $false; error = 'invalid_manifest'; message = 'CookbookVersion is required.' }
    }

    $sha256 = $null
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha.ComputeHash($Bytes)
            $sb = New-Object System.Text.StringBuilder
            foreach ($b in $hashBytes) { [void]$sb.Append($b.ToString('X2')) }
            $sha256 = $sb.ToString()
        } finally { $sha.Dispose() }
    } catch {
        return @{ ok = $false; error = 'staging_write_failed'; message = ('SHA-256 over upload bytes failed: ' + $_.Exception.Message) }
    }

    # Advisory fast-fail: if the SPA computed its own SHA before
    # POSTing and that value disagrees with what arrived on the wire,
    # the bytes were corrupted in transit and there is no point
    # spending a manifest fetch on them. This is NEVER the trust
    # boundary -- the manifest sha256 below is.
    if (-not [string]::IsNullOrWhiteSpace($ClientReportedSha256)) {
        $clientShaNorm = (($ClientReportedSha256 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant())
        if ($clientShaNorm.Length -eq 64 -and $clientShaNorm -ne $sha256) {
            return @{
                ok                  = $false
                error               = 'client_sha_mismatch'
                message             = ('Client-reported SHA-256 ' + $clientShaNorm + ' does not match server-computed SHA-256 ' + $sha256 + ' over the uploaded bytes.')
                localSha256         = $sha256
                clientReportedSha256= $clientShaNorm
                clientFilenameHint  = $ClientReportedFilename
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersionSha256)) {
        $verSha = $ExpectedVersionSha256.ToUpperInvariant()
        if ($verSha -ne $sha256) {
            return @{
                ok                 = $false
                error              = 'hash_not_approved'
                message            = ('Uploaded SHA-256 ' + $sha256 + ' does not match VERSION.json paxScript.sha256 ' + $verSha + '.')
                localSha256        = $sha256
                expectedSha256     = $verSha
                clientFilenameHint = $ClientReportedFilename
            }
        }
    }

    $selectArgs = @{
        Manifest        = $Manifest
        CookbookVersion = $CookbookVersion
        TargetSha256    = $sha256
    }
    if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) {
        $selectArgs['TargetVersion'] = $TargetVersion
    }
    $sel = Select-CompatibleEngineEntry @selectArgs
    if ($null -eq $sel -or -not $sel.ok) {
        $reason  = if ($null -ne $sel -and $sel.error)   { [string]$sel.error }   else { 'no_compatible_engine' }
        $msgText = if ($null -ne $sel -and $sel.message) { [string]$sel.message } else { 'Select-CompatibleEngineEntry rejected the uploaded byte hash.' }
        if ($reason -eq 'no_compatible_engine') {
            return @{
                ok                 = $false
                error              = 'hash_not_approved'
                message            = ('Uploaded SHA-256 ' + $sha256 + ' does not match any approved entry compatible with cookbook ' + $CookbookVersion + '.')
                localSha256        = $sha256
                selectError        = $reason
                clientFilenameHint = $ClientReportedFilename
            }
        }
        return @{
            ok                 = $false
            error              = $reason
            message            = $msgText
            localSha256        = $sha256
            clientFilenameHint = $ClientReportedFilename
        }
    }

    $entry = $sel.entry

    try {
        if (-not (Test-Path -LiteralPath $WorkDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
        }
    } catch {
        return @{
            ok      = $false
            error   = 'work_directory_create_failed'
            message = ('Failed to create work directory "' + $WorkDirectory + '": ' + $_.Exception.Message)
        }
    }

    $stagedName = 'staged-upload-' + ([Guid]::NewGuid().ToString('N')) + '.ps1'
    $stagedPath = Join-Path $WorkDirectory $stagedName
    try {
        [System.IO.File]::WriteAllBytes($stagedPath, $Bytes)
    } catch {
        return @{
            ok      = $false
            error   = 'staging_write_failed'
            message = ('Failed to write staged file "' + $stagedPath + '": ' + $_.Exception.Message)
        }
    }

    $reHash = $null
    try {
        $reHash = ((Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash).ToUpperInvariant()
    } catch {
        try { Remove-Item -LiteralPath $stagedPath -Force -ErrorAction SilentlyContinue } catch { }
        return @{
            ok      = $false
            error   = 'post_write_hash_mismatch'
            message = ('Failed to re-hash staged file "' + $stagedPath + '": ' + $_.Exception.Message)
        }
    }
    if ($reHash -ne $sha256) {
        try { Remove-Item -LiteralPath $stagedPath -Force -ErrorAction SilentlyContinue } catch { }
        return @{
            ok           = $false
            error        = 'post_write_hash_mismatch'
            message      = ('Staged file SHA-256 ' + $reHash + ' does not match pre-write hash ' + $sha256 + '.')
            postWriteSha = $reHash
            preWriteSha  = $sha256
        }
    }

    return @{
        ok                  = $true
        stagedPath          = $stagedPath
        sha256              = $sha256
        version             = [string]$entry['version']
        source              = $Script:AcquisitionSourceLocalFile
        entry               = $entry
        bytes               = $Bytes
        originalPath        = $null
        originalSha256      = $sha256
        workDirectory       = $WorkDirectory
        clientFilenameHint  = $ClientReportedFilename
        clientReportedSha256= $ClientReportedSha256
    }
}

# =====================================================================
# Activate + persist (Stage 3+)
# =====================================================================

function Set-PaxScriptActivated {
    <#
    .SYNOPSIS
        Activates a previously-staged PAX script: copies the staged
        bytes byte-for-byte UNCHANGED to the canonical managed path,
        re-hashes the on-disk file, verifies the post-write SHA-256
        equals the pre-validated ExpectedSha256, and stamps the
        paxAcquisition block of install-state.json with pending=false,
        the manifest provenance, and activatedAtUtc / validatedAtUtc.
    .NOTES
        Plan section 4.5 immutability: byte primitives are
        [System.IO.File]::Copy and [System.IO.File]::Move (overwrite).
        SHA-256 is computed over the staged file BEFORE the copy and
        over the canonical file AFTER the move; both must equal
        ExpectedSha256 verbatim or activation fails. install-state.json
        is a sidecar metadata document; text JSON APIs are permitted
        on it per the immutability contract.
    .OUTPUTS
        Success: @{ ok=$true; canonicalPath; sha256; version; source;
                    activatedAtUtc; validatedAtUtc; statePath }
        Failure: @{ ok=$false; error=<token>; message=<text>; ... }
        Error tokens (closed set):
            invalid_source, invalid_version, invalid_expected_sha,
            staged_file_missing, staged_hash_mismatch,
            canonical_dir_create_failed, canonical_write_failed,
            post_write_hash_mismatch, install_state_write_failed
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$StagedFilePath,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$CanonicalScriptPath,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $false)][string]$ManifestId,
        [Parameter(Mandatory = $false)][string]$ManifestHash,
        [Parameter(Mandatory = $false)][string]$ManifestVersion,
        [Parameter(Mandatory = $false)][string]$StatePath
    )

    if ([string]::IsNullOrWhiteSpace($Source)) {
        return @{ ok = $false; error = 'invalid_source'; message = 'Source is required.' }
    }
    $allowedSources = @(
        $Script:AcquisitionSourceDownload,
        $Script:AcquisitionSourceLocalFile,
        $Script:AcquisitionSourceAutomation
    )
    if ($allowedSources -notcontains $Source) {
        return @{
            ok      = $false
            error   = 'invalid_source'
            message = ('Source "' + $Source + '" is not in the allowed set: ' + ($allowedSources -join ', ') + '.')
        }
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        return @{ ok = $false; error = 'invalid_version'; message = 'Version is required.' }
    }
    $expected = $ExpectedSha256.ToUpperInvariant()
    if ($expected -notmatch '^[0-9A-F]{64}$') {
        return @{ ok = $false; error = 'invalid_expected_sha'; message = ('ExpectedSha256 "' + $ExpectedSha256 + '" is not a 64-character hex string.') }
    }
    if ([string]::IsNullOrWhiteSpace($CanonicalScriptPath)) {
        return @{ ok = $false; error = 'canonical_write_failed'; message = 'CanonicalScriptPath is empty.' }
    }

    if (-not (Test-Path -LiteralPath $StagedFilePath -PathType Leaf)) {
        return @{ ok = $false; error = 'staged_file_missing'; message = ('Staged file does not exist: ' + $StagedFilePath) }
    }
    $stagedHash = $null
    try {
        $stagedHash = ((Get-FileHash -LiteralPath $StagedFilePath -Algorithm SHA256).Hash).ToUpperInvariant()
    } catch {
        return @{ ok = $false; error = 'staged_hash_mismatch'; message = ('Failed to hash staged file "' + $StagedFilePath + '": ' + $_.Exception.Message) }
    }
    if ($stagedHash -ne $expected) {
        return @{
            ok             = $false
            error          = 'staged_hash_mismatch'
            message        = ('Staged file SHA-256 ' + $stagedHash + ' does not match ExpectedSha256 ' + $expected + '.')
            stagedSha256   = $stagedHash
            expectedSha256 = $expected
        }
    }

    $canonDir = Split-Path -Parent $CanonicalScriptPath
    if (-not [string]::IsNullOrWhiteSpace($canonDir)) {
        try {
            if (-not (Test-Path -LiteralPath $canonDir -PathType Container)) {
                New-Item -ItemType Directory -Path $canonDir -Force | Out-Null
            }
        } catch {
            return @{
                ok      = $false
                error   = 'canonical_dir_create_failed'
                message = ('Failed to create canonical directory "' + $canonDir + '": ' + $_.Exception.Message)
            }
        }
    }

    # Copy staged bytes to a same-directory temp file, verify hash on
    # the temp, then atomically Move-with-overwrite onto the canonical
    # path. [System.IO.File]::Copy + ::Move avoid any text re-encoding.
    $tmpPath = $CanonicalScriptPath + '.tmp-' + ([Guid]::NewGuid().ToString('N'))
    try {
        [System.IO.File]::Copy($StagedFilePath, $tmpPath, $true)
    } catch {
        try { if (Test-Path -LiteralPath $tmpPath) { Remove-Item -LiteralPath $tmpPath -Force -ErrorAction SilentlyContinue } } catch { }
        return @{
            ok      = $false
            error   = 'canonical_write_failed'
            message = ('Failed to copy staged file to canonical temp "' + $tmpPath + '": ' + $_.Exception.Message)
        }
    }

    $tmpHash = $null
    try {
        $tmpHash = ((Get-FileHash -LiteralPath $tmpPath -Algorithm SHA256).Hash).ToUpperInvariant()
    } catch {
        try { Remove-Item -LiteralPath $tmpPath -Force -ErrorAction SilentlyContinue } catch { }
        return @{
            ok      = $false
            error   = 'post_write_hash_mismatch'
            message = ('Failed to hash canonical temp "' + $tmpPath + '": ' + $_.Exception.Message)
        }
    }
    if ($tmpHash -ne $expected) {
        try { Remove-Item -LiteralPath $tmpPath -Force -ErrorAction SilentlyContinue } catch { }
        return @{
            ok             = $false
            error          = 'post_write_hash_mismatch'
            message        = ('Canonical temp SHA-256 ' + $tmpHash + ' does not match ExpectedSha256 ' + $expected + '.')
            tempSha256     = $tmpHash
            expectedSha256 = $expected
        }
    }

    try {
        [System.IO.File]::Move($tmpPath, $CanonicalScriptPath, $true)
    } catch {
        try { Remove-Item -LiteralPath $tmpPath -Force -ErrorAction SilentlyContinue } catch { }
        return @{
            ok      = $false
            error   = 'canonical_write_failed'
            message = ('Failed to move canonical temp onto canonical path "' + $CanonicalScriptPath + '": ' + $_.Exception.Message)
        }
    }

    # Final post-move verification: hash the canonical file ON DISK.
    $postHash = $null
    try {
        $postHash = ((Get-FileHash -LiteralPath $CanonicalScriptPath -Algorithm SHA256).Hash).ToUpperInvariant()
    } catch {
        return @{
            ok      = $false
            error   = 'post_write_hash_mismatch'
            message = ('Failed to hash post-move canonical file "' + $CanonicalScriptPath + '": ' + $_.Exception.Message)
        }
    }
    if ($postHash -ne $expected) {
        return @{
            ok              = $false
            error           = 'post_write_hash_mismatch'
            message         = ('Canonical SHA-256 ' + $postHash + ' does not match ExpectedSha256 ' + $expected + '.')
            canonicalSha256 = $postHash
            expectedSha256  = $expected
        }
    }

    $nowUtc = ([DateTimeOffset]::UtcNow).ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $manifestIdField      = if ([string]::IsNullOrWhiteSpace($ManifestId))      { $null } else { $ManifestId }
    $manifestHashField    = if ([string]::IsNullOrWhiteSpace($ManifestHash))    { $null } else { $ManifestHash }
    $manifestVersionField = if ([string]::IsNullOrWhiteSpace($ManifestVersion)) { $null } else { $ManifestVersion }
    $fields = [ordered]@{
        pending          = $false
        source           = $Source
        version          = $Version
        sha256           = $expected
        manifestId       = $manifestIdField
        manifestHash     = $manifestHashField
        manifestVersion  = $manifestVersionField
        validatedAtUtc   = $nowUtc
        activatedAtUtc   = $nowUtc
        lastAttemptError = $null
    }
    $resolvedStatePath = if ([string]::IsNullOrWhiteSpace($StatePath)) { Get-PaxAcquisitionInstallStatePath } else { $StatePath }
    try {
        [void](Write-PaxAcquisitionInstallState -Fields $fields -StatePath $resolvedStatePath)
    } catch {
        return @{
            ok            = $false
            error         = 'install_state_write_failed'
            message       = ('Activation copied canonical bytes but install-state write failed: ' + $_.Exception.Message)
            canonicalPath = $CanonicalScriptPath
            sha256        = $expected
        }
    }

    return @{
        ok             = $true
        canonicalPath  = $CanonicalScriptPath
        sha256         = $expected
        version        = $Version
        source         = $Source
        activatedAtUtc = $nowUtc
        validatedAtUtc = $nowUtc
        statePath      = $resolvedStatePath
    }
}

function Write-PaxAcquisitionInstallState {
    <#
    .SYNOPSIS
        Reads install-state.json (creating an empty document if
        missing), merges -Fields into the paxAcquisition block,
        preserves all sibling top-level fields, and writes the
        document back atomically using text JSON APIs.

        This function operates on install-state.json (a sidecar
        metadata document), NOT on PAX script bytes — text APIs are
        permitted here. The §4.5 immutability contract applies to
        Set-PaxScriptActivated, not to this helper.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Fields,
        [Parameter(Mandatory = $false)][string]$StatePath
    )

    if ($null -eq $Fields -or -not ($Fields -is [System.Collections.IDictionary])) {
        throw 'invalid_fields: -Fields must be a hashtable.'
    }
    if ([string]::IsNullOrWhiteSpace($StatePath)) {
        $StatePath = Get-PaxAcquisitionInstallStatePath
    }

    $stateDir = Split-Path -Parent $StatePath
    if (-not [string]::IsNullOrWhiteSpace($stateDir) -and -not (Test-Path -LiteralPath $stateDir -PathType Container)) {
        New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
    }

    $doc = [ordered]@{}
    if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
        try {
            $raw = Get-Content -LiteralPath $StatePath -Raw -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace($raw)) {
                $parsed = $raw | ConvertFrom-Json -AsHashtable -Depth 12 -ErrorAction Stop
                if ($null -ne $parsed -and $parsed -is [System.Collections.IDictionary]) {
                    $doc = [ordered]@{}
                    foreach ($k in $parsed.Keys) { $doc[$k] = $parsed[$k] }
                }
            }
        } catch {
            # Corrupt existing document — preserve nothing, start fresh. Caller
            # may inspect the file beforehand if recovery is needed.
            $doc = [ordered]@{}
        }
    }

    $existing = $null
    if ($doc.Contains('paxAcquisition') -and $doc['paxAcquisition'] -is [System.Collections.IDictionary]) {
        $existing = $doc['paxAcquisition']
    }

    $merged = [ordered]@{}
    if ($null -ne $existing) {
        foreach ($k in $existing.Keys) { $merged[$k] = $existing[$k] }
    }
    foreach ($k in $Fields.Keys) { $merged[$k] = $Fields[$k] }

    $doc['paxAcquisition'] = $merged

    $json = $doc | ConvertTo-Json -Depth 12
    Set-Content -LiteralPath $StatePath -Value $json -Encoding utf8 -NoNewline

    return $merged
}

Export-ModuleMember -Function @(
    'Get-PaxAcquisitionInstallStatePath',
    'Read-PaxAcquisitionInstallState',
    'Get-PaxAcquisitionState',
    'Resolve-PaxAcquisitionPolicy',
    'Test-ApprovedEngineManifest',
    'Select-CompatibleEngineEntry',
    'Get-ApprovedEngineManifestPackage',
    'Get-PaxScriptByDownload',
    'Get-PaxScriptByLocalFile',
    'Get-PaxScriptByUploadBytes',
    'Set-PaxScriptActivated',
    'Write-PaxAcquisitionInstallState'
) -Variable @(
    'AcquisitionStateAcquired',
    'AcquisitionStateAcquisitionPending',
    'AcquisitionStateInFlight',
    'AcquisitionStateFailed',
    'AcquisitionSourceDownload',
    'AcquisitionSourceLocalFile',
    'AcquisitionSourceAutomation'
)
