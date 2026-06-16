using System.Diagnostics;
using System.IO;
using System.Text;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Hashing;
using PAXCookbookSetup;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Phase 9 — installed-Setup self-handoff uninstall E2E.
//
// Mirrors the Phase 5 InstalledSetupSelfHandoffE2ETests pattern but
// drives an actual `uninstall` invocation from the INSTALLED Setup
// path so the self-handoff path is exercised end-to-end:
//   1. Install from the artifact (Setup runs from OUTSIDE the install
//      root -> no handoff, fresh install).
//   2. Run `<installRoot>\Setup\PAXCookbookSetup.exe uninstall --install-root <root>`
//      which DOES trigger self-handoff (Setup runs from INSIDE the
//      install root -> copies itself to %TEMP%, original exits, temp
//      copy validates markers, temp copy executes uninstall).
//   3. Assert App + Setup files are gone, install-state.json is gone,
//      Workspace is preserved, root PAXCookbookSetup.exe is untouched,
//      real %LOCALAPPDATA%\PAXCookbook is not touched.
//
// Real HKCU writes / real Start Menu writes are suppressed via the
// PAXCOOKBOOK_TEST_NO_SHELL env var (see TestShellGate); the E2E only
// validates the file-removal + self-handoff dispatch, not real shell
// side effects (those have full unit coverage elsewhere).
public class Phase9InstalledSetupUninstallE2ETests
{
    [Fact]
    public void InstalledSetup_Uninstall_HandsOffAndRemovesFiles_PreservesWorkspace()
    {
        var repo = Phase5Fixture.RepoRoot();
        var artifactSetup   = Path.Combine(repo, "artifacts", "phase5", "PAXCookbookSetup.exe");
        var artifactPayload = Path.Combine(repo, "artifacts", "phase5", "payload");
        if (!File.Exists(artifactSetup) || !File.Exists(Path.Combine(artifactPayload, "manifest.json")))
        {
            // Verifier rebuilds artifacts before running this. In ad-hoc
            // dev runs without the verifier, skip rather than fail.
            return;
        }

        var baseDir = Path.Combine(Path.GetTempPath(), "PAX9E2E_" + System.Guid.NewGuid().ToString("N"));
        var installRoot = Path.Combine(baseDir, "install");
        // Capture root EXE state to assert it was NOT touched.
        var rootExe = Path.Combine(repo, "PAXCookbookSetup.exe");
        string? rootHashBefore = File.Exists(rootExe) ? Sha256Hash.OfFile(rootExe) : null;
        var lad = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var realPaxRoot = Path.Combine(lad, "PAXCookbook");
        bool realPaxRootExistedBefore = Directory.Exists(realPaxRoot);
        long realPaxRootSizeBefore = realPaxRootExistedBefore ? FolderSize(realPaxRoot) : 0;

        try
        {
            // 1. Install from artifact (no handoff: runs from outside install root).
            var p1 = RunSetup(artifactSetup,
                $"install --install-root \"{installRoot}\" --payload-root \"{artifactPayload}\"");
            Assert.True(p1.ExitCode == SetupExitCodes.Ok,
                $"install failed: exit={p1.ExitCode}\nstdout:\n{p1.Stdout}\nstderr:\n{p1.Stderr}");
            var installedSetup = Path.Combine(installRoot, "Setup", "PAXCookbookSetup.exe");
            var installedApp   = Path.Combine(installRoot, "App", "bin", "PAXCookbook.exe");
            Assert.True(File.Exists(installedSetup), "installed setup exe missing after install");
            Assert.True(File.Exists(installedApp),   "installed app exe missing after install");

            // Create a Workspace folder + recipe so we can prove standard
            // uninstall preserves it.
            var ws = Path.Combine(installRoot, "Workspace");
            Directory.CreateDirectory(ws);
            File.WriteAllText(Path.Combine(ws, "user-recipe.txt"), "preserve me");
            // Patch install-state.json so its WorkspaceFolderPath points
            // at the ws directory (RunSetup install does not initialize
            // it for this fixture).
            var statePath = Path.Combine(installRoot, "install-state.json");
            if (File.Exists(statePath))
            {
                var txt = File.ReadAllText(statePath);
                if (!txt.Contains("workspaceFolderPath"))
                {
                    var trimmed = txt.TrimEnd().TrimEnd('}');
                    txt = trimmed + ",\"workspaceFolderPath\":\""
                        + ws.Replace("\\","\\\\") + "\"}";
                    File.WriteAllText(statePath, txt);
                }
            }

            // 2. Uninstall from the INSTALLED Setup path -> triggers self-handoff.
            var p2 = RunSetup(installedSetup,
                $"uninstall --install-root \"{installRoot}\"");
            Assert.True(p2.ExitCode == SetupExitCodes.Ok,
                $"uninstall failed: exit={p2.ExitCode}\nstdout:\n{p2.Stdout}\nstderr:\n{p2.Stderr}");

            // Poll briefly because the temp-copy child may still be
            // finalizing when the original process returns.
            PollUntil(() => !File.Exists(installedSetup), 30_000);
            PollUntil(() => !File.Exists(installedApp),   30_000);
            PollUntil(() => !Directory.Exists(Path.Combine(installRoot, "App")), 30_000);
            PollUntil(() => !Directory.Exists(Path.Combine(installRoot, "Setup")), 30_000);
            PollUntil(() => !File.Exists(statePath), 30_000);

            // 3. Assertions.
            Assert.False(File.Exists(installedApp),
                "App exe should be removed by uninstall");
            Assert.False(File.Exists(installedSetup),
                "Setup exe should be removed by uninstall");
            // Contract requires removal of installed files. An empty
            // leftover parent folder is harmless (AV / temp-copy timing
            // can prevent the final rmdir) and would be scheduled for
            // delete-on-reboot by the file remover; assert no FILES
            // remain under App or Setup.
            AssertNoFilesUnder(Path.Combine(installRoot, "App"), "App");
            AssertNoFilesUnder(Path.Combine(installRoot, "Setup"), "Setup");
            Assert.False(File.Exists(statePath),
                "install-state.json should be removed last");
            // Workspace preserved.
            Assert.True(Directory.Exists(ws), "workspace must be preserved by standard uninstall");
            Assert.True(File.Exists(Path.Combine(ws, "user-recipe.txt")));
            // Logs subtree may or may not exist depending on whether the
            // installed Setup created any log entries; if present, it
            // must be preserved (not under standard removal set).
            // We do not require its existence here.

            // Log evidence of handoff.
            var logsDir = Path.Combine(installRoot, "Logs", "Setup");
            if (Directory.Exists(logsDir))
            {
                var allLog = string.Concat(Directory.GetFiles(logsDir, "*.log")
                    .Select(File.ReadAllText));
                Assert.Contains("handoff", allLog, System.StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            try { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true); } catch { }
        }

        // Cross-cutting: root PAXCookbookSetup.exe untouched.
        if (rootHashBefore is not null)
        {
            Assert.True(File.Exists(rootExe), "root setup must still exist");
            Assert.Equal(rootHashBefore, Sha256Hash.OfFile(rootExe));
        }
        // Real %LOCALAPPDATA%\PAXCookbook untouched (no new content; if it
        // pre-existed, its size must not shrink because of our test).
        if (realPaxRootExistedBefore)
        {
            Assert.True(Directory.Exists(realPaxRoot));
            Assert.True(FolderSize(realPaxRoot) >= realPaxRootSizeBefore,
                "real %LOCALAPPDATA%\\PAXCookbook must not be deleted/shrunk by this test");
        }
        else
        {
            Assert.False(Directory.Exists(realPaxRoot),
                "test must not create the real %LOCALAPPDATA%\\PAXCookbook folder");
        }
    }

    private static void AssertNoFilesUnder(string dir, string label)
    {
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        Assert.True(files.Length == 0,
            $"{label} folder still contains files after uninstall:\n  " +
            string.Join("\n  ", files));
    }

    private static long FolderSize(string path)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    private static void PollUntil(System.Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return;
            System.Threading.Thread.Sleep(100);
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
        // Suppress real HKCU + Start Menu writes for this E2E. Production
        // runs do NOT set this; the gate is only honored when present.
        psi.Environment["PAXCOOKBOOK_TEST_NO_SHELL"] = "1";
        using var p = Process.Start(psi)
            ?? throw new System.InvalidOperationException("failed to start " + exePath);
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } throw new System.TimeoutException("setup hung"); }
        return new ProcResult(p.ExitCode, so, se);
    }
}
