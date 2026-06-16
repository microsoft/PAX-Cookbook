using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Verifies the ShellLeakGuard defense-in-depth helper used by
// HandoffEndToEndTests (Phase3) and InstalledSetupSelfHandoffE2ETests
// (Phase5). The guard's contract:
//   - On construction, snapshot the existence of the PAX-owned HKCU
//     ARP key, paxcookbook protocol key, and Start Menu group.
//   - On Dispose, remove any of those three surfaces that did NOT
//     exist at snapshot time and DO exist now. Surfaces that DID
//     exist at snapshot time are preserved.
//
// These tests use unique per-test sentinel names so they cannot
// collide with a real installed PAXCookbook product on the host.
// They never touch the production `PAXCookbook` ARP key,
// `paxcookbook` protocol key, or `PAX Cookbook` Start Menu group.

public class ShellLeakGuardTests
{
    private const string ArpParent = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ProtoParent = @"Software\Classes";

    private static string StartMenuPrograms() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs");

    private static (string arp, string proto, string startMenu) NewSentinelNames()
    {
        var tag = Guid.NewGuid().ToString("N");
        return (
            "PAXCookbookLeakGuardSentinel_" + tag,
            "paxcookbookleakguardsentinel" + tag,
            "PAX Cookbook LeakGuardSentinel " + tag);
    }

    private static void WriteArp(string subKey)
    {
        using var parent = Registry.CurrentUser.OpenSubKey(ArpParent, writable: true)
            ?? throw new InvalidOperationException("HKCU Uninstall parent missing");
        using var key = parent.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException("failed to create ARP sentinel");
        key.SetValue("DisplayName", "PAX Cookbook Sentinel " + subKey);
        key.SetValue("DisplayVersion", "0.0.0-sentinel");
    }

    private static void WriteProto(string subKey)
    {
        using var parent = Registry.CurrentUser.OpenSubKey(ProtoParent, writable: true)
            ?? throw new InvalidOperationException("HKCU Classes parent missing");
        using var key = parent.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException("failed to create proto sentinel");
        key.SetValue("", "URL:PAX Cookbook Sentinel " + subKey);
        key.SetValue("URL Protocol", "");
    }

    private static void WriteStartMenu(string group)
    {
        var path = Path.Combine(StartMenuPrograms(), group);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "sentinel.txt"), group);
    }

    private static bool ArpExists(string subKey)
    {
        using var parent = Registry.CurrentUser.OpenSubKey(ArpParent, writable: false);
        if (parent is null) return false;
        using var key = parent.OpenSubKey(subKey, writable: false);
        return key is not null;
    }

    private static bool ProtoExists(string subKey)
    {
        using var parent = Registry.CurrentUser.OpenSubKey(ProtoParent, writable: false);
        if (parent is null) return false;
        using var key = parent.OpenSubKey(subKey, writable: false);
        return key is not null;
    }

    private static bool StartMenuExists(string group)
        => Directory.Exists(Path.Combine(StartMenuPrograms(), group));

    private static void TryDeleteArp(string subKey)
    {
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(ArpParent, writable: true);
            parent?.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static void TryDeleteProto(string subKey)
    {
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(ProtoParent, writable: true);
            parent?.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static void TryDeleteStartMenu(string group)
    {
        try
        {
            var path = Path.Combine(StartMenuPrograms(), group);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void Snapshot_WithSentinelNames_RemovesNewSentinelArp_OnDispose()
    {
        var (arp, proto, sm) = NewSentinelNames();
        try
        {
            Assert.False(ArpExists(arp));
            using (var guard = ShellLeakGuard.SnapshotWithNames(arp, proto, sm))
            {
                Assert.False(guard.ArpExistedAtSnapshot);
                WriteArp(arp);
                Assert.True(ArpExists(arp));
            }
            Assert.False(ArpExists(arp));
        }
        finally
        {
            TryDeleteArp(arp);
        }
    }

    [Fact]
    public void Snapshot_WithSentinelNames_RemovesNewSentinelProto_OnDispose()
    {
        var (arp, proto, sm) = NewSentinelNames();
        try
        {
            Assert.False(ProtoExists(proto));
            using (var guard = ShellLeakGuard.SnapshotWithNames(arp, proto, sm))
            {
                Assert.False(guard.ProtoExistedAtSnapshot);
                WriteProto(proto);
                Assert.True(ProtoExists(proto));
            }
            Assert.False(ProtoExists(proto));
        }
        finally
        {
            TryDeleteProto(proto);
        }
    }

    [Fact]
    public void Snapshot_WithSentinelNames_RemovesNewSentinelStartMenuGroup_OnDispose()
    {
        var (arp, proto, sm) = NewSentinelNames();
        try
        {
            Assert.False(StartMenuExists(sm));
            using (var guard = ShellLeakGuard.SnapshotWithNames(arp, proto, sm))
            {
                Assert.False(guard.StartMenuExistedAtSnapshot);
                WriteStartMenu(sm);
                Assert.True(StartMenuExists(sm));
            }
            Assert.False(StartMenuExists(sm));
        }
        finally
        {
            TryDeleteStartMenu(sm);
        }
    }

    [Fact]
    public void Snapshot_WithSentinelNames_PreservesPreExistingArp_OnDispose()
    {
        var (arp, proto, sm) = NewSentinelNames();
        try
        {
            WriteArp(arp);
            Assert.True(ArpExists(arp));
            using (var guard = ShellLeakGuard.SnapshotWithNames(arp, proto, sm))
            {
                Assert.True(guard.ArpExistedAtSnapshot);
                // No mutation inside the using block.
            }
            // The pre-existing key must be preserved.
            Assert.True(ArpExists(arp));
        }
        finally
        {
            TryDeleteArp(arp);
        }
    }

    [Fact]
    public void Snapshot_WithSentinelNames_PreservesPreExistingProto_OnDispose()
    {
        var (arp, proto, sm) = NewSentinelNames();
        try
        {
            WriteProto(proto);
            Assert.True(ProtoExists(proto));
            using (var guard = ShellLeakGuard.SnapshotWithNames(arp, proto, sm))
            {
                Assert.True(guard.ProtoExistedAtSnapshot);
            }
            Assert.True(ProtoExists(proto));
        }
        finally
        {
            TryDeleteProto(proto);
        }
    }

    [Fact]
    public void Snapshot_WithSentinelNames_PreservesPreExistingStartMenuGroup_OnDispose()
    {
        var (arp, proto, sm) = NewSentinelNames();
        try
        {
            WriteStartMenu(sm);
            Assert.True(StartMenuExists(sm));
            using (var guard = ShellLeakGuard.SnapshotWithNames(arp, proto, sm))
            {
                Assert.True(guard.StartMenuExistedAtSnapshot);
            }
            Assert.True(StartMenuExists(sm));
        }
        finally
        {
            TryDeleteStartMenu(sm);
        }
    }

    [Fact]
    public void Snapshot_DefaultNames_NoChange_DoesNotMutateRealPaxState()
    {
        // Defensive: the production-named guard must be safe to wrap
        // around any test, regardless of whether the host has a real
        // PAXCookbook install. This test does NO mutation inside the
        // using block; the guard must be a no-op.
        bool arpBefore;
        bool protoBefore;
        bool startMenuBefore;
        using (var probe = Registry.CurrentUser.OpenSubKey(
            $@"{ArpParent}\{ShellLeakGuard.DefaultArpSubKey}", writable: false))
        {
            arpBefore = probe is not null;
        }
        using (var probe = Registry.CurrentUser.OpenSubKey(
            $@"{ProtoParent}\{ShellLeakGuard.DefaultProtoSubKey}", writable: false))
        {
            protoBefore = probe is not null;
        }
        startMenuBefore = Directory.Exists(Path.Combine(
            StartMenuPrograms(), ShellLeakGuard.DefaultStartMenuGroup));

        using (var _ = ShellLeakGuard.Snapshot()) { }

        bool arpAfter;
        bool protoAfter;
        bool startMenuAfter;
        using (var probe = Registry.CurrentUser.OpenSubKey(
            $@"{ArpParent}\{ShellLeakGuard.DefaultArpSubKey}", writable: false))
        {
            arpAfter = probe is not null;
        }
        using (var probe = Registry.CurrentUser.OpenSubKey(
            $@"{ProtoParent}\{ShellLeakGuard.DefaultProtoSubKey}", writable: false))
        {
            protoAfter = probe is not null;
        }
        startMenuAfter = Directory.Exists(Path.Combine(
            StartMenuPrograms(), ShellLeakGuard.DefaultStartMenuGroup));

        Assert.Equal(arpBefore, arpAfter);
        Assert.Equal(protoBefore, protoAfter);
        Assert.Equal(startMenuBefore, startMenuAfter);
    }

    [Fact]
    public void PaxcookbookTestNoShell_PsiEnvironment_PropagatesToChildProcess()
    {
        // Verifies the env-var propagation primitive used by the
        // hardened HandoffEndToEndTests + InstalledSetupSelfHandoffE2E
        // tests. ProcessStartInfo.Environment is a copy-of-parent
        // dictionary; setting a single key adds it without clearing
        // PATH, COMSPEC, etc. The child process inherits the
        // modified block.
        var comspec = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrEmpty(comspec) || !File.Exists(comspec))
        {
            return;
        }
        var psi = new ProcessStartInfo
        {
            FileName = comspec,
            Arguments = "/c echo %PAXCOOKBOOK_TEST_NO_SHELL%",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["PAXCOOKBOOK_TEST_NO_SHELL"] = "1";
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start child");
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        Assert.Equal(0, p.ExitCode);
        Assert.Equal("1", stdout.Trim());
    }
}
