using System.Text;
using Microsoft.Data.Sqlite;
using PAXCookbook.Shared.Contracts;

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
    // Sentinel id used to mark the resume cook's provenance (cook.log, the
    // started/interrupted sentinels, and cook-context.json). It is deliberately
    // NOT a valid recipe id: recipe
    // ids are 26-character Crockford-base32 ULIDs (IsValidRecipeId =
    // ^[0-9A-HJKMNP-TV-Z]{26}$), so an underscore-bearing, lowercase, 10-character
    // literal can never collide with a real recipe id. It is used ONLY as the
    // cook-folder bucket and the supervisor's recipe-id provenance for a resume
    // cook; no recipe ever exists under it.
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
    //   bad checkpoint path     : 400 invalid_checkpoint_path (empty, relative,
    //                             drive-relative, malformed, missing, or an
    //                             existing file that is not .json). No folder,
    //                             row, or spawn.
    //   engine not acquired     : 409 acquisitionRequired (the cook path's gate-6 body).
    //   same checkpoint running : 409 resume_already_running { cookId } — refused by the
    //                             partial unique index, so no second folder, row, or spawn.
    //   disk floor / path budget: 507 insufficient_disk_space / 400 workspace_path_too_long.
    //   unusable Chef's Key     : bounded 412 (chefKeyNotFound / chefKeyModeMismatch /
    //                             chefKeySecretMissing) before any folder, row, or spawn.
    //   spawn failure           : bounded 500; row -> interrupted, interrupted.json written.
    //   spawn success           : 201 { cookId, cookFolder, trigger:"resume", checkpoint }.
    //
    // A Resume is authorized by the Unlocked broker session plus explicit user
    // confirmation (both enforced upstream); there is no per-operation re-auth.
    public static (int Status, object Body) StartResumeCook(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        string? checkpointPath,
        bool force,
        string? chefKeyId,
        string? pwshPathOverride)
    {
        // The three preparation phases (gates -> authorization -> preparation) are
        // ordered by the portable CookPreparationSequence, the SINGLE SOURCE
        // of that order across the desktop host and the service host. It owns the
        // PHASE ORDER only. The Resume GATES are deliberately NOT converged with
        // the recipe gates — a Resume has no recipe to load, validate, or check for
        // busyness — and the spawn/supervisor boundary stays entirely outside the
        // sequence. Statuses, bodies, side-effect order, argv, and timing are what
        // they were before Resume entered the sequence; the adapter simply holds
        // the resume state privately between phases instead of in local variables.
        ResumeCookPreparationAdapter adapter = new(
            workspacePath, versionInfo, engine, checkpointPath, force, chefKeyId);

        CookPreparationOutcome outcome =
            CookPreparationSequence.Execute(CookTriggerKind.Resume, adapter);

        if (!outcome.Prepared)
        {
            // Every refusal path recorded its own bounded, secret-free
            // (status, body) — the SAME response that point returned before Resume
            // entered the sequence. The defensive fallback can only be reached if
            // the sequence refused without any phase running (an undeclared
            // trigger), which the literal CookTriggerKind.Resume above makes
            // unreachable; it fails closed rather than fabricating a cook.
            return adapter.BoundedRefusal
                ?? (500, new { error = "resume_preparation_not_authorized" });
        }

        PreparedCook prepared = adapter.Prepared;

        // (m) Spawn + supervise through the SINGLE sanctioned mechanism. The
        // sentinel is passed as the supervisor's recipe-id provenance (cook.log,
        // sentinels); the cook row itself carries recipe_id NULL. The supervisor
        // re-hashes the managed engine immediately before spawn (constraint 6),
        // injects child-only GRAPH_* credentials when a key is bound, tees
        // cook.log, and registers the cook for stop/cancel. A spawn failure moves
        // the row to 'interrupted', which releases the checkpoint identity for a
        // later retry without any extra compensation here.
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
                checkpoint = new { path = adapter.Checkpoint, force },
            });
        }

        return (spawnStatus, spawnBody);
    }

    // Desktop behaviour for the three preparation phases of a RESUME. It holds the
    // resume state — the canonical checkpoint path, its kind, its concurrency
    // identity, the resolved Chef's Key, the bounded refusal, and the prepared cook
    // — PRIVATELY between phases, and delegates each phase to the existing resume
    // helpers unchanged. None of that state crosses the portable contract, which
    // sees only "did this phase proceed?": no checkpoint path, invocation plan,
    // credential, cook id, cook folder, HTTP body, or private App type is exposed
    // there.
    //
    // No visibility was widened for this: PreparedCook, ResumeCheckpointKind,
    // ChefKeyModel.ChefKeyResolved and this adapter itself all keep the visibility
    // they already had.
    private sealed class ResumeCookPreparationAdapter : ICookPreparationAdapter
    {
        private readonly string _workspacePath;
        private readonly VersionInfo _versionInfo;
        private readonly EngineAcquisitionResult _engine;
        private readonly string? _checkpointPath;
        private readonly bool _force;
        private readonly string? _chefKeyId;

        private string _checkpoint = string.Empty;
        private ResumeCheckpointKind _checkpointKind;
        private string _checkpointIdentity = string.Empty;
        private int _refusalStatus;
        private object? _refusalBody;
        private CookAuthorizationPhaseSelection _authorizationSelected = CookAuthorizationPhaseSelection.None;
        private int _gatePhaseCalls;
        private int _authorizationPhaseCalls;
        private int _preparationPhaseCalls;

        internal ResumeCookPreparationAdapter(
            string workspacePath,
            VersionInfo versionInfo,
            EngineAcquisitionResult engine,
            string? checkpointPath,
            bool force,
            string? chefKeyId)
        {
            _workspacePath = workspacePath;
            _versionInfo = versionInfo;
            _engine = engine;
            _checkpointPath = checkpointPath;
            _force = force;
            _chefKeyId = chefKeyId;
        }

        // The bounded, secret-free response recorded by whichever phase refused —
        // byte-for-byte the response that point already returned before Resume
        // entered the sequence. Null while nothing has refused.
        internal (int Status, object Body)? BoundedRefusal =>
            _refusalBody is null ? null : (_refusalStatus, _refusalBody);

        // The prepared cook produced by phase 3. Valid only after a Prepared
        // outcome; default otherwise, so a refused resume can never carry a cook id
        // into the spawn boundary.
        internal PreparedCook Prepared { get; private set; }

        // The CANONICAL checkpoint path established by phase 1 — the only
        // checkpoint value used in argv, provenance, the 201 body, and the
        // concurrency identity.
        internal string Checkpoint => _checkpoint;

        // Bounded record of WHICH authorization phase the sequence selected and how
        // many times each phase was requested. Read only by test seams; production
        // never branches on it.
        internal CookAuthorizationPhaseSelection AuthorizationSelected => _authorizationSelected;

        internal int GatePhaseCalls => _gatePhaseCalls;

        internal int AuthorizationPhaseCalls => _authorizationPhaseCalls;

        internal int PreparationPhaseCalls => _preparationPhaseCalls;

        // Phase 1 — the RESUME gates: checkpoint-path validation, then engine
        // acquisition. These are the resume path's own gates and are not converged
        // with the recipe gates.
        public bool EvaluateGatesPhase()
        {
            _gatePhaseCalls++;

            // (a) Validate and normalize the checkpoint path through the single
            // server-side validator. It refuses an empty, relative, drive-relative,
            // malformed, non-existent, or wrong-kind location, and returns the
            // canonical path plus the resume KIND (folder vs explicit .json file)
            // decided by what is actually on disk. Every refusal maps to the SAME
            // existing 400 invalid_checkpoint_path contract; `reason` is additive
            // provenance, not a new error code. The checkpoint CONTENTS are still
            // never opened or parsed here -- the engine owns that.
            ResumeCheckpointPathResult checkpointResult = ResumeCheckpointPath.Validate(_checkpointPath);
            if (!checkpointResult.Ok)
            {
                return Refuse(400, new
                {
                    error = "invalid_checkpoint_path",
                    reason = DescribeCheckpointRejection(checkpointResult.Rejection),
                    message = ExplainCheckpointRejection(checkpointResult.Rejection),
                });
            }

            // From here on the CANONICAL path is the only checkpoint value used: in
            // argv, in the persisted provenance blocks, in the 201 body, and as the
            // concurrency identity.
            _checkpoint = checkpointResult.CanonicalPath;
            _checkpointKind = checkpointResult.Kind;
            _checkpointIdentity = checkpointResult.IdentityKey;

            // (b) Engine acquisition gate (mirrors the cook path's gate 6). A resume
            // invokes the managed engine, so an unacquired engine is refused with the
            // same 409 acquisitionRequired body the SPA engine-acquisition overlay
            // renders on.
            if (!_engine.IsAcquired)
            {
                return Refuse(409, _engine.ToGate409Body("POST", "/api/v1/resume-cook"));
            }

            return true;
        }

        // Phase 2 for a RESUME — a NO-OP POLICY ACKNOWLEDGEMENT, not an active
        // authorization check. Brian's session-authentication policy supersedes the
        // historical fresh-per-operation gate: a Resume is authorized UPSTREAM by
        // the Unlocked broker session (the lock middleware returns 423 when Locked)
        // plus the explicit user confirmation that issued this request. There is NO
        // per-operation identity ceremony and NO resume re-auth grant, so there is
        // nothing to evaluate here. Its only significance is that the sequence
        // invokes THIS member, and only this member, for a resume trigger — which
        // is what proves phase SELECTION and proves no new ceremony was added.
        public bool AuthorizeResumePhase() => Acknowledge(CookAuthorizationPhaseSelection.Resume);

        // Phase 2 for triggers this adapter can NEVER serve. A resume has no recipe,
        // no recipe schedule, and no manual-cook recipe binding, so neither of these
        // is reachable through the resume entry point. Both FAIL CLOSED with a
        // bounded, secret-free disposition rather than returning true: a true here
        // would let a mis-routed manual or scheduled cook authorize itself through
        // the resume adapter.
        public bool AuthorizeManualPhase() => RefuseTriggerMismatch(CookAuthorizationPhaseSelection.Manual);

        public bool AuthorizeScheduledPhase() => RefuseTriggerMismatch(CookAuthorizationPhaseSelection.Scheduled);

        // Phase 3 — the resume preparation: disk floor, path budget, optional Chef's
        // Key, invocation plan, projected paths, the ATOMIC reservation, the cook
        // folder, the PreparedCook, and the pre-spawn folder files. Every helper it
        // calls is the existing one, in the existing order; nothing moved across a
        // phase boundary.
        public bool PreparePhase()
        {
            _preparationPhaseCalls++;

            // (d) Pre-spawn disk-space hard floor + classic MAX_PATH budget (mirrors
            // the cook path's gates 11/12). The resume route carries no per-call disk
            // override, so the production default floor is used.
            DiskPrecheckResult disk = TestCookDiskPrecheck(_workspacePath, DefaultMinFreeDiskBytesForCook);
            if (!disk.Ok)
            {
                return Refuse(507, new
                {
                    error = "insufficient_disk_space",
                    reason = disk.Reason,
                    detail = disk.Detail,
                    freeBytes = disk.FreeBytes,
                    requiredBytes = disk.RequiredBytes,
                    drive = disk.DriveName,
                });
            }

            int effWorkspaceLen = _workspacePath.Length;
            if ((effWorkspaceLen + 96) > 260)
            {
                return Refuse(400, new
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
            string resolvedChefKeyId = (_chefKeyId ?? string.Empty).Trim();
            ChefKeyModel.ChefKeyResolved? resolvedChefKey = null;
            if (resolvedChefKeyId.Length > 0)
            {
                resolvedChefKey = ChefKeyModel.ResolveForRecipe(resolvedChefKeyId);
                if (resolvedChefKey is null)
                {
                    return Refuse(412, new
                    {
                        error = "chefKeyNotFound",
                        chefKeyId = resolvedChefKeyId,
                        message = "Chef's Key '" + resolvedChefKeyId + "' does not exist.",
                    });
                }

                if (string.IsNullOrEmpty(resolvedChefKey.RecipeAuthMode))
                {
                    return Refuse(412, new
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
                    return Refuse(412, new
                    {
                        error = "chefKeySecretMissing",
                        chefKeyId = resolvedChefKeyId,
                        message = "Chef's Key '" + resolvedChefKeyId + "' (AppRegistrationSecret) has no stored client secret.",
                    });
                }
            }

            // (f, g) Project the resume invocation plan against the MANAGED engine
            // path. Pure string projection — no file read, no spawn. The resume KIND
            // comes from the validator's on-disk classification, never from the
            // filename suffix.
            PaxAdapter.InvocationPlan plan = BuildResumeInvocationPlan(
                _checkpoint, _checkpointKind, _force, resolvedChefKey, _engine.ManagedEnginePath);

            // (h) Project the per-cook folder paths. NOTHING is created yet: the
            // reservation below must be the FIRST side effect so two simultaneous
            // resumes of the same checkpoint cannot both leave debris.
            string cookId = NewCookId();
            string cookFolderRel = Path.Combine("Cooks", ResumeCookFolderBucket, cookId);
            string cookFolderAbs = Path.Combine(CooksDir(_workspacePath), ResumeCookFolderBucket, cookId);
            string createdAt = CookUtcNowIso();

            // (i) ATOMIC RESERVATION. The cook row carries the checkpoint identity and
            // is written FIRST, against a partial unique index over
            // (resume_checkpoint_identity) restricted to status='running'. The
            // duplicate is refused by the same statement that would have created it,
            // so there is no check-then-insert window: exactly one of N simultaneous
            // resumes of the same checkpoint wins, and the losers never create a
            // folder, never write a file, and never reach the spawn.
            string? conflictingCookId;
            try
            {
                conflictingCookId = AddResumeCookRow(
                    _workspacePath, _versionInfo, _engine, plan, cookId, cookFolderRel, createdAt,
                    _checkpoint, _checkpointIdentity, _force,
                    resolvedChefKeyId.Length > 0 ? resolvedChefKeyId : null);
            }
            catch (Exception ex)
            {
                return Refuse(500, new { error = "cook_row_insert_failed", detail = ex.Message });
            }

            if (conflictingCookId is not null)
            {
                return Refuse(409, new
                {
                    error = "resume_already_running",
                    cookId = conflictingCookId,
                    checkpoint = new { path = _checkpoint },
                    message = "That checkpoint is already being resumed by a run that is still going.",
                });
            }

            // (j) Create the per-cook folder under the resume bucket. From this point
            // on any failure must RELEASE the reservation, otherwise a phantom
            // 'running' row would block every future resume of this checkpoint.
            try
            {
                Directory.CreateDirectory(cookFolderAbs);
            }
            catch (Exception ex)
            {
                ReleaseCookReservation(_workspacePath, cookId);
                return Refuse(500, new { error = "cook_folder_create_failed", detail = ex.Message });
            }

            // (k) Build the PreparedCook the shared supervisor consumes. The resolved
            // Chef's Key (or null) is carried as the credential-injection source; the
            // interactive-window decision uses the SAME helper the recipe path uses
            // (WebLogin needs an MSAL/WAM parent window; a null key / any other mode is
            // headless).
            bool requiresInteractiveWindow = RequiresInteractiveWindowForAuthMode(resolvedChefKey?.RecipeAuthMode);
            var prepared = new PreparedCook(
                cookId, cookFolderAbs, cookFolderRel, plan, createdAt, resolvedChefKey, requiresInteractiveWindow);

            // (l) Write the pre-spawn cook-folder files (resume variant: a resume
            // marker instead of a recipe snapshot, a cook-context with trigger
            // "resume" and the non-secret checkpoint block, the rendered command, and
            // the structured argv).
            try
            {
                WriteResumeCookFolderFiles(
                    cookFolderAbs, _versionInfo, _engine, plan, cookId, createdAt,
                    _checkpoint, _force, resolvedChefKeyId.Length > 0 ? resolvedChefKeyId : null);
            }
            catch (Exception ex)
            {
                ReleaseCookReservation(_workspacePath, cookId);
                return Refuse(500, new { error = "cook_init_files_failed", detail = ex.Message });
            }

            Prepared = prepared;
            return true;
        }

        // Records that the no-op policy acknowledgement phase ran, and proceeds.
        private bool Acknowledge(CookAuthorizationPhaseSelection phase)
        {
            _authorizationPhaseCalls++;
            _authorizationSelected = phase;
            return true;
        }

        // A cross-trigger authorization phase this adapter can never serve. Bounded
        // and secret-free: it names no checkpoint, no path, and no identity. The
        // phase that was requested IS recorded, so a mis-route is observable rather
        // than silent.
        private bool RefuseTriggerMismatch(CookAuthorizationPhaseSelection phase)
        {
            _authorizationPhaseCalls++;
            _authorizationSelected = phase;
            return Refuse(500, new { error = "resume_authorization_trigger_mismatch" });
        }

        private bool Refuse(int status, object body)
        {
            _refusalStatus = status;
            _refusalBody = body;
            return false;
        }
    }

    // Bounded, non-secret refusal codes for a checkpoint path. Every one of them
    // is carried inside the SAME 400 invalid_checkpoint_path body; none of them
    // is a new error code and none echoes the rejected path.
    private static string DescribeCheckpointRejection(ResumeCheckpointRejection rejection) =>
        rejection switch
        {
            ResumeCheckpointRejection.Empty => "checkpoint_path_empty",
            ResumeCheckpointRejection.NotFullyQualified => "checkpoint_path_not_absolute",
            ResumeCheckpointRejection.Malformed => "checkpoint_path_malformed",
            ResumeCheckpointRejection.NotFound => "checkpoint_path_not_found",
            ResumeCheckpointRejection.WrongKind => "checkpoint_path_wrong_kind",
            _ => "checkpoint_path_invalid",
        };

    private static string ExplainCheckpointRejection(ResumeCheckpointRejection rejection) =>
        rejection switch
        {
            ResumeCheckpointRejection.Empty =>
                "A non-empty checkpointPath is required to resume a cook.",
            ResumeCheckpointRejection.NotFullyQualified =>
                "checkpointPath must be a full path, such as C:\\... or \\\\server\\share\\...",
            ResumeCheckpointRejection.Malformed =>
                "checkpointPath is not a usable filesystem path.",
            ResumeCheckpointRejection.NotFound =>
                "checkpointPath does not exist on this PC.",
            ResumeCheckpointRejection.WrongKind =>
                "checkpointPath names a file that is not a .json checkpoint. Point at the run's output folder or its checkpoint .json.",
            _ => "checkpointPath is not a usable checkpoint location.",
        };

    // Projects the resume InvocationPlan: the resume argv, the rendered PAX
    // command, and the outer pwsh spawn expression against the MANAGED engine
    // path. The spawn-expression assembly is identical to the recipe path
    // (GetInvocationPlan): & '<engine>' <paxCommand>, then the
    // -NoProfile/-NoLogo/-Command spawn argv.
    private static PaxAdapter.InvocationPlan BuildResumeInvocationPlan(
        string checkpoint,
        ResumeCheckpointKind checkpointKind,
        bool force,
        ChefKeyModel.ChefKeyResolved? resolvedChefKey,
        string paxScriptPath)
    {
        (List<string> paxArgv, string paxCommand) = BuildResumeArgvAndCommand(
            checkpoint, checkpointKind, force, resolvedChefKey);

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
        string checkpoint,
        ResumeCheckpointKind checkpointKind,
        bool force,
        ChefKeyModel.ChefKeyResolved? resolvedChefKey)
    {
        var argv = new List<string>();
        var commandParts = new List<string>();

        // Resume target. The kind is decided by what is on disk, NOT by the
        // filename suffix: an existing checkpoint .json file is the explicit
        // resume target, and an existing folder makes PAX auto-discover (bare
        // -Resume) and write into that output directory. Classifying by suffix
        // would render a DIRECTORY named "foo.json" as an explicit resume file.
        bool explicitFile = checkpointKind == ResumeCheckpointKind.JsonFile;
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

        // Output-shaping switches (-Dashboard, -FillerLabel / -FillerLabelText,
        // -Deidentify) are intentionally NOT re-emitted on resume. PAX documents
        // -Resume as standalone — only -Force and auth overrides are accepted on
        // the command line — and restores every output-shaping setting from the
        // checkpoint, which is the sole source of truth on resume. The checkpoint
        // persists and restores dashboard, deidentify, and the hierarchy-filler
        // mode + literal, so re-passing them here is redundant at best and rejected
        // by the resume allow-list at worst.

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

    // Reserves the resume cook index row. Mirrors the recipe path's
    // ReserveRecipeCookRow exactly except: recipe_id is NULL (a resume has no recipe; the column is nullable with an
    // ON DELETE SET NULL foreign key, so NULL is the correct, foreign-key-safe
    // value and the read surface already projects a NULL recipe_id safely),
    // trigger is "resume", recipe_snapshot_json is the resume marker rather than
    // a recipe document, and resume_checkpoint_identity carries the canonical
    // checkpoint identity that the partial unique index gates on. started_at is
    // left NULL (no child has started); the redacted argv equals the argv because
    // the spawn argv carries no secret.
    //
    // Returns null when the reservation succeeded, or the cook id of the resume
    // that already holds this checkpoint when the persistence-boundary gate
    // refused the insert. Any other failure throws, preserving the existing 500
    // cook_row_insert_failed contract.
    private static string? AddResumeCookRow(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        PaxAdapter.InvocationPlan plan,
        string cookId,
        string cookFolderRel,
        string createdAt,
        string checkpoint,
        string checkpointIdentity,
        bool force,
        string? chefKeyId)
    {
        string recipeSnapshotJson = Encoding.UTF8.GetString(
            JsonModel.SerializeToUtf8Bytes(BuildResumeMarker(checkpoint, force, chefKeyId)));
        string commandArgvJson = Encoding.UTF8.GetString(
            JsonModel.SerializeToUtf8Bytes(new List<object?>(plan.SpawnArgv)));
        string paxScriptVersion = engine.Version ?? versionInfo.PaxVersion;

        return ReserveResumeCookRowCore(
            workspacePath, cookId, checkpointIdentity, recipeSnapshotJson, commandArgvJson,
            engine.ManagedEnginePath, paxScriptVersion, cookFolderRel, createdAt);
    }

    // SQLite's extended result code for a UNIQUE constraint violation
    // (SQLITE_CONSTRAINT_UNIQUE). The conflict is detected on this code, never on
    // message text, so a localized or reworded provider message cannot silently
    // turn a real conflict into a generic 500 or vice versa.
    private const int SqliteConstraintUnique = 2067;

    // The atomic reservation itself: ONE insert, and the classification of its
    // failure. There is deliberately no SELECT before the INSERT — a
    // check-then-insert would leave a window in which two simultaneous resumes of
    // the same checkpoint both see "nothing running" and both proceed. The
    // SELECT below runs only AFTER the database has already refused the write,
    // and exists solely to name the winner in the 409 body.
    private static string? ReserveResumeCookRowCore(
        string workspacePath,
        string cookId,
        string checkpointIdentity,
        string recipeSnapshotJson,
        string commandArgvJson,
        string paxScriptPath,
        string paxScriptVersion,
        string cookFolderRel,
        string createdAt)
    {
        using SqliteConnection conn = OpenCookIndex(workspacePath);

        try
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO cooks (" +
                "cook_id, recipe_id, recipe_snapshot_json, command_argv_json, command_argv_redacted, " +
                "pax_script_path, pax_script_version, trigger, cook_folder, status, started_at, " +
                "created_at, updated_at, resume_checkpoint_identity) VALUES (" +
                "$cook_id, NULL, $recipe_snapshot_json, $command_argv_json, $command_argv_redacted, " +
                "$pax_script_path, $pax_script_version, $trigger, $cook_folder, $status, NULL, " +
                "$created_at, $updated_at, $resume_identity);";

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
            Add("$pax_script_path", paxScriptPath);
            Add("$pax_script_version", paxScriptVersion);
            Add("$trigger", "resume");
            Add("$cook_folder", cookFolderRel);
            Add("$status", "running");
            Add("$created_at", createdAt);
            Add("$updated_at", createdAt);
            Add("$resume_identity", checkpointIdentity);

            cmd.ExecuteNonQuery();
            return null;
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            string? runningCookId = SelectRunningResumeCookId(conn, checkpointIdentity);
            if (runningCookId is not null)
            {
                return runningCookId;
            }

            // A UNIQUE violation with no running resume for this identity is NOT
            // our concurrency gate (a cook_id primary-key collision, say). Let it
            // surface as the existing 500 cook_row_insert_failed rather than
            // mislabel it a conflict.
            throw;
        }
    }

    private static string? SelectRunningResumeCookId(SqliteConnection conn, string checkpointIdentity)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT cook_id FROM cooks WHERE resume_checkpoint_identity = $resume_identity " +
            "AND status = 'running' LIMIT 1;";
        SqliteParameter p = cmd.CreateParameter();
        p.ParameterName = "$resume_identity";
        p.Value = checkpointIdentity;
        cmd.Parameters.Add(p);

        object? value = cmd.ExecuteScalar();
        return value is string id && id.Length > 0 ? id : null;
    }

    // Compensation for a reservation whose cook never started, shared by the
    // resume path and the recipe path. The row is DELETED so no phantom 'running'
    // cook can hold the checkpoint identity (resume) or the recipe (recipe path)
    // forever, and so the caller's existing no-row-on-failure behaviour is
    // preserved. If the delete itself fails, the row is best-effort moved out of
    // 'running', which also releases the gate. Never throws: the caller is already
    // returning a bounded 500 and must not have it replaced by a compensation
    // failure.
    private static void ReleaseCookReservation(string workspacePath, string cookId)
    {
        try
        {
            using SqliteConnection conn = OpenCookIndex(workspacePath);
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM cooks WHERE cook_id = $cook_id;";
            SqliteParameter p = cmd.CreateParameter();
            p.ParameterName = "$cook_id";
            p.Value = cookId;
            cmd.Parameters.Add(p);
            cmd.ExecuteNonQuery();
            return;
        }
        catch
        {
            // Fall through to the status release below.
        }

        try
        {
            using SqliteConnection conn = OpenCookIndex(workspacePath);
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE cooks SET status = 'interrupted', updated_at = $updated_at " +
                "WHERE cook_id = $cook_id;";

            SqliteParameter updated = cmd.CreateParameter();
            updated.ParameterName = "$updated_at";
            updated.Value = CookUtcNowIso();
            cmd.Parameters.Add(updated);

            SqliteParameter id = cmd.CreateParameter();
            id.ParameterName = "$cook_id";
            id.Value = cookId;
            cmd.Parameters.Add(id);

            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Best effort only.
        }
    }

    private static SqliteConnection OpenCookIndex(string workspacePath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile(workspacePath),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        var conn = new SqliteConnection(csb.ConnectionString);
        conn.Open();
        return conn;
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
        string trimmed = (checkpointPath ?? string.Empty).Trim();

        // The seam deliberately keeps the SUFFIX heuristic rather than the
        // production existence check, because its whole purpose is to project a
        // command for a SYNTHETIC path that does not exist on the machine running
        // the smoke. The production route never uses this heuristic: it classifies
        // by what is actually on disk (ResumeCheckpointPath.Validate).
        ResumeCheckpointKind seamKind =
            trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? ResumeCheckpointKind.JsonFile
                : ResumeCheckpointKind.Directory;

        (_, string command) = BuildResumeArgvAndCommand(
            trimmed, seamKind, force, resolvedChefKey: null);
        return command;
    }

    // Test-only reservation seam. Exercises the REAL atomic reservation
    // (ReserveResumeCookRowCore) against a caller-supplied workspace so the
    // concurrency behaviour can be proven WITHOUT an acquired engine, a Chef's
    // Key, a cook folder, a supervisor, or any spawn. It never runs PAX, never
    // starts a Bake, and never reads or returns a secret. Reachable only from the
    // test assembly via InternalsVisibleTo.
    //
    // Returns null when the reservation won, or the cook id of the resume that
    // already holds the identity.
    internal static string? TestSeamReserveResumeCookRow(
        string workspacePath, string cookId, string checkpointIdentity) =>
        ReserveResumeCookRowCore(
            workspacePath,
            cookId,
            checkpointIdentity,
            recipeSnapshotJson: "{\"kind\":\"resume\"}",
            commandArgvJson: "[]",
            paxScriptPath: "test-seam",
            paxScriptVersion: "test-seam",
            cookFolderRel: Path.Combine("Cooks", ResumeCookFolderBucket, cookId),
            createdAt: CookUtcNowIso());

    // Test-only compensation seam. Exercises the REAL release path so a
    // reservation whose cook never started can be proven not to leave a phantom
    // 'running' row behind. Spawns nothing and reads no secret.
    internal static void TestSeamReleaseResumeCookReservation(string workspacePath, string cookId) =>
        ReleaseCookReservation(workspacePath, cookId);

    // Runs the REAL sequence with the REAL resume adapter for an ARBITRARY trigger
    // — including triggers the resume host can never serve — and projects only
    // bounded values: the NAME of the authorization phase that was invoked, the
    // per-phase invocation counts, the disposition, the furthest phase, whether a
    // bounded refusal was recorded, that refusal's HTTP status, and whether
    // prepared state exists. The refusal BODY, the checkpoint path, the identity,
    // the invocation plan, and the cook id are deliberately NOT projected.
    //
    // It never spawns: the prepared state is never handed to SpawnAndSupervise.
    // Callers that must avoid every side effect pass a workspace path over the
    // MAX_PATH budget, which refuses inside phase 3 before the reservation.
    internal static (string AuthorizationPhase,
                     int GateCalls,
                     int AuthorizationCalls,
                     int PreparationCalls,
                     CookPreparationDisposition Disposition,
                     CookPreparationPhase FurthestPhase,
                     bool HasBoundedRefusal,
                     int RefusalStatus,
                     bool HasPreparedState) TestSeamResumeAuthorizationRouting(
        string workspacePath,
        VersionInfo versionInfo,
        EngineAcquisitionResult engine,
        string? checkpointPath,
        bool force,
        string? chefKeyId,
        CookTriggerKind trigger)
    {
        ResumeCookPreparationAdapter adapter = new(
            workspacePath, versionInfo, engine, checkpointPath, force, chefKeyId);

        CookPreparationOutcome outcome =
            CookPreparationSequence.Execute(trigger, adapter);

        (int Status, object Body)? refusal = adapter.BoundedRefusal;
        return (adapter.AuthorizationSelected.ToString(),
                adapter.GatePhaseCalls,
                adapter.AuthorizationPhaseCalls,
                adapter.PreparationPhaseCalls,
                outcome.Disposition,
                outcome.FurthestPhaseInvoked,
                refusal is not null,
                refusal?.Status ?? 0,
                adapter.Prepared.CookId is { Length: > 0 });
    }
}
