using System.Diagnostics;

namespace PAXCookbook.App;

// Open an output / log FILE in the user's default application (the "Open log"
// buttons on the homepage Last Bake card and the Bakes detail). The SPA POSTs
// the on-disk path of a bake's managed cook.log; the broker opens it with the
// OS-registered default handler for that file type (Notepad, VS Code, a PDF
// viewer, etc.) via UseShellExecute=true.
//
//   POST /api/v1/open-file  { path: "<file-path>" }
//     -> 200 { ok: true,  opened: "<path>" }
//     -> 400 invalid_path      (missing / blank / relative / quote or control char)
//     -> 404 file_not_found    (not a file on disk)
//     -> 415 unsupported_type  (extension not in the safe-to-open allowlist)
//     -> 500 open_failed       (the shell could not open it)
//
// SECURITY POSTURE (audited):
//   * EXTENSION ALLOWLIST. Only a fixed, closed set of inert, viewer-friendly
//     document types may be opened: .log .txt .csv .json .xml .html .pdf. Every
//     other extension is refused with 415 BEFORE any launch — and crucially
//     every executable / script type (.exe .bat .cmd .ps1 .msi .dll .vbs .js
//     .com .scr .lnk .hta and the like) is NOT on the list, so it can never be
//     opened. Because the list is a closed allowlist (reject-unless-present),
//     no new executable type can slip through.
//   * The path must be ABSOLUTE (rooted) and must EXIST as a FILE
//     (File.Exists). A relative path, a directory, or a non-existent path is
//     refused; nothing is ever created. This blocks traversal-to-nonexistent
//     and directory targets.
//   * The path is rejected if it contains a double-quote or any control
//     character (defense in depth).
//   * UseShellExecute=true hands the already-validated file to the Windows
//     shell, which opens it with the user's REGISTERED default handler for the
//     extension — it is NOT a direct process execution of the file. Combined
//     with the allowlist (no executable / script types), the shell opens the
//     file in a viewer; it never runs it as a program.
//   * Reads NO secret and NO tenant data (the cook.log is already broker-
//     redacted before it is ever written), invokes NO PAX, spawns NO cook, and
//     touches NO engine bytes / install-state / cook / scheduler state. PAX is
//     still run only by the gated Bake flow (constraints 8/9 unaffected).
//   * Gating is enforced upstream exactly like open-path: bearer token, CSRF
//     header, and the broker lock (423 when Locked). It is intentionally NOT on
//     BrokerLock.AllowedWhenLocked, so a POST while the appliance is Locked
//     returns 423 before this model runs. No Windows Hello / WebAuthn step-up —
//     opening a log file in a viewer is a low-stakes UI affordance.
internal static class OpenFileModel
{
    // Closed allowlist of inert, viewer-friendly document extensions. Executable
    // and script types are deliberately absent and can never be opened here.
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".txt", ".csv", ".json", ".xml", ".html", ".pdf",
    };

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
            return (400, new { error = "invalid_path", message = "A valid file path is required." });
        }

        // Require an absolute (rooted) path; a relative path is never accepted.
        // A UNC path (\\server\share\...) is rooted and allowed.
        if (!Path.IsPathRooted(path))
        {
            return (400, new { error = "invalid_path", message = "An absolute file path is required." });
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return (400, new { error = "invalid_path", message = "A valid file path is required." });
        }

        // Extension allowlist — checked before the existence probe so an
        // executable / script path is always refused with 415, never opened.
        string ext = Path.GetExtension(full);
        if (ext.Length == 0 || !AllowedExtensions.Contains(ext))
        {
            return (415, new { error = "unsupported_type", message = "Only log and document files can be opened." });
        }

        if (!File.Exists(full))
        {
            return (404, new { error = "file_not_found", message = "That file no longer exists." });
        }

        try
        {
            // UseShellExecute=true: the Windows shell opens the validated file
            // with the user's registered default app for the extension. The
            // allowlist above guarantees the target is an inert document type,
            // never an executable or script.
            var psi = new ProcessStartInfo
            {
                FileName = full,
                UseShellExecute = true,
            };
            using Process? proc = Process.Start(psi);
            // The launched viewer owns its own lifetime; a non-throwing start is
            // success (the handle may be null when the shell routes to an
            // already-running app instance).
        }
        catch
        {
            return (500, new { error = "open_failed", message = "The file could not be opened." });
        }

        return (200, new { ok = true, opened = full });
    }
}
