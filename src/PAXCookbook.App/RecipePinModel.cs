using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Native v1 product feature (X11) — recipe pin / unpin. This is NOT an
// oracle-parity port: X9 proved the live PowerShell oracle has no pin/unpin
// route. It is a deliberate, sanctioned native divergence built on the existing
// dormant recipes.is_pinned column, approved as a v1 product feature.
//
//   POST /api/v1/recipes/{id}/pin    -> set is_pinned = 1
//   POST /api/v1/recipes/{id}/unpin  -> set is_pinned = 0
//
// It is a pure DB row-only mutation. It never rewrites the recipe JSON file,
// never changes the file hash, never moves a file, never invokes PAX, never
// reads or mutates the PAX bytes, and never touches cook / scheduler /
// notification / update / auth-profile state.
//
// Gating mirrors the recipe CRUD / materialize routes: bearer token, CSRF
// header, and broker lock are all enforced upstream by the middleware. There is
// NO per-op re-auth — pinning is metadata-only and lower-stakes than the
// re-auth-gated cook / scheduled-task / auth-profile operations, so it follows
// the same no-re-auth gating as create / update / delete / materialize.
//
// updated_at decision: a real pin-state transition bumps updated_at (the
// recipe-list / detail metadata changed, so the modification timestamp should
// reflect it). An idempotent no-op (pin an already-pinned recipe, or unpin an
// already-unpinned recipe) performs NO write at all — is_pinned and updated_at
// are both left untouched — and returns the existing state. This guarantees "no
// duplicate side effects" for repeated calls.
internal static class RecipePinModel
{
    private static string DatabaseFile(string workspacePath) =>
        Path.Combine(workspacePath, "Database", "cookbook.sqlite");

    // Returns (httpStatus, body). The recipe-id format is validated by the
    // caller before this is invoked. `pinned` is the desired terminal state
    // (true = pin, false = unpin).
    public static (int Status, object Body) Handle(string workspacePath, string recipeId, bool pinned)
    {
        RowSnapshot? snap = GetRow(workspacePath, recipeId);

        // Not found OR soft-deleted -> 404 not_found (a soft-deleted recipe is
        // invisible to the product surface, exactly like the CRUD routes).
        if (snap is null || snap.Value.DeletedAt is not null)
        {
            return (404, new { error = "not_found" });
        }

        int target = pinned ? 1 : 0;

        // Idempotent no-op: already in the desired state. No write, no
        // updated_at bump, no duplicate side effects. Return the existing state.
        if (snap.Value.IsPinned == target)
        {
            return (200, new
            {
                recipeId,
                isPinned = pinned,
                updatedAt = snap.Value.UpdatedAt,
            });
        }

        // Real state transition: row-only UPDATE of is_pinned + updated_at.
        string now = UtcNowIso();
        int affected = SetPinned(workspacePath, recipeId, target, now);
        if (affected != 1)
        {
            // The row was live a moment ago; a 0-row update means it was deleted
            // concurrently. Report not_found rather than fabricating success.
            return (404, new { error = "not_found" });
        }

        return (200, new
        {
            recipeId,
            isPinned = pinned,
            updatedAt = now,
        });
    }

    private readonly record struct RowSnapshot(int IsPinned, string UpdatedAt, string? DeletedAt);

    // SELECT the pin state, modification timestamp, and soft-delete marker.
    // Opened read-write so the subsequent UPDATE runs against the same database.
    private static RowSnapshot? GetRow(string workspacePath, string recipeId)
    {
        string dbFile = DatabaseFile(workspacePath);
        if (!File.Exists(dbFile))
        {
            return null;
        }

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbFile,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };

        using var conn = new SqliteConnection(csb.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT is_pinned, updated_at, deleted_at FROM recipes WHERE recipe_id = $id;";
        cmd.Parameters.AddWithValue("$id", recipeId);

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        int isPinned = (int)reader.GetInt64(0);
        string updatedAt = reader.GetString(1);
        string? deletedAt = reader.IsDBNull(2) ? null : reader.GetString(2);
        return new RowSnapshot(isPinned, updatedAt, deletedAt);
    }

    // Row-only UPDATE. is_pinned + updated_at WHERE recipe_id AND deleted_at IS
    // NULL. No other column is touched; the recipe file and its hash are not
    // involved. Returns the affected-row count.
    private static int SetPinned(string workspacePath, string recipeId, int isPinned, string updatedAt)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile(workspacePath),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };

        using var conn = new SqliteConnection(csb.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE recipes SET is_pinned = $is_pinned, updated_at = $updated_at " +
            "WHERE recipe_id = $recipe_id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$is_pinned", isPinned);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);
        cmd.Parameters.AddWithValue("$recipe_id", recipeId);

        return cmd.ExecuteNonQuery();
    }

    // Oracle: Get-UtcNowIso -> (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ').
    private static string UtcNowIso() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
