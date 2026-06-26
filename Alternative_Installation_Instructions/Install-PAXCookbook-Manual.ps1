<#
.SYNOPSIS
    Manual (no-Setup-EXE) installer finisher for PAX Cookbook.

.DESCRIPTION
    For locked-down machines where the unsigned PAX Cookbook Setup.exe is
    hard-blocked by Defender/WDAC with no "Run anyway" option. The app itself
    never needs the Setup EXE: it runs as the Microsoft-signed dotnet.exe host
    executing the app DLL. This script replicates the handful of *functional*
    things the normal Setup does, so a downloaded payload behaves like a real
    install:

      0. Extract PAX_Cookbook_Payload.zip into the install folder if it hasn't
         been extracted yet (looks in your Downloads folder by default).
      1. Unblock-File the whole tree (strip Mark-of-the-Web). Without this, the
         engine .ps1 carries the internet "zone" mark and PowerShell's default
         RemoteSigned policy refuses to run it, so bakes fail with "not digitally
         signed".
      2. Create the per-user Workspace folder (recipes + bake history live here).
      3. Write installed-skus.json with the real payload SHA so the in-app
         updater does NOT show a spurious "update available" prompt.
      4. Create a Start Menu shortcut AND (by default) a Desktop shortcut whose
         Target is the user's dotnet.exe and whose arguments are the app DLL +
         --workspace/--approot -- byte-for-byte the same launch line a normal
         install creates (see ShortcutCatalog.cs). Use -NoDesktop to skip the
         Desktop shortcut.
      5. (By default) Add the per-user HKCU Run value that starts the headless
         broker at login, mirroring AutoStartRegistrar.cs, so scheduled bakes can
         fire with no window open. Use -NoAutoStart to skip it.

    Runs on Windows PowerShell 5.1 or PowerShell 7, so you can simply right-click
    the file and choose "Run with PowerShell". It is idempotent (safe to re-run),
    makes NO HKLM writes, requires NO administrator rights, installs NO Windows
    service, and never executes the unsigned apphost EXE.

.PARAMETER InstallRoot
    The folder where the app lives. If it doesn't already contain an "App"
    subfolder, the script creates it and extracts PAX_Cookbook_Payload.zip into
    it for you. Defaults to %LOCALAPPDATA%\PAXCookbook.

.PARAMETER PayloadZip
    Optional. The PAX_Cookbook_Payload.zip you downloaded. Used as the extraction
    source (when InstallRoot isn't populated yet) AND to compute the payload SHA
    for installed-skus.json (so the updater stays quiet). If omitted, the script
    looks for the zip in your Downloads folder and next to InstallRoot.

.PARAMETER NoDesktop
    Skip the Desktop shortcut. A Desktop shortcut is created by default; the
    Start Menu shortcut is always created.

.PARAMETER NoAutoStart
    Skip registering the headless broker to start at login. Start-at-login is
    enabled by default so scheduled bakes can fire with no window open.

.PARAMETER NoPause
    Don't wait for a keypress before the window closes. Useful for unattended
    runs; omit it for a right-click run so you can read the result.

.EXAMPLE
    # Simplest: right-click Install-PAXCookbook-Manual.ps1 in your Downloads
    # folder and choose "Run with PowerShell". Installs to %LOCALAPPDATA%\PAXCookbook
    # with a Start Menu shortcut, a Desktop shortcut, and start-at-login (the defaults).

.EXAMPLE
    # Same defaults, from a PowerShell terminal:
    pwsh -ExecutionPolicy Bypass -File .\Install-PAXCookbook-Manual.ps1

.EXAMPLE
    # No Desktop shortcut and no start-at-login (Start Menu shortcut only):
    pwsh -ExecutionPolicy Bypass -File .\Install-PAXCookbook-Manual.ps1 -NoDesktop -NoAutoStart

.EXAMPLE
    # Desktop shortcut, but NOT start-at-login:
    pwsh -ExecutionPolicy Bypass -File .\Install-PAXCookbook-Manual.ps1 -NoAutoStart

.EXAMPLE
    # Start-at-login, but NO Desktop shortcut:
    pwsh -ExecutionPolicy Bypass -File .\Install-PAXCookbook-Manual.ps1 -NoDesktop

.EXAMPLE
    # Custom install folder and an explicit zip path:
    pwsh -ExecutionPolicy Bypass -File .\Install-PAXCookbook-Manual.ps1 -InstallRoot "D:\Apps\PAXCookbook" -PayloadZip "C:\Users\me\Downloads\PAX_Cookbook_Payload.zip"

.NOTES
    After downloading the payload zip (and this script) to your Downloads folder,
    just right-click this file and choose "Run with PowerShell". The script does
    not download anything and makes no network calls.
#>
[CmdletBinding()]
param(
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'PAXCookbook'),
    [string] $PayloadZip  = '',
    [switch] $NoDesktop,
    [switch] $NoAutoStart,
    [switch] $NoPause
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Product identity -- mirrors src/PAXCookbook.Shared/ProductConstants.cs.
# Keep these in sync with the app if they ever change.
# ---------------------------------------------------------------------------
$ProductName        = 'PAX Cookbook'
$AppRootFolderName  = 'App'                  # ProductConstants.AppRootFolderName
$BinRootFolderName  = 'bin'                  # ProductConstants.BinRootFolderName
$WorkspaceFolderName= 'Workspace'            # ProductConstants.WorkspaceFolderName
$AppDllName         = 'PAX Cookbook.dll'     # ProductConstants.AppDllName
$AppExeName         = 'PAX Cookbook.exe'     # ProductConstants.AppExeName (icon source ONLY)
$RunValueName       = 'PAX Cookbook'         # AutoStartRegistrar.ValueName

function Write-Step    ([string]$m) { Write-Host "  [*] $m" -ForegroundColor Cyan }
function Write-OkLine  ([string]$m) { Write-Host "  [+] $m" -ForegroundColor Green }
function Write-WarnLine([string]$m) { Write-Host "  [!] $m" -ForegroundColor Yellow }
function Wait-Close { if (-not $NoPause) { Write-Host ""; Read-Host "Press Enter to close this window" | Out-Null } }
function Fail          ([string]$m) { Write-Host ""; Write-Host "ERROR: $m" -ForegroundColor Red; Wait-Close; exit 1 }

Write-Host ""
Write-Host "PAX Cookbook -- manual install finisher" -ForegroundColor White
Write-Host "=======================================" -ForegroundColor White

# ---------------------------------------------------------------------------
# 0. Minimum OS guard -- PAX Cookbook requires Windows 10 or later. Stop here
#    with a friendly message instead of completing the whole setup only to
#    dead-end at first launch on an unsupported version of Windows. Both
#    Windows PowerShell 5.1 and PowerShell 7 report the true OS version here
#    (their hosts declare Windows 10 compatibility), so Major is 10 on
#    Windows 10/11 and 6 on Windows 7/8/8.1.
# ---------------------------------------------------------------------------
if ([System.Environment]::OSVersion.Version.Major -lt 10) {
    Write-Host ""
    Write-Host "PAX Cookbook requires Windows 10 or later. This computer is running an" -ForegroundColor Yellow
    Write-Host "earlier version of Windows, so the app can't run here." -ForegroundColor Yellow
    Wait-Close
    exit 1
}

# ---------------------------------------------------------------------------
# 1. Validate the extracted payload.
# ---------------------------------------------------------------------------
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$appRoot = Join-Path $InstallRoot $AppRootFolderName
$binDir  = Join-Path $appRoot $BinRootFolderName
$appDll  = Join-Path $binDir  $AppDllName
$appExe  = Join-Path $binDir  $AppExeName

# ---------------------------------------------------------------------------
# 1a. Auto-extract the payload if it hasn't been extracted yet.
#     This removes the fragile Windows Explorer "Extract All" step, whose dialog
#     does NOT expand %LOCALAPPDATA% and rejects it as "destination folder path
#     is invalid". When the App folder isn't present under InstallRoot but the
#     payload zip is available, we extract it here with Expand-Archive, which
#     resolves env vars and creates the destination. Idempotent: skipped once
#     App\bin\<dll> already exists, so re-running never re-extracts.
# ---------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $appDll)) {
    $zipForExtract = $null
    $extractCandidates = @()
    if ($PayloadZip) { $extractCandidates += $PayloadZip }
    $extractCandidates += (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads\PAX_Cookbook_Payload.zip')
    $extractCandidates += (Join-Path (Split-Path -Parent $InstallRoot) 'PAX_Cookbook_Payload.zip')
    foreach ($z in $extractCandidates) {
        if ($z -and (Test-Path -LiteralPath $z)) { $zipForExtract = $z; break }
    }
    if ($zipForExtract) {
        Write-Step "Extracting the app from: $zipForExtract"
        New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
        try {
            Expand-Archive -LiteralPath $zipForExtract -DestinationPath $InstallRoot -Force
        } catch {
            Fail "Could not extract the payload zip:`n  $zipForExtract`n`n$($_.Exception.Message)"
        }
        Write-OkLine "Extracted the app into: $InstallRoot"
    }
}

if (-not (Test-Path -LiteralPath $appDll)) {
    Fail @"
Could not find the app at:
  $appDll

Make sure PAX_Cookbook_Payload.zip is in your Downloads folder (or pass its path
with -PayloadZip), or extract it yourself and point -InstallRoot at the folder
that contains the 'App' subfolder. For example, if -InstallRoot is
  $InstallRoot
that folder should contain: App\bin\$AppDllName
"@
}
Write-OkLine "Found the app: $appDll"

# ---------------------------------------------------------------------------
# 2. Resolve the Microsoft-signed dotnet.exe host.
#    Same probe order as the app (DotNetLaunch.cs): standard per-machine install
#    locations, then the DOTNET_ROOT family, then PATH. We require a CONCRETE
#    path so the shortcut never depends on PATH resolution at click time.
# ---------------------------------------------------------------------------
function Resolve-DotNet {
    foreach ($envName in @('ProgramFiles','ProgramW6432','ProgramFiles(x86)')) {
        $pf = [Environment]::GetEnvironmentVariable($envName)
        if ($pf) {
            $cand = Join-Path (Join-Path $pf 'dotnet') 'dotnet.exe'
            if (Test-Path -LiteralPath $cand) { return $cand }
        }
    }
    foreach ($envName in @('DOTNET_ROOT','DOTNET_ROOT(x86)','DOTNET_ROOT_X64')) {
        $root = [Environment]::GetEnvironmentVariable($envName)
        if ($root) {
            $cand = Join-Path $root 'dotnet.exe'
            if (Test-Path -LiteralPath $cand) { return $cand }
        }
    }
    $cmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -and (Test-Path -LiteralPath $cmd.Source)) { return $cmd.Source }
    return $null
}

$dotnet = Resolve-DotNet
if (-not $dotnet) {
    Fail @"
Microsoft .NET (dotnet.exe) was not found on this PC.

PAX Cookbook runs on the Microsoft-signed .NET host. Please install the
.NET 8 Desktop Runtime AND the ASP.NET Core 8 Runtime (both are free, signed
Microsoft installers) from:
  https://dotnet.microsoft.com/download/dotnet/8.0
Then run this script again.
"@
}
Write-OkLine "Found Microsoft .NET host: $dotnet"

# ---------------------------------------------------------------------------
# 3. Strip Mark-of-the-Web from the whole extracted tree (THE key step).
#    Replicates Setup's MarkOfTheWeb.StripTree. Without this, downloaded files
#    keep their internet "zone" mark and PowerShell's RemoteSigned policy blocks
#    the unsigned engine .ps1 at bake time.
# ---------------------------------------------------------------------------
Write-Step "Unblocking files (telling Windows the extracted files are safe to run)..."
$unblocked = 0
Get-ChildItem -LiteralPath $InstallRoot -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
    try { Unblock-File -LiteralPath $_.FullName -ErrorAction Stop; $unblocked++ } catch { }
}
Write-OkLine "Unblocked $unblocked file(s)."

# ---------------------------------------------------------------------------
# 4. Create the per-user Workspace folder (recipes + bake history).
#    The app also creates this on first run, but we make it now so the path in
#    the shortcut/Run command always exists.
# ---------------------------------------------------------------------------
$workspace = Join-Path $InstallRoot $WorkspaceFolderName
if (-not (Test-Path -LiteralPath $workspace)) {
    New-Item -ItemType Directory -Force -Path $workspace | Out-Null
    Write-OkLine "Created workspace folder: $workspace"
} else {
    Write-OkLine "Workspace folder already present: $workspace"
}

# ---------------------------------------------------------------------------
# 5. Write installed-skus.json so the updater does not show a false
#    "update available" prompt. Mirrors InstalledSkusWriter.cs:
#    { schemaVersion, appVersion, payloadSha256, recordedAtUtc }.
#    The app only reads payloadSha256 (lowercased), comparing it to the
#    published versions.json; a correct value keeps the updater quiet.
# ---------------------------------------------------------------------------
function Get-PayloadSha {
    param([string]$zip)
    if ($zip -and (Test-Path -LiteralPath $zip)) {
        return (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $null
}

# Try the explicit zip, then a few likely locations.
$zipCandidates = @()
if ($PayloadZip) { $zipCandidates += $PayloadZip }
$zipCandidates += (Join-Path (Split-Path -Parent $InstallRoot) 'PAX_Cookbook_Payload.zip')
$zipCandidates += (Join-Path $InstallRoot 'PAX_Cookbook_Payload.zip')
$zipCandidates += (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads\PAX_Cookbook_Payload.zip')

$payloadSha = $null
foreach ($z in $zipCandidates) {
    $payloadSha = Get-PayloadSha -zip $z
    if ($payloadSha) { Write-OkLine "Computed payload SHA from: $z"; break }
}

# appVersion comes from the shipped App\VERSION.json (cookbook.version).
$appVersion = '0.0.0'
$versionJson = Join-Path $appRoot 'VERSION.json'
if (Test-Path -LiteralPath $versionJson) {
    try {
        $vj = Get-Content -LiteralPath $versionJson -Raw | ConvertFrom-Json
        if ($vj.cookbook.version) { $appVersion = [string]$vj.cookbook.version }
    } catch { }
}

$skus = [ordered]@{
    schemaVersion = 1
    appVersion    = $appVersion
    payloadSha256 = $payloadSha   # null is acceptable; app falls back to version compare
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
}
$skusPath = Join-Path $InstallRoot 'installed-skus.json'
$skusJson = $skus | ConvertTo-Json
# Write UTF-8 WITHOUT a BOM (version-agnostic: Set-Content -Encoding UTF8 adds a
# BOM on Windows PowerShell 5.1). Matches the real Setup's InstalledSkusWriter.
[System.IO.File]::WriteAllText($skusPath, $skusJson, (New-Object System.Text.UTF8Encoding($false)))
if ($payloadSha) {
    Write-OkLine "Wrote installed-skus.json (payloadSha256=$payloadSha)"
} else {
    Write-WarnLine "Wrote installed-skus.json with no payload SHA (zip not found)."
    Write-WarnLine "The app may show a one-time 'update available' message -- it is harmless."
}

# ---------------------------------------------------------------------------
# 6. Create the launch shortcut(s). Target/Arguments/WorkingDirectory mirror
#    ShortcutCatalog.cs EXACTLY so this shortcut launches identically to a
#    normal install. The icon is read from the shipped apphost EXE (icon reads
#    are always allowed; the EXE is never executed). The shortcut is created
#    minimized so the dotnet.exe console never flashes.
#
#    NOTE: a real install also stamps the shortcut's AppUserModelID
#    ($Aumid = 'PAXCookbook.App.v1') via the Windows shell property store, which
#    WScript.Shell cannot set. The app sets its own AUMID at runtime, so the
#    only difference is initial taskbar grouping/pinning identity -- cosmetic,
#    and it does not affect launching.
# ---------------------------------------------------------------------------
$argTail = "`"$appDll`" --workspace `"$workspace`" --approot `"$appRoot`""

function New-AppShortcut {
    param([string]$LinkPath)
    $dir = Split-Path -Parent $LinkPath
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $lnk = $shell.CreateShortcut($LinkPath)
        $lnk.TargetPath       = $dotnet
        $lnk.Arguments        = $argTail
        $lnk.WorkingDirectory = $binDir
        $lnk.IconLocation     = "$appExe,0"
        $lnk.Description       = $ProductName
        $lnk.WindowStyle      = 7      # 7 = minimized -> no console flash
        $lnk.Save()
    } finally {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$startMenuLnk = Join-Path $startMenuDir "$ProductName.lnk"
New-AppShortcut -LinkPath $startMenuLnk
Write-OkLine "Created Start Menu shortcut: $startMenuLnk"

if (-not $NoDesktop) {
    $desktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "$ProductName.lnk"
    New-AppShortcut -LinkPath $desktopLnk
    Write-OkLine "Created Desktop shortcut: $desktopLnk"
}

# ---------------------------------------------------------------------------
# 7. Optional: register the headless broker to start at login (HKCU Run).
#    Mirrors AutoStartRegistrar.cs: a single per-user Run value whose command is
#    "<dotnet>" "<dll>" --headless --workspace "<ws>" --approot "<app>".
#    HKCU only -- no HKLM, no admin, no service.
# ---------------------------------------------------------------------------
if (-not $NoAutoStart) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runCmd = "`"$dotnet`" `"$appDll`" --headless --workspace `"$workspace`" --approot `"$appRoot`""
    if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    New-ItemProperty -Path $runKey -Name $RunValueName -Value $runCmd -PropertyType String -Force | Out-Null
    Write-OkLine "Registered start-at-login (HKCU Run -> '$RunValueName')."
}

# ---------------------------------------------------------------------------
# 8. Success summary.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Done. PAX Cookbook is ready." -ForegroundColor Green
Write-Host ""
Write-Host "  Installed at : $InstallRoot"
Write-Host "  Workspace    : $workspace"
Write-Host "  Launch with  : Start Menu -> '$ProductName'$(if (-not $NoDesktop) { '  (or the Desktop shortcut)' })"
Write-Host ""
Write-Host "  First launch sets up the analysis engine automatically -- nothing else to do."
Write-Host "  If a bake ever fails saying 'not digitally signed', re-run this script"
Write-Host "  (the unblock step) and try again."
Write-Host ""
Wait-Close
