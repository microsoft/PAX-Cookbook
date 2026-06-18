using System;
using System.IO;

namespace PAXCookbook.App;

// App-local mirror of PAXCookbook.Shared.DotNetLaunch. PAXCookbook.App does NOT
// reference PAXCookbook.Shared (it mirrors the few constants it needs), so the
// dotnet-host resolution is duplicated here. It MUST stay in sync with the
// Shared version so the installer's auto-start command (AutoStartRegistrar) and
// the in-app "Start at login" toggle (AutoStartSettingsModel) produce the same
// launch line. Under corporate WDAC the unsigned apphost cannot be executed, so
// every self-launch runs the Microsoft-signed dotnet.exe with the app DLL.
internal static class DotNetLaunch
{
    // Full path to the Microsoft-signed dotnet.exe host. Resolves dynamically so
    // a self-launch never bakes in an assumed path: it probes the standard
    // per-machine install locations, then the DOTNET_ROOT family, then the
    // machine PATH, and only falls back to the bare "dotnet.exe" name (resolved
    // via the App Paths key / PATH at launch) when nothing concrete is found.
    public static string DotNetExePath()
    {
        // 1. Standard per-machine install locations.
        foreach (var env in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
        {
            var pf = SafeGetEnv(env);
            if (string.IsNullOrEmpty(pf)) continue;
            var candidate = Path.Combine(pf, "dotnet", "dotnet.exe");
            if (SafeFileExists(candidate)) return candidate;
        }

        // 2. DOTNET_ROOT family — written by the .NET installer and honored by the
        //    host. The most reliable signal when .NET lives off the default
        //    Program Files path.
        foreach (var env in new[] { "DOTNET_ROOT", "DOTNET_ROOT(x86)", "DOTNET_ROOT_X64" })
        {
            var root = SafeGetEnv(env);
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "dotnet.exe");
            if (SafeFileExists(candidate)) return candidate;
        }

        // 3. PATH search — finds dotnet.exe wherever the installer put it on the
        //    machine PATH (winget, custom installs).
        var fromPath = DotNetFromPath();
        if (fromPath is not null) return fromPath;

        // 4. Last resort: the bare name, resolved via the App Paths key / PATH at
        //    launch time.
        return "dotnet.exe";
    }

    private static string? SafeGetEnv(string name)
    {
        try { return Environment.GetEnvironmentVariable(name); }
        catch { return null; }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static string? DotNetFromPath()
    {
        var path = SafeGetEnv("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate;
            try { candidate = Path.Combine(dir.Trim(), "dotnet.exe"); }
            catch { continue; }
            if (SafeFileExists(candidate)) return candidate;
        }
        return null;
    }

    // The managed entry assembly this process runs from (its bin dir holds it).
    // Valid whether the process was started via the apphost or via dotnet.
    public static string OwnDllPath()
        => Path.Combine(AppContext.BaseDirectory, "PAX Cookbook.dll");
}
