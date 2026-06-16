using System.Text.Json.Nodes;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B2 -- POST /api/v1/recipes/preview orchestrator.
//
// Ports Invoke-RecipePreview (app\broker\Routes\Recipes.ps1 ~line 747).
// Two-branch discriminator:
//
//   * Lookup-by-id  -- body has { recipeId } AND no `identity` leaf.
//   * Draft preview -- body looks like a full recipe (with `identity`).
//
// Lookup path:
//   1. Check the recipes row; 404 not_found if missing or soft-deleted.
//   2. Read the snapshot file via RecipeSnapshotStore.Read; map the
//      four-status envelope (missing/malformed/unsupported) to the
//      preview HTTP envelopes.
//   3. Use the on-disk recipe as the projection input.
//
// Draft path:
//   1. Stamp recipeId (new if absent), recipeSchemaVersion,
//      paxAdapterVersion, createdBy. createdAt/updatedAt are NOT
//      stamped (preview is stateless).
//   2. Test-RecipeAll. On fail -> 400 validation_failed with AJV
//      errors.
//
// Both branches converge:
//   3. If recipe.auth.mode is AppRegistrationSecret or
//      AppRegistrationCertificate, require authProfileId, resolve the
//      row, assert profile.mode == recipe.auth.mode. Each gate maps
//      to a single AJV-shape error anchored at /auth/authProfileId.
//   4. Project via IRecipePreviewPlanProvider. SidecarFailed or
//      RecipeRejected -> single-error envelope anchored at
//      /advanced/extraArguments (PS catch-block parity).
//   5. Return Ok with command/argv/extraArguments/spawn.{command,argv}.
public sealed class RecipePreviewService
{
    private readonly RecipeMutationStore         _rows;
    private readonly RecipeSnapshotStore         _snapshots;
    private readonly RecipeValidator             _validator;
    private readonly SqliteWorkspaceReader       _workspaceReader;
    private readonly IRecipePreviewPlanProvider  _planProvider;
    private readonly string                      _paxScriptPath;
    private readonly string                      _paxAdapterVersion;
    private readonly RecipeCreatedBy             _createdBy;
    private readonly Func<string>                _idFactory;

    public RecipePreviewService(
        RecipeMutationStore         rows,
        RecipeSnapshotStore         snapshots,
        RecipeValidator             validator,
        SqliteWorkspaceReader       workspaceReader,
        IRecipePreviewPlanProvider  planProvider,
        string                      paxScriptPath,
        string                      paxAdapterVersion,
        RecipeCreatedBy             createdBy,
        Func<string>                idFactory)
    {
        _rows              = rows;
        _snapshots         = snapshots;
        _validator         = validator;
        _workspaceReader   = workspaceReader;
        _planProvider      = planProvider;
        _paxScriptPath     = paxScriptPath;
        _paxAdapterVersion = paxAdapterVersion;
        _createdBy         = createdBy;
        _idFactory         = idFactory;
    }

    public RecipePreviewOutcome Preview(JsonNode? requestBody)
    {
        if (requestBody is not JsonObject body) return RecipePreviewOutcome.InvalidJson;

        bool hasRecipeId = body.ContainsKey("recipeId");
        bool hasIdentity = body.ContainsKey("identity");
        bool isLookup    = hasRecipeId && !hasIdentity;

        JsonObject recipe;
        string recipeId;

        if (isLookup)
        {
            recipeId = TryGetString(body, "recipeId") ?? "";
            if (string.IsNullOrEmpty(recipeId)) return RecipePreviewOutcome.InvalidJson;

            var row = _rows.GetActiveRow(recipeId);
            if (row is null) return RecipePreviewOutcome.NotFound(recipeId);

            var load = _snapshots.Read(recipeId, RecipeValidator.SupportedSchemaVersion);
            switch (load.Status)
            {
                case RecipeFileStatus.Missing:
                    return RecipePreviewOutcome.FileMissing(recipeId);
                case RecipeFileStatus.Malformed:
                    return RecipePreviewOutcome.FileMalformed(recipeId, load.Detail);
                case RecipeFileStatus.UnsupportedSchemaVersion:
                    return RecipePreviewOutcome.UnsupportedSchemaVersion(recipeId, load.Detail);
                case RecipeFileStatus.Ok:
                    recipe = load.Recipe!;
                    break;
                default:
                    return RecipePreviewOutcome.LoadUnknownStatus(recipeId, load.Status.ToString());
            }
        }
        else
        {
            // Draft path. Server fills the fields the editor doesn't
            // ask the user to manage so validation succeeds for a UI
            // body that has only the human-managed leaves. None of
            // these fills persist; preview is stateless.
            if (!body.ContainsKey("recipeId"))
                body["recipeId"] = _idFactory();
            if (!body.ContainsKey("recipeSchemaVersion"))
                body["recipeSchemaVersion"] = RecipeValidator.SupportedSchemaVersion;
            if (!body.ContainsKey("paxAdapterVersion"))
                body["paxAdapterVersion"] = _paxAdapterVersion;
            if (!body.ContainsKey("createdBy"))
            {
                body["createdBy"] = new JsonObject
                {
                    ["cookbookVersion"]   = _createdBy.CookbookVersion,
                    ["bundledPaxVersion"] = _createdBy.BundledPaxVersion,
                    ["releaseChannel"]    = _createdBy.ReleaseChannel,
                };
            }

            var verdict = _validator.TestAll(body);
            if (!verdict.Ok) return RecipePreviewOutcome.ValidationFailed(verdict.Errors);

            recipe   = body;
            recipeId = TryGetString(body, "recipeId") ?? "";
        }

        // Auth-profile binding gates (both paths). Matches PS Phase AF.
        var authMode      = TryGetStringPath(recipe, "auth", "mode") ?? "";
        var authProfileId = TryGetStringPath(recipe, "auth", "authProfileId") ?? "";
        if (authMode == "AppRegistrationSecret" || authMode == "AppRegistrationCertificate")
        {
            if (string.IsNullOrWhiteSpace(authProfileId))
            {
                return RecipePreviewOutcome.ValidationFailed(new[]
                {
                    new ValidationError(
                        InstancePath: "/auth/authProfileId",
                        Keyword:      "required",
                        Message:      "must have required property 'authProfileId' when auth.mode is '" + authMode + "'",
                        Params:       new Dictionary<string, object?> { ["missingProperty"] = "authProfileId" }),
                });
            }
            var profile = _workspaceReader.GetAuthProfileById(authProfileId);
            if (profile is null)
            {
                return RecipePreviewOutcome.ValidationFailed(new[]
                {
                    new ValidationError(
                        InstancePath: "/auth/authProfileId",
                        Keyword:      "profileNotFound",
                        Message:      "auth profile '" + authProfileId + "' does not exist",
                        Params:       new Dictionary<string, object?> { ["authProfileId"] = authProfileId }),
                });
            }
            if (!string.Equals(profile.Mode, authMode, StringComparison.Ordinal))
            {
                return RecipePreviewOutcome.ValidationFailed(new[]
                {
                    new ValidationError(
                        InstancePath: "/auth/authProfileId",
                        Keyword:      "profileModeMismatch",
                        Message:      "auth profile '" + authProfileId + "' is mode '" + profile.Mode +
                                      "' but recipe.auth.mode is '" + authMode + "'",
                        Params:       new Dictionary<string, object?>
                        {
                            ["recipeMode"]  = authMode,
                            ["profileMode"] = profile.Mode,
                        }),
                });
            }
        }

        // Projection via the adapter sidecar (or test stub).
        var recipeJson = recipe.ToJsonString();
        var planResult = _planProvider.Resolve(recipeJson, _paxScriptPath);
        if (planResult.Status != PaxInvocationPlanStatus.Ok || planResult.Plan is null)
        {
            var detail = planResult.Detail ?? "projection_failed";
            return RecipePreviewOutcome.ValidationFailed(new[]
            {
                new ValidationError(
                    InstancePath: "/advanced/extraArguments",
                    Keyword:      "projection",
                    Message:      detail,
                    Params:       new Dictionary<string, object?>()),
            });
        }

        var plan = planResult.Plan;
        return RecipePreviewOutcome.Ok(
            recipeId:       recipeId,
            command:        plan.PaxCommand,
            argv:           plan.PaxArgv,
            extraArguments: plan.ExtraArguments,
            spawnCommand:   plan.SpawnCommand,
            spawnArgv:      plan.SpawnArgv);
    }

    private static string? TryGetString(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node)) return null;
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    private static string? TryGetStringPath(JsonObject root, params string[] segments)
    {
        JsonNode? cursor = root;
        foreach (var seg in segments)
        {
            if (cursor is not JsonObject obj || !obj.TryGetPropertyValue(seg, out var next)) return null;
            cursor = next;
        }
        return cursor is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
