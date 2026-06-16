using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Scheduled-task routes (X7a.3). Backs the three
// /api/v1/recipes/{id}/scheduled-task verbs:
//
//   PUT    -> persist the recipe's `schedule` block (enabled + recurrence +
//             scheduledTaskId + updatedAt) and register a per-user Windows
//             Scheduled Task whose action is the X7a.2 native one-shot
//             ("PAX Cookbook.exe --run-scheduled-recipe <id> --workspace <ws>
//             --approot <app>"). Scheduling is orthogonal to executionMode:
//             the recipe stays manually cookable; the PUT only writes the
//             schedule metadata and registers the OS task.
//   DELETE -> remove the OS task and clear the recipe's schedule block.
//   GET    -> read-only probe / drift report (stored schedule vs OS task).
//
// Decision-2 boundary (Brian-approved): ALL Windows Task Scheduler access is
// isolated to the single sanctioned PowerShell registrar
// (app/install/Register-PAXScheduledRecipe.ps1). This C# model NEVER calls a
// scheduler API directly; it shells the registrar via `Start-Process pwsh
// -File <registrar>` (System.Diagnostics.Process) and reads its exit code +
// bounded output. It uses no managed scheduler client library, no command-line
// task tool, and no COM scheduler service from C# -- every scheduler touch is
// delegated to the registrar.
//
// Constraint 14 (secrets never leak): every value this model writes or returns
// is metadata only -- a bool, a ULID task id, recurrence ints, a timestamp, an
// exit code, and a bounded registrar error keyword. The registrar argv carries
// NO secret (the bound Chef's Key is read from Windows Credential Manager at
// FIRE time by the broker, CK-3). No secret, tenant id, client id, certificate
// thumbprint, command line, or workspace path tail is ever placed in a route
// body or an error token.
//
// Persistence parity: the read-modify-write mirrors RecipeUpdateModel exactly
// (atomic temp + rename write, SHA-256 index hash, byte-level rollback) so a
// failed write or a failed OS registration never leaves a recipe corrupted or
// falsely marked scheduled.
internal static class RecipeScheduleModel
{
    private const int RegistrarTimeoutMs = 60000;

    private static string RecipeFilePath(string workspacePath, string recipeId) =>
        Path.Combine(workspacePath, "Recipes", recipeId + ".recipe.json");

    private static string DatabaseFile(string workspacePath) =>
        Path.Combine(workspacePath, "Database", "cookbook.sqlite");

    // -----------------------------------------------------------------
    // PUT /api/v1/recipes/{id}/scheduled-task
    // -----------------------------------------------------------------
    public static (int Status, object Body) PutScheduledTask(
        string workspacePath,
        string appRoot,
        string recipeId,
        object? requestBody,
        string? registrarPathOverride,
        string? taskFolderOverride,
        string? pwshOverride)
    {
        // a. recipe-id shape.
        if (!RecipeReadModel.IsValidRecipeId(recipeId))
        {
            return (400, new { error = "invalid_recipe_id", recipeId });
        }

        // b. load the stored recipe (404 when no row / soft-deleted / file missing).
        RecipeReadModel.PreviewLoadResult loaded = RecipeReadModel.LoadForPreview(workspacePath, recipeId);
        if (loaded.Status != 200 || loaded.Recipe is null)
        {
            return (loaded.Status, loaded.ErrorBody ?? new { error = "not_found", recipeId });
        }
        Dictionary<string, object?> recipe = loaded.Recipe;

        // c. validate the recurrence (same rules as the X7a.1 schedule gate and
        //    the registrar's Test-RecurrenceShape).
        Dictionary<string, object?>? recurrenceInput = ExtractRecurrence(requestBody);
        if (recurrenceInput is null)
        {
            return (400, new
            {
                error = "invalid_recurrence",
                recipeId,
                errors = new object[] { new { code = "recurrenceMissing", message = "A recurrence object is required." } },
            });
        }
        (bool recOk, Dictionary<string, object?>? normalizedRecurrence, List<object> recErrors) =
            ValidateRecurrence(recurrenceInput);
        if (!recOk || normalizedRecurrence is null)
        {
            return (400, new { error = "invalid_recurrence", recipeId, errors = recErrors });
        }

        // d. a schedule requires a bound + resolvable Chef's Key (all auth modes;
        //    Decision 1 / CK-2). Reuses the readiness CK resolution -- metadata
        //    only, never a secret.
        string? boundChefKeyId = GetBoundChefKeyId(recipe);
        if (string.IsNullOrWhiteSpace(boundChefKeyId) ||
            ChefKeyModel.ResolveForRecipe(boundChefKeyId) is null)
        {
            return (409, new
            {
                error = "schedule_requires_chef_key",
                recipeId,
                message = "Scheduling requires a bound Chef's Key. Bind a Chef's Key to this recipe, then enable scheduling.",
            });
        }

        // e. scheduledTaskId: reuse the recipe's existing one (PUT-as-update) or
        //    mint a fresh ULID.
        RecipeReadModel.ScheduleInfo? existingSchedule = RecipeReadModel.ProjectSchedule(recipe);
        string scheduledTaskId =
            existingSchedule?.ScheduledTaskId is string sid && RecipeReadModel.IsValidRecipeId(sid)
                ? sid
                : NewUlid();

        // f. read-modify-write: stamp the schedule block, RE-VALIDATE the whole
        //    recipe, persist atomically. On validation failure, never persist.
        string now = UtcNowIso();
        recipe["updatedAt"] = now;
        recipe["schedule"] = BuildScheduleBlock(enabled: true, scheduledTaskId, normalizedRecurrence, now);

        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(recipe);
        if (!ok)
        {
            return (412, new { error = "recipe_invalid", recipeId, errors });
        }

        string finalPath = RecipeFilePath(workspacePath, recipeId);
        byte[]? oldBytes = File.Exists(finalPath) ? File.ReadAllBytes(finalPath) : null;
        RowState? oldRow = GetRowState(workspacePath, recipeId);

        try
        {
            string newHash = WriteRecipeFile(finalPath, recipe);
            UpdateRecipeRow(workspacePath, recipeId, newHash, now);
        }
        catch (Exception ex)
        {
            RestoreRecipe(finalPath, oldBytes, workspacePath, recipeId, oldRow);
            return (500, new { error = "schedule_persist_failed", recipeId, detail = ex.Message });
        }

        // g. register the OS task. On any non-zero exit (or launch failure), ROLL
        //    BACK the schedule write so the recipe is never falsely scheduled.
        RegistrarResult reg = RunRegistrar(
            "register", workspacePath, recipeId, scheduledTaskId,
            EncodeRecurrenceJson(normalizedRecurrence),
            registrarPathOverride, taskFolderOverride, pwshOverride, appRoot);

        if (reg.ExitCode != 0)
        {
            RestoreRecipe(finalPath, oldBytes, workspacePath, recipeId, oldRow);
            return (reg.Launched ? 502 : 500, new
            {
                error = "schedule_registration_failed",
                recipeId,
                registrarExitCode = reg.ExitCode,
                token = reg.ErrorToken,
            });
        }

        return (200, new
        {
            recipeId,
            schedule = BuildScheduleBlock(enabled: true, scheduledTaskId, normalizedRecurrence, now),
        });
    }

    // -----------------------------------------------------------------
    // DELETE /api/v1/recipes/{id}/scheduled-task
    // -----------------------------------------------------------------
    public static (int Status, object Body) DeleteScheduledTask(
        string workspacePath,
        string appRoot,
        string recipeId,
        string? registrarPathOverride,
        string? taskFolderOverride,
        string? pwshOverride)
    {
        if (!RecipeReadModel.IsValidRecipeId(recipeId))
        {
            return (400, new { error = "invalid_recipe_id", recipeId });
        }

        RecipeReadModel.PreviewLoadResult loaded = RecipeReadModel.LoadForPreview(workspacePath, recipeId);
        if (loaded.Status != 200 || loaded.Recipe is null)
        {
            return (loaded.Status, loaded.ErrorBody ?? new { error = "not_found", recipeId });
        }
        Dictionary<string, object?> recipe = loaded.Recipe;

        RecipeReadModel.ScheduleInfo? existing = RecipeReadModel.ProjectSchedule(recipe);
        if (existing is null)
        {
            // Idempotent: nothing scheduled -> nothing to remove, no registrar call.
            return (200, new { recipeId, schedule = (object?)null });
        }

        // Unregister FIRST. On a hard failure, leave the schedule block intact so
        // stored state and OS state stay consistent.
        string scheduledTaskId = existing.ScheduledTaskId ?? string.Empty;
        RegistrarResult reg = RunRegistrar(
            "unregister", workspacePath, recipeId, scheduledTaskId,
            recurrenceJson: null,
            registrarPathOverride, taskFolderOverride, pwshOverride, appRoot);

        if (reg.ExitCode != 0)
        {
            return (reg.Launched ? 502 : 500, new
            {
                error = "schedule_unregistration_failed",
                recipeId,
                registrarExitCode = reg.ExitCode,
                token = reg.ErrorToken,
            });
        }

        // Success (incl. the registrar's no_task_present exit 0): remove the
        // schedule block, re-validate, persist.
        string now = UtcNowIso();
        recipe.Remove("schedule");
        recipe["updatedAt"] = now;

        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(recipe);
        if (!ok)
        {
            return (412, new { error = "recipe_invalid", recipeId, errors });
        }

        string finalPath = RecipeFilePath(workspacePath, recipeId);
        try
        {
            string newHash = WriteRecipeFile(finalPath, recipe);
            UpdateRecipeRow(workspacePath, recipeId, newHash, now);
        }
        catch (Exception ex)
        {
            return (500, new { error = "schedule_persist_failed", recipeId, detail = ex.Message });
        }

        return (200, new { recipeId, schedule = (object?)null });
    }

    // -----------------------------------------------------------------
    // GET /api/v1/recipes/{id}/scheduled-task  (read-only probe / drift)
    // -----------------------------------------------------------------
    public static (int Status, object Body) GetScheduledTask(
        string workspacePath,
        string appRoot,
        string recipeId,
        string? registrarPathOverride,
        string? taskFolderOverride,
        string? pwshOverride)
    {
        if (!RecipeReadModel.IsValidRecipeId(recipeId))
        {
            return (400, new { error = "invalid_recipe_id", recipeId });
        }

        RecipeReadModel.PreviewLoadResult loaded = RecipeReadModel.LoadForPreview(workspacePath, recipeId);
        if (loaded.Status != 200 || loaded.Recipe is null)
        {
            return (loaded.Status, loaded.ErrorBody ?? new { error = "not_found", recipeId });
        }
        RecipeReadModel.ScheduleInfo? stored = RecipeReadModel.ProjectSchedule(loaded.Recipe);

        // Probe the OS task (read-only). Never throws: a probe error is surfaced
        // as a bounded field with present=false.
        RegistrarResult reg = RunRegistrar(
            "probe", workspacePath, recipeId, scheduledTaskId: string.Empty,
            recurrenceJson: null,
            registrarPathOverride, taskFolderOverride, pwshOverride, appRoot);

        object osTask = BuildOsTaskProjection(reg, out bool osPresent);
        bool storedEnabled = stored?.Enabled ?? false;

        return (200, new
        {
            recipeId,
            schedule = ScheduleInfoToBody(stored),
            osTask,
            drift = storedEnabled != osPresent,
        });
    }

    // =================================================================
    // Recurrence extraction + validation
    // =================================================================

    // Accepts either a bare recurrence object ({kind,hour,minute,daysOfWeek?})
    // or a wrapper carrying a `recurrence` child object. Returns null when the
    // body is not a JSON object.
    private static Dictionary<string, object?>? ExtractRecurrence(object? requestBody)
    {
        if (requestBody is not Dictionary<string, object?> body)
        {
            return null;
        }
        if (body.TryGetValue("recurrence", out object? rObj) && rObj is Dictionary<string, object?> r)
        {
            return r;
        }
        return body;
    }

    // Mirrors RecipeValidationModel.ScheduleShapeGate's recurrence rules and the
    // registrar's Test-RecurrenceShape: daily|weekly, hour [0,23], minute [0,59];
    // weekly requires 1..7 unique days each in [0,6]. Returns a NORMALIZED
    // recurrence (kind lower-cased, hour/minute/daysOfWeek as `long` so the
    // schema walker's "integer" check accepts the injected schedule block;
    // weekly daysOfWeek deduped + sorted).
    private static (bool Ok, Dictionary<string, object?>? Normalized, List<object> Errors) ValidateRecurrence(
        Dictionary<string, object?> r)
    {
        var errors = new List<object>();

        string kind = r.TryGetValue("kind", out object? k) ? JsonModel.Str(k) : string.Empty;
        bool isDaily = string.Equals(kind, "daily", StringComparison.OrdinalIgnoreCase);
        bool isWeekly = string.Equals(kind, "weekly", StringComparison.OrdinalIgnoreCase);
        if (!isDaily && !isWeekly)
        {
            errors.Add(new { code = "recurrenceKindInvalid", message = "recurrence.kind must be 'daily' or 'weekly'." });
        }

        int hour = 0;
        bool hourOk = r.TryGetValue("hour", out object? h) && JsonModel.TryInt(h, out hour) && hour >= 0 && hour <= 23;
        if (!hourOk)
        {
            errors.Add(new { code = "recurrenceHourOutOfRange", message = "recurrence.hour must be an integer in [0, 23]." });
        }

        int minute = 0;
        bool minOk = r.TryGetValue("minute", out object? m) && JsonModel.TryInt(m, out minute) && minute >= 0 && minute <= 59;
        if (!minOk)
        {
            errors.Add(new { code = "recurrenceMinuteOutOfRange", message = "recurrence.minute must be an integer in [0, 59]." });
        }

        long[]? days = null;
        if (isWeekly)
        {
            if (!r.TryGetValue("daysOfWeek", out object? d) || d is not List<object?> dayList)
            {
                errors.Add(new { code = "weeklyDaysOfWeekMissing", message = "recurrence.daysOfWeek (1-7 entries, 0=Sunday..6=Saturday) is required for a weekly schedule." });
            }
            else
            {
                var set = new SortedSet<int>();
                bool anyBad = false;
                foreach (object? item in dayList)
                {
                    if (JsonModel.TryInt(item, out int di) && di >= 0 && di <= 6)
                    {
                        set.Add(di);
                    }
                    else
                    {
                        anyBad = true;
                    }
                }
                if (anyBad || set.Count < 1 || set.Count > 7)
                {
                    errors.Add(new { code = "weeklyDaysOfWeekInvalid", message = "recurrence.daysOfWeek must contain 1-7 unique integers in [0, 6]." });
                }
                else
                {
                    days = set.Select(x => (long)x).ToArray();
                }
            }
        }

        if (errors.Count > 0)
        {
            return (false, null, errors);
        }

        var normalized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["kind"] = isDaily ? "daily" : "weekly",
            ["hour"] = (long)hour,
            ["minute"] = (long)minute,
        };
        if (isWeekly && days is not null)
        {
            normalized["daysOfWeek"] = days.Select(x => (object?)x).ToList();
        }
        return (true, normalized, errors);
    }

    // Compact JSON for the registrar's -RecurrenceJson argument. The longs
    // serialize as JSON integers, which Test-RecurrenceShape reads as ints.
    private static string EncodeRecurrenceJson(Dictionary<string, object?> recurrence) =>
        Encoding.UTF8.GetString(JsonModel.SerializeToUtf8Bytes(recurrence));

    // =================================================================
    // Schedule block + response projections (metadata only, secret-free)
    // =================================================================

    private static Dictionary<string, object?> BuildScheduleBlock(
        bool enabled, string scheduledTaskId, Dictionary<string, object?> normalizedRecurrence, string updatedAt) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = enabled,
            ["scheduledTaskId"] = scheduledTaskId,
            ["recurrence"] = CloneRecurrence(normalizedRecurrence),
            ["updatedAt"] = updatedAt,
        };

    // A fresh copy so the persisted recipe tree and the response body never alias
    // the same nested dictionary.
    private static Dictionary<string, object?> CloneRecurrence(Dictionary<string, object?> rec)
    {
        var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["kind"] = rec.TryGetValue("kind", out object? k) ? k : null,
            ["hour"] = rec.TryGetValue("hour", out object? h) ? h : null,
            ["minute"] = rec.TryGetValue("minute", out object? m) ? m : null,
        };
        if (rec.TryGetValue("daysOfWeek", out object? d) && d is List<object?> days)
        {
            copy["daysOfWeek"] = new List<object?>(days);
        }
        return copy;
    }

    private static object? ScheduleInfoToBody(RecipeReadModel.ScheduleInfo? s)
    {
        if (s is null)
        {
            return null;
        }
        var recurrence = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["kind"] = s.Kind,
            ["hour"] = (long)s.Hour,
            ["minute"] = (long)s.Minute,
        };
        if (s.DaysOfWeek is not null)
        {
            recurrence["daysOfWeek"] = s.DaysOfWeek.Select(x => (object?)(long)x).ToList();
        }
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = s.Enabled,
            ["scheduledTaskId"] = s.ScheduledTaskId,
            ["recurrence"] = recurrence,
            ["updatedAt"] = s.UpdatedAt,
        };
    }

    // Builds the bounded OS-task projection from a probe RegistrarResult. NEVER
    // echoes the raw exe path or the full argument string: the action is reduced
    // to a single secret-free boolean (`actionTargetsRunner`) proving it targets
    // the native one-shot. Never throws.
    private static object BuildOsTaskProjection(RegistrarResult reg, out bool present)
    {
        present = false;

        if (reg.ExitCode != 0)
        {
            return new
            {
                present = false,
                probeError = reg.ErrorToken ?? "probe_failed",
            };
        }

        object? parsed;
        try
        {
            parsed = JsonModel.Parse(reg.Stdout.Trim());
        }
        catch
        {
            return new { present = false, probeError = "probe_parse_failed" };
        }
        if (parsed is not Dictionary<string, object?> doc)
        {
            return new { present = false, probeError = "probe_parse_failed" };
        }

        bool exists = doc.TryGetValue("exists", out object? e) && e is bool eb && eb;
        present = exists;

        string? state = doc.TryGetValue("state", out object? st) && st is string ss ? ss : null;
        bool? enabled = doc.TryGetValue("enabled", out object? en) && en is bool enb ? enb : null;
        string? probeError = doc.TryGetValue("probeError", out object? pe) && pe is string pes ? pes : null;
        string? nextRunTime = doc.TryGetValue("nextRunTime", out object? nr) && nr is string nrs ? nrs : null;
        string? lastRunTime = doc.TryGetValue("lastRunTime", out object? lr) && lr is string lrs ? lrs : null;
        int? lastTaskResult =
            doc.TryGetValue("lastTaskResult", out object? lt) && JsonModel.TryInt(lt, out int ltv) ? ltv : null;

        bool actionTargetsRunner = false;
        if (doc.TryGetValue("action", out object? a) && a is Dictionary<string, object?> action)
        {
            string execute = action.TryGetValue("execute", out object? ex) && ex is string exs ? exs : string.Empty;
            string arguments = action.TryGetValue("arguments", out object? ar) && ar is string ars ? ars : string.Empty;
            actionTargetsRunner =
                execute.EndsWith("PAX Cookbook.exe", StringComparison.OrdinalIgnoreCase) &&
                arguments.Contains("--run-scheduled-recipe", StringComparison.OrdinalIgnoreCase);
        }

        object trigger = ProjectProbeTrigger(doc);

        return new
        {
            present = exists,
            state,
            enabled,
            trigger,
            actionTargetsRunner,
            nextRunTime,
            lastRunTime,
            lastTaskResult,
            probeError,
        };
    }

    private static object ProjectProbeTrigger(Dictionary<string, object?> doc)
    {
        if (doc.TryGetValue("trigger", out object? t) && t is Dictionary<string, object?> trig)
        {
            string kind = trig.TryGetValue("kind", out object? k) && k is string ks ? ks : "other";
            int? hour = trig.TryGetValue("hour", out object? h) && JsonModel.TryInt(h, out int hv) ? hv : null;
            int? minute = trig.TryGetValue("minute", out object? m) && JsonModel.TryInt(m, out int mv) ? mv : null;
            List<object?>? days = null;
            if (trig.TryGetValue("daysOfWeek", out object? d) && d is List<object?> dl)
            {
                days = new List<object?>(dl);
            }
            return new { kind, hour, minute, daysOfWeek = days };
        }
        return new { kind = "other", hour = (int?)null, minute = (int?)null, daysOfWeek = (List<object?>?)null };
    }

    private static string? GetBoundChefKeyId(Dictionary<string, object?> recipe)
    {
        if (recipe.TryGetValue("auth", out object? authObj) &&
            authObj is Dictionary<string, object?> auth &&
            auth.TryGetValue("chefKeyId", out object? ck) &&
            ck is string s && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }
        return null;
    }

    // =================================================================
    // Registrar invocation (the ONLY scheduler touch -- via Start-Process pwsh)
    // =================================================================

    private readonly record struct RegistrarResult(
        bool Launched, int ExitCode, string? ErrorToken, string Stdout, string Stderr);

    private static RegistrarResult RunRegistrar(
        string action,
        string workspacePath,
        string recipeId,
        string scheduledTaskId,
        string? recurrenceJson,
        string? registrarPathOverride,
        string? taskFolderOverride,
        string? pwshOverride,
        string appRoot)
    {
        string registrar = string.IsNullOrWhiteSpace(registrarPathOverride)
            ? Path.Combine(appRoot, "install", "Register-PAXScheduledRecipe.ps1")
            : registrarPathOverride!;
        string pwsh = string.IsNullOrWhiteSpace(pwshOverride) ? "pwsh" : pwshOverride!;

        var psi = new ProcessStartInfo
        {
            FileName = pwsh,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = appRoot,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(registrar);
        psi.ArgumentList.Add("-Action");
        psi.ArgumentList.Add(action);

        bool mutating = action is "register" or "unregister";
        if (mutating)
        {
            // register / unregister require a workspace; probe does not.
            psi.ArgumentList.Add("-WorkspacePath");
            psi.ArgumentList.Add(workspacePath);
        }
        psi.ArgumentList.Add("-RecipeId");
        psi.ArgumentList.Add(recipeId);
        if (mutating)
        {
            psi.ArgumentList.Add("-ScheduledTaskId");
            psi.ArgumentList.Add(scheduledTaskId);
        }
        if (action == "register" && recurrenceJson is not null)
        {
            psi.ArgumentList.Add("-RecurrenceJson");
            psi.ArgumentList.Add(recurrenceJson);
        }
        if (!string.IsNullOrWhiteSpace(taskFolderOverride))
        {
            psi.ArgumentList.Add("-TaskFolderOverride");
            psi.ArgumentList.Add(taskFolderOverride!);
        }

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
            {
                return new RegistrarResult(false, -1, "process_start_returned_null", string.Empty, string.Empty);
            }

            // Drain both pipes concurrently to avoid a child-write deadlock.
            Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(RegistrarTimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return new RegistrarResult(true, -2, "registrar_timeout", string.Empty, string.Empty);
            }
            proc.WaitForExit(); // ensure the async readers complete

            string stdout = Bound(outTask.GetAwaiter().GetResult());
            string stderr = Bound(errTask.GetAwaiter().GetResult());
            int exit = proc.ExitCode;
            return new RegistrarResult(
                true, exit, exit == 0 ? null : ExtractToken(stderr, stdout), stdout, stderr);
        }
        catch (Exception)
        {
            return new RegistrarResult(false, -1, "registrar_launch_failed", string.Empty, string.Empty);
        }
        finally
        {
            proc?.Dispose();
        }
    }

    // The registrar writes errors as "[Registrar] <token>: <message>". Extract
    // ONLY the leading keyword token (e.g. register_failed, exe_missing,
    // recurrence_invalid) -- never the message tail, never a path / HRESULT /
    // command line (constraint 14).
    private static string ExtractToken(string stderr, string stdout) =>
        FindToken(stderr) ?? FindToken(stdout) ?? "registrar_failed";

    private static string? FindToken(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        const string marker = "[Registrar]";
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }
            string rest = line.Substring(idx + marker.Length).Trim();
            if (rest.Length == 0)
            {
                continue;
            }
            int colon = rest.IndexOf(':');
            string token = (colon >= 0 ? rest.Substring(0, colon) : rest).Trim();
            if (token.Length == 0)
            {
                continue;
            }
            return token.Length > 64 ? token.Substring(0, 64) : token;
        }
        return null;
    }

    private static string Bound(string s) => s.Length <= 16384 ? s : s.Substring(0, 16384);

    // =================================================================
    // Persistence (mirrors RecipeUpdateModel: atomic write + index hash +
    // byte-level rollback)
    // =================================================================

    private readonly record struct RowState(string? FileHash, string? UpdatedAt);

    private static RowState? GetRowState(string workspacePath, string recipeId)
    {
        string dbFile = DatabaseFile(workspacePath);
        if (!File.Exists(dbFile))
        {
            return null;
        }
        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = dbFile,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            using var conn = new SqliteConnection(csb.ConnectionString);
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT file_hash, updated_at FROM recipes WHERE recipe_id = $id;";
            cmd.Parameters.AddWithValue("$id", recipeId);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }
            string? fileHash = reader.IsDBNull(0) ? null : reader.GetString(0);
            string? updatedAt = reader.IsDBNull(1) ? null : reader.GetString(1);
            return new RowState(fileHash, updatedAt);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    // Write-temp + atomic rename so a concurrent reader never sees a half-written
    // file. Returns the SHA-256 hash (hex, lowercase) of the final bytes.
    private static string WriteRecipeFile(string finalPath, Dictionary<string, object?> recipe)
    {
        byte[] bytes = JsonModel.SerializeToUtf8Bytes(recipe);
        string tempPath = finalPath + ".tmp";

        File.WriteAllBytes(tempPath, bytes);
        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }
        File.Move(tempPath, finalPath);

        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // UPDATE the index row's file_hash + updated_at (status / name unchanged --
    // a schedule edit changes neither). The recipe document is authoritative.
    private static void UpdateRecipeRow(string workspacePath, string recipeId, string fileHash, string updatedAt)
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
            "UPDATE recipes SET file_hash = $file_hash, updated_at = $updated_at " +
            "WHERE recipe_id = $recipe_id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$file_hash", fileHash);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);
        cmd.Parameters.AddWithValue("$recipe_id", recipeId);
        cmd.ExecuteNonQuery();
    }

    // Restores the recipe file + index row to their pre-write state. Used when a
    // persist exception or a failed OS registration must leave the recipe exactly
    // as it was (never falsely scheduled).
    private static void RestoreRecipe(
        string finalPath, byte[]? oldBytes, string workspacePath, string recipeId, RowState? oldRow)
    {
        try
        {
            if (oldBytes is null)
            {
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }
            }
            else
            {
                File.WriteAllBytes(finalPath, oldBytes);
            }
        }
        catch
        {
            // Best-effort file restore.
        }

        if (oldRow is { FileHash: { } fh, UpdatedAt: { } ua })
        {
            try
            {
                UpdateRecipeRow(workspacePath, recipeId, fh, ua);
            }
            catch
            {
                // Best-effort row restore.
            }
        }
    }

    // =================================================================
    // Small shared helpers (kept local; mirror RecipeCreateModel)
    // =================================================================

    private static string UtcNowIso() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static readonly char[] UlidAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();

    // 128-bit ULID (Crockford base32, 26 chars): 48-bit ms-since-epoch (10 chars)
    // + 80-bit randomness (16 chars). Matches RecipeCreateModel.NewRecipeId so a
    // scheduledTaskId satisfies the same ULID pattern.
    private static string NewUlid()
    {
        long msSinceEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var tsChars = new char[10];
        long v = msSinceEpoch;
        for (int i = 9; i >= 0; i--)
        {
            tsChars[i] = UlidAlphabet[(int)(v & 0x1F)];
            v >>= 5;
        }

        byte[] randBytes = new byte[10];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randBytes);
        }

        var rndChars = new char[16];
        long bitBuf = 0;
        int bitCount = 0;
        int outIdx = 0;
        foreach (byte b in randBytes)
        {
            bitBuf = (bitBuf << 8) | b;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                int idx = (int)((bitBuf >> bitCount) & 0x1F);
                rndChars[outIdx++] = UlidAlphabet[idx];
            }
        }

        return new string(tsChars) + new string(rndChars);
    }
}
