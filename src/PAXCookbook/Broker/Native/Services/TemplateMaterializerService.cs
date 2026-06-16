using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B2 -- POST /api/v1/templates/{id}/materialize orchestrator.
//
// Ports Invoke-TemplateMaterialize (app\broker\Routes\Templates.ps1
// ~line 226). Six gates in source order:
//
//   1. Catalog lookup        -> 404 template_not_found
//   2. PAX compatibility     -> 412 template_incompatible
//   3. JSON parse / object   -> 400 invalid_json
//   4. Materialize-body validator (TemplateMaterializeBodySchema)
//                            -> 400 materialize_body_invalid
//   5. ConvertTo-MaterializedRecipe + Test-RecipeAll
//                            -> 400 materialize_recipe_invalid
//   6. Initialize dirs + write file + insert row
//                            -> 201 { recipeId, recipe }
//
// File-first, row-second persistence (Stage 3i-B1 parity). Row insert
// failure rolls back the file on best-effort.
public sealed class TemplateMaterializerService
{
    private readonly TemplateCatalogReader  _catalog;
    private readonly RecipeSnapshotStore    _snapshots;
    private readonly RecipeMutationStore    _rows;
    private readonly RecipeValidator        _validator;
    private readonly Func<DateTimeOffset>   _clock;
    private readonly Func<string>           _idFactory;
    private readonly string                 _paxAdapterVersion;
    private readonly string                 _bundledPaxVersion;
    private readonly RecipeCreatedBy        _createdBy;

    public TemplateMaterializerService(
        TemplateCatalogReader  catalog,
        RecipeSnapshotStore    snapshots,
        RecipeMutationStore    rows,
        RecipeValidator        validator,
        Func<DateTimeOffset>   clock,
        Func<string>           idFactory,
        string                 paxAdapterVersion,
        string                 bundledPaxVersion,
        RecipeCreatedBy        createdBy)
    {
        _catalog           = catalog;
        _snapshots         = snapshots;
        _rows              = rows;
        _validator         = validator;
        _clock             = clock;
        _idFactory         = idFactory;
        _paxAdapterVersion = paxAdapterVersion;
        _bundledPaxVersion = bundledPaxVersion;
        _createdBy         = createdBy;
    }

    public TemplateMaterializeOutcome Materialize(string templateId, JsonNode? requestBody)
    {
        if (!_catalog.TryGetDocument(templateId, out var doc))
            return TemplateMaterializeOutcome.NotFound(templateId);

        var minPax = TryReadString(doc, "minPaxScriptVersion") ?? "";
        var paxErr = TemplatePaxCompatibilityChecker.Check(minPax, _bundledPaxVersion);
        if (paxErr is not null)
        {
            return TemplateMaterializeOutcome.Incompatible(
                templateId,
                _bundledPaxVersion,
                minPax,
                paxErr);
        }

        if (requestBody is not JsonObject body)
            return TemplateMaterializeOutcome.InvalidJson;

        var bodyErrors = TemplateMaterializeBodyValidator.Validate(body);
        if (bodyErrors.Count > 0)
            return TemplateMaterializeOutcome.BodyInvalid(bodyErrors);

        var nowDto = _clock();
        var now    = ToIso(nowDto);
        var id     = _idFactory();
        var recipe = BuildMaterializedRecipe(doc, body, now, id, templateId);

        var verdict = _validator.TestAll(recipe);
        if (!verdict.Ok)
            return TemplateMaterializeOutcome.RecipeInvalid(templateId, id, verdict.Errors);

        var templateVersion = TryReadString(doc, "templateVersion") ?? "";
        var sourceRef       = templateId + "@" + templateVersion;

        _snapshots.EnsureDirs();
        var write = _snapshots.Write(id, recipe);
        try
        {
            _rows.Insert(new RecipeInsertRow(
                RecipeId:            id,
                Name:                TryReadStringPath(recipe, "identity", "name") ?? "",
                PaxAdapterVersion:   _paxAdapterVersion,
                RecipeSchemaVersion: RecipeValidator.SupportedSchemaVersion,
                FilePath:            write.FilePath,
                FileHash:            write.FileHash,
                CreatedAt:           now,
                UpdatedAt:           now,
                Source:              "template",
                SourceRef:           sourceRef));
        }
        catch
        {
            try { File.Delete(write.FilePath); } catch { /* best-effort rollback */ }
            throw;
        }

        return TemplateMaterializeOutcome.Created(id, recipe);
    }

    // ConvertTo-MaterializedRecipe parity (Templates.ps1 ~line 92).
    // Defensive reads of the recipeDefaults sub-blocks; missing leaves
    // yield Hashtable-equivalent defaults (false for bool, empty
    // string for string).
    internal JsonObject BuildMaterializedRecipe(
        JsonElement template,
        JsonObject  body,
        string      nowIso,
        string      recipeId,
        string      templateId)
    {
        var defaultsRoot     = TryReadObject(template, "recipeDefaults");
        var defaultsIng      = TryReadObject(defaultsRoot, "ingredients");
        var defaultsIngM365  = TryReadObject(defaultsIng, "m365Usage");
        var defaultsIngEntra = TryReadObject(defaultsIng, "entraUserData");
        var defaultsProc     = TryReadObject(defaultsRoot, "processing");
        var defaultsAuth     = TryReadObject(defaultsRoot, "auth");

        var includeM365  = TryReadBool(defaultsIngM365,  "includeM365Usage");
        var includeUser  = TryReadBool(defaultsIngEntra, "includeUserInfo");
        var rollup       = TryReadString(defaultsProc,   "rollup") ?? "";
        var defaultsMode = TryReadString(defaultsAuth,   "mode")   ?? "";

        var templateVersion = TryReadString(template, "templateVersion") ?? "";

        var createdBy = new JsonObject
        {
            ["cookbookVersion"]   = _createdBy.CookbookVersion,
            ["bundledPaxVersion"] = _createdBy.BundledPaxVersion,
            ["releaseChannel"]    = _createdBy.ReleaseChannel,
            ["fromTemplate"]      = new JsonObject
            {
                ["templateId"]      = templateId,
                ["templateVersion"] = templateVersion,
            },
        };

        return new JsonObject
        {
            ["recipeId"]            = recipeId,
            ["recipeSchemaVersion"] = RecipeValidator.SupportedSchemaVersion,
            ["paxAdapterVersion"]   = _paxAdapterVersion,
            ["createdAt"]           = nowIso,
            ["updatedAt"]           = nowIso,
            ["createdBy"]           = createdBy,
            ["identity"] = new JsonObject
            {
                ["name"] = TryReadStringPath(body, "identity", "name") ?? "",
            },
            ["ingredients"] = new JsonObject
            {
                ["m365Usage"]     = new JsonObject { ["includeM365Usage"] = includeM365 },
                ["entraUserData"] = new JsonObject { ["includeUserInfo"]  = includeUser },
            },
            ["query"] = new JsonObject
            {
                ["startDate"] = TryReadStringPath(body, "query", "startDate") ?? "",
                ["endDate"]   = TryReadStringPath(body, "query", "endDate")   ?? "",
            },
            ["processing"] = new JsonObject
            {
                ["rollup"] = rollup,
            },
            ["destinations"] = new JsonObject
            {
                ["fact"] = new JsonObject
                {
                    ["path"] = TryReadStringPath(body, "destinations", "fact", "path") ?? "",
                },
            },
            ["auth"] = new JsonObject
            {
                ["mode"]     = defaultsMode,
                ["tenantId"] = TryReadStringPath(body, "auth", "tenantId") ?? "",
            },
        };
    }

    // ---------- helpers ----------

    private static string ToIso(DateTimeOffset dto) =>
        dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string? TryReadString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static JsonElement TryReadObject(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return default;
        if (!el.TryGetProperty(name, out var v)) return default;
        return v.ValueKind == JsonValueKind.Object ? v : default;
    }

    private static bool TryReadBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(name, out var v)) return false;
        return v.ValueKind == JsonValueKind.True;
    }

    private static string? TryReadStringPath(JsonObject root, params string[] segments)
    {
        JsonNode? cursor = root;
        foreach (var seg in segments)
        {
            if (cursor is not JsonObject obj || !obj.TryGetPropertyValue(seg, out var next)) return null;
            cursor = next;
        }
        return cursor is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
