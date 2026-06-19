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

// Stage 3i-B2 parity tests for the native broker's recipe preview +
// template materialize surface:
//   POST /api/v1/recipes/preview
//   POST /api/v1/templates/{id}/materialize
//
// Shares the "NativeBrokerHostPortBinding" xUnit collection with the
// rest of the NativeBrokerHost tests so port-17654 binding is
// serialised across Stage 3a-3i runs. All tests inject a
// FakeRecipePreviewPlanProvider so the suite never spawns a real
// pwsh sidecar (the production seam is exercised by Stage 3e cook
// tests where the sidecar already runs).
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3iB2Tests
{
    // Canonical PAX baseline hash. Stage 3i-B2 is a BROKER-side
    // change; the PAX script itself does not move. Asserted in a
    // tripwire fact so any drift in the bundled script is loud.
    private const string PaxScriptBaselineHash =
        "1A9BC94783683AE1DA68EE6A86DE2106A96122B67B14EE20090E6687792E3878";

    // Crockford Base32 ULIDs (no I, L, O, U). Distinct from
    // Stage 3i-B1's fixtures so the two suites can run interleaved
    // even if a future change widens cross-cutting state.
    private const string FactoryRecipeId  = "01JKMNPQRSTVWXYZABCDEFGH28";
    private const string ExistingRecipeId = "01JKMNPQRSTVWXYZABCDEFGH26";

    // GUID-format auth profile ids. authProfileId is validated as a
    // UUID by RecipeValidator, so the test fixtures use canonical GUID
    // strings rather than the slug-style ids you see in Stage 3c read
    // tests (which never round-trip through the recipe schema).
    private const string ProfileSecretId    = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string ProfileCertId      = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string ProfileMissingId   = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    private static readonly DateTimeOffset FrozenClockUtc =
        new(2026, 5, 27, 12, 34, 56, TimeSpan.FromTicks(0));

    // ============================================================
    //  POST /api/v1/recipes/preview -- draft path
    // ============================================================

    [Fact]
    public async Task PostPreviewDraft_returns_200_with_projection_for_minimal_body()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(MinimalRecipeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal(FactoryRecipeId, doc.RootElement.GetProperty("recipeId").GetString());
            Assert.Equal("FAKE_PAX_COMMAND",
                doc.RootElement.GetProperty("command").GetString());
            var argv = doc.RootElement.GetProperty("argv");
            Assert.Equal(JsonValueKind.Array, argv.ValueKind);
            Assert.True(argv.GetArrayLength() > 0);
            Assert.Equal("", doc.RootElement.GetProperty("extraArguments").GetString());
            var spawn = doc.RootElement.GetProperty("spawn");
            Assert.Equal("FAKE_SPAWN_COMMAND", spawn.GetProperty("command").GetString());
            Assert.Equal(4, spawn.GetProperty("argv").GetArrayLength());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewDraft_does_not_mutate_database_or_filesystem()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(MinimalRecipeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // No row, no snapshot file -- preview is stateless.
            Assert.Null(fx.GetRecipeRow(FactoryRecipeId));
            Assert.False(File.Exists(Path.Combine(
                fx.WorkspaceFolderPath, "Recipes", FactoryRecipeId + ".recipe.json")));
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public async Task PostPreview_400_invalid_json_for_malformed_body(string body)
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_json",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewDraft_400_validation_failed_missing_identity_name()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // identity present but name missing
            var body = @"{""identity"":{},""ingredients"":{},""query"":{""mode"":""audit"",""startDate"":""2026-01-01"",""endDate"":""2026-01-31""},""processing"":{""rollup"":""Rollup""},""destinations"":{""fact"":{""mode"":""outputPath"",""path"":""C:\\\\temp\\\\x.csv""}},""auth"":{""mode"":""WebLogin"",""tenantId"":""11111111-1111-1111-1111-111111111111""}}";
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("validation_failed",
                doc.RootElement.GetProperty("error").GetString());
            var errors = doc.RootElement.GetProperty("errors");
            Assert.True(errors.GetArrayLength() > 0);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewDraft_preserves_caller_supplied_recipeId()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            const string callerId = "01JKMNPQRSTVWXYZABCDEF9999";
            var body = MinimalRecipeBody(recipeId: callerId);
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            // body contained `identity` -> draft path, factory NOT consulted.
            Assert.Equal(callerId, doc.RootElement.GetProperty("recipeId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewDraft_app_registration_missing_authProfileId_validation_failed()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = MinimalRecipeBody(authMode: "AppRegistrationSecret");
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("validation_failed",
                doc.RootElement.GetProperty("error").GetString());
            var err = doc.RootElement.GetProperty("errors")[0];
            Assert.Equal("/auth/authProfileId", err.GetProperty("instancePath").GetString());
            Assert.Equal("required",            err.GetProperty("keyword").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewDraft_app_registration_profile_not_found_validation_failed()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = MinimalRecipeBody(
                authMode: "AppRegistrationSecret",
                authProfileId: ProfileMissingId);
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var err = doc.RootElement.GetProperty("errors")[0];
            Assert.Equal("/auth/authProfileId", err.GetProperty("instancePath").GetString());
            Assert.Equal("profileNotFound",     err.GetProperty("keyword").GetString());
            Assert.Equal(ProfileMissingId,
                err.GetProperty("params").GetProperty("authProfileId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewDraft_app_registration_profile_mode_mismatch_validation_failed()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            await fx.SeedAuthProfileAsync(ProfileCertId, mode: "AppRegistrationCertificate");

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = MinimalRecipeBody(
                authMode: "AppRegistrationSecret",
                authProfileId: ProfileCertId);
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var err = doc.RootElement.GetProperty("errors")[0];
            Assert.Equal("profileModeMismatch", err.GetProperty("keyword").GetString());
            Assert.Equal("AppRegistrationSecret",
                err.GetProperty("params").GetProperty("recipeMode").GetString());
            Assert.Equal("AppRegistrationCertificate",
                err.GetProperty("params").GetProperty("profileMode").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewDraft_app_registration_profile_match_succeeds()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            await fx.SeedAuthProfileAsync(ProfileSecretId, mode: "AppRegistrationSecret");

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = MinimalRecipeBody(
                authMode: "AppRegistrationSecret",
                authProfileId: ProfileSecretId);
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewDraft_projection_failure_returns_extraArguments_error()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle(
                factoryId:    FactoryRecipeId,
                planProvider: new FakeRecipePreviewPlanProvider
                {
                    Next = (_, _) => PaxInvocationPlanResult.RecipeRejected("bad_switch_in_trailer"),
                }));
            var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(MinimalRecipeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("validation_failed",
                doc.RootElement.GetProperty("error").GetString());
            var err = doc.RootElement.GetProperty("errors")[0];
            Assert.Equal("/advanced/extraArguments",
                err.GetProperty("instancePath").GetString());
            Assert.Equal("projection",
                err.GetProperty("keyword").GetString());
            Assert.Equal("bad_switch_in_trailer",
                err.GetProperty("message").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/recipes/preview -- lookup path
    // ============================================================

    [Fact]
    public async Task PostPreviewLookup_returns_200_with_projection_for_existing_recipe()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            await fx.SeedExistingRecipeAsync(ExistingRecipeId);

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var lookupBody = @"{""recipeId"":""" + ExistingRecipeId + @"""}";
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(lookupBody, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal(ExistingRecipeId,
                doc.RootElement.GetProperty("recipeId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewLookup_404_not_found_when_row_missing()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""recipeId"":""" + ExistingRecipeId + @"""}";
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("not_found",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(ExistingRecipeId,
                doc.RootElement.GetProperty("recipeId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewLookup_404_recipe_file_missing_when_file_gone()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            await fx.SeedExistingRecipeAsync(ExistingRecipeId, writeFile: false);

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""recipeId"":""" + ExistingRecipeId + @"""}";
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_file_missing",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewLookup_422_recipe_file_malformed_when_file_unreadable()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            await fx.SeedExistingRecipeAsync(ExistingRecipeId, writeFile: false);
            File.WriteAllText(
                Path.Combine(fx.WorkspaceFolderPath, "Recipes",
                    ExistingRecipeId + ".recipe.json"),
                "{ this isn't json", new UTF8Encoding(false));

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""recipeId"":""" + ExistingRecipeId + @"""}";
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)422, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_file_malformed",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewLookup_422_recipe_unsupported_schema_version()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            await fx.SeedExistingRecipeAsync(ExistingRecipeId, schemaVersion: 2);

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""recipeId"":""" + ExistingRecipeId + @"""}";
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)422, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_unsupported_schema_version",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(1,
                doc.RootElement.GetProperty("supportedSchemaVersion").GetInt32());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostPreviewLookup_404_when_row_soft_deleted()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            await fx.SeedExistingRecipeAsync(ExistingRecipeId,
                deletedAt: "2026-05-26T00:00:00.000Z");

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""recipeId"":""" + ExistingRecipeId + @"""}";
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("not_found",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/templates/{id}/materialize
    // ============================================================

    [Fact]
    public async Task PostMaterialize_201_creates_row_and_file_with_fromTemplate_provenance()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await fx.WriteTemplateAsync("acme-baseline", templateVersion: "1.2.3", minPaxScriptVersion: "0.0.1");

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/templates/acme-baseline/materialize",
                new StringContent(MinimalMaterializeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)201, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal(FactoryRecipeId,
                doc.RootElement.GetProperty("recipeId").GetString());

            var recipe = doc.RootElement.GetProperty("recipe");
            Assert.Equal(FactoryRecipeId, recipe.GetProperty("recipeId").GetString());
            Assert.Equal(1, recipe.GetProperty("recipeSchemaVersion").GetInt32());
            Assert.Equal("2026-05-27T12:34:56.000Z",
                recipe.GetProperty("createdAt").GetString());
            Assert.Equal("2026-05-27T12:34:56.000Z",
                recipe.GetProperty("updatedAt").GetString());

            var fromT = recipe.GetProperty("createdBy").GetProperty("fromTemplate");
            Assert.Equal("acme-baseline", fromT.GetProperty("templateId").GetString());
            Assert.Equal("1.2.3", fromT.GetProperty("templateVersion").GetString());

            // Row written with source='template' / source_ref='acme-baseline@1.2.3'.
            var row = fx.GetRecipeRow(FactoryRecipeId);
            Assert.NotNull(row);
            Assert.Equal("template", row!.Value.Source);
            Assert.Equal("acme-baseline@1.2.3", row.Value.SourceRef);

            // File present, hash matches row.
            var filePath = Path.Combine(fx.WorkspaceFolderPath, "Recipes",
                FactoryRecipeId + ".recipe.json");
            Assert.True(File.Exists(filePath));
            var diskHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath)))
                .ToLowerInvariant();
            Assert.Equal(diskHash, row.Value.FileHash);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostMaterialize_404_template_not_found()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/templates/missing-template/materialize",
                new StringContent(MinimalMaterializeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("template_not_found",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("missing-template",
                doc.RootElement.GetProperty("templateId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostMaterialize_412_template_incompatible_with_paxIncompatible_detail()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync(bundledPaxVersion: "1.0.0");
        await fx.WriteTemplateAsync("requires-newer-pax",
            templateVersion:      "1.0.0",
            minPaxScriptVersion:  "2.0.0");

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/templates/requires-newer-pax/materialize",
                new StringContent(MinimalMaterializeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)412, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("template_incompatible",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("requires-newer-pax",
                doc.RootElement.GetProperty("templateId").GetString());
            Assert.Equal("1.0.0",
                doc.RootElement.GetProperty("bundledPaxVersion").GetString());
            Assert.Equal("2.0.0",
                doc.RootElement.GetProperty("minPaxScriptVersion").GetString());
            var detail = doc.RootElement.GetProperty("details")[0];
            Assert.Equal("/minPaxScriptVersion",
                detail.GetProperty("instancePath").GetString());
            Assert.Equal("paxIncompatible",
                detail.GetProperty("keyword").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public async Task PostMaterialize_400_invalid_json(string body)
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await fx.WriteTemplateAsync("acme-baseline");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/templates/acme-baseline/materialize",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_json",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostMaterialize_400_body_invalid_missing_identity_name()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await fx.WriteTemplateAsync("acme-baseline");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""identity"":{},""auth"":{""tenantId"":""11111111-1111-1111-1111-111111111111""},""query"":{""startDate"":""2026-01-01"",""endDate"":""2026-01-31""},""destinations"":{""fact"":{""path"":""C:\\\\out.csv""}}}";
            using var resp = await http.PostAsync(
                "/api/v1/templates/acme-baseline/materialize",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("materialize_body_invalid",
                doc.RootElement.GetProperty("error").GetString());
            var err = doc.RootElement.GetProperty("errors")[0];
            Assert.Equal("/identity", err.GetProperty("instancePath").GetString());
            Assert.Equal("required",  err.GetProperty("keyword").GetString());
            Assert.Equal("name",
                err.GetProperty("params").GetProperty("missingProperty").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostMaterialize_400_body_invalid_tenantId_pattern()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await fx.WriteTemplateAsync("acme-baseline");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // tenantId not a GUID
            var body = @"{""identity"":{""name"":""hi""},""auth"":{""tenantId"":""not-a-guid""},""query"":{""startDate"":""2026-01-01"",""endDate"":""2026-01-31""},""destinations"":{""fact"":{""path"":""C:\\\\out.csv""}}}";
            using var resp = await http.PostAsync(
                "/api/v1/templates/acme-baseline/materialize",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("materialize_body_invalid",
                doc.RootElement.GetProperty("error").GetString());
            var err = doc.RootElement.GetProperty("errors")[0];
            Assert.Equal("/auth/tenantId",
                err.GetProperty("instancePath").GetString());
            Assert.Equal("pattern", err.GetProperty("keyword").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostMaterialize_400_body_invalid_startDate_format()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await fx.WriteTemplateAsync("acme-baseline");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""identity"":{""name"":""hi""},""auth"":{""tenantId"":""11111111-1111-1111-1111-111111111111""},""query"":{""startDate"":""01/02/2026"",""endDate"":""2026-01-31""},""destinations"":{""fact"":{""path"":""C:\\\\out.csv""}}}";
            using var resp = await http.PostAsync(
                "/api/v1/templates/acme-baseline/materialize",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var err = doc.RootElement.GetProperty("errors")[0];
            Assert.Equal("/query/startDate",
                err.GetProperty("instancePath").GetString());
            Assert.Equal("format", err.GetProperty("keyword").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostMaterialize_400_body_invalid_additional_property()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await fx.WriteTemplateAsync("acme-baseline");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle());
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""identity"":{""name"":""hi""},""auth"":{""tenantId"":""11111111-1111-1111-1111-111111111111""},""query"":{""startDate"":""2026-01-01"",""endDate"":""2026-01-31""},""destinations"":{""fact"":{""path"":""C:\\\\out.csv""}},""extra"":""not-allowed""}";
            using var resp = await http.PostAsync(
                "/api/v1/templates/acme-baseline/materialize",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("materialize_body_invalid",
                doc.RootElement.GetProperty("error").GetString());
            var err = doc.RootElement.GetProperty("errors")[0];
            Assert.Equal("additionalProperties", err.GetProperty("keyword").GetString());
            Assert.Equal("extra",
                err.GetProperty("params").GetProperty("additionalProperty").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostMaterialize_no_row_no_file_when_body_invalid()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await fx.WriteTemplateAsync("acme-baseline");
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle(factoryId: FactoryRecipeId));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = @"{""identity"":{},""auth"":{""tenantId"":""11111111-1111-1111-1111-111111111111""},""query"":{""startDate"":""2026-01-01"",""endDate"":""2026-01-31""},""destinations"":{""fact"":{""path"":""C:\\\\out.csv""}}}";
            using var resp = await http.PostAsync(
                "/api/v1/templates/acme-baseline/materialize",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Null(fx.GetRecipeRow(FactoryRecipeId));
            Assert.False(File.Exists(Path.Combine(
                fx.WorkspaceFolderPath, "Recipes", FactoryRecipeId + ".recipe.json")));
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  Regression
    // ============================================================

    [Fact]
    public async Task Existing_recipe_mutation_routes_still_work_with_preview_registered()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3iB1ServiceOverride(fx.BuildB1Bundle())
            .WithStage3iB2ServiceOverride(fx.BuildB2Bundle(factoryId: "01JKMNPQRSTVWXYZABCDEF1111"));
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/recipes",
                new StringContent(MinimalRecipeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal((HttpStatusCode)201, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            // Stage 3i-B1 factory id wins (its bundle takes effect for POST).
            Assert.NotNull(doc.RootElement.GetProperty("recipeId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Preview_route_not_registered_when_versionInfo_missing()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync(deleteVersionFile: true);
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/recipes/preview",
                new StringContent(MinimalRecipeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Materialize_route_not_registered_when_versionInfo_missing()
    {
        await using var fx = await Stage3iB2Fixture.CreateAsync(deleteVersionFile: true);
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/templates/anything/materialize",
                new StringContent(MinimalMaterializeBody(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public void Bundled_pax_script_hash_unchanged()
    {
        // Stage 3i-B2 is BROKER-side only. The bundled PAX script
        // (app\install\PAX_Cookbook.ps1) must not move. If the repo
        // layout puts the script outside test-reachable space the
        // tripwire short-circuits (lockfile-driven hash check
        // elsewhere remains the source of truth).
        var paxScript = Path.Combine(
            Path.GetDirectoryName(typeof(NativeBrokerHostStage3iB2Tests).Assembly.Location)!,
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

    private static string MinimalRecipeBody(
        string  name          = "Stage 3i-B2 preview",
        string? recipeId      = null,
        string  authMode      = "WebLogin",
        string? authProfileId = null)
    {
        var ridPart = recipeId is null ? "" : @"""recipeId"":""" + recipeId + @""",";
        var authProfileIdPart = authProfileId is null
            ? ""
            : @",""authProfileId"":""" + authProfileId + @"""";
        return "{"
            + ridPart
            + @"""identity"":{""name"":""" + name + @"""},"
            + @"""ingredients"":{},"
            + @"""query"":{""mode"":""audit"",""startDate"":""2026-01-01"",""endDate"":""2026-01-31""},"
            + @"""processing"":{""rollup"":""Rollup""},"
            + @"""destinations"":{""fact"":{""mode"":""outputPath"",""path"":""C:\\\\temp\\\\stage3ib2.csv""}},"
            + @"""auth"":{""mode"":""" + authMode + @""",""tenantId"":""11111111-1111-1111-1111-111111111111""" + authProfileIdPart + "}"
            + "}";
    }

    private static string MinimalMaterializeBody() => @"{""identity"":{""name"":""mat-1""},""auth"":{""tenantId"":""11111111-1111-1111-1111-111111111111""},""query"":{""startDate"":""2026-01-01"",""endDate"":""2026-01-31""},""destinations"":{""fact"":{""path"":""C:\\\\out\\\\fact.csv""}}}";

    private sealed class FakeRecipePreviewPlanProvider : IRecipePreviewPlanProvider
    {
        public Func<string, string, PaxInvocationPlanResult>? Next { get; set; }

        public PaxInvocationPlanResult Resolve(string recipeJson, string paxScriptPath)
        {
            if (Next is not null) return Next(recipeJson, paxScriptPath);
            return PaxInvocationPlanResult.Ok(new PaxInvocationPlan(
                PaxArgv:        new[] { "-TenantId", "11111111-1111-1111-1111-111111111111" },
                ExtraArguments: "",
                PaxCommand:     "FAKE_PAX_COMMAND",
                SpawnArgv:      new[] { "-NoProfile", "-NoLogo", "-Command", "FAKE_SPAWN_INNER" },
                SpawnCommand:   "FAKE_SPAWN_COMMAND",
                PaxScriptPath:  paxScriptPath));
        }
    }

    private sealed class Stage3iB2Fixture : IAsyncDisposable
    {
        public string Root                { get; }
        public string WorkspaceFolderPath { get; }
        public string DatabaseFilePath    { get; }
        public string AppRoot             { get; }
        public string TemplatesDir        { get; }
        public string PaxScriptPath       { get; }
        public string CookbookVersion     { get; }
        public string BundledPaxVersion   { get; }
        public string ReleaseChannel      { get; }
        public string PaxAdapterVersion   => BundledPaxVersion;
        public NativeBrokerHostOptions Options { get; }

        private Stage3iB2Fixture(
            string root, string workspace, string database, string appRoot,
            string templatesDir, string paxScriptPath,
            string cookbookVersion, string bundledPax, string channel,
            NativeBrokerHostOptions options)
        {
            Root                = root;
            WorkspaceFolderPath = workspace;
            DatabaseFilePath    = database;
            AppRoot             = appRoot;
            TemplatesDir        = templatesDir;
            PaxScriptPath       = paxScriptPath;
            CookbookVersion     = cookbookVersion;
            BundledPaxVersion   = bundledPax;
            ReleaseChannel      = channel;
            Options             = options;
        }

        public static async Task<Stage3iB2Fixture> CreateAsync(
            string cookbookVersion    = "1.0.0",
            string bundledPaxVersion  = "1.0.0",
            string releaseChannel     = "stable",
            bool   deleteVersionFile  = false)
        {
            var root = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3iB2_" + Guid.NewGuid().ToString("N"));
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

            const string fakePaxBody = "# Stage 3i-B2 test stand-in PAX script -- not executed.\n";
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
            if (deleteVersionFile) File.Delete(versionPath);

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

            return new Stage3iB2Fixture(
                root, workspace, databaseFile, appRoot, templatesDir, paxScriptPath,
                cookbookVersion, bundledPaxVersion, releaseChannel, options);
        }

        public Stage3iB1ServiceBundle BuildB1Bundle(
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

        public Stage3iB2ServiceBundle BuildB2Bundle(
            string?                       factoryId    = null,
            DateTimeOffset?               clockOverride = null,
            IRecipePreviewPlanProvider?   planProvider  = null)
        {
            return new Stage3iB2ServiceBundle
            {
                Clock             = () => clockOverride ?? FrozenClockUtc,
                RecipeIdFactory   = factoryId is null ? null : () => factoryId,
                PaxAdapterVersion = PaxAdapterVersion,
                BundledPaxVersion = BundledPaxVersion,
                CreatedByTemplate = new RecipeCreatedBy(
                    CookbookVersion:   CookbookVersion,
                    BundledPaxVersion: BundledPaxVersion,
                    ReleaseChannel:    ReleaseChannel),
                PreviewPlanProvider = planProvider ?? new FakeRecipePreviewPlanProvider(),
            };
        }

        public async Task WriteTemplateAsync(
            string templateId,
            string templateVersion     = "1.0.0",
            string minPaxScriptVersion = "0.0.1")
        {
            var templatePath = Path.Combine(TemplatesDir, templateId + ".template.json");
            var json = "{"
                + "\"templateId\":\""        + templateId + "\","
                + "\"templateVersion\":\""   + templateVersion + "\","
                + "\"templateSchemaVersion\":1,"
                + "\"name\":\"Acme baseline\","
                + "\"description\":\"Test template\","
                + "\"minPaxScriptVersion\":\"" + minPaxScriptVersion + "\","
                + "\"recipeDefaults\":{"
                +     "\"ingredients\":{"
                +         "\"m365Usage\":{\"includeM365Usage\":false},"
                +         "\"entraUserData\":{\"includeUserInfo\":false}},"
                +     "\"processing\":{\"rollup\":\"Rollup\"},"
                +     "\"auth\":{\"mode\":\"WebLogin\"}}"
                + "}";
            File.WriteAllText(templatePath, json, new UTF8Encoding(false));
            await Task.CompletedTask;
        }

        public async Task SeedAuthProfileAsync(
            string authProfileId,
            string mode      = "AppRegistrationSecret",
            string tenantId  = "11111111-1111-1111-1111-111111111111",
            string clientId  = "22222222-2222-2222-2222-222222222222")
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
INSERT INTO auth_profiles (auth_profile_id, name, mode, tenant_id, client_id,
                           cred_man_target, cert_thumbprint, cert_store, description,
                           last_verified_at, last_verified_result, created_at, updated_at)
VALUES ($id, $name, $mode, $tenant, $client,
        NULL, NULL, NULL, NULL,
        NULL, NULL, $created, $updated);";
            cmd.Parameters.AddWithValue("$id",      authProfileId);
            cmd.Parameters.AddWithValue("$name",    authProfileId);
            cmd.Parameters.AddWithValue("$mode",    mode);
            cmd.Parameters.AddWithValue("$tenant",  tenantId);
            cmd.Parameters.AddWithValue("$client",  clientId);
            cmd.Parameters.AddWithValue("$created", "2026-01-01T00:00:00.000Z");
            cmd.Parameters.AddWithValue("$updated", "2026-01-01T00:00:00.000Z");
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public async Task SeedExistingRecipeAsync(
            string recipeId,
            string  name           = "Existing recipe",
            string  createdAt      = "2026-04-01T00:00:00.000Z",
            string  updatedAt      = "2026-04-01T00:00:00.000Z",
            string  fileHash       = "deadbeef",
            string? deletedAt      = null,
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
        $file, $hash, 'ready', 0, $created, $updated, $deleted);";
            cmd.Parameters.AddWithValue("$id",      recipeId);
            cmd.Parameters.AddWithValue("$name",    name);
            cmd.Parameters.AddWithValue("$pax",     PaxAdapterVersion);
            cmd.Parameters.AddWithValue("$schema",  schemaVersion);
            cmd.Parameters.AddWithValue("$file",    filePath);
            cmd.Parameters.AddWithValue("$hash",    fileHash);
            cmd.Parameters.AddWithValue("$created", createdAt);
            cmd.Parameters.AddWithValue("$updated", updatedAt);
            cmd.Parameters.AddWithValue("$deleted", (object?)deletedAt ?? DBNull.Value);
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
