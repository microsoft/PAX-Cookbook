using System.Diagnostics;

namespace PAXCookbook.App;

// Open the Windows folder that contains an output file, in File Explorer
// (Item 3D/3E "Open folder"). The SPA's "Open folder" buttons (Last Bake,
// Bakes, etc.) POST the output file (or folder) path; the broker opens File
// Explorer at the containing directory so the user can jump to their exported
// files.
//
//   POST /api/v1/open-path  { path: "<file-or-folder>" }
//     -> 200 { ok: true,  opened: "<directory>" }
//     -> 400 invalid_path    (missing / blank / contains a quote or control char)
//     -> 404 path_not_found  (neither a file nor a directory on disk)
//     -> 500 open_failed     (Explorer could not be launched)
//
// SECURITY POSTURE (audited):
//   * It opens a FOLDER in File Explorer ONLY. It NEVER opens or executes a
//     file: for a file path it resolves the CONTAINING DIRECTORY and opens
//     that; for a directory path it opens the directory. explorer.exe with a
//     folder argument is a viewer — it browses the folder and never runs
//     anything in it. There is NO ShellExecute of a file, so no default-app or
//     executable launch is possible (UseShellExecute=false, explorer.exe is the
//     only program ever started, and only with a single folder argument).
//   * The target must EXIST on disk (File.Exists || Directory.Exists). Opening
//     an existing folder is exactly what the user can already do in Explorer
//     themselves — it is not a privilege escalation, an exfiltration, or an
//     execution primitive. A non-existent path is refused (404) and never
//     created.
//   * The path is rejected if it contains a double-quote or any control
//     character (defense-in-depth on top of ArgumentList, which already passes
//     the directory as a single non-shell-parsed argument).
//   * It reads NO secret and NO tenant data, invokes NO PAX, spawns NO cook,
//     and touches NO engine bytes / install-state / cook / scheduler /
//     notification state. PAX is still run only by the gated Bake flow
//     (constraints 8/9 unaffected).
//   * Gating is enforced upstream exactly like every other /api/v1/* route:
//     bearer token, CSRF header, and the broker lock. It is intentionally NOT
//     on BrokerLock.AllowedWhenLocked, so a POST while the appliance is Locked
//     returns 423 brokerLocked before this model ever runs. There is no Windows
//     Hello / WebAuthn step-up — opening a folder is a low-stakes UI affordance.
internal static class OpenPathModel
{
    public static (int Status, object Body) Handle(object? body)
    {
        Dictionary<string, object?>? dict = JsonModel.AsDict(body);

        string path = string.Empty;
        if (dict is not null && dict.TryGetValue("path", out object? raw))
        {
            path = JsonModel.Str(raw).Trim();
        }

        if (path.Length == 0 || path.IndexOf('"') >= 0 || path.Any(char.IsControl))
        {
            return (400, new { error = "invalid_path", message = "A valid file or folder path is required." });
        }

        string directory;
        try
        {
            string full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                directory = full;
            }
            else if (File.Exists(full))
            {
                directory = Path.GetDirectoryName(full) ?? string.Empty;
            }
            else
            {
                return (404, new { error = "path_not_found", message = "That file or folder no longer exists." });
            }
        }
        catch
        {
            return (400, new { error = "invalid_path", message = "A valid file or folder path is required." });
        }

        if (directory.Length == 0 || directory.IndexOf('"') >= 0 || !Directory.Exists(directory))
        {
            return (404, new { error = "path_not_found", message = "That folder no longer exists." });
        }

        try
        {
            // Launch explorer.exe from the Windows directory by full path so the
            // PATH cannot be hijacked, with the target folder as a single
            // non-shell-parsed argument. explorer.exe browses the folder; it
            // never executes any file in it.
            string explorerExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (!File.Exists(explorerExe))
            {
                explorerExe = "explorer.exe";
            }
            var psi = new ProcessStartInfo
            {
                FileName = explorerExe,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(directory);
            using Process? proc = Process.Start(psi);
            // explorer.exe routes to the already-running shell and the launcher
            // handle may exit immediately; a non-throwing start is success.
        }
        catch
        {
            return (500, new { error = "open_failed", message = "File Explorer could not be opened." });
        }

        return (200, new { ok = true, opened = directory });
    }
}
