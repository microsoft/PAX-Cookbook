using System.Diagnostics;
using System.Threading;

namespace PAXCookbook.App;

// POST /api/v1/updates/apply — start an in-place update.
//
// This NEVER overwrites app files itself. It hands off to the proven installer:
// it launches the installed Setup's `update` verb and returns. The installer
// then downloads the LATEST payload from the GitHub Release (verified against
// versions.json), stops EVERY PAX Cookbook process (graceful HTTP shutdown, then
// a process-tree kill so locked App\bin files are released), copies the new
// payload, verifies it, and cleans stale binaries — all scoped to App\bin, so
// recipes, bake history, Chef's Keys, and other user data are never touched.
//
// Safety for arbitrary updates: because the installer fully STOPS the running
// app before copying (rather than hot-patching a live process), an update is
// safe no matter how different the new files are. A pre-copy failure (e.g. the
// network is down) leaves the existing install completely intact, so a failed
// update is never destructive. The installer does not auto-relaunch, so the app
// closes to finish and the user reopens it.
//
// WDAC-safe launch model (identical to the auto-start daemon registration in
// AutoStartSettingsModel): the Microsoft-signed dotnet.exe host runs the
// framework-dependent Setup DLL; the unsigned Setup apphost is never invoked.
internal static class UpdateApplyModel
{
    // appRoot is "<installRoot>\App"; the installed Setup DLL lives at
    // "<installRoot>\Setup\PAXCookbookSetup.dll".
    public static (int Status, object Body) Apply(string appRoot)
    {
        string trimmed = appRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? installRoot = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(installRoot))
        {
            return (500, new
            {
                error = "install_root_unresolved",
                message = "Could not resolve the PAX Cookbook install location.",
            });
        }

        string setupDll = Path.Combine(installRoot, "Setup", "PAXCookbookSetup.dll");
        if (!File.Exists(setupDll))
        {
            return (409, new
            {
                error = "updater_unavailable",
                message = "The PAX Cookbook updater is not available on this PC. "
                    + "Reinstall PAX Cookbook to update it.",
            });
        }

        string dotnet = DotNetLaunch.DotNetExePath();
        string cmd = $"\"{dotnet}\" \"{setupDll}\" apply-update";
        LogApply(installRoot, "apply requested; launching updater: " + cmd);

        try
        {
            // Run the apply-update verb: a VISIBLE update wizard that downloads
            // the latest payload, stops every PAX Cookbook process (IAppStopper)
            // and copies it over the install — the proven "install over a running
            // app" path — while showing progress, then ends on a completion
            // screen with Open / Close. (Unlike the old hidden `install --quiet`,
            // the entire update is now visible to the user.)
            //
            // UseShellExecute=true launches the updater THROUGH the shell, so it
            // is re-parented away from this broker. That matters: the installer
            // stops every PAX Cookbook process (a process-tree kill), and if the
            // updater were a child of this broker it would kill itself
            // mid-update. Detaching lets it outlive the broker it is replacing.
            // WindowStyle is left Normal so the wizard window shows; the Setup
            // hides only its own dotnet.exe console at startup, not the window.
            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
                WorkingDirectory = installRoot,
            };
            psi.ArgumentList.Add(setupDll);
            psi.ArgumentList.Add("apply-update");

            Process? proc = Process.Start(psi);
            if (proc is null)
            {
                LogApply(installRoot, "ERROR: Process.Start returned null");
                return (500, new
                {
                    error = "updater_launch_failed",
                    message = "Could not start the PAX Cookbook updater.",
                });
            }

            int? pid = null;
            try { pid = proc.Id; } catch { /* may have already exited */ }
            LogApply(installRoot, $"updater launched OK (pid={pid?.ToString() ?? "unknown"}). "
                + "The updater wizard will download, stop and replace the app, then show a finish screen.");

            // Hard-exit THIS process shortly after the 202 flushes. The broker
            // that served this request loads App\bin\PAX Cookbook.dll and so
            // LOCKS it; the only reliable way to release that lock fast enough
            // for the updater's copy is to terminate ourselves — Environment.Exit
            // tears the process down instantly (no graceful Kestrel/WebView2
            // shutdown that can linger with background threads or a wedged
            // WebView2 dispose). This is the permanent fix for the recurring
            // "exit code 50" locked-file failure. A short delay lets the 202
            // reach the UI first; the detached updater does its own
            // stop-and-wait-until-clear as a backstop (covering the separate
            // window process) and re-registers shortcuts/ARP after copying.
            // Stale broker.port / workspace.lock are handled on the next launch.
            LogApply(installRoot, "scheduling hard self-exit so App\\bin locks release for the updater");
            var exitThread = new Thread(() =>
            {
                try { Thread.Sleep(700); } catch { /* ignore */ }
                Environment.Exit(0);
            })
            {
                IsBackground = true,
                Name = "PAXCookbook.UpdateSelfExit",
            };
            exitThread.Start();

            return (202, new
            {
                ok = true,
                message = "The PAX Cookbook Updater is opening. It will download the update, "
                    + "then close PAX Cookbook to finish.",
            });
        }
        catch (Exception ex)
        {
            LogApply(installRoot, "ERROR: updater launch threw: " + ex.Message);
            return (500, new
            {
                error = "updater_launch_failed",
                message = "Could not start the PAX Cookbook updater.",
                detail = ex.Message,
            });
        }
    }

    // Best-effort append-only log so an update launch can be diagnosed from disk
    // even though the app closes seconds later. Never throws.
    private static void LogApply(string installRoot, string line)
    {
        try
        {
            string dir = Path.Combine(installRoot, "Logs");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.UtcNow.ToString("o");
            File.AppendAllText(
                Path.Combine(dir, "update-apply.log"),
                $"[{stamp}] {line}{Environment.NewLine}");
        }
        catch
        {
            /* logging is best-effort */
        }
    }
}
