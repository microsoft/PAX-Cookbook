using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- cook stop / kill / resume orchestration.
//
// STOP: cooperative cancellation. Looks up the cook in
// ICookProcessRegistry; 404 cook_not_active if absent. Otherwise
// calls RequestStop and returns 202 with cookId.
//
// KILL: force termination. Same registry lookup; 404 cook_not_active
// if absent. Calls ForceKill and returns 202 with cookId.
//
// RESUME: most complex of the three. Validates the cookId, looks up
// the parent cook row (read-only), verifies status="interrupted",
// verifies error_class is in CookClosureReasons.Resumable (rejects
// cancel_kill), verifies the checkpoint file exists at
// <CookFolder>\checkpoint.json, verifies the recipe is loadable
// (looks up recipes table by parent's recipe_id), composes a
// CookResumeSpawnRequest with a freshly minted cookId, and hands
// off to ICookResumeSpawner. If the spawner returns Outcome="spawned"
// the route returns 201; if Outcome="deferred" the route returns 501
// with the spawner's failure code (Stage 3j wiring will swap in a
// real spawner).
//
// The new cookId is a lowercase GUID without braces, matching the
// PS broker's New-CookId convention.
public sealed class CookControlService
{
    private readonly SqliteWorkspaceReader _reader;
    private readonly ICookProcessRegistry  _registry;
    private readonly ICookResumeSpawner    _resumeSpawner;
    private readonly Func<string>          _newCookId;

    public CookControlService(
        SqliteWorkspaceReader reader,
        ICookProcessRegistry  registry,
        ICookResumeSpawner    resumeSpawner,
        Func<string>?         newCookId = null)
    {
        _reader        = reader        ?? throw new ArgumentNullException(nameof(reader));
        _registry      = registry      ?? throw new ArgumentNullException(nameof(registry));
        _resumeSpawner = resumeSpawner ?? throw new ArgumentNullException(nameof(resumeSpawner));
        _newCookId     = newCookId     ?? (() => Guid.NewGuid().ToString("D").ToLowerInvariant());
    }

    public StopOutcome Stop(string cookId)
    {
        if (!IsCookIdShape(cookId)) return new StopOutcome.InvalidCookId(cookId);
        if (!_registry.TryGet(cookId, out _)) return new StopOutcome.NotActive(cookId);
        var ok = _registry.RequestStop(cookId);
        if (!ok) return new StopOutcome.SignalFailed(cookId);
        return new StopOutcome.Accepted(cookId);
    }

    public KillOutcome Kill(string cookId)
    {
        if (!IsCookIdShape(cookId)) return new KillOutcome.InvalidCookId(cookId);
        if (!_registry.TryGet(cookId, out _)) return new KillOutcome.NotActive(cookId);
        var ok = _registry.ForceKill(cookId);
        if (!ok) return new KillOutcome.SignalFailed(cookId);
        return new KillOutcome.Accepted(cookId);
    }

    public ResumeOutcome Resume(string parentCookId)
    {
        if (!IsCookIdShape(parentCookId))
            return new ResumeOutcome.InvalidCookId(parentCookId);

        var parent = _reader.GetCookById(parentCookId);
        if (parent is null) return new ResumeOutcome.NotFound(parentCookId);

        if (!string.Equals(parent.Status, "interrupted", StringComparison.Ordinal))
            return new ResumeOutcome.NotResumable(parentCookId, "status_not_interrupted", parent.Status);

        if (string.IsNullOrEmpty(parent.ErrorClass)
            || Array.IndexOf(CookClosureReasons.Resumable, parent.ErrorClass) < 0)
        {
            return new ResumeOutcome.NotResumable(
                parentCookId,
                "closure_reason_not_resumable",
                parent.ErrorClass ?? "<null>");
        }

        if (string.IsNullOrEmpty(parent.RecipeId))
            return new ResumeOutcome.NotResumable(parentCookId, "recipe_id_missing", parent.Status);

        var recipe = _reader.GetRecipeById(parent.RecipeId);
        if (recipe is null)
            return new ResumeOutcome.RecipeInvalid(parentCookId, parent.RecipeId, "recipe_not_found");
        if (!string.IsNullOrEmpty(recipe.DeletedAt))
            return new ResumeOutcome.RecipeInvalid(parentCookId, parent.RecipeId, "recipe_deleted");
        if (!File.Exists(recipe.FilePath))
            return new ResumeOutcome.RecipeInvalid(parentCookId, parent.RecipeId, "recipe_file_missing");

        // Checkpoint file path convention (matches the PS broker's
        // Save-CookCheckpoint output path):
        //   <CookFolder>\checkpoint.json
        var checkpointFile = Path.Combine(parent.CookFolder, "checkpoint.json");
        if (!File.Exists(checkpointFile))
            return new ResumeOutcome.CheckpointVanished(parentCookId, checkpointFile);

        var newCookId   = _newCookId();
        var newRunDir   = Path.Combine(Path.GetDirectoryName(parent.CookFolder)!, newCookId);
        var spawnReq = new CookResumeSpawnRequest(
            ParentCookId:       parentCookId,
            NewCookId:          newCookId,
            RecipeId:           parent.RecipeId,
            CookFolder:         newRunDir,
            CheckpointFilePath: checkpointFile,
            RecipeFilePath:     recipe.FilePath,
            PaxScriptPath:      parent.PaxScriptPath,
            PaxScriptVersion:   parent.PaxScriptVersion);
        var spawnResult = _resumeSpawner.Spawn(spawnReq);

        return spawnResult.Outcome switch
        {
            "spawned"  => new ResumeOutcome.Spawned(parentCookId, newCookId, parent.RecipeId, newRunDir),
            "deferred" => new ResumeOutcome.Deferred(
                parentCookId,
                spawnResult.FailureCode   ?? "cook_resume_spawn_deferred_native_stage3i",
                spawnResult.FailureDetail ?? "spawn deferred"),
            _ => new ResumeOutcome.SpawnFailed(
                parentCookId,
                spawnResult.FailureCode   ?? "cook_resume_spawn_failed",
                spawnResult.FailureDetail ?? "unspecified failure"),
        };
    }

    // Lowercase GUID without braces. The PS broker uses the same
    // convention for cookId; cook ids inside the SQLite store are
    // normalized to lowercase.
    private static bool IsCookIdShape(string? cookId)
    {
        if (string.IsNullOrEmpty(cookId)) return false;
        if (cookId.Length != 36) return false;
        for (int i = 0; i < cookId.Length; i++)
        {
            var c = cookId[i];
            if (i == 8 || i == 13 || i == 18 || i == 23)
            {
                if (c != '-') return false;
            }
            else if (!IsHexLower(c))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsHexLower(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');

    public abstract record StopOutcome
    {
        public sealed record Accepted(string CookId)        : StopOutcome;
        public sealed record InvalidCookId(string CookId)   : StopOutcome;
        public sealed record NotActive(string CookId)       : StopOutcome;
        public sealed record SignalFailed(string CookId)    : StopOutcome;
    }

    public abstract record KillOutcome
    {
        public sealed record Accepted(string CookId)        : KillOutcome;
        public sealed record InvalidCookId(string CookId)   : KillOutcome;
        public sealed record NotActive(string CookId)       : KillOutcome;
        public sealed record SignalFailed(string CookId)    : KillOutcome;
    }

    public abstract record ResumeOutcome
    {
        public sealed record Spawned(string ParentCookId, string NewCookId, string RecipeId, string CookFolder) : ResumeOutcome;
        public sealed record Deferred(string ParentCookId, string FailureCode, string FailureDetail)             : ResumeOutcome;
        public sealed record SpawnFailed(string ParentCookId, string FailureCode, string FailureDetail)          : ResumeOutcome;
        public sealed record InvalidCookId(string CookId)                                                        : ResumeOutcome;
        public sealed record NotFound(string CookId)                                                             : ResumeOutcome;
        public sealed record NotResumable(string CookId, string Reason, string Detail)                           : ResumeOutcome;
        public sealed record RecipeInvalid(string CookId, string RecipeId, string Reason)                        : ResumeOutcome;
        public sealed record CheckpointVanished(string CookId, string CheckpointPath)                            : ResumeOutcome;
    }
}
