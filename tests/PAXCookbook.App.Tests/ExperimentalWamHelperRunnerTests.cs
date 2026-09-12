using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S3 Phase 1 — guarded helper runner. Deterministic: exercises the runner
// against generated fake helper scripts (real pwsh 7). No Azure/Graph.
[Collection("WamHelperRunnerSerial")]
public sealed class ExperimentalWamHelperRunnerTests
{
    private static string NewDir()
    {
        string p = Path.Combine(Path.GetTempPath(), "paxwamrun_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    // Writes a fake helper .ps1 accepting the exact args the runner passes.
    private static string WriteHelper(string dir, string bodyBeforeExit, int exitCode = 0)
    {
        string path = Path.Combine(dir, "New-PaxCookbookEntraWamSetup.ps1");
        string script = $@"
param(
  [string]$Action,
  [string]$SetupResultPath,
  [switch]$PlanOnly,
  [Parameter(ValueFromRemainingArguments=$true)]$Rest
)
{bodyBeforeExit}
exit {exitCode}
";
        File.WriteAllText(path, script);
        return path;
    }

    private static bool PwshAvailable() => !string.IsNullOrEmpty(PwshLocator.Resolve());

    [Fact]
    public void Success_WritesResult_ReturnsRawJson()
    {
        if (!PwshAvailable()) return;
        string dir = NewDir();
        try
        {
            string helper = WriteHelper(dir, "'{\"ok\":true}' | Set-Content -LiteralPath $SetupResultPath");
            var runner = new ExperimentalWamHelperRunner(helper, timeout: TimeSpan.FromSeconds(30));
            ExperimentalWamHelperRunResult r = runner.Run(ExperimentalWamHelperOperation.Verify);
            Assert.Equal(ExperimentalWamHelperStatus.Success, r.Status);
            Assert.Contains("\"ok\":true", r.RawJson);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Success_WithMatchingHash_Passes_MismatchFails()
    {
        if (!PwshAvailable()) return;
        string dir = NewDir();
        try
        {
            string helper = WriteHelper(dir, "'{\"ok\":true}' | Set-Content -LiteralPath $SetupResultPath");
            string realHash = ExperimentalWamHelperRunner.ComputeSha256(helper);

            var ok = new ExperimentalWamHelperRunner(helper, expectedSha256: realHash, timeout: TimeSpan.FromSeconds(30));
            Assert.Equal(ExperimentalWamHelperStatus.Success, ok.Run(ExperimentalWamHelperOperation.Verify).Status);

            var bad = new ExperimentalWamHelperRunner(helper, expectedSha256: "00" + realHash.Substring(2), timeout: TimeSpan.FromSeconds(30));
            Assert.Equal(ExperimentalWamHelperStatus.HelperHashMismatch, bad.Run(ExperimentalWamHelperOperation.Verify).Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void HelperMissing_ReturnsHelperMissing()
    {
        if (!PwshAvailable()) return;
        string dir = NewDir();
        try
        {
            var runner = new ExperimentalWamHelperRunner(Path.Combine(dir, "does-not-exist.ps1"));
            Assert.Equal(ExperimentalWamHelperStatus.HelperMissing, runner.Run(ExperimentalWamHelperOperation.Verify).Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PwshMissing_ReturnsPwshMissing()
    {
        string dir = NewDir();
        try
        {
            string helper = WriteHelper(dir, "'{}' | Set-Content -LiteralPath $SetupResultPath");
            var runner = new ExperimentalWamHelperRunner(helper, pwshPathOverride: Path.Combine(dir, "no-pwsh.exe"));
            Assert.Equal(ExperimentalWamHelperStatus.PwshMissing, runner.Run(ExperimentalWamHelperOperation.Verify).Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NonZeroExit_ReturnsNonZeroExit()
    {
        if (!PwshAvailable()) return;
        string dir = NewDir();
        try
        {
            string helper = WriteHelper(dir, "# no result", exitCode: 3);
            var runner = new ExperimentalWamHelperRunner(helper, timeout: TimeSpan.FromSeconds(30));
            ExperimentalWamHelperRunResult r = runner.Run(ExperimentalWamHelperOperation.Verify);
            Assert.Equal(ExperimentalWamHelperStatus.NonZeroExit, r.Status);
            Assert.Equal(3, r.ExitCode);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResultMissing_WhenHelperWritesNothing()
    {
        if (!PwshAvailable()) return;
        string dir = NewDir();
        try
        {
            string helper = WriteHelper(dir, "# writes no result file", exitCode: 0);
            var runner = new ExperimentalWamHelperRunner(helper, timeout: TimeSpan.FromSeconds(30));
            Assert.Equal(ExperimentalWamHelperStatus.ResultMissing, runner.Run(ExperimentalWamHelperOperation.Verify).Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Timeout_KillsHelper_ReturnsTimeout()
    {
        if (!PwshAvailable()) return;
        string dir = NewDir();
        try
        {
            string helper = WriteHelper(dir, "Start-Sleep -Seconds 10; '{}' | Set-Content -LiteralPath $SetupResultPath");
            var runner = new ExperimentalWamHelperRunner(helper, timeout: TimeSpan.FromMilliseconds(800));
            Assert.Equal(ExperimentalWamHelperStatus.Timeout, runner.Run(ExperimentalWamHelperOperation.Verify).Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OversizedResult_ReturnsOversized()
    {
        if (!PwshAvailable()) return;
        string dir = NewDir();
        try
        {
            string helper = WriteHelper(dir, "('x' * 5000) | Set-Content -LiteralPath $SetupResultPath");
            var runner = new ExperimentalWamHelperRunner(helper, maxResultBytes: 1000, timeout: TimeSpan.FromSeconds(30));
            Assert.Equal(ExperimentalWamHelperStatus.OversizedResult, runner.Run(ExperimentalWamHelperOperation.Verify).Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Concurrent_SecondCall_ReturnsBusy()
    {
        if (!PwshAvailable()) return;
        string dir = NewDir();
        try
        {
            string helper = WriteHelper(dir, "Start-Sleep -Seconds 3; '{\"ok\":true}' | Set-Content -LiteralPath $SetupResultPath");
            var runner = new ExperimentalWamHelperRunner(helper, timeout: TimeSpan.FromSeconds(30));

            Task<ExperimentalWamHelperRunResult> first = Task.Run(() => runner.Run(ExperimentalWamHelperOperation.Verify));
            await Task.Delay(500); // let the first acquire the gate + start
            ExperimentalWamHelperRunResult second = runner.Run(ExperimentalWamHelperOperation.Verify);
            Assert.Equal(ExperimentalWamHelperStatus.Busy, second.Status);
            ExperimentalWamHelperRunResult firstResult = await first;
            Assert.Equal(ExperimentalWamHelperStatus.Success, firstResult.Status);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TempDirectory_IsDeletedAfterRun()
    {
        if (!PwshAvailable()) return;
        string dir = NewDir();
        try
        {
            string helper = WriteHelper(dir, "'{\"ok\":true}' | Set-Content -LiteralPath $SetupResultPath");
            var runner = new ExperimentalWamHelperRunner(helper, timeout: TimeSpan.FromSeconds(30));
            runner.Run(ExperimentalWamHelperOperation.Verify);

            // No paxwamhelper_* temp dir should remain.
            string[] leftovers = Directory.GetDirectories(Path.GetTempPath(), "paxwamhelper_*");
            Assert.Empty(leftovers);
        }
        finally { Directory.Delete(dir, true); }
    }
}
