#requires -Version 7.4

# =====================================================================
# Build-Release.ps1  Local, deterministic release-build pipeline for
# PAX Cookbook.
#
# WHAT THIS SCRIPT DOES
#
#   Reads the working copy of the repository, enumerates the appliance
#   file set (app/, launcher/) minus the closed exclusion list, and
#   emits a reproducible release directory containing:
#
#     pax-cookbook-<version>.zip            : the release package
#     pax-cookbook-<version>.zip.sha256     : sha256sum-format sidecar
#     pax-cookbook-<version>.release.json   : signed-ish release metadata
#                                             (signing.state="unsigned")
#     pax-cookbook-<version>.manifest.json  : update-manifest snapshot
#                                             that satisfies the
#                                             broker's manifest schema
#     pax-cookbook-<version>.release-notes.md : distributor-fill-in template
#
# WHAT THIS SCRIPT DELIBERATELY DOES NOT DO
#
#   - Does not perform any network call. No publish, upload, fetch,
#     or HTTP at all.
#   - Does not perform any git mutation. The -SourceCommit parameter
#     is a read-only INPUT captured into release metadata.
#   - Does not perform any code signing. Producing a signed package
#     is a distributor step performed AFTER this script, against a
#     distributor-held certificate, with distributor tooling.
#   - Does not invoke the installer, the broker, or the launcher.
#   - Does not modify VERSION.json or any source under app/ or launcher/.
#
# DETERMINISM CONTRACT
#
#   For a given (RepoRoot working-tree state, Channel, SourceEpoch,
#   PackageBaseUrl, ReleaseNotesUrl, BuildId) tuple, the produced
#   <pkg>.zip is bit-for-bit identical. BuildId and BuildAtUtc default
#   to deterministic derivations of SourceEpoch when not provided, so
#   re-running with the same SourceEpoch alone yields the same outputs.
#
# PARAMETERS
#
#   -RepoRoot         : repo root. Defaults to two levels above this
#                       script ($PSScriptRoot\..\..).
#   -OutputRoot       : directory the artifacts are written into.
#                       Defaults to <RepoRoot>\dist\<channel>\
#                       pax-cookbook-<version>.
#   -Channel          : release channel string. Defaults to whatever
#                       app/VERSION.json declares (typically 'stable').
#   -SourceEpoch      : a UTC DateTime used to stamp every ZIP entry
#                       (rounded to 2-second DOS time). Defaults to
#                       parsing VERSION.json's releaseTimestamp, or
#                       a canonical fallback 2024-01-01T00:00:00Z if
#                       that field is null.
#   -SourceCommit     : optional opaque commit identifier (e.g. a git
#                       SHA) captured verbatim into release metadata.
#   -PackageBaseUrl   : optional HTTPS base URL. When provided, the
#                       manifest snapshot embeds the full HTTPS
#                       package URL. When omitted, the snapshot keeps
#                       a placeholder string for the distributor to fill
#                       in later (and Test-UpdateManifestSchema will
#                       reject the snapshot until they do).
#   -ReleaseNotesUrl  : optional HTTPS URL for the release notes.
#   -BuildId          : optional opaque ID. Defaults to a value
#                       derived deterministically from SourceEpoch.
#   -Force            : delete an existing OutputRoot before writing.
# =====================================================================

[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$OutputRoot,
    [string]$Channel,
    [Nullable[DateTime]]$SourceEpoch,
    [string]$SourceCommit,
    [string]$PackageBaseUrl,
    [string]$ReleaseNotesUrl,
    [string]$BuildId,
    [switch]$InternalTestProfile,
    [switch]$InternalTestUnsigned,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# PLAN 6.1 / 5.5: -InternalTestUnsigned requires -InternalTestProfile.
# Refuse the single-flag combination BEFORE any other check runs.
if ($InternalTestUnsigned.IsPresent -and -not $InternalTestProfile.IsPresent) {
    throw '-InternalTestUnsigned requires -InternalTestProfile on the same invocation. Refusing to build.'
}

function Resolve-RepoRoot {
    param([string]$Override)
    if (-not [string]::IsNullOrWhiteSpace($Override)) {
        if (-not (Test-Path -LiteralPath $Override -PathType Container)) {
            throw ('RepoRoot does not exist: ' + $Override)
        }
        return (Resolve-Path -LiteralPath $Override).Path.TrimEnd('\','/')
    }
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\','/')
}

function Get-DefaultSourceEpoch {
    param([pscustomobject]$VersionInfo)
    if (-not [string]::IsNullOrWhiteSpace($VersionInfo.ReleaseTimestamp)) {
        $parsed = [datetime]::MinValue
        if ([datetime]::TryParse(
                $VersionInfo.ReleaseTimestamp,
                $null,
                [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal,
                [ref]$parsed)) {
            return $parsed
        }
    }
    # Canonical fallback: 2024-01-01T00:00:00Z. Picking a fixed value
    # so different machines produce the same ZIP.
    return [datetime]::new(2024, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
}

function Get-DeterministicBuildId {
    param([datetime]$SourceEpochUtc, [string]$CookbookVersion, [string]$Channel)
    $material = ('pax-cookbook|' + $CookbookVersion + '|' + $Channel + '|' + $SourceEpochUtc.ToString('o'))
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($material)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($bytes)
    } finally {
        $sha.Dispose()
    }
    # Take the first 16 bytes and stamp a UUID v4-ish shape from them
    # (version + variant bits set so it parses as a valid GUID). Use
    # an explicit byte[] buffer because PowerShell range-indexing into
    # a byte array can degrade to object[], which [guid]::new() will
    # reject.
    $guidBytes = New-Object 'byte[]' 16
    [System.Array]::Copy($hash, 0, $guidBytes, 0, 16)
    $guidBytes[7] = [byte](($guidBytes[7] -band 0x0F) -bor 0x40)
    $guidBytes[8] = [byte](($guidBytes[8] -band 0x3F) -bor 0x80)
    return ([guid]::new($guidBytes)).ToString()
}

function Write-JsonFile {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Path)
    $text = $Value | ConvertTo-Json -Depth 12
    # Normalise line endings + strip BOM for deterministic byte
    # output across machines.
    $text = $text -replace "`r`n","`n"
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($text + "`n")
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

# ---------------------------------------------------------------------
# Boot
# ---------------------------------------------------------------------
$resolvedRepoRoot = Resolve-RepoRoot -Override $RepoRoot
$modulePath = Join-Path $PSScriptRoot 'Release.psm1'
if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
    throw ('Release.psm1 not found alongside Build-Release.ps1: ' + $modulePath)
}
Import-Module $modulePath -Force -Global -ErrorAction Stop

Write-Host ('[release] Repo root        : ' + $resolvedRepoRoot)

$versionInfo = Get-ReleaseVersionInfo -RepoRoot $resolvedRepoRoot
$resolvedChannel = if ([string]::IsNullOrWhiteSpace($Channel)) { $versionInfo.Channel } else { $Channel }
if ([string]::IsNullOrWhiteSpace($resolvedChannel)) { $resolvedChannel = 'stable' }

$resolvedSourceEpoch = if ($null -ne $SourceEpoch) {
    ([datetime]$SourceEpoch).ToUniversalTime()
} else {
    Get-DefaultSourceEpoch -VersionInfo $versionInfo
}

$packageFile = 'pax-cookbook-' + $versionInfo.CookbookVersion + '.zip'

$resolvedOutputRoot = if (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot
} else {
    Join-Path (Join-Path (Join-Path $resolvedRepoRoot 'dist') $resolvedChannel) ('pax-cookbook-' + $versionInfo.CookbookVersion)
}

Write-Host ('[release] Channel          : ' + $resolvedChannel)
Write-Host ('[release] Cookbook version : ' + $versionInfo.CookbookVersion)
Write-Host ('[release] PAX script ver.  : ' + $versionInfo.PaxScriptVersion)
Write-Host ('[release] Source epoch UTC : ' + $resolvedSourceEpoch.ToString('o'))
Write-Host ('[release] Output root      : ' + $resolvedOutputRoot)

# ---------------------------------------------------------------------
# Profile resolution (PLAN 5.3 catalog)
# ---------------------------------------------------------------------
# legacy-embedded : source has no acquisitionPolicy field (today's source)
# production       : source is external + neither flag set
# internal-test-signed   : source is external + -InternalTestProfile only
# internal-test-unsigned : source is external + -InternalTestProfile -InternalTestUnsigned

$resolvedProfile = if (-not $versionInfo.HasExternalPolicyFields) {
    'legacy-embedded'
} elseif ($InternalTestUnsigned.IsPresent) {
    'internal-test-unsigned'
} elseif ($InternalTestProfile.IsPresent) {
    'internal-test-signed'
} else {
    'production'
}

# Production profile refuses a source whose VERSION.json carries
# manifestSignaturePolicy='internal-test-bypass' -- such an artifact
# would never be customer-facing.
if ($resolvedProfile -eq 'production' -and
    [string]$versionInfo.ManifestSignaturePolicy -eq 'internal-test-bypass') {
    throw 'production_build_refuses_internal_test_bypass: VERSION.json.paxScript.manifestSignaturePolicy must be "required" for a production build (saw "internal-test-bypass"). This artifact cannot be customer-facing.'
}
# Internal-test-unsigned profile refuses a source whose VERSION.json
# carries manifestSignaturePolicy='required' (the build-flag pair
# would otherwise stamp a bypass artifact from a non-bypass source).
if ($resolvedProfile -eq 'internal-test-unsigned' -and
    $versionInfo.HasExternalPolicyFields -and
    [string]$versionInfo.ManifestSignaturePolicy -ne 'internal-test-bypass') {
    throw ('internal_test_unsigned_source_mismatch: -InternalTestUnsigned requires VERSION.json.paxScript.manifestSignaturePolicy = "internal-test-bypass" (got "' + [string]$versionInfo.ManifestSignaturePolicy + '").')
}
if ($resolvedProfile -eq 'internal-test-signed' -and
    $versionInfo.HasExternalPolicyFields -and
    [string]$versionInfo.ManifestSignaturePolicy -ne 'required') {
    throw ('internal_test_signed_source_mismatch: -InternalTestProfile (signed) requires VERSION.json.paxScript.manifestSignaturePolicy = "required" (got "' + [string]$versionInfo.ManifestSignaturePolicy + '").')
}

Write-Host ('[release] Profile          : ' + $resolvedProfile)

if (Test-Path -LiteralPath $resolvedOutputRoot) {
    if (-not $Force) {
        throw ('OutputRoot already exists. Pass -Force to overwrite: ' + $resolvedOutputRoot)
    }
    Remove-Item -LiteralPath $resolvedOutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null

# ---------------------------------------------------------------------
# Enumerate appliance file set
# ---------------------------------------------------------------------
# Apply PLAN 6.5 pax-exclusion patterns ONLY when source is external-
# policy. Legacy embedded sources retain the historical PAX-script
# inclusion.
$applyPaxExclusions = ([string]$versionInfo.AcquisitionPolicy -eq 'external')
$fileSet = Get-ReleaseFileSet -RepoRoot $resolvedRepoRoot -ApplyExternalPaxExclusions:$applyPaxExclusions
if ($null -eq $fileSet -or $fileSet.Count -eq 0) {
    throw 'Release file set is empty -- nothing to package. Refusing to write an empty ZIP.'
}
Write-Host ('[release] File count       : ' + $fileSet.Count)

# Defense in depth: re-test every selected path against the exclusion
# list before we write anything. The walker already did this, but a
# bug there must not leak secrets into the ZIP.
foreach ($rel in $fileSet) {
    if (Test-ReleaseExclusion -RelativePath $rel) {
        throw ('Refusing to package excluded path (defense-in-depth guard): ' + $rel)
    }
}

# ---------------------------------------------------------------------
# Build deterministic ZIP
# ---------------------------------------------------------------------
$zipPath = Join-Path $resolvedOutputRoot $packageFile
$zipResult = New-CanonicalZip `
    -RepoRoot $resolvedRepoRoot `
    -RelativePaths $fileSet `
    -ZipPath $zipPath `
    -EntryTimestampUtc $resolvedSourceEpoch

Write-Host ('[release] ZIP path         : ' + $zipResult.Path)
Write-Host ('[release] ZIP size bytes   : ' + $zipResult.SizeBytes)
Write-Host ('[release] ZIP sha256       : ' + $zipResult.Sha256)
Write-Host ('[release] ZIP entries      : ' + $zipResult.EntryCount)

# ---------------------------------------------------------------------
# Sidecars
# ---------------------------------------------------------------------
$sidecarPath = Write-Sha256Sidecar -PackagePath $zipPath -Sha256 $zipResult.Sha256
Write-Host ('[release] SHA256 sidecar   : ' + $sidecarPath)

# ---------------------------------------------------------------------
# Release metadata
# ---------------------------------------------------------------------
$buildIdResolved = if ([string]::IsNullOrWhiteSpace($BuildId)) {
    Get-DeterministicBuildId -SourceEpochUtc $resolvedSourceEpoch -CookbookVersion $versionInfo.CookbookVersion -Channel $resolvedChannel
} else {
    $BuildId
}

$exclusionPatternCount = (Get-ReleaseExclusionPatterns).Count
$builtAtUtc = $resolvedSourceEpoch   # Deterministic: built-at == source epoch by default.

$metadata = New-ReleaseMetadata `
    -VersionInfo $versionInfo `
    -Channel $resolvedChannel `
    -BuildId $buildIdResolved `
    -BuiltAtUtc $builtAtUtc `
    -BuiltOnHost ([System.Environment]::MachineName) `
    -SourceCommit $SourceCommit `
    -PackageFile $packageFile `
    -PackageSizeBytes $zipResult.SizeBytes `
    -PackageSha256 $zipResult.Sha256 `
    -FileCount $fileSet.Count `
    -ExclusionPatternCount $exclusionPatternCount `
    -Profile $resolvedProfile `
    -Notes 'Local deterministic release build. No cryptographic signature has been applied; verify integrity via the .sha256 sidecar before publishing.'

# Update signing.sidecarFile so the metadata explicitly points at the
# attestation file we just wrote.
$metadata.signing.sidecarFile = (Split-Path -Leaf $sidecarPath)

$metadataPath = Join-Path $resolvedOutputRoot ('pax-cookbook-' + $versionInfo.CookbookVersion + '.release.json')
Write-JsonFile -Value $metadata -Path $metadataPath
Write-Host ('[release] release.json     : ' + $metadataPath)

# ---------------------------------------------------------------------
# Manifest snapshot
# ---------------------------------------------------------------------
$manifestSnapshot = New-ReleaseManifest `
    -VersionInfo $versionInfo `
    -Channel $resolvedChannel `
    -BuiltAtUtc $builtAtUtc `
    -PackageFile $packageFile `
    -PackageSha256 $zipResult.Sha256 `
    -PackageBaseUrl $PackageBaseUrl `
    -ReleaseNotesUrl $ReleaseNotesUrl

$manifestPath = Join-Path $resolvedOutputRoot ('pax-cookbook-' + $versionInfo.CookbookVersion + '.manifest.json')
Write-JsonFile -Value $manifestSnapshot -Path $manifestPath
Write-Host ('[release] manifest.json    : ' + $manifestPath)

# ---------------------------------------------------------------------
# Release notes template (copied unmodified)
# ---------------------------------------------------------------------
$notesTemplatePath = Join-Path $PSScriptRoot 'RELEASE_NOTES.md.template'
if (-not (Test-Path -LiteralPath $notesTemplatePath -PathType Leaf)) {
    throw ('RELEASE_NOTES.md.template not found alongside Build-Release.ps1: ' + $notesTemplatePath)
}
$notesOutPath = Join-Path $resolvedOutputRoot ('pax-cookbook-' + $versionInfo.CookbookVersion + '.release-notes.md')
$rawNotes = Get-Content -LiteralPath $notesTemplatePath -Raw
$rawNotes = $rawNotes -replace "`r`n","`n"
$rawNotes = $rawNotes.Replace('{{COOKBOOK_VERSION}}', $versionInfo.CookbookVersion)
$rawNotes = $rawNotes.Replace('{{CHANNEL}}', $resolvedChannel)
$rawNotes = $rawNotes.Replace('{{PAX_SCRIPT_VERSION}}', $versionInfo.PaxScriptVersion)
$rawNotes = $rawNotes.Replace('{{PACKAGE_FILE}}', $packageFile)
$rawNotes = $rawNotes.Replace('{{PACKAGE_SHA256}}', $zipResult.Sha256)
$rawNotes = $rawNotes.Replace('{{BUILT_AT_UTC}}', $builtAtUtc.ToString('o'))
$bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($rawNotes)
[System.IO.File]::WriteAllBytes($notesOutPath, $bytes)
Write-Host ('[release] release-notes.md : ' + $notesOutPath)

# ---------------------------------------------------------------------
# Post-build verification (defense-in-depth)
# ---------------------------------------------------------------------
$verifySha = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToUpperInvariant()
if ($verifySha -ne $zipResult.Sha256) {
    throw ('Internal error: ZIP changed under us. Expected ' + $zipResult.Sha256 + ' got ' + $verifySha)
}

# Re-read the ZIP and ensure no excluded path leaked in.
Add-Type -AssemblyName 'System.IO.Compression.FileSystem' -ErrorAction SilentlyContinue | Out-Null
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($entry in $archive.Entries) {
        if (Test-ReleaseExclusion -RelativePath $entry.FullName) {
            throw ('Excluded path leaked into ZIP: ' + $entry.FullName)
        }
    }
} finally {
    $archive.Dispose()
}

# ---------------------------------------------------------------------
# External-policy fail-closed gate (PLAN 6.1)
# ---------------------------------------------------------------------
# Skipped for legacy-embedded sources (Test-ReleaseExternalPolicyInvariants
# returns ok=$true with skipped=$true when VERSION.json lacks the
# acquisitionPolicy field). For external sources, all 23 invariants
# from PLAN 6.1 are evaluated; ANY failure aborts the build with a
# single ERROR line naming each failing invariant code.
$gateProfile = if ($resolvedProfile -eq 'legacy-embedded') { 'legacy-embedded' } else { 'auto' }
$gate = Test-ReleaseExternalPolicyInvariants `
    -VersionInfo $versionInfo `
    -ManifestSnapshot $manifestSnapshot `
    -PackageRelativePaths $fileSet `
    -SourceRepoRoot $resolvedRepoRoot `
    -Profile $gateProfile

if ($gate.skipped) {
    Write-Host ('[release] External-policy gate skipped (' + $gate.profile + ').')
} elseif (-not $gate.ok) {
    foreach ($f in $gate.failures) {
        Write-Host ('[release] ERROR external_policy_invariant: ' + $f.code + ' :: ' + $f.message) -ForegroundColor Red
    }
    throw ('external_policy_invariants_failed: ' + (@($gate.failures | ForEach-Object { $_.code }) -join ', '))
} else {
    Write-Host ('[release] External-policy gate PASS (' + $gate.profile + ', ' + @($gate.checks).Count + ' checks).')
}

Write-Host ''
Write-Host '[release] BUILD COMPLETE'
Write-Host ('[release]   Package        : ' + $zipPath)
Write-Host ('[release]   SHA-256        : ' + $zipResult.Sha256)
Write-Host ('[release]   Sidecar        : ' + $sidecarPath)
Write-Host ('[release]   Metadata       : ' + $metadataPath)
Write-Host ('[release]   Manifest       : ' + $manifestPath)
Write-Host ('[release]   Release notes  : ' + $notesOutPath)

# Emit a structured summary on the success pipeline for callers
# (such as the verification harness) that wrap us.
return [pscustomobject]@{
    OutputRoot       = $resolvedOutputRoot
    PackagePath      = $zipPath
    PackageSha256    = $zipResult.Sha256
    PackageSize      = $zipResult.SizeBytes
    SidecarPath      = $sidecarPath
    MetadataPath     = $metadataPath
    ManifestPath     = $manifestPath
    ReleaseNotesPath = $notesOutPath
    FileCount        = $fileSet.Count
    BuildId          = $buildIdResolved
    BuiltAtUtc       = $builtAtUtc
    SourceEpochUtc   = $resolvedSourceEpoch
    Channel          = $resolvedChannel
    CookbookVersion  = $versionInfo.CookbookVersion
}
