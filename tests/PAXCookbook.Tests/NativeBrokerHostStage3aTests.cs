using System.Net.Http;
using System.Text.Json;
using PAXCookbook.Broker.Native;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3a parity tests for the native broker host. These exercise
// only the in-process Kestrel host (no PowerShell broker, no
// workspace.lock, no WebView2). Subsequent sub-stages add routes
// and parity diffs against the PowerShell broker.
//
// The xUnit Collection serializes execution against the Stage 3b
// class. Both bind to port 17654 (with fallback through 17664), so
// parallel execution races on the same socket.
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3aTests
{
    private static NativeBrokerHostOptions OptionsForTest() =>
        new(
            PreferredPort: 0,
            PortRangeStart: 17654,
            PortRangeEnd: 17664,
            WorkspaceFolderPath: @"C:\Users\test\PAXCookbook\Workspace");

    [Fact]
    public void SelectPort_picks_a_port_in_range()
    {
        var port = NativeBrokerHost.SelectPort(17654, 17654, 17664);
        Assert.InRange(port, 17654, 17664);
    }

    [Fact]
    public void SelectPort_rejects_inverted_range()
    {
        Assert.Throws<ArgumentException>(() =>
            NativeBrokerHost.SelectPort(17654, 17664, 17654));
    }

    [Fact]
    public async Task Host_starts_and_health_returns_native_envelope()
    {
        await using var host = new NativeBrokerHost(OptionsForTest());
        var start = await host.StartAsync();
        Assert.InRange(start.Port, 17654, 17664);
        Assert.Equal("http://localhost:" + start.Port, start.BaseUrl);

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = await http.GetStringAsync("/api/v1/health");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal("ok", root.GetProperty("status").GetString());
            Assert.Equal("native", root.GetProperty("broker").GetString());
            Assert.Equal(start.Port, root.GetProperty("port").GetInt32());
            Assert.True(root.GetProperty("pid").GetInt32() > 0);
            Assert.True(root.TryGetProperty("workspaceFolderPath", out _));
            Assert.True(root.TryGetProperty("timestamp", out _));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Host_binds_both_localhost_and_127_0_0_1()
    {
        await using var host = new NativeBrokerHost(OptionsForTest());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient();
            var ipv4 = await http.GetStringAsync(
                "http://127.0.0.1:" + start.Port + "/api/v1/health");
            var localhost = await http.GetStringAsync(
                "http://localhost:" + start.Port + "/api/v1/health");
            Assert.Contains("\"broker\":\"native\"", ipv4);
            Assert.Contains("\"broker\":\"native\"", localhost);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
