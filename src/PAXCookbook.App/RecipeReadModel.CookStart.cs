using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PAXCookbook.Shared.Contracts;

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
        //
        // Cycle 15 — the organization branch of gate 14 is CAPABILITY-GATED on
        // the ACQUIRED engine.
        //
        // Cycle 16 — the REAL machine policy, trusted ProgramData inventory,
        // certificate catalog, clock, and engine capability are now wired in. The
        // authority is passed as a FACTORY, not a value, so (a) a personal Recipe
        // performs none of that work and its path is unchanged, and (b) the
        // organization branch always re-evaluates a FRESH snapshot at the Cook
        // boundary instead of reusing anything a readiness or preview call
        // computed earlier. Nothing is cached across requests.
        Func<OrganizationCookPreparationContext> organizationPreparation =
            () => ProductionOrganizationAuthority.CreateSnapshot(versionInfo, engine);

        (int authStatus, object? authBody, PaxAdapter.ChefKeyAuthRow? chefKeyRow,
            ChefKeyModel.ChefKeyResolved? resolvedChefKey) =
            ResolveChefKeyForProjection(recipe, recipeId, organizationPreparation);
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

        // Project the per-cook identity and folder paths. NOTHING is created
        // yet: the reservation below must be the FIRST side effect.
        string cookId = NewCookId();
        string cookFolderAbs = Path.Combine(CooksDir(workspacePath), recipeId, cookId);
        string cookFolderRel = Path.Combine("Cooks", recipeId, cookId);
        string createdAt = CookUtcNowIso();

        // ATOMIC RESERVATION (was gate 18). The cook index row is written FIRST,
        // by a single conditional INSERT that refuses when a running cook already
        // exists for this recipe. The refusal comes from the same statement that
        // would have created the row, so there is no check-then-insert window:
        // the gate-9 busy check above remains a read-only fast path, but THIS
        // statement is the concurrency authority. It cannot precede gate 15 —
        // command_argv_json, command_argv_redacted, pax_script_path and
        // pax_script_version are all NOT NULL and all derived from the plan.
        RecipeCookReservation reservation;
        try
        {
            reservation = ReserveRecipeCookRow(
                workspacePath, versionInfo, engine, plan, cookId, recipeId, cookFolderRel, createdAt, kind);
        }
        catch (Exception ex)
        {
            // A genuine persistence failure stays the existing 500; only a
            // zero-row refusal is a concurrency conflict.
            return (500, new { error = "cook_row_insert_failed", recipeId, detail = ex.Message }, default);
        }

        if (!reservation.Reserved)
        {
            // Same bounded 409 contract gate 9 emits. The loser created no
            // folder, no file, no supervisor, and no process. The winning cook id
            // is surfaced when it can be read safely; it is never fabricated.
            return (409, new { error = "recipe_busy", recipeId, cookId = reservation.RunningCookId }, default);
        }

        // Gate 16 — create the per-cook folder. From here on any failure must
        // RELEASE the reservation, otherwise a phantom 'running' row would block
        // every future cook of this recipe.
        try
        {
            Directory.CreateDirectory(cookFolderAbs);
        }
        catch (Exception ex)
        {
            ReleaseCookReservation(workspacePath, cookId);
            return (500, new { error = "cook_folder_create_failed", recipeId, detail = ex.Message }, default);
        }

        // Gate 17 — write the pre-spawn cook-folder files atomically.
        try
        {
            WriteCookFolderFiles(cookFolderAbs, recipe, versionInfo, engine, plan, cookId, recipeId, createdAt, kind);
        }
        catch (Exception ex)
        {
            ReleaseCookReservation(workspacePath, cookId);
            return (500, new { error = "cook_init_files_failed", recipeId, detail = ex.Message }, default);
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
        Dictionary<string, object?> recipe, string recipeId,
        Func<OrganizationCookPreparationContext>? organizationAuthority = null)
    {
        string authMode = string.Empty;
        string chefKeyId = string.Empty;
        string organizationKeyId = string.Empty;
        if (recipe.TryGetValue("auth", out object? authObj) && authObj is Dictionary<string, object?> auth)
        {
            if (auth.TryGetValue("mode", out object? m)) { authMode = JsonModel.Str(m); }
            if (auth.TryGetValue("chefKeyId", out object? a)) { chefKeyId = JsonModel.Str(a); }
            if (auth.TryGetValue("organizationKeyId", out object? o)) { organizationKeyId = JsonModel.Str(o); }
        }

        // Cycle 15 — ORGANIZATION-KEY CAPABILITY-GATED BLOCK (replaces the
        // Cycle-14s unconditional block). It is still the FIRST statement of
        // gate 14: ahead of the isApp test, the chefKeyId test,
        // ChefKeyModel.ResolveForRecipe, PaxAdapter.GetInvocationPlan (gate 15),
        // the cook folder (gate 16), the cook row (gate 18), and any process
        // creation. It NEVER falls back to chefKeyId, never maps to a personal
        // Windows Credential Manager target, never derives or maps a SHA-1
        // thumbprint, and never emits the opaque identifier or any
        // tenant/client/certificate reference in the bounded body. Personal
        // Recipes are untouched: they fall straight through.
        //
        // Cycle 16 — the authority snapshot is materialized HERE, at the first
        // organization-specific boundary, so the whole chain is re-evaluated
        // fresh for this Cook and a personal Recipe never triggers it at all.
        if (!string.IsNullOrWhiteSpace(organizationKeyId))
        {
            return ResolveOrganizationCookPreparation(
                recipe, recipeId, organizationKeyId, organizationAuthority?.Invoke());
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

    // Cycle 15 — the injected inputs the organization branch of gate 14 needs.
    // Everything is INJECTED so this file performs no inventory, catalog, clock,
    // registry, ProgramData, tenant, Graph, or network I/O of its own. A null
    // context, a null capability, a null evaluation, and a null catalog all fail
    // CLOSED. Cycle 16 supplies the REAL instances through
    // ProductionOrganizationAuthority, and makes `Capability` a deferred read so
    // an unauthorized policy or an unready binding short-circuits the acquired-
    // engine record entirely (canonical order: capability is step 9, last).
    internal sealed record OrganizationCookPreparationContext(
        Func<EngineCapabilityState>? Capability,
        OrganizationInventoryEvaluation? Evaluation,
        ICertificateCatalog? Catalog,
        ICertificateUsabilityClock? Clock);

    // Cycle 15 — ORGANIZATION COOK PREPARATION.
    //
    // Order is fixed and fail-closed:
    //   1. Re-evaluate the Cycle-14 chain (policy -> inventory -> entry ->
    //      resolution -> usability) with the EXISTING evaluator. Not ready ->
    //      the bounded readiness refusal.
    //   2. Require the ACQUIRED engine to attest the SHA-256 selector capability.
    //      Not `Available` -> the SAME bounded refusal, verbatim.
    //   3. Only then produce canonical certificate-auth preparation from the
    //      ALREADY-EXISTING inventory reference. It computes NO hash, opens NO
    //      certificate store, touches NO private key, and maps NOTHING to SHA-1.
    //
    // Because the current acquired engine declares no capability, step 2 always
    // refuses in production: organization Recipes stay blocked.
    private static (int Status, object? Body, PaxAdapter.ChefKeyAuthRow? ChefKey, ChefKeyModel.ChefKeyResolved? Resolved)
        ResolveOrganizationCookPreparation(
            Dictionary<string, object?> recipe, string recipeId, string organizationKeyId,
            OrganizationCookPreparationContext? context)
    {
        PaxAdapter.ChefKeyAuthRow? row =
            TryPrepareOrganizationAuthRow(recipe, organizationKeyId, context);
        if (row is null)
        {
            return (412, OrganizationKeyNotYetRunnableBody(recipeId), null, null);
        }

        // The opaque identifier and the display name never reach this row, and
        // no secret is resolved: certificate auth carries no client secret.
        return (200, null, row, null);
    }

    // Cycle 16 — the SINGLE bounded organization preparation decision, shared by
    // the pre-Cook hard gate and the non-authoritative preview/readiness
    // projection so the two can never disagree. Each caller passes its OWN fresh
    // authority snapshot, so sharing this pure decision does NOT share state: a
    // preview result never becomes Cook authority.
    //
    // Returns null for EVERY refusal, which the callers map to their own bounded
    // not-yet-runnable body. It resolves no personal credential, never falls back
    // to chefKeyId, opens no store, computes no hash, and constructs nothing.
    internal static PaxAdapter.ChefKeyAuthRow? TryPrepareOrganizationAuthRow(
        Dictionary<string, object?> recipe, string organizationKeyId,
        OrganizationCookPreparationContext? context)
    {
        // 1. The Cycle-14 binding readiness chain, evaluated against the REAL
        //    recipe binding (so a Recipe that also carries a personal key, or a
        //    non-certificate sign-in mode, fails closed here).
        OrganizationKeyBindingReadiness? readiness = RecipeReadinessModel.ProjectOrganizationKeyBinding(
            recipe, context?.Evaluation, context?.Catalog, context?.Clock);
        if (readiness is null || !readiness.Ready)
        {
            return null;
        }

        // 2. Runtime engine capability. Read ONLY now, so an unauthorized
        //    policy, an unprovisioned inventory, an unknown/disabled entry, an
        //    unresolvable reference, and an unusable certificate all refuse
        //    without ever consulting the acquired-engine record.
        if (context is null
            || context.Capability is null
            || context.Capability() != EngineCapabilityState.Available)
        {
            return null;
        }

        // 3. Canonical certificate-auth preparation from the already-existing
        //    inventory reference.
        OrganizationKeyInventoryEntry? entry = FindOrganizationEntry(context.Evaluation, organizationKeyId);
        string? certificateSha256 = Sha256Hex.Normalize(entry?.CertificateSha256);
        if (entry is null
            || certificateSha256 is null
            || string.IsNullOrWhiteSpace(entry.ClientReference))
        {
            return null;
        }

        return new PaxAdapter.ChefKeyAuthRow(
            OrganizationKeyRecipeBinding.RequiredAuthMode,
            entry.ClientReference,
            CertThumbprint: null,
            CertSha256: certificateSha256);
    }

    // Exactly ONE match or nothing: an ambiguous identifier never surfaces an
    // arbitrary entry.
    private static OrganizationKeyInventoryEntry? FindOrganizationEntry(
        OrganizationInventoryEvaluation? evaluation, string organizationKeyId)
    {
        if (evaluation is null) { return null; }
        OrganizationKeyInventoryEntry? match = null;
        foreach (OrganizationKeyInventoryEntry candidate in evaluation.Entries)
        {
            if (candidate is null ||
                !string.Equals(candidate.OrganizationKeyId, organizationKeyId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (match is not null) { return null; }
            match = candidate;
        }
        return match;
    }

    // The Cycle-14s bounded refusal, verbatim.
    private static object OrganizationKeyNotYetRunnableBody(string recipeId) => new
    {
        error = "recipe_invalid",
        recipeId,
        executionStatus = OrganizationKeyRunnability.ExecutionStatus,
        errors = new object[]
        {
            new
            {
                path = OrganizationKeyRunnability.InstancePath,
                keyword = OrganizationKeyRunnability.Keyword,
                message = OrganizationKeyRunnability.Message,
                @params = new
                {
                    executionStatus = OrganizationKeyRunnability.ExecutionStatus,
                    detail = OrganizationKeyRunnability.Detail,
                },
            },
        },
    };

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

    // Cycle 14s test-only seam. Drives the SAME gate-14 helper the manual and
    // scheduled cook start paths call, and returns the BOUNDED body so the
    // organization-key hard block can be asserted verbatim WITHOUT a running
    // broker, a saved recipe, an acquired engine, an invocation plan, a cook
    // folder, a cook row, or any spawn. It resolves no credential and returns no
    // secret. Reachable only from the test assembly via InternalsVisibleTo.
    internal static (int Status, object? Body, bool HasRow) TestSeamResolveAuthForProjection(
        string? authMode, string? chefKeyId, string? organizationKeyId)
    {
        var auth = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = authMode ?? string.Empty,
        };
        if (!string.IsNullOrEmpty(chefKeyId))
        {
            auth["chefKeyId"] = chefKeyId;
        }
        if (!string.IsNullOrEmpty(organizationKeyId))
        {
            auth["organizationKeyId"] = organizationKeyId;
        }
        var recipe = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["auth"] = auth,
        };

        (int status, object? body, PaxAdapter.ChefKeyAuthRow? row, _) =
            ResolveChefKeyForProjection(recipe, "cycle-14s-seam");
        return (status, body, row is not null);
    }

    // Cycle 15 test-only seam. Drives the SAME gate-14 helper with an INJECTED
    // engine capability state, inventory evaluation, catalog, and clock, so both
    // the capability-gated refusal and the canonical preparation can be asserted
    // WITHOUT a real acquisition, a real certificate store, an invocation plan, a
    // cook folder, a cook row, or any spawn. Reachable only from the test
    // assembly via InternalsVisibleTo.
    internal static (int Status, object? Body, PaxAdapter.ChefKeyAuthRow? Row)
        TestSeamResolveOrganizationCookPreparation(
            string organizationKeyId,
            EngineCapabilityState capability,
            OrganizationInventoryEvaluation? evaluation,
            ICertificateCatalog? catalog,
            ICertificateUsabilityClock? clock)
    {
        var auth = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = OrganizationKeyRecipeBinding.RequiredAuthMode,
            ["organizationKeyId"] = organizationKeyId,
        };
        var recipe = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["auth"] = auth,
        };

        (int status, object? body, PaxAdapter.ChefKeyAuthRow? row, _) =
            ResolveChefKeyForProjection(
                recipe,
                "cycle-15-seam",
                () => new OrganizationCookPreparationContext(
                    () => capability, evaluation, catalog, clock));
        return (status, body, row);
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

    // Outcome of the atomic recipe-cook reservation. Reserved is false ONLY when
    // the conditional INSERT matched no row because a running cook already holds
    // this recipe; RunningCookId names that winner when it can be read safely and
    // is null otherwise (it is never guessed). Any other persistence failure
    // throws, preserving the existing 500 cook_row_insert_failed contract.
    private readonly record struct RecipeCookReservation(bool Reserved, string? RunningCookId);

    // Oracle: Add-CookRow, hardened into an ATOMIC RESERVATION. INSERTs at most
    // one cook row. started_at is left NULL (no child has started); the redacted
    // argv equals the argv because the spawn argv carries no secret material.
    private static RecipeCookReservation ReserveRecipeCookRow(
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

        return ReserveRecipeCookRowCore(
            workspacePath, cookId, recipeId, recipeSnapshotJson, commandArgvJson,
            engine.ManagedEnginePath, paxScriptVersion,
            kind == CookKind.Scheduled ? "scheduled" : "manual", cookFolderRel, createdAt);
    }

    // The atomic reservation itself: ONE conditional INSERT. There is
    // deliberately no SELECT before it — a check-then-insert leaves a window in
    // which two simultaneous cooks of the same recipe both see "nothing running"
    // and both proceed. The SELECT below runs only AFTER the database has already
    // declined the write, and exists solely to name the winner in the 409 body.
    private static RecipeCookReservation ReserveRecipeCookRowCore(
        string workspacePath,
        string cookId,
        string recipeId,
        string recipeSnapshotJson,
        string commandArgvJson,
        string paxScriptPath,
        string paxScriptVersion,
        string trigger,
        string cookFolderRel,
        string createdAt)
    {
        using SqliteConnection conn = OpenCookIndex(workspacePath);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO cooks (" +
            "cook_id, recipe_id, recipe_snapshot_json, command_argv_json, command_argv_redacted, " +
            "pax_script_path, pax_script_version, trigger, cook_folder, status, started_at, " +
            "created_at, updated_at) " +
            "SELECT $cook_id, $recipe_id, $recipe_snapshot_json, $command_argv_json, $command_argv_redacted, " +
            "$pax_script_path, $pax_script_version, $trigger, $cook_folder, $status, NULL, " +
            "$created_at, $updated_at " +
            "WHERE NOT EXISTS (" +
            "SELECT 1 FROM cooks WHERE recipe_id = $recipe_id AND status = 'running');";

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
        Add("$pax_script_path", paxScriptPath);
        Add("$pax_script_version", paxScriptVersion);
        Add("$trigger", trigger);
        Add("$cook_folder", cookFolderRel);
        Add("$status", "running");
        Add("$created_at", createdAt);
        Add("$updated_at", createdAt);

        int rows = cmd.ExecuteNonQuery();
        return rows == 1
            ? new RecipeCookReservation(true, null)
            : new RecipeCookReservation(false, SelectRunningRecipeCookId(conn, recipeId));
    }

    private static string? SelectRunningRecipeCookId(SqliteConnection conn, string recipeId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT cook_id FROM cooks WHERE recipe_id = $recipe_id AND status = 'running' LIMIT 1;";
        SqliteParameter p = cmd.CreateParameter();
        p.ParameterName = "$recipe_id";
        p.Value = recipeId;
        cmd.Parameters.Add(p);

        object? value = cmd.ExecuteScalar();
        return value is string id && id.Length > 0 ? id : null;
    }

    // Test-only reservation seam. Exercises the REAL atomic reservation
    // (ReserveRecipeCookRowCore) against a caller-supplied workspace so the
    // concurrency behaviour can be proven WITHOUT an acquired engine, a Chef's
    // Key, a cook folder, a supervisor, or any spawn. It never runs PAX, never
    // starts a Bake, and never reads or returns a secret. Reachable only from the
    // test assembly via InternalsVisibleTo.
    internal static (bool Reserved, string? RunningCookId) TestSeamReserveRecipeCookRow(
        string workspacePath, string cookId, string recipeId)
    {
        RecipeCookReservation r = ReserveRecipeCookRowCore(
            workspacePath,
            cookId,
            recipeId,
            recipeSnapshotJson: "{\"kind\":\"recipe\"}",
            commandArgvJson: "[]",
            paxScriptPath: "test-seam",
            paxScriptVersion: "test-seam",
            trigger: "manual",
            cookFolderRel: Path.Combine("Cooks", recipeId, cookId),
            createdAt: CookUtcNowIso());
        return (r.Reserved, r.RunningCookId);
    }

    // Test-only compensation seam for the recipe path. Exercises the REAL shared
    // release helper so a reservation whose cook never started can be proven not
    // to leave a phantom 'running' row behind. Spawns nothing, reads no secret.
    internal static void TestSeamReleaseRecipeCookReservation(string workspacePath, string cookId) =>
        ReleaseCookReservation(workspacePath, cookId);

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
