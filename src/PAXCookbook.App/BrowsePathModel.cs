using System.Diagnostics;
using System.Windows.Forms;

namespace PAXCookbook.App;

// Native file/folder picker for SPA path inputs (Item 7). Shows a Windows
// OpenFileDialog (mode=file) or FolderBrowserDialog (mode=folder) on a
// dedicated STA thread and returns the path the user selected:
//
//   POST /api/v1/browse-path  { mode: "file"|"folder", title?, initialDirectory?, filters? }
//     -> 200 { path: "<chosen>", cancelled: false }   (user picked)
//     -> 200 { path: null,       cancelled: true  }    (user cancelled)
//     -> 400 invalid_mode                              (mode missing / not file|folder)
//     -> 500 browse_failed                             (dialog could not be shown)
//
// This is a UI helper ONLY. It exists so the SPA can offer a native "Browse…"
// button for path fields (e.g. a checkpoint file or an output folder) instead
// of a free-text box. It reads NO secret and NO tenant data, invokes NO PAX,
// spawns NO cook, and touches NO engine bytes / install-state / cook /
// scheduler / notification state — it only shows a common dialog and returns
// the selected path string back to the caller.
//
// Gating is enforced upstream by the central middleware exactly like every
// other /api/v1/* route: bearer token, CSRF header, and the broker lock. It is
// intentionally NOT on BrokerLock.AllowedWhenLocked, so a POST while the
// appliance is Locked returns 423 brokerLocked before this model ever runs.
// There is NO Windows Hello / WebAuthn step-up: returning a path string the
// user picked is a lower-stakes UI affordance than the re-auth-gated cook /
// scheduled-task / Chef's-Key operations, so it follows the same no-re-auth
// gating as the other SPA helper routes.
//
// ASP.NET request handlers run on MTA threadpool threads, but the WinForms
// common dialogs require an STA apartment, so the dialog is shown on a
// dedicated STA thread whose result is captured back into locals. When the
// host window handle is available the dialog is parented to it; in a headless
// context (no main window) it is shown ownerless.
internal static class BrowsePathModel
{
    // Defensive cap on the failure message returned to the SPA so a Win32 /
    // dialog error surfaces a short, single-line reason rather than an
    // unbounded multi-line string. The model touches no secret or tenant data,
    // so this is purely a length guard, not a redaction.
    private const int MaxErrorMessageLength = 400;

    private const string AllFilesFilter = "All files (*.*)|*.*";

    // body is the parsed JSON tree from JsonModel.ReadBodyAsync — a
    // case-insensitive Dictionary<string, object?> or null for an empty /
    // invalid body. Every field is read defensively.
    public static (int Status, object Body) Handle(object? body)
    {
        Dictionary<string, object?>? dict = JsonModel.AsDict(body);

        // mode (required): "file" or "folder".
        string mode = string.Empty;
        if (dict is not null && dict.TryGetValue("mode", out object? modeRaw))
        {
            mode = JsonModel.Str(modeRaw).Trim();
        }

        bool isFolder = string.Equals(mode, "folder", StringComparison.OrdinalIgnoreCase);
        bool isFile = string.Equals(mode, "file", StringComparison.OrdinalIgnoreCase);
        if (!isFolder && !isFile)
        {
            return (400, new { error = "invalid_mode", message = "mode must be 'file' or 'folder'." });
        }

        // title (optional): only honored when it is an actual non-blank string.
        string? title = null;
        if (dict is not null &&
            dict.TryGetValue("title", out object? titleRaw) &&
            titleRaw is string titleStr &&
            titleStr.Trim().Length > 0)
        {
            title = titleStr;
        }

        // initialDirectory (optional): only honored when it resolves to a real
        // directory on disk. For a file path, its containing directory is used.
        string? initialDir = ResolveInitialDirectory(dict);

        // filters (optional, file mode only): converted to an OpenFileDialog
        // Filter string. Folder mode ignores filters entirely.
        string filter = isFile ? BuildFilter(dict) : string.Empty;

        try
        {
            DialogResult result = DialogResult.Cancel;
            string? selectedPath = null;
            Exception? workerError = null;

            // Runs on the dedicated STA thread. Any exception is captured into
            // workerError rather than left to propagate (an unhandled exception
            // on a non-threadpool thread would terminate the host process).
            void ShowPickerOnStaThread()
            {
                try
                {
                    IntPtr ownerHandle = TryGetOwnerHandle();

                    if (isFolder)
                    {
                        using var folderDialog = new FolderBrowserDialog
                        {
                            // UseDescriptionForTitle shows Description as the
                            // title bar text in the modern (Vista-style) dialog.
                            UseDescriptionForTitle = true,
                        };
                        if (!string.IsNullOrEmpty(title))
                        {
                            folderDialog.Description = title;
                        }
                        if (!string.IsNullOrEmpty(initialDir))
                        {
                            folderDialog.SelectedPath = initialDir;
                        }

                        result = ShowDialog(folderDialog, ownerHandle);
                        if (result == DialogResult.OK)
                        {
                            selectedPath = folderDialog.SelectedPath;
                        }
                    }
                    else
                    {
                        using var fileDialog = new OpenFileDialog
                        {
                            CheckFileExists = true,
                            Multiselect = false,
                            Filter = filter,
                        };
                        if (!string.IsNullOrEmpty(title))
                        {
                            fileDialog.Title = title;
                        }
                        if (!string.IsNullOrEmpty(initialDir))
                        {
                            fileDialog.InitialDirectory = initialDir;
                        }

                        result = ShowDialog(fileDialog, ownerHandle);
                        if (result == DialogResult.OK)
                        {
                            selectedPath = fileDialog.FileName;
                        }
                    }
                }
                catch (Exception ex)
                {
                    workerError = ex;
                }
            }

            var thread = new Thread(ShowPickerOnStaThread);
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (workerError is not null)
            {
                return (500, new { error = "browse_failed", message = Bound(workerError.Message) });
            }

            // User picked: OK and a non-empty path.
            if (result == DialogResult.OK && !string.IsNullOrEmpty(selectedPath))
            {
                return (200, new { path = selectedPath, cancelled = false });
            }

            // Any non-OK result (or an empty path) is a cancellation.
            return (200, new { path = (string?)null, cancelled = true });
        }
        catch (Exception ex)
        {
            // Never let an unexpected failure throw out of Handle.
            return (500, new { error = "browse_failed", message = Bound(ex.Message) });
        }
    }

    // Resolves the optional starting directory. Returns null (ignored
    // silently) when the field is absent, blank, malformed, or does not point
    // at a directory that exists. A file path is reduced to its containing
    // directory.
    private static string? ResolveInitialDirectory(Dictionary<string, object?>? dict)
    {
        if (dict is null ||
            !dict.TryGetValue("initialDirectory", out object? raw) ||
            raw is not string candidate ||
            candidate.Length == 0)
        {
            return null;
        }

        try
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            string? parent = Path.GetDirectoryName(candidate);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                return parent;
            }
        }
        catch
        {
            // Illegal path characters or similar — treat as not provided.
        }

        return null;
    }

    // Builds an OpenFileDialog Filter string from the optional filters array,
    // e.g. "Checkpoint files (*.json)|*.json|All files (*.*)|*.*". Malformed
    // entries are skipped. When no usable filter group survives, falls back to
    // "All files (*.*)|*.*". An "All files" fallback group is always appended
    // after any custom groups (matching the common Windows convention).
    private static string BuildFilter(Dictionary<string, object?>? dict)
    {
        if (dict is null || !dict.TryGetValue("filters", out object? rawFilters))
        {
            return AllFilesFilter;
        }

        List<object?>? filterList = JsonModel.AsList(rawFilters);
        if (filterList is null || filterList.Count == 0)
        {
            return AllFilesFilter;
        }

        var segments = new List<string>();
        foreach (object? entry in filterList)
        {
            Dictionary<string, object?>? f = JsonModel.AsDict(entry);
            if (f is null)
            {
                continue; // Not an object — skip.
            }

            string name = f.TryGetValue("name", out object? n) && n is string ns ? ns.Trim() : string.Empty;
            List<object?>? extList = f.TryGetValue("extensions", out object? e) ? JsonModel.AsList(e) : null;
            if (name.Length == 0 || extList is null || extList.Count == 0)
            {
                continue; // A usable group needs a name and at least one extension.
            }

            var patterns = new List<string>();
            foreach (object? extObj in extList)
            {
                if (extObj is not string ext)
                {
                    continue;
                }
                // Accept "json", ".json", or "*.json" — normalize to "*.json".
                string clean = ext.Trim().TrimStart('*').TrimStart('.').Trim();
                if (clean.Length == 0)
                {
                    continue;
                }
                patterns.Add("*." + clean);
            }

            if (patterns.Count == 0)
            {
                continue;
            }

            string joined = string.Join(";", patterns);
            segments.Add($"{name} ({joined})|{joined}");
        }

        if (segments.Count == 0)
        {
            return AllFilesFilter;
        }

        segments.Add(AllFilesFilter);
        return string.Join("|", segments);
    }

    // The host window handle, or IntPtr.Zero when there is no main window
    // (e.g. a headless context) so the dialog can be shown ownerless.
    private static IntPtr TryGetOwnerHandle()
    {
        try
        {
            return Process.GetCurrentProcess().MainWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // Shows the dialog parented to the host window when a handle is available;
    // otherwise shows it ownerless.
    private static DialogResult ShowDialog(CommonDialog dialog, IntPtr ownerHandle)
    {
        if (ownerHandle != IntPtr.Zero)
        {
            return dialog.ShowDialog(new OwnerWindow(ownerHandle));
        }
        return dialog.ShowDialog();
    }

    // Collapses to a single line and caps the length so the failure body stays
    // a short, bounded reason rather than an unbounded multi-line message.
    private static string Bound(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "The file picker could not be shown.";
        }

        string single = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (single.Length > MaxErrorMessageLength)
        {
            single = single.Substring(0, MaxErrorMessageLength);
        }
        return single;
    }

    // Minimal IWin32Window so the common dialogs can be parented to the host
    // window handle without taking a dependency on a Form instance.
    private sealed class OwnerWindow : IWin32Window
    {
        public OwnerWindow(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }
    }
}