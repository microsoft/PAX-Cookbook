#requires -Version 7.4

# =====================================================================
# Build-Setup.ps1  Build the distributable single-file installer
# PAX_Cookbook_Setup.exe with an embedded payload.
#
# WHAT THIS SCRIPT DOES
#
#   1. Publishes the C# native broker (PAXCookbook.App) framework-
#      dependent, producing "PAX Cookbook.exe" and its sidecars.
#   2. Builds the React product shell (emitted into app/web/app).
#   3. Stages the installable App tree (the current appliance file set)
#      plus a self-contained stage-1 Setup runtime used for in-place
#      repair/update.
#   4. Writes the payload manifest (manifest.json) with SHA-256 + size
#      for every installed file.
#   5. Compresses the payload and publishes a self-contained single-file
#      Setup EXE that carries the payload as an embedded resource.
#   6. Emits the distributable as PAX_Cookbook_Setup.exe.
#
# OUTPUTS (all under gitignored folders)
#
#   artifacts\setup\payload\                  staged payload + manifest
#   artifacts\setup\PAXCookbookSetup.exe      built single-file installer
#   dist\setup\PAX_Cookbook_Setup.exe         distributable / Release asset
#   artifacts\setup\build.log                 full build log
#
# HARD RULES
#
#   * The PAX engine script bytes are immutable. This script only READS
#     and COPIES resources/pax/*.ps1; it asserts the SHA-256 is unchanged.
#   * Output goes ONLY under artifacts\ and dist\. Never to repo root.
#   * No network calls. No git operations. No signing.
#   * The installed App is framework-dependent (.NET 8 Desktop Runtime
#     required at runtime), matching the proven production install.
# =====================================================================

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$AppVersion    = '',
    [string]$SetupVersion  = '',
    [string]$BuildId       = '',
    [switch]$SkipReactBuild
)

$ErrorActionPreference = 'Stop'

$expectedEngineSha = '0DD230734715ABD15CF4C0A76013672BF9AD6713C3F82520A6333B0DCDAAD361'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\','/')
Set-Location $root

# ---------------------------------------------------------------------
# Canonical version from app\VERSION.json
# ---------------------------------------------------------------------
$versionJson = Join-Path $root 'app\VERSION.json'
if (-not (Test-Path $versionJson)) { throw "VERSION.json missing at: $versionJson" }
$versionInfo = Get-Content $versionJson -Raw | ConvertFrom-Json
$canonicalVersion = $versionInfo.cookbook.version
$channel = $versionInfo.channel
if ([string]::IsNullOrWhiteSpace($canonicalVersion)) { throw 'VERSION.json: cookbook.version is empty.' }
if ([string]::IsNullOrWhiteSpace($channel)) { $channel = 'stable' }
if ([string]::IsNullOrWhiteSpace($AppVersion))   { $AppVersion   = $canonicalVersion }
if ([string]::IsNullOrWhiteSpace($SetupVersion)) { $SetupVersion = $canonicalVersion }

$out = Join-Path $root 'artifacts\setup'
$distDir = Join-Path $root 'dist\setup'
$logFile = Join-Path $out 'build.log'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

function Log([string]$m) {
    $line = "[$(Get-Date -Format o)] $m"
    Write-Host $line
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
}

function Invoke-Step([string]$label, [scriptblock]$body) {
    Log "==> $label"
    & $body
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "step failed: $label (exit $LASTEXITCODE)"
    }
}

function HashOf([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function SizeOf([string]$p) { (Get-Item -LiteralPath $p).Length }

$pubApp        = Join-Path $out 'publish\App'
$pubSetupStage = Join-Path $out 'publish\Setup-stage1'
$pubSetupFinal = Join-Path $out 'publish\Setup'
$payload       = Join-Path $out 'payload'
$payloadZip    = Join-Path $out 'PAXCookbookPayload.zip'
New-Item -ItemType Directory -Force -Path $pubApp,$pubSetupStage,$pubSetupFinal,$payload | Out-Null

Log "PAX Cookbook single-file Setup build"
Log "  repo            : $root"
Log "  app version     : $AppVersion"
Log "  setup version   : $SetupVersion"
Log "  channel         : $channel"

# ---------------------------------------------------------------------
# [1/7] Publish the native broker (framework-dependent, matches install)
# ---------------------------------------------------------------------
Invoke-Step '[1/7] publish PAXCookbook.App (framework-dependent)' {
    dotnet publish (Join-Path $root 'src\PAXCookbook.App\PAXCookbook.App.csproj') `
        -c $Configuration -o $pubApp --nologo 2>&1 |
        Tee-Object -Append -FilePath $logFile | Out-Null
}
$appExePublished = Join-Path $pubApp 'PAX Cookbook.exe'
if (-not (Test-Path -LiteralPath $appExePublished)) {
    throw "App publish did not produce 'PAX Cookbook.exe' at: $appExePublished"
}

# ---------------------------------------------------------------------
# [2/7] Build the React product shell (emits to app\web\app)
# ---------------------------------------------------------------------
if ($SkipReactBuild) {
    Log '[2/7] skip React build (-SkipReactBuild); staging existing app\web\app'
} else {
    Invoke-Step '[2/7] build React shell (npm run build)' {
        Push-Location (Join-Path $root 'app\web-react')
        try {
            if (-not (Test-Path 'node_modules')) {
                Log '  node_modules missing; running npm ci'
                npm ci 2>&1 | Tee-Object -Append -FilePath $logFile | Out-Null
            }
            npm run build 2>&1 | Tee-Object -Append -FilePath $logFile | Out-Null
        } finally { Pop-Location }
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $root 'app\web\app\index.html'))) {
    throw 'React build output missing: app\web\app\index.html'
}

# ---------------------------------------------------------------------
# [3/7] Stage the payload App tree
# ---------------------------------------------------------------------
Invoke-Step '[3/7] stage payload tree' {
    $appDest = Join-Path $payload 'App'
    $appBinDest = Join-Path $appDest 'bin'
    New-Item -ItemType Directory -Force -Path $appBinDest | Out-Null
    Copy-Item (Join-Path $pubApp '*') $appBinDest -Recurse -Force

    # Current appliance runtime subtrees (read-only stage from app\).
    $runtimeDirs = @('broker','web','lib','resources','templates','launcher','install')
    foreach ($d in $runtimeDirs) {
        $src = Join-Path $root ('app\' + $d)
        if (-not (Test-Path -LiteralPath $src)) { throw "missing required runtime source dir: $src" }
        $dst = Join-Path $appDest $d
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Copy-Item (Join-Path $src '*') $dst -Recurse -Force
    }
    Copy-Item (Join-Path $root 'app\VERSION.json') (Join-Path $appDest 'VERSION.json') -Force

    # Defensive scrub: never ship dev/build artifacts in the payload.
    Get-ChildItem $appDest -Recurse -Force -Directory |
        Where-Object { $_.Name -in @('bin','obj','node_modules','_temp','_archive','_backup','.git','.vs') -and $_.FullName -ne $appBinDest } |
        ForEach-Object {
            Log ("  scrub: removing {0}" -f $_.FullName)
            Remove-Item $_.FullName -Recurse -Force
        }
}

# Engine immutability gate.
$stagedEngine = Join-Path $payload 'App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1'
if (-not (Test-Path -LiteralPath $stagedEngine)) { throw "staged engine missing: $stagedEngine" }
$stagedEngineSha = HashOf $stagedEngine
if ($stagedEngineSha -ne $expectedEngineSha.ToLowerInvariant()) {
    throw "ENGINE BYTES CHANGED. expected $expectedEngineSha got $stagedEngineSha"
}
Log "  engine sha verified immutable: $stagedEngineSha"

# ---------------------------------------------------------------------
# [4/7] Publish stage-1 Setup (self-contained single-file, no payload)
# ---------------------------------------------------------------------
Invoke-Step '[4/7] publish Setup stage-1 (self-contained, no payload)' {
    dotnet publish (Join-Path $root 'src\PAXCookbookSetup\PAXCookbookSetup.csproj') `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=embedded `
        -o $pubSetupStage --nologo 2>&1 |
        Tee-Object -Append -FilePath $logFile | Out-Null
}
$stage1Exe = Join-Path $pubSetupStage 'PAXCookbookSetup.exe'
if (-not (Test-Path -LiteralPath $stage1Exe)) { throw "stage-1 Setup EXE missing: $stage1Exe" }

# Setup\* (installed repair/update runtime) + root copy (manifest target).
$setupDest = Join-Path $payload 'Setup'
New-Item -ItemType Directory -Force -Path $setupDest | Out-Null
Copy-Item (Join-Path $pubSetupStage '*') $setupDest -Recurse -Force
Copy-Item $stage1Exe (Join-Path $payload 'PAXCookbookSetup.exe') -Force

# ---------------------------------------------------------------------
# [5/7] Write manifest.json
# ---------------------------------------------------------------------
Invoke-Step '[5/7] write manifest.json' {
    $appExeStaged   = Join-Path $payload 'App\bin\PAX Cookbook.exe'
    $setupExeStaged = Join-Path $payload 'PAXCookbookSetup.exe'
    if (-not (Test-Path -LiteralPath $appExeStaged))   { throw "missing staged App exe:   $appExeStaged" }
    if (-not (Test-Path -LiteralPath $setupExeStaged)) { throw "missing staged Setup exe: $setupExeStaged" }

    $requiredRuntime = @(
        'App\bin\PAX Cookbook.exe'
        'App\broker\Start-Broker.ps1'
        'App\web\index.html'
        'App\resources\manifest.json'
        'App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1'
        'App\VERSION.json'
        'App\launcher\RuntimeDiscovery.psm1'
        'App\install\Install-PAXCookbook.ps1'
    )
    foreach ($r in $requiredRuntime) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $r))) {
            throw "required runtime file missing from staged payload: $r"
        }
    }
    foreach ($d in @('App\lib\sqlite','App\templates')) {
        $dd = Join-Path $payload $d
        if (-not (Test-Path -LiteralPath $dd)) { throw "required runtime dir missing: $d" }
        if (@(Get-ChildItem -LiteralPath $dd -File -Recurse).Count -lt 1) {
            throw "required runtime dir is empty: $d"
        }
    }

    $files = [System.Collections.Generic.List[object]]::new()
    # Every file under payload\App\ except the AppExe (dedicated entry).
    Get-ChildItem (Join-Path $payload 'App') -File -Recurse | ForEach-Object {
        $rel = $_.FullName.Substring($payload.Length).TrimStart('\','/')
        if ($rel -ieq 'App\bin\PAX Cookbook.exe') { return }
        $files.Add([ordered]@{
            relativeInstallPath = $rel
            sha256 = HashOf $_.FullName
            sizeBytes = $_.Length
        })
    }
    # Setup\* subtree entries.
    Get-ChildItem (Join-Path $payload 'Setup') -File -Recurse | ForEach-Object {
        $rel = $_.FullName.Substring($payload.Length).TrimStart('\','/')
        $files.Add([ordered]@{
            relativeInstallPath = $rel
            sha256 = HashOf $_.FullName
            sizeBytes = $_.Length
        })
    }

    $build = if ([string]::IsNullOrWhiteSpace($BuildId)) {
        'setup-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    } else { $BuildId }

    $m = [ordered]@{
        product = 'PAXCookbook'
        manifestSchemaVersion = 1
        appVersion = $AppVersion
        setupVersion = $SetupVersion
        buildId = $build
        builtAtUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        channel = $channel
        targetOs = 'windows'
        targetArch = 'x64'
        webView2RuntimeRequirement = [ordered]@{
            minimumPv = '0.0.0'
            detectionPaths = @(
                'HKLM64\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}\pv',
                'HKLM32\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}\pv',
                'HKCU\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}\pv'
            )
        }
        payload = [ordered]@{
            setupExe = [ordered]@{
                name = 'PAXCookbookSetup.exe'
                sha256 = HashOf $setupExeStaged
                sizeBytes = SizeOf $setupExeStaged
            }
            appExe = [ordered]@{
                name = 'PAX Cookbook.exe'
                sha256 = HashOf $appExeStaged
                sizeBytes = SizeOf $appExeStaged
                relativeInstallPath = 'App\bin\PAX Cookbook.exe'
            }
            files = $files.ToArray()
        }
    }
    $m | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding UTF8
    Log ("  manifest files count: {0}" -f $files.Count)
}

# ---------------------------------------------------------------------
# [6/7] Zip the payload
# ---------------------------------------------------------------------
Invoke-Step '[6/7] zip payload tree' {
    if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($payload, $payloadZip,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Log ("  payload zip size: {0:N1} MiB" -f ((Get-Item $payloadZip).Length / 1MB))
}

# ---------------------------------------------------------------------
# [7/7] Publish final Setup with embedded payload
# ---------------------------------------------------------------------
Invoke-Step '[7/7] publish Setup (self-contained single-file + embedded payload)' {
    dotnet publish (Join-Path $root 'src\PAXCookbookSetup\PAXCookbookSetup.csproj') `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=embedded `
        "-p:EmbeddedPayloadZip=$payloadZip" `
        -o $pubSetupFinal --nologo 2>&1 |
        Tee-Object -Append -FilePath $logFile | Out-Null
}

$finalSrc = Join-Path $pubSetupFinal 'PAXCookbookSetup.exe'
if (-not (Test-Path -LiteralPath $finalSrc)) { throw "final Setup EXE missing: $finalSrc" }
$builtExe = Join-Path $out 'PAXCookbookSetup.exe'
Copy-Item $finalSrc $builtExe -Force

# Distributable name (user-facing download / GitHub Release asset).
$distExe = Join-Path $distDir 'PAX_Cookbook_Setup.exe'
Copy-Item $finalSrc $distExe -Force

$finalSize = (Get-Item $distExe).Length
$finalHash = HashOf $distExe
Log ''
Log "ARTIFACT (built)        : $builtExe"
Log "ARTIFACT (distributable): $distExe"
Log ("  size  : {0:N1} MiB ({1} bytes)" -f ($finalSize/1MB), $finalSize)
Log "  sha256: $finalHash"
Log ''
Log 'Build complete.'

Write-Host ''
Write-Host "DISTRIBUTABLE: $distExe"
Write-Host ("SIZE: {0:N1} MiB" -f ($finalSize/1MB))
Write-Host "SHA256: $finalHash"
