#requires -Version 7.4

# =====================================================================
# PAX Cookbook installer / update script (the appliance installer).
#
# This is the single PowerShell script that performs both first-time
# install and in-place update of PAX Cookbook. It is committed in
# source and ships *inside* the release zip at install/Install-
# PAXCookbook.ps1. The user invokes it directly only for first-time
# install (they extract the release zip, then run this file). Every
# subsequent update is performed by the *staged* copy of the script
# living inside %LOCALAPPDATA%\PAXCookbook\Updates\<version>\extracted\
# install\, launched by the broker after an explicit click.
#
# Hard rules (enforced by the bundled verification harness):
#
#   - No elevation. The script never calls Start-Process -Verb RunAs
#     and never requires administrator. It runs entirely under the
#     calling user's context.
#   - No registry mutation. No HKLM/HKCU writes, no Set-ItemProperty
#     against any registry path.
#   - No services / scheduled tasks. No New-Service, Set-Service,
#     Register-ScheduledTask, sc.exe.
#   - No outbound HTTP. No Invoke-WebRequest, Invoke-RestMethod,
#     System.Net.WebClient, System.Net.Http.HttpClient. The broker
#     performs the download; the installer only reads bytes from disk.
#   - No certificate / signing mutation. No Set-AuthenticodeSignature,
#     New-SelfSignedCertificate, Import-Certificate, certutil. Code
#     signing is a distributor responsibility performed BEFORE
#     this script ships, against a distributor-held certificate.
#   - No execution-policy / Defender / firewall mutation. No
#     Set-ExecutionPolicy, Set-MpPreference, New-NetFirewallRule,
#     Unblock-File.
#   - No PATH / environment-variable mutation. No
#     [Environment]::SetEnvironmentVariable, setx.
#   - No custom bundled-PAX path. The bundled PAX script lives at
#     resources/pax/PAX_Purview_Audit_Log_Processor.ps1 inside the
#     install root, period. No configurable bundled path, no
#     environment-variable override.
#
# What the installer implements:
#
#   - Canonical install root resolution (LocalApplicationData based).
#   - Self-locating from $PSScriptRoot.
#   - install mode  : fresh install onto an empty install root.
#   - update mode   : backup-then-replace of an existing App\ tree.
#   - uninstall mode: removal of App\ (and optionally Backups/Updates).
#   - check    mode : read-only inspection of the current install.
#   - Bounded local install log at <InstallRoot>\install.log.
#
# What the installer deliberately does NOT do:
#
#   - Download the release zip (the broker performs that on user click).
#   - Reach out to the manifest URL (the broker performs that too).
#   - Call Get-AuthenticodeSignature on its peers as part of trust
#     gating. Authenticode is verified by Windows / the PowerShell host
#     based on the user-invoked execution policy.
#   - Prune backup-retention beyond a default keep-N policy.
#   - Auto-relaunch Cookbook after install.
# =====================================================================

[CmdletBinding(DefaultParameterSetName = 'Mode')]
param(
    # Operation mode.
    #   install          = first install (refuses to clobber existing tree).
    #   update           = in-place update (broker handoff only).
    #   uninstall        = remove App\ tree (and optionally backups/updates).
    #   check            = read-only state report.
    #   repair-shortcuts = rewrite the user-scope Start Menu shortcuts
    #                      (and the Desktop shortcut, if one already
    #                      exists and is PAX-owned) against the
    #                      current install tree. Does NOT copy any
    #                      files; requires an existing install. Force-
    #                      deletes prior .lnks before writing fresh
    #                      ones so the Windows shell IconCache.db re-
    #                      reads the icon path -- the explicit repair
    #                      path for stale shortcuts left by older
    #                      installer versions whose .lnk pointed icon
    #                      at pwsh.exe,0 (V1.S18-shortcut).
    #   remove-shortcuts = delete all PAX-owned user-scope shortcuts
    #                      (the Start Menu PAX Cookbook folder and
    #                      its contents, the legacy single-file Start
    #                      Menu .lnk, and the Desktop .lnk if PAX-
    #                      owned). Does NOT touch the App\ tree,
    #                      Backups\, Updates\, or any operator
    #                      workspace data. The supported way for an
    #                      operator to remove the Start Menu entry
    #                      without browsing to the folder by hand
    #                      (V1.S19 PART F).
    [Parameter(Position = 0)]
    [ValidateSet('install', 'update', 'uninstall', 'uninstall-prompt', 'check', 'repair-shortcuts', 'remove-shortcuts')]
    [string]$Mode = 'check',

    # Path to a handoff JSON record produced by the broker before the
    # broker exits and detaches the installer. Required for `update`
    # mode unless -SourceRoot is also provided (test-harness path).
    [Parameter()]
    [string]$Handoff,

    # Path to an extracted release tree to use as the source of the
    # install/update. Defaults to walking up from $PSScriptRoot to the
    # nearest parent that contains VERSION.json. Useful for the
    # verification harness when no handoff record exists.
    [Parameter()]
    [string]$SourceRoot,

    # Override the install root (default
    # %LOCALAPPDATA%\PAXCookbook). This exists so the verification
    # harness can sandbox the installer; it is not exposed in any
    # user-facing documentation and not surfaced in the SPA. The
    # bundled PAX script path is always
    # <InstallRoot>\App\resources\pax\PAX_Purview_Audit_Log_Processor.ps1
    # regardless of this override.
    [Parameter()]
    [string]$InstallRoot,

    # uninstall mode only: also remove Backups\ and Updates\ subtrees.
    [Parameter()]
    [switch]$Purge,

    # Phase S: shortcut policy (install + update + repair-shortcuts modes).
    #
    # Default behavior: install/update/repair-shortcuts creates a
    # single flat USER-SCOPE Start Menu shortcut "PAX Cookbook"
    # targeting the native host App\bin\PAX Cookbook.exe -- no
    # program-group folder and no user-facing maintenance shortcuts
    # (maintenance runs through the installer CLI modes). It also
    # creates a USER-SCOPE Desktop shortcut "PAX Cookbook.lnk"
    # targeting the same native host. The Desktop shortcut is ON by
    # default because PAX Cookbook is a local Windows appliance and
    # the Desktop is the canonical launch surface for end users.
    #
    # -CreateDesktopShortcut : RETAINED for backward compatibility.
    #                          Functionally a no-op: the Desktop
    #                          shortcut is now created by default.
    # -NoDesktopShortcut     : OPT-OUT. Do not create a Desktop
    #                          shortcut. Does NOT delete an existing
    #                          Desktop shortcut unless the mode is
    #                          remove-shortcuts or uninstall.
    # -NoShortcuts           : create NEITHER Start Menu nor Desktop
    #                          shortcuts. Mutually exclusive with
    #                          -CreateDesktopShortcut.
    #
    # If both -CreateDesktopShortcut AND -NoDesktopShortcut are
    # supplied, -NoDesktopShortcut wins and the installer logs a
    # warning. Use -NoDesktopShortcut alone for clarity.
    #
    # No registry edits, no autorun, no scheduled tasks, no machine-
    # scope shortcuts. All shortcut paths land under per-user folders
    # resolved via [Environment]::GetFolderPath('StartMenu' / 'Desktop').
    [Parameter()]
    [switch]$CreateDesktopShortcut,

    [Parameter()]
    [switch]$NoDesktopShortcut,

    [Parameter()]
    [switch]$NoShortcuts,

    # TEST-ONLY knob (analogous to -InstallRoot). When set, the
    # shortcut helpers write their .lnk files under
    # "<ShortcutSandboxRoot>\StartMenu\Programs\" and
    # "<ShortcutSandboxRoot>\Desktop\" instead of the operator's real
    # %APPDATA%\Microsoft\Windows\Start Menu\Programs and Desktop
    # folders. Used exclusively by the Phase S verification harness so
    # that running the test suite never pollutes the operator's actual
    # Start Menu. Not surfaced in any UI; not documented to users.
    [Parameter()]
    [string]$ShortcutSandboxRoot,

    # Skip the interactive `Are you sure?` gate in uninstall mode. The
    # harness uses this; users should not.
    [Parameter()]
    [switch]$Yes,

    # External-PAX automation-seed pair (install mode). Supplying both
    # parameters causes the installer to validate the operator-supplied
    # PAX script + approved-engine manifest against the same broker
    # acquisition pipeline that the SPA upload flow uses, and to
    # write the canonical PAX script at install time with
    # paxAcquisition.source = 'automation' in install-state.json.
    # Supplying only one of the pair is a configuration error: the
    # installer emits a WARN, ignores both values, and writes the
    # pending paxAcquisition block (operator must complete acquisition
    # via the SPA on first launch). Supplying neither is the normal
    # external policy install path (pending block written; SPA picks
    # up acquisition on first launch). The pair is rejected outright
    # in update mode -- automation seeding occurs only at install.
    [Parameter()]
    [string]$PaxScriptPath = '',

    [Parameter()]
    [string]$PaxManifestPath = ''
)

$ErrorActionPreference = 'Stop'

# Validate mutually-exclusive shortcut switches early, before any I/O.
# -CreateDesktopShortcut + -NoDesktopShortcut: not a fatal error in
# UX-1S because Desktop shortcut creation is now the default; we
# preserve -CreateDesktopShortcut as a backward-compatible no-op and
# let -NoDesktopShortcut win when both are supplied. Warn so the
# operator notices the redundancy.
if ($CreateDesktopShortcut -and $NoDesktopShortcut) {
    Write-Host '-CreateDesktopShortcut and -NoDesktopShortcut both supplied; -NoDesktopShortcut wins.' -ForegroundColor Yellow
}
if ($CreateDesktopShortcut -and $NoShortcuts) {
    Write-Host '-CreateDesktopShortcut and -NoShortcuts are mutually exclusive.' -ForegroundColor Red
    exit 1
}

# Compute the effective desktop shortcut policy. UX-1S: ON by
# default, OFF only when the operator explicitly passes
# -NoDesktopShortcut (or when -NoShortcuts suppresses everything).
# The legacy -CreateDesktopShortcut switch no longer participates
# in this decision; it is retained for backward compatibility only.
$Script:DesktopShortcutEnabled = (-not $NoDesktopShortcut.IsPresent)
if ($NoShortcuts) { $Script:DesktopShortcutEnabled = $false }

# =====================================================================
# 0. Hard runtime preconditions
# =====================================================================

if ($PSVersionTable.PSVersion.Major -lt 7 -or
    ($PSVersionTable.PSVersion.Major -eq 7 -and $PSVersionTable.PSVersion.Minor -lt 4)) {
    Write-Host ''
    Write-Host 'PAX Cookbook installer requires PowerShell 7.4 or newer.' -ForegroundColor Red
    Write-Host ('Detected: PowerShell ' + $PSVersionTable.PSVersion.ToString()) -ForegroundColor Red
    Write-Host ''
    exit 1
}

# Refuse to run on non-Windows hosts. The install layout is rooted at
# %LOCALAPPDATA%, which is a Windows construct. Cross-platform support
# is permanently out of scope (the appliance is Windows-only).
if (-not $IsWindows) {
    Write-Host ''
    Write-Host 'PAX Cookbook installer runs on Windows only.' -ForegroundColor Red
    Write-Host ''
    exit 1
}

# =====================================================================
# 1. Path resolution
# =====================================================================

function Resolve-SourceRoot {
    # Walk up from this script's own location until we find a parent
    # whose direct contents include VERSION.json. That parent is the
    # release-zip extracted root (or the source repo's app/ folder
    # when running from source, since app/VERSION.json sits at the
    # same relative position).
    param([string]$Override)

    if ($Override) {
        $abs = [System.IO.Path]::GetFullPath($Override)
        $vf  = Join-Path $abs 'VERSION.json'
        if (-not (Test-Path -LiteralPath $vf -PathType Leaf)) {
            throw ('SourceRoot override does not contain VERSION.json: ' + $abs)
        }
        return $abs
    }

    $cur = Split-Path -Parent $PSCommandPath
    while ($cur) {
        $parent = Split-Path -Parent $cur
        if (-not $parent -or $parent -eq $cur) { break }
        $vf = Join-Path $parent 'VERSION.json'
        if (Test-Path -LiteralPath $vf -PathType Leaf) {
            return $parent
        }
        $cur = $parent
    }
    throw 'Cannot locate VERSION.json by walking up from the installer script. Is the release zip extracted with the install/ folder intact?'
}

function Resolve-InstallRoot {
    param([string]$Override)
    if ($Override) {
        return [System.IO.Path]::GetFullPath($Override)
    }
    $lad = [Environment]::GetFolderPath('LocalApplicationData')
    if (-not $lad) {
        throw 'Cannot resolve LocalApplicationData folder path.'
    }
    return (Join-Path $lad 'PAXCookbook')
}

$Script:InstallerSourceRoot = $null
$Script:InstallerInstallRoot = $null
try {
    # `check`, `uninstall`, `uninstall-prompt`, and `repair-shortcuts`
    # operate against an existing install only and need no source
    # tree; everything else must resolve a source root that contains
    # VERSION.json.
    if ($Mode -ne 'check' -and $Mode -ne 'uninstall' -and $Mode -ne 'uninstall-prompt' -and $Mode -ne 'repair-shortcuts') {
        $Script:InstallerSourceRoot = Resolve-SourceRoot -Override $SourceRoot
    }
    $Script:InstallerInstallRoot = Resolve-InstallRoot -Override $InstallRoot
} catch {
    Write-Host ('Installer path resolution failed: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}

# Derived absolute paths inside the install root.
$Script:AppDir       = Join-Path $Script:InstallerInstallRoot 'App'
$Script:BackupsDir   = Join-Path $Script:InstallerInstallRoot 'Backups'
$Script:UpdatesDir   = Join-Path $Script:InstallerInstallRoot 'Updates'
$Script:LogFile      = Join-Path $Script:InstallerInstallRoot 'install.log'

# Phase S: optional test-only shortcut sandbox (see -ShortcutSandboxRoot
# parameter documentation). Promoted to script scope so the shortcut
# path helpers can read it without re-parsing $PSBoundParameters.
#
# Note: in a .ps1 script, the param-bound variable `$ShortcutSandboxRoot`
# already lives in `$Script:` scope, so this `if` block only NORMALIZES
# the path; it MUST NOT pre-init `$Script:ShortcutSandboxRoot` to `$null`,
# because doing so would clobber the parameter value.
if ($ShortcutSandboxRoot) {
    $Script:ShortcutSandboxRoot = [System.IO.Path]::GetFullPath($ShortcutSandboxRoot)
}

# =====================================================================
# 2. Bounded local logging
# =====================================================================

function Write-InstallLog {
    param([string]$Line, [string]$Level = 'INFO')
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $entry = '[' + $stamp + '] [' + $Level + '] ' + $Line
    # Ensure the install root exists so we can write the log even on
    # a brand-new install.
    if (-not (Test-Path -LiteralPath $Script:InstallerInstallRoot -PathType Container)) {
        try {
            New-Item -ItemType Directory -Path $Script:InstallerInstallRoot -Force | Out-Null
        } catch { }
    }
    try {
        Add-Content -LiteralPath $Script:LogFile -Value $entry -Encoding utf8
    } catch { }
    if ($Level -eq 'ERROR') {
        Write-Host $entry -ForegroundColor Red
    } elseif ($Level -eq 'WARN') {
        Write-Host $entry -ForegroundColor Yellow
    } else {
        Write-Host $entry
    }
}

function Write-InstallSection {
    param([string]$Title)
    Write-InstallLog ''
    Write-InstallLog ('=' * 70)
    Write-InstallLog $Title
    Write-InstallLog ('=' * 70)
}

# =====================================================================
# 3. Helpers
# =====================================================================

function Read-JsonFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ('JSON file not found: ' + $Path)
    }
    $raw = Get-Content -LiteralPath $Path -Raw
    return ($raw | ConvertFrom-Json -AsHashtable -Depth 12)
}

function Write-JsonFile {
    param([string]$Path, [object]$Object)
    $json = $Object | ConvertTo-Json -Depth 12
    Set-Content -LiteralPath $Path -Value $json -Encoding utf8
}

function Get-Sha256Hex {
    param([string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

function Test-IsBrokerRunning {
    # The running broker must exit before the installer mutates App\.
    #
    # Detection uses two layered signals so the installer can never
    # confuse itself (or any other maintenance-mode pwsh.exe) with a
    # live broker:
    #
    #   (1) Authoritative -- workspace.lock. The launcher records the
    #       chef's workspace path in
    #         %APPDATA%\PAXCookbook\cookbook.bootstrap.json
    #       and the broker writes its own PID into
    #         <WorkspacePath>\Runtime\workspace.lock
    #       as the canonical "broker is up" signal (the same record
    #       the launcher reuse path consults). If that PID is alive,
    #       is a pwsh(.exe) image, and is not THIS process, the
    #       broker is running.
    #
    #   (2) Heuristic fallback -- narrowed pwsh.exe command-line scan.
    #       Used only when the bootstrap pointer or workspace.lock is
    #       absent / unreadable / schema-incomplete. The Cookbook
    #       broker is dot-source invoked from
    #         <AppDir>\launcher\Start-PAXCookbook.ps1
    #       (or, transitively, from
    #         <AppDir>\launcher\Start-PAXCookbookSupportMode.ps1
    #       which delegates to the normal launcher), so a running
    #       broker is always a pwsh.exe whose CommandLine names one
    #       of those two launcher scripts under THIS install root.
    #       The current process ($PID) is excluded so a maintenance-
    #       mode installer never self-classifies as the broker, and
    #       any cmdline that names <AppDir>\install\Install-PAXCookbook.ps1
    #       is rejected because the installer itself does not host a
    #       broker. The Stop launcher, the repair-shortcuts mode, the
    #       uninstall-prompt mode, and the scheduled-cook entry are
    #       likewise non-broker pwsh.exe processes and never satisfy
    #       the include filter.
    #
    # Best-effort; not a cryptographic guarantee.
    if (-not (Test-Path -LiteralPath $Script:AppDir -PathType Container)) { return $false }

    $appDir = $Script:AppDir.TrimEnd('\','/')

    # ---- Branch 0: native host (production runtime) probe -------------
    # The production runtime is the native C# host "PAX Cookbook.exe".
    # It owns its own window/lifecycle and does NOT write a
    # workspace.lock brokerProcessId, so Branches 1-2 (pwsh/broker)
    # cannot see it. Detect any PAX Cookbook.exe whose executable path
    # or command line sits under THIS install root. Only OUR install's
    # native host is matched; a host from a different install is
    # ignored. The current process is never PAX Cookbook.exe (the
    # installer is pwsh), so no self-exclusion is required here.
    try {
        $nativeCands = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'PAX Cookbook.exe'" -ErrorAction SilentlyContinue
        if ($nativeCands) {
            foreach ($nc in $nativeCands) {
                $ncExe = [string]$nc.ExecutablePath
                $ncCl  = [string]$nc.CommandLine
                if (($ncExe -and ($ncExe -like ('*' + $appDir + '*'))) -or
                    ($ncCl  -and ($ncCl  -like ('*' + $appDir + '*')))) {
                    return $true
                }
            }
        }
    } catch { }

    # ---- Branch 1: authoritative workspace.lock probe ----------------
    try {
        $bootstrapPath = Join-Path $env:APPDATA 'PAXCookbook\cookbook.bootstrap.json'
        if (Test-Path -LiteralPath $bootstrapPath -PathType Leaf) {
            $bsRaw = Get-Content -LiteralPath $bootstrapPath -Raw -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace($bsRaw)) {
                $bs = $bsRaw | ConvertFrom-Json -ErrorAction Stop
                $wsPath = $null
                try { $wsPath = [string]$bs.workspaceFolderPath } catch { $wsPath = $null }
                if (-not [string]::IsNullOrWhiteSpace($wsPath)) {
                    $lockPath = Join-Path $wsPath 'Runtime\workspace.lock'
                    if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
                        $lkRaw = Get-Content -LiteralPath $lockPath -Raw -ErrorAction Stop
                        if (-not [string]::IsNullOrWhiteSpace($lkRaw)) {
                            $lk = $lkRaw | ConvertFrom-Json -ErrorAction Stop
                            $brokerPid = 0
                            try { $brokerPid = [int]$lk.brokerProcessId } catch { $brokerPid = 0 }
                            if ($brokerPid -gt 0 -and $brokerPid -ne $PID) {
                                $proc = Get-Process -Id $brokerPid -ErrorAction SilentlyContinue
                                if ($proc) {
                                    $imageName = $null
                                    try {
                                        $imagePath = [string]$proc.Path
                                        if (-not [string]::IsNullOrWhiteSpace($imagePath)) {
                                            $imageName = [System.IO.Path]::GetFileName($imagePath).ToLowerInvariant()
                                        }
                                    } catch { $imageName = $null }
                                    if ($imageName -eq 'pwsh.exe' -or $imageName -eq 'pwsh') {
                                        return $true
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    } catch { }

    # ---- Branch 2: narrowed pwsh.exe command-line heuristic -----------
    $launcherMain    = Join-Path $appDir 'launcher\Start-PAXCookbook.ps1'
    $launcherSupport = Join-Path $appDir 'launcher\Start-PAXCookbookSupportMode.ps1'
    $installerPath   = Join-Path $appDir 'install\Install-PAXCookbook.ps1'
    try {
        $cands = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'pwsh.exe'" -ErrorAction SilentlyContinue
        foreach ($c in $cands) {
            if (-not $c.CommandLine) { continue }
            $procId = 0
            try { $procId = [int]$c.ProcessId } catch { $procId = 0 }
            if ($procId -le 0 -or $procId -eq $PID) { continue }
            $cline = [string]$c.CommandLine
            # An installer / maintenance-mode pwsh.exe never hosts a
            # broker; reject before checking the launcher includes so a
            # path that contains both (e.g. relaunch staging) cannot
            # leak through.
            if ($cline -like ('*' + $installerPath + '*')) { continue }
            # Accept iff the cmdline references a broker-hosting
            # launcher under THIS install root.
            if (($cline -like ('*' + $launcherMain    + '*')) -or
                ($cline -like ('*' + $launcherSupport + '*'))) {
                return $true
            }
        }
    } catch { }

    return $false
}

function Get-LocalVersionInfo {
    $vf = Join-Path $Script:AppDir 'VERSION.json'
    if (-not (Test-Path -LiteralPath $vf -PathType Leaf)) { return $null }
    try {
        return (Read-JsonFile -Path $vf)
    } catch { return $null }
}

function Copy-TreeContents {
    param([string]$Source, [string]$Destination)
    # Robust directory copy: contents of $Source land directly under
    # $Destination. The trailing wildcard plus -Recurse mirrors the
    # subtree without nesting the source folder name.
    if (-not (Test-Path -LiteralPath $Destination -PathType Container)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        $dst = Join-Path $Destination $_.Name
        Copy-Item -LiteralPath $_.FullName -Destination $dst -Recurse -Force
    }
}

function Test-IsCanonicalPaxScript {
    # The bundled PAX script must live at the canonical relative path
    # inside App\, period. There is no custom-path support.
    param([string]$AppRoot)
    $expected = Join-Path $AppRoot 'resources\pax\PAX_Purview_Audit_Log_Processor.ps1'
    return (Test-Path -LiteralPath $expected -PathType Leaf)
}

function Compare-PaxScriptIntegrity {
    # After install/update, the bundled PAX script's SHA-256 must
    # match VERSION.json.paxScript.sha256 byte-for-byte. Mismatch is
    # a critical condition; the caller is responsible for rollback.
    param([string]$AppRoot)
    $vf = Join-Path $AppRoot 'VERSION.json'
    $ver = Read-JsonFile -Path $vf
    $reportedSha = ([string]$ver.paxScript.sha256).ToUpperInvariant()
    $rel = [string]$ver.paxScript.relativePath
    if (-not $rel) { $rel = 'resources/pax/PAX_Purview_Audit_Log_Processor.ps1' }
    $abs = Join-Path $AppRoot ($rel -replace '/', '\')
    if (-not (Test-Path -LiteralPath $abs -PathType Leaf)) {
        return @{ ok = $false; reason = 'bundled-pax-missing'; path = $abs }
    }
    $actualSha = Get-Sha256Hex -Path $abs
    if ($actualSha -ne $reportedSha) {
        return @{ ok = $false; reason = 'sha-mismatch'; expected = $reportedSha; actual = $actualSha; path = $abs }
    }
    return @{ ok = $true; sha = $actualSha; path = $abs }
}

# ---------------------------------------------------------------------
# Phase 13 Stage 1 helpers: external PAX engine acquisition policy
# ---------------------------------------------------------------------
#
# Per plan §9.2 / §9.3, the installer reads
# VERSION.json.paxScript.acquisitionPolicy and branches its bundled-PAX
# integrity gate as follows:
#
#   * acquisitionPolicy missing  -> legacy "embedded" semantics (today).
#   * acquisitionPolicy="embedded" -> legacy semantics unchanged.
#   * acquisitionPolicy="external" + canonical PAX script file PRESENT
#       -> run existing integrity gate (tampering still halts install).
#   * acquisitionPolicy="external" + canonical PAX script file ABSENT
#       -> skip integrity gate, write install-state.json with
#          paxAcquisition.pending = true, complete install. Acquisition
#          itself happens later in the broker's first-launch SPA
#          (Stage 2 wires Engine\Acquisition.psm1 + Routes\Setup.ps1).
#
# The installer NEVER fetches, hashes, reads, or copies the PAX script
# bytes outside the canonical placement that Copy-TreeContents already
# performs. It does NOT mutate the script. It does NOT make outbound
# HTTP calls. The §4.5 immutability contract is preserved.

$Script:KnownAcquisitionPolicies = @('external', 'embedded')

function Get-PaxAcquisitionPolicy {
    # Read VERSION.json.paxScript.acquisitionPolicy. Missing field
    # returns "embedded" for legacy / Phase 12 historical artifact
    # compatibility (D4). Unknown value throws.
    param([Parameter(Mandatory)][string]$VersionFile)
    if (-not (Test-Path -LiteralPath $VersionFile -PathType Leaf)) {
        throw ('VERSION.json not found at ' + $VersionFile)
    }
    $ver = Read-JsonFile -Path $VersionFile
    if (-not $ver -or -not $ver.paxScript) { return 'embedded' }
    $hasField = $false
    try {
        $hasField = ($null -ne $ver.paxScript.PSObject.Properties.Match('acquisitionPolicy'))
    } catch { $hasField = $false }
    if (-not $hasField) { return 'embedded' }
    $raw = [string]$ver.paxScript.acquisitionPolicy
    if ([string]::IsNullOrWhiteSpace($raw)) { return 'embedded' }
    if ($Script:KnownAcquisitionPolicies -notcontains $raw) {
        throw ('VERSION.json declares unknown paxScript.acquisitionPolicy "' + $raw + '". Allowed: external (or legacy: embedded).')
    }
    return $raw
}

function Test-PaxScriptPresentAtCanonicalPath {
    # Pure filesystem check delegated to Test-IsCanonicalPaxScript.
    param([Parameter(Mandatory)][string]$AppRoot)
    return (Test-IsCanonicalPaxScript -AppRoot $AppRoot)
}

function Get-InstallStateFilePath {
    # %LOCALAPPDATA%\PAXCookbook\install-state.json (sibling of App\).
    # Plan §3.12.
    return (Join-Path $Script:InstallerInstallRoot 'install-state.json')
}

function Write-InstallStatePaxAcquisition {
    # General-purpose paxAcquisition block writer. Accepts a hashtable
    # of plan §3.3 fields, merges them over any existing block, and
    # preserves sibling top-level keys in install-state.json. The
    # caller is responsible for supplying the closed §3.3 field set
    # (pending, source, version, sha256, manifestId, manifestHash,
    # manifestVersion, validatedAtUtc, activatedAtUtc, lastAttemptError).
    # No `policy` field — that lives in VERSION.json, not in the
    # install-state block. install-state.json is a sidecar metadata
    # document; text JSON APIs are permitted on it per the §4.5
    # immutability contract.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Fields
    )
    if ($null -eq $Fields -or -not ($Fields -is [System.Collections.IDictionary])) {
        throw 'Write-InstallStatePaxAcquisition: -Fields must be a hashtable.'
    }
    $stateFile = Get-InstallStateFilePath
    $stateDir  = Split-Path -Parent $stateFile
    if (-not (Test-Path -LiteralPath $stateDir -PathType Container)) {
        New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
    }
    $existing = $null
    if (Test-Path -LiteralPath $stateFile -PathType Leaf) {
        try { $existing = Read-JsonFile -Path $stateFile } catch { $existing = $null }
    }
    if (-not $existing) { $existing = @{} }

    $existingBlock = $null
    if ($existing -is [System.Collections.IDictionary]) {
        if ($existing.Contains('paxAcquisition')) { $existingBlock = $existing['paxAcquisition'] }
    } else {
        try {
            $prop = $existing.PSObject.Properties.Match('paxAcquisition')
            if ($prop -and $prop.Count -gt 0) { $existingBlock = $existing.paxAcquisition }
        } catch { $existingBlock = $null }
    }

    $merged = [ordered]@{
        pending          = $null
        source           = $null
        version          = $null
        sha256           = $null
        manifestId       = $null
        manifestHash     = $null
        manifestVersion  = $null
        validatedAtUtc   = $null
        activatedAtUtc   = $null
        lastAttemptError = $null
    }
    if ($existingBlock) {
        foreach ($k in @($merged.Keys)) {
            if ($existingBlock -is [System.Collections.IDictionary]) {
                if ($existingBlock.Contains($k)) { $merged[$k] = $existingBlock[$k] }
            } else {
                try {
                    $p = $existingBlock.PSObject.Properties.Match($k)
                    if ($p -and $p.Count -gt 0) { $merged[$k] = $existingBlock.$k }
                } catch { }
            }
        }
    }
    foreach ($k in $Fields.Keys) {
        $merged[[string]$k] = $Fields[$k]
    }

    if ($existing -is [System.Collections.IDictionary]) {
        $existing['paxAcquisition'] = $merged
    } else {
        if ($existing.PSObject.Properties.Match('paxAcquisition').Count -gt 0) {
            $existing.paxAcquisition = $merged
        } else {
            $existing | Add-Member -NotePropertyName 'paxAcquisition' -NotePropertyValue $merged -Force
        }
    }
    Write-JsonFile -Path $stateFile -Object $existing
    return $merged
}

function Write-InstallStatePaxAcquisitionPending {
    # Writes install-state.json's paxAcquisition block with pending=true
    # and the schema-mandated null placeholders (plan §3.3 shape).
    # Retains the -Policy parameter for backward source-compat but the
    # `policy` field is NOT persisted — paxScript.acquisitionPolicy
    # lives in VERSION.json, not in install-state.json.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Policy
    )
    $stateFile = Get-InstallStateFilePath
    [void](Write-InstallStatePaxAcquisition -Fields ([ordered]@{
        pending          = $true
        source           = $null
        version          = $null
        sha256           = $null
        manifestId       = $null
        manifestHash     = $null
        manifestVersion  = $null
        validatedAtUtc   = $null
        activatedAtUtc   = $null
        lastAttemptError = $null
    }))
    Write-InstallLog ('Wrote install-state.json paxAcquisition.pending=true (policy=' + $Policy + ') at ' + $stateFile)
}

function Move-PaxScriptToQuarantine {
    # Move a canonical-path PAX script byte-for-byte into the
    # quarantine subfolder so the new acquisition run starts from a
    # clean canonical path. Byte preservation is enforced by using
    # [System.IO.File]::Move. Returns the quarantine path on success
    # or $null if the source did not exist.
    #
    # Plan §9.3: the quarantine name encodes the first 8 hex chars of
    # the prior SHA-256 plus a UTC timestamp so multiple quarantined
    # versions never collide.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$AppRoot,
        [Parameter(Mandatory)][string]$OldSha256
    )
    $canonical = Join-Path $AppRoot 'resources\pax\PAX_Purview_Audit_Log_Processor.ps1'
    if (-not (Test-Path -LiteralPath $canonical -PathType Leaf)) { return $null }

    $shaUpper = ([string]$OldSha256).ToUpperInvariant()
    if ($shaUpper -notmatch '^[0-9A-F]{64}$') {
        $shaUpper = (Get-Sha256Hex -Path $canonical).ToUpperInvariant()
    }
    $shaTag = $shaUpper.Substring(0,8)
    $tsTag  = ([DateTimeOffset]::UtcNow).ToString('yyyyMMddTHHmmssZ')

    $qDir = Join-Path $AppRoot 'resources\pax\.quarantine'
    if (-not (Test-Path -LiteralPath $qDir -PathType Container)) {
        New-Item -ItemType Directory -Path $qDir -Force | Out-Null
    }
    $qPath = Join-Path $qDir ('PAX_Purview_Audit_Log_Processor.' + $shaTag + '.' + $tsTag + '.ps1')

    [System.IO.File]::Move($canonical, $qPath)
    Write-InstallLog ('Quarantined prior PAX script -> ' + $qPath)
    return $qPath
}

function Invoke-PaxAutomationSeedValidation {
    # Runs the §3.5 automation-seed pipeline against an operator-
    # supplied PAX script + approved-engine manifest pair, using the
    # same broker acquisition helpers the SPA upload flow uses. On
    # success, the canonical PAX script is written under App\ and
    # install-state.json's paxAcquisition block is stamped with
    # source='automation' / pending=false. On failure, returns a
    # structured lastAttemptError record; the caller writes the
    # pending block with that error.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PaxScriptPath,
        [Parameter(Mandatory)][string]$PaxManifestPath,
        [Parameter(Mandatory)][string]$AppRoot,
        [Parameter(Mandatory)][string]$WorkDirectory,
        [Parameter()][string]$StatePath
    )

    $now = { ([DateTimeOffset]::UtcNow).ToString('yyyy-MM-ddTHH:mm:ss.fffZ') }
    $fail = {
        param([string]$phase, [string]$code, [string]$message)
        return @{
            ok               = $false
            lastAttemptError = [ordered]@{
                phase        = $phase
                code         = $code
                message      = $message
                occurredAtUtc = & $now
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($PaxScriptPath))   { return (& $fail 'input' 'pax_script_path_empty'   'PaxScriptPath is empty.') }
    if ([string]::IsNullOrWhiteSpace($PaxManifestPath)) { return (& $fail 'input' 'pax_manifest_path_empty' 'PaxManifestPath is empty.') }
    if (-not (Test-Path -LiteralPath $PaxScriptPath   -PathType Leaf)) { return (& $fail 'input' 'pax_script_missing'   ('Operator-supplied PAX script not found: ' + $PaxScriptPath)) }
    if (-not (Test-Path -LiteralPath $PaxManifestPath -PathType Leaf)) { return (& $fail 'input' 'pax_manifest_missing' ('Operator-supplied PAX manifest not found: ' + $PaxManifestPath)) }

    $vfPath = Join-Path $AppRoot 'VERSION.json'
    if (-not (Test-Path -LiteralPath $vfPath -PathType Leaf)) {
        return (& $fail 'input' 'version_json_missing' ('VERSION.json not found at ' + $vfPath))
    }
    $ver = $null
    try { $ver = Read-JsonFile -Path $vfPath } catch { return (& $fail 'input' 'version_json_unreadable' $_.Exception.Message) }

    $expectedScriptSha     = $null
    $expectedScriptVersion = $null
    $cookbookVersion       = $null
    $expectedThumb         = $null
    $sigPolicy             = 'required'
    try {
        $expectedScriptSha     = ([string]$ver.paxScript.sha256).ToUpperInvariant()
        $expectedScriptVersion = [string]$ver.paxScript.version
        $cookbookVersion       = [string]$ver.cookbook.version
        $pax = $ver.paxScript
        $hasThumb  = $false
        $hasPolicy = $false
        if ($pax -is [System.Collections.IDictionary]) {
            $hasThumb  = $pax.Contains('engineManifestTrustAnchorThumbprint')
            $hasPolicy = $pax.Contains('manifestSignaturePolicy')
        } else {
            $hasThumb  = ($pax.PSObject.Properties.Match('engineManifestTrustAnchorThumbprint').Count -gt 0)
            $hasPolicy = ($pax.PSObject.Properties.Match('manifestSignaturePolicy').Count -gt 0)
        }
        if ($hasThumb) { $expectedThumb = [string]$pax.engineManifestTrustAnchorThumbprint }
        if ($hasPolicy) {
            $candidate = [string]$pax.manifestSignaturePolicy
            if (-not [string]::IsNullOrWhiteSpace($candidate)) { $sigPolicy = $candidate }
        }
    } catch {
        return (& $fail 'input' 'version_json_shape' ('VERSION.json is missing required paxScript fields: ' + $_.Exception.Message))
    }
    if ([string]::IsNullOrWhiteSpace($expectedScriptSha) -or $expectedScriptSha -notmatch '^[0-9A-F]{64}$') {
        return (& $fail 'input' 'version_pin_invalid' 'VERSION.json paxScript.sha256 must be a 64-character hex string.')
    }
    if ([string]::IsNullOrWhiteSpace($cookbookVersion)) {
        return (& $fail 'input' 'version_cookbook_missing' 'VERSION.json cookbook.version is empty.')
    }
    if ($sigPolicy -ne 'required' -and $sigPolicy -ne 'internal-test-bypass') {
        return (& $fail 'input' 'signature_policy_unknown' ('VERSION.json paxScript.manifestSignaturePolicy "' + $sigPolicy + '" is not recognized.'))
    }

    # Side-load the broker acquisition stack. The §3.5 pipeline is
    # implemented entirely in app/broker/* — the installer reuses it
    # verbatim (the only difference is the input vector).
    $brokerDir = Join-Path $AppRoot 'broker'
    $acquisitionPath    = Join-Path $brokerDir 'Engine\Acquisition.psm1'
    $manifestSchemaPath = Join-Path $brokerDir 'Engine\ManifestSchema.psm1'
    $signaturePath      = Join-Path $brokerDir 'Update\Signature.psm1'
    foreach ($p in @($acquisitionPath, $manifestSchemaPath, $signaturePath)) {
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) {
            return (& $fail 'input' 'broker_module_missing' ('Required broker module not found: ' + $p))
        }
    }
    try {
        Import-Module -Name $manifestSchemaPath -Force -DisableNameChecking | Out-Null
        Import-Module -Name $signaturePath      -Force -DisableNameChecking | Out-Null
        Import-Module -Name $acquisitionPath    -Force -DisableNameChecking | Out-Null
    } catch {
        return (& $fail 'input' 'broker_module_load_failed' $_.Exception.Message)
    }

    if (-not (Test-Path -LiteralPath $WorkDirectory -PathType Container)) {
        try { New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null }
        catch { return (& $fail 'input' 'work_directory_create_failed' $_.Exception.Message) }
    }

    # Step 1: manifest signature + schema verification.
    $sigPath = $PaxManifestPath + '.sig'
    $manifestArgs = @{
        ManifestBodyPath = $PaxManifestPath
        SignaturePolicy  = $sigPolicy
    }
    if ($sigPolicy -eq 'required') {
        $manifestArgs['ManifestSignaturePath']         = $sigPath
        $manifestArgs['ExpectedTrustAnchorThumbprint'] = $expectedThumb
    } elseif (Test-Path -LiteralPath $sigPath -PathType Leaf) {
        $manifestArgs['ManifestSignaturePath'] = $sigPath
        if (-not [string]::IsNullOrWhiteSpace($expectedThumb)) {
            $manifestArgs['ExpectedTrustAnchorThumbprint'] = $expectedThumb
        }
    }
    $manRes = Test-ApprovedEngineManifest @manifestArgs
    if (-not $manRes.ok) {
        return (& $fail 'manifest' ([string]$manRes.error) ([string]$manRes.message))
    }

    # Step 2: approved-only entry selection compatible with cookbook.
    $sel = Select-CompatibleEngineEntry `
        -Manifest        $manRes.manifest `
        -CookbookVersion $cookbookVersion `
        -TargetVersion   $expectedScriptVersion `
        -TargetSha256    $expectedScriptSha
    if (-not $sel.ok) {
        return (& $fail 'select' ([string]$sel.error) ([string]$sel.message))
    }
    $entry = $sel.entry

    # Step 3: read operator bytes, triple-match SHA, stage under WorkDirectory.
    $local = Get-PaxScriptByLocalFile `
        -LocalFilePath         $PaxScriptPath `
        -Manifest              $manRes.manifest `
        -WorkDirectory         $WorkDirectory `
        -CookbookVersion       $cookbookVersion `
        -TargetVersion         $expectedScriptVersion `
        -ExpectedVersionSha256 $expectedScriptSha
    if (-not $local.ok) {
        return (& $fail 'localfile' ([string]$local.error) ([string]$local.message))
    }

    # Step 4: activate the staged file onto the canonical path and
    # stamp install-state.json with source='automation'.
    $canonical = Join-Path $AppRoot 'resources\pax\PAX_Purview_Audit_Log_Processor.ps1'
    $activateArgs = @{
        StagedFilePath      = $local.stagedPath
        ExpectedSha256      = $expectedScriptSha
        Version             = [string]$entry['version']
        CanonicalScriptPath = $canonical
        Source              = 'automation'
        ManifestId          = $manRes.manifestId
        ManifestHash        = $manRes.manifestHash
        ManifestVersion     = $manRes.manifestVersion
    }
    if (-not [string]::IsNullOrWhiteSpace($StatePath)) { $activateArgs['StatePath'] = $StatePath }
    $act = Set-PaxScriptActivated @activateArgs
    if (-not $act.ok) {
        return (& $fail 'activate' ([string]$act.error) ([string]$act.message))
    }

    return @{
        ok              = $true
        canonicalPath   = $act.canonicalPath
        sha256          = $act.sha256
        version         = $act.version
        source          = $act.source
        manifestId      = $manRes.manifestId
        manifestHash    = $manRes.manifestHash
        manifestVersion = $manRes.manifestVersion
        validatedAtUtc  = $act.validatedAtUtc
        activatedAtUtc  = $act.activatedAtUtc
        statePath       = $act.statePath
    }
}

# ---------------------------------------------------------------------
# Phase S helpers: launcher subtree copy + user-scope shortcut lifecycle
# ---------------------------------------------------------------------
#
# The release zip ships the launcher script at "<extracted>/launcher/
# Start-PAXCookbook.ps1" -- one level ABOVE the installer's SourceRoot
# (which is "<extracted>/app/"). The launcher is not part of the App\
# tree that Copy-TreeContents transfers; we must copy it separately,
# into "<InstallRoot>\App\launcher\Start-PAXCookbook.ps1" so the
# canonical Start Menu shortcut has a stable target.
#
# The launcher script self-locates and supports both the source-tree
# layout (<repo>\launcher\Start-PAXCookbook.ps1) and the installed
# layout (<InstallRoot>\App\launcher\Start-PAXCookbook.ps1). See the
# path-discovery section at the top of the launcher.

function Copy-LauncherSubtree {
    # Copy "<SourceRoot>\..\launcher\" into "<AppDir>\launcher\".
    # If the sibling launcher folder does not exist (e.g. running from
    # an unusual layout), log a warning and return $false. The caller
    # decides whether the shortcut step can still proceed.
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$AppDir
    )
    $sourceParent  = Split-Path -Parent $SourceRoot
    $launcherSrc   = Join-Path $sourceParent 'launcher'
    $launcherEntry = Join-Path $launcherSrc  'Start-PAXCookbook.ps1'
    if (-not (Test-Path -LiteralPath $launcherEntry -PathType Leaf)) {
        Write-InstallLog ('Launcher source not found at expected location: ' + $launcherEntry) 'WARN'
        Write-InstallLog 'Shortcut creation will be skipped because no stable launcher path exists in the install tree.' 'WARN'
        return $false
    }
    $launcherDst = Join-Path $AppDir 'launcher'
    if (-not (Test-Path -LiteralPath $launcherDst -PathType Container)) {
        New-Item -ItemType Directory -Path $launcherDst -Force | Out-Null
    }
    # Copy contents (not the folder itself) so we don't nest.
    Get-ChildItem -LiteralPath $launcherSrc -Force | ForEach-Object {
        $dst = Join-Path $launcherDst $_.Name
        Copy-Item -LiteralPath $_.FullName -Destination $dst -Recurse -Force
    }
    Write-InstallLog ('Copied launcher subtree: ' + $launcherSrc + ' -> ' + $launcherDst)
    return $true
}

function Get-ShortcutStartMenuFolder {
    # V1.S19 PART F: user-scope Start Menu FOLDER that contains all PAX-
    # owned shortcuts (main launcher + maintenance shortcuts). No
    # machine-scope, no registry edits.
    if ($Script:ShortcutSandboxRoot) {
        $programs = Join-Path $Script:ShortcutSandboxRoot 'StartMenu\Programs'
        if (-not (Test-Path -LiteralPath $programs -PathType Container)) {
            New-Item -ItemType Directory -Path $programs -Force | Out-Null
        }
        return (Join-Path $programs 'PAX Cookbook')
    }
    $sm = [Environment]::GetFolderPath('StartMenu')
    if ([string]::IsNullOrWhiteSpace($sm)) {
        throw 'Cannot resolve user StartMenu folder.'
    }
    $programs = Join-Path $sm 'Programs'
    if (-not (Test-Path -LiteralPath $programs -PathType Container)) {
        New-Item -ItemType Directory -Path $programs -Force | Out-Null
    }
    return (Join-Path $programs 'PAX Cookbook')
}

function Get-ShortcutStartMenuPath {
    # User-scope Start Menu shortcut path. The user-facing Start Menu
    # carries exactly one flat "PAX Cookbook" shortcut directly under
    # Programs\ -- no program-group folder and no maintenance entries.
    if ($Script:ShortcutSandboxRoot) {
        return (Join-Path $Script:ShortcutSandboxRoot 'StartMenu\Programs\PAX Cookbook.lnk')
    }
    $sm = [Environment]::GetFolderPath('StartMenu')
    if ([string]::IsNullOrWhiteSpace($sm)) {
        throw 'Cannot resolve user StartMenu folder.'
    }
    $programs = Join-Path $sm 'Programs'
    if (-not (Test-Path -LiteralPath $programs -PathType Container)) {
        New-Item -ItemType Directory -Path $programs -Force | Out-Null
    }
    return (Join-Path $programs 'PAX Cookbook.lnk')
}

function Get-ShortcutRepairPath {
    # "Repair PAX Cookbook Shortcuts" maintenance entry. Capital S
    # in "Shortcuts" is canonical; the pre-rename lowercase variant
    # is swept by Get-ShortcutRepairPathLegacy on install / update /
    # repair so an existing chef does not end up with two entries.
    $folder = Get-ShortcutStartMenuFolder
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }
    return (Join-Path $folder 'Repair PAX Cookbook Shortcuts.lnk')
}

function Get-ShortcutRepairPathLegacy {
    # Pre-rename filename for the Repair shortcut (lowercase "s" in
    # "shortcuts"). NTFS is case-insensitive, so this path resolves
    # to the same on-disk entry as the canonical Repair path, but
    # the on-disk casing only changes if the legacy file is deleted
    # first and the canonical-cased file is created fresh. The
    # install / update / repair / uninstall flows do exactly that.
    $folder = Get-ShortcutStartMenuFolder
    return (Join-Path $folder 'Repair PAX Cookbook shortcuts.lnk')
}

function Get-ShortcutRemovePath {
    # Legacy "Remove PAX Cookbook shortcuts" Start Menu entry. This
    # entry is no longer created as a user-facing Start Menu shortcut
    # (chefs now use Settings or the CLI to remove shortcuts), but
    # the path is still resolved so install / update / repair /
    # uninstall flows can sweep any pre-existing entry from an older
    # install. The CLI maintenance mode -Mode remove-shortcuts still
    # works; it is invoked directly, not via a Start Menu shortcut.
    $folder = Get-ShortcutStartMenuFolder
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }
    return (Join-Path $folder 'Remove PAX Cookbook shortcuts.lnk')
}

function Get-ShortcutSupportModePath {
    # UX-1H10 PART F: "PAX Cookbook Support Mode" Start Menu entry.
    # Launches Start-PAXCookbookSupportMode.ps1 from the install tree.
    $folder = Get-ShortcutStartMenuFolder
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }
    return (Join-Path $folder 'PAX Cookbook Support Mode.lnk')
}

function Get-ShortcutStopPath {
    # "Stop PAX Cookbook Server" Start Menu entry.
    # Launches Stop-PAXCookbook.ps1 from the install tree.
    $folder = Get-ShortcutStartMenuFolder
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }
    return (Join-Path $folder 'Stop PAX Cookbook Server.lnk')
}

function Get-ShortcutStopPathLegacy {
    # Pre-rename filename for the Stop shortcut. Older installs wrote
    # the entry as "Stop PAX Cookbook.lnk"; install / update / repair
    # / uninstall flows sweep this path so the operator does not end
    # up with two Start Menu entries for the same action.
    $folder = Get-ShortcutStartMenuFolder
    return (Join-Path $folder 'Stop PAX Cookbook.lnk')
}

function Get-ShortcutUninstallPath {
    # "Uninstall PAX Cookbook" Start Menu entry. Targets this
    # installer with -Mode uninstall-prompt, which shows a plain-
    # language WinForms confirmation dialog before any files are
    # removed. The shortcut filename naturally sorts last among the
    # PAX Cookbook Start Menu entries (P, P, R, S, U) so the
    # destructive action is at the bottom of the alphabetical list.
    $folder = Get-ShortcutStartMenuFolder
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }
    return (Join-Path $folder 'Uninstall PAX Cookbook.lnk')
}

function Get-LegacyStartMenuShortcutPath {
    # V1.S19 PART F: the pre-folder Start Menu shortcut path. Old
    # installs (<= V1.S18) wrote a single "PAX Cookbook.lnk" directly
    # under Programs\. The new install/update/repair flow migrates
    # that .lnk into the new "PAX Cookbook" folder and deletes the
    # legacy file so the operator does not end up with two shortcuts.
    if ($Script:ShortcutSandboxRoot) {
        return (Join-Path $Script:ShortcutSandboxRoot 'StartMenu\Programs\PAX Cookbook.lnk')
    }
    $sm = [Environment]::GetFolderPath('StartMenu')
    if ([string]::IsNullOrWhiteSpace($sm)) {
        throw 'Cannot resolve user StartMenu folder.'
    }
    return (Join-Path $sm 'Programs\PAX Cookbook.lnk')
}

function Get-ShortcutDesktopPath {
    if ($Script:ShortcutSandboxRoot) {
        $dt = Join-Path $Script:ShortcutSandboxRoot 'Desktop'
        if (-not (Test-Path -LiteralPath $dt -PathType Container)) {
            New-Item -ItemType Directory -Path $dt -Force | Out-Null
        }
        return (Join-Path $dt 'PAX Cookbook.lnk')
    }
    $dt = [Environment]::GetFolderPath('Desktop')
    if ([string]::IsNullOrWhiteSpace($dt)) {
        throw 'Cannot resolve user Desktop folder.'
    }
    return (Join-Path $dt 'PAX Cookbook.lnk')
}

function Resolve-PwshExePath {
    # Locate pwsh.exe for the shortcut TargetPath. Refuse to point at
    # the legacy Windows PowerShell powershell.exe; the launcher
    # requires PowerShell 7.4+.
    $cmd = Get-Command -Name 'pwsh.exe' -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -and (Test-Path -LiteralPath $cmd.Source -PathType Leaf)) {
        return $cmd.Source
    }
    # Fallback: use the path of the currently running pwsh process.
    try {
        $proc = Get-Process -Id $PID -ErrorAction Stop
        if ($proc.Path -and (Test-Path -LiteralPath $proc.Path -PathType Leaf)) {
            $name = [System.IO.Path]::GetFileName($proc.Path).ToLowerInvariant()
            if ($name -eq 'pwsh.exe' -or $name -eq 'pwsh') {
                return $proc.Path
            }
        }
    } catch { }
    throw 'Cannot locate pwsh.exe on this machine. PAX Cookbook requires PowerShell 7.4+.'
}

function Add-UserScopeShortcut {
    # Create or overwrite a .lnk file. User-scope only; never invokes
    # Start-Process -Verb RunAs, never edits the registry, never
    # creates services or scheduled tasks.
    #
    # The shortcut's argument string is constrained to:
    #   -NoLogo -NoProfile -ExecutionPolicy Bypass -File "<canonical launcher path>"
    # No -EncodedCommand, no -Command with embedded script. The
    # launcher binary (Start-PAXCookbook.ps1) is delivered inside the
    # release ZIP, whose entry-by-entry SHA-256 manifest is verified
    # by Test-PaxPackageTrust before the install proceeds; that is
    # the integrity boundary, not the per-script Authenticode chain.
    # -ExecutionPolicy Bypass is required because the unpacked
    # launcher inherits Mark-of-the-Web from the downloaded ZIP and
    # would otherwise be blocked by the default RemoteSigned policy,
    # producing the operator-visible "shortcut flashes black terminal
    # and exits" failure mode (V1.S18-shortcut root cause).
    param(
        [Parameter(Mandatory)][string]$ShortcutPath,
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)][string]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter()][string]$IconLocation,
        [Parameter()][string]$Description,
        [Parameter()][ValidateSet(1, 3, 7)][int]$WindowStyle = 1
    )
    if ([string]::IsNullOrWhiteSpace($ShortcutPath)) { throw 'ShortcutPath must be a non-empty path.' }

    $parent = Split-Path -Parent $ShortcutPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $wsh = $null
    $lnk = $null
    try {
        $wsh = New-Object -ComObject WScript.Shell
        $lnk = $wsh.CreateShortcut($ShortcutPath)
        $lnk.TargetPath       = $TargetPath
        $lnk.Arguments        = $Arguments
        $lnk.WorkingDirectory = $WorkingDirectory
        # UX-1H10: WScript.Shell shortcut WindowStyle property only
        # supports 1=Normal, 3=Maximized, 7=Minimized. Hidden cannot
        # be expressed at this layer; the caller must instead pass
        # "-WindowStyle Hidden" inside the Arguments string so pwsh.exe
        # hides its own console after start-up. WindowStyle remains
        # Normal (1) for shortcuts whose target wants a visible
        # console (Support Mode, Stop, Repair, Remove).
        $lnk.WindowStyle      = $WindowStyle
        if ($IconLocation)  { $lnk.IconLocation = $IconLocation }
        if ($Description)   { $lnk.Description  = $Description }
        $lnk.Save()
    } finally {
        if ($lnk) { try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($lnk) } catch { } }
        if ($wsh) { try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($wsh) } catch { } }
    }
    if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) {
        throw ('Shortcut was not written: ' + $ShortcutPath)
    }
    Write-InstallLog ('Shortcut created: ' + $ShortcutPath)
}

function Remove-UserScopeShortcut {
    param([Parameter(Mandatory)][string]$ShortcutPath)
    if (Test-Path -LiteralPath $ShortcutPath -PathType Leaf) {
        try {
            Remove-Item -LiteralPath $ShortcutPath -Force
            Write-InstallLog ('Shortcut removed: ' + $ShortcutPath)
        } catch {
            Write-InstallLog ('Failed to remove shortcut ' + $ShortcutPath + ': ' + $_.Exception.Message) 'WARN'
        }
    } else {
        Write-InstallLog ('Shortcut not present (skip): ' + $ShortcutPath)
    }
}

function Initialize-ShortcutPropertyStoreType {
    # Lazily compile an in-process C# helper that opens a .lnk via
    # IShellLink / IPersistFile, then sets
    # PKEY_AppUserModel_ExcludeFromShowInNewInstall
    # ({9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3} 8) to VARIANT_TRUE so
    # Windows hides the shortcut from the Start Menu "Recently added"
    # surface (and, on Windows 11, from the Recommended section's
    # newly-installed-app row). The shortcut still appears in the
    # alphabetical "All apps" list.
    #
    # Best-effort: if Add-Type fails (constrained language mode,
    # missing compiler, etc.) we log WARN and return $false. Callers
    # continue without suppression and rely on creation-order
    # mitigation alone.
    if (([System.Management.Automation.PSTypeName]'PaxCookbook.ShortcutPropertyStore').Type) {
        return $true
    }
    $cs = @'
using System;
using System.Runtime.InteropServices;

namespace PaxCookbook
{
    public static class ShortcutPropertyStore
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        public class ShellLink { }

        [ComImport]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile(out IntPtr ppszFileName);
        }

        [ComImport]
        [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint cProps);
            [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, IntPtr pv);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, IntPtr pv);
            [PreserveSig] int Commit();
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(IntPtr pvar);

        public static void SetExcludeFromShowInNewInstall(string lnkPath)
        {
            object link = new ShellLink();
            IntPtr pv = IntPtr.Zero;
            try
            {
                // STGM_READWRITE = 2
                ((IPersistFile)link).Load(lnkPath, 2u);

                PROPERTYKEY key = new PROPERTYKEY();
                key.fmtid = new Guid("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3");
                key.pid   = 8u;

                // PROPVARIANT is at most 24 bytes (x64). Allocate 32 for headroom.
                pv = Marshal.AllocCoTaskMem(32);
                for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 11);   // vt = VT_BOOL
                Marshal.WriteInt16(pv, 8, -1);   // boolVal = VARIANT_TRUE

                int hr = ((IPropertyStore)link).SetValue(ref key, pv);
                if (hr < 0) throw new System.ComponentModel.Win32Exception(hr, "IPropertyStore.SetValue failed");
                hr = ((IPropertyStore)link).Commit();
                if (hr < 0) throw new System.ComponentModel.Win32Exception(hr, "IPropertyStore.Commit failed");

                ((IPersistFile)link).Save(lnkPath, true);
            }
            finally
            {
                if (pv != IntPtr.Zero)
                {
                    PropVariantClear(pv);
                    Marshal.FreeCoTaskMem(pv);
                }
                if (link != null) Marshal.FinalReleaseComObject(link);
            }
        }

        public static void SetAppUserModelID(string lnkPath, string aumid)
        {
            // Doctrine sec 11.5: stamp PKEY_AppUserModel_ID
            // ({9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3} 5) with the
            // canonical PAX Cookbook AUMID so Shell groups every
            // PAX-owned taskbar tile and jump list under the same
            // identity, regardless of which .lnk was clicked.
            //
            // VARTYPE: VT_LPWSTR (31). The string buffer must be
            // allocated with CoTaskMemAlloc; PropVariantClear is
            // responsible for freeing it.
            if (string.IsNullOrEmpty(aumid)) throw new ArgumentException("aumid is required", "aumid");
            object link = new ShellLink();
            IntPtr pv = IntPtr.Zero;
            try
            {
                ((IPersistFile)link).Load(lnkPath, 2u);

                PROPERTYKEY key = new PROPERTYKEY();
                key.fmtid = new Guid("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3");
                key.pid   = 5u;

                IntPtr str = Marshal.StringToCoTaskMemUni(aumid);

                pv = Marshal.AllocCoTaskMem(32);
                for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 31);   // vt = VT_LPWSTR
                Marshal.WriteIntPtr(pv, 8, str); // pwszVal pointer at offset 8

                int hr = ((IPropertyStore)link).SetValue(ref key, pv);
                if (hr < 0) throw new System.ComponentModel.Win32Exception(hr, "IPropertyStore.SetValue (AUMID) failed");
                hr = ((IPropertyStore)link).Commit();
                if (hr < 0) throw new System.ComponentModel.Win32Exception(hr, "IPropertyStore.Commit (AUMID) failed");

                ((IPersistFile)link).Save(lnkPath, true);
            }
            finally
            {
                if (pv != IntPtr.Zero)
                {
                    PropVariantClear(pv);
                    Marshal.FreeCoTaskMem(pv);
                }
                if (link != null) Marshal.FinalReleaseComObject(link);
            }
        }
    }
}
'@
    try {
        Add-Type -TypeDefinition $cs -Language CSharp -ErrorAction Stop | Out-Null
        return $true
    } catch {
        Write-InstallLog ('Recommended-suppression helper failed to compile; suppression will be skipped: ' + $_.Exception.Message) 'WARN'
        return $false
    }
}

function Set-ShortcutExcludeFromShowInNewInstall {
    # Apply PKEY_AppUserModel_ExcludeFromShowInNewInstall to a
    # freshly-written .lnk so the Start Menu does not flag it as
    # "Recently added" (and Windows 11 Recommended is less likely
    # to surface it as a newly-installed app). Best-effort: any
    # failure is logged as WARN and the shortcut itself is left in
    # place; creation-order remains the primary mitigation.
    param([Parameter(Mandatory)][string]$ShortcutPath)
    if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) {
        Write-InstallLog ('Cannot apply Recommended-suppression: shortcut not found at ' + $ShortcutPath) 'WARN'
        return $false
    }
    if (-not (Initialize-ShortcutPropertyStoreType)) {
        return $false
    }
    try {
        [PaxCookbook.ShortcutPropertyStore]::SetExcludeFromShowInNewInstall($ShortcutPath)
        Write-InstallLog ('Recommended-suppression applied: ' + $ShortcutPath)
        return $true
    } catch {
        Write-InstallLog ('Recommended-suppression failed for ' + $ShortcutPath + ': ' + $_.Exception.Message) 'WARN'
        return $false
    }
}

function Set-ShortcutAppUserModelID {
    # Stamp PKEY_AppUserModel_ID on a freshly-written .lnk so Shell
    # groups every PAX-owned shortcut, taskbar tile, and jump list
    # under the same AUMID. Doctrine sec 11.5: the canonical literal
    # is 'PAXCookbook.Local.v1' and it is also stamped on the
    # launcher pwsh.exe process via SetCurrentProcessExplicitAppUserModelID.
    # Best-effort: any failure is logged as WARN and the shortcut is
    # left without an AUMID stamp (Shell falls back to a synthesized
    # AUMID based on the .lnk path, which still works but does not
    # group taskbar tiles).
    param(
        [Parameter(Mandatory)][string]$ShortcutPath,
        [Parameter(Mandatory)][string]$AumID
    )
    if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) {
        Write-InstallLog ('Cannot apply AUMID: shortcut not found at ' + $ShortcutPath) 'WARN'
        return $false
    }
    if (-not (Initialize-ShortcutPropertyStoreType)) {
        return $false
    }
    try {
        [PaxCookbook.ShortcutPropertyStore]::SetAppUserModelID($ShortcutPath, $AumID)
        Write-InstallLog ('AUMID stamped (' + $AumID + '): ' + $ShortcutPath)
        return $true
    } catch {
        Write-InstallLog ('AUMID stamp failed for ' + $ShortcutPath + ': ' + $_.Exception.Message) 'WARN'
        return $false
    }
}

function Add-UserScopeShortcutSuppressed {
    # Create a .lnk that should NOT surface in the Start Menu
    # "Recently added" / Windows 11 Recommended lists.
    #
    # Race window we are closing: Add-UserScopeShortcut writes the
    # .lnk to its final location via WScript.Shell.Save() and only
    # then does Set-ShortcutExcludeFromShowInNewInstall reopen the
    # file to stamp PKEY_AppUserModel_ExcludeFromShowInNewInstall.
    # During the few-millisecond gap between Save() and the
    # IPropertyStore commit, the Windows Shell sees a brand-new .lnk
    # appear in the Start Menu directory WITHOUT the suppression
    # bit, indexes it as a newly-installed app, and caches the
    # decision. Setting the property bit afterwards does not
    # retroactively un-cache that decision.
    #
    # Mitigation: build the .lnk in %TEMP% (which the Shell is not
    # tracking for new-install events), stamp the suppression
    # property there, and then atomically Move-Item the file into
    # its final Start Menu location. The Shell sees the .lnk appear
    # at its final path with the bit already on.
    #
    # Caller contract matches Add-UserScopeShortcut. If the staging
    # path or property bit step fails, fall back to the legacy
    # in-place create + post-stamp behaviour so the shortcut is
    # still installed (just less effectively suppressed).
    param(
        [Parameter(Mandatory)][string]$ShortcutPath,
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)][string]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter()][string]$IconLocation,
        [Parameter()][string]$Description,
        [Parameter()][ValidateSet(1, 3, 7)][int]$WindowStyle = 1
    )

    $parent = Split-Path -Parent $ShortcutPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    # Remove any pre-existing copy at the final path so the move at
    # the end is unambiguous and the Shell does not see an update
    # event on a name it has already cached.
    if (Test-Path -LiteralPath $ShortcutPath -PathType Leaf) {
        try {
            Remove-Item -LiteralPath $ShortcutPath -Force
        } catch {
            Write-InstallLog ('Could not pre-delete shortcut ' + $ShortcutPath + ': ' + $_.Exception.Message) 'WARN'
        }
    }

    $leaf      = Split-Path -Leaf $ShortcutPath
    $stageRoot = Join-Path $env:TEMP ('PAXCookbookShortcutStage-' + ([guid]::NewGuid().ToString('N').Substring(0, 12)))
    $stagePath = Join-Path $stageRoot $leaf

    $stagedOk = $false
    try {
        New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
        Add-UserScopeShortcut `
            -ShortcutPath     $stagePath `
            -TargetPath       $TargetPath `
            -Arguments        $Arguments `
            -WorkingDirectory $WorkingDirectory `
            -IconLocation     $IconLocation `
            -Description      $Description `
            -WindowStyle      $WindowStyle
        $stagedOk = (Set-ShortcutExcludeFromShowInNewInstall -ShortcutPath $stagePath)
    } catch {
        Write-InstallLog ('Suppressed-shortcut staging failed for ' + $ShortcutPath + ': ' + $_.Exception.Message) 'WARN'
        $stagedOk = $false
    }

    if ($stagedOk -and (Test-Path -LiteralPath $stagePath -PathType Leaf)) {
        try {
            Move-Item -LiteralPath $stagePath -Destination $ShortcutPath -Force
            Write-InstallLog ('Shortcut staged with suppression then moved into place: ' + $ShortcutPath)
        } catch {
            Write-InstallLog ('Could not move staged shortcut into place; falling back to in-place create: ' + $_.Exception.Message) 'WARN'
            $stagedOk = $false
        }
    }

    if (-not $stagedOk) {
        # Fallback: create directly at the final path and stamp the
        # property bit after the fact. Logged so smoke can surface
        # the regression.
        Write-InstallLog ('Falling back to in-place create + post-stamp for suppressed shortcut: ' + $ShortcutPath) 'WARN'
        Add-UserScopeShortcut `
            -ShortcutPath     $ShortcutPath `
            -TargetPath       $TargetPath `
            -Arguments        $Arguments `
            -WorkingDirectory $WorkingDirectory `
            -IconLocation     $IconLocation `
            -Description      $Description `
            -WindowStyle      $WindowStyle
        [void](Set-ShortcutExcludeFromShowInNewInstall -ShortcutPath $ShortcutPath)
    }

    # Best-effort cleanup of the staging directory.
    if (Test-Path -LiteralPath $stageRoot -PathType Container) {
        try {
            Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction Stop
        } catch {
            Write-InstallLog ('Could not remove shortcut staging directory ' + $stageRoot + ': ' + $_.Exception.Message) 'WARN'
        }
    }
}

function Stop-PAXEdgeAppWindow {
    # Terminate any Edge --app= window that the appliance previously
    # launched against this install root's EdgeAppData user-data-dir.
    # This is a prerequisite for Clear-EdgeAppWindowIconCache: while
    # Edge is running it holds an exclusive lock on Default\Favicons
    # (sqlite) and on the Web Applications\_crx_*\Icons\ files, so
    # the cache purge silently degrades to "skipped" and the stale
    # icon survives the install. The next launch then re-reads the
    # cached icon and the operator sees the same faded taskbar tile
    # they reported before the update.
    #
    # Targeting: match ONLY msedge.exe processes whose CommandLine
    # references this install's EdgeAppData path. The match is
    # case-insensitive and uses -like with the install-root prefix
    # so renderer / GPU / utility subprocesses (which inherit the
    # full --user-data-dir argument from the parent) are also caught.
    # The operator's general-purpose Edge browser is untouched
    # because its user-data-dir lives under %LOCALAPPDATA%\Microsoft
    # \Edge\User Data, never under our install root.
    param(
        [Parameter(Mandatory)][string]$InstallRoot
    )
    $edgeData = Join-Path $InstallRoot 'EdgeAppData'
    $needle = '*' + $edgeData + '*'
    $killed = 0
    try {
        $procs = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'msedge.exe'" -ErrorAction SilentlyContinue
    } catch {
        Write-InstallLog ('Stop-PAXEdgeAppWindow: Win32_Process query failed (Edge cache lock release skipped): ' + $_.Exception.Message) 'WARN'
        return
    }
    if (-not $procs) {
        Write-InstallLog 'Stop-PAXEdgeAppWindow: no PAX Edge --app= windows running.'
        return
    }
    foreach ($p in $procs) {
        if (-not $p.CommandLine) { continue }
        if ($p.CommandLine -notlike $needle) { continue }
        try {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
            Write-InstallLog ('Stop-PAXEdgeAppWindow: terminated PID ' + $p.ProcessId + ' (Edge --app= bound to ' + $edgeData + ')')
            $killed++
        } catch {
            Write-InstallLog ('Stop-PAXEdgeAppWindow: failed to terminate PID ' + $p.ProcessId + ' -- ' + $_.Exception.Message) 'WARN'
        }
    }
    if ($killed -gt 0) {
        # Give Edge a brief moment to flush its sqlite journal and
        # release file handles before the caller attempts to delete
        # the cache files. Without this short settle window, the
        # subsequent Remove-Item can still race the OS handle close
        # and fall through to the "locked" warning path.
        Start-Sleep -Milliseconds 400
        Write-InstallLog ('Stop-PAXEdgeAppWindow: ' + $killed + ' Edge --app= process(es) terminated; cache files should now be unlocked.')
    } else {
        Write-InstallLog 'Stop-PAXEdgeAppWindow: no matching Edge processes (CommandLine did not reference our EdgeAppData path).'
    }
}

function Clear-EdgeAppWindowIconCache {
    # Purge Edge's favicon + Web Applications icon caches inside the
    # PAX Edge user-data-dir so the running app window re-derives its
    # taskbar / title-bar icon from the current HTML <link rel="icon">
    # and manifest.webmanifest entries on the next launch.
    #
    # Background: Edge in --app= mode caches the resolved window icon
    # per --user-data-dir. If a prior version of the appliance shipped
    # an HTML icon chain where the small-frame favicon (.ico with only
    # 16/32/48 px layers) appeared first, Edge stamped that into its
    # Favicons sqlite DB and into Default\Web Applications\. The shell
    # then stretched the 48 px frame to fill the ~96 px taskbar tile,
    # rendering the icon faded / washed out. Reordering the HTML in a
    # later release does NOT trigger Edge to re-fetch on its own
    # because the per-document URL is unchanged. The only reliable
    # remediation is to clear the Edge-side icon cache so the next
    # session re-runs icon resolution against the new HTML/manifest.
    #
    # Best-effort: any file that cannot be removed (e.g. because Edge
    # is currently running and holding a lock on the sqlite DB) is
    # logged as a warning and the install continues. The Edge profile
    # itself (cookies, local storage, preferences) is preserved.
    param(
        [Parameter(Mandatory)][string]$InstallRoot
    )
    $edgeData = Join-Path $InstallRoot 'EdgeAppData'
    $defaultDir = Join-Path $edgeData 'Default'
    if (-not (Test-Path -LiteralPath $defaultDir -PathType Container)) {
        Write-InstallLog ('Edge favicon cache purge skipped: no Edge user-data-dir at ' + $defaultDir)
        return
    }
    $purged = 0
    $skipped = 0
    # 1. The Favicons sqlite DB + journal: stores favicon URL -> blob.
    $faviconTargets = @(
        (Join-Path $defaultDir 'Favicons'),
        (Join-Path $defaultDir 'Favicons-journal')
    )
    foreach ($target in $faviconTargets) {
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            try {
                Remove-Item -LiteralPath $target -Force -ErrorAction Stop
                Write-InstallLog ('Edge favicon cache purged: ' + $target)
                $purged++
            } catch {
                Write-InstallLog ('Edge favicon cache file locked (will be re-derived on next launch if possible): ' + $target + ' -- ' + $_.Exception.Message) 'WARN'
                $skipped++
            }
        }
    }
    # 2. The Web Applications subtree: stores per-PWA resolved icons
    #    under _crx_<id>\Icons\. Removing it forces Edge to redo
    #    manifest icon resolution next time the app URL is opened.
    $webAppsDir = Join-Path $defaultDir 'Web Applications'
    if (Test-Path -LiteralPath $webAppsDir -PathType Container) {
        try {
            Remove-Item -LiteralPath $webAppsDir -Recurse -Force -ErrorAction Stop
            Write-InstallLog ('Edge Web Applications cache purged: ' + $webAppsDir)
            $purged++
        } catch {
            Write-InstallLog ('Edge Web Applications cache could not be fully cleared: ' + $webAppsDir + ' -- ' + $_.Exception.Message) 'WARN'
            $skipped++
        }
    }
    # 3. Top Sites sqlite stores thumbnail blobs which include
    #    favicon-derived tiles. Best-effort: drop these too.
    $topSitesTargets = @(
        (Join-Path $defaultDir 'Top Sites'),
        (Join-Path $defaultDir 'Top Sites-journal')
    )
    foreach ($target in $topSitesTargets) {
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            try {
                Remove-Item -LiteralPath $target -Force -ErrorAction Stop
                $purged++
            } catch {
                $skipped++
            }
        }
    }
    # 4. HTTP content caches: even after the Favicons sqlite DB is
    #    cleared, Edge can rehydrate the favicon mapping by serving
    #    the bytes of images/pax-cookbook-app-icon.ico (and any
    #    historical favicon URL) from these on-disk caches without
    #    re-fetching from the broker. If a previous appliance build
    #    populated the cache with a small-frame .ico, the new icon
    #    chain alone is not enough to refresh the taskbar tile -- the
    #    cached resource must be evicted so Edge re-fetches the
    #    current bytes from the local broker on next launch. The
    #    user's session storage / cookies are preserved (different
    #    subdirectories below Default\). Best-effort recursive delete
    #    with per-file try/catch.
    $contentCacheDirs = @(
        (Join-Path $defaultDir 'Cache'),
        (Join-Path $defaultDir 'Code Cache'),
        (Join-Path $defaultDir 'GPUCache'),
        (Join-Path $defaultDir 'Service Worker')
    )
    foreach ($dir in $contentCacheDirs) {
        if (Test-Path -LiteralPath $dir -PathType Container) {
            try {
                Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Stop
                Write-InstallLog ('Edge content cache purged: ' + $dir)
                $purged++
            } catch {
                Write-InstallLog ('Edge content cache could not be fully cleared: ' + $dir + ' -- ' + $_.Exception.Message) 'WARN'
                $skipped++
            }
        }
    }
    Write-InstallLog ('Edge icon cache purge complete: ' + $purged + ' removed, ' + $skipped + ' skipped (locked or missing)')
}

function Invoke-ShortcutCreation {
    # Apply the operator's shortcut policy to the freshly-installed
    # appliance. The user-facing Start Menu carries exactly one flat
    # shortcut:
    #
    #   Programs\PAX Cookbook.lnk   -> App\bin\PAX Cookbook.exe (native host)
    #
    # There is no program-group folder and there are no user-facing
    # maintenance shortcuts. Any prior "PAX Cookbook" program-group
    # folder (a main entry plus Support Mode / Repair / Stop /
    # Uninstall entries) and the known legacy maintenance .lnk names
    # are swept first; only PAX-owned .lnks are removed, and the
    # folder is deleted once it is empty. The underlying maintenance
    # scripts stay in the install tree and remain reachable through
    # the installer CLI modes (repair-shortcuts, remove-shortcuts,
    # uninstall-prompt).
    #
    # The single flat shortcut is left eligible for Windows "Recently
    # added" / Recommended so the appliance surfaces after a fresh
    # install, and its AUMID is stamped to match the native host
    # process identity so a pinned shortcut and the live window share
    # one taskbar identity.
    #
    # A USER-SCOPE Desktop shortcut targeting the same native host is
    # created per the Desktop policy switches below. Both shortcuts
    # reference the bundled PAX Cookbook icon.
    #
    # Returns $true if at least one shortcut was created, $false if
    # -NoShortcuts suppressed everything.
    param(
        [Parameter(Mandatory)][string]$AppDir,
        [switch]$CreateDesktopShortcut,
        [switch]$NoDesktopShortcut,
        [switch]$NoShortcuts
    )
    if ($NoShortcuts) {
        Write-InstallLog 'Shortcut policy: -NoShortcuts. No Start Menu or Desktop shortcut will be created.'
        return $false
    }

    $launcher = Join-Path $AppDir 'launcher\Start-PAXCookbook.ps1'
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        Write-InstallLog ('Cannot create shortcuts: launcher not present at ' + $launcher) 'WARN'
        return $false
    }

    $installer = Join-Path $AppDir 'install\Install-PAXCookbook.ps1'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        Write-InstallLog ('Cannot create maintenance shortcuts: installer script not present at ' + $installer) 'WARN'
        return $false
    }

    # UX-1H10 PART F: Support Mode + Stop launchers live alongside
    # the main launcher in the launcher subtree that Copy-LauncherSubtree
    # mirrors into the install. They are optional: if a release ZIP
    # predates UX-1H10 and does not ship them, the corresponding
    # shortcuts are simply skipped (logged WARN).
    $supportLauncher = Join-Path $AppDir 'launcher\Start-PAXCookbookSupportMode.ps1'
    $stopLauncher    = Join-Path $AppDir 'launcher\Stop-PAXCookbook.ps1'

    $pwsh = Resolve-PwshExePath
    $nativeExe = Join-Path $AppDir 'bin\PAX Cookbook.exe'
    if (-not (Test-Path -LiteralPath $nativeExe -PathType Leaf)) {
        Write-InstallLog ('Cannot create main shortcuts: native host not present at ' + $nativeExe) 'WARN'
        return $false
    }

    # Production runtime is the native C# host. Main user-facing shortcuts
    # must target PAX Cookbook.exe directly, not pwsh.exe and not the frozen
    # PowerShell broker/launcher. Maintenance shortcuts remain PowerShell
    # tooling because they run installer/stop scripts, not the app runtime.
    $installRootForShortcut = Split-Path -Parent $AppDir
    $workspacePath = Join-Path $installRootForShortcut 'Workspace'
    $args_         = '--workspace "' + $workspacePath + '" --approot "' + $AppDir + '"'
    $repairArgs_   = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $installer + '" -Mode repair-shortcuts'
    $supportArgs_  = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $supportLauncher + '"'
    $stopArgs_     = '-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $stopLauncher + '"'
    $uninstallArgs_= '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $installer + '" -Mode uninstall-prompt'
    $workDir = $AppDir

    # Icon resolution: the bundled PAX Cookbook icon ships at
    # App\web\images\pax-cookbook.ico. The installer requires it; if
    # the icon is missing from the install tree the package itself is
    # incomplete (the Test-PaxPackageTrust manifest check would have
    # caught it), so refuse to create a shortcut with the generic
    # pwsh.exe icon -- that was the V1.S18-shortcut "command prompt
    # icon" failure mode.
    #
    # Icon-priority doctrine (UXR_EDGE_APP_WINDOW_VISUAL_IDENTITY_AND_LAUNCH_POLISH_01):
    # The canonical PAX chef-hat is pax-cookbook-app-icon.ico -- a
    # multi-size ICONDIR with a 256x256 layer that renders crisply at
    # every Start Menu / taskbar / title-bar scale Windows asks for.
    # Prefer it FIRST so the shortcut's IconLocation always feeds the
    # large surfaces (Start tile, taskbar 32-48 px) the high-res
    # chef-hat. The slimmer pax-cookbook-favicon.ico (16/32/48 only)
    # is reserved for the web favicon chain; using it for shortcuts
    # caused the taskbar tile to render super-faded because the shell
    # stretched a 48-px layer into a 96-px tile.
    # Fall back to the richer multi-size pax-cookbook-app-icon.ico
    # when nothing else is present, then to the legacy pax-cookbook.ico
    # for older release packages. The IconLocation is set without an
    # explicit ",0" string because WSH/Shell normalises that anyway,
    # and we never want to pin to a non-zero ICONDIR ordinal.
    $preferredIco = Join-Path $AppDir 'web\images\pax-cookbook-app-icon.ico'
    $faviconIco   = Join-Path $AppDir 'web\images\pax-cookbook-favicon.ico'
    $legacyIco    = Join-Path $AppDir 'web\images\pax-cookbook.ico'
    if (Test-Path -LiteralPath $preferredIco -PathType Leaf) {
        $customIco = $preferredIco
    } elseif (Test-Path -LiteralPath $legacyIco -PathType Leaf) {
        $customIco = $legacyIco
    } elseif (Test-Path -LiteralPath $faviconIco -PathType Leaf) {
        $customIco = $faviconIco
    } else {
        Write-InstallLog ('Cannot create shortcuts: bundled icon missing at ' + $preferredIco) 'WARN'
        return $false
    }
    $iconLoc = $customIco
    $desc           = 'PAX Cookbook - local appliance for Purview Audit Log Processor.'
    $repairDesc     = 'Repair the PAX Cookbook Start Menu and Desktop shortcuts so they point at the current install.'
    $supportDesc    = 'Open PAX Cookbook in Support Mode. Brings the broker console to the foreground (or starts it visible) and opens logs and a status file for diagnostics.'
    $stopDesc       = 'Stop the running PAX Cookbook broker for this workspace. Only terminates the verified broker process; never touches unrelated pwsh.exe processes.'
    $uninstallDesc  = 'Uninstall PAX Cookbook from this Windows user profile. Shows a confirmation dialog before any files are removed; cancelling is safe.'

    $created = $false

    # ================================================================
    # 1. SWEEP all legacy / renamed-away .lnk variants up-front so
    #    the canonical set is written to disk fresh.
    # ================================================================
    # Pre-rename lowercase Repair .lnk (NTFS preserves case on overwrite).
    $repairPathLegacy = Get-ShortcutRepairPathLegacy
    if (Test-Path -LiteralPath $repairPathLegacy -PathType Leaf) {
        if (Test-IsPaxOwnedShortcut -ShortcutPath $repairPathLegacy -AppDir $AppDir) {
            Remove-UserScopeShortcut -ShortcutPath $repairPathLegacy
            Write-InstallLog ('Removed pre-rename Repair shortcut: ' + $repairPathLegacy)
        } else {
            Write-InstallLog ('Pre-rename Repair shortcut exists but is not PAX-owned; left untouched: ' + $repairPathLegacy) 'WARN'
        }
    }
    # Pre-v1 "Remove PAX Cookbook shortcuts.lnk" Start Menu entry is
    # no longer surfaced; sweep any existing PAX-owned copy.
    $removePath = Get-ShortcutRemovePath
    if (Test-Path -LiteralPath $removePath -PathType Leaf) {
        if (Test-IsPaxOwnedShortcut -ShortcutPath $removePath -AppDir $AppDir) {
            Remove-UserScopeShortcut -ShortcutPath $removePath
            Write-InstallLog ('Removed legacy Remove shortcut: ' + $removePath)
        } else {
            Write-InstallLog ('Legacy Remove shortcut exists but is not PAX-owned; left untouched: ' + $removePath) 'WARN'
        }
    }
    # Pre-rename "Stop PAX Cookbook.lnk" (without the "Server" suffix).
    $stopPathLegacy = Get-ShortcutStopPathLegacy
    if (Test-Path -LiteralPath $stopPathLegacy -PathType Leaf) {
        if (Test-IsPaxOwnedShortcut -ShortcutPath $stopPathLegacy -AppDir $AppDir) {
            Remove-UserScopeShortcut -ShortcutPath $stopPathLegacy
            Write-InstallLog ('Removed pre-rename Stop shortcut: ' + $stopPathLegacy)
        } else {
            Write-InstallLog ('Pre-rename Stop shortcut exists but is not PAX-owned; left untouched: ' + $stopPathLegacy) 'WARN'
        }
    }

    # ================================================================
    # 2. Sweep any prior "PAX Cookbook" Start Menu program-group
    #    folder. The user-facing Start Menu now carries exactly one
    #    flat "PAX Cookbook" shortcut and no maintenance entries, so
    #    the older folder layout (main launcher + Support Mode +
    #    Repair + Stop + Uninstall) is removed here. Only PAX-owned
    #    .lnks are removed; a third-party file the operator dropped in
    #    the folder is left alone. The underlying maintenance scripts
    #    in the install tree are NOT touched -- they remain reachable
    #    through the installer CLI modes (repair-shortcuts,
    #    remove-shortcuts, uninstall-prompt).
    # ================================================================
    $groupFolder = Get-ShortcutStartMenuFolder
    if (Test-Path -LiteralPath $groupFolder -PathType Container) {
        $groupEntryNames = @(
            'PAX Cookbook.lnk',
            'PAX Cookbook Support Mode.lnk',
            'Repair PAX Cookbook Shortcuts.lnk',
            'Repair PAX Cookbook shortcuts.lnk',
            'Repair PAX Cookbook.lnk',
            'Stop PAX Cookbook Server.lnk',
            'Stop PAX Cookbook.lnk',
            'Remove PAX Cookbook shortcuts.lnk',
            'Uninstall PAX Cookbook.lnk'
        )
        foreach ($name in $groupEntryNames) {
            $entry = Join-Path $groupFolder $name
            if (Test-Path -LiteralPath $entry -PathType Leaf) {
                if (Test-IsPaxOwnedShortcut -ShortcutPath $entry -AppDir $AppDir) {
                    Remove-UserScopeShortcut -ShortcutPath $entry
                    Write-InstallLog ('Removed legacy Start Menu group entry: ' + $entry)
                } else {
                    Write-InstallLog ('Start Menu group entry exists but is not PAX-owned; left untouched: ' + $entry) 'WARN'
                }
            }
        }
        $groupRemaining = @(Get-ChildItem -LiteralPath $groupFolder -Force -ErrorAction SilentlyContinue)
        if ($groupRemaining.Count -eq 0) {
            try {
                Remove-Item -LiteralPath $groupFolder -Force
                Write-InstallLog ('Removed empty Start Menu program-group folder: ' + $groupFolder)
            } catch {
                Write-InstallLog ('Failed to remove Start Menu folder ' + $groupFolder + ': ' + $_.Exception.Message) 'WARN'
            }
        } else {
            Write-InstallLog ('Start Menu group folder not empty; left in place: ' + $groupFolder)
        }
    }

    # ================================================================
    # 3. Create the single flat PAX Cookbook Start Menu shortcut.
    #    Intentionally NO Set-ShortcutExcludeFromShowInNewInstall
    #    here: the appliance launcher SHOULD surface in "Recently
    #    added" / Recommended after a fresh install.
    # ================================================================
    $smPath = Get-ShortcutStartMenuPath
    Add-UserScopeShortcut `
        -ShortcutPath     $smPath `
        -TargetPath       $nativeExe `
        -Arguments        $args_ `
        -WorkingDirectory $workDir `
        -IconLocation     $iconLoc `
        -Description      $desc
    $created = $true

    if ($NoDesktopShortcut) {
        # UX-1S: explicit opt-out. Do not create a new Desktop
        # shortcut and do not refresh an existing one. Leave any
        # operator-created Desktop .lnk completely untouched.
        Write-InstallLog 'Shortcut policy: -NoDesktopShortcut. Desktop shortcut will not be created or refreshed.'
    } elseif ($CreateDesktopShortcut) {
        $dtPath = Get-ShortcutDesktopPath
        Add-UserScopeShortcut `
            -ShortcutPath     $dtPath `
            -TargetPath       $nativeExe `
            -Arguments        $args_ `
            -WorkingDirectory $workDir `
            -IconLocation     $iconLoc `
            -Description      $desc
        $created = $true
    } else {
        # V1.S18-shortcut: if a Desktop shortcut already exists AND
        # is clearly OWNED by PAX Cookbook (its arguments point at
        # OUR Start-PAXCookbook.ps1, OR its working directory sits
        # under the install root), refresh it in-place to pick up
        # the current target/args/icon. We do NOT delete it; we do
        # NOT create one if absent. Operator-created shortcuts that
        # do not match the ownership test are left alone.
        #
        # UX-1S: this branch is now only reached if the caller
        # explicitly passed -CreateDesktopShortcut:$false without
        # passing -NoDesktopShortcut, which the default invocation
        # path does not do. Kept for completeness and for callers
        # that opt out of creation but still want existing PAX-
        # owned .lnks refreshed against the current install.
        $dtPath = Get-ShortcutDesktopPath
        if (Test-Path -LiteralPath $dtPath -PathType Leaf) {
            if (Test-IsPaxOwnedShortcut -ShortcutPath $dtPath -AppDir $AppDir) {
                Add-UserScopeShortcut `
                    -ShortcutPath     $dtPath `
                    -TargetPath       $nativeExe `
                    -Arguments        $args_ `
                    -WorkingDirectory $workDir `
                    -IconLocation     $iconLoc `
                    -Description      $desc
                Write-InstallLog ('Refreshed existing PAX-owned Desktop shortcut: ' + $dtPath)
                $created = $true
            } else {
                Write-InstallLog ('Desktop shortcut exists but is not PAX-owned; left untouched: ' + $dtPath)
            }
        } else {
            Write-InstallLog 'Shortcut policy: Desktop shortcut creation suppressed (caller passed -CreateDesktopShortcut:$false).'
        }
    }

    # ================================================================
    # 4. Stamp PKEY_AppUserModel_ID on the single PAX Cookbook Start
    #    Menu shortcut and the Desktop shortcut so the running native
    #    app window groups under the same identity for taskbar pin +
    #    jump list (doctrine sec 11.5). The native host process sets
    #    the same AppUserModelID at startup, so a pinned shortcut and
    #    the live window share one taskbar identity.
    #    Best-effort: any failure is logged and the rest of the
    #    install continues.
    # ================================================================
    $aumid = 'PAXCookbook.Local.v1'
    $aumidTargets = @(
        (Get-ShortcutStartMenuPath),
        (Get-ShortcutDesktopPath)
    )
    foreach ($lnk in $aumidTargets) {
        if (Test-Path -LiteralPath $lnk -PathType Leaf) {
            [void](Set-ShortcutAppUserModelID -ShortcutPath $lnk -AumID $aumid)
        }
    }

    return $created
}

function Test-IsPaxOwnedShortcut {
    # Heuristic ownership test for an existing .lnk. We only repair
    # or remove a shortcut whose Arguments name our Start-PAXCookbook.ps1
    # launcher OR our installer script Install-PAXCookbook.ps1 (for
    # the V1.S19 PART F maintenance .lnks), OR whose WorkingDirectory
    # sits inside the install root. Both reads happen via the same
    # WScript.Shell COM bridge used by Add-UserScopeShortcut so
    # behaviour is symmetric.
    param(
        [Parameter(Mandatory)][string]$ShortcutPath,
        [Parameter(Mandatory)][string]$AppDir
    )
    if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) { return $false }
    $wsh = $null
    $lnk = $null
    try {
        $wsh = New-Object -ComObject WScript.Shell
        $lnk = $wsh.CreateShortcut($ShortcutPath)
        $args_       = [string]$lnk.Arguments
        $workingDir  = [string]$lnk.WorkingDirectory
        $argsMatch   = $false
        if ($args_) {
            if ($args_.IndexOf('Start-PAXCookbook.ps1', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $argsMatch = $true
            } elseif ($args_.IndexOf('Install-PAXCookbook.ps1', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $argsMatch = $true
            } elseif ($args_.IndexOf('Start-PAXCookbookSupportMode.ps1', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                # UX-1H10: Support Mode launcher.
                $argsMatch = $true
            } elseif ($args_.IndexOf('Stop-PAXCookbook.ps1', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                # UX-1H10: Stop launcher.
                $argsMatch = $true
            }
        }
        $wdMatch     = $false
        if ($workingDir -and $AppDir) {
            try {
                $wdAbs  = [System.IO.Path]::GetFullPath($workingDir).TrimEnd([char]'\')
                $appAbs = [System.IO.Path]::GetFullPath($AppDir).TrimEnd([char]'\')
                if ($wdAbs.Equals($appAbs, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $wdMatch = $true
                } elseif ($wdAbs.StartsWith($appAbs + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $wdMatch = $true
                }
            } catch { }
        }
        return ($argsMatch -or $wdMatch)
    } catch {
        return $false
    } finally {
        if ($lnk) { try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($lnk) } catch { } }
        if ($wsh) { try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($wsh) } catch { } }
    }
}

function Invoke-ShortcutRemoval {
    # Remove all PAX-owned user-scope shortcuts. This covers the
    # canonical Start Menu folder layout (folder + main + repair +
    # support + stop .lnks), the pre-rename legacy entries (lowercase
    # "Repair PAX Cookbook shortcuts.lnk", legacy "Stop PAX Cookbook.lnk",
    # legacy "Remove PAX Cookbook shortcuts.lnk"), the legacy single-
    # file Start Menu shortcut from pre-folder installs, and the
    # Desktop .lnk if it is PAX-owned. Always safe to call: missing
    # files are logged and skipped, never thrown. Third-party
    # shortcuts that happen to occupy our canonical paths are left
    # alone (logged as WARN).
    param([string]$AppDir)

    # Canonical Start Menu folder layout plus pre-rename legacy
    # entries. Each known PAX-owned .lnk is removed individually so
    # a third-party file the operator dropped into the folder is
    # preserved.
    $folder           = Get-ShortcutStartMenuFolder
    $smPath           = Get-ShortcutStartMenuPath
    $repairPath       = Get-ShortcutRepairPath
    $repairPathLegacy = Get-ShortcutRepairPathLegacy
    $removePath       = Get-ShortcutRemovePath
    $uninstallPath    = Get-ShortcutUninstallPath
    foreach ($p in @($smPath, $repairPath, $repairPathLegacy, $removePath, $uninstallPath)) {
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            if (-not $AppDir -or (Test-IsPaxOwnedShortcut -ShortcutPath $p -AppDir $AppDir)) {
                Remove-UserScopeShortcut -ShortcutPath $p
            } else {
                Write-InstallLog ('Start Menu shortcut exists but is not PAX-owned; left untouched: ' + $p) 'WARN'
            }
        }
    }
    # Support Mode + Stop maintenance shortcuts. The legacy "Stop
    # PAX Cookbook.lnk" name is also swept so pre-rename installs
    # do not leave a stale Start Menu entry behind.
    $supportPath    = Get-ShortcutSupportModePath
    $stopPath       = Get-ShortcutStopPath
    $stopPathLegacy = Get-ShortcutStopPathLegacy
    foreach ($p in @($supportPath, $stopPath, $stopPathLegacy)) {
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            if (-not $AppDir -or (Test-IsPaxOwnedShortcut -ShortcutPath $p -AppDir $AppDir)) {
                Remove-UserScopeShortcut -ShortcutPath $p
            } else {
                Write-InstallLog ('Start Menu shortcut exists but is not PAX-owned; left untouched: ' + $p) 'WARN'
            }
        }
    }
    # If the folder is now empty, remove it. If a third-party file
    # is still there, leave it alone.
    if (Test-Path -LiteralPath $folder -PathType Container) {
        $remaining = @(Get-ChildItem -LiteralPath $folder -Force -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) {
            try {
                Remove-Item -LiteralPath $folder -Force
                Write-InstallLog ('Start Menu folder removed (empty): ' + $folder)
            } catch {
                Write-InstallLog ('Failed to remove Start Menu folder ' + $folder + ': ' + $_.Exception.Message) 'WARN'
            }
        } else {
            Write-InstallLog ('Start Menu folder not empty; left in place: ' + $folder)
        }
    }

    # Legacy pre-folder single-file Start Menu shortcut.
    $legacyPath = Get-LegacyStartMenuShortcutPath
    if (Test-Path -LiteralPath $legacyPath -PathType Leaf) {
        if (-not $AppDir -or (Test-IsPaxOwnedShortcut -ShortcutPath $legacyPath -AppDir $AppDir)) {
            Remove-UserScopeShortcut -ShortcutPath $legacyPath
        } else {
            Write-InstallLog ('Legacy Start Menu shortcut exists but is not PAX-owned; left untouched: ' + $legacyPath) 'WARN'
        }
    }

    # Desktop shortcut: only remove if PAX-owned.
    $dtPath = Get-ShortcutDesktopPath
    if (Test-Path -LiteralPath $dtPath -PathType Leaf) {
        if (-not $AppDir -or (Test-IsPaxOwnedShortcut -ShortcutPath $dtPath -AppDir $AppDir)) {
            Remove-UserScopeShortcut -ShortcutPath $dtPath
        } else {
            Write-InstallLog ('Desktop shortcut exists but is not PAX-owned; left untouched: ' + $dtPath) 'WARN'
        }
    }
}

# =====================================================================
# 4. Mode: install (first-time install)
# =====================================================================

function Invoke-InstallMode {
    Write-InstallSection ('Mode: install   InstallRoot: ' + $Script:InstallerInstallRoot)
    Write-InstallLog    ('SourceRoot      : ' + $Script:InstallerSourceRoot)

    # Refuse to clobber an existing install. The user should run
    # update mode (which is reserved for the broker handoff) or
    # uninstall first.
    $local = Get-LocalVersionInfo
    if ($local) {
        Write-InstallLog ('Existing install detected: cookbook.version=' + [string]$local.cookbook.version) 'ERROR'
        Write-InstallLog 'Install mode refuses to overwrite an existing App\ tree. Use uninstall first, or wait for the broker to hand off an update.' 'ERROR'
        return 1
    }

    # Materialize the canonical layout.
    if (-not (Test-Path -LiteralPath $Script:AppDir -PathType Container)) {
        New-Item -ItemType Directory -Path $Script:AppDir -Force | Out-Null
        Write-InstallLog ('Created: ' + $Script:AppDir)
    }
    if (-not (Test-Path -LiteralPath $Script:BackupsDir -PathType Container)) {
        New-Item -ItemType Directory -Path $Script:BackupsDir -Force | Out-Null
        Write-InstallLog ('Created: ' + $Script:BackupsDir)
    }
    if (-not (Test-Path -LiteralPath $Script:UpdatesDir -PathType Container)) {
        New-Item -ItemType Directory -Path $Script:UpdatesDir -Force | Out-Null
        Write-InstallLog ('Created: ' + $Script:UpdatesDir)
    }

    # Copy source root contents into App\.
    Write-InstallLog ('Copying source root contents into App\')
    Copy-TreeContents -Source $Script:InstallerSourceRoot -Destination $Script:AppDir

    # Phase 13 Stage 1: branch on paxScript.acquisitionPolicy.
    # See Get-PaxAcquisitionPolicy header for the full decision matrix.
    $vfForPolicy = Join-Path $Script:AppDir 'VERSION.json'
    $paxPolicy   = Get-PaxAcquisitionPolicy -VersionFile $vfForPolicy
    Write-InstallLog ('PAX acquisition policy          : ' + $paxPolicy)

    if ($paxPolicy -eq 'external' -and -not (Test-PaxScriptPresentAtCanonicalPath -AppRoot $Script:AppDir)) {
        # External-policy artifact + canonical PAX script absent =
        # expected first-install state. Two routes from here:
        #   (a) Operator passed BOTH -PaxScriptPath and -PaxManifestPath:
        #       run the §3.5 automation-seed pipeline now; on success
        #       the canonical PAX script lands at install time with
        #       paxAcquisition.source='automation' / pending=false; on
        #       failure record lastAttemptError and leave pending=true.
        #   (b) Operator passed only one of the pair:
        #       WARN, ignore both, write pending block.
        #   (c) Operator passed neither (the normal path):
        #       write pending block; broker first-launch SPA acquires.
        $hasScript   = -not [string]::IsNullOrWhiteSpace($PaxScriptPath)
        $hasManifest = -not [string]::IsNullOrWhiteSpace($PaxManifestPath)
        if ($hasScript -and $hasManifest) {
            Write-InstallLog ('External-policy artifact: attempting automation-seed validation (script="' + $PaxScriptPath + '", manifest="' + $PaxManifestPath + '").')
            $seedWorkDir = Join-Path $Script:InstallerInstallRoot 'Updates\seed-work'
            $seed = Invoke-PaxAutomationSeedValidation `
                -PaxScriptPath   $PaxScriptPath `
                -PaxManifestPath $PaxManifestPath `
                -AppRoot         $Script:AppDir `
                -WorkDirectory   $seedWorkDir
            if ($seed.ok) {
                Write-InstallLog ('Automation seed validated: source=automation sha=' + $seed.sha256 + ' version=' + $seed.version + ' manifestId=' + $seed.manifestId)
                [void](Write-InstallStatePaxAcquisition -Fields ([ordered]@{
                    pending          = $false
                    source           = 'automation'
                    version          = $seed.version
                    sha256           = $seed.sha256
                    manifestId       = $seed.manifestId
                    manifestHash     = $seed.manifestHash
                    manifestVersion  = $seed.manifestVersion
                    validatedAtUtc   = $seed.validatedAtUtc
                    activatedAtUtc   = $seed.activatedAtUtc
                    lastAttemptError = $null
                }))
            } else {
                $lae = $seed.lastAttemptError
                Write-InstallLog ('Automation seed validation FAILED: phase=' + $lae.phase + ' code=' + $lae.code + ' message=' + $lae.message) 'WARN'
                Write-InstallLog 'Falling back to pending acquisition; operator must complete acquisition via the SPA on first launch.'
                [void](Write-InstallStatePaxAcquisition -Fields ([ordered]@{
                    pending          = $true
                    source           = $null
                    version          = $null
                    sha256           = $null
                    manifestId       = $null
                    manifestHash     = $null
                    manifestVersion  = $null
                    validatedAtUtc   = $null
                    activatedAtUtc   = $null
                    lastAttemptError = $lae
                }))
            }
        } elseif ($hasScript -or $hasManifest) {
            Write-InstallLog 'automation_seed_pair_incomplete: -PaxScriptPath and -PaxManifestPath must be supplied together. Ignoring both; writing pending block.' 'WARN'
            Write-InstallStatePaxAcquisitionPending -Policy $paxPolicy
        } else {
            Write-InstallLog 'External-policy artifact: PAX engine not bundled. Acquisition will run at first launch.'
            Write-InstallStatePaxAcquisitionPending -Policy $paxPolicy
        }
    } else {
        # Legacy (embedded / missing) OR external + canonical script
        # already present from a prior acquisition: run the existing
        # integrity gate. Tampered or missing-when-required halts install.
        if (-not (Test-IsCanonicalPaxScript -AppRoot $Script:AppDir)) {
            Write-InstallLog 'Bundled PAX script is NOT at the canonical path after copy. Aborting.' 'ERROR'
            return 2
        }
        $integrity = Compare-PaxScriptIntegrity -AppRoot $Script:AppDir
        if (-not $integrity.ok) {
            Write-InstallLog ('PAX integrity check FAILED: ' + ($integrity | ConvertTo-Json -Compress)) 'ERROR'
            return 3
        }
        Write-InstallLog ('Bundled PAX SHA-256 verified: ' + $integrity.sha)
    }

    # Phase S: copy the launcher subtree into the install tree so the
    # shortcut has a stable target inside App\.
    [void](Copy-LauncherSubtree -SourceRoot $Script:InstallerSourceRoot -AppDir $Script:AppDir)

    # Stamp installedAt on the new VERSION.json.
    $vf  = Join-Path $Script:AppDir 'VERSION.json'
    $ver = Read-JsonFile -Path $vf
    if (-not $ver.cookbook) { $ver.cookbook = @{} }
    $ver.cookbook.installedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    Write-JsonFile -Path $vf -Object $ver
    Write-InstallLog ('Stamped cookbook.installedAt = ' + $ver.cookbook.installedAt)

    # Phase S: shortcut lifecycle (user-scope only).
    try {
        [void](Invoke-ShortcutCreation `
            -AppDir $Script:AppDir `
            -CreateDesktopShortcut:$Script:DesktopShortcutEnabled `
            -NoDesktopShortcut:$NoDesktopShortcut `
            -NoShortcuts:$NoShortcuts)
    } catch {
        Write-InstallLog ('Shortcut creation failed (install proceeds without shortcuts): ' + $_.Exception.Message) 'WARN'
    }

    Write-InstallLog ''
    Write-InstallLog ('Install complete. App root: ' + $Script:AppDir)
    return 0
}

# =====================================================================
# 5. Mode: update (in-place update via broker handoff or harness)
# =====================================================================

function Resolve-UpdateSourceRoot {
    # Update mode source comes from one of:
    #   (1) the handoff JSON's "stagedExtractedPath" key (broker path)
    #   (2) the -SourceRoot parameter (test harness path)
    #   (3) (NOT a fallback) walking up from $PSScriptRoot — that
    #       would be the wrong source on a normal update because the
    #       installer is invoked from the *staged* location.
    if ($SourceRoot) {
        return $Script:InstallerSourceRoot
    }
    if ($Handoff) {
        if (-not (Test-Path -LiteralPath $Handoff -PathType Leaf)) {
            throw ('Handoff record not found: ' + $Handoff)
        }
        $h = Read-JsonFile -Path $Handoff
        if (-not $h.stagedExtractedPath) {
            throw 'Handoff record missing stagedExtractedPath.'
        }
        $sp = [string]$h.stagedExtractedPath
        $vf = Join-Path $sp 'VERSION.json'
        if (-not (Test-Path -LiteralPath $vf -PathType Leaf)) {
            throw ('stagedExtractedPath does not contain VERSION.json: ' + $sp)
        }
        return $sp
    }
    # If neither is provided, fall back to the script's source root
    # (already resolved). This is the harness fallback.
    return $Script:InstallerSourceRoot
}

function Invoke-UpdateMode {
    Write-InstallSection ('Mode: update   InstallRoot: ' + $Script:InstallerInstallRoot)

    # Resolve the staged source.
    $stagedRoot = $null
    try {
        $stagedRoot = Resolve-UpdateSourceRoot
    } catch {
        Write-InstallLog ('Update source resolution failed: ' + $_.Exception.Message) 'ERROR'
        return 1
    }
    Write-InstallLog ('StagedSourceRoot: ' + $stagedRoot)

    # Refuse if no existing App\ tree exists. Update mode is reserved
    # for transitioning from N -> N+1, not for first-time install.
    $local = Get-LocalVersionInfo
    if (-not $local) {
        Write-InstallLog ('No existing App\ tree at ' + $Script:AppDir + '. Use install mode for first-time install.') 'ERROR'
        return 2
    }
    $oldVersion = [string]$local.cookbook.version
    if (-not $oldVersion) { $oldVersion = 'unknown' }
    Write-InstallLog ('Existing installed cookbook.version = ' + $oldVersion)

    # Refuse if the broker appears to be live. The broker must exit
    # before the installer mutates App\.
    if (Test-IsBrokerRunning) {
        Write-InstallLog ('Broker appears to be running against ' + $Script:AppDir + '. Stop it before running update mode.') 'ERROR'
        return 3
    }

    # Backup the existing App\ tree to Backups\App_<oldVersion>_<ts>\.
    $ts = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    $backupDir = Join-Path $Script:BackupsDir ('App_' + $oldVersion + '_' + $ts)
    Write-InstallLog ('Moving current App\ to backup: ' + $backupDir)
    if (-not (Test-Path -LiteralPath $Script:BackupsDir -PathType Container)) {
        New-Item -ItemType Directory -Path $Script:BackupsDir -Force | Out-Null
    }
    try {
        Move-Item -LiteralPath $Script:AppDir -Destination $backupDir -Force -ErrorAction Stop
    } catch {
        Write-InstallLog ('Failed to back up current App\: ' + $_.Exception.Message) 'ERROR'
        return 4
    }

    # Recreate App\ and copy staged content into it.
    try {
        New-Item -ItemType Directory -Path $Script:AppDir -Force | Out-Null
        Write-InstallLog ('Copying staged source into App\')
        Copy-TreeContents -Source $stagedRoot -Destination $Script:AppDir
    } catch {
        Write-InstallLog ('Failed to copy staged content into App\: ' + $_.Exception.Message) 'ERROR'
        # Rollback.
        try { Remove-Item -LiteralPath $Script:AppDir -Recurse -Force } catch {}
        try { Move-Item -LiteralPath $backupDir -Destination $Script:AppDir -Force } catch {}
        Write-InstallLog ('Rolled back to previous App\ tree from ' + $backupDir) 'WARN'
        return 5
    }

    # Verify canonical bundled PAX placement + integrity.
    # Phase 13 Stage 1: branch on paxScript.acquisitionPolicy from the
    # newly-staged VERSION.json. Update mode follows the same matrix as
    # install mode (Get-PaxAcquisitionPolicy header). Rollback on
    # tampering, not on legitimate absence under external policy.
    $vfForPolicy = Join-Path $Script:AppDir 'VERSION.json'
    $paxPolicy   = Get-PaxAcquisitionPolicy -VersionFile $vfForPolicy
    Write-InstallLog ('PAX acquisition policy          : ' + $paxPolicy)

    if ($paxPolicy -eq 'external' -and -not (Test-PaxScriptPresentAtCanonicalPath -AppRoot $Script:AppDir)) {
        Write-InstallLog 'External-policy artifact: PAX engine not bundled in update payload. Acquisition will run at first launch.'
        Write-InstallStatePaxAcquisitionPending -Policy $paxPolicy
    } else {
        if (-not (Test-IsCanonicalPaxScript -AppRoot $Script:AppDir)) {
            Write-InstallLog 'Bundled PAX not at canonical path after staged copy. Rolling back.' 'ERROR'
            try { Remove-Item -LiteralPath $Script:AppDir -Recurse -Force } catch {}
            try { Move-Item -LiteralPath $backupDir -Destination $Script:AppDir -Force } catch {}
            return 6
        }
        $integrity = Compare-PaxScriptIntegrity -AppRoot $Script:AppDir
        if (-not $integrity.ok) {
            # External-policy update + canonical PAX present + SHA
            # mismatches new VERSION.json pin: plan §9.3 says quarantine
            # the prior bytes, write a pending acquisition block with
            # lastAttemptError, complete the app-shell update, and
            # DO NOT roll back. Acquisition will be re-driven via the
            # SPA on first launch. The legacy embedded path (no
            # acquisitionPolicy field) still rolls back -- only the
            # explicit external policy gets the quarantine-not-rollback
            # treatment because only it has a well-defined recovery
            # surface (the SPA setup wizard).
            if ($paxPolicy -eq 'external' -and $integrity.reason -eq 'sha-mismatch') {
                $oldSha = [string]$integrity.actual
                $newSha = [string]$integrity.expected
                Write-InstallLog ('External-policy update: PAX canonical SHA-256 (' + $oldSha + ') does not match new VERSION.json pin (' + $newSha + '). Quarantining prior bytes and writing pending acquisition.') 'WARN'
                $qPath = $null
                try {
                    $qPath = Move-PaxScriptToQuarantine -AppRoot $Script:AppDir -OldSha256 $oldSha
                } catch {
                    Write-InstallLog ('Quarantine move failed (continuing update): ' + $_.Exception.Message) 'WARN'
                }
                $lae = [ordered]@{
                    phase         = 'hash'
                    code          = 'pax_script_hash_mismatch_after_update'
                    message       = ('Canonical PAX SHA-256 ' + $oldSha + ' did not match new pin ' + $newSha + '. Prior bytes moved to ' + ([string]$qPath) + '.')
                    occurredAtUtc = ([DateTimeOffset]::UtcNow).ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
                }
                [void](Write-InstallStatePaxAcquisition -Fields ([ordered]@{
                    pending          = $true
                    source           = $null
                    version          = $null
                    sha256           = $null
                    manifestId       = $null
                    manifestHash     = $null
                    manifestVersion  = $null
                    validatedAtUtc   = $null
                    activatedAtUtc   = $null
                    lastAttemptError = $lae
                }))
                Write-InstallLog 'acquisition_required_after_update: app-shell update completed; PAX acquisition must be re-driven via the SPA on first launch.' 'WARN'
            } else {
                Write-InstallLog ('PAX integrity check FAILED after staged copy: ' + ($integrity | ConvertTo-Json -Compress)) 'ERROR'
                try { Remove-Item -LiteralPath $Script:AppDir -Recurse -Force } catch {}
                try { Move-Item -LiteralPath $backupDir -Destination $Script:AppDir -Force } catch {}
                Write-InstallLog ('Rolled back to previous App\ tree from ' + $backupDir) 'WARN'
                return 7
            }
        } else {
            Write-InstallLog ('Bundled PAX SHA-256 verified post-update: ' + $integrity.sha)
        }
    }

    # Phase S: refresh the launcher subtree inside App\ from the staged
    # tree so the shortcut target stays valid after an update.
    [void](Copy-LauncherSubtree -SourceRoot $stagedRoot -AppDir $Script:AppDir)

    # Stamp installedAt on the new VERSION.json.
    $vf  = Join-Path $Script:AppDir 'VERSION.json'
    $ver = Read-JsonFile -Path $vf
    if (-not $ver.cookbook) { $ver.cookbook = @{} }
    $ver.cookbook.installedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    Write-JsonFile -Path $vf -Object $ver
    Write-InstallLog ('Stamped cookbook.installedAt = ' + $ver.cookbook.installedAt)

    # Purge Edge's cached window-icon mapping so the running --app=
    # window re-derives its taskbar / title-bar icon from the new
    # HTML <link rel="icon"> chain and manifest on the next launch.
    # This is best-effort and runs on every update install -- the
    # alternative (a faded taskbar tile when shipping a new icon
    # asset) is far worse than a one-time icon re-fetch.
    # Step 1: terminate any in-flight PAX Edge --app= window so it
    #         releases its Favicons sqlite lock and the cache purge
    #         actually deletes the files (instead of silently
    #         skipping them and leaving the stale icon in place).
    try {
        Stop-PAXEdgeAppWindow -InstallRoot $Script:InstallerInstallRoot
    } catch {
        Write-InstallLog ('PAX Edge --app= shutdown failed (icon cache may not refresh until operator closes Edge): ' + $_.Exception.Message) 'WARN'
    }
    try {
        Clear-EdgeAppWindowIconCache -InstallRoot $Script:InstallerInstallRoot
    } catch {
        Write-InstallLog ('Edge icon cache purge failed (update proceeds): ' + $_.Exception.Message) 'WARN'
    }

    # Phase S: re-create shortcuts. The launcher path is unchanged
    # (App\launcher\Start-PAXCookbook.ps1), but rewriting the .lnk
    # files refreshes their internal state and recovers shortcuts the
    # operator may have deleted between updates.
    try {
        [void](Invoke-ShortcutCreation `
            -AppDir $Script:AppDir `
            -CreateDesktopShortcut:$Script:DesktopShortcutEnabled `
            -NoDesktopShortcut:$NoDesktopShortcut `
            -NoShortcuts:$NoShortcuts)
    } catch {
        Write-InstallLog ('Shortcut refresh failed (update proceeds without shortcuts): ' + $_.Exception.Message) 'WARN'
    }

    Write-InstallLog ''
    Write-InstallLog ('Update complete. New cookbook.version = ' + [string]$ver.cookbook.version + '   Backup: ' + $backupDir)
    return 0
}

# =====================================================================
# 6. Mode: uninstall
# =====================================================================

function Invoke-UninstallMode {
    Write-InstallSection ('Mode: uninstall   InstallRoot: ' + $Script:InstallerInstallRoot)

    if (-not (Test-Path -LiteralPath $Script:InstallerInstallRoot -PathType Container)) {
        Write-InstallLog ('Install root does not exist. Nothing to do.') 'WARN'
        # Even with no install root, remove any orphan shortcuts the
        # user may still have, so uninstall always leaves the system
        # clean of PAX Cookbook entries.
        try { Invoke-ShortcutRemoval -AppDir $Script:AppDir } catch {
            Write-InstallLog ('Orphan shortcut removal failed: ' + $_.Exception.Message) 'WARN'
        }
        return 0
    }

    if (-not $Yes) {
        Write-InstallLog 'Refusing to uninstall without -Yes. Re-run with -Yes to confirm.' 'ERROR'
        return 1
    }

    if (Test-IsBrokerRunning) {
        Write-InstallLog ('Broker appears to be running against ' + $Script:AppDir + '. Stop it before uninstalling.') 'ERROR'
        return 2
    }

    # Phase S: remove user-scope shortcuts FIRST. The launcher path is
    # about to disappear; orphan shortcuts must not survive.
    try { Invoke-ShortcutRemoval -AppDir $Script:AppDir } catch {
        Write-InstallLog ('Shortcut removal raised: ' + $_.Exception.Message) 'WARN'
    }

    if (Test-Path -LiteralPath $Script:AppDir -PathType Container) {
        try {
            Remove-Item -LiteralPath $Script:AppDir -Recurse -Force
            Write-InstallLog ('Removed: ' + $Script:AppDir)
        } catch {
            Write-InstallLog ('Failed to remove App\ tree: ' + $_.Exception.Message) 'ERROR'
            return 3
        }
    }

    # Doctrine sec 11.6: sweep the Edge app-window user-data
    # directory at %LOCALAPPDATA%\PAXCookbook\EdgeAppData. This is
    # the Chromium profile created by Open-EdgeAppModeToRuntime; it
    # is uniquely owned by the appliance, so uninstall takes it.
    # The parent %LOCALAPPDATA%\PAXCookbook folder is shared with
    # Updates\, so we only remove the EdgeAppData subtree -- not
    # the whole parent.
    $edgeAppData = Join-Path $env:LOCALAPPDATA 'PAXCookbook\EdgeAppData'
    if (Test-Path -LiteralPath $edgeAppData -PathType Container) {
        try {
            Remove-Item -LiteralPath $edgeAppData -Recurse -Force
            Write-InstallLog ('Removed: ' + $edgeAppData)
        } catch {
            Write-InstallLog ('Failed to remove EdgeAppData\: ' + $_.Exception.Message) 'WARN'
        }
    }

    if ($Purge) {
        if (Test-Path -LiteralPath $Script:BackupsDir -PathType Container) {
            try {
                Remove-Item -LiteralPath $Script:BackupsDir -Recurse -Force
                Write-InstallLog ('Removed: ' + $Script:BackupsDir)
            } catch {
                Write-InstallLog ('Failed to remove Backups\: ' + $_.Exception.Message) 'WARN'
            }
        }
        if (Test-Path -LiteralPath $Script:UpdatesDir -PathType Container) {
            try {
                Remove-Item -LiteralPath $Script:UpdatesDir -Recurse -Force
                Write-InstallLog ('Removed: ' + $Script:UpdatesDir)
            } catch {
                Write-InstallLog ('Failed to remove Updates\: ' + $_.Exception.Message) 'WARN'
            }
        }
    }

    Write-InstallLog 'Uninstall complete. (Workspace data outside the install root is untouched.)'
    return 0
}

# =====================================================================
# 6b. Mode: uninstall-prompt (user-facing confirmed uninstall)
# =====================================================================

function Invoke-UninstallPromptMode {
    # User-facing entry point invoked by the "Uninstall PAX Cookbook"
    # Start Menu shortcut. Steps:
    #
    #   1. Refuse if no install root present (nothing to do).
    #   2. Refuse if the broker appears to be running, with an OK-only
    #      dialog instructing the operator to click "Stop PAX Cookbook
    #      Server" first.
    #   3. Show a plain-language WinForms confirmation dialog that
    #      spells out EXACTLY what will and will NOT be deleted.
    #      Default focus is "No" (cancel-safe).
    #   4. On Yes: copy this installer to a fresh %TEMP% folder, then
    #      spawn pwsh.exe from that relocated copy with
    #         -Mode uninstall -Yes -Purge -InstallRoot <real-root>
    #      and exit so the shortcut-launched pwsh.exe releases any
    #      handle on the install tree. The relocated installer
    #      removes App\, Backups\, Updates\, and all PAX-owned
    #      shortcuts. The %TEMP%\PAXCookbookUninstall-* folder is the
    #      one acceptable leftover artifact (documented in the
    #      operator guide).
    #   5. On No: log "Uninstall cancelled" and exit 0 with no
    #      side effects.
    #
    # This mode INTENTIONALLY DOES NOT delete operator workspaces or
    # any folder outside the install root. Workspaces live at user-
    # chosen paths the installer does not track; auto-deleting them
    # would risk taking user data (recipes, bake outputs, exports)
    # with the appliance. The dialog tells the operator how to remove
    # workspaces by hand.
    Write-InstallSection ('Mode: uninstall-prompt   InstallRoot: ' + $Script:InstallerInstallRoot)

    if (-not (Test-Path -LiteralPath $Script:InstallerInstallRoot -PathType Container)) {
        Write-InstallLog ('Install root does not exist; nothing to uninstall.') 'WARN'
        return 0
    }

    $installerSrc = Join-Path $Script:AppDir 'install\Install-PAXCookbook.ps1'
    if (-not (Test-Path -LiteralPath $installerSrc -PathType Leaf)) {
        Write-InstallLog ('Installer not present at expected path: ' + $installerSrc) 'ERROR'
        return 1
    }

    # Pre-flight: refuse if broker is running. Tell the operator how
    # to stop it; do not auto-terminate it.
    if (Test-IsBrokerRunning) {
        Write-InstallLog ('Broker appears to be running against ' + $Script:AppDir + '; refusing to prompt for uninstall.') 'ERROR'
        try {
            Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
            [System.Windows.Forms.MessageBox]::Show(
                "PAX Cookbook is currently running.`r`n`r`nClick `"Stop PAX Cookbook Server`" in the PAX Cookbook Start Menu folder, wait for the server window to close, then re-run Uninstall.",
                'PAX Cookbook - Uninstall',
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
        } catch {
            Write-InstallLog ('WinForms unavailable while broker is running: ' + $_.Exception.Message) 'WARN'
        }
        return 2
    }

    # Plain-language confirmation copy. No raw %LOCALAPPDATA% paths
    # in the visible body; the dialog speaks in operator terms only.
    $heading = 'Uninstall PAX Cookbook?'
    $body = @'
This will remove:
  - Installed PAX Cookbook app files
  - PAX Cookbook Start Menu shortcuts
  - PAX Cookbook Desktop shortcut, if present
  - Local update cache and previous-version backups
  - Local Edge app-window data for PAX Cookbook (sign-in cookies, history)

This will not remove:
  - Your selected workspace folder
  - Recipes, bake history, logs, and runtime files inside the workspace
  - Exported Purview, Entra, or Microsoft 365 files saved outside the install folder
  - Windows scheduled tasks you created outside Cookbook

After uninstall:
  - Reinstall PAX Cookbook to use the app again.
  - If you want to remove scheduled tasks, delete them separately in Windows Task Scheduler.

Continue with uninstall?
'@

    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        Add-Type -AssemblyName System.Drawing       -ErrorAction Stop
    } catch {
        Write-InstallLog ('WinForms unavailable; cannot show uninstall confirmation: ' + $_.Exception.Message) 'ERROR'
        Write-InstallLog 'Run the installer manually with -Mode uninstall -Yes -Purge to uninstall without the dialog.' 'ERROR'
        return 3
    }

    # Custom-sized confirmation dialog. The native MessageBox.Show()
    # surface picks its width from a tight set of internal heuristics
    # that produces a cramped column for multi-line text; building a
    # plain Form lets us pick a comfortable reading width and keep
    # the No button focused by default.
    $form = New-Object System.Windows.Forms.Form
    $form.Text            = 'PAX Cookbook - Uninstall'
    $form.StartPosition   = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $form.MaximizeBox     = $false
    $form.MinimizeBox     = $false
    $form.ShowIcon        = $false
    $form.ShowInTaskbar   = $false
    $form.ClientSize      = New-Object System.Drawing.Size(600, 440)
    $form.AutoScaleMode   = [System.Windows.Forms.AutoScaleMode]::Dpi
    $form.Font            = New-Object System.Drawing.Font('Segoe UI', 9)

    $headingLabel = New-Object System.Windows.Forms.Label
    $headingLabel.Text      = $heading
    $headingLabel.Location  = New-Object System.Drawing.Point(20, 18)
    $headingLabel.Size      = New-Object System.Drawing.Size(560, 28)
    $headingLabel.Font      = New-Object System.Drawing.Font('Segoe UI', 12, [System.Drawing.FontStyle]::Bold)
    $headingLabel.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
    $form.Controls.Add($headingLabel)

    $bodyBox = New-Object System.Windows.Forms.TextBox
    $bodyBox.Multiline   = $true
    $bodyBox.ReadOnly    = $true
    $bodyBox.ScrollBars  = [System.Windows.Forms.ScrollBars]::Vertical
    $bodyBox.WordWrap    = $true
    $bodyBox.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    $bodyBox.BackColor   = $form.BackColor
    $bodyBox.TabStop     = $false
    $bodyBox.Location    = New-Object System.Drawing.Point(20, 52)
    $bodyBox.Size        = New-Object System.Drawing.Size(560, 340)
    $bodyBox.Text        = $body
    $form.Controls.Add($bodyBox)

    $noButton = New-Object System.Windows.Forms.Button
    $noButton.Text         = '&No, cancel'
    $noButton.Location     = New-Object System.Drawing.Point(380, 402)
    $noButton.Size         = New-Object System.Drawing.Size(100, 26)
    $noButton.DialogResult = [System.Windows.Forms.DialogResult]::No
    $form.Controls.Add($noButton)

    $yesButton = New-Object System.Windows.Forms.Button
    $yesButton.Text         = '&Yes, uninstall'
    $yesButton.Location     = New-Object System.Drawing.Point(486, 402)
    $yesButton.Size         = New-Object System.Drawing.Size(100, 26)
    $yesButton.DialogResult = [System.Windows.Forms.DialogResult]::Yes
    $form.Controls.Add($yesButton)

    # Enter cancels (AcceptButton = No); Escape also cancels. The
    # destructive action requires a deliberate mouse click or
    # explicit Alt+Y to fire.
    $form.AcceptButton = $noButton
    $form.CancelButton = $noButton
    $form.ActiveControl = $noButton

    $dialogResult = $form.ShowDialog()
    $form.Dispose()
    if ($dialogResult -ne [System.Windows.Forms.DialogResult]::Yes) {
        Write-InstallLog 'Uninstall cancelled by user.'
        return 0
    }

    # Relocate the installer so the install tree can be removed
    # without releasing-handle conflicts.
    $stamp   = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss')
    $guid    = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $tempDir = Join-Path $env:TEMP ('PAXCookbookUninstall-' + $stamp + '-' + $guid)
    $installerDst = $null
    try {
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
        $installerDst = Join-Path $tempDir 'Install-PAXCookbook.ps1'
        Copy-Item -LiteralPath $installerSrc -Destination $installerDst -Force
        Write-InstallLog ('Relocated installer for self-uninstall: ' + $installerDst)
    } catch {
        Write-InstallLog ('Failed to stage uninstall helper at ' + $tempDir + ': ' + $_.Exception.Message) 'ERROR'
        return 4
    }

    $pwshExe = $null
    try { $pwshExe = Resolve-PwshExePath } catch {
        Write-InstallLog ('Cannot locate pwsh.exe to launch uninstaller: ' + $_.Exception.Message) 'ERROR'
        return 5
    }
    $relocArgs = @(
        '-NoLogo','-NoProfile','-ExecutionPolicy','Bypass','-File', $installerDst,
        '-Mode','uninstall','-Yes','-Purge','-InstallRoot', $Script:InstallerInstallRoot
    )
    Write-InstallLog ('Spawning relocated uninstaller: ' + $pwshExe + ' ' + ($relocArgs -join ' '))
    try {
        Start-Process -FilePath $pwshExe -ArgumentList $relocArgs -WindowStyle Hidden | Out-Null
    } catch {
        Write-InstallLog ('Failed to spawn relocated uninstaller: ' + $_.Exception.Message) 'ERROR'
        return 6
    }
    Write-InstallLog ('Uninstall confirmed. Relocated installer launched; this process exits so the install tree can be removed.')
    return 0
}

# =====================================================================
# 7. Mode: check (read-only state report)
# =====================================================================

function Invoke-CheckMode {
    Write-InstallSection ('Mode: check   InstallRoot: ' + $Script:InstallerInstallRoot)

    $installerVersion = 'unknown'
    if ($Script:InstallerSourceRoot) {
        try {
            $srcVer = Read-JsonFile -Path (Join-Path $Script:InstallerSourceRoot 'VERSION.json')
            if ($srcVer.cookbook -and $srcVer.cookbook.version) {
                $installerVersion = [string]$srcVer.cookbook.version
            }
        } catch { }
    }
    Write-InstallLog ('Installer (this script) version : ' + $installerVersion)
    Write-InstallLog ('Install root                    : ' + $Script:InstallerInstallRoot)
    Write-InstallLog ('App\                            : ' + $Script:AppDir)
    Write-InstallLog ('Backups\                        : ' + $Script:BackupsDir)
    Write-InstallLog ('Updates\                        : ' + $Script:UpdatesDir)
    Write-InstallLog ('Log file                        : ' + $Script:LogFile)

    $local = Get-LocalVersionInfo
    if (-not $local) {
        Write-InstallLog 'No App\VERSION.json found. Cookbook is not installed at this location.' 'WARN'
        return 0
    }
    Write-InstallLog ('Installed cookbook.version      : ' + [string]$local.cookbook.version)
    Write-InstallLog ('Installed paxScript.version     : ' + [string]$local.paxScript.version)
    Write-InstallLog ('Installed paxScript.sha256      : ' + [string]$local.paxScript.sha256)
    Write-InstallLog ('Installed cookbook.installedAt  : ' + [string]$local.cookbook.installedAt)

    # Phase 13 Stage 1: report acquisition policy, and under "external"
    # policy treat a legitimately-absent canonical PAX script as
    # "acquisition pending" rather than an integrity failure.
    $vfForPolicy = Join-Path $Script:AppDir 'VERSION.json'
    $paxPolicy   = Get-PaxAcquisitionPolicy -VersionFile $vfForPolicy
    Write-InstallLog ('PAX acquisition policy          : ' + $paxPolicy)

    if ($paxPolicy -eq 'external' -and -not (Test-PaxScriptPresentAtCanonicalPath -AppRoot $Script:AppDir)) {
        Write-InstallLog ('Bundled PAX integrity           : NOT BUNDLED (external policy; acquisition pending at first launch)')
        return 0
    }

    $integrity = Compare-PaxScriptIntegrity -AppRoot $Script:AppDir
    if ($integrity.ok) {
        Write-InstallLog ('Bundled PAX integrity           : OK (sha256 matches VERSION.json)')
    } else {
        Write-InstallLog ('Bundled PAX integrity           : FAILED  ' + ($integrity | ConvertTo-Json -Compress)) 'ERROR'
        return 4
    }
    return 0
}

# =====================================================================
# 7b. Mode: repair-shortcuts (rewrite stale .lnks against current tree)
# =====================================================================

function Invoke-RepairShortcutsMode {
    # Repair the user-scope Start Menu shortcuts AND the Desktop
    # shortcut so that they point at the launcher in the current
    # install tree and reference the bundled .ico. This mode copies
    # no files; it only rewrites .lnk records under the operator's
    # per-user folders. It exists because older installer versions
    # wrote a Start Menu .lnk with no IconLocation override, which
    # made the shell display the generic pwsh.exe icon (V1.S18-
    # shortcut failure mode). Running this mode after an update
    # brings stale shortcuts back into alignment without having to
    # uninstall/reinstall.
    #
    # UX-1S: Desktop shortcut recreation is now the DEFAULT for this
    # mode. The operator opts out per-invocation by passing
    # -NoDesktopShortcut, in which case repair will neither delete
    # nor recreate the Desktop .lnk -- any existing PAX-owned Desktop
    # shortcut is left exactly as found. -NoShortcuts continues to
    # mean "remove all PAX-owned shortcuts" (including the Desktop
    # one) and is honored before any recreation step.
    Write-InstallSection ('Mode: repair-shortcuts   InstallRoot: ' + $Script:InstallerInstallRoot)

    if (-not (Test-Path -LiteralPath $Script:AppDir -PathType Container)) {
        Write-InstallLog ('App\ directory not found at: ' + $Script:AppDir) 'ERROR'
        Write-InstallLog 'repair-shortcuts requires an already-installed appliance.' 'ERROR'
        return 1
    }
    $launcher = Join-Path $Script:AppDir 'launcher\Start-PAXCookbook.ps1'
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        Write-InstallLog ('Launcher not present at: ' + $launcher) 'ERROR'
        return 2
    }
    # Prefer the refreshed multi-size app icon; fall back to the
    # legacy single-size icon so older installs can still be repaired.
    $preferredIco = Join-Path $Script:AppDir 'web\images\pax-cookbook-app-icon.ico'
    $legacyIco    = Join-Path $Script:AppDir 'web\images\pax-cookbook.ico'
    if (Test-Path -LiteralPath $preferredIco -PathType Leaf) {
        $customIco = $preferredIco
    } elseif (Test-Path -LiteralPath $legacyIco -PathType Leaf) {
        $customIco = $legacyIco
    } else {
        Write-InstallLog ('Bundled icon missing at: ' + $legacyIco) 'ERROR'
        Write-InstallLog 'The installed App\ tree is incomplete; cannot repair shortcuts.' 'ERROR'
        return 3
    }

    if ($NoShortcuts) {
        Write-InstallLog '-NoShortcuts: removing all PAX-owned shortcuts and not recreating them.'
        Invoke-ShortcutRemoval -AppDir $Script:AppDir
        return 0
    }

    # Force-delete any PAX-owned Start Menu .lnks first. Overwriting in
    # place via WScript.Shell sometimes leaves the Windows shell
    # IconCache.db pinned to the previous icon (the explicit reason
    # chefs see the pwsh icon even after a corrected installer runs).
    # Delete-then-create yields a guaranteed fresh icon read because
    # the .lnk's identity in the cache is invalidated.
    #
    # The canonical Start Menu layout is a folder containing the main
    # launcher .lnk plus three maintenance / runtime .lnks (repair,
    # support, stop). The pre-rename lowercase Repair shortcut and
    # the legacy Remove shortcut are also force-deleted here if they
    # exist and are PAX-owned. The legacy single-file pre-folder
    # shortcut at "Programs\PAX Cookbook.lnk" is removed by the
    # ShortcutCreation step below as part of migration.
    $smPath           = Get-ShortcutStartMenuPath
    $repairPath       = Get-ShortcutRepairPath
    $repairPathLegacy = Get-ShortcutRepairPathLegacy
    $removePath       = Get-ShortcutRemovePath
    $uninstallPath    = Get-ShortcutUninstallPath
    foreach ($p in @($smPath, $repairPath, $repairPathLegacy, $removePath, $uninstallPath)) {
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            if (Test-IsPaxOwnedShortcut -ShortcutPath $p -AppDir $Script:AppDir) {
                Remove-UserScopeShortcut -ShortcutPath $p
            } else {
                Write-InstallLog ('Start Menu shortcut exists but is not PAX-owned; left untouched: ' + $p) 'WARN'
                Write-InstallLog 'Repair did NOT modify a third-party shortcut at the canonical path.' 'WARN'
                return 4
            }
        }
    }
    # Same delete-first treatment for the Desktop .lnk when
    # -NoDesktopShortcut was NOT supplied. UX-1S: Desktop shortcut
    # recreation is the default for repair-shortcuts, so we delete
    # the PAX-owned Desktop .lnk here (to invalidate the IconCache
    # entry) and let the Invoke-ShortcutCreation call below write a
    # fresh one. When -NoDesktopShortcut IS supplied, any existing
    # Desktop .lnk is left exactly as found and no new one is
    # written. -NoShortcuts already returned earlier.
    if (-not $NoDesktopShortcut) {
        $dtPath = Get-ShortcutDesktopPath
        if (Test-Path -LiteralPath $dtPath -PathType Leaf) {
            if (Test-IsPaxOwnedShortcut -ShortcutPath $dtPath -AppDir $Script:AppDir) {
                Remove-UserScopeShortcut -ShortcutPath $dtPath
            } else {
                Write-InstallLog ('Desktop shortcut exists but is not PAX-owned; left untouched: ' + $dtPath) 'WARN'
            }
        }
    } else {
        Write-InstallLog '-NoDesktopShortcut: existing Desktop shortcut (if any) will be preserved as-is.'
    }

    [void](Invoke-ShortcutCreation `
        -AppDir $Script:AppDir `
        -CreateDesktopShortcut:$Script:DesktopShortcutEnabled `
        -NoDesktopShortcut:$NoDesktopShortcut `
        -NoShortcuts:$NoShortcuts)

    return 0
}

# =====================================================================
# 7c. Mode: remove-shortcuts (delete PAX-owned shortcuts, leave app
#     tree, backups, updates, and workspace data alone)
# =====================================================================

function Invoke-RemoveShortcutsMode {
    # V1.S19 PART F: operator-facing entry point for deleting the
    # Start Menu folder + .lnks + Desktop .lnk without touching any
    # installed application files, backups, updates, or workspace
    # data. Surfaced in the Start Menu folder as the
    # "Remove PAX Cookbook shortcuts" entry so the operator does not
    # have to browse to Programs\PAX Cookbook\ by hand.
    #
    # Idempotent: missing files are logged and skipped. Third-party
    # shortcuts that happen to occupy a PAX canonical path are
    # logged as WARN and left alone -- this mode never deletes
    # something it cannot prove belongs to PAX Cookbook.
    Write-InstallSection ('Mode: remove-shortcuts   InstallRoot: ' + $Script:InstallerInstallRoot)

    # Use the install root for the PAX-ownership check when it
    # exists, but the mode still runs (and removes legacy .lnks)
    # even if the App\ tree has already been deleted.
    $appForCheck = $Script:AppDir
    try { Invoke-ShortcutRemoval -AppDir $appForCheck } catch {
        Write-InstallLog ('Shortcut removal raised: ' + $_.Exception.Message) 'WARN'
        return 1
    }
    Write-InstallLog 'Shortcut removal complete. App tree, backups, updates, and workspace data were NOT modified.'
    return 0
}

# =====================================================================
# 8. Dispatch
# =====================================================================

Write-InstallLog ''
Write-InstallLog ('Install-PAXCookbook.ps1 invoked   mode=' + $Mode + '   pid=' + $PID + '   user=' + $env:USERNAME)

$rc = 0
switch ($Mode) {
    'install'          { $rc = Invoke-InstallMode }
    'update'           { $rc = Invoke-UpdateMode }
    'uninstall'        { $rc = Invoke-UninstallMode }
    'uninstall-prompt' { $rc = Invoke-UninstallPromptMode }
    'check'            { $rc = Invoke-CheckMode }
    'repair-shortcuts' { $rc = Invoke-RepairShortcutsMode }
    'remove-shortcuts' { $rc = Invoke-RemoveShortcutsMode }
    default            {
        Write-InstallLog ('Unknown mode: ' + $Mode) 'ERROR'
        $rc = 1
    }
}

Write-InstallLog ('Exit code: ' + $rc)
exit $rc
