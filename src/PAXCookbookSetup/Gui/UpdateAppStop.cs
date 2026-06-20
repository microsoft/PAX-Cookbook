using PAXCookbook.Shared;
using PAXCookbookSetup.Uninstall;

namespace PAXCookbookSetup.Gui;

// Bulletproofs the apply-update copy against "Installation failed (exit code
// 50)" — a locked App\bin file. It does not return until every PAX Cookbook
// process for this install is gone AND the main app DLL is actually writable
// (all file locks released), or the timeouts elapse.
//
// Two independent guarantees layered together, because each has a failure mode:
//   1. Process discovery + kill (RealAppStopper) — but module enumeration can
//      transiently MISS a process, so we RE-SCAN and force-kill in a loop.
//   2. A file-writability probe on App\bin\PAX Cookbook.dll — process-discovery
//      independent: even if a locking process can't be found, we WAIT until the
//      lock is gone before copying. Combined with the app hard-exiting itself
//      when it launches us, this removes the race for good.
internal static class UpdateAppStop
{
    public static void WaitUntilClear(string installRoot, SetupLogger log, Action<string> progress)
    {
        progress("Closing PAX Cookbook\u2026");

        // 1. Graceful stop (HTTP /shutdown) + kill of the snapshot, the same
        //    module-scanning stopper install/uninstall use.
        var stopper = new RealAppStopper();
        var stop = stopper.TryStop(installRoot, 30_000);
        log.Write("update-appstop", fields: new Dictionary<string, object?>
        {
            ["invoked"] = stop.Invoked,
            ["exeFound"] = stop.ExeFound,
            ["exited"] = stop.Exited,
            ["detail"] = stop.Detail,
        });

        // 2. Re-scan and force-kill until nothing remains (~10s). Re-scanning is
        //    the point: a process the first snapshot missed is caught here.
        int killRounds = 0;
        bool processesClear = false;
        for (int i = 0; i < 20; i++)
        {
            if (!stopper.AnyAppProcesses(installRoot)) { processesClear = true; break; }
            stopper.KillAppProcesses(installRoot);
            killRounds++;
            Thread.Sleep(500);
        }

        // 3. The ultimate guarantee: wait until the main app DLL is writable
        //    (every lock released) before the copy runs (~10s).
        string dll = DotNetLaunch.AppDllPath(installRoot);
        bool dllWritable = false;
        for (int i = 0; i < 20; i++)
        {
            if (!File.Exists(dll)) { dllWritable = true; break; }
            try
            {
                using var f = File.Open(dll, FileMode.Open, FileAccess.Write, FileShare.None);
                dllWritable = true;
                break;
            }
            catch (IOException) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) { Thread.Sleep(500); }
        }

        log.Write("update-appstop-clear", fields: new Dictionary<string, object?>
        {
            ["killRounds"] = killRounds,
            ["processesClear"] = processesClear,
            ["dllWritable"] = dllWritable,
            ["dll"] = dll,
        });
    }
}
