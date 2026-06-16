using System.Globalization;
using System.Text.Json.Nodes;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B3 -- port of RecipeTakeoutImporter.psm1::
// Resolve-RecipeTakeoutTargetName and New-RecipeFromTakeoutEnvelope.
//
// Pure transforms: no I/O, no SQLite, no RecipeValidator call. The
// caller (RecipeTakeoutService) is responsible for collision lookup,
// persistence, and post-materialization overrides (identity.name,
// recipeSchemaVersion, paxAdapterVersion, recipeId).
public sealed class RecipeTakeoutImporter
{
    public const int ImporterMaxNumericSuffix = 99;

    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    // K-5 (revised in UXR_F2D): Windows-style numeric suffix walk.
    // Returns the trimmed proposed name when no collision exists,
    // "Name (1)" through "Name (99)" when collisions occur, or null
    // when all 100 candidates collide.
    public string? ResolveTargetName(string proposedName, IEnumerable<string>? existingNames)
    {
        if (string.IsNullOrWhiteSpace(proposedName)) return null;
        var trimmed = proposedName.Trim();
        var existing = (existingNames ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n.Trim())
            .ToArray();

        if (!existing.Contains(trimmed, NameComparer))
            return trimmed;

        for (int i = 1; i <= ImporterMaxNumericSuffix; i++)
        {
            var candidate = string.Format(CultureInfo.InvariantCulture, "{0} ({1})", trimmed, i);
            if (!existing.Contains(candidate, NameComparer))
                return candidate;
        }
        return null;
    }

    // Walks the envelope and returns the "pending" recipe payload
    // plus Chef's Key prep flags. Caller MUST:
    //   * Override identity.name with the chef-provided trimmed name.
    //   * Override recipeSchemaVersion to the broker's authoritative value.
    //   * Override paxAdapterVersion to the broker's authoritative value.
    //   * Override recipeId to the freshly-generated ULID.
    //
    // Throws when the envelope is missing 'recipe' / 'recipe' is not
    // an object. The route layer converts that into 500
    // takeout_persist_failed.
    public RecipeTakeoutMaterialized MaterializePending(
        JsonObject     envelope,
        DateTimeOffset nowUtc,
        string         newRecipeId,
        string?        cookbookVersion,
        string?        bundledPaxVersion,
        string?        releaseChannel)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        if (envelope["recipe"] is not JsonObject recipeNode)
            throw new InvalidOperationException("envelope.recipe must be an object");

        var pending = (JsonObject)recipeNode.DeepClone();

        // 1. Strip residual authProfileId (defense in depth).
        if (pending["auth"] is JsonObject authBlock && authBlock.ContainsKey("authProfileId"))
            authBlock.Remove("authProfileId");

        // 2. Restamp timestamps (ISO 8601 'o' format, UTC).
        var iso = nowUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        pending["createdAt"] = iso;
        pending["updatedAt"] = iso;

        // 3. Restamp createdBy while preserving createdBy.fromTemplate.
        JsonNode? fromTemplate = null;
        if (pending["createdBy"] is JsonObject cb && cb["fromTemplate"] is JsonNode ftNode)
            fromTemplate = ftNode.DeepClone();
        var newCreatedBy = new JsonObject();
        if (!string.IsNullOrWhiteSpace(cookbookVersion))
            newCreatedBy["cookbookVersion"] = cookbookVersion;
        if (!string.IsNullOrWhiteSpace(bundledPaxVersion))
            newCreatedBy["bundledPaxVersion"] = bundledPaxVersion;
        if (!string.IsNullOrWhiteSpace(releaseChannel))
            newCreatedBy["releaseChannel"] = releaseChannel;
        if (fromTemplate is not null)
            newCreatedBy["fromTemplate"] = fromTemplate;
        pending["createdBy"] = newCreatedBy;

        // 4. Stamp the fresh recipeId.
        if (!string.IsNullOrWhiteSpace(newRecipeId))
            pending["recipeId"] = newRecipeId;

        // 5. Determine Chef's Key prep flags.
        bool needsChefKey = false;
        string? chefKeyMode = null;
        if (envelope["chefKey"] is JsonObject ck)
        {
            if (ck["requirement"] is JsonValue rv && rv.TryGetValue<string>(out var req) && req == "required")
            {
                needsChefKey = true;
                if (ck["mode"] is JsonValue mv && mv.TryGetValue<string>(out var mode))
                    chefKeyMode = mode;
            }
        }
        if (!needsChefKey && pending["auth"] is JsonObject ab
            && ab["mode"] is JsonValue mvAuth && mvAuth.TryGetValue<string>(out var authMode)
            && RecipeTakeoutSanitizer.AppRegistrationModes.Contains(authMode))
        {
            needsChefKey = true;
            chefKeyMode  = authMode;
        }

        string? importedFromId = null;
        if (envelope["sourceRecipe"] is JsonObject sr
            && sr["id"] is JsonValue idv && idv.TryGetValue<string>(out var srcId))
            importedFromId = srcId;

        return new RecipeTakeoutMaterialized(
            Recipe:          pending,
            NeedsChefKey:    needsChefKey,
            ChefKeyMode:     chefKeyMode,
            ImportedFromId:  importedFromId);
    }
}
