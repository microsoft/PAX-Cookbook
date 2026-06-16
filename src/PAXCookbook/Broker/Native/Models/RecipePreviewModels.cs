using System.Text.Json.Nodes;

namespace PAXCookbook.Broker.Native.Models;

// Stage 3i-B2 -- /api/v1/recipes/preview outcome shapes.
//
// Mirrors the four-status envelope returned by Invoke-RecipePreview
// (app\broker\Routes\Recipes.ps1 ~line 747). The discriminator family
// covers both branches the route accepts:
//
//   * Lookup-by-id     -- body carries { recipeId } and no identity
//     leaf. Outcomes for that branch: NotFound, FileMissing,
//     FileMalformed, UnsupportedSchemaVersion, LoadUnknownStatus.
//
//   * Full draft body  -- body carries identity (and optionally a
//     recipeId the operator wants to keep). Outcomes for that branch:
//     ValidationFailed (AJV-shape errors) for the Test-RecipeAll fail
//     path, plus the three auth-profile binding gates which are also
//     emitted as ValidationFailed since the PowerShell route reuses
//     New-ValidationError for them.
//
// All branches converge on the projection step (Get-PaxInvocationPlan)
// which can produce ProjectionFailed (mapped to a single-error
// validation_failed envelope anchored at /advanced/extraArguments,
// matching PS Invoke-RecipePreview's catch block).
//
// Ok carries the projected plan view that the SPA renders.
public abstract record RecipePreviewOutcome
{
    public sealed record InvalidJsonResult                    : RecipePreviewOutcome;
    public sealed record NotFoundResult(string RecipeId)      : RecipePreviewOutcome;
    public sealed record FileMissingResult(string RecipeId)   : RecipePreviewOutcome;
    public sealed record FileMalformedResult(string RecipeId, string? Detail) : RecipePreviewOutcome;
    public sealed record UnsupportedSchemaVersionResult(string RecipeId, string? Detail) : RecipePreviewOutcome;
    public sealed record LoadUnknownStatusResult(string RecipeId, string Status) : RecipePreviewOutcome;
    public sealed record ValidationFailedResult(IReadOnlyList<ValidationError> Errors) : RecipePreviewOutcome;
    public sealed record OkResult(
        string RecipeId,
        string Command,
        IReadOnlyList<string> Argv,
        string ExtraArguments,
        string SpawnCommand,
        IReadOnlyList<string> SpawnArgv) : RecipePreviewOutcome;

    public static RecipePreviewOutcome InvalidJson { get; } = new InvalidJsonResult();
    public static RecipePreviewOutcome NotFound(string id)                        => new NotFoundResult(id);
    public static RecipePreviewOutcome FileMissing(string id)                     => new FileMissingResult(id);
    public static RecipePreviewOutcome FileMalformed(string id, string? d)        => new FileMalformedResult(id, d);
    public static RecipePreviewOutcome UnsupportedSchemaVersion(string id, string? d) => new UnsupportedSchemaVersionResult(id, d);
    public static RecipePreviewOutcome LoadUnknownStatus(string id, string s)     => new LoadUnknownStatusResult(id, s);
    public static RecipePreviewOutcome ValidationFailed(IReadOnlyList<ValidationError> errors) => new ValidationFailedResult(errors);
    public static RecipePreviewOutcome Ok(string recipeId, string command, IReadOnlyList<string> argv,
        string extraArguments, string spawnCommand, IReadOnlyList<string> spawnArgv)
        => new OkResult(recipeId, command, argv, extraArguments, spawnCommand, spawnArgv);
}
