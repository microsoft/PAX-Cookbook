using System;
using System.IO;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup;
using PAXCookbookSetup.Provider;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Setup-side provider selection, transactional writes, pre-unlock repair
// planning, lifecycle policy, and the app-delegated work-account gate.
// Deterministic; no MSAL, no live tenant, no real process spawn.
public sealed class ProviderSetupTests
{
    private static string NewTempBase()
    {
        string path = Path.Combine(Path.GetTempPath(), "paxsetup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeHello : IHelloSupportProbe
    {
        private readonly bool _available;
        public FakeHello(bool available) => _available = available;
        public bool IsWindowsHelloAvailable() => _available;
    }

    private sealed class FakeWorkGate : IWorkAccountSetupGate
    {
        private readonly bool _ready;
        private readonly bool _testResult;
        public int ReadyCalls { get; private set; }
        public int TestCalls { get; private set; }
        public FakeWorkGate(bool ready, bool testResult)
        {
            _ready = ready;
            _testResult = testResult;
        }
        public bool IsWorkAccountReady() { ReadyCalls++; return _ready; }
        public bool RunNativeWamAuthTest() { TestCalls++; return _testResult; }
    }

    // ---- ProviderSelectionWriter (transactional) -------------------------

    [Fact]
    public void Writer_RestoreAfterWrite_ReturnsToAbsence()
    {
        string baseDir = NewTempBase();
        try
        {
            var w = new ProviderSelectionWriter(baseDir);
            w.Snapshot(); // no prior record
            w.WriteSelectionLast(SelectedSessionProvider.WorkAccount, ProviderSelectionSource.Setup);
            Assert.True(File.Exists(w.SelectionPath));
            w.Restore();
            Assert.False(File.Exists(w.SelectionPath));
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Writer_RestoreAfterWrite_ReturnsPriorRecordExactly()
    {
        string baseDir = NewTempBase();
        try
        {
            SessionProviderStore.Save(baseDir, SelectedSessionProvider.WindowsHello, ProviderSelectionSource.Setup);
            byte[] before = File.ReadAllBytes(SessionProviderStore.ResolveSelectionPath(baseDir));

            var w = new ProviderSelectionWriter(baseDir);
            w.Snapshot();
            w.WriteSelectionLast(SelectedSessionProvider.WorkAccount, ProviderSelectionSource.Settings);
            w.Restore();

            byte[] after = File.ReadAllBytes(SessionProviderStore.ResolveSelectionPath(baseDir));
            Assert.Equal(before, after);
            Assert.Equal(SelectedSessionProvider.WindowsHello, SessionProviderStore.Load(baseDir).Provider);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    // ---- ProviderSetupService: Windows Hello -----------------------------

    [Fact]
    public void SelectWindowsHello_Available_WritesSelection()
    {
        string baseDir = NewTempBase();
        try
        {
            var svc = new ProviderSetupService(baseDir, new FakeHello(true), new FakeWorkGate(false, false));
            ProviderSetupResult r = svc.SelectWindowsHello(ProviderSelectionSource.Setup);
            Assert.True(r.Succeeded);
            Assert.Equal(SelectedSessionProvider.WindowsHello, SessionProviderStore.Load(baseDir).Provider);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void SelectWindowsHello_Unavailable_WritesNothing()
    {
        string baseDir = NewTempBase();
        try
        {
            var svc = new ProviderSetupService(baseDir, new FakeHello(false), new FakeWorkGate(false, false));
            ProviderSetupResult r = svc.SelectWindowsHello(ProviderSelectionSource.Setup);
            Assert.Equal(ProviderSetupStatus.HelloUnavailable, r.Status);
            Assert.False(File.Exists(SessionProviderStore.ResolveSelectionPath(baseDir)));
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    // ---- ProviderSetupService: Work account ------------------------------

    [Fact]
    public void SelectWorkAccount_NotReady_WritesNothing_NoNativeTest()
    {
        string baseDir = NewTempBase();
        try
        {
            var gate = new FakeWorkGate(ready: false, testResult: true);
            var svc = new ProviderSetupService(baseDir, new FakeHello(true), gate);
            ProviderSetupResult r = svc.SelectWorkAccount(ProviderSelectionSource.Setup);
            Assert.Equal(ProviderSetupStatus.WorkAccountNotReady, r.Status);
            Assert.Equal(0, gate.TestCalls); // never ran the native test
            Assert.False(File.Exists(SessionProviderStore.ResolveSelectionPath(baseDir)));
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void SelectWorkAccount_NativeTestFails_WritesNothing_PreservesPrior()
    {
        string baseDir = NewTempBase();
        try
        {
            // Prior selection is Windows Hello; a failed work-account switch must
            // preserve it (no partial switch).
            SessionProviderStore.Save(baseDir, SelectedSessionProvider.WindowsHello, ProviderSelectionSource.Setup);
            var gate = new FakeWorkGate(ready: true, testResult: false);
            var svc = new ProviderSetupService(baseDir, new FakeHello(true), gate);

            ProviderSetupResult r = svc.SelectWorkAccount(ProviderSelectionSource.SetupRepair);
            Assert.Equal(ProviderSetupStatus.NativeTestFailed, r.Status);
            Assert.Equal(1, gate.TestCalls);
            Assert.Equal(SelectedSessionProvider.WindowsHello, SessionProviderStore.Load(baseDir).Provider);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void SelectWorkAccount_ReadyAndTestPasses_WritesWorkAccountLast()
    {
        string baseDir = NewTempBase();
        try
        {
            var gate = new FakeWorkGate(ready: true, testResult: true);
            var svc = new ProviderSetupService(baseDir, new FakeHello(true), gate);
            ProviderSetupResult r = svc.SelectWorkAccount(ProviderSelectionSource.Setup);
            Assert.True(r.Succeeded);
            SessionProviderSelection sel = SessionProviderStore.Load(baseDir);
            Assert.Equal(SelectedSessionProvider.WorkAccount, sel.Provider);
            Assert.Equal(ProviderSelectionSource.Setup, sel.Source);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    // ---- SetupRepairPlanner ---------------------------------------------

    private static SessionProviderSelection Selected(SelectedSessionProvider p)
        => SessionProviderSelection.Selected(p, ProviderSelectionSource.Setup, DateTimeOffset.UtcNow);

    [Fact]
    public void Repair_BrokenWorkAccount_OffersRepairSwitchRemoveVerify()
    {
        var plan = SetupRepairPlanner.Plan(new ProviderRepairInputs
        {
            Selection = Selected(SelectedSessionProvider.WorkAccount),
            HelloAvailable = true,
            WorkAccountChannelEnabled = true,
            WorkAccountConfigPresent = true,
            WorkAccountVerified = false,
            TenantAdminAuthorized = true,
        });
        Assert.Contains(ProviderRepairAction.RepairCurrentProvider, plan);
        Assert.Contains(ProviderRepairAction.SwitchToWindowsHello, plan);
        Assert.Contains(ProviderRepairAction.RemoveBrokenWorkAccountConfig, plan);
        Assert.Contains(ProviderRepairAction.VerifyOrDeprovisionTenant, plan);
        // Already work-account selected -> no redundant switch-to-work-account.
        Assert.DoesNotContain(ProviderRepairAction.SwitchToWorkAccount, plan);
    }

    [Fact]
    public void Repair_BrokenHello_CanSwitchToWorkAccount_WithoutHello()
    {
        var plan = SetupRepairPlanner.Plan(new ProviderRepairInputs
        {
            Selection = Selected(SelectedSessionProvider.WindowsHello),
            HelloAvailable = false, // Hello broken/unavailable
            WorkAccountChannelEnabled = true,
            WorkAccountConfigPresent = false,
            TenantAdminAuthorized = false,
        });
        Assert.Contains(ProviderRepairAction.RepairCurrentProvider, plan);
        Assert.Contains(ProviderRepairAction.SwitchToWorkAccount, plan);
        // Hello unavailable -> not offered as a switch target.
        Assert.DoesNotContain(ProviderRepairAction.SwitchToWindowsHello, plan);
        // No local work-account config -> no remove/verify.
        Assert.DoesNotContain(ProviderRepairAction.RemoveBrokenWorkAccountConfig, plan);
        Assert.DoesNotContain(ProviderRepairAction.VerifyOrDeprovisionTenant, plan);
    }

    [Fact]
    public void Repair_RecoveryRecord_OffersBothSwitches()
    {
        var plan = SetupRepairPlanner.Plan(new ProviderRepairInputs
        {
            Selection = SessionProviderSelection.Recovery(),
            HelloAvailable = true,
            WorkAccountChannelEnabled = true,
            WorkAccountConfigPresent = false,
            TenantAdminAuthorized = false,
        });
        Assert.Contains(ProviderRepairAction.SwitchToWindowsHello, plan);
        Assert.Contains(ProviderRepairAction.SwitchToWorkAccount, plan);
    }

    [Fact]
    public void Repair_StableChannel_NeverOffersWorkAccountActions()
    {
        var plan = SetupRepairPlanner.Plan(new ProviderRepairInputs
        {
            Selection = Selected(SelectedSessionProvider.WindowsHello),
            HelloAvailable = true,
            WorkAccountChannelEnabled = false, // stable
            WorkAccountConfigPresent = true,
            TenantAdminAuthorized = true,
        });
        Assert.DoesNotContain(ProviderRepairAction.SwitchToWorkAccount, plan);
        Assert.DoesNotContain(ProviderRepairAction.RemoveBrokenWorkAccountConfig, plan);
        Assert.DoesNotContain(ProviderRepairAction.VerifyOrDeprovisionTenant, plan);
    }

    // ---- ProviderLifecyclePolicy ----------------------------------------

    [Fact]
    public void Lifecycle_Update_PreservesSelection_NeverResets()
    {
        Assert.Equal(ProviderLifecycleDecision.PreserveExisting,
            ProviderLifecyclePolicy.ForUpgradeOrReinstall(Selected(SelectedSessionProvider.WorkAccount)));
        Assert.False(ProviderLifecyclePolicy.UpdateResetsToWindowsHello);
        Assert.False(ProviderLifecyclePolicy.UpdateRerunsConsentOrNativeTest);
    }

    [Fact]
    public void Lifecycle_Update_MalformedRecord_RoutesToRecovery()
    {
        Assert.Equal(ProviderLifecycleDecision.RecoveryRequired,
            ProviderLifecyclePolicy.ForUpgradeOrReinstall(SessionProviderSelection.Recovery()));
    }

    [Fact]
    public void Lifecycle_FreshInstall_NoRecord_RequiresSelection()
    {
        Assert.Equal(ProviderLifecycleDecision.FreshSelectionRequired,
            ProviderLifecyclePolicy.ForFreshInstall(recordPresent: false));
        Assert.Equal(ProviderLifecycleDecision.PreserveExisting,
            ProviderLifecyclePolicy.ForFreshInstall(recordPresent: true));
    }

    [Fact]
    public void Lifecycle_Uninstall_StandardRetains_FullRemoves_NeverTenant()
    {
        Assert.False(ProviderLifecyclePolicy.RemovesProviderRecordOnUninstall(UninstallMode.Standard));
        Assert.True(ProviderLifecyclePolicy.RemovesProviderRecordOnUninstall(UninstallMode.Full));
        Assert.False(ProviderLifecyclePolicy.UninstallDeletesTenantRegistrations);
    }

    // ---- AppLaunchWorkAccountGate ---------------------------------------

    private static void WriteJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    // Launcher double that simulates the app: on Start it writes the given
    // result JSON to the result-file path passed after the ResultFlag, then
    // returns null (no real process).
    private sealed class ResultWritingLauncher : IProcessLauncher
    {
        private readonly string? _resultJson;
        public LaunchRecord? Last { get; private set; }
        public ResultWritingLauncher(string? resultJson) => _resultJson = resultJson;
        public System.Diagnostics.Process? Start(string fileName, IList<string> arguments)
        {
            Last = new LaunchRecord(fileName, new System.Collections.Generic.List<string>(arguments));
            if (_resultJson is not null)
            {
                int idx = -1;
                for (int i = 0; i < arguments.Count - 1; i++)
                {
                    if (arguments[i] == AppLaunchWorkAccountGate.ResultFlag) { idx = i + 1; break; }
                }
                if (idx >= 0)
                {
                    WriteJson(arguments[idx], _resultJson);
                }
            }
            return null;
        }
    }

    [Fact]
    public void Gate_Ready_RequiresConfigAndVerifiedVerification()
    {
        string baseDir = NewTempBase();
        try
        {
            string configDir = Path.Combine(baseDir, "PAXCookbook", "Config");
            string resultFile = Path.Combine(baseDir, "result.json");
            var gate = new AppLaunchWorkAccountGate(baseDir, "app.exe", new RecordingProcessLauncher(), resultFile);

            Assert.False(gate.IsWorkAccountReady()); // nothing present

            WriteJson(Path.Combine(configDir, "experimental-wam.json"), "{ \"schemaVersion\": 2 }");
            WriteJson(Path.Combine(configDir, "experimental-wam-verification.json"), "{ \"outcome\": \"none\" }");
            Assert.False(gate.IsWorkAccountReady()); // present but not verified

            WriteJson(Path.Combine(configDir, "experimental-wam-verification.json"), "{ \"outcome\": \"verified\" }");
            Assert.True(gate.IsWorkAccountReady());
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Gate_NativeTest_LaunchesAppWithFlags_AndReadsApproved()
    {
        string baseDir = NewTempBase();
        try
        {
            string resultFile = Path.Combine(baseDir, "result.json");
            var launcher = new ResultWritingLauncher("{ \"approved\": true }");
            var gate = new AppLaunchWorkAccountGate(baseDir, "PAX Cookbook.exe", launcher, resultFile);

            Assert.True(gate.RunNativeWamAuthTest());

            Assert.NotNull(launcher.Last);
            Assert.Equal("PAX Cookbook.exe", launcher.Last!.FileName);
            Assert.Contains(AppLaunchWorkAccountGate.NativeTestFlag, launcher.Last.Arguments);
            Assert.Contains(AppLaunchWorkAccountGate.ConfigFlag, launcher.Last.Arguments);
            Assert.Contains(AppLaunchWorkAccountGate.ResultFlag, launcher.Last.Arguments);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Gate_NativeTest_MissingOrDeniedResult_IsFailure()
    {
        string baseDir = NewTempBase();
        try
        {
            string resultFile = Path.Combine(baseDir, "result.json");

            // App wrote no result at all -> failure.
            var noResultGate = new AppLaunchWorkAccountGate(baseDir, "app.exe", new ResultWritingLauncher(null), resultFile);
            Assert.False(noResultGate.RunNativeWamAuthTest());

            // App wrote an explicit denial -> failure.
            var deniedGate = new AppLaunchWorkAccountGate(baseDir, "app.exe", new ResultWritingLauncher("{ \"approved\": false }"), resultFile);
            Assert.False(deniedGate.RunNativeWamAuthTest());
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }
}
