using System.Text.Json.Nodes;

namespace PAXCookbook.Broker.Native.Models;

// Stage 3i-B2 -- POST /api/v1/templates/{id}/materialize outcome shapes.
//
// Mirrors the envelope chain emitted by Invoke-TemplateMaterialize
// (app\broker\Routes\Templates.ps1 ~line 226):
//
//   404 template_not_found        -> NotFoundResult
//   412 template_incompatible     -> IncompatibleResult
//   400 invalid_json              -> InvalidJsonResult
//   400 materialize_body_invalid  -> BodyInvalidResult
//   400 materialize_recipe_invalid-> RecipeInvalidResult
//   201 { recipeId, recipe }      -> CreatedResult
//
// IncompatibleResult carries the bundledPaxVersion + minPaxScriptVersion
// plus a single inner ValidationError detail anchored at
// /minPaxScriptVersion (parity with the PS Test-TemplatePaxCompatibility
// emission shape).
public abstract record TemplateMaterializeOutcome
{
    public sealed record NotFoundResult(string TemplateId) : TemplateMaterializeOutcome;
    public sealed record IncompatibleResult(
        string TemplateId,
        string BundledPaxVersion,
        string MinPaxScriptVersion,
        ValidationError Detail) : TemplateMaterializeOutcome;
    public sealed record InvalidJsonResult : TemplateMaterializeOutcome;
    public sealed record BodyInvalidResult(IReadOnlyList<ValidationError> Errors) : TemplateMaterializeOutcome;
    public sealed record RecipeInvalidResult(
        string TemplateId,
        string RecipeId,
        IReadOnlyList<ValidationError> Errors) : TemplateMaterializeOutcome;
    public sealed record CreatedResult(string RecipeId, JsonObject Recipe) : TemplateMaterializeOutcome;

    public static TemplateMaterializeOutcome InvalidJson { get; } = new InvalidJsonResult();
    public static TemplateMaterializeOutcome NotFound(string id) => new NotFoundResult(id);
    public static TemplateMaterializeOutcome Incompatible(string id, string bundled, string min, ValidationError detail)
        => new IncompatibleResult(id, bundled, min, detail);
    public static TemplateMaterializeOutcome BodyInvalid(IReadOnlyList<ValidationError> errors)
        => new BodyInvalidResult(errors);
    public static TemplateMaterializeOutcome RecipeInvalid(string templateId, string recipeId, IReadOnlyList<ValidationError> errors)
        => new RecipeInvalidResult(templateId, recipeId, errors);
    public static TemplateMaterializeOutcome Created(string recipeId, JsonObject recipe)
        => new CreatedResult(recipeId, recipe);
}
