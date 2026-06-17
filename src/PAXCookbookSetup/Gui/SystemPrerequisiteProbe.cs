using System.Diagnostics;
using Microsoft.Win32;

namespace PAXCookbookSetup.Gui;

// Production IPrerequisiteProbe: real PATH/filesystem/process/registry probes.
// Every method is defensive (never throws) so detection degrades to
// "not found" rather than crashing the wizard on an unusual machine.
public sealed class SystemPrerequisiteProbe : IPrerequisiteProbe
{
    private readonly int _versionTimeoutMs;

    public SystemPrerequisiteProbe(int versionTimeoutMs = 4000)
    {
        _versionTimeoutMs = versionTimeoutMs;
    }

    public string? ResolveOnPath(string exeName)
    {
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar)) return null;
            foreach (var dir in pathVar.Split(Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidate;
                try { candidate = Path.Combine(dir, exeName); }
                catch { continue; } // malformed PATH entry
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    public bool FileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    public IEnumerable<string> EnumerateDirectories(string parent, string pattern)
    {
        try
        {
            if (!Directory.Exists(parent)) return Array.Empty<string>();
            return Directory.EnumerateDirectories(parent, pattern);
        }
        catch { return Array.Empty<string>(); }
    }

    public string? GetEnvPath(string envVarName)
    {
        try
        {
            var v = Environment.GetEnvironmentVariable(envVarName);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    public string? RunVersion(string exePath, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;

            // Read both streams; a version may land on stdout (modern tools)
            // or stderr (older Python). Combine and let the parser scan.
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(_versionTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }
            var combined = (stdout + "\n" + stderr).Trim();
            return combined.Length == 0 ? null : combined;
        }
        catch { return null; }
    }

    public string? ReadHklmString(string subKey, string? valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    public IEnumerable<string> EnumerateHklmSubKeyNames(string subKey)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
            return key is null ? Array.Empty<string>() : key.GetSubKeyNames();
        }
        catch { return Array.Empty<string>(); }
    }
}
