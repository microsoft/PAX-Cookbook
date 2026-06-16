#requires -Version 7.4

# =====================================================================
# Update-PaxManagedEngine.ps1  Repeatable admin-only ingestion of the
# approved managed PAX script used by PAX Cookbook.
#
# WHAT THIS SCRIPT DOES
#
#   Given an approved PAX script source file and its intended version,
#   this script ingests it as the canonical managed engine for the
#   repository:
#
#     1. Validates the source exists, is non-empty, and is large enough
#        to be a real PAX script (guards against truncated/placeholder
#        inputs).
#     2. Computes the SHA-256 of the source bytes.
#     3. If -ExpectedSha256 is supplied, requires the computed hash to
#        match it exactly (an out-of-band approval check).
#     4. Reads the version declared inside the source and warns if it
#        disagrees with -Version (the supplied value still wins).
#     5. Runs the compatibility / impact analyzer
#        (Test-PaxScriptCompatibility.ps1) against the candidate, using
#        the currently installed managed script as the baseline, so the
#        operator sees whether the new script changes the switch
#        contract the Cookbook UI/parser/renderer/catalog/tests depend
#        on. Skipped with -SkipCompatibilityCheck. With
#        -FailOnHighImpactCompatibility the run aborts before any file
#        is written if a HIGH-impact finding is reported.
#     6. Copies the source byte-for-byte to the canonical managed path
#        app/resources/pax/PAX_Purview_Audit_Log_Processor.ps1. The
#        bytes are not normalised, reformatted, re-encoded, signed, or
#        patched in any way.
#     7. Verifies the destination hash equals the source hash.
#     8. Updates the active engine-identity metadata
#        (app/resources/manifest.json and app/VERSION.json) so the
#        recorded sha256 and version describe the newly ingested script.
#     9. Updates the approved-SHA constants embedded in the active
#        smoke scripts so the test suite tracks the current managed
#        engine without manual edits.
#
#   The operation is idempotent: re-running it with the same source
#   produces no further changes. -WhatIf previews every change without
#   touching disk.
#
# WHAT THIS SCRIPT DELIBERATELY DOES NOT DO
#
#   - Never executes, dot-sources, imports, or otherwise runs the PAX
#     script. It only treats it as opaque bytes.
#   - Never performs any network call, publish, upload, or fetch.
#   - Never performs any git operation.
#   - Never builds the broker or the web bundle.
#   - Never touches the React command-translation adapter version
#     constants. Those describe the command contract the builder emits
#     and are intentionally decoupled from managed-engine identity.
#   - Never edits historical phase reports or archived copies.
#
# PARAMETERS
#
#   -SourcePath      : path to the approved PAX script to ingest.
#   -Version         : the version string to record for the ingested
#                      script (e.g. '1.11.4').
#   -ExpectedSha256  : optional. If supplied, the computed source hash
#                      must equal this value or the run aborts.
#   -RepoRoot        : repo root. Defaults to two levels above this
#                      script ($PSScriptRoot\..\..).
#   -SmokeRoot       : directory whose smoke_*.ps1 files carry the
#                      approved-SHA constants. Defaults to
#                      <RepoRoot>\_temp\phase_am_product_ux_restoration.
#   -MinimumPlausibleBytes : reject sources smaller than this. Default
#                      1024.
#   -SkipCompatibilityCheck : skip the pre-ingestion compatibility /
#                      impact analysis.
#   -CompatibilityReportPath : optional path for the analyzer's JSON
#                      report.
#   -FailOnHighImpactCompatibility : abort before writing any file if
#                      the analyzer reports a HIGH-impact finding.
#
# OUTPUT
#
#   Emits a PSCustomObject summary (source/destination paths, old/new
#   version, old/new sha256, files updated) and prints a human-readable
#   report plus recommended validation commands.
# =====================================================================

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $SourcePath,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $ExpectedSha256,

    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,

    [string] $SmokeRoot,

    [int] $MinimumPlausibleBytes = 1024,

    # Skip the pre-ingestion compatibility / impact analysis entirely.
    [switch] $SkipCompatibilityCheck,

    # Optional path for the compatibility analyzer's JSON report.
    [string] $CompatibilityReportPath,

    # Abort the ingestion (before any file is written) if the
    # compatibility analyzer reports a HIGH-impact finding.
    [switch] $FailOnHighImpactCompatibility
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------

function Get-LiteralOccurrenceCount {
    param([string] $Text, [string] $Needle)
    $count = 0
    $index = 0
    while (($index = $Text.IndexOf($Needle, $index)) -ge 0) {
        $count++
        $index += $Needle.Length
    }
    return $count
}

function Get-NormalizedSha256 {
    param([string] $Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

# Reads a UTF-8 text file, applies an ordered set of literal replacements,
# and writes it back (unless -WhatIf). Each replacement may declare an
# expected occurrence count; a mismatch aborts before any write so the
# tool never guesses. Returns a record describing what changed.
function Update-FileLiteral {
    param(
        [string] $Path,
        # Array of @{ Old = ''; New = ''; ExpectedCount = $null } entries.
        [System.Collections.IEnumerable] $Replacements,
        [string] $Label
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Cannot update '$Label': file not found at $Path"
    }

    $original = [System.IO.File]::ReadAllText($Path)
    $updated  = $original

    foreach ($r in $Replacements) {
        $old = [string] $r.Old
        $new = [string] $r.New

        if ($old -eq $new) { continue }

        $present = Get-LiteralOccurrenceCount -Text $updated -Needle $old

        if ($null -ne $r.ExpectedCount) {
            $expected = [int] $r.ExpectedCount
            if ($present -ne $expected) {
                throw ("Refusing to edit '{0}': expected {1} occurrence(s) of `"{2}`" but found {3}." -f `
                    $Label, $expected, $old, $present)
            }
        }

        if ($present -gt 0) {
            $updated = $updated.Replace($old, $new)
        }
    }

    $changed = ($updated -ne $original)

    if ($changed) {
        if ($PSCmdlet.ShouldProcess($Path, 'Update literal references')) {
            $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
            [System.IO.File]::WriteAllText($Path, $updated, $utf8NoBom)
        }
    }

    return [PSCustomObject]@{
        Path    = $Path
        Label   = $Label
        Changed = $changed
    }
}

# ---------------------------------------------------------------------
# Resolve canonical repository paths
# ---------------------------------------------------------------------

$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

if (-not $SmokeRoot) {
    $SmokeRoot = Join-Path $RepoRoot '_temp\phase_am_product_ux_restoration'
}

$managedScriptPath = Join-Path $RepoRoot 'app\resources\pax\PAX_Purview_Audit_Log_Processor.ps1'
$manifestPath      = Join-Path $RepoRoot 'app\resources\manifest.json'
$versionJsonPath   = Join-Path $RepoRoot 'app\VERSION.json'

# ---------------------------------------------------------------------
# 1. Validate the source
# ---------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "Source PAX script not found: $SourcePath"
}

$sourceItem  = Get-Item -LiteralPath $SourcePath
$sourceBytes = $sourceItem.Length

if ($sourceBytes -lt $MinimumPlausibleBytes) {
    throw ("Source PAX script is implausibly small ({0} bytes; minimum {1}). Refusing to ingest a possibly truncated or placeholder file." -f `
        $sourceBytes, $MinimumPlausibleBytes)
}

# ---------------------------------------------------------------------
# 2. Compute the source hash
# ---------------------------------------------------------------------

$newSha = Get-NormalizedSha256 -Path $SourcePath
$newVersion = $Version.Trim()

# ---------------------------------------------------------------------
# 3. Optional approval check
# ---------------------------------------------------------------------

if ($ExpectedSha256) {
    $expected = $ExpectedSha256.Trim().ToUpperInvariant()
    if ($newSha -ne $expected) {
        throw ("Computed source SHA-256 does not match -ExpectedSha256.`n  expected: {0}`n  computed: {1}" -f `
            $expected, $newSha)
    }
    Write-Verbose "ExpectedSha256 check passed."
}

# ---------------------------------------------------------------------
# 4. Inspect the version declared inside the source (warn-only)
# ---------------------------------------------------------------------

$sourceText = [System.IO.File]::ReadAllText($SourcePath)
$declaredVersion = $null

foreach ($line in ($sourceText -split "`n")) {
    $trimmed = $line.Trim()
    if ($trimmed.StartsWith('$ScriptVersion')) {
        $start = $trimmed.IndexOf("'")
        $end   = $trimmed.LastIndexOf("'")
        if ($start -ge 0 -and $end -gt $start) {
            $declaredVersion = $trimmed.Substring($start + 1, $end - $start - 1)
            break
        }
    }
}

if ($declaredVersion -and $declaredVersion -ne $newVersion) {
    Write-Warning ("Source declares `$ScriptVersion = '{0}' but -Version was '{1}'. Recording '{1}' as supplied." -f `
        $declaredVersion, $newVersion)
}

# ---------------------------------------------------------------------
# 5. Compatibility / impact analysis (read-only; never runs the PAX
#    script). Uses the currently installed managed script as the
#    baseline so the operator sees what the incoming candidate changes
#    in the switch contract the Cookbook depends on.
# ---------------------------------------------------------------------

$compat = $null
if ($SkipCompatibilityCheck) {
    Write-Verbose "Compatibility check skipped (-SkipCompatibilityCheck)."
} else {
    $compatTool = Join-Path $PSScriptRoot 'Test-PaxScriptCompatibility.ps1'
    if (-not (Test-Path -LiteralPath $compatTool)) {
        throw "Compatibility analyzer not found next to this script: $compatTool. Pass -SkipCompatibilityCheck to bypass."
    }

    $compatArgs = @{
        SourcePath = $SourcePath
        Version    = $newVersion
        RepoRoot   = $RepoRoot
    }
    if (Test-Path -LiteralPath $managedScriptPath) {
        $compatArgs.BaselinePath = $managedScriptPath
    }
    if ($CompatibilityReportPath) {
        $compatArgs.ReportPath = $CompatibilityReportPath
    }

    # Invoke in-process and capture the report object. Deliberately do
    # NOT pass -FailOnHighImpact: that would call `exit` inside the child
    # and terminate this parent. The HIGH-impact gate is enforced here
    # instead, before any file is written.
    $compat = & $compatTool @compatArgs

    if ($null -ne $compat -and $compat.counts.high -gt 0) {
        if ($FailOnHighImpactCompatibility) {
            throw ("Aborting ingestion: the compatibility analyzer reported {0} HIGH-impact finding(s). Resolve them (catalog / parser / renderer / UI / tests) or re-run with -SkipCompatibilityCheck. No files were written." -f `
                $compat.counts.high)
        }
        Write-Warning ("Compatibility analyzer reported {0} HIGH-impact finding(s). Continuing because -FailOnHighImpactCompatibility was not supplied." -f `
            $compat.counts.high)
    }
}

# ---------------------------------------------------------------------
# 6. Read current (old) engine identity from the manifest
# ---------------------------------------------------------------------

$manifestObj = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$oldSha     = ([string] $manifestObj.includedPaxScript.sha256).ToUpperInvariant()
$oldVersion = [string] $manifestObj.includedPaxScript.version

$alreadyCurrent = ($oldSha -eq $newSha)

Write-Verbose ("Old managed engine: version {0}, sha {1}" -f $oldVersion, $oldSha)
Write-Verbose ("New managed engine: version {0}, sha {1}" -f $newVersion, $newSha)

# ---------------------------------------------------------------------
# 7. Copy the source byte-for-byte to the canonical managed path
# ---------------------------------------------------------------------

$destChanged = $false
if ($alreadyCurrent -and (Test-Path -LiteralPath $managedScriptPath)) {
    $existingSha = Get-NormalizedSha256 -Path $managedScriptPath
    if ($existingSha -eq $newSha) {
        Write-Verbose "Managed script already byte-identical to source; no copy needed."
    } else {
        $destChanged = $true
    }
} else {
    $destChanged = $true
}

if ($destChanged) {
    if ($PSCmdlet.ShouldProcess($managedScriptPath, 'Copy approved PAX script byte-for-byte')) {
        Copy-Item -LiteralPath $SourcePath -Destination $managedScriptPath -Force
        $destSha = Get-NormalizedSha256 -Path $managedScriptPath
        if ($destSha -ne $newSha) {
            throw ("Byte-for-byte copy verification FAILED.`n  source: {0}`n  dest:   {1}" -f $newSha, $destSha)
        }
        Write-Verbose "Destination hash verified equal to source hash."
    }
}

# ---------------------------------------------------------------------
# 8. Update active engine-identity metadata
# ---------------------------------------------------------------------

$metadataResults = @()

$metadataReplacements = @(
    @{ Old = $oldSha;                          New = $newSha;     ExpectedCount = $null },
    @{ Old = ('"version": "{0}"' -f $oldVersion); New = ('"version": "{0}"' -f $newVersion); ExpectedCount = 1 }
)

$metadataResults += Update-FileLiteral -Path $manifestPath    -Replacements $metadataReplacements -Label 'manifest.json'
$metadataResults += Update-FileLiteral -Path $versionJsonPath -Replacements $metadataReplacements -Label 'VERSION.json'

# ---------------------------------------------------------------------
# 9. Update approved-SHA constants in the active smoke suite
# ---------------------------------------------------------------------

$smokeResults = @()

if (Test-Path -LiteralPath $SmokeRoot) {
    $smokeFiles = Get-ChildItem -LiteralPath $SmokeRoot -Filter 'smoke_*.ps1' -File
    foreach ($f in $smokeFiles) {
        $text = [System.IO.File]::ReadAllText($f.FullName)
        if (-not $text.Contains($oldSha)) { continue }

        $smokeReplacements = @(
            @{ Old = $oldSha;                                  New = $newSha;     ExpectedCount = $null },
            @{ Old = ('frozen PAX v{0}' -f $oldVersion);       New = ('frozen PAX v{0}' -f $newVersion); ExpectedCount = $null }
        )

        $smokeResults += Update-FileLiteral -Path $f.FullName -Replacements $smokeReplacements -Label $f.Name
    }
} else {
    Write-Warning "SmokeRoot not found: $SmokeRoot (skipping smoke constant updates)."
}

# ---------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------

$changedMetadata = @($metadataResults | Where-Object { $_.Changed } | ForEach-Object { $_.Label })
$changedSmokes   = @($smokeResults   | Where-Object { $_.Changed } | ForEach-Object { $_.Label })

$compatRecommendation = if ($null -ne $compat) { $compat.recommendation } elseif ($SkipCompatibilityCheck) { 'SKIPPED' } else { 'UNAVAILABLE' }
$compatHigh   = if ($null -ne $compat) { $compat.counts.high }   else { $null }
$compatMedium = if ($null -ne $compat) { $compat.counts.medium } else { $null }
$compatLow    = if ($null -ne $compat) { $compat.counts.low }    else { $null }

$summary = [PSCustomObject]@{
    SourcePath            = (Resolve-Path -LiteralPath $SourcePath).Path
    SourceBytes           = $sourceBytes
    DestinationPath       = $managedScriptPath
    DeclaredVersion       = $declaredVersion
    OldVersion            = $oldVersion
    NewVersion            = $newVersion
    OldSha256             = $oldSha
    NewSha256             = $newSha
    AlreadyCurrent        = $alreadyCurrent
    ScriptCopied          = $destChanged
    MetadataUpdated       = $changedMetadata
    SmokesUpdated         = $changedSmokes
    CompatRecommendation  = $compatRecommendation
    CompatHigh            = $compatHigh
    CompatMedium          = $compatMedium
    CompatLow             = $compatLow
    WhatIf                = [bool]$WhatIfPreference
}

$mode = if ($WhatIfPreference) { 'PREVIEW (-WhatIf: no files written)' } else { 'APPLIED' }

Write-Host ''
Write-Host '=====================================================================' -ForegroundColor Cyan
Write-Host (' PAX managed engine ingestion  [{0}]' -f $mode) -ForegroundColor Cyan
Write-Host '=====================================================================' -ForegroundColor Cyan
Write-Host (' Source        : {0}' -f $summary.SourcePath)
Write-Host (' Source bytes  : {0}' -f $summary.SourceBytes)
Write-Host (' Destination   : {0}' -f $summary.DestinationPath)
Write-Host (' Declared ver  : {0}' -f $summary.DeclaredVersion)
Write-Host (' Version       : {0}  ->  {1}' -f $summary.OldVersion, $summary.NewVersion)
Write-Host (' SHA-256       : {0}' -f $summary.OldSha256)
Write-Host ('                 ->  {0}' -f $summary.NewSha256)
if ($summary.AlreadyCurrent) {
    Write-Host ' Managed engine already current (idempotent no-op).' -ForegroundColor Green
}
Write-Host (' Script copied : {0}' -f $summary.ScriptCopied)
$metadataDisplay = if ($summary.MetadataUpdated.Count -gt 0) { $summary.MetadataUpdated -join ', ' } else { '(none)' }
Write-Host (' Metadata      : {0}' -f $metadataDisplay)
Write-Host (' Smokes updated: {0}' -f $summary.SmokesUpdated.Count)
Write-Host '---------------------------------------------------------------------'
if ($null -ne $compat) {
    $compatColor = switch ($summary.CompatRecommendation) {
        'ACTION_REQUIRED' { 'Red' }
        'REVIEW_RECOMMENDED' { 'Yellow' }
        default { 'Green' }
    }
    Write-Host (' Compatibility : {0}  (HIGH={1} MEDIUM={2} LOW={3})' -f `
        $summary.CompatRecommendation, $summary.CompatHigh, $summary.CompatMedium, $summary.CompatLow) -ForegroundColor $compatColor
    Write-Host ('                 {0}' -f $compat.uiUxImpact)
} else {
    Write-Host (' Compatibility : {0}' -f $summary.CompatRecommendation)
}
Write-Host '---------------------------------------------------------------------'
Write-Host ' Recommended validation:'
Write-Host ('   Get-FileHash -Algorithm SHA256 "{0}"' -f $summary.DestinationPath)
Write-Host ('   (Get-Content "{0}" -Raw | ConvertFrom-Json).includedPaxScript | Format-List version, sha256' -f $manifestPath)
Write-Host ('   (Get-Content "{0}" -Raw | ConvertFrom-Json).paxScript | Format-List version, sha256' -f $versionJsonPath)
Write-Host '====================================================================='
Write-Host ''

$summary
