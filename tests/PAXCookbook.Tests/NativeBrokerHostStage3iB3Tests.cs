using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3i-B3 parity tests for the native broker's recipe-takeout
// surface:
//   POST /api/v1/recipes/<ulid>/takeout      (export)
//   POST /api/v1/recipe-takeout/validate     (validate)
//   POST /api/v1/recipe-takeout/import       (import)
//
// Shares the "NativeBrokerHostPortBinding" xUnit collection with the
// rest of the NativeBrokerHost tests so port-17654 binding is
// serialised across Stage 3a-3i runs.
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3iB3Tests
{
    // PAX baseline tripwire. Stage 3i-B3 is a BROKER-side change;
    // the PAX script does not move.
    private const string PaxScriptBaselineHash =
        "1A9BC94783683AE1DA68EE6A86DE2106A96122B67B14EE20090E6687792E3878";

    // ULIDs distinct from Stage 3i-B1 / Stage 3i-B2 so the three
    // suites can run interleaved even if a future change widens
    // cross-cutting state.
    private const string FactoryRecipeId  = "01JKMNPQRSTVWXYZABCDEFGH35";
    private const string ExistingRecipeId = "01JKMNPQRSTVWXYZABCDEFGH36";

    private const string ProfileSecretId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private static readonly DateTimeOffset FrozenClockUtc =
        new(2026, 5, 27, 12, 34, 56, TimeSpan.FromTicks(0));

    // ============================================================
    //  POST /api/v1/recipes/{id}/takeout (export)
    // ============================================================

    [Fact]
    public async Task PostExport_returns_200_envelope_with_attachment_headers()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await fx.SeedExistingRecipeAsync(ExistingRecipeId, name: "Cookbook Test");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + ExistingRecipeId + "/takeout",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            Assert.Equal("no-store", resp.Headers.CacheControl?.ToString());
            var disp = resp.Content.Headers.ContentDisposition;
            Assert.NotNull(disp);
            Assert.Equal("attachment", disp!.DispositionType);
            Assert.Equal("\"cookbook-test.paxrecipe.json\"", disp.FileName);
            Assert.True(resp.Headers.TryGetValues("Access-Control-Expose-Headers", out var v));
            Assert.Contains("Content-Disposition", string.Join(",", v));

            var doc = await ReadJsonAsync(resp);
            Assert.Equal(1, doc.RootElement.GetProperty("takeoutSchemaVersion").GetInt32());
            Assert.Equal("pax-cookbook.recipe-takeout",
                doc.RootElement.GetProperty("kind").GetString());
            Assert.True(doc.RootElement.TryGetProperty("recipe", out var recipe));
            Assert.Equal("Cookbook Test",
                recipe.GetProperty("identity").GetProperty("name").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostExport_with_invalid_ulid_returns_400_invalid_recipe_id()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/recipes/not-a-ulid/takeout",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_recipe_id",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostExport_with_missing_recipe_returns_404()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + ExistingRecipeId + "/takeout",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_not_found",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/recipe-takeout/validate
    // ============================================================

    [Fact]
    public async Task PostValidate_returns_200_preview_with_nameSuggestion()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await fx.SeedExistingRecipeAsync(ExistingRecipeId, name: "Source recipe");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };

            // Export first so we get a clean envelope to feed back.
            var envelopeJson = await ExportRecipeAsync(http, ExistingRecipeId);
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/validate",
                new StringContent(envelopeJson, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("valid").GetBoolean());
            var name = doc.RootElement.GetProperty("recipe").GetProperty("name").GetString();
            Assert.Equal("Source recipe", name);
            // The recipe already exists in this workspace -> collision.
            var sug = doc.RootElement.GetProperty("nameSuggestion");
            Assert.True(sug.GetProperty("collision").GetBoolean());
            Assert.Equal("Source recipe (1)", sug.GetProperty("suggestedName").GetString());
            Assert.Equal("windows_numeric_suffix",
                sug.GetProperty("collisionRule").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostValidate_with_empty_body_returns_400_invalid_json()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/validate",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_json", doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostValidate_with_forbidden_field_returns_400_with_fieldName()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // Envelope with a 'clientSecret' nested in recipe.auth.
            var body = @"{""takeoutSchemaVersion"":1,""kind"":""pax-cookbook.recipe-takeout"",""exportedAtUtc"":""2026-05-27T12:34:56.0000000Z"",""recipe"":{""identity"":{""name"":""x""},""auth"":{""clientSecret"":""xyz""}},""excluded"":[""x""]}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/validate",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("takeout_contains_forbidden_secret_field",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("clientSecret",
                doc.RootElement.GetProperty("fieldName").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostValidate_with_authProfileId_returns_400_with_explicit_path()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""takeoutSchemaVersion"":1,""kind"":""pax-cookbook.recipe-takeout"",""exportedAtUtc"":""2026-05-27T12:34:56.0000000Z"",""recipe"":{""identity"":{""name"":""x""},""auth"":{""mode"":""WebLogin"",""authProfileId"":""aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa""}},""excluded"":[""x""]}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/validate",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("takeout_contains_forbidden_secret_field",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("authProfileId",
                doc.RootElement.GetProperty("fieldName").GetString());
            Assert.Equal("/recipe/auth/authProfileId",
                doc.RootElement.GetProperty("path").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostValidate_with_bad_schema_version_returns_400_unsupported()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""takeoutSchemaVersion"":99,""kind"":""pax-cookbook.recipe-takeout"",""exportedAtUtc"":""2026-05-27T12:34:56.0000000Z"",""recipe"":{""identity"":{""name"":""x""}},""excluded"":[""x""]}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/validate",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("takeout_schema_version_unsupported",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostValidate_with_bad_kind_returns_400_kind_invalid()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""takeoutSchemaVersion"":1,""kind"":""wrong"",""exportedAtUtc"":""2026-05-27T12:34:56.0000000Z"",""recipe"":{""identity"":{""name"":""x""}},""excluded"":[""x""]}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/validate",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("takeout_kind_invalid",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostValidate_with_unknown_top_field_returns_400_unknown()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""takeoutSchemaVersion"":1,""kind"":""pax-cookbook.recipe-takeout"",""exportedAtUtc"":""2026-05-27T12:34:56.0000000Z"",""recipe"":{""identity"":{""name"":""x""}},""excluded"":[""x""],""extraField"":1}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/validate",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("takeout_unknown_field",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostValidate_missing_required_returns_400_shape_invalid()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // Missing 'recipe' entirely.
            var body = @"{""takeoutSchemaVersion"":1,""kind"":""pax-cookbook.recipe-takeout"",""exportedAtUtc"":""2026-05-27T12:34:56.0000000Z"",""excluded"":[""x""]}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/validate",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("takeout_shape_invalid",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostValidate_body_over_256_kib_returns_413()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var big = new string('a', 257 * 1024);
            var body = @"{""junk"":""" + big + @"""}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/validate",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)413, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("payload_too_large",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(256 * 1024,
                doc.RootElement.GetProperty("limitBytes").GetInt32());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/recipe-takeout/import
    // ============================================================

    [Fact]
    public async Task PostImport_creates_recipe_with_source_takeout()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await fx.SeedExistingRecipeAsync(ExistingRecipeId, name: "Source recipe");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var envelopeJson = await ExportRecipeAsync(http, ExistingRecipeId);
            var wrapper = @"{""takeout"":" + envelopeJson + @",""targetRecipeName"":""Imported recipe""}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/import",
                new StringContent(wrapper, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("imported").GetBoolean());
            Assert.Equal(FactoryRecipeId,
                doc.RootElement.GetProperty("recipeId").GetString());
            Assert.Equal("Imported recipe",
                doc.RootElement.GetProperty("recipeName").GetString());

            var row = fx.GetRecipeRow(FactoryRecipeId);
            Assert.NotNull(row);
            Assert.Equal("Imported recipe", row!.Value.Name);
            Assert.Equal("takeout", row.Value.Source);
            Assert.Equal(ExistingRecipeId, row.Value.SourceRef);

            var path = Path.Combine(
                fx.WorkspaceFolderPath, "Recipes", FactoryRecipeId + ".recipe.json");
            Assert.True(File.Exists(path));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostImport_missing_targetRecipeName_returns_400_required()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await fx.SeedExistingRecipeAsync(ExistingRecipeId, name: "Source recipe");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var envelopeJson = await ExportRecipeAsync(http, ExistingRecipeId);
            var wrapper = @"{""takeout"":" + envelopeJson + "}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/import",
                new StringContent(wrapper, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_name_required",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostImport_invalid_char_in_name_returns_400_with_reason()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await fx.SeedExistingRecipeAsync(ExistingRecipeId, name: "Source recipe");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var envelopeJson = await ExportRecipeAsync(http, ExistingRecipeId);
            var wrapper = @"{""takeout"":" + envelopeJson + @",""targetRecipeName"":""bad/name""}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/import",
                new StringContent(wrapper, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_name_invalid",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("invalid_char",
                doc.RootElement.GetProperty("reason").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostImport_unknown_wrapper_field_returns_400_unknown()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var wrapper = @"{""takeout"":{},""targetRecipeName"":""x"",""extra"":1}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/import",
                new StringContent(wrapper, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("takeout_unknown_field",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostImport_name_collision_returns_409_with_nextSuggestion()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await fx.SeedExistingRecipeAsync(ExistingRecipeId, name: "Source recipe");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var envelopeJson = await ExportRecipeAsync(http, ExistingRecipeId);
            // Use the SAME name as the existing recipe -> collision.
            var wrapper = @"{""takeout"":" + envelopeJson + @",""targetRecipeName"":""Source recipe""}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/import",
                new StringContent(wrapper, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_name_conflict",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("Source recipe (1)",
                doc.RootElement.GetProperty("nextSuggestion").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostImport_with_authProfileId_in_envelope_returns_400_with_path()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""takeoutSchemaVersion"":1,""kind"":""pax-cookbook.recipe-takeout"",""exportedAtUtc"":""2026-05-27T12:34:56.0000000Z"",""recipe"":{""identity"":{""name"":""x""},""auth"":{""mode"":""WebLogin"",""authProfileId"":""aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa""}},""excluded"":[""x""]}";
            var wrapper = @"{""takeout"":" + body + @",""targetRecipeName"":""xyz""}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/import",
                new StringContent(wrapper, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("takeout_contains_forbidden_secret_field",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("authProfileId",
                doc.RootElement.GetProperty("fieldName").GetString());
            Assert.Equal("/recipe/auth/authProfileId",
                doc.RootElement.GetProperty("path").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostImport_body_over_256_kib_returns_413()
    {
        await using var fx = await Stage3iB3Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB3ServiceOverride(fx.BuildB3Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var big = new string('a', 257 * 1024);
            var body = @"{""junk"":""" + big + @"""}";
            using var resp = await http.PostAsync("/api/v1/recipe-takeout/import",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)413, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("payload_too_large",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public void Bundled_pax_script_hash_unchanged()
    {
        // Stage 3i-B3 is BROKER-side only. The bundled PAX script
        // must not move. If the repo layout puts the script outside
        // test-reachable space the tripwire short-circuits
        // (lockfile-driven hash check elsewhere remains the source
        // of truth).
        var paxScript = Path.Combine(
            Path.GetDirectoryName(typeof(NativeBrokerHostStage3iB3Tests).Assembly.Location)!,
            "..", "..", "..", "..", "..", "app", "install", "PAX_Cookbook.ps1");
        if (!File.Exists(paxScript)) return;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paxScript)));
        Assert.Equal(PaxScriptBaselineHash, hash);
    }

    // ============================================================
    //  Helpers
    // ============================================================

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp)
    {
        var s = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(string.IsNullOrEmpty(s) ? "{}" : s);
    }

    private static async Task<string> ExportRecipeAsync(HttpClient http, string recipeId)
    {
        using var resp = await http.PostAsync(
            "/api/v1/recipes/" + recipeId + "/takeout",
            new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadAsStringAsync();
    }

    private sealed class Stage3iB3Fixture : IAsyncDisposable
    {
        public string Root                { get; }
        public string WorkspaceFolderPath { get; }
        public string DatabaseFilePath    { get; }
        public string AppRoot             { get; }
        public string PaxScriptPath       { get; }
        public string CookbookVersion     { get; }
        public string BundledPaxVersion   { get; }
        public string ReleaseChannel      { get; }
        public string PaxAdapterVersion   => BundledPaxVersion;
        public NativeBrokerHostOptions Options { get; }

        private Stage3iB3Fixture(
            string root, string workspace, string database, string appRoot,
            string paxScriptPath,
            string cookbookVersion, string bundledPax, string channel,
            NativeBrokerHostOptions options)
        {
            Root                = root;
            WorkspaceFolderPath = workspace;
            DatabaseFilePath    = database;
            AppRoot             = appRoot;
            PaxScriptPath       = paxScriptPath;
            CookbookVersion     = cookbookVersion;
            BundledPaxVersion   = bundledPax;
            ReleaseChannel      = channel;
            Options             = options;
        }

        public static async Task<Stage3iB3Fixture> CreateAsync(
            string cookbookVersion    = "1.0.0",
            string bundledPaxVersion  = "1.0.0",
            string releaseChannel     = "stable")
        {
            var root = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3iB3_" + Guid.NewGuid().ToString("N"));
            var workspace     = Path.Combine(root, "Workspace");
            var databaseDir   = Path.Combine(workspace, "Database");
            var databaseFile  = Path.Combine(databaseDir, "cookbook.sqlite");
            var recipesDir    = Path.Combine(workspace, "Recipes");
            var cooksDir      = Path.Combine(workspace, "Cooks");
            var appRoot       = Path.Combine(root, "AppRoot");
            var templatesDir  = Path.Combine(appRoot, "templates");
            var paxResDir     = Path.Combine(appRoot, "resources", "pax");
            var paxScriptPath = Path.Combine(paxResDir, "PAX_test.ps1");
            var versionPath   = Path.Combine(appRoot, "VERSION.json");

            Directory.CreateDirectory(databaseDir);
            Directory.CreateDirectory(recipesDir);
            Directory.CreateDirectory(cooksDir);
            Directory.CreateDirectory(templatesDir);
            Directory.CreateDirectory(paxResDir);

            const string fakePaxBody = "# Stage 3i-B3 test stand-in PAX script -- not executed.\n";
            File.WriteAllText(paxScriptPath, fakePaxBody, new UTF8Encoding(false));
            var paxSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fakePaxBody)));

            File.WriteAllText(versionPath,
                "{"
                + "\"schemaVersion\":1,"
                + "\"channel\":\"" + releaseChannel + "\","
                + "\"cookbook\":{\"version\":\"" + cookbookVersion + "\"},"
                + "\"paxScript\":{"
                +     "\"name\":\"PAX Test\","
                +     "\"version\":\"" + bundledPaxVersion + "\","
                +     "\"relativePath\":\"resources/pax/PAX_test.ps1\","
                +     "\"sha256\":\"" + paxSha + "\"},"
                + "\"updateManifestUrl\":null"
                + "}");

            await SeedSchemaAsync(databaseFile);

            var options = new NativeBrokerHostOptions(
                PreferredPort:       17654,
                PortRangeStart:      17654,
                PortRangeEnd:        17664,
                WorkspaceFolderPath: workspace,
                AppRoot:             appRoot,
                VersionFilePath:     versionPath,
                TemplatesDir:        templatesDir,
                PaxScriptPath:       paxScriptPath);

            return new Stage3iB3Fixture(
                root, workspace, databaseFile, appRoot, paxScriptPath,
                cookbookVersion, bundledPaxVersion, releaseChannel, options);
        }

        public Stage3iB3ServiceBundle BuildB3Bundle(
            string?                       factoryId    = null,
            DateTimeOffset?               clockOverride = null,
            Func<string, string?>?        labelLookup  = null)
        {
            return new Stage3iB3ServiceBundle
            {
                Clock                = () => clockOverride ?? FrozenClockUtc,
                RecipeIdFactory      = factoryId is null ? null : () => factoryId,
                PaxAdapterVersion    = PaxAdapterVersion,
                BundledPaxVersion    = BundledPaxVersion,
                CookbookVersion      = CookbookVersion,
                ReleaseChannel       = ReleaseChannel,
                WorkspaceInstallPath = WorkspaceFolderPath,
                ChefKeyLabelLookup   = labelLookup,
            };
        }

        public async Task SeedExistingRecipeAsync(
            string recipeId,
            string  name           = "Existing recipe",
            string  createdAt      = "2026-04-01T00:00:00.000Z",
            string  updatedAt      = "2026-04-01T00:00:00.000Z",
            string  fileHash       = "deadbeef",
            bool    writeFile      = true,
            int     schemaVersion  = 1)
        {
            var filePath = Path.Combine(WorkspaceFolderPath, "Recipes",
                recipeId + ".recipe.json");
            if (writeFile)
            {
                var disk = @"{""recipeId"":""" + recipeId + @""",""recipeSchemaVersion"":" + schemaVersion + @",""paxAdapterVersion"":""" + PaxAdapterVersion + @""",""identity"":{""name"":""" + name + @"""},""ingredients"":{},""query"":{""mode"":""audit"",""startDate"":""2026-01-01"",""endDate"":""2026-01-31""},""processing"":{""rollup"":""Rollup""},""destinations"":{""fact"":{""mode"":""outputPath"",""path"":""C:\\\\out.csv""}},""auth"":{""mode"":""WebLogin"",""tenantId"":""11111111-1111-1111-1111-111111111111""},""createdAt"":""" + createdAt + @""",""updatedAt"":""" + updatedAt + @""",""createdBy"":{""cookbookVersion"":""0.9.0"",""bundledPaxVersion"":""0.5.0"",""releaseChannel"":""preview""}}";
                File.WriteAllText(filePath, disk, new UTF8Encoding(false));
            }

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
                     created_at, updated_at, deleted_at)
VALUES ($id, $name, $pax, $schema, 'workspace',
        $file, $hash, 'ready', 0, $created, $updated, NULL);";
            cmd.Parameters.AddWithValue("$id",      recipeId);
            cmd.Parameters.AddWithValue("$name",    name);
            cmd.Parameters.AddWithValue("$pax",     PaxAdapterVersion);
            cmd.Parameters.AddWithValue("$schema",  schemaVersion);
            cmd.Parameters.AddWithValue("$file",    filePath);
            cmd.Parameters.AddWithValue("$hash",    fileHash);
            cmd.Parameters.AddWithValue("$created", createdAt);
            cmd.Parameters.AddWithValue("$updated", updatedAt);
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public RecipeRowView? GetRecipeRow(string recipeId)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseFilePath,
                Mode       = SqliteOpenMode.ReadOnly,
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT recipe_id, name, file_hash, pax_adapter_version, recipe_schema_version,
       source, source_ref, created_at, updated_at, deleted_at
FROM recipes WHERE recipe_id = $id;";
            cmd.Parameters.AddWithValue("$id", recipeId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var deletedAt = reader.IsDBNull(9) ? null : reader.GetString(9);
            if (deletedAt is not null) return null;
            return new RecipeRowView(
                RecipeId:            reader.GetString(0),
                Name:                reader.GetString(1),
                FileHash:            reader.GetString(2),
                PaxAdapterVersion:   reader.GetString(3),
                RecipeSchemaVersion: reader.GetInt32(4),
                Source:              reader.GetString(5),
                SourceRef:           reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAt:           reader.GetString(7),
                UpdatedAt:           reader.GetString(8),
                DeletedAt:           deletedAt);
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
    status                 TEXT NOT NULL,
    started_at             TEXT,
    finished_at            TEXT,
    created_at             TEXT NOT NULL,
    updated_at             TEXT NOT NULL
);
CREATE TABLE auth_profiles (
    auth_profile_id        TEXT PRIMARY KEY,
    name                   TEXT NOT NULL,
    mode                   TEXT NOT NULL,
    tenant_id              TEXT NOT NULL,
    client_id              TEXT NOT NULL,
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
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch { /* best-effort */ }
            return ValueTask.CompletedTask;
        }
    }

    private readonly record struct RecipeRowView(
        string  RecipeId,
        string  Name,
        string  FileHash,
        string  PaxAdapterVersion,
        int     RecipeSchemaVersion,
        string  Source,
        string? SourceRef,
        string  CreatedAt,
        string  UpdatedAt,
        string? DeletedAt);
}
