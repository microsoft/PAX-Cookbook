using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using PAXCookbook.Broker.Native;
using PAXCookbook.Broker.Native.Services;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3i-A parity tests for the native broker's broker-lifecycle
// (/api/v1/broker/close-intent + /api/v1/broker/shutdown), bundled
// PAX export (/api/v1/runtime/pax-script/download), update telemetry
// + check + download (/api/v1/updates/state | /check | /download |
// /apply), and cook readiness (/api/v1/cooks/readiness) routes.
//
// All HTTP is loopback only via NativeBrokerHost.StartAsync(); the
// host is torn down per test. Each test uses an isolated temp
// workspace + AppRoot under PAXCookbookStage3iA_<guid>/. The
// Stage3iAServiceBundle is injected via
// NativeBrokerHost.WithStage3iAServiceOverride so:
//
//   * /updates/check + /updates/download NEVER hit the network --
//     they go through a FakeHttpMessageHandler instance that serves
//     canned manifest + package bodies.
//   * /broker/shutdown NEVER tears down the test host -- the
//     coordinator records the Signal call and StopApplication is
//     never invoked (the test calls host.StopAsync explicitly).
//   * The clock is deterministic so close-intent marker timestamps
//     are predictable.
//
// Tests share the "NativeBrokerHostPortBinding" xUnit collection
// with Stage 3a-3h so port-17654 binding is serialised.
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3iATests
{
    // Canonical PAX baseline hash. Stage 3i-A is a BROKER-side change;
    // the PAX script itself does not move. This is asserted in two
    // tests so any drift in the bundled script is loud.
    private const string PaxScriptBaselineHash =
        "5893B42807079CD8E321FE19C50C97188AD39A545BA7B90945657FDAE0BCE390";

    // Crockford-base32 ULID (uppercase, no I L O U). 26 chars.
    private const string SampleRecipeId = "01HQRC7N5VRSXG8K9MZTABCDEF";
    private const string SampleCookId   = "01HQRC7N5VRSXG8K9MZTCDKAAB";

    private static readonly DateTimeOffset FrozenClockUtc =
        new(2026, 5, 27, 12, 34, 56, TimeSpan.Zero);

    // ============================================================
    //  POST /api/v1/broker/close-intent
    // ============================================================

    [Fact]
    public async Task CloseIntent_returns_413_when_body_exceeds_limit()
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // 2048 bytes of JSON-shaped padding.
            var padded = "{\"intent\":\"" + new string('a', 2048) + "\"}";
            using var resp = await http.PostAsync(
                "/api/v1/broker/close-intent",
                new StringContent(padded, Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)413, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("payload_too_large",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public async Task CloseIntent_returns_400_invalid_json_for_malformed_body(string body)
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/broker/close-intent",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_json",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task CloseIntent_returns_400_invalid_intent_with_allowed_list()
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsJsonAsync(
                "/api/v1/broker/close-intent",
                new { intent = "wipe-disk" });
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_intent",
                doc.RootElement.GetProperty("error").GetString());
            var allowed = doc.RootElement.GetProperty("allowed");
            Assert.Equal(JsonValueKind.Array, allowed.ValueKind);
            var values = new HashSet<string>();
            foreach (var item in allowed.EnumerateArray())
                values.Add(item.GetString()!);
            Assert.Contains("app-only-close", values);
            Assert.Contains("stop-server",    values);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task CloseIntent_writes_marker_and_returns_202_for_valid_intent()
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsJsonAsync(
                "/api/v1/broker/close-intent",
                new { intent = "app-only-close" });
            Assert.Equal((HttpStatusCode)202, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("app-only-close",
                doc.RootElement.GetProperty("intent").GetString());

            // Marker file must exist in the Runtime directory
            // under the canonical fixed name app-close-intent.json.
            var markerPath = Path.Combine(
                fx.WorkspaceFolderPath, "Runtime", "app-close-intent.json");
            Assert.True(File.Exists(markerPath));
            var json = await File.ReadAllTextAsync(markerPath);
            using var marker = JsonDocument.Parse(json);
            Assert.Equal("app-only-close",
                marker.RootElement.GetProperty("intent").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task CloseIntent_other_methods_return_404()
    {
        // The native broker uses a catch-all MapFallback so an
        // unmatched-verb on an /api/* path falls through to the
        // API 404 handler rather than emitting a 405. This matches
        // the existing Stage 3a-3h behaviour.
        await using var fx = await Stage3iAFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/broker/close-intent");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/broker/shutdown
    // ============================================================

    [Fact]
    public async Task Shutdown_returns_202_with_shutdown_initiated_envelope()
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        var shutdown = new RecordingShutdownCoordinator();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle(shutdown: shutdown));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/broker/shutdown",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)202, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("shutdown_initiated",
                doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("operator_close_app",
                doc.RootElement.GetProperty("reason").GetString());
            Assert.Equal("PAX Cookbook server is shutting down.",
                doc.RootElement.GetProperty("message").GetString());

            // The coordinator was signalled with the operator-close
            // reason after the response was flushed.
            Assert.True(shutdown.HasBeenSignalled);
            Assert.Equal("operator_close_app", shutdown.Reason);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Shutdown_other_methods_return_404()
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/broker/shutdown");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  GET /api/v1/runtime/pax-script/download
    // ============================================================

    [Fact]
    public async Task PaxScriptDownload_returns_bytes_and_filename_for_valid_version()
    {
        await using var fx = await Stage3iAFixture.CreateAsync(paxScriptVersion: "1.2.3");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/runtime/pax-script/download");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/octet-stream",
                resp.Content.Headers.ContentType!.MediaType);
            var disp = resp.Content.Headers.ContentDisposition!;
            Assert.Equal("attachment", disp.DispositionType);
            // FileName is quoted by HttpClient parser; strip quotes.
            var name = (disp.FileName ?? string.Empty).Trim('"');
            Assert.Equal("PAX_Purview_Audit_Log_Processor_v1.2.3.ps1", name);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Assert.Equal(File.ReadAllBytes(fx.PaxScriptPath), bytes);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PaxScriptDownload_filename_sanitizes_invalid_version_to_unknown()
    {
        // Version with a forbidden character (space) must collapse
        // to the literal "unknown".
        await using var fx = await Stage3iAFixture.CreateAsync(
            paxScriptVersion: "1.2.3 evil");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/runtime/pax-script/download");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var name = (resp.Content.Headers.ContentDisposition!.FileName ?? string.Empty)
                .Trim('"');
            Assert.Equal("PAX_Purview_Audit_Log_Processor_vunknown.ps1", name);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PaxScriptDownload_returns_500_when_script_file_missing()
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        // Delete the fake PAX script file -- the route is still
        // registered (path string + VersionInfo are present) but the
        // reader returns pax_script_unavailable.
        File.Delete(fx.PaxScriptPath);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/runtime/pax-script/download");
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("pax_script_unavailable",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PaxScriptDownload_other_methods_return_404()
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/runtime/pax-script/download",
                new StringContent("", Encoding.UTF8));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  GET /api/v1/updates/state
    // ============================================================

    [Fact]
    public async Task UpdatesState_reports_manifestUrlConfigured_false_when_url_null()
    {
        await using var fx = await Stage3iAFixture.CreateAsync(manifestUrl: null);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/updates/state");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            // /updates/state returns the snapshot object directly
            // (no envelope) so manifestUrlConfigured is top-level.
            Assert.False(doc.RootElement.GetProperty("manifestUrlConfigured").GetBoolean());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task UpdatesState_reports_manifestUrlConfigured_true_when_url_set()
    {
        await using var fx = await Stage3iAFixture.CreateAsync(
            manifestUrl: "https://updates.example.test/manifest.json");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/updates/state");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.True(doc.RootElement.GetProperty("manifestUrlConfigured").GetBoolean());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/updates/check
    // ============================================================

    [Fact]
    public async Task UpdatesCheck_returns_notConfigured_when_manifestUrl_null()
    {
        await using var fx = await Stage3iAFixture.CreateAsync(manifestUrl: null);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/updates/check",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var check = doc.RootElement.GetProperty("check");
            Assert.Equal("notConfigured", check.GetProperty("state").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("<UPDATE_MANIFEST_URL>")]
    [InlineData("not-a-url")]
    public async Task UpdatesCheck_returns_manifestUrlInvalid_for_bad_url(string url)
    {
        await using var fx = await Stage3iAFixture.CreateAsync(manifestUrl: url);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/updates/check",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var check = doc.RootElement.GetProperty("check");
            Assert.Equal("manifestUrlInvalid", check.GetProperty("state").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task UpdatesCheck_returns_fetchFailed_when_handler_returns_500()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await using var fx = await Stage3iAFixture.CreateAsync(
            manifestUrl: "https://updates.example.test/manifest.json");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle(manifestHandler: handler));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/updates/check",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var check = doc.RootElement.GetProperty("check");
            Assert.Equal("fetchFailed", check.GetProperty("state").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task UpdatesCheck_returns_manifestInvalid_when_body_unparseable()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not a json", Encoding.UTF8,
                    "application/json"),
            });
        await using var fx = await Stage3iAFixture.CreateAsync(
            manifestUrl: "https://updates.example.test/manifest.json");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle(manifestHandler: handler));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/updates/check",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var check = doc.RootElement.GetProperty("check");
            Assert.Equal("manifestInvalid", check.GetProperty("state").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task UpdatesCheck_returns_upToDate_when_manifest_version_lte_current()
    {
        var manifest = BuildManifestJson(
            channel: "stable",
            cookbookVersion: "1.0.0",
            packageUrl: "https://updates.example.test/cookbook-1.0.0.zip",
            packageSha256: new string('A', 64),
            paxName: "PAX Test",
            paxVersion: "1.0.0",
            paxRelativePath: "resources/pax/PAX.ps1",
            paxSha256: Stage3iAFixture.FakePaxSha256);
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifest, Encoding.UTF8,
                    "application/json"),
            });
        await using var fx = await Stage3iAFixture.CreateAsync(
            manifestUrl: "https://updates.example.test/manifest.json",
            cookbookVersion: "1.0.0");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle(manifestHandler: handler));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/updates/check",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var check = doc.RootElement.GetProperty("check");
            Assert.Equal("upToDate", check.GetProperty("state").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task UpdatesCheck_returns_updateAvailable_and_records_bundledPaxChanges()
    {
        var manifest = BuildManifestJson(
            channel: "stable",
            cookbookVersion: "2.0.0",
            packageUrl: "https://updates.example.test/cookbook-2.0.0.zip",
            packageSha256: new string('B', 64),
            paxName: "PAX Test",
            paxVersion: "1.5.0",
            paxRelativePath: "resources/pax/PAX.ps1",
            paxSha256: new string('C', 64));
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifest, Encoding.UTF8,
                    "application/json"),
            });
        await using var fx = await Stage3iAFixture.CreateAsync(
            manifestUrl: "https://updates.example.test/manifest.json",
            cookbookVersion: "1.0.0");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle(manifestHandler: handler));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/updates/check",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var check = doc.RootElement.GetProperty("check");
            Assert.Equal("updateAvailable", check.GetProperty("state").GetString());
            // bundledPaxChanges.changes must be true because the
            // manifest's paxScript.sha256 differs from the fixture's
            // bundled paxSha256.
            var paxChanges = check.GetProperty("bundledPaxChanges");
            Assert.True(paxChanges.GetProperty("changes").GetBoolean());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/updates/download
    // ============================================================

    [Fact]
    public async Task UpdatesDownload_returns_409_when_no_manifest_snapshot()
    {
        await using var fx = await Stage3iAFixture.CreateAsync(
            manifestUrl: "https://updates.example.test/manifest.json");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/updates/download",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("no_manifest_snapshot",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/updates/apply
    // ============================================================

    [Fact]
    public async Task UpdatesApply_returns_501_updates_apply_deferred()
    {
        await using var fx = await Stage3iAFixture.CreateAsync(
            manifestUrl: "https://updates.example.test/manifest.json");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/updates/apply",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("not_implemented",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("updates_apply_deferred",
                doc.RootElement.GetProperty("code").GetString());
            Assert.Equal("3i-B",
                doc.RootElement.GetProperty("stage").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/cooks/readiness
    // ============================================================

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public async Task CooksReadiness_returns_400_invalid_json_for_malformed_body(string body)
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/cooks/readiness",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_json",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task CooksReadiness_returns_blocked_for_invalid_recipe_id_format()
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsJsonAsync(
                "/api/v1/cooks/readiness",
                new { recipeId = "not-a-ulid" });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("blocked", doc.RootElement.GetProperty("status").GetString());
            var checks = doc.RootElement.GetProperty("checks");
            var idCheck = FindCheck(checks, "recipe.recipe_id_format");
            Assert.Equal("blocked", idCheck.GetProperty("status").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task CooksReadiness_returns_blocked_when_database_missing()
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        // Delete the SQLite database file so the workspace.database_present
        // check fires even though the recipe id is well-formed.
        File.Delete(fx.DatabaseFilePath);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsJsonAsync(
                "/api/v1/cooks/readiness",
                new { recipeId = SampleRecipeId });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("blocked", doc.RootElement.GetProperty("status").GetString());
            var checks = doc.RootElement.GetProperty("checks");
            var dbCheck = FindCheck(checks, "workspace.database_present");
            Assert.Equal("blocked", dbCheck.GetProperty("status").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task CooksReadiness_returns_blocked_when_pax_script_sha256_mismatch()
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        // Overwrite the PAX script with different bytes so the SHA-256
        // mismatch fires.
        File.WriteAllText(fx.PaxScriptPath, "## tampered\n", new UTF8Encoding(false));
        await fx.SeedRecipeRowAsync(SampleRecipeId);
        await fx.WriteRecipeSnapshotAsync(SampleRecipeId);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsJsonAsync(
                "/api/v1/cooks/readiness",
                new { recipeId = SampleRecipeId });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("blocked", doc.RootElement.GetProperty("status").GetString());
            var checks = doc.RootElement.GetProperty("checks");
            var paxCheck = FindCheck(checks, "pax.script_integrity");
            Assert.Equal("blocked", paxCheck.GetProperty("status").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task CooksReadiness_returns_ok_when_recipe_healthy_and_no_cookId()
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId);
        await fx.WriteRecipeSnapshotAsync(SampleRecipeId);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsJsonAsync(
                "/api/v1/cooks/readiness",
                new { recipeId = SampleRecipeId });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());

            // The summary must show no blocked / warning entries and
            // the recipe.recipe_id_format check must be ok.
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(0, summary.GetProperty("blocked").GetInt32());
            Assert.Equal(0, summary.GetProperty("warning").GetInt32());

            var checks = doc.RootElement.GetProperty("checks");
            var idCheck = FindCheck(checks, "recipe.recipe_id_format");
            Assert.Equal("ok", idCheck.GetProperty("status").GetString());
            var dbCheck = FindCheck(checks, "workspace.database_present");
            Assert.Equal("ok", dbCheck.GetProperty("status").GetString());
            var paxCheck = FindCheck(checks, "pax.script_integrity");
            Assert.Equal("ok", paxCheck.GetProperty("status").GetString());

            // Deferred checks must surface as not_checked.
            var netCheck = FindCheck(checks, "network.reachability");
            Assert.Equal("not_checked", netCheck.GetProperty("status").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task CooksReadiness_returns_blocked_when_cookId_supplied_but_missing()
    {
        await using var fx = await Stage3iAFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId);
        await fx.WriteRecipeSnapshotAsync(SampleRecipeId);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iAServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsJsonAsync(
                "/api/v1/cooks/readiness",
                new { recipeId = SampleRecipeId, cookId = SampleCookId });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("blocked", doc.RootElement.GetProperty("status").GetString());
            var checks = doc.RootElement.GetProperty("checks");
            var cookCheck = FindCheck(checks, "resume.cook_present");
            Assert.Equal("blocked", cookCheck.GetProperty("status").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  PAX baseline -- tripwire
    // ============================================================

    [Fact]
    public void Pax_script_baseline_hash_is_unchanged()
    {
        var paxScriptPath = LocatePaxScript();
        var bytes  = File.ReadAllBytes(paxScriptPath);
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.Equal(PaxScriptBaselineHash, actual);
    }

    private static string LocatePaxScript()
    {
        // Walk up from the test bin/ until we find the repo root that
        // contains app/resources/pax/PAX_Purview_Audit_Log_Processor.ps1.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "app", "resources", "pax",
                "PAX_Purview_Audit_Log_Processor.ps1");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not locate PAX_Purview_Audit_Log_Processor.ps1 from "
            + AppContext.BaseDirectory);
    }

    // ============================================================
    //  Helpers
    // ============================================================

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return JsonDocument.Parse(bytes);
    }

    private static JsonElement FindCheck(JsonElement checks, string id)
    {
        foreach (var c in checks.EnumerateArray())
        {
            if (c.TryGetProperty("id", out var idProp)
                && string.Equals(idProp.GetString(), id, StringComparison.Ordinal))
            {
                return c;
            }
        }
        throw new InvalidOperationException(
            "Stage 3i-A test expected check id '" + id + "' in /cooks/readiness "
            + "response but it was not found.");
    }

    private static string BuildManifestJson(
        string channel,
        string cookbookVersion,
        string packageUrl,
        string packageSha256,
        string paxName,
        string paxVersion,
        string paxRelativePath,
        string paxSha256)
    {
        // Schema parity with Updates.ps1 / UpdateManifestParser:
        //   schemaVersion=1, channel, releaseTimestamp, latestCookbook
        //   {version, packageUrl, sha256}, includedPaxScript
        //   {name, version, relativePath, sha256}, compatibility
        //   {minCookbookVersionForPaxScript, minimumCompatibleInstallerVersion}.
        var obj = new
        {
            schemaVersion = 1,
            channel,
            releaseTimestamp = "2024-01-15T12:00:00Z",
            latestCookbook = new
            {
                version    = cookbookVersion,
                packageUrl = packageUrl,
                sha256     = packageSha256,
            },
            includedPaxScript = new
            {
                name         = paxName,
                version      = paxVersion,
                relativePath = paxRelativePath,
                sha256       = paxSha256,
            },
            compatibility = new
            {
                minCookbookVersionForPaxScript    = "1.0.0",
                minimumCompatibleInstallerVersion = "1.0.0",
            },
        };
        return JsonSerializer.Serialize(obj);
    }

    // ============================================================
    //  Fixture + fakes
    // ============================================================

    private sealed class Stage3iAFixture : IAsyncDisposable
    {
        // Sha256 of the fake PAX script body the fixture writes.
        // Computed dynamically below to stay in sync with FakePaxBody.
        public static readonly string FakePaxSha256 =
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(FakePaxBody)));

        private const string FakePaxBody =
            "# Stage 3i-A test stand-in PAX script -- not executed.\n";

        public string Root                { get; }
        public string WorkspaceFolderPath { get; }
        public string DatabaseFilePath    { get; }
        public string PaxScriptPath       { get; }
        public string PaxScriptVersion    { get; }
        public NativeBrokerHostOptions Options { get; }

        private Stage3iAFixture(
            string root, string workspace, string database,
            string paxScriptPath, string paxScriptVersion,
            NativeBrokerHostOptions options)
        {
            Root                = root;
            WorkspaceFolderPath = workspace;
            DatabaseFilePath    = database;
            PaxScriptPath       = paxScriptPath;
            PaxScriptVersion    = paxScriptVersion;
            Options             = options;
        }

        public static async Task<Stage3iAFixture> CreateAsync(
            string?  manifestUrl       = null,
            string   cookbookVersion   = "1.0.0",
            string   paxScriptVersion  = "1.0.0")
        {
            var root = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3iA_" + Guid.NewGuid().ToString("N"));
            var workspace     = Path.Combine(root, "Workspace");
            var databaseDir   = Path.Combine(workspace, "Database");
            var databaseFile  = Path.Combine(databaseDir, "cookbook.sqlite");
            var recipesDir    = Path.Combine(workspace, "Recipes");
            var cooksDir      = Path.Combine(workspace, "Cooks");
            var appRoot       = Path.Combine(root, "AppRoot");
            var paxResDir     = Path.Combine(appRoot, "resources", "pax");
            var paxScriptPath = Path.Combine(paxResDir, "PAX_test.ps1");
            var versionPath   = Path.Combine(appRoot, "VERSION.json");

            Directory.CreateDirectory(databaseDir);
            Directory.CreateDirectory(recipesDir);
            Directory.CreateDirectory(cooksDir);
            Directory.CreateDirectory(paxResDir);

            File.WriteAllText(paxScriptPath, FakePaxBody, new UTF8Encoding(false));

            // VERSION.json -- updateManifestUrl may be null or a
            // string. The schema mirrors VersionInfoReader.
            var manifestUrlJson = manifestUrl is null
                ? "null"
                : "\"" + manifestUrl + "\"";
            File.WriteAllText(versionPath,
                "{"
                + "\"schemaVersion\":1,"
                + "\"channel\":\"stable\","
                + "\"cookbook\":{\"version\":\"" + cookbookVersion + "\"},"
                + "\"paxScript\":{"
                +     "\"name\":\"PAX Test\","
                +     "\"version\":\"" + paxScriptVersion + "\","
                +     "\"relativePath\":\"resources/pax/PAX_test.ps1\","
                +     "\"sha256\":\"" + FakePaxSha256 + "\"},"
                + "\"updateManifestUrl\":" + manifestUrlJson
                + "}");

            await SeedSchemaAsync(databaseFile);

            var options = new NativeBrokerHostOptions(
                PreferredPort:       17654,
                PortRangeStart:      17654,
                PortRangeEnd:        17664,
                WorkspaceFolderPath: workspace,
                AppRoot:             appRoot,
                VersionFilePath:     versionPath,
                PaxScriptPath:       paxScriptPath);

            return new Stage3iAFixture(
                root, workspace, databaseFile,
                paxScriptPath, paxScriptVersion, options);
        }

        public Stage3iAServiceBundle BuildBundle(
            HttpMessageHandler?         manifestHandler = null,
            HttpMessageHandler?         packageHandler  = null,
            IBrokerShutdownCoordinator? shutdown        = null)
        {
            return new Stage3iAServiceBundle
            {
                UpdateManifestHttpHandler = manifestHandler,
                UpdatePackageHttpHandler  = packageHandler,
                Clock                     = () => FrozenClockUtc,
                ShutdownCoordinator       = shutdown ?? new RecordingShutdownCoordinator(),
            };
        }

        public async Task SeedRecipeRowAsync(string recipeId)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseFilePath,
                Mode       = SqliteOpenMode.ReadWrite,
            }.ToString();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO recipes (recipe_id, name, pax_adapter_version, recipe_schema_version,
                     source, file_path, file_hash, status, is_pinned,
                     created_at, updated_at)
VALUES ($id, $name, '1.0.0', 1, 'workspace',
        $file, 'hash', 'active', 0,
        '2026-05-27T08:00:00Z', '2026-05-27T08:00:00Z');";
            cmd.Parameters.AddWithValue("$id",   recipeId);
            cmd.Parameters.AddWithValue("$name", "Stage 3i-A recipe");
            cmd.Parameters.AddWithValue("$file",
                Path.Combine("Recipes", recipeId + ".recipe.json"));
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public Task WriteRecipeSnapshotAsync(string recipeId)
        {
            var path = Path.Combine(WorkspaceFolderPath, "Recipes",
                recipeId + ".recipe.json");
            var snapshot = new
            {
                recipeSchemaVersion = 1,
                name                = "Stage 3i-A recipe",
                executionMode       = "local-manual",
                auth                = new { mode = "AppRegistrationCertificate" },
            };
            File.WriteAllText(path,
                JsonSerializer.Serialize(snapshot,
                    new JsonSerializerOptions { WriteIndented = false }),
                new UTF8Encoding(false));
            return Task.CompletedTask;
        }

        private static async Task SeedSchemaAsync(string databaseFile)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = databaseFile,
                Mode       = SqliteOpenMode.ReadWriteCreate,
            }.ToString();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE recipes (
    recipe_id              TEXT PRIMARY KEY,
    name                   TEXT NOT NULL,
    description            TEXT,
    tags_json              TEXT NOT NULL DEFAULT '[]',
    pax_adapter_version    TEXT NOT NULL,
    recipe_schema_version  INTEGER NOT NULL,
    source                 TEXT NOT NULL,
    source_ref             TEXT,
    file_path              TEXT NOT NULL UNIQUE,
    file_hash              TEXT NOT NULL,
    status                 TEXT NOT NULL DEFAULT 'draft',
    is_pinned              INTEGER NOT NULL DEFAULT 0,
    last_validated_at      TEXT,
    last_validation_status TEXT,
    last_cooked_at         TEXT,
    last_cook_id           TEXT,
    created_at             TEXT NOT NULL,
    updated_at             TEXT NOT NULL,
    deleted_at             TEXT
);
CREATE TABLE cooks (
    cook_id                TEXT PRIMARY KEY,
    recipe_id              TEXT,
    recipe_version_id      TEXT,
    recipe_snapshot_json   TEXT,
    command_argv_json      TEXT,
    command_argv_redacted  TEXT,
    pax_script_path        TEXT,
    pax_script_version     TEXT,
    trigger                TEXT NOT NULL,
    schedule_id            TEXT,
    parent_cook_id         TEXT,
    cook_folder            TEXT,
    pid                    INTEGER,
    status                 TEXT NOT NULL,
    exit_code              INTEGER,
    started_at             TEXT,
    finished_at            TEXT,
    duration_seconds       REAL,
    error_class            TEXT,
    error_message          TEXT,
    summary_path           TEXT,
    created_at             TEXT NOT NULL,
    updated_at             TEXT NOT NULL
);
CREATE TABLE auth_profiles (
    auth_profile_id        TEXT PRIMARY KEY,
    name                   TEXT NOT NULL,
    mode                   TEXT NOT NULL,
    tenant_id              TEXT,
    client_id              TEXT,
    cred_man_target        TEXT,
    cert_thumbprint        TEXT,
    cert_store             TEXT,
    description            TEXT,
    last_verified_at       TEXT,
    last_verified_result   TEXT,
    created_at             TEXT NOT NULL,
    updated_at             TEXT NOT NULL
);";
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup -- temp dir survives even if a
                // file handle is still open. OS will GC it eventually.
            }
            return ValueTask.CompletedTask;
        }
    }

    // Records IBrokerShutdownCoordinator.Signal calls and intentionally
    // does NOT call IHostApplicationLifetime.StopApplication so the
    // test host stays alive until the test method's StopAsync.
    private sealed class RecordingShutdownCoordinator : IBrokerShutdownCoordinator
    {
        public bool    HasBeenSignalled { get; private set; }
        public string? Reason           { get; private set; }

        public void Signal(string reason)
        {
            HasBeenSignalled = true;
            Reason           = reason;
        }
    }

    // Minimal HttpMessageHandler that returns a canned response from
    // a per-request delegate. The handler is wired into the manifest
    // probe / package downloader through the Stage 3i-A service
    // bundle so /updates/check + /updates/download never reach the
    // network.
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
        public FakeHttpMessageHandler(
            Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _factory = factory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_factory(request));
    }
}
