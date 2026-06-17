using System;
using System.Collections.Generic;
using PAXCookbookSetup.Gui;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Slice B: unit tests for the PowerShell 7 installer component. The pure
// helpers (GitHub asset selection, msiexec args, MSI exit-code classification,
// host allow-list) plus the orchestration flow are exercised with fakes — no
// network, no elevation, no real process.
public class PowerShell7InstallerTests
{
    // -----------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------
    private sealed class FakeDownloader : IPrereqDownloader
    {
        public string? TextResult;
        public bool DownloadResult = true;
        public string? LastDownloadUrl;
        public string? LastTextUrl;
        public string? GetText(string url, string? accept = null) { LastTextUrl = url; return TextResult; }
        public bool DownloadFile(string url, string destPath) { LastDownloadUrl = url; return DownloadResult; }
    }

    private sealed class FakeElevatedLauncher : IElevatedLauncher
    {
        public ElevatedLaunchResult Result = ElevatedLaunchResult.Ran(0);
        public string? LastFileName;
        public string? LastArguments;
        public Action? OnRun;   // fired when the elevated launch is invoked
        public ElevatedLaunchResult RunElevatedAndWait(string fileName, string arguments, int timeoutMs)
        {
            LastFileName = fileName; LastArguments = arguments;
            OnRun?.Invoke();
            return Result;
        }
    }

    // Probe that reports PowerShell 7 present or absent on demand.
    private sealed class TogglePs7Probe : IPrerequisiteProbe
    {
        public bool Ps7Present;
        public string? ResolveOnPath(string exeName)
            => (Ps7Present && exeName == "pwsh.exe") ? @"C:\PS\pwsh.exe" : null;
        public bool FileExists(string path) => false;
        public IEnumerable<string> EnumerateDirectories(string parent, string pattern) => Array.Empty<string>();
        public string? GetEnvPath(string envVarName) => null;
        public string? RunVersion(string exePath, string arguments) => Ps7Present ? "PowerShell 7.4.6" : null;
        public string? ReadHklmString(string subKey, string? valueName) => null;
        public IEnumerable<string> EnumerateHklmSubKeyNames(string subKey) => Array.Empty<string>();
    }

    private static PowerShell7Installer Build(FakeDownloader d, FakeElevatedLauncher e, TogglePs7Probe probe)
        => new(d, e, new PrerequisiteDetector(probe));

    private static readonly string RealisticReleaseJson = """
    {
      "tag_name": "v7.4.6",
      "assets": [
        { "name": "PowerShell-7.4.6-win-arm64.msi", "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.4.6/PowerShell-7.4.6-win-arm64.msi" },
        { "name": "PowerShell-7.4.6-win-x86.msi",   "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.4.6/PowerShell-7.4.6-win-x86.msi" },
        { "name": "PowerShell-7.4.6-win-x64.zip",   "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.4.6/PowerShell-7.4.6-win-x64.zip" },
        { "name": "PowerShell-7.4.6-win-x64.msi",   "browser_download_url": "https://github.com/PowerShell/PowerShell/releases/download/v7.4.6/PowerShell-7.4.6-win-x64.msi" }
      ]
    }
    """;

    // -----------------------------------------------------------------
    // Pure helpers
    // -----------------------------------------------------------------
    [Fact]
    public void SelectMsi_PicksWinX64FromRealisticJson()
    {
        var url = PowerShell7Installer.TrySelectWinX64MsiUrl(RealisticReleaseJson);
        Assert.Equal(
            "https://github.com/PowerShell/PowerShell/releases/download/v7.4.6/PowerShell-7.4.6-win-x64.msi",
            url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"assets\": [] }")]
    [InlineData("{ \"assets\": [ { \"name\": \"PowerShell-7.4.6-win-arm64.msi\", \"browser_download_url\": \"https://github.com/x.msi\" } ] }")]
    public void SelectMsi_ReturnsNullWhenNoWinX64Msi(string json)
        => Assert.Null(PowerShell7Installer.TrySelectWinX64MsiUrl(json));

    [Fact]
    public void SelectMsi_RejectsNonGitHubAssetUrl()
    {
        // A poisoned asset URL pointing off-host must be rejected by the allow-list.
        const string json = """
        { "assets": [ { "name": "PowerShell-7.4.6-win-x64.msi", "browser_download_url": "https://evil.example.com/PowerShell-7.4.6-win-x64.msi" } ] }
        """;
        Assert.Null(PowerShell7Installer.TrySelectWinX64MsiUrl(json));
    }

    [Fact]
    public void BuildMsiArguments_HasSilentAndConsumerOptions()
    {
        var args = PowerShell7Installer.BuildMsiArguments(@"C:\tmp\ps.msi");
        Assert.Contains("/i \"C:\\tmp\\ps.msi\"", args);
        Assert.Contains("/qn", args);
        Assert.Contains("/norestart", args);
        Assert.Contains("ADD_EXPLORER_CONTEXT_MENU_OPENPOWERSHELL=0", args);
        Assert.Contains("ADD_FILE_CONTEXT_MENU_RUNPOWERSHELL=0", args);
        Assert.Contains("ENABLE_PSREMOTING=0", args);
        Assert.Contains("REGISTER_MANIFEST=1", args);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3010, true)]
    [InlineData(1603, false)]
    [InlineData(1602, false)]
    public void IsMsiSuccess_Classifies(int code, bool ok)
        => Assert.Equal(ok, PowerShell7Installer.IsMsiSuccess(code));

    [Theory]
    [InlineData("https://github.com/PowerShell/x.msi", true)]
    [InlineData("https://objects.githubusercontent.com/x.msi", true)]
    [InlineData("https://www.python.org/ftp/python/x.exe", true)]
    [InlineData("http://github.com/x.msi", false)]      // not https
    [InlineData("https://evil.com/x.msi", false)]        // wrong host
    [InlineData("https://github.com.evil.com/x.msi", false)] // suffix-spoof
    [InlineData("https://evilgithub.com/x.msi", false)]  // substring-spoof (no dot boundary)
    [InlineData("https://github.com@evil.com/x.msi", false)] // userinfo trick -> Host is evil.com
    [InlineData("ftp://python.org/x", false)]
    [InlineData(null, false)]
    public void DownloadHosts_AllowList(string? url, bool allowed)
        => Assert.Equal(allowed, PrereqDownloadHosts.IsAllowed(url));

    // -----------------------------------------------------------------
    // Orchestration
    // -----------------------------------------------------------------
    [Fact]
    public void Install_AlreadyPresent_SkipsDownload()
    {
        var d = new FakeDownloader();
        var e = new FakeElevatedLauncher();
        var probe = new TogglePs7Probe { Ps7Present = true };

        var r = Build(d, e, probe).Install(TempDir(), _ => { });

        Assert.Equal(PrerequisiteInstallOutcome.AlreadyPresent, r.Outcome);
        Assert.Null(d.LastDownloadUrl);   // never downloaded
        Assert.Null(e.LastArguments);     // never launched
    }

    [Fact]
    public void Install_HappyPath_DownloadsRunsAndVerifies()
    {
        var d = new FakeDownloader { TextResult = RealisticReleaseJson, DownloadResult = true };
        var probe = new TogglePs7Probe { Ps7Present = false }; // absent before install
        var e = new FakeElevatedLauncher { Result = ElevatedLaunchResult.Ran(0) };
        // A successful elevated install makes PowerShell 7 visible to re-detection.
        e.OnRun = () => probe.Ps7Present = true;

        var r = Build(d, e, probe).Install(TempDir(), _ => { });

        Assert.Equal(PrerequisiteInstallOutcome.Installed, r.Outcome);
        Assert.EndsWith("installed successfully.", r.Message);
        Assert.Equal("msiexec.exe", e.LastFileName);
        Assert.Contains("win-x64.msi", d.LastDownloadUrl);
    }

    [Fact]
    public void Install_UacDeclined_ReturnsDeclined()
    {
        var d = new FakeDownloader { TextResult = RealisticReleaseJson, DownloadResult = true };
        var e = new FakeElevatedLauncher { Result = ElevatedLaunchResult.Declined() };
        var probe = new TogglePs7Probe { Ps7Present = false };

        var r = Build(d, e, probe).Install(TempDir(), _ => { });

        Assert.Equal(PrerequisiteInstallOutcome.UserDeclined, r.Outcome);
    }

    [Fact]
    public void Install_DownloadFails_ReturnsFailed()
    {
        var d = new FakeDownloader { TextResult = RealisticReleaseJson, DownloadResult = false };
        var e = new FakeElevatedLauncher();
        var probe = new TogglePs7Probe { Ps7Present = false };

        var r = Build(d, e, probe).Install(TempDir(), _ => { });

        Assert.Equal(PrerequisiteInstallOutcome.Failed, r.Outcome);
        Assert.Null(e.LastArguments); // never reached the elevated launch
    }

    [Fact]
    public void Install_ApiUnreachable_UsesFallbackUrl()
    {
        // No JSON from the API -> the installer must fall back to the known-good URL.
        var d = new FakeDownloader { TextResult = null, DownloadResult = true };
        var e = new FakeElevatedLauncher { Result = ElevatedLaunchResult.Ran(0) };
        var probe = new TogglePs7Probe { Ps7Present = false };

        var r = Build(d, e, probe).Install(TempDir(), _ => { });

        Assert.Equal(PowerShell7Installer.FallbackMsiUrl, d.LastDownloadUrl);
        // Installed-but-undetected still reports Installed (success-with-note).
        Assert.Equal(PrerequisiteInstallOutcome.Installed, r.Outcome);
    }

    [Fact]
    public void Install_MsiFailsExitCode_ReturnsFailed()
    {
        var d = new FakeDownloader { TextResult = RealisticReleaseJson, DownloadResult = true };
        var e = new FakeElevatedLauncher { Result = ElevatedLaunchResult.Ran(1603) };
        var probe = new TogglePs7Probe { Ps7Present = false };

        var r = Build(d, e, probe).Install(TempDir(), _ => { });

        Assert.Equal(PrerequisiteInstallOutcome.Failed, r.Outcome);
        Assert.Contains("1603", r.Message);
    }

    private static string TempDir()
        => System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "PAXPrereqTest_" + Guid.NewGuid().ToString("N").Substring(0, 10));
}
