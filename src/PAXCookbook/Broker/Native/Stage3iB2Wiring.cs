using Microsoft.AspNetCore.Builder;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Routes;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native;

// Stage 3i-B2 -- composition root for the recipe-preview + template-
// materialize surface.
//
// Two route families, two independent gates:
//
//   * POST /api/v1/recipes/preview
//       Gated on (workspacePaths, RecipesDir, DatabaseFile,
//       PaxAdapterVersion, CreatedByTemplate) AND a preview plan
//       provider. The provider is either:
//
//         (a) the test-injected IRecipePreviewPlanProvider on the
//             bundle override, OR
//         (b) a DefaultRecipePreviewPlanProvider over a freshly
//             constructed Stage-3e PaxInvocationPlanProvider when
//             (pwsh + adapterModule + paxScript) are all present.
//
//       Note: NativeBrokerHost already builds a PaxInvocationPlanProvider
//       inside the Stage 3e cook-execution block. Stage 3i-B2 builds
//       a SECOND instance for preview rather than wiring through the
//       Stage 3e construction site because:
//         1. The Stage 3e block is gated on additional state
//            (PaxScriptIntegrityVerifier needs versionInfo) and lives
//            inside a different conditional; coupling the two wirings
//            risks regressing 3a/3b test fixtures.
//         2. PaxInvocationPlanProvider holds no expensive state -- it
//            shells out a fresh pwsh sidecar per call -- so a second
//            instance has no per-host cost.
//
//   * POST /api/v1/templates/{id}/materialize
//       Gated on (workspacePaths, RecipesDir, DatabaseFile,
//       PaxAdapterVersion, BundledPaxVersion, CreatedByTemplate) AND
//       a non-null template catalog. Catalog may be empty -- in that
//       case every request returns 404 template_not_found (PS parity).
//
// When any gate is unsatisfied the corresponding route is NOT
// registered. Unmatched POST then falls through to the catch-all 404
// served by MapFallback, matching the pre-3i-B2 native broker shape.
internal static class Stage3iB2Wiring
{
    public static void Register(
        WebApplication            app,
        WorkspacePaths?           workspacePaths,
        SqliteWorkspaceReader?    sqlite,
        TemplateCatalogReader?    catalog,
        Stage3iB2ServiceBundle?   overrideBundle,
        string?                   pwshPath,
        string?                   adapterModulePath,
        string?                   paxScriptPath)
    {
        if (workspacePaths is null) return;
        if (string.IsNullOrWhiteSpace(workspacePaths.RecipesDir)) return;
        if (string.IsNullOrWhiteSpace(workspacePaths.DatabaseFile)) return;
        if (sqlite is null) return;

        var paxAdapterVersion = overrideBundle?.PaxAdapterVersion;
        var bundledPaxVersion = overrideBundle?.BundledPaxVersion;
        var createdBy         = overrideBundle?.CreatedByTemplate;
        if (string.IsNullOrWhiteSpace(paxAdapterVersion)) return;
        if (createdBy is null) return;

        var clock     = overrideBundle?.Clock           ?? (() => DateTimeOffset.UtcNow);
        var idFactory = overrideBundle?.RecipeIdFactory ?? Stage3iB1ServiceBundle.NewRecipeId;

        var snapshots = new RecipeSnapshotStore(workspacePaths.WorkspaceFolderPath);
        var rows      = new RecipeMutationStore(workspacePaths.DatabaseFile);
        var validator = new RecipeValidator();

        // ---- Preview route ----
        IRecipePreviewPlanProvider? planProvider = overrideBundle?.PreviewPlanProvider;
        if (planProvider is null
            && !string.IsNullOrWhiteSpace(pwshPath)
            && !string.IsNullOrWhiteSpace(adapterModulePath)
            && !string.IsNullOrWhiteSpace(paxScriptPath))
        {
            planProvider = new DefaultRecipePreviewPlanProvider(
                new PaxInvocationPlanProvider(pwshPath!, adapterModulePath!));
        }
        if (planProvider is not null)
        {
            var previewService = new RecipePreviewService(
                rows:              rows,
                snapshots:         snapshots,
                validator:         validator,
                workspaceReader:   sqlite,
                planProvider:      planProvider,
                paxScriptPath:     paxScriptPath ?? string.Empty,
                paxAdapterVersion: paxAdapterVersion!,
                createdBy:         createdBy,
                idFactory:         idFactory);
            RecipePreviewRoutes.Register(app, previewService);
        }

        // ---- Materialize route ----
        if (catalog is not null && !string.IsNullOrWhiteSpace(bundledPaxVersion))
        {
            var materializer = new TemplateMaterializerService(
                catalog:           catalog,
                snapshots:         snapshots,
                rows:              rows,
                validator:         validator,
                clock:             clock,
                idFactory:         idFactory,
                paxAdapterVersion: paxAdapterVersion!,
                bundledPaxVersion: bundledPaxVersion!,
                createdBy:         createdBy);
            TemplateMaterializeRoutes.Register(app, materializer);
        }
    }
}
