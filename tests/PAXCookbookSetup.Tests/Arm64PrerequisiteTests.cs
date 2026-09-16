using System.Runtime.InteropServices;
using PAXCookbookSetup.Gui;
using Xunit;

namespace PAXCookbookSetup.Tests;

// ARM64 prerequisite coverage. Two internal testers on ARM64 Windows could
// install but not launch: the installer downloaded the x64 .NET 8 runtime
// (which lands under Program Files (x86)\dotnet) while the app launches through
// the native ARM64 dotnet.exe host (Program Files\dotnet), which then reports
// "No frameworks were found". These tests pin the architecture-aware URL +
// detection behaviour so each prerequisite is fetched for the right machine.
public class Arm64PrerequisiteTests
{
    private sealed class RuntimeDownloader : IPrereqDownloader
    {
        public string? Url;
        public string? GetText(string url, string? accept = null) => throw new System.InvalidOperationException();
        public bool DownloadFile(string url, string destPath) { Url = url; return true; }
    }

    private sealed class RuntimeLauncher : IElevatedLauncher
    {
        public int Calls;
        public ElevatedLaunchResult RunElevatedAndWait(string fileName, string arguments, int timeoutMs)
        {
            Calls++;
            return ElevatedLaunchResult.Ran(0);
        }
    }

    [Theory]
    [InlineData(Architecture.X64, "x64", false)]
    [InlineData(Architecture.X64, "x64", true)]
    [InlineData(Architecture.X86, "x86", false)]
    [InlineData(Architecture.X86, "x86", true)]
    public void ExplicitRuntimeInstaller_DetectsAndDownloadsSameTarget(Architecture target, string rid, bool aspNet)
    {
        var probe = new WizardDetectionTests.FakeProbe();
        string framework = aspNet ? "Microsoft.AspNetCore.App" : "Microsoft.WindowsDesktop.App";
        probe.HklmSubKeys[$@"SOFTWARE\dotnet\Setup\InstalledVersions\arm64\sharedfx\{framework}"] = new[] { "8.0.28" };
        var detector = new PrerequisiteDetector(probe, Architecture.Arm64);
        var downloader = new RuntimeDownloader();
        var launcher = new RuntimeLauncher();
        IPrerequisiteInstaller installer = aspNet
            ? new AspNetCoreRuntimeInstaller(downloader, launcher, detector, target)
            : new DotNet8DesktopRuntimeInstaller(downloader, launcher, detector, target);
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pax-runtime-test-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(installer.Install(tempDir, _ => { }).Satisfied);
            Assert.EndsWith($"-win-{rid}.exe", downloader.Url);
            Assert.Equal(1, launcher.Calls);
            probe.HklmSubKeys[$@"SOFTWARE\dotnet\Setup\InstalledVersions\{rid}\sharedfx\{framework}"] = new[] { "8.0.28" };
            Assert.Equal(PrerequisiteInstallOutcome.AlreadyPresent, installer.Install(tempDir, _ => { }).Outcome);
            Assert.Equal(1, launcher.Calls);
        }
        finally { if (System.IO.Directory.Exists(tempDir)) System.IO.Directory.Delete(tempDir, true); }
    }

    // -----------------------------------------------------------------
    // PrereqArch RID mapping
    // -----------------------------------------------------------------
    [Theory]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.Arm64, "arm64")]
    [InlineData(Architecture.X86, "x86")]
    public void PrereqArch_Rid_MapsArchitecture(Architecture arch, string expected)
        => Assert.Equal(expected, PrereqArch.Rid(arch));

    // -----------------------------------------------------------------
    // PrereqArch machine PROCESSOR_ARCHITECTURE mapping (the FIX: the real
    // hardware comes from the machine registry, not OSArchitecture, which an
    // x64-emulated process on ARM64 reports as X64).
    // -----------------------------------------------------------------
    [Theory]
    [InlineData("ARM64", Architecture.Arm64)]
    [InlineData("arm64", Architecture.Arm64)]   // case-insensitive
    [InlineData("AMD64", Architecture.X64)]
    [InlineData(" AMD64 ", Architecture.X64)]   // trimmed
    [InlineData("x86", Architecture.X86)]
    [InlineData("X86", Architecture.X86)]
    public void MapProcessorArchitecture_MapsKnownValues(string value, Architecture expected)
        => Assert.Equal(expected, PrereqArch.MapProcessorArchitecture(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("RISCV64")]
    [InlineData("IA64")]
    public void MapProcessorArchitecture_ReturnsNullForUnknown(string? value)
        => Assert.Null(PrereqArch.MapProcessorArchitecture(value));

    // -----------------------------------------------------------------
    // .NET 8 Desktop Runtime
    // -----------------------------------------------------------------
    [Fact]
    public void DotNet_BuildDownloadUrl_Arm64_PicksArm64Installer()
    {
        var url = DotNet8DesktopRuntimeInstaller.BuildDownloadUrl(Architecture.Arm64);
        Assert.EndsWith("windowsdesktop-runtime-8.0.11-win-arm64.exe", url);
        Assert.True(PrereqDownloadHosts.IsAllowed(url));
    }

    [Fact]
    public void DotNet_BuildDownloadUrl_X64_PicksX64Installer()
    {
        var url = DotNet8DesktopRuntimeInstaller.BuildDownloadUrl(Architecture.X64);
        Assert.EndsWith("windowsdesktop-runtime-8.0.11-win-x64.exe", url);
        Assert.True(PrereqDownloadHosts.IsAllowed(url));
    }

    // -----------------------------------------------------------------
    // ASP.NET Core 8 Runtime (the broker needs Microsoft.AspNetCore.App, which
    // the Desktop Runtime does not include)
    // -----------------------------------------------------------------
    [Fact]
    public void AspNetCore_BuildDownloadUrl_Arm64_PicksArm64Installer()
    {
        var url = AspNetCoreRuntimeInstaller.BuildDownloadUrl(Architecture.Arm64);
        Assert.EndsWith("aspnetcore-runtime-8.0.28-win-arm64.exe", url);
        Assert.True(PrereqDownloadHosts.IsAllowed(url));
    }

    [Fact]
    public void AspNetCore_BuildDownloadUrl_X64_PicksX64Installer()
    {
        var url = AspNetCoreRuntimeInstaller.BuildDownloadUrl(Architecture.X64);
        Assert.EndsWith("aspnetcore-runtime-8.0.28-win-x64.exe", url);
        Assert.True(PrereqDownloadHosts.IsAllowed(url));
    }

    [Fact]
    public void RuntimeDownloadUrls_DefaultPublicX86BehaviorIsPreserved()
    {
        Assert.Equal(DotNet8DesktopRuntimeInstaller.BuildDownloadUrl(Architecture.X64),
            DotNet8DesktopRuntimeInstaller.BuildDownloadUrl(Architecture.X86));
        Assert.Equal(AspNetCoreRuntimeInstaller.BuildDownloadUrl(Architecture.X64),
            AspNetCoreRuntimeInstaller.BuildDownloadUrl(Architecture.X86));
    }

    // -----------------------------------------------------------------
    // PowerShell 7
    // -----------------------------------------------------------------
    private const string Ps7ReleaseJson = """
    {
      "assets": [
        { "name": "PowerShell-7.4.6-win-arm64.msi", "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.4.6/PowerShell-7.4.6-win-arm64.msi" },
        { "name": "PowerShell-7.4.6-win-x64.msi",   "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.4.6/PowerShell-7.4.6-win-x64.msi" }
      ]
    }
    """;

    [Fact]
    public void PowerShell_SelectMsi_Arm64_PicksArm64Asset()
    {
        var url = PowerShell7Installer.TrySelectMsiUrl(Ps7ReleaseJson, Architecture.Arm64);
        Assert.EndsWith("PowerShell-7.4.6-win-arm64.msi", url);
        Assert.True(PrereqDownloadHosts.IsAllowed(url));
    }

    [Fact]
    public void PowerShell_SelectMsi_X64_PicksX64Asset()
    {
        var url = PowerShell7Installer.TrySelectMsiUrl(Ps7ReleaseJson, Architecture.X64);
        Assert.EndsWith("PowerShell-7.4.6-win-x64.msi", url);
    }

    [Fact]
    public void PowerShell_Fallback_Arm64_IsArm64Msi()
    {
        var url = PowerShell7Installer.FallbackMsiUrlFor(Architecture.Arm64);
        Assert.EndsWith("PowerShell-7.4.6-win-arm64.msi", url);
        Assert.True(PrereqDownloadHosts.IsAllowed(url));
    }

    [Fact]
    public void PowerShell_Fallback_X64_MatchesLegacyConstant()
        => Assert.Equal(PowerShell7Installer.FallbackMsiUrl,
                        PowerShell7Installer.FallbackMsiUrlFor(Architecture.X64));

    // -----------------------------------------------------------------
    // Python
    // -----------------------------------------------------------------
    [Fact]
    public void Python_BuildInstallerUrl_Arm64_PicksArm64Installer()
    {
        var url = PythonInstaller.BuildInstallerUrl(Architecture.Arm64);
        Assert.EndsWith("-arm64.exe", url);
        Assert.Contains("python.org", url);
        Assert.True(PrereqDownloadHosts.IsAllowed(url));
    }

    [Fact]
    public void Python_BuildInstallerUrl_X64_PicksAmd64Installer()
    {
        var url = PythonInstaller.BuildInstallerUrl(Architecture.X64);
        Assert.EndsWith("-amd64.exe", url);
        Assert.True(PrereqDownloadHosts.IsAllowed(url));
    }
}
