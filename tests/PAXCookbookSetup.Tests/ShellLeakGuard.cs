using System;
using System.IO;
using Microsoft.Win32;

namespace PAXCookbookSetup.Tests;

// Defense-in-depth safety net for Setup.Tests E2E tests that spawn the
// real PAXCookbookSetup.exe as a child process. Snapshots the
// pre-existing state of the PAX-owned HKCU ARP key, paxcookbook
// protocol key, and Start Menu group at construction time. On Dispose
// (including the failed-assertion path via `using var`), removes any
// of those three surfaces that did NOT exist at snapshot time. Surfaces
// that DID exist at snapshot time are never touched.
//
// Scope is explicit, narrow, and PAX-only:
//   - HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\<arpSubKey>
//   - HKCU:\Software\Classes\<protoSubKey>
//   - %APPDATA%\Microsoft\Windows\Start Menu\Programs\<startMenuGroup>
//
// Out of scope (never touched):
//   - HKLM
//   - taskbar pins
//   - %LOCALAPPDATA%\PAXCookbook\Workspace
//   - non-PAX HKCU keys
//   - external folders
//
// Primary defense remains the PAXCOOKBOOK_TEST_NO_SHELL=1 env var on the
// child process: when set, the installed Setup uses NoOpRegistryWriter
// + NoOpShortcutWriter from production code (see TestShellGate). The
// leak guard is a second line of defense in case the env var is missed
// or a future code path bypasses TestShellGate.
public sealed class ShellLeakGuard : IDisposable
{
    public const string DefaultArpSubKey = "PAXCookbook";
    public const string DefaultProtoSubKey = "paxcookbook";
    public const string DefaultStartMenuGroup = "PAX Cookbook";

    private const string ArpParentPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ProtoParentPath = @"Software\Classes";

    private readonly string _arpSubKey;
    private readonly string _protoSubKey;
    private readonly string _startMenuGroup;
    private readonly bool _arpExistedAtSnapshot;
    private readonly bool _protoExistedAtSnapshot;
    private readonly bool _startMenuExistedAtSnapshot;
    private bool _disposed;

    private ShellLeakGuard(string arpSubKey, string protoSubKey, string startMenuGroup)
    {
        _arpSubKey = arpSubKey;
        _protoSubKey = protoSubKey;
        _startMenuGroup = startMenuGroup;
        _arpExistedAtSnapshot = ArpKeyExists();
        _protoExistedAtSnapshot = ProtoKeyExists();
        _startMenuExistedAtSnapshot = StartMenuGroupExists();
    }

    // Snapshot using the production PAX names. Use in E2E tests that
    // exercise the real installed Setup.
    public static ShellLeakGuard Snapshot() =>
        new ShellLeakGuard(DefaultArpSubKey, DefaultProtoSubKey, DefaultStartMenuGroup);

    // Snapshot using caller-supplied unique sentinel names. Use in
    // unit-tests-of-this-guard so we never collide with a real PAX
    // install on the host.
    public static ShellLeakGuard SnapshotWithNames(
        string arpSubKey, string protoSubKey, string startMenuGroup) =>
        new ShellLeakGuard(arpSubKey, protoSubKey, startMenuGroup);

    public bool ArpExistedAtSnapshot => _arpExistedAtSnapshot;
    public bool ProtoExistedAtSnapshot => _protoExistedAtSnapshot;
    public bool StartMenuExistedAtSnapshot => _startMenuExistedAtSnapshot;

    public bool ArpExistsNow() => ArpKeyExists();
    public bool ProtoExistsNow() => ProtoKeyExists();
    public bool StartMenuExistsNow() => StartMenuGroupExists();

    public string ArpFullPath => $@"HKCU\{ArpParentPath}\{_arpSubKey}";
    public string ProtoFullPath => $@"HKCU\{ProtoParentPath}\{_protoSubKey}";
    public string StartMenuGroupFullPath => StartMenuGroupPath();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // For each of the three surfaces, only remove if (a) it did NOT
        // exist at snapshot time and (b) it DOES exist now. This way the
        // guard cannot disturb pre-existing real PAX state on the host.
        if (!_arpExistedAtSnapshot && ArpKeyExists())
        {
            TryDeleteArpKey();
        }
        if (!_protoExistedAtSnapshot && ProtoKeyExists())
        {
            TryDeleteProtoKey();
        }
        if (!_startMenuExistedAtSnapshot && StartMenuGroupExists())
        {
            TryDeleteStartMenuGroup();
        }
    }

    private bool ArpKeyExists()
    {
        using var parent = Registry.CurrentUser.OpenSubKey(ArpParentPath, writable: false);
        if (parent is null) return false;
        using var key = parent.OpenSubKey(_arpSubKey, writable: false);
        return key is not null;
    }

    private bool ProtoKeyExists()
    {
        using var parent = Registry.CurrentUser.OpenSubKey(ProtoParentPath, writable: false);
        if (parent is null) return false;
        using var key = parent.OpenSubKey(_protoSubKey, writable: false);
        return key is not null;
    }

    private bool StartMenuGroupExists() => Directory.Exists(StartMenuGroupPath());

    private string StartMenuGroupPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs",
        _startMenuGroup);

    private void TryDeleteArpKey()
    {
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(
                ArpParentPath, writable: true);
            parent?.DeleteSubKeyTree(_arpSubKey, throwOnMissingSubKey: false);
        }
        catch
        {
            // Best-effort: a registry permission failure must not mask
            // the original assertion. Re-running the test (or the
            // sibling proof test) will surface a residual leak.
        }
    }

    private void TryDeleteProtoKey()
    {
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(
                ProtoParentPath, writable: true);
            parent?.DeleteSubKeyTree(_protoSubKey, throwOnMissingSubKey: false);
        }
        catch
        {
        }
    }

    private void TryDeleteStartMenuGroup()
    {
        try
        {
            Directory.Delete(StartMenuGroupPath(), recursive: true);
        }
        catch
        {
        }
    }
}
