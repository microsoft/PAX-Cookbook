using System;
using System.Collections.Generic;
using System.Linq;
using PAXCookbookSetup.Gui;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Slice A: unit tests for the GUI wizard's prerequisite detection logic.
// A fake IPrerequisiteProbe drives the (pure) ordering + version-gate
// behaviour in PrerequisiteDetector without touching the real machine.
public class WizardDetectionTests
{
    // -----------------------------------------------------------------
    // Configurable fake probe
    // -----------------------------------------------------------------
    private sealed class FakeProbe : IPrerequisiteProbe
    {
        public readonly Dictionary<string, string?> OnPath = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string?> Env = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<(string parent, string pattern), string[]> Dirs = new();
        public readonly Dictionary<string, string?> Versions = new(StringComparer.OrdinalIgnoreCase); // exePath -> output
        public readonly Dictionary<(string sub, string? val), string?> Hklm = new();
        public readonly Dictionary<string, string[]> HklmSubKeys = new(StringComparer.OrdinalIgnoreCase);

        public string? ResolveOnPath(string exeName) => OnPath.TryGetValue(exeName, out var p) ? p : null;
        public bool FileExists(string path) => Files.Contains(path);
        public IEnumerable<string> EnumerateDirectories(string parent, string pattern)
            => Dirs.TryGetValue((parent, pattern), out var d) ? d : Array.Empty<string>();
        public string? GetEnvPath(string envVarName) => Env.TryGetValue(envVarName, out var v) ? v : null;
        public string? RunVersion(string exePath, string arguments)
            => Versions.TryGetValue(exePath, out var o) ? o : null;
        public string? ReadHklmString(string subKey, string? valueName)
            => Hklm.TryGetValue((subKey, valueName), out var v) ? v : null;
        public IEnumerable<string> EnumerateHklmSubKeyNames(string subKey)
            => HklmSubKeys.TryGetValue(subKey, out var s) ? s : Array.Empty<string>();
    }

    // -----------------------------------------------------------------
    // TryParseVersion (pure)
    // -----------------------------------------------------------------
    [Theory]
    [InlineData("PowerShell 7.4.2", 7, 4, 2)]
    [InlineData("Python 3.12.1", 3, 12, 1)]
    [InlineData("Python 3.9", 3, 9, 0)]
    [InlineData("7.2.0", 7, 2, 0)]
    [InlineData("Python 3.13.0a1", 3, 13, 0)]
    public void TryParseVersion_ParsesDottedVersions(string text, int maj, int min, int bld)
    {
        Assert.True(PrerequisiteDetector.TryParseVersion(text, out var v));
        Assert.Equal(maj, v.Major);
        Assert.Equal(min, v.Minor);
        Assert.Equal(bld, v.Build < 0 ? 0 : v.Build);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no version here")]
    public void TryParseVersion_RejectsGarbage(string? text)
        => Assert.False(PrerequisiteDetector.TryParseVersion(text, out _));

    // -----------------------------------------------------------------
    // PowerShell 7
    // -----------------------------------------------------------------
    [Fact]
    public void Ps7_FoundOnPath_WithGoodVersion_IsSatisfied()
    {
        var f = new FakeProbe();
        f.OnPath["pwsh.exe"] = @"C:\PSPath\pwsh.exe";
        f.Versions[@"C:\PSPath\pwsh.exe"] = "PowerShell 7.4.2";

        var s = new PrerequisiteDetector(f).DetectPowerShell7();

        Assert.True(s.Satisfied);
        Assert.Equal("7.4.2", s.DetectedVersion);
        Assert.Equal("PATH", s.DetectionSource);
        Assert.Equal(@"C:\PSPath\pwsh.exe", s.Path);
    }

    [Fact]
    public void Ps7_NotFoundAnywhere_IsMissing()
    {
        var s = new PrerequisiteDetector(new FakeProbe()).DetectPowerShell7();
        Assert.False(s.Satisfied);
        Assert.Null(s.DetectedVersion);
        Assert.Equal("not-found", s.DetectionSource);
    }

    [Fact]
    public void Ps7_TooOldOnPath_ButNewerInProgramFiles_PrefersSatisfied()
    {
        var f = new FakeProbe();
        f.OnPath["pwsh.exe"] = @"C:\Old\pwsh.exe";
        f.Versions[@"C:\Old\pwsh.exe"] = "PowerShell 7.0.1"; // too old (< 7.2)
        f.Env["ProgramFiles"] = @"C:\Program Files";
        var pf = @"C:\Program Files\PowerShell\7\pwsh.exe";
        f.Files.Add(pf);
        f.Versions[pf] = "PowerShell 7.4.0";

        var s = new PrerequisiteDetector(f).DetectPowerShell7();

        Assert.True(s.Satisfied);
        // 7.4.0 formats without the trailing .0 build component.
        Assert.Equal("7.4", s.DetectedVersion);
        Assert.Equal("ProgramFiles", s.DetectionSource);
    }

    [Fact]
    public void Ps7_OnlyTooOld_ReportsTooOldNotMissing()
    {
        var f = new FakeProbe();
        f.OnPath["pwsh.exe"] = @"C:\Old\pwsh.exe";
        f.Versions[@"C:\Old\pwsh.exe"] = "PowerShell 7.1.5";

        var s = new PrerequisiteDetector(f).DetectPowerShell7();

        Assert.False(s.Satisfied);
        Assert.Equal("7.1.5", s.DetectedVersion);
        Assert.Equal("too-old", s.DetectionSource);
    }

    [Fact]
    public void Ps7_AtExactMinimum_IsSatisfied()
    {
        var f = new FakeProbe();
        f.OnPath["pwsh.exe"] = @"C:\PS\pwsh.exe";
        f.Versions[@"C:\PS\pwsh.exe"] = "PowerShell 7.2.0";

        Assert.True(new PrerequisiteDetector(f).DetectPowerShell7().Satisfied);
    }

    [Fact]
    public void Ps7_LocatedButVersionUnreadable_IsNotConfirmed()
    {
        var f = new FakeProbe();
        f.OnPath["pwsh.exe"] = @"C:\PS\pwsh.exe"; // present, but RunVersion returns null

        var s = new PrerequisiteDetector(f).DetectPowerShell7();

        // Version could not be confirmed and there is no other candidate, so
        // the requirement is not claimed as met.
        Assert.False(s.Satisfied);
        Assert.Equal("not-found", s.DetectionSource);
    }

    [Fact]
    public void Ps7_FromRegistryOnly_IsSatisfied()
    {
        var f = new FakeProbe();
        const string root = @"SOFTWARE\Microsoft\PowerShellCore\InstalledVersions";
        f.HklmSubKeys[root] = new[] { "{guid-1}" };
        f.Hklm[(root + @"\{guid-1}", "SemanticVersion")] = "7.3.6";
        f.Hklm[(root + @"\{guid-1}", "InstallLocation")] = @"C:\PS7";

        var s = new PrerequisiteDetector(f).DetectPowerShell7();

        Assert.True(s.Satisfied);
        Assert.Equal("7.3.6", s.DetectedVersion);
        Assert.Equal("Registry", s.DetectionSource);
    }

    // -----------------------------------------------------------------
    // Python
    // -----------------------------------------------------------------
    [Fact]
    public void Python_FoundOnPath_IsSatisfied()
    {
        var f = new FakeProbe();
        f.OnPath["python.exe"] = @"C:\Py\python.exe";
        f.Versions[@"C:\Py\python.exe"] = "Python 3.12.1";

        var s = new PrerequisiteDetector(f).DetectPython();

        Assert.True(s.Satisfied);
        Assert.Equal("3.12.1", s.DetectedVersion);
        Assert.Equal("PATH", s.DetectionSource);
    }

    [Fact]
    public void Python_BelowMinimum_ReportsTooOld()
    {
        var f = new FakeProbe();
        f.OnPath["python.exe"] = @"C:\Py\python.exe";
        f.Versions[@"C:\Py\python.exe"] = "Python 3.8.10";

        var s = new PrerequisiteDetector(f).DetectPython();

        Assert.False(s.Satisfied);
        Assert.Equal("3.8.10", s.DetectedVersion);
        Assert.Equal("too-old", s.DetectionSource);
    }

    [Fact]
    public void Python_StoreStubPrintsNothing_FallsThroughToLocalAppData()
    {
        var f = new FakeProbe();
        // python.exe on PATH is the Windows Store execution-alias stub:
        // located, but --version yields no output (RunVersion null). The
        // detector must NOT claim it as satisfied; it must fall through to
        // the real per-user install under %LOCALAPPDATA%.
        f.OnPath["python.exe"] = @"C:\Users\u\AppData\Local\Microsoft\WindowsApps\python.exe";
        // (no Versions entry -> RunVersion returns null for the stub)
        f.Env["LOCALAPPDATA"] = @"C:\Users\u\AppData\Local";
        var parent = @"C:\Users\u\AppData\Local\Programs\Python";
        f.Dirs[(parent, "Python3*")] = new[] { parent + @"\Python312" };
        var py = parent + @"\Python312\python.exe";
        f.Files.Add(py);
        f.Versions[py] = "Python 3.12.4";

        var s = new PrerequisiteDetector(f).DetectPython();

        Assert.True(s.Satisfied);
        Assert.Equal("3.12.4", s.DetectedVersion);
        Assert.Equal("LocalAppData", s.DetectionSource);
    }

    [Fact]
    public void Python_OnlyStoreStubOnPath_IsMissing()
    {
        var f = new FakeProbe();
        // Only the version-less Store stub is present anywhere.
        f.OnPath["python.exe"] = @"C:\Users\u\AppData\Local\Microsoft\WindowsApps\python.exe";

        var s = new PrerequisiteDetector(f).DetectPython();

        Assert.False(s.Satisfied);
        Assert.Equal("not-found", s.DetectionSource);
    }

    [Fact]
    public void Python_ViaPyLauncher_IsSatisfied()
    {
        var f = new FakeProbe();
        f.OnPath["py.exe"] = @"C:\Windows\py.exe";
        f.Versions[@"C:\Windows\py.exe"] = "Python 3.11.9";

        var s = new PrerequisiteDetector(f).DetectPython();

        Assert.True(s.Satisfied);
        Assert.Equal("3.11.9", s.DetectedVersion);
        Assert.Equal("py-launcher", s.DetectionSource);
    }

    [Fact]
    public void Python_NotFound_IsMissing()
    {
        var s = new PrerequisiteDetector(new FakeProbe()).DetectPython();
        Assert.False(s.Satisfied);
        Assert.Equal("not-found", s.DetectionSource);
    }

    // -----------------------------------------------------------------
    // .NET 8 Desktop Runtime — multi-strategy detection
    // (registry / `dotnet --list-runtimes` / well-known shared-framework folder)
    // -----------------------------------------------------------------
    private const string DotNetRegRoot =
        @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App";
    private const string DotNetSharedRoot =
        @"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App";

    [Fact]
    public void DotNet8_FoundInRegistry_IsSatisfied()
    {
        var f = new FakeProbe();
        f.HklmSubKeys[DotNetRegRoot] = new[] { "8.0.11" };

        var s = new PrerequisiteDetector(f).DetectDotNet8DesktopRuntime();

        Assert.True(s.Satisfied);
        Assert.Equal("8.0.11", s.DetectedVersion);
        Assert.Equal("Registry", s.DetectionSource);
    }

    [Fact]
    public void DotNet8_Registry_PicksHighest8x_NotStringSort()
    {
        var f = new FakeProbe();
        // String ordering would wrongly rank "8.0.9" above "8.0.11";
        // version comparison must select 8.0.11.
        f.HklmSubKeys[DotNetRegRoot] = new[] { "8.0.9", "8.0.11", "6.0.30" };

        var s = new PrerequisiteDetector(f).DetectDotNet8DesktopRuntime();

        Assert.True(s.Satisfied);
        Assert.Equal("8.0.11", s.DetectedVersion);
        Assert.Equal("Registry", s.DetectionSource);
    }

    [Fact]
    public void DotNet8_NotInRegistry_FoundViaListRuntimes_IsSatisfied()
    {
        var f = new FakeProbe();
        // Registry empty (winget / Visual Studio / SDK install).
        // `dotnet --list-runtimes` reports the WindowsDesktop runtime.
        f.Versions["dotnet"] =
            "Microsoft.AspNetCore.App 8.0.11 [C:\\Program Files\\dotnet\\shared\\Microsoft.AspNetCore.App]\n" +
            "Microsoft.NETCore.App 8.0.11 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]\n" +
            "Microsoft.WindowsDesktop.App 8.0.11 [C:\\Program Files\\dotnet\\shared\\Microsoft.WindowsDesktop.App]";

        var s = new PrerequisiteDetector(f).DetectDotNet8DesktopRuntime();

        Assert.True(s.Satisfied);
        Assert.Equal("8.0.11", s.DetectedVersion);
        Assert.Equal("dotnet --list-runtimes", s.DetectionSource);
    }

    [Fact]
    public void DotNet8_ListRuntimes_IgnoresNon8_PicksHighestDesktop()
    {
        var f = new FakeProbe();
        f.Versions["dotnet"] =
            "Microsoft.WindowsDesktop.App 6.0.30 [C:\\x]\n" +
            "Microsoft.WindowsDesktop.App 8.0.8 [C:\\x]\n" +
            "Microsoft.WindowsDesktop.App 8.0.28 [C:\\x]\n" +
            "Microsoft.NETCore.App 9.0.0 [C:\\x]";

        var s = new PrerequisiteDetector(f).DetectDotNet8DesktopRuntime();

        Assert.True(s.Satisfied);
        Assert.Equal("8.0.28", s.DetectedVersion);
        Assert.Equal("dotnet --list-runtimes", s.DetectionSource);
    }

    [Fact]
    public void DotNet8_ListRuntimes_OnlyNonDesktop8_IsMissing()
    {
        var f = new FakeProbe();
        // 8.x present, but not the WindowsDesktop runtime PAX Cookbook needs.
        f.Versions["dotnet"] =
            "Microsoft.NETCore.App 8.0.11 [C:\\x]\n" +
            "Microsoft.AspNetCore.App 8.0.11 [C:\\x]";

        var s = new PrerequisiteDetector(f).DetectDotNet8DesktopRuntime();

        Assert.False(s.Satisfied);
        Assert.Equal("not-found", s.DetectionSource);
    }

    [Fact]
    public void DotNet8_FoundViaWellKnownFolder_IsSatisfied()
    {
        var f = new FakeProbe();
        f.Env["ProgramFiles"] = @"C:\Program Files";
        f.Dirs[(DotNetSharedRoot, "8.*")] = new[]
        {
            DotNetSharedRoot + @"\8.0.11",
            DotNetSharedRoot + @"\8.0.28",
        };

        var s = new PrerequisiteDetector(f).DetectDotNet8DesktopRuntime();

        Assert.True(s.Satisfied);
        Assert.Equal("8.0.28", s.DetectedVersion);
        Assert.Equal("ProgramFiles", s.DetectionSource);
    }

    [Fact]
    public void DotNet8_RegistryPreferredOverListRuntimes()
    {
        var f = new FakeProbe();
        f.HklmSubKeys[DotNetRegRoot] = new[] { "8.0.11" };
        f.Versions["dotnet"] = "Microsoft.WindowsDesktop.App 8.0.28 [C:\\x]";

        var s = new PrerequisiteDetector(f).DetectDotNet8DesktopRuntime();

        Assert.True(s.Satisfied);
        Assert.Equal("Registry", s.DetectionSource); // strategy 1 short-circuits
        Assert.Equal("8.0.11", s.DetectedVersion);
    }

    [Fact]
    public void DotNet8_NotFoundAnywhere_IsMissing()
    {
        var s = new PrerequisiteDetector(new FakeProbe()).DetectDotNet8DesktopRuntime();
        Assert.False(s.Satisfied);
        Assert.Null(s.DetectedVersion);
        Assert.Equal("not-found", s.DetectionSource);
    }

    // -----------------------------------------------------------------
    // Display lines
    // -----------------------------------------------------------------
    [Fact]
    public void DisplayLine_Reflects_State()
    {
        var found = new PrerequisiteStatus(PrerequisiteKind.Python, "Python", true, "3.12.1", @"C:\p", "PATH");
        Assert.Contains("found (v3.12.1)", found.ToDisplayLine());

        var missing = PrerequisiteStatus.Missing(PrerequisiteKind.PowerShell7, "PowerShell 7");
        Assert.Contains("not found", missing.ToDisplayLine());

        var tooOld = new PrerequisiteStatus(PrerequisiteKind.PowerShell7, "PowerShell 7", false, "7.0.1", null, "too-old");
        Assert.Contains("will be installed", tooOld.ToDisplayLine());
    }

    [Fact]
    public void Detector_SearchOrder_PrefersPathOverProgramFiles()
    {
        var f = new FakeProbe();
        f.OnPath["pwsh.exe"] = @"C:\OnPath\pwsh.exe";
        f.Versions[@"C:\OnPath\pwsh.exe"] = "PowerShell 7.4.2";
        f.Env["ProgramFiles"] = @"C:\Program Files";
        var pf = @"C:\Program Files\PowerShell\7\pwsh.exe";
        f.Files.Add(pf);
        f.Versions[pf] = "PowerShell 7.5.0";

        var s = new PrerequisiteDetector(f).DetectPowerShell7();

        // PATH is probed first and already satisfies, so it wins.
        Assert.Equal("PATH", s.DetectionSource);
        Assert.Equal(@"C:\OnPath\pwsh.exe", s.Path);
    }
}
