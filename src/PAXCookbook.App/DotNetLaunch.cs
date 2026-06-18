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
    // Full path to the Microsoft-signed dotnet.exe host. Prefers the standard
    // per-machine install locations; falls back to the bare "dotnet.exe" name
    // (resolved via the App Paths key / PATH at launch time).
    public static string DotNetExePath()
    {
        foreach (var env in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
        {
            string? pf;
            try { pf = Environment.GetEnvironmentVariable(env); }
            catch { pf = null; }
            if (string.IsNullOrEmpty(pf)) continue;
            var candidate = Path.Combine(pf, "dotnet", "dotnet.exe");
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* ignore and keep probing */ }
        }
        return "dotnet.exe";
    }

    // The managed entry assembly this process runs from (its bin dir holds it).
    // Valid whether the process was started via the apphost or via dotnet.
    public static string OwnDllPath()
        => Path.Combine(AppContext.BaseDirectory, "PAX Cookbook.dll");
}
