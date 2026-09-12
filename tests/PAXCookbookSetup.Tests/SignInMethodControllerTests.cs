using System;
using System.IO;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup;
using PAXCookbookSetup.Provider;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Deterministic tests for the wizard sign-in controller: choice exclusivity,
// staged config/verification, native-test gating, continue-gate, and the
// transactional final commit. No MSAL, no real process, no UI.
public sealed class SignInMethodControllerTests
{
    private sealed class FakeHello : IHelloSupportProbe
    {
        private readonly bool _a;
        public FakeHello(bool a) => _a = a;
        public bool IsWindowsHelloAvailable() => _a;
    }

    // Simulates the app native-test writing an approval result during launch.
    private sealed class ApprovingLauncher : IProcessLauncher
    {
        private readonly bool _approve;
        public LaunchRecord? Last { get; private set; }
        public ApprovingLauncher(bool approve) => _approve = approve;
        public System.Diagnostics.Process? Start(string fileName, System.Collections.Generic.IList<string> arguments)
        {
            Last = new LaunchRecord(fileName, new System.Collections.Generic.List<string>(arguments));
            for (int i = 0; i < arguments.Count - 1; i++)
            {
                if (arguments[i] == AppLaunchWorkAccountGate.ResultFlag)
                {
                    string rp = arguments[i + 1];
                    Directory.CreateDirectory(Path.GetDirectoryName(rp)!);
                    File.WriteAllText(rp, _approve ? "{ \"approved\": true }" : "{ \"approved\": false }");
                }
            }
            return null;
        }
    }

    private static (string baseDir, string staging) NewDirs()
    {
        string root = Path.Combine(Path.GetTempPath(), "paxsic_" + Guid.NewGuid().ToString("N"));
        string baseDir = Path.Combine(root, "lad");
        string staging = Path.Combine(root, "stage");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(staging);
        return (baseDir, staging);
    }

    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";

    private static string WriteProvisionResult(string dir)
    {
        string p = Path.Combine(dir, "prov.json");
        File.WriteAllText(p,
            "{ \"kind\": \"pax-cookbook-wam-setup-result\", \"resultKind\": \"provision\", " +
            $"\"tenantId\": \"{TenantId}\", \"clientAppId\": \"{ClientId}\" }}");
        return p;
    }

    private static SignInMethodController New(string baseDir, string staging, bool experimental, bool hello, bool approve)
        => new(experimental, baseDir, staging, "PAX Cookbook.exe", new ApprovingLauncher(approve), new FakeHello(hello));

    [Fact]
    public void ChooseWorkAccount_Ignored_OnStableChannel()
    {
        var (b, s) = NewDirs();
        try
        {
            var c = New(b, s, experimental: false, hello: true, approve: true);
            c.ChooseWorkAccount();
            Assert.Equal(SignInProviderChoice.WindowsHello, c.Choice);
            Assert.False(c.WorkAccountOfferedInChannel);
        }
        finally { Directory.Delete(Directory.GetParent(b)!.FullName, recursive: true); }
    }

    [Fact]
    public void Import_StagesConfig_ResetsVerifyAndTest()
    {
        var (b, s) = NewDirs();
        try
        {
            var c = New(b, s, experimental: true, hello: true, approve: true);
            Assert.True(c.ImportSetupResult(WriteProvisionResult(s)));
            Assert.True(c.WorkConfigStaged);
            Assert.False(c.WorkVerified);
            Assert.False(c.NativeTestPassed);
            Assert.True(File.Exists(c.StagedConfigPath));
            string cfg = File.ReadAllText(c.StagedConfigPath);
            Assert.Contains("\"schemaVersion\": 2", cfg);
            Assert.Contains(TenantId, cfg);
        }
        finally { Directory.Delete(Directory.GetParent(b)!.FullName, recursive: true); }
    }

    [Fact]
    public void Import_InvalidResult_Fails()
    {
        var (b, s) = NewDirs();
        try
        {
            var c = New(b, s, experimental: true, hello: true, approve: true);
            string bad = Path.Combine(s, "bad.json");
            File.WriteAllText(bad, "{ \"tenantId\": \"nope\" }");
            Assert.False(c.ImportSetupResult(bad));
            Assert.False(c.WorkConfigStaged);
        }
        finally { Directory.Delete(Directory.GetParent(b)!.FullName, recursive: true); }
    }

    [Fact]
    public void WorkAccount_FullHappyPath_CommitsWorkAccountLast()
    {
        var (b, s) = NewDirs();
        try
        {
            var c = New(b, s, experimental: true, hello: true, approve: true);
            c.ChooseWorkAccount();
            Assert.True(c.ImportSetupResult(WriteProvisionResult(s)));
            Assert.True(c.MarkVerified(true));
            Assert.True(c.RunNativeTest(Path.Combine(s, "test.json")));
            Assert.True(c.CanContinue());

            ProviderSetupResult r = c.Commit();
            Assert.True(r.Succeeded);
            Assert.Equal(SelectedSessionProvider.WorkAccount, r.Selected);

            // Selection + staged WAM files landed in the final Config folder.
            var sel = SessionProviderStore.Load(b);
            Assert.Equal(SelectedSessionProvider.WorkAccount, sel.Provider);
            Assert.Equal(ProviderSelectionSource.Setup, sel.Source);
            string cfgDir = Path.Combine(b, "PAXCookbook", "Config");
            Assert.True(File.Exists(Path.Combine(cfgDir, "experimental-wam.json")));
            Assert.True(File.Exists(Path.Combine(cfgDir, "experimental-wam-verification.json")));
        }
        finally { Directory.Delete(Directory.GetParent(b)!.FullName, recursive: true); }
    }

    [Fact]
    public void WorkAccount_TestFails_CannotContinue_NoSelectionWritten()
    {
        var (b, s) = NewDirs();
        try
        {
            var c = New(b, s, experimental: true, hello: true, approve: false);
            c.ChooseWorkAccount();
            Assert.True(c.ImportSetupResult(WriteProvisionResult(s)));
            Assert.True(c.MarkVerified(true));
            Assert.False(c.RunNativeTest(Path.Combine(s, "test.json")));
            Assert.False(c.CanContinue());
            // No selection record should exist (fresh base migrates to Hello).
            Assert.True(SessionProviderStore.Load(b).Migrated);
        }
        finally { Directory.Delete(Directory.GetParent(b)!.FullName, recursive: true); }
    }

    [Fact]
    public void WorkAccount_NotVerified_CannotContinue()
    {
        var (b, s) = NewDirs();
        try
        {
            var c = New(b, s, experimental: true, hello: true, approve: true);
            c.ChooseWorkAccount();
            Assert.True(c.ImportSetupResult(WriteProvisionResult(s)));
            // Skipped Verify.
            Assert.True(c.RunNativeTest(Path.Combine(s, "test.json")));
            Assert.False(c.CanContinue());
        }
        finally { Directory.Delete(Directory.GetParent(b)!.FullName, recursive: true); }
    }

    [Fact]
    public void Hello_HappyPath_CommitsHello()
    {
        var (b, s) = NewDirs();
        try
        {
            var c = New(b, s, experimental: true, hello: true, approve: true);
            c.ChooseWindowsHello();
            Assert.True(c.CanContinue());
            ProviderSetupResult r = c.Commit();
            Assert.True(r.Succeeded);
            Assert.Equal(SelectedSessionProvider.WindowsHello, SessionProviderStore.Load(b).Provider);
        }
        finally { Directory.Delete(Directory.GetParent(b)!.FullName, recursive: true); }
    }

    [Fact]
    public void Hello_Unavailable_CannotContinue()
    {
        var (b, s) = NewDirs();
        try
        {
            var c = New(b, s, experimental: true, hello: false, approve: true);
            c.ChooseWindowsHello();
            Assert.False(c.CanContinue());
        }
        finally { Directory.Delete(Directory.GetParent(b)!.FullName, recursive: true); }
    }
}
