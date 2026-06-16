using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B3 -- orchestrator for the three takeout endpoints. Pure
// in-memory pipeline above SQLite / file IO; the routes only translate
// the outcome envelopes into HTTP responses.
public sealed class RecipeTakeoutService
{
    public const string UlidPattern = "^[0-9A-HJKMNP-TV-Z]{26}$";

    private static readonly System.Text.RegularExpressions.Regex UlidRegex =
        new(UlidPattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly RecipeSnapshotStore     _snapshots;
    private readonly RecipeMutationStore     _rows;
    private readonly SqliteWorkspaceReader   _sqlite;
    private readonly RecipeTakeoutSanitizer  _sanitizer;
    private readonly RecipeTakeoutValidator  _validator;
    private readonly RecipeTakeoutImporter   _importer;
    private readonly Func<DateTimeOffset>    _clock;
    private readonly Func<string>            _idFactory;
    private readonly string                  _paxAdapterVersion;
    private readonly string?                 _cookbookVersion;
    private readonly string?                 _bundledPaxVersion;
    private readonly string?                 _releaseChannel;
    private readonly string?                 _workspaceInstallPath;
    private readonly Func<string, string?>?  _chefKeyLabelLookup;

    public RecipeTakeoutService(
        RecipeSnapshotStore     snapshots,
        RecipeMutationStore     rows,
        SqliteWorkspaceReader   sqlite,
        RecipeTakeoutSanitizer  sanitizer,
        RecipeTakeoutValidator  validator,
        RecipeTakeoutImporter   importer,
        Func<DateTimeOffset>    clock,
        Func<string>            idFactory,
        string                  paxAdapterVersion,
        string?                 cookbookVersion,
        string?                 bundledPaxVersion,
        string?                 releaseChannel,
        string?                 workspaceInstallPath,
        Func<string, string?>?  chefKeyLabelLookup)
    {
        _snapshots             = snapshots;
        _rows                  = rows;
        _sqlite                = sqlite;
        _sanitizer             = sanitizer;
        _validator             = validator;
        _importer              = importer;
        _clock                 = clock;
        _idFactory             = idFactory;
        _paxAdapterVersion     = paxAdapterVersion;
        _cookbookVersion       = cookbookVersion;
        _bundledPaxVersion     = bundledPaxVersion;
        _releaseChannel        = releaseChannel;
        _workspaceInstallPath  = workspaceInstallPath;
        _chefKeyLabelLookup    = chefKeyLabelLookup;
    }

    // ================================================================
    //  Export
    // ================================================================

    public TakeoutExportOutcome Export(string recipeId)
    {
        if (string.IsNullOrWhiteSpace(recipeId) || !UlidRegex.IsMatch(recipeId))
            return TakeoutExportOutcome.InvalidId(recipeId);

        // Read row first so a recipe with a missing file is still
        // distinguishable from "row not found". Both surface as 404
        // recipe_not_found per PS Read-RecipeFile semantics.
        var meta = _rows.GetActiveRow(recipeId);
        if (meta is null) return TakeoutExportOutcome.NotFound(recipeId);

        // Read recipe from disk via snapshot store.
        var load = _snapshots.Read(recipeId, RecipeValidator.SupportedSchemaVersion);
        if (load.Status != RecipeFileStatus.Ok || load.Recipe is null)
            return TakeoutExportOutcome.NotFound(recipeId);

        var recipe = load.Recipe;

        // Optional Chef's Key source-display label. Never throws.
        string? sourceLabel = null;
        if (recipe["auth"] is JsonObject auth
            && auth["authProfileId"] is JsonValue av && av.TryGetValue<string>(out var apid)
            && !string.IsNullOrWhiteSpace(apid))
        {
            try { sourceLabel = _chefKeyLabelLookup?.Invoke(apid); } catch { sourceLabel = null; }
        }

        JsonObject envelope;
        try
        {
            envelope = _sanitizer.BuildEnvelope(
                sourceRecipe:         recipe,
                exportedAtUtc:        _clock(),
                cookbookVersion:      _cookbookVersion,
                bundledPaxVersion:    _bundledPaxVersion,
                releaseChannel:       _releaseChannel,
                workspaceInstallPath: _workspaceInstallPath,
                chefKeySourceLabel:   sourceLabel);
        }
        catch
        {
            return TakeoutExportOutcome.SanitizationFailed();
        }

        if (!IsEnvelopeStructureSane(envelope))
            return TakeoutExportOutcome.EnvelopeInvalid();

        string? sourceName = null;
        if (recipe["identity"] is JsonObject id
            && id["name"] is JsonValue nv && nv.TryGetValue<string>(out var s))
            sourceName = s;
        var filename = BuildFilenameSlug(sourceName);

        return TakeoutExportOutcome.Ok(envelope, filename);
    }

    public static string BuildFilenameSlug(string? recipeName)
    {
        if (string.IsNullOrWhiteSpace(recipeName)) return "recipe.paxrecipe.json";
        var lower = recipeName.ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in lower)
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            else sb.Append('-');
        }
        var s = sb.ToString();
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-");
        s = s.Trim('-');
        if (s.Length > 60) s = s[..60].TrimEnd('-');
        if (s.Length == 0) s = "recipe";
        return s + ".paxrecipe.json";
    }

    // Defensive post-check that the sanitizer emitted the expected
    // top-level shape. Mirrors PS Test-RecipeTakeoutEnvelopeStructure.
    public static bool IsEnvelopeStructureSane(JsonObject env)
    {
        if (env is null) return false;
        if (env["takeoutSchemaVersion"] is not JsonValue v1
            || !(v1.TryGetValue<int>(out var sv) && sv == RecipeTakeoutSanitizer.TakeoutSchemaVersion))
            return false;
        if (env["kind"] is not JsonValue v2
            || !(v2.TryGetValue<string>(out var k) && k == RecipeTakeoutSanitizer.TakeoutKindConstant))
            return false;
        if (!env.ContainsKey("exportedAtUtc")) return false;
        if (env["recipe"] is not JsonObject recipe) return false;
        if (!env.ContainsKey("excluded")) return false;
        if (recipe["auth"] is JsonObject auth && auth.ContainsKey("authProfileId")) return false;
        return true;
    }

    // ================================================================
    //  Validate
    // ================================================================

    public TakeoutValidateOutcome Validate(JsonObject? envelope)
    {
        if (envelope is null) return TakeoutValidateOutcome.InvalidJson();

        // Defense-in-depth.
        try
        {
            var forbidden = RecipeTakeoutSanitizer.FindForbiddenFieldName(
                envelope, RecipeTakeoutSanitizer.TakeoutForbiddenSecretFields);
            if (forbidden is not null)
                return TakeoutValidateOutcome.ForbiddenSecretField(fieldName: forbidden);
            var secretTag = RecipeTakeoutSanitizer.FindForbiddenSecretValue(envelope);
            if (secretTag is not null)
                return TakeoutValidateOutcome.ForbiddenSecretField(kind: secretTag);
        }
        catch
        {
            return TakeoutValidateOutcome.ForbiddenSecretField();
        }

        // Explicit authProfileId leakage.
        if (envelope["recipe"] is JsonObject recipeNode
            && recipeNode["auth"] is JsonObject authNode
            && authNode.ContainsKey("authProfileId"))
        {
            return TakeoutValidateOutcome.ForbiddenSecretField(
                fieldName: "authProfileId", path: "/recipe/auth/authProfileId");
        }

        var verdict = _validator.ValidateStructure(envelope);
        if (!verdict.Ok)
        {
            var code = _validator.MapErrorsToCode(verdict.Errors);
            return TakeoutValidateOutcome.StructuralFailure(code, verdict.Errors);
        }

        var preview = BuildPreview(envelope);
        return TakeoutValidateOutcome.OkPreview(preview);
    }

    // Walks the envelope to build the validate preview shape locked
    // in recipe_takeout_api_contract_draft.md. Pure transform; no DB
    // beyond the existing-name lookup for nameSuggestion.
    private JsonObject BuildPreview(JsonObject envelope)
    {
        JsonObject? recipe = envelope["recipe"] as JsonObject;
        string? recipeName = null;
        if (recipe?["identity"] is JsonObject idObj
            && idObj["name"] is JsonValue nv && nv.TryGetValue<string>(out var name))
            recipeName = name;

        string? sourceRecipeId  = null;
        JsonNode? sourceTemplate = null;
        if (envelope["sourceRecipe"] is JsonObject sr)
        {
            if (sr["id"] is JsonValue idv && idv.TryGetValue<string>(out var srid))
                sourceRecipeId = srid;
            if (sr["sourceTemplate"] is JsonNode st) sourceTemplate = st.DeepClone();
        }

        bool chefKeyRequired = false;
        string? chefKeyMode  = null;
        string? chefKeyLabel = null;
        if (envelope["chefKey"] is JsonObject ck)
        {
            if (ck["requirement"] is JsonValue rv && rv.TryGetValue<string>(out var req) && req == "required")
                chefKeyRequired = true;
            if (ck["mode"] is JsonValue mv && mv.TryGetValue<string>(out var m))               chefKeyMode  = m;
            if (ck["sourceDisplayLabel"] is JsonValue dv && dv.TryGetValue<string>(out var d)) chefKeyLabel = d;
        }
        if (!chefKeyRequired && recipe?["auth"] is JsonObject ab
            && ab["mode"] is JsonValue am && am.TryGetValue<string>(out var authMode)
            && RecipeTakeoutSanitizer.AppRegistrationModes.Contains(authMode))
        {
            chefKeyRequired = true;
            chefKeyMode ??= authMode;
        }

        var warnings = new JsonArray();
        bool hasPathWarn = false, hasTenantWarn = false;
        if (envelope["warnings"] is JsonArray ws)
        {
            foreach (var w in ws)
            {
                if (w is not JsonObject wo) continue;
                string code = "";
                if (wo["code"] is JsonValue cv && cv.TryGetValue<string>(out var cs)) code = cs;
                var entry = new JsonObject
                {
                    ["code"]   = code,
                    ["origin"] = "export",
                };
                if (wo["path"] is JsonValue pv && pv.TryGetValue<string>(out var ps))     entry["path"]   = ps;
                if (wo["detail"] is JsonValue dv2 && dv2.TryGetValue<string>(out var ds)) entry["detail"] = ds;
                warnings.Add(entry);
                if (code.StartsWith("path_", StringComparison.Ordinal))        hasPathWarn   = true;
                if (code == "tenant_id_present_review_recommended")            hasTenantWarn = true;
            }
        }

        bool needsChefKey = chefKeyRequired;
        bool needsPaths   = hasPathWarn;
        bool needsTenant  = hasTenantWarn;
        bool needsAny     = needsChefKey || needsPaths || needsTenant;
        string state      = needsAny ? "needs_prep" : "ready_after_import";

        var reasons = new List<string>();
        if (needsChefKey) reasons.Add("chef key binding");
        if (needsPaths)   reasons.Add("path review");
        if (needsTenant)  reasons.Add("tenant review");
        string message = needsAny
            ? "Envelope is valid. Prep Station required after import: " + string.Join(", ", reasons) + "."
            : "Envelope is valid. Recipe is ready to bake after import.";

        var preview = new JsonObject
        {
            ["ok"]    = true,
            ["valid"] = true,
            ["state"] = state,
            ["recipe"] = new JsonObject
            {
                ["name"]           = recipeName,
                ["sourceRecipeId"] = sourceRecipeId,
                ["sourceTemplate"] = sourceTemplate,
            },
            ["chefKey"] = new JsonObject
            {
                ["required"]           = chefKeyRequired,
                ["mode"]               = chefKeyMode,
                ["sourceDisplayLabel"] = chefKeyLabel,
            },
            ["warnings"]  = warnings,
            ["needsPrep"] = new JsonObject
            {
                ["chefKey"] = needsChefKey,
                ["paths"]   = needsPaths,
                ["tenant"]  = needsTenant,
            },
            ["message"]        = message,
            ["nameSuggestion"] = BuildNameSuggestion(envelope),
        };
        return preview;
    }

    private JsonObject BuildNameSuggestion(JsonObject envelope)
    {
        string? source = null;
        if (envelope["recipe"] is JsonObject r
            && r["identity"] is JsonObject id
            && id["name"] is JsonValue nv && nv.TryGetValue<string>(out var s))
            source = s.Trim();

        var existingNames = GetExistingActiveRecipeNames();
        if (string.IsNullOrEmpty(source))
        {
            return new JsonObject
            {
                ["sourceName"]    = null,
                ["suggestedName"] = null,
                ["collision"]     = false,
                ["collisionRule"] = "windows_numeric_suffix",
                ["maxSuffix"]     = RecipeTakeoutImporter.ImporterMaxNumericSuffix,
            };
        }
        bool collision = existingNames.Any(n =>
            string.Equals(n.Trim(), source, StringComparison.OrdinalIgnoreCase));
        string? suggested = collision
            ? _importer.ResolveTargetName(source, existingNames)
            : source;
        return new JsonObject
        {
            ["sourceName"]    = source,
            ["suggestedName"] = suggested,
            ["collision"]     = collision,
            ["collisionRule"] = "windows_numeric_suffix",
            ["maxSuffix"]     = RecipeTakeoutImporter.ImporterMaxNumericSuffix,
        };
    }

    public IReadOnlyList<string> GetExistingActiveRecipeNames()
    {
        var rows = _sqlite.TryListRecipes();
        if (rows is null) return Array.Empty<string>();
        return rows.Select(r => r.Name).Where(n => !string.IsNullOrEmpty(n)).ToArray();
    }

    // ================================================================
    //  Import
    // ================================================================

    public TakeoutImportOutcome Import(JsonObject? wrapper)
    {
        if (wrapper is null) return TakeoutImportOutcome.InvalidJson();

        // Wrapper shape: exactly { takeout, targetRecipeName }.
        foreach (var kv in wrapper)
        {
            if (kv.Key != "takeout" && kv.Key != "targetRecipeName")
            {
                return TakeoutImportOutcome.UnknownWrapperField("/" + kv.Key);
            }
        }

        // targetRecipeName presence + type.
        string? rawName = null;
        if (wrapper.TryGetPropertyValue("targetRecipeName", out var nv))
        {
            if (nv is JsonValue nvv && nvv.TryGetValue<string>(out var s)) rawName = s;
            else if (nv is null) rawName = null;
            else return TakeoutImportOutcome.RecipeNameRequired();
        }
        else
        {
            return TakeoutImportOutcome.RecipeNameRequired();
        }
        if (rawName is null) return TakeoutImportOutcome.RecipeNameRequired();
        var trimmedName = rawName.Trim();
        if (string.IsNullOrEmpty(trimmedName)) return TakeoutImportOutcome.RecipeNameRequired();

        var nameReason = _validator.ValidateTargetName(trimmedName);
        if (nameReason is not null)
            return TakeoutImportOutcome.RecipeNameInvalid(nameReason);

        // takeout presence + IDictionary.
        if (!wrapper.TryGetPropertyValue("takeout", out var takeoutNode))
            return TakeoutImportOutcome.TakeoutShapeInvalid(new[]
            {
                new RecipeTakeoutValidationError("/takeout", "missing required property"),
            });
        if (takeoutNode is not JsonObject envelope)
            return TakeoutImportOutcome.TakeoutShapeInvalid(new[]
            {
                new RecipeTakeoutValidationError("/takeout", "must be an object"),
            });

        // Reuse F2C defense-in-depth on the envelope.
        try
        {
            var forbidden = RecipeTakeoutSanitizer.FindForbiddenFieldName(
                envelope, RecipeTakeoutSanitizer.TakeoutForbiddenSecretFields);
            if (forbidden is not null)
                return TakeoutImportOutcome.ForbiddenSecretField(fieldName: forbidden);
            var secretTag = RecipeTakeoutSanitizer.FindForbiddenSecretValue(envelope);
            if (secretTag is not null)
                return TakeoutImportOutcome.ForbiddenSecretField(kind: secretTag);
        }
        catch
        {
            return TakeoutImportOutcome.ForbiddenSecretField();
        }

        if (envelope["recipe"] is JsonObject rn
            && rn["auth"] is JsonObject an
            && an.ContainsKey("authProfileId"))
        {
            return TakeoutImportOutcome.ForbiddenSecretField(
                fieldName: "authProfileId", path: "/recipe/auth/authProfileId");
        }

        var verdict = _validator.ValidateStructure(envelope);
        if (!verdict.Ok)
        {
            var code = _validator.MapErrorsToCode(verdict.Errors);
            return TakeoutImportOutcome.StructuralFailure(code, verdict.Errors);
        }

        // Collision check (case-insensitive, trim-aware).
        var existingNames = GetExistingActiveRecipeNames();
        bool collision = existingNames.Any(n =>
            string.Equals(n.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase));
        if (collision)
        {
            var next = _importer.ResolveTargetName(trimmedName, existingNames);
            return TakeoutImportOutcome.NameConflict(
                trimmedName,
                next,
                "A recipe named '" + trimmedName + "' already exists in this Cookbook.");
        }

        // Materialize.
        string newId;
        RecipeTakeoutMaterialized material;
        try
        {
            newId = _idFactory();
            material = _importer.MaterializePending(
                envelope:          envelope,
                nowUtc:            _clock(),
                newRecipeId:       newId,
                cookbookVersion:   _cookbookVersion,
                bundledPaxVersion: _bundledPaxVersion,
                releaseChannel:    _releaseChannel);
        }
        catch
        {
            return TakeoutImportOutcome.PersistFailed();
        }

        var pending = material.Recipe;
        // Override identity.name with chef-provided name.
        if (pending["identity"] is not JsonObject identityObj)
        {
            identityObj = new JsonObject();
            pending["identity"] = identityObj;
        }
        identityObj["name"] = trimmedName;
        // Stamp destination's authoritative schema + adapter versions.
        pending["recipeSchemaVersion"] = RecipeValidator.SupportedSchemaVersion;
        pending["paxAdapterVersion"]   = _paxAdapterVersion;
        // Ensure recipeId is the fresh ULID.
        pending["recipeId"] = newId;

        string? sourceRecipeId = material.ImportedFromId;

        // Persist: file-first, row-second (mirrors RecipeMutationService.Create).
        string fileHash;
        try
        {
            _snapshots.EnsureDirs();
            var writeResult = _snapshots.Write(newId, pending);
            fileHash = writeResult.FileHash;
        }
        catch
        {
            return TakeoutImportOutcome.PersistFailed();
        }

        try
        {
            _rows.Insert(new RecipeInsertRow(
                RecipeId:            newId,
                Name:                trimmedName,
                PaxAdapterVersion:   _paxAdapterVersion,
                RecipeSchemaVersion: RecipeValidator.SupportedSchemaVersion,
                FilePath:            _snapshots.GetRecipeFilePath(newId),
                FileHash:            fileHash,
                CreatedAt:           (string)pending["updatedAt"]!,
                UpdatedAt:           (string)pending["updatedAt"]!,
                Source:              "takeout",
                SourceRef:           sourceRecipeId));
        }
        catch
        {
            // Rollback: delete the file we just wrote.
            try
            {
                var fp = _snapshots.GetRecipeFilePath(newId);
                if (File.Exists(fp)) File.Delete(fp);
            }
            catch { /* best-effort */ }
            return TakeoutImportOutcome.PersistFailed();
        }

        return TakeoutImportOutcome.Created(newId, trimmedName,
            material.NeedsChefKey, material.ChefKeyMode, pending);
    }
}

// ====================================================================
//  Outcome envelopes
// ====================================================================

public abstract record TakeoutExportOutcome
{
    public sealed record InvalidIdResult(string RecipeId) : TakeoutExportOutcome;
    public sealed record NotFoundResult(string RecipeId)  : TakeoutExportOutcome;
    public sealed record SanitizationFailedResult()       : TakeoutExportOutcome;
    public sealed record EnvelopeInvalidResult()          : TakeoutExportOutcome;
    public sealed record OkResult(JsonObject Envelope, string Filename) : TakeoutExportOutcome;

    public static TakeoutExportOutcome InvalidId(string id)        => new InvalidIdResult(id);
    public static TakeoutExportOutcome NotFound(string id)         => new NotFoundResult(id);
    public static TakeoutExportOutcome SanitizationFailed()        => new SanitizationFailedResult();
    public static TakeoutExportOutcome EnvelopeInvalid()           => new EnvelopeInvalidResult();
    public static TakeoutExportOutcome Ok(JsonObject env, string fn) => new OkResult(env, fn);
}

public abstract record TakeoutValidateOutcome
{
    public sealed record InvalidJsonResult() : TakeoutValidateOutcome;
    public sealed record ForbiddenSecretFieldResult(string? FieldName, string? Kind, string? Path) : TakeoutValidateOutcome;
    public sealed record StructuralFailureResult(string Code, IReadOnlyList<RecipeTakeoutValidationError> Errors) : TakeoutValidateOutcome;
    public sealed record OkPreviewResult(JsonObject Preview) : TakeoutValidateOutcome;

    public static TakeoutValidateOutcome InvalidJson() => new InvalidJsonResult();
    public static TakeoutValidateOutcome ForbiddenSecretField(
        string? fieldName = null, string? kind = null, string? path = null)
        => new ForbiddenSecretFieldResult(fieldName, kind, path);
    public static TakeoutValidateOutcome StructuralFailure(string code,
        IReadOnlyList<RecipeTakeoutValidationError> errors)
        => new StructuralFailureResult(code, errors);
    public static TakeoutValidateOutcome OkPreview(JsonObject preview) => new OkPreviewResult(preview);
}

public abstract record TakeoutImportOutcome
{
    public sealed record InvalidJsonResult() : TakeoutImportOutcome;
    public sealed record UnknownWrapperFieldResult(string Path) : TakeoutImportOutcome;
    public sealed record RecipeNameRequiredResult() : TakeoutImportOutcome;
    public sealed record RecipeNameInvalidResult(string Reason) : TakeoutImportOutcome;
    public sealed record TakeoutShapeInvalidResult(IReadOnlyList<RecipeTakeoutValidationError> Errors) : TakeoutImportOutcome;
    public sealed record ForbiddenSecretFieldResult(string? FieldName, string? Kind, string? Path) : TakeoutImportOutcome;
    public sealed record StructuralFailureResult(string Code, IReadOnlyList<RecipeTakeoutValidationError> Errors) : TakeoutImportOutcome;
    public sealed record NameConflictResult(string AttemptedName, string? NextSuggestion, string Message) : TakeoutImportOutcome;
    public sealed record PersistFailedResult() : TakeoutImportOutcome;
    public sealed record CreatedResult(string RecipeId, string RecipeName, bool NeedsChefKey, string? ChefKeyMode, JsonObject Recipe) : TakeoutImportOutcome;

    public static TakeoutImportOutcome InvalidJson()                       => new InvalidJsonResult();
    public static TakeoutImportOutcome UnknownWrapperField(string p)       => new UnknownWrapperFieldResult(p);
    public static TakeoutImportOutcome RecipeNameRequired()                => new RecipeNameRequiredResult();
    public static TakeoutImportOutcome RecipeNameInvalid(string reason)    => new RecipeNameInvalidResult(reason);
    public static TakeoutImportOutcome TakeoutShapeInvalid(IReadOnlyList<RecipeTakeoutValidationError> e) => new TakeoutShapeInvalidResult(e);
    public static TakeoutImportOutcome ForbiddenSecretField(
        string? fieldName = null, string? kind = null, string? path = null)
        => new ForbiddenSecretFieldResult(fieldName, kind, path);
    public static TakeoutImportOutcome StructuralFailure(string code,
        IReadOnlyList<RecipeTakeoutValidationError> errors)
        => new StructuralFailureResult(code, errors);
    public static TakeoutImportOutcome NameConflict(string attempted, string? next, string msg)
        => new NameConflictResult(attempted, next, msg);
    public static TakeoutImportOutcome PersistFailed() => new PersistFailedResult();
    public static TakeoutImportOutcome Created(string id, string name, bool needsChefKey, string? mode, JsonObject recipe)
        => new CreatedResult(id, name, needsChefKey, mode, recipe);
}
