using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Cook history read surface + lifecycle reconciliation + post-run output
// discovery. This is the backend foundation that lets a Bake be observed and
// safely reasoned about WITHOUT any SPA execution wiring:
//
//   * ListCooks / GetCookDetail / GetCookLog back three read-only GET routes
//     that project the cooks index and the managed cook-folder sentinels. They
//     never spawn a child, never read or mutate the PAX bytes, never read a
//     secret, never serve an arbitrary filesystem path, and only ever surface
//     the REDACTED command projection.
//   * ReconcileCooksAtStartup heals rows left in 'running' when the broker
//     exited mid-cook. It never kills a process and never deletes a log or an
//     output; it only transitions the index row and repairs the sentinel.
//   * WriteReadinessSnapshot persists a no-secret readiness summary at cook
//     preparation time; DiscoverAndRecordOutputs records destination path /
//     size / existence (never contents) once a cook reaches a terminal state.
//
// All wire bodies are anonymous objects whose property names ARE the emitted
// JSON keys; the record types below are the typed carriers that those bodies
// are built from.
internal static partial class RecipeReadModel
{
    // ---- Read DTO carriers (typed projections; wire shape is built from these) ----

    private sealed record CookListItem(
        string CookId,
        string RecipeId,
        string? RecipeName,
        string Status,
        string? Trigger,
        string? StartedAt,
        string? FinishedAt,
        double? DurationSeconds,
        long? ExitCode,
        string? ClosureReason,
        string? ErrorClass,
        string CreatedAt,
        string? UpdatedAt);

    private sealed record CookDetailView(
        string CookId,
        string RecipeId,
        string Status,
        string? Trigger,
        string? StartedAt,
        string? FinishedAt,
        double? DurationSeconds,
        long? ExitCode,
        string? ClosureReason,
        string? ErrorClass,
        string? ErrorMessage,
        string CreatedAt,
        string? UpdatedAt,
        string? CommandRedacted);

    internal sealed record CookLogResult(int Status, string ContentType, string Text);

    private sealed record OutputPathSummary(string Role, string? Path, bool Exists, long? SizeBytes);

    private sealed record EngineFingerprintSummary(string? Version, string? Sha256, string? Path);

    private sealed record ReadinessSnapshotSummary(
        string Status,
        string EngineStatus,
        string? EngineSha256,
        string AuthMode,
        bool ChefKeyBound,
        IReadOnlyList<string> Requirements,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Errors,
        OutputPathSummary FactDestination,
        OutputPathSummary UserInfoDestination);

    // Canonical cook status vocabulary. The index stores the writer's literal
    // string; Normalize folds it to a stable, lowercase value so the read
    // surface never leaks an unexpected token to the SPA.
    private static class CookStatuses
    {
        internal const string Running = "running";
        internal const string Completed = "completed";
        internal const string Errored = "errored";
        internal const string Canceled = "canceled";
        internal const string Interrupted = "interrupted";
        internal const string Unknown = "unknown";

        internal static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Unknown;
            }
            switch (raw.Trim().ToLowerInvariant())
            {
                case "running": return Running;
                case "completed": return Completed;
                case "errored": return Errored;
                case "canceled":
                case "cancelled": return Canceled;
                case "interrupted": return Interrupted;
                default: return Unknown;
            }
        }
    }

    private readonly record struct CookRow(
        string CookId,
        string RecipeId,
        string Status,
        string? Trigger,
        string? StartedAt,
        string? FinishedAt,
        double? DurationSeconds,
        long? ExitCode,
        string? ClosureReason,
        string? ErrorClass,
        string? ErrorMessage,
        string CreatedAt,
        string? UpdatedAt,
        string? CommandRedacted,
        string CookFolder,
        string? RecipeSnapshotJson);

    private static bool IsValidCookId(string cookId) => CookIdPattern().IsMatch(cookId);

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex CookIdPattern();

    // ---- GET /api/v1/cooks ----
    // Newest-first index projection. No database file yields an empty list (a
    // clean first-run shape, never an error).
    internal static object ListCooks(string workspacePath)
    {
        var items = new List<CookListItem>();
        using SqliteConnection? conn = OpenReadOnly(workspacePath);
        if (conn is not null)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT cook_id, recipe_id, recipe_snapshot_json, status, trigger, started_at, " +
                "finished_at, duration_seconds, exit_code, closure_reason, error_class, " +
                "created_at, updated_at FROM cooks ORDER BY created_at DESC, cook_id DESC;";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                items.Add(new CookListItem(
                    CookId: r.GetString(0),
                    RecipeId: r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    RecipeName: ExtractRecipeName(r.IsDBNull(2) ? null : r.GetString(2)),
                    Status: CookStatuses.Normalize(r.IsDBNull(3) ? null : r.GetString(3)),
                    Trigger: r.IsDBNull(4) ? null : r.GetString(4),
                    StartedAt: r.IsDBNull(5) ? null : r.GetString(5),
                    FinishedAt: r.IsDBNull(6) ? null : r.GetString(6),
                    DurationSeconds: r.IsDBNull(7) ? (double?)null : r.GetDouble(7),
                    ExitCode: r.IsDBNull(8) ? (long?)null : r.GetInt64(8),
                    ClosureReason: r.IsDBNull(9) ? null : r.GetString(9),
                    ErrorClass: r.IsDBNull(10) ? null : r.GetString(10),
                    CreatedAt: r.IsDBNull(11) ? string.Empty : r.GetString(11),
                    UpdatedAt: r.IsDBNull(12) ? null : r.GetString(12)));
            }
        }

        return new
        {
            cooks = items.Select(i => new
            {
                cookId = i.CookId,
                recipeId = i.RecipeId,
                recipeName = i.RecipeName,
                status = i.Status,
                trigger = i.Trigger,
                startedAt = i.StartedAt,
                finishedAt = i.FinishedAt,
                durationSeconds = i.DurationSeconds,
                exitCode = i.ExitCode,
                closureReason = i.ClosureReason,
                errorClass = i.ErrorClass,
                createdAt = i.CreatedAt,
                updatedAt = i.UpdatedAt,
            }).ToList(),
        };
    }

    // ---- GET /api/v1/cooks/{cookId} ----
    internal static (int Status, object Body) GetCookDetail(string workspacePath, string cookId)
    {
        if (string.IsNullOrWhiteSpace(cookId) || !IsValidCookId(cookId))
        {
            return (400, new { error = "invalid_cook_id", cookId });
        }

        CookRow? maybe = ReadCookRow(workspacePath, cookId);
        if (maybe is null)
        {
            return (404, new { error = "cook_not_found", cookId });
        }

        CookRow c = maybe.Value;
        string cookFolderAbs = ResolveCookFolderAbs(workspacePath, c.CookFolder);

        object? recipeSummary = BuildRecipeSummary(c.RecipeSnapshotJson);
        object? engine = ReadEngineFingerprint(cookFolderAbs);
        object? readiness = ReadCookFolderJson(cookFolderAbs, "readiness-snapshot.json");
        object? outputs = ReadCookFolderJson(cookFolderAbs, "outputs.json");

        // On-disk path to the managed, broker-redacted cook.log, surfaced ONLY
        // when the file exists so the SPA's "Open log" buttons can open it in the
        // user's default app via /api/v1/open-file (which re-validates it). The
        // path is derived from the row's managed cook_folder, never from request
        // input; null when no log file is present yet.
        string logFileAbs = cookFolderAbs.Length == 0
            ? string.Empty
            : Path.Combine(cookFolderAbs, "cook.log");
        string? logPath = logFileAbs.Length > 0 && File.Exists(logFileAbs) ? logFileAbs : null;

        // Resume cooks surface their non-secret checkpoint provenance (path /
        // force / chefKeyId) from cook-context.json so the Bakes detail can show
        // the checkpoint path instead of a recipe name. Read only for a resume
        // trigger; every other cook projects checkpoint = null (additive field).
        object? checkpoint = string.Equals(c.Trigger, "resume", StringComparison.OrdinalIgnoreCase)
            ? ReadResumeCheckpointBlock(cookFolderAbs)
            : null;

        var view = new CookDetailView(
            CookId: c.CookId,
            RecipeId: c.RecipeId,
            Status: CookStatuses.Normalize(c.Status),
            Trigger: c.Trigger,
            StartedAt: c.StartedAt,
            FinishedAt: c.FinishedAt,
            DurationSeconds: c.DurationSeconds,
            ExitCode: c.ExitCode,
            ClosureReason: c.ClosureReason,
            ErrorClass: c.ErrorClass,
            ErrorMessage: c.ErrorMessage,
            CreatedAt: c.CreatedAt,
            UpdatedAt: c.UpdatedAt,
            CommandRedacted: c.CommandRedacted);

        return (200, new
        {
            cookId = view.CookId,
            recipeId = view.RecipeId,
            status = view.Status,
            trigger = view.Trigger,
            startedAt = view.StartedAt,
            finishedAt = view.FinishedAt,
            durationSeconds = view.DurationSeconds,
            exitCode = view.ExitCode,
            closureReason = view.ClosureReason,
            commandRedacted = view.CommandRedacted,
            recipe = recipeSummary,
            engine,
            readiness,
            outputs,
            logPath,
            checkpoint,
            errorSummary = new
            {
                errorClass = view.ErrorClass,
                errorMessage = view.ErrorMessage,
                closureReason = view.ClosureReason,
            },
            createdAt = view.CreatedAt,
            updatedAt = view.UpdatedAt,
        });
    }

    // ---- GET /api/v1/cooks/{cookId}/log ----
    // Serves the managed cook.log as text/plain. The log path is derived from
    // the row's stored cook_folder (which is always a managed Cooks\ relative
    // path), never from request input, and is re-confirmed to live under the
    // workspace Cooks\ root before any read. The file is opened with a shared
    // read handle so a live supervisor still streaming into it is never blocked.
    internal static CookLogResult GetCookLog(string workspacePath, string cookId)
    {
        if (string.IsNullOrWhiteSpace(cookId) || !IsValidCookId(cookId))
        {
            return new CookLogResult(400, "application/json", JsonError("invalid_cook_id", cookId));
        }

        CookRow? maybe = ReadCookRow(workspacePath, cookId);
        if (maybe is null)
        {
            return new CookLogResult(404, "application/json", JsonError("cook_not_found", cookId));
        }

        string cookFolderAbs = ResolveCookFolderAbs(workspacePath, maybe.Value.CookFolder);
        string logPath = cookFolderAbs.Length == 0
            ? string.Empty
            : Path.Combine(cookFolderAbs, "cook.log");

        if (logPath.Length == 0 || !File.Exists(logPath))
        {
            return new CookLogResult(404, "application/json", JsonError("cook_log_not_found", cookId));
        }

        string? text = SafeReadAllText(logPath);
        if (text is null)
        {
            return new CookLogResult(404, "application/json", JsonError("cook_log_not_found", cookId));
        }

        return new CookLogResult(200, "text/plain; charset=utf-8", text);
    }

    // ---- Startup reconciliation ----
    // Heals cooks left 'running' when the broker exited mid-cook. Returns the
    // number of rows transitioned. Conservative by construction: it never kills
    // a process (PID liveness is untrustworthy after a restart due to PID
    // reuse) and never deletes a log or an output. If the child actually
    // finished but the terminal index write was lost, the finished.json
    // sentinel is authoritative and the row is reconciled to its true terminal
    // state; otherwise the cook is marked interrupted / broker_exited.
    internal static int ReconcileCooksAtStartup(string workspacePath)
    {
        string dbFile = DatabaseFile(workspacePath);
        if (!File.Exists(dbFile))
        {
            return 0;
        }

        var stale = new List<(string CookId, string RecipeId, string CookFolder)>();
        try
        {
            using SqliteConnection conn = OpenCookReadWrite(workspacePath);
            using SqliteCommand sel = conn.CreateCommand();
            sel.CommandText =
                "SELECT cook_id, recipe_id, cook_folder FROM cooks WHERE status = 'running';";
            using SqliteDataReader r = sel.ExecuteReader();
            while (r.Read())
            {
                stale.Add((
                    r.GetString(0),
                    r.IsDBNull(1) ? string.Empty : r.GetString(1),
                    r.IsDBNull(2) ? string.Empty : r.GetString(2)));
            }
        }
        catch
        {
            return 0;
        }

        int reconciled = 0;
        foreach ((string cookId, _, string cookFolderRel) in stale)
        {
            try
            {
                if (ReconcileOneStaleCook(workspacePath, cookId, cookFolderRel))
                {
                    reconciled++;
                }
            }
            catch
            {
                // Best-effort per row; one bad row never blocks the rest.
            }
        }
        return reconciled;
    }

    private static bool ReconcileOneStaleCook(
        string workspacePath, string cookId, string cookFolderRel)
    {
        string cookFolderAbs = ResolveCookFolderAbs(workspacePath, cookFolderRel);
        string finishedPath = cookFolderAbs.Length == 0
            ? string.Empty
            : Path.Combine(cookFolderAbs, "finished.json");
        string interruptedPath = cookFolderAbs.Length == 0
            ? string.Empty
            : Path.Combine(cookFolderAbs, "interrupted.json");

        // Child actually finished; only the index write was lost. Reconcile from
        // the finished.json sentinel.
        if (finishedPath.Length > 0 && File.Exists(finishedPath))
        {
            var fin = JsonModel.Parse(SafeReadAllText(finishedPath) ?? string.Empty)
                as Dictionary<string, object?>;
            long exitCode = -1;
            string finishedAt = CookUtcNowIso();
            double duration = 0;
            if (fin is not null)
            {
                if (fin.TryGetValue("exitCode", out var ec) && ec is long ecl)
                {
                    exitCode = ecl;
                }
                if (fin.TryGetValue("finishedAt", out var fa))
                {
                    string s = JsonModel.Str(fa);
                    if (!string.IsNullOrEmpty(s))
                    {
                        finishedAt = s;
                    }
                }
                if (fin.TryGetValue("durationSec", out var ds))
                {
                    duration = ds switch
                    {
                        double dd => dd,
                        long dl => dl,
                        _ => 0,
                    };
                }
            }

            bool clean = exitCode == 0;
            UpdateCookTerminal(
                workspacePath, cookId, clean ? CookStatuses.Completed : CookStatuses.Errored,
                (int)exitCode, finishedAt, duration,
                clean ? null : "nonzero_exit", null, clean ? "clean_exit" : "nonzero_exit");
            DiscoverAndRecordOutputs(workspacePath, cookFolderAbs);
            return true;
        }

        // Broker died mid-run. Mark interrupted / broker_exited and repair the
        // sentinel. Preserve any partial outputs (discover, never delete).
        string interruptedAt = CookUtcNowIso();
        if (cookFolderAbs.Length > 0)
        {
            try { Directory.CreateDirectory(cookFolderAbs); } catch { /* best-effort */ }
            if (!(interruptedPath.Length > 0 && File.Exists(interruptedPath)))
            {
                WriteInterruptedSentinel(
                    cookFolderAbs, interruptedAt, Environment.MachineName, "broker_exited");
            }
            DiscoverAndRecordOutputs(workspacePath, cookFolderAbs);
        }
        UpdateCookTerminal(
            workspacePath, cookId, CookStatuses.Interrupted, exitCode: null,
            finishedAt: interruptedAt, durationSeconds: 0, errorClass: "broker_exited",
            errorMessage: null, closureReason: "broker_exited");
        return true;
    }

    // ---- Readiness snapshot (written at cook preparation time) ----
    // A no-secret summary of why the cook was allowed to prepare: engine state,
    // auth MODE (never a secret), the configured destinations, and empty
    // warning / error lists (the prepare gates already passed by the time this
    // runs). Surfaced read-only via cook detail.
    private static void WriteReadinessSnapshot(
        string cookFolderAbs,
        Dictionary<string, object?> recipe,
        EngineAcquisitionResult engine)
    {
        try
        {
            var snapshot = new ReadinessSnapshotSummary(
                Status: "prepared",
                EngineStatus: "acquired",
                EngineSha256: engine.RecordedSha256,
                AuthMode: ResolveAuthModeLabel(recipe),
                ChefKeyBound: !string.IsNullOrEmpty(GetChefKeyId(recipe)),
                Requirements: new List<string> { "engine_acquired", "auth_mode_present", "destinations_present" },
                Warnings: new List<string>(),
                Errors: new List<string>(),
                FactDestination: BuildOutputSummary("fact", recipe, "fact"),
                UserInfoDestination: BuildOutputSummary("userInfo", recipe, "userInfo"));

            WriteAtomicUtf8NoBom(
                Path.Combine(cookFolderAbs, "readiness-snapshot.json"),
                JsonModel.SerializeToUtf8Bytes(ReadinessToDict(snapshot), indented: true));
        }
        catch
        {
            // readiness-snapshot.json is advisory; never block cook preparation.
        }
    }

    // ---- Output discovery (recorded once a cook reaches a terminal state) ----
    // Records each configured destination's path / existence / size ONLY. The
    // output contents are never read. Missing recipe snapshots and missing
    // output files are recorded as a clean absence, never an error.
    private static void DiscoverAndRecordOutputs(string workspacePath, string cookFolderAbs)
    {
        if (string.IsNullOrEmpty(cookFolderAbs))
        {
            return;
        }

        try
        {
            string snapshotPath = Path.Combine(cookFolderAbs, "recipe-snapshot.json");
            var outputs = new List<OutputPathSummary>();
            if (File.Exists(snapshotPath))
            {
                var recipe = JsonModel.Parse(SafeReadAllText(snapshotPath) ?? string.Empty)
                    as Dictionary<string, object?>;
                // The write-new fact mode hands PAX -OutputPath as a DIRECTORY and
                // PAX names the CSV inside it, so the actual output is discovered
                // by scanning that directory (metadata only). append-mode fact and
                // userInfo keep the declared-path existence check.
                outputs.Add(BuildDiscoveredFactSummary(recipe));
                outputs.Add(BuildOutputSummary("userInfo", recipe, "userInfo"));
            }

            var doc = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1L,
                ["discoveredAt"] = CookUtcNowIso(),
                ["outputs"] = outputs.Select(o => (object?)OutputToDict(o)).ToList(),
            };
            WriteAtomicUtf8NoBom(
                Path.Combine(cookFolderAbs, "outputs.json"),
                JsonModel.SerializeToUtf8Bytes(doc, indented: true));
        }
        catch
        {
            // outputs.json is advisory; never block finalization or reconcile.
        }

        _ = workspacePath;
    }

    private static OutputPathSummary BuildOutputSummary(
        string role, Dictionary<string, object?>? recipe, string key)
    {
        string? path = ResolveDestinationPath(recipe, key);
        if (string.IsNullOrEmpty(path))
        {
            return new OutputPathSummary(role, null, false, null);
        }

        bool exists = false;
        long? size = null;
        try
        {
            if (File.Exists(path))
            {
                exists = true;
                size = new FileInfo(path).Length;
            }
        }
        catch
        {
            // An unreadable destination is recorded as absent, never an error.
        }
        return new OutputPathSummary(role, path, exists, size);
    }

    // Discovery-time fact summary. The write-new (-OutputPath) fact mode hands
    // PAX a DIRECTORY (see PaxAdapter.ResolveFactOutputDirectory); PAX writes and
    // names its own CSV(s) inside it. So for write-new we SCAN that directory for
    // PAX's output CSV and record the newest match (path + size + existence). The
    // file CONTENTS are never read (constraint 14). append-mode fact uses the
    // explicit -AppendFile path and keeps the declared-file existence check.
    private static OutputPathSummary BuildDiscoveredFactSummary(Dictionary<string, object?>? recipe)
    {
        if (recipe is null)
        {
            return new OutputPathSummary("fact", null, false, null);
        }
        if (!(recipe.TryGetValue("destinations", out var dRaw) && dRaw is Dictionary<string, object?> dest) ||
            !(dest.TryGetValue("fact", out var nRaw) && nRaw is Dictionary<string, object?> node))
        {
            return new OutputPathSummary("fact", null, false, null);
        }

        string mode = node.TryGetValue("mode", out var m) ? JsonModel.Str(m) : string.Empty;
        string path = node.TryGetValue("path", out var p) ? JsonModel.Str(p) : string.Empty;

        // append mode (or no write-new path) -> explicit named file; keep the
        // existing declared-path existence check.
        bool isAppend = string.Equals(mode, "append", StringComparison.OrdinalIgnoreCase);
        if (isAppend || string.IsNullOrEmpty(path))
        {
            return BuildOutputSummary("fact", recipe, "fact");
        }

        // write-new (-OutputPath) -> scan the PAX output directory for the file
        // PAX actually wrote and named.
        string outDir = PaxAdapter.ResolveFactOutputDirectory(path);
        string? discovered = FindNewestPaxCsv(outDir);
        if (discovered is null)
        {
            // No output produced yet: record the output directory, exists:false.
            return new OutputPathSummary("fact", outDir, false, null);
        }
        long? size = null;
        try { size = new FileInfo(discovered).Length; }
        catch { /* unreadable file recorded without a size */ }
        return new OutputPathSummary("fact", discovered, true, size);
    }

    // Scans a PAX fact-output directory for the engine's auto-named CSV. PAX
    // prefixes its outputs with "Purview_" (audit / usage-activity / rollup), so
    // prefer those, fall back to any *.csv, and return the newest by write time.
    // Returns null when the directory is absent or holds no CSV. Read-only: only
    // file names / timestamps are inspected, never the file contents.
    private static string? FindNewestPaxCsv(string? directory)
    {
        try
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return null;
            }
            var matches = Directory.EnumerateFiles(directory, "Purview_*.csv", SearchOption.TopDirectoryOnly).ToList();
            if (matches.Count == 0)
            {
                matches = Directory.EnumerateFiles(directory, "*.csv", SearchOption.TopDirectoryOnly).ToList();
            }
            string? newest = null;
            DateTime newestUtc = DateTime.MinValue;
            foreach (string f in matches)
            {
                DateTime when;
                try { when = new FileInfo(f).LastWriteTimeUtc; }
                catch { continue; }
                if (newest is null || when > newestUtc)
                {
                    newest = f;
                    newestUtc = when;
                }
            }
            return newest;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveDestinationPath(Dictionary<string, object?>? recipe, string key)
    {
        if (recipe is null)
        {
            return null;
        }
        if (!(recipe.TryGetValue("destinations", out var dRaw) && dRaw is Dictionary<string, object?> dest))
        {
            return null;
        }
        if (!(dest.TryGetValue(key, out var nRaw) && nRaw is Dictionary<string, object?> node))
        {
            return null;
        }

        string mode = node.TryGetValue("mode", out var m) ? JsonModel.Str(m) : string.Empty;
        string? path = node.TryGetValue("path", out var p) ? JsonModel.Str(p) : null;
        string? appendFile = node.TryGetValue("appendFile", out var af) ? JsonModel.Str(af) : null;

        if (mode == "append" && !string.IsNullOrEmpty(appendFile))
        {
            return appendFile;
        }
        if (!string.IsNullOrEmpty(path))
        {
            return path;
        }
        return string.IsNullOrEmpty(appendFile) ? null : appendFile;
    }

    // ---- Detail building helpers ----

    private static object? BuildRecipeSummary(string? recipeSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(recipeSnapshotJson))
        {
            return null;
        }
        if (JsonModel.Parse(recipeSnapshotJson) is not Dictionary<string, object?> recipe)
        {
            return null;
        }

        return new
        {
            recipeId = recipe.TryGetValue("recipeId", out var rid) ? JsonModel.Str(rid) : null,
            name = ExtractRecipeName(recipeSnapshotJson),
            authMode = ResolveAuthModeLabel(recipe),
            dashboard = ResolveDashboard(recipe),
            destinations = new
            {
                fact = DestinationSummary(recipe, "fact"),
                userInfo = DestinationSummary(recipe, "userInfo"),
            },
        };
    }

    // Reads the recipe snapshot's processing.dashboard. Returns the lowercased
    // target ('aio' / 'aibv') or null when absent, surfacing the recipe's
    // dashboard on the cook detail alongside auth mode and destinations.
    private static string? ResolveDashboard(Dictionary<string, object?> recipe)
    {
        if (!(recipe.TryGetValue("processing", out var pRaw) && pRaw is Dictionary<string, object?> proc))
        {
            return null;
        }
        if (!proc.TryGetValue("dashboard", out var dRaw))
        {
            return null;
        }
        string value = JsonModel.Str(dRaw);
        return string.IsNullOrWhiteSpace(value) ? null : value.ToLowerInvariant();
    }

    private static object? DestinationSummary(Dictionary<string, object?> recipe, string key)
    {
        if (!(recipe.TryGetValue("destinations", out var dRaw) && dRaw is Dictionary<string, object?> dest))
        {
            return null;
        }
        if (!(dest.TryGetValue(key, out var nRaw) && nRaw is Dictionary<string, object?> node))
        {
            return null;
        }
        return new
        {
            path = node.TryGetValue("path", out var p) ? JsonModel.Str(p) : null,
            mode = node.TryGetValue("mode", out var m) ? JsonModel.Str(m) : null,
            appendFile = node.TryGetValue("appendFile", out var af) ? JsonModel.Str(af) : null,
        };
    }

    private static object? ReadEngineFingerprint(string cookFolderAbs)
    {
        if (ReadCookFolderJson(cookFolderAbs, "cook-context.json") is not Dictionary<string, object?> ctx)
        {
            return null;
        }
        if (!(ctx.TryGetValue("bundledPax", out var bpRaw) && bpRaw is Dictionary<string, object?> bp))
        {
            return null;
        }

        var fp = new EngineFingerprintSummary(
            Version: bp.TryGetValue("version", out var v) ? JsonModel.Str(v) : null,
            Sha256: bp.TryGetValue("sha256", out var s) ? JsonModel.Str(s) : null,
            Path: bp.TryGetValue("path", out var p) ? JsonModel.Str(p) : null);

        return new { version = fp.Version, sha256 = fp.Sha256, path = fp.Path };
    }

    // Reads the non-secret resume checkpoint provenance block (path / force /
    // chefKeyId) written into a resume cook's cook-context.json. Returns null for
    // a cook with no checkpoint block (every non-resume cook), so the detail's
    // checkpoint field is null except on resume cooks. Never reads a secret: the
    // checkpoint block carries only the local path, the force flag, and the
    // operator's chosen Chef's Key id (or null).
    private static object? ReadResumeCheckpointBlock(string cookFolderAbs)
    {
        if (ReadCookFolderJson(cookFolderAbs, "cook-context.json") is not Dictionary<string, object?> ctx)
        {
            return null;
        }
        if (!(ctx.TryGetValue("checkpoint", out var cpRaw) && cpRaw is Dictionary<string, object?> cp))
        {
            return null;
        }
        return new
        {
            path = cp.TryGetValue("path", out var p) ? JsonModel.Str(p) : null,
            force = cp.TryGetValue("force", out var f) && JsonModel.Bool(f),
            chefKeyId = cp.TryGetValue("chefKeyId", out var k) && k is not null ? JsonModel.Str(k) : null,
        };
    }

    private static string? ExtractRecipeName(string? recipeSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(recipeSnapshotJson))
        {
            return null;
        }
        if (JsonModel.Parse(recipeSnapshotJson) is not Dictionary<string, object?> recipe)
        {
            return null;
        }
        if (recipe.TryGetValue("identity", out var idRaw) && idRaw is Dictionary<string, object?> identity
            && identity.TryGetValue("name", out var n))
        {
            string s = JsonModel.Str(n);
            if (!string.IsNullOrEmpty(s))
            {
                return s;
            }
        }
        return null;
    }

    private static string ResolveAuthModeLabel(Dictionary<string, object?>? recipe)
    {
        if (recipe is not null && recipe.TryGetValue("auth", out var aRaw)
            && aRaw is Dictionary<string, object?> auth && auth.TryGetValue("mode", out var m))
        {
            string s = JsonModel.Str(m);
            if (!string.IsNullOrEmpty(s))
            {
                return s;
            }
        }
        return "unknown";
    }

    private static string? GetChefKeyId(Dictionary<string, object?>? recipe)
    {
        if (recipe is not null && recipe.TryGetValue("auth", out var aRaw)
            && aRaw is Dictionary<string, object?> auth && auth.TryGetValue("chefKeyId", out var v))
        {
            string s = JsonModel.Str(v);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        return null;
    }

    private static Dictionary<string, object?> ReadinessToDict(ReadinessSnapshotSummary s) => new()
    {
        ["schemaVersion"] = 1L,
        ["status"] = s.Status,
        ["engineStatus"] = s.EngineStatus,
        ["engineSha256"] = s.EngineSha256,
        ["authMode"] = s.AuthMode,
        ["chefKeyBound"] = s.ChefKeyBound,
        ["requirements"] = s.Requirements.Select(x => (object?)x).ToList(),
        ["warnings"] = s.Warnings.Select(x => (object?)x).ToList(),
        ["errors"] = s.Errors.Select(x => (object?)x).ToList(),
        ["destinations"] = new Dictionary<string, object?>
        {
            ["fact"] = OutputToDict(s.FactDestination),
            ["userInfo"] = OutputToDict(s.UserInfoDestination),
        },
    };

    private static Dictionary<string, object?> OutputToDict(OutputPathSummary o) => new()
    {
        ["role"] = o.Role,
        ["path"] = o.Path,
        ["exists"] = o.Exists,
        ["sizeBytes"] = o.SizeBytes,
    };

    // ---- Low-level reads ----

    private static CookRow? ReadCookRow(string workspacePath, string cookId)
    {
        using SqliteConnection? conn = OpenReadOnly(workspacePath);
        if (conn is null)
        {
            return null;
        }

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT cook_id, recipe_id, status, trigger, started_at, finished_at, " +
            "duration_seconds, exit_code, closure_reason, error_class, error_message, " +
            "created_at, updated_at, command_argv_redacted, cook_folder, recipe_snapshot_json " +
            "FROM cooks WHERE cook_id = $cook_id LIMIT 1;";
        SqliteParameter p = cmd.CreateParameter();
        p.ParameterName = "$cook_id";
        p.Value = cookId;
        cmd.Parameters.Add(p);

        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }

        return new CookRow(
            CookId: r.GetString(0),
            RecipeId: r.IsDBNull(1) ? string.Empty : r.GetString(1),
            Status: r.IsDBNull(2) ? string.Empty : r.GetString(2),
            Trigger: r.IsDBNull(3) ? null : r.GetString(3),
            StartedAt: r.IsDBNull(4) ? null : r.GetString(4),
            FinishedAt: r.IsDBNull(5) ? null : r.GetString(5),
            DurationSeconds: r.IsDBNull(6) ? (double?)null : r.GetDouble(6),
            ExitCode: r.IsDBNull(7) ? (long?)null : r.GetInt64(7),
            ClosureReason: r.IsDBNull(8) ? null : r.GetString(8),
            ErrorClass: r.IsDBNull(9) ? null : r.GetString(9),
            ErrorMessage: r.IsDBNull(10) ? null : r.GetString(10),
            CreatedAt: r.IsDBNull(11) ? string.Empty : r.GetString(11),
            UpdatedAt: r.IsDBNull(12) ? null : r.GetString(12),
            CommandRedacted: r.IsDBNull(13) ? null : r.GetString(13),
            CookFolder: r.IsDBNull(14) ? string.Empty : r.GetString(14),
            RecipeSnapshotJson: r.IsDBNull(15) ? null : r.GetString(15));
    }

    private static object? ReadCookFolderJson(string cookFolderAbs, string fileName)
    {
        if (string.IsNullOrEmpty(cookFolderAbs))
        {
            return null;
        }
        string p = Path.Combine(cookFolderAbs, fileName);
        if (!File.Exists(p))
        {
            return null;
        }
        string? raw = SafeReadAllText(p);
        return raw is null ? null : JsonModel.Parse(raw);
    }

    // Resolves a row's stored cook_folder to an absolute path and confirms it
    // lives under the workspace Cooks\ root. Anything that resolves outside that
    // managed root is rejected (returns empty), so the read surface can never be
    // steered to an arbitrary filesystem location.
    private static string ResolveCookFolderAbs(string workspacePath, string cookFolderRel)
    {
        if (string.IsNullOrEmpty(cookFolderRel))
        {
            return string.Empty;
        }

        string cooksRoot = Path.GetFullPath(CooksDir(workspacePath));
        string candidate = Path.GetFullPath(Path.IsPathRooted(cookFolderRel)
            ? cookFolderRel
            : Path.Combine(workspacePath, cookFolderRel));

        string normRoot = cooksRoot.EndsWith(Path.DirectorySeparatorChar)
            ? cooksRoot
            : cooksRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate, cooksRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        return candidate;
    }

    private static string? SafeReadAllText(string path)
    {
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader sr = new(fs, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static string JsonError(string code, string cookId) =>
        Encoding.UTF8.GetString(JsonModel.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["error"] = code,
            ["cookId"] = cookId,
        }, indented: false));
}
