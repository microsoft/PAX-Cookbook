using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 31 - the resume concurrency gate lives at the SQLite persistence
// boundary, not in application code.
//
// A resume cook has no recipe id, so the recipe path's
// "one running cook per recipe" check (RecipeReadModel.CookStart.cs) never
// covered it: two resumes of the SAME checkpoint could both reach
// SpawnAndSupervise and have two PAX children write the same output tree.
//
// The gate is a PARTIAL UNIQUE INDEX over cooks(resume_checkpoint_identity)
// restricted to status='running'. That placement is what makes it ATOMIC: the
// duplicate is rejected by the same statement that would have created it, so
// there is no check-then-insert window a second caller can slip through. It
// also makes "a prior terminal resume must not block" hold by construction --
// a cook that has left 'running' is no longer in the index.
//
// These tests spawn nothing. No PAX process, no Bake, no engine, no supervisor.
// They exercise the schema against a throwaway SQLite workspace under the OS
// temp directory.
public class ResumeCookReservationTests : IDisposable
{
    internal const int SqliteConstraintUnique = 2067;
    internal const string RunningIdentityIndex = "ux_cooks_resume_running_identity";
    internal const string IdentityColumn = "resume_checkpoint_identity";

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

    private string NewWorkspace() => TrackWorkspace(_workspaces);

    internal static string TrackWorkspace(List<string> sink)
    {
        string root = Path.Combine(
            Path.GetTempPath(), "paxcb-c31-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        sink.Add(root);
        WorkspaceDatabase.EnsureInitialized(root);
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

    private static void RawInsertResumeRow(
        SqliteConnection conn, string cookId, string identity, string status)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO cooks (cook_id, recipe_id, recipe_snapshot_json, command_argv_json, " +
            "command_argv_redacted, pax_script_path, pax_script_version, trigger, cook_folder, " +
            "status, started_at, created_at, updated_at, " + IdentityColumn + ") VALUES (" +
            "$cook_id, NULL, '{}', '[]', '[]', 'p', 'v', 'resume', $folder, $status, NULL, " +
            "'2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', $identity);";
        cmd.Parameters.AddWithValue("$cook_id", cookId);
        cmd.Parameters.AddWithValue("$folder", "Cooks/__resume__/" + cookId);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$identity", identity);
        cmd.ExecuteNonQuery();
    }

    internal static long CountRunning(SqliteConnection conn, string identity) =>
        Count(
            conn,
            "SELECT COUNT(*) FROM cooks WHERE " + IdentityColumn + " = $identity AND status = 'running';",
            identity);

    internal static long CountAny(SqliteConnection conn, string identity) =>
        Count(
            conn,
            "SELECT COUNT(*) FROM cooks WHERE " + IdentityColumn + " = $identity;",
            identity);

    private static long Count(SqliteConnection conn, string sql, string identity)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$identity", identity);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ---- schema: the gate exists at the persistence boundary ----------------

    [Fact]
    public void Schema_carries_the_resume_identity_column_and_the_partial_running_unique_index()
    {
        string workspace = NewWorkspace();
        using SqliteConnection conn = Open(workspace);

        var columns = new List<string>();
        using (SqliteCommand info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(cooks);";
            using SqliteDataReader reader = info.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        Assert.Contains(IdentityColumn, columns);
        // Positive control: the reader really can distinguish present from
        // absent, so the assertion above is capable of failing.
        Assert.Contains("cook_id", columns);
        Assert.DoesNotContain("column_that_must_never_exist", columns);

        string? indexSql;
        using (SqliteCommand idx = conn.CreateCommand())
        {
            idx.CommandText =
                "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name;";
            idx.Parameters.AddWithValue("$name", RunningIdentityIndex);
            indexSql = idx.ExecuteScalar() as string;
        }

        Assert.NotNull(indexSql);
        Assert.Contains("UNIQUE", indexSql!, StringComparison.OrdinalIgnoreCase);
        // The WHERE clause is what makes the gate partial: it constrains only
        // rows that are still running.
        Assert.Contains("WHERE", indexSql!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'running'", indexSql!, StringComparison.Ordinal);
    }

    // ---- atomicity: SQLite itself rejects the duplicate ---------------------

    [Fact]
    public void Duplicate_running_identity_is_rejected_by_sqlite_itself_not_by_a_prior_read()
    {
        string workspace = NewWorkspace();
        using SqliteConnection conn = Open(workspace);

        RawInsertResumeRow(conn, "cook-a", "C:\\PAX\\OUT", "running");

        // No SELECT precedes this INSERT. The rejection therefore cannot come
        // from an application-level check-then-insert: it can only come from the
        // index enforced by the INSERT statement itself.
        SqliteException ex = Assert.Throws<SqliteException>(
            () => RawInsertResumeRow(conn, "cook-b", "C:\\PAX\\OUT", "running"));
        Assert.Equal(SqliteConstraintUnique, ex.SqliteExtendedErrorCode);

        Assert.Equal(1, CountRunning(conn, "C:\\PAX\\OUT"));
        Assert.Equal(1, CountAny(conn, "C:\\PAX\\OUT"));
    }

    [Fact]
    public void Different_identities_are_not_blocked()
    {
        string workspace = NewWorkspace();
        using SqliteConnection conn = Open(workspace);

        RawInsertResumeRow(conn, "cook-a", "C:\\PAX\\OUT-A", "running");
        RawInsertResumeRow(conn, "cook-b", "C:\\PAX\\OUT-B", "running");

        Assert.Equal(1, CountRunning(conn, "C:\\PAX\\OUT-A"));
        Assert.Equal(1, CountRunning(conn, "C:\\PAX\\OUT-B"));
    }

    [Fact]
    public void A_null_identity_is_never_constrained_so_recipe_cooks_are_untouched()
    {
        string workspace = NewWorkspace();
        using SqliteConnection conn = Open(workspace);

        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO cooks (cook_id, recipe_id, recipe_snapshot_json, command_argv_json, " +
                "command_argv_redacted, pax_script_path, pax_script_version, trigger, cook_folder, " +
                "status, started_at, created_at, updated_at) VALUES " +
                "('r1', NULL, '{}', '[]', '[]', 'p', 'v', 'manual', 'Cooks/x/r1', 'running', NULL, " +
                "'2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z'), " +
                "('r2', NULL, '{}', '[]', '[]', 'p', 'v', 'manual', 'Cooks/x/r2', 'running', NULL, " +
                "'2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z');";
            cmd.ExecuteNonQuery();
        }

        using SqliteCommand count = conn.CreateCommand();
        count.CommandText =
            "SELECT COUNT(*) FROM cooks WHERE " + IdentityColumn + " IS NULL AND status = 'running';";
        Assert.Equal(2, Convert.ToInt64(count.ExecuteScalar()));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("interrupted")]
    [InlineData("cancelled")]
    public void A_prior_terminal_resume_of_the_same_checkpoint_does_not_block(string terminalStatus)
    {
        string workspace = NewWorkspace();
        using SqliteConnection conn = Open(workspace);

        RawInsertResumeRow(conn, "cook-old", "C:\\PAX\\OUT", terminalStatus);
        RawInsertResumeRow(conn, "cook-new", "C:\\PAX\\OUT", "running");

        Assert.Equal(2, CountAny(conn, "C:\\PAX\\OUT"));
        Assert.Equal(1, CountRunning(conn, "C:\\PAX\\OUT"));

        // Positive control for the theory: with a RUNNING row already present the
        // very same insert is refused, so "terminal does not block" is a real
        // property of the status predicate and not a vacuous pass.
        SqliteException ex = Assert.Throws<SqliteException>(
            () => RawInsertResumeRow(conn, "cook-third", "C:\\PAX\\OUT", "running"));
        Assert.Equal(SqliteConstraintUnique, ex.SqliteExtendedErrorCode);
    }

    [Fact]
    public void Leaving_running_releases_the_identity_for_a_later_resume()
    {
        string workspace = NewWorkspace();
        using SqliteConnection conn = Open(workspace);

        RawInsertResumeRow(conn, "cook-a", "C:\\PAX\\OUT", "running");
        Assert.Throws<SqliteException>(
            () => RawInsertResumeRow(conn, "cook-b", "C:\\PAX\\OUT", "running"));

        using (SqliteCommand upd = conn.CreateCommand())
        {
            upd.CommandText = "UPDATE cooks SET status = 'completed' WHERE cook_id = 'cook-a';";
            upd.ExecuteNonQuery();
        }

        RawInsertResumeRow(conn, "cook-b", "C:\\PAX\\OUT", "running");
        Assert.Equal(1, CountRunning(conn, "C:\\PAX\\OUT"));
        Assert.Equal(2, CountAny(conn, "C:\\PAX\\OUT"));
    }

    [Fact]
    public void Migration_repairs_a_database_created_before_the_identity_column_existed()
    {
        // A workspace is bootstrapped, then the index and column are dropped to
        // simulate a database created before this cycle, then EnsureInitialized
        // runs again. The additive migration must restore both.
        string workspace = NewWorkspace();

        using (SqliteConnection conn = Open(workspace))
        {
            using SqliteCommand drop = conn.CreateCommand();
            drop.CommandText =
                "DROP INDEX IF EXISTS " + RunningIdentityIndex + ";" +
                "ALTER TABLE cooks DROP COLUMN " + IdentityColumn + ";";
            drop.ExecuteNonQuery();
        }

        using (SqliteConnection stale = Open(workspace))
        {
            using SqliteCommand check = stale.CreateCommand();
            check.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('cooks') WHERE name = $identity;";
            check.Parameters.AddWithValue("$identity", IdentityColumn);
            // Positive control: the simulated-old database really is missing the
            // column, so the repair assertion below cannot pass vacuously.
            Assert.Equal(0, Convert.ToInt64(check.ExecuteScalar()));
        }

        WorkspaceDatabase.EnsureInitialized(workspace);

        using SqliteConnection repaired = Open(workspace);
        using (SqliteCommand after = repaired.CreateCommand())
        {
            after.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('cooks') WHERE name = $identity;";
            after.Parameters.AddWithValue("$identity", IdentityColumn);
            Assert.Equal(1, Convert.ToInt64(after.ExecuteScalar()));
        }

        using SqliteCommand idx = repaired.CreateCommand();
        idx.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";
        idx.Parameters.AddWithValue("$name", RunningIdentityIndex);
        Assert.Equal(1, Convert.ToInt64(idx.ExecuteScalar()));
    }
}
