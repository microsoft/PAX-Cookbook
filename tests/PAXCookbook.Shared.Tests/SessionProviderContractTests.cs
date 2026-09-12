using System;
using System.IO;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// Cross-component provider-selection contract. This is the SINGLE source of
// truth compiled into both PAXCookbook.Shared (Setup) and, via a linked source
// reference, into the native host (PAXCookbook.App). These tests exercise the
// contract from the Shared side; because the app links the identical file, a
// record written here is byte-for-byte the record the app reads (and vice
// versa) — that is the App/Setup interoperability guarantee.
public sealed class SessionProviderContractTests
{
    private static string NewTempBase()
    {
        string path = Path.Combine(Path.GetTempPath(), "paxspc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Path_IsTheExactSharedLayout()
    {
        string baseDir = Path.Combine("X", "base");
        string path = SessionProviderStore.ResolveSelectionPath(baseDir);
        Assert.Equal(Path.Combine(baseDir, "PAXCookbook", "Config", "session-provider.json"), path);
    }

    [Fact]
    public void MissingRecord_MigratesToWindowsHello()
    {
        string baseDir = NewTempBase();
        try
        {
            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.Equal(SelectedSessionProvider.WindowsHello, sel.Provider);
            Assert.True(sel.Migrated);
            Assert.False(sel.RecoveryRequired);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Theory]
    [InlineData(ProviderSelectionSource.Setup)]
    [InlineData(ProviderSelectionSource.Settings)]
    [InlineData(ProviderSelectionSource.SetupRepair)]
    public void SetupWrite_IsReadableWithSourceAndTimestamp(ProviderSelectionSource source)
    {
        string baseDir = NewTempBase();
        try
        {
            var when = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
            SessionProviderStore.Save(baseDir, SelectedSessionProvider.WorkAccount, source, when);

            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.Equal(SelectedSessionProvider.WorkAccount, sel.Provider);
            Assert.Equal(source, sel.Source);
            Assert.False(sel.RecoveryRequired);
            Assert.Equal(when, sel.RecordedUtc);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void SelectionSurvivesRewrite_LastWriteWins()
    {
        string baseDir = NewTempBase();
        try
        {
            SessionProviderStore.Save(baseDir, SelectedSessionProvider.WorkAccount, ProviderSelectionSource.Setup);
            SessionProviderStore.Save(baseDir, SelectedSessionProvider.WindowsHello, ProviderSelectionSource.SetupRepair);
            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.Equal(SelectedSessionProvider.WindowsHello, sel.Provider);
            Assert.Equal(ProviderSelectionSource.SetupRepair, sel.Source);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void MalformedOrUnknown_FailsClosedToRecovery()
    {
        string baseDir = NewTempBase();
        try
        {
            string path = SessionProviderStore.ResolveSelectionPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            File.WriteAllText(path, "{ broken");
            Assert.True(SessionProviderStore.Load(baseDir).RecoveryRequired);

            File.WriteAllText(path, "{ \"schemaVersion\": 2, \"selectedProvider\": \"work_account\", \"source\": \"setup\", \"recordedUtc\": \"2026-07-20T00:00:00Z\" }");
            Assert.True(SessionProviderStore.Load(baseDir).RecoveryRequired);

            File.WriteAllText(path, "{ \"schemaVersion\": 1, \"selectedProvider\": \"palm_scan\", \"source\": \"setup\", \"recordedUtc\": \"2026-07-20T00:00:00Z\" }");
            Assert.True(SessionProviderStore.Load(baseDir).RecoveryRequired);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Delete_RemovesRecord()
    {
        string baseDir = NewTempBase();
        try
        {
            SessionProviderStore.Save(baseDir, SelectedSessionProvider.WorkAccount, ProviderSelectionSource.Setup);
            Assert.True(File.Exists(SessionProviderStore.ResolveSelectionPath(baseDir)));
            SessionProviderStore.Delete(baseDir);
            Assert.False(File.Exists(SessionProviderStore.ResolveSelectionPath(baseDir)));
            // After removal, a fresh read migrates to Windows Hello (no error).
            Assert.True(SessionProviderStore.Load(baseDir).Migrated);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void WireValues_AreTheClosedContract()
    {
        Assert.Equal("windows_hello", SessionProviderStore.WindowsHelloId);
        Assert.Equal("work_account", SessionProviderStore.WorkAccountId);
        Assert.Equal("windows_hello", SessionProviderStore.ToWire(SelectedSessionProvider.WindowsHello));
        Assert.Equal("work_account", SessionProviderStore.ToWire(SelectedSessionProvider.WorkAccount));
        Assert.Equal("setup", SessionProviderStore.SourceToWire(ProviderSelectionSource.Setup));
        Assert.Equal("settings", SessionProviderStore.SourceToWire(ProviderSelectionSource.Settings));
        Assert.Equal("setup_repair", SessionProviderStore.SourceToWire(ProviderSelectionSource.SetupRepair));
        Assert.Equal("migration", SessionProviderStore.SourceToWire(ProviderSelectionSource.Migration));
    }

    [Fact]
    public void PersistedRecord_HasNoIdentifiers()
    {
        string baseDir = NewTempBase();
        try
        {
            SessionProviderStore.Save(baseDir, SelectedSessionProvider.WorkAccount, ProviderSelectionSource.Setup);
            string raw = File.ReadAllText(SessionProviderStore.ResolveSelectionPath(baseDir));
            Assert.DoesNotContain("tenant", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("client", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }
}
