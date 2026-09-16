using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup;
using PAXCookbookSetup.Gui;
using PAXCookbookSetup.Uninstall;
using PAXCookbookSetup.Provider;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Deterministic tests for the wizard sign-in controller: choice exclusivity,
// staged config/verification, native-test gating, continue-gate, and the
// transactional final commit. No MSAL or UI; the offline helper fixture alone starts PowerShell.
[Collection("Setup UI STA")]
public sealed class SignInMethodControllerTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public SignInMethodControllerTests(Xunit.Abstractions.ITestOutputHelper output)
        => _output = output;

    private sealed class MissingAppLauncher : IProcessLauncher
    {
        public LaunchRecord? Last => null;

        public System.Diagnostics.Process? Start(string fileName, System.Collections.Generic.IList<string> arguments)
            => throw new System.ComponentModel.Win32Exception(2);
    }

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
        public Action? OnStart { get; set; }
        public LaunchRecord? Last { get; private set; }
        public ApprovingLauncher(bool approve) => _approve = approve;
        public System.Diagnostics.Process? Start(string fileName, System.Collections.Generic.IList<string> arguments)
        {
            Last = new LaunchRecord(fileName, new System.Collections.Generic.List<string>(arguments));
            OnStart?.Invoke();
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
            Assert.True(c.ImportSetupResult(WriteVerifiedResult(s)));
            Assert.True(c.WorkVerified);
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
            Assert.True(c.ImportSetupResult(WriteVerifiedResult(s)));
            Assert.True(c.WorkVerified);
            Assert.False(c.RunNativeTest(Path.Combine(s, "test.json")));
            Assert.False(c.CanContinue());
            // No selection record should exist (fresh base migrates to Hello).
            Assert.True(SessionProviderStore.Load(b).Migrated);
        }
        finally { Directory.Delete(Directory.GetParent(b)!.FullName, recursive: true); }
    }

    [Fact]
    public void WorkAccount_MissingApp_PropagatesWin32Exception_AndCannotContinue()
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            var launcher = new MissingAppLauncher();
            var controller = new SignInMethodController(true, baseDir, staging,
                Path.Combine(staging, "missing", "PAX Cookbook.exe"), launcher, new FakeHello(true));
            controller.ChooseWorkAccount();
            Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
            Assert.True(controller.WorkVerified);
            Assert.False(controller.NativeTestPassed);
            Assert.False(controller.CanContinue());

            var exception = Assert.Throws<System.ComponentModel.Win32Exception>(
                () => controller.RunNativeTest(Path.Combine(staging, "native-test.json")));

            Assert.Equal(2, exception.NativeErrorCode);
            Assert.True(controller.WorkVerified);
            Assert.False(controller.NativeTestPassed);
            Assert.False(controller.CanContinue());
            Assert.True(SessionProviderStore.Load(baseDir).Migrated);
            Assert.Null(launcher.Last);
            _output.WriteLine(JsonSerializer.Serialize(new
            {
                classification = "DEFECT_CHARACTERIZATION_NOT_WORKFLOW_READINESS",
                exceptionClass = exception.GetType().FullName,
                nativeErrorCode = exception.NativeErrorCode,
                workVerified = controller.WorkVerified,
                nativeTestPassed = controller.NativeTestPassed,
                canContinue = controller.CanContinue(),
                providerSelectionMigrated = SessionProviderStore.Load(baseDir).Migrated,
                launcherLastIsNull = launcher.Last is null
            }));
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, recursive: true); }
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

    private const string ImportedUtc = "2025-01-02T03:04:05.0000000Z";
    private const string ImportedFingerprint = "d66bfc9dad28f84724047e1c38ba7b8b8fd2ba083af8e7189f6efc1fc1a00faf";

    private static JsonObject VerifiedDocument()
        => JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1,
            kind = "pax-cookbook-wam-setup-result",
            resultKind = "verify",
            providerId = "entra-wam",
            providerVersion = "1",
            authorizationModel = "single-configured-tenant",
            tenantId = TenantId,
            clientAppId = ClientId,
            structuralVerification = new
            {
                success = true,
                checks = new
                {
                    applicationExists = true,
                    servicePrincipalExists = true,
                    pickerCompatibleAudience = true,
                    wamRedirectExact = true,
                    graphUserReadOnly = true,
                    noAppPermission = true,
                    noCustomScope = true,
                    noCredential = true,
                    noUnexpectedRedirect = true,
                    allPrincipalsUserReadGrant = true
                }
            },
            grantResult = new { allPrincipalsUserRead = true },
            configFingerprint = ImportedFingerprint,
            verifiedUtc = ImportedUtc
        })!.AsObject();

    private static string WriteVerifiedResult(string staging)
    {
        string path = Path.Combine(staging, "verify.json");
        File.WriteAllText(path, VerifiedDocument().ToJsonString());
        return path;
    }

    private static void AssertImportGatesClosed(SignInMethodController controller)
    {
        Assert.False(controller.WorkConfigStaged);
        Assert.False(controller.WorkVerified);
        Assert.False(controller.NativeTestPassed);
        Assert.False(controller.CanContinue());
        Assert.Null(typeof(SignInMethodController).GetMethod("MarkVerified"));
        Assert.False(controller.RunNativeTest("unused-result.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Import_Verified_PreservesRecord_RequiresLocalNativeTest(bool uppercaseFingerprint)
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            var controller = New(baseDir, staging, experimental: true, hello: false, approve: true);
            controller.ChooseWorkAccount();
            string path = WriteVerifiedResult(staging);
            JsonObject document = VerifiedDocument();
            string fingerprint = uppercaseFingerprint ? ImportedFingerprint.ToUpperInvariant() : ImportedFingerprint;
            document["configFingerprint"] = fingerprint;
            File.WriteAllText(path, "\uFEFF" + document.ToJsonString());
            Assert.True(controller.ImportSetupResult(path));
            Assert.True(controller.WorkConfigStaged);
            Assert.True(controller.WorkVerified);
            Assert.False(controller.NativeTestPassed);
            Assert.False(controller.CanContinue());
            string recordPath = Path.Combine(Path.GetDirectoryName(controller.StagedConfigPath)!, "experimental-wam-verification.json");
            JsonObject record = JsonNode.Parse(File.ReadAllText(recordPath))!.AsObject();
            Assert.Equal(4, record.Count);
            Assert.Equal(1, record["schemaVersion"]!.GetValue<int>());
            Assert.Equal("verified", record["outcome"]!.GetValue<string>());
            Assert.Equal(ImportedUtc, record["verifiedUtc"]!.GetValue<string>());
            Assert.Equal(fingerprint, record["configFingerprint"]!.GetValue<string>());
            Assert.True(controller.RunNativeTest(Path.Combine(staging, "native.json")));
            Assert.True(controller.CanContinue());
            Assert.True(controller.Commit().Succeeded);
            Assert.Equal(SelectedSessionProvider.WorkAccount, SessionProviderStore.Load(baseDir).Provider);
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, recursive: true); }
    }

    [Theory]
    [InlineData("tenantId", "\"33333333-3333-3333-3333-333333333333\"")]
    [InlineData("clientAppId", "\"33333333-3333-3333-3333-333333333333\"")]
    [InlineData("tenantId", "\"invalid\"")]
    [InlineData("clientAppId", "\"22222222222222222222222222222222\"")]
    [InlineData("schemaVersion", "-1")]
    [InlineData("schemaVersion", "\"1\"")]
    [InlineData("providerId", "\"windows-hello\"")]
    [InlineData("providerVersion", "1")]
    [InlineData("authorizationModel", "\"any-tenant\"")]
    [InlineData("kind", "\"other\"")]
    [InlineData("resultKind", "\"provision\"")]
    [InlineData("resultKind", "\"other\"")]
    [InlineData("resultKind", null)]
    [InlineData("verifiedUtc", "\"not-a-date\"")]
    [InlineData("verifiedUtc", "\"2025-01-02T03:04:05\"")]
    [InlineData("verifiedUtc", "\"2025-01-02T03:04:05+01:00\"")]
    [InlineData("verifiedUtc", "null")]
    [InlineData("configFingerprint", "\"\"")]
    [InlineData("configFingerprint", null)]
    [InlineData("nativeTestPassed", "true")]
    [InlineData("structuralVerification", "[]")]
    [InlineData("structuralVerification.success", "false")]
    [InlineData("structuralVerification.success", "\"true\"")]
    [InlineData("structuralVerification.checks", "{}")]
    [InlineData("structuralVerification.checks.noCredential", "false")]
    [InlineData("structuralVerification.checks.allPrincipalsUserReadGrant", "false")]
    [InlineData("structuralVerification.checks.nativeTestPassed", "true")]
    [InlineData("grantResult", "null")]
    [InlineData("grantResult.allPrincipalsUserRead", "false")]
    [InlineData("grantResult.allPrincipalsUserRead", null)]
    public void Import_InvalidVerification_AfterSuccess_RefusesAndClears(string propertyPath, string? replacement)
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            var controller = New(baseDir, staging, experimental: true, hello: false, approve: true);
            controller.ChooseWorkAccount();
            string path = WriteVerifiedResult(staging);
            Assert.True(controller.ImportSetupResult(path));
            Assert.True(controller.RunNativeTest(Path.Combine(staging, "native.json")));
            Assert.True(controller.CanContinue());
            JsonObject document = VerifiedDocument();
            string[] parts = propertyPath.Split('.');
            JsonObject owner = document;
            for (int index = 0; index < parts.Length - 1; index++) owner = owner[parts[index]]!.AsObject();
            if (replacement is null) owner.Remove(parts[^1]);
            else owner[parts[^1]] = JsonNode.Parse(replacement);
            File.WriteAllText(path, document.ToJsonString());
            Assert.False(controller.ImportSetupResult(path));
            AssertImportGatesClosed(controller);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(controller.StagedConfigPath)!, "experimental-wam-verification.json")));
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, recursive: true); }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("missing")]
    [InlineData("invalid-path")]
    [InlineData("array")]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-check")]
    [InlineData("duplicate-legacy")]
    [InlineData("oversized")]
    [InlineData("delete-failure")]
    [InlineData("write-failure")]
    public void Import_ReadOrStagingFailure_AfterSuccess_ClosesGates(string failure)
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            var controller = New(baseDir, staging, experimental: true, hello: false, approve: true);
            controller.ChooseWorkAccount();
            string path = WriteVerifiedResult(staging);
            Assert.True(controller.ImportSetupResult(path));
            Assert.True(controller.RunNativeTest(Path.Combine(staging, "native.json")));
            Assert.True(controller.CanContinue());
            string verificationPath = Path.Combine(Path.GetDirectoryName(controller.StagedConfigPath)!, "experimental-wam-verification.json");
            using FileStream? heldFile = failure == "delete-failure"
                ? new FileStream(verificationPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            switch (failure)
            {
                case "malformed": File.WriteAllText(path, "{"); break;
                case "missing": File.Delete(path); break;
                case "invalid-path": path = "\0"; break;
                case "array": File.WriteAllText(path, "[]"); break;
                case "duplicate-root":
                    File.WriteAllText(path, VerifiedDocument().ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"));
                    break;
                case "duplicate-check":
                    File.WriteAllText(path, VerifiedDocument().ToJsonString().Replace("\"noCredential\":true", "\"noCredential\":true,\"noCredential\":true"));
                    break;
                case "duplicate-legacy":
                    File.WriteAllText(path, $"{{\"tenantId\":\"{TenantId}\",\"tenantId\":\"{TenantId}\",\"clientAppId\":\"{ClientId}\"}}");
                    break;
                case "oversized": File.AppendAllText(path, new string(' ', 64 * 1024)); break;
                case "write-failure":
                    File.Delete(controller.StagedConfigPath);
                    Directory.CreateDirectory(controller.StagedConfigPath);
                    break;
            }
            Assert.False(controller.ImportSetupResult(path));
            AssertImportGatesClosed(controller);
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, recursive: true); }
    }

    private static JsonObject ProvisionDocument(bool structuralSuccess = true, bool consentGranted = true)
    {
        JsonObject document = VerifiedDocument();
        document["resultKind"] = "provision";
        document.Remove("grantResult");
        document.Remove("configFingerprint");
        document["helper"] = new JsonObject
        {
            ["ownershipTag"] = "pax-cookbook-wam-provisioner",
            ["name"] = "New-PaxCookbookEntraWamSetup"
        };
        document["structuralVerification"]!["success"] = structuralSuccess;
        document["structuralVerification"]!["checks"]!["allPrincipalsUserReadGrant"] = consentGranted;
        return document;
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Import_AuthenticProvision_AfterSuccess_StagesOnlyConfig(bool structuralSuccess, bool consentGranted)
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            var controller = New(baseDir, staging, experimental: true, hello: false, approve: true);
            controller.ChooseWorkAccount();
            string path = WriteVerifiedResult(staging);
            Assert.True(controller.ImportSetupResult(path));
            Assert.True(controller.RunNativeTest(Path.Combine(staging, "native.json")));
            Assert.True(controller.CanContinue());
            string verificationPath = Path.Combine(Path.GetDirectoryName(controller.StagedConfigPath)!, "experimental-wam-verification.json");
            Assert.True(File.Exists(verificationPath));
            File.WriteAllText(path, ProvisionDocument(structuralSuccess, consentGranted).ToJsonString());
            Assert.True(controller.ImportSetupResult(path));
            Assert.True(controller.WorkConfigStaged);
            Assert.False(controller.WorkVerified);
            Assert.False(controller.NativeTestPassed);
            Assert.False(controller.CanContinue());
            Assert.False(File.Exists(verificationPath));
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, recursive: true); }
    }

    [Theory]
    [InlineData("schemaVersion", "-1")]
    [InlineData("schemaVersion", "\"1\"")]
    [InlineData("kind", "\"other\"")]
    [InlineData("resultKind", null)]
    [InlineData("resultKind", "\"other\"")]
    [InlineData("resultKind", "\"verify\"")]
    [InlineData("providerId", "\"windows-hello\"")]
    [InlineData("providerVersion", "1")]
    [InlineData("authorizationModel", "\"any-tenant\"")]
    [InlineData("tenantId", "\"invalid\"")]
    [InlineData("clientAppId", "null")]
    [InlineData("helper", null)]
    [InlineData("helper.ownershipTag", "\"synthetic\"")]
    [InlineData("helper.name", "\"other\"")]
    [InlineData("structuralVerification", "[]")]
    [InlineData("structuralVerification.success", "\"false\"")]
    [InlineData("structuralVerification.checks", "{}")]
    [InlineData("structuralVerification.checks.allPrincipalsUserReadGrant", "\"false\"")]
    [InlineData("verifiedUtc", "\"not-a-date\"")]
    [InlineData("verifiedUtc", "\"2025-01-02T03:04:05\"")]
    [InlineData("verifiedUtc", "\"2025-01-02T03:04:05+01:00\"")]
    [InlineData("grantResult", "{\"allPrincipalsUserRead\":true}")]
    [InlineData("configFingerprint", "\"\"")]
    [InlineData("outcome", "\"verified\"")]
    [InlineData("nativeTestPassed", "true")]
    public void Import_InvalidStructuredProvision_AfterSuccess_RefusesAndClears(string propertyPath, string? replacement)
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            var controller = New(baseDir, staging, experimental: true, hello: false, approve: true);
            controller.ChooseWorkAccount();
            string path = WriteVerifiedResult(staging);
            Assert.True(controller.ImportSetupResult(path));
            Assert.True(controller.RunNativeTest(Path.Combine(staging, "native.json")));
            Assert.True(controller.CanContinue());
            JsonObject document = ProvisionDocument();
            string[] parts = propertyPath.Split('.');
            JsonObject owner = document;
            for (int index = 0; index < parts.Length - 1; index++) owner = owner[parts[index]]!.AsObject();
            if (replacement is null) owner.Remove(parts[^1]);
            else owner[parts[^1]] = JsonNode.Parse(replacement);
            File.WriteAllText(path, document.ToJsonString());
            Assert.False(controller.ImportSetupResult(path));
            AssertImportGatesClosed(controller);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(controller.StagedConfigPath)!, "experimental-wam-verification.json")));
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, recursive: true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Import_Legacy_AfterSuccess_StagesOnlyConfig(bool forgedNativeFlag, bool bareIds)
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            var controller = New(baseDir, staging, experimental: true, hello: false, approve: true);
            controller.ChooseWorkAccount();
            Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
            Assert.True(controller.RunNativeTest(Path.Combine(staging, "native.json")));
            Assert.True(controller.CanContinue());
            string path = WriteProvisionResult(staging);
            if (bareIds)
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new { tenantId = TenantId, clientAppId = ClientId }));
            }
            if (forgedNativeFlag)
            {
                JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                document["nativeTestPassed"] = true;
                File.WriteAllText(path, document.ToJsonString());
            }
            Assert.True(controller.ImportSetupResult(path));
            Assert.True(controller.WorkConfigStaged);
            Assert.False(controller.WorkVerified);
            Assert.False(controller.NativeTestPassed);
            Assert.False(controller.CanContinue());
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(controller.StagedConfigPath)!, "experimental-wam-verification.json")));
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task AdminToolkit_RealHelperWorkflow_IsOfflineAndImportsVerifiedOutput()
    {
        const string expectedHash = "880436F136FD24FD331F9F8DB7EF462EC013A76F14FF046A1F6E6A6C04531100";
        DirectoryInfo? repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "tools", "entra", "New-PaxCookbookEntraWamSetup.ps1")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string source = Path.Combine(repository!.FullName, "tools", "entra", "New-PaxCookbookEntraWamSetup.ps1");
        string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        string beforeHash = Hash(source);
        Assert.Equal(expectedHash, beforeHash);
        var (baseDir, staging) = NewDirs();
        string root = Directory.GetParent(baseDir)!.FullName;
        var evidence = new JsonObject { ["helperSource"] = source, ["helperBefore"] = beforeHash };
        var runs = new JsonArray();
        var gates = new JsonArray();
        evidence["runs"] = runs;
        evidence["importGates"] = gates;

        void CheckGates(string name, SignInMethodController controller, bool staged, bool verified, bool native, bool ready)
        {
            bool[] actual = { controller.WorkConfigStaged, controller.WorkVerified, controller.NativeTestPassed, controller.CanContinue() };
            bool[] expected = { staged, verified, native, ready };
            gates.Add(JsonSerializer.SerializeToNode(new { name, fields = new[] { "staged", "verified", "native", "canContinue" }, actual, expected }));
            Assert.Equal(expected, actual);
        }

        try
        {
            foreach (string scenario in new[] { "accepted", "creation-no", "consent-no-confirmed" })
            {
                string scenarioRoot = Path.Combine(root, scenario);
                string helper = Path.Combine(scenarioRoot, "tools", "entra", Path.GetFileName(source));
                Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
                string childTemp = Directory.CreateDirectory(Path.Combine(scenarioRoot, "temp")).FullName;
                File.Copy(source, helper);
                Assert.Equal(beforeHash, Hash(helper));
                Assert.False(Directory.Exists(Path.Combine(scenarioRoot, "tools", "guards")));
                string fixture = Path.Combine(scenarioRoot, "fixture.ps1");
                File.WriteAllText(fixture, OfflineAdminHelperFixture);
                var start = new System.Diagnostics.ProcessStartInfo(@"C:\Program Files\PowerShell\7\pwsh.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = scenarioRoot
                };
                foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", fixture, "-Scenario", scenario })
                    start.ArgumentList.Add(argument);
                start.Environment["TEMP"] = childTemp;
                start.Environment["TMP"] = childTemp;
                start.Environment["PATH"] = string.Empty;
                using var process = new System.Diagnostics.Process { StartInfo = start };
                Assert.True(process.Start());
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    throw;
                }
                finally
                {
                    var run = new JsonObject
                    {
                        ["scenario"] = scenario,
                        ["exitCode"] = process.HasExited ? process.ExitCode : null,
                        ["stdout"] = await stdout,
                        ["stderr"] = await stderr,
                        ["helperCopyAfter"] = Hash(helper)
                    };
                    var documents = new JsonObject();
                    foreach (string path in Directory.GetFiles(scenarioRoot, "*.json"))
                        documents[Path.GetFileName(path)] = JsonNode.Parse(File.ReadAllText(path));
                    run["documents"] = documents;
                    runs.Add(run);
                }
                Assert.Equal(beforeHash, Hash(helper));
                Assert.Equal(0, process.ExitCode);
                if (scenario == "creation-no")
                {
                    Assert.False(File.Exists(Path.Combine(scenarioRoot, "provision.json")));
                    continue;
                }

                var controller = New(baseDir, staging, experimental: true, hello: false, approve: true);
                controller.ChooseWorkAccount();
                string provisionPath = Path.Combine(scenarioRoot, "provision.json");
                JsonObject provision = JsonNode.Parse(File.ReadAllText(provisionPath))!.AsObject();
                Assert.False(provision.ContainsKey("configFingerprint"));
                Assert.False(provision.ContainsKey("grantResult"));
                Assert.True(controller.ImportSetupResult(provisionPath));
                CheckGates(scenario + ":provision", controller, true, false, false, false);
                string verifyPath = Path.Combine(scenarioRoot, "verified.json");
                bool imported = controller.ImportSetupResult(verifyPath);
                gates.Add(JsonSerializer.SerializeToNode(new { name = scenario + ":verify-import", actual = imported, expected = scenario == "accepted" }));
                if (scenario != "accepted")
                {
                    Assert.False(imported);
                    CheckGates(scenario + ":verify", controller, false, false, false, false);
                    continue;
                }
                Assert.True(imported);
                CheckGates("accepted:verify", controller, true, true, false, false);
                Assert.True(controller.RunNativeTest(Path.Combine(staging, "fake-native.json")));
                CheckGates("accepted:fake-native", controller, true, true, true, true);
                ProviderSetupResult committed = controller.Commit();
                SelectedSessionProvider selected = SessionProviderStore.Load(baseDir).Provider;
                evidence["commit"] = JsonSerializer.SerializeToNode(new { committed.Succeeded, committed.Selected, persisted = selected, syntheticNativeOnly = true });
                Assert.True(committed.Succeeded);
                Assert.Equal(SelectedSessionProvider.WorkAccount, committed.Selected);
                Assert.Equal(SelectedSessionProvider.WorkAccount, selected);

                JsonObject tampered = JsonNode.Parse(File.ReadAllText(verifyPath))!.AsObject();
                tampered["clientAppId"] = "33333333-3333-3333-3333-333333333333";
                string tamperedPath = Path.Combine(staging, "tampered.json");
                File.WriteAllText(tamperedPath, tampered.ToJsonString());
                evidence["tamperedVerify"] = tampered.DeepClone();
                bool tamperImported = controller.ImportSetupResult(tamperedPath);
                gates.Add(JsonSerializer.SerializeToNode(new { name = "retained-fingerprint-tamper", actual = tamperImported, expected = false }));
                Assert.False(tamperImported);
                CheckGates("accepted:tamper", controller, false, false, false, false);
            }
        }
        finally
        {
            evidence["helperAfter"] = Hash(source);
            _output.WriteLine("STEP5G_EVIDENCE=" + evidence.ToJsonString());
            Directory.Delete(root, recursive: true);
            Assert.Equal(beforeHash, Hash(source));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async System.Threading.Tasks.Task NativeChild_BoundedFailure_RetainsUntilExplicitExitCleanup(bool timeout, bool killThrows)
    {
        var (baseDir, staging) = NewDirs();
        var child = new ControlledNativeChild { KillThrows = killThrows };
        try
        {
            using var cancel = new System.Threading.CancellationTokenSource();
            var launcher = new ControlledNativeLauncher(child);
            string result = Path.Combine(staging, "native-test.json");
            var gate = new AppLaunchWorkAccountGate(baseDir, "inert-app", launcher, result,
                timeout ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromMinutes(1));
            var running = gate.RunNativeWamAuthTestAsync(cancel.Token);
            await child.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (!timeout) cancel.Cancel();
            bool approved = await running.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(approved);
            Assert.True(gate.HasOutstandingNativeChild);
            Assert.False(child.Disposed);
            Assert.Equal(1, child.KillCalls);
            File.WriteAllText(result, "{\"approved\":true}");
            Assert.False(await gate.RunNativeWamAuthTestAsync(System.Threading.CancellationToken.None));
            Assert.Equal(1, launcher.Starts);
            Assert.True(File.Exists(result));
            Assert.False(await gate.RetryNativeCleanupAsync());
            Assert.False(child.Disposed);
            child.Exit.TrySetResult(true);
            Assert.True(await gate.RetryNativeCleanupAsync());
            Assert.False(gate.HasOutstandingNativeChild);
            Assert.True(child.Disposed);
            Assert.False(File.Exists(result));
            _output.WriteLine(JsonSerializer.Serialize(new { journey = "owned-native-child", timeout, killThrows,
                approved, child.KillCalls, launcher.Starts, child.Disposed, gate.HasOutstandingNativeChild }));
        }
        finally { child.Exit.TrySetResult(true); Directory.Delete(Directory.GetParent(baseDir)!.FullName, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task NativeChild_ConfirmedExit_ReleasesOnSuccessfulKill(bool timeout)
    {
        var (baseDir, staging) = NewDirs();
        var child = new ControlledNativeChild { ExitOnKill = true };
        try
        {
            using var cancel = new System.Threading.CancellationTokenSource();
            var gate = new AppLaunchWorkAccountGate(baseDir, "inert-app", new ControlledNativeLauncher(child),
                Path.Combine(staging, "result.json"), timeout ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromMinutes(1));
            var running = gate.RunNativeWamAuthTestAsync(cancel.Token);
            await child.WaitEntered.Task;
            if (!timeout) cancel.Cancel();
            Assert.False(await running.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(gate.HasOutstandingNativeChild);
            Assert.True(child.Disposed);
            Assert.Equal(1, child.KillCalls);
        }
        finally { child.Exit.TrySetResult(true); Directory.Delete(Directory.GetParent(baseDir)!.FullName, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task WizardPreparation_NativeChild_StopRetryRetainsPayloadAndRejectsLateApproval(bool close)
    {
        var (baseDir, staging) = NewDirs();
        var child = new ControlledNativeChild { KillThrows = true };
        try
        {
            using var log = new SetupLogger(Path.Combine(baseDir, "logs"));
            WizardSignInPreparation? owner = null;
            WizardInstallRunner.PreparedPayload? prepared = null;
            var launcher = new ControlledNativeLauncher(child);
            var controller = new SignInMethodController(true, baseDir, staging,
                () => owner!.ResolveNativeAppPath(), launcher, new FakeHello(true));
            owner = new WizardSignInPreparation(controller, async (early, token) =>
            {
                var result = await PrepareJourneyPayload(Path.Combine(baseDir, "PAXCookbook"), log, early, token,
                    Phase11PayloadResolverTests.PreparedZip(), () => { });
                prepared = result.Payload;
                return result;
            }, (_, _, _) => System.Threading.Tasks.Task.FromResult(new PrerequisiteCoordinatorResult(Array.Empty<NamedPrerequisiteResult>(), false)),
                (_, _) => throw new InvalidOperationException("Install must not run."));
            using (owner)
            {
                controller.ChooseWorkAccount();
                Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
                string resultPath = Path.Combine(staging, "result.json");
                var running = owner.TestAsync(resultPath);
                await child.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var stopped = close ? owner.StopAsync() : null;
                if (!close) owner.ChooseWindowsHello();
                Assert.False(await running.WaitAsync(TimeSpan.FromSeconds(3)));
                if (stopped is not null)
                    await Assert.ThrowsAsync<InvalidOperationException>(() => stopped.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True(controller.HasOutstandingNativeChild);
                Assert.True(owner.CanRetryCleanup);
                Assert.False(owner.CanReleaseStaging);
                Assert.False(owner.CanContinue);
                Assert.False(owner.CanImport);
                Assert.False(owner.CanTest);
                Assert.True(Directory.Exists(prepared!.PayloadRoot));
                Assert.True(File.Exists(prepared.DownloadedZipPath));
                File.WriteAllText(resultPath, "{\"approved\":true}");
                Assert.False(await controller.RunNativeTestAsync(resultPath, System.Threading.CancellationToken.None));
                Assert.Equal(1, launcher.Starts);
                Assert.True(File.Exists(resultPath));
                Assert.False(await owner.RetryCleanupAsync());
                child.Exit.TrySetResult(true);
                if (close) await owner.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
                else Assert.True(await owner.RetryCleanupAsync());
                Assert.False(controller.HasOutstandingNativeChild);
                Assert.False(controller.NativeTestPassed);
                Assert.False(File.Exists(resultPath));
                Assert.Equal(!close, Directory.Exists(prepared.PayloadRoot));
                Assert.Equal(!close, owner.CanContinue);
                Assert.True(child.Disposed);
                _output.WriteLine(JsonSerializer.Serialize(new { universe = "Fake owned child and real disposable payload; cancel or close, failed cleanup, late exit, explicit retry",
                    close, controller.HasOutstandingNativeChild, controller.NativeTestPassed, owner.CanContinue, launcher.Starts, child.Disposed }));
            }
            Assert.False(Directory.Exists(prepared!.PayloadRoot));
        }
        finally { child.Exit.TrySetResult(true); Directory.Delete(Directory.GetParent(baseDir)!.FullName, true); }
    }

    [Theory]
    [InlineData("cancel-return")]
    [InlineData("cancel-throw")]
    [InlineData("download-failure")]
    public async System.Threading.Tasks.Task WizardPreparation_PartialDownloadFailure_RetainsCleanupOwnership(string failure)
    {
        static bool CleanupBlocked(JsonObject state)
            => state["directoryExists"]!.GetValue<bool>() &&
               !state["canReleaseStaging"]!.GetValue<bool>() &&
               state["canRetryCleanup"]!.GetValue<bool>() &&
               !state["canContinue"]!.GetValue<bool>() &&
               !state["canTest"]!.GetValue<bool>() &&
               !state["canImport"]!.GetValue<bool>();

        static JsonObject Observe(WizardSignInPreparation owner, string? ownedRoot) => new()
        {
            ["directoryExists"] = Directory.Exists(ownedRoot),
            ["canReleaseStaging"] = owner.CanReleaseStaging,
            ["canRetryCleanup"] = owner.CanRetryCleanup,
            ["canContinue"] = owner.CanContinue,
            ["canTest"] = owner.CanTest,
            ["canImport"] = owner.CanImport
        };

        var positive = new JsonObject
        {
            ["directoryExists"] = true, ["canReleaseStaging"] = false,
            ["canRetryCleanup"] = true, ["canContinue"] = false,
            ["canTest"] = false, ["canImport"] = false
        };
        var negative = positive.DeepClone().AsObject();
        negative["canReleaseStaging"] = true;
        var journeys = new JsonArray();
        var evidence = new JsonObject
        {
            ["universe"] = "This failure mode; unlocked and FileShare.Read-locked partial files in canonical PrepareAsync-owned OS-temp roots; fake native launcher and prerequisites/install delegates only",
            ["failure"] = failure,
            ["predicate"] = "directoryExists && !canReleaseStaging && canRetryCleanup && !canContinue && !canTest && !canImport",
            ["positiveControl"] = CleanupBlocked(positive),
            ["negativeControl"] = CleanupBlocked(negative),
            ["positiveInput"] = positive,
            ["negativeInput"] = negative,
            ["journeys"] = journeys
        };
        var (baseDir, staging) = NewDirs();
        string sentinel = Path.Combine(Directory.GetParent(baseDir)!.FullName, "neighbor.txt");
        File.WriteAllText(sentinel, "unchanged neighbor");
        try
        {
            Assert.True(CleanupBlocked(positive));
            Assert.False(CleanupBlocked(negative));
            foreach (bool lockedPartial in new[] { false, true })
            {
                FileStream? held = null;
                string? ownedRoot = null;
                string? partialPath = null;
                int downloads = 0;
                int nativeLaunches = 0;
                int prerequisites = 0;
                int installs = 0;
                WizardSignInPreparation? owner = null;
                var row = new JsonObject { ["lockedPartial"] = lockedPartial };
                journeys.Add(row);
                using var log = new SetupLogger(Path.Combine(baseDir, lockedPartial ? "locked-logs" : "unlocked-logs"));
                var launcher = new ApprovingLauncher(true) { OnStart = () => nativeLaunches++ };
                var controller = new SignInMethodController(true, baseDir, staging,
                    () => owner!.ResolveNativeAppPath(), launcher, new FakeHello(true));
                owner = new WizardSignInPreparation(controller, async (early, token) =>
                {
                    var result = await WizardInstallRunner.PrepareAsync(Path.Combine(baseDir, "PAXCookbook"),
                        null, _ => { }, log, new WizardInstallRunner.PreparationDependencies((stage, downloadToken) =>
                        {
                            downloads++;
                            ownedRoot = stage;
                            partialPath = Path.Combine(stage, "partial.zip");
                            File.WriteAllBytes(partialPath, new byte[] { 0x50, 0x4b, 0x03, 0x04 });
                            if (lockedPartial)
                                held = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            if (failure != "download-failure") owner!.Cancel();
                            if (failure == "cancel-throw") downloadToken.ThrowIfCancellationRequested();
                            return System.Threading.Tasks.Task.FromResult(
                                new PayloadDownloader.DownloadResult(false, null, "Synthetic partial download failure."));
                        }, () => false), token, early);
                    row["preparation"] = JsonSerializer.SerializeToNode(new
                    {
                        result.Success, payloadReturned = result.Payload is not null, result.ExitCode
                    });
                    return result;
                }, (_, _, _) =>
                {
                    prerequisites++;
                    return System.Threading.Tasks.Task.FromResult(
                        new PrerequisiteCoordinatorResult(Array.Empty<NamedPrerequisiteResult>(), false));
                }, (_, _) =>
                {
                    installs++;
                    throw new InvalidOperationException("Install must not run.");
                });
                try
                {
                    controller.ChooseWorkAccount();
                    Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
                    row["testPassed"] = await owner.TestAsync(Path.Combine(staging, "result.json"))
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    row["afterTest"] = Observe(owner, ownedRoot);
                    row["cleanupBlocked"] = CleanupBlocked(row["afterTest"]!.AsObject());
                    row["partialExistsWhileHeld"] = File.Exists(partialPath);
                    row["handleHeld"] = held is not null && !held.SafeFileHandle.IsClosed;
                    var stopError = await Record.ExceptionAsync(() => owner.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)));
                    row["stopErrorType"] = stopError?.GetType().FullName;
                    row["stopRefused"] = stopError is InvalidOperationException;
                    row["afterStop"] = Observe(owner, ownedRoot);
                    held?.Dispose();
                    held = null;
                    row["retryAfterRelease"] = await owner.RetryCleanupAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    var retryStopError = await Record.ExceptionAsync(() => owner.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)));
                    row["retryStopErrorType"] = retryStopError?.GetType().FullName;
                    row["afterReleaseAndStop"] = Observe(owner, ownedRoot);
                    row["sentinelUnchanged"] = File.ReadAllText(sentinel) == "unchanged neighbor";
                    row["nativeTestPassed"] = controller.NativeTestPassed;
                }
                finally
                {
                    held?.Dispose();
                    var finalStopError = await Record.ExceptionAsync(() => owner.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)));
                    row["finalStopErrorType"] = finalStopError?.GetType().FullName;
                    row["rootRemainsAfterOwnerTeardown"] = Directory.Exists(ownedRoot);
                    row["downloads"] = downloads;
                    row["nativeLaunches"] = nativeLaunches;
                    row["prerequisites"] = prerequisites;
                    row["installs"] = installs;
                }
            }

            foreach (JsonNode journey in journeys)
            {
                bool lockedPartial = journey["lockedPartial"]!.GetValue<bool>();
                Assert.False(journey["testPassed"]!.GetValue<bool>());
                Assert.False(journey["preparation"]!["Success"]!.GetValue<bool>());
                Assert.False(journey["preparation"]!["payloadReturned"]!.GetValue<bool>());
                Assert.Equal(lockedPartial, journey["partialExistsWhileHeld"]!.GetValue<bool>());
                Assert.Equal(lockedPartial, journey["handleHeld"]!.GetValue<bool>());
                Assert.False(journey["nativeTestPassed"]!.GetValue<bool>());
                Assert.True(journey["sentinelUnchanged"]!.GetValue<bool>());
                Assert.Equal(1, journey["downloads"]!.GetValue<int>());
                Assert.Equal(0, journey["nativeLaunches"]!.GetValue<int>());
                Assert.Equal(0, journey["prerequisites"]!.GetValue<int>());
                Assert.Equal(0, journey["installs"]!.GetValue<int>());
                Assert.Equal(lockedPartial, CleanupBlocked(journey["afterTest"]!.AsObject()));
                Assert.Equal(!lockedPartial, journey["afterTest"]!["canReleaseStaging"]!.GetValue<bool>());
                Assert.Equal(lockedPartial, journey["afterTest"]!["canRetryCleanup"]!.GetValue<bool>());
                Assert.Equal(!lockedPartial, journey["afterTest"]!["canTest"]!.GetValue<bool>());
                Assert.Equal(!lockedPartial, journey["afterTest"]!["canImport"]!.GetValue<bool>());
                Assert.False(journey["afterTest"]!["canContinue"]!.GetValue<bool>());
                Assert.Equal(lockedPartial, journey["stopRefused"]!.GetValue<bool>());
                Assert.Equal(lockedPartial, journey["afterStop"]!["directoryExists"]!.GetValue<bool>());
                Assert.Equal(!lockedPartial, journey["afterStop"]!["canReleaseStaging"]!.GetValue<bool>());
                Assert.True(journey["retryAfterRelease"]!.GetValue<bool>());
                Assert.Null(journey["retryStopErrorType"]);
                Assert.False(journey["afterReleaseAndStop"]!["directoryExists"]!.GetValue<bool>());
                Assert.False(journey["rootRemainsAfterOwnerTeardown"]!.GetValue<bool>());
            }
        }
        finally
        {
            _output.WriteLine("PARTIAL_CLEANUP_EVIDENCE=" + evidence.ToJsonString());
            Directory.Delete(Directory.GetParent(baseDir)!.FullName, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async System.Threading.Tasks.Task WizardPreparation_PrerequisiteCleanup_WaitsForAcknowledgment(bool locked, bool throws)
    {
        var (baseDir, staging) = NewDirs();
        var entered = new System.Threading.Tasks.TaskCompletionSource<string>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        using var acknowledge = new System.Threading.ManualResetEventSlim();
        FileStream? held = null;
        WizardSignInPreparation? owner = null;
        int preparations = 0;
        int installs = 0;
        var controller = new SignInMethodController(true, baseDir, staging, () => "unused", new ApprovingLauncher(true), new FakeHello(true));
        controller.ChooseWindowsHello();
        owner = new WizardSignInPreparation(controller,
            (_, _) => { preparations++; throw new InvalidOperationException("Unexpected preparation"); },
            (_, _, _) => System.Threading.Tasks.Task.FromResult(owner!.RunPrerequisites(root =>
            {
                Directory.CreateDirectory(root);
                string partial = Path.Combine(root, "partial.tmp");
                File.WriteAllText(partial, "synthetic prerequisite");
                if (locked) held = File.Open(partial, FileMode.Open, FileAccess.Read, FileShare.Read);
                entered.TrySetResult(root);
                if (!acknowledge.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                if (throws) throw new IOException("Synthetic prerequisite failure");
                return new PrerequisiteCoordinatorResult(Array.Empty<NamedPrerequisiteResult>(), true);
            })),
            (_, _) => { installs++; throw new InvalidOperationException("Unexpected install"); });
        try
        {
            var operation = owner.InstallAsync();
            string ownedRoot = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stopped = owner.StopAsync();
            bool Waiting(bool completed, bool exists, bool release) => !completed && exists && !release;
            bool positive = Waiting(false, true, false);
            bool negative = Waiting(true, false, true);
            bool waiting = Waiting(stopped.IsCompleted, Directory.Exists(ownedRoot), owner.CanReleaseStaging);
            Assert.True(positive);
            Assert.False(negative);
            Assert.True(waiting);
            acknowledge.Set();
            await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(3)));
            var stopError = await Record.ExceptionAsync(() => stopped.WaitAsync(TimeSpan.FromSeconds(3)));
            bool retained = Directory.Exists(ownedRoot) && !owner.CanReleaseStaging && owner.CanRetryCleanup;
            Assert.Equal(locked, retained);
            Assert.Equal(locked, stopError is InvalidOperationException);
            held?.Dispose();
            held = null;
            await owner.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            bool remainsAfterProductCleanup = Directory.Exists(ownedRoot);
            _output.WriteLine("PREREQUISITE_CLEANUP_EVIDENCE=" + JsonSerializer.Serialize(new
            {
                universe = "Hello owner with fake synchronous prerequisite acknowledgment, partial file, return/throw, locked/unlocked; no installers",
                predicate = "!stopCompleted && ownedDirectoryExists && !canReleaseStaging", positive, negative, waiting,
                locked, throws, retained, stopErrorType = stopError?.GetType().FullName,
                remainsAfterProductCleanup, preparations, installs
            }));
            Assert.False(remainsAfterProductCleanup);
            Assert.True(owner.CanReleaseStaging);
            Assert.Equal(0, preparations);
            Assert.Equal(0, installs);
        }
        finally
        {
            acknowledge.Set();
            held?.Dispose();
            await owner.StopAsync();
            Directory.Delete(Directory.GetParent(baseDir)!.FullName, true);
        }
    }

    [Fact]
    public void NativeTest_LazyPreparedPath_ResolvesAtLaunch_AndFailureRevokesPass()
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            string? preparedPath = null;
            int resolutions = 0;
            var launcher = new ApprovingLauncher(true);
            var controller = new SignInMethodController(true, baseDir, staging,
                () =>
                {
                    resolutions++;
                    return preparedPath ?? throw new InvalidOperationException("Not prepared.");
                }, launcher, new FakeHello(true));
            Assert.Equal(0, resolutions);
            controller.ChooseWorkAccount();
            Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
            Assert.False(controller.NativeTestPassed);
            preparedPath = Path.Combine(staging, "prepared", "App", "PAX Cookbook.exe");
            Assert.True(controller.RunNativeTest(Path.Combine(staging, "native-test.json")));
            Assert.Equal(preparedPath, launcher.Last!.FileName);
            Assert.Equal(1, resolutions);
            Assert.True(controller.CanContinue());
            preparedPath = null;
            Assert.Throws<InvalidOperationException>(() =>
                controller.RunNativeTest(Path.Combine(staging, "native-test.json")));
            Assert.False(controller.NativeTestPassed);
            Assert.False(controller.CanContinue());
            Assert.True(controller.WorkVerified);
            Assert.True(controller.WorkConfigStaged);
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, recursive: true); }
    }

    [Theory]
    [InlineData("cancel-before")]
    [InlineData("cancel-during")]
    [InlineData("invalidate-during")]
    [InlineData("stale-locked")]
    public async System.Threading.Tasks.Task NativeTest_CancellationAndStaleResult_CannotRestoreApproval(string refusal)
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            using var cancel = new System.Threading.CancellationTokenSource();
            var launcher = new ApprovingLauncher(true);
            var controller = new SignInMethodController(true, baseDir, staging,
                "inert-app", launcher, new FakeHello(true));
            controller.ChooseWorkAccount();
            Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
            string result = Path.Combine(staging, "native-test.json");
            Assert.True(await controller.RunNativeTestAsync(result, cancel.Token));
            Assert.True(controller.CanContinue());
            if (refusal == "cancel-before") cancel.Cancel();
            if (refusal == "cancel-during") launcher.OnStart = cancel.Cancel;
            if (refusal == "invalidate-during") launcher.OnStart = controller.InvalidateNativeTest;
            using var locked = refusal == "stale-locked"
                ? new FileStream(result, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            Assert.False(await controller.RunNativeTestAsync(result, cancel.Token));
            Assert.False(controller.NativeTestPassed);
            Assert.False(controller.CanContinue());
            Assert.True(controller.WorkVerified);
            controller.ChooseWindowsHello();
            Assert.True(controller.CanContinue());
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task WizardPreparation_FreshVerifiedImport_TestAndInstall_DownloadsOnce()
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            using var log = new SetupLogger(Path.Combine(staging, "logs"));
            int downloads = 0;
            var events = new System.Collections.Generic.List<string>();
            WizardInstallRunner.PreparedPayload? prepared = null;
            bool samePrepared = false;
            JsonNode? syntheticConfig = null;
            bool? installRootExistsAtNativeTest = null;
            bool? preparedForEarlyExecution = null;
            bool? artifactVerifiedAtNativeTest = null;
            string? validatedAppPath = null;
            var zip = Phase11PayloadResolverTests.PreparedZip();
            var launcher = new ApprovingLauncher(true);
            WizardSignInPreparation? owner = null;
            var controller = new SignInMethodController(true, baseDir, staging,
                () => owner!.ResolveNativeAppPath(), launcher, new FakeHello(true));
            var shell = new Phase11PayloadResolverTests.PreparedShell();
            var stopper = new RecordingAppStopper();
            string installRoot = Path.Combine(baseDir, "PAXCookbook");
            string? preparedRoot = null;
            bool installRootExistsInitially = Directory.Exists(installRoot);
            Assert.False(installRootExistsInitially);
            launcher.OnStart = () =>
            {
                installRootExistsAtNativeTest = Directory.Exists(installRoot);
                Assert.False(installRootExistsAtNativeTest.Value);
                Assert.NotNull(prepared);
                preparedForEarlyExecution = prepared.PreparedForEarlyExecution;
                artifactVerifiedAtNativeTest = prepared.VerifyCurrentArtifact();
                Assert.True(preparedForEarlyExecution.Value);
                Assert.True(artifactVerifiedAtNativeTest.Value);
                validatedAppPath = prepared.GetValidatedAppExePath();
                Assert.Equal(validatedAppPath, launcher.Last!.FileName);
                syntheticConfig = JsonNode.Parse(File.ReadAllText(controller.StagedConfigPath));
                Assert.NotNull(syntheticConfig);
                events.Add("native-test");
            };
            owner = new WizardSignInPreparation(controller,
                async (early, token) =>
                {
                    var result = await WizardInstallRunner.PrepareAsync(installRoot, null, _ => { }, log,
                        new WizardInstallRunner.PreparationDependencies((stage, _) =>
                        {
                            downloads++;
                            events.Add("download");
                            return System.Threading.Tasks.Task.FromResult(Phase11PayloadResolverTests.DeliverZip(stage, zip,
                                new ManifestVerifier.PayloadExpectation(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)), zip.Length, "1.0.0")));
                        }, () => false), token, early);
                    prepared = result.Payload;
                    preparedRoot = result.Payload?.PayloadRoot;
                    events.Add("prepared");
                    return result;
                }, (_, early, _) =>
                {
                    events.Add(early ? "native-prerequisites" : "install-prerequisites");
                    return System.Threading.Tasks.Task.FromResult(new PrerequisiteCoordinatorResult(Array.Empty<NamedPrerequisiteResult>(), false));
                }, (payload, token) =>
                {
                    samePrepared = ReferenceEquals(prepared, payload);
                    Assert.Same(prepared, payload);
                    events.Add("install");
                    return System.Threading.Tasks.Task.FromResult(WizardInstallRunner.InstallPrepared(payload,
                        installRoot, _ => { }, log, shell, stopper, token));
                });
            using (owner)
            {
                Assert.True(owner.CanContinue);
                Assert.Equal(0, downloads);
                controller.ChooseWorkAccount();
                Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
                Assert.False(owner.CanContinue);
                bool installRootExistsBeforeTest = Directory.Exists(installRoot);
                Assert.False(installRootExistsBeforeTest);
                Assert.True(await owner.TestAsync(Path.Combine(staging, "result.json")));
                Assert.Equal(Path.GetFullPath(Path.Combine(preparedRoot!, "App/bin/PAXCookbook.exe")), launcher.Last!.FileName);
                Assert.True(owner.CanContinue);
                string[] nativeArguments = launcher.Last.Arguments.ToArray();
                byte[] manifest = File.ReadAllBytes(Path.Combine(preparedRoot!, "manifest.json"));
                var artifact = new
                {
                    prepared!.PayloadRoot,
                    prepared.Origin,
                    prepared.ManifestSha256,
                    observedManifestSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manifest)),
                    prepared.DownloadedZipPath,
                    prepared.DownloadedZipSha256,
                    zipSha256 = ManifestVerifier.ComputeSha256(prepared.DownloadedZipPath!),
                    zipLength = new FileInfo(prepared.DownloadedZipPath!).Length
                };
                Assert.True((await owner.InstallAsync()).Success);
                Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(preparedRoot!, "manifest.json")));
                Assert.Single(shell.InstallRoots);
                Assert.Equal(1, downloads);
                static bool Ordered(string[] observed, bool sameObject) => sameObject && observed.SequenceEqual(new[]
                {
                    "download", "prepared", "native-prerequisites", "native-test", "install-prerequisites", "install"
                });
                string[] observed = events.ToArray();
                string[] reordered = (string[])observed.Clone();
                int nativeIndex = Array.IndexOf(reordered, "native-test");
                int installIndex = Array.IndexOf(reordered, "install");
                Assert.True(nativeIndex >= 0);
                Assert.True(installIndex >= 0);
                (reordered[nativeIndex], reordered[installIndex]) = (reordered[installIndex], reordered[nativeIndex]);
                bool positiveControl = Ordered(observed, samePrepared);
                bool negativeControl = Ordered(reordered, samePrepared);
                bool identityNegativeControl = Ordered(observed, false);
                _output.WriteLine("FRESH_ORDERING_EVIDENCE=" + JsonSerializer.Serialize(new
                {
                    universe = "Callbacks in this synthetic fresh verified-import TestAsync then InstallAsync journey and the PreparedPayload object passed to install",
                    predicate = "sameObject && observed.SequenceEqual(download,prepared,native-prerequisites,native-test,install-prerequisites,install)",
                    safety = "synthetic-only repeated-digit fixture; ApprovingLauncher records and writes approval without starting an OS process",
                    events = observed, reordered, positiveControl, negativeControl, identityNegativeControl,
                    identityNegativeInput = new { events = observed, sameObject = false },
                    samePrepared, downloads, launcher = launcher.Last, nativeArguments, configuration = syntheticConfig,
                    installRoot, installRootExistsInitially, installRootExistsBeforeTest, installRootExistsAtNativeTest,
                    preparedForEarlyExecution, artifactVerifiedAtNativeTest, validatedAppPath, artifact
                }));
                Assert.True(positiveControl);
                Assert.False(negativeControl);
                Assert.False(identityNegativeControl);
            }
            Assert.False(Directory.Exists(preparedRoot));
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, recursive: true); }
    }

    private static System.Threading.Tasks.Task<WizardPreparationResult> PrepareJourneyPayload(
        string installRoot, SetupLogger log, bool early, System.Threading.CancellationToken cancel,
        byte[] zip, Action downloaded)
        => WizardInstallRunner.PrepareAsync(installRoot, null, _ => { }, log,
            new WizardInstallRunner.PreparationDependencies((stage, _) =>
            {
                downloaded();
                return System.Threading.Tasks.Task.FromResult(Phase11PayloadResolverTests.DeliverZip(stage, zip,
                    new ManifestVerifier.PayloadExpectation(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)), zip.Length, "1.0.0")));
            }, () => false), cancel, early);

    [Theory]
    [InlineData("member")]
    [InlineData("manifest")]
    [InlineData("zip")]
    public async System.Threading.Tasks.Task WizardPreparation_AfterTestTamper_RefusesBeforePrerequisitesOrInstall(string changedPart)
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            using var log = new SetupLogger(Path.Combine(staging, "logs"));
            byte[] zip = Phase11PayloadResolverTests.PreparedZip();
            int downloads = 0, prerequisiteCalls = 0, installCalls = 0;
            string installRoot = Path.Combine(baseDir, "PAXCookbook");
            var shell = new Phase11PayloadResolverTests.PreparedShell();
            var launcher = new ApprovingLauncher(true);
            WizardSignInPreparation? owner = null;
            WizardInstallRunner.PreparedPayload? prepared = null;
            var controller = new SignInMethodController(true, baseDir, staging,
                () => owner!.ResolveNativeAppPath(), launcher, new FakeHello(true));
            owner = new WizardSignInPreparation(controller, async (early, token) =>
            {
                var result = await PrepareJourneyPayload(installRoot, log, early, token, zip, () => downloads++);
                prepared = result.Payload;
                return result;
            }, (_, _, _) =>
            {
                prerequisiteCalls++;
                return System.Threading.Tasks.Task.FromResult(new PrerequisiteCoordinatorResult(Array.Empty<NamedPrerequisiteResult>(), false));
            }, (payload, token) =>
            {
                installCalls++;
                return System.Threading.Tasks.Task.FromResult(WizardInstallRunner.InstallPrepared(payload,
                    installRoot, _ => { }, log, shell, new RecordingAppStopper(), token));
            });
            using (owner)
            {
                controller.ChooseWorkAccount();
                Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
                Assert.True(await owner.TestAsync(Path.Combine(staging, "result.json")));
                Assert.True(prepared!.VerifyCurrentArtifact());
                string changedPath = changedPart switch
                {
                    "manifest" => Path.Combine(prepared.PayloadRoot, "manifest.json"),
                    "zip" => prepared.DownloadedZipPath!,
                    _ => Path.Combine(prepared.PayloadRoot, "App/bin/member.txt")
                };
                byte[] original = File.ReadAllBytes(changedPath);
                File.AppendAllText(changedPath, "changed");
                var result = await owner.InstallAsync();
                Assert.False(result.Success);
                Assert.True(owner.RequiresFreshPreparation);
                Assert.False(controller.NativeTestPassed);
                Assert.False(owner.CanContinue);
                Assert.Equal(1, prerequisiteCalls);
                Assert.Equal(0, installCalls);
                Assert.Empty(shell.InstallRoots);
                Assert.False(Directory.Exists(installRoot));
                Assert.Equal(1, downloads);
                File.WriteAllBytes(changedPath, original);
                Assert.False(prepared.VerifyCurrentArtifact());
                _output.WriteLine(JsonSerializer.Serialize(new { journey = "after-test-tamper", changedPart,
                    downloads, prerequisiteCalls, installCalls, result.Success, owner.RequiresFreshPreparation }));
            }
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, true); }
    }

    [Theory]
    [InlineData("member")]
    [InlineData("manifest")]
    [InlineData("zip")]
    [InlineData("missing-runtime-dependency")]
    public async System.Threading.Tasks.Task WizardPreparation_Hello_ExplicitRetryReleasesInvalidatedPayload(string changedPart)
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            using var log = new SetupLogger(Path.Combine(staging, "logs"));
            byte[] zip = Phase11PayloadResolverTests.PreparedZip();
            int downloads = 0, prerequisiteCalls = 0, installCalls = 0;
            string installRoot = Path.Combine(baseDir, "PAXCookbook");
            var shell = new Phase11PayloadResolverTests.PreparedShell();
            var launcher = new ApprovingLauncher(true);
            WizardSignInPreparation? owner = null;
            WizardInstallRunner.PreparedPayload? prepared = null;
            var controller = new SignInMethodController(true, baseDir, staging,
                () => owner!.ResolveNativeAppPath(), launcher, new FakeHello(true));
            owner = new WizardSignInPreparation(controller, async (early, token) =>
            {
                var result = await PrepareJourneyPayload(installRoot, log, early, token, zip, () => downloads++);
                prepared = result.Payload;
                return result;
            }, (_, _, _) =>
            {
                prerequisiteCalls++;
                return System.Threading.Tasks.Task.FromResult(new PrerequisiteCoordinatorResult(Array.Empty<NamedPrerequisiteResult>(), false));
            }, (payload, token) =>
            {
                installCalls++;
                return System.Threading.Tasks.Task.FromResult(WizardInstallRunner.InstallPrepared(payload,
                    installRoot, _ => { }, log, shell, new RecordingAppStopper(), token));
            });
            using (owner)
            {
                controller.ChooseWorkAccount();
                Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
                Assert.True(await owner.TestAsync(Path.Combine(staging, "result.json")));
                Assert.True(prepared!.VerifyCurrentArtifact());
                var invalidated = prepared;
                string changedPath = changedPart switch
                {
                    "manifest" => Path.Combine(prepared.PayloadRoot, "manifest.json"),
                    "zip" => prepared.DownloadedZipPath!,
                    "missing-runtime-dependency" => Path.Combine(prepared.PayloadRoot, "App/bin/runtimes/win-x64/native/msalruntime.dll"),
                    _ => Path.Combine(prepared.PayloadRoot, "App/bin/member.txt")
                };
                if (changedPart == "missing-runtime-dependency") File.Delete(changedPath);
                else File.AppendAllText(changedPath, "changed");
                Assert.False((await owner.InstallAsync()).Success);
                Assert.True(owner.RequiresFreshPreparation);
                Assert.False(controller.NativeTestPassed);
                Assert.True(controller.WorkConfigStaged);
                Assert.True(controller.WorkVerified);
                Assert.Equal(1, prerequisiteCalls);
                Assert.Equal(0, installCalls);
                Assert.Empty(shell.InstallRoots);
                Assert.False(Directory.Exists(installRoot));
                Assert.Equal(1, downloads);

                owner.ChooseWindowsHello();
                Assert.Equal(1, downloads);
                Assert.True(owner.RequiresFreshPreparation);
                Assert.True(Directory.Exists(invalidated.PayloadRoot));
                Assert.False((await owner.InstallAsync()).Success);
                Assert.Equal(1, prerequisiteCalls);
                Assert.Equal(0, installCalls);
                Assert.Empty(shell.InstallRoots);
                Assert.Equal(1, downloads);
                Assert.True(owner.CanRetryCleanup);
                Assert.True(await owner.RetryCleanupAsync());
                Assert.False(owner.RequiresFreshPreparation);
                Assert.False(owner.CanRetryCleanup);
                Assert.False(Directory.Exists(invalidated.PayloadRoot));
                Assert.Equal(1, downloads);
                Assert.True(controller.WorkConfigStaged);
                Assert.True(controller.WorkVerified);
                Assert.False(controller.NativeTestPassed);
                Assert.True(owner.CanContinue);

                Assert.True((await owner.InstallAsync()).Success);
                Assert.NotSame(invalidated, prepared);
                Assert.True(prepared!.VerifyCurrentArtifact());
                Assert.Single(shell.InstallRoots);
                Assert.Equal(1, installCalls);
                Assert.Equal(2, prerequisiteCalls);
                Assert.Equal(2, downloads);
                Assert.False(controller.NativeTestPassed);
                controller.ChooseWorkAccount();
                Assert.False(owner.CanContinue);
                Assert.True(controller.WorkVerified);
                _output.WriteLine(JsonSerializer.Serialize(new { journey = "hello-explicit-retry", changedPart,
                    downloads, prerequisiteCalls, installCalls, owner.RequiresFreshPreparation, controller.NativeTestPassed }));
            }
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, true); }
    }

    [Theory]
    [InlineData("no-work")]
    [InlineData("failed-work")]
    [InlineData("prepared-work")]
    public async System.Threading.Tasks.Task WizardPreparation_Hello_IsLazyAndReusesSuccessfulPreparation(string journey)
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            using var log = new SetupLogger(Path.Combine(staging, "logs"));
            byte[] zip = Phase11PayloadResolverTests.PreparedZip();
            int downloads = 0, attempts = 0, normalPrerequisites = 0;
            string installRoot = Path.Combine(baseDir, "PAXCookbook");
            var shell = new Phase11PayloadResolverTests.PreparedShell();
            var launcher = new ApprovingLauncher(true);
            WizardSignInPreparation? owner = null;
            var controller = new SignInMethodController(true, baseDir, staging,
                () => owner!.ResolveNativeAppPath(), launcher, new FakeHello(true));
            owner = new WizardSignInPreparation(controller, (early, token) =>
            {
                attempts++;
                if (early && journey == "failed-work") throw new IOException("private failure details");
                if (!early) Assert.True(normalPrerequisites > 0);
                return PrepareJourneyPayload(installRoot, log, early, token, zip, () => downloads++);
            }, (_, early, _) =>
            {
                if (!early) normalPrerequisites++;
                return System.Threading.Tasks.Task.FromResult(new PrerequisiteCoordinatorResult(Array.Empty<NamedPrerequisiteResult>(), false));
            }, (payload, token) => System.Threading.Tasks.Task.FromResult(WizardInstallRunner.InstallPrepared(payload,
                installRoot, _ => { }, log, shell, new RecordingAppStopper(), token)));
            using (owner)
            {
                owner.ChooseWindowsHello();
                Assert.True(owner.CanContinue);
                Assert.Equal(0, attempts);
                if (journey != "no-work")
                {
                    controller.ChooseWorkAccount();
                    Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
                    Assert.Equal(0, attempts);
                    Assert.Equal(journey == "prepared-work", await owner.TestAsync(Path.Combine(staging, "result.json")));
                }
                int attemptsBeforeHello = attempts;
                owner.ChooseWindowsHello();
                Assert.Equal(attemptsBeforeHello, attempts);
                Assert.False(controller.NativeTestPassed);
                Assert.True(owner.CanContinue);
                Assert.True((await owner.InstallAsync()).Success);
                Assert.Single(shell.InstallRoots);
                Assert.Equal(1, downloads);
                Assert.Equal(journey == "failed-work" ? 2 : 1, attempts);
                if (journey == "no-work") Assert.Null(launcher.Last);
                _output.WriteLine(JsonSerializer.Serialize(new { journey, downloads, attempts, normalPrerequisites }));
            }
            var unavailable = New(baseDir, staging, true, false, true);
            using var blocked = new WizardSignInPreparation(unavailable,
                (_, _) => throw new InvalidOperationException(), (_, _, _) => throw new InvalidOperationException(),
                (_, _) => throw new InvalidOperationException());
            blocked.ChooseWindowsHello();
            Assert.False(blocked.CanContinue);
            Assert.False((await blocked.InstallAsync()).Success);
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, true); }
    }

    [Theory]
    [InlineData("launch-throws")]
    [InlineData("runtime-missing")]
    [InlineData("wrong-architecture")]
    [InlineData("missing-app")]
    [InlineData("tampered-dependency")]
    public async System.Threading.Tasks.Task WizardPreparation_FailureIsBounded_AndRetryUsesVerifiedPayload(string failure)
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            using var log = new SetupLogger(Path.Combine(staging, "logs"));
            byte[] zip = Phase11PayloadResolverTests.PreparedZip();
            int downloads = 0;
            bool injectFailure = true;
            string installRoot = Path.Combine(baseDir, "PAXCookbook");
            var probe = new WizardDetectionTests.FakeProbe();
            var desktop = new PrerequisiteCoordinatorTests.FakeInstaller(PrerequisiteKind.DotNet8DesktopRuntime, PrerequisiteInstallResult.Installed("synthetic"));
            var aspNet = new PrerequisiteCoordinatorTests.FakeInstaller(PrerequisiteKind.AspNetCoreRuntime, PrerequisiteInstallResult.Installed("synthetic"));
            var coordinator = new PrerequisiteCoordinator(new IPrerequisiteInstaller[] { desktop, aspNet });
            var launcher = new ApprovingLauncher(true);
            launcher.OnStart = () => { if (injectFailure && failure == "launch-throws") throw new IOException("private failure details"); };
            WizardSignInPreparation? owner = null;
            var controller = new SignInMethodController(true, baseDir, staging,
                () => owner!.ResolveNativeAppPath(), launcher, new FakeHello(true));
            owner = new WizardSignInPreparation(controller, async (early, token) =>
            {
                var result = await PrepareJourneyPayload(installRoot, log, early, token, zip, () => downloads++);
                if (injectFailure && failure == "missing-app")
                    File.Delete(result.Payload!.GetValidatedAppExePath());
                if (injectFailure && failure == "tampered-dependency")
                    File.AppendAllText(Path.Combine(result.Payload!.PayloadRoot, "App/bin/runtimes/win-x64/native/msalruntime.dll"), "changed");
                return result;
            }, (payload, early, token) =>
            {
                Assert.True(early);
                _ = payload!.GetValidatedAppExePath();
                Assert.Equal("x64", payload.TargetArch);
                string rid = injectFailure && failure == "wrong-architecture" ? "arm64" : "x64";
                if (!(injectFailure && failure == "runtime-missing"))
                {
                    probe.HklmSubKeys[$@"SOFTWARE\dotnet\Setup\InstalledVersions\{rid}\sharedfx\Microsoft.WindowsDesktop.App"] = new[] { "8.0.28" };
                    probe.HklmSubKeys[$@"SOFTWARE\dotnet\Setup\InstalledVersions\{rid}\sharedfx\Microsoft.AspNetCore.App"] = new[] { "8.0.28" };
                }
                return System.Threading.Tasks.Task.FromResult(coordinator.RunDetected(
                    new PrerequisiteDetector(probe, System.Runtime.InteropServices.Architecture.Arm64)
                        .ForRuntimeArchitecture(System.Runtime.InteropServices.Architecture.X64),
                    true, staging, _ => { }, (_, _) => RetryExitDecision.ExitSetup, token));
            }, (_, _) => throw new InvalidOperationException());
            using (owner)
            {
                controller.ChooseWorkAccount();
                Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
                Assert.False(await owner.TestAsync(Path.Combine(staging, "result.json")));
                Assert.False(controller.NativeTestPassed);
                Assert.False(owner.CanContinue);
                Assert.True(owner.CanImport);
                Assert.True(owner.CanTest);
                Assert.DoesNotContain("private", owner.Status);
                if (failure != "launch-throws") Assert.Null(launcher.Last);
                owner.ChooseWindowsHello();
                Assert.True(owner.CanContinue);
                controller.ChooseWorkAccount();
                injectFailure = false;
                Assert.True(await owner.TestAsync(Path.Combine(staging, "result.json")));
                Assert.True(owner.CanContinue);
                Assert.Equal(failure is "missing-app" or "tampered-dependency" ? 2 : 1, downloads);
                _output.WriteLine(JsonSerializer.Serialize(new { failure, downloads, controller.NativeTestPassed, owner.CanContinue }));
            }
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task WizardPreparation_LatePreparedReturn_IsDisposedAfterHelloOrClose(bool close)
    {
        var (baseDir, staging) = NewDirs();
        var release = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var log = new SetupLogger(Path.Combine(staging, "logs"));
            var entered = new System.Threading.Tasks.TaskCompletionSource<WizardInstallRunner.PreparedPayload>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] zip = Phase11PayloadResolverTests.PreparedZip();
            string source = Path.Combine(staging, "source.zip");
            File.WriteAllBytes(source, zip);
            string neighbor = Path.Combine(baseDir, "neighbor.txt");
            File.WriteAllText(neighbor, "keep");
            var launcher = new ApprovingLauncher(true);
            WizardSignInPreparation? owner = null;
            var controller = new SignInMethodController(true, baseDir, staging, () => owner!.ResolveNativeAppPath(), launcher, new FakeHello(true));
            owner = new WizardSignInPreparation(controller, async (early, token) =>
            {
                var result = await PrepareJourneyPayload(Path.Combine(baseDir, "PAXCookbook"), log, early, token, zip, () => { });
                entered.SetResult(result.Payload!);
                await release.Task;
                return result;
            }, (_, _, _) => throw new InvalidOperationException("No prerequisites after cancellation."),
                (_, _) => throw new InvalidOperationException());
            using (owner)
            {
                controller.ChooseWorkAccount();
                Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
                var running = owner.TestAsync(Path.Combine(staging, "result.json"));
                var returned = await entered.Task;
                string payloadRoot = returned.PayloadRoot;
                string zipPath = returned.DownloadedZipPath!;
                if (close) owner.Dispose(); else owner.ChooseWindowsHello();
                Assert.True(owner.IsBusy);
                Assert.False(owner.CanContinue);
                Assert.True(Directory.Exists(payloadRoot));
                release.SetResult(true);
                Assert.False(await running);
                Assert.False(Directory.Exists(payloadRoot));
                Assert.False(File.Exists(zipPath));
                Assert.False(owner.IsBusy);
                Assert.Equal(!close, owner.CanContinue);
                Assert.Null(launcher.Last);
                Assert.Equal(zip, File.ReadAllBytes(source));
                Assert.Equal("keep", File.ReadAllText(neighbor));
                _output.WriteLine(JsonSerializer.Serialize(new { journey = "late-prepared-return", close, owner.IsBusy, owner.CanContinue }));
            }
        }
        finally { release.TrySetResult(true); Directory.Delete(Directory.GetParent(baseDir)!.FullName, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task WizardPreparation_NativeLateCompletion_CannotApproveOrUseDisposedPayload(bool close)
    {
        var (baseDir, staging) = NewDirs();
        var release = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var log = new SetupLogger(Path.Combine(staging, "logs"));
            var entered = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            WizardInstallRunner.PreparedPayload? prepared = null;
            var launcher = new ApprovingLauncher(true);
            launcher.OnStart = () =>
            {
                entered.SetResult(true);
                release.Task.GetAwaiter().GetResult();
                Assert.True(File.Exists(launcher.Last!.FileName));
                Assert.True(Directory.Exists(staging));
            };
            WizardSignInPreparation? owner = null;
            var controller = new SignInMethodController(true, baseDir, staging, () => owner!.ResolveNativeAppPath(), launcher, new FakeHello(true));
            owner = new WizardSignInPreparation(controller, async (early, token) =>
            {
                var result = await PrepareJourneyPayload(Path.Combine(baseDir, "PAXCookbook"), log, early, token,
                    Phase11PayloadResolverTests.PreparedZip(), () => { });
                prepared = result.Payload;
                return result;
            }, (_, _, _) => System.Threading.Tasks.Task.FromResult(new PrerequisiteCoordinatorResult(Array.Empty<NamedPrerequisiteResult>(), false)),
                (_, _) => throw new InvalidOperationException());
            using (owner)
            {
                controller.ChooseWorkAccount();
                Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
                var running = owner.TestAsync(Path.Combine(staging, "result.json"));
                await entered.Task;
                string payloadRoot = prepared!.PayloadRoot;
                if (close) owner.Dispose(); else owner.ChooseWindowsHello();
                Assert.True(Directory.Exists(payloadRoot));
                Assert.True(owner.IsBusy);
                Assert.False(owner.CanContinue);
                release.SetResult(true);
                Assert.False(await running);
                Assert.False(controller.NativeTestPassed);
                Assert.Equal(!close, Directory.Exists(payloadRoot));
                Assert.Equal(!close, owner.CanContinue);
                if (close) Assert.Throws<OperationCanceledException>(() => owner.ResolveNativeAppPath());
                _output.WriteLine(JsonSerializer.Serialize(new { journey = "native-late-completion", close, controller.NativeTestPassed, owner.CanContinue }));
            }
        }
        finally { release.TrySetResult(true); Directory.Delete(Directory.GetParent(baseDir)!.FullName, true); }
    }

    [Fact]
    public void Commit_UsesCurrentDestinationWithoutReimportingOrRetesting()
    {
        var (baseDir, staging) = NewDirs();
        try
        {
            var controller = New(baseDir, staging, true, true, true);
            controller.ChooseWorkAccount();
            Assert.True(controller.ImportSetupResult(WriteVerifiedResult(staging)));
            Assert.True(controller.RunNativeTest(Path.Combine(staging, "result.json")));
            string current = Path.Combine(baseDir, "new-location");
            Assert.True(controller.Commit(current).Succeeded);
            Assert.Equal(SelectedSessionProvider.WorkAccount, SessionProviderStore.Load(current).Provider);
            Assert.False(Directory.Exists(Path.Combine(baseDir, "PAXCookbook")));
            Assert.True(controller.CanContinue());
        }
        finally { Directory.Delete(Directory.GetParent(baseDir)!.FullName, true); }
    }

    internal sealed class ControlledNativeChild : INativeChildProcess
    {
        public System.Threading.Tasks.TaskCompletionSource<bool> Exit { get; } = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Threading.Tasks.TaskCompletionSource<bool> WaitEntered { get; } = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        public bool KillThrows { get; set; }
        public bool ExitOnKill { get; set; }
        public int KillCalls { get; private set; }
        public bool Disposed { get; private set; }
        public bool HasExited => Exit.Task.IsCompletedSuccessfully;
        public System.Threading.Tasks.Task WaitForExitAsync(System.Threading.CancellationToken cancel)
        {
            WaitEntered.TrySetResult(true);
            return Exit.Task.WaitAsync(cancel);
        }
        public void Kill()
        {
            KillCalls++;
            if (KillThrows) throw new InvalidOperationException("Synthetic kill refusal.");
            if (ExitOnKill) Exit.TrySetResult(true);
        }
        public void Dispose() => Disposed = true;
    }

    internal sealed class ControlledNativeLauncher(ControlledNativeChild child) : IProcessLauncher, INativeChildLauncher
    {
        public int Starts { get; private set; }
        public LaunchRecord? Last { get; private set; }
        public System.Diagnostics.Process? Start(string fileName, System.Collections.Generic.IList<string> arguments)
            => throw new InvalidOperationException("No OS launch is allowed in this fixture.");
        public INativeChildProcess StartNativeChild(string fileName, System.Collections.Generic.IList<string> arguments)
        {
            Starts++;
            Last = new LaunchRecord(fileName, arguments.ToArray());
            return child;
        }
    }

    private const string OfflineAdminHelperFixture = """
        param([ValidateSet('accepted','creation-no','consent-no-confirmed')][string]$Scenario)
        $ErrorActionPreference = 'Stop'
        $fixtureRoot = $PSScriptRoot
        $helperPath = Join-Path $fixtureRoot 'tools/entra/New-PaxCookbookEntraWamSetup.ps1'
        $fixtureClient = '22222222-2222-2222-2222-222222222222'
        $fixtureObject = '33333333-3333-3333-3333-333333333333'
        $fixtureSp = '44444444-4444-4444-4444-444444444444'
        $fixtureGraphSp = '55555555-5555-5555-5555-555555555555'
        $fixtureGrant = '66666666-6666-6666-6666-666666666666'
        $fixtureGraphApp = '00000003-0000-0000-c000-000000000000'
        $grantUrl = "https://graph.microsoft.com/v1.0/servicePrincipals/$fixtureSp/oauth2PermissionGrants"
        $script:mockApp = $null
        $script:mockSp = $null
        $script:mockGrant = $null
        $script:stage = 'calibration'
        $script:requests = [Collections.Generic.List[object]]::new()
        $script:prompts = [Collections.Generic.List[object]]::new()
        $script:checks = [Collections.Generic.List[object]]::new()
        $script:answers = [Collections.Generic.Queue[string]]::new()
        $calibration = [ordered]@{}

        function Test-Value($Actual, $Expected) {
            return ((ConvertTo-Json -InputObject $Actual -Depth 20 -Compress) -ceq (ConvertTo-Json -InputObject $Expected -Depth 20 -Compress))
        }
        function Assert-Value([string]$Name, $Actual, $Expected) {
            $passed = Test-Value $Actual $Expected
            $script:checks.Add([ordered]@{ name=$Name; actual=$Actual; expected=$Expected; passed=$passed })
            if (-not $passed) { throw "Fixture assertion failed: $Name" }
        }
        function Get-Mutations($Records) {
            foreach ($record in $Records) {
                $vector = @($record.argv)
                if ($record.accepted -and (($vector[0] -ceq 'rest' -and $vector[2] -cin @('PATCH','POST','DELETE')) -or
                    ($vector[0] -ceq 'ad' -and $vector[2] -cin @('create','delete')))) { $record }
            }
        }
        function Read-Host([string]$Prompt) {
            if ($script:answers.Count -eq 0) { throw 'Unexpected prompt; no synthetic answer remains.' }
            $answer = $script:answers.Dequeue()
            $script:prompts.Add([ordered]@{ stage=$script:stage; question=$Prompt; answer=$answer })
            return $answer
        }
        function Start-Sleep([int]$Seconds) {
            if ($Seconds -notin @(2,3)) { throw 'Unexpected helper delay.' }
        }
        function az {
            $vector = @($args)
            $record = [ordered]@{ stage=$script:stage; argv=$vector; accepted=$false }
            $script:requests.Add($record)
            if (Test-Value $vector @('account','show','--only-show-errors','-o','json')) {
                $record.accepted = $true
                return '{"tenantId":"11111111-1111-1111-1111-111111111111","user":{"name":"synthetic-user"}}'
            }
            if (Test-Value $vector @('ad','app','create','--display-name','PAX Cookbook Sign-In','--sign-in-audience','AzureADandPersonalMicrosoftAccount','--only-show-errors','-o','json')) {
                Assert-Value 'create starts with no app' $script:mockApp $null
                $record.accepted = $true
                $script:mockApp = [pscustomobject]@{
                    appId=$fixtureClient; id=$fixtureObject; displayName=$vector[4]; signInAudience=$vector[6]
                    tags=@(); publicClient=@{redirectUris=@()}; requiredResourceAccess=@()
                    api=@{oauth2PermissionScopes=@()}; passwordCredentials=@(); keyCredentials=@()
                    web=@{redirectUris=@()}; spa=@{redirectUris=@()}; identifierUris=@()
                }
                return (ConvertTo-Json -InputObject $script:mockApp -Depth 12 -Compress)
            }
            $patchFile = Join-Path $env:TEMP 'pax_app_patch.json'
            if (Test-Value $vector @('rest','--method','PATCH','--url',"https://graph.microsoft.com/v1.0/applications/$fixtureObject",'--headers','Content-Type=application/json','--body',"@$patchFile",'--only-show-errors')) {
                $body = Get-Content -LiteralPath $patchFile -Raw | ConvertFrom-Json
                Assert-Value 'patch keys' @($body.PSObject.Properties.Name | Sort-Object) @('publicClient','requiredResourceAccess','tags')
                Assert-Value 'patch ownership' @($body.tags) @('pax-cookbook-wam-provisioner')
                Assert-Value 'patch redirects' @($body.publicClient.redirectUris) @("ms-appx-web://microsoft.aad.brokerplugin/$fixtureClient")
                Assert-Value 'patch Graph resource count' @($body.requiredResourceAccess).Count 1
                Assert-Value 'patch Graph resource' $body.requiredResourceAccess[0].resourceAppId $fixtureGraphApp
                Assert-Value 'patch Graph access count' @($body.requiredResourceAccess[0].resourceAccess).Count 1
                Assert-Value 'patch User.Read' $body.requiredResourceAccess[0].resourceAccess[0].id 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'
                Assert-Value 'patch delegated only' $body.requiredResourceAccess[0].resourceAccess[0].type 'Scope'
                $record.accepted = $true
                $script:mockApp.tags = $body.tags
                $script:mockApp.publicClient = $body.publicClient
                $script:mockApp.requiredResourceAccess = $body.requiredResourceAccess
                return '{}'
            }
            if (Test-Value $vector @('ad','sp','create','--id',$fixtureClient,'--only-show-errors','-o','json')) {
                Assert-Value 'SP requires app' ($null -ne $script:mockApp) $true
                $record.accepted = $true
                $script:mockSp = [pscustomobject]@{id=$fixtureSp; appId=$fixtureClient}
                return (ConvertTo-Json -InputObject $script:mockSp -Compress)
            }
            if (Test-Value $vector @('ad','sp','show','--id',$fixtureClient,'--only-show-errors','-o','json')) {
                $record.accepted = $true
                return (ConvertTo-Json -InputObject $script:mockSp -Compress)
            }
            if (Test-Value $vector @('ad','sp','show','--id',$fixtureGraphApp,'--only-show-errors','-o','json')) {
                $record.accepted = $true
                return (ConvertTo-Json -InputObject @{id=$fixtureGraphSp; appId=$fixtureGraphApp} -Compress)
            }
            if ((Test-Value $vector @('ad','app','show','--id',$fixtureClient,'--only-show-errors','-o','json')) -or
                (Test-Value $vector @('ad','app','show','--id',$fixtureObject,'--only-show-errors','-o','json'))) {
                $record.accepted = $true
                return (ConvertTo-Json -InputObject $script:mockApp -Depth 12 -Compress)
            }
            if (Test-Value $vector @('ad','app','list','--all','--only-show-errors','-o','json')) {
                $record.accepted = $true
                $apps = @(); if ($null -ne $script:mockApp) { $apps = @($script:mockApp) }
                return (ConvertTo-Json -InputObject $apps -Depth 12 -Compress)
            }
            if (Test-Value $vector @('rest','--method','GET','--url',$grantUrl,'--only-show-errors','-o','json')) {
                $record.accepted = $true
                $grants = @(); if ($null -ne $script:mockGrant) { $grants = @($script:mockGrant) }
                return (ConvertTo-Json -InputObject @{value=$grants} -Depth 8 -Compress)
            }
            $grantFile = Join-Path $env:TEMP 'pax_grant.json'
            if (Test-Value $vector @('rest','--method','POST','--url','https://graph.microsoft.com/v1.0/oauth2PermissionGrants','--headers','Content-Type=application/json','--body',"@$grantFile",'--only-show-errors')) {
                $body = Get-Content -LiteralPath $grantFile -Raw | ConvertFrom-Json
                Assert-Value 'grant exact keys' @($body.PSObject.Properties.Name | Sort-Object) @('clientId','consentType','resourceId','scope')
                Assert-Value 'grant client' $body.clientId $fixtureSp
                Assert-Value 'grant audience' $body.consentType 'AllPrincipals'
                Assert-Value 'grant resource' $body.resourceId $fixtureGraphSp
                Assert-Value 'grant scope' $body.scope 'User.Read'
                Assert-Value 'grant requires SP' ($null -ne $script:mockSp) $true
                $record.accepted = $true
                $body | Add-Member -NotePropertyName id -NotePropertyValue $fixtureGrant
                $script:mockGrant = $body
                return (ConvertTo-Json -InputObject $body -Compress)
            }
            if (Test-Value $vector @('rest','--method','DELETE','--url',"https://graph.microsoft.com/v1.0/oauth2PermissionGrants/$fixtureGrant",'--only-show-errors')) {
                Assert-Value 'delete requires grant' ($null -ne $script:mockGrant) $true
                $record.accepted = $true
                $script:mockGrant = $null
                return '{}'
            }
            if (Test-Value $vector @('ad','app','delete','--id',$fixtureClient,'--only-show-errors')) {
                Assert-Value 'delete requires app' ($null -ne $script:mockApp) $true
                Assert-Value 'grant deleted before app' $script:mockGrant $null
                $record.accepted = $true
                $script:mockApp = $null
                $script:mockSp = $null
                return
            }
            throw 'FAKE_AZ_REFUSED'
        }

        try {
            $calibration.universe = 'Only structured requests, mock state, helper JSON files and parser states from this isolated fixture; no host or tenant census.'
            $calibration.equalityPositive = Test-Value @('GET','known') @('GET','known')
            $calibration.equalityNegative = Test-Value @('GET','unknown') @('GET','known')
            $positive = @{ accepted=$true; argv=@('ad','app','create') }
            $negative = @{ accepted=$true; argv=@('account','show') }
            $calibration.mutationPositive = @(Get-Mutations @($positive)).Count
            $calibration.mutationNegative = @(Get-Mutations @($negative)).Count
            $calibration.filePositive = Test-Path -LiteralPath $PSCommandPath
            $calibration.fileNegative = Test-Path -LiteralPath (Join-Path $fixtureRoot 'absent-control.json')
            Assert-Value 'equality calibration' @($calibration.equalityPositive,$calibration.equalityNegative) @($true,$false)
            Assert-Value 'mutation calibration' @($calibration.mutationPositive,$calibration.mutationNegative) @(1,0)
            Assert-Value 'file calibration' @($calibration.filePositive,$calibration.fileNegative) @($true,$false)
            Assert-Value 'az resolution' (Get-Command az).CommandType.ToString() 'Function'
            $calibration.correctGet = az rest --method GET --url $grantUrl --only-show-errors -o json | ConvertFrom-Json
            Assert-Value 'known GET control' @($calibration.correctGet.value) @()
            $calibration.refusals = @()
            foreach ($control in @(
                @{name='unknown-method'; vector=@('rest','--method','PUT','--url',$grantUrl,'--only-show-errors','-o','json')},
                @{name='unknown-url'; vector=@('rest','--method','GET','--url','https://graph.microsoft.com/v1.0/unknown','--only-show-errors','-o','json')},
                @{name='unknown-command'; vector=@('login')}
            )) {
                $refused = $false
                try { $controlVector = $control.vector; az @controlVector | Out-Null }
                catch { if ($_.Exception.Message -cne 'FAKE_AZ_REFUSED') { throw }; $refused = $true }
                $calibration.refusals += @{name=$control.name; refused=$refused}
                Assert-Value $control.name $refused $true
            }
            $script:stage = 'provision'
            if ($Scenario -eq 'creation-no') { $script:answers.Enqueue('') }
            else {
                $script:answers.Enqueue('yes')
                $script:answers.Enqueue($(if ($Scenario -eq 'accepted') { 'yes' } else { '' }))
            }
            $provisionPath = Join-Path $fixtureRoot 'provision.json'
            . $helperPath -Action Provision -DisplayNamePrefix 'PAX Cookbook Sign-In' -SetupResultPath $provisionPath -Confirmed:($Scenario -eq 'consent-no-confirmed')
            Assert-Value 'creation prompt default No' $script:prompts[0].question 'Proceed with creating these objects? [y/N]'
            Assert-Value 'answers consumed' $script:answers.Count 0
            if ($Scenario -eq 'creation-no') {
                Assert-Value 'creation cancelled mutations' @(Get-Mutations $script:requests).Count 0
                Assert-Value 'creation cancelled output absent' (Test-Path -LiteralPath $provisionPath) $false
                Assert-Value 'creation cancelled app absent' $script:mockApp $null
                return
            }
            Assert-Value 'consent prompt default No, including Confirmed' $script:prompts[1].question 'Grant tenant-wide consent for User.Read now? [y/N]'
            Assert-Value 'both prompts consumed' $script:prompts.Count 2
            Assert-Value 'consent matches answer' ($null -ne $script:mockGrant) ($Scenario -eq 'accepted')
            $script:stage = 'verify'
            . $helperPath -Action Verify -DisplayNamePrefix 'PAX Cookbook Sign-In' -SetupResultPath (Join-Path $fixtureRoot 'verified.json')
            Assert-Value 'Verify read only' @(Get-Mutations @($script:requests | Where-Object stage -eq 'verify')).Count 0
            if ($Scenario -ne 'accepted') { return }
            $script:stage = 'plan'
            . $helperPath -Action Deprovision -PlanOnly -DisplayNamePrefix 'PAX Cookbook Sign-In' -SetupResultPath (Join-Path $fixtureRoot 'plan.json')
            Assert-Value 'PlanOnly read only' @(Get-Mutations @($script:requests | Where-Object stage -eq 'plan')).Count 0
            Assert-Value 'PlanOnly no prompt' $script:prompts.Count 2
            $script:stage = 'cancel-delete'
            $script:answers.Enqueue('')
            . $helperPath -Action Deprovision -DisplayNamePrefix 'PAX Cookbook Sign-In' -SetupResultPath (Join-Path $fixtureRoot 'cancel-delete.json')
            Assert-Value 'cancel delete read only' @(Get-Mutations @($script:requests | Where-Object stage -eq 'cancel-delete')).Count 0
            Assert-Value 'cancel delete output absent' (Test-Path -LiteralPath (Join-Path $fixtureRoot 'cancel-delete.json')) $false
            Assert-Value 'cancel delete app retained' ($null -ne $script:mockApp) $true
            Assert-Value 'cancel delete grant retained' ($null -ne $script:mockGrant) $true
            $script:stage = 'delete'
            $script:answers.Enqueue('yes')
            . $helperPath -Action Deprovision -DisplayNamePrefix 'PAX Cookbook Sign-In' -SetupResultPath (Join-Path $fixtureRoot 'deleted.json')
            $deletions = @(Get-Mutations @($script:requests | Where-Object stage -eq 'delete'))
            Assert-Value 'exact delete sequence' @($deletions | ForEach-Object { ,$_.argv }) @(
                @('rest','--method','DELETE','--url',"https://graph.microsoft.com/v1.0/oauth2PermissionGrants/$fixtureGrant",'--only-show-errors'),
                @('ad','app','delete','--id',$fixtureClient,'--only-show-errors')
            )
            Assert-Value 'deletion prompts default No' @($script:prompts[2].question,$script:prompts[3].question) @('Delete the registrations listed above? [y/N]','Delete the registrations listed above? [y/N]')
            Assert-Value 'delete answers consumed' $script:answers.Count 0
            Assert-Value 'final synthetic state absent' @($script:mockApp,$script:mockSp,$script:mockGrant) @($null,$null,$null)
            $deleted = Get-Content -LiteralPath (Join-Path $fixtureRoot 'deleted.json') -Raw | ConvertFrom-Json
            Assert-Value 'helper reports absence' $deleted.absenceVerified $true
        }
        finally {
            [ordered]@{ scenario=$Scenario; calibration=$calibration; requests=@($script:requests.ToArray()); prompts=@($script:prompts.ToArray()); checks=@($script:checks.ToArray()) } |
                ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'fixture-evidence.json') -Encoding utf8
        }
        """;
}
