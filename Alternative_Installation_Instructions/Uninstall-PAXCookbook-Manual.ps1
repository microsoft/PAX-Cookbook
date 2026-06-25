<#
.SYNOPSIS
    Manual uninstaller for a manually-installed PAX Cookbook.

.DESCRIPTION
    The companion to Install-PAXCookbook-Manual.ps1. It automatically undoes
    everything that script (and the manual guide) set up, so you don't have to
    delete shortcuts, registry values, and folders by hand:

      1. Remove the start-at-sign-in entry (the per-user HKCU Run value).
      2. Remove the Start Menu shortcut.
      3. Remove the Desktop shortcut.
      4. Delete the install folder (the app AND your saved recipes + bake
         history live here), plus the small per-user data PAX Cookbook keeps
         under %LOCALAPPDATA%\PAXCookbook and %APPDATA%\PAXCookbook.

    Nothing was ever installed system-wide, so this needs NO administrator
    rights, makes NO HKLM changes, and touches NO Windows services. It only
    removes the current user's own PAX Cookbook files and settings.

    You can simply right-click this file and choose "Run with PowerShell"
    (it runs on Windows PowerShell 5.1 or PowerShell 7). It asks you to confirm
    before deleting anything.

.PARAMETER InstallRoot
    The folder PAX Cookbook was installed to. Defaults to %LOCALAPPDATA%\PAXCookbook
    (the install script's default). Pass this only if you installed to a custom
    folder with -InstallRoot.

.PARAMETER Yes
    Skip the confirmation prompt and remove everything immediately. Use with
    care -- this permanently deletes your recipes and bake history.

.PARAMETER NoPause
    Don't wait for a keypress before the window closes (useful for unattended
    runs; omit it for a right-click run so you can read the result).

.EXAMPLE
    # Simplest: right-click Uninstall-PAXCookbook-Manual.ps1 and choose
    # "Run with PowerShell". It asks you to confirm, then removes everything.

.EXAMPLE
    # From a terminal, no prompt:
    pwsh -ExecutionPolicy Bypass -File .\Uninstall-PAXCookbook-Manual.ps1 -Yes

.EXAMPLE
    # If you installed to a custom folder:
    pwsh -ExecutionPolicy Bypass -File .\Uninstall-PAXCookbook-Manual.ps1 -InstallRoot "D:\Apps\PAXCookbook"

.NOTES
    Close PAX Cookbook before running this. If the app (or its background
    start-at-sign-in process) is still running, Windows keeps its files open and
    the folder delete will fail; the script tells you exactly what was left.
#>
[CmdletBinding()]
param(
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'PAXCookbook'),
    [switch] $Yes,
    [switch] $NoPause
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Product identity -- mirrors Install-PAXCookbook-Manual.ps1 / ProductConstants.cs.
# ---------------------------------------------------------------------------
$ProductName  = 'PAX Cookbook'
$RunValueName = 'PAX Cookbook'   # AutoStartRegistrar.ValueName

function Write-Step    ([string]$m) { Write-Host "  [*] $m" -ForegroundColor Cyan }
function Write-OkLine  ([string]$m) { Write-Host "  [+] $m" -ForegroundColor Green }
function Write-WarnLine([string]$m) { Write-Host "  [!] $m" -ForegroundColor Yellow }
function Wait-Close { if (-not $NoPause) { Write-Host ""; Read-Host "Press Enter to close this window" | Out-Null } }

Write-Host ""
Write-Host "PAX Cookbook -- manual uninstaller" -ForegroundColor White
Write-Host "==================================" -ForegroundColor White

$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)

# The per-user data PAX Cookbook keeps regardless of where the app was installed:
#   %LOCALAPPDATA%\PAXCookbook  -> engine, acquisition state, WebView2 data, updates
#   %APPDATA%\PAXCookbook       -> small bootstrap/settings file
# For the default install these sit INSIDE InstallRoot; for a custom InstallRoot
# they're separate, so we clean them up explicitly too.
$localData = Join-Path $env:LOCALAPPDATA 'PAXCookbook'
$roamData  = Join-Path $env:APPDATA      'PAXCookbook'

# Build the list of folders to remove (de-duplicated, only those that exist).
$folders = New-Object System.Collections.Generic.List[string]
foreach ($f in @($InstallRoot, $localData, $roamData)) {
    $full = [System.IO.Path]::GetFullPath($f)
    if ((Test-Path -LiteralPath $full) -and -not ($folders -contains $full)) {
        $folders.Add($full)
    }
}

$startMenuLnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs' | Join-Path -ChildPath "$ProductName.lnk"
$desktopLnk   = Join-Path ([Environment]::GetFolderPath('Desktop')) "$ProductName.lnk"

# ---------------------------------------------------------------------------
# Show exactly what will be removed, then confirm.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "This will remove PAX Cookbook for the current user only:" -ForegroundColor White
Write-Host "  - Start-at-sign-in entry (HKCU Run -> '$RunValueName')"
Write-Host "  - Start Menu shortcut : $startMenuLnk"
Write-Host "  - Desktop shortcut    : $desktopLnk"
foreach ($f in $folders) { Write-Host "  - Folder (deleted)    : $f" }
Write-Host ""
Write-WarnLine "Your saved recipes and bake history are inside the install folder and"
Write-WarnLine "WILL be permanently deleted. Close PAX Cookbook first."

if (-not $Yes) {
    Write-Host ""
    $answer = Read-Host "Type YES to remove PAX Cookbook (anything else cancels)"
    if ($answer -ne 'YES') {
        Write-Host ""
        Write-Host "Cancelled. Nothing was changed." -ForegroundColor Yellow
        Wait-Close
        exit 0
    }
}

Write-Host ""

# ---------------------------------------------------------------------------
# 1. Remove the start-at-sign-in entry. Positive-ID: only delete OUR value
#    (named 'PAX Cookbook') whose command points at a PAX Cookbook DLL, so a
#    same-named value from a different product is left untouched.
# ---------------------------------------------------------------------------
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
try {
    $existing = $null
    if (Test-Path -LiteralPath $runKey) {
        $prop = Get-ItemProperty -LiteralPath $runKey -Name $RunValueName -ErrorAction SilentlyContinue
        if ($prop) { $existing = [string]$prop.$RunValueName }
    }
    if ($existing) {
        if ($existing -match 'PAX Cookbook\.dll') {
            Remove-ItemProperty -LiteralPath $runKey -Name $RunValueName -ErrorAction Stop
            Write-OkLine "Removed start-at-sign-in entry (HKCU Run -> '$RunValueName')."
        } else {
            Write-WarnLine "A Run value named '$RunValueName' exists but doesn't point at PAX Cookbook; left it alone."
        }
    } else {
        Write-OkLine "No start-at-sign-in entry to remove."
    }
} catch {
    Write-WarnLine "Could not remove the start-at-sign-in entry: $($_.Exception.Message)"
}

# ---------------------------------------------------------------------------
# 2 + 3. Remove the Start Menu and Desktop shortcuts.
# ---------------------------------------------------------------------------
foreach ($lnk in @($startMenuLnk, $desktopLnk)) {
    try {
        if (Test-Path -LiteralPath $lnk) {
            Remove-Item -LiteralPath $lnk -Force -ErrorAction Stop
            Write-OkLine "Removed shortcut: $lnk"
        }
    } catch {
        Write-WarnLine "Could not remove shortcut '$lnk': $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------------------
# 4. Delete the install folder + per-user data folders.
# ---------------------------------------------------------------------------
$failed = New-Object System.Collections.Generic.List[string]
foreach ($f in $folders) {
    Write-Step "Deleting $f"
    try {
        Remove-Item -LiteralPath $f -Recurse -Force -ErrorAction Stop
        Write-OkLine "Deleted: $f"
    } catch {
        $failed.Add($f)
        Write-WarnLine "Could not fully delete '$f': $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------------------
# Summary.
# ---------------------------------------------------------------------------
Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "Done. PAX Cookbook has been removed." -ForegroundColor Green
} else {
    Write-Host "Almost done -- some files could not be deleted." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  This usually means PAX Cookbook is still running. Close it (check the"
    Write-Host "  system tray and Task Manager for 'PAX Cookbook' or 'dotnet'), then run"
    Write-Host "  this uninstaller again. Folders left behind:"
    foreach ($f in $failed) { Write-Host "    $f" }
}
Write-Host ""
Wait-Close
