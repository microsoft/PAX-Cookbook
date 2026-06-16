using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3c -- read-only SQLite gateway for the native broker. All
// queries are parameterised (no string concatenation of request
// values). Connection lifecycle is per-call: the connection is opened
// in ReadOnly mode, the query is executed, the connection is disposed.
// Mode=ReadOnly cannot create or migrate the database -- if
// cookbook.sqlite is missing, opening fails and the readers below
// surface that as the appropriate "not configured" sentinel for the
// route layer.
//
// Doctrine:
//   - Mode=ReadOnly on every open.
//   - Cache=Private. The PowerShell broker uses a shared cache across
//     the broker process; the native broker matches read-only
//     semantics but keeps each handle private to avoid contending
//     with the PS broker should it also be running during the
//     parallel-implementation window before Stage 3j.
//   - NEVER execute PRAGMA, CREATE, INSERT, UPDATE, DELETE, ATTACH,
//     DETACH, VACUUM. All callers go through ExecuteReader against a
//     parameterised SELECT.
//   - Missing DB file -> SqliteException at Open(); callers catch it
//     and return the appropriate sentinel (TryListRecipes returns
//     null, GetRecipeById returns null, etc.).
public sealed class SqliteWorkspaceReader
{
    private readonly WorkspacePaths _paths;

    public SqliteWorkspaceReader(WorkspacePaths paths)
    {
        _paths = paths;
    }

    public WorkspacePaths Paths => _paths;

    public bool DatabaseFileExists() => File.Exists(_paths.DatabaseFile);

    // ---------------- Recipes ----------------

    public IReadOnlyList<RecipeListRow>? TryListRecipes()
    {
        if (!DatabaseFileExists()) return null;
        try
        {
            using var conn = OpenReadOnly();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT recipe_id, name, status, last_cooked_at, last_cook_id, created_at, updated_at
                FROM recipes
                WHERE deleted_at IS NULL
                ORDER BY created_at DESC";

            var list = new List<RecipeListRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new RecipeListRow(
                    RecipeId:     r.GetString(0),
                    Name:         r.GetString(1),
                    Status:       r.GetString(2),
                    LastCookedAt: GetNullableString(r, 3),
                    LastCookId:   GetNullableString(r, 4),
                    CreatedAt:    r.GetString(5),
                    UpdatedAt:    r.GetString(6)));
            }
            return list;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public RecipeMetaRow? GetRecipeById(string recipeId)
    {
        if (!DatabaseFileExists()) return null;
        try
        {
            using var conn = OpenReadOnly();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT recipe_id, name, file_path, file_hash, status, is_pinned,
                       pax_adapter_version, recipe_schema_version, source, source_ref,
                       last_validated_at, last_validation_status, last_cooked_at,
                       last_cook_id, created_at, updated_at, deleted_at
                FROM recipes
                WHERE recipe_id = $id";
            cmd.Parameters.AddWithValue("$id", recipeId);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            return new RecipeMetaRow(
                RecipeId:             r.GetString(0),
                Name:                 r.GetString(1),
                FilePath:             r.GetString(2),
                FileHash:             r.GetString(3),
                Status:               r.GetString(4),
                IsPinned:             r.GetInt32(5),
                PaxAdapterVersion:    r.GetString(6),
                RecipeSchemaVersion:  r.GetInt32(7),
                Source:               r.GetString(8),
                SourceRef:            GetNullableString(r, 9),
                LastValidatedAt:      GetNullableString(r, 10),
                LastValidationStatus: GetNullableString(r, 11),
                LastCookedAt:         GetNullableString(r, 12),
                LastCookId:           GetNullableString(r, 13),
                CreatedAt:            r.GetString(14),
                UpdatedAt:            r.GetString(15),
                DeletedAt:            GetNullableString(r, 16));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    // ---------------- Cooks ----------------

    public IReadOnlyList<CookRow>? TryListCooks()
    {
        if (!DatabaseFileExists()) return null;
        try
        {
            using var conn = OpenReadOnly();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT cook_id, recipe_id, status, exit_code, pid, cook_folder,
                       pax_script_path, pax_script_version, trigger, started_at,
                       finished_at, duration_seconds, error_class, error_message,
                       created_at, updated_at, summary_path, parent_cook_id
                FROM cooks
                ORDER BY created_at DESC";

            var list = new List<CookRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(ReadCookRow(r));
            }
            return list;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public CookRow? GetCookById(string cookId)
    {
        if (!DatabaseFileExists()) return null;
        try
        {
            using var conn = OpenReadOnly();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT cook_id, recipe_id, status, exit_code, pid, cook_folder,
                       pax_script_path, pax_script_version, trigger, started_at,
                       finished_at, duration_seconds, error_class, error_message,
                       created_at, updated_at, summary_path, parent_cook_id
                FROM cooks
                WHERE cook_id = $id";
            cmd.Parameters.AddWithValue("$id", cookId);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return ReadCookRow(r);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static CookRow ReadCookRow(SqliteDataReader r) => new(
        CookId:           r.GetString(0),
        RecipeId:         GetNullableString(r, 1),
        Status:           r.GetString(2),
        ExitCode:         GetNullableInt32(r, 3),
        Pid:              GetNullableInt32(r, 4),
        CookFolder:       r.GetString(5),
        PaxScriptPath:    r.GetString(6),
        PaxScriptVersion: r.GetString(7),
        Trigger:          r.GetString(8),
        StartedAt:        GetNullableString(r, 9),
        FinishedAt:       GetNullableString(r, 10),
        DurationSeconds:  GetNullableDouble(r, 11),
        ErrorClass:       GetNullableString(r, 12),
        ErrorMessage:     GetNullableString(r, 13),
        CreatedAt:        r.GetString(14),
        UpdatedAt:        r.GetString(15),
        SummaryPath:      GetNullableString(r, 16),
        ParentCookId:     GetNullableString(r, 17));

    // ---------------- Auth profiles ----------------

    public IReadOnlyList<AuthProfileRow>? TryListAuthProfiles()
    {
        if (!DatabaseFileExists()) return null;
        try
        {
            using var conn = OpenReadOnly();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT auth_profile_id, name, mode, tenant_id, client_id,
                       cred_man_target, cert_thumbprint, cert_store, description,
                       last_verified_at, last_verified_result, created_at, updated_at
                FROM auth_profiles
                ORDER BY name COLLATE NOCASE ASC";

            var list = new List<AuthProfileRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(ReadAuthProfileRow(r));
            }
            return list;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public AuthProfileRow? GetAuthProfileById(string authProfileId)
    {
        if (!DatabaseFileExists()) return null;
        try
        {
            using var conn = OpenReadOnly();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT auth_profile_id, name, mode, tenant_id, client_id,
                       cred_man_target, cert_thumbprint, cert_store, description,
                       last_verified_at, last_verified_result, created_at, updated_at
                FROM auth_profiles
                WHERE auth_profile_id = $id";
            cmd.Parameters.AddWithValue("$id", authProfileId);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return ReadAuthProfileRow(r);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static AuthProfileRow ReadAuthProfileRow(SqliteDataReader r) => new(
        AuthProfileId:      r.GetString(0),
        Name:               r.GetString(1),
        Mode:               r.GetString(2),
        TenantId:           r.GetString(3),
        ClientId:           r.GetString(4),
        CredManTarget:      GetNullableString(r, 5),
        CertThumbprint:     GetNullableString(r, 6),
        CertStore:          GetNullableString(r, 7),
        Description:        GetNullableString(r, 8),
        LastVerifiedAt:     GetNullableString(r, 9),
        LastVerifiedResult: GetNullableString(r, 10),
        CreatedAt:          r.GetString(11),
        UpdatedAt:          r.GetString(12));

    // ---------------- Internals ----------------

    private SqliteConnection OpenReadOnly()
    {
        // Mode=ReadOnly: forbids schema mutation, INSERT/UPDATE/DELETE,
        // and (critically for the parallel-implementation window)
        // cannot create the database file if it is missing. The PS
        // broker opens its handle with ReadWriteCreate at startup, so
        // by the time a native-broker request runs against a real
        // workspace the file exists; tests that run without the PS
        // broker present provide their own fixture DB.
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabaseFile,
            Mode       = SqliteOpenMode.ReadOnly,
            Cache      = SqliteCacheMode.Private,
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    private static string? GetNullableString(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    private static int? GetNullableInt32(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetInt32(ordinal);

    private static double? GetNullableDouble(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetDouble(ordinal);
}
