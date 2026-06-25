#requires -Version 7.0
<#
.SYNOPSIS
    Dead-simple launcher for a manually-installed PAX Cookbook.

.DESCRIPTION
    Starts PAX Cookbook by running the Microsoft-signed dotnet.exe host against
    the app DLL with the right --workspace/--approot arguments -- the same way
    the Start Menu shortcut does. Use this if you'd rather double-click a script
    than use the shortcut. It makes no changes to your PC and no network calls.

.PARAMETER InstallRoot
    The folder where you extracted PAX_Cookbook_Payload.zip (contains the 'App'
    subfolder). Defaults to %LOCALAPPDATA%\PAXCookbook.

.EXAMPLE
    pwsh -File .\Launch-PAXCookbook-Manual.ps1
#>
[CmdletBinding()]
param(
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'PAXCookbook')
)

$ErrorActionPreference = 'Stop'

$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$appRoot   = Join-Path $InstallRoot 'App'
$binDir    = Join-Path $appRoot 'bin'
$appDll    = Join-Path $binDir 'PAX Cookbook.dll'
$workspace = Join-Path $InstallRoot 'Workspace'

if (-not (Test-Path -LiteralPath $appDll)) {
    Write-Host "ERROR: Could not find PAX Cookbook at: $appDll" -ForegroundColor Red
    Write-Host "Point -InstallRoot at the folder that contains the 'App' subfolder." -ForegroundColor Red
    exit 1
}

# Resolve the Microsoft-signed dotnet.exe host (same probe order as the app).
function Resolve-DotNet {
    foreach ($n in @('ProgramFiles','ProgramW6432','ProgramFiles(x86)')) {
        $pf = [Environment]::GetEnvironmentVariable($n)
        if ($pf) { $c = Join-Path (Join-Path $pf 'dotnet') 'dotnet.exe'; if (Test-Path -LiteralPath $c) { return $c } }
    }
    foreach ($n in @('DOTNET_ROOT','DOTNET_ROOT(x86)','DOTNET_ROOT_X64')) {
        $r = [Environment]::GetEnvironmentVariable($n)
        if ($r) { $c = Join-Path $r 'dotnet.exe'; if (Test-Path -LiteralPath $c) { return $c } }
    }
    $cmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -and (Test-Path -LiteralPath $cmd.Source)) { return $cmd.Source }
    return $null
}

$dotnet = Resolve-DotNet
if (-not $dotnet) {
    Write-Host "ERROR: Microsoft .NET (dotnet.exe) was not found." -ForegroundColor Red
    Write-Host "Install the .NET 8 Desktop Runtime + ASP.NET Core 8 Runtime from" -ForegroundColor Red
    Write-Host "  https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $workspace)) { New-Item -ItemType Directory -Force -Path $workspace | Out-Null }

# Launch. WorkingDirectory is the app bin folder, matching the shortcut.
Start-Process -FilePath $dotnet `
    -ArgumentList @("`"$appDll`"", '--workspace', "`"$workspace`"", '--approot', "`"$appRoot`"") `
    -WorkingDirectory $binDir | Out-Null

Write-Host "PAX Cookbook is starting..." -ForegroundColor Green
