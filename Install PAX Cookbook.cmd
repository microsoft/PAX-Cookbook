@echo off
rem =====================================================================
rem PAX Cookbook  top-level installer convenience wrapper.
rem
rem This .cmd is a thin, double-click-friendly wrapper around the real
rem PowerShell installer that ships in this release at:
rem     app\install\Install-PAXCookbook.ps1
rem
rem What this wrapper does:
rem   - Resolves its own folder (the extracted release-zip root).
rem   - Locates pwsh.exe (PowerShell 7.4+); refuses to fall back to
rem     legacy Windows PowerShell 5.1 (powershell.exe).
rem   - Invokes the existing PowerShell installer with explicit
rem     install mode and safe quoting for paths that contain spaces.
rem   - Returns the PowerShell installer's exit code verbatim.
rem   - On failure (non-zero exit code) only, pauses so the operator
rem     can read the error before the console window closes.
rem
rem What this wrapper deliberately does NOT do:
rem   - Does NOT request administrator elevation.
rem   - Does NOT bypass any installer integrity check.
rem   - Does NOT copy files itself or duplicate installer logic.
rem   - Does NOT change machine-wide PowerShell execution policy.
rem   - Does NOT modify PATH or any environment variable persistently.
rem   - Does NOT install or modify any Windows service.
rem   - Does NOT modify any Windows Firewall rule.
rem =====================================================================

setlocal

set "ROOT=%~dp0"
set "INSTALLER=%ROOT%app\install\Install-PAXCookbook.ps1"

if not exist "%INSTALLER%" (
    echo.
    echo PAX Cookbook installer not found at:
    echo   %INSTALLER%
    echo.
    echo This wrapper must run from the extracted release-zip root.
    echo Make sure the ZIP has been fully extracted and that the
    echo "app\install\" folder is present next to this .cmd file.
    echo.
    pause
    exit /b 1
)

where pwsh.exe >nul 2>nul
if errorlevel 1 (
    echo.
    echo PowerShell 7.4 or newer is required to install PAX Cookbook.
    echo The "pwsh.exe" executable was not found on this machine.
    echo.
    echo Install PowerShell 7.4+ from:
    echo   https://aka.ms/PSWindows
    echo.
    echo Then re-run this installer.
    echo.
    pause
    exit /b 1
)

pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%INSTALLER%" install
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
    echo.
    echo PAX Cookbook install failed with exit code %EXITCODE%.
    echo See the install log under %%LOCALAPPDATA%%\PAXCookbook\install.log
    echo for details.
    echo.
    pause
)

exit /b %EXITCODE%
