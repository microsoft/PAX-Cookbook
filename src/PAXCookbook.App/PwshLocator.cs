namespace PAXCookbook.App;

// Resolves the full path to PowerShell 7 (pwsh.exe) for spawning bake/cook
// children and the scheduled-task registrar. The child must NOT rely on PATH:
// the prerequisite installer places PowerShell 7 under
// %ProgramFiles%\PowerShell\7, but a just-completed install may not have
// refreshed the broker process's PATH, so a bare "pwsh" spawn fails with
// "cannot find the file specified". This searches PATH first (a normal setup),
// then the standard per-machine install locations the installer uses (including
// ProgramW6432 / ProgramFiles(x86) so an x64-emulated host on ARM64 still finds
// the native install). No other interpreter (powershell.exe, etc.) is selected.
internal static class PwshLocator
{
    // The full path to pwsh.exe, or null when PowerShell 7 is not found.
    internal static string? Resolve()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* a malformed PATH entry is skipped */ }
        }

        foreach (var envVar in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
        {
            var pf = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrEmpty(pf)) continue;
            try
            {
                var candidate = Path.Combine(pf, "PowerShell", "7", "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* skip an unreadable location */ }
        }

        return null;
    }
}
