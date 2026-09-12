using System;
using System.IO;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Provider;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Deterministic tests for the pre-unlock provider repair service. No app unlock,
// no MSAL, no live tenant.
public sealed class ProviderRepairServiceTests
{
    private sealed class FakeHello : IHelloSupportProbe
    {
        private readonly bool _a;
        public FakeHello(bool a) => _a = a;
        public bool IsWindowsHelloAvailable() => _a;
    }

    private sealed class FakeGate : IWorkAccountSetupGate
    {
        private readonly bool _ready, _test;
        public FakeGate(bool ready, bool test) { _ready = ready; _test = test; }
        public bool IsWorkAccountReady() => _ready;
        public bool RunNativeWamAuthTest() => _test;
    }

    private static string NewBase()
    {
        string p = Path.Combine(Path.GetTempPath(), "paxrepair_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static void WriteWamConfig(string baseDir, bool verified)
    {
        string dir = Path.Combine(baseDir, "PAXCookbook", "Config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "experimental-wam.json"), "{ \"schemaVersion\": 2 }");
        File.WriteAllText(Path.Combine(dir, "experimental-wam-verification.json"),
            verified ? "{ \"outcome\": \"verified\" }" : "{ \"outcome\": \"none\" }");
    }

    [Fact]
    public void ReadStatus_ReflectsSelectionAndHealth()
    {
        string b = NewBase();
        try
        {
            SessionProviderStore.Save(b, SelectedSessionProvider.WorkAccount, ProviderSelectionSource.Setup);
            WriteWamConfig(b, verified: true);
            var svc = new ProviderRepairService(b, new FakeHello(true));
            ProviderRepairStatus s = svc.ReadStatus();
            Assert.Equal(SelectedSessionProvider.WorkAccount, s.Selection.Provider);
            Assert.True(s.HelloAvailable);
            Assert.True(s.WorkAccountConfigPresent);
            Assert.True(s.WorkAccountVerified);
        }
        finally { Directory.Delete(b, recursive: true); }
    }

    [Fact]
    public void SwitchToHello_WritesRepairSourcedSelection()
    {
        string b = NewBase();
        try
        {
            SessionProviderStore.Save(b, SelectedSessionProvider.WorkAccount, ProviderSelectionSource.Setup);
            var svc = new ProviderRepairService(b, new FakeHello(true));
            ProviderSetupResult r = svc.SwitchToWindowsHello();
            Assert.True(r.Succeeded);
            SessionProviderSelection sel = SessionProviderStore.Load(b);
            Assert.Equal(SelectedSessionProvider.WindowsHello, sel.Provider);
            Assert.Equal(ProviderSelectionSource.SetupRepair, sel.Source);
        }
        finally { Directory.Delete(b, recursive: true); }
    }

    [Fact]
    public void SwitchToHello_Unavailable_PreservesSelection()
    {
        string b = NewBase();
        try
        {
            SessionProviderStore.Save(b, SelectedSessionProvider.WorkAccount, ProviderSelectionSource.Setup);
            var svc = new ProviderRepairService(b, new FakeHello(false));
            ProviderSetupResult r = svc.SwitchToWindowsHello();
            Assert.Equal(ProviderSetupStatus.HelloUnavailable, r.Status);
            Assert.Equal(SelectedSessionProvider.WorkAccount, SessionProviderStore.Load(b).Provider);
        }
        finally { Directory.Delete(b, recursive: true); }
    }

    [Fact]
    public void SwitchToWork_TestFails_PreservesSelection()
    {
        string b = NewBase();
        try
        {
            SessionProviderStore.Save(b, SelectedSessionProvider.WindowsHello, ProviderSelectionSource.Setup);
            var svc = new ProviderRepairService(b, new FakeHello(true));
            ProviderSetupResult r = svc.SwitchToWorkAccount(new FakeGate(ready: true, test: false));
            Assert.Equal(ProviderSetupStatus.NativeTestFailed, r.Status);
            Assert.Equal(SelectedSessionProvider.WindowsHello, SessionProviderStore.Load(b).Provider);
        }
        finally { Directory.Delete(b, recursive: true); }
    }

    [Fact]
    public void SwitchToWork_ReadyAndTestPasses_WritesRepairSourced()
    {
        string b = NewBase();
        try
        {
            var svc = new ProviderRepairService(b, new FakeHello(true));
            ProviderSetupResult r = svc.SwitchToWorkAccount(new FakeGate(ready: true, test: true));
            Assert.True(r.Succeeded);
            SessionProviderSelection sel = SessionProviderStore.Load(b);
            Assert.Equal(SelectedSessionProvider.WorkAccount, sel.Provider);
            Assert.Equal(ProviderSelectionSource.SetupRepair, sel.Source);
        }
        finally { Directory.Delete(b, recursive: true); }
    }

    [Fact]
    public void RemoveLocalWorkConfig_DeletesFiles_PreservesSelection()
    {
        string b = NewBase();
        try
        {
            SessionProviderStore.Save(b, SelectedSessionProvider.WorkAccount, ProviderSelectionSource.Setup);
            WriteWamConfig(b, verified: true);
            var svc = new ProviderRepairService(b, new FakeHello(true));
            Assert.True(svc.RemoveLocalWorkConfig());
            string dir = Path.Combine(b, "PAXCookbook", "Config");
            Assert.False(File.Exists(Path.Combine(dir, "experimental-wam.json")));
            Assert.False(File.Exists(Path.Combine(dir, "experimental-wam-verification.json")));
            // Selection record (user's chosen provider) is untouched.
            Assert.Equal(SelectedSessionProvider.WorkAccount, SessionProviderStore.Load(b).Provider);
        }
        finally { Directory.Delete(b, recursive: true); }
    }

    [Fact]
    public void AvailableActions_StableChannel_NoWorkAccountActions()
    {
        string b = NewBase();
        try
        {
            SessionProviderStore.Save(b, SelectedSessionProvider.WindowsHello, ProviderSelectionSource.Setup);
            WriteWamConfig(b, verified: false);
            var svc = new ProviderRepairService(b, new FakeHello(true));
            var actions = svc.AvailableActions(workAccountChannelEnabled: false, tenantAdminAuthorized: true);
            Assert.DoesNotContain(ProviderRepairAction.SwitchToWorkAccount, actions);
            Assert.DoesNotContain(ProviderRepairAction.RemoveBrokenWorkAccountConfig, actions);
        }
        finally { Directory.Delete(b, recursive: true); }
    }

    [Fact]
    public void AvailableActions_MalformedSelection_OffersRecoverySwitches()
    {
        string b = NewBase();
        try
        {
            string dir = Path.Combine(b, "PAXCookbook", "Config");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "session-provider.json"), "{ broken");
            var svc = new ProviderRepairService(b, new FakeHello(true));
            var actions = svc.AvailableActions(workAccountChannelEnabled: true, tenantAdminAuthorized: false);
            Assert.Contains(ProviderRepairAction.SwitchToWindowsHello, actions);
            Assert.Contains(ProviderRepairAction.SwitchToWorkAccount, actions);
        }
        finally { Directory.Delete(b, recursive: true); }
    }
}
