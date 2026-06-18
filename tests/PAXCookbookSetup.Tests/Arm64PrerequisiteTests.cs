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
