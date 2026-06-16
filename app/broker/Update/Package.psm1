#requires -Version 7.4

# Package.psm1
#
# Cookbook update package download, SHA-256 verification, and staging.
#
# Exposes:
#   Get-UpdatePackageStagingDir  - Returns the canonical Updates folder
#                                  under the workspace. Creates it on
#                                  first call. Pure path arithmetic +
#                                  best-effort directory create; no
#                                  network.
#
#   Save-UpdatePackage           - Downloads exactly ONE update package
#                                  from the supplied URL, verifies its
#                                  SHA-256 against the supplied hex
#                                  digest, and stages it into the
#                                  workspace Updates folder. Returns a
#                                  structured result.
#
#   Get-StagedPackageInventory   - Lists every staged package currently
#                                  present under the workspace Updates
#                                  folder, surfacing the .metadata.json
#                                  sidecar content for each. Filesystem
#                                  read only; no network.
#
# The choke-point for outbound HTTPS in this module is Save-UpdatePackage.
# Manifest.psm1 owns the other choke-point (manifest fetch). No other
# broker code performs outbound HTTPS.
#
# Forbidden by design (do NOT add):
#   - automatic extraction of the staged zip
#   - automatic invocation of the installer
#   - automatic broker restart
#   - automatic deletion of older staged packages
#   - parallel/resumable downloads
#   - background polling
#   - any "self-update" pathway

Set-StrictMode -Version Latest

# Manifest.psm1 owns URL syntax validation. Package downloads must
# re-assert the same rule at the choke-point, so we import it here.
Import-Module -Force (Join-Path $PSScriptRoot 'Manifest.psm1')

# 250 MB cap. Real Cookbook release packages are a small fraction of
# this; the ceiling prevents pathological behaviour from a misconfigured
# or hostile manifest URL.
$Script:MaxPackageBytes  = 250 * 1024 * 1024
$Script:PackageTimeoutSec = 600

# Conservative filename allow-pattern. The manifest controls the
# filename, but Cookbook also re-asserts it. Only ASCII letters, digits,
# dot, dash, underscore; must end in ".zip".
$Script:AllowedFilenameRegex = '^[A-Za-z0-9._-]{1,160}\.zip$'

function Get-UpdatePackageStagingDir {
    param([Parameter(Mandatory)][string]$WorkspacePath)
    $dir = Join-Path $WorkspacePath 'Updates'
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    return $dir
}

function Get-FilenameFromUrlOrManifest {
    # The manifest does not currently carry a separate "filename" field;
    # the filename is derived from the last segment of packageUrl. This
    # keeps the manifest small and unambiguous. The derived filename is
    # validated against the conservative allow-pattern.
    param([Parameter(Mandatory)][string]$PackageUrl)
    $uri = [Uri]::new($PackageUrl, [UriKind]::Absolute)
    $segments = $uri.Segments
    if (-not $segments -or $segments.Count -eq 0) { return $null }
    $raw = ($segments[-1]).Trim('/')
    # URL-decode (just in case). Disallow path-traversal sequences.
    $name = [Uri]::UnescapeDataString($raw)
    if ($name -match '\.\.|/|\\') { return $null }
    if ($name -notmatch $Script:AllowedFilenameRegex) { return $null }
    return $name
}

function Save-UpdatePackage {
    param(
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter(Mandatory)][string]$PackageUrl,
        [Parameter(Mandatory)][string]$ExpectedSha256,
        [Parameter(Mandatory)][string]$ExpectedCookbookVersion,
        [Parameter(Mandatory)][string]$CookbookVersion,
        [Parameter(Mandatory)]$ManifestSnapshot
    )

    # URL must pass the same validation the manifest URL uses (HTTPS or
    # loopback HTTP). This is independent of any check the manifest
    # body may have already passed -- we re-validate at the choke-point
    # so a code-path that bypasses the validator can never sneak through.
    $urlCheck = Test-UpdateManifestUrl -Url $PackageUrl
    if (-not $urlCheck.ok) {
        return @{ ok = $false; error = ('package_url_' + $urlCheck.error); message = ('Package URL rejected: ' + $urlCheck.message) }
    }

    if ($ExpectedSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        return @{ ok = $false; error = 'invalid_sha256'; message = 'ExpectedSha256 must be a 64-character hex string.' }
    }

    $filename = Get-FilenameFromUrlOrManifest -PackageUrl $PackageUrl
    if (-not $filename) {
        return @{
            ok      = $false
            error   = 'malformed_filename'
            message = 'Could not derive a safe filename from the package URL.'
        }
    }

    $stagingDir = Get-UpdatePackageStagingDir -WorkspacePath $WorkspacePath
    $finalPath    = Join-Path $stagingDir $filename
    $partialPath  = $finalPath + '.partial'
    $metadataPath = $finalPath + '.metadata.json'

    # If a fully-staged copy already matches the requested SHA-256,
    # treat this as idempotent success and DO NOT re-download.
    if (Test-Path -LiteralPath $finalPath -PathType Leaf) {
        $existingHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $finalPath).Hash.ToUpperInvariant()
        if ($existingHash -eq $ExpectedSha256.ToUpperInvariant()) {
            return @{
                ok            = $true
                alreadyStaged = $true
                filename      = $filename
                path          = $finalPath
                sha256        = $existingHash
                sizeBytes     = (Get-Item -LiteralPath $finalPath).Length
                metadataPath  = $metadataPath
            }
        }
        # Fall through: refuse to overwrite a file with the wrong hash
        # in the staging slot. The operator must move/delete it.
        return @{
            ok      = $false
            error   = 'staging_slot_occupied'
            message = ('A different file with the same name already occupies the staging slot: ' + $finalPath)
        }
    }

    # Clean any leftover partial from a previous failed attempt.
    if (Test-Path -LiteralPath $partialPath -PathType Leaf) {
        Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
    }

    $userAgent = 'PAXCookbook/' + $CookbookVersion + ' (package-download)'

    # Single deterministic GET, no redirect-follow, hard timeout, stream
    # to disk. Invoke-WebRequest with -OutFile writes directly to disk
    # without buffering the whole body in memory.
    try {
        Invoke-WebRequest `
            -Uri $urlCheck.uri.AbsoluteUri `
            -Method GET `
            -UserAgent $userAgent `
            -UseBasicParsing `
            -MaximumRedirection 0 `
            -TimeoutSec $Script:PackageTimeoutSec `
            -OutFile $partialPath `
            -ErrorAction Stop
    } catch {
        if (Test-Path -LiteralPath $partialPath -PathType Leaf) {
            Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
        }
        return @{
            ok      = $false
            error   = 'download_failed'
            message = ('Package download failed: ' + $_.Exception.Message)
        }
    }

    if (-not (Test-Path -LiteralPath $partialPath -PathType Leaf)) {
        return @{ ok = $false; error = 'download_failed'; message = 'Package download did not produce a file.' }
    }

    $size = (Get-Item -LiteralPath $partialPath).Length
    if ($size -le 0) {
        Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
        return @{ ok = $false; error = 'empty_download'; message = 'Package download produced a zero-byte file.' }
    }
    if ($size -gt $Script:MaxPackageBytes) {
        Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
        return @{
            ok      = $false
            error   = 'package_too_large'
            message = ('Package exceeds ' + $Script:MaxPackageBytes + ' bytes (' + $size + ').')
        }
    }

    # SHA-256 verification. Reject on any mismatch and remove the partial.
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $partialPath).Hash.ToUpperInvariant()
    $expectedHash = $ExpectedSha256.ToUpperInvariant()
    if ($actualHash -ne $expectedHash) {
        Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
        return @{
            ok       = $false
            error    = 'sha256_mismatch'
            message  = 'Downloaded package SHA-256 does not match the manifest. The file was discarded.'
            expected = $expectedHash
            actual   = $actualHash
        }
    }

    # Atomic rename to the final filename.
    try {
        Move-Item -LiteralPath $partialPath -Destination $finalPath -Force -ErrorAction Stop
    } catch {
        Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
        return @{ ok = $false; error = 'stage_rename_failed'; message = ('Could not finalize staged package: ' + $_.Exception.Message) }
    }

    # Sidecar metadata. Includes the manifest snapshot, the verified
    # hash, and the file size. Stays alongside the staged package so
    # the operator can confirm provenance without re-fetching the
    # manifest.
    $metadata = [ordered]@{
        cookbookVersionAtDownload = $CookbookVersion
        downloadedAtUtc           = (Get-Date).ToUniversalTime().ToString('o')
        sourceUrl                 = $urlCheck.uri.AbsoluteUri
        filename                  = $filename
        sizeBytes                 = $size
        sha256                    = $actualHash
        expectedCookbookVersion   = $ExpectedCookbookVersion
        manifestSnapshot          = $ManifestSnapshot
    }
    try {
        ($metadata | ConvertTo-Json -Depth 12) |
            Set-Content -LiteralPath $metadataPath -Encoding utf8 -ErrorAction Stop
    } catch {
        # Metadata sidecar write failure is non-fatal: the staged package
        # is still verifiable from its own hash. Surface the issue but
        # don't undo the staging.
        return @{
            ok            = $true
            alreadyStaged = $false
            filename      = $filename
            path          = $finalPath
            sha256        = $actualHash
            sizeBytes     = $size
            metadataPath  = $null
            warning       = ('Staged file is verifiable, but the metadata sidecar could not be written: ' + $_.Exception.Message)
        }
    }

    return @{
        ok            = $true
        alreadyStaged = $false
        filename      = $filename
        path          = $finalPath
        sha256        = $actualHash
        sizeBytes     = $size
        metadataPath  = $metadataPath
    }
}

function Get-StagedPackageInventory {
    param([Parameter(Mandatory)][string]$WorkspacePath)

    $stagingDir = Get-UpdatePackageStagingDir -WorkspacePath $WorkspacePath
    $items = @()

    # Trust.psm1 may or may not be loaded depending on which test
    # surface called us. We attach trust state opportunistically so
    # tests that import only Package.psm1 keep working, and the
    # production broker (which loads both modules) gets the full
    # truthful trust block on every staged item.
    $trustAvailable = $null -ne (Get-Command -Name 'Get-PackageTrustState' -ErrorAction SilentlyContinue)

    Get-ChildItem -LiteralPath $stagingDir -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match $Script:AllowedFilenameRegex
    } | ForEach-Object {
        $pkg = $_.FullName
        $meta = $pkg + '.metadata.json'
        $entry = [ordered]@{
            filename     = $_.Name
            path         = $pkg
            sizeBytes    = $_.Length
            modifiedUtc  = $_.LastWriteTimeUtc.ToString('o')
            metadataPath = $null
            metadata     = $null
            trust        = $null
        }
        if (Test-Path -LiteralPath $meta -PathType Leaf) {
            $entry.metadataPath = $meta
            try {
                $entry.metadata = Get-Content -LiteralPath $meta -Raw | ConvertFrom-Json -AsHashtable
            } catch {
                $entry.metadata = $null
            }
        }
        if ($trustAvailable) {
            try {
                $entry.trust = Get-PackageTrustState -PackagePath $pkg -WorkspacePath $WorkspacePath
            } catch {
                $entry.trust = $null
            }
        }
        $items += [pscustomobject]$entry
    }

    return ,$items
}

Export-ModuleMember -Function `
    Get-UpdatePackageStagingDir, `
    Save-UpdatePackage, `
    Get-StagedPackageInventory
