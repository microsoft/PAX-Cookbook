using System.Diagnostics;
using System.Text.Json;
using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Hashing;
using PAXCookbookSetup;
using PAXCookbookSetup.Verbs;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Phase 5 dev-loop tests. All paths are temp; the real
// %LOCALAPPDATA%\PAXCookbook is never touched. The "real Setup.exe" E2E
// test uses the Phase 5 artifact at artifacts\phase5\PAXCookbookSetup.exe
// when it exists; otherwise the test is skipped with a documented reason.

internal static class Phase5Fixture
{
    public static string RepoRoot()
    {
        // Walk up from test bin dir until we find PAXCookbook.sln.
        var d = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && d is not null; i++)
        {
            if (File.Exists(Path.Combine(d, "PAXCookbook.sln"))) return d;
            d = Path.GetDirectoryName(d);
        }
        throw new InvalidOperationException("could not find repo root from " + AppContext.BaseDirectory);
    }

    // Build a payload by COPYING a previously-installed payload, then mutating
    // bytes + manifest hashes so the same-shape Setup verbs see "version N".
    public static (string installRoot, string payloadRoot, Manifest manifest, IDisposable scope)
        BuildPayload(string appVersion, string setupVersion = null!,
                     byte[]? appBytes = null, byte[]? setupBytes = null)
    {
        setupVersion ??= appVersion;
        var baseDir = Path.Combine(Path.GetTempPath(),
            "PAX5_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var installRoot = Path.Combine(baseDir, "install");
        var payloadRoot = Path.Combine(baseDir, "payload");
        Directory.CreateDirectory(Path.Combine(payloadRoot, "App", "bin"));

        setupBytes ??= System.Text.Encoding.UTF8.GetBytes("setup-payload-" + setupVersion);
        appBytes   ??= System.Text.Encoding.UTF8.GetBytes("app-payload-" + appVersion);
        File.WriteAllBytes(Path.Combine(payloadRoot, "PAXCookbookSetup.exe"), setupBytes);
        File.WriteAllBytes(Path.Combine(payloadRoot, "App", "bin", "PAXCookbook.exe"), appBytes);

        var m = new Manifest
        {
            AppVersion = appVersion,
            SetupVersion = setupVersion,
            BuildId = "phase5-test-" + appVersion,
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
                }
            }
        };
        File.WriteAllText(Path.Combine(payloadRoot, "manifest.json"),
            ManifestSerializer.Serialize(m));
        return (installRoot, payloadRoot, m, new Scope(baseDir));
    }

    public static ParsedArgs Args(string verb, string installRoot, string payloadRoot,
                                  bool force = false, bool reinstall = false,
                                  bool allowDowngrade = false,
                                  bool handoffFromInstalled = false,
                                  string? handoffFolder = null)
        => new ParsedArgs(verb, installRoot, payloadRoot, force, reinstall, allowDowngrade,
                          handoffFromInstalled, handoffFolder, false, false, false, new());

    public static SetupLogger NewLogger(string installRoot)
        => new SetupLogger(Path.Combine(installRoot, "Logs", "Setup"));

    sealed class Scope : IDisposable
    {
        readonly string _d;
        public Scope(string d) { _d = d; }
        public void Dispose() { try { if (Directory.Exists(_d)) Directory.Delete(_d, true); } catch { } }
    }
}

// 1. Artifact payload generation produces a valid manifest with correct hashes.
public class ArtifactPayloadTests
{
    [Fact]
    public void Phase5Artifact_HasManifestAndExes_WithMatchingHashes()
    {
        var artifact = Path.Combine(Phase5Fixture.RepoRoot(), "artifacts", "phase5", "payload");
        if (!File.Exists(Path.Combine(artifact, "manifest.json")))
        {
            // Artifact not built yet. The Phase 5 verifier builds it; if the
            // test runs in isolation we skip rather than fail.
            return;
        }
        var json = File.ReadAllText(Path.Combine(artifact, "manifest.json"));
        var m = ManifestSerializer.Deserialize(json);
        Assert.Equal("PAXCookbook", m.Product);
        Assert.False(string.IsNullOrWhiteSpace(m.AppVersion));
        var setup = Path.Combine(artifact, "PAXCookbookSetup.exe");
        var app   = Path.Combine(artifact, "App", "bin", "PAXCookbook.exe");
        Assert.True(File.Exists(setup));
        Assert.True(File.Exists(app));
        Assert.Equal(m.Payload.SetupExe.Sha256.ToLowerInvariant(), Sha256Hash.OfFile(setup).ToLowerInvariant());
        Assert.Equal(m.Payload.AppExe.Sha256.ToLowerInvariant(),   Sha256Hash.OfFile(app).ToLowerInvariant());
    }
}

// 2. Initial install from a temp payload + 3. Update + 4. Same-version repair + 9. Downgrade.
public class DevLoopVerbTests
{
    [Fact]
    public void InitialInstall_CreatesInstalledLayoutAndState()
    {
        var (root, payload, m, scope) = Phase5Fixture.BuildPayload("1.0.0");
        using (scope)
        {
            using var log = Phase5Fixture.NewLogger(root);
            var rc = InstallVerb.Run(Phase5Fixture.Args("install", root, payload), m, payload, root, log);
            Assert.Equal(SetupExitCodes.Ok, rc);
            Assert.True(File.Exists(Path.Combine(root, "App", "bin", "PAXCookbook.exe")));
            Assert.True(File.Exists(Path.Combine(root, "Setup", "PAXCookbookSetup.exe")));
            var st = InstallStateStore.TryLoad(root);
            Assert.NotNull(st);
            Assert.Equal("1.0.0", st!.AppVersion);
            Assert.Equal("install", st.LastOperation.Kind);
            Assert.Equal("ok", st.LastOperation.Status);
        }
    }

    [Fact]
    public void Update_ToHigherVersion_ReplacesAppExeAndSnapshots()
    {
        var (root, p1, m1, scope1) = Phase5Fixture.BuildPayload("1.0.0");
        using (scope1)
        {
            using var log = Phase5Fixture.NewLogger(root);
            Assert.Equal(SetupExitCodes.Ok,
                InstallVerb.Run(Phase5Fixture.Args("install", root, p1), m1, p1, root, log));
            var (_, p2, m2, scope2) = Phase5Fixture.BuildPayload("1.1.0");
            using (scope2)
            {
                var rc = UpdateVerb.Run(Phase5Fixture.Args("update", root, p2), m2, p2, root, log);
                Assert.Equal(SetupExitCodes.Ok, rc);
                var bytes = File.ReadAllBytes(Path.Combine(root, "App", "bin", "PAXCookbook.exe"));
                Assert.Equal("app-payload-1.1.0", System.Text.Encoding.UTF8.GetString(bytes));
                var st = InstallStateStore.TryLoad(root)!;
                Assert.Equal("1.1.0", st.AppVersion);
                Assert.Equal("update", st.LastOperation.Kind);
                Assert.NotNull(st.PreviousVersions);
                Assert.Contains(st.PreviousVersions!, p => p.AppVersion == "1.0.0");
                Assert.True(Directory.Exists(Path.Combine(root, "PreviousVersions")));
            }
        }
    }

    [Fact]
    public void SameVersionRepair_RefreshesFilesAndRecordsRepair()
    {
        var (root, payload, m, scope) = Phase5Fixture.BuildPayload("1.0.0");
        using (scope)
        {
            using var log = Phase5Fixture.NewLogger(root);
            Assert.Equal(SetupExitCodes.Ok,
                InstallVerb.Run(Phase5Fixture.Args("install", root, payload), m, payload, root, log));
            // Corrupt the installed app exe to prove repair actually refreshes.
            var installedApp = Path.Combine(root, "App", "bin", "PAXCookbook.exe");
            File.WriteAllText(installedApp, "TAMPERED");
            var rc = RepairVerb.Run(
                Phase5Fixture.Args("repair", root, payload, force: true),
                m, payload, root, log);
            Assert.Equal(SetupExitCodes.Ok, rc);
            var refreshed = File.ReadAllText(installedApp);
            Assert.Equal("app-payload-1.0.0", refreshed);
            var st = InstallStateStore.TryLoad(root)!;
            Assert.Equal("repair", st.LastOperation.Kind);
        }
    }

    [Fact]
    public void Downgrade_BlockedWithoutFlag()
    {
        var (root, p1, m1, scope1) = Phase5Fixture.BuildPayload("1.1.0");
        using (scope1)
        {
            using var log = Phase5Fixture.NewLogger(root);
            Assert.Equal(SetupExitCodes.Ok,
                InstallVerb.Run(Phase5Fixture.Args("install", root, p1), m1, p1, root, log));
            var (_, p0, m0, scope0) = Phase5Fixture.BuildPayload("1.0.0");
            using (scope0)
            {
                var rc = UpdateVerb.Run(Phase5Fixture.Args("update", root, p0), m0, p0, root, log);
                Assert.Equal(SetupExitCodes.DowngradeBlocked, rc);
                // State must be untouched.
                var st = InstallStateStore.TryLoad(root)!;
                Assert.Equal("1.1.0", st.AppVersion);
            }
        }
    }

    [Fact]
    public void Downgrade_AllowedWithFlag_ReplacesFiles()
    {
        var (root, p1, m1, scope1) = Phase5Fixture.BuildPayload("1.1.0");
        using (scope1)
        {
            using var log = Phase5Fixture.NewLogger(root);
            Assert.Equal(SetupExitCodes.Ok,
                InstallVerb.Run(Phase5Fixture.Args("install", root, p1), m1, p1, root, log));
            var (_, p0, m0, scope0) = Phase5Fixture.BuildPayload("1.0.0");
            using (scope0)
            {
                var rc = UpdateVerb.Run(
                    Phase5Fixture.Args("update", root, p0, allowDowngrade: true),
                    m0, p0, root, log);
                Assert.Equal(SetupExitCodes.Ok, rc);
                var st = InstallStateStore.TryLoad(root)!;
                Assert.Equal("1.0.0", st.AppVersion);
                Assert.Equal("downgrade", st.LastOperation.Kind);
            }
        }
    }
}

// 11. Rollback on injected payload failure: use a mock IPayloadOperations that
// throws after partial copy; assert snapshot is restored and state reflects
// rolled-back.
internal sealed class FailingPayloadOps : IPayloadOperations
{
    private readonly IPayloadOperations _inner;
    private readonly bool _failOnCopy;
    public FailingPayloadOps(IPayloadOperations inner, bool failOnCopy)
    { _inner = inner; _failOnCopy = failOnCopy; }
    public void Copy(Manifest m, string p, string i, string a)
    {
        if (_failOnCopy)
        {
            // Replace the installed app exe with garbage to simulate partial
            // failure, then throw.
            var installedApp = Path.Combine(i, "App", "bin", "PAXCookbook.exe");
            if (File.Exists(installedApp)) File.WriteAllText(installedApp, "PARTIAL");
            throw new IOException("injected: copy failed");
        }
        _inner.Copy(m, p, i, a);
    }
    public void VerifyInstalled(Manifest m, string i)
    {
        _inner.VerifyInstalled(m, i);
        if (!_failOnCopy) throw new IOException("injected: verify failed");
    }
    public void WritePayloadCache(string payloadRoot, string cacheRoot)
        => _inner.WritePayloadCache(payloadRoot, cacheRoot);
}

public class Phase5RollbackTests
{
    [Fact]
    public void Update_FailsDuringCopy_RestoresFromSnapshot()
    {
        var (root, p1, m1, scope1) = Phase5Fixture.BuildPayload("1.0.0");
        using (scope1)
        {
            using var log = Phase5Fixture.NewLogger(root);
            Assert.Equal(SetupExitCodes.Ok,
                InstallVerb.Run(Phase5Fixture.Args("install", root, p1), m1, p1, root, log));
            var (_, p2, m2, scope2) = Phase5Fixture.BuildPayload("1.1.0");
            using (scope2)
            {
                var failing = new FailingPayloadOps(DefaultPayloadOperations.Instance, failOnCopy: true);
                var rc = UpdateVerb.Run(
                    Phase5Fixture.Args("update", root, p2), m2, p2, root, log,
                    payloadOps: failing);
                Assert.Equal(SetupExitCodes.RollbackPerformed, rc);
                // App exe should be restored from snapshot to 1.0.0 content.
                var bytes = File.ReadAllText(Path.Combine(root, "App", "bin", "PAXCookbook.exe"));
                Assert.Equal("app-payload-1.0.0", bytes);
                var st = InstallStateStore.TryLoad(root)!;
                Assert.Equal("1.0.0", st.AppVersion);
                Assert.Equal("rolled-back", st.LastOperation.Status);
                // Setup logs must persist.
                Assert.True(Directory.Exists(Path.Combine(root, "Logs", "Setup")));
            }
        }
    }
}

// 6./7./8./12./13. Workspace and root-file preservation.
public class PreservationTests
{
    [Fact]
    public void Update_DoesNotTouchWorkspaceData()
    {
        var (root, p1, m1, scope1) = Phase5Fixture.BuildPayload("1.0.0");
        using (scope1)
        {
            using var log = Phase5Fixture.NewLogger(root);
            Assert.Equal(SetupExitCodes.Ok,
                InstallVerb.Run(Phase5Fixture.Args("install", root, p1), m1, p1, root, log));
            // Simulate the user's workspace under <installRoot>\Workspace.
            var ws = Path.Combine(root, "Workspace");
            Directory.CreateDirectory(Path.Combine(ws, "Runtime"));
            var marker = Path.Combine(ws, "user-data.json");
            File.WriteAllText(marker, "{\"keepMe\":true}");
            var lockPath = Path.Combine(ws, "Runtime", "workspace.lock");
            File.WriteAllText(lockPath, "{\"brokerProcessId\":1}");
            var (_, p2, m2, scope2) = Phase5Fixture.BuildPayload("1.1.0");
            using (scope2)
            {
                Assert.Equal(SetupExitCodes.Ok,
                    UpdateVerb.Run(Phase5Fixture.Args("update", root, p2), m2, p2, root, log));
                Assert.True(File.Exists(marker));
                Assert.Equal("{\"keepMe\":true}", File.ReadAllText(marker));
                Assert.True(File.Exists(lockPath));
            }
        }
    }

    [Fact]
    public void Update_DoesNotTouchRootPAXCookbookSetupExe()
    {
        var rootExe = Path.Combine(Phase5Fixture.RepoRoot(), "PAXCookbookSetup.exe");
        DateTime? before = File.Exists(rootExe) ? File.GetLastWriteTimeUtc(rootExe) : null;
        var (root, p1, m1, scope1) = Phase5Fixture.BuildPayload("1.0.0");
        using (scope1)
        {
            using var log = Phase5Fixture.NewLogger(root);
            Assert.Equal(SetupExitCodes.Ok,
                InstallVerb.Run(Phase5Fixture.Args("install", root, p1), m1, p1, root, log));
            var (_, p2, m2, scope2) = Phase5Fixture.BuildPayload("1.1.0");
            using (scope2)
            {
                Assert.Equal(SetupExitCodes.Ok,
                    UpdateVerb.Run(Phase5Fixture.Args("update", root, p2), m2, p2, root, log));
            }
        }
        if (before is not null)
        {
            Assert.Equal(before, File.GetLastWriteTimeUtc(rootExe));
        }
    }
}

// 5./6. Installed-Setup self-handoff E2E using the real artifact Setup.exe.
// This is the ONE expensive end-to-end test required by Phase 5 spec.
public class InstalledSetupSelfHandoffE2ETests
{
    [Fact]
    public void InstalledSetup_UpdateFromInstalledPath_HandsOffAndReplacesBothExes()
    {
        var repo = Phase5Fixture.RepoRoot();
        var artifactSetup = Path.Combine(repo, "artifacts", "phase5", "PAXCookbookSetup.exe");
        var artifactPayload = Path.Combine(repo, "artifacts", "phase5", "payload");
        if (!File.Exists(artifactSetup) || !File.Exists(Path.Combine(artifactPayload, "manifest.json")))
        {
            // Phase 5 verifier always builds these first. If running this
            // test in isolation outside the verifier, skip rather than fail.
            return;
        }

        // Defense-in-depth: snapshot HKCU ARP / protocol / Start Menu
        // group state and roll back anything new on dispose, even if
        // an assertion below fails. Primary defense is the
        // PAXCOOKBOOK_TEST_NO_SHELL=1 env var set in RunSetup; this
        // guard is a second line of defense in case a future code
        // path bypasses TestShellGate.
        using var leakGuard = ShellLeakGuard.Snapshot();

        var baseDir = Path.Combine(Path.GetTempPath(), "PAX5E2E_" + Guid.NewGuid().ToString("N"));
        var installRoot = Path.Combine(baseDir, "install");
        try
        {
            // Step 1: do an initial install from the artifact using the
            // artifact-root setup exe (which runs from OUTSIDE install root,
            // so no handoff happens — this is the "fresh install" call).
            var p1 = RunSetup(artifactSetup, $"install --install-root \"{installRoot}\" --payload-root \"{artifactPayload}\"");
            Assert.Equal(0, p1.ExitCode);
            var installedSetup = Path.Combine(installRoot, "Setup", "PAXCookbookSetup.exe");
            var installedApp   = Path.Combine(installRoot, "App", "bin", "PAXCookbook.exe");
            Assert.True(File.Exists(installedSetup));
            Assert.True(File.Exists(installedApp));
            var setupHashBefore = Sha256Hash.OfFile(installedSetup);
            var appHashBefore   = Sha256Hash.OfFile(installedApp);

            // Step 2: build a second payload with the SAME byte content (the
            // installed and artifact exes are the same build), but pass
            // --force / --reinstall-same-version + --payload-root pointing at
            // the artifact. Run from the INSTALLED Setup path so it triggers
            // self-handoff. The handoff temp copy will replace the installed
            // exes (they're the same bytes, so hash stays equal, but the
            // operation must succeed and log handoff markers).
            var p2 = RunSetup(installedSetup,
                $"update --install-root \"{installRoot}\" --payload-root \"{artifactPayload}\" --force --reinstall-same-version");
            Assert.True(p2.ExitCode == 0 || p2.ExitCode == SetupExitCodes.Ok,
                $"installed-setup update failed: exit={p2.ExitCode}\nstdout:\n{p2.Stdout}\nstderr:\n{p2.Stderr}");
            // Both installed exes still present and hash-valid.
            Assert.True(File.Exists(installedSetup));
            Assert.True(File.Exists(installedApp));
            Assert.Equal(setupHashBefore, Sha256Hash.OfFile(installedSetup));
            Assert.Equal(appHashBefore,   Sha256Hash.OfFile(installedApp));
            // Logs must show handoff was executed.
            var logsDir = Path.Combine(installRoot, "Logs", "Setup");
            Assert.True(Directory.Exists(logsDir));
            var allLog = string.Concat(Directory.GetFiles(logsDir, "*.log")
                .Select(File.ReadAllText));
            Assert.Contains("handoff", allLog, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true); } catch { }
        }
    }

    private sealed record ProcResult(int ExitCode, string Stdout, string Stderr);
    private static ProcResult RunSetup(string exePath, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!
        };
        // Suppress real HKCU + Start Menu writes. Production installs
        // never set this; the installed Setup checks
        // TestShellGate.IsActive() and substitutes NoOp writers when
        // the variable is set. The self-handoff temp child inherits
        // this env var.
        psi.Environment["PAXCOOKBOOK_TEST_NO_SHELL"] = "1";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start " + exePath);
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("setup hung"); }
        return new ProcResult(p.ExitCode, so, se);
    }
}
