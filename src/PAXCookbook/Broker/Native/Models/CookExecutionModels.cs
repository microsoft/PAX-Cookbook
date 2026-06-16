namespace PAXCookbook.Broker.Native.Models;

// Stage 3e -- request / response / error shapes for the native cook-
// start route. Parity with the PowerShell broker's Invoke-CookStart
// (Routes/Cooks.ps1 ~line 1262) limited to what Stage 3e ports:
//
//   * 201 success returns { cookId, recipeId, cookFolder } where
//     cookFolder is ABSOLUTE on the wire (the workspace-relative form
//     is what lives in the cooks.cook_folder column, not the response).
//   * Each failure mode maps to a controlled (status, code, message)
//     triple. Stage 3e adds two NEW sentinels NOT present in the PS
//     broker -- `auth_mode_not_implemented_native_stage3e` (App* and
//     ManagedIdentity recipes refused) and `adapter_sidecar_failed`
//     (the one-shot pwsh sidecar that hosts Get-PaxInvocationPlan
//     could not be started or returned non-JSON). Both surface as a
//     501 / 500 respectively so a caller can distinguish "this stage
//     does not handle that yet" from "the upstream PAX adapter
//     rejected the recipe".
public sealed record CookStartResponse(
    string CookId,
    string RecipeId,
    string CookFolder);

public sealed record CookStartError(
    int      StatusCode,
    string   Code,
    string   Message,
    object?  Details = null);

// Convenience factories so each route caller (and each service that
// raises a controlled error) constructs the canonical envelope rather
// than re-inventing the shape. The status codes mirror the PS broker
// status mapping verbatim where Stage 3e ports the behavior, and
// pick the closest controlled status where Stage 3e defers.
public static class CookStartErrors
{
    public static CookStartError InvalidRecipeId(string detail) => new(
        StatusCode: 400, Code: "invalid_recipe_id",
        Message:    "recipeId is not a valid Crockford ULID.",
        Details:    new { detail });

    public static CookStartError RecipeNotFound(string recipeId) => new(
        StatusCode: 404, Code: "not_found",
        Message:    "Recipe not found.",
        Details:    new { recipeId });

    public static CookStartError RecipeFileMissing(string recipeId) => new(
        StatusCode: 500, Code: "recipe_file_missing",
        Message:    "Recipe row exists but the recipe file is missing on disk.",
        Details:    new { recipeId });

    public static CookStartError RecipeInvalid(string detail, string? path = null) => new(
        StatusCode: 412, Code: "recipe_invalid",
        Message:    "Recipe is invalid.",
        Details:    path is null
                        ? (object)new { detail }
                        : (object)new { detail, path });

    public static CookStartError PaxScriptIntegrity(string expected, string actual) => new(
        StatusCode: 500, Code: "pax_script_integrity",
        Message:    "Bundled PAX script SHA-256 mismatch.",
        Details:    new { expected, actual });

    public static CookStartError PaxScriptMissing(string paxScriptPath) => new(
        StatusCode: 500, Code: "pax_script_integrity",
        Message:    "Bundled PAX script not found on disk.",
        Details:    new { paxScriptPath });

    public static CookStartError VersionFileMissing(string versionFilePath) => new(
        StatusCode: 500, Code: "pax_script_integrity",
        Message:    "VERSION.json is missing or unreadable.",
        Details:    new { versionFilePath });

    public static CookStartError RecipeBusy(string cookId) => new(
        StatusCode: 409, Code: "recipe_busy",
        Message:    "A cook is already running for this recipe.",
        Details:    new { cookId });

    public static CookStartError AuthModeDeferred(string mode) => new(
        StatusCode: 501, Code: "auth_mode_not_implemented_native_stage3e",
        Message:    "This auth mode is not yet implemented in the native broker (Stage 3e ports WebLogin / DeviceCode only).",
        Details:    new { mode });

    public static CookStartError ExecutionModeRejected(string executionMode) => new(
        StatusCode: 412, Code: "recipe_invalid",
        Message:    "executionMode must be local-manual for the manual cook entry point.",
        Details:    new { executionMode, path = "/executionMode" });

    public static CookStartError AdapterSidecarFailed(string detail) => new(
        StatusCode: 500, Code: "adapter_sidecar_failed",
        Message:    "PowerShell adapter sidecar failed.",
        Details:    new { detail });

    public static CookStartError CookFolderCreateFailed(string detail) => new(
        StatusCode: 500, Code: "cook_folder_create_failed",
        Message:    "Failed to create the per-cook folder.",
        Details:    new { detail });

    public static CookStartError CookInitFilesFailed(string detail) => new(
        StatusCode: 500, Code: "cook_init_files_failed",
        Message:    "Failed to write one or more cook initialization files.",
        Details:    new { detail });

    public static CookStartError CookRowInsertFailed(string detail) => new(
        StatusCode: 500, Code: "cook_row_insert_failed",
        Message:    "Failed to insert the cook row.",
        Details:    new { detail });

    public static CookStartError SupervisorSpawnFailed(string detail) => new(
        StatusCode: 500, Code: "supervisor_spawn_failed",
        Message:    "Failed to spawn the PAX child process.",
        Details:    new { detail });

    public static CookStartError WorkspaceUnavailable() => new(
        StatusCode: 500, Code: "workspace_unavailable",
        Message:    "Workspace folder is not configured.");
}
