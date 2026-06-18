using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PAXCookbook.App;

// Persistent startup diagnostics. Writes launch milestones and any fatal startup
// error (with stack trace) to a file under the user's profile so a tester whose
// app "does nothing" — no window, no tray, no message — can send the log to
// support. The console is hidden after early initialization, so without this a
// launch that throws before the window appears would be completely silent. Every
// method is best-effort and never throws: diagnostics must not become a new
// failure mode.
internal static class StartupLog
{
    private static readonly object Gate = new();
    private static string? _resolvedPath;

    // %LOCALAPPDATA%\PAXCookbook\Logs\startup.log, with a %TEMP% fallback. The
    // log persists across launches (appended, never truncated) so repeated
    // failures accumulate for diagnosis.
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
                    _resolvedPath = Path.Combine(dir, "startup.log");
                    return _resolvedPath;
                }
            }
            catch
            {
                // Fall through to the TEMP fallback below.
            }

            try { _resolvedPath = Path.Combine(Path.GetTempPath(), "PAXCookbook-startup.log"); }
            catch { _resolvedPath = string.Empty; }
            return _resolvedPath ?? string.Empty;
        }
    }

    // Opens a new launch block and records the environment a tester would need to
    // diagnose a "nothing happens" launch (runtime, host, working directory,
    // assembly location, command line).
    public static void Begin(string[] args)
    {
        try
        {
            Write(string.Empty);
            Write("================================================================");
            Mark("PAX Cookbook startup beginning");
            Mark("dotnet runtime: " + RuntimeInformation.FrameworkDescription);
            Mark("OS: " + RuntimeInformation.OSDescription);
            Mark("architecture: process=" + RuntimeInformation.ProcessArchitecture
                 + ", os=" + RuntimeInformation.OSArchitecture);
            Mark("process id: " + Environment.ProcessId);
            Mark("host process: " + (Environment.ProcessPath ?? "(unknown)"));
            Mark("working directory: " + Environment.CurrentDirectory);
            Mark("assembly location: " + typeof(StartupLog).Assembly.Location);
            Mark("args: " + (args is { Length: > 0 } ? string.Join(' ', args) : "(none)"));
        }
        catch
        {
            // Never throw from diagnostics.
        }
    }

    // Records a timestamped milestone.
    public static void Mark(string milestone)
    {
        try { Write("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + milestone); }
        catch { /* best-effort */ }
    }

    // Records a fatal startup failure with full type, message, inner exceptions
    // and stack trace — the highest-value evidence for diagnosing a launch that
    // never reaches the window.
    public static void Fatal(string where, Exception? ex)
    {
        try
        {
            Mark("FATAL at " + where + ": " +
                 (ex is null ? "(no exception object)" : ex.GetType().FullName + ": " + ex.Message));
            for (var inner = ex?.InnerException; inner is not null; inner = inner.InnerException)
            {
                Write("  inner: " + inner.GetType().FullName + ": " + inner.Message);
            }
            if (ex?.StackTrace is { Length: > 0 } stack) Write(stack);
        }
        catch
        {
            // best-effort
        }
    }

    // Records a fatal startup failure described by a message — for handled FATALs
    // that return a non-zero exit code without throwing.
    public static void Fatal(string where, string message)
        => Mark("FATAL at " + where + ": " + message);

    private static void Write(string line)
    {
        var path = LogPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            lock (Gate) { File.AppendAllText(path, line + Environment.NewLine); }
        }
        catch
        {
            // Disk full / locked / access denied — diagnostics are best-effort.
        }
    }
}
