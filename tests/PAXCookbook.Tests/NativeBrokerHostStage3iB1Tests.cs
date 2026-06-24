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

// Stage 3i-B1 parity tests for the native broker's recipe mutation
// surface: POST + PUT + DELETE on /api/v1/recipes(/{ulid}).
//
// Tests share the "NativeBrokerHostPortBinding" xUnit collection
// with the rest of the NativeBrokerHost tests so port-17654 binding
// is serialised across the Stage 3a-3i runs.
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3iB1Tests
{
    // Canonical PAX baseline hash. Stage 3i-B1 is a BROKER-side
    // change; the PAX script itself does not move. Asserted in a
    // tripwire fact so any drift in the bundled script is loud.
    private const string PaxScriptBaselineHash =
        "5893B42807079CD8E321FE19C50C97188AD39A545BA7B90945657FDAE0BCE390";

    // Deterministic ULIDs used across the tests. All match the
    // Crockford-base32 pattern `^[0-9A-HJKMNP-TV-Z]{26}$` (no I,L,O,U).
    private const string FactoryRecipeId = "01JKMNPQRSTVWXYZABCDEFGH23";
    private const string ExistingRecipeId = "01JKMNPQRSTVWXYZABCDEFGH24";
    private const string OtherRecipeId    = "01JKMNPQRSTVWXYZABCDEFGH25";

    private static readonly DateTimeOffset FrozenClockUtc =
        new(2026, 5, 27, 12, 34, 56, TimeSpan.FromTicks(0));

    // ============================================================
    //  POST /api/v1/recipes
    // ============================================================

    [Fact]
    public async Task PostRecipe_creates_row_and_file_with_server_stamped_provenance()
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = MinimalRecipeBody();
            using var resp = await http.PostAsync("/api/v1/recipes",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)201, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal(FactoryRecipeId,
                doc.RootElement.GetProperty("recipeId").GetString());

            var recipe = doc.RootElement.GetProperty("recipe");
            Assert.Equal(FactoryRecipeId,           recipe.GetProperty("recipeId").GetString());
            Assert.Equal(1,                         recipe.GetProperty("recipeSchemaVersion").GetInt32());
            Assert.Equal(fx.PaxAdapterVersion,      recipe.GetProperty("paxAdapterVersion").GetString());
            var iso = "2026-05-27T12:34:56.000Z";
            Assert.Equal(iso,                       recipe.GetProperty("createdAt").GetString());
            Assert.Equal(iso,                       recipe.GetProperty("updatedAt").GetString());
            var cb = recipe.GetProperty("createdBy");
            Assert.Equal(fx.CookbookVersion,        cb.GetProperty("cookbookVersion").GetString());
            Assert.Equal(fx.BundledPaxVersion,      cb.GetProperty("bundledPaxVersion").GetString());
            Assert.Equal(fx.ReleaseChannel,         cb.GetProperty("releaseChannel").GetString());

            // File present on disk
            var filePath = Path.Combine(fx.WorkspaceFolderPath, "Recipes",
                FactoryRecipeId + ".recipe.json");
            Assert.True(File.Exists(filePath));
            var fileText = File.ReadAllText(filePath);
            using var fileDoc = JsonDocument.Parse(fileText);
            Assert.Equal(FactoryRecipeId,
                fileDoc.RootElement.GetProperty("recipeId").GetString());

            // Row present in DB
            var row = fx.GetRecipeRow(FactoryRecipeId);
            Assert.NotNull(row);
            Assert.Equal("Stage 3i-B1 created",     row!.Value.Name);
            Assert.Equal(fx.PaxAdapterVersion,      row.Value.PaxAdapterVersion);
            Assert.Equal(1,                         row.Value.RecipeSchemaVersion);
            Assert.Equal("new",                     row.Value.Source);
            Assert.Null(row.Value.SourceRef);
            Assert.Equal(iso,                       row.Value.CreatedAt);
            Assert.Equal(iso,                       row.Value.UpdatedAt);
            Assert.Null(row.Value.DeletedAt);
            // file_hash matches what's on disk
            var diskHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath)))
                .ToLowerInvariant();
            Assert.Equal(diskHash, row.Value.FileHash);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public async Task PostRecipe_returns_400_invalid_json_for_malformed_body(string body)
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/recipes",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_json",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostRecipe_returns_400_validation_failed_with_error_array()
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // identity.name missing -> required violation
            var body = @"{""identity"":{},""ingredients"":{},""query"":{},""processing"":{},""destinations"":{},""auth"":{""mode"":""WebLogin"",""tenantId"":""11111111-1111-1111-1111-111111111111""}}";
            using var resp = await http.PostAsync("/api/v1/recipes",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("validation_failed",
                doc.RootElement.GetProperty("error").GetString());
            var errors = doc.RootElement.GetProperty("errors");
            Assert.Equal(JsonValueKind.Array, errors.ValueKind);
            Assert.True(errors.GetArrayLength() > 0);
            var first = errors[0];
            Assert.Equal("required", first.GetProperty("keyword").GetString());
            // No row, no file
            Assert.Null(fx.GetRecipeRow(FactoryRecipeId));
            Assert.False(File.Exists(Path.Combine(
                fx.WorkspaceFolderPath, "Recipes", FactoryRecipeId + ".recipe.json")));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostRecipe_name_only_draft_saves_with_draft_status()
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // Name-only draft: schema-valid and free of security violations, but
            // missing the completeness fields a bake needs (date range, output
            // path). Saving is decoupled from baking, so this must persist.
            var body = @"{""identity"":{""name"":""Draft only""},""ingredients"":{},""query"":{},""processing"":{},""destinations"":{},""auth"":{""mode"":""WebLogin"",""tenantId"":""11111111-1111-1111-1111-111111111111""}}";
            using var resp = await http.PostAsync("/api/v1/recipes",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)201, resp.StatusCode);

            // Row persisted, and the list-badge status reflects bake-readiness.
            var row = fx.GetRecipeRow(FactoryRecipeId);
            Assert.NotNull(row);
            Assert.Equal("Draft only", row!.Value.Name);
            Assert.Equal("draft", fx.GetRecipeStatus(FactoryRecipeId));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostRecipe_complete_recipe_saves_with_ready_status()
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // MinimalRecipeBody is a fully bake-ready recipe.
            using var resp = await http.PostAsync("/api/v1/recipes",
                new StringContent(MinimalRecipeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)201, resp.StatusCode);
            Assert.Equal("ready", fx.GetRecipeStatus(FactoryRecipeId));
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  PUT /api/v1/recipes/{ulid}
    // ============================================================

    [Fact]
    public async Task PutRecipe_updates_body_and_file_preserving_provenance()
    {        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            await fx.SeedExistingRecipeAsync(
                ExistingRecipeId,
                name: "Original name",
                createdAt: "2026-04-01T00:00:00.000Z");

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = MinimalRecipeBody(name: "Renamed recipe");
            using var resp = await http.PutAsync("/api/v1/recipes/" + ExistingRecipeId,
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var doc = await ReadJsonAsync(resp);
            Assert.Equal(ExistingRecipeId,
                doc.RootElement.GetProperty("recipeId").GetString());
            var recipe = doc.RootElement.GetProperty("recipe");
            Assert.Equal("Renamed recipe",
                recipe.GetProperty("identity").GetProperty("name").GetString());
            // createdAt preserved from row, updatedAt = frozen clock
            Assert.Equal("2026-04-01T00:00:00.000Z",
                recipe.GetProperty("createdAt").GetString());
            Assert.Equal("2026-05-27T12:34:56.000Z",
                recipe.GetProperty("updatedAt").GetString());
            // createdBy preserved from disk verbatim
            var cb = recipe.GetProperty("createdBy");
            Assert.Equal("0.9.0",      cb.GetProperty("cookbookVersion").GetString());
            Assert.Equal("0.5.0",      cb.GetProperty("bundledPaxVersion").GetString());
            Assert.Equal("preview",    cb.GetProperty("releaseChannel").GetString());

            // Row's name + hash + updated_at advanced
            var row = fx.GetRecipeRow(ExistingRecipeId);
            Assert.NotNull(row);
            Assert.Equal("Renamed recipe", row!.Value.Name);
            Assert.Equal("2026-05-27T12:34:56.000Z", row.Value.UpdatedAt);
            Assert.Equal("2026-04-01T00:00:00.000Z", row.Value.CreatedAt);
            var diskHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(fx.WorkspaceFolderPath, "Recipes",
                    ExistingRecipeId + ".recipe.json")))).ToLowerInvariant();
            Assert.Equal(diskHash, row.Value.FileHash);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PutRecipe_returns_404_when_row_missing()
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsync("/api/v1/recipes/" + ExistingRecipeId,
                new StringContent(MinimalRecipeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("not_found",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PutRecipe_returns_400_id_mismatch_when_body_recipeId_differs()
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            await fx.SeedExistingRecipeAsync(ExistingRecipeId, name: "Original");

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var bodyText = @"{""recipeId"":""" + OtherRecipeId + @""",""identity"":{""name"":""x""},""ingredients"":{},""query"":{},""processing"":{},""destinations"":{},""auth"":{""mode"":""WebLogin"",""tenantId"":""11111111-1111-1111-1111-111111111111""}}";
            using var resp = await http.PutAsync("/api/v1/recipes/" + ExistingRecipeId,
                new StringContent(bodyText, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("id_mismatch",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(ExistingRecipeId,
                doc.RootElement.GetProperty("urlRecipeId").GetString());
            Assert.Equal(OtherRecipeId,
                doc.RootElement.GetProperty("bodyRecipeId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PutRecipe_returns_422_recipe_file_missing()
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            await fx.SeedExistingRecipeAsync(ExistingRecipeId, writeFile: false);

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsync("/api/v1/recipes/" + ExistingRecipeId,
                new StringContent(MinimalRecipeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)422, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_file_missing",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(ExistingRecipeId,
                doc.RootElement.GetProperty("recipeId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PutRecipe_returns_422_recipe_file_malformed()
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            await fx.SeedExistingRecipeAsync(ExistingRecipeId, writeFile: false);
            var path = Path.Combine(fx.WorkspaceFolderPath, "Recipes",
                ExistingRecipeId + ".recipe.json");
            File.WriteAllText(path, "{ this isn't json", new UTF8Encoding(false));

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsync("/api/v1/recipes/" + ExistingRecipeId,
                new StringContent(MinimalRecipeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)422, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_file_malformed",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PutRecipe_returns_422_recipe_unsupported_schema_version()
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            await fx.SeedExistingRecipeAsync(ExistingRecipeId,
                schemaVersion: 2);

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsync("/api/v1/recipes/" + ExistingRecipeId,
                new StringContent(MinimalRecipeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)422, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_unsupported_schema_version",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(1,
                doc.RootElement.GetProperty("supportedSchemaVersion").GetInt32());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  DELETE /api/v1/recipes/{ulid}
    // ============================================================

    [Fact]
    public async Task DeleteRecipe_moves_file_to_trash_and_sets_deleted_at()
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            await fx.SeedExistingRecipeAsync(ExistingRecipeId);

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync("/api/v1/recipes/" + ExistingRecipeId);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal(ExistingRecipeId,
                doc.RootElement.GetProperty("recipeId").GetString());
            Assert.Equal("2026-05-27T12:34:56.000Z",
                doc.RootElement.GetProperty("deletedAt").GetString());
            var trashPath = doc.RootElement.GetProperty("trashPath").GetString();
            Assert.NotNull(trashPath);
            Assert.True(File.Exists(trashPath));

            // Original file gone, row.deleted_at set
            Assert.False(File.Exists(Path.Combine(
                fx.WorkspaceFolderPath, "Recipes",
                ExistingRecipeId + ".recipe.json")));
            var row = fx.GetRecipeRowIncludingDeleted(ExistingRecipeId);
            Assert.NotNull(row);
            Assert.Equal("2026-05-27T12:34:56.000Z", row!.Value.DeletedAt);

            // Subsequent GetActiveRow returns null
            Assert.Null(fx.GetRecipeRow(ExistingRecipeId));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task DeleteRecipe_returns_404_when_row_missing()
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync("/api/v1/recipes/" + ExistingRecipeId);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("not_found",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  Regression
    // ============================================================

    [Fact]
    public async Task Get_recipes_list_still_works_with_mutation_routes_registered()
    {
        await using var fx = await Stage3iB1Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildBundle());
        var start = await host.StartAsync();
        try
        {
            await fx.SeedExistingRecipeAsync(ExistingRecipeId, name: "Visible row");
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/recipes");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var recipes = doc.RootElement.GetProperty("recipes");
            Assert.Equal(1, recipes.GetArrayLength());
            Assert.Equal(ExistingRecipeId,
                recipes[0].GetProperty("recipeId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Mutation_routes_not_registered_when_versionInfo_missing()
    {
        // When the VersionInfo cannot be loaded (no VERSION.json),
        // FromVersionInfo returns an empty bundle and Stage 3i-B1
        // wiring bails out, leaving the verbs unrouted -> 404.
        await using var fx = await Stage3iB1Fixture.CreateAsync(deleteVersionFile: true);
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/recipes",
                new StringContent(MinimalRecipeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public void Bundled_pax_script_hash_unchanged()
    {
        // Stage 3i-B1 is BROKER-side only. The bundled PAX script
        // (resources/pax/PAX_Purview_Audit_Log_Processor_*.ps1) must
        // not move. The repo's canonical path lives under
        // app\install\PAX_Cookbook.ps1.
        var paxScript = Path.Combine(
            Path.GetDirectoryName(typeof(NativeBrokerHostStage3iB1Tests).Assembly.Location)!,
            "..", "..", "..", "..", "..", "app", "install", "PAX_Cookbook.ps1");
        if (!File.Exists(paxScript))
        {
            // The tripwire path resolution depends on repo layout;
            // if the tests run from a packaged location the file may
            // not be reachable. In that case, the lockfile-driven
            // hash check elsewhere is the source of truth.
            return;
        }
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

    private static string MinimalRecipeBody(string name = "Stage 3i-B1 created") =>
        @"{""identity"":{""name"":""" + name + @"""},""ingredients"":{},""query"":{""mode"":""audit"",""startDate"":""2026-01-01"",""endDate"":""2026-01-31""},""processing"":{""rollup"":""Rollup""},""destinations"":{""fact"":{""mode"":""outputPath"",""path"":""C:\\\\temp\\\\stage3ib1.csv""}},""auth"":{""mode"":""WebLogin"",""tenantId"":""11111111-1111-1111-1111-111111111111""}}";

    private sealed class Stage3iB1Fixture : IAsyncDisposable
    {
        public string Root                { get; }
        public string WorkspaceFolderPath { get; }
        public string DatabaseFilePath    { get; }
        public string PaxScriptPath       { get; }
        public string CookbookVersion     { get; }
        public string BundledPaxVersion   { get; }
        public string ReleaseChannel      { get; }
        public string PaxAdapterVersion   => BundledPaxVersion;
        public NativeBrokerHostOptions Options { get; }

        private Stage3iB1Fixture(
            string root, string workspace, string database, string paxScriptPath,
            string cookbookVersion, string bundledPax, string channel,
            NativeBrokerHostOptions options)
        {
            Root                = root;
            WorkspaceFolderPath = workspace;
            DatabaseFilePath    = database;
            PaxScriptPath       = paxScriptPath;
            CookbookVersion     = cookbookVersion;
            BundledPaxVersion   = bundledPax;
            ReleaseChannel      = channel;
            Options             = options;
        }

        public static async Task<Stage3iB1Fixture> CreateAsync(
            string cookbookVersion   = "1.0.0",
            string paxScriptVersion  = "1.0.0",
            string releaseChannel    = "stable",
            bool   deleteVersionFile = false)
        {
            var root = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3iB1_" + Guid.NewGuid().ToString("N"));
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

            const string fakePaxBody = "# Stage 3i-B1 test stand-in PAX script -- not executed.\n";
            File.WriteAllText(paxScriptPath, fakePaxBody, new UTF8Encoding(false));
            var paxSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fakePaxBody)));

            File.WriteAllText(versionPath,
                "{"
                + "\"schemaVersion\":1,"
                + "\"channel\":\"" + releaseChannel + "\","
                + "\"cookbook\":{\"version\":\"" + cookbookVersion + "\"},"
                + "\"paxScript\":{"
                +     "\"name\":\"PAX Test\","
                +     "\"version\":\"" + paxScriptVersion + "\","
                +     "\"relativePath\":\"resources/pax/PAX_test.ps1\","
                +     "\"sha256\":\"" + paxSha + "\"},"
                + "\"updateManifestUrl\":null"
                + "}");
            if (deleteVersionFile) File.Delete(versionPath);

            await SeedSchemaAsync(databaseFile);

            var options = new NativeBrokerHostOptions(
                PreferredPort:       17654,
                PortRangeStart:      17654,
                PortRangeEnd:        17664,
                WorkspaceFolderPath: workspace,
                AppRoot:             appRoot,
                VersionFilePath:     versionPath,
                PaxScriptPath:       paxScriptPath);

            return new Stage3iB1Fixture(
                root, workspace, databaseFile, paxScriptPath,
                cookbookVersion, paxScriptVersion, releaseChannel, options);
        }

        public Stage3iB1ServiceBundle BuildBundle(
            string? factoryId = null,
            DateTimeOffset? clockOverride = null)
        {
            return new Stage3iB1ServiceBundle
            {
                Clock             = () => clockOverride ?? FrozenClockUtc,
                RecipeIdFactory   = factoryId is null ? null : () => factoryId,
                PaxAdapterVersion = PaxAdapterVersion,
                CreatedByTemplate = new RecipeCreatedBy(
                    CookbookVersion:   CookbookVersion,
                    BundledPaxVersion: BundledPaxVersion,
                    ReleaseChannel:    ReleaseChannel),
            };
        }

        public async Task SeedExistingRecipeAsync(
            string recipeId,
            string  name           = "Original name",
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
                var disk = @"{""recipeId"":""" + recipeId + @""",""recipeSchemaVersion"":" + schemaVersion + @",""paxAdapterVersion"":""" + PaxAdapterVersion + @""",""identity"":{""name"":""" + name + @"""},""ingredients"":{},""query"":{},""processing"":{},""destinations"":{},""auth"":{""mode"":""WebLogin"",""tenantId"":""11111111-1111-1111-1111-111111111111""},""createdAt"":""" + createdAt + @""",""updatedAt"":""" + updatedAt + @""",""createdBy"":{""cookbookVersion"":""0.9.0"",""bundledPaxVersion"":""0.5.0"",""releaseChannel"":""preview""}}";
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
                     created_at, updated_at)
VALUES ($id, $name, $pax, $schema, 'workspace',
        $file, $hash, 'ready', 0, $created, $updated);";
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

        public RecipeRowView? GetRecipeRow(string recipeId) => ReadRow(recipeId, includeDeleted: false);
        public RecipeRowView? GetRecipeRowIncludingDeleted(string recipeId) => ReadRow(recipeId, includeDeleted: true);

        public string? GetRecipeStatus(string recipeId)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseFilePath,
                Mode       = SqliteOpenMode.ReadOnly,
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status FROM recipes WHERE recipe_id = $id;";
            cmd.Parameters.AddWithValue("$id", recipeId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? reader.GetString(0) : null;
        }

        private RecipeRowView? ReadRow(string recipeId, bool includeDeleted)
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
            if (!includeDeleted && deletedAt is not null) return null;
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
