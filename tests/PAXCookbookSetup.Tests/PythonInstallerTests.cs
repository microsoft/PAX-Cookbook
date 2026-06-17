using System;
using System.Collections.Generic;
using PAXCookbookSetup.Gui;
using Xunit;

namespace PAXCookbookSetup.Tests;

// Slice C: unit tests for the Python installer component. Per-user install (no
// elevation). Pure helpers + orchestration exercised with fakes — no network,
// no real process.
public class PythonInstallerTests
{
    private sealed class FakeDownloader : IPrereqDownloader
    {
        public bool DownloadResult = true;
        public string? LastDownloadUrl;
        public string? GetText(string url, string? accept = null) => null;
        public bool DownloadFile(string url, string destPath) { LastDownloadUrl = url; return DownloadResult; }
    }

    private sealed class FakeSilentLauncher : ISilentLauncher
    {
        public SilentLaunchResult Result = SilentLaunchResult.Ran(0);
        public string? LastFileName;
        public string? LastArguments;
        public Action? OnRun;
        public SilentLaunchResult RunAndWait(string fileName, string arguments, int timeoutMs)
        {
            LastFileName = fileName; LastArguments = arguments; OnRun?.Invoke(); return Result;
        }
    }

    private sealed class TogglePythonProbe : IPrerequisiteProbe
    {
        public bool PythonPresent;
        public string? ResolveOnPath(string exeName)
            => (PythonPresent && exeName == "python.exe") ? @"C:\Py\python.exe" : null;
        public bool FileExists(string path) => false;
        public IEnumerable<string> EnumerateDirectories(string parent, string pattern) => Array.Empty<string>();
        public string? GetEnvPath(string envVarName) => null;
        public string? RunVersion(string exePath, string arguments) => PythonPresent ? "Python 3.12.8" : null;
        public string? ReadHklmString(string subKey, string? valueName) => null;
        public IEnumerable<string> EnumerateHklmSubKeyNames(string subKey) => Array.Empty<string>();
    }

    private static PythonInstaller Build(FakeDownloader d, FakeSilentLauncher e, TogglePythonProbe probe)
        => new(d, e, new PrerequisiteDetector(probe));

    // -----------------------------------------------------------------
    // Pure helpers
    // -----------------------------------------------------------------
    [Fact]
    public void BuildArguments_IsPerUserSilentOnPath()
    {
        var a = PythonInstaller.BuildInstallerArguments();
        Assert.Contains("/quiet", a);
        Assert.Contains("InstallAllUsers=0", a);   // per-user, no admin
        Assert.Contains("PrependPath=1", a);
        Assert.Contains("Include_test=0", a);
    }

    [Fact]
    public void InstallerUrl_IsAllowListedPythonOrgAmd64()
    {
        Assert.True(PrereqDownloadHosts.IsAllowed(PythonInstaller.InstallerUrl));
        Assert.EndsWith("-amd64.exe", PythonInstaller.InstallerUrl);
        Assert.Contains("python.org", PythonInstaller.InstallerUrl);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3010, true)]
    [InlineData(1603, false)]
    [InlineData(1602, false)]
    public void IsInstallSuccess_Classifies(int code, bool ok)
        => Assert.Equal(ok, PythonInstaller.IsInstallSuccess(code));

    // -----------------------------------------------------------------
    // Orchestration
    // -----------------------------------------------------------------
    [Fact]
    public void Install_AlreadyPresent_SkipsDownload()
    {
        var d = new FakeDownloader();
        var e = new FakeSilentLauncher();
        var probe = new TogglePythonProbe { PythonPresent = true };

        var r = Build(d, e, probe).Install(TempDir(), _ => { });

        Assert.Equal(PrerequisiteInstallOutcome.AlreadyPresent, r.Outcome);
        Assert.Null(d.LastDownloadUrl);
        Assert.Null(e.LastArguments);
    }

    [Fact]
    public void Install_HappyPath_DownloadsRunsAndVerifies()
    {
        var d = new FakeDownloader { DownloadResult = true };
        var probe = new TogglePythonProbe { PythonPresent = false };
        var e = new FakeSilentLauncher { Result = SilentLaunchResult.Ran(0) };
        e.OnRun = () => probe.PythonPresent = true;

        var r = Build(d, e, probe).Install(TempDir(), _ => { });

        Assert.Equal(PrerequisiteInstallOutcome.Installed, r.Outcome);
        Assert.EndsWith("installed successfully.", r.Message);
        Assert.Equal(PythonInstaller.InstallerUrl, d.LastDownloadUrl);
        Assert.Contains("InstallAllUsers=0", e.LastArguments);
    }

    [Fact]
    public void Install_NoElevation_LaunchesPerUser()
    {
        // The installer exe (not msiexec) is launched directly via the silent
        // (non-elevated) launcher — proving the per-user, no-admin path.
        var d = new FakeDownloader { DownloadResult = true };
        var probe = new TogglePythonProbe { PythonPresent = false };
        var e = new FakeSilentLauncher { Result = SilentLaunchResult.Ran(0) };

        Build(d, e, probe).Install(TempDir(), _ => { });

        Assert.NotNull(e.LastFileName);
        Assert.EndsWith("python-amd64.exe", e.LastFileName);
    }

    [Fact]
    public void Install_DownloadFails_ReturnsFailed()
    {
        var d = new FakeDownloader { DownloadResult = false };
        var e = new FakeSilentLauncher();
        var probe = new TogglePythonProbe { PythonPresent = false };

        var r = Build(d, e, probe).Install(TempDir(), _ => { });

        Assert.Equal(PrerequisiteInstallOutcome.Failed, r.Outcome);
        Assert.Null(e.LastArguments);
    }

    [Fact]
    public void Install_BadExitCode_ReturnsFailed()
    {
        var d = new FakeDownloader { DownloadResult = true };
        var e = new FakeSilentLauncher { Result = SilentLaunchResult.Ran(1603) };
        var probe = new TogglePythonProbe { PythonPresent = false };

        var r = Build(d, e, probe).Install(TempDir(), _ => { });

        Assert.Equal(PrerequisiteInstallOutcome.Failed, r.Outcome);
        Assert.Contains("1603", r.Message);
    }

    [Fact]
    public void Install_InstalledButUndetected_ReportsInstalledWithNote()
    {
        var d = new FakeDownloader { DownloadResult = true };
        var e = new FakeSilentLauncher { Result = SilentLaunchResult.Ran(0) };
        var probe = new TogglePythonProbe { PythonPresent = false }; // stays invisible to this process

        var r = Build(d, e, probe).Install(TempDir(), _ => { });

        Assert.Equal(PrerequisiteInstallOutcome.Installed, r.Outcome);
        Assert.Contains("sign out", r.Message);
    }

    private static string TempDir()
        => System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "PAXPyTest_" + Guid.NewGuid().ToString("N").Substring(0, 10));
}
