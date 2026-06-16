using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3e parity tests for the native broker's cook-start route.
//
// Each test boots a fresh Stage3eWorkspaceFixture (temp directory)
// with:
//   * Workspace/Database/cookbook.sqlite seeded with the production
//     cooks-table schema (richer than the Stage 3d fixture: adds
//     recipe_snapshot_json, command_argv_json, command_argv_redacted,
//     and closure_reason/closure_evidence_json/abnormal_close_recorded_utc).
//   * Workspace/Recipes/<ulid>.recipe.json seeded with the Stage 3e
//     test payload (auth.mode + executionMode + paxParameters).
//   * AppRoot/resources/pax/fixture.ps1 -- a canonical stub PAX
//     script that reads its parameters and emits stdout/stderr lines
//     and exits with a configurable code.
//   * AppRoot/broker/Pax/Adapter.psm1 -- a stub adapter whose
//     Get-PaxInvocationPlan reads $Recipe.paxParameters and returns
//     a hashtable matching the production shape: SpawnArgv/PaxArgv/
//     SpawnCommand/PaxCommand/PaxScriptPath/ExtraArguments.
//   * AppRoot/VERSION.json with the canonical fixture PAX SHA-256
//     filled in at fixture build time (so the integrity verifier
//     matches the on-disk script).
//   * NativeBrokerHostOptions with PwshPath probed from
//     %ProgramFiles%\PowerShell\7\pwsh.exe.
//
// Tests assume PowerShell 7 is installed -- the cook-start route is
// fundamentally about a PowerShell sidecar + child process, so a
// machine without pwsh.exe cannot exercise it. The fixture asserts
// pwsh presence in CreateAsync().
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3eTests
{
    private const string PaxScriptBaselineHash =
        "0DD230734715ABD15CF4C0A76013672BF9AD6713C3F82520A6333B0DCDAAD361";

    // ---------- 1. Happy path: 201 + body shape ----------

    [Fact]
    public async Task CookStart_returns_201_with_cookId_recipeId_cookFolder()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "ok recipe", outLines: new[] { "hello" });
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal(recipeId, root.GetProperty("recipeId").GetString());
            Assert.Equal(36, root.GetProperty("cookId").GetString()!.Length);
            var cookFolder = root.GetProperty("cookFolder").GetString()!;
            Assert.True(Path.IsPathFullyQualified(cookFolder),
                "cookFolder must be absolute. got=" + cookFolder);
            Assert.True(Directory.Exists(cookFolder),
                "cookFolder must exist. got=" + cookFolder);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 2. Cook row inserted with running -> completed transition ----------

    [Fact]
    public async Task CookStart_writes_row_completed_on_exit_zero()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "completes", outLines: new[] { "x" });
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var cookId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("cookId").GetString()!;

            var row = ReadCookRow(fx.DatabaseFile, cookId);
            Assert.Equal("completed", row.Status);
            Assert.Equal(0, row.ExitCode);
            Assert.True(row.Pid is > 0);
            Assert.NotNull(row.StartedAt);
            Assert.NotNull(row.FinishedAt);
            Assert.NotNull(row.DurationSeconds);
            Assert.Null(row.ErrorClass);
            Assert.Null(row.ErrorMessage);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 3. Cook folder + 5 init files ----------

    [Fact]
    public async Task CookStart_creates_cook_folder_and_5_init_files()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "init", outLines: new[] { "x" });
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var cookFolder = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("cookFolder").GetString()!;

            Assert.True(File.Exists(Path.Combine(cookFolder, "recipe-snapshot.json")));
            Assert.True(File.Exists(Path.Combine(cookFolder, "cook-context.json")));
            Assert.True(File.Exists(Path.Combine(cookFolder, "command.txt")));
            Assert.True(File.Exists(Path.Combine(cookFolder, "command-argv.json")));
            Assert.True(File.Exists(Path.Combine(cookFolder, "cook.log")));
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 4. stdout captured to cook.log ----------

    [Fact]
    public async Task CookStart_captures_stdout_lines_to_cook_log()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "stdout",
            outLines: new[] { "line-one", "line-two" });
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var cookFolder = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("cookFolder").GetString()!;

            var log = File.ReadAllText(Path.Combine(cookFolder, "cook.log"));
            Assert.Contains("line-one", log);
            Assert.Contains("line-two", log);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 5. stderr captured with [STDERR] prefix ----------

    [Fact]
    public async Task CookStart_captures_stderr_lines_with_prefix()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "stderr",
            outLines: new[] { "stdout-line" },
            errLines: new[] { "stderr-line" });
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var cookFolder = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("cookFolder").GetString()!;

            var log = File.ReadAllText(Path.Combine(cookFolder, "cook.log"));
            Assert.Contains("stdout-line", log);
            Assert.Contains("[STDERR] stderr-line", log);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 6. Nonzero exit -> errored + nonzero_exit ----------

    [Fact]
    public async Task CookStart_marks_row_errored_on_nonzero_exit()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "exit5",
            outLines: new[] { "before" }, exitCode: 5);
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            // Stage 3e returns 500 supervisor_spawn_failed only when
            // the spawn itself failed -- a child that started and
            // then exited nonzero is still a successful "cook
            // started" from the route's perspective. We return 201
            // and reflect the failure in the cook row.
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var cookId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("cookId").GetString()!;

            var row = ReadCookRow(fx.DatabaseFile, cookId);
            Assert.Equal("errored", row.Status);
            Assert.Equal(5, row.ExitCode);
            Assert.Equal("nonzero_exit", row.ErrorClass);
            Assert.Equal("exit_5", row.ErrorMessage);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 7. Invalid ULID -> 400 ----------

    [Fact]
    public async Task CookStart_rejects_non_ulid_recipeId_with_400()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/not-a-ulid/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("invalid_recipe_id",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 8. Unknown recipe -> 404 ----------

    [Fact]
    public async Task CookStart_returns_404_when_recipe_row_missing()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/01HZ0000000000000000000000/cook",
                new StringContent(""));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("not_found",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 9. Recipe file missing -> 500 ----------

    [Fact]
    public async Task CookStart_returns_500_when_recipe_file_missing_on_disk()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "x", outLines: new[] { "x" });
        // Delete the recipe file but leave the DB row in place.
        File.Delete(Path.Combine(fx.WorkspaceFolderPath, "Recipes",
            recipeId + ".recipe.json"));

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("recipe_file_missing",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 10. Recipe file malformed JSON -> 412 ----------

    [Fact]
    public async Task CookStart_returns_412_when_recipe_file_malformed()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "bad", outLines: new[] { "x" });
        File.WriteAllText(Path.Combine(fx.WorkspaceFolderPath, "Recipes",
            recipeId + ".recipe.json"), "{this is not valid json");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("recipe_invalid",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 11. Recipe schemaVersion != 1 -> 412 ----------

    [Fact]
    public async Task CookStart_returns_412_when_recipe_schema_version_unsupported()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "v2", outLines: new[] { "x" });
        // Re-write with schemaVersion=2.
        var path = Path.Combine(fx.WorkspaceFolderPath, "Recipes",
            recipeId + ".recipe.json");
        var raw = File.ReadAllText(path);
        File.WriteAllText(path,
            raw.Replace("\"recipeSchemaVersion\":1", "\"recipeSchemaVersion\":2"));

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("recipe_invalid",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 12. AppRegistrationSecret -> 501 ----------

    [Fact]
    public async Task CookStart_returns_501_when_auth_mode_AppRegistrationSecret()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "app-secret",
            outLines: new[] { "x" }, authMode: "AppRegistrationSecret");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("auth_mode_not_implemented_native_stage3e",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 13. AppRegistrationCertificate -> 501 ----------

    [Fact]
    public async Task CookStart_returns_501_when_auth_mode_AppRegistrationCertificate()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "app-cert",
            outLines: new[] { "x" }, authMode: "AppRegistrationCertificate");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("auth_mode_not_implemented_native_stage3e",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 14. ManagedIdentity -> 501 ----------

    [Fact]
    public async Task CookStart_returns_501_when_auth_mode_ManagedIdentity()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "mi",
            outLines: new[] { "x" }, authMode: "ManagedIdentity");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("auth_mode_not_implemented_native_stage3e",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 15. executionMode != local-manual -> 412 ----------

    [Fact]
    public async Task CookStart_returns_412_when_executionMode_not_local_manual()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "sched",
            outLines: new[] { "x" }, executionMode: "scheduled");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("recipe_invalid",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 16. PAX hash mismatch -> 500 pax_script_integrity ----------

    [Fact]
    public async Task CookStart_returns_500_when_pax_hash_mismatches_baseline()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "tamper", outLines: new[] { "x" });
        // Mutate the PAX script after fixture setup so the on-disk
        // hash no longer matches the baseline in VERSION.json.
        File.AppendAllText(fx.PaxScriptPath, "\n# tampered\n");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("pax_script_integrity",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 17. PAX script missing -> 500 pax_script_integrity ----------

    [Fact]
    public async Task CookStart_returns_500_when_pax_script_missing_from_disk()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "no-pax", outLines: new[] { "x" });
        File.Delete(fx.PaxScriptPath);

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("pax_script_integrity",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 18. Per-recipe concurrency -> 409 recipe_busy ----------

    [Fact]
    public async Task CookStart_returns_409_for_concurrent_cook_on_same_recipe()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "concurrent",
            outLines: new[] { "x" }, sleepMs: 1500);

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);

            // Fire the first request and immediately fire the second
            // -- the first sleeps in PAX for 1.5s so the second hits
            // the busy guard.
            var first = Task.Run(() => http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent("")));

            // Allow the first request's StartCook to enter
            // _runningRecipes before the second probes the guard.
            await Task.Delay(300);

            using var second = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
            Assert.Equal("recipe_busy",
                doc.RootElement.GetProperty("error").GetString());

            using var firstResp = await first;
            Assert.Equal(HttpStatusCode.Created, firstResp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 19. cookFolder is absolute, DB stores relative ----------

    [Fact]
    public async Task CookStart_response_cookFolder_is_absolute_db_value_is_relative()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "paths", outLines: new[] { "x" });

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var bodyDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var responseFolder = bodyDoc.RootElement.GetProperty("cookFolder").GetString()!;
            var cookId = bodyDoc.RootElement.GetProperty("cookId").GetString()!;
            Assert.True(Path.IsPathFullyQualified(responseFolder));

            var row = ReadCookRow(fx.DatabaseFile, cookId);
            // PS broker invariant: DB stores workspace-relative with
            // forward slashes.
            Assert.StartsWith("Cooks/", row.CookFolder);
            Assert.DoesNotContain("\\", row.CookFolder);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 20. cooks row records command_argv_json ----------

    [Fact]
    public async Task CookStart_writes_command_argv_json_column()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "argv", outLines: new[] { "x" });

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var cookId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("cookId").GetString()!;

            var argv = ReadCookColumnString(fx.DatabaseFile, cookId,
                "command_argv_json");
            using var doc = JsonDocument.Parse(argv);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            // SpawnArgv is [NoProfile, NoLogo, Command, <expression>] so
            // four elements -- the runner does not re-quote.
            Assert.Equal(4, doc.RootElement.GetArrayLength());
            Assert.Equal("-NoProfile", doc.RootElement[0].GetString());
            Assert.Equal("-NoLogo",    doc.RootElement[1].GetString());
            Assert.Equal("-Command",   doc.RootElement[2].GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 21. Unknown /api still 404 (Stage 3a/3b regression) ----------

    [Fact]
    public async Task Unknown_api_v1_path_returns_404_after_stage_3e_registration()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.GetAsync("/api/v1/no-such-thing");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 22. SPA still serves / (Stage 3a regression) ----------

    [Fact]
    public async Task Root_path_still_serves_index_html_after_stage_3e_registration()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("<!doctype html>", body, StringComparison.OrdinalIgnoreCase);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 23. Stage 3c cook-read sees the new cook row ----------

    [Fact]
    public async Task Stage3c_cook_list_includes_row_created_by_stage3e()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "list-it", outLines: new[] { "x" });

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var cookId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("cookId").GetString()!;

            using var listResp = await http.GetAsync("/api/v1/cooks");
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
            var listBody = await listResp.Content.ReadAsStringAsync();
            using var listDoc = JsonDocument.Parse(listBody);
            var cooks = listDoc.RootElement.GetProperty("cooks");
            Assert.True(cooks.GetArrayLength() >= 1);
            var found = false;
            for (var i = 0; i < cooks.GetArrayLength(); i++)
            {
                if (cooks[i].GetProperty("cookId").GetString() == cookId) { found = true; break; }
            }
            Assert.True(found, "stage 3c list must surface the stage 3e row");
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 24. Stage 3c cook log read returns captured stdout ----------

    [Fact]
    public async Task Stage3c_cook_log_returns_captured_stdout_after_stage3e_cook()
    {
        await using var fx = await Stage3eWorkspaceFixture.CreateAsync();
        var recipeId = fx.SeedRecipe(name: "log-read",
            outLines: new[] { "captured-line-marker" });

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = NewClient(start.BaseUrl);
            using var resp = await http.PostAsync(
                "/api/v1/recipes/" + recipeId + "/cook", new StringContent(""));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var cookId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("cookId").GetString()!;

            using var logResp = await http.GetAsync("/api/v1/cooks/" + cookId + "/log");
            Assert.Equal(HttpStatusCode.OK, logResp.StatusCode);
            var logBody = await logResp.Content.ReadAsStringAsync();
            Assert.Contains("captured-line-marker", logBody);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 25. PAX baseline hash unchanged ----------

    [Fact]
    public void PAX_script_hash_unchanged_at_stage_3e()
    {
        var repoRoot = FindRepoRoot();
        var paxPath = Path.Combine(repoRoot,
            "app", "resources", "pax", "PAX_Purview_Audit_Log_Processor.ps1");
        Assert.True(File.Exists(paxPath), "PAX script must exist at " + paxPath);
        using var fs = File.OpenRead(paxPath);
        var hex = Convert.ToHexString(SHA256.HashData(fs));
        Assert.Equal(PaxScriptBaselineHash, hex);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static HttpClient NewClient(string baseUrl) =>
        new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "PAXCookbook.sln"))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent ?? string.Empty;
        }
        throw new InvalidOperationException("Could not locate repo root.");
    }

    private sealed record CookRowSnapshot(
        string Status,
        int? ExitCode,
        int? Pid,
        string CookFolder,
        string? StartedAt,
        string? FinishedAt,
        double? DurationSeconds,
        string? ErrorClass,
        string? ErrorMessage);

    private static CookRowSnapshot ReadCookRow(string databaseFile, string cookId)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode       = SqliteOpenMode.ReadOnly,
        }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT status, exit_code, pid, cook_folder,
                                   started_at, finished_at, duration_seconds,
                                   error_class, error_message
                              FROM cooks WHERE cook_id = $id";
        cmd.Parameters.AddWithValue("$id", cookId);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read(), "expected cook row for " + cookId);
        return new CookRowSnapshot(
            Status:          r.GetString(0),
            ExitCode:        r.IsDBNull(1) ? null : r.GetInt32(1),
            Pid:             r.IsDBNull(2) ? null : r.GetInt32(2),
            CookFolder:      r.GetString(3),
            StartedAt:       r.IsDBNull(4) ? null : r.GetString(4),
            FinishedAt:      r.IsDBNull(5) ? null : r.GetString(5),
            DurationSeconds: r.IsDBNull(6) ? null : r.GetDouble(6),
            ErrorClass:      r.IsDBNull(7) ? null : r.GetString(7),
            ErrorMessage:    r.IsDBNull(8) ? null : r.GetString(8));
    }

    private static string ReadCookColumnString(string databaseFile,
        string cookId, string columnName)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode       = SqliteOpenMode.ReadOnly,
        }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT [{columnName}] FROM cooks WHERE cook_id = $id";
        cmd.Parameters.AddWithValue("$id", cookId);
        var v = cmd.ExecuteScalar();
        return v as string ?? string.Empty;
    }

    // ============================================================
    // Stage 3e fixture
    // ============================================================

    private sealed class Stage3eWorkspaceFixture : IAsyncDisposable
    {
        public string Root { get; }
        public string WorkspaceFolderPath { get; }
        public string AppRoot { get; }
        public string DatabaseFile { get; }
        public string PaxScriptPath { get; }
        public NativeBrokerHostOptions Options { get; }

        private Stage3eWorkspaceFixture(string root, string ws, string appRoot,
            string db, string paxScript, NativeBrokerHostOptions options)
        {
            Root = root;
            WorkspaceFolderPath = ws;
            AppRoot = appRoot;
            DatabaseFile = db;
            PaxScriptPath = paxScript;
            Options = options;
        }

        public static async Task<Stage3eWorkspaceFixture> CreateAsync()
        {
            var pwsh = ResolvePwsh();
            Assert.True(File.Exists(pwsh),
                "Stage 3e tests require PowerShell 7 (pwsh.exe) at "
                + "%ProgramFiles%\\PowerShell\\7\\pwsh.exe");

            var root = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3e_" + Guid.NewGuid().ToString("N"));
            var workspace   = Path.Combine(root, "Workspace");
            var databaseDir = Path.Combine(workspace, "Database");
            var databaseFile= Path.Combine(databaseDir, "cookbook.sqlite");
            var recipesDir  = Path.Combine(workspace, "Recipes");
            var cooksDir    = Path.Combine(workspace, "Cooks");
            var appRoot     = Path.Combine(root, "AppRoot");
            var webRoot     = Path.Combine(appRoot, "web");
            var versionFile = Path.Combine(appRoot, "VERSION.json");
            var templates   = Path.Combine(appRoot, "templates");
            var paxDir      = Path.Combine(appRoot, "resources", "pax");
            var paxScript   = Path.Combine(paxDir, "fixture.ps1");
            var adapterDir  = Path.Combine(appRoot, "broker", "Pax");
            var adapter     = Path.Combine(adapterDir, "Adapter.psm1");

            Directory.CreateDirectory(databaseDir);
            Directory.CreateDirectory(recipesDir);
            Directory.CreateDirectory(cooksDir);
            Directory.CreateDirectory(webRoot);
            Directory.CreateDirectory(templates);
            Directory.CreateDirectory(paxDir);
            Directory.CreateDirectory(adapterDir);

            File.WriteAllText(paxScript, FixturePaxScript, new UTF8Encoding(false));
            File.WriteAllText(adapter,   FixtureAdapter,   new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(webRoot, "index.html"),
                "<!doctype html><html><body>stage3e</body></html>");

            var sha = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(paxScript)));
            File.WriteAllText(versionFile,
                "{\"schemaVersion\":1,\"channel\":\"stable\"," +
                "\"cookbook\":{\"version\":\"0.0.0-stage3e\"}," +
                "\"paxScript\":{\"name\":\"Stage 3e Stub PAX\"," +
                "\"version\":\"0.0.0-stage3e\"," +
                "\"relativePath\":\"resources/pax/fixture.ps1\"," +
                "\"sha256\":\"" + sha + "\"}," +
                "\"updateManifestUrl\":null}",
                new UTF8Encoding(false));

            await SeedDatabaseAsync(databaseFile);

            var options = new NativeBrokerHostOptions(
                PreferredPort:       17654,
                PortRangeStart:      17654,
                PortRangeEnd:        17664,
                WorkspaceFolderPath: workspace,
                WebRoot:             webRoot,
                AppRoot:             appRoot,
                VersionFilePath:     versionFile,
                TemplatesDir:        templates,
                PaxScriptPath:       paxScript,
                AdapterModulePath:   adapter,
                PwshPath:            pwsh);

            return new Stage3eWorkspaceFixture(root, workspace, appRoot,
                databaseFile, paxScript, options);
        }

        // Inserts a recipes row + writes <ulid>.recipe.json under the
        // workspace's Recipes dir. Returns the recipeId. The recipe
        // payload encodes the stub PAX behavior in paxParameters --
        // the stub Adapter.psm1 reads those parameters to construct
        // SpawnArgv.
        public string SeedRecipe(
            string name,
            string[]? outLines = null,
            string[]? errLines = null,
            int exitCode = 0,
            int sleepMs = 0,
            string authMode = "WebLogin",
            string executionMode = "local-manual")
        {
            outLines ??= Array.Empty<string>();
            errLines ??= Array.Empty<string>();

            var recipeId = NewUlid();
            var recipePath = Path.Combine(WorkspaceFolderPath, "Recipes",
                recipeId + ".recipe.json");
            var nowUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                System.Globalization.CultureInfo.InvariantCulture);

            // Build a strict JSON document so escape rules are clean.
            var recipe = new
            {
                recipeSchemaVersion = 1,
                name,
                executionMode,
                auth = new { mode = authMode },
                paxParameters = new
                {
                    exitCode,
                    sleepMs,
                    outLines,
                    errLines,
                }
            };
            File.WriteAllText(recipePath,
                JsonSerializer.Serialize(recipe,
                    new JsonSerializerOptions { WriteIndented = false }),
                new UTF8Encoding(false));

            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseFile,
                Mode       = SqliteOpenMode.ReadWrite,
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO recipes (
                recipe_id, name, file_path, file_hash, status,
                is_pinned, pax_adapter_version, recipe_schema_version,
                source, source_ref, last_validated_at, last_validation_status,
                last_cooked_at, last_cook_id, created_at, updated_at, deleted_at
            ) VALUES (
                $id, $name, $file_path, $file_hash, 'active',
                0, '0.0.0-stage3e', 1,
                'stage3e_test', NULL, NULL, NULL,
                NULL, NULL, $now, $now, NULL);";
            cmd.Parameters.AddWithValue("$id",        recipeId);
            cmd.Parameters.AddWithValue("$name",      name);
            cmd.Parameters.AddWithValue("$file_path", recipePath);
            cmd.Parameters.AddWithValue("$file_hash",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(recipePath))));
            cmd.Parameters.AddWithValue("$now",       nowUtc);
            cmd.ExecuteNonQuery();

            return recipeId;
        }

        private static string ResolvePwsh()
        {
            var pf  = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(pf))
            {
                var p = Path.Combine(pf, "PowerShell", "7", "pwsh.exe");
                if (File.Exists(p)) return p;
            }
            var pfx = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(pfx))
            {
                var p = Path.Combine(pfx, "PowerShell", "7", "pwsh.exe");
                if (File.Exists(p)) return p;
            }
            return string.Empty;
        }

        private static string NewUlid()
        {
            // Stage 3e accepts any 26-char Crockford ULID. Tests don't
            // need monotonic ordering, so we synthesize a random
            // alphabet sample of the right length.
            const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            Span<char> buf = stackalloc char[26];
            Span<byte> rnd = stackalloc byte[26];
            RandomNumberGenerator.Fill(rnd);
            for (var i = 0; i < 26; i++) buf[i] = alphabet[rnd[i] % alphabet.Length];
            return new string(buf);
        }

        private static async Task SeedDatabaseAsync(string databaseFile)
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
    recipe_id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    file_path TEXT NOT NULL,
    file_hash TEXT NOT NULL,
    status TEXT NOT NULL,
    is_pinned INTEGER NOT NULL,
    pax_adapter_version TEXT NOT NULL,
    recipe_schema_version INTEGER NOT NULL,
    source TEXT NOT NULL,
    source_ref TEXT,
    last_validated_at TEXT,
    last_validation_status TEXT,
    last_cooked_at TEXT,
    last_cook_id TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    deleted_at TEXT
);
-- Stage 3e cooks schema -- mirrors the production CREATE TABLE in
-- Start-Broker.ps1 (~line 1260) plus the deferred terminal-taxonomy
-- columns added at runtime by Add-CookColumnIfMissing.
CREATE TABLE cooks (
    cook_id TEXT PRIMARY KEY,
    recipe_id TEXT,
    recipe_version_id TEXT,
    recipe_snapshot_json TEXT NOT NULL,
    command_argv_json TEXT NOT NULL,
    command_argv_redacted TEXT NOT NULL,
    pax_script_path TEXT NOT NULL,
    pax_script_version TEXT NOT NULL,
    trigger TEXT NOT NULL,
    schedule_id TEXT,
    parent_cook_id TEXT,
    cook_folder TEXT NOT NULL,
    pid INTEGER,
    status TEXT NOT NULL,
    exit_code INTEGER,
    started_at TEXT,
    finished_at TEXT,
    duration_seconds REAL,
    error_class TEXT,
    error_message TEXT,
    summary_path TEXT,
    closure_reason TEXT,
    closure_evidence_json TEXT,
    abnormal_close_recorded_utc TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE auth_profiles (
    auth_profile_id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    mode TEXT NOT NULL,
    tenant_id TEXT,
    client_id TEXT,
    cred_man_target TEXT,
    cert_thumbprint TEXT,
    cert_store TEXT,
    description TEXT,
    last_verified_at TEXT,
    last_verified_result TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);";
            await cmd.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup -- temp dir survives even if a
                // file handle is still open. Garbage-collected by the
                // OS eventually.
            }
            return ValueTask.CompletedTask;
        }

        // Canonical stub PAX. Reads parameters, emits stdout/stderr
        // lines, optionally sleeps, exits with a configurable code.
        // The orchestrator does not parse PAX output -- the runner
        // tees stdout/stderr into cook.log via async drains.
        private const string FixturePaxScript =
@"param(
  [int]$ExitCode = 0,
  [int]$SleepMs = 0,
  [string]$OutLinesJson = '[]',
  [string]$ErrLinesJson = '[]'
)
$ErrorActionPreference = 'Continue'
try { $out = @($OutLinesJson | ConvertFrom-Json) } catch { $out = @() }
try { $err = @($ErrLinesJson | ConvertFrom-Json) } catch { $err = @() }
foreach ($l in $out) { if ($null -ne $l) { [Console]::Out.WriteLine([string]$l) } }
foreach ($l in $err) { if ($null -ne $l) { [Console]::Error.WriteLine([string]$l) } }
if ($SleepMs -gt 0) { Start-Sleep -Milliseconds $SleepMs }
[Environment]::Exit($ExitCode)
";

        // Stage 3e stub adapter. Get-PaxInvocationPlan reads the
        // recipe's paxParameters hashtable and returns the same shape
        // as the production adapter (SpawnArgv / PaxArgv / SpawnCommand
        // / PaxCommand / ExtraArguments / PaxScriptPath). The runner
        // only cares about SpawnArgv -- the other fields are present
        // so the sidecar contract matches production.
        private const string FixtureAdapter =
@"function Get-PaxInvocationPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Recipe,
        [Parameter(Mandatory)][string] $PaxScriptPath,
        [Parameter(Mandatory)][string] $ExecutionMode
    )
    $p = $null
    if ($Recipe -is [hashtable]) {
        if ($Recipe.ContainsKey('paxParameters')) { $p = $Recipe['paxParameters'] }
    } elseif ($Recipe.PSObject.Properties.Name -contains 'paxParameters') {
        $p = $Recipe.paxParameters
    }
    function _Get([object]$h, [string]$k, $def) {
        if ($null -eq $h) { return $def }
        if ($h -is [hashtable] -and $h.ContainsKey($k)) { return $h[$k] }
        if ($h.PSObject.Properties.Name -contains $k) { return $h.$k }
        return $def
    }
    $exit  = [int](_Get $p 'exitCode' 0)
    $sleep = [int](_Get $p 'sleepMs'  0)
    $out   = @(_Get $p 'outLines' @())
    $err   = @(_Get $p 'errLines' @())
    $outJson = ($out | ConvertTo-Json -Compress -Depth 4)
    if ($null -eq $outJson -or $outJson -eq '') { $outJson = '[]' }
    if ($outJson -notlike '[*]') { $outJson = '[' + $outJson + ']' }
    $errJson = ($err | ConvertTo-Json -Compress -Depth 4)
    if ($null -eq $errJson -or $errJson -eq '') { $errJson = '[]' }
    if ($errJson -notlike '[*]') { $errJson = '[' + $errJson + ']' }
    $escOut = $outJson -replace ""'"", ""''""
    $escErr = $errJson -replace ""'"", ""''""
    $escPax = $PaxScriptPath -replace ""'"", ""''""
    $cmd = ""& '"" + $escPax + ""' -ExitCode "" + $exit + "" -SleepMs "" + $sleep +
           "" -OutLinesJson '"" + $escOut + ""' -ErrLinesJson '"" + $escErr + ""'""
    $spawnArgv = @('-NoProfile','-NoLogo','-Command',$cmd)
    $paxArgv   = @('-NoProfile','-NoLogo','-File',$PaxScriptPath)
    return @{
        paxArgv        = $paxArgv
        paxCommand     = ($paxArgv -join ' ')
        extraArguments = ''
        spawnArgv      = $spawnArgv
        spawnCommand   = ($spawnArgv -join ' ')
        paxScriptPath  = $PaxScriptPath
    }
}
Export-ModuleMember -Function Get-PaxInvocationPlan
";
    }
}
