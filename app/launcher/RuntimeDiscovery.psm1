#requires -Version 7.4

# =====================================================================
# RuntimeDiscovery.psm1
#
# Read-only helpers used by the canonical launcher
# (launcher/Start-PAXCookbook.ps1, installed at
# <InstallRoot>\App\launcher\Start-PAXCookbook.ps1) to:
#
#   * decide whether the broker is already running for the chosen
#     workspace, so the launcher can REUSE it instead of spawning a
#     second broker;
#   * resolve the authoritative loopback runtime URL from the
#     workspace.lock file that the broker writes;
#   * open the user's DEFAULT browser to that URL (without naming any
#     specific browser binary);
#   * stand up a short-lived watcher that polls for the broker to come
#     up after a fresh spawn, then opens the browser.
#
# Hard rules (enforced by the Phase S verification harness):
#
#   - Read-only against the workspace.lock file. The launcher never
#     writes the lock; only the broker writes it (Start-Broker.ps1
#     section C3, Write-WorkspaceLock).
#   - Reuse the same authoritative shape the broker already verifies:
#     PID alive + pwsh image + TCP connect on the recorded port +
#     /api/v1/health 200 with matching workspaceFolderPath. Nothing new
#     is added to the lock file schema.
#   - No outbound HTTP. Only loopback (`http://127.0.0.1:<port>/...`)
#     probes ever go on the wire.
#   - No machine-wide mutation. No registry writes, no scheduled
#     tasks, no services, no autorun, no PATH edits, no
#     ExecutionPolicy / Defender / firewall mutation.
#   - Browser open uses `Start-Process -FilePath <url>`. The user's
#     registered URL handler decides which browser launches. No
#     browser binary is ever named -- with one carve-out documented
#     in doctrine §11: `msedge.exe` is allowed as the host for the
#     canonical app-window experience (`Open-EdgeAppModeToRuntime`).
#     When Edge is not installed, the dispatcher falls back to the
#     OS-registered http handler (`Open-DefaultBrowserToRuntime`) and
#     no non-Microsoft browser binary is ever invoked by name.
#   - No hidden-window powershell, no -ExecutionPolicy Bypass, no
#     -EncodedCommand, no obfuscated launches.
#   - Refuse to open any URL that is not loopback http. A malformed
#     lock file pointing at an external host MUST NOT cause the
#     launcher to navigate the user's browser away from the local
#     loopback adapter. [Uri]::IsLoopback covers both 127.0.0.1 and
#     localhost; the canonical browser-facing form is localhost
#     (UX-1H6: Chrome/Edge reject 127.0.0.1 as a WebAuthn effective
#     domain).
# =====================================================================

Set-StrictMode -Version Latest


function Get-WorkspaceLockPath {
    # The canonical lock-file path inside a workspace. The broker owns
    # the file's lifetime; this function only computes the path.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$WorkspacePath)
    return (Join-Path $WorkspacePath 'Runtime\workspace.lock')
}


function Read-WorkspaceLockSnapshot {
    # Parse the workspace.lock file and return a structured snapshot, or
    # $null if the file is missing, unreadable, or schema-incomplete.
    # Never throws on documented failure modes; the launcher treats any
    # null return as "no recorded broker, proceed to spawn".
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$WorkspacePath)

    $lockPath = Get-WorkspaceLockPath -WorkspacePath $WorkspacePath
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) { return $null }

    $raw = $null
    try {
        $raw = Get-Content -LiteralPath $lockPath -Raw -ErrorAction Stop
    } catch {
        return $null
    }
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }

    $obj = $null
    try {
        $obj = $raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        return $null
    }
    if (-not $obj) { return $null }
    $names = @($obj.PSObject.Properties.Name)
    if ($names -notcontains 'brokerProcessId') { return $null }
    if ($names -notcontains 'brokerPort')      { return $null }

    $pid_ = 0
    $port = 0
    try { $pid_ = [int]$obj.brokerProcessId } catch { return $null }
    try { $port = [int]$obj.brokerPort      } catch { return $null }
    if ($pid_ -le 0 -or $port -le 0 -or $port -gt 65535) { return $null }

    $launchTs    = if ($names -contains 'launchTimestampUtc') { [string]$obj.launchTimestampUtc } else { $null }
    $machineName = if ($names -contains 'machineName')        { [string]$obj.machineName }        else { $null }
    $userName    = if ($names -contains 'windowsUserName')    { [string]$obj.windowsUserName }    else { $null }
    $cookVer     = if ($names -contains 'cookbookVersion')    { [string]$obj.cookbookVersion }    else { $null }

    return [pscustomobject][ordered]@{
        BrokerProcessId    = $pid_
        BrokerPort         = $port
        LaunchTimestampUtc = $launchTs
        MachineName        = $machineName
        WindowsUserName    = $userName
        CookbookVersion    = $cookVer
        LockPath           = $lockPath
    }
}


function Test-LiveBrokerProcess {
    # True iff the recorded PID is alive AND its image is pwsh(.exe).
    # If Get-Process cannot read the image path (rights, race), we
    # treat the broker as not-live so the launcher prefers spawning a
    # clean broker over reusing an ambiguous one.
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$ProcessId)

    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) { return $false }

    $path = $null
    try { $path = $proc.Path } catch { }
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }

    $name = [System.IO.Path]::GetFileName($path).ToLowerInvariant()
    return ($name -eq 'pwsh.exe' -or $name -eq 'pwsh')
}


function Test-LiveBrokerTcpPort {
    # Short-timeout loopback TCP connect. Bounded by $TimeoutMs so the
    # launcher never blocks on a stuck port.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$Port,
        [int]$TimeoutMs = 500
    )

    if ($Port -le 0 -or $Port -gt 65535) { return $false }

    $tcp = [System.Net.Sockets.TcpClient]::new()
    try {
        $iar = $tcp.BeginConnect([System.Net.IPAddress]::Loopback, $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs)) { return $false }
        $tcp.EndConnect($iar)
        return $true
    } catch {
        return $false
    } finally {
        try { $tcp.Close() } catch { }
        try { $tcp.Dispose() } catch { }
    }
}


function Test-LiveBrokerHealth {
    # /api/v1/health probe with the strict workspaceFolderPath match.
    # This is the canonical "is THIS broker serving THIS workspace?"
    # check; the same shape the broker uses internally in
    # Test-PriorBrokerActive.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$ExpectedWorkspacePath,
        [int]$TimeoutSec = 2
    )

    try {
        $url = 'http://127.0.0.1:' + $Port + '/api/v1/health'
        $resp = Invoke-WebRequest -Uri $url -TimeoutSec $TimeoutSec -UseBasicParsing -ErrorAction Stop
        if ($resp.StatusCode -ne 200) { return $false }
        $body = $resp.Content | ConvertFrom-Json -ErrorAction Stop
        if (-not $body.workspaceFolderPath) { return $false }
        $a = [System.IO.Path]::GetFullPath([string]$body.workspaceFolderPath).TrimEnd('\','/')
        $b = [System.IO.Path]::GetFullPath($ExpectedWorkspacePath).TrimEnd('\','/')
        return [string]::Equals($a, $b, [System.StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}


function Test-LiveBrokerAvailable {
    # Composite reuse predicate. Returns the snapshot if every layer
    # passes, otherwise $null. The launcher reuses an existing broker
    # IFF this returns non-$null.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$WorkspacePath)

    $snap = Read-WorkspaceLockSnapshot -WorkspacePath $WorkspacePath
    if (-not $snap) { return $null }
    if (-not (Test-LiveBrokerProcess -ProcessId $snap.BrokerProcessId)) { return $null }
    if (-not (Test-LiveBrokerTcpPort -Port $snap.BrokerPort))            { return $null }
    if (-not (Test-LiveBrokerHealth -Port $snap.BrokerPort -ExpectedWorkspacePath $WorkspacePath)) { return $null }
    return $snap
}


function Get-BrokerSessionToken {
    # Read the broker session token from <WorkspacePath>\Runtime\broker.token.
    # The broker writes this sidecar inside Write-WorkspaceLock alongside
    # workspace.lock; the token is a base64url string and is the credential
    # boot.js must capture from the URL fragment for the SPA to authenticate
    # against the broker.
    #
    # Returns the token string on success, or $null if the sidecar is
    # missing, unreadable, empty, or does not match the expected shape.
    # Never throws on documented failure modes; the launcher treats $null
    # as 'no token yet, open the browser tokenless and warn the operator'.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$WorkspacePath)

    $tokenPath = Join-Path $WorkspacePath 'Runtime\broker.token'
    if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) { return $null }

    $raw = $null
    try {
        $raw = [System.IO.File]::ReadAllText($tokenPath, [System.Text.UTF8Encoding]::new($false))
    } catch {
        return $null
    }
    if ($null -eq $raw) { return $null }

    $tok = $raw.Trim()
    if ([string]::IsNullOrEmpty($tok)) { return $null }

    # Strict base64url-safe shape. New-SessionToken in the broker emits
    # 43-char tokens; the 20..120 envelope tolerates any future widening
    # without re-opening this code path.
    if ($tok -notmatch '^[A-Za-z0-9_-]{20,120}$') { return $null }

    return $tok
}


function Format-RedactedRuntimeUrl {
    # Strip the session token from a runtime URL so it can be logged
    # without exposing the credential. Returns the URL with the token
    # value replaced by '<REDACTED>'. URLs with no fragment, or with
    # only a route fragment, pass through verbatim.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RuntimeUrl)

    if ([string]::IsNullOrWhiteSpace($RuntimeUrl)) { return $RuntimeUrl }
    if ($RuntimeUrl -match '^(http://[^#]+)#(.*)$') {
        $base = $matches[1]
        $frag = $matches[2]
        if ($frag -match '^t=[A-Za-z0-9_-]+$') {
            return $base + '#t=<REDACTED>'
        }
        if ($frag -match '^(.+)&t=[A-Za-z0-9_-]+$') {
            return $base + '#' + $matches[1] + '&t=<REDACTED>'
        }
        return $RuntimeUrl
    }
    return $RuntimeUrl
}


function Get-RuntimeUrlFromSnapshot {
    # Compose the authoritative loopback URL from a lock snapshot.
    # UX-1H6: the canonical browser-facing origin is
    # http://localhost:<port>. Chrome/Edge reject 127.0.0.1 as a
    # valid WebAuthn effective domain ("SecurityError: This is an
    # invalid domain"), so the launcher steers the user's browser to
    # the localhost form. The broker still binds 127.0.0.1 on the
    # same port for back-compat with internal health probes and any
    # operator who pastes the IP directly; only the URL the launcher
    # OPENS in the browser flips to localhost.
    # The PORT is dynamic and comes from the snapshot. When -Token is
    # supplied (and matches the base64url-safe shape), the token is
    # appended as a URL fragment so boot.js in the SPA can capture it
    # into sessionStorage and the api.js bearer wall stops rejecting
    # every request as 401.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Snapshot,
        [string]$Token
    )

    if ($null -eq $Snapshot)                  { throw 'Snapshot is null.' }
    if (-not $Snapshot.BrokerPort)            { throw 'Snapshot has no BrokerPort.' }
    $port = [int]$Snapshot.BrokerPort
    if ($port -le 0 -or $port -gt 65535)      { throw ('Snapshot port out of range: ' + $port) }

    $base = 'http://localhost:' + $port + '/'
    if ([string]::IsNullOrEmpty($Token)) { return $base }
    if ($Token -notmatch '^[A-Za-z0-9_-]{20,120}$') {
        throw 'Refusing to compose runtime URL: token does not match expected base64url-safe shape.'
    }
    return $base + '#t=' + $Token
}


function Test-LoopbackHttpRuntimeUrl {
    # Shared URL guard used by every browser-open path. Throws when
    # the URL is not parseable, not absolute, not http, not loopback,
    # or carries a fragment whose shape is not one of the two
    # documented forms (#t=<token> or #<route>&t=<token>). The guard
    # is doctrine: a malformed lock file or hand-constructed URL MUST
    # NOT be able to navigate the user's browser to an external host
    # or smuggle additional query/fragment shapes.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RuntimeUrl)

    $uri = $null
    try { $uri = [System.Uri]$RuntimeUrl } catch {
        throw ('Refusing to open browser: URL is not parseable.')
    }
    $safeForError = Format-RedactedRuntimeUrl -RuntimeUrl $RuntimeUrl
    if (-not $uri.IsAbsoluteUri) {
        throw ('Refusing to open browser: URL is not absolute: ' + $safeForError)
    }
    if ($uri.Scheme -ne 'http') {
        throw ('Refusing to open browser: URL scheme is not http: ' + $safeForError)
    }
    if (-not $uri.IsLoopback) {
        throw ('Refusing to open browser: URL is not loopback: ' + $safeForError)
    }
    if ($uri.Fragment) {
        $frag = $uri.Fragment.TrimStart('#')
        $isTokenOnly  = ($frag -match '^t=[A-Za-z0-9_-]+$')
        $isRouteToken = ($frag -match '^[A-Za-z0-9_/-]+&t=[A-Za-z0-9_-]+$')
        if (-not ($isTokenOnly -or $isRouteToken)) {
            throw 'Refusing to open browser: URL fragment shape is not recognised.'
        }
    }
}


function Open-DefaultBrowserToRuntime {
    # Open the user's default browser at $RuntimeUrl. The URL MUST be
    # loopback http. Anything else throws -- a malformed lock file MUST
    # NOT be able to navigate the user's browser to an external host.
    # When a fragment is present, it must match one of the documented
    # shapes (#t=<token> or #<route>&t=<token>); any other fragment is
    # rejected as a defensive guard against a malformed handoff path.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RuntimeUrl)

    Test-LoopbackHttpRuntimeUrl -RuntimeUrl $RuntimeUrl

    # Start-Process with a URL invokes the OS-registered http URL
    # handler -- i.e. the user's default browser. No browser binary is
    # ever named here.
    [void](Start-Process -FilePath $RuntimeUrl)
}


function Find-EdgeExePath {
    # Locate msedge.exe via the standard discovery order. Returns the
    # first probe whose ProductName matches 'Microsoft Edge*'. Returns
    # $null if no match is found -- callers fall back to the default
    # browser path. Doctrine §11: msedge.exe is the ONLY browser
    # binary the appliance may invoke by name; no other browser is
    # ever discovered here.
    #
    # Probe order:
    #   1. HKLM App Paths     (per-machine native install)
    #   2. HKLM WOW6432Node   (per-machine x86 install)
    #   3. HKCU App Paths     (per-user install)
    #   4. $env:ProgramFiles\Microsoft\Edge\Application\msedge.exe
    #   5. ${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe
    #   6. $env:LOCALAPPDATA\Microsoft\Edge\Application\msedge.exe
    #   7. Get-Command msedge.exe (PATH fallback)
    [CmdletBinding()]
    param()

    $appPathsKeys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe'
    )
    $directProbes = @(
        (Join-Path $env:ProgramFiles            'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path ${env:ProgramFiles(x86)}     'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:LOCALAPPDATA            'Microsoft\Edge\Application\msedge.exe')
    )

    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($keyPath in $appPathsKeys) {
        try {
            if (Test-Path -LiteralPath $keyPath) {
                $val = (Get-ItemProperty -LiteralPath $keyPath -ErrorAction Stop).'(default)'
                if (-not $val) {
                    $val = (Get-Item -LiteralPath $keyPath -ErrorAction Stop).GetValue('')
                }
                if ($val) { [void]$candidates.Add([string]$val) }
            }
        } catch { }
    }
    foreach ($probe in $directProbes) {
        if ($probe) { [void]$candidates.Add($probe) }
    }
    try {
        $cmd = Get-Command -Name 'msedge.exe' -ErrorAction SilentlyContinue
        if ($cmd -and $cmd.Source) { [void]$candidates.Add($cmd.Source) }
    } catch { }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        try {
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
            $info = Get-Item -LiteralPath $candidate -ErrorAction Stop
            $vi   = $info.VersionInfo
            $productName = ''
            if ($vi) { $productName = [string]$vi.ProductName }
            if ($productName -like 'Microsoft Edge*') {
                return $info.FullName
            }
        } catch { continue }
    }
    return $null
}


function Set-EdgeAppWindowShellIdentity {
    # Stamp PKEY_AppUserModel_ID and PKEY_AppUserModel_RelaunchIconResource
    # on the main window of the Edge --app= process so the Windows
    # shell renders the taskbar tile from our .ico (the same code path
    # used for .lnk shortcuts) instead of the bitmap Chromium derives
    # from the page favicon.
    #
    # The stamping work runs in a DETACHED pwsh subprocess (hidden
    # window, no console attachment). Reasons:
    #
    #   1. Crash isolation. The stamping invokes native COM via
    #      SHGetPropertyStoreForWindow; any defect in the P/Invoke
    #      surface (struct layout, lifetime) would otherwise crash
    #      the launcher CLR with an AccessViolationException and kill
    #      the broker (which is the launcher's foreground child).
    #   2. Lifetime independence. The launcher exits shortly after
    #      dispatching the browser; a same-process polling loop would
    #      die before Edge's main window appears.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$Aumid,
        [Parameter(Mandatory)][string]$IconPath
    )

    $childScript = @'
param([int]$ParentPid, [string]$Aumid, [string]$IconPath)

$cs = @"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class PaxShellIdentity
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)]  public ushort vt;
        [FieldOffset(2)]  public ushort wReserved1;
        [FieldOffset(4)]  public ushort wReserved2;
        [FieldOffset(6)]  public ushort wReserved3;
        [FieldOffset(8)]  public IntPtr pointerValue;
        [FieldOffset(16)] public IntPtr unionTail;
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        int Commit();
    }

    [DllImport("shell32.dll")]
    public static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid riid,
        out IPropertyStore propStore);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    public const ushort VT_LPWSTR = 31;

    public static PROPERTYKEY PKEY_AppUserModel_ID = new PROPERTYKEY {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
    public static PROPERTYKEY PKEY_AppUserModel_RelaunchIconResource = new PROPERTYKEY {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 3 };

    public static void Stamp(IntPtr hwnd, string aumid, string iconResource)
    {
        Guid iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out store);
        if (hr != 0 || store == null) return;
        try {
            SetString(store, ref PKEY_AppUserModel_ID, aumid);
            SetString(store, ref PKEY_AppUserModel_RelaunchIconResource, iconResource);
            store.Commit();
        } finally {
            Marshal.ReleaseComObject(store);
        }
    }

    static void SetString(IPropertyStore store, ref PROPERTYKEY key, string value)
    {
        PROPVARIANT pv = new PROPVARIANT();
        pv.vt = VT_LPWSTR;
        pv.pointerValue = Marshal.StringToCoTaskMemUni(value);
        try {
            store.SetValue(ref key, ref pv);
        } finally {
            PropVariantClear(ref pv);
        }
    }
}
"@
try { Add-Type -TypeDefinition $cs -Language CSharp -ErrorAction Stop } catch { return }

$iconResource = $IconPath + ',0'
$deadline = (Get-Date).AddSeconds(30)
$stampedHandles = @{}

while ((Get-Date) -lt $deadline) {
    $candidates = @()
    try { $candidates += Get-Process -Id $ParentPid -ErrorAction Stop } catch { }
    try {
        foreach ($p in (Get-Process -Name 'msedge' -ErrorAction SilentlyContinue)) {
            if ($candidates.Id -notcontains $p.Id) { $candidates += $p }
        }
    } catch { }

    foreach ($p in $candidates) {
        try { $hwnd = $p.MainWindowHandle } catch { continue }
        if ($hwnd -eq [IntPtr]::Zero) { continue }
        $key = [string]$hwnd
        if ($stampedHandles.ContainsKey($key)) { continue }
        try {
            [PaxShellIdentity]::Stamp($hwnd, $Aumid, $iconResource)
            $stampedHandles[$key] = $true
        } catch { }
    }

    if ($stampedHandles.Count -gt 0) {
        Start-Sleep -Milliseconds 750
        foreach ($p in (Get-Process -Name 'msedge' -ErrorAction SilentlyContinue)) {
            try { $hwnd = $p.MainWindowHandle } catch { continue }
            if ($hwnd -eq [IntPtr]::Zero) { continue }
            $key = [string]$hwnd
            if ($stampedHandles.ContainsKey($key)) { continue }
            try {
                [PaxShellIdentity]::Stamp($hwnd, $Aumid, $iconResource)
                $stampedHandles[$key] = $true
            } catch { }
        }
        return
    }

    Start-Sleep -Milliseconds 250
}
'@

    try {
        $scriptPath = Join-Path $env:TEMP ('PAXCookbook-EdgeIdentityStamp-' + [guid]::NewGuid().ToString('N') + '.ps1')
        Set-Content -LiteralPath $scriptPath -Value $childScript -Encoding utf8 -Force

        $pwshExe = (Get-Process -Id $PID).Path
        if (-not $pwshExe -or -not (Test-Path -LiteralPath $pwshExe)) {
            $cmd = Get-Command pwsh.exe -ErrorAction SilentlyContinue
            if ($cmd) { $pwshExe = $cmd.Source }
        }
        if (-not $pwshExe) { return }

        $argList = @(
            '-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden',
            '-ExecutionPolicy', 'Bypass',
            '-File', $scriptPath,
            '-ParentPid', $ProcessId,
            '-Aumid', $Aumid,
            '-IconPath', $IconPath
        )
        $null = Start-Process -FilePath $pwshExe -ArgumentList $argList -WindowStyle Hidden -PassThru -ErrorAction Stop
    } catch {
        # Stamping is best-effort polish; failure leaves Chromium's
        # default favicon on the taskbar but does not affect launch.
    }
}


function Open-EdgeAppModeToRuntime {
    # Launch Microsoft Edge in app-window mode at the given runtime
    # URL. Doctrine §11.4 canonical command line:
    #
    #   msedge.exe --app=<runtimeUrl>
    #              --user-data-dir=%LOCALAPPDATA%\PAXCookbook\EdgeAppData
    #              --profile-directory=Default
    #              --no-first-run
    #              --no-default-browser-check
    #              --disable-sync
    #              --disable-features=msEdgeSignInPromo,msAccountAuthMSAUI,EdgeWelcomePage
    #              --start-maximized
    #
    # The URL is validated by Test-LoopbackHttpRuntimeUrl and is
    # passed ONLY inside the --app= flag. The -ArgumentList argv
    # array form is used so the URL is never re-parsed by a shell.
    #
    # Flag intent (UXR_EDGE_APP_WINDOW_VISUAL_IDENTITY_AND_LAUNCH_POLISH_01):
    #   --disable-sync
    #       Suppresses the "Sign in to sync your data" tab + dialog
    #       that Edge shows on a fresh user-data-dir whenever Windows
    #       has a signed-in MSA available. The Cookbook is a local
    #       appliance; the chef should never be invited to push
    #       Cookbook state into a Microsoft cloud account.
    #   --disable-features=msEdgeSignInPromo,msAccountAuthMSAUI,EdgeWelcomePage
    #       Belt-and-braces suppression of the residual sign-in promo
    #       UI surfaces (the right-rail promo, the MSA auth web UI,
    #       the standalone welcome page) that --disable-sync alone
    #       does not always suppress in newer Edge channels. These
    #       are documented Edge feature flags; they do NOT use
    #       --guest or --inprivate mode (which would break WebAuthn
    #       and discard Cookbook state).
    #   --start-maximized
    #       Opens the app-window maximized on first launch so the
    #       chef does not see a small floating window in a corner.
    #       For subsequent launches Edge restores the previous window
    #       state from its user-data-dir; this flag is harmless there.
    #
    # After Start-Process -PassThru returns, the launcher writes
    # <Workspace>\Runtime\browser.window.json with the redacted URL
    # so a session token never lands on disk.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RuntimeUrl,
        [Parameter(Mandatory)][string]$WorkspacePath
    )

    Test-LoopbackHttpRuntimeUrl -RuntimeUrl $RuntimeUrl

    $edge = Find-EdgeExePath
    if (-not $edge) {
        throw 'Microsoft Edge (msedge.exe) was not found on this machine.'
    }

    $userDataDir = Join-Path $env:LOCALAPPDATA 'PAXCookbook\EdgeAppData'
    if (-not (Test-Path -LiteralPath $userDataDir -PathType Container)) {
        try { $null = New-Item -ItemType Directory -Path $userDataDir -Force } catch { }
    }

    $argList = @(
        ('--app=' + $RuntimeUrl),
        ('--user-data-dir=' + $userDataDir),
        '--profile-directory=Default',
        '--no-first-run',
        '--no-default-browser-check',
        '--disable-sync',
        '--disable-features=msEdgeSignInPromo,msAccountAuthMSAUI,EdgeWelcomePage',
        '--start-maximized'
    )

    $proc = Start-Process -FilePath $edge -ArgumentList $argList -PassThru -ErrorAction Stop

    # Stamp PKEY_AppUserModel_ID and PKEY_AppUserModel_RelaunchIconResource
    # on Edge's main window once it appears. With these two shell
    # properties set, Explorer renders the taskbar tile by reading the
    # .ico DIRECTLY (picking the frame that matches the tile size at
    # the user's DPI), bypassing Chromium's favicon downscale path.
    # The taskbar tile then becomes pixel-identical to the chef-hat
    # tile produced by the Start Menu / Desktop shortcut, which is the
    # whole point: the Edge --app= window must visually match its
    # shortcut peers.
    try {
        $iconPath = Join-Path $PSScriptRoot '..\web\images\pax-cookbook-app-icon.ico'
        $iconPath = [System.IO.Path]::GetFullPath($iconPath)
        if (Test-Path -LiteralPath $iconPath -PathType Leaf) {
            Set-EdgeAppWindowShellIdentity -ProcessId $proc.Id -Aumid 'PAXCookbook.Local.v1' -IconPath $iconPath
        }
    } catch {
        # Identity stamping is best-effort polish; if it fails the
        # Edge window still works, the chef just sees Chromium's
        # downscaled favicon on the taskbar instead of the crisp
        # multi-frame .ico. Do not abort the launch.
    }

    $runtimeDir = Join-Path $WorkspacePath 'Runtime'
    if (-not (Test-Path -LiteralPath $runtimeDir -PathType Container)) {
        try { $null = New-Item -ItemType Directory -Path $runtimeDir -Force } catch { }
    }
    $sidecarPath = Join-Path $runtimeDir 'browser.window.json'
    $redactedUrl = Format-RedactedRuntimeUrl -RuntimeUrl $RuntimeUrl
    $payload = [ordered]@{
        schemaVersion = 1
        browser       = 'msedge'
        mode          = 'app-window'
        processId     = if ($proc) { [int]$proc.Id } else { 0 }
        url           = $redactedUrl
        aumid         = 'PAXCookbook.Local.v1'
        userDataDir   = $userDataDir
        edgeExePath   = $edge
        startedUtc    = (Get-Date).ToUniversalTime().ToString('o')
    }
    try {
        $json = $payload | ConvertTo-Json -Depth 4
        $tmp  = $sidecarPath + '.tmp'
        Set-Content -LiteralPath $tmp -Value $json -Encoding utf8 -NoNewline -Force
        Move-Item -LiteralPath $tmp -Destination $sidecarPath -Force
    } catch {
        # Sidecar is informational; failing to write must not abort
        # the launch.
    }

    return [pscustomobject][ordered]@{
        Browser   = 'msedge'
        Mode      = 'app-window'
        ProcessId = if ($proc) { [int]$proc.Id } else { 0 }
        Url       = $redactedUrl
    }
}


function Open-CookbookUi {
    # Single dispatcher used by every launcher path. Try Edge
    # app-window first; on any failure (Edge not installed, or
    # Start-Process throws) fall back to the OS-registered default
    # browser. Both paths route through Test-LoopbackHttpRuntimeUrl,
    # so a malformed URL is rejected before EITHER browser is
    # touched.
    #
    # Duplicate-launch guard (UXR_EDGE_APP_WINDOW_VISUAL_IDENTITY_AND_LAUNCH_POLISH_01):
    # Before spawning a new Edge app-window, check the workspace's
    # `Runtime\browser.window.json` sidecar. If the recorded process
    # is still alive AND its image is msedge AND it exposes a real
    # main window handle, restore + foreground the existing window
    # via Win32 ShowWindowAsync(SW_RESTORE) + SetForegroundWindow
    # rather than spawning a second instance. The launcher's
    # in-process Invoke-BrowserOpenOnce guard already prevents
    # accidental double-open within a SINGLE launcher run; this
    # cross-launch guard prevents the second Start Menu click from
    # opening a second app-window when the broker is being reused.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RuntimeUrl,
        [Parameter(Mandatory)][string]$WorkspacePath
    )

    Test-LoopbackHttpRuntimeUrl -RuntimeUrl $RuntimeUrl

    # Cross-launch duplicate-window guard. Best-effort: any failure
    # here falls through to a normal Edge spawn so the chef always
    # ends up at the Cookbook UI.
    try {
        $sidecarPath = Join-Path $WorkspacePath 'Runtime\browser.window.json'
        if (Test-Path -LiteralPath $sidecarPath -PathType Leaf) {
            $sidecar = Get-Content -LiteralPath $sidecarPath -Raw | ConvertFrom-Json -ErrorAction Stop
            $sidecarNames = @($sidecar.PSObject.Properties.Name)
            $sidecarPid = 0
            if ($sidecarNames -contains 'processId') { $sidecarPid = [int]$sidecar.processId }
            if ($sidecarPid -gt 0) {
                $existing = Get-Process -Id $sidecarPid -ErrorAction SilentlyContinue
                if ($existing -and -not $existing.HasExited -and ($existing.ProcessName -ieq 'msedge')) {
                    if (-not ('PAXCookbookLauncher.EdgeReveal' -as [type])) {
                        Add-Type -Namespace 'PAXCookbookLauncher' -Name 'EdgeReveal' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool ShowWindowAsync(System.IntPtr hWnd, int nCmdShow);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool BringWindowToTop(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool IsIconic(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool IsWindow(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool IsWindowVisible(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern System.IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint lpdwProcessId);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern uint GetCurrentThreadId();
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct INPUT { public uint type; public InputUnion U; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
public struct InputUnion {
    [System.Runtime.InteropServices.FieldOffset(0)] public KEYBDINPUT ki;
    [System.Runtime.InteropServices.FieldOffset(0)] public MOUSEINPUT mi;
    [System.Runtime.InteropServices.FieldOffset(0)] public HARDWAREINPUT hi;
}
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public System.IntPtr dwExtraInfo; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public System.IntPtr dwExtraInfo; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
public const int SW_RESTORE = 9;
public const int SW_SHOW    = 5;
public const int SW_SHOWMAXIMIZED = 3;
public const uint INPUT_KEYBOARD  = 1;
public const uint KEYEVENTF_KEYUP = 0x0002;
public const ushort VK_MENU       = 0x12;

// Force a window owned by another process to the foreground.
// SetForegroundWindow is rate-limited and ignored unless the calling
// thread owns the current foreground window, holds a fresh user
// input event, or attaches its input queue to the target. We do the
// belt+suspenders combo: send a phantom Alt key to grant ourselves
// a fresh input event, attach input queues, restore + bring-to-top,
// then SetForegroundWindow, then detach.
public static bool ForceForeground(System.IntPtr hWnd, bool restoreIfMinimized) {
    if (hWnd == System.IntPtr.Zero || !IsWindow(hWnd)) { return false; }
    if (restoreIfMinimized && IsIconic(hWnd)) {
        ShowWindow(hWnd, SW_RESTORE);
    }
    // Phantom Alt: tap down + up. Windows treats SetForegroundWindow
    // as user-initiated for the next few ms after a keyboard event.
    INPUT[] alt = new INPUT[2];
    alt[0].type = INPUT_KEYBOARD;
    alt[0].U.ki.wVk = VK_MENU;
    alt[1].type = INPUT_KEYBOARD;
    alt[1].U.ki.wVk = VK_MENU;
    alt[1].U.ki.dwFlags = KEYEVENTF_KEYUP;
    SendInput((uint)alt.Length, alt, System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)));
    System.IntPtr fg = GetForegroundWindow();
    uint fgTid = 0;
    uint dummy = 0;
    if (fg != System.IntPtr.Zero) { fgTid = GetWindowThreadProcessId(fg, out dummy); }
    uint myTid = GetCurrentThreadId();
    uint tgtTid = GetWindowThreadProcessId(hWnd, out dummy);
    bool attachedFg = false; bool attachedTgt = false;
    if (fgTid != 0 && fgTid != myTid) {
        attachedFg = AttachThreadInput(myTid, fgTid, true);
    }
    if (tgtTid != 0 && tgtTid != myTid && tgtTid != fgTid) {
        attachedTgt = AttachThreadInput(myTid, tgtTid, true);
    }
    BringWindowToTop(hWnd);
    bool ok = SetForegroundWindow(hWnd);
    if (attachedFg)  { AttachThreadInput(myTid, fgTid,  false); }
    if (attachedTgt) { AttachThreadInput(myTid, tgtTid, false); }
    return ok;
}
'@
                    }
                    # MainWindowHandle can lag behind the kernel by a
                    # refresh; bounded retry covers that race.
                    $hwnd = [System.IntPtr]::Zero
                    for ($i = 0; $i -lt 3; $i++) {
                        try { $existing.Refresh() } catch { }
                        $hwnd = $existing.MainWindowHandle
                        if ($hwnd -ne [System.IntPtr]::Zero) { break }
                        Start-Sleep -Milliseconds 150
                    }
                    if ($hwnd -ne [System.IntPtr]::Zero `
                            -and [PAXCookbookLauncher.EdgeReveal]::IsWindow($hwnd) `
                            -and [PAXCookbookLauncher.EdgeReveal]::IsWindowVisible($hwnd)) {
                        # ForceForeground does the AttachThreadInput
                        # + phantom-Alt + BringWindowToTop dance that
                        # SetForegroundWindow alone cannot pull off
                        # against a window owned by another process.
                        [void][PAXCookbookLauncher.EdgeReveal]::ForceForeground($hwnd, $true)
                        Write-Host ('  Reusing existing Cookbook Edge app-window (PID ' + $sidecarPid + '); skipping new spawn.') -ForegroundColor Green
                        return [pscustomobject][ordered]@{
                            Browser   = 'msedge'
                            Mode      = 'app-window-reused'
                            ProcessId = $sidecarPid
                            Url       = (Format-RedactedRuntimeUrl -RuntimeUrl $RuntimeUrl)
                        }
                    }
                }
            }
        }
    } catch {
        # Sidecar parse / Win32 failure: fall through to a fresh spawn.
    }

    $edge = Find-EdgeExePath
    if ($edge) {
        try {
            return Open-EdgeAppModeToRuntime -RuntimeUrl $RuntimeUrl -WorkspacePath $WorkspacePath
        } catch {
            Write-Host ('  WARN    Edge app-window launch failed; falling back to default browser: ' + $_.Exception.Message) -ForegroundColor Yellow
        }
    }

    Open-DefaultBrowserToRuntime -RuntimeUrl $RuntimeUrl
    return [pscustomobject][ordered]@{
        Browser   = 'default'
        Mode      = 'shell-open'
        ProcessId = 0
        Url       = (Format-RedactedRuntimeUrl -RuntimeUrl $RuntimeUrl)
    }
}


function Wait-ForBrokerAndOpenBrowser {
    # After the launcher spawns a fresh broker, run this in a
    # background ThreadJob. Polls the workspace lock file for the
    # broker's port and health, then opens the default browser. Bounded
    # by TimeoutSec so the watcher cannot hang indefinitely.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$WorkspacePath,
        [int]$TimeoutSec = 30,
        [int]$PollIntervalMs = 250
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $snap = Read-WorkspaceLockSnapshot -WorkspacePath $WorkspacePath
        if ($snap -and `
            (Test-LiveBrokerTcpPort -Port $snap.BrokerPort) -and `
            (Test-LiveBrokerHealth -Port $snap.BrokerPort -ExpectedWorkspacePath $WorkspacePath)) {

            # Once health is up the broker has already returned from
            # Write-WorkspaceLock, so broker.token should be on disk.
            # Short bounded retry covers a write/visibility race; if
            # the sidecar is still missing the launcher opens the
            # browser tokenless and the operator sees the SPA's own
            # 'No session token in this tab' banner.
            $token = $null
            $tokenDeadline = (Get-Date).AddSeconds(3)
            while ((Get-Date) -lt $tokenDeadline) {
                $token = Get-BrokerSessionToken -WorkspacePath $WorkspacePath
                if ($token) { break }
                Start-Sleep -Milliseconds 100
            }

            $url = Get-RuntimeUrlFromSnapshot -Snapshot $snap -Token $token
            $redacted = Format-RedactedRuntimeUrl -RuntimeUrl $url
            try {
                $opened = Open-CookbookUi -RuntimeUrl $url -WorkspacePath $WorkspacePath
                return [pscustomobject][ordered]@{
                    Opened  = $true
                    Url     = $redacted
                    Port    = [int]$snap.BrokerPort
                    Token   = [bool]$token
                    Browser = $opened.Browser
                    Mode    = $opened.Mode
                    Reason  = $null
                }
            } catch {
                return [pscustomobject][ordered]@{
                    Opened  = $false
                    Url     = $redacted
                    Port    = [int]$snap.BrokerPort
                    Token   = [bool]$token
                    Browser = $null
                    Mode    = $null
                    Reason  = $_.Exception.Message
                }
            }
        }
        Start-Sleep -Milliseconds $PollIntervalMs
    }
    return [pscustomobject][ordered]@{
        Opened  = $false
        Url     = $null
        Port    = 0
        Token   = $false
        Browser = $null
        Mode    = $null
        Reason  = 'timeout'
    }
}


Export-ModuleMember -Function `
    Get-WorkspaceLockPath, `
    Read-WorkspaceLockSnapshot, `
    Test-LiveBrokerProcess, `
    Test-LiveBrokerTcpPort, `
    Test-LiveBrokerHealth, `
    Test-LiveBrokerAvailable, `
    Get-BrokerSessionToken, `
    Format-RedactedRuntimeUrl, `
    Get-RuntimeUrlFromSnapshot, `
    Test-LoopbackHttpRuntimeUrl, `
    Open-DefaultBrowserToRuntime, `
    Find-EdgeExePath, `
    Open-EdgeAppModeToRuntime, `
    Open-CookbookUi, `
    Wait-ForBrokerAndOpenBrowser
