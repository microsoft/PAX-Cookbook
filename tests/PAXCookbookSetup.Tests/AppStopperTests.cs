using System;
using System.IO;
using PAXCookbookSetup.Uninstall;
using Xunit;

namespace PAXCookbookSetup.Tests;

// B1 fix: RealAppStopper stops the native broker via the authenticated
// loopback HTTP shutdown endpoint (POST /api/v1/shutdown) instead of the
// retired "PAX Cookbook.exe stop" command-line verb (the native broker has
// no such verb). These tests cover the deterministic no-broker-running path:
// when nothing is running from this install's app exe, TryStop must report
// "nothing to stop" so the uninstall proceeds rather than aborting.
public class AppStopperTests
{
    [Fact]
    public void TryStop_NoProcessRunning_ReportsNothingToStop()
    {
        // A fresh temp install root: no app exe, no broker.port, no token,
        // and (because the path is unique) no running process whose main
        // module is <root>\App\bin\PAX Cookbook.exe.
        var root = Path.Combine(Path.GetTempPath(),
            "PAXStopperTest_" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(root);
        try
        {
            var result = new RealAppStopper().TryStop(root, timeoutMs: 2000);

            // Invoked=false means there was nothing to stop, so the uninstall
            // stop-failure gate cannot fire.
            Assert.False(result.Invoked);
            Assert.True(result.Exited);
            Assert.False(result.ExeFound);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TryStop_NoProcess_DoesNotTripUninstallStopGate()
    {
        // Mirror the UninstallOperations stop-failure predicate:
        //   stopFailed = Invoked && ExeFound && (!Exited || ExitCode != 0)
        // For the no-process case it must evaluate to false.
        var root = Path.Combine(Path.GetTempPath(),
            "PAXStopperTest_" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(root);
        try
        {
            var stop = new RealAppStopper().TryStop(root, timeoutMs: 2000);
            bool stopFailed = stop.Invoked && stop.ExeFound
                              && (!stop.Exited || (stop.ExitCode ?? -1) != 0);
            Assert.False(stopFailed);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TryStop_StaleLockWithDeadPid_ReportsNothingToStop()
    {
        // A workspace.lock whose recorded brokerProcessId is a now-dead pid (a
        // recycled / stale lock) must NOT be treated as a running app: the
        // supplement path resolves the pid, finds it is not alive, and reports
        // nothing to stop — so the uninstall proceeds and an upgrade is not
        // blocked by a leftover lock from a crashed broker.
        var root = Path.Combine(Path.GetTempPath(),
            "PAXStopperTest_" + Guid.NewGuid().ToString("N").Substring(0, 12));
        var runtime = Path.Combine(root, "Workspace", "Runtime");
        Directory.CreateDirectory(runtime);

        // Deterministically obtain a pid that has already exited.
        int deadPid;
        using (var tmp = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit")
            { CreateNoWindow = true, UseShellExecute = false })!)
        {
            deadPid = tmp.Id;
            tmp.WaitForExit();
        }

        File.WriteAllText(Path.Combine(runtime, "workspace.lock"),
            "{ \"brokerProcessId\": " + deadPid + ", \"brokerPort\": 1 }");
        try
        {
            var result = new RealAppStopper().TryStop(root, timeoutMs: 2000);
            Assert.False(result.Invoked);
            Assert.True(result.Exited);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
