using System.Runtime.InteropServices;

namespace PAXCookbookSetup.Gui;

// Persistent prerequisite diagnostics. Captures architecture detection, the
// .NET / PowerShell / Python detection decisions, the chosen download URLs,
// whether each download succeeded, and the installer exit codes to
// %LOCALAPPDATA%\PAXCookbook\Logs\prereq.log (the same folder as the app's
// startup.log) so a tester whose install "did nothing" can send the log.
//
// Logging is INACTIVE until Begin() is called. The production install flows
// (CLI + GUI wizard) call Begin() once at the start; unit tests that construct
// the detector/installers directly never call it, so every Write() is a no-op
// in tests — no file I/O, no shared-file contention, no behaviour change.
// All methods are best-effort and never throw.
public static class PrereqLog
{
    private static readonly object Gate = new();
    private static string? _resolvedPath;
    private static volatile bool _active;

    // %LOCALAPPDATA%\PAXCookbook\Logs\prereq.log, with a %TEMP% fallback.
    // Appended, never truncated, so repeated attempts accumulate for diagnosis.
    public static string LogPath
    {
        get
        {
            if (_resolvedPath is not null) return _resolvedPath;
            try
            {
                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(baseDir))
                {
                    var dir = Path.Combine(baseDir, "PAXCookbook", "Logs");
                    Directory.CreateDirectory(dir);
                    _resolvedPath = Path.Combine(dir, "prereq.log");
                    return _resolvedPath;
                }
            }
            catch
            {
                // Fall through to the TEMP fallback below.
            }

            try { _resolvedPath = Path.Combine(Path.GetTempPath(), "PAXCookbook-prereq.log"); }
            catch { _resolvedPath = string.Empty; }
            return _resolvedPath ?? string.Empty;
        }
    }

    // Activates logging and records the architecture detection result — the
    // single most important fact for diagnosing an ARM64 "no frameworks" launch.
    public static void Begin()
    {
        _active = true;
        try
        {
            Write(string.Empty);
            Write("================================================================");
            Write("[PREREQ] session start");
            Write("[PREREQ] OSArchitecture (RuntimeInformation) = " + RuntimeInformation.OSArchitecture);
            Write("[PREREQ] ProcessArchitecture                 = " + RuntimeInformation.ProcessArchitecture);
            Write("[PREREQ] Resolved machine architecture       = " + PrereqArch.Os + " (rid " + PrereqArch.Rid() + ")");
            Write("[PREREQ] Architecture resolution method      = " + PrereqArch.ResolutionMethod);
        }
        catch
        {
            // Never throw from diagnostics.
        }
    }

    public static void Write(string line)
    {
        if (!_active) return;
        var path = LogPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var stamped = string.IsNullOrEmpty(line)
                ? line
                : "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + line;
            lock (Gate) { File.AppendAllText(path, stamped + Environment.NewLine); }
        }
        catch
        {
            // Disk full / locked / denied — diagnostics are best-effort.
        }
    }
}
