#requires -Version 7.4

# =====================================================================
# Test-PaxScriptCompatibility.ps1  Admin-only compatibility / impact
# analysis for a candidate PAX script against the PAX Cookbook product
# surface (UI/UX, recipe model, command parser, command renderer, the
# switch catalog, and the test suite).
#
# WHY THIS EXISTS
#
#   The Cookbook drives the managed PAX engine through a fixed switch
#   contract expressed in the React switch catalog
#   (app/web-react/src/features/mini-kitchen/data/pax-switch-catalog.ts).
#   That catalog is the single source of truth consumed by the command
#   renderer (recipe -> command), the pasted-command importer
#   (command -> recipe), the advanced-argument handler, the permissions
#   resolver, and the recipe translator. When a future PAX release adds,
#   removes, or renames a parameter, the Cookbook may need a matching
#   change to the catalog, the parser/renderer, the UI labels/help, the
#   readiness checks, or the tests. This script makes that impact
#   visible BEFORE a new PAX script is ingested as the managed engine.
#
# WHAT THIS SCRIPT DOES
#
#   1. Validates the candidate source exists and is a plausible PAX
#      script.
#   2. Reads it as TEXT ONLY and parses it with the PowerShell language
#      parser to extract the top-level param() block parameter names and
#      the -Auth ValidateSet values. The parser builds a syntax tree; it
#      never executes, dot-sources, or imports the script.
#   3. Extracts the version the source declares (header banner and/or
#      $ScriptVersion).
#   4. Reads the Cookbook switch catalog and extracts the active switch
#      names, the removed/unsupported switch names, and the -Auth
#      enumValues the UI offers.
#   5. Compares the PAX parameter surface against the catalog and (when
#      -BaselinePath is supplied) against a prior PAX script, and
#      classifies every difference by impact:
#        HIGH   - a switch the Cookbook actively emits is gone from PAX,
#                 or an -Auth value the UI offers is gone from PAX
#                 (would break a real cook).
#        MEDIUM - a PAX parameter that is new (versus the baseline) or
#                 otherwise not covered and not a known intentional
#                 exclusion (a candidate for exposure / review).
#        LOW    - long-standing intentional exclusions and cosmetic
#                 notes (no action required).
#   6. Emits a machine-readable JSON report (when -ReportPath is given)
#      and a human-readable console summary, and returns the report
#      object.
#
# WHAT THIS SCRIPT DELIBERATELY DOES NOT DO
#
#   - Never executes, dot-sources, imports, or runs the PAX script, and
#     never spawns a process to run it. It only reads bytes/text and
#     parses syntax.
#   - Never performs any network call.
#   - Never performs any git operation.
#   - Never modifies the PAX source, the catalog, the product source,
#     the manifest, VERSION.json, or any smoke script.
#   - Never ingests or copies the candidate script. Use
#     Update-PaxManagedEngine.ps1 for ingestion.
#
# EXIT CODE
#
#   0 by default. Returns 1 only when -FailOnHighImpact is supplied AND
#   at least one HIGH-impact finding is present, so it can gate an
#   automated ingestion pipeline without failing on advisory findings.
# =====================================================================

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $SourcePath,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    # Optional prior PAX script to diff the parameter surface against.
    # The strongest novelty signal: identical parameter sets prove a
    # release is parser/renderer/catalog-neutral (e.g. a bug-fix-only
    # release).
    [string] $BaselinePath,

    # Optional path to write the JSON report to. When omitted, the
    # report object is still returned and summarised on the console.
    [string] $ReportPath,

    # Return exit code 1 if any HIGH-impact finding is present.
    [switch] $FailOnHighImpact,

    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,

    # The Cookbook switch catalog (single source of truth for the
    # switches the builder is allowed to emit). Defaults to the repo
    # location.
    [string] $CatalogPath,

    [int] $MinimumPlausibleBytes = 1024
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------
# Intentional-exclusion allowlist
#
# PAX exposes many parameters the Cookbook deliberately does NOT surface
# in the builder: secrets that must never be persisted, advanced
# performance/adaptive tuning knobs, mode/help/resume plumbing, and
# parameters the Cookbook drives implicitly through presets. A PAX
# parameter that is uncovered by the catalog AND appears here is treated
# as a long-standing intentional exclusion (LOW), not a new switch that
# warrants UI review. The baseline diff remains the authoritative
# novelty check when a -BaselinePath is supplied.
# ---------------------------------------------------------------------
$script:KnownUnexposedParameters = @(
    # Secrets / credential material the lite recipe never carries.
    'ClientSecret', 'ClientCertificateStoreLocation', 'ClientCertificatePath', 'ClientCertificatePassword',
    # Driven implicitly through dashboard presets / ActivityTypes.
    'RecordTypes', 'ServiceTypes',
    # Advanced flattening / raw replay.
    'FlatDepth', 'ExplosionThreads',
    # Adaptive / performance tuning knobs.
    'DisableAdaptive', 'ProgressSmoothingAlpha', 'HighLatencyMs', 'MemoryPressureMB', 'MaxMemoryMB',
    'StatusIntervalSeconds', 'LowLatencyMs', 'LowLatencyConsecutive', 'ThroughputDropPct',
    'ThroughputSmoothingAlpha', 'AdaptiveConcurrencyCeiling', 'ExportProgressInterval',
    'StreamingSchemaSample', 'StreamingChunkSize', 'MaxPartitions', 'MaxNetworkOutageMinutes',
    # Mode / help / resume plumbing and internal log path.
    'UseEOM', 'Help', 'IncludeCopilotInteraction', 'RemainingArgs', 'OutputPathLog'
)

# Category buckets used to flag changes in identity, output destination,
# date window, M365 usage, Entra / Agent 365 enrichment, and Fabric /
# SharePoint surfaces. A change in any of these draws extra attention.
function Get-ParameterCategory {
    param([string] $Name)
    $categories = @()
    if ($Name -eq 'Auth' -or $Name -eq 'TenantId' -or $Name -eq 'ClientId' -or
        $Name -eq 'ClientSecret' -or $Name.StartsWith('ClientCertificate')) {
        $categories += 'authIdentity'
    }
    if ($Name.StartsWith('OutputPath') -or $Name.StartsWith('Append')) {
        $categories += 'outputPath'
    }
    if ($Name -eq 'StartDate' -or $Name -eq 'EndDate') {
        $categories += 'dateWindow'
    }
    if ($Name -eq 'IncludeM365Usage' -or $Name -eq 'ExcludeCopilotInteraction' -or
        $Name -eq 'IncludeCopilotInteraction') {
        $categories += 'm365Usage'
    }
    if ($Name -like '*UserInfo*' -or $Name -like '*Agent365*') {
        $categories += 'entraAgent365'
    }
    if ($Name -like '*Fabric*' -or $Name -like '*SharePoint*' -or $Name -like '*OneLake*') {
        $categories += 'fabricSharePoint'
    }
    return , $categories
}

# ---------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------

# Parse a PowerShell script file with the language parser (NO execution)
# and return the names of the parameters declared in its top-level
# param() block, plus the ValidateSet values for any -Auth parameter.
function Get-ScriptParameterSurface {
    param([string] $Path)

    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)

    $names = New-Object System.Collections.Generic.List[string]
    $authValues = New-Object System.Collections.Generic.List[string]

    $paramBlock = $null
    if ($null -ne $ast -and $null -ne $ast.ParamBlock) {
        $paramBlock = $ast.ParamBlock
    } else {
        # Fallback: locate the first ParamBlockAst anywhere in the tree
        # (defensive; the top-level block is expected above).
        $found = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.ParamBlockAst] }, $false)
        if ($null -ne $found) { $paramBlock = $found }
    }

    if ($null -ne $paramBlock) {
        foreach ($p in $paramBlock.Parameters) {
            $pname = $p.Name.VariablePath.UserPath
            $names.Add($pname)
            if ($pname -eq 'Auth') {
                foreach ($attr in $p.Attributes) {
                    if ($attr -is [System.Management.Automation.Language.AttributeAst] -and
                        $attr.TypeName.FullName -eq 'ValidateSet') {
                        foreach ($arg in $attr.PositionalArguments) {
                            if ($arg -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                                $authValues.Add($arg.Value)
                            }
                        }
                    }
                }
            }
        }
    }

    return [PSCustomObject]@{
        ParameterNames = $names.ToArray()
        AuthValues     = $authValues.ToArray()
        ParseErrors    = @($errors).Count
    }
}

# Extract the version a PAX script declares: the "# Version: vX.Y.Z"
# header banner and/or the $ScriptVersion = 'X.Y.Z' assignment. Returns
# the first found (banner preferred), with the trailing 'v' stripped.
function Get-DeclaredPaxVersion {
    param([string] $Text)
    $banner = $null
    $scriptVar = $null
    foreach ($line in ($Text -split "`n")) {
        $trimmed = $line.Trim()
        if ($null -eq $banner -and $trimmed.StartsWith('# Version:')) {
            $val = $trimmed.Substring('# Version:'.Length).Trim()
            if ($val.StartsWith('v')) { $val = $val.Substring(1) }
            $banner = $val
        }
        if ($null -eq $scriptVar -and $trimmed.StartsWith('$ScriptVersion')) {
            $start = $trimmed.IndexOf("'")
            $end = $trimmed.LastIndexOf("'")
            if ($start -ge 0 -and $end -gt $start) {
                $scriptVar = $trimmed.Substring($start + 1, $end - $start - 1)
            }
        }
        if ($null -ne $banner -and $null -ne $scriptVar) { break }
    }
    if ($null -ne $banner) { return $banner }
    return $scriptVar
}

# Extract switch names from one TypeScript catalog array. Reads the slice
# of catalog text between the array's declaration and the supplied end
# marker, then collects the object-level `name: '...'` properties. This
# is a read-only text scan; it never evaluates the TypeScript.
function Get-CatalogSwitchNames {
    param([string] $Text, [string] $StartMarker, [string] $EndMarker)

    $startIndex = $Text.IndexOf($StartMarker)
    if ($startIndex -lt 0) { return @() }

    $endIndex = if ([string]::IsNullOrEmpty($EndMarker)) { $Text.Length } else {
        $found = $Text.IndexOf($EndMarker, $startIndex + $StartMarker.Length)
        if ($found -lt 0) { $Text.Length } else { $found }
    }

    $slice = $Text.Substring($startIndex, $endIndex - $startIndex)

    $names = New-Object System.Collections.Generic.List[string]
    foreach ($m in [System.Text.RegularExpressions.Regex]::Matches($slice, "(?m)^\s*name:\s*'([^']+)'")) {
        $names.Add($m.Groups[1].Value)
    }
    return $names.ToArray()
}

# Extract the -Auth enumValues array from the catalog text (the values
# the UI offers for the identity / auth selector).
function Get-CatalogAuthEnumValues {
    param([string] $Text)
    $nameIdx = $Text.IndexOf("name: 'Auth'")
    if ($nameIdx -lt 0) { return @() }
    $enumIdx = $Text.IndexOf('enumValues:', $nameIdx)
    if ($enumIdx -lt 0) { return @() }
    $open = $Text.IndexOf('[', $enumIdx)
    $close = $Text.IndexOf(']', $open)
    if ($open -lt 0 -or $close -lt 0) { return @() }
    $inner = $Text.Substring($open + 1, $close - $open - 1)
    $values = New-Object System.Collections.Generic.List[string]
    foreach ($m in [System.Text.RegularExpressions.Regex]::Matches($inner, "'([^']+)'")) {
        $values.Add($m.Groups[1].Value)
    }
    return $values.ToArray()
}

function Get-CatalogVersion {
    param([string] $ConstantsText)
    foreach ($line in ($ConstantsText -split "`n")) {
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith('export const SWITCH_CATALOG_VERSION')) {
            $start = $trimmed.IndexOf("'")
            $end = $trimmed.LastIndexOf("'")
            if ($start -ge 0 -and $end -gt $start) {
                return $trimmed.Substring($start + 1, $end - $start - 1)
            }
        }
    }
    return $null
}

# ---------------------------------------------------------------------
# Resolve paths
# ---------------------------------------------------------------------

$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

if (-not $CatalogPath) {
    $CatalogPath = Join-Path $RepoRoot 'app\web-react\src\features\mini-kitchen\data\pax-switch-catalog.ts'
}
$constantsPath = Join-Path $RepoRoot 'app\web-react\src\features\mini-kitchen\data\mini-kitchen-constants.ts'

# ---------------------------------------------------------------------
# 1. Validate the candidate source
# ---------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "Candidate PAX script not found: $SourcePath"
}

$sourceItem = Get-Item -LiteralPath $SourcePath
if ($sourceItem.Length -lt $MinimumPlausibleBytes) {
    throw ("Candidate PAX script is implausibly small ({0} bytes; minimum {1}). Refusing to analyse a possibly truncated or placeholder file." -f `
        $sourceItem.Length, $MinimumPlausibleBytes)
}

if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf)) {
    throw "Switch catalog not found: $CatalogPath"
}

# ---------------------------------------------------------------------
# 2-3. Read source as text, extract parameter surface and version
# ---------------------------------------------------------------------

$sourceText = [System.IO.File]::ReadAllText($SourcePath)
$sourceSurface = Get-ScriptParameterSurface -Path $SourcePath
$sourceParams = $sourceSurface.ParameterNames
$sourceAuthValues = $sourceSurface.AuthValues
$declaredVersion = Get-DeclaredPaxVersion -Text $sourceText
$suppliedVersion = $Version.Trim()

if ($sourceParams.Count -eq 0) {
    throw "Could not extract any top-level parameters from the candidate PAX script. Aborting rather than reporting an empty surface."
}

# ---------------------------------------------------------------------
# 4. Read the catalog coverage
# ---------------------------------------------------------------------

$catalogText = [System.IO.File]::ReadAllText($CatalogPath)
$activeNames = Get-CatalogSwitchNames -Text $catalogText `
    -StartMarker 'export const PAX_SWITCH_CATALOG' `
    -EndMarker 'export const REMOVED_OR_UNSUPPORTED_SWITCHES'
$removedNames = Get-CatalogSwitchNames -Text $catalogText `
    -StartMarker 'export const REMOVED_OR_UNSUPPORTED_SWITCHES' `
    -EndMarker ''
$catalogAuthValues = Get-CatalogAuthEnumValues -Text $catalogText

$catalogVersion = $null
if (Test-Path -LiteralPath $constantsPath -PathType Leaf) {
    $catalogVersion = Get-CatalogVersion -ConstantsText ([System.IO.File]::ReadAllText($constantsPath))
}

if ($activeNames.Count -eq 0) {
    throw "Could not extract any active switch names from the catalog at $CatalogPath. Aborting rather than reporting empty coverage."
}

# ---------------------------------------------------------------------
# 5. Read the optional baseline parameter surface
# ---------------------------------------------------------------------

$baselineParams = $null
$baselineVersion = $null
if ($BaselinePath) {
    if (-not (Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
        throw "Baseline PAX script not found: $BaselinePath"
    }
    $baselineSurface = Get-ScriptParameterSurface -Path $BaselinePath
    $baselineParams = $baselineSurface.ParameterNames
    $baselineVersion = Get-DeclaredPaxVersion -Text ([System.IO.File]::ReadAllText($BaselinePath))
}

# ---------------------------------------------------------------------
# 6. Classify
# ---------------------------------------------------------------------

$sourceSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$sourceParams, [System.StringComparer]::Ordinal)
$activeSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$activeNames, [System.StringComparer]::Ordinal)
$removedSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$removedNames, [System.StringComparer]::Ordinal)
$unexposedSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$script:KnownUnexposedParameters, [System.StringComparer]::Ordinal)
$baselineSet = $null
if ($null -ne $baselineParams) {
    $baselineSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$baselineParams, [System.StringComparer]::Ordinal)
}

$highFindings = New-Object System.Collections.Generic.List[object]
$mediumFindings = New-Object System.Collections.Generic.List[object]
$lowFindings = New-Object System.Collections.Generic.List[object]

function New-Finding {
    param([string] $Code, [string] $Parameter, [string[]] $Categories, [string] $Message)
    return [PSCustomObject]@{
        code       = $Code
        parameter  = $Parameter
        categories = @($Categories)
        message    = $Message
    }
}

# (a) Active catalog switches that PAX no longer accepts -> HIGH.
foreach ($name in $activeNames) {
    if (-not $sourceSet.Contains($name)) {
        $cats = Get-ParameterCategory -Name $name
        $highFindings.Add((New-Finding -Code 'CATALOG_SWITCH_MISSING_IN_PAX' -Parameter $name -Categories $cats `
            -Message "The Cookbook actively emits -$name but the candidate PAX script has no such parameter. A real cook would fail. Update the catalog/renderer/parser and tests before ingesting.")) | Out-Null
    }
}

# (b) -Auth values the UI offers that PAX no longer accepts -> HIGH.
if ($sourceAuthValues.Count -gt 0 -and $catalogAuthValues.Count -gt 0) {
    $sourceAuthSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$sourceAuthValues, [System.StringComparer]::Ordinal)
    foreach ($v in $catalogAuthValues) {
        if (-not $sourceAuthSet.Contains($v)) {
            $highFindings.Add((New-Finding -Code 'AUTH_ENUM_VALUE_MISSING_IN_PAX' -Parameter 'Auth' -Categories @('authIdentity') `
                -Message "The UI offers -Auth '$v' but the candidate PAX script's -Auth ValidateSet no longer includes it. Remove it from the catalog enumValues and UI, or block it, before ingesting.")) | Out-Null
        }
    }
}

# (c) PAX parameters not covered by the catalog.
foreach ($name in $sourceParams) {
    if ($activeSet.Contains($name)) { continue }
    if ($removedSet.Contains($name)) {
        # PAX still declares a switch the Cookbook deliberately refuses to
        # emit. That is the expected, safe state (the param() gate / UI
        # block still applies). Informational only.
        $lowFindings.Add((New-Finding -Code 'REMOVED_LIST_SWITCH_STILL_PRESENT' -Parameter $name -Categories (Get-ParameterCategory -Name $name) `
            -Message "PAX still declares -$name, which the Cookbook lists as removed/unsupported and refuses to emit. Expected; no action.")) | Out-Null
        continue
    }

    $cats = Get-ParameterCategory -Name $name
    $isNewVsBaseline = ($null -ne $baselineSet) -and (-not $baselineSet.Contains($name))
    $isKnownUnexposed = $unexposedSet.Contains($name)

    if ($isNewVsBaseline) {
        # Genuinely new since the baseline and not yet covered -> review.
        $mediumFindings.Add((New-Finding -Code 'PAX_PARAM_NEW_VS_BASELINE' -Parameter $name -Categories $cats `
            -Message "The candidate PAX script adds -$name (absent in the baseline) and the Cookbook does not cover it. Review whether it should be exposed in the builder, added to the catalog, or documented as an intentional exclusion.")) | Out-Null
    }
    elseif ($isKnownUnexposed) {
        # Long-standing intentional exclusion.
        $lowFindings.Add((New-Finding -Code 'PAX_PARAM_UNCOVERED_PREEXISTING' -Parameter $name -Categories $cats `
            -Message "PAX declares -$name, which the Cookbook intentionally does not surface (secret / tuning / mode / implicit). No action.")) | Out-Null
    }
    else {
        # Uncovered and not on the intentional-exclusion list. Without a
        # baseline we cannot prove novelty, so flag for review.
        $mediumFindings.Add((New-Finding -Code 'PAX_PARAM_NEW_UNCOVERED' -Parameter $name -Categories $cats `
            -Message "PAX declares -$name, which is not covered by the catalog and is not on the intentional-exclusion list. Review whether it should be exposed, added to the catalog, or added to the exclusion list.")) | Out-Null
    }
}

# (d) Baseline diff: parameters removed since the baseline.
if ($null -ne $baselineSet) {
    foreach ($name in $baselineParams) {
        if (-not $sourceSet.Contains($name)) {
            $cats = Get-ParameterCategory -Name $name
            if ($activeSet.Contains($name)) {
                # Already reported as HIGH in (a); skip to avoid duplication.
                continue
            }
            $mediumFindings.Add((New-Finding -Code 'BASELINE_PARAM_REMOVED' -Parameter $name -Categories $cats `
                -Message "The baseline declared -$name but the candidate PAX script removed it. The Cookbook does not actively emit it, but confirm no parser/importer/advanced-args path relies on it.")) | Out-Null
        }
    }

    # Possible rename heuristic: at least one added and one removed.
    $addedVsBaseline = @($sourceParams | Where-Object { -not $baselineSet.Contains($_) })
    $removedVsBaseline = @($baselineParams | Where-Object { -not $sourceSet.Contains($_) })
    if ($addedVsBaseline.Count -gt 0 -and $removedVsBaseline.Count -gt 0) {
        $mediumFindings.Add((New-Finding -Code 'POSSIBLE_RENAME' -Parameter '(multiple)' -Categories @() `
            -Message ("The candidate adds {0} parameter(s) and removes {1} versus the baseline. Inspect for renamed switches that the catalog/parser must track. Added: {2}. Removed: {3}." -f `
                $addedVsBaseline.Count, $removedVsBaseline.Count, ($addedVsBaseline -join ', '), ($removedVsBaseline -join ', ')))) | Out-Null
    }
}

# ---------------------------------------------------------------------
# Category coverage summary
# ---------------------------------------------------------------------

$categoryNames = @('authIdentity', 'outputPath', 'dateWindow', 'm365Usage', 'entraAgent365', 'fabricSharePoint')
$categorySummary = [ordered]@{}
foreach ($cat in $categoryNames) {
    $inPax = @($sourceParams | Where-Object { (Get-ParameterCategory -Name $_) -contains $cat })
    $covered = @($inPax | Where-Object { $activeSet.Contains($_) })
    $categorySummary[$cat] = [PSCustomObject]@{
        paxParameters    = $inPax
        coveredByCatalog = $covered
    }
}

# ---------------------------------------------------------------------
# Verdict
# ---------------------------------------------------------------------

$highCount = $highFindings.Count
$mediumCount = $mediumFindings.Count
$lowCount = $lowFindings.Count

$recommendation = if ($highCount -gt 0) { 'ACTION_REQUIRED' }
    elseif ($mediumCount -gt 0) { 'REVIEW_RECOMMENDED' }
    else { 'NO_UI_UX_CHANGES_REQUIRED' }

$uiUxImpact = switch ($recommendation) {
    'ACTION_REQUIRED' { 'The candidate PAX script changes the switch contract the Cookbook depends on. Update the catalog / parser / renderer / UI / tests before ingesting.' }
    'REVIEW_RECOMMENDED' { 'The candidate PAX script introduces parameter(s) not covered by the catalog. Review whether the builder should expose them; no breaking change to existing cooks was detected.' }
    default { 'No UI/UX, parser, renderer, catalog, or test changes are required for this candidate. Every switch the Cookbook emits remains accepted by the candidate PAX script.' }
}

$baselineInfo = $null
if ($BaselinePath) {
    $baselineInfo = [PSCustomObject]@{
        path            = (Resolve-Path -LiteralPath $BaselinePath).Path
        declaredVersion = $baselineVersion
        parameterCount  = $baselineParams.Count
    }
}

$sourceInfo = [PSCustomObject]@{
    path            = (Resolve-Path -LiteralPath $SourcePath).Path
    bytes           = $sourceItem.Length
    declaredVersion = $declaredVersion
    suppliedVersion = $suppliedVersion
    parseErrors     = $sourceSurface.ParseErrors
}

$paxInfo = [PSCustomObject]@{
    count      = $sourceParams.Count
    names      = @($sourceParams)
    authValues = @($sourceAuthValues)
}

$catalogInfo = [PSCustomObject]@{
    path         = (Resolve-Path -LiteralPath $CatalogPath).Path
    version      = $catalogVersion
    activeCount  = $activeNames.Count
    removedCount = $removedNames.Count
    activeNames  = @($activeNames)
    removedNames = @($removedNames)
    authValues   = @($catalogAuthValues)
}

$findingsInfo = [PSCustomObject]@{
    high   = $highFindings.ToArray()
    medium = $mediumFindings.ToArray()
    low    = $lowFindings.ToArray()
}

$countsInfo = [PSCustomObject]@{
    high   = $highCount
    medium = $mediumCount
    low    = $lowCount
}

$report = [PSCustomObject]@{
    schemaVersion  = 1
    tool           = 'Test-PaxScriptCompatibility'
    generatedUtc   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    source         = $sourceInfo
    version        = $suppliedVersion
    baseline       = $baselineInfo
    paxParameters  = $paxInfo
    catalog        = $catalogInfo
    categories     = [PSCustomObject]$categorySummary
    findings       = $findingsInfo
    counts         = $countsInfo
    recommendation = $recommendation
    uiUxImpact     = $uiUxImpact
}

# ---------------------------------------------------------------------
# Write JSON report (optional)
# ---------------------------------------------------------------------

$reportWritten = $false
if ($ReportPath) {
    if ($PSCmdlet.ShouldProcess($ReportPath, 'Write compatibility report JSON')) {
        $dir = Split-Path -Parent $ReportPath
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        $json = $report | ConvertTo-Json -Depth 8
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($ReportPath, $json, $utf8NoBom)
        $reportWritten = $true
    }
}

# ---------------------------------------------------------------------
# Console summary
# ---------------------------------------------------------------------

$recoColor = switch ($recommendation) {
    'ACTION_REQUIRED' { 'Red' }
    'REVIEW_RECOMMENDED' { 'Yellow' }
    default { 'Green' }
}

Write-Host ''
Write-Host '=====================================================================' -ForegroundColor Cyan
Write-Host ' PAX script compatibility / impact analysis' -ForegroundColor Cyan
Write-Host '=====================================================================' -ForegroundColor Cyan
Write-Host (' Candidate     : {0}' -f $report.source.path)
Write-Host (' Declared ver  : {0}   (supplied: {1})' -f $declaredVersion, $suppliedVersion)
if ($BaselinePath) {
    Write-Host (' Baseline      : {0}  (v{1})' -f $report.baseline.path, $baselineVersion)
} else {
    Write-Host ' Baseline      : (none supplied - novelty inferred from exclusion list only)'
}
Write-Host (' PAX params    : {0}   Auth values: {1}' -f $sourceParams.Count, ($sourceAuthValues -join '/'))
Write-Host (' Catalog       : v{0}  active={1} removed={2}' -f $catalogVersion, $activeNames.Count, $removedNames.Count)
Write-Host '---------------------------------------------------------------------'
Write-Host (' HIGH   : {0}' -f $highCount) -ForegroundColor $(if ($highCount -gt 0) { 'Red' } else { 'Green' })
foreach ($f in $highFindings) { Write-Host ('   [HIGH]   {0}: {1}' -f $f.code, $f.message) -ForegroundColor Red }
Write-Host (' MEDIUM : {0}' -f $mediumCount) -ForegroundColor $(if ($mediumCount -gt 0) { 'Yellow' } else { 'Green' })
foreach ($f in $mediumFindings) { Write-Host ('   [MEDIUM] {0}: {1}' -f $f.code, $f.message) -ForegroundColor Yellow }
Write-Host (' LOW    : {0}' -f $lowCount)
Write-Host '---------------------------------------------------------------------'
Write-Host (' Recommendation: {0}' -f $recommendation) -ForegroundColor $recoColor
Write-Host (' {0}' -f $uiUxImpact)
if ($reportWritten) {
    Write-Host (' Report written: {0}' -f (Resolve-Path -LiteralPath $ReportPath).Path)
} elseif ($ReportPath) {
    Write-Host (' Report path   : {0}  (preview: not written)' -f $ReportPath)
}
Write-Host '====================================================================='
Write-Host ''

# ---------------------------------------------------------------------
# Return / exit
# ---------------------------------------------------------------------

$report

if ($FailOnHighImpact -and $highCount -gt 0) {
    exit 1
}
