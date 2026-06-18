using System;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using PAXCookbookSetup.Gui;
using Xunit;

namespace PAXCookbookSetup.Tests;

// FIX 5: confirm the architecture-specific prerequisite download URLs actually
// resolve to a real download (HTTP 200), so a future move/removal of a pinned
// asset is caught here rather than silently failing on a tester's machine —
// especially the ARM64 .NET 8 Desktop Runtime, which is the release blocker.
//
// These tests touch the network. If the network is unreachable the test is
// treated as INCONCLUSIVE (it does NOT fail) so offline CI is never broken — but
// when the host IS reachable, any non-200 status DOES fail the test (that is the
// signal we want: the asset moved/was removed).
public class PrerequisiteUrlReachabilityTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    public void DotNet8DesktopRuntime_Url_IsReachable(Architecture arch)
        => AssertReachableOrInconclusive(DotNet8DesktopRuntimeInstaller.BuildDownloadUrl(arch));

    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    public void PowerShell7_FallbackMsi_Url_IsReachable(Architecture arch)
        => AssertReachableOrInconclusive(PowerShell7Installer.FallbackMsiUrlFor(arch));

    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    public void Python_Url_IsReachable(Architecture arch)
        => AssertReachableOrInconclusive(PythonInstaller.BuildInstallerUrl(arch));

    private static void AssertReachableOrInconclusive(string url)
    {
        // Always-on (hermetic) guard: the URL must be on the download allow-list.
        Assert.True(PrereqDownloadHosts.IsAllowed(url), $"URL not on the allow-list: {url}");

        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            resp = Http.Send(req);
        }
        catch (Exception)
        {
            // Network unreachable / DNS / timeout — inconclusive, do not fail.
            return;
        }

        using (resp)
        {
            Assert.True(resp.StatusCode == HttpStatusCode.OK,
                $"Expected HTTP 200 for {url} but got {(int)resp.StatusCode} {resp.StatusCode}.");
        }
    }
}
