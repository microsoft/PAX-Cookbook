namespace PAXCookbook.App;

// Read-only preview-load extension of the recipe read model (X6). Reuses the
// existing private GetRecipeRow + ReadRecipeFile read paths and projects the
// authoritative recipe document into the mutable CLR value tree the validator
// and PAX adapter consume. This adds NO new I/O surface: it opens the workspace
// read-only exactly like the GET detail route, never writes, never repairs a
// file, and never touches the PAX engine.
internal static partial class RecipeReadModel
{
    // Discriminated result of a stored-recipe preview load. On success Status
    // is 200 and Recipe is the parsed value tree; otherwise Status / ErrorBody
    // carry the same envelope the GET detail route uses and Recipe is null.
    internal readonly record struct PreviewLoadResult(
        int Status,
        object? ErrorBody,
        Dictionary<string, object?>? Recipe);

    // Oracle: the lookup branch of Invoke-RecipePreview. The discriminator
    // (recipeId present && identity absent) is decided by the caller; this only
    // performs the read-only load + status mapping.
    public static PreviewLoadResult LoadForPreview(string workspacePath, string recipeId)
    {
        IReadOnlyDictionary<string, object?>? row = GetRecipeRow(workspacePath, recipeId);
        if (row is null || row["deleted_at"] is not null)
        {
            return new PreviewLoadResult(404, new { error = "not_found", recipeId }, null);
        }

        RecipeFileResult loaded = ReadRecipeFile(workspacePath, recipeId);
        switch (loaded.Status)
        {
            case "ok":
                var tree = JsonModel.FromElement(loaded.Recipe!.Value) as Dictionary<string, object?>;
                if (tree is null)
                {
                    // A non-object recipe document would have been classified
                    // malformed by ReadRecipeFile; reaching here means the parsed
                    // root was not an object after all. Surface it as malformed
                    // rather than projecting a non-recipe value.
                    return new PreviewLoadResult(422,
                        new { error = "recipe_file_malformed", recipeId, detail = "json_root_not_object" }, null);
                }
                // Back-compat (CK-2 / B2): drop a stray deprecated auth.authProfileId
                // from a legacy stored recipe so the preview / readiness projection
                // sees only the chefKeyId binding.
                RecipeValidationModel.StripDeprecatedAuthFields(tree);
                return new PreviewLoadResult(200, null, tree);
            case "missing":
                return new PreviewLoadResult(404, new { error = "recipe_file_missing", recipeId }, null);
            case "malformed":
                return new PreviewLoadResult(422,
                    new { error = "recipe_file_malformed", recipeId, detail = loaded.Detail }, null);
            case "unsupported_schema_version":
                return new PreviewLoadResult(422, new
                {
                    error = "recipe_unsupported_schema_version",
                    recipeId,
                    supportedSchemaVersion = SupportedSchemaVersion,
                    detail = loaded.Detail,
                }, null);
            default:
                return new PreviewLoadResult(500,
                    new { error = "recipe_load_unknown_status", recipeId, status = loaded.Status }, null);
        }
    }
}
