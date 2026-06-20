using System.Diagnostics;

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

        try
        {
            // UseShellExecute=true launches the updater THROUGH the shell, so it
            // is re-parented away from this broker. That matters: the updater
            // (via the installer's IAppStopper) stops every PAX Cookbook process,
            // and if the updater were a child of this broker, that process-tree
            // stop would kill the updater itself mid-update. Detaching lets the
            // updater outlive the broker it is replacing. The window is hidden so
            // no console flashes.
            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = installRoot,
            };
            psi.ArgumentList.Add(setupDll);
            psi.ArgumentList.Add("update");
            psi.ArgumentList.Add("--quiet");

            Process? proc = Process.Start(psi);
            LogApply(installRoot, proc is null
                ? "update launch returned null process"
                : $"update launched (pid={proc.Id}): \"{dotnet}\" \"{setupDll}\" update --quiet");
            if (proc is null)
            {
                return (500, new
                {
                    error = "updater_launch_failed",
                    message = "Could not start the PAX Cookbook updater.",
                });
            }

            return (202, new
            {
                ok = true,
                message = "PAX Cookbook is updating. The app will close to finish, "
                    + "then you can reopen it.",
            });
        }
        catch (Exception ex)
        {
            LogApply(installRoot, "update launch FAILED: " + ex.Message);
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
