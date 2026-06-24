using System.Text;
using Microsoft.Data.Sqlite;

namespace PAXCookbook.App;

// Resume-from-checkpoint recovery cook. A resume is a one-time recovery action,
// NOT a recipe: it is never saved, never validated as a recipe, never listed in
// the recipe list, and has no recipes-table row. It runs a PAX `-Resume`
// recovery pass through the SINGLE sanctioned cook execution mechanism — the
// same SpawnAndSupervise the manual and scheduled cook paths funnel into — so it
// shares the one supervised child, the engine SHA re-verify immediately before
// spawn, the cook.log tee, the cook status/stop/cancel lifecycle, and the
// child-only GRAPH_* credential injection. There is exactly one execution
// mechanism; this is a second ENTRY POINT into it (like the scheduled one-shot),
// not a second spawn/execution channel.
//
// The verified PAX `-Resume` contract: the engine parses $Resume from its
// remaining args. A checkpoint that is a file (.json) is passed as the explicit
// resume target (`-Resume "<file>"`); a checkpoint folder is passed as a bare
// auto-discover resume plus the output directory (`-Resume -OutputPath
// "<folder>"`); -Force overrides PAX's resume safety prompts; and when a Chef's
// Key is supplied the auth switches are appended exactly as the recipe path
// emits them. When no Chef's Key is supplied (the common path) no auth switches
// are emitted and no credentials are injected — PAX restores the saved sign-in
// from the checkpoint itself.
//
// The checkpoint path is a local filesystem path supplied by the operator. It is
// non-secret and is stored in the cook-context.json checkpoint block, the cook
// row's resume marker, and command.txt for provenance. The engine reads and
// validates the checkpoint contents; this model never opens, parses, or stats
// the checkpoint file. No secret is ever persisted, returned, or logged
// (constraint 14): the only secret-bearing path is the reused child-only
// credential injection, which reads the bound Chef's Key secret from Windows
// Credential Manager at spawn, injects it onto the child environment only, and
// scrubs it — identical to the recipe cook path.
internal static partial class RecipeReadModel
{
    // Sentinel id used to key the resume cook's gate-10 Windows Hello step-up and
    // to mark the cook's provenance (cook.log, the started/interrupted sentinels,
    // and cook-context.json). It is deliberately NOT a valid recipe id: recipe
    // ids are 26-character Crockford-base32 ULIDs (IsValidRecipeId =
    // ^[0-9A-HJKMNP-TV-Z]{26}$), so an underscore-bearing, lowercase, 10-character
    // literal can never collide with a real recipe id. Gate 10 is satisfied by a
    // single-use authorization the WebAuthn step-up grants for THIS exact id (the
    // verify route binds the grant to whatever id the request carries, with no
    // recipe-format or existence check), so the resume step-up reuses the manual
    // cook ceremony end-to-end without any recipe ever existing.
    private const string ResumeReAuthId = "__resume__";

    // Cook-folder bucket for resume cooks. Resume has no recipe id, so its cooks
    // live under Cooks\__resume__\<cookId> rather than Cooks\<recipeId>\<cookId>.
    // The segment stays under the managed Cooks\ root, so the read surface's
    // cook-folder containment check accepts it and the pre-spawn MAX_PATH budget
    // (reserved 96 chars) comfortably covers it.
    private const string ResumeCookFolderBucket = "__resume__";

    // Public resume-cook entry point (POST /api/v1/resume-cook). Runs the resume
    // recovery pass through the single sanctioned cook execution mechanism with a
    // resume-shaped invocation plan. Returns (httpStatus, body):
    //
    //   empty checkpoint path   : 400 invalid_checkpoint_path (no folder, row, or spawn).
    //   engine not acquired     : 409 acquisitionRequired (the cook path's gate-6 body).
    //   no manualCook re-auth   : 401 reAuthRequired (gate 10; no folder, row, or spawn).
    //   disk floor / path budget: 507 insufficient_disk_space / 400 workspace_path_too_long.
    //   unusable Chef's Key     : bounded 412 (chefKeyNotFound / chefKeyModeMismatch /
    //                             chefKeySecretMissing) before any folder, row, or spawn.
    //   spawn failure           : bounded 500; row -> interrupted, interrupted.json written.
    //   spawn success           : 201 { cookId, cookFolder, trigger:"resume", checkpoint }.
    //
    // reAuthVerified is the CLI-only manual-cook re-auth test seam (the same seam
    // the manual cook route honors); production passes false and gate 10 consumes
    // a real browser-owned WebAuthn authorization.
    public static (int Status, object Body) StartResumeCook(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        string? checkpointPath,
        bool force,
        string? chefKeyId,
        string? dashboard,
        bool deidentify,
        string? fillerLabel,
        string? fillerLabelText,
        bool reAuthVerified,
        string? pwshPathOverride)
    {
        // (a) Validate the checkpoint path. A light non-empty check only: the
        // engine reads and validates the checkpoint contents, and the path need
        // not exist yet from this model's point of view. The contents are never
        // opened or parsed here.
        string checkpoint = (checkpointPath ?? string.Empty).Trim();
        if (checkpoint.Length == 0)
        {
            return (400, new
            {
                error = "invalid_checkpoint_path",
                message = "A non-empty checkpointPath is required to resume a cook.",
            });
        }

        // (b) Engine acquisition gate (mirrors the cook path's gate 6). A resume
        // invokes the managed engine, so an unacquired engine is refused with the
        // same 409 acquisitionRequired body the SPA engine-acquisition overlay
        // renders on.
        if (!engine.IsAcquired)
        {
            return (409, engine.ToGate409Body("POST", "/api/v1/resume-cook"));
        }

        // (c) Gate 10 — per-operation Windows Hello step-up (MANDATORY, fail
        // closed). Identical to the manual cook branch but keyed to the resume
        // sentinel: production consumes a single-use, sentinel-bound,
        // lock-generation-bound in-memory authorization minted by a real
        // browser-owned WebAuthn step-up; the CLI-only test seam authorizes
        // automated smoke. A non-verified state is NEVER coerced into success —
        // the resume fails closed with 401 reAuthRequired before any folder, row,
        // or child exists. opClass is "manualCook" so the existing React
        // manual-cook re-auth helper engages unchanged (it keys on the opClass and
        // the id it supplied to the verify route).
        bool reAuthOk;
        if (reAuthVerified)
        {
            reAuthOk = true;
        }
        else
        {
            (bool consumed, _) = ManualCookReAuth.TryConsume(ResumeReAuthId, BrokerLock.CurrentLockGeneration);
            reAuthOk = consumed;
        }

        if (!reAuthOk)
        {
            return (401, new
            {
                code = "reAuthRequired",
                error = "reAuthRequired",
                opClass = "manualCook",
                verificationResult = "Required",
                message = "A fresh Windows Hello verification is required before starting a resume cook.",
            });
        }

        // (d) Pre-spawn disk-space hard floor + classic MAX_PATH budget (mirrors
        // the cook path's gates 11/12). The resume route carries no per-call disk
        // override, so the production default floor is used.
        DiskPrecheckResult disk = TestCookDiskPrecheck(workspacePath, DefaultMinFreeDiskBytesForCook);
        if (!disk.Ok)
        {
            return (507, new
            {
                error = "insufficient_disk_space",
                reason = disk.Reason,
                detail = disk.Detail,
                freeBytes = disk.FreeBytes,
                requiredBytes = disk.RequiredBytes,
                drive = disk.DriveName,
            });
        }

        int effWorkspaceLen = workspacePath.Length;
        if ((effWorkspaceLen + 96) > 260)
        {
            return (400, new
            {
                error = "workspace_path_too_long",
                workspacePathLength = effWorkspaceLen,
                reservedChildBudget = 96,
                classicLimit = 260,
                reason = "pre_spawn_path_length_exceeds_max_path",
                detail = "Workspace path length (" + effWorkspaceLen +
                    ") plus the reserved per-cook child budget (96) exceeds the classic MAX_PATH limit (260).",
            });
        }

        // (e) Resolve the Chef's Key ONLY when chefKeyId is supplied. The common
        // resume path omits chefKeyId entirely: PAX restores the saved sign-in
        // from the checkpoint, so no key is resolved, no auth switches are
        // emitted, and no credentials are injected. When a key is supplied it is
        // resolved from the per-user Windows Credential Manager vault by the SAME
        // resolver the recipe path uses (metadata only — the secret is read later,
        // at spawn, by the reused injection helper). Bounded, secret-free 412
        // errors mirror the recipe path. A resume has no recipe auth.mode to
        // compare against, so the mode check instead rejects a key whose type does
        // not map to a usable PAX sign-in mode.
        string resolvedChefKeyId = (chefKeyId ?? string.Empty).Trim();
        ChefKeyModel.ChefKeyResolved? resolvedChefKey = null;
        if (resolvedChefKeyId.Length > 0)
        {
            resolvedChefKey = ChefKeyModel.ResolveForRecipe(resolvedChefKeyId);
            if (resolvedChefKey is null)
            {
                return (412, new
                {
                    error = "chefKeyNotFound",
                    chefKeyId = resolvedChefKeyId,
                    message = "Chef's Key '" + resolvedChefKeyId + "' does not exist.",
                });
            }

            if (string.IsNullOrEmpty(resolvedChefKey.RecipeAuthMode))
            {
                return (412, new
                {
                    error = "chefKeyModeMismatch",
                    chefKeyId = resolvedChefKeyId,
                    chefKeyType = resolvedChefKey.AuthType,
                    message = "Chef's Key '" + resolvedChefKeyId + "' does not map to a usable sign-in mode for a resume.",
                });
            }

            if (string.Equals(resolvedChefKey.AuthType, ChefKeyModel.AuthAppRegSecret, StringComparison.OrdinalIgnoreCase) &&
                !resolvedChefKey.HasSecret)
            {
                return (412, new
                {
                    error = "chefKeySecretMissing",
                    chefKeyId = resolvedChefKeyId,
                    message = "Chef's Key '" + resolvedChefKeyId + "' (AppRegistrationSecret) has no stored client secret.",
                });
            }
        }

        // (f, g) Project the resume invocation plan against the MANAGED engine
        // path. Pure string projection — no file read, no spawn.
        PaxAdapter.InvocationPlan plan = BuildResumeInvocationPlan(
            checkpoint, force, resolvedChefKey, dashboard, deidentify, fillerLabel, fillerLabelText, engine.ManagedEnginePath);

        // (h) Create the per-cook folder under the resume bucket.
        string cookId = NewCookId();
        string cookFolderRel = Path.Combine("Cooks", ResumeCookFolderBucket, cookId);
        string cookFolderAbs = Path.Combine(CooksDir(workspacePath), ResumeCookFolderBucket, cookId);
        string createdAt = CookUtcNowIso();
        try
        {
            Directory.CreateDirectory(cookFolderAbs);
        }
        catch (Exception ex)
        {
            return (500, new { error = "cook_folder_create_failed", detail = ex.Message });
        }

        // (i) Build the PreparedCook the shared supervisor consumes. The resolved
        // Chef's Key (or null) is carried as the credential-injection source; the
        // interactive-window decision uses the SAME helper the recipe path uses
        // (WebLogin needs an MSAL/WAM parent window; a null key / any other mode is
        // headless).
        bool requiresInteractiveWindow = RequiresInteractiveWindowForAuthMode(resolvedChefKey?.RecipeAuthMode);
        var prepared = new PreparedCook(
            cookId, cookFolderAbs, cookFolderRel, plan, createdAt, resolvedChefKey, requiresInteractiveWindow);

        // (j) Write the pre-spawn cook-folder files (resume variant: a resume
        // marker instead of a recipe snapshot, a cook-context with trigger
        // "resume" and the non-secret checkpoint block, the rendered command, and
        // the structured argv).
        try
        {
            WriteResumeCookFolderFiles(
                cookFolderAbs, versionInfo, engine, plan, cookId, createdAt,
                checkpoint, force, resolvedChefKeyId.Length > 0 ? resolvedChefKeyId : null);
        }
        catch (Exception ex)
        {
            return (500, new { error = "cook_init_files_failed", detail = ex.Message });
        }

        // (k) Insert the cook index row (status='running', started_at NULL,
        // recipe_id NULL, trigger 'resume').
        try
        {
            AddResumeCookRow(
                workspacePath, versionInfo, engine, plan, cookId, cookFolderRel, createdAt,
                checkpoint, force, resolvedChefKeyId.Length > 0 ? resolvedChefKeyId : null);
        }
        catch (Exception ex)
        {
            return (500, new { error = "cook_row_insert_failed", detail = ex.Message });
        }

        // (l) Spawn + supervise through the SINGLE sanctioned mechanism. The
        // sentinel is passed as the supervisor's recipe-id provenance (cook.log,
        // sentinels); the cook row itself carries recipe_id NULL. The supervisor
        // re-hashes the managed engine immediately before spawn (constraint 6),
        // injects child-only GRAPH_* credentials when a key is bound, tees
        // cook.log, and registers the cook for stop/cancel.
        (int spawnStatus, object spawnBody) = SpawnAndSupervise(
            workspacePath, versionInfo, engine, prepared, ResumeReAuthId, pwshPathOverride, joinSupervisor: false);

        if (spawnStatus == 201)
        {
            // Post-process the shared 201 into the resume shape: surface the
            // trigger and the non-secret checkpoint provenance instead of the
            // recipe-shaped body. The sentinel recipe id is intentionally not
            // returned.
            return (201, new
            {
                cookId = prepared.CookId,
                cookFolder = prepared.CookFolderRel,
                trigger = "resume",
                checkpoint = new { path = checkpoint, force },
            });
        }

        return (spawnStatus, spawnBody);
    }

    // Projects the resume InvocationPlan: the resume argv, the rendered PAX
    // command, and the outer pwsh spawn expression against the MANAGED engine
    // path. The spawn-expression assembly is identical to the recipe path
    // (GetInvocationPlan): & '<engine>' <paxCommand>, then the
    // -NoProfile/-NoLogo/-Command spawn argv.
    private static PaxAdapter.InvocationPlan BuildResumeInvocationPlan(
        string checkpoint, bool force, ChefKeyModel.ChefKeyResolved? resolvedChefKey, string? dashboard,
        bool deidentify, string? fillerLabel, string? fillerLabelText, string paxScriptPath)
    {
        (List<string> paxArgv, string paxCommand) = BuildResumeArgvAndCommand(
            checkpoint, force, resolvedChefKey, dashboard, deidentify, fillerLabel, fillerLabelText);

        string escapedPath = paxScriptPath.Replace("'", "''");
        string commandExpr = $"& '{escapedPath}' {paxCommand}";
        commandExpr = commandExpr.TrimEnd();

        var spawnArgv = new List<string> { "-NoProfile", "-NoLogo", "-Command", commandExpr };
        string spawnCommand = "pwsh -NoProfile -NoLogo -Command \"" + commandExpr.Replace("\"", "\\\"") + "\"";

        return new PaxAdapter.InvocationPlan(paxArgv, string.Empty, paxCommand, spawnArgv, spawnCommand, paxScriptPath);
    }

    // Builds the resume PAX argv and its rendered command string together.
    //
    // The command string is assembled directly here (rather than via
    // PaxAdapter.ConvertToCommandString) on purpose: ConvertToCommandString
    // treats -Resume as an always-quote-its-value switch and would quote whatever
    // token follows it. That is correct for the explicit-file form (-Resume
    // "<file>") but wrong for the bare auto-discover form, where -Resume takes no
    // value and is followed by -OutputPath — ConvertToCommandString would emit
    // `-Resume "-OutputPath" <folder>`, quoting -OutputPath as if it were the
    // resume target so the folder never binds. So the resume command is built
    // token-by-token, reusing PaxAdapter.ConvertToQuotedArg (the same value
    // escaper the recipe path uses) for the path values and emitting the auth-tail
    // values unquoted exactly as the recipe path does. The stored paxArgv keeps
    // its natural order (resume tokens, then -Force, then the auth tail).
    private static (List<string> Argv, string Command) BuildResumeArgvAndCommand(
        string checkpoint, bool force, ChefKeyModel.ChefKeyResolved? resolvedChefKey, string? dashboard,
        bool deidentify, string? fillerLabel, string? fillerLabelText)
    {
        var argv = new List<string>();
        var commandParts = new List<string>();

        // Resume target. A .json checkpoint (case-insensitive) is the explicit
        // resume file; anything else is treated as a checkpoint folder, so PAX
        // auto-discovers (bare -Resume) and writes into that output directory.
        bool explicitFile = checkpoint.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        if (explicitFile)
        {
            argv.Add("-Resume");
            argv.Add(checkpoint);
            commandParts.Add("-Resume");
            commandParts.Add(PaxAdapter.ConvertToQuotedArg(checkpoint));
        }
        else
        {
            argv.Add("-Resume");
            argv.Add("-OutputPath");
            argv.Add(checkpoint);
            commandParts.Add("-Resume");
            commandParts.Add("-OutputPath");
            commandParts.Add(PaxAdapter.ConvertToQuotedArg(checkpoint));
        }

        if (force)
        {
            argv.Add("-Force");
            commandParts.Add("-Force");
        }

        // Dashboard. The resume path emits only the AIBV dashboard switch, and
        // only when the operator selected it: AIO is PAX's default (omitted) and
        // M365 is implied by -IncludeM365Usage, which a resume never emits, so
        // there is no -Dashboard/-IncludeM365Usage conflict to guard here. AIBV
        // needs no quoting, so it is emitted as two bare tokens like the auth tail.
        if (string.Equals(dashboard, "aibv", StringComparison.OrdinalIgnoreCase))
        {
            argv.Add("-Dashboard");
            argv.Add("AIBV");
            commandParts.Add("-Dashboard");
            commandParts.Add("AIBV");
        }

        // Hierarchy filler. Like -Dashboard, the rollup is implied by the
        // checkpoint, so the filler switch is re-supplied whenever the operator
        // selected one. 'Fixed' carries its literal label via -FillerLabelText.
        // The values are emitted unquoted like the auth tail, except the custom
        // text which is quoted (it may contain spaces).
        string fillerLabelTok = (fillerLabel ?? string.Empty).Trim();
        if (fillerLabelTok.Length > 0)
        {
            argv.Add("-FillerLabel");
            argv.Add(fillerLabelTok);
            commandParts.Add("-FillerLabel");
            commandParts.Add(fillerLabelTok);
            if (string.Equals(fillerLabelTok, "Fixed", StringComparison.OrdinalIgnoreCase))
            {
                string fillerText = (fillerLabelText ?? string.Empty).Trim();
                if (fillerText.Length > 0)
                {
                    argv.Add("-FillerLabelText");
                    argv.Add(fillerText);
                    commandParts.Add("-FillerLabelText");
                    commandParts.Add(PaxAdapter.ConvertToQuotedArg(fillerText));
                }
            }
        }

        // De-identify. Engine-wide one-way anonymization of the resumed run's
        // output; re-supplied so a resumed de-identify cook does not emit
        // identified rows.
        if (deidentify)
        {
            argv.Add("-Deidentify");
            commandParts.Add("-Deidentify");
        }

        // Auth tail — only when a Chef's Key was resolved. Mirrors the recipe
        // path's Get-PaxArgvArray tail: -TenantId, -Auth (AppRegistration* mapped
        // to "AppRegistration"; WebLogin / DeviceCode passed through), and for the
        // App-registration modes -ClientId plus, for the certificate mode,
        // -ClientCertificateThumbprint. All values are non-secret metadata read
        // from the resolved Chef's Key; the secret / certificate private key is
        // NEVER a CLI arg — it is injected onto the child environment only by the
        // reused supervisor credential injection. Auth-tail values are emitted
        // unquoted exactly as the recipe path renders them.
        if (resolvedChefKey is not null)
        {
            string tenantId = resolvedChefKey.TenantId ?? string.Empty;
            if (tenantId.Length > 0)
            {
                argv.Add("-TenantId");
                argv.Add(tenantId);
                commandParts.Add("-TenantId");
                commandParts.Add(tenantId);
            }

            string paxAuthValue = MapResumeAuthValue(resolvedChefKey.RecipeAuthMode);
            if (paxAuthValue.Length > 0)
            {
                argv.Add("-Auth");
                argv.Add(paxAuthValue);
                commandParts.Add("-Auth");
                commandParts.Add(paxAuthValue);
            }

            bool isAppReg =
                string.Equals(resolvedChefKey.RecipeAuthMode, "AppRegistrationSecret", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resolvedChefKey.RecipeAuthMode, "AppRegistrationCertificate", StringComparison.OrdinalIgnoreCase);
            if (isAppReg)
            {
                string clientId = resolvedChefKey.ClientId ?? string.Empty;
                if (clientId.Length > 0)
                {
                    argv.Add("-ClientId");
                    argv.Add(clientId);
                    commandParts.Add("-ClientId");
                    commandParts.Add(clientId);
                }

                if (string.Equals(resolvedChefKey.RecipeAuthMode, "AppRegistrationCertificate", StringComparison.OrdinalIgnoreCase))
                {
                    string thumb = resolvedChefKey.CertThumbprint ?? string.Empty;
                    if (thumb.Length > 0)
                    {
                        argv.Add("-ClientCertificateThumbprint");
                        argv.Add(thumb);
                        commandParts.Add("-ClientCertificateThumbprint");
                        commandParts.Add(thumb);
                    }
                }
            }
        }

        return (argv, string.Join(" ", commandParts));
    }

    // Recipe auth.mode -> PAX -Auth value (the same mapping the recipe argv
    // projection uses). The App-registration secret/certificate modes both map to
    // PAX's single "AppRegistration" auth value; WebLogin / DeviceCode pass
    // through unchanged.
    private static string MapResumeAuthValue(string recipeAuthMode) =>
        (string.Equals(recipeAuthMode, "AppRegistrationSecret", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(recipeAuthMode, "AppRegistrationCertificate", StringComparison.OrdinalIgnoreCase))
            ? "AppRegistration"
            : recipeAuthMode;

    // The non-secret checkpoint provenance block written into cook-context.json
    // and the resume marker. chefKeyId is the operator's chosen key id (or null
    // for the common "restore from checkpoint" path); the secret itself is never
    // recorded.
    private static Dictionary<string, object?> BuildResumeCheckpointBlock(
        string checkpoint, bool force, string? chefKeyId) => new()
    {
        ["path"] = checkpoint,
        ["force"] = force,
        ["chefKeyId"] = string.IsNullOrEmpty(chefKeyId) ? null : chefKeyId,
    };

    // The small resume marker stored where a recipe path would store the recipe
    // snapshot. It is explicitly NOT a recipe document: it marks the slot as a
    // resume and carries the non-secret checkpoint provenance. The read surface
    // tolerates it (it has no identity/name/destinations, so the cook list/detail
    // recipe summary projects null fields rather than crashing).
    private static Dictionary<string, object?> BuildResumeMarker(
        string checkpoint, bool force, string? chefKeyId) => new()
    {
        ["kind"] = "resume",
        ["checkpoint"] = BuildResumeCheckpointBlock(checkpoint, force, chefKeyId),
    };

    // Writes the pre-spawn cook-folder files for a resume cook. A resume has no
    // recipe, so recipe-snapshot.json holds the resume marker (not a recipe) and
    // no readiness snapshot is written; cook-context.json carries trigger
    // "resume" plus the non-secret checkpoint block. All files are non-secret
    // (constraint 14) and written atomically.
    private static void WriteResumeCookFolderFiles(
        string cookFolderAbs,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        PaxAdapter.InvocationPlan plan,
        string cookId,
        string createdAt,
        string checkpoint,
        bool force,
        string? chefKeyId)
    {
        // recipe-snapshot.json — resume marker (NOT a recipe).
        WriteAtomicUtf8NoBom(
            Path.Combine(cookFolderAbs, "recipe-snapshot.json"),
            JsonModel.SerializeToUtf8Bytes(BuildResumeMarker(checkpoint, force, chefKeyId), indented: true));

        // cook-context.json — pre-spawn runtime identity block with the resume
        // trigger and the non-secret checkpoint block. The sentinel is recorded as
        // the context recipe id for provenance (the read surface reads the cook
        // row's recipe_id, which is NULL, not this field).
        Dictionary<string, object?> context = BuildCookContextBlock(
            versionInfo, engine, cookId, ResumeReAuthId, createdAt, "resume");
        context["checkpoint"] = BuildResumeCheckpointBlock(checkpoint, force, chefKeyId);
        WriteAtomicUtf8NoBom(
            Path.Combine(cookFolderAbs, "cook-context.json"),
            JsonModel.SerializeToUtf8Bytes(context, indented: true));

        // command.txt — the rendered PAX resume command (NOT executed; no secret).
        WriteAtomicUtf8NoBom(
            Path.Combine(cookFolderAbs, "command.txt"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(plan.PaxCommand));

        // command-argv.json — the structured projection (no secret).
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
    }

    // Inserts the resume cook index row. Mirrors AddCookRow exactly except:
    // recipe_id is NULL (a resume has no recipe; the column is nullable with an
    // ON DELETE SET NULL foreign key, so NULL is the correct, foreign-key-safe
    // value and the read surface already projects a NULL recipe_id safely),
    // trigger is "resume", and recipe_snapshot_json is the resume marker rather
    // than a recipe document. started_at is left NULL (no child has started); the
    // redacted argv equals the argv because the spawn argv carries no secret.
    private static void AddResumeCookRow(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        PaxAdapter.InvocationPlan plan,
        string cookId,
        string cookFolderRel,
        string createdAt,
        string checkpoint,
        bool force,
        string? chefKeyId)
    {
        string recipeSnapshotJson = Encoding.UTF8.GetString(
            JsonModel.SerializeToUtf8Bytes(BuildResumeMarker(checkpoint, force, chefKeyId)));
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
            "$cook_id, NULL, $recipe_snapshot_json, $command_argv_json, $command_argv_redacted, " +
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
        Add("$recipe_snapshot_json", recipeSnapshotJson);
        Add("$command_argv_json", commandArgvJson);
        Add("$command_argv_redacted", commandArgvJson);
        Add("$pax_script_path", engine.ManagedEnginePath);
        Add("$pax_script_version", paxScriptVersion);
        Add("$trigger", "resume");
        Add("$cook_folder", cookFolderRel);
        Add("$status", "running");
        Add("$created_at", createdAt);
        Add("$updated_at", createdAt);

        cmd.ExecuteNonQuery();
    }

    // CLI-only test seam hook. Projects the rendered PAX resume command for a
    // checkpoint path with no Chef's Key resolved (so no auth tail), letting the
    // smoke harness assert the explicit-file vs folder vs -Force argv rules
    // WITHOUT a running broker, an acquired engine, a Windows Credential Manager
    // read, a re-auth ceremony, or any spawn. It never resolves a key and never
    // returns or logs a secret. Reachable only from the --test-seam-resume-command
    // CLI seam, never from a route.
    internal static string TestSeamBuildResumeCommand(string checkpointPath, bool force)
    {
        (_, string command) = BuildResumeArgvAndCommand(
            (checkpointPath ?? string.Empty).Trim(), force, resolvedChefKey: null, dashboard: null,
            deidentify: false, fillerLabel: null, fillerLabelText: null);
        return command;
    }
}
