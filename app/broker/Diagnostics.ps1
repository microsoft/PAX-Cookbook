#requires -Version 7.4

# Diagnostics.ps1
#
# Phase AI.C6.1 -- diagnostics bundle primitive (G1 of AI.C6).
# Phase AI.C6.2 -- bundle completeness (G2) + read-only guarantee (G3):
#                  pre-flight guards FAIL FAST when any required input
#                  is missing (DB file, VERSION.json, recent-errors
#                  ring) so the bundle is never partial. Read-only
#                  behavior is structurally pinned by the AI.C6.2
#                  smoke. No new file kinds; no new options.
#
# Exposes a SINGLE function: Export-DiagnosticsBundle. The function
# packages raw forensic evidence so an operator can analyze it
# externally. The broker produces evidence; it does NOT analyze.
#
# This file is dot-sourced from Start-Broker.ps1, so the $Script:
# scope here is the broker script scope -- the same scope that
# defines $Script:DatabaseFile, $Script:VersionFile,
# $Script:WorkspacePath, $Script:RecentErrors, and
# $Script:CookbookVersion. This matches the AI.C5.1 dot-source
# pattern for Environment.ps1.
#
# Doctrine (AI.C6 entry slice; load-bearing; DO NOT paraphrase):
#
#   - The bundle is a STRICT COPY of files plus a serialization
#     of the in-memory recent-error ring. No row is reshaped, no
#     row is filtered, no row is summarized, no table is queried.
#
#   - The function opens NO connection to the SQLite database.
#     It copies the database file (and its WAL / SHM sidecars
#     when present) at the filesystem level. Including the
#     sidecars is part of "the entire file"; without them a WAL-
#     mode snapshot is incomplete and recent writes would be
#     missing from the bundle.
#
#   - The function is read-only with respect to the database, the
#     workspace, and the version file. The only writes it
#     performs are inside the destination bundle directory.
#
#   - There is no scheduled invocation, no background collector,
#     no HTTP route, and no automatic upload. Export is operator-
#     initiated, file-based, and pull-only.
#
#   - There is no read or query surface over the observation
#     tables. The bundle exposes the raw .sqlite file; the
#     operator inspects it externally with sqlite3 / DB Browser /
#     etc. This preserves the AI.C2 / AI.C3 / AI.C5 invariant
#     that the broker never reads its own observation tables at
#     runtime.

function Export-DiagnosticsBundle {
    <#
    .SYNOPSIS
    Export a diagnostics bundle: raw SQLite file, recent errors,
    VERSION.json, and bundle metadata.

    .PARAMETER OutputPath
    Destination directory (or .zip path when -AsZip is supplied).
    When omitted, the bundle is written to
    <workspace>\Diagnostics\bundle_<utc-stamp>.

    .PARAMETER AsZip
    Package the bundle as a single .zip archive next to the bundle
    directory and remove the staging directory. The .zip is a
    simple Compress-Archive output; no row-level processing.

    .OUTPUTS
    [pscustomobject] with BundlePath, Format ('folder' | 'zip'),
    and a Contents list naming each file copied or written.
    #>
    [CmdletBinding()]
    param(
        [Parameter()] [string]$OutputPath,
        [Parameter()] [switch]$AsZip
    )

    # ---- Pre-flight completeness guards (AI.C6.2 G2) ----
    # The bundle's value to an operator depends on it being
    # COMPLETE. A partial bundle is worse than a clear failure
    # because the operator may not notice the omission. Fail fast
    # if any of the three required inputs is missing.
    if ([string]::IsNullOrWhiteSpace($Script:DatabaseFile) -or -not (Test-Path -LiteralPath $Script:DatabaseFile -PathType Leaf)) {
        throw ('Export-DiagnosticsBundle: required input missing -- cookbook database file not present at ''' + [string]$Script:DatabaseFile + '''. Bundle aborted; no files written.')
    }
    if ([string]::IsNullOrWhiteSpace($Script:VersionFile) -or -not (Test-Path -LiteralPath $Script:VersionFile -PathType Leaf)) {
        throw ('Export-DiagnosticsBundle: required input missing -- VERSION.json not present at ''' + [string]$Script:VersionFile + '''. Bundle aborted; no files written.')
    }
    if ($null -eq $Script:RecentErrors) {
        throw 'Export-DiagnosticsBundle: required input missing -- $Script:RecentErrors ring buffer is not initialized. Bundle aborted; no files written.'
    }

    $utcStamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $defaultRoot = Join-Path $Script:WorkspacePath 'Diagnostics'
        $bundleDir   = Join-Path $defaultRoot ('bundle_' + $utcStamp)
    } else {
        $bundleDir = $OutputPath
        if ($AsZip -and $bundleDir.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
            # Caller named the zip; stage in a sibling directory.
            $bundleDir = $bundleDir.Substring(0, $bundleDir.Length - 4)
        }
    }

    if (-not (Test-Path -LiteralPath $bundleDir)) {
        New-Item -ItemType Directory -Path $bundleDir -Force | Out-Null
    }

    $contents = New-Object System.Collections.Generic.List[string]

    # ---- 1. SQLite database file (and its WAL / SHM sidecars) ----
    # Copy the database file verbatim. The broker opens NO database
    # connection from this function; the copy is a filesystem-level
    # snapshot. WAL / SHM sidecars are included only when present.
    # Presence of the main DB file is guaranteed by the pre-flight
    # guard above (AI.C6.2 G2); we never reach this point with the
    # main file missing.
    $dbLeaf = Split-Path -Leaf $Script:DatabaseFile
    Copy-Item -LiteralPath $Script:DatabaseFile -Destination (Join-Path $bundleDir $dbLeaf) -Force
    $contents.Add($dbLeaf) | Out-Null
    foreach ($side in @('-wal','-shm')) {
        $sidePath = $Script:DatabaseFile + $side
        if (Test-Path -LiteralPath $sidePath -PathType Leaf) {
            $sideLeaf = Split-Path -Leaf $sidePath
            Copy-Item -LiteralPath $sidePath -Destination (Join-Path $bundleDir $sideLeaf) -Force
            $contents.Add($sideLeaf) | Out-Null
        }
    }

    # ---- 2. Recent errors ring buffer serialization ----
    # The ring buffer IS the recent-errors source. The broker keeps
    # no separate log file; recentErrors is the authoritative record.
    # We serialize the ring as-is (no filtering, no reshaping), plus
    # the overflow counter so the operator knows whether older
    # entries were displaced.
    $ringSnapshot = [pscustomobject]@{
        capturedAtUtc       = (Get-Date).ToUniversalTime().ToString('o')
        capacity            = $Script:RecentErrorCapacity
        overflowCount       = $Script:RecentErrorOverflowCount
        recentErrors        = @($Script:RecentErrors.ToArray())
        recentErrorCount    = @($Script:RecentErrors.ToArray()).Count
    }
    $ringPath = Join-Path $bundleDir 'recent_errors.json'
    $ringSnapshot | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ringPath -Encoding UTF8
    $contents.Add('recent_errors.json') | Out-Null

    # ---- 3. VERSION.json (config / version source of truth) ----
    # Presence guaranteed by the pre-flight guard (AI.C6.2 G2).
    Copy-Item -LiteralPath $Script:VersionFile -Destination (Join-Path $bundleDir 'VERSION.json') -Force
    $contents.Add('VERSION.json') | Out-Null

    # ---- 4. Bundle metadata ----
    $metadata = [pscustomobject]@{
        bundleFormat       = 'ai_c6_2'
        createdAtUtc       = (Get-Date).ToUniversalTime().ToString('o')
        brokerCookbookVersion = $Script:CookbookVersion
        brokerPaxScriptVersion = $Script:PaxScriptVersion
        brokerReleaseChannel   = $Script:ReleaseChannel
        sourceWorkspacePath = $Script:WorkspacePath
        sourceDatabaseFile  = $Script:DatabaseFile
        sourceVersionFile   = $Script:VersionFile
    }
    $metaPath = Join-Path $bundleDir 'metadata.json'
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $metaPath -Encoding UTF8
    $contents.Add('metadata.json') | Out-Null

    # ---- 5. Optional .zip packaging ----
    if ($AsZip) {
        $zipPath = $bundleDir + '.zip'
        if ($OutputPath -and $OutputPath.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
            $zipPath = $OutputPath
        }
        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -LiteralPath $zipPath -Force
        }
        Compress-Archive -Path (Join-Path $bundleDir '*') -DestinationPath $zipPath -Force
        Remove-Item -LiteralPath $bundleDir -Recurse -Force
        return [pscustomobject]@{
            BundlePath = $zipPath
            Format     = 'zip'
            Contents   = $contents.ToArray()
        }
    }

    return [pscustomobject]@{
        BundlePath = $bundleDir
        Format     = 'folder'
        Contents   = $contents.ToArray()
    }
}
