using System.Security.Cryptography;
using PAXCookbook.Shared;
using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Hashing;
using PAXCookbook.Shared.Io;
using PAXCookbookSetup;
using PAXCookbookSetup.Verbs;
using Xunit;

namespace PAXCookbookSetup.Tests;

internal static class TestFixture
{
    public static (string installRoot, string payloadRoot, Manifest manifest, IDisposable scope)
        BuildPayload(string appVersion = "1.0.0", string? extraFileContent = null)
    {
        var baseDir = Path.Combine(Path.GetTempPath(),
            "PAXCookbookSetupTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var installRoot = Path.Combine(baseDir, "install");
        var payloadRoot = Path.Combine(baseDir, "payload");
        Directory.CreateDirectory(payloadRoot);
        Directory.CreateDirectory(Path.Combine(payloadRoot, "App", "bin"));

        var setupBytes = System.Text.Encoding.UTF8.GetBytes("fake-setup-" + appVersion);
        var appBytes = System.Text.Encoding.UTF8.GetBytes("fake-app-" + appVersion);
        File.WriteAllBytes(Path.Combine(payloadRoot, "PAXCookbookSetup.exe"), setupBytes);
        File.WriteAllBytes(Path.Combine(payloadRoot, "App", "bin", "PAXCookbook.exe"), appBytes);

        var files = new List<ManifestFile>();
        if (extraFileContent != null)
        {
            var rel = Path.Combine("App", "resources", "data.txt");
            var full = Path.Combine(payloadRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, extraFileContent);
            files.Add(new ManifestFile
            {
                RelativeInstallPath = rel,
                Sha256 = Sha256Hash.OfFile(full),
                SizeBytes = new FileInfo(full).Length
            });
        }

        var m = new Manifest
        {
            AppVersion = appVersion,
            SetupVersion = appVersion,
            BuildId = "test",
            Payload = new ManifestPayload
            {
                SetupExe = new ManifestSetupExe
                {
                    Name = "PAXCookbookSetup.exe",
                    Sha256 = Sha256Hash.OfBytes(setupBytes),
                    SizeBytes = setupBytes.Length
                },
                AppExe = new ManifestAppExe
                {
                    Name = "PAXCookbook.exe",
                    Sha256 = Sha256Hash.OfBytes(appBytes),
                    SizeBytes = appBytes.Length,
                    RelativeInstallPath = @"App\bin\PAXCookbook.exe"
                },
                Files = files
            }
        };
        File.WriteAllText(Path.Combine(payloadRoot, "manifest.json"),
            ManifestSerializer.Serialize(m));

        return (installRoot, payloadRoot, m, new Scope(baseDir));
    }

    public static SetupLogger NewLogger(string installRoot)
        => new SetupLogger(Path.Combine(installRoot, "Logs", "Setup"));

    public static ParsedArgs Args(string verb, string installRoot, string payloadRoot,
                                  bool force = false, bool reinstall = false,
                                  bool allowDowngrade = false,
                                  bool handoffFromInstalled = false,
                                  string? handoffFolder = null)
        => new ParsedArgs(verb, installRoot, payloadRoot, force, reinstall, allowDowngrade,
                          handoffFromInstalled, handoffFolder, false, false, false, new());

    sealed class Scope : IDisposable
    {
        readonly string _dir;
        public Scope(string d) { _dir = d; }
        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }
    }
}

public class ArgParserTests
{
    [Fact] public void ParsesInstallVerbAndPayload()
    {
        var p = ArgParser.Parse(new[] { "install", "--payload-root", @"C:\p", "--install-root", @"C:\i" });
        Assert.Equal("install", p.Verb);
        Assert.Equal(@"C:\p", p.PayloadRoot);
        Assert.Equal(@"C:\i", p.InstallRootOverride);
        Assert.Empty(p.Errors);
    }
    [Fact] public void DefaultsToHelpWhenEmpty()
        => Assert.Equal("help", ArgParser.Parse(Array.Empty<string>()).Verb);
    [Fact] public void RejectsUnknownVerb()
        => Assert.Contains(ArgParser.Parse(new[] { "frobnicate" }).Errors,
                           e => e.Contains("unknown verb"));
    [Fact] public void RejectsHandoffWithoutFolder()
    {
        var p = ArgParser.Parse(new[] { "install", "--handoff-from-installed" });
        Assert.Contains(p.Errors, e => e.Contains("requires --handoff-folder"));
    }
    [Fact] public void RecognizesForceAndReinstallFlags()
    {
        var p = ArgParser.Parse(new[] { "repair", "--force", "--reinstall-same-version",
                                        "--payload-root", "p" });
        Assert.True(p.Force);
        Assert.True(p.ReinstallSameVersion);
        Assert.True(p.IsSameVersionRepair);
    }
}

public class SafePathTests
{
    [Theory]
    [InlineData(@"foo\bar.txt")] [InlineData("App/bin/PAXCookbook.exe")]
    public void Accepts(string r) => Assert.True(SafePath.IsSafeRelative(r));

    [Theory]
    [InlineData(@"..\evil.txt")] [InlineData("C:\\abs.txt")]
    [InlineData(@"foo\..\..\evil")] [InlineData("")] [InlineData(@"\rooted")]
    [InlineData(@"foo\.\bar")]
    public void Rejects(string r) => Assert.False(SafePath.IsSafeRelative(r));

    [Fact] public void CombineUnderRoot_RejectsTraversal()
    {
        var root = Path.GetTempPath();
        Assert.Throws<ArgumentException>(() => SafePath.CombineUnderRoot(root, @"..\x"));
    }
}

public class ManifestValidatorTests
{
    [Fact] public void Validates_GoodPayload()
    {
        var (_, payload, m, scope) = TestFixture.BuildPayload();
        using (scope)
        {
            var r = ManifestValidator.Validate(m, payload);
            Assert.True(r.Ok, string.Join("; ", r.Errors));
        }
    }

    [Fact] public void Rejects_TamperedAppExe()
    {
        var (_, payload, m, scope) = TestFixture.BuildPayload();
        using (scope)
        {
            File.WriteAllText(Path.Combine(payload, @"App\bin\PAXCookbook.exe"), "tampered");
            var r = ManifestValidator.Validate(m, payload);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains("sha256 mismatch") || e.Contains("size mismatch"));
        }
    }

    [Fact] public void Rejects_PathTraversalInFiles()
    {
        var (_, payload, m, scope) = TestFixture.BuildPayload(extraFileContent: "x");
        using (scope)
        {
            var bad = m with { Payload = m.Payload with
            {
                Files = new() { new ManifestFile
                {
                    RelativeInstallPath = @"..\evil.txt", Sha256 = "X", SizeBytes = 1
                }}
            }};
            var r = ManifestValidator.Validate(bad, payload);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Contains("unsafe"));
        }
    }
}

public class InstallStateStoreTests
{
    [Fact] public void AtomicSaveAndLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PAXISS_" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = new InstallState { AppVersion = "1.2.3", InstallRoot = dir };
            InstallStateStore.Save(dir, s);
            var back = InstallStateStore.Load(dir);
            Assert.Equal("1.2.3", back.AppVersion);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class InstallVerbTests
{
    [Fact] public void InstallWritesStateAndFiles()
    {
        var (installRoot, payloadRoot, m, scope) = TestFixture.BuildPayload();
        using (scope)
        {
            var log = TestFixture.NewLogger(installRoot);
            var rc = InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                                     m, payloadRoot, installRoot, log);
            Assert.Equal(SetupExitCodes.Ok, rc);
            Assert.True(File.Exists(Path.Combine(installRoot, @"App\bin\PAXCookbook.exe")));
            Assert.True(File.Exists(Path.Combine(installRoot, @"Setup\PAXCookbookSetup.exe")));
            var s = InstallStateStore.Load(installRoot);
            Assert.Equal(m.AppVersion, s.AppVersion);
            Assert.Equal("install", s.LastOperation.Kind);
            Assert.Equal("ok", s.LastOperation.Status);
        }
    }

    [Fact] public void InstallWritesWebView2Unknown()
    {
        // Phase 3 has not implemented WebView2 detection. The persisted
        // status must be unknown / not-evaluated, not a false "missing".
        var (installRoot, payloadRoot, m, scope) = TestFixture.BuildPayload();
        using (scope)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                            m, payloadRoot, installRoot, log);
            var s = InstallStateStore.Load(installRoot);
            Assert.Equal("unknown", s.WebView2RuntimeStatus.Status);
            Assert.Null(s.WebView2RuntimeStatus.Present);
            Assert.Empty(s.WebView2RuntimeStatus.Sources);
            Assert.Null(s.WebView2RuntimeStatus.Pv);
        }
    }
}

public class UpdateVerbTests
{
    [Fact] public void UpdateReplacesAndSnapshots()
    {
        var (installRoot, p1, m1, scope1) = TestFixture.BuildPayload("1.0.0");
        using (scope1)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, p1), m1, p1, installRoot, log);

            var (_, p2, m2, scope2) = TestFixture.BuildPayload("1.1.0");
            using (scope2)
            {
                var rc = UpdateVerb.Run(TestFixture.Args("update", installRoot, p2),
                                        m2, p2, installRoot, log);
                Assert.Equal(SetupExitCodes.Ok, rc);
                var s = InstallStateStore.Load(installRoot);
                Assert.Equal("1.1.0", s.AppVersion);
                Assert.NotNull(s.PreviousVersions);
                Assert.Single(s.PreviousVersions!);
                Assert.Equal("1.0.0", s.PreviousVersions![0].AppVersion);
                Assert.True(Directory.Exists(Path.Combine(installRoot, "PreviousVersions")));
            }
        }
    }

    [Fact] public void SameVersionNoOpWithoutForce()
    {
        var (installRoot, p1, m1, scope1) = TestFixture.BuildPayload("1.0.0");
        using (scope1)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, p1), m1, p1, installRoot, log);
            var rc = UpdateVerb.Run(TestFixture.Args("update", installRoot, p1),
                                    m1, p1, installRoot, log);
            Assert.Equal(SetupExitCodes.Ok, rc);
            var s = InstallStateStore.Load(installRoot);
            Assert.Null(s.PreviousVersions);
        }
    }

    [Fact] public void SameVersionForceTriggersRepair()
    {
        var (installRoot, p1, m1, scope1) = TestFixture.BuildPayload("1.0.0");
        using (scope1)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, p1), m1, p1, installRoot, log);
            var rc = UpdateVerb.Run(TestFixture.Args("update", installRoot, p1, force: true),
                                    m1, p1, installRoot, log);
            Assert.Equal(SetupExitCodes.Ok, rc);
            var s = InstallStateStore.Load(installRoot);
            Assert.NotNull(s.PreviousVersions);
            Assert.Equal("repair", s.PreviousVersions![0].Reason);
        }
    }

    [Fact] public void DowngradeBlockedWithoutFlag()
    {
        var (installRoot, p1, m1, scope1) = TestFixture.BuildPayload("2.0.0");
        using (scope1)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, p1), m1, p1, installRoot, log);
            var (_, p2, m2, scope2) = TestFixture.BuildPayload("1.0.0");
            using (scope2)
            {
                var rc = UpdateVerb.Run(TestFixture.Args("update", installRoot, p2),
                                        m2, p2, installRoot, log);
                Assert.Equal(SetupExitCodes.DowngradeBlocked, rc);
            }
        }
    }
}

public class RollbackTests
{
    sealed class FailingOps : IPayloadOperations
    {
        public void Copy(Manifest m, string p, string i, string a)
            => PayloadCopier.Copy(m, p, i, a);
        public void VerifyInstalled(Manifest m, string i)
            => throw new IOException("injected verify failure");
        public void WritePayloadCache(string payloadRoot, string cacheRoot)
        {
            // Unreachable: this stub fails at VerifyInstalled before
            // InstallVerb's payload-cache write step is invoked. No-op
            // keeps the IPayloadOperations contract satisfied without
            // depending on the cache writer's internal type.
        }
    }

    [Fact] public void ReplaceFailure_RestoresSnapshot()
    {
        var (installRoot, p1, m1, scope1) = TestFixture.BuildPayload("1.0.0");
        using (scope1)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, p1), m1, p1, installRoot, log);
            var (_, p2, m2, scope2) = TestFixture.BuildPayload("1.1.0");
            using (scope2)
            {
                var rc = UpdateVerb.Run(TestFixture.Args("update", installRoot, p2),
                                        m2, p2, installRoot, log,
                                        isRepair: false, payloadOps: new FailingOps());
                Assert.Equal(SetupExitCodes.RollbackPerformed, rc);
                var s = InstallStateStore.Load(installRoot);
                Assert.Equal("1.0.0", s.AppVersion);
                Assert.Equal("rolled-back", s.LastOperation.Status);
            }
        }
    }
}

public class SelfHandoffTests
{
    [Fact] public void DetectsHandoffRequiredWhenUnderInstallRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "PAXSH_" + Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(root, "Setup", "PAXCookbookSetup.exe");
        Assert.True(SelfHandoff.ShouldHandOff(exe, root, handoffFromInstalled: false));
    }

    [Fact] public void NoHandoffWhenAlreadyHandedOff()
    {
        var root = Path.Combine(Path.GetTempPath(), "PAXSH_" + Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(root, "Setup", "PAXCookbookSetup.exe");
        Assert.False(SelfHandoff.ShouldHandOff(exe, root, handoffFromInstalled: true));
    }

    [Fact] public void NoHandoffWhenOutsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "PAXSH_" + Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(Path.GetTempPath(), "Other", "PAXCookbookSetup.exe");
        Assert.False(SelfHandoff.ShouldHandOff(exe, root, handoffFromInstalled: false));
    }

    [Fact] public void MarkerValidation_RejectsMismatchedFolder()
    {
        var tmp = Path.GetTempPath();
        var fakeFolder = Path.Combine(tmp, "PAXCookbookSetup_AAA");
        var args = new ParsedArgs("install", null, null, false, false, false,
                                  true, fakeFolder, false, false, false, new());
        var runningExe = Path.Combine(tmp, "PAXCookbookSetup_BBB", "PAXCookbookSetup.exe");
        var r = SelfHandoff.ValidateMarkers(runningExe, args, tmp);
        Assert.False(r.Ok);
    }

    [Fact] public void MarkerValidation_RejectsOutsideTemp()
    {
        var folder = Path.Combine(@"C:\NotTemp", "PAXCookbookSetup_XYZ");
        var args = new ParsedArgs("install", null, null, false, false, false,
                                  true, folder, false, false, false, new());
        var runningExe = Path.Combine(folder, "PAXCookbookSetup.exe");
        var r = SelfHandoff.ValidateMarkers(runningExe, args, Path.GetTempPath());
        Assert.False(r.Ok);
        Assert.Contains("under %TEMP%", r.Error);
    }

    [Fact] public void MarkerValidation_AcceptsMatched()
    {
        var tmp = Path.GetTempPath();
        var folder = Path.Combine(tmp, "PAXCookbookSetup_XYZ");
        var args = new ParsedArgs("install", null, null, false, false, false,
                                  true, folder, false, false, false, new());
        var runningExe = Path.Combine(folder, "PAXCookbookSetup.exe");
        var r = SelfHandoff.ValidateMarkers(runningExe, args, tmp);
        Assert.True(r.Ok, r.Error);
    }

    [Fact] public void CopySelfToTemp_VerifiesHash()
    {
        var src = Path.Combine(Path.GetTempPath(), "PAXSRC_" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4, 5 });
        try
        {
            var c = SelfHandoff.CopySelfToTemp(src, Path.GetTempPath());
            try
            {
                Assert.True(File.Exists(c.TempExePath));
                Assert.Equal(Sha256Hash.OfFile(src), c.Sha256);
            }
            finally { Directory.Delete(c.TempFolder, true); }
        }
        finally { File.Delete(src); }
    }

    [Fact] public void BuildHandoffArgs_IncludesMarkers()
    {
        var orig = TestFixture.Args("update", @"C:\install", @"C:\payload",
                                    force: true, allowDowngrade: true);
        var args = SelfHandoff.BuildHandoffArgs(orig, @"C:\Temp\foo", @"C:\install");
        Assert.Equal("update", args[0]);
        Assert.Contains("--install-root", args);
        Assert.Contains("--payload-root", args);
        Assert.Contains("--force", args);
        Assert.Contains("--allow-downgrade", args);
        Assert.Contains("--handoff-from-installed", args);
        Assert.Contains("--handoff-folder", args);
        Assert.Contains(@"C:\Temp\foo", args);
    }

    [Fact] public void HandoffRunner_LiveSpawn_CapturesInvocation()
    {
        // Fake source EXE.
        var srcDir = Path.Combine(Path.GetTempPath(), "PAXFAKE_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        var src = Path.Combine(srcDir, "PAXCookbookSetup.exe");
        File.WriteAllBytes(src, new byte[] { 0xAA, 0xBB, 0xCC });
        try
        {
            var log = TestFixture.NewLogger(srcDir);
            var rec = new RecordingProcessLauncher();
            var orig = TestFixture.Args("install", srcDir, @"C:\payload");
            var r = HandoffRunner.Run(orig, src, srcDir, Path.GetTempPath(), rec, log);
            Assert.Equal(SetupExitCodes.Ok, r.ExitCode);
            Assert.NotNull(rec.Last);
            // WDAC-safe relaunch: the temp Setup copy is run through the signed
            // dotnet.exe host (dotnet.exe "<temp>\PAXCookbookSetup.*" <args>), so
            // the launched process is dotnet.exe and the first argument is the
            // temp copy, not the temp file itself.
            Assert.EndsWith("dotnet.exe", rec.Last!.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(r.TempExePath, rec.Last.Arguments[0]);
            Assert.Contains("--handoff-from-installed", rec.Last.Arguments);
            Assert.Contains("--handoff-folder", rec.Last.Arguments);
            Assert.True(File.Exists(r.TempExePath));
            Assert.Equal(Sha256Hash.OfFile(src), r.Sha256);
            Directory.Delete(r.TempFolder, true);
        }
        finally { try { Directory.Delete(srcDir, true); } catch { } }
    }

    [Fact] public void CleanupTempFolder_SchedulesDeferredOnFailure()
    {
        // Use a non-existent path that triggers the catch via a simulated
        // hold: force exception by passing a path with an open file.
        var tempDir = Path.Combine(Path.GetTempPath(), "PAXCLEAN_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var hold = Path.Combine(tempDir, "locked.bin");
        File.WriteAllText(hold, "x");
        var deferred = new RecordingDeferredDeleter();
        var log = TestFixture.NewLogger(tempDir);
        using (var s = new FileStream(hold, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            SelfHandoff.CleanupTempFolder(tempDir, deferred, log);
        }
        // Either delete succeeded after stream closed (race), or deferred was scheduled.
        if (Directory.Exists(tempDir))
        {
            Assert.NotEmpty(deferred.Scheduled);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact] public void Win32DeferredDeleter_ScheduleReturns()
    {
        var p = Path.Combine(Path.GetTempPath(), "PAXMV_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(p, "x");
        try
        {
            var d = new Win32DeferredDeleter();
            // P/Invoke must not throw; result may be true or false depending on AV/policy.
            var _ = d.ScheduleDeleteOnReboot(p);
        }
        finally { try { File.Delete(p); } catch { } }
    }
}

public class WorkspacePreservationTests
{
    [Fact] public void Install_DoesNotTouchExternalFolder()
    {
        var externalWorkspace = Path.Combine(Path.GetTempPath(), "PAXWS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalWorkspace);
        var sentinel = Path.Combine(externalWorkspace, "user-data.txt");
        File.WriteAllText(sentinel, "untouched");
        var (installRoot, payloadRoot, m, scope) = TestFixture.BuildPayload();
        using (scope)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                            m, payloadRoot, installRoot, log);
            Assert.Equal("untouched", File.ReadAllText(sentinel));
        }
        try { Directory.Delete(externalWorkspace, true); } catch { }
    }
}

public class StatusVerbTests
{
    [Fact] public void NotInstalledStatus()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PAXST_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new SetupLogger(Path.Combine(dir, "Logs", "Setup"));
            var sw = new StringWriter();
            var rc = StatusVerb.Run(dir, log, sw);
            Assert.Equal(0, rc);
            Assert.Contains("not-installed", sw.ToString());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact] public void InstalledStatus()
    {
        var (installRoot, payloadRoot, m, scope) = TestFixture.BuildPayload("3.1.4");
        using (scope)
        {
            var log = TestFixture.NewLogger(installRoot);
            InstallVerb.Run(TestFixture.Args("install", installRoot, payloadRoot),
                            m, payloadRoot, installRoot, log);
            var sw = new StringWriter();
            var rc = StatusVerb.Run(installRoot, log, sw);
            Assert.Equal(0, rc);
            Assert.Contains("3.1.4", sw.ToString());
            Assert.Contains("installed", sw.ToString());
        }
    }
}

// End-to-end smoke: build the real PAXCookbookSetup.exe, copy it into a
// temp install root (simulating "Setup is installed under install root"),
// invoke it with install + payload; the real process must self-handoff
// to a temp copy and exit 0. The temp copy then performs the install.
public class HandoffEndToEndTests
{
    [Fact]
    public void RealSetup_SelfHandsOff_AndInstalls()
    {
        // Locate the built PAXCookbookSetup.exe relative to test binary.
        var here = Path.GetDirectoryName(typeof(HandoffEndToEndTests).Assembly.Location)!;
        // tests\PAXCookbookSetup.Tests\bin\Debug\net8.0-windows ->
        // src\PAXCookbookSetup\bin\Debug\net8.0-windows
        var setupExe = Path.GetFullPath(Path.Combine(here,
            "..", "..", "..", "..", "..",
            "src", "PAXCookbookSetup", "bin", "Debug", "net8.0-windows",
            "PAXCookbookSetup.exe"));
        if (!File.Exists(setupExe))
        {
            // Skip when not yet built; the dotnet test command always builds first
            // so in normal CI this exists.
            return;
        }

        // Defense-in-depth: snapshot HKCU ARP / protocol / Start Menu
        // group state and roll back anything new on dispose, even if
        // an assertion below fails. Primary defense is the
        // PAXCOOKBOOK_TEST_NO_SHELL=1 env var on the child Setup
        // process (set on psi.Environment below); the guard is a
        // second line of defense in case a future code path bypasses
        // TestShellGate.
        using var leakGuard = ShellLeakGuard.Snapshot();

        var (_, payloadRoot, _, scope) = TestFixture.BuildPayload("9.9.9");
        using (scope)
        {
            var installRoot = Path.Combine(Path.GetTempPath(),
                "PAXE2E_" + Guid.NewGuid().ToString("N"));
            var setupDir = Path.Combine(installRoot, "Setup");
            Directory.CreateDirectory(setupDir);
            var installedSetup = Path.Combine(setupDir, "PAXCookbookSetup.exe");
            File.Copy(setupExe, installedSetup);
            // Copy adjacent .dll deps so the framework-dependent EXE can boot.
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(setupExe)!))
            {
                var dst = Path.Combine(setupDir, Path.GetFileName(f));
                if (!File.Exists(dst)) File.Copy(f, dst);
            }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = installedSetup,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                // Suppress real HKCU + Start Menu writes. Production
                // installs never set this; the installed Setup checks
                // TestShellGate.IsActive() and substitutes NoOp writers
                // when the variable is set. Inherited by the
                // self-handoff temp child process.
                psi.Environment["PAXCOOKBOOK_TEST_NO_SHELL"] = "1";
                psi.ArgumentList.Add("install");
                psi.ArgumentList.Add("--install-root"); psi.ArgumentList.Add(installRoot);
                psi.ArgumentList.Add("--payload-root"); psi.ArgumentList.Add(payloadRoot);
                var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit(15000);
                Assert.True(p.HasExited, "original process should exit quickly after handoff");
                Assert.Equal(0, p.ExitCode);
                // Wait briefly for child to finish installing.
                var deadline = DateTime.UtcNow.AddSeconds(20);
                var stateFile = Path.Combine(installRoot, "install-state.json");
                while (!File.Exists(stateFile) && DateTime.UtcNow < deadline)
                    Thread.Sleep(200);
                Assert.True(File.Exists(stateFile),
                    "temp-copy child should have produced install-state.json");
                var s = InstallStateStore.Load(installRoot);
                Assert.Equal("9.9.9", s.AppVersion);
                // Setup log records handoff-launched and handoff-marker-accepted.
                var logDay = DateTime.UtcNow.ToString("yyyyMMdd");
                var logFile = Path.Combine(installRoot, "Logs", "Setup", $"setup-{logDay}.log");
                Assert.True(File.Exists(logFile));
                var lines = File.ReadAllText(logFile);
                Assert.Contains("handoff-launched", lines);
                Assert.Contains("handoff-marker-accepted", lines);
            }
            finally { try { Directory.Delete(installRoot, true); } catch { } }
        }
    }
}
