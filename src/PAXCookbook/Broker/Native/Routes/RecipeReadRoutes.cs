using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3c -- read-only Recipes surface.
//
//   GET /api/v1/recipes         -- list of non-deleted recipes
//                                  (Routes/Recipes.ps1 list view).
//   GET /api/v1/recipes/{ulid}  -- DB-row metadata only. The
//                                  PowerShell broker also performs a
//                                  4-status file probe (ok / missing
//                                  / malformed / unsupported_schema_version)
//                                  via Resolve-RecipeFileLoad, which
//                                  parses the .pantry.json file on
//                                  disk. That file-load half is
//                                  deferred -- see Stage 3c record.
//                                  The native broker returns only the
//                                  `meta` block here, plus a sentinel
//                                  `recipeFileLoad` indicating
//                                  deferral.
public static class RecipeReadRoutes
{
    public static void Register(IEndpointRouteBuilder app, SqliteWorkspaceReader reader)
    {
        app.MapGet("/api/v1/recipes", () =>
        {
            var rows = reader.TryListRecipes();
            if (rows is null)
            {
                return Results.Json(new
                {
                    error = "workspace_database_unavailable",
                    detail = "cookbook.sqlite missing or unreadable",
                },
                statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Json(new
            {
                recipes = rows.Select(r => new
                {
                    recipeId     = r.RecipeId,
                    name         = r.Name,
                    status       = r.Status,
                    lastCookedAt = r.LastCookedAt,
                    lastCookId   = r.LastCookId,
                    createdAt    = r.CreatedAt,
                    updatedAt    = r.UpdatedAt,
                }).ToArray(),
            });
        });

        app.MapGet("/api/v1/recipes/{ulid}", (string ulid) =>
        {
            if (string.IsNullOrWhiteSpace(ulid))
            {
                return Results.Json(new
                {
                    error = "recipe_id_required",
                },
                statusCode: StatusCodes.Status400BadRequest);
            }

            var row = reader.GetRecipeById(ulid);
            if (row is null)
            {
                return Results.Json(new
                {
                    error = "recipe_not_found",
                    recipeId = ulid,
                },
                statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(new
            {
                meta = new
                {
                    recipeId             = row.RecipeId,
                    name                 = row.Name,
                    filePath             = row.FilePath,
                    fileHash             = row.FileHash,
                    status               = row.Status,
                    isPinned             = row.IsPinned,
                    paxAdapterVersion    = row.PaxAdapterVersion,
                    recipeSchemaVersion  = row.RecipeSchemaVersion,
                    source               = row.Source,
                    sourceRef            = row.SourceRef,
                    lastValidatedAt      = row.LastValidatedAt,
                    lastValidationStatus = row.LastValidationStatus,
                    lastCookedAt         = row.LastCookedAt,
                    lastCookId           = row.LastCookId,
                    createdAt            = row.CreatedAt,
                    updatedAt            = row.UpdatedAt,
                    deletedAt            = row.DeletedAt,
                },
                // Deferred: file-load 4-status probe + parsed body.
                recipeFileLoad = new
                {
                    deferred = true,
                    deferredReason = "stage_3c_metadata_only",
                    plannedStage = "3d_or_later",
                },
            });
        });
    }
}
