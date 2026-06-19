using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3g parity tests for the native broker's scheduled-task surface.
// Each test uses an isolated Stage3gWorkspaceFixture (temp directory
// with recipes / cooks / scheduled_tasks tables). The real installed
// workspace and the real app/ directory are NEVER touched; the
// production registrar PowerShell file is NEVER spawned; Windows Task
// Scheduler is NEVER mutated.
//
// Tests share the "NativeBrokerHostPortBinding" xUnit collection with
// Stage 3a-3f so port-17654 binding is serialised.
//
// Coverage:
//   * GET list      -- empty / single / multi / LEFT JOIN recipes /
//                      LEFT JOIN with recipeDeletedAt / Cache-Control /
//                      workspace_database_unavailable.
//   * GET single    -- 400 recipe_id_invalid / 404 recipe_not_found /
//                      404 recipe_trashed / registered:false / full
//                      envelope when registered / last_stale_check_at
//                      refreshed / staleReason sentinel /
//                      projectionHashCurrent null / projectionHash
//                      Registered match / Cache-Control.
//   * Health        -- pure-function composer: not_registered, current,
//                      unknown, last_run_running, last_run_completed,
//                      last_run_failed (pax_nonzero_exit /
//                      wrapper_spawn_failed / wrapper_internal_error /
//                      generic), last_run_interrupted (orphan /
//                      generic), last_run_refused (status / errorClass),
//                      stale (when hashRecomputed=true and mismatch).
//   * PUT           -- 501 / scheduled_task_put_deferred / deferred
//                      array exactly ["webauthn_reauth", "projection_
//                      hash", "credential_manager_write"] / planned
//                      Stage / Cache-Control.
//   * DELETE        -- 501 / scheduled_task_delete_deferred / deferred
//                      array exactly ["webauthn_reauth"] / planned
//                      Stage / Cache-Control.
//   * Registrar     -- argv shape: pwsh + Register-PAXScheduledRecipe
//                      .ps1 / register includes -RecurrenceJson /
//                      unregister omits -RecurrenceJson / argv NEVER
//                      contains a -ClientSecret or similar / argv does
//                      NOT invoke PAXCookbook.exe / argv does NOT
//                      invoke Start-Broker.ps1 / PruneLogs keeps 32
//                      newest, deletes the rest / FakeScheduledTask
//                      Registrar records calls and surfaces exit code.
//   * PAX tripwire  -- Stage 3g must not have touched
//                      app\resources\pax\PAX_Purview_Audit_Log_
//                      Processor.ps1.
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3gTests
{
    private const string PaxScriptBaselineHash =
        "1A9BC94783683AE1DA68EE6A86DE2106A96122B67B14EE20090E6687792E3878";

    // A valid Crockford-base32 ULID (uppercase, no I L O U). 26 chars.
    private const string SampleRecipeId     = "01HQRC7N5VRSXG8K9MZTABCDEF";
    private const string SampleRecipeIdAlt  = "01HQRC7N5VRSXG8K9MZTABCDEG";
    private const string SampleScheduledTaskId = "01HQRC7N5VRSXG8K9MZTABZZZZ";

    // ============================================================
    //  GET /api/v1/scheduled-tasks -- list
    // ============================================================

    [Fact]
    public async Task List_returns_empty_array_when_no_scheduled_tasks_seeded()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/scheduled-tasks");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var arr = doc.RootElement.GetProperty("scheduledTasks");
            Assert.Equal(JsonValueKind.Array, arr.ValueKind);
            Assert.Equal(0, arr.GetArrayLength());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task List_returns_no_store_cache_header()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/scheduled-tasks");
            Assert.Equal("no-store", resp.Headers.CacheControl?.ToString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task List_returns_single_row_with_join_to_recipes_table()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeAsync(SampleRecipeId, "Test recipe");
        await fx.SeedScheduledTaskAsync(
            scheduledTaskId: SampleScheduledTaskId,
            recipeId:        SampleRecipeId,
            projectionHash:  "abc123");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/scheduled-tasks");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var arr = doc.RootElement.GetProperty("scheduledTasks");
            Assert.Equal(1, arr.GetArrayLength());
            var row = arr[0];
            Assert.Equal(SampleScheduledTaskId, row.GetProperty("scheduledTaskId").GetString());
            Assert.Equal(SampleRecipeId,         row.GetProperty("recipeId").GetString());
            Assert.Equal("Test recipe",          row.GetProperty("recipeName").GetString());
            Assert.Equal(JsonValueKind.Null,     row.GetProperty("recipeDeletedAt").ValueKind);
            Assert.Equal("abc123",               row.GetProperty("recipeProjectionHash").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task List_surfaces_recipeDeletedAt_when_recipe_is_trashed()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeAsync(SampleRecipeId, "Trashed recipe",
            deletedAt: "2026-05-27T18:00:00Z");
        await fx.SeedScheduledTaskAsync(
            scheduledTaskId: SampleScheduledTaskId,
            recipeId:        SampleRecipeId,
            projectionHash:  "abc123");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/scheduled-tasks");
            var doc = await ReadJsonAsync(resp);
            var row = doc.RootElement.GetProperty("scheduledTasks")[0];
            Assert.Equal("2026-05-27T18:00:00Z",
                row.GetProperty("recipeDeletedAt").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task List_returns_multiple_rows_ordered_by_registered_at_desc()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeAsync(SampleRecipeId,    "Recipe A");
        await fx.SeedRecipeAsync(SampleRecipeIdAlt, "Recipe B");
        await fx.SeedScheduledTaskAsync(
            scheduledTaskId: SampleScheduledTaskId,
            recipeId:        SampleRecipeId,
            projectionHash:  "hashA",
            registeredAt:    "2026-05-27T08:00:00Z");
        await fx.SeedScheduledTaskAsync(
            scheduledTaskId: "01HQRC7N5VRSXG8K9MZTABZZZA",
            recipeId:        SampleRecipeIdAlt,
            projectionHash:  "hashB",
            registeredAt:    "2026-05-27T09:00:00Z");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/scheduled-tasks");
            var doc = await ReadJsonAsync(resp);
            var arr = doc.RootElement.GetProperty("scheduledTasks");
            Assert.Equal(2, arr.GetArrayLength());
            // ORDER BY registered_at DESC -- newer (09:00) first.
            Assert.Equal("hashB", arr[0].GetProperty("recipeProjectionHash").GetString());
            Assert.Equal("hashA", arr[1].GetProperty("recipeProjectionHash").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task List_returns_500_workspace_database_unavailable_when_db_missing()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        // Delete the SQLite file to simulate an uninitialised workspace.
        File.Delete(fx.DatabaseFilePath);
        SqliteConnection.ClearAllPools();

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/scheduled-tasks");
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("workspace_database_unavailable",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  GET /api/v1/recipes/{id}/scheduled-task -- single
    // ============================================================

    [Fact]
    public async Task Single_returns_400_recipe_id_invalid_for_non_ulid()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/recipes/not-a-ulid/scheduled-task");
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_id_invalid",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Single_returns_404_recipe_not_found_when_recipe_missing()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync($"/api/v1/recipes/{SampleRecipeId}/scheduled-task");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_not_found",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Single_returns_404_recipe_trashed_when_recipe_deleted_at_set()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeAsync(SampleRecipeId, "Trashed recipe",
            deletedAt: "2026-05-27T18:00:00Z");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync($"/api/v1/recipes/{SampleRecipeId}/scheduled-task");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_trashed",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Single_returns_registered_false_when_no_scheduled_tasks_row()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeAsync(SampleRecipeId, "Unregistered recipe");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync($"/api/v1/recipes/{SampleRecipeId}/scheduled-task");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.False(doc.RootElement.GetProperty("registered").GetBoolean());
            Assert.Equal(JsonValueKind.Null,
                doc.RootElement.GetProperty("scheduledTask").ValueKind);
            var health = doc.RootElement.GetProperty("health");
            Assert.Equal("not_registered", health.GetProperty("status").GetString());
            Assert.Equal(ScheduledTaskHealthComposer.MessageNotRegistered,
                health.GetProperty("message").GetString());
            Assert.Equal("projection_hash_unavailable_in_native_broker",
                doc.RootElement.GetProperty("staleReason").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Single_returns_full_envelope_when_registered_with_no_cook_history()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeAsync(SampleRecipeId, "Registered recipe");
        await fx.SeedScheduledTaskAsync(
            scheduledTaskId: SampleScheduledTaskId,
            recipeId:        SampleRecipeId,
            projectionHash:  "registered-hash");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync($"/api/v1/recipes/{SampleRecipeId}/scheduled-task");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.True(doc.RootElement.GetProperty("registered").GetBoolean());

            var st = doc.RootElement.GetProperty("scheduledTask");
            Assert.Equal(SampleScheduledTaskId, st.GetProperty("scheduledTaskId").GetString());
            Assert.Equal(SampleRecipeId,         st.GetProperty("recipeId").GetString());
            Assert.Equal("registered-hash",      st.GetProperty("recipeProjectionHash").GetString());

            var health = doc.RootElement.GetProperty("health");
            // No terminal cook + no running cook + no hash recompute
            // = unknown branch (Stage 3g honesty).
            Assert.Equal("unknown",               health.GetProperty("status").GetString());
            Assert.Equal(ScheduledTaskHealthComposer.MessageUnknown,
                health.GetProperty("message").GetString());
            Assert.Equal(JsonValueKind.Null,
                health.GetProperty("projectionHashCurrent").ValueKind);
            Assert.Equal("registered-hash",
                health.GetProperty("projectionHashRegistered").GetString());

            Assert.Equal("projection_hash_unavailable_in_native_broker",
                doc.RootElement.GetProperty("staleReason").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Single_refreshes_last_stale_check_at_on_each_call()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeAsync(SampleRecipeId, "Registered recipe");
        await fx.SeedScheduledTaskAsync(
            scheduledTaskId: SampleScheduledTaskId,
            recipeId:        SampleRecipeId,
            projectionHash:  "h");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync($"/api/v1/recipes/{SampleRecipeId}/scheduled-task");
            var doc = await ReadJsonAsync(resp);
            var stamp = doc.RootElement.GetProperty("scheduledTask")
                          .GetProperty("lastStaleCheckAt").GetString();
            Assert.False(string.IsNullOrEmpty(stamp));
            // Also surfaced in health.staleProjectionCheckedAt.
            Assert.Equal(stamp, doc.RootElement.GetProperty("health")
                                  .GetProperty("staleProjectionCheckedAt").GetString());

            // The DB row should now also carry the same stamp.
            var dbStamp = fx.ReadLastStaleCheckAt(SampleRecipeId);
            Assert.Equal(stamp, dbStamp);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Single_returns_no_store_cache_header()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync($"/api/v1/recipes/{SampleRecipeId}/scheduled-task");
            Assert.Equal("no-store", resp.Headers.CacheControl?.ToString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Single_surfaces_last_run_completed_when_terminal_cook_present()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeAsync(SampleRecipeId, "Registered recipe");
        await fx.SeedScheduledTaskAsync(
            scheduledTaskId: SampleScheduledTaskId,
            recipeId:        SampleRecipeId,
            projectionHash:  "h");
        await fx.SeedCookAsync(
            cookId:          Guid.NewGuid().ToString("N"),
            recipeId:        SampleRecipeId,
            scheduleId:      SampleScheduledTaskId,
            trigger:         "scheduled",
            status:          "completed",
            errorClass:      null,
            exitCode:        0,
            startedAt:       "2026-05-27T10:00:00Z",
            finishedAt:      "2026-05-27T10:00:30Z");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync($"/api/v1/recipes/{SampleRecipeId}/scheduled-task");
            var doc = await ReadJsonAsync(resp);
            var health = doc.RootElement.GetProperty("health");
            // With hashRecomputed=false the "current" branch is
            // unreachable, so a sole completed terminal cook lands in
            // "unknown" -- which is the Stage 3g honest outcome.
            Assert.Equal("unknown", health.GetProperty("status").GetString());
            Assert.Equal("completed", health.GetProperty("lastTerminalStatus").GetString());
            Assert.Equal("2026-05-27T10:00:30Z",
                health.GetProperty("lastTerminalAt").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Single_surfaces_last_run_failed_when_terminal_cook_failed()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeAsync(SampleRecipeId, "Registered recipe");
        await fx.SeedScheduledTaskAsync(
            scheduledTaskId: SampleScheduledTaskId,
            recipeId:        SampleRecipeId,
            projectionHash:  "h");
        await fx.SeedCookAsync(
            cookId:          Guid.NewGuid().ToString("N"),
            recipeId:        SampleRecipeId,
            scheduleId:      SampleScheduledTaskId,
            trigger:         "scheduled",
            status:          "failed",
            errorClass:      "pax_nonzero_exit",
            exitCode:        2,
            startedAt:       "2026-05-27T10:00:00Z",
            finishedAt:      "2026-05-27T10:00:30Z");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync($"/api/v1/recipes/{SampleRecipeId}/scheduled-task");
            var doc = await ReadJsonAsync(resp);
            var health = doc.RootElement.GetProperty("health");
            Assert.Equal("last_run_failed", health.GetProperty("status").GetString());
            Assert.Equal(ScheduledTaskHealthComposer.MessageLastRunFailedPaxNonzeroExit,
                health.GetProperty("message").GetString());
            Assert.Equal("pax_nonzero_exit",
                health.GetProperty("lastTerminalErrorClass").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Single_surfaces_last_run_running_when_running_cook_exists()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeAsync(SampleRecipeId, "Registered recipe");
        await fx.SeedScheduledTaskAsync(
            scheduledTaskId: SampleScheduledTaskId,
            recipeId:        SampleRecipeId,
            projectionHash:  "h");
        await fx.SeedCookAsync(
            cookId:          Guid.NewGuid().ToString("N"),
            recipeId:        SampleRecipeId,
            scheduleId:      SampleScheduledTaskId,
            trigger:         "scheduled",
            status:          "running",
            errorClass:      null,
            exitCode:        null,
            startedAt:       "2026-05-27T10:00:00Z",
            finishedAt:      null);

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync($"/api/v1/recipes/{SampleRecipeId}/scheduled-task");
            var doc = await ReadJsonAsync(resp);
            var health = doc.RootElement.GetProperty("health");
            Assert.Equal("last_run_running", health.GetProperty("status").GetString());
            Assert.Equal(ScheduledTaskHealthComposer.MessageLastRunRunning,
                health.GetProperty("message").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  V1.S07 health composer (pure function)
    // ============================================================

    [Fact]
    public void Health_not_registered_when_task_row_null()
    {
        var h = ScheduledTaskHealthComposer.Compose(
            taskRow: null, currentHash: null, hashRecomputed: false,
            staleCheckedAt: null, lastTerminal: null, hasRunning: false);
        Assert.Equal("not_registered", h.Status);
        Assert.False(h.Stale);
        Assert.Null(h.ProjectionHashCurrent);
        Assert.Null(h.ProjectionHashRegistered);
        Assert.Equal(ScheduledTaskHealthComposer.MessageNotRegistered, h.Message);
    }

    [Fact]
    public void Health_stale_branch_fires_when_hash_recomputed_and_mismatch()
    {
        var row = MakeTaskRow(registeredHash: "old");
        var h = ScheduledTaskHealthComposer.Compose(
            taskRow: row, currentHash: "new", hashRecomputed: true,
            staleCheckedAt: "2026-05-27T12:00:00Z", lastTerminal: null,
            hasRunning: false);
        Assert.Equal("stale", h.Status);
        Assert.True(h.Stale);
        Assert.Equal(ScheduledTaskHealthComposer.MessageStale, h.Message);
    }

    [Fact]
    public void Health_current_branch_with_completed_run()
    {
        var row = MakeTaskRow(registeredHash: "h");
        var term = MakeTerminal("completed", null);
        var h = ScheduledTaskHealthComposer.Compose(
            taskRow: row, currentHash: "h", hashRecomputed: true,
            staleCheckedAt: "2026-05-27T12:00:00Z",
            lastTerminal: term, hasRunning: false);
        Assert.Equal("current", h.Status);
        Assert.False(h.Stale);
        Assert.Equal(ScheduledTaskHealthComposer.MessageCurrentWithCompleted, h.Message);
    }

    [Fact]
    public void Health_current_branch_without_runs()
    {
        var row = MakeTaskRow(registeredHash: "h");
        var h = ScheduledTaskHealthComposer.Compose(
            taskRow: row, currentHash: "h", hashRecomputed: true,
            staleCheckedAt: null, lastTerminal: null, hasRunning: false);
        Assert.Equal("current", h.Status);
        Assert.Equal(ScheduledTaskHealthComposer.MessageCurrentNoRuns, h.Message);
    }

    [Fact]
    public void Health_unknown_when_hash_recompute_failed()
    {
        var row = MakeTaskRow(registeredHash: "h");
        var h = ScheduledTaskHealthComposer.Compose(
            taskRow: row, currentHash: null, hashRecomputed: false,
            staleCheckedAt: null, lastTerminal: null, hasRunning: false);
        Assert.Equal("unknown", h.Status);
        Assert.Equal(ScheduledTaskHealthComposer.MessageUnknown, h.Message);
    }

    [Fact]
    public void Health_last_run_refused_via_status()
    {
        var row = MakeTaskRow();
        var term = MakeTerminal("refused", null);
        var h = ScheduledTaskHealthComposer.Compose(
            row, null, false, null, term, false);
        Assert.Equal("last_run_refused", h.Status);
        Assert.Equal(ScheduledTaskHealthComposer.MessageLastRunRefused, h.Message);
    }

    [Fact]
    public void Health_last_run_refused_via_error_class_on_completed_status()
    {
        // PS broker treats refused_stale_projection error_class as
        // refused-equivalent even when status field says otherwise.
        var row = MakeTaskRow();
        var term = MakeTerminal("completed", "refused_stale_projection");
        var h = ScheduledTaskHealthComposer.Compose(
            row, null, false, null, term, false);
        Assert.Equal("last_run_refused", h.Status);
    }

    [Theory]
    [InlineData("pax_nonzero_exit",
        "Last scheduled run failed in PAX. Open the run and inspect the PAX log for the exit code and reason.")]
    [InlineData("wrapper_spawn_failed",
        "Last scheduled run failed: the wrapper could not spawn PAX. Open the run and inspect the wrapper envelope.")]
    [InlineData("wrapper_internal_error",
        "Last scheduled run failed: wrapper internal error. Open the run and inspect the wrapper envelope.")]
    [InlineData(null,
        "Last scheduled run failed. Open the run and inspect the PAX log.")]
    public void Health_last_run_failed_messages(string? errorClass, string expectedMessage)
    {
        var row = MakeTaskRow();
        var term = MakeTerminal("failed", errorClass);
        var h = ScheduledTaskHealthComposer.Compose(
            row, null, false, null, term, false);
        Assert.Equal("last_run_failed", h.Status);
        Assert.Equal(expectedMessage, h.Message);
    }

    [Theory]
    [InlineData("wrapper_orphan_classified",
        "Last scheduled run was orphan-classified after the grace window. Inspect the wrapper folder and Task Scheduler history.")]
    [InlineData(null,
        "Last scheduled run was interrupted. Inspect the wrapper folder and Task Scheduler history.")]
    public void Health_last_run_interrupted_messages(string? errorClass, string expectedMessage)
    {
        var row = MakeTaskRow();
        var term = MakeTerminal("interrupted", errorClass);
        var h = ScheduledTaskHealthComposer.Compose(
            row, null, false, null, term, false);
        Assert.Equal("last_run_interrupted", h.Status);
        Assert.Equal(expectedMessage, h.Message);
    }

    [Fact]
    public void Health_last_run_running_when_has_running_and_no_terminal()
    {
        var row = MakeTaskRow();
        var h = ScheduledTaskHealthComposer.Compose(
            row, null, false, null, lastTerminal: null, hasRunning: true);
        Assert.Equal("last_run_running", h.Status);
    }

    [Fact]
    public void Health_terminal_at_falls_back_to_started_at_when_finished_at_null()
    {
        var row = MakeTaskRow();
        var term = new ScheduledTaskTerminalCook(
            CookId: "c1", Status: "failed", ErrorClass: null,
            ExitCode: 1, StartedAt: "2026-05-27T10:00:00Z",
            FinishedAt: null);
        var h = ScheduledTaskHealthComposer.Compose(
            row, null, false, null, term, false);
        Assert.Equal("2026-05-27T10:00:00Z", h.LastTerminalAt);
    }

    // ============================================================
    //  PUT 501 contract
    // ============================================================

    [Fact]
    public async Task Put_returns_501_with_scheduled_task_put_deferred()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                $"/api/v1/recipes/{SampleRecipeId}/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 0 } });
            Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
            Assert.Equal("no-store", resp.Headers.CacheControl?.ToString());

            var doc = await ReadJsonAsync(resp);
            Assert.Equal("scheduled_task_put_deferred",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("3h",
                doc.RootElement.GetProperty("plannedStage").GetString());

            var deferred = doc.RootElement.GetProperty("deferred").EnumerateArray()
                .Select(e => e.GetString()!).ToArray();
            // Stage 3h terminology correction: scheduleConfig re-auth uses
            // the Windows UserConsentVerifier (Hello/PIN/biometric), NOT
            // WebAuthn. WebAuthn is reserved for the SPA lock-overlay.
            Assert.Equal(
                new[] { "windows_reauth", "projection_hash", "credential_manager_write" },
                deferred);
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  DELETE 501 contract
    // ============================================================

    [Fact]
    public async Task Delete_returns_501_with_scheduled_task_delete_deferred()
    {
        await using var fx = await Stage3gWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync(
                $"/api/v1/recipes/{SampleRecipeId}/scheduled-task");
            Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
            Assert.Equal("no-store", resp.Headers.CacheControl?.ToString());

            var doc = await ReadJsonAsync(resp);
            Assert.Equal("scheduled_task_delete_deferred",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("3h",
                doc.RootElement.GetProperty("plannedStage").GetString());

            var deferred = doc.RootElement.GetProperty("deferred").EnumerateArray()
                .Select(e => e.GetString()!).ToArray();
            // Stage 3h terminology correction: see PUT deferred token.
            Assert.Equal(new[] { "windows_reauth" }, deferred);
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  Registrar argv builder
    // ============================================================

    [Fact]
    public void Registrar_argv_for_register_action_matches_ps_broker_shape()
    {
        var req = new ScheduledTaskRegistrarRequest(
            Action:          "register",
            RecipeId:        SampleRecipeId,
            ScheduledTaskId: SampleScheduledTaskId,
            WorkspacePath:   @"C:\Workspace",
            RecurrenceJson:  "{\"kind\":\"daily\",\"hour\":9,\"minute\":0}");
        var argv = WindowsScheduledTaskRegistrar.BuildArgumentList(
            @"C:\AppRoot\install\Register-PAXScheduledRecipe.ps1", req);
        // Index 0-2: -NoProfile -NoLogo -NonInteractive (pwsh hardening).
        Assert.Equal("-NoProfile",      argv[0]);
        Assert.Equal("-NoLogo",         argv[1]);
        Assert.Equal("-NonInteractive", argv[2]);
        // Index 3-4: -File <registrar>.
        Assert.Equal("-File", argv[3]);
        Assert.Equal(@"C:\AppRoot\install\Register-PAXScheduledRecipe.ps1", argv[4]);
        // Index 5-6: -Action register.
        Assert.Equal("-Action", argv[5]);
        Assert.Equal("register", argv[6]);
        // Index 7-8: -WorkspacePath.
        Assert.Equal("-WorkspacePath", argv[7]);
        Assert.Equal(@"C:\Workspace", argv[8]);
        // Index 9-10: -RecipeId.
        Assert.Equal("-RecipeId", argv[9]);
        Assert.Equal(SampleRecipeId, argv[10]);
        // Index 11-12: -ScheduledTaskId.
        Assert.Equal("-ScheduledTaskId", argv[11]);
        Assert.Equal(SampleScheduledTaskId, argv[12]);
        // Index 13-14: -RecurrenceJson <json>.
        Assert.Equal("-RecurrenceJson", argv[13]);
        Assert.Equal("{\"kind\":\"daily\",\"hour\":9,\"minute\":0}", argv[14]);
        Assert.Equal(15, argv.Count);
    }

    [Fact]
    public void Registrar_argv_for_unregister_action_omits_recurrence_json()
    {
        var req = new ScheduledTaskRegistrarRequest(
            Action:          "unregister",
            RecipeId:        SampleRecipeId,
            ScheduledTaskId: SampleScheduledTaskId,
            WorkspacePath:   @"C:\Workspace",
            RecurrenceJson:  null);
        var argv = WindowsScheduledTaskRegistrar.BuildArgumentList(
            @"C:\AppRoot\install\Register-PAXScheduledRecipe.ps1", req);
        Assert.Equal("unregister", argv[6]);
        Assert.DoesNotContain("-RecurrenceJson", argv);
        Assert.Equal(13, argv.Count);
    }

    [Fact]
    public void Registrar_argv_never_contains_a_client_secret_argument_name()
    {
        var req = new ScheduledTaskRegistrarRequest(
            Action:          "register",
            RecipeId:        SampleRecipeId,
            ScheduledTaskId: SampleScheduledTaskId,
            WorkspacePath:   @"C:\Workspace",
            RecurrenceJson:  "{\"kind\":\"daily\",\"hour\":9,\"minute\":0}");
        var argv = WindowsScheduledTaskRegistrar.BuildArgumentList(
            @"C:\AppRoot\install\Register-PAXScheduledRecipe.ps1", req);
        // The argv contract carries NO secret-bearing parameter -- the
        // wrapper reads the secret from Windows Credential Manager at
        // fire-time, not from the broker process. Stage 3g must keep
        // this invariant even though PUT/DELETE return 501.
        foreach (var token in argv)
        {
            Assert.DoesNotContain("ClientSecret", token, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Secret",       token, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Password",     token, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Registrar_argv_never_invokes_PAXCookbook_or_StartBroker()
    {
        var req = new ScheduledTaskRegistrarRequest(
            Action:          "register",
            RecipeId:        SampleRecipeId,
            ScheduledTaskId: SampleScheduledTaskId,
            WorkspacePath:   @"C:\Workspace",
            RecurrenceJson:  "{}");
        var argv = WindowsScheduledTaskRegistrar.BuildArgumentList(
            @"C:\AppRoot\install\Register-PAXScheduledRecipe.ps1", req);
        // The registrar must be the SOLE file invoked. PAX/Cookbook
        // executables and Start-Broker.ps1 must never appear on the
        // argv -- doctrine boundary check.
        foreach (var token in argv)
        {
            Assert.DoesNotContain("PAXCookbook.exe",   token, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Start-Broker.ps1",  token, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Registrar_argv_rejects_unknown_action()
    {
        var req = new ScheduledTaskRegistrarRequest(
            Action:          "obliterate",
            RecipeId:        SampleRecipeId,
            ScheduledTaskId: SampleScheduledTaskId,
            WorkspacePath:   @"C:\Workspace",
            RecurrenceJson:  null);
        Assert.Throws<ArgumentException>(() =>
            WindowsScheduledTaskRegistrar.BuildArgumentList(@"C:\registrar.ps1", req));
    }

    [Fact]
    public void Registrar_prune_logs_keeps_only_thirty_two_newest_files()
    {
        var staging = Path.Combine(Path.GetTempPath(),
            "PAXCookbookStage3gPrune_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            // Create 40 files spaced 1 minute apart so LastWriteTimeUtc
            // sort is deterministic.
            var baseTime = DateTime.UtcNow.AddMinutes(-200);
            for (int i = 0; i < 40; i++)
            {
                var p = Path.Combine(staging, $"register_x_{i:D3}.out.log");
                File.WriteAllText(p, "x");
                File.SetLastWriteTimeUtc(p, baseTime.AddMinutes(i));
            }
            var removed = WindowsScheduledTaskRegistrar.PruneLogs(staging, 32);
            Assert.Equal(8, removed);
            var remaining = Directory.GetFiles(staging);
            Assert.Equal(32, remaining.Length);
            // The 8 oldest (000..007) should be gone.
            for (int i = 0; i < 8; i++)
            {
                Assert.False(File.Exists(Path.Combine(staging, $"register_x_{i:D3}.out.log")));
            }
            // The 32 newest (008..039) should remain.
            for (int i = 8; i < 40; i++)
            {
                Assert.True(File.Exists(Path.Combine(staging, $"register_x_{i:D3}.out.log")));
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Registrar_prune_logs_is_noop_when_below_limit()
    {
        var staging = Path.Combine(Path.GetTempPath(),
            "PAXCookbookStage3gPrune_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            for (int i = 0; i < 10; i++)
            {
                File.WriteAllText(
                    Path.Combine(staging, $"register_x_{i:D3}.out.log"), "x");
            }
            var removed = WindowsScheduledTaskRegistrar.PruneLogs(staging, 32);
            Assert.Equal(0, removed);
            Assert.Equal(10, Directory.GetFiles(staging).Length);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Fake_registrar_records_calls_and_surfaces_canned_exit_code()
    {
        var fake = new FakeScheduledTaskRegistrar
        {
            CannedResult = new ScheduledTaskRegistrarResult(
                ExitCode: 7, Stdout: "out", Stderr: "err",
                LogPath: null, DurationMs: 42),
        };
        var req = new ScheduledTaskRegistrarRequest(
            Action: "register", RecipeId: SampleRecipeId,
            ScheduledTaskId: SampleScheduledTaskId,
            WorkspacePath: @"C:\Workspace",
            RecurrenceJson: "{\"k\":1}");
        var result = await fake.InvokeAsync(req);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("out", result.Stdout);
        Assert.Equal("err", result.Stderr);
        Assert.Single(fake.Calls);
        Assert.Equal("register", fake.Calls[0].Action);
    }

    // ============================================================
    //  PAX baseline tripwire
    // ============================================================

    [Fact]
    public void PAX_script_baseline_hash_unchanged()
    {
        var repoRoot   = ResolveRepoRoot();
        var paxScript  = Path.Combine(repoRoot, "app", "resources", "pax",
            "PAX_Purview_Audit_Log_Processor.ps1");
        Assert.True(File.Exists(paxScript), $"PAX script missing: {paxScript}");
        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(paxScript);
        var hash = Convert.ToHexString(sha.ComputeHash(fs));
        Assert.Equal(PaxScriptBaselineHash, hash);
    }

    // ============================================================
    //  Helpers
    // ============================================================

    private static ScheduledTaskRow MakeTaskRow(string registeredHash = "h") =>
        new ScheduledTaskRow(
            ScheduledTaskId:      SampleScheduledTaskId,
            RecipeId:             SampleRecipeId,
            WindowsTaskName:      "PAXCookbook_Test",
            WindowsTaskPath:      @"\PAX Cookbook\",
            RecipeProjectionHash: registeredHash,
            PaxScriptVersion:     "0.0.0-test",
            RegisteredAt:         "2026-05-27T08:00:00Z",
            RegisteredByUser:     "TEST\\User",
            LastImportedCookId:   null,
            LastImportedAt:       null,
            LastStaleCheckAt:     null,
            Status:               "active",
            CreatedAt:            "2026-05-27T08:00:00Z",
            UpdatedAt:            "2026-05-27T08:00:00Z");

    private static ScheduledTaskTerminalCook MakeTerminal(
        string status, string? errorClass) =>
        new ScheduledTaskTerminalCook(
            CookId:     "c-" + Guid.NewGuid().ToString("N"),
            Status:     status,
            ErrorClass: errorClass,
            ExitCode:   string.Equals(status, "completed", StringComparison.Ordinal) ? 0 : 1,
            StartedAt:  "2026-05-27T10:00:00Z",
            FinishedAt: "2026-05-27T10:00:30Z");

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp)
    {
        using var stream = await resp.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    // Find the repo root by walking up from the test assembly location
    // until we find a directory that contains app\resources\pax. Used
    // by the PAX tripwire fact.
    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "app", "resources", "pax")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repo root from " + AppContext.BaseDirectory);
    }

    // ============================================================
    //  Stage 3g workspace fixture
    // ============================================================

    private sealed class Stage3gWorkspaceFixture : IAsyncDisposable
    {
        public string Root { get; }
        public string WorkspaceFolderPath { get; }
        public string DatabaseFilePath { get; }
        public NativeBrokerHostOptions Options { get; }

        private Stage3gWorkspaceFixture(
            string root, string workspace, string database,
            NativeBrokerHostOptions options)
        {
            Root = root;
            WorkspaceFolderPath = workspace;
            DatabaseFilePath = database;
            Options = options;
        }

        public static async Task<Stage3gWorkspaceFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3g_" + Guid.NewGuid().ToString("N"));
            var workspace   = Path.Combine(root, "Workspace");
            var databaseDir = Path.Combine(workspace, "Database");
            var databaseFile = Path.Combine(databaseDir, "cookbook.sqlite");
            Directory.CreateDirectory(databaseDir);

            await SeedSchemaAsync(databaseFile);

            var options = new NativeBrokerHostOptions(
                PreferredPort:       17654,
                PortRangeStart:      17654,
                PortRangeEnd:        17664,
                WorkspaceFolderPath: workspace);

            return new Stage3gWorkspaceFixture(
                root, workspace, databaseFile, options);
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

            using (var cmd = conn.CreateCommand())
            {
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
CREATE TABLE scheduled_tasks (
    scheduled_task_id        TEXT PRIMARY KEY,
    recipe_id                TEXT NOT NULL UNIQUE,
    windows_task_name        TEXT NOT NULL,
    windows_task_path        TEXT NOT NULL DEFAULT '\PAX Cookbook\',
    recipe_projection_hash   TEXT NOT NULL,
    pax_script_version       TEXT NOT NULL,
    registered_at            TEXT NOT NULL,
    registered_by_user       TEXT NOT NULL,
    last_imported_cook_id    TEXT,
    last_imported_at         TEXT,
    last_stale_check_at      TEXT,
    status                   TEXT NOT NULL DEFAULT 'active',
    created_at               TEXT NOT NULL,
    updated_at               TEXT NOT NULL
);";
                await cmd.ExecuteNonQueryAsync();
            }
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public async Task SeedRecipeAsync(
            string recipeId,
            string name,
            string? deletedAt = null)
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
                     created_at, updated_at, deleted_at)
VALUES ($id, $name, '1.0.0', 1, 'workspace',
        $file, 'hash', 'active', 0,
        '2026-05-27T08:00:00Z', '2026-05-27T08:00:00Z', $deleted);";
            cmd.Parameters.AddWithValue("$id",      recipeId);
            cmd.Parameters.AddWithValue("$name",    name);
            cmd.Parameters.AddWithValue("$file",    "Recipes/" + recipeId + ".pantry.json");
            cmd.Parameters.AddWithValue("$deleted", (object?)deletedAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public async Task SeedScheduledTaskAsync(
            string scheduledTaskId,
            string recipeId,
            string projectionHash,
            string registeredAt = "2026-05-27T08:00:00Z")
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
INSERT INTO scheduled_tasks (
    scheduled_task_id, recipe_id, windows_task_name, windows_task_path,
    recipe_projection_hash, pax_script_version,
    registered_at, registered_by_user, status, created_at, updated_at)
VALUES ($stid, $rid, $tname, '\PAX Cookbook\',
        $hash, '0.0.0-test',
        $reg, 'TEST\User', 'active', $reg, $reg);";
            cmd.Parameters.AddWithValue("$stid",  scheduledTaskId);
            cmd.Parameters.AddWithValue("$rid",   recipeId);
            cmd.Parameters.AddWithValue("$tname", "PAXCookbook_" + recipeId);
            cmd.Parameters.AddWithValue("$hash",  projectionHash);
            cmd.Parameters.AddWithValue("$reg",   registeredAt);
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public async Task SeedCookAsync(
            string cookId,
            string recipeId,
            string scheduleId,
            string trigger,
            string status,
            string? errorClass,
            int? exitCode,
            string? startedAt,
            string? finishedAt)
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
INSERT INTO cooks (cook_id, recipe_id, trigger, schedule_id, status,
                   exit_code, started_at, finished_at, error_class,
                   created_at, updated_at)
VALUES ($id, $rid, $trig, $sid, $status,
        $exit, $start, $fin, $err,
        $start, $start);";
            cmd.Parameters.AddWithValue("$id",     cookId);
            cmd.Parameters.AddWithValue("$rid",    recipeId);
            cmd.Parameters.AddWithValue("$trig",   trigger);
            cmd.Parameters.AddWithValue("$sid",    scheduleId);
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$exit",   (object?)exitCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$start",  (object?)startedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fin",    (object?)finishedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$err",    (object?)errorClass ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public string? ReadLastStaleCheckAt(string recipeId)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseFilePath,
                Mode       = SqliteOpenMode.ReadOnly,
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT last_stale_check_at FROM scheduled_tasks WHERE recipe_id = $rid";
            cmd.Parameters.AddWithValue("$rid", recipeId);
            var v = cmd.ExecuteScalar();
            return v is null || v is DBNull ? null : (string)v;
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch { }
            return ValueTask.CompletedTask;
        }
    }

    // Fake registrar used by the FakeScheduledTaskRegistrar fact above.
    // Lives next to the test class for visibility.
    private sealed class FakeScheduledTaskRegistrar : IScheduledTaskRegistrar
    {
        public List<ScheduledTaskRegistrarRequest> Calls { get; } = new();
        public ScheduledTaskRegistrarResult CannedResult { get; set; } =
            new ScheduledTaskRegistrarResult(
                ExitCode: 0, Stdout: "", Stderr: "",
                LogPath: null, DurationMs: 0);

        public Task<ScheduledTaskRegistrarResult> InvokeAsync(
            ScheduledTaskRegistrarRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return Task.FromResult(CannedResult);
        }
    }
}
