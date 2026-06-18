using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Hashing;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Phase 11 — single-EXE installer E2E. Uses the artifact produced by
// _temp/phase_11_single_exe_setup_payload_bundle/build_single_exe_setup.ps1
// at artifacts\phase11\PAXCookbookSetup.exe.
//
// All operations target temp folders. Real %LOCALAPPDATA%\PAXCookbook
// is never touched. PAXCOOKBOOK_TEST_NO_SHELL=1 suppresses real Start
// Menu / HKCU writes.
//
// Artifact-missing policy:
//   * In a normal `dotnet test` run, tests skip silently if the artifact
//     is not present (the developer hasn't run the build script yet).
//   * When PAX_PHASE11_REQUIRE_ARTIFACT=1 is set (the verifier sets
//     this), missing artifact is a HARD FAILURE — the verifier builds
//     the artifact first and therefore expects every test to actually
//     run. This closes the "verifier reports green because tests
//     skipped" loophole.
public class Phase11SingleExeE2ETests
{
    public const string RequireArtifactEnvVar = "PAX_PHASE11_REQUIRE_ARTIFACT";

    private static string RepoRoot() => Phase5Fixture.RepoRoot();
    private static string ArtifactExe()
        => Path.Combine(RepoRoot(), "artifacts", "phase11", "PAXCookbookSetup.exe");

    // Returns the artifact path if it exists; otherwise either throws
    // (verifier mode) or returns null so the caller can skip silently.
    private static string? RequireArtifact()
    {
        var exe = ArtifactExe();
        if (File.Exists(exe)) return exe;
        if (string.Equals(Environment.GetEnvironmentVariable(RequireArtifactEnvVar),
                          "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"verifier mode ({RequireArtifactEnvVar}=1) requires artifact at {exe} " +
                "but it does not exist. Run build_single_exe_setup.ps1 first.");
        }
        return null;
    }

    [Fact]
    public void SingleExe_Install_NoPayloadRoot_Succeeds()
    {
        var exe = RequireArtifact();
        if (exe is null) return;
        var (repo, rootExeBefore) = SnapshotRoot();
        var installRoot = NewInstallRoot();
        try
        {
            var p = RunSetup(exe, $"install --install-root \"{installRoot}\"");
            Assert.True(p.ExitCode == SetupExitCodes.Ok,
                $"install failed exit={p.ExitCode}\nstdout:\n{p.Stdout}\nstderr:\n{p.Stderr}");
            Assert.True(File.Exists(Path.Combine(installRoot, "install-state.json")));
            Assert.True(File.Exists(Path.Combine(installRoot, "App", "bin", "PAXCookbook.exe")));
            Assert.True(File.Exists(Path.Combine(installRoot, "Setup", "PAXCookbookSetup.exe")));
            // Setup log should contain a payload-resolved record with origin=embedded.
            var logs = Path.Combine(installRoot, "Logs", "Setup");
            if (Directory.Exists(logs))
            {
                var all = string.Concat(Directory.GetFiles(logs, "*.log")
                    .Select(File.ReadAllText));
                Assert.Contains("payload-resolved", all);
                Assert.Contains("embedded", all);
            }
        }
        finally { TryRm(installRoot); AssertRootUntouched(repo, rootExeBefore); }
    }

    [Fact]
    public void SingleExe_Update_NoPayloadRoot_Succeeds()
    {
        var exe = RequireArtifact();
        if (exe is null) return;
        var (repo, rootExeBefore) = SnapshotRoot();
        var installRoot = NewInstallRoot();
        try
        {
            var p1 = RunSetup(exe, $"install --install-root \"{installRoot}\"");
            Assert.True(p1.ExitCode == SetupExitCodes.Ok, $"install: {p1.Stderr}");
            var p2 = RunSetup(exe, $"update --install-root \"{installRoot}\" --reinstall-same-version");
            Assert.True(p2.ExitCode == SetupExitCodes.Ok,
                $"update failed exit={p2.ExitCode}\nstdout:\n{p2.Stdout}\nstderr:\n{p2.Stderr}");
            Assert.True(File.Exists(Path.Combine(installRoot, "App", "bin", "PAXCookbook.exe")));
        }
        finally { TryRm(installRoot); AssertRootUntouched(repo, rootExeBefore); }
    }

    [Fact]
    public void SingleExe_Repair_NoPayloadRoot_Succeeds()
    {
        var exe = RequireArtifact();
        if (exe is null) return;
        var (repo, rootExeBefore) = SnapshotRoot();
        var installRoot = NewInstallRoot();
        try
        {
            var p1 = RunSetup(exe, $"install --install-root \"{installRoot}\"");
            Assert.True(p1.ExitCode == SetupExitCodes.Ok, $"install: {p1.Stderr}");
            // Corrupt the installed app exe by truncating, then repair.
            var appExe = Path.Combine(installRoot, "App", "bin", "PAXCookbook.exe");
            File.WriteAllBytes(appExe, new byte[] { 0 });
            var p3 = RunSetup(exe, $"repair --install-root \"{installRoot}\" --force");
            Assert.True(p3.ExitCode == SetupExitCodes.Ok,
                $"repair failed exit={p3.ExitCode}\nstdout:\n{p3.Stdout}\nstderr:\n{p3.Stderr}");
            Assert.True(new FileInfo(appExe).Length > 1024,
                "repair must restore the truncated app exe");
        }
        finally { TryRm(installRoot); AssertRootUntouched(repo, rootExeBefore); }
    }

    [Fact]
    public void SingleExe_InstalledSetupUninstall_Succeeds()
    {
        var exe = RequireArtifact();
        if (exe is null) return;
        var (repo, rootExeBefore) = SnapshotRoot();
        var installRoot = NewInstallRoot();
        try
        {
            var p1 = RunSetup(exe, $"install --install-root \"{installRoot}\"");
            Assert.True(p1.ExitCode == SetupExitCodes.Ok, $"install: {p1.Stderr}");
            var installedSetup = Path.Combine(installRoot, "Setup", "PAXCookbookSetup.exe");
            Assert.True(File.Exists(installedSetup));
            var p2 = RunSetup(installedSetup, $"uninstall --install-root \"{installRoot}\"");
            Assert.True(p2.ExitCode == SetupExitCodes.Ok,
                $"uninstall failed exit={p2.ExitCode}\nstdout:\n{p2.Stdout}\nstderr:\n{p2.Stderr}");
            // Poll because temp-copy handoff finishes asynchronously.
            PollUntil(() => !File.Exists(installedSetup), 30_000);
            Assert.False(File.Exists(installedSetup));
        }
        finally { TryRm(installRoot); AssertRootUntouched(repo, rootExeBefore); }
    }

    // Functional smoke: after the single-EXE installer extracts and
    // installs into a sandbox install root, the installed product must
    // contain ALL runtime assets needed to start the broker and serve
    // the web UI — broker scripts, web surface, SQLite, resources,
    // templates, launcher PSM1, installer .ps1, and VERSION.json — AND
    // the App exe must actually run end-to-end in --install-root mode
    // and emit a status JSON that reports `installed=true` plus the
    // sandbox install root. This is the check that Phase 11's previous
    // "fake green" reporting failed: the manifest was a partial bin-only
    // payload, so the installed tree was missing the runtime entirely.
    [Fact]
    public void SingleExe_InstalledTree_HasFullRuntimeAssetsAndStatusRuns()
    {
        var exe = RequireArtifact();
        if (exe is null) return;
        var (repo, rootExeBefore) = SnapshotRoot();
        var installRoot = NewInstallRoot();
        // Snapshot Start Menu BEFORE install so we can distinguish
        // "test created the folder" from "folder pre-existed from a
        // prior real install on this user account". The assertion at
        // the end only fails on a NEW creation.
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs", "PAX Cookbook");
        var startMenuExistedBefore = Directory.Exists(startMenu);
        try
        {
            var p1 = RunSetup(exe, $"install --install-root \"{installRoot}\"");
            Assert.True(p1.ExitCode == SetupExitCodes.Ok,
                $"install failed exit={p1.ExitCode}\nstdout:\n{p1.Stdout}\nstderr:\n{p1.Stderr}");

            // --- Required runtime files (must exist as actual files) ---
            string[] requiredFiles =
            {
                Path.Combine(installRoot, "install-state.json"),
                Path.Combine(installRoot, "App", "bin", "PAXCookbook.exe"),
                Path.Combine(installRoot, "App", "broker", "Start-Broker.ps1"),
                Path.Combine(installRoot, "App", "web", "index.html"),
                Path.Combine(installRoot, "App", "resources", "manifest.json"),
                Path.Combine(installRoot, "App", "resources", "pax",
                             "PAX_Purview_Audit_Log_Processor.ps1"),
                Path.Combine(installRoot, "App", "VERSION.json"),
                Path.Combine(installRoot, "App", "launcher", "RuntimeDiscovery.psm1"),
                Path.Combine(installRoot, "Setup", "PAXCookbookSetup.exe")
            };
            foreach (var f in requiredFiles)
                Assert.True(File.Exists(f), $"installed runtime file missing: {f}");

            // --- Required runtime dirs (must have ≥1 file) ---
            string[] requiredDirs =
            {
                Path.Combine(installRoot, "App", "lib", "sqlite"),
                Path.Combine(installRoot, "App", "templates")
            };
            foreach (var d in requiredDirs)
            {
                Assert.True(Directory.Exists(d), $"installed runtime dir missing: {d}");
                Assert.True(Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any(),
                    $"installed runtime dir is empty: {d}");
            }

            // --- Functional smoke: App exe runs end-to-end ---
            var appExe = Path.Combine(installRoot, "App", "bin", "PAXCookbook.exe");
            var status = RunSetup(appExe, $"status --install-root \"{installRoot}\"");
            Assert.True(status.ExitCode == 0,
                $"app status failed exit={status.ExitCode}\nstdout:\n{status.Stdout}\nstderr:\n{status.Stderr}");
            Assert.False(string.IsNullOrWhiteSpace(status.Stdout),
                "app status produced no stdout");

            using var doc = JsonDocument.Parse(status.Stdout);
            var root = doc.RootElement;
            Assert.Equal("PAX Cookbook", root.GetProperty("product").GetString());
            Assert.True(root.GetProperty("installed").GetBoolean(),
                "status JSON must report installed=true after sandbox install");
            Assert.True(root.GetProperty("installStatePresent").GetBoolean(),
                "status JSON must report installStatePresent=true after sandbox install");
            var reportedRoot = root.GetProperty("installRoot").GetString() ?? "";
            // Normalize via DirectoryInfo.FullName, which expands DOS 8.3
            // short names to long form on Windows (Path.GetTempPath()
            // often returns the short form, e.g. C:\Users\BMIDDE~1\...,
            // while the App reports the long form).
            string CanonDir(string p) =>
                new DirectoryInfo(p).FullName.TrimEnd(Path.DirectorySeparatorChar);
            Assert.Equal(CanonDir(installRoot), CanonDir(reportedRoot));

            // --- BrokerPaths semantics: broker entry script must be
            // resolvable in the installed layout. Mirrors the resolver's
            // primary check (<installRoot>\App\broker\Start-Broker.ps1).
            var brokerScript = Path.Combine(installRoot, "App", "broker", "Start-Broker.ps1");
            Assert.True(File.Exists(brokerScript),
                $"broker resolver primary path missing: {brokerScript}");
            // --- No real Start Menu writes happened during install ---
            // PAXCOOKBOOK_TEST_NO_SHELL=1 in RunSetup forces no-op shell
            // writers, so the test must NOT have created the Start Menu
            // PAX Cookbook folder. If the folder pre-existed (from a
            // prior real install on this user account), that's fine —
            // the assertion only fires when the test itself created it.
            var startMenuExistsAfter = Directory.Exists(startMenu);
            Assert.True(startMenuExistedBefore || !startMenuExistsAfter,
                $"Start Menu folder was created by PAXCOOKBOOK_TEST_NO_SHELL install " +
                $"(did not pre-exist; appeared after install): {startMenu}");
        }
        finally { TryRm(installRoot); AssertRootUntouched(repo, rootExeBefore); }
    }

    // ---------- helpers ----------

    private static (string repo, string? hash) SnapshotRoot()
    {
        var repo = RepoRoot();
        var rootExe = Path.Combine(repo, "PAXCookbookSetup.exe");
        var h = File.Exists(rootExe) ? Sha256Hash.OfFile(rootExe) : null;
        return (repo, h);
    }

    private static void AssertRootUntouched(string repo, string? before)
    {
        var rootExe = Path.Combine(repo, "PAXCookbookSetup.exe");
        if (before is null)
        {
            Assert.False(File.Exists(rootExe), "test must not create root setup exe");
        }
        else
        {
            Assert.True(File.Exists(rootExe));
            Assert.Equal(before, Sha256Hash.OfFile(rootExe));
        }
        // Real %LOCALAPPDATA%\PAXCookbook is checked elsewhere; we use
        // explicit --install-root for everything so we never touch it.
    }

    private static string NewInstallRoot()
        => Path.Combine(Path.GetTempPath(), "PAX11E2E_" + Guid.NewGuid().ToString("N"));

    private static void TryRm(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }

    private static void PollUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return;
            System.Threading.Thread.Sleep(150);
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
        psi.Environment["PAXCOOKBOOK_TEST_NO_SHELL"] = "1";
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start " + exePath);
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("setup hung"); }
        return new ProcResult(p.ExitCode, so, se);
    }
}
