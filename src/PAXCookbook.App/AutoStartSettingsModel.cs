using Microsoft.Win32;

namespace PAXCookbook.App;

// Settings → Startup route handlers (V2 two-process auto-start toggle).
//
// Two authenticated routes behind the same Bearer + CSRF + broker-lock gate as
// the other settings routes (enforced upstream in Program.cs):
//
//   GET  /api/v1/settings/autostart   ->  { enabled }
//   POST /api/v1/settings/autostart   ->  { ok, enabled }   body { enabled: bool }
//
// They read / write the per-user HKCU Run value that auto-starts the headless
// broker daemon at logon, so the user can turn background auto-start on or off
// after install without reinstalling.
//
// Cross-component contract: the Run key + value name MUST stay identical to the
// ones the native installer writes (PAXCookbookSetup.Shell.AutoStartRegistrar:
// RootSubKey "Software\Microsoft\Windows\CurrentVersion\Run", ValueName
// "PAX Cookbook") so the Settings toggle, the installer, and the uninstaller
// all operate on the SAME registry value — never a divergent duplicate. The
// written command also mirrors the installer's launch line so toggling off then
// on reproduces exactly what the installer created.
//
// Scope / safety: HKCU only (never HKLM); only this ONE named value (the Run
// KEY is shared with other applications and is never created or deleted by the
// off path). No secret is read or written — the value is a launch command line
// for our own signed executable. The exe path is the ACTUAL running executable
// (Environment.ProcessPath), so it is always correct regardless of where the app
// was installed.
internal static class AutoStartSettingsModel
{
    // MUST match PAXCookbookSetup.Shell.AutoStartRegistrar.RootSubKey / ValueName.
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PAX Cookbook";

    // First-launch marker. Its presence means the user (or a prior GET default)
    // has already established an auto-start choice, so the "default ON" logic
    // never re-creates the Run key after the user has explicitly turned it off.
    // Lives next to the per-user engine/install state under %LOCALAPPDATA%.
    private static string InitializedFlagPath()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "PAXCookbook", "autostart-initialized");
    }

    private static bool InitializedFlagExists()
    {
        try { return File.Exists(InitializedFlagPath()); }
        catch { return false; }
    }

    private static void WriteInitializedFlag()
    {
        try
        {
            string path = InitializedFlagPath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
            if (!File.Exists(path))
            {
                File.WriteAllText(path, DateTime.UtcNow.ToString("o"));
            }
        }
        catch { /* best-effort: a missing flag just means the default may re-apply. */ }
    }

    // ---------------------------------------------------------------------
    // GET /api/v1/settings/autostart
    // enabled iff the Run value exists AND its first quoted token is an exe file
    // that still exists. On the FIRST launch (no initialized flag) with no Run
    // value, auto-start defaults ON: the key is created now so scheduled bakes
    // work out of the box. Once the user has made a choice (flag written), the
    // default never re-applies — an explicit OFF is respected.
    // ---------------------------------------------------------------------
    public static (int Status, object Body) Get(string workspacePath, string appRoot)
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: false))
            {
                string? value = key?.GetValue(RunValueName) as string;
                if (!string.IsNullOrEmpty(value) && QuotedExeExists(value!))
                {
                    // Already enabled — record that a choice now exists so a
                    // later OFF toggle is respected and not re-defaulted.
                    WriteInitializedFlag();
                    return (200, new { enabled = true });
                }
            }

            // Run value absent or stale. Default ON only on the very first read
            // (before any user choice). Creating the key writes the same command
            // the installer's AutoStartRegistrar would.
            if (!InitializedFlagExists())
            {
                try
                {
                    using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunSubKey, writable: true)
                        ?? throw new InvalidOperationException("The per-user Run key is unavailable.");
                    key.SetValue(RunValueName, BuildCommand(workspacePath, appRoot), RegistryValueKind.String);
                    WriteInitializedFlag();
                    return (200, new { enabled = true });
                }
                catch
                {
                    // Could not create the key; report OFF and do NOT write the
                    // flag so the default can be retried on a later read.
                    return (200, new { enabled = false });
                }
            }

            // Absent + already initialized = the user turned it off; respect it.
            return (200, new { enabled = false });
        }
        catch
        {
            // Any read failure is reported as "off" rather than surfacing an
            // error — the toggle simply shows disabled.
            return (200, new { enabled = false });
        }
    }

    // ---------------------------------------------------------------------
    // POST /api/v1/settings/autostart   body { enabled: bool }
    // true  -> write the Run value = "<exe>" --headless --workspace <ws> --approot <app>
    // false -> remove ONLY our value (never the shared Run key)
    // ---------------------------------------------------------------------
    public static (int Status, object Body) Set(object? body, string workspacePath, string appRoot)
    {
        if (body is not Dictionary<string, object?> request)
        {
            return (400, new { error = "invalid_json" });
        }
        if (!request.TryGetValue("enabled", out object? raw) || raw is not bool enabled)
        {
            return (400, new
            {
                error = "validation_failed",
                reason = "enabled_required",
                field = "enabled",
                message = "enabled must be provided as a boolean.",
            });
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunSubKey, writable: true)
                ?? throw new InvalidOperationException("The per-user Run key is unavailable.");
            if (enabled)
            {
                key.SetValue(RunValueName, BuildCommand(workspacePath, appRoot), RegistryValueKind.String);
            }
            else
            {
                // Delete only our value; the shared Run key and any other app's
                // values are left intact.
                if (key.GetValue(RunValueName) is not null)
                {
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            return (500, new
            {
                error = "autostart_write_failed",
                message = "The startup setting could not be saved.",
            });
        }

        // Record that the user has made an explicit choice so the first-launch
        // "default ON" never re-creates the key after an OFF.
        WriteInitializedFlag();
        return (200, new { ok = true, enabled });
    }

    // Build the SAME launch command the installer's AutoStartRegistrar writes:
    //   "<exe>" --headless --workspace "<ws>" --approot "<approot>"
    // so toggling off then on reproduces the installer's key byte-for-byte. The
    // exe path is the running executable (Environment.ProcessPath); the
    // workspace / approot are the values THIS broker was launched with (the
    // canonical production paths in an installed app), normalized to absolute.
    private static string BuildCommand(string workspacePath, string appRoot)
    {
        string exe = Environment.ProcessPath ?? string.Empty;
        string exeFull = SafeFullPath(exe);
        string wsFull = SafeFullPath(workspacePath);
        string appFull = SafeFullPath(appRoot);
        return $"\"{exeFull}\" --headless --workspace \"{wsFull}\" --approot \"{appFull}\"";
    }

    private static string SafeFullPath(string p)
    {
        try { return string.IsNullOrEmpty(p) ? p : Path.GetFullPath(p); }
        catch { return p; }
    }

    // Parse the first quoted token (the exe path) from a Run command and test
    // that it exists on disk. Falls back to the first space-delimited token for
    // an unquoted value.
    private static bool QuotedExeExists(string command)
    {
        string path = command;
        if (path.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = path.IndexOf('"', 1);
            if (end > 1) { path = path.Substring(1, end - 1); }
        }
        else
        {
            int sp = path.IndexOf(' ');
            if (sp > 0) { path = path.Substring(0, sp); }
        }
        try { return File.Exists(path); }
        catch { return false; }
    }
}
