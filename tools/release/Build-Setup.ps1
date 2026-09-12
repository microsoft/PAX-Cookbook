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
#   7. Updates the channel-authoritative versions.json with the payload
#      SHA-256 + size so Setup verifies the payload at runtime.
#   8. Verifies artifact sizes are within expected bounds.
#
# OUTPUTS (all under gitignored folders)
#
#   artifacts\setup\<channel>\payload\             staged payload + manifest
#   artifacts\setup\<channel>\PAXCookbookSetup.exe built single-file installer
#   dist\<channel>\PAX_Cookbook_Setup.exe           distributable Setup
#   dist\<channel>\PAX_Cookbook_Payload.zip         distributable payload
#   artifacts\setup\<channel>\build.log             full build log
#
# HARD RULES
#
#   * The PAX engine script bytes are immutable. This script only READS
#     and COPIES resources/pax/*.ps1; it asserts the SHA-256 is unchanged.
#   * Build outputs go under channel-specific artifacts\ and dist\ roots.
#     Only stable may write repository-root versions.json.
#   * No network calls. No git operations. No signing.
#   * The installed App is framework-dependent (.NET 8 Desktop Runtime
#     required at runtime), matching the proven production install.
#   * Setup EXE must be < 80 MiB (self-contained, no trimming for WinForms).
#   * Payload ZIP must be <= 100 MiB. The payload now also carries ONE
#     self-contained service administrative helper (see below), so the older
#     25 MiB ceiling no longer describes a correct payload.
#
# PRERELEASE SERVICE ADMINISTRATIVE HELPER - SECURITY CLASSIFICATION
#
#   The payload carries exactly ONE self-contained, directly launched service
#   administrative helper at payload\Setup\PAXCookbookServiceAdminHelper.exe,
#   with a deterministic service payload embedded inside it.
#
#   That helper is UNSIGNED PRERELEASE CODE. It is accepted only for internal
#   prerelease development and attended validation. An unsigned helper does not
#   resist replacement by a local user; UAC may display an unknown publisher;
#   prerelease validation proves functionality, not production tamper
#   resistance. This is unacceptable for GA. GA activation remains BLOCKED
#   until the exact helper is Authenticode signed and the expected publisher
#   policy is configured and verified (cycle-60 ruling).
#
#   Nothing in this script signs anything, elevates anything, registers a
#   service, writes ProgramData or HKLM, or launches the helper.
# =====================================================================

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$AppVersion    = '',
    [string]$SetupVersion  = '',
    [string]$BuildId       = '',
    [string]$Channel       = '',
    [switch]$SkipReactBuild
)

$ErrorActionPreference = 'Stop'

$expectedEngineSha = '99AB97232C76022771197B84AF880ED48D9E4B83F678F05262C38A223D3814C9'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\','/')
Set-Location $root

# ---------------------------------------------------------------------
# Resolve the one release contract from app\VERSION.json and explicit inputs.
# ---------------------------------------------------------------------
$versionJson = Join-Path $root 'app\VERSION.json'
$releaseContractModule = Join-Path $PSScriptRoot 'SetupReleaseContract.psm1'
Import-Module $releaseContractModule -Force -ErrorAction Stop
$release = Resolve-PaxSetupReleaseContract `
    -RepositoryRoot $root `
    -SourceVersionPath $versionJson `
    -AppVersion $AppVersion `
    -SetupVersion $SetupVersion `
    -BuildId $BuildId `
    -Channel $Channel
$versionInfo = Get-Content -LiteralPath $versionJson -Raw | ConvertFrom-Json -Depth 32
$AppVersion = $release.DisplayVersion
$SetupVersion = $release.DisplayVersion
$BuildId = $release.Provenance
$channel = $release.Channel
$versionProperties = @(
    "-p:Version=$($release.DisplayVersion)"
    "-p:AssemblyVersion=$($release.AssemblyVersion)"
    "-p:FileVersion=$($release.FileVersion)"
    "-p:InformationalVersion=$($release.InformationalVersion)"
    '-p:IncludeSourceRevisionInInformationalVersion=false'
)

# The single UTC instant for THIS build. Stamped (in two formats) into both the
# staged VERSION.json (cookbook.buildTimestamp, dashed) and versions.json
# (current.builtAtUtc, ISO) so the app's reported build date and the published
# "available" build date are the SAME real time. (Previously builtAtUtc was read
# from the source VERSION.json's static releaseTimestamp, so the "Available"
# build date showed a stale placeholder date.)
$buildUtc = (Get-Date).ToUniversalTime()

$out = $release.BuildRoot
$distDir = $release.DistRoot
$versionsPath = $release.VersionsPath
$authoritativeVersionJson = $release.BuildVersionPath
$logFile = Join-Path $out 'build.log'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# Materialize the build-authoritative VERSION.json before the first publish.
# The payload receives these exact bytes later; binaries receive stamps from
# the same resolved contract object.
$versionInfo.cookbook.version = $release.DisplayVersion
$buildTimestamp = $buildUtc.ToString('yyyy-MM-dd-HH-mm-ss-UTC')
if ($versionInfo.cookbook.PSObject.Properties.Name -contains 'buildTimestamp') {
    $versionInfo.cookbook.buildTimestamp = $buildTimestamp
} else {
    $versionInfo.cookbook | Add-Member -NotePropertyName 'buildTimestamp' -NotePropertyValue $buildTimestamp
}
$versionInfo.channel = $release.Channel
if ($release.Channel -ceq 'internal') { $versionInfo.updateManifestUrl = $null }
Assert-PaxSetupReleaseWritePath -Contract $release -Path $authoritativeVersionJson -Purpose build | Out-Null
[IO.File]::WriteAllText(
    $authoritativeVersionJson,
    ($versionInfo | ConvertTo-Json -Depth 32),
    [Text.UTF8Encoding]::new($false))

function Log([string]$m) {
    $line = "[$(Get-Date -Format o)] $m"
    Write-Host $line
    $sourcePath = Join-Path ([System.IO.Path]::GetTempPath()) ("pax-build-log-{0}.tmp" -f [Guid]::NewGuid().ToString('N'))
    try {
        if (Test-Path -LiteralPath $logFile) {
            [System.IO.File]::Copy($logFile, $sourcePath, $false)
        }
        Add-Content -LiteralPath $sourcePath -Value $line -Encoding UTF8
        $p1Path = Join-Path $PSScriptRoot '..\guards\Invoke-P1ArtifactPersistencePathPolicy.ps1'
        if (Test-Path -LiteralPath $p1Path -PathType Leaf) {
            $p1Arguments = @(
                '-NoProfile', '-File', $p1Path,
                '-Mode', 'Persist',
                '-DestinationPath', $logFile,
                '-SourcePath', $sourcePath,
                '-ExpectedSourceSha256', '7B5257D14BC4D590AEC60BC08A9FB21A72721B93A3B8739AF9CDD77BAFEE5D57'
            )
            & ([Environment]::ProcessPath) @p1Arguments *> $null
            if ($LASTEXITCODE -ne 0) { throw 'Build log persistence failed.' }
        } else {
            [System.IO.File]::Copy($sourcePath, $logFile, $true)
        }
    } finally {
        Remove-Item -LiteralPath $sourcePath -Force -ErrorAction SilentlyContinue
    }
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
$pubServiceFd  = Join-Path $out 'publish\Service-fd'
$pubHelper     = Join-Path $out 'publish\ServiceAdminHelper'
$payload       = Join-Path $out 'payload'
$payloadZip    = Join-Path $out 'PAXCookbookPayload.zip'
$servicePayloadArchive = Join-Path $out 'service-payload.zip'
New-Item -ItemType Directory -Force -Path $pubApp,$pubSetupStage,$pubSetupFd,$pubSetupFinal,$pubServiceFd,$pubHelper,$payload | Out-Null

Log "PAX Cookbook single-file Setup build"
Log "  repo            : $root"
Log "  app version     : $AppVersion"
Log "  setup version   : $SetupVersion"
Log "  channel         : $channel"

# ---------------------------------------------------------------------
# [1/7] Publish the native broker (framework-dependent, matches install)
# ---------------------------------------------------------------------
Invoke-Step '[1/7] publish PAXCookbook.App (framework-dependent)' {
    # The experimental channel carries the gated Entra WAM (work-account) provider,
    # so its App payload is published with the ExperimentalWam feature build
    # (adds the MSAL broker dependencies). The stable channel publishes without it
    # and therefore contains no OAuth broker.
    $appPublishArgs = @('-c', $Configuration, '-o', $pubApp, '--nologo')
    $appPublishArgs += $versionProperties
    if ($channel -eq 'experimental') { $appPublishArgs += '-p:ExperimentalWam=true' }
    dotnet publish (Join-Path $root 'src\PAXCookbook.App\PAXCookbook.App.csproj') `
        @appPublishArgs 2>&1 |
        Tee-Object -Append -FilePath $logFile | Out-Null
}
$appExePublished = Join-Path $pubApp 'PAX Cookbook.exe'
if (-not (Test-Path -LiteralPath $appExePublished)) {
    throw "App publish did not produce 'PAX Cookbook.exe' at: $appExePublished"
}
$appDllPublished = Join-Path $pubApp 'PAX Cookbook.dll'
Test-PaxFileVersionArtifact -Path $appExePublished -ExpectedFileVersion $release.FileVersion | Out-Null
Test-PaxManagedAssemblyMetadata `
    -Path $appDllPublished `
    -ExpectedAssemblyVersion $release.AssemblyVersion `
    -ExpectedInformationalVersion $release.InformationalVersion | Out-Null

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
    $entraHelperSource = Join-Path $root 'tools\entra\New-PaxCookbookEntraWamSetup.ps1'
    if (-not (Test-Path -LiteralPath $entraHelperSource -PathType Leaf)) {
        throw "missing required Entra helper: $entraHelperSource"
    }
    $entraHelperDestination = Join-Path $payload 'tools\entra\New-PaxCookbookEntraWamSetup.ps1'
    New-Item -ItemType Directory -Force -Path (Split-Path $entraHelperDestination) | Out-Null
    Copy-Item -LiteralPath $entraHelperSource -Destination $entraHelperDestination -Force
    # Copy the pre-publish authoritative document byte-for-byte. No staged copy
    # is reparsed or independently stamped.
    $stagedVersionJson = Join-Path $appDest 'VERSION.json'
    Copy-Item -LiteralPath $authoritativeVersionJson -Destination $stagedVersionJson -Force
    $authoritativeBytes = [IO.File]::ReadAllBytes($authoritativeVersionJson)
    $stagedBytes = [IO.File]::ReadAllBytes($stagedVersionJson)
    if (-not [Linq.Enumerable]::SequenceEqual[byte]($authoritativeBytes, $stagedBytes)) {
        throw 'payload App/VERSION.json differs from the build-authoritative document'
    }
    Log "  staged authoritative VERSION.json: version=$AppVersion channel=$channel buildTimestamp=$buildTimestamp"

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
        @versionProperties `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=embedded `
        -p:PaxChannel=$channel `
        -o $pubSetupStage --nologo 2>&1 |
        Tee-Object -Append -FilePath $logFile | Out-Null
}
$stage1Exe = Join-Path $pubSetupStage 'PAXCookbookSetup.exe'
if (-not (Test-Path -LiteralPath $stage1Exe)) { throw "stage-1 Setup EXE missing: $stage1Exe" }
Test-PaxFileVersionArtifact -Path $stage1Exe -ExpectedFileVersion $release.FileVersion | Out-Null

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
    # Attended-prerelease unsigned-helper opt-in for the INSTALLED Setup only.
    dotnet publish (Join-Path $root 'src\PAXCookbookSetup\PAXCookbookSetup.csproj') `
        @versionProperties `
        -c $Configuration --self-contained false `
        -p:UseAppHost=false -p:DebugType=none -p:DebugSymbols=false `
        -p:PaxChannel=$channel `
        -p:PrereleaseUnsignedHelper=true `
        -o $pubSetupFd --nologo 2>&1 |
        Tee-Object -Append -FilePath $logFile | Out-Null
}
$setupDllPublished = Join-Path $pubSetupFd 'PAXCookbookSetup.dll'
if (-not (Test-Path -LiteralPath $setupDllPublished)) {
    throw "framework-dependent Setup publish did not produce PAXCookbookSetup.dll at: $setupDllPublished"
}
Test-PaxFileVersionArtifact -Path $setupDllPublished -ExpectedFileVersion $release.FileVersion | Out-Null
Test-PaxManagedAssemblyMetadata `
    -Path $setupDllPublished `
    -ExpectedAssemblyVersion $release.AssemblyVersion `
    -ExpectedInformationalVersion $release.InformationalVersion `
    -ExpectedPaxChannel $release.Channel | Out-Null
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
# [4c/8] Prerelease service administrative helper + deterministic payload
# ---------------------------------------------------------------------
# SECURITY CLASSIFICATION - DO NOT SOFTEN THIS. The helper staged below is
# UNSIGNED PRERELEASE CODE, accepted only for internal prerelease development
# and attended validation. An unsigned helper does not resist replacement by a
# local user; UAC may display an unknown publisher; prerelease validation
# proves functionality, not production tamper resistance. This is unacceptable
# for GA, and GA activation remains BLOCKED until this exact helper is
# Authenticode signed and the expected publisher policy is configured and
# verified. The inner manifest built here is a CORRUPTION/CONSISTENCY check
# only - it travels inside the container it describes and detects no adversary.
#
# Nothing here signs, elevates, registers a service, writes ProgramData or
# HKLM, or launches the helper.

# THE FROZEN SERVICE RUNTIME ALLOW-LIST. Derived empirically from the real
# framework-dependent service publish in cycle 61 - 36 files, no apphost, no
# symbols, and NO PAXCookbook.Shared.dll (the service compile-links its Shared
# contracts instead of referencing that assembly). Ordinal order.
$frozenServiceMembers = @(
    'Microsoft.Extensions.Configuration.Abstractions.dll'
    'Microsoft.Extensions.Configuration.Binder.dll'
    'Microsoft.Extensions.Configuration.CommandLine.dll'
    'Microsoft.Extensions.Configuration.EnvironmentVariables.dll'
    'Microsoft.Extensions.Configuration.FileExtensions.dll'
    'Microsoft.Extensions.Configuration.Json.dll'
    'Microsoft.Extensions.Configuration.UserSecrets.dll'
    'Microsoft.Extensions.Configuration.dll'
    'Microsoft.Extensions.DependencyInjection.Abstractions.dll'
    'Microsoft.Extensions.DependencyInjection.dll'
    'Microsoft.Extensions.Diagnostics.Abstractions.dll'
    'Microsoft.Extensions.Diagnostics.dll'
    'Microsoft.Extensions.FileProviders.Abstractions.dll'
    'Microsoft.Extensions.FileProviders.Physical.dll'
    'Microsoft.Extensions.FileSystemGlobbing.dll'
    'Microsoft.Extensions.Hosting.Abstractions.dll'
    'Microsoft.Extensions.Hosting.WindowsServices.dll'
    'Microsoft.Extensions.Hosting.dll'
    'Microsoft.Extensions.Logging.Abstractions.dll'
    'Microsoft.Extensions.Logging.Configuration.dll'
    'Microsoft.Extensions.Logging.Console.dll'
    'Microsoft.Extensions.Logging.Debug.dll'
    'Microsoft.Extensions.Logging.EventLog.dll'
    'Microsoft.Extensions.Logging.EventSource.dll'
    'Microsoft.Extensions.Logging.dll'
    'Microsoft.Extensions.Options.ConfigurationExtensions.dll'
    'Microsoft.Extensions.Options.dll'
    'Microsoft.Extensions.Primitives.dll'
    'PAXCookbook.Service.deps.json'
    'PAXCookbook.Service.dll'
    'PAXCookbook.Service.runtimeconfig.json'
    'System.Diagnostics.EventLog.dll'
    'System.ServiceProcess.ServiceController.dll'
    'runtimes/win/lib/net8.0/System.Diagnostics.EventLog.Messages.dll'
    'runtimes/win/lib/net8.0/System.Diagnostics.EventLog.dll'
    'runtimes/win/lib/net8.0/System.ServiceProcess.ServiceController.dll'
)

$serviceAdminHelperFileName = 'PAXCookbookServiceAdminHelper.exe'
$servicePayloadManifestName = 'service-payload-manifest.json'

function New-DeterministicServicePayloadArchive {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)][string[]]$OrderedMemberNames,
        [Parameter(Mandatory)][string]$ManifestEntryName
    )

    # Hand-built with ZipArchive. ZipFile::CreateFromDirectory cannot produce a
    # deterministic archive because it controls neither entry order nor entry
    # timestamps. Fixed ordinal entry order (manifest first), a fixed
    # DOS-representable timestamp (1980 floor, 2-second precision), one fixed
    # compression method for every member, no directory entries, no comments.
    $fixedTimestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $memberBytes = New-Object 'System.Collections.Generic.Dictionary[string,byte[]]' ([StringComparer]::Ordinal)
    $entryBlocks = New-Object System.Collections.Generic.List[string]

    foreach ($memberName in $OrderedMemberNames) {
        $memberPath = Join-Path $SourceDirectory ($memberName.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $memberPath)) {
            throw "deterministic payload member missing from service publish: $memberName"
        }
        $bytes = [IO.File]::ReadAllBytes($memberPath)
        $memberBytes[$memberName] = $bytes
        $memberSha = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
        $block = "    {`n      `"name`": `"$memberName`",`n      `"sizeBytes`": $($bytes.LongLength),`n      `"sha256`": `"$memberSha`"`n    }"
        $entryBlocks.Add($block)
    }

    $filesText = "[`n" + ($entryBlocks -join ",`n") + "`n  ]"
    $topLevel = New-Object System.Collections.Generic.List[string]
    $topLevel.Add('  "schemaVersion": 1')
    $topLevel.Add('  "targetOs": "windows"')
    $topLevel.Add('  "targetArch": "x64"')
    $topLevel.Add('  "files": ' + $filesText)
    $manifestJson = "{`n" + ($topLevel -join ",`n") + "`n}"

    # Canonical serialization guards: LF only, and escape-free (a backslash
    # could only be an alternate separator or a JSON escape, and the closed
    # schema needs neither).
    if ($manifestJson.Contains("`r")) { throw 'deterministic payload manifest contains a carriage return' }
    if ($manifestJson.Contains('\')) { throw 'deterministic payload manifest contains a backslash' }
    $manifestBytes = (New-Object Text.UTF8Encoding($false)).GetBytes($manifestJson)

    $orderedEntries = New-Object System.Collections.Generic.List[string]
    $orderedEntries.Add($ManifestEntryName)
    foreach ($memberName in $OrderedMemberNames) { $orderedEntries.Add($memberName) }

    $sourcePath = Join-Path ([System.IO.Path]::GetTempPath()) ("pax-service-payload-{0}.zip" -f [Guid]::NewGuid().ToString('N'))
    try {
        $fileStream = [IO.File]::Open($sourcePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        try {
            $zipArchive = New-Object System.IO.Compression.ZipArchive(
                $fileStream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
            try {
                foreach ($entryName in $orderedEntries) {
                    $entry = $zipArchive.CreateEntry(
                        $entryName, [System.IO.Compression.CompressionLevel]::NoCompression)
                    $entry.LastWriteTime = $fixedTimestamp
                    $entryStream = $entry.Open()
                    try {
                        $entryBytes = if ($entryName -ceq $ManifestEntryName) { $manifestBytes } else { $memberBytes[$entryName] }
                        $entryStream.Write($entryBytes, 0, $entryBytes.Length)
                    } finally { $entryStream.Dispose() }
                }
            } finally { $zipArchive.Dispose() }
        } finally { $fileStream.Dispose() }

        $p1Path = Join-Path $PSScriptRoot '..\guards\Invoke-P1ArtifactPersistencePathPolicy.ps1'
        if (Test-Path -LiteralPath $p1Path -PathType Leaf) {
            $p1Arguments = @(
                '-NoProfile', '-File', $p1Path,
                '-Mode', 'Persist',
                '-DestinationPath', $DestinationPath,
                '-SourcePath', $sourcePath,
                '-ExpectedSourceSha256', '7B5257D14BC4D590AEC60BC08A9FB21A72721B93A3B8739AF9CDD77BAFEE5D57'
            )
            & ([Environment]::ProcessPath) @p1Arguments *> $null
            if ($LASTEXITCODE -ne 0) { throw 'Service payload archive persistence failed.' }
        } else {
            [System.IO.File]::Copy($sourcePath, $DestinationPath, $true)
        }
    } finally {
        Remove-Item -LiteralPath $sourcePath -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Step '[4c/8] publish PAXCookbook.Service (framework-dependent, no apphost, no symbols)' {
    dotnet publish (Join-Path $root 'src\PAXCookbook.Service\PAXCookbook.Service.csproj') `
        -c $Configuration --self-contained false `
        -p:UseAppHost=false -p:DebugType=none -p:DebugSymbols=false `
        -o $pubServiceFd --nologo 2>&1 |
        Tee-Object -Append -FilePath $logFile | Out-Null
}

# STRUCTURAL ANTI-BLOAT: the service runtime must be exactly the frozen set.
$serviceFound = New-Object System.Collections.Generic.List[string]
foreach ($serviceFile in (Get-ChildItem -LiteralPath $pubServiceFd -File -Recurse)) {
    $serviceFound.Add($serviceFile.FullName.Substring($pubServiceFd.Length).TrimStart('\').Replace('\', '/'))
}
$serviceFoundArray = $serviceFound.ToArray()
[Array]::Sort($serviceFoundArray, [StringComparer]::Ordinal)
$frozenSorted = $frozenServiceMembers.Clone()
[Array]::Sort($frozenSorted, [StringComparer]::Ordinal)
if ($serviceFoundArray.Count -ne $frozenSorted.Count) {
    throw ("service publish inventory changed: expected {0} files, found {1}. The frozen allow-list must be re-derived deliberately, never widened silently." -f $frozenSorted.Count, $serviceFoundArray.Count)
}
for ($i = 0; $i -lt $frozenSorted.Count; $i++) {
    if ($serviceFoundArray[$i] -cne $frozenSorted[$i]) {
        throw ("service publish inventory changed at position {0}: expected '{1}', found '{2}'." -f $i, $frozenSorted[$i], $serviceFoundArray[$i])
    }
}
foreach ($serviceMember in $frozenServiceMembers) {
    if ($serviceMember.EndsWith('.exe') -or $serviceMember.EndsWith('.pdb')) {
        throw "embedded service runtime must contain no apphost and no symbols: $serviceMember"
    }
}
# Cross-check the packaging allow-list against the compiled contract, so the
# PowerShell builder and the C# verifier can never drift apart unnoticed.
$formatSourcePath = Join-Path $root 'src\PAXCookbook.ServiceAdminHelper\Payload\ServicePayloadArchiveFormat.cs'
$formatSource = [IO.File]::ReadAllText($formatSourcePath)
foreach ($serviceMember in $frozenServiceMembers) {
    if ($formatSource.IndexOf(('"' + $serviceMember + '"'), [StringComparison]::Ordinal) -lt 0) {
        throw "frozen member not declared in ServicePayloadArchiveFormat.cs: $serviceMember"
    }
}
Log ("  service runtime allow-list verified: {0} files, no apphost, no symbols" -f $frozenSorted.Count)

Invoke-Step '[4c/8] build deterministic service payload archive (twice, byte-identity required)' {
    New-DeterministicServicePayloadArchive -SourceDirectory $pubServiceFd `
        -DestinationPath $servicePayloadArchive `
        -OrderedMemberNames $frozenServiceMembers `
        -ManifestEntryName $servicePayloadManifestName

    $determinismProbe = Join-Path $out 'service-payload.determinism.zip'
    New-DeterministicServicePayloadArchive -SourceDirectory $pubServiceFd `
        -DestinationPath $determinismProbe `
        -OrderedMemberNames $frozenServiceMembers `
        -ManifestEntryName $servicePayloadManifestName

    $firstBytes = [IO.File]::ReadAllBytes($servicePayloadArchive)
    $secondBytes = [IO.File]::ReadAllBytes($determinismProbe)
    if ($firstBytes.LongLength -ne $secondBytes.LongLength) {
        throw 'deterministic payload archive is not reproducible: lengths differ'
    }
    for ($i = 0; $i -lt $firstBytes.Length; $i++) {
        if ($firstBytes[$i] -ne $secondBytes[$i]) {
            throw "deterministic payload archive is not reproducible: byte $i differs"
        }
    }
    $firstSha = HashOf $servicePayloadArchive
    $secondSha = HashOf $determinismProbe
    if ($firstSha -ne $secondSha) { throw 'deterministic payload archive is not reproducible: sha mismatch' }
    Remove-Item -LiteralPath $determinismProbe -Force
    Log ("  deterministic service payload archive: {0} bytes sha256={1} (byte-identical on rebuild)" -f (SizeOf $servicePayloadArchive), $firstSha)
}

Invoke-Step '[4c/8] publish UNSIGNED PRERELEASE service admin helper (self-contained, single file)' {
    dotnet publish (Join-Path $root 'src\PAXCookbook.ServiceAdminHelper\PAXCookbook.ServiceAdminHelper.csproj') `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:DebugSymbols=false `
        -p:PrereleaseUnsignedHelper=true `
        "-p:ServicePayloadArchive=$servicePayloadArchive" `
        -o $pubHelper --nologo 2>&1 |
        Tee-Object -Append -FilePath $logFile | Out-Null
}

$helperExeBuilt = Join-Path $pubHelper $serviceAdminHelperFileName
if (-not (Test-Path -LiteralPath $helperExeBuilt)) {
    throw "service admin helper publish did not produce $serviceAdminHelperFileName at: $helperExeBuilt"
}
# STRUCTURAL ANTI-BLOAT: exactly one artifact, and no sibling managed helper
# assembly or symbols alongside it.
$helperPublished = @(Get-ChildItem -LiteralPath $pubHelper -File -Recurse)
if ($helperPublished.Count -ne 1) {
    throw ("service admin helper publish must produce exactly one file; found {0}: {1}" -f $helperPublished.Count, (($helperPublished | ForEach-Object { $_.Name }) -join ', '))
}
if ($helperPublished[0].Name -cne $serviceAdminHelperFileName) {
    throw "service admin helper publish produced an unexpected file: $($helperPublished[0].Name)"
}
Copy-Item $helperExeBuilt (Join-Path $setupStageDir $serviceAdminHelperFileName) -Force
Log ("  staged UNSIGNED PRERELEASE service admin helper: {0} ({1:N1} MiB)" -f $serviceAdminHelperFileName, ((SizeOf $helperExeBuilt) / 1MB))

# STRUCTURAL ANTI-BLOAT over the whole staged payload.
$stagedSetupFiles = @(Get-ChildItem -LiteralPath $setupStageDir -File -Recurse)
if ($stagedSetupFiles.Count -ne ($setupFdFiles.Count + 1)) {
    throw ("payload\Setup must contain exactly {0} files; found {1}" -f ($setupFdFiles.Count + 1), $stagedSetupFiles.Count)
}
$helperCopies = @(Get-ChildItem -LiteralPath $payload -File -Recurse |
    Where-Object { $_.Name -like '*ServiceAdminHelper*' })
if ($helperCopies.Count -ne 1) {
    throw ("the payload must carry exactly ONE service admin helper; found {0}" -f $helperCopies.Count)
}
$expectedHelperPath = Join-Path $setupStageDir $serviceAdminHelperFileName
if ($helperCopies[0].FullName -ne $expectedHelperPath) {
    throw "the service admin helper is not at its one fixed payload path: $($helperCopies[0].FullName)"
}
$strayHelpers = @(Get-ChildItem -LiteralPath $payload -File -Recurse |
    Where-Object { $_.Extension -eq '.exe' -and $_.Name -cne $serviceAdminHelperFileName -and $_.Name -cne 'PAX Cookbook.exe' })
if ($strayHelpers.Count -gt 0) {
    throw ("unexpected executable in payload: {0}" -f (($strayHelpers | ForEach-Object { $_.Name }) -join ', '))
}
$straySymbols = @(Get-ChildItem -LiteralPath $setupStageDir -File -Recurse | Where-Object { $_.Extension -eq '.pdb' })
if ($straySymbols.Count -gt 0) {
    throw ("symbols must never ship in payload\Setup: {0}" -f (($straySymbols | ForEach-Object { $_.Name }) -join ', '))
}
# NOTE, deliberately NOT enforced here: payload\App\bin carries the App's own
# PAX Cookbook.pdb. That is PRE-EXISTING App publish behaviour, unrelated to the
# service admin helper, and silently deleting it from the payload would be an
# unreviewed change to a different component. It is reported, not fixed.
$appSymbols = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Where-Object { $_.Extension -eq '.pdb' })
if ($appSymbols.Count -gt 0) {
    Log ("  NOTE (pre-existing, not introduced by the helper): payload carries {0} symbol file(s): {1}" -f $appSymbols.Count, (($appSymbols | ForEach-Object { $_.Name }) -join ', '))
}
Log '  payload anti-bloat checks passed: one helper, one fixed path, no Setup symbols, no stray executables'

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
        'tools\entra\New-PaxCookbookEntraWamSetup.ps1'
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
    $files.Add([ordered]@{
        relativeInstallPath = 'tools\entra\New-PaxCookbookEntraWamSetup.ps1'
        sha256 = HashOf (Join-Path $payload 'tools\entra\New-PaxCookbookEntraWamSetup.ps1')
        sizeBytes = SizeOf (Join-Path $payload 'tools\entra\New-PaxCookbookEntraWamSetup.ps1')
    })

    $m = [ordered]@{
        product = 'PAXCookbook'
        manifestSchemaVersion = 1
        appVersion = $AppVersion
        setupVersion = $SetupVersion
        buildId = $release.Provenance
        builtAtUtc = $buildUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
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
    $manifestPath = Join-Path $payload 'manifest.json'
    Assert-PaxSetupReleaseWritePath -Contract $release -Path $manifestPath -Purpose build | Out-Null
    $m | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Log ("  manifest files count: {0}" -f $files.Count)

    # CLOSED MANIFEST ENFORCEMENT: every payload member except the manifest
    # itself must be manifested with a SHA-256 and a size. The dedicated appExe
    # entry accounts for the one file deliberately kept out of files[].
    $payloadMembers = @(Get-ChildItem -LiteralPath $payload -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath })
    if (($files.Count + 1) -ne $payloadMembers.Count) {
        throw ("payload manifest is not closed: {0} manifested + 1 appExe, but {1} payload files exist" -f $files.Count, $payloadMembers.Count)
    }
    foreach ($manifested in $files) {
        if ([string]::IsNullOrWhiteSpace($manifested.sha256) -or $manifested.sizeBytes -lt 0) {
            throw ("payload member is not hash/size verified: {0}" -f $manifested.relativeInstallPath)
        }
    }
    Log '  payload manifest is closed: every member carries a sha256 and a size'
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
        @versionProperties `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=embedded `
        -p:PaxChannel=$channel `
        -o $pubSetupFinal --nologo 2>&1 |
        Tee-Object -Append -FilePath $logFile | Out-Null
}

$finalSrc = Join-Path $pubSetupFinal 'PAXCookbookSetup.exe'
if (-not (Test-Path -LiteralPath $finalSrc)) { throw "final Setup EXE missing: $finalSrc" }
Test-PaxFileVersionArtifact -Path $finalSrc -ExpectedFileVersion $release.FileVersion | Out-Null
$builtExe = Join-Path $out 'PAXCookbookSetup.exe'
Assert-PaxSetupReleaseWritePath -Contract $release -Path $builtExe -Purpose build | Out-Null
Copy-Item $finalSrc $builtExe -Force

# Distributable names (user-facing downloads / GitHub Release assets).
$distExe = Join-Path $distDir 'PAX_Cookbook_Setup.exe'
$distPayload = Join-Path $distDir 'PAX_Cookbook_Payload.zip'
Assert-PaxSetupReleaseWritePath -Contract $release -Path $distExe -Purpose distribution | Out-Null
Assert-PaxSetupReleaseWritePath -Contract $release -Path $distPayload -Purpose distribution | Out-Null
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
# Update the resolved channel manifest.
#
# Decouples the signed Setup EXE from the payload: Setup downloads this
# file at runtime and verifies the payload zip against the sha256/size
# below, so a payload update only requires committing this manifest — the
# Setup EXE never needs rebuilding/re-signing for routine payload updates.
# minimumSetupVersion is preserved from the existing file (only bumped
# deliberately when a payload genuinely requires a newer Setup).
# ---------------------------------------------------------------------
Invoke-Step 'update versions.json manifest' {
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
            channel = $channel
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
    Assert-PaxSetupReleaseWritePath -Contract $release -Path $versionsPath -Purpose manifest | Out-Null
    $vm | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $versionsPath -Encoding UTF8
    Log "  versions.json updated: channel=$channel payload sha=$payloadHash setup sha=$finalHash engine=$($versionInfo.paxScript.version) builtAtUtc=$builtAtUtc"
}

# ---------------------------------------------------------------------
# [8/8] Size verification gates
# ---------------------------------------------------------------------
Invoke-Step '[8/8] verify artifact sizes' {
    $maxSetupBytes = 80 * 1024 * 1024    # 80 MiB max for self-contained Setup (no trimming, WinForms)
    # 100 MiB max for the payload. The payload carries the framework-dependent
    # App, the framework-dependent Setup runtime, and ONE approved self-contained
    # service administrative helper. A self-contained runtime inside the payload
    # is therefore NOT automatically a leak - the helper is an expected member.
    # Exceeding this ceiling means unexpected payload growth or an unintended
    # ADDITIONAL runtime, and must be investigated rather than raised again.
    $maxPayloadBytes = 100 * 1024 * 1024

    if ($finalSize -gt $maxSetupBytes) {
        throw "Setup EXE too large: {0:N1} MiB > 80 MiB limit. Payload may be embedded or build issue." -f ($finalSize/1MB)
    }
    if ($payloadSize -gt $maxPayloadBytes) {
        throw "Payload ZIP too large: {0:N1} MiB > 100 MiB limit. This indicates unexpected payload growth or an unintended ADDITIONAL runtime beyond the one approved self-contained service admin helper." -f ($payloadSize/1MB)
    }
    Log ("  Setup  : {0:N1} MiB (< 80 MiB limit) PASS" -f ($finalSize/1MB))
    Log ("  Payload: {0:N1} MiB (<= 100 MiB limit) PASS" -f ($payloadSize/1MB))

    # Measured helper contribution, read from the payload ZIP's own central
    # directory rather than estimated.
    $helperCompressed = 0
    $zipRead = [System.IO.Compression.ZipFile]::OpenRead($distPayload)
    try {
        foreach ($zipEntry in $zipRead.Entries) {
            if ($zipEntry.FullName -like ('*' + $serviceAdminHelperFileName)) {
                $helperCompressed = $zipEntry.CompressedLength
            }
        }
    } finally { $zipRead.Dispose() }
    $headroom = $maxPayloadBytes - $payloadSize
    Log ("  Helper : {0:N1} MiB compressed inside the payload ZIP (UNSIGNED PRERELEASE)" -f ($helperCompressed/1MB))
    Log ("  Headroom: {0:N1} MiB remaining under the 100 MiB payload ceiling" -f ($headroom/1MB))
    Write-Host ("PAYLOAD_HELPER_COMPRESSED_BYTES={0}" -f $helperCompressed)
    Write-Host ("PAYLOAD_HEADROOM_BYTES={0}" -f $headroom)
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
