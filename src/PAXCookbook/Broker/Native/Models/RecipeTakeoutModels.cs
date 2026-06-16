using System.Text.Json.Nodes;

namespace PAXCookbook.Broker.Native.Models;

// Stage 3i-B3 -- shared records for the Recipe Takeout pipeline.
//
// Port of the inline shapes used by RecipeTakeoutSanitizer.psm1 /
// RecipeTakeoutImporter.psm1 / Routes\RecipeTakeout.ps1 (PowerShell
// broker). The C# port keeps the same field names, the same JSON
// envelope shape, and the same constants so the SPA wire contract
// (recipe-list.js TAKEOUT_VALIDATE_PATH / TAKEOUT_IMPORT_PATH) does
// not move.

// envelope.recipe (decoded validation/import body) -- the route
// layer pre-decodes this once and hands it to every helper, so all
// the helpers can stay JsonObject-driven without re-parsing.
public sealed record RecipeTakeoutStructuralVerdict(
    bool Ok,
    IReadOnlyList<RecipeTakeoutValidationError> Errors);

public sealed record RecipeTakeoutValidationError(string Path, string Message);

public sealed record RecipeTakeoutWarning(string Code, string? Path = null, string? Detail = null);

// Output of New-RecipeFromTakeoutEnvelope (PS) -- the materialized
// pending recipe plus the Chef's Key prep flags the import route
// surfaces back to the SPA.
public sealed record RecipeTakeoutMaterialized(
    JsonObject Recipe,
    bool       NeedsChefKey,
    string?    ChefKeyMode,
    string?    ImportedFromId);
