#requires -Version 7.4

# =====================================================================
# Release.psm1  Pure functions for the PAX Cookbook local release-
# build pipeline.
#
# This module is dev-tooling. It does NOT ship inside the release
# package. Build-Release.ps1 imports it and uses its functions to
# enumerate the appliance file set, compose canonical release
# metadata + manifest snapshot, and produce a deterministic ZIP.
#
# Hard rules (enforced by the Phase Q verification harness):
#
#   - No outbound network calls. The release pipeline reads the
#     local working copy and writes local output. It does NOT
#     publish, upload, fetch, or call anything over the network.
#   - No git mutation. No `git commit`, `git push`, `git tag`,
#     `git reset --hard`. Source-commit identity is an INPUT to the
#     pipeline, captured verbatim into release metadata; it is never
#     mutated.
#   - No auto-signing. The pipeline produces structurally complete
#     release metadata with `signing.state = "unsigned"` and
#     `signing.verified = $false`. It does not invoke signtool,
#     AzureSignTool, Set-AuthenticodeSignature, or anything else.
#   - No auto-install / auto-launch. The pipeline produces files; it
#     does not call Install-PAXCookbook, Start-Process, or anything
#     that runs the appliance.
#   - Closed-key allow-lists for the JSON it emits. Every release
#     artifact has an exhaustively enumerated key set.
#   - Truthful schema integration. The manifest snapshot is shaped
#     to satisfy Test-UpdateManifestSchema in the broker's
#     Manifest.psm1 -- the same validator the runtime uses.
#
# Exported functions:
#
#   - Get-ReleaseIncludeRoots
#       The closed list of repo-relative roots that constitute the
#       appliance package.
#
#   - Get-ReleaseIncludeTopLevelFiles
#       The closed list of repo-relative INDIVIDUAL files at the
#       package top level that ship alongside the include roots
#       (e.g. the double-click CMD installer wrapper).
#
#   - Get-ReleaseExclusionPatterns
#       The closed list of regex patterns (case-insensitive) applied
#       to forward-slash relative paths. A match means "this path is
#       NOT in the release package."
#
#   - Test-ReleaseExclusion
#       Returns $true if a given repo-relative path matches any
#       exclusion pattern.
#
#   - Get-ReleaseFileSet
#       Walks the include roots under a repo root, applies exclusions,
#       and returns a stable, sorted list of relative paths.
#
#   - Get-ReleaseVersionInfo
#       Reads app/VERSION.json and returns the canonical version
#       block.
#
#   - New-ReleaseMetadata
#       Composes the release.json object describing the build.
#
#   - New-ReleaseManifest
#       Composes the manifest.json snapshot for this release. The
#       result validates against the broker's manifest schema.
#
#   - New-CanonicalZip
#       Writes a deterministic ZIP with sorted entries and pinned
#       LastWriteTime. Returns a pscustomobject with Path, SizeBytes,
#       Sha256, EntryCount.
#
#   - Write-Sha256Sidecar
#       Writes a sha256sum-format sidecar file ("<HEX>  <filename>").
# =====================================================================

# Closed list of repo-relative roots that make up the appliance.
# Anything outside these roots is, by definition, not in the package.
$Script:ReleaseIncludeRoots = @(
    'app',
    'launcher'
)

# Closed list of repo-relative INDIVIDUAL FILES at the package top
# level that ship alongside the include roots above. These exist so
# the operator has a small surface of top-level convenience entry
# points after extracting the ZIP without forcing us to invent a
# whole new include root for one or two files.
#
# Rules for adding to this list:
#   - Path is repo-relative, forward-slash, lowercase or exact case.
#   - File must exist in the working tree at build time (the walker
#     refuses to package a missing top-level file).
#   - File must NOT match any pattern in $Script:ReleaseExclusionPatterns.
#   - File is expected to be a small launcher / wrapper / shim --
#     binary payloads belong under app/ or launcher/.
$Script:ReleaseIncludeTopLevelFiles = @(
    'Install PAX Cookbook.cmd'
)

# Closed list of exclusion regex patterns. Applied to forward-slash
# repo-relative paths with the (?i) flag enabled.
$Script:ReleaseExclusionPatterns = @(
    # Top-level non-appliance trees.
    '^(_temp|_backup|temp|docs|tools|dist|scripts)(/|$)',

    # Product-shell build SOURCE. Only the compiled static bundle under
    # app/web/app ships in the appliance package; the React/Vite source,
    # its config, and its build cache stay out of the ZIP.
    '^app/web-react(/|$)',

    # Top-level dev-only files at the repo root. (Defensive: these
    # would only sneak in if someone listed the repo root, but we
    # match them anyway.)
    '^(README\.md|script_output\.txt|RELEASE\.md)$',

    # Source-control + IDE + tool artifacts anywhere in the tree.
    '(^|/)\.git(/|$)',
    '(^|/)\.gitattributes$',
    '(^|/)\.gitignore$',
    '(^|/)\.vs(/|$)',
    '(^|/)\.vscode(/|$)',
    '(^|/)\.idea(/|$)',
    '(^|/)node_modules(/|$)',

    # Phase-archive directories may live anywhere in the tree (e.g.
    # app/install/_archive/). Test-ReleaseExternalPolicyInvariants
    # flags any '_archive/' path as forbidden dev-state leakage, so
    # the walker must exclude them at the source.
    '(^|/)_archive(/|$)',
    '(^|/)__pycache__(/|$)',

    # OS clutter anywhere.
    '(^|/)(\.DS_Store|Thumbs\.db|desktop\.ini)$',

    # User-owned operational state directories (must never appear in
    # a release ZIP -- they are produced by the appliance at runtime,
    # not by the developer).
    '(^|/)(Updates|Runs|Cooks|Logs|Backups|recipes|Trust|Workspaces)(/|$)',

    # State / transient / log file extensions anywhere.
    '\.(sqlite|sqlite-journal|sqlite-shm|sqlite-wal|db|partial|tmp|temp|log|bak|swp)$',

    # Signing secret extensions anywhere. Hard guard.
    '\.(pfx|p12|pem|key|crt|cer|jks|keystore|pkcs12)$',

    # Trust artifacts that live in the operator workspace, never in
    # the release ZIP.
    '(^|/)trusted-signers\.json$',
    '\.sig$',
    '\.signer\.json$',

    # Package staging sidecars from prior local builds.
    '\.zip\.metadata\.json$',
    '\.zip\.sha256$',
    '\.zip$',

    # Test sandboxes occasionally leaked into the tree.
    '(^|/)paxcb_smoke_[A-Za-z0-9_-]+(/|$)'
)

# Allowed top-level keys on the emitted <pkg>.release.json document.
$Script:AllowedReleaseMetadataKeys = @(
    'schemaVersion',
    'cookbookVersion',
    'channel',
    'buildId',
    'builtAtUtc',
    'builtOnHost',
    'sourceCommit',
    'packageFile',
    'packageSizeBytes',
    'packageSha256',
    'manifestSchemaVersion',
    'paxScript',
    'signing',
    'fileCount',
    'exclusionPatternCount',
    'publishable',
    'notes'
)

$Script:AllowedReleaseSigningKeys = @(
    'state',
    'profile',
    'verified',
    'signerCertThumbprint',
    'signedAtUtc',
    'signatureAlgorithm',
    'sidecarFile',
    'notes'
)

# Allowed keys under release.json.paxScript and manifest.includedPaxScript.
# Five external-policy fields layered on top of the four base fields.
$Script:AllowedReleasePaxScriptKeys = @(
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

# Profile catalog (the build-side closed set). The release pipeline
# stamps signing.state + signing.profile + top-level publishable from
# the active profile per row.
$Script:ReleaseProfileCatalog = @{
    'production' = @{
        signingState            = 'production-signed-required'
        signingProfile          = 'production'
        manifestSignaturePolicy = 'required'
        publishable             = $true
        allowLoopbackHttp       = $false
        notes                   = 'Production-profile build. Awaits detached signing via Sign-Release.ps1 before customer publish.'
    }
    'internal-test-signed' = @{
        signingState            = 'internal-test'
        signingProfile          = 'internal-test-signed'
        manifestSignaturePolicy = 'required'
        publishable             = $false
        allowLoopbackHttp       = $true
        notes                   = 'Internal-test signed build. NOT customer-publishable. Manifest signature verification still required at runtime.'
    }
    'internal-test-unsigned' = @{
        signingState            = 'internal-test-unsigned'
        signingProfile          = 'internal-test-unsigned'
        manifestSignaturePolicy = 'internal-test-bypass'
        publishable             = $false
        allowLoopbackHttp       = $true
        notes                   = 'Internal-test unsigned build. NOT customer-publishable. Runtime manifest signature verification is BYPASSED.'
    }
    'legacy-embedded' = @{
        signingState            = 'unsigned'
        signingProfile          = $null
        manifestSignaturePolicy = $null
        publishable             = $false
        allowLoopbackHttp       = $false
        notes                   = 'Legacy embedded historical artifact. External-policy gates do not apply. NOT customer-publishable post-Stage-5.'
    }
}

# Additional exclusion patterns applied ONLY when VersionInfo.AcquisitionPolicy = 'external'
# (per PLAN §6.5). Under legacy embedded these are NOT applied so the
# Phase 12 historical-artifact carve-out remains intact.
#
# The directory rule is scoped to ^app/resources/pax(/|$) rather than
# the broader (^|/)pax(/|$) because the broker bundles a separate,
# always-required Pax/ adapter module at app/broker/Pax/Adapter.psm1
# (Convert-RecipeToPaxArgv + Get-PaxInvocationPlan). A case-insensitive
# unscoped 'pax' regex would strip that module and the broker would
# hard-fail with 'no valid module file' at startup line 4385.
$Script:ReleaseExternalPaxExclusionPatterns = @(
    '(?i)^app/resources/pax/PAX_Purview_Audit_Log_Processor\.ps1$',
    '(?i)^app/resources/pax(/|$)'
)

# Closed enumeration of allowed acquisition-policy values mirrored from
# the broker's Manifest.psm1. Kept in sync by Pass C close.
$Script:ReleaseAllowedAcquisitionPolicies      = @('external','embedded')
$Script:ReleaseAllowedManifestSignaturePolicies = @('required','internal-test-bypass')

# Placeholder regex enforced in ALL profiles (per PLAN §6.3) -- never
# bypassable. Case-sensitive. Wrapped in single-quotes to keep the
# leading dollar literal (PS double-quoted regex literal trap).
$Script:ReleasePlaceholderRegex = '^<(TODO|PLACEHOLDER|UNSET|TBD|FILL_ME|PENDING)_[A-Z0-9_]+>$'

# Source-grep forbidden tokens (PLAN §6.1 last three invariants).
# Each entry is treated as a regex (CASE-INSENSITIVE via -match)
# applied to the AST-comment-stripped text of every .ps1/.psm1 under
# app/. Casing variants must NOT be a bypass route, so the matcher
# stays case-insensitive on purpose. Tokens are wrapped in single
# quotes to keep the leading dollar literal (PS double-quoted regex
# literal trap).
$Script:ReleaseForbiddenExportTokens = @(
    'Export-PaxScript',
    '/api/v1/[^''\"\s]*?/pax/export',
    'paxScript/export',
    '/pax/export',
    'pax-script/download',
    'Export-PaxBytes',
    'Export-PaxEngine',
    'Invoke-RuntimeExportPaxScriptGet',
    'onClickPaxExport',
    'pax_export_disabled',
    'PAX_EXPORT'
)
$Script:ReleaseForbiddenSideloadTokens = @(
    'sideload',
    'automation-path',
    'AcquisitionSourceSideload',
    'AcquisitionSourceAutomationPath',
    'ProbePax'
)
$Script:ReleaseForbiddenUseAnywayTokens = @(
    'useAnyway',
    'use_anyway',
    'acceptUnsigned',
    'skipSignature',
    'bypassSignature',
    'allowUnsigned',
    'BypassSig',
    'force-accept-unsigned'
)

$Script:ReleaseMetadataSchemaVersion = 1


# ---------------------------------------------------------------------
# Include / exclude
# ---------------------------------------------------------------------
function Get-ReleaseIncludeRoots {
    return ,@($Script:ReleaseIncludeRoots)
}

function Get-ReleaseIncludeTopLevelFiles {
    return ,@($Script:ReleaseIncludeTopLevelFiles)
}

function Get-ReleaseExclusionPatterns {
    return ,@($Script:ReleaseExclusionPatterns)
}

function Test-ReleaseExclusion {
    param([Parameter(Mandatory)][string]$RelativePath)
    $norm = ($RelativePath -replace '\\','/').TrimStart('/')
    foreach ($pattern in $Script:ReleaseExclusionPatterns) {
        if ($norm -match ('(?i)' + $pattern)) { return $true }
    }
    return $false
}

function Get-ReleaseFileSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [switch]$ApplyExternalPaxExclusions
    )
    if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
        throw ('RepoRoot does not exist or is not a directory: ' + $RepoRoot)
    }
    $absRepo = (Resolve-Path -LiteralPath $RepoRoot).Path.TrimEnd('\','/')

    # Conditional extension patterns under external acquisition policy
    # (PLAN §6.5). Under legacy embedded these patterns are skipped
    # so the historical PAX-script artifact remains in the package.
    $extraPatterns = @()
    if ($ApplyExternalPaxExclusions.IsPresent) {
        $extraPatterns = $Script:ReleaseExternalPaxExclusionPatterns
    }

    $results = New-Object System.Collections.Generic.List[string]
    foreach ($root in $Script:ReleaseIncludeRoots) {
        $rootAbs = Join-Path $absRepo $root
        if (-not (Test-Path -LiteralPath $rootAbs -PathType Container)) { continue }
        $files = Get-ChildItem -LiteralPath $rootAbs -Recurse -File -Force -ErrorAction Stop
        foreach ($f in $files) {
            $rel = $f.FullName.Substring($absRepo.Length).TrimStart('\','/')
            $rel = $rel -replace '\\','/'
            if (Test-ReleaseExclusion -RelativePath $rel) { continue }
            $excludedByExtra = $false
            foreach ($p in $extraPatterns) {
                if ($rel -match $p) { $excludedByExtra = $true; break }
            }
            if ($excludedByExtra) { continue }
            $results.Add($rel)
        }
    }

    # Top-level convenience files (e.g. the double-click CMD installer
    # wrapper). Each entry must exist at $RepoRoot and must not match
    # any exclusion pattern; we refuse to package a missing top-level
    # file (release build fails loudly) and refuse to ship a top-level
    # file that the exclusion list would otherwise filter out.
    foreach ($rel in $Script:ReleaseIncludeTopLevelFiles) {
        $relNorm = ($rel -replace '\\','/').TrimStart('/')
        $abs = Join-Path $absRepo $relNorm
        if (-not (Test-Path -LiteralPath $abs -PathType Leaf)) {
            throw ('Top-level include file missing at repo root: ' + $relNorm)
        }
        if (Test-ReleaseExclusion -RelativePath $relNorm) {
            throw ('Top-level include file matches an exclusion pattern: ' + $relNorm)
        }
        $extraHit = $false
        foreach ($p in $extraPatterns) {
            if ($relNorm -match $p) { $extraHit = $true; break }
        }
        if ($extraHit) {
            throw ('Top-level include file matches an external-policy exclusion pattern: ' + $relNorm)
        }
        $results.Add($relNorm)
    }

    # Stable lexicographic ordering, case-insensitive then case-sensitive
    # as a tie-breaker.
    $sorted = $results | Sort-Object -Property @{Expression={$_.ToLowerInvariant()}}, @{Expression={$_}}
    return ,@($sorted)
}


# ---------------------------------------------------------------------
# Version + PAX-script info
# ---------------------------------------------------------------------
function Get-ReleaseVersionInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot
    )
    $vf = Join-Path (Join-Path $RepoRoot 'app') 'VERSION.json'
    if (-not (Test-Path -LiteralPath $vf -PathType Leaf)) {
        throw ('VERSION.json not found at: ' + $vf)
    }
    $raw = Get-Content -LiteralPath $vf -Raw -ErrorAction Stop
    $json = $raw | ConvertFrom-Json -AsHashtable -Depth 12
    if (-not (Test-HashtableLike $json)) {
        throw 'VERSION.json did not deserialize to an object.'
    }
    if (-not (Test-HashtableLike $json['cookbook'])) {
        throw 'VERSION.json is missing or malformed "cookbook" block.'
    }
    if (-not (Test-HashtableLike $json['paxScript'])) {
        throw 'VERSION.json is missing or malformed "paxScript" block.'
    }

    # Read the five external-policy fields with null-tolerance so
    # legacy embedded-shape sources continue to load without error.
    # Validation of these fields' values (when present) is handled by
    # Test-ReleaseExternalPolicyInvariants -- this getter is shape-
    # neutral on purpose.
    $pax = $json['paxScript']
    $extAcquisitionPolicy   = if ($pax.Contains('acquisitionPolicy'))                    { [string]$pax['acquisitionPolicy'] }                   else { $null }
    $extExportEnabledRaw    = if ($pax.Contains('exportEnabled'))                        { $pax['exportEnabled'] }                                else { $null }
    $extEngineManifestUrl   = if ($pax.Contains('engineManifestUrl'))                    { [string]$pax['engineManifestUrl'] }                    else { $null }
    $extThumb               = if ($pax.Contains('engineManifestTrustAnchorThumbprint')) { $pax['engineManifestTrustAnchorThumbprint'] }          else { $null }
    $extSigPolicy           = if ($pax.Contains('manifestSignaturePolicy'))              { [string]$pax['manifestSignaturePolicy'] }              else { $null }

    return [pscustomobject]@{
        SchemaVersion                       = [int]$json['schemaVersion']
        Channel                             = [string]$json['channel']
        CookbookVersion                     = [string]$json['cookbook']['version']
        ReleaseTimestamp                    = [string]$json['cookbook']['releaseTimestamp']
        PaxScriptName                       = [string]$json['paxScript']['name']
        PaxScriptVersion                    = [string]$json['paxScript']['version']
        PaxScriptRelativePath               = [string]$json['paxScript']['relativePath']
        PaxScriptSha256                     = [string]$json['paxScript']['sha256']
        AcquisitionPolicy                   = $extAcquisitionPolicy
        ExportEnabled                       = $extExportEnabledRaw
        EngineManifestUrl                   = $extEngineManifestUrl
        EngineManifestTrustAnchorThumbprint = $extThumb
        ManifestSignaturePolicy             = $extSigPolicy
        HasExternalPolicyFields             = ($null -ne $extAcquisitionPolicy)
    }
}

function Test-HashtableLike {
    param($Value)
    return ($Value -is [hashtable] -or $Value -is [System.Collections.Specialized.OrderedDictionary])
}


# ---------------------------------------------------------------------
# Release metadata
# ---------------------------------------------------------------------
function New-ReleaseMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$VersionInfo,
        [Parameter(Mandatory)][string]$Channel,
        [Parameter(Mandatory)][string]$BuildId,
        [Parameter(Mandatory)][datetime]$BuiltAtUtc,
        [Parameter(Mandatory)][string]$BuiltOnHost,
        [string]$SourceCommit,
        [Parameter(Mandatory)][string]$PackageFile,
        [Parameter(Mandatory)][long]$PackageSizeBytes,
        [Parameter(Mandatory)][string]$PackageSha256,
        [Parameter(Mandatory)][int]$FileCount,
        [Parameter(Mandatory)][int]$ExclusionPatternCount,
        [string]$Notes,
        [ValidateSet('production','internal-test-signed','internal-test-unsigned','legacy-embedded')]
        [string]$Profile = 'legacy-embedded'
    )

    if (-not $Script:ReleaseProfileCatalog.ContainsKey($Profile)) {
        throw ('Unknown release profile: ' + $Profile)
    }
    $profileEntry = $Script:ReleaseProfileCatalog[$Profile]

    $signing = [ordered]@{
        state                = [string]$profileEntry.signingState
        profile              = $profileEntry.signingProfile
        verified             = $false
        signerCertThumbprint = $null
        signedAtUtc          = $null
        signatureAlgorithm   = $null
        sidecarFile          = $null
        notes                = [string]$profileEntry.notes
    }
    foreach ($k in $signing.Keys) {
        if ($Script:AllowedReleaseSigningKeys -notcontains $k) {
            throw ('Internal error: signing block has unknown key "' + $k + '".')
        }
    }

    $sourceCommitValue = if ([string]::IsNullOrWhiteSpace($SourceCommit)) { $null } else { $SourceCommit }

    # Compose the paxScript sub-block. Five external-policy fields are
    # emitted ONLY when VersionInfo carries them; legacy embedded
    # sources emit just the four base fields (back-compat).
    $paxScript = [ordered]@{
        name         = $VersionInfo.PaxScriptName
        version      = $VersionInfo.PaxScriptVersion
        relativePath = $VersionInfo.PaxScriptRelativePath
        sha256       = $VersionInfo.PaxScriptSha256.ToUpperInvariant()
    }
    if ($VersionInfo.HasExternalPolicyFields) {
        $paxScript['acquisitionPolicy']                   = $VersionInfo.AcquisitionPolicy
        $paxScript['exportEnabled']                       = $VersionInfo.ExportEnabled
        $paxScript['engineManifestUrl']                   = $VersionInfo.EngineManifestUrl
        $paxScript['engineManifestTrustAnchorThumbprint'] = $VersionInfo.EngineManifestTrustAnchorThumbprint
        $paxScript['manifestSignaturePolicy']             = $VersionInfo.ManifestSignaturePolicy
    }
    foreach ($k in $paxScript.Keys) {
        if ($Script:AllowedReleasePaxScriptKeys -notcontains $k) {
            throw ('Internal error: paxScript block has unknown key "' + $k + '".')
        }
    }

    $meta = [ordered]@{
        schemaVersion         = $Script:ReleaseMetadataSchemaVersion
        cookbookVersion       = $VersionInfo.CookbookVersion
        channel               = $Channel
        buildId               = $BuildId
        builtAtUtc            = $BuiltAtUtc.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        builtOnHost           = $BuiltOnHost
        sourceCommit          = $sourceCommitValue
        packageFile           = $PackageFile
        packageSizeBytes      = $PackageSizeBytes
        packageSha256         = $PackageSha256.ToUpperInvariant()
        manifestSchemaVersion = $VersionInfo.SchemaVersion
        paxScript             = $paxScript
        signing               = $signing
        fileCount             = $FileCount
        exclusionPatternCount = $ExclusionPatternCount
        publishable           = [bool]$profileEntry.publishable
        notes                 = $Notes
    }

    # Verify our own schema: every emitted top-level key must be in
    # the allow-list. This is a self-check guard.
    foreach ($k in $meta.Keys) {
        if ($Script:AllowedReleaseMetadataKeys -notcontains $k) {
            throw ('Internal error: release metadata has unknown key "' + $k + '".')
        }
    }
    return $meta
}


# ---------------------------------------------------------------------
# Update-manifest snapshot
# ---------------------------------------------------------------------
function New-ReleaseManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$VersionInfo,
        [Parameter(Mandatory)][string]$Channel,
        [Parameter(Mandatory)][datetime]$BuiltAtUtc,
        [Parameter(Mandatory)][string]$PackageFile,
        [Parameter(Mandatory)][string]$PackageSha256,
        [string]$PackageBaseUrl,
        [string]$ReleaseNotesUrl
    )

    $packageUrl = if ([string]::IsNullOrWhiteSpace($PackageBaseUrl)) {
        '<TODO_RELEASE_URL_PACKAGE_ZIP>'
    } else {
        ($PackageBaseUrl.TrimEnd('/') + '/' + $PackageFile)
    }
    $notesUrl = if ([string]::IsNullOrWhiteSpace($ReleaseNotesUrl)) {
        '<TODO_RELEASE_NOTES_URL>'
    } else {
        $ReleaseNotesUrl
    }

    $manifest = [ordered]@{
        schemaVersion    = $VersionInfo.SchemaVersion
        channel          = $Channel
        releaseTimestamp = $BuiltAtUtc.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        latestCookbook   = [ordered]@{
            version         = $VersionInfo.CookbookVersion
            packageUrl      = $packageUrl
            sha256          = $PackageSha256.ToUpperInvariant()
            releaseNotesUrl = $notesUrl
        }
        includedPaxScript = [ordered]@{
            name         = $VersionInfo.PaxScriptName
            version      = $VersionInfo.PaxScriptVersion
            relativePath = $VersionInfo.PaxScriptRelativePath
            sha256       = $VersionInfo.PaxScriptSha256.ToUpperInvariant()
        }
        compatibility = [ordered]@{
            minCookbookVersionForPaxScript    = $VersionInfo.CookbookVersion
            minimumCompatibleInstallerVersion = $VersionInfo.CookbookVersion
        }
    }

    # Mirror the five external-policy fields verbatim onto the
    # includedPaxScript block when VersionInfo carries them. The
    # broker's Test-UpdateManifestSchema validates the resulting
    # shape; Test-ReleaseExternalPolicyInvariants verifies VERSION
    # mirror agreement.
    if ($VersionInfo.HasExternalPolicyFields) {
        $manifest.includedPaxScript['acquisitionPolicy']                   = $VersionInfo.AcquisitionPolicy
        $manifest.includedPaxScript['exportEnabled']                       = $VersionInfo.ExportEnabled
        $manifest.includedPaxScript['engineManifestUrl']                   = $VersionInfo.EngineManifestUrl
        $manifest.includedPaxScript['engineManifestTrustAnchorThumbprint'] = $VersionInfo.EngineManifestTrustAnchorThumbprint
        $manifest.includedPaxScript['manifestSignaturePolicy']             = $VersionInfo.ManifestSignaturePolicy
    }
    foreach ($k in $manifest.includedPaxScript.Keys) {
        if ($Script:AllowedReleasePaxScriptKeys -notcontains $k) {
            throw ('Internal error: includedPaxScript block has unknown key "' + $k + '".')
        }
    }
    return $manifest
}


# ---------------------------------------------------------------------
# Deterministic ZIP creation
# ---------------------------------------------------------------------
function New-CanonicalZip {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string[]]$RelativePaths,
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][datetime]$EntryTimestampUtc
    )
    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }
    Add-Type -AssemblyName 'System.IO.Compression' -ErrorAction SilentlyContinue | Out-Null
    Add-Type -AssemblyName 'System.IO.Compression.FileSystem' -ErrorAction SilentlyContinue | Out-Null

    $absRepo = (Resolve-Path -LiteralPath $RepoRoot).Path.TrimEnd('\','/')
    # Stamp every entry with the same DOS-time-compatible timestamp,
    # converted to local kind because ZipArchiveEntry.LastWriteTime is
    # a DateTimeOffset internally and the ZIP DOS-time format has
    # 2-second resolution starting in 1980.
    $tsUtc = $EntryTimestampUtc.ToUniversalTime()
    if ($tsUtc.Year -lt 1980) {
        throw 'EntryTimestampUtc must be on or after 1980-01-01 for ZIP DOS time.'
    }
    # Round down to the nearest 2 seconds (DOS time resolution).
    $rounded = [datetime]::new(
        $tsUtc.Year, $tsUtc.Month, $tsUtc.Day,
        $tsUtc.Hour, $tsUtc.Minute,
        (([math]::Floor($tsUtc.Second / 2.0)) * 2),
        [DateTimeKind]::Utc
    )
    $entryDto = [DateTimeOffset]::new($rounded)

    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
    try {
        $zip = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Create
        )
        try {
            foreach ($rel in $RelativePaths) {
                $relForward = ($rel -replace '\\','/')
                $entry = $zip.CreateEntry($relForward, [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $entryDto
                $fileAbs = Join-Path $absRepo $rel
                $src  = [System.IO.File]::OpenRead($fileAbs)
                try {
                    $dst = $entry.Open()
                    try   { $src.CopyTo($dst) }
                    finally { $dst.Dispose() }
                } finally {
                    $src.Dispose()
                }
            }
        } finally {
            $zip.Dispose()
        }
    } finally {
        $stream.Dispose()
    }

    $info = Get-Item -LiteralPath $ZipPath
    $sha  = (Get-FileHash -Algorithm SHA256 -LiteralPath $ZipPath).Hash.ToUpperInvariant()
    return [pscustomobject]@{
        Path       = (Resolve-Path -LiteralPath $ZipPath).Path
        SizeBytes  = [long]$info.Length
        Sha256     = $sha
        EntryCount = $RelativePaths.Count
    }
}


# ---------------------------------------------------------------------
# SHA sidecar (sha256sum format)
# ---------------------------------------------------------------------
function Write-Sha256Sidecar {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$Sha256
    )
    $sidecarPath = $PackagePath + '.sha256'
    $line = ($Sha256.ToUpperInvariant() + '  ' + (Split-Path -Leaf $PackagePath))
    # Use UTF-8 *without* BOM so the sidecar matches sha256sum tooling
    # byte-for-byte on Linux verifiers.
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($line + "`n")
    [System.IO.File]::WriteAllBytes($sidecarPath, $bytes)
    return $sidecarPath
}


# ---------------------------------------------------------------------
# Release profile catalog accessor
# ---------------------------------------------------------------------
function Get-ReleaseProfileCatalog {
    # Returns a shallow clone of the closed profile catalog. Callers
    # must not mutate the returned table.
    $clone = @{}
    foreach ($k in $Script:ReleaseProfileCatalog.Keys) {
        $entry = $Script:ReleaseProfileCatalog[$k]
        $copy = @{}
        foreach ($ek in $entry.Keys) { $copy[$ek] = $entry[$ek] }
        $clone[$k] = $copy
    }
    return $clone
}


# ---------------------------------------------------------------------
# External-policy fail-closed validator (PLAN 6.1 invariants)
# ---------------------------------------------------------------------
#
# Single entry point. Returns a structured hashtable:
#   @{ ok = $true/$false; profile = '<name>'; skipped = $true/$false;
#      failures = @( @{ code='<token>'; message='<text>' }, ... );
#      warnings = @( '<text>', ... );
#      checks   = @( @{ code='<token>'; ok = $bool; detail='<text>' }, ... ) }
#
# - Profile selection: when -Profile is 'auto', the function infers the
#   profile from VersionInfo.ManifestSignaturePolicy + presence of
#   external fields. 'required' + external -> 'production'. 'internal-
#   test-bypass' + external -> 'internal-test-unsigned'. Embedded source
#   -> 'legacy-embedded' (gates skipped with skipped=$true).
# - VersionInfo and ManifestSnapshot must always be provided.
# - PackageRelativePaths is optional. When omitted, package-content
#   invariants are not run.
# - SourceRepoRoot is optional. When omitted, source-grep invariants
#   are not run (the fixture smoke uses synthetic trees; the live
#   Build-Release run always provides the real repo root).
#
function Test-ReleaseExternalPolicyInvariants {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$VersionInfo,
        [Parameter(Mandatory)][hashtable]$ManifestSnapshot,
        [string[]]$PackageRelativePaths,
        [string]$SourceRepoRoot,
        [ValidateSet('auto','production','internal-test-signed','internal-test-unsigned','legacy-embedded')]
        [string]$Profile = 'auto'
    )

    $failures = New-Object System.Collections.Generic.List[hashtable]
    $warnings = New-Object System.Collections.Generic.List[string]
    $checks   = New-Object System.Collections.Generic.List[hashtable]

    function Add-Check {
        param([string]$Code, [bool]$Ok, [string]$Detail = '')
        $checks.Add(@{ code = $Code; ok = $Ok; detail = $Detail })
    }
    function Add-Failure {
        param([string]$Code, [string]$Message)
        $failures.Add(@{ code = $Code; message = $Message })
        Add-Check -Code $Code -Ok $false -Detail $Message
    }

    # ------------------------------------------------------------------
    # Profile resolution
    # ------------------------------------------------------------------
    $effectiveProfile = $Profile
    if ($effectiveProfile -eq 'auto') {
        if (-not $VersionInfo.HasExternalPolicyFields) {
            $effectiveProfile = 'legacy-embedded'
        } elseif ([string]$VersionInfo.ManifestSignaturePolicy -eq 'internal-test-bypass') {
            $effectiveProfile = 'internal-test-unsigned'
        } else {
            $effectiveProfile = 'production'
        }
    }

    if (-not $Script:ReleaseProfileCatalog.ContainsKey($effectiveProfile)) {
        Add-Failure 'unknown_profile' ('Unknown release profile: ' + $effectiveProfile)
        return @{ ok = $false; profile = $effectiveProfile; skipped = $false; failures = @($failures); warnings = @($warnings); checks = @($checks) }
    }
    $profileEntry = $Script:ReleaseProfileCatalog[$effectiveProfile]

    # Legacy embedded -> skip all external invariants.
    if ($effectiveProfile -eq 'legacy-embedded') {
        Add-Check -Code 'legacy_embedded_skip' -Ok $true -Detail 'Source VERSION.json carries no acquisitionPolicy field; external-policy gates do not apply.'
        return @{ ok = $true; profile = $effectiveProfile; skipped = $true; failures = @(); warnings = @($warnings); checks = @($checks) }
    }

    # ------------------------------------------------------------------
    # PLAN 6.1 -- Policy presence (VERSION.json side)
    # ------------------------------------------------------------------
    if ([string]$VersionInfo.AcquisitionPolicy -ne 'external') {
        Add-Failure 'version_policy_not_external' ('VERSION.json.paxScript.acquisitionPolicy must equal "external" for an external-profile build (got "' + [string]$VersionInfo.AcquisitionPolicy + '").')
    } else {
        Add-Check -Code 'version_policy_external' -Ok $true
    }

    # ------------------------------------------------------------------
    # Manifest snapshot shape gate
    # ------------------------------------------------------------------
    if (-not $ManifestSnapshot.Contains('includedPaxScript')) {
        Add-Failure 'manifest_no_included_pax_script' 'ManifestSnapshot is missing "includedPaxScript".'
        return @{ ok = $false; profile = $effectiveProfile; skipped = $false; failures = @($failures); warnings = @($warnings); checks = @($checks) }
    }
    $mip = $ManifestSnapshot['includedPaxScript']
    if (-not (Test-HashtableLike $mip)) {
        Add-Failure 'manifest_included_pax_script_shape' 'ManifestSnapshot.includedPaxScript is not a hashtable.'
        return @{ ok = $false; profile = $effectiveProfile; skipped = $false; failures = @($failures); warnings = @($warnings); checks = @($checks) }
    }

    # ------------------------------------------------------------------
    # PLAN 6.1 -- Manifest mirror (five fields agree)
    # ------------------------------------------------------------------
    $mirrorPairs = @(
        @{ field = 'acquisitionPolicy';                   versionValue = $VersionInfo.AcquisitionPolicy }
        @{ field = 'exportEnabled';                       versionValue = $VersionInfo.ExportEnabled }
        @{ field = 'engineManifestUrl';                   versionValue = $VersionInfo.EngineManifestUrl }
        @{ field = 'engineManifestTrustAnchorThumbprint'; versionValue = $VersionInfo.EngineManifestTrustAnchorThumbprint }
        @{ field = 'manifestSignaturePolicy';             versionValue = $VersionInfo.ManifestSignaturePolicy }
    )
    foreach ($p in $mirrorPairs) {
        $hasField = $mip.Contains($p.field)
        $manifestValue = if ($hasField) { $mip[$p.field] } else { $null }
        $vv = $p.versionValue
        if (($null -eq $manifestValue -and $null -ne $vv) -or
            ($null -ne $manifestValue -and $null -eq $vv) -or
            ($null -ne $manifestValue -and $null -ne $vv -and ([string]$manifestValue -ne [string]$vv))) {
            Add-Failure ('manifest_mirror_mismatch_' + $p.field) ('manifest.includedPaxScript.' + $p.field + ' = "' + [string]$manifestValue + '" does NOT match VERSION.json.paxScript.' + $p.field + ' = "' + [string]$vv + '".')
        } else {
            Add-Check -Code ('manifest_mirror_' + $p.field) -Ok $true
        }
    }

    # ------------------------------------------------------------------
    # PLAN 6.1 -- manifestSignaturePolicy enum + profile alignment
    # ------------------------------------------------------------------
    $sigPolicy = [string]$VersionInfo.ManifestSignaturePolicy
    if ([string]::IsNullOrWhiteSpace($sigPolicy)) {
        Add-Failure 'missing_manifest_signature_policy' 'VERSION.json.paxScript.manifestSignaturePolicy is required under acquisitionPolicy="external".'
    } elseif ($Script:ReleaseAllowedManifestSignaturePolicies -notcontains $sigPolicy) {
        Add-Failure 'invalid_manifest_signature_policy' ('manifestSignaturePolicy must be one of: ' + ($Script:ReleaseAllowedManifestSignaturePolicies -join ', ') + '. Got "' + $sigPolicy + '".')
    } else {
        $expectedForProfile = [string]$profileEntry.manifestSignaturePolicy
        if ($sigPolicy -ne $expectedForProfile) {
            if ($effectiveProfile -eq 'production' -and $sigPolicy -eq 'internal-test-bypass') {
                Add-Failure 'production_build_refuses_internal_test_bypass' 'VERSION.json.paxScript.manifestSignaturePolicy must be "required" for a production build (saw "internal-test-bypass"). This artifact cannot be customer-facing.'
            } else {
                Add-Failure 'manifest_signature_policy_profile_mismatch' ('manifestSignaturePolicy "' + $sigPolicy + '" does not match the required value for profile "' + $effectiveProfile + '" ("' + $expectedForProfile + '").')
            }
        } else {
            Add-Check -Code 'manifest_signature_policy_aligned' -Ok $true
        }
    }

    # ------------------------------------------------------------------
    # PLAN 6.1 -- HTTPS URL + placeholder rejection
    # ------------------------------------------------------------------
    $url = [string]$VersionInfo.EngineManifestUrl
    if ([string]::IsNullOrWhiteSpace($url)) {
        Add-Failure 'missing_engine_manifest_url' 'VERSION.json.paxScript.engineManifestUrl is required.'
    } else {
        # Placeholder regex applies in ALL profiles (PLAN 6.3).
        if ($url -cmatch $Script:ReleasePlaceholderRegex) {
            Add-Failure 'engine_manifest_url_placeholder' ('engineManifestUrl is a placeholder ("' + $url + '"). Placeholders are rejected in all profiles.')
        } else {
            $uri = $null
            $parsed = [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri)
            if (-not $parsed) {
                Add-Failure 'engine_manifest_url_malformed' ('engineManifestUrl is not a well-formed absolute URI: "' + $url + '".')
            } else {
                $scheme = $uri.Scheme.ToLowerInvariant()
                if ($scheme -eq 'https') {
                    Add-Check -Code 'engine_manifest_url_https' -Ok $true
                } elseif ($scheme -eq 'http' -and $uri.IsLoopback -and $profileEntry.allowLoopbackHttp) {
                    Add-Check -Code 'engine_manifest_url_loopback_http_allowed' -Ok $true -Detail 'Loopback HTTP URL accepted under internal-test profile.'
                } else {
                    Add-Failure 'engine_manifest_url_scheme_forbidden' ('engineManifestUrl must use HTTPS (got "' + $scheme + '"). Loopback HTTP is permitted only under internal-test profiles.')
                }
            }
        }
    }

    # ------------------------------------------------------------------
    # PLAN 6.1 -- Thumbprint format + nullable-under-bypass rule
    # ------------------------------------------------------------------
    $thumbVal     = $VersionInfo.EngineManifestTrustAnchorThumbprint
    $thumbIsNull  = ($null -eq $thumbVal)
    $thumbStr     = if ($thumbIsNull) { '' } else { [string]$thumbVal }
    # Placeholder rejection BEFORE null handling (so "<TODO_THUMB>" fails
    # even under bypass).
    if (-not $thumbIsNull -and $thumbStr -cmatch $Script:ReleasePlaceholderRegex) {
        Add-Failure 'engine_manifest_trust_anchor_thumbprint_placeholder' ('engineManifestTrustAnchorThumbprint is a placeholder ("' + $thumbStr + '"). Placeholders are rejected in all profiles.')
    } elseif ($effectiveProfile -eq 'internal-test-unsigned') {
        # Null OR 40-hex accepted.
        if ($thumbIsNull) {
            Add-Check -Code 'engine_manifest_trust_anchor_thumbprint_null_under_bypass' -Ok $true
        } else {
            $clean = ($thumbStr -replace '[^0-9A-Fa-f]', '')
            if ($clean.Length -ne 40) {
                Add-Failure 'engine_manifest_trust_anchor_thumbprint_invalid' ('engineManifestTrustAnchorThumbprint must be a 40-hex SHA-1 thumbprint or literal null under internal-test-unsigned (got "' + $thumbStr + '").')
            } else {
                Add-Check -Code 'engine_manifest_trust_anchor_thumbprint_40hex' -Ok $true
            }
        }
    } else {
        if ($thumbIsNull -or [string]::IsNullOrWhiteSpace($thumbStr)) {
            Add-Failure 'missing_engine_manifest_trust_anchor_thumbprint' 'engineManifestTrustAnchorThumbprint is required (40-hex SHA-1) under production / internal-test-signed profiles.'
        } else {
            $clean = ($thumbStr -replace '[^0-9A-Fa-f]', '')
            if ($clean.Length -ne 40) {
                Add-Failure 'engine_manifest_trust_anchor_thumbprint_invalid' ('engineManifestTrustAnchorThumbprint must be a 40-hex SHA-1 thumbprint (got "' + $thumbStr + '").')
            } else {
                Add-Check -Code 'engine_manifest_trust_anchor_thumbprint_40hex' -Ok $true
            }
        }
    }

    # ------------------------------------------------------------------
    # PLAN 6.1 -- exportEnabled must be present AND false
    # ------------------------------------------------------------------
    $exportVal = $VersionInfo.ExportEnabled
    if ($null -eq $exportVal) {
        Add-Failure 'missing_export_enabled' 'VERSION.json.paxScript.exportEnabled is required under acquisitionPolicy="external".'
    } elseif ($exportVal -isnot [bool]) {
        Add-Failure 'export_enabled_not_bool' ('VERSION.json.paxScript.exportEnabled must be a JSON boolean (got type "' + $exportVal.GetType().FullName + '").')
    } elseif ($exportVal) {
        Add-Failure 'export_enabled_true_forbidden' 'VERSION.json.paxScript.exportEnabled must be false. There is no PAX-script export route.'
    } else {
        Add-Check -Code 'export_enabled_false' -Ok $true
    }

    # ------------------------------------------------------------------
    # PLAN 6.1 -- Package-content invariants (only when paths supplied)
    # ------------------------------------------------------------------
    if ($PSBoundParameters.ContainsKey('PackageRelativePaths') -and $null -ne $PackageRelativePaths) {
        $paths = @($PackageRelativePaths | ForEach-Object { ($_ -replace '\\','/').TrimStart('/') })

        # Script absent (PLAN 6.1 "Script absent" + "No pax/ directory")
        $paxHits = @($paths | Where-Object { $_ -match '(?i)^app/resources/pax/PAX_Purview_Audit_Log_Processor\.ps1$' })
        if (@($paxHits).Count -gt 0) {
            Add-Failure 'pax_script_present_in_package' ('PAX script must NOT be in the external-policy ZIP. Found: ' + ($paxHits -join ', '))
        } else {
            Add-Check -Code 'pax_script_absent' -Ok $true
        }
        # Scoped to ^app/resources/pax(/|$) to match the walker rule;
        # the broker's own app/broker/Pax/Adapter.psm1 adapter module
        # is a required startup dependency, not the bundled PAX script.
        $paxDirHits = @($paths | Where-Object { $_ -match '(?i)^app/resources/pax(/|$)' -or $_ -match '(?i)(^|/)PAX_Purview_[^/]+$' })
        if (@($paxDirHits).Count -gt 0) {
            Add-Failure 'pax_directory_present_in_package' ('No app/resources/pax/ directory or PAX_Purview_* file may exist in an external-policy ZIP. Found: ' + ($paxDirHits -join ', '))
        } else {
            Add-Check -Code 'no_pax_directory' -Ok $true
        }

        # Required acquisition routes
        $requiredAcquisitionPaths = @(
            'app/broker/Routes/Setup.ps1',
            'app/broker/Engine/Acquisition.psm1'
        )
        foreach ($req in $requiredAcquisitionPaths) {
            $found = @($paths | Where-Object { $_ -ieq $req })
            if (@($found).Count -eq 0) {
                Add-Failure 'missing_required_acquisition_path' ('Required acquisition path missing from package: ' + $req)
            } else {
                Add-Check -Code ('present_' + ($req -replace '[^A-Za-z0-9]', '_')) -Ok $true
            }
        }

        # _temp / _archive / dist / fixtures must not leak
        $leakHits = @($paths | Where-Object { $_ -match '(?i)(^|/)(_temp|_archive|dist|fixtures|scripts|_backup)(/|$)' })
        if (@($leakHits).Count -gt 0) {
            Add-Failure 'temp_leakage_in_package' ('Package contains forbidden dev directory entries: ' + ($leakHits -join ', '))
        } else {
            Add-Check -Code 'no_temp_or_archive_leak' -Ok $true
        }

        # Signature artifact presence vs policy (PLAN 6.1 "Signature
        # artifact presence matches policy")
        $sigArtifacts = @($paths | Where-Object { $_ -match '(?i)\.sig$' -or $_ -match '(?i)\.signer\.json$' })
        if ($effectiveProfile -eq 'internal-test-unsigned') {
            if (@($sigArtifacts).Count -gt 0) {
                Add-Failure 'unsigned_profile_includes_signature_artifact' ('Internal-test unsigned build MUST NOT include any .sig / .signer.json artifacts. Found: ' + ($sigArtifacts -join ', '))
            } else {
                Add-Check -Code 'unsigned_profile_no_sig_artifacts' -Ok $true
            }
        } else {
            Add-Check -Code 'signed_profile_sig_artifacts_count' -Ok $true -Detail ('count=' + @($sigArtifacts).Count + ' (signing performed post-build by Sign-Release.ps1)')
        }

        # Misleading-embedded check (defense-in-depth): no VERSION.json
        # inside the package may declare acquisitionPolicy=embedded.
        $versionFiles = @($paths | Where-Object { $_ -match '(?i)(^|/)VERSION\.json$' })
        foreach ($vf in $versionFiles) {
            # We only have the path list here; the smoke harness performs
            # the content check separately when staging fixtures.
            Add-Check -Code 'version_file_path_present' -Ok $true -Detail $vf
        }
    }

    # ------------------------------------------------------------------
    # PLAN 6.1 -- Source-grep invariants (only when source root supplied)
    # ------------------------------------------------------------------
    if ($PSBoundParameters.ContainsKey('SourceRepoRoot') -and -not [string]::IsNullOrWhiteSpace($SourceRepoRoot)) {
        $sourceRootAbs = (Resolve-Path -LiteralPath $SourceRepoRoot).Path
        $appDir   = Join-Path $sourceRootAbs 'app'
        $brokerDir   = Join-Path $appDir 'broker'
        $installerSrc = Join-Path $appDir 'install\Install-PAXCookbook.ps1'
        $webDir   = Join-Path $appDir 'web'

        $psFiles = New-Object System.Collections.Generic.List[string]
        if (Test-Path -LiteralPath $brokerDir) {
            Get-ChildItem -LiteralPath $brokerDir -Recurse -File -Force -Include *.ps1,*.psm1 |
                ForEach-Object { $psFiles.Add($_.FullName) }
        }
        if (Test-Path -LiteralPath $installerSrc) { $psFiles.Add($installerSrc) }

        $jsFiles = New-Object System.Collections.Generic.List[string]
        if (Test-Path -LiteralPath $webDir) {
            Get-ChildItem -LiteralPath $webDir -Recurse -File -Force -Include *.js,*.html |
                ForEach-Object { $jsFiles.Add($_.FullName) }
        }

        function Get-ReleasePsNonCommentText {
            param([string]$Path)
            $tokens = $null; $errs = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errs)
            if (-not $tokens) { return '' }
            $nonComment = $tokens | Where-Object { $_.Kind -ne 'Comment' }
            return (($nonComment | ForEach-Object { $_.Text }) -join ' ')
        }
        function Get-ReleaseJsNonCommentText {
            param([string]$Path)
            $raw = [System.IO.File]::ReadAllText($Path)
            $stripped = [regex]::Replace($raw, '/\*[\s\S]*?\*/', ' ')
            $stripped = [regex]::Replace($stripped, '(?m)//[^\r\n]*$', ' ')
            return $stripped
        }

        $exportHits   = New-Object System.Collections.Generic.List[string]
        $sideloadHits = New-Object System.Collections.Generic.List[string]
        $useAnyHits   = New-Object System.Collections.Generic.List[string]

        foreach ($f in $psFiles) {
            $text = Get-ReleasePsNonCommentText -Path $f
            foreach ($tok in $Script:ReleaseForbiddenExportTokens) {
                if ($text -match $tok) { $exportHits.Add($f + ' :: ' + $tok) }
            }
            foreach ($tok in $Script:ReleaseForbiddenSideloadTokens) {
                if ($text -match $tok) { $sideloadHits.Add($f + ' :: ' + $tok) }
            }
            foreach ($tok in $Script:ReleaseForbiddenUseAnywayTokens) {
                if ($text -match $tok) { $useAnyHits.Add($f + ' :: ' + $tok) }
            }
        }
        foreach ($f in $jsFiles) {
            $text = Get-ReleaseJsNonCommentText -Path $f
            foreach ($tok in $Script:ReleaseForbiddenExportTokens) {
                if ($text -match $tok) { $exportHits.Add($f + ' :: ' + $tok) }
            }
            foreach ($tok in $Script:ReleaseForbiddenUseAnywayTokens) {
                if ($text -match $tok) { $useAnyHits.Add($f + ' :: ' + $tok) }
            }
        }

        if (@($exportHits).Count -gt 0) {
            Add-Failure 'export_route_token_present' ('Forbidden export-route tokens found in source: ' + ($exportHits -join '; '))
        } else {
            Add-Check -Code 'no_export_route_tokens' -Ok $true -Detail ('Scanned ' + $psFiles.Count + ' PS files, ' + $jsFiles.Count + ' JS/HTML files.')
        }
        if (@($sideloadHits).Count -gt 0) {
            Add-Failure 'sideload_token_present' ('Forbidden sideload tokens found in source: ' + ($sideloadHits -join '; '))
        } else {
            Add-Check -Code 'no_sideload_tokens' -Ok $true
        }
        if (@($useAnyHits).Count -gt 0) {
            Add-Failure 'use_anyway_token_present' ('Forbidden runtime-bypass tokens found in source: ' + ($useAnyHits -join '; '))
        } else {
            Add-Check -Code 'no_use_anyway_tokens' -Ok $true
        }
    }

    $okFinal = (@($failures).Count -eq 0)
    return @{
        ok       = $okFinal
        profile  = $effectiveProfile
        skipped  = $false
        failures = @($failures)
        warnings = @($warnings)
        checks   = @($checks)
    }
}


# ---------------------------------------------------------------------
# Publish-gate check (used by Publish-Release.ps1)
# ---------------------------------------------------------------------
#
# Reads a <pkg>.release.json sidecar from disk and returns whether the
# artifact is publishable per PLAN 6.1 "Publish gate refuses non-signed
# artifacts". Returns:
#   @{ ok = $true/$false; reason = '<token>'; message = '<text>' }
#
function Test-ReleasePublishable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ReleaseJsonPath
    )
    if (-not (Test-Path -LiteralPath $ReleaseJsonPath -PathType Leaf)) {
        return @{ ok = $false; reason = 'release_json_missing'; message = ('release.json not found at: ' + $ReleaseJsonPath) }
    }
    $raw = Get-Content -LiteralPath $ReleaseJsonPath -Raw -ErrorAction Stop
    $body = $null
    try {
        $body = $raw | ConvertFrom-Json -AsHashtable -Depth 12
    } catch {
        return @{ ok = $false; reason = 'release_json_unparseable'; message = ('release.json unparseable: ' + $_.Exception.Message) }
    }
    if (-not (Test-HashtableLike $body)) {
        return @{ ok = $false; reason = 'release_json_not_object'; message = 'release.json top-level value is not a JSON object.' }
    }

    # Validate top-level allow-list (closed schema).
    foreach ($k in @($body.Keys)) {
        if ($Script:AllowedReleaseMetadataKeys -notcontains $k) {
            return @{ ok = $false; reason = 'release_json_unknown_field'; message = ('release.json has unknown top-level field: "' + $k + '".') }
        }
    }

    if (-not $body.Contains('publishable') -or $body['publishable'] -isnot [bool] -or -not $body['publishable']) {
        return @{ ok = $false; reason = 'not_publishable'; message = 'release.json.publishable is not literal true.' }
    }
    if (-not $body.Contains('signing') -or -not (Test-HashtableLike $body['signing'])) {
        return @{ ok = $false; reason = 'signing_block_missing'; message = 'release.json.signing block missing or malformed.' }
    }
    $sig = $body['signing']
    $state   = if ($sig.Contains('state'))   { [string]$sig['state'] }   else { '' }
    $profile = if ($sig.Contains('profile')) { [string]$sig['profile'] } else { '' }
    if ($state -ne 'signed') {
        return @{ ok = $false; reason = 'signing_state_not_signed'; message = ('release.json.signing.state must be "signed" for publish (got "' + $state + '").') }
    }
    if ($profile -ne 'production') {
        return @{ ok = $false; reason = 'signing_profile_not_production'; message = ('release.json.signing.profile must be "production" for publish (got "' + $profile + '").') }
    }

    # Placeholder fields anywhere in paxScript top-level reject publish.
    if ($body.Contains('paxScript') -and (Test-HashtableLike $body['paxScript'])) {
        $pax = $body['paxScript']
        foreach ($k in @($pax.Keys)) {
            $v = $pax[$k]
            if ($null -ne $v -and $v -is [string] -and $v -cmatch $Script:ReleasePlaceholderRegex) {
                return @{ ok = $false; reason = 'placeholder_field_in_pax_script'; message = ('release.json.paxScript.' + $k + ' is a placeholder ("' + $v + '").') }
            }
        }
    }

    return @{ ok = $true; reason = $null; message = 'Artifact is publishable.' }
}


Export-ModuleMember -Function `
    Get-ReleaseIncludeRoots, `
    Get-ReleaseIncludeTopLevelFiles, `
    Get-ReleaseExclusionPatterns, `
    Test-ReleaseExclusion, `
    Get-ReleaseFileSet, `
    Get-ReleaseVersionInfo, `
    New-ReleaseMetadata, `
    New-ReleaseManifest, `
    New-CanonicalZip, `
    Write-Sha256Sidecar, `
    Get-ReleaseProfileCatalog, `
    Test-ReleaseExternalPolicyInvariants, `
    Test-ReleasePublishable
