using System.Text.RegularExpressions;

namespace PAXCookbookSetup.Gui;

// Pure-ish detection logic for the two external prerequisites. All system
// interaction goes through IPrerequisiteProbe so the ordering + version-gate
// behaviour is fully unit-testable. The detector returns the FIRST candidate
// (in the documented search order) that satisfies the minimum version; if it
// only ever sees a too-old install, it reports that (so the wizard can offer
// to upgrade); otherwise it reports "not found".
public sealed class PrerequisiteDetector
{
    public static readonly Version MinDotNet8 = new(8, 0);
    public static readonly Version MinPowerShell = new(7, 2);
    public static readonly Version MinPython = new(3, 9);

    private readonly IPrerequisiteProbe _probe;

    public PrerequisiteDetector(IPrerequisiteProbe probe) => _probe = probe;

    private readonly record struct Candidate(bool Located, string? Path, Version? Version, string Source);

    public PrerequisiteStatus DetectDotNet8DesktopRuntime()
        => EvaluateDotNet8();

    public PrerequisiteStatus DetectPowerShell7()
        => Evaluate(PrerequisiteKind.PowerShell7, "PowerShell 7", MinPowerShell, PowerShellCandidates());

    public PrerequisiteStatus DetectPython()
        => Evaluate(PrerequisiteKind.Python, "Python", MinPython, PythonCandidates());

    // Check the registry for .NET 8 Desktop Runtime. The WindowsDesktop app
    // framework is installed in HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App
    // with subkeys named after versions (e.g. "8.0.11"). We look for any 8.0.x version.
    private PrerequisiteStatus EvaluateDotNet8()
    {
        const string root = @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App";
        var versions = _probe.EnumerateHklmSubKeyNames(root).ToList();
        if (!versions.Any())
            return PrerequisiteStatus.Missing(PrerequisiteKind.DotNet8DesktopRuntime, ".NET 8 Desktop Runtime");

        foreach (var verStr in versions.OrderByDescending(v => v))
        {
            if (TryParseVersion(verStr, out var version) && version >= MinDotNet8 && version.Major == 8)
            {
                return new PrerequisiteStatus(
                    PrerequisiteKind.DotNet8DesktopRuntime,
                    ".NET 8 Desktop Runtime",
                    true,
                    FormatVersion(version),
                    null,
                    "Registry");
            }
        }

        return PrerequisiteStatus.Missing(PrerequisiteKind.DotNet8DesktopRuntime, ".NET 8 Desktop Runtime");
    }

    // -----------------------------------------------------------------
    // Search-order candidate generators (lazy — probing stops as soon as
    // Evaluate returns a satisfied result).
    // -----------------------------------------------------------------
    private IEnumerable<Candidate> PowerShellCandidates()
    {
        // 1. pwsh.exe on PATH
        var onPath = _probe.ResolveOnPath("pwsh.exe");
        if (onPath is not null)
            yield return new Candidate(true, onPath, ProbeVersion(onPath, "--version"), "PATH");

        // 2..4 standard install locations
        foreach (var (env, rel, src) in new[]
        {
            ("ProgramFiles",      Path.Combine("PowerShell", "7", "pwsh.exe"),     "ProgramFiles"),
            ("ProgramFiles(x86)", Path.Combine("PowerShell", "7", "pwsh.exe"),     "ProgramFiles(x86)"),
            ("LOCALAPPDATA",      Path.Combine("Microsoft", "PowerShell", "pwsh.exe"), "LocalAppData"),
        })
        {
            var basePath = _probe.GetEnvPath(env);
            if (string.IsNullOrEmpty(basePath)) continue;
            var p = Path.Combine(basePath, rel);
            if (_probe.FileExists(p))
                yield return new Candidate(true, p, ProbeVersion(p, "--version"), src);
        }

        // 5. registry: HKLM\SOFTWARE\Microsoft\PowerShellCore\InstalledVersions\<guid>
        const string root = @"SOFTWARE\Microsoft\PowerShellCore\InstalledVersions";
        foreach (var sub in _probe.EnumerateHklmSubKeyNames(root))
        {
            var full = root + "\\" + sub;
            var semantic = _probe.ReadHklmString(full, "SemanticVersion")
                           ?? _probe.ReadHklmString(full, "Version");
            Version? v = TryParseVersion(semantic, out var parsed) ? parsed : null;
            var loc = _probe.ReadHklmString(full, "InstallLocation");
            var exe = string.IsNullOrEmpty(loc) ? null : Path.Combine(loc, "pwsh.exe");
            yield return new Candidate(true, exe, v, "Registry");
        }
    }

    private IEnumerable<Candidate> PythonCandidates()
    {
        // 1. python.exe / 2. python3.exe on PATH
        foreach (var exe in new[] { "python.exe", "python3.exe" })
        {
            var onPath = _probe.ResolveOnPath(exe);
            if (onPath is not null)
                yield return new Candidate(true, onPath, ProbeVersion(onPath, "--version"), "PATH");
        }

        // 3. %LOCALAPPDATA%\Programs\Python\Python3*\python.exe
        // 4. %ProgramFiles%\Python3*\python.exe
        foreach (var (env, sub, pattern) in new[]
        {
            ("LOCALAPPDATA", Path.Combine("Programs", "Python"), "Python3*"),
            ("ProgramFiles", "",                                  "Python3*"),
        })
        {
            var basePath = _probe.GetEnvPath(env);
            if (string.IsNullOrEmpty(basePath)) continue;
            var parent = string.IsNullOrEmpty(sub) ? basePath : Path.Combine(basePath, sub);
            foreach (var dir in _probe.EnumerateDirectories(parent, pattern))
            {
                var p = Path.Combine(dir, "python.exe");
                if (_probe.FileExists(p))
                    yield return new Candidate(true, p, ProbeVersion(p, "--version"),
                        env == "LOCALAPPDATA" ? "LocalAppData" : "ProgramFiles");
            }
        }

        // 5. py.exe launcher -> py -3 --version
        var py = _probe.ResolveOnPath("py.exe");
        if (py is not null)
            yield return new Candidate(true, py, ProbeVersion(py, "-3 --version"), "py-launcher");
    }

    // -----------------------------------------------------------------
    // Shared evaluation: first candidate with a confirmed version >= the
    // minimum wins. A candidate whose version cannot be read is skipped on
    // purpose — most importantly the Windows Store "python.exe" execution-
    // alias stub, which is on PATH but prints no version; treating it as
    // satisfied would be a false positive. The first too-old install is
    // remembered so the wizard can offer an upgrade rather than just "missing".
    // -----------------------------------------------------------------
    private static PrerequisiteStatus Evaluate(
        PrerequisiteKind kind, string name, Version min, IEnumerable<Candidate> candidates)
    {
        PrerequisiteStatus? tooOld = null;
        foreach (var c in candidates)
        {
            if (!c.Located) continue;

            // Located but the version could not be confirmed -> skip and keep
            // looking (do not claim the requirement is met on faith).
            if (c.Version is null) continue;

            if (c.Version >= min)
                return new PrerequisiteStatus(kind, name, true,
                    FormatVersion(c.Version), c.Path, c.Source);

            tooOld ??= new PrerequisiteStatus(kind, name, false,
                FormatVersion(c.Version), c.Path, "too-old");
        }
        return tooOld ?? PrerequisiteStatus.Missing(kind, name);
    }

    private Version? ProbeVersion(string exePath, string args)
        => TryParseVersion(_probe.RunVersion(exePath, args), out var v) ? v : null;

    // Extract the first dotted version (major.minor[.build]) from a tool's
    // --version output. Searching with a regex is fine; this never edits code.
    public static bool TryParseVersion(string? output, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(output)) return false;
        var m = Regex.Match(output, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return false;
        try
        {
            int major = int.Parse(m.Groups[1].Value);
            int minor = int.Parse(m.Groups[2].Value);
            int build = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            version = new Version(major, minor, build);
            return true;
        }
        catch { return false; }
    }

    private static string FormatVersion(Version v)
        => v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
}
