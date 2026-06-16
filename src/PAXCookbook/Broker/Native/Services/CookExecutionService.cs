using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3e -- orchestrator for POST /api/v1/recipes/{ulid}/cook.
// Glues the discovery-phase services together in the same order as
// the PowerShell broker's Invoke-CookStart (Routes/Cooks.ps1 ~line
// 1262), but limited to the Stage 3e scope:
//
//   * Validate the ULID.
//   * Look up the recipe row (404 if missing or deleted).
//   * Load the recipe file (412 / 500 on malformed / missing).
//   * Verify the PAX script SHA-256 (500 pax_script_integrity).
//   * Refuse non-WebLogin/DeviceCode auth modes (501 deferred).
//   * Reject non-local-manual executionMode (412 recipe_invalid).
//   * Per-recipe concurrency guard (409 recipe_busy).
//   * Resolve the PAX invocation plan via the hidden adapter sidecar.
//   * Create the cook folder + 5 init files.
//   * INSERT the cook row.
//   * Spawn PAX via PaxProcessRunner; on exit UPDATE the cook row
//     terminal state.
//
// Out of scope for Stage 3e (each returns a deferred / clean refusal):
//   * Phase AF re-auth gate.
//   * Disk + path-length prechecks.
//   * App* + ManagedIdentity auth.
//   * Scheduler-origin observation row.
//   * WebSocket log streaming.
//   * Stop / kill / resume routes.
//   * cook_artifacts rollup.
//   * closure_reason / closure_evidence_json / abnormal_close_recorded_utc.
//
// The orchestrator returns CookStartOutcome -- a discriminated record
// that carries either the success body (201) or a controlled error
// envelope. The route caller maps each Status to the canonical HTTP
// shape so all error responses share one writer.
public sealed class CookExecutionService
{
    private readonly SqliteWorkspaceReader     _sqlite;
    private readonly CookRowWriter             _writer;
    private readonly RecipeFileReader          _recipes;
    private readonly PaxScriptIntegrityVerifier _integrity;
    private readonly PaxInvocationPlanProvider _adapter;
    private readonly CookFolderService         _folders;
    private readonly PaxProcessRunner          _runner;
    private readonly VersionInfo               _versionInfo;
    private readonly Func<DateTimeOffset>      _utcNow;
    private readonly Action<string, Exception?>? _errorSink;
    private readonly ICookProcessRegistry?     _registry;

    // Tracks running cooks the orchestrator itself spawned. Stage 3e
    // does NOT touch the cooks.status='running' column for the
    // concurrency check beyond what CookRowWriter.GetRunningCookIdForRecipe
    // returns -- the DB is the source of truth. This local cache is
    // a defense in depth so two concurrent requests on the same
    // recipe within the same broker instance always serialize via
    // the recipe-id lock even when the DB write hasn't been seen yet
    // by the second request's SELECT (the cook row is INSERTed BEFORE
    // we release the per-recipe lock).
    private readonly object _runningGate = new();
    private readonly HashSet<string> _runningRecipes = new(StringComparer.OrdinalIgnoreCase);

    public CookExecutionService(
        SqliteWorkspaceReader     sqlite,
        CookRowWriter             writer,
        RecipeFileReader          recipes,
        PaxScriptIntegrityVerifier integrity,
        PaxInvocationPlanProvider adapter,
        CookFolderService         folders,
        PaxProcessRunner          runner,
        VersionInfo               versionInfo,
        Func<DateTimeOffset>?     utcNow    = null,
        Action<string, Exception?>? errorSink = null,
        ICookProcessRegistry?     registry  = null)
    {
        _sqlite    = sqlite;
        _writer    = writer;
        _recipes   = recipes;
        _integrity = integrity;
        _adapter   = adapter;
        _folders   = folders;
        _runner    = runner;
        _versionInfo = versionInfo;
        _utcNow    = utcNow ?? (() => DateTimeOffset.UtcNow);
        _errorSink = errorSink;
        _registry  = registry;
    }

    public CookStartOutcome StartCook(string recipeId)
    {
        // 1. ULID format check (parity with $Script:RecipeIdPattern).
        if (!IsValidUlid(recipeId))
        {
            return CookStartOutcome.Error(CookStartErrors.InvalidRecipeId(
                "recipeId must be a 26-char Crockford ULID"));
        }

        // 2. Recipe row.
        var row = _sqlite.GetRecipeById(recipeId);
        if (row is null)
        {
            return CookStartOutcome.Error(CookStartErrors.RecipeNotFound(recipeId));
        }
        if (!string.IsNullOrEmpty(row.DeletedAt))
        {
            return CookStartOutcome.Error(CookStartErrors.RecipeNotFound(recipeId));
        }

        // 3. Recipe file.
        var loaded = _recipes.Load(recipeId);
        switch (loaded.Status)
        {
            case RecipeFileReadStatus.Missing:
                return CookStartOutcome.Error(CookStartErrors.RecipeFileMissing(recipeId));
            case RecipeFileReadStatus.Malformed:
                return CookStartOutcome.Error(CookStartErrors.RecipeInvalid(
                    loaded.Detail ?? "malformed"));
            case RecipeFileReadStatus.UnsupportedSchemaVersion:
                return CookStartOutcome.Error(CookStartErrors.RecipeInvalid(
                    loaded.Detail ?? "unsupported_schema_version",
                    path: "/recipeSchemaVersion"));
        }

        // 4. PAX script integrity (rehash + compare to VERSION.json).
        var integrity = _integrity.Verify();
        switch (integrity.Status)
        {
            case PaxIntegrityStatus.MissingScript:
                return CookStartOutcome.Error(CookStartErrors.PaxScriptMissing(
                    integrity.Detail ?? _integrity.PaxScriptPath));
            case PaxIntegrityStatus.NoBaseline:
                return CookStartOutcome.Error(CookStartErrors.VersionFileMissing(
                    integrity.Detail ?? "version_file_missing"));
            case PaxIntegrityStatus.HashFailed:
                return CookStartOutcome.Error(CookStartErrors.VersionFileMissing(
                    integrity.Detail ?? "hash_failed"));
            case PaxIntegrityStatus.Mismatch:
                return CookStartOutcome.Error(CookStartErrors.PaxScriptIntegrity(
                    integrity.Expected ?? string.Empty,
                    integrity.Actual ?? string.Empty));
        }

        // 5. executionMode gate. Parity with PS broker: manual cook
        // entry requires executionMode == 'local-manual'.
        if (!string.Equals(loaded.ExecutionMode, "local-manual",
                           StringComparison.OrdinalIgnoreCase))
        {
            return CookStartOutcome.Error(CookStartErrors.ExecutionModeRejected(
                loaded.ExecutionMode ?? string.Empty));
        }

        // 6. Auth-mode gate. Stage 3e supports the modes that DO NOT
        // require an auth_profile lookup (and therefore no Credential
        // Manager read): WebLogin and DeviceCode. App* and
        // ManagedIdentity return a controlled 501 sentinel so the
        // SPA can render "not yet implemented in native broker".
        if (IsAuthModeDeferred(loaded.AuthMode))
        {
            return CookStartOutcome.Error(CookStartErrors.AuthModeDeferred(
                loaded.AuthMode ?? "unknown"));
        }

        // 7. Per-recipe concurrency. Probe the DB and the in-memory
        // serialization set; both must say "not running".
        var existing = _writer.GetRunningCookIdForRecipe(recipeId);
        if (existing is not null)
        {
            return CookStartOutcome.Error(CookStartErrors.RecipeBusy(existing));
        }
        lock (_runningGate)
        {
            if (_runningRecipes.Contains(recipeId))
            {
                // Best-effort surface for in-flight cook -- the DB
                // INSERT hasn't committed yet so the SELECT above
                // misses it. We don't know the cook_id of the in-flight
                // cook from this side; return the recipe id placeholder
                // so the SPA can render "another cook started moments
                // ago".
                return CookStartOutcome.Error(CookStartErrors.RecipeBusy("pending"));
            }
            _runningRecipes.Add(recipeId);
        }

        try
        {
            return RunCook(recipeId, loaded.RawJson!);
        }
        finally
        {
            lock (_runningGate)
            {
                _runningRecipes.Remove(recipeId);
            }
        }
    }

    private CookStartOutcome RunCook(string recipeId, string recipeJson)
    {
        // 8. Adapter sidecar.
        var planResult = _adapter.Resolve(recipeJson, _integrity.PaxScriptPath);
        switch (planResult.Status)
        {
            case PaxInvocationPlanStatus.RecipeRejected:
                return CookStartOutcome.Error(CookStartErrors.RecipeInvalid(
                    planResult.Detail ?? "adapter_rejected"));
            case PaxInvocationPlanStatus.SidecarFailed:
                return CookStartOutcome.Error(CookStartErrors.AdapterSidecarFailed(
                    planResult.Detail ?? "sidecar_failed"));
        }
        var plan = planResult.Plan!;

        // 9. Cook id + folder + init files.
        var cookId = NewCookId();
        var createdAt = ToIso8601(_utcNow());
        var cookContext = CookFolderService.BuildCookContextJson(
            cookId:            cookId,
            recipeId:          recipeId,
            trigger:           "manual",
            createdAtUtc:      createdAt,
            cookbookVersion:   _versionInfo.CookbookVersion,
            bundledPaxVersion: _integrity.PaxScriptVersion,
            releaseChannel:    _versionInfo.ReleaseChannel,
            paxScriptSha256:   _integrity.ExpectedSha256,
            paxScriptPath:     _integrity.PaxScriptPath);
        var commandArgvFile = CookFolderService.BuildCommandArgvJson(plan);
        var spawnArgvCol    = CookFolderService.BuildSpawnArgvJson(plan.SpawnArgv);

        CookFolderLayout layout;
        try
        {
            layout = _folders.Create(recipeId, cookId, new CookInitFiles(
                RecipeSnapshotJson: recipeJson,
                CookContextJson:    cookContext,
                CommandText:        plan.PaxCommand,
                CommandArgvJson:    commandArgvFile));
        }
        catch (CookFolderException ex)
        {
            return CookStartOutcome.Error(
                ex.Message.StartsWith("cook_folder_create_failed", StringComparison.Ordinal)
                    ? CookStartErrors.CookFolderCreateFailed(ex.Message)
                    : CookStartErrors.CookInitFilesFailed(ex.Message));
        }

        // 10. INSERT the cook row in status='running'.
        var startedAt = ToIso8601(_utcNow());
        try
        {
            _writer.InsertCookRow(new CookInsertParams(
                CookId:              cookId,
                RecipeId:            recipeId,
                RecipeSnapshotJson:  recipeJson,
                CommandArgvJson:     spawnArgvCol,
                CommandArgvRedacted: spawnArgvCol,
                PaxScriptPath:       _integrity.PaxScriptPath,
                PaxScriptVersion:    _integrity.PaxScriptVersion ?? string.Empty,
                Trigger:             "manual",
                CookFolderRelative:  layout.WorkspaceRelative,
                Status:              "running",
                Pid:                 null,
                StartedAtUtc:        startedAt,
                CreatedAtUtc:        createdAt,
                UpdatedAtUtc:        startedAt));
        }
        catch (Exception ex)
        {
            _errorSink?.Invoke("cook_row_insert_failed: " + ex.Message, ex);
            return CookStartOutcome.Error(CookStartErrors.CookRowInsertFailed(ex.Message));
        }

        // 11. Spawn PAX. The spawn is awaited synchronously here so
        // the Stage 3e response shape mirrors the PS broker's 201
        // (which returns BEFORE the cook completes -- the PS broker
        // uses a ThreadJob). Stage 3e returns AFTER the cook
        // completes inside this request. This is an intentional
        // simplification for the parallel-implementation window;
        // Stage 3f will move the spawn off-thread and stream the log.
        var logPath = _folders.GetCookLogPath(layout.AbsolutePath);
        PaxRunResult runResult;
        CookLogWriter? log = null;
        // Stage 3j -- per-cook CTS so the cook process registry's
        // RequestStop / ForceKill delegates can signal the runner to
        // kill the pwsh tree. Cancellation = force kill in the
        // current runner (no cooperative stop wire); cooperative
        // semantics will land when Stage 3f moves the spawn
        // off-thread.
        var cookCts = new CancellationTokenSource();
        Action signalKill = () => { try { cookCts.Cancel(); } catch { } };
        // Register a placeholder handle so /stop and /kill find the
        // entry the instant the cook enters this block. The pid is
        // refreshed in onProcessStarted below.
        _registry?.Register(cookId, new CookProcessHandle(
            cookId:      cookId,
            processId:   0,
            requestStop: signalKill,
            forceKill:   signalKill));
        try
        {
            log = new CookLogWriter(logPath);
            runResult = _runner.RunAsync(
                plan,
                layout.AbsolutePath,
                log,
                cookCts.Token,
                onProcessStarted: realPid =>
                {
                    // Re-register with the real OS pid so /stop and
                    // /kill responses can report the pid the broker
                    // is signalling.
                    _registry?.Register(cookId, new CookProcessHandle(
                        cookId:      cookId,
                        processId:   realPid,
                        requestStop: signalKill,
                        forceKill:   signalKill));
                }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _errorSink?.Invoke("spawn_failed: " + ex.Message, ex);
            ApplyTerminalState(cookId, startedAt, PaxRunResult.SpawnFailed(ex.Message));
            return CookStartOutcome.Error(CookStartErrors.SupervisorSpawnFailed(ex.Message));
        }
        finally
        {
            if (log is not null)
            {
                log.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            _registry?.Deregister(cookId);
            cookCts.Dispose();
        }

        ApplyTerminalState(cookId, startedAt, runResult);

        if (runResult.Status == PaxRunStatus.SpawnFailed)
        {
            return CookStartOutcome.Error(CookStartErrors.SupervisorSpawnFailed(
                runResult.Detail ?? "spawn_failed"));
        }

        return CookStartOutcome.Success(new CookStartResponse(
            CookId:     cookId,
            RecipeId:   recipeId,
            CookFolder: layout.AbsolutePath));
    }

    private void ApplyTerminalState(string cookId, string startedAtUtc, PaxRunResult run)
    {
        string status;
        string? errorClass;
        string? errorMessage;
        int? exitCode;
        int? pid = run.Pid;
        DateTimeOffset finishedUtcRaw = run.FinishedUtc ?? _utcNow();
        double? duration = null;

        if (DateTimeOffset.TryParse(startedAtUtc, out var startParsed))
        {
            duration = Math.Round((finishedUtcRaw - startParsed).TotalSeconds, 3);
        }

        switch (run.Status)
        {
            case PaxRunStatus.Exited when run.ExitCode == 0:
                status      = "completed";
                errorClass  = null;
                errorMessage = null;
                exitCode    = 0;
                break;
            case PaxRunStatus.Exited:
                status      = "errored";
                errorClass  = "nonzero_exit";
                errorMessage = "exit_" + run.ExitCode;
                exitCode    = run.ExitCode;
                break;
            case PaxRunStatus.SpawnFailed:
            default:
                status      = "interrupted";
                errorClass  = "spawn_failed";
                errorMessage = run.Detail ?? "spawn_failed";
                exitCode    = null;
                break;
        }

        try
        {
            _writer.UpdateTerminalState(new CookTerminalUpdate(
                CookId:          cookId,
                Status:          status,
                ExitCode:        exitCode,
                Pid:             pid,
                FinishedAtUtc:   ToIso8601(finishedUtcRaw),
                DurationSeconds: duration,
                ErrorClass:      errorClass,
                ErrorMessage:    errorMessage,
                UpdatedAtUtc:    ToIso8601(_utcNow())));
        }
        catch (Exception ex)
        {
            _errorSink?.Invoke("terminal_update_failed: " + ex.Message, ex);
        }
    }

    private static string NewCookId() =>
        Guid.NewGuid().ToString().ToLowerInvariant();

    private static string ToIso8601(DateTimeOffset utc) =>
        utc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
            System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsValidUlid(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 26) return false;
        foreach (var c in s)
        {
            // Crockford alphabet: 0-9 A-H J K M N P-T V W X Y Z
            // (no I, L, O, U)
            if (c >= '0' && c <= '9') continue;
            var u = char.ToUpperInvariant(c);
            if (u == 'I' || u == 'L' || u == 'O' || u == 'U') return false;
            if (u >= 'A' && u <= 'Z') continue;
            return false;
        }
        return true;
    }

    private static bool IsAuthModeDeferred(string? mode)
    {
        if (string.IsNullOrEmpty(mode)) return false;
        return mode.StartsWith("AppRegistration", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("ManagedIdentity", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record CookStartOutcome(
    bool               IsSuccess,
    CookStartResponse? Response,
    CookStartError?    ErrorEnvelope)
{
    public static CookStartOutcome Success(CookStartResponse r) => new(true, r, null);
    public static CookStartOutcome Error(CookStartError e)      => new(false, null, e);
}
