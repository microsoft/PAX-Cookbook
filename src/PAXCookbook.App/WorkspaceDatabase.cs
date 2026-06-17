using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Native port of the oracle's workspace-database bootstrap (the C4/M1 schema
// block in app\broker\Start-Broker.ps1). The PowerShell broker created
// <workspace>\Database\cookbook.sqlite with the full schema on first launch;
// the native broker's read routes were written to tolerate a missing database
// (an absent file yields a real empty list), but the write routes open the
// index read-write and fail when the file has never been created. On a fresh
// install nothing else creates it -- not the installer and not the payload --
// so the very first recipe save returned persist_failed.
//
// EnsureInitialized closes that gap: it creates the Database folder and the
// SQLite file (ReadWriteCreate) and runs the same CREATE TABLE / CREATE INDEX
// statements the oracle runs, all IF NOT EXISTS so it is idempotent. Running it
// at every startup is a no-op on an existing database and a full bootstrap on a
// fresh workspace. It never touches the PAX engine, never spawns a process, and
// writes nothing but schema plus the single _schema_meta provenance row.
internal static class WorkspaceDatabase
{
    private const long SchemaVersion = 1L;

    // Oracle parity: $Script:M1_Ddl. The core schema -- the same column sets,
    // defaults, and indexes the native read/write models already expect.
    private const string CoreDdl = @"
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS _schema_meta (
  id              INTEGER PRIMARY KEY CHECK (id = 1),
  schema_version  INTEGER NOT NULL,
  workspace_id    TEXT    NOT NULL,
  created_at      TEXT    NOT NULL,
  updated_at      TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS recipes (
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
CREATE INDEX IF NOT EXISTS idx_recipes_name        ON recipes(name);
CREATE INDEX IF NOT EXISTS idx_recipes_status      ON recipes(status);
CREATE INDEX IF NOT EXISTS idx_recipes_last_cooked ON recipes(last_cooked_at);

CREATE TABLE IF NOT EXISTS cooks (
  cook_id                TEXT PRIMARY KEY,
  recipe_id              TEXT REFERENCES recipes(recipe_id) ON DELETE SET NULL,
  recipe_version_id      TEXT,
  recipe_snapshot_json   TEXT NOT NULL,
  command_argv_json      TEXT NOT NULL,
  command_argv_redacted  TEXT NOT NULL,
  pax_script_path        TEXT NOT NULL,
  pax_script_version     TEXT NOT NULL,
  trigger                TEXT NOT NULL,
  schedule_id            TEXT,
  parent_cook_id         TEXT REFERENCES cooks(cook_id) ON DELETE SET NULL,
  cook_folder            TEXT NOT NULL,
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
CREATE INDEX IF NOT EXISTS idx_cooks_recipe     ON cooks(recipe_id, started_at);
CREATE INDEX IF NOT EXISTS idx_cooks_status     ON cooks(status, started_at);
CREATE INDEX IF NOT EXISTS idx_cooks_started_at ON cooks(started_at);
CREATE INDEX IF NOT EXISTS idx_cooks_schedule   ON cooks(schedule_id, started_at);

CREATE TABLE IF NOT EXISTS cook_artifacts (
  cook_artifact_id   TEXT PRIMARY KEY,
  cook_id            TEXT NOT NULL REFERENCES cooks(cook_id) ON DELETE CASCADE,
  stream             TEXT NOT NULL,
  artifact_kind      TEXT NOT NULL,
  tier               TEXT NOT NULL,
  location           TEXT NOT NULL,
  size_bytes         INTEGER,
  row_count          INTEGER,
  is_append          INTEGER NOT NULL DEFAULT 0,
  pantry_dataset_id  TEXT,
  created_at         TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_cook_artifacts_cook ON cook_artifacts(cook_id);

CREATE TABLE IF NOT EXISTS settings (
  key    TEXT PRIMARY KEY,
  value  TEXT NOT NULL,
  scope  TEXT NOT NULL DEFAULT 'global'
);

CREATE TABLE IF NOT EXISTS auth_profiles (
  auth_profile_id      TEXT PRIMARY KEY,
  name                 TEXT NOT NULL UNIQUE,
  mode                 TEXT NOT NULL CHECK (mode IN ('AppRegistrationSecret','AppRegistrationCertificate')),
  tenant_id            TEXT NOT NULL,
  client_id            TEXT NOT NULL,
  cred_man_target      TEXT,
  cert_thumbprint      TEXT,
  cert_store           TEXT,
  description          TEXT,
  last_verified_at     TEXT,
  last_verified_result TEXT,
  created_at           TEXT NOT NULL,
  updated_at           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_auth_profiles_mode ON auth_profiles(mode);
";

    // Oracle parity: the V1.S06c scheduled_tasks registry created after the M1
    // block. Windows Task Scheduler owns recurrence truth; this stores only the
    // non-secret metadata pointers.
    private const string ScheduledTasksDdl = @"
CREATE TABLE IF NOT EXISTS scheduled_tasks (
    scheduled_task_id        TEXT PRIMARY KEY,
    recipe_id                TEXT NOT NULL UNIQUE
                               REFERENCES recipes(recipe_id) ON DELETE CASCADE,
    windows_task_name        TEXT NOT NULL,
    windows_task_path        TEXT NOT NULL DEFAULT '\PAX Cookbook\',
    recipe_projection_hash   TEXT NOT NULL,
    pax_script_version       TEXT NOT NULL,
    registered_at            TEXT NOT NULL,
    registered_by_user       TEXT NOT NULL,
    last_imported_cook_id    TEXT REFERENCES cooks(cook_id) ON DELETE SET NULL,
    last_imported_at         TEXT,
    last_stale_check_at      TEXT,
    status                   TEXT NOT NULL DEFAULT 'active',
    registered_recurrence_json TEXT,
    created_at               TEXT NOT NULL,
    updated_at               TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_scheduled_tasks_recipe ON scheduled_tasks(recipe_id);
CREATE INDEX IF NOT EXISTS idx_scheduled_tasks_status ON scheduled_tasks(status);
";

    private static string DatabaseDir(string workspacePath) =>
        Path.Combine(workspacePath, "Database");

    private static string DatabaseFile(string workspacePath) =>
        Path.Combine(DatabaseDir(workspacePath), "cookbook.sqlite");

    // Idempotent. Creates <workspace>\Database\cookbook.sqlite (and its folder)
    // if missing and ensures the full schema exists. Safe to call on every
    // startup: a no-op on an already-initialized workspace.
    public static void EnsureInitialized(string workspacePath)
    {
        string dbDir = DatabaseDir(workspacePath);
        Directory.CreateDirectory(dbDir);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile(workspacePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };

        using var conn = new SqliteConnection(csb.ConnectionString);
        conn.Open();

        // Oracle parity: WAL is file-scoped and persisted on the database file,
        // so setting it once here applies to every later short-lived connection
        // the read/write models open.
        Execute(conn, "PRAGMA journal_mode=WAL;");

        Execute(conn, CoreDdl);
        Execute(conn, ScheduledTasksDdl);
        MigrateCookColumns(conn);

        SeedSchemaMeta(conn);
    }

    // Oracle parity: the additive cook columns applied AFTER the M1 DDL
    // (Start-Broker.ps1 Apply-M1Schema). The native broker reads and writes
    // cooks.closure_reason; the others are carried for oracle-schema fidelity.
    // SQLite has no ADD COLUMN IF NOT EXISTS, so each column is added only when
    // PRAGMA table_info reports it absent -- which also repairs an existing
    // database created before these columns were added.
    private static readonly (string Column, string Type)[] CookColumnMigrations =
    {
        ("closure_reason", "TEXT"),
        ("closure_evidence_json", "TEXT"),
        ("abnormal_close_recorded_utc", "TEXT"),
        ("orphan_pid", "INTEGER"),
        ("orphan_probe_verdict", "TEXT"),
        ("recovery_run_id", "TEXT"),
        ("broker_session_id_at_shutdown", "TEXT"),
    };

    private static void MigrateCookColumns(SqliteConnection conn)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(cooks);";
            using SqliteDataReader r = info.ExecuteReader();
            while (r.Read())
            {
                // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk.
                existing.Add(r.GetString(1));
            }
        }

        foreach ((string column, string type) in CookColumnMigrations)
        {
            if (!existing.Contains(column))
            {
                // column + type come from the fixed list above, never user input.
                Execute(conn, $"ALTER TABLE cooks ADD COLUMN {column} {type};");
            }
        }

        Execute(conn, "CREATE INDEX IF NOT EXISTS idx_cooks_closure_reason ON cooks(closure_reason);");
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // Oracle parity: the _schema_meta single-row upsert. Inserts the provenance
    // row on a fresh database; leaves an existing row untouched.
    private static void SeedSchemaMeta(SqliteConnection conn)
    {
        using (SqliteCommand check = conn.CreateCommand())
        {
            check.CommandText = "SELECT workspace_id FROM _schema_meta WHERE id = 1;";
            object? existing = check.ExecuteScalar();
            if (existing is not null && existing is not DBNull)
            {
                return;
            }
        }

        string now = DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string workspaceId = Guid.NewGuid().ToString();

        using SqliteCommand insert = conn.CreateCommand();
        insert.CommandText = @"
INSERT INTO _schema_meta (id, schema_version, workspace_id, created_at, updated_at)
VALUES (1, $schema_version, $workspace_id, $now, $now);";
        insert.Parameters.AddWithValue("$schema_version", SchemaVersion);
        insert.Parameters.AddWithValue("$workspace_id", workspaceId);
        insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();
    }
}
