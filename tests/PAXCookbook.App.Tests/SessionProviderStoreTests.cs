using System;
using System.IO;
using PAXCookbook.App;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// Mutually-exclusive selected session-provider store + runtime. Deterministic;
// no live WAM/tenant/MSAL dependency. Proves: missing selection migrates to
// Windows Hello (not an error); malformed/unknown/wrong-schema selections fail
// closed to recovery (never a silent default); round-trips survive a fresh
// runtime (restart); and the persisted file carries NO tenant/client identifier.
public sealed class SessionProviderStoreTests
{
    private static string NewTempBase()
    {
        string path = Path.Combine(Path.GetTempPath(), "paxsp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Load_NoFile_MigratesToWindowsHello_NotRecovery()
    {
        string baseDir = NewTempBase();
        try
        {
            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.Equal(SelectedSessionProvider.WindowsHello, sel.Provider);
            Assert.False(sel.RecoveryRequired);
            Assert.True(sel.Migrated);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void SaveWorkAccount_ThenLoad_RoundTrips()
    {
        string baseDir = NewTempBase();
        try
        {
            SessionProviderStore.Save(baseDir, SelectedSessionProvider.WorkAccount, ProviderSelectionSource.Setup);
            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.Equal(SelectedSessionProvider.WorkAccount, sel.Provider);
            Assert.False(sel.RecoveryRequired);
            Assert.False(sel.Migrated);
            Assert.Equal(ProviderSelectionSource.Setup, sel.Source);
            Assert.NotNull(sel.RecordedUtc);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void SaveWindowsHello_ThenLoad_RoundTrips()
    {
        string baseDir = NewTempBase();
        try
        {
            SessionProviderStore.Save(baseDir, SelectedSessionProvider.WindowsHello, ProviderSelectionSource.Settings);
            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.Equal(SelectedSessionProvider.WindowsHello, sel.Provider);
            Assert.False(sel.RecoveryRequired);
            Assert.False(sel.Migrated);
            Assert.Equal(ProviderSelectionSource.Settings, sel.Source);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Load_MalformedJson_FailsClosedToRecovery()
    {
        string baseDir = NewTempBase();
        try
        {
            string path = SessionProviderStore.ResolveSelectionPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.True(sel.RecoveryRequired);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Load_UnknownProviderValue_FailsClosedToRecovery()
    {
        string baseDir = NewTempBase();
        try
        {
            string path = SessionProviderStore.ResolveSelectionPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ \"schemaVersion\": 1, \"selectedProvider\": \"totp\" }");
            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.True(sel.RecoveryRequired);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Load_WrongSchema_FailsClosedToRecovery()
    {
        string baseDir = NewTempBase();
        try
        {
            string path = SessionProviderStore.ResolveSelectionPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ \"schemaVersion\": 99, \"selectedProvider\": \"work_account\" }");
            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.True(sel.RecoveryRequired);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Load_EmptyFile_FailsClosedToRecovery()
    {
        string baseDir = NewTempBase();
        try
        {
            string path = SessionProviderStore.ResolveSelectionPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "   ");
            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.True(sel.RecoveryRequired);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Load_MissingSourceOrTimestamp_FailsClosedToRecovery()
    {
        string baseDir = NewTempBase();
        try
        {
            string path = SessionProviderStore.ResolveSelectionPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Valid schema + provider but missing the required source/timestamp.
            File.WriteAllText(path, "{ \"schemaVersion\": 1, \"selectedProvider\": \"work_account\" }");
            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.True(sel.RecoveryRequired);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Load_UnknownSource_FailsClosedToRecovery()
    {
        string baseDir = NewTempBase();
        try
        {
            string path = SessionProviderStore.ResolveSelectionPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                "{ \"schemaVersion\": 1, \"selectedProvider\": \"work_account\", \"source\": \"hacker\", \"recordedUtc\": \"2026-07-20T00:00:00Z\" }");
            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.True(sel.RecoveryRequired);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void PersistedFile_ContainsNoTenantOrClientIdentifier()
    {
        string baseDir = NewTempBase();
        try
        {
            SessionProviderStore.Save(baseDir, SelectedSessionProvider.WorkAccount, ProviderSelectionSource.SetupRepair);
            string raw = File.ReadAllText(SessionProviderStore.ResolveSelectionPath(baseDir));
            Assert.DoesNotContain("tenant", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("client", raw, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("selectedProvider", raw, StringComparison.Ordinal);
            Assert.Contains("work_account", raw, StringComparison.Ordinal);
            Assert.Contains("setup_repair", raw, StringComparison.Ordinal);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void ToWire_MapsBothProviders()
    {
        Assert.Equal("windows_hello", SessionProviderStore.ToWire(SelectedSessionProvider.WindowsHello));
        Assert.Equal("work_account", SessionProviderStore.ToWire(SelectedSessionProvider.WorkAccount));
    }

    [Fact]
    public void Runtime_Select_Persists_AcrossFreshInstance()
    {
        string baseDir = NewTempBase();
        try
        {
            var runtime = new SessionProviderRuntime(baseDir);
            Assert.True(runtime.GetSelection().Migrated); // default before any explicit select

            runtime.Select(SelectedSessionProvider.WorkAccount);
            Assert.Equal(SelectedSessionProvider.WorkAccount, runtime.GetSelection().Provider);

            // A fresh runtime (restart) reads the same durable selection.
            var restarted = new SessionProviderRuntime(baseDir);
            SessionProviderSelection sel = restarted.GetSelection();
            Assert.Equal(SelectedSessionProvider.WorkAccount, sel.Provider);
            Assert.False(sel.RecoveryRequired);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Runtime_Reselect_OverwritesAtomically()
    {
        string baseDir = NewTempBase();
        try
        {
            var runtime = new SessionProviderRuntime(baseDir);
            runtime.Select(SelectedSessionProvider.WorkAccount);
            runtime.Select(SelectedSessionProvider.WindowsHello);
            Assert.Equal(SelectedSessionProvider.WindowsHello, runtime.GetSelection().Provider);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }
}
