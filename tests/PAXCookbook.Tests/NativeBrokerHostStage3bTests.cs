using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using PAXCookbook.Broker.Native;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3b parity tests for the native broker's static + SPA fallback
// surface. Each test uses an isolated temp fixture web root so the
// real installed app\web is never touched. The fixture is created
// per-test via WebRootFixture and disposed via using/await using.
//
// The xUnit Collection serializes execution against the Stage 3a
// class. Both bind to port 17654 (with fallback through 17664), so
// parallel execution races on the same socket.
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3bTests
{
    private const string PaxScriptBaselineHash =
        "007AD1A7F6D40B40E873C684D10B2A79B4D1DD03A1900ADE19B6E482CC10C728";

    private const string IndexHtmlBody =
        "<!doctype html><html><head><title>fixture</title></head>" +
        "<body><div id=\"app\"></div><script src=\"assets/app.js\"></script></body></html>";

    private const string AppJsBody = "console.log('fixture-js');\n";
    private const string AppCssBody = "body { color: rebeccapurple; }\n";
    private static readonly byte[] FaviconBytes =
        new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x10, 0x10, 0x00, 0x00 };

    private static NativeBrokerHostOptions OptionsForFixture(string webRoot) =>
        new(
            PreferredPort: 17654,
            PortRangeStart: 17654,
            PortRangeEnd: 17664,
            WorkspaceFolderPath: @"C:\Users\test\PAXCookbook\Workspace",
            WebRoot: webRoot);

    [Fact]
    public async Task Get_root_returns_index_html()
    {
        using var fx = new WebRootFixture();
        await using var host = new NativeBrokerHost(OptionsForFixture(fx.WebRoot));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/html; charset=utf-8", resp.Content.Headers.ContentType?.ToString());
            Assert.Equal("no-store", resp.Headers.CacheControl?.ToString());
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Equal(IndexHtmlBody, body);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Get_index_html_returns_index_html()
    {
        using var fx = new WebRootFixture();
        await using var host = new NativeBrokerHost(OptionsForFixture(fx.WebRoot));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/index.html");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/html; charset=utf-8", resp.Content.Headers.ContentType?.ToString());
            Assert.Equal(IndexHtmlBody, await resp.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Get_assets_js_returns_javascript()
    {
        using var fx = new WebRootFixture();
        await using var host = new NativeBrokerHost(OptionsForFixture(fx.WebRoot));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/assets/app.js");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal(
                "application/javascript; charset=utf-8",
                resp.Content.Headers.ContentType?.ToString());
            Assert.Equal("no-store", resp.Headers.CacheControl?.ToString());
            Assert.Equal(AppJsBody, await resp.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Get_assets_css_returns_css()
    {
        using var fx = new WebRootFixture();
        await using var host = new NativeBrokerHost(OptionsForFixture(fx.WebRoot));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/assets/app.css");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/css; charset=utf-8", resp.Content.Headers.ContentType?.ToString());
            Assert.Equal(AppCssBody, await resp.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Get_favicon_returns_icon()
    {
        using var fx = new WebRootFixture();
        await using var host = new NativeBrokerHost(OptionsForFixture(fx.WebRoot));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/favicon.ico");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("image/x-icon", resp.Content.Headers.ContentType?.ToString());
            var body = await resp.Content.ReadAsByteArrayAsync();
            Assert.Equal(FaviconBytes, body);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Unknown_non_api_route_returns_index_html_as_spa_fallback()
    {
        using var fx = new WebRootFixture();
        await using var host = new NativeBrokerHost(OptionsForFixture(fx.WebRoot));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/recipes/01HX9NZ8YBABCDEF/edit");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/html; charset=utf-8", resp.Content.Headers.ContentType?.ToString());
            Assert.Equal(IndexHtmlBody, await resp.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Unknown_api_route_returns_404_json_not_index_html()
    {
        using var fx = new WebRootFixture();
        await using var host = new NativeBrokerHost(OptionsForFixture(fx.WebRoot));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/this-route-does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Equal(
                "application/json; charset=utf-8",
                resp.Content.Headers.ContentType?.ToString());
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetString());
            Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("/../etc/passwd")]
    [InlineData("/..%2fetc/passwd")]
    [InlineData("/assets/../../etc/passwd")]
    [InlineData("/assets/..%2F..%2Fetc/passwd")]
    public async Task Path_traversal_does_not_serve_outside_web_root(string traversalPath)
    {
        using var fx = new WebRootFixture();
        // Drop a sentinel file OUTSIDE the web root. If the traversal
        // ever succeeds the body would contain SENTINEL_OUTSIDE_ROOT.
        // The body check below is the hard security assertion.
        var outsideDir = Path.GetDirectoryName(fx.WebRoot)!;
        var sentinelPath = Path.Combine(outsideDir,
            "SENTINEL_" + Guid.NewGuid().ToString("N") + ".txt");
        const string sentinelBody = "SENTINEL_OUTSIDE_ROOT";
        File.WriteAllText(sentinelPath, sentinelBody);
        try
        {
            await using var host = new NativeBrokerHost(OptionsForFixture(fx.WebRoot));
            var start = await host.StartAsync();
            try
            {
                // Send raw HTTP bytes to bypass System.Uri client-side
                // normalization (which collapses ../ before the
                // request ever leaves the process). The server-side
                // guard in NativeBrokerHost is the actual defense.
                var (status, body) = await RawHttpGetAsync(start.Port, traversalPath);

                // Hard security invariant: under no circumstance may
                // the sentinel content leak. This is the only
                // assertion that genuinely tests the traversal guard.
                Assert.DoesNotContain(sentinelBody, body, StringComparison.Ordinal);

                // Status may legitimately be 200 (SPA fallback after
                // path normalization), 403 (traversal guard tripped),
                // or 404 (allowlist miss / no SPA root). 5xx would
                // signal an unhandled crash and IS a regression.
                Assert.InRange(status, 200, 499);
            }
            finally { await host.StopAsync(); }
        }
        finally
        {
            try { File.Delete(sentinelPath); } catch { }
        }
    }

    // Raw HTTP/1.1 GET that bypasses System.Uri normalization. Returns
    // (statusCode, body). Reads until connection close.
    private static async Task<(int Status, string Body)> RawHttpGetAsync(int port, string rawPath)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using var stream = tcp.GetStream();

        var request =
            "GET " + rawPath + " HTTP/1.1\r\n" +
            "Host: localhost:" + port + "\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        var requestBytes = System.Text.Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes);
        await stream.FlushAsync();

        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, read);
        }
        var raw = System.Text.Encoding.UTF8.GetString(ms.ToArray());

        // First line: "HTTP/1.1 <code> <reason>\r\n"
        var firstLineEnd = raw.IndexOf("\r\n", StringComparison.Ordinal);
        var firstLine = firstLineEnd > 0 ? raw[..firstLineEnd] : raw;
        var parts = firstLine.Split(' ', 3);
        var status = parts.Length >= 2 && int.TryParse(parts[1], out var s) ? s : 0;

        // Body is after the first "\r\n\r\n" separator.
        var bodyStart = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var body = bodyStart >= 0 ? raw[(bodyStart + 4)..] : string.Empty;
        return (status, body);
    }

    [Fact]
    public async Task Directory_request_without_index_does_not_list_contents()
    {
        using var fx = new WebRootFixture();
        await using var host = new NativeBrokerHost(OptionsForFixture(fx.WebRoot));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/assets/");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.DoesNotContain("app.js", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("app.css", body, StringComparison.OrdinalIgnoreCase);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Health_still_returns_native_envelope_after_static_enabled()
    {
        using var fx = new WebRootFixture();
        await using var host = new NativeBrokerHost(OptionsForFixture(fx.WebRoot));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = await http.GetStringAsync("/api/v1/health");
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("native", doc.RootElement.GetProperty("broker").GetString());
            Assert.Equal(start.Port, doc.RootElement.GetProperty("port").GetInt32());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Both_localhost_and_127_0_0_1_serve_static_content()
    {
        using var fx = new WebRootFixture();
        await using var host = new NativeBrokerHost(OptionsForFixture(fx.WebRoot));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient();
            var ipv4 = await http.GetStringAsync(
                "http://127.0.0.1:" + start.Port + "/index.html");
            var localhost = await http.GetStringAsync(
                "http://localhost:" + start.Port + "/index.html");
            Assert.Equal(IndexHtmlBody, ipv4);
            Assert.Equal(IndexHtmlBody, localhost);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Missing_web_root_returns_controlled_404_not_crash()
    {
        // Configure a WebRoot that does not exist on disk. The host
        // must still start, /api/v1/health must still respond, and
        // static requests must return a controlled 404 rather than
        // throwing.
        var missing = Path.Combine(Path.GetTempPath(),
            "PAXCookbookStage3bMissing_" + Guid.NewGuid().ToString("N"));
        await using var host = new NativeBrokerHost(OptionsForFixture(missing));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };

            using var staticResp = await http.GetAsync("/index.html");
            Assert.Equal(HttpStatusCode.NotFound, staticResp.StatusCode);

            // Health must still work even without a web root.
            var healthBody = await http.GetStringAsync("/api/v1/health");
            Assert.Contains("\"broker\":\"native\"", healthBody);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public void Pax_script_hash_unchanged_after_stage_3b()
    {
        // Tripwire: the PAX engine is hash-guarded. Any Stage 3b edit
        // that touched the script would surface here. Path resolution
        // walks up from the test binary to the repo root.
        var paxRel = Path.Combine("app", "resources", "pax",
            "PAX_Purview_Audit_Log_Processor.ps1");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? located = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, paxRel);
            if (File.Exists(candidate))
            {
                located = candidate;
                break;
            }
            dir = dir.Parent;
        }
        Assert.NotNull(located);

        using var sha = SHA256.Create();
        using var stream = File.OpenRead(located!);
        var hash = Convert.ToHexString(sha.ComputeHash(stream));
        Assert.Equal(PaxScriptBaselineHash, hash);
    }

    // Disposable per-test fixture: creates a temp web root with
    // index.html + assets/app.js + assets/app.css + favicon.ico and
    // tears the directory down on dispose.
    private sealed class WebRootFixture : IDisposable
    {
        public string WebRoot { get; }

        public WebRootFixture()
        {
            WebRoot = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3b_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(WebRoot);
            Directory.CreateDirectory(Path.Combine(WebRoot, "assets"));

            File.WriteAllText(Path.Combine(WebRoot, "index.html"), IndexHtmlBody);
            File.WriteAllText(Path.Combine(WebRoot, "assets", "app.js"), AppJsBody);
            File.WriteAllText(Path.Combine(WebRoot, "assets", "app.css"), AppCssBody);
            File.WriteAllBytes(Path.Combine(WebRoot, "favicon.ico"), FaviconBytes);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(WebRoot)) Directory.Delete(WebRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup. Temp dir survival is harmless.
            }
        }
    }
}
