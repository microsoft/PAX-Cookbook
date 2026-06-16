using Microsoft.AspNetCore.Builder;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Routes;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native;

// Stage 3i-B3 -- composition root for the recipe-takeout route family:
//
//   POST /api/v1/recipes/<ulid>/takeout      (export)
//   POST /api/v1/recipe-takeout/validate     (validate)
//   POST /api/v1/recipe-takeout/import       (import)
//
// Gated on:
//   * workspacePaths + RecipesDir + DatabaseFile + WorkspaceFolderPath
//   * sqlite reader present
//   * paxAdapterVersion non-empty (used as INSERT stamp + as
//     recipe.paxAdapterVersion override on import)
//
// CookbookVersion / BundledPaxVersion / ReleaseChannel are only used
// for the export envelope provenance (createdBy + envelope-level
// version fingerprint). If they are missing, the export still works
// but those fields land as null in the envelope -- the sanitizer is
// already null-tolerant for these.
//
// When the gate is not satisfied this wiring is a no-op; the routes
// are not registered and any inbound POST falls through to the
// catch-all 404. Matches Stage 3i-B1 / Stage 3i-B2 patterns.
internal static class Stage3iB3Wiring
{
    public static void Register(
        WebApplication            app,
        WorkspacePaths?           workspacePaths,
        SqliteWorkspaceReader?    sqlite,
        Stage3iB3ServiceBundle?   overrideBundle)
    {
        if (workspacePaths is null) return;
        if (string.IsNullOrWhiteSpace(workspacePaths.WorkspaceFolderPath)) return;
        if (string.IsNullOrWhiteSpace(workspacePaths.RecipesDir)) return;
        if (string.IsNullOrWhiteSpace(workspacePaths.DatabaseFile)) return;
        if (sqlite is null) return;

        var paxAdapterVersion = overrideBundle?.PaxAdapterVersion;
        if (string.IsNullOrWhiteSpace(paxAdapterVersion)) return;

        var clock     = overrideBundle?.Clock           ?? (() => DateTimeOffset.UtcNow);
        var idFactory = overrideBundle?.RecipeIdFactory ?? Stage3iB1ServiceBundle.NewRecipeId;

        var snapshots = new RecipeSnapshotStore(workspacePaths.WorkspaceFolderPath);
        var rows      = new RecipeMutationStore(workspacePaths.DatabaseFile);
        var sanitizer = new RecipeTakeoutSanitizer();
        var validator = new RecipeTakeoutValidator();
        var importer  = new RecipeTakeoutImporter();

        var service = new RecipeTakeoutService(
            snapshots:             snapshots,
            rows:                  rows,
            sqlite:                sqlite,
            sanitizer:             sanitizer,
            validator:             validator,
            importer:              importer,
            clock:                 clock,
            idFactory:             idFactory,
            paxAdapterVersion:     paxAdapterVersion!,
            cookbookVersion:       overrideBundle?.CookbookVersion,
            bundledPaxVersion:     overrideBundle?.BundledPaxVersion,
            releaseChannel:        overrideBundle?.ReleaseChannel,
            workspaceInstallPath:  overrideBundle?.WorkspaceInstallPath,
            chefKeyLabelLookup:    overrideBundle?.ChefKeyLabelLookup);

        RecipeTakeoutRoutes.Register(app, service);
    }
}
