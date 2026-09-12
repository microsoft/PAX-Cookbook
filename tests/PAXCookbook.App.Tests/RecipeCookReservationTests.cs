using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 32 - the RECIPE cook concurrency gate lives at the SQLite persistence
// boundary, not in application code.
//
// Before this cycle the recipe path refused a concurrent cook with a DETACHED
// read (gate 9, Get-RunningCookIdForRecipe) whose result was acted on many steps
// later at the row insert (gate 18). That is a check-then-insert race: two
// simultaneous manual Bakes of the same recipe could both see "nothing running",
// both create a cook folder, and both spawn a PAX child against the same output
// tree.
//
// The gate is now a single CONDITIONAL INSERT:
//   INSERT INTO cooks (...) SELECT ...
//   WHERE NOT EXISTS (SELECT 1 FROM cooks WHERE recipe_id = $recipe_id AND status = 'running');
// The duplicate is declined by the same statement that would have created it, so
// there is no window a second caller can slip through, and "a prior terminal cook
// must not block" holds by construction.
//
// Deliberately NOT a unique index: recipe_id is existing POPULATED data and
// WorkspaceDatabase.EnsureInitialized runs long before ReconcileCooksAtStartup
// with no exception handling, so a CREATE UNIQUE INDEX over already-duplicated
// running rows would throw out of startup and brick the workspace.
//
// These tests spawn nothing. No PAX process, no Bake, no engine, no supervisor,
// no cook folder. They exercise the production reservation helper against a
// throwaway SQLite workspace under the OS temp directory.
public class RecipeCookReservationTests : IDisposable
{
    private readonly List<string> _workspaces = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string workspace in _workspaces)
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch
            {
                // A leftover temp workspace is harmless; never fail a test on cleanup.
            }
        }

        GC.SuppressFinalize(this);
    }

    // Real 26-character Crockford-base32 ULIDs, so the ids used here are the same
    // shape the production route validates at gate 1.
    internal const string RecipeA = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    internal const string RecipeB = "01BX5ZZKBKACTAV9WEVGEMMVRZ";

    private string NewWorkspace(params string[] recipeIds)
    {
        string root = Path.Combine(Path.GetTempPath(), "paxcb-c32-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _workspaces.Add(root);
        WorkspaceDatabase.EnsureInitialized(root);
        using (SqliteConnection conn = Open(root))
        {
            foreach (string recipeId in recipeIds)
            {
                SeedRecipeRow(conn, recipeId);
            }
        }
        return root;
    }

    internal static SqliteConnection Open(string workspace)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(workspace, "Database", "cookbook.sqlite"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        var conn = new SqliteConnection(csb.ConnectionString);
        conn.Open();
        return conn;
    }

    // The cooks.recipe_id foreign key points at recipes(recipe_id), so every
    // recipe used by a reservation gets a real index row.
    internal static void SeedRecipeRow(SqliteConnection conn, string recipeId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT OR IGNORE INTO recipes (recipe_id, name, pax_adapter_version, " +
            "recipe_schema_version, source, file_path, file_hash, status, created_at, updated_at) " +
            "VALUES ($id, 'seeded', '1.11.11', 1, 'test', $path, 'hash', 'ready', " +
            "'2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');";
        cmd.Parameters.AddWithValue("$id", recipeId);
        cmd.Parameters.AddWithValue("$path", "Recipes/" + recipeId + ".recipe.json");
        cmd.ExecuteNonQuery();
    }

    internal static long CountRunning(SqliteConnection conn, string recipeId) =>
        Count(conn, "SELECT COUNT(*) FROM cooks WHERE recipe_id = $id AND status = 'running';", recipeId);

    internal static long CountAny(SqliteConnection conn, string recipeId) =>
        Count(conn, "SELECT COUNT(*) FROM cooks WHERE recipe_id = $id;", recipeId);

    private static long Count(SqliteConnection conn, string sql, string recipeId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", recipeId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static int RecipeCookFolderCount(string workspace, string recipeId)
    {
        string bucket = Path.Combine(workspace, "Cooks", recipeId);
        return Directory.Exists(bucket) ? Directory.GetDirectories(bucket).Length : 0;
    }

    // The SAME insert the production reservation issues, MINUS the conditional
    // WHERE NOT EXISTS predicate. Used only as the positive control below.
    private static int UnguardedInsert(SqliteConnection conn, string cookId, string recipeId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO cooks (cook_id, recipe_id, recipe_snapshot_json, command_argv_json, " +
            "command_argv_redacted, pax_script_path, pax_script_version, trigger, cook_folder, " +
            "status, started_at, created_at, updated_at) " +
            "SELECT $cook_id, $recipe_id, '{}', '[]', '[]', 'test-seam', 'test-seam', 'manual', " +
            "$folder, 'running', NULL, '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z';";
        cmd.Parameters.AddWithValue("$cook_id", cookId);
        cmd.Parameters.AddWithValue("$recipe_id", recipeId);
        cmd.Parameters.AddWithValue("$folder", "Cooks/" + recipeId + "/" + cookId);
        return cmd.ExecuteNonQuery();
    }

    // ---- sequential contention ----------------------------------------------

    [Fact]
    public void A_second_reservation_for_the_same_recipe_is_refused_and_names_the_running_cook()
    {
        string workspace = NewWorkspace(RecipeA);

        (bool firstReserved, string? firstConflict) =
            RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-first", RecipeA);
        Assert.True(firstReserved);
        Assert.Null(firstConflict);

        (bool secondReserved, string? secondConflict) =
            RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-second", RecipeA);

        Assert.False(secondReserved);
        Assert.Equal("cook-first", secondConflict);

        using SqliteConnection conn = Open(workspace);
        Assert.Equal(1, CountRunning(conn, RecipeA));
        // The refused reservation wrote NO extra row of ANY status ...
        Assert.Equal(1, CountAny(conn, RecipeA));
        // ... and created no cook folder, because the reservation is now the
        // FIRST side effect and the loser never gets past it.
        Assert.Equal(0, RecipeCookFolderCount(workspace, RecipeA));
    }

    [Fact]
    public void The_conditional_predicate_is_what_refuses_the_duplicate()
    {
        // POSITIVE CONTROL for the whole reservation. It proves the refusal above
        // is produced by the WHERE NOT EXISTS predicate and by nothing else: the
        // byte-identical insert WITHOUT that predicate happily creates a second
        // running row for the same recipe. If the predicate were deleted from
        // ReserveRecipeCookRowCore, the production seam would behave exactly like
        // UnguardedInsert here and every reservation assertion in this file would
        // fail.
        string workspace = NewWorkspace(RecipeA);

        Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-first", RecipeA).Reserved);

        using (SqliteConnection unguarded = Open(workspace))
        {
            Assert.Equal(1, UnguardedInsert(unguarded, "cook-unguarded", RecipeA));
            // Without the predicate SQLite creates the duplicate the product must
            // never have: TWO running cooks for one recipe.
            Assert.Equal(2, CountRunning(unguarded, RecipeA));

            using SqliteCommand del = unguarded.CreateCommand();
            del.CommandText = "DELETE FROM cooks WHERE cook_id = 'cook-unguarded';";
            Assert.Equal(1, del.ExecuteNonQuery());
        }

        // The guarded production statement, on the same database and the same
        // recipe, declines instead.
        (bool reserved, string? conflict) =
            RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-guarded", RecipeA);
        Assert.False(reserved);
        Assert.Equal("cook-first", conflict);

        using SqliteConnection after = Open(workspace);
        Assert.Equal(1, CountRunning(after, RecipeA));
        Assert.Equal(1, CountAny(after, RecipeA));
    }

    [Fact]
    public void A_different_recipe_reserves_successfully()
    {
        string workspace = NewWorkspace(RecipeA, RecipeB);

        Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-a", RecipeA).Reserved);
        Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-b", RecipeB).Reserved);

        using SqliteConnection conn = Open(workspace);
        Assert.Equal(1, CountRunning(conn, RecipeA));
        Assert.Equal(1, CountRunning(conn, RecipeB));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("interrupted")]
    [InlineData("cancelled")]
    public void A_prior_terminal_cook_of_the_same_recipe_does_not_block(string terminalStatus)
    {
        string workspace = NewWorkspace(RecipeA);

        Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-old", RecipeA).Reserved);

        // Positive control: while the prior cook is still RUNNING the recipe is
        // genuinely held, so the success after the terminal transition below is
        // not a vacuous pass.
        Assert.Equal(
            (false, "cook-old"),
            RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-blocked", RecipeA));

        using (SqliteConnection conn = Open(workspace))
        using (SqliteCommand upd = conn.CreateCommand())
        {
            upd.CommandText = "UPDATE cooks SET status = $status WHERE cook_id = 'cook-old';";
            upd.Parameters.AddWithValue("$status", terminalStatus);
            Assert.Equal(1, upd.ExecuteNonQuery());
        }

        Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-new", RecipeA).Reserved);

        using SqliteConnection after = Open(workspace);
        Assert.Equal(1, CountRunning(after, RecipeA));
        Assert.Equal(2, CountAny(after, RecipeA));
    }

    [Fact]
    public void A_running_resume_cook_with_a_null_recipe_id_never_blocks_a_recipe_cook()
    {
        // recipe_id IS NULL never satisfies `recipe_id = $recipe_id`, so the
        // resume bucket and the recipe bucket stay independent.
        string workspace = NewWorkspace(RecipeA);
        Assert.Null(RecipeReadModel.TestSeamReserveResumeCookRow(workspace, "cook-resume", "C:\\PAX\\OUT"));

        Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-recipe", RecipeA).Reserved);

        using SqliteConnection conn = Open(workspace);
        Assert.Equal(1, CountRunning(conn, RecipeA));
    }

    // ---- simultaneous contention --------------------------------------------

    [Fact]
    public void Simultaneous_reservations_for_the_same_recipe_yield_exactly_one_winner()
    {
        string workspace = NewWorkspace(RecipeA);
        const int racers = 8;

        (bool[] reserved, string?[] conflicts, Exception?[] faults) =
            RaceReservations(workspace, racers, _ => RecipeA);

        Assert.All(faults, fault => Assert.Null(fault));

        int winners = 0;
        string? winnerCookId = null;
        for (int i = 0; i < racers; i++)
        {
            if (reserved[i])
            {
                winners++;
                winnerCookId = CookId(i);
            }
        }

        Assert.Equal(1, winners);
        Assert.NotNull(winnerCookId);

        // Every loser was told about the SAME winner, so the 409 body names the
        // real running cook rather than a guess.
        for (int i = 0; i < racers; i++)
        {
            if (!reserved[i])
            {
                Assert.Equal(winnerCookId, conflicts[i]);
            }
        }

        using SqliteConnection conn = Open(workspace);
        Assert.Equal(1, CountRunning(conn, RecipeA));
        Assert.Equal(1, CountAny(conn, RecipeA));
        Assert.Equal(0, RecipeCookFolderCount(workspace, RecipeA));
    }

    [Fact]
    public void Simultaneous_reservations_for_different_recipes_all_succeed()
    {
        // Positive control for the race test above: the same barrier, the same
        // thread count, and the same seam produce EIGHT winners when the recipes
        // differ, so "exactly one winner" is a property of the recipe collision
        // and not of the harness serializing everything.
        string workspace = NewWorkspace();
        const int racers = 8;

        var recipeIds = new string[racers];
        using (SqliteConnection seed = Open(workspace))
        {
            for (int i = 0; i < racers; i++)
            {
                recipeIds[i] = DistinctRecipeId(i);
                SeedRecipeRow(seed, recipeIds[i]);
            }
        }

        (bool[] reserved, _, Exception?[] faults) = RaceReservations(workspace, racers, i => recipeIds[i]);

        Assert.All(faults, fault => Assert.Null(fault));
        Assert.All(reserved, value => Assert.True(value));

        using SqliteConnection conn = Open(workspace);
        for (int i = 0; i < racers; i++)
        {
            Assert.Equal(1, CountRunning(conn, recipeIds[i]));
        }
    }

    // ---- REAL write-lock contention -----------------------------------------

    [Fact]
    public void A_loser_blocked_by_a_real_write_lock_resolves_within_the_default_command_timeout()
    {
        // Nothing in this repository configures a SQLite busy timeout, so the
        // reservation depends implicitly on Microsoft.Data.Sqlite's DEFAULT
        // command timeout (30 s) to retry SQLITE_BUSY rather than surfacing it.
        // This test makes that dependency explicit: the winner holds a real
        // IMMEDIATE write transaction, so the loser's INSERT genuinely cannot
        // acquire the write lock and must wait for the commit.
        string workspace = NewWorkspace(RecipeA);
        const int lockHoldMs = 800;

        using var started = new ManualResetEventSlim(false);
        var loserFinished = new ManualResetEventSlim(false);
        bool reserved = true;
        string? conflict = null;
        Exception? fault = null;
        long elapsedMs = -1;

        using SqliteConnection holder = Open(workspace);
        using (SqliteCommand begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }
        Assert.Equal(1, UnguardedInsert(holder, "cook-winner", RecipeA));

        Task loser = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            started.Set();
            try
            {
                (reserved, conflict) =
                    RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-loser", RecipeA);
            }
            catch (Exception ex)
            {
                fault = ex;
            }
            sw.Stop();
            elapsedMs = sw.ElapsedMilliseconds;
            loserFinished.Set();
        });

        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Thread.Sleep(lockHoldMs);

        // The loser is genuinely BLOCKED on the write lock right now. If the
        // contention were not real this would already have completed.
        Assert.False(loserFinished.IsSet);

        using (SqliteCommand commit = holder.CreateCommand())
        {
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
        }

        Assert.True(Task.WaitAll(new[] { loser }, TimeSpan.FromSeconds(60)));

        // The loser resolved as a bounded refusal, NOT as a surfaced SQLITE_BUSY
        // and not as a generic failure.
        Assert.Null(fault);
        Assert.False(reserved);
        Assert.Equal("cook-winner", conflict);

        // It really did wait on the lock ...
        Assert.True(elapsedMs >= lockHoldMs - 100, "loser elapsed ms = " + elapsedMs);
        // ... and it resolved well inside the default 30 s command timeout.
        Assert.True(elapsedMs < 30_000, "loser elapsed ms = " + elapsedMs);

        using SqliteConnection after = Open(workspace);
        Assert.Equal(1, CountRunning(after, RecipeA));
        Assert.Equal(1, CountAny(after, RecipeA));
        Assert.Equal(0, RecipeCookFolderCount(workspace, RecipeA));
    }

    // ---- compensation --------------------------------------------------------

    [Fact]
    public void Releasing_a_reservation_removes_the_row_and_frees_the_recipe()
    {
        string workspace = NewWorkspace(RecipeA);

        Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-a", RecipeA).Reserved);

        // Positive control: the recipe really IS held before the release.
        Assert.Equal(
            (false, "cook-a"),
            RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-b", RecipeA));

        RecipeReadModel.TestSeamReleaseRecipeCookReservation(workspace, "cook-a");

        using (SqliteConnection conn = Open(workspace))
        {
            // No phantom row of ANY status survives a reservation that never
            // started, which preserves the pre-cycle-32 behaviour that a failed
            // cook folder / init-files step leaves no cook history at all.
            Assert.Equal(0, CountAny(conn, RecipeA));
        }

        Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-b", RecipeA).Reserved);

        using SqliteConnection after = Open(workspace);
        Assert.Equal(1, CountRunning(after, RecipeA));
    }

    [Fact]
    public void Releasing_an_unknown_cook_id_is_a_harmless_no_op()
    {
        string workspace = NewWorkspace(RecipeA);

        Assert.True(RecipeReadModel.TestSeamReserveRecipeCookRow(workspace, "cook-a", RecipeA).Reserved);

        RecipeReadModel.TestSeamReleaseRecipeCookReservation(workspace, "cook-never-existed");

        using SqliteConnection conn = Open(workspace);
        Assert.Equal(1, CountRunning(conn, RecipeA));
    }

    // ---- harness -------------------------------------------------------------

    private static string CookId(int index) => "cook-" + index.ToString("D2");

    // Eight distinct, well-formed 26-character Crockford-base32 ids.
    private static string DistinctRecipeId(int index) =>
        "01ARZ3NDEKTSV4RRFFQ69G5F" + index.ToString("D2");

    private static (bool[] Reserved, string?[] Conflicts, Exception?[] Faults) RaceReservations(
        string workspace, int racers, Func<int, string> recipeFor)
    {
        using var barrier = new Barrier(racers);
        var reserved = new bool[racers];
        var conflicts = new string?[racers];
        var faults = new Exception?[racers];
        var tasks = new Task[racers];

        for (int i = 0; i < racers; i++)
        {
            int index = i;
            tasks[index] = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        (reserved[index], conflicts[index]) =
                            RecipeReadModel.TestSeamReserveRecipeCookRow(
                                workspace, CookId(index), recipeFor(index));
                    }
                    catch (Exception ex)
                    {
                        faults[index] = ex;
                    }
                },
                TaskCreationOptions.LongRunning);
        }

        Task.WaitAll(tasks, TimeSpan.FromSeconds(120));
        return (reserved, conflicts, faults);
    }
}
