using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3i-B3 -- POST routes for the Recipe Takeout surface:
//
//   POST /api/v1/recipes/<ulid>/takeout      (export)
//   POST /api/v1/recipe-takeout/validate     (validate)
//   POST /api/v1/recipe-takeout/import       (import)
//
// 256 KiB raw-body cap enforced on validate + import BEFORE JSON
// parse so a malicious client cannot pad a tiny envelope with
// megabytes of whitespace. Export has no request body.
//
// The route layer only translates outcome envelopes from
// RecipeTakeoutService into HTTP responses; all envelope shape and
// error-code precedence lives in the service.
public static class RecipeTakeoutRoutes
{
    public const int    TakeoutBodyMaxBytes = 256 * 1024;
    private const string UlidPattern = RecipeTakeoutService.UlidPattern;
    private static readonly System.Text.RegularExpressions.Regex UlidRegex =
        new(UlidPattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    public static void Register(IEndpointRouteBuilder app, RecipeTakeoutService service)
    {
        // -------- Export --------
        app.MapPost("/api/v1/recipes/{recipeId}/takeout", async (HttpContext ctx, string recipeId) =>
        {
            if (!UlidRegex.IsMatch(recipeId))
            {
                await WriteJson(ctx, 400, new { error = "invalid_recipe_id", recipeId });
                return;
            }
            var outcome = service.Export(recipeId);
            switch (outcome)
            {
                case TakeoutExportOutcome.InvalidIdResult inv:
                    await WriteJson(ctx, 400, new { error = "invalid_recipe_id", recipeId = inv.RecipeId });
                    return;
                case TakeoutExportOutcome.NotFoundResult nf:
                    await WriteJson(ctx, 404, new { error = "recipe_not_found", recipeId = nf.RecipeId });
                    return;
                case TakeoutExportOutcome.SanitizationFailedResult:
                    await WriteJson(ctx, 500, new { error = "takeout_sanitization_failed" });
                    return;
                case TakeoutExportOutcome.EnvelopeInvalidResult:
                    await WriteJson(ctx, 500, new { error = "takeout_envelope_invalid" });
                    return;
                case TakeoutExportOutcome.OkResult ok:
                    await WriteExport(ctx, ok.Envelope, ok.Filename);
                    return;
                default:
                    await WriteJson(ctx, 500, new { error = "internal_error" });
                    return;
            }
        });

        // -------- Validate --------
        app.MapPost("/api/v1/recipe-takeout/validate", async (HttpContext ctx) =>
        {
            var body = await ReadCappedBodyAsync(ctx);
            if (body.Status == BodyReadStatus.TooLarge)
            {
                await WriteJson(ctx, 413, new { error = "payload_too_large", limitBytes = TakeoutBodyMaxBytes });
                return;
            }
            if (body.Status == BodyReadStatus.Empty)
            {
                await WriteJson(ctx, 400, new { error = "invalid_json" });
                return;
            }
            JsonObject? envelope = TryParseJsonObject(body.Bytes!);
            if (envelope is null)
            {
                await WriteJson(ctx, 400, new { error = "invalid_json" });
                return;
            }
            var outcome = service.Validate(envelope);
            await WriteValidateOutcome(ctx, outcome);
        });

        // -------- Import --------
        app.MapPost("/api/v1/recipe-takeout/import", async (HttpContext ctx) =>
        {
            var body = await ReadCappedBodyAsync(ctx);
            if (body.Status == BodyReadStatus.TooLarge)
            {
                await WriteJson(ctx, 413, new { error = "payload_too_large", limitBytes = TakeoutBodyMaxBytes });
                return;
            }
            if (body.Status == BodyReadStatus.Empty)
            {
                await WriteJson(ctx, 400, new { error = "invalid_json" });
                return;
            }
            JsonObject? wrapper = TryParseJsonObject(body.Bytes!);
            if (wrapper is null)
            {
                await WriteJson(ctx, 400, new { error = "invalid_json" });
                return;
            }
            var outcome = service.Import(wrapper);
            await WriteImportOutcome(ctx, outcome);
        });
    }

    // ----------------------------------------------------------------
    //  Response writers
    // ----------------------------------------------------------------

    private static async Task WriteExport(HttpContext ctx, JsonObject envelope, string filename)
    {
        // Compact JSON to match PS ConvertTo-Json -Compress. JsonNode.ToJsonString() with
        // no options defaults to WriteIndented=false; passing a JsonSerializerOptions instance
        // would require a TypeInfoResolver under net8.0.
        var json  = envelope.ToJsonString();
        var bytes = Utf8NoBom.GetBytes(json);
        ctx.Response.StatusCode    = 200;
        ctx.Response.ContentType   = "application/json; charset=utf-8";
        ctx.Response.ContentLength = bytes.LongLength;
        ctx.Response.Headers["Cache-Control"]                 = "no-store";
        ctx.Response.Headers["Content-Disposition"]           = "attachment; filename=\"" + filename + "\"";
        ctx.Response.Headers["Access-Control-Expose-Headers"] = "Content-Disposition";
        await ctx.Response.Body.WriteAsync(bytes);
    }

    private static async Task WriteValidateOutcome(HttpContext ctx, TakeoutValidateOutcome outcome)
    {
        switch (outcome)
        {
            case TakeoutValidateOutcome.InvalidJsonResult:
                await WriteJson(ctx, 400, new { error = "invalid_json" });
                return;
            case TakeoutValidateOutcome.ForbiddenSecretFieldResult fsf:
                await WriteJson(ctx, 400, BuildForbiddenSecretFieldEnvelope(fsf.FieldName, fsf.Kind, fsf.Path));
                return;
            case TakeoutValidateOutcome.StructuralFailureResult sf:
                await WriteJson(ctx, 400, new
                {
                    error  = sf.Code,
                    errors = sf.Errors.Select(SerializeError).ToArray(),
                });
                return;
            case TakeoutValidateOutcome.OkPreviewResult ok:
                await WriteJsonNode(ctx, 200, ok.Preview);
                return;
            default:
                await WriteJson(ctx, 500, new { error = "internal_error" });
                return;
        }
    }

    private static async Task WriteImportOutcome(HttpContext ctx, TakeoutImportOutcome outcome)
    {
        switch (outcome)
        {
            case TakeoutImportOutcome.InvalidJsonResult:
                await WriteJson(ctx, 400, new { error = "invalid_json" });
                return;
            case TakeoutImportOutcome.UnknownWrapperFieldResult uwf:
                await WriteJson(ctx, 400, new
                {
                    error  = "takeout_unknown_field",
                    errors = new[] { new { path = uwf.Path, message = "unknown top-level property" } },
                });
                return;
            case TakeoutImportOutcome.RecipeNameRequiredResult:
                await WriteJson(ctx, 400, new { error = "recipe_name_required" });
                return;
            case TakeoutImportOutcome.RecipeNameInvalidResult rni:
                await WriteJson(ctx, 400, new { error = "recipe_name_invalid", reason = rni.Reason });
                return;
            case TakeoutImportOutcome.TakeoutShapeInvalidResult ts:
                await WriteJson(ctx, 400, new
                {
                    error  = "takeout_shape_invalid",
                    errors = ts.Errors.Select(SerializeError).ToArray(),
                });
                return;
            case TakeoutImportOutcome.ForbiddenSecretFieldResult fsf:
                await WriteJson(ctx, 400, BuildForbiddenSecretFieldEnvelope(fsf.FieldName, fsf.Kind, fsf.Path));
                return;
            case TakeoutImportOutcome.StructuralFailureResult sf:
                await WriteJson(ctx, 400, new
                {
                    error  = sf.Code,
                    errors = sf.Errors.Select(SerializeError).ToArray(),
                });
                return;
            case TakeoutImportOutcome.NameConflictResult nc:
                await WriteJson(ctx, 409, new
                {
                    error          = "recipe_name_conflict",
                    message        = nc.Message,
                    nextSuggestion = nc.NextSuggestion,
                });
                return;
            case TakeoutImportOutcome.PersistFailedResult:
                await WriteJson(ctx, 500, new { error = "takeout_persist_failed" });
                return;
            case TakeoutImportOutcome.CreatedResult c:
                var body = new JsonObject
                {
                    ["ok"]         = true,
                    ["imported"]   = true,
                    ["recipeId"]   = c.RecipeId,
                    ["recipeName"] = c.RecipeName,
                    ["needsPrep"]  = new JsonObject
                    {
                        ["chefKey"] = c.NeedsChefKey,
                        ["mode"]    = c.ChefKeyMode,
                    },
                    ["recipe"] = c.Recipe.DeepClone(),
                };
                await WriteJsonNode(ctx, 201, body);
                return;
            default:
                await WriteJson(ctx, 500, new { error = "internal_error" });
                return;
        }
    }

    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static object BuildForbiddenSecretFieldEnvelope(string? fieldName, string? kind, string? path)
    {
        var d = new Dictionary<string, object?> { ["error"] = "takeout_contains_forbidden_secret_field" };
        if (fieldName is not null) d["fieldName"] = fieldName;
        if (kind is not null)      d["kind"]      = kind;
        if (path is not null)      d["path"]      = path;
        return d;
    }

    private static object SerializeError(RecipeTakeoutValidationError e) =>
        new { path = e.Path, message = e.Message };

    private static async Task WriteJson(HttpContext ctx, int status, object body)
    {
        ctx.Response.StatusCode  = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsJsonAsync(body);
    }

    private static async Task WriteJsonNode(HttpContext ctx, int status, JsonNode node)
    {
        var json  = node.ToJsonString();
        var bytes = Utf8NoBom.GetBytes(json);
        ctx.Response.StatusCode    = status;
        ctx.Response.ContentType   = "application/json; charset=utf-8";
        ctx.Response.ContentLength = bytes.LongLength;
        await ctx.Response.Body.WriteAsync(bytes);
    }

    private static JsonObject? TryParseJsonObject(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var node = JsonNode.Parse(doc.RootElement.GetRawText());
            return node as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private enum BodyReadStatus { Ok, Empty, TooLarge }

    private readonly record struct BodyReadResult(BodyReadStatus Status, byte[]? Bytes);

    private static async Task<BodyReadResult> ReadCappedBodyAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        if (req.ContentLength is long len && len > TakeoutBodyMaxBytes)
            return new(BodyReadStatus.TooLarge, null);

        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int read;
        while ((read = await req.Body.ReadAsync(buf)) > 0)
        {
            if (ms.Length + read > TakeoutBodyMaxBytes)
                return new(BodyReadStatus.TooLarge, null);
            ms.Write(buf, 0, read);
        }
        if (ms.Length == 0) return new(BodyReadStatus.Empty, null);
        return new(BodyReadStatus.Ok, ms.ToArray());
    }
}
