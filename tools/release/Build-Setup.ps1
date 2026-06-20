#requires -Version 7.4

# =====================================================================
# Build-Setup.ps1  Build the distributable bootstrapper installer
# PAX_Cookbook_Setup.exe (lightweight, downloads payload at runtime)
# and the separate PAX_Cookbook_Payload.zip.
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
#   5. Compresses the payload into PAX_Cookbook_Payload.zip.
#   6. Publishes a self-contained single-file Setup EXE that downloads
#      the payload from GitHub at runtime (no embedded payload).
#   7. Updates versions.json (repo root) with the payload SHA-256 + size
#      so the signed Setup verifies the payload at runtime without being
#      rebuilt for routine payload updates.
#   8. Verifies artifact sizes are within expected bounds.
#
# OUTPUTS (all under gitignored folders)
#
#   artifacts\setup\payload\                  staged payload + manifest
#   artifacts\setup\PAXCookbookSetup.exe      built single-file installer
#   dist\setup\PAX_Cookbook_Setup.exe         distributable Setup (bootstrapper)
#   dist\setup\PAX_Cookbook_Payload.zip       distributable payload (Release asset)
#   artifacts\setup\build.log                 full build log
#
# HARD RULES
#
#   * The PAX engine script bytes are immutable. This script only READS
#     and COPIES resources/pax/*.ps1; it asserts the SHA-256 is unchanged.
#   * Build outputs go under artifacts\ and dist\. The ONLY repo-root file
#     written is versions.json (the payload SHA manifest Setup verifies).
#   * No network calls. No git operations. No signing.
#   * The installed App is framework-dependent (.NET 8 Desktop Runtime
#     required at runtime), matching the proven production install.
#   * Setup EXE must be < 80 MiB (self-contained, no trimming for WinForms).
#   * Payload ZIP must be < 25 MiB (framework-dependent App).
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

$expectedEngineSha = '1A9BC94783683AE1DA68EE6A86DE2106A96122B67B14EE20090E6687792E3878'

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

# The single UTC instant for THIS build. Stamped (in two formats) into both the
# staged VERSION.json (cookbook.buildTimestamp, dashed) and versions.json
# (current.builtAtUtc, ISO) so the app's reported build date and the published
# "available" build date are the SAME real time. (Previously builtAtUtc was read
# from the source VERSION.json's static releaseTimestamp, so the "Available"
# build date showed a stale placeholder date.)
$buildUtc = (Get-Date).ToUniversalTime()

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
$pubSetupFd    = Join-Path $out 'publish\Setup-fd'
$pubSetupFinal = Join-Path $out 'publish\Setup'
$payload       = Join-Path $out 'payload'
$payloadZip    = Join-Path $out 'PAXCookbookPayload.zip'
New-Item -ItemType Directory -Force -Path $pubApp,$pubSetupStage,$pubSetupFd,$pubSetupFinal,$payload | Out-Null

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

    # Stamp this build's UTC timestamp into the STAGED VERSION.json only (a build
    # output). The source app\VERSION.json is left untouched so it does not churn
    # on every build. The broker reads cookbook.buildTimestamp at runtime and the
    # Settings page shows it as the build date.
    $stagedVersionJson = Join-Path $appDest 'VERSION.json'
    $buildTimestamp = $buildUtc.ToString('yyyy-MM-dd-HH-mm-ss-UTC')
    $vj = Get-Content -LiteralPath $stagedVersionJson -Raw | ConvertFrom-Json
    if ($null -eq $vj.cookbook) { throw "staged VERSION.json missing cookbook object: $stagedVersionJson" }
    if ($vj.cookbook.PSObject.Properties.Name -contains 'buildTimestamp') {
        $vj.cookbook.buildTimestamp = $buildTimestamp
    } else {
        $vj.cookbook | Add-Member -NotePropertyName 'buildTimestamp' -NotePropertyValue $buildTimestamp
    }
    $vj | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $stagedVersionJson -Encoding UTF8
    Log "  stamped build timestamp into staged VERSION.json: $buildTimestamp"

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

# ---------------------------------------------------------------------
# [4b/8] Publish framework-dependent Setup runtime into payload\Setup
# ---------------------------------------------------------------------
# The installed Setup is run by the Microsoft-signed dotnet.exe host
# (dotnet.exe "<installRoot>\Setup\PAXCookbookSetup.dll" <verb>), which is
# WDAC-safe. The unsigned single-file Setup apphost is BLOCKED by WDAC, so
# Add/Remove Programs uninstall (and repair/upgrade) must run the DLL through
# dotnet.exe — the same proven launch model as the app itself. We publish
# framework-dependent with UseAppHost=false (NO unsigned EXE emitted) and ship
# only the managed runtime files; the WinForms / .NET 8 Desktop assemblies come
# from the installed .NET 8 Desktop Runtime prerequisite.
Invoke-Step '[4b/8] publish Setup framework-dependent (payload\Setup)' {
    dotnet publish (Join-Path $root 'src\PAXCookbookSetup\PAXCookbookSetup.csproj') `
        -c $Configuration --self-contained false `
        -p:UseAppHost=false -p:DebugType=none -p:DebugSymbols=false `
        -o $pubSetupFd --nologo 2>&1 |
        Tee-Object -Append -FilePath $logFile | Out-Null
}
$setupDllPublished = Join-Path $pubSetupFd 'PAXCookbookSetup.dll'
if (-not (Test-Path -LiteralPath $setupDllPublished)) {
    throw "framework-dependent Setup publish did not produce PAXCookbookSetup.dll at: $setupDllPublished"
}
# Stage only the managed runtime files the installed Setup needs to boot under
# dotnet.exe. No .pdb, no unsigned apphost.
$setupStageDir = Join-Path $payload 'Setup'
New-Item -ItemType Directory -Force -Path $setupStageDir | Out-Null
$setupFdFiles = @(
    'PAXCookbookSetup.dll'
    'PAXCookbookSetup.runtimeconfig.json'
    'PAXCookbookSetup.deps.json'
    'PAXCookbook.Shared.dll'
)
foreach ($f in $setupFdFiles) {
    $srcF = Join-Path $pubSetupFd $f
    if (-not (Test-Path -LiteralPath $srcF)) {
        throw "framework-dependent Setup publish missing required file: $f"
    }
    Copy-Item $srcF (Join-Path $setupStageDir $f) -Force
}
# The installed Setup runs via the Microsoft-signed dotnet.exe host directly
# (the ARP UninstallString is `dotnet.exe "...PAXCookbookSetup.dll" uninstall`).
# No wscript / uninstall.vbs is shipped -- strict corporate WDAC blocks script
# hosts, and Setup hides its own console window at startup for the interactive
# uninstall.
Log ("  staged framework-dependent Setup: {0} files into payload\Setup" -f $setupFdFiles.Count)

# ---------------------------------------------------------------------
# [5/7] Write manifest.json
# ---------------------------------------------------------------------
Invoke-Step '[5/7] write manifest.json' {
    $appExeStaged = Join-Path $payload 'App\bin\PAX Cookbook.exe'
    if (-not (Test-Path -LiteralPath $appExeStaged)) { throw "missing staged App exe: $appExeStaged" }

    $requiredRuntime = @(
        'App\bin\PAX Cookbook.exe'
        'App\bin\PAX Cookbook.dll'
        'App\broker\Start-Broker.ps1'
        'App\web\index.html'
        'App\resources\manifest.json'
        'App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1'
        'App\VERSION.json'
        'App\launcher\RuntimeDiscovery.psm1'
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
    # Framework-dependent Setup runtime under payload\Setup\ (installed and
    # hash-verified like every other payload file). The installed Setup runs via
    # the signed dotnet.exe host for WDAC-safe uninstall/repair/upgrade.
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
                sha256 = HashOf $stage1Exe
                sizeBytes = SizeOf $stage1Exe
                note = 'Bootstrapper apphost metadata; not installed. The installed Setup is the framework-dependent PAXCookbookSetup.dll under Setup\ (in files[]), run via dotnet.exe.'
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
# [6/8] Zip the payload
# ---------------------------------------------------------------------
Invoke-Step '[6/8] zip payload tree' {
    if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($payload, $payloadZip,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Log ("  payload zip size: {0:N1} MiB" -f ((Get-Item $payloadZip).Length / 1MB))
}

# ---------------------------------------------------------------------
# [7/8] Publish lightweight Setup (no embedded payload)
# ---------------------------------------------------------------------
# The bootstrapper Setup downloads the payload from GitHub at runtime.
# Note: WinForms does not support IL trimming, so we keep it self-contained
# but unembedded. The major size reduction comes from the framework-dependent
# App payload.
Invoke-Step '[7/8] publish Setup (self-contained, no payload)' {
    dotnet publish (Join-Path $root 'src\PAXCookbookSetup\PAXCookbookSetup.csproj') `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=embedded `
        -o $pubSetupFinal --nologo 2>&1 |
        Tee-Object -Append -FilePath $logFile | Out-Null
}

$finalSrc = Join-Path $pubSetupFinal 'PAXCookbookSetup.exe'
if (-not (Test-Path -LiteralPath $finalSrc)) { throw "final Setup EXE missing: $finalSrc" }
$builtExe = Join-Path $out 'PAXCookbookSetup.exe'
Copy-Item $finalSrc $builtExe -Force

# Distributable names (user-facing downloads / GitHub Release assets).
$distExe = Join-Path $distDir 'PAX_Cookbook_Setup.exe'
$distPayload = Join-Path $distDir 'PAX_Cookbook_Payload.zip'
Copy-Item $finalSrc $distExe -Force
Copy-Item $payloadZip $distPayload -Force

$finalSize = (Get-Item $distExe).Length
$finalHash = HashOf $distExe
$payloadSize = (Get-Item $distPayload).Length
$payloadHash = HashOf $distPayload

Log ''
Log "ARTIFACT (Setup)   : $distExe"
Log ("  size  : {0:N1} MiB ({1} bytes)" -f ($finalSize/1MB), $finalSize)
Log "  sha256: $finalHash"
Log ''
Log "ARTIFACT (Payload) : $distPayload"
Log ("  size  : {0:N1} MiB ({1} bytes)" -f ($payloadSize/1MB), $payloadSize)
Log "  sha256: $payloadHash"

# ---------------------------------------------------------------------
# Update versions.json (repo root) — the payload SHA manifest.
#
# Decouples the signed Setup EXE from the payload: Setup downloads this
# file at runtime and verifies the payload zip against the sha256/size
# below, so a payload update only requires committing this manifest — the
# Setup EXE never needs rebuilding/re-signing for routine payload updates.
# minimumSetupVersion is preserved from the existing file (only bumped
# deliberately when a payload genuinely requires a newer Setup).
# ---------------------------------------------------------------------
Invoke-Step 'update versions.json manifest' {
    $versionsPath = Join-Path $root 'versions.json'
    $minSetup = '1.0.0'
    if (Test-Path -LiteralPath $versionsPath) {
        try {
            $existing = Get-Content -LiteralPath $versionsPath -Raw | ConvertFrom-Json
            if ($existing.current -and $existing.current.minimumSetupVersion) {
                $minSetup = [string]$existing.current.minimumSetupVersion
            }
        } catch { }
    }
    # The published "available" build date. Use the SAME real build instant that
    # was stamped into the staged VERSION.json (cookbook.buildTimestamp), in ISO
    # 8601 UTC. The in-app updater uses this only for DISPLAY (the up-to-date
    # decision is by payload SHA, not by timestamp), so a real instant here makes
    # the "Available" card show the actual build time instead of a stale
    # placeholder.
    $builtAtUtc = $buildUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $vm = [ordered]@{
        schemaVersion = 1
        current = [ordered]@{
            version = $AppVersion
            builtAtUtc = $builtAtUtc
            payload = [ordered]@{
                filename = 'PAX_Cookbook_Payload.zip'
                sha256   = $payloadHash
                size     = $payloadSize
            }
            setup = [ordered]@{
                filename = 'PAX_Cookbook_Setup.exe'
                sha256   = $finalHash
                size     = $finalSize
            }
            engine = [ordered]@{
                version = $versionInfo.paxScript.version
                sha256  = $expectedEngineSha.ToLowerInvariant()
            }
            minimumSetupVersion = $minSetup
        }
    }
    $vm | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $versionsPath -Encoding UTF8
    Log "  versions.json updated: payload sha=$payloadHash setup sha=$finalHash engine=$($versionInfo.paxScript.version) builtAtUtc=$builtAtUtc"
}

# ---------------------------------------------------------------------
# [8/8] Size verification gates
# ---------------------------------------------------------------------
Invoke-Step '[8/8] verify artifact sizes' {
    $maxSetupBytes = 80 * 1024 * 1024   # 80 MiB max for self-contained Setup (no trimming, WinForms)
    $maxPayloadBytes = 25 * 1024 * 1024 # 25 MiB max for framework-dependent payload
    
    if ($finalSize -gt $maxSetupBytes) {
        throw "Setup EXE too large: {0:N1} MiB > 80 MiB limit. Payload may be embedded or build issue." -f ($finalSize/1MB)
    }
    if ($payloadSize -gt $maxPayloadBytes) {
        throw "Payload ZIP too large: {0:N1} MiB > 25 MiB limit. Self-contained runtime may have leaked in." -f ($payloadSize/1MB)
    }
    Log ("  Setup  : {0:N1} MiB (< 80 MiB limit) PASS" -f ($finalSize/1MB))
    Log ("  Payload: {0:N1} MiB (< 25 MiB limit) PASS" -f ($payloadSize/1MB))
}

Log ''
Log 'Build complete. TWO artifacts produced:'
Log "  1. $distExe (bootstrapper, downloads payload at runtime)"
Log "  2. $distPayload (uploaded to GitHub Release)"

Write-Host ''
Write-Host "DISTRIBUTABLE (Setup)  : $distExe"
Write-Host ("  SIZE: {0:N1} MiB" -f ($finalSize/1MB))
Write-Host "  SHA256: $finalHash"
Write-Host ''
Write-Host "DISTRIBUTABLE (Payload): $distPayload"
Write-Host ("  SIZE: {0:N1} MiB" -f ($payloadSize/1MB))
Write-Host "  SHA256: $payloadHash"
