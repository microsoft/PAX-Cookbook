using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Native cook-start PREPARATION pipeline (X15). Ports the oracle
// Invoke-CookStart gate chain (app\broker\Routes\Cooks.ps1) up to — but
// deliberately NOT including — the child-process spawn. This model:
//   * validates the cook-start gate chain in oracle order,
//   * projects the PAX invocation plan (pure string projection),
//   * writes the pre-spawn cook-folder files atomically, and
//   * inserts the cook index row (status='running', started_at NULL).
// It NEVER starts the PAX engine, NEVER invokes pwsh/PowerShell, NEVER reads or
// mutates the managed engine bytes, and NEVER reads an auth-profile secret. The
// actual child invocation + supervisor land in a later slice (X16).
internal static partial class RecipeReadModel
{
    // Oracle: $Script:M1_CookContextSchemaVer (Routes\Cooks.ps1).
    private const int CookContextSchemaVersion = 1;

    // Oracle: $Script:MinFreeDiskBytesForCook = 500MB (Start-Broker.ps1).
    public const long DefaultMinFreeDiskBytesForCook = 524288000L;

    private static string CooksDir(string workspacePath) => Path.Combine(workspacePath, "Cooks");

    // Oracle: New-CookId -> [guid]::NewGuid().ToString().ToLowerInvariant().
    private static string NewCookId() => Guid.NewGuid().ToString().ToLowerInvariant();

    // Oracle: Get-UtcNowIso.
    private static string CookUtcNowIso() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    // Native cook-start preparation. Returns (httpStatus, body). The recipe-id
    // format is re-validated here (gate 1) so the model owns the whole chain.
    //
    //   persist == false : public no-child path. Runs the read-only gate chain
    //                      (recipe -> acquisition -> busy) and then terminates
    //                      with a bounded 501 cook_child_not_implemented_x15.
    //                      No cook folder, no files, no row, no spawn.
    //   persist == true  : test-only preparation seam (CLI --test-seam-cook-prepare).
    //                      Continues through the pre-spawn prep gates, writes the
    //                      cook-folder files and the cook row, and returns a
    //                      bounded test-only 200 cook_prepared_no_child. Still no
    //                      spawn, no started_at, no fabricated bake.
    public static (int Status, object Body) PrepareCookStart(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        string recipeId,
        bool persist,
        string method,
        string requestPath,
        long minFreeDiskBytes)
    {
        CookGateOutcome gate = EvaluateCookGatesThroughBusy(
            workspacePath, engine, recipeId, method, requestPath);
        if (!gate.Passed)
        {
            return (gate.Status, gate.Body!);
        }
        Dictionary<string, object?> recipe = gate.Recipe!;

        // --- No-child terminal boundary -------------------------------------
        // Gate 10 (per-operation fresh Windows Hello, OpClass 'manualCook') and
        // the child spawn are NOT performed on this preparation path. The public
        // route stops here with a bounded 501 rather than faking a Verified
        // verdict or a successful bake; the real child invocation lives on the
        // X16 StartManualCook path.
        if (!persist)
        {
            return (501, new
            {
                error = "cook_child_not_implemented_x15",
                message = "Cook-start preparation is validated, but launching the PAX engine child process is not implemented in the X15 no-child slice.",
                slice = "V1_OFFICE_GRADE_X15_COOK_START_PIPELINE_NO_CHILD",
            });
        }

        // --- Test-only preparation seam (persist == true) -------------------
        // Gate 10 re-auth is intentionally bypassed on this seam: there is no
        // production WebAuthn ceremony in the smoke harness, and faking a
        // Verified verdict is forbidden. The seam is unreachable in normal
        // runtime (the desktop launcher never passes --test-seam-cook-prepare).
        (int? prepStatus, object? prepBody, PreparedCook prepared) = PrepareCookArtifacts(
            workspacePath, versionInfo, engine, recipe, recipeId, minFreeDiskBytes, CookKind.Manual);
        if (prepStatus.HasValue)
        {
            return (prepStatus.Value, prepBody!);
        }

        // Bounded test-only success: the cook is PREPARED, never started.
        return (200, new
        {
            result = "cook_prepared_no_child",
            cookId = prepared.CookId,
            recipeId,
            cookFolder = prepared.CookFolderRel,
        });
    }

    // Result of the read-only cook-start gate chain (gates 1..9). When Passed is
    // true the validated recipe tree is carried in Recipe; otherwise (Status,
    // Body) is the bounded early response.
    private readonly record struct CookGateOutcome(
        bool Passed, int Status, object? Body, Dictionary<string, object?>? Recipe);

    // Gates 1..9 of the oracle Invoke-CookStart chain: recipe-id format, index
    // row presence, on-disk parse/presence, full recipe validation, approved
    // managed engine integrity, and the per-recipe concurrency refusal. Pure
    // read-only: no folder, no row, no spawn.
    private static CookGateOutcome EvaluateCookGatesThroughBusy(
        string workspacePath,
        EngineAcquisitionResult engine,
        string recipeId,
        string method,
        string requestPath)
    {
        // Gate 1 — recipe-id format.
        if (!IsValidRecipeId(recipeId))
        {
            return new CookGateOutcome(false, 400, new { error = "invalid_recipe_id", recipeId }, null);
        }

        // Gate 2 — index row exists and is not soft-deleted.
        IReadOnlyDictionary<string, object?>? row = GetRecipeRow(workspacePath, recipeId);
        if (row is null || row["deleted_at"] is not null)
        {
            return new CookGateOutcome(false, 404, new { error = "not_found", recipeId }, null);
        }

        // Gates 3/4 — load the on-disk recipe document (parse / presence).
        RecipeTreeLoad load = LoadRecipeTree(workspacePath, recipeId);
        switch (load.Status)
        {
            case "missing":
                return new CookGateOutcome(false, 500, new { error = "recipe_file_missing", recipeId }, null);
            case "malformed":
                return new CookGateOutcome(false, 412, new
                {
                    error = "recipe_invalid",
                    recipeId,
                    errors = new object[]
                    {
                        new { path = "", message = "on-disk recipe is unparseable JSON: " + (load.Detail ?? string.Empty) },
                    },
                }, null);
            case "unsupported_schema_version":
                return new CookGateOutcome(false, 412, new
                {
                    error = "recipe_invalid",
                    recipeId,
                    errors = new object[]
                    {
                        new
                        {
                            path = "/recipeSchemaVersion",
                            keyword = "unsupportedSchemaVersion",
                            message = "on-disk recipe schema version is not supported: " + (load.Detail ?? string.Empty),
                        },
                    },
                }, null);
        }

        Dictionary<string, object?>? recipe = load.Recipe;
        if (recipe is null)
        {
            // Defensive: an "ok" status with no tree should not happen, but never
            // fabricate a cook from a null recipe.
            return new CookGateOutcome(false, 500, new { error = "recipe_file_missing", recipeId }, null);
        }

        // Gate 5 — full recipe validation (oracle Test-RecipeAll).
        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(recipe);
        if (!ok)
        {
            return new CookGateOutcome(false, 412, new { error = "recipe_invalid", recipeId, errors }, null);
        }

        // Gate 6 — approved managed engine present + byte-integrity. The native
        // EngineAcquisition.Resolve re-hashes the managed engine and only reports
        // IsAcquired when the file is present AND matches the recorded approved
        // hash, so this single gate covers the oracle's separate engine-presence
        // (gate 7) and per-cook re-hash (gate 8) pax_script_integrity branches. A
        // failure here surfaces the X13-compatible 409 acquisitionRequired the
        // SPA engine-acquisition overlay renders on.
        if (!engine.IsAcquired)
        {
            return new CookGateOutcome(false, 409, engine.ToGate409Body(method, requestPath), null);
        }

        // Gate 9 — refuse a concurrent cook for the same recipe.
        string? runningCookId = GetRunningCookIdForRecipe(workspacePath, recipeId);
        if (runningCookId is not null)
        {
            return new CookGateOutcome(false, 409, new { error = "recipe_busy", recipeId, cookId = runningCookId }, null);
        }

        return new CookGateOutcome(true, 0, null, recipe);
    }

    // Pre-spawn cook artifacts produced by gates 11..18: the cook id, the
    // absolute + workspace-relative cook folder, the frozen PAX invocation plan,
    // the creation timestamp, the resolved (secret-free) Chef's Key the
    // supervisor injects as child-only GRAPH_* credentials at spawn time (CK-3),
    // and whether the recipe's auth mode needs an interactive console window for
    // the child (WebLogin's MSAL/WAM browser sign-in needs a parent HWND, so its
    // cook is spawned with CreateNoWindow=false; every other / unbound mode runs
    // headless). CredentialInjection is null for interactive / unbound recipes;
    // it never carries the secret itself.
    private readonly record struct PreparedCook(
        string CookId,
        string CookFolderAbs,
        string CookFolderRel,
        PaxAdapter.InvocationPlan Plan,
        string CreatedAt,
        ChefKeyModel.ChefKeyResolved? CredentialInjection,
        bool RequiresInteractiveWindow);

    // Whether a recipe auth mode needs an interactive console window allocated
    // for the PAX child. WebLogin performs interactive MSAL/WAM browser sign-in,
    // which requires a parent window handle; spawning it headless
    // (CreateNoWindow=true) makes MSAL fail with "A window handle must be
    // configured" and PAX self-exits without authenticating. DeviceCode (CK-4
    // relays the code, no browser), App-registration cert/secret (unattended),
    // and any unknown / empty mode all run headless. Used both by the supervisor
    // (via PreparedCook) and the --test-seam-cook-window-decision seam.
    internal static bool RequiresInteractiveWindowForAuthMode(string? authMode) =>
        string.Equals(authMode, "WebLogin", StringComparison.OrdinalIgnoreCase);

    // Gates 11..18 of the oracle Invoke-CookStart chain: disk floor, MAX_PATH
    // budget, manual-entry execution-mode, auth-profile resolution (NO secret
    // read), PAX plan projection, cook-folder creation, pre-spawn file writes,
    // and the cook index row insert. Returns (null, null, prepared) on success;
    // otherwise the bounded early (status, body) with a default PreparedCook.
    //
    // The CookKind threads two kind-specific differences through preparation:
    //   * gate 13 (execution-mode) is enforced for Manual cooks only — a Scheduled
    //     cook coexists with any executionMode (it was already authorized by its
    //     enabled schedule), so it is never rejected on executionMode;
    //   * the recorded cook trigger ("manual" vs "scheduled") is written into both
    //     the cook-context file and the cook index row.
    // The projected PAX invocation plan (gate 15) is IDENTICAL for both kinds.
    private static (int? Status, object? Body, PreparedCook Prepared) PrepareCookArtifacts(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        Dictionary<string, object?> recipe,
        string recipeId,
        long minFreeDiskBytes,
        CookKind kind)
    {
        // Gate 11 — pre-spawn disk-space hard floor.
        DiskPrecheckResult disk = TestCookDiskPrecheck(workspacePath, minFreeDiskBytes);
        if (!disk.Ok)
        {
            return (507, new
            {
                error = "insufficient_disk_space",
                recipeId,
                reason = disk.Reason,
                detail = disk.Detail,
                freeBytes = disk.FreeBytes,
                requiredBytes = disk.RequiredBytes,
                drive = disk.DriveName,
            }, default);
        }

        // Gate 12 — pre-spawn classic MAX_PATH budget check.
        int effWorkspaceLen = workspacePath.Length;
        if ((effWorkspaceLen + 96) > 260)
        {
            return (400, new
            {
                error = "workspace_path_too_long",
                recipeId,
                workspacePathLength = effWorkspaceLen,
                reservedChildBudget = 96,
                classicLimit = 260,
                reason = "pre_spawn_path_length_exceeds_max_path",
                detail = "Workspace path length (" + effWorkspaceLen +
                    ") plus the reserved per-cook child budget (96) exceeds the classic MAX_PATH limit (260).",
            }, default);
        }

        // Gate 13 — execution-mode. A MANUAL cook may only run a manually-entered
        // recipe (executionMode == "local-manual"); a SCHEDULED cook coexists with
        // any executionMode (already authorized by its enabled schedule), so it is
        // not rejected here.
        string execMode = JsonModel.Str(recipe.TryGetValue("executionMode", out object? em) ? em : null);
        if (kind == CookKind.Manual &&
            !string.Equals(execMode, "local-manual", StringComparison.Ordinal))
        {
            return (412, new
            {
                error = "recipe_invalid",
                recipeId,
                errors = new object[]
                {
                    new
                    {
                        path = "/executionMode",
                        keyword = "manualEntryNotAllowed",
                        message = "Recipe executionMode '" + execMode + "' cannot be cooked from the manual cook route.",
                        @params = new { executionMode = execMode },
                    },
                },
            }, default);
        }

        // Gate 14 — App-registration Chef's Key resolution (NO secret read). The
        // resolved (secret-free) Chef's Key is carried into PreparedCook so the
        // supervisor can inject child-only GRAPH_* credentials at spawn (CK-3).
        (int authStatus, object? authBody, PaxAdapter.ChefKeyAuthRow? chefKeyRow,
            ChefKeyModel.ChefKeyResolved? resolvedChefKey) =
            ResolveChefKeyForProjection(recipe, recipeId);
        if (authBody is not null)
        {
            return (authStatus, authBody, default);
        }

        // Gate 15 — project the authoritative PAX invocation plan against the
        // MANAGED engine path. Pure string projection: no file read, no spawn.
        PaxAdapter.InvocationPlan plan;
        try
        {
            plan = PaxAdapter.GetInvocationPlan(recipe, engine.ManagedEnginePath, chefKeyRow, "local-manual");
        }
        catch (PaxAdapter.ProjectionException ex)
        {
            return (412, new
            {
                error = "recipe_invalid",
                recipeId,
                errors = new object[]
                {
                    new { path = "/advanced/extraArguments", message = ex.Message },
                },
            }, default);
        }

        // Gate 16 — create the per-cook folder.
        string cookId = NewCookId();
        string cookFolderAbs = Path.Combine(CooksDir(workspacePath), recipeId, cookId);
        string cookFolderRel = Path.Combine("Cooks", recipeId, cookId);
        try
        {
            Directory.CreateDirectory(cookFolderAbs);
        }
        catch (Exception ex)
        {
            return (500, new { error = "cook_folder_create_failed", recipeId, detail = ex.Message }, default);
        }

        // Gate 17 — write the pre-spawn cook-folder files atomically.
        string createdAt = CookUtcNowIso();
        try
        {
            WriteCookFolderFiles(cookFolderAbs, recipe, versionInfo, engine, plan, cookId, recipeId, createdAt, kind);
        }
        catch (Exception ex)
        {
            return (500, new { error = "cook_init_files_failed", recipeId, detail = ex.Message }, default);
        }

        // Gate 18 — insert the cook index row (status='running', started_at NULL).
        try
        {
            AddCookRow(workspacePath, versionInfo, engine, plan, cookId, recipeId, cookFolderRel, createdAt, kind);
        }
        catch (Exception ex)
        {
            return (500, new { error = "cook_row_insert_failed", recipeId, detail = ex.Message }, default);
        }

        // Decide whether this recipe's auth mode needs an interactive console
        // window allocated for the PAX child (WebLogin's MSAL/WAM browser
        // sign-in needs a parent HWND). Read straight from recipe.auth.mode; an
        // unbound / unknown / empty mode defaults to headless.
        string authModeForWindow = string.Empty;
        if (recipe.TryGetValue("auth", out object? authRawForWindow) &&
            authRawForWindow is Dictionary<string, object?> authDictForWindow &&
            authDictForWindow.TryGetValue("mode", out object? authModeRawForWindow))
        {
            authModeForWindow = JsonModel.Str(authModeRawForWindow);
        }
        bool requiresInteractiveWindow = RequiresInteractiveWindowForAuthMode(authModeForWindow);

        return (null, null, new PreparedCook(
            cookId, cookFolderAbs, cookFolderRel, plan, createdAt, resolvedChefKey, requiresInteractiveWindow));
    }

    // Oracle: Get-RunningCookIdForRecipe. Read-only; null when the cooks table
    // is absent / unreadable or no running cook exists for this recipe.
    private static string? GetRunningCookIdForRecipe(string workspacePath, string recipeId)
    {
        try
        {
            using SqliteConnection? conn = OpenReadOnly(workspacePath);
            if (conn is null)
            {
                return null;
            }

            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT cook_id FROM cooks WHERE recipe_id = $rid AND status = 'running' LIMIT 1;";
            SqliteParameter p = cmd.CreateParameter();
            p.ParameterName = "$rid";
            p.Value = recipeId;
            cmd.Parameters.Add(p);

            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }
            return reader.IsDBNull(0) ? null : reader.GetString(0);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private readonly record struct DiskPrecheckResult(
        bool Ok, long FreeBytes, long RequiredBytes, string DriveName, string Reason, string Detail);

    // Oracle: Test-CookDiskPrecheck (Start-Broker.ps1). A HARD-FLOOR pre-check
    // that NEVER throws: an unresolvable drive yields ok=false with a structured
    // reason rather than a 500.
    private static DiskPrecheckResult TestCookDiskPrecheck(string path, long requiredBytes)
    {
        if (requiredBytes < 0)
        {
            requiredBytes = DefaultMinFreeDiskBytesForCook;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return new DiskPrecheckResult(false, -1, requiredBytes, string.Empty, "drive_unresolved", "Path is empty.");
        }
        try
        {
            string? rootPath = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return new DiskPrecheckResult(false, -1, requiredBytes, string.Empty, "drive_unresolved",
                    "Could not resolve a volume root for path '" + path + "'.");
            }
            var drive = new DriveInfo(rootPath);
            string driveName = drive.Name;
            if (!drive.IsReady)
            {
                return new DiskPrecheckResult(false, -1, requiredBytes, driveName, "drive_unresolved",
                    "Drive '" + driveName + "' is not ready.");
            }
            long free = drive.AvailableFreeSpace;
            if (free >= requiredBytes)
            {
                return new DiskPrecheckResult(true, free, requiredBytes, driveName, "ok", string.Empty);
            }
            return new DiskPrecheckResult(false, free, requiredBytes, driveName, "insufficient_space",
                "Drive '" + driveName + "' has " + free + " byte(s) free; cook precheck requires at least " + requiredBytes + ".");
        }
        catch (Exception ex)
        {
            return new DiskPrecheckResult(false, -1, requiredBytes, string.Empty, "probe_failed",
                "DriveInfo probe threw: " + ex.Message);
        }
    }

    // Gate 14 helper. Returns (status, body, chefKeyRow, resolved). body is
    // non-null only when a validation error short-circuits the gate. Interactive /
    // WebLogin / empty auth modes resolve to a null row + null resolved (projected
    // directly, no credentials). The Chef's Key is read from the per-user Windows
    // Credential Manager vault (CK-1): metadata only -- the secret is never read
    // here (constraint 14). The resolved (secret-free) Chef's Key flows to the
    // supervisor for child-only GRAPH_* injection at spawn (CK-3).
    private static (int Status, object? Body, PaxAdapter.ChefKeyAuthRow? ChefKey, ChefKeyModel.ChefKeyResolved? Resolved) ResolveChefKeyForProjection(
        Dictionary<string, object?> recipe, string recipeId)
    {
        string authMode = string.Empty;
        string chefKeyId = string.Empty;
        if (recipe.TryGetValue("auth", out object? authObj) && authObj is Dictionary<string, object?> auth)
        {
            if (auth.TryGetValue("mode", out object? m)) { authMode = JsonModel.Str(m); }
            if (auth.TryGetValue("chefKeyId", out object? a)) { chefKeyId = JsonModel.Str(a); }
        }

        bool isApp =
            string.Equals(authMode, "AppRegistrationSecret", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(authMode, "AppRegistrationCertificate", StringComparison.OrdinalIgnoreCase);
        if (!isApp)
        {
            return (200, null, null, null);
        }

        if (string.IsNullOrWhiteSpace(chefKeyId))
        {
            return (412, new
            {
                error = "recipe_invalid",
                recipeId,
                errors = new object[]
                {
                    new
                    {
                        path = "/auth/chefKeyId",
                        keyword = "required",
                        message = "Recipe auth.mode is '" + authMode + "' but no chefKeyId is set.",
                    },
                },
            }, null, null);
        }

        ChefKeyModel.ChefKeyResolved? resolved = ChefKeyModel.ResolveForRecipe(chefKeyId);
        if (resolved is null)
        {
            return (412, new
            {
                error = "recipe_invalid",
                recipeId,
                errors = new object[]
                {
                    new
                    {
                        path = "/auth/chefKeyId",
                        keyword = "chefKeyNotFound",
                        message = "Chef's Key '" + chefKeyId + "' does not exist.",
                        @params = new { chefKeyId },
                    },
                },
            }, null, null);
        }

        if (!string.Equals(resolved.RecipeAuthMode, authMode, StringComparison.OrdinalIgnoreCase))
        {
            return (412, new
            {
                error = "recipe_invalid",
                recipeId,
                errors = new object[]
                {
                    new
                    {
                        path = "/auth/mode",
                        keyword = "chefKeyModeMismatch",
                        message = "Recipe auth.mode '" + authMode + "' does not match Chef's Key type '" + resolved.AuthType + "'.",
                        @params = new { recipeMode = authMode, chefKeyType = resolved.AuthType },
                    },
                },
            }, null, null);
        }

        // App-registration-secret recipes must have a stored client secret to
        // inject at spawn (CK-3). A bound Chef's Key with no secret is refused
        // here -- a bounded, secret-free error before any folder / row / spawn --
        // rather than launching a child that cannot authenticate.
        if (string.Equals(authMode, "AppRegistrationSecret", StringComparison.OrdinalIgnoreCase) &&
            !resolved.HasSecret)
        {
            return (412, new
            {
                error = "recipe_invalid",
                recipeId,
                errors = new object[]
                {
                    new
                    {
                        path = "/auth/chefKeyId",
                        keyword = "chefKeySecretMissing",
                        message = "Chef's Key '" + chefKeyId + "' (AppRegistrationSecret) has no stored client secret.",
                        @params = new { chefKeyId },
                    },
                },
            }, null, null);
        }

        return (200, null, new PaxAdapter.ChefKeyAuthRow(authMode, resolved.ClientId, resolved.CertThumbprint), resolved);
    }

    // CK-3 test-only seam hook. Drives the gate-14 Chef's Key resolution against a
    // minimal in-memory recipe so the smoke harness can assert the bounded
    // resolve-or-error behavior (the App-registration 501 is gone; a bound key
    // resolves, an unbound / missing / secret-less key returns a bounded error)
    // WITHOUT a running broker, a saved recipe, an acquired engine, a re-auth
    // ceremony, or any spawn. Returns (httpStatus, hasRow, resolvedHasSecret); it
    // never returns or logs the secret. Reachable only from the
    // --test-seam-cook-credential-env CLI seam, never from a route.
    internal static (int Status, bool HasRow, bool ResolvedHasSecret) TestSeamResolveChefKeyForProjection(
        string authMode, string? chefKeyId)
    {
        var auth = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = authMode,
        };
        if (!string.IsNullOrEmpty(chefKeyId))
        {
            auth["chefKeyId"] = chefKeyId;
        }
        var recipe = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["auth"] = auth,
        };

        (int status, _, PaxAdapter.ChefKeyAuthRow? row, ChefKeyModel.ChefKeyResolved? resolved) =
            ResolveChefKeyForProjection(recipe, "ck3-seam");
        return (status, row is not null, resolved?.HasSecret ?? false);
    }

    // Writes the four pre-spawn cook-folder files atomically. The oracle also
    // touches an empty cook.log here; X15 deliberately omits it (the supervisor
    // owns cook.log appends and there is no pre-spawn consumer without a child).
    private static void WriteCookFolderFiles(
        string cookFolderAbs,
        Dictionary<string, object?> recipe,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        PaxAdapter.InvocationPlan plan,
        string cookId,
        string recipeId,
        string createdAt,
        CookKind kind)
    {
        // The recorded cook trigger: "scheduled" for a scheduled run, "manual"
        // otherwise. Non-secret provenance only (constraint 14).
        string trigger = kind == CookKind.Scheduled ? "scheduled" : "manual";

        // recipe-snapshot.json — point-in-time recipe document.
        WriteAtomicUtf8NoBom(
            Path.Combine(cookFolderAbs, "recipe-snapshot.json"),
            JsonModel.SerializeToUtf8Bytes(recipe, indented: true));

        // cook-context.json — pre-spawn runtime identity block.
        Dictionary<string, object?> context = BuildCookContextBlock(
            versionInfo, engine, cookId, recipeId, createdAt, trigger);
        WriteAtomicUtf8NoBom(
            Path.Combine(cookFolderAbs, "cook-context.json"),
            JsonModel.SerializeToUtf8Bytes(context, indented: true));

        // command.txt — the rendered PAX command (NOT executed).
        WriteAtomicUtf8NoBom(
            Path.Combine(cookFolderAbs, "command.txt"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(plan.PaxCommand));

        // command-argv.json — the structured projection.
        var argv = new Dictionary<string, object?>
        {
            ["paxArgv"] = new List<object?>(plan.PaxArgv),
            ["extraArguments"] = plan.ExtraArguments,
            ["spawnArgv"] = new List<object?>(plan.SpawnArgv),
            ["paxScriptPath"] = plan.PaxScriptPath,
        };
        WriteAtomicUtf8NoBom(
            Path.Combine(cookFolderAbs, "command-argv.json"),
            JsonModel.SerializeToUtf8Bytes(argv, indented: true));

        // readiness-snapshot.json — no-secret summary of the prepared cook's
        // readiness (engine / auth mode / destinations). Surfaced read-only via
        // the cook detail route; advisory, so it never blocks preparation.
        WriteReadinessSnapshot(cookFolderAbs, recipe, engine);
    }

    // Oracle: Get-CookContextBlock. The bundledPax block reflects the MANAGED
    // engine that would be invoked (version / sha / path), preserving the schema
    // key name for SPA parity.
    private static Dictionary<string, object?> BuildCookContextBlock(
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        string cookId,
        string recipeId,
        string createdAt,
        string trigger)
    {
        return new Dictionary<string, object?>
        {
            ["schemaVersion"] = CookContextSchemaVersion,
            ["cookId"] = cookId,
            ["recipeId"] = recipeId,
            ["createdAt"] = createdAt,
            ["trigger"] = trigger,
            ["createdBy"] = new Dictionary<string, object?>
            {
                ["cookbookVersion"] = versionInfo.CookbookVersion,
                ["bundledPaxVersion"] = versionInfo.PaxVersion,
                ["releaseChannel"] = versionInfo.ReleaseChannel,
            },
            ["bundledPax"] = new Dictionary<string, object?>
            {
                ["version"] = engine.Version ?? versionInfo.PaxVersion,
                ["sha256"] = engine.RecordedSha256,
                ["path"] = engine.ManagedEnginePath,
            },
            ["host"] = Environment.MachineName,
        };
    }

    // Oracle: Add-CookRow. INSERTs exactly one cook row. started_at is left NULL
    // (no child has started); the redacted argv equals the argv because the
    // spawn argv carries no secret material.
    private static void AddCookRow(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        PaxAdapter.InvocationPlan plan,
        string cookId,
        string recipeId,
        string cookFolderRel,
        string createdAt,
        CookKind kind)
    {
        string recipeSnapshotJson = Encoding.UTF8.GetString(
            JsonModel.SerializeToUtf8Bytes(LoadRecipeTree(workspacePath, recipeId).Recipe));
        string commandArgvJson = Encoding.UTF8.GetString(
            JsonModel.SerializeToUtf8Bytes(new List<object?>(plan.SpawnArgv)));
        string paxScriptVersion = engine.Version ?? versionInfo.PaxVersion;

        string dbFile = DatabaseFile(workspacePath);
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
            "INSERT INTO cooks (" +
            "cook_id, recipe_id, recipe_snapshot_json, command_argv_json, command_argv_redacted, " +
            "pax_script_path, pax_script_version, trigger, cook_folder, status, started_at, " +
            "created_at, updated_at) VALUES (" +
            "$cook_id, $recipe_id, $recipe_snapshot_json, $command_argv_json, $command_argv_redacted, " +
            "$pax_script_path, $pax_script_version, $trigger, $cook_folder, $status, NULL, " +
            "$created_at, $updated_at);";

        void Add(string name, object? value)
        {
            SqliteParameter p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        Add("$cook_id", cookId);
        Add("$recipe_id", recipeId);
        Add("$recipe_snapshot_json", recipeSnapshotJson);
        Add("$command_argv_json", commandArgvJson);
        Add("$command_argv_redacted", commandArgvJson);
        Add("$pax_script_path", engine.ManagedEnginePath);
        Add("$pax_script_version", paxScriptVersion);
        Add("$trigger", kind == CookKind.Scheduled ? "scheduled" : "manual");
        Add("$cook_folder", cookFolderRel);
        Add("$status", "running");
        Add("$created_at", createdAt);
        Add("$updated_at", createdAt);

        cmd.ExecuteNonQuery();
    }

    // Write-temp + atomic rename. UTF-8 no BOM. A concurrent reader never sees a
    // half-written file, and a failure leaves no partial final file.
    private static void WriteAtomicUtf8NoBom(string finalPath, byte[] bytes)
    {
        string tempPath = finalPath + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }
        File.Move(tempPath, finalPath);
    }
}
