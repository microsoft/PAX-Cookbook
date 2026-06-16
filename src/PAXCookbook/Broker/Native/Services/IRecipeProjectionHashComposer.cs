using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3h -- recipe projection-hash composer abstraction. The
// scheduled-task PUT route MUST stamp recipe_projection_hash on the
// scheduled_tasks row at registration time so that future drift
// detection (Stage 3g GET / V1.S07 health) can flag staleness when
// the recipe definition changes. Hash format and inputs MUST be
// bit-identical to the PowerShell broker's Get-RecipeProjectionHash
// (app\broker\Pax\Adapter.psm1) -- the native implementation
// delegates to the PS function via a hidden one-shot pwsh sidecar
// so parity is guaranteed by construction.
//
// Inputs mirror Get-RecipeProjectionHash:
//   * Recipe         -- workspace recipe JSON file (.recipe.json).
//   * PaxScriptPath  -- absolute path to the bundled PAX script.
//   * AuthProfile    -- resolved auth_profile row (null for auth
//                       modes that don't consume one).
//   * ExecutionMode  -- 'local-scheduled' for the scheduled-task
//                       PUT route, mirroring
//                       $Script:ScheduledTaskExecutionMode.
//   * PaxScriptVersion -- VERSION.json paxScript.version.
//
// Returns RecipeProjectionHashResult with Sha256Hex on success and
// a structured Error otherwise.
public interface IRecipeProjectionHashComposer
{
    Task<RecipeProjectionHashResult> ComposeAsync(
        string recipeFilePath,
        string paxScriptPath,
        AuthProfileRow? authProfile,
        string executionMode,
        string paxScriptVersion,
        CancellationToken cancellationToken = default);
}
