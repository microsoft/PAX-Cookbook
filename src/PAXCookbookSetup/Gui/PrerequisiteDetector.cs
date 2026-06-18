using System.Runtime.InteropServices;
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
    private readonly Architecture _hostArch;

    public PrerequisiteDetector(IPrerequisiteProbe probe, Architecture? hostArchitecture = null)
    {
        _probe = probe;
        // Default to the REAL machine architecture (PrereqArch.Os reads the
        // machine registry PROCESSOR_ARCHITECTURE). Setup runs x64-emulated on
        // ARM64 — where RuntimeInformation.OSArchitecture wrongly returns X64 —
        // and the app launches via the native host, so the runtime we look for
        // must match the machine, not this (possibly emulated) process.
        _hostArch = hostArchitecture ?? PrereqArch.Os;
    }

    private readonly record struct Candidate(bool Located, string? Path, Version? Version, string Source);

    public PrerequisiteStatus DetectDotNet8DesktopRuntime()
        => EvaluateDotNet8();

    public PrerequisiteStatus DetectAspNetCoreRuntime()
        => EvaluateAspNetCore();

    public PrerequisiteStatus DetectPowerShell7()
        => Evaluate(PrerequisiteKind.PowerShell7, "PowerShell 7", MinPowerShell, PowerShellCandidates());

    public PrerequisiteStatus DetectPython()
        => Evaluate(PrerequisiteKind.Python, "Python", MinPython, PythonCandidates());

    // Detect a shared .NET 8 framework with three independent strategies, in
    // cost order, returning satisfied as soon as any finds a >= 8.0 install.
    // A single method (the registry alone) false-negatives when .NET 8 was
    // installed via winget / Visual Studio / the SDK, which do not write the
    // sharedfx registry key — so the fallbacks confirm those installs too.
    //   1. Registry — HKLM\...\sharedfx\<framework> subkeys (fast, no spawn).
    //   2. `dotnet --list-runtimes` — parses "<framework> 8.x.y".
    //   3. Well-known shared-framework folder —
    //      %ProgramFiles%\dotnet\shared\<framework>\8.*.
    // The app needs BOTH Microsoft.WindowsDesktop.App (the WinForms WebView2
    // host) AND Microsoft.AspNetCore.App (the in-process Kestrel broker); the
    // Desktop Runtime does NOT include ASP.NET Core, so each is detected — and
    // installed — independently.
    private PrerequisiteStatus EvaluateDotNet8()
        => EvaluateSharedFramework("Microsoft.WindowsDesktop.App",
                                   PrerequisiteKind.DotNet8DesktopRuntime,
                                   ".NET 8 Desktop Runtime", ".NET 8");

    private PrerequisiteStatus EvaluateAspNetCore()
        => EvaluateSharedFramework("Microsoft.AspNetCore.App",
                                   PrerequisiteKind.AspNetCoreRuntime,
                                   "ASP.NET Core 8 Runtime", "ASP.NET Core");

    private PrerequisiteStatus EvaluateSharedFramework(
        string frameworkId, PrerequisiteKind kind, string displayName, string logTag)
    {
        // Strategy 1: registry, under the machine-architecture subkey. The app
        // launches through the native dotnet.exe host, so it needs the runtime
        // matching the OS architecture (arm64 on an ARM64 machine, x64 on x64).
        // Checking only the x64 subkey would miss an ARM64 install and — worse —
        // falsely report "satisfied" when only an unusable x64 runtime is present
        // on an ARM64 machine, so the wizard would skip installing the arm64
        // runtime the app actually needs.
        string archRid = PrereqArch.Rid(_hostArch);
        PrereqLog.Write($"[PREREQ] {logTag} detection: arch={_hostArch} rid={archRid}");
        string root = $@"SOFTWARE\dotnet\Setup\InstalledVersions\{archRid}\sharedfx\{frameworkId}";
        var registrySubkeys = _probe.EnumerateHklmSubKeyNames(root).ToList();
        PrereqLog.Write($"[PREREQ] {logTag} detection: registry key checked = HKLM\\{root}");
        PrereqLog.Write($"[PREREQ] {logTag} detection: registry subkeys = [{string.Join(", ", registrySubkeys)}]");
        var fromRegistry = HighestDotNet8(registrySubkeys);
        if (fromRegistry is not null)
        {
            PrereqLog.Write($"[PREREQ] {logTag} detection: RESULT = installed (Registry, {FormatVersion(fromRegistry)})");
            return FrameworkFound(kind, displayName, fromRegistry, "Registry");
        }

        // Strategy 2: `dotnet --list-runtimes`, but only from the NATIVE host the
        // app actually launches (%ProgramFiles%\dotnet\dotnet.exe). On ARM64 a
        // stray x64 dotnet on PATH would otherwise report an 8.0 runtime the
        // native ARM64 host cannot load. When ProgramFiles gives no signal
        // (unit tests), fall back to PATH resolution of "dotnet".
        string listHost;
        var programFilesForHost = _probe.GetEnvPath("ProgramFiles");
        if (!string.IsNullOrEmpty(programFilesForHost))
        {
            var nativeDotnet = Path.Combine(programFilesForHost, "dotnet", "dotnet.exe");
            listHost = _probe.FileExists(nativeDotnet) ? nativeDotnet : "";
        }
        else
        {
            listHost = "dotnet";
        }
        PrereqLog.Write($"[PREREQ] {logTag} detection: list-runtimes host = {(listHost.Length > 0 ? listHost : "(skipped)")}");
        if (listHost.Length > 0)
        {
            var listed = _probe.RunVersion(listHost, "--list-runtimes");
            PrereqLog.Write($"[PREREQ] {logTag} detection: list-runtimes output = {(string.IsNullOrWhiteSpace(listed) ? "(none)" : listed!.Replace("\r", " ").Replace("\n", " | "))}");
            if (!string.IsNullOrWhiteSpace(listed))
            {
                Version? best = null;
                foreach (Match mm in Regex.Matches(listed, Regex.Escape(frameworkId) + @"\s+(\d+\.\d+\.\d+)"))
                {
                    if (TryParseVersion(mm.Groups[1].Value, out var v) && v.Major == 8 && v >= MinDotNet8
                        && (best is null || v > best))
                        best = v;
                }
                if (best is not null)
                {
                    PrereqLog.Write($"[PREREQ] {logTag} detection: RESULT = installed (list-runtimes, {FormatVersion(best)})");
                    return FrameworkFound(kind, displayName, best, "dotnet --list-runtimes");
                }
            }
        }

        // Strategy 3: well-known shared-framework folder (dotnet not on PATH).
        var programFiles = _probe.GetEnvPath("ProgramFiles");
        if (!string.IsNullOrEmpty(programFiles))
        {
            var sharedRoot = Path.Combine(programFiles, "dotnet", "shared", frameworkId);
            var fromDisk = HighestDotNet8(
                _probe.EnumerateDirectories(sharedRoot, "8.*").Select(d => Path.GetFileName(d)));
            if (fromDisk is not null)
            {
                PrereqLog.Write($"[PREREQ] {logTag} detection: RESULT = installed (ProgramFiles, {FormatVersion(fromDisk)})");
                return FrameworkFound(kind, displayName, fromDisk, "ProgramFiles");
            }
        }

        PrereqLog.Write($"[PREREQ] {logTag} detection: RESULT = not installed");
        return PrerequisiteStatus.Missing(kind, displayName);
    }

    // Highest 8.x.y version (>= MinDotNet8) among the supplied version strings,
    // or null when none qualify. Non-8.x and unparseable entries are ignored.
    private static Version? HighestDotNet8(IEnumerable<string?> versionStrings)
    {
        Version? best = null;
        foreach (var s in versionStrings)
        {
            if (TryParseVersion(s, out var v) && v.Major == 8 && v >= MinDotNet8
                && (best is null || v > best))
                best = v;
        }
        return best;
    }

    private static PrerequisiteStatus FrameworkFound(
        PrerequisiteKind kind, string displayName, Version version, string source)
        => new PrerequisiteStatus(kind, displayName, true, FormatVersion(version), null, source);

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
