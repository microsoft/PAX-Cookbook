using Microsoft.AspNetCore.Builder;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Routes;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native;

// Stage 3i-B1 -- composition root for the recipe mutation surface
// (POST + PUT + DELETE on /api/v1/recipes). Gated on:
//
//   * workspacePaths.RecipesDir          (snapshot store + trash)
//   * workspacePaths.DatabaseFile        (row writes)
//   * bundle.PaxAdapterVersion           (server-stamp paxAdapterVersion)
//   * bundle.CreatedByTemplate           (server-stamp createdBy)
//
// When any gate is unsatisfied the route family is NOT registered.
// Unmatched POST/PUT/DELETE on /api/v1/recipes then falls through to
// the catch-all 404 -- matching the pre-3i-B1 native broker shape.
//
// Registration order matters: the host calls this after
// RecipeReadRoutes.Register (GET-only). The MapPost / MapPut /
// MapDelete handlers added here cover distinct verbs, so there is no
// conflict with the existing GET routes -- the ASP.NET Core router
// matches by (path, verb) tuple.
internal static class Stage3iB1Wiring
{
    public static void Register(
        WebApplication           app,
        WorkspacePaths?          workspacePaths,
        Stage3iB1ServiceBundle?  overrideBundle)
    {
        if (workspacePaths is null) return;
        if (string.IsNullOrWhiteSpace(workspacePaths.RecipesDir)) return;
        if (string.IsNullOrWhiteSpace(workspacePaths.DatabaseFile)) return;

        var paxAdapterVersion = overrideBundle?.PaxAdapterVersion;
        var createdBy         = overrideBundle?.CreatedByTemplate;
        if (string.IsNullOrWhiteSpace(paxAdapterVersion) || createdBy is null) return;

        var clock     = overrideBundle?.Clock           ?? (() => DateTimeOffset.UtcNow);
        var idFactory = overrideBundle?.RecipeIdFactory ?? Stage3iB1ServiceBundle.NewRecipeId;

        var snapshots = new RecipeSnapshotStore(workspacePaths.WorkspaceFolderPath);
        var rows      = new RecipeMutationStore(workspacePaths.DatabaseFile);
        var validator = new RecipeValidator();
        var service   = new RecipeMutationService(
            snapshots:         snapshots,
            rows:              rows,
            validator:         validator,
            clock:             clock,
            idFactory:         idFactory,
            paxAdapterVersion: paxAdapterVersion!,
            createdBy:         createdBy);

        RecipeMutationRoutes.Register(app, service);
    }
}
