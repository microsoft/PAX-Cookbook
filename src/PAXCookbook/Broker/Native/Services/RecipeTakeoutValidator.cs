using System.Globalization;
using System.Text.Json.Nodes;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B3 -- structural envelope validator + chef-name validator.
//
// Port of:
//   * RecipeTakeoutImporter.psm1::Test-RecipeTakeoutEnvelope
//   * RecipeTakeout.ps1::Get-RecipeTakeoutValidateErrorCode
//   * RecipeTakeout.ps1::Test-RecipeTakeoutNameWindowsValid
//
// Pure transforms. No I/O. The envelope and the chef-supplied name
// are inspected as-is; the validator never rewrites or normalises
// them.
public sealed class RecipeTakeoutValidator
{
    public const int    SchemaVersion          = 1;
    public const string Kind                   = "pax-cookbook.recipe-takeout";
    public const int    NameMaxLength          = 200;

    private static readonly char[] FilenameReservedChars = new[]
    {
        '<','>',':','"','/','\\','|','?','*',
    };

    public static readonly IReadOnlyList<string> AllowedTopLevel = new[]
    {
        "takeoutSchemaVersion","kind","exportedAtUtc","exportedBy",
        "recipe","chefKey","sourceRecipe","warnings","excluded","extensions",
    };

    public static readonly IReadOnlyList<string> RequiredTopLevel = new[]
    {
        "takeoutSchemaVersion","kind","exportedAtUtc","recipe","excluded",
    };

    // ----------------------------------------------------------------
    //  Envelope structural validation
    // ----------------------------------------------------------------

    public RecipeTakeoutStructuralVerdict ValidateStructure(JsonObject? envelope)
    {
        var errs = new List<RecipeTakeoutValidationError>();
        if (envelope is null)
        {
            errs.Add(new("", "envelope is null"));
            return new(false, errs);
        }

        foreach (var req in RequiredTopLevel)
        {
            if (!envelope.ContainsKey(req))
                errs.Add(new("/" + req, "required property missing"));
        }
        foreach (var kv in envelope)
        {
            if (!AllowedTopLevel.Contains(kv.Key))
                errs.Add(new("/" + kv.Key, "unknown top-level property"));
        }

        if (envelope.TryGetPropertyValue("takeoutSchemaVersion", out var sv))
        {
            int? num = null;
            if (sv is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) num = i;
                else if (v.TryGetValue<long>(out var l)) num = (int)l;
            }
            if (num is null || num.Value != SchemaVersion)
                errs.Add(new("/takeoutSchemaVersion",
                    "takeoutSchemaVersion must equal " + SchemaVersion.ToString(CultureInfo.InvariantCulture)));
        }

        if (envelope.TryGetPropertyValue("kind", out var kv2))
        {
            string? k = (kv2 is JsonValue v2 && v2.TryGetValue<string>(out var s)) ? s : null;
            if (k != Kind)
                errs.Add(new("/kind", "kind must equal '" + Kind + "'"));
        }

        if (envelope.TryGetPropertyValue("exportedAtUtc", out var ev))
        {
            string? s = (ev is JsonValue evv && evv.TryGetValue<string>(out var str)) ? str : null;
            if (s is null || !DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                                out _))
                errs.Add(new("/exportedAtUtc", "exportedAtUtc must be a valid date-time string"));
        }

        if (envelope.TryGetPropertyValue("recipe", out var rec))
        {
            if (rec is not JsonObject rObj)
                errs.Add(new("/recipe", "recipe must be an object"));
            else if (!rObj.TryGetPropertyValue("identity", out var idn))
                errs.Add(new("/recipe/identity", "recipe.identity is required"));
            else if (idn is not JsonObject idObj)
                errs.Add(new("/recipe/identity", "recipe.identity must be an object"));
            else
            {
                string? nm = null;
                if (idObj.TryGetPropertyValue("name", out var nv)
                    && nv is JsonValue nvv && nvv.TryGetValue<string>(out var s))
                    nm = s;
                if (string.IsNullOrWhiteSpace(nm))
                    errs.Add(new("/recipe/identity/name", "recipe.identity.name is required"));
            }
        }

        if (envelope.TryGetPropertyValue("chefKey", out var ck))
        {
            if (ck is not JsonObject ckObj)
                errs.Add(new("/chefKey", "chefKey must be an object when present"));
            else
            {
                string? req = null;
                if (ckObj.TryGetPropertyValue("requirement", out var rv)
                    && rv is JsonValue rvv && rvv.TryGetValue<string>(out var s)) req = s;
                if (req is null)
                    errs.Add(new("/chefKey/requirement", "chefKey.requirement is required when chefKey is present"));
                else if (req != "required" && req != "none")
                    errs.Add(new("/chefKey/requirement", "chefKey.requirement must be 'required' or 'none'"));
            }
        }

        if (envelope.TryGetPropertyValue("excluded", out var exc))
        {
            if (exc is not JsonArray exArr)
                errs.Add(new("/excluded", "excluded must be an array"));
            else if (exArr.Count == 0)
                errs.Add(new("/excluded", "excluded must contain at least one entry"));
        }

        if (envelope.ContainsKey("extensions"))
            errs.Add(new("/extensions",
                "v1 broker refuses 'extensions' until takeoutSchemaVersion advances"));

        return new(errs.Count == 0, errs);
    }

    // Precedence (PS Get-RecipeTakeoutValidateErrorCode):
    //   schema_version_unsupported > kind_invalid > unknown_field > shape_invalid.
    public string MapErrorsToCode(IReadOnlyList<RecipeTakeoutValidationError> errors)
    {
        if (errors is null || errors.Count == 0) return "takeout_shape_invalid";
        bool hasKind = false, hasVersion = false, hasUnknown = false;
        foreach (var e in errors)
        {
            if (e.Path == "/takeoutSchemaVersion")           hasVersion = true;
            if (e.Path == "/kind")                            hasKind    = true;
            if (e.Message == "unknown top-level property")    hasUnknown = true;
        }
        if (hasVersion) return "takeout_schema_version_unsupported";
        if (hasKind)    return "takeout_kind_invalid";
        if (hasUnknown) return "takeout_unknown_field";
        return "takeout_shape_invalid";
    }

    // ----------------------------------------------------------------
    //  Chef-supplied targetRecipeName validation
    // ----------------------------------------------------------------

    // Returns null on success or one of: "length", "control", "invalid_char".
    public string? ValidateTargetName(string trimmedName)
    {
        if (string.IsNullOrEmpty(trimmedName))            return "length";
        if (trimmedName.Length > NameMaxLength)           return "length";
        foreach (var ch in trimmedName)
            if (ch < 0x20)                                return "control";
        foreach (var ch in trimmedName)
            if (Array.IndexOf(FilenameReservedChars, ch) >= 0) return "invalid_char";
        return null;
    }
}
