using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B1 -- recipe validator port of app\broker\Routes\RecipeValidator.ps1
// (1,832 lines / 99 KB). Faithful to the PowerShell engine:
//
//   * JSON Schema 2020-12 walker covering the keywords actually used
//     by the M1 recipe schema (type, const, enum, minLength, maxLength,
//     pattern, format=date|date-time, required, additionalProperties,
//     properties, minItems, items). Not implemented: oneOf/anyOf/allOf
//     /not/if-then-else/prefixItems/uniqueItems/dependentRequired/
//     dependentSchemas because the M1 schema does not use them
//     (RecipeValidator.ps1 doesn't either).
//
//   * Seventeen cross-field gates run after schema, in the exact order
//     of Test-RecipeAll.
//
//   * AJV-shape ValidationError envelope { instancePath, keyword,
//     message, params } emitted by every gate. instancePath is a JSON
//     Pointer (RFC 6901); root is empty string "", nested is "/auth/mode",
//     array element is "/query/activityTypes/0".
//
// Caller is responsible for constructing the recipe as a JsonObject --
// the route layer parses the request body once and hands the same
// JsonObject to schema validation, server-stamping, and persistence.
public sealed class RecipeValidator
{
    public const int SupportedSchemaVersion = 1;

    // -------- public API ----------------------------------------------------

    // Mirrors Test-RecipeAll. Returns ok=false plus the full error
    // list (no max cap; never short-circuits between gates). The PS
    // semantics is "schema errors first, then every cross-field gate
    // in source order; each gate that finds no problem contributes no
    // errors". Type-mismatch inside the walker terminates that node's
    // own further keyword checks but does NOT stop subsequent gates.
    public ValidationVerdict TestAll(JsonNode? recipe)
    {
        var errors = new List<ValidationError>();

        // Seq 1 -- structural JSON Schema walk.
        WalkSchema(recipe, RecipeSchemaRoot, instancePath: "", errors);

        if (recipe is not JsonObject obj)
        {
            // PS walker emits a top-level type error and downstream
            // gates silently no-op on a non-object root. Mirror that
            // exactly: bail out after the schema error is recorded.
            return new ValidationVerdict(errors.Count == 0, errors);
        }

        // Seq 2 -- output-path tier policy.
        ValidateOutputPathTier(obj, errors);

        // Seq 3 -- date-range coherence.
        ValidateQueryDateRange(obj, errors);

        // Seq 4 -- removed-switch scan in advanced.extraArguments.
        ValidateExtraArgumentsRemovedSwitches(obj, errors);

        // Seq 5 -- auth.mode <-> authProfileId binding.
        ValidateAuthProfileBinding(obj, errors);

        // Seq 6 -- executionMode x auth.mode matrix.
        ValidateExecutionModeAuthMatrix(obj, errors);

        // Seq 7 -- secret-shape scan in advanced.extraArguments.
        ValidateExtraArgumentsSecretShape(obj, errors);

        // Seq 8 -- destinations.fact mode/path/appendFile mutex.
        ValidateFactOutputMode(obj, errors);

        // Seq 9 -- destinations.userInfo mode/path/appendFile mutex.
        ValidateUserInfoOutputMode(obj, errors);

        // Seq 10 -- audit vs userInfoOnly shape gate.
        ValidateQueryShape(obj, errors);

        // Seq 11 -- activityTypes under rollup constraint.
        ValidateActivityTypesUnderRollup(obj, errors);

        // Seq 12 -- m365Usage gate (excludeCopilot requires m365Usage).
        ValidateM365UsageGate(obj, errors);

        // Seq 13 -- userInfo channel requires includeUserInfo.
        ValidateUserInfoChannelGate(obj, errors);

        // Seq 14 -- agentFilter mode/agentIds mutex.
        ValidateAgentFilterShape(obj, errors);

        // Seq 15 -- unsupported switches in trailer.
        ValidateExtraArgumentsUnsupportedSwitches(obj, errors);

        // Seq 16 -- structurally-owned switches in trailer.
        ValidateExtraArgumentsStructurallyOwned(obj, errors);

        // Seq 17 -- rollup blockers.
        ValidateRollupBlockers(obj, errors);

        return new ValidationVerdict(errors.Count == 0, errors);
    }

    // ============================================================
    //  Schema walker (JSON Schema 2020-12, subset used by M1)
    // ============================================================

    private static void WalkSchema(
        JsonNode? node,
        SchemaNode schema,
        string instancePath,
        List<ValidationError> errors)
    {
        // type check first; bail this node's further keywords on
        // mismatch (matches Test-RecipeSchemaNode early-return).
        if (schema.Type is not null && !TypeMatches(node, schema.Type))
        {
            errors.Add(new ValidationError(
                InstancePath: instancePath,
                Keyword:      "type",
                Message:      "must be " + schema.Type,
                Params:       new Dictionary<string, object?> { ["type"] = schema.Type }));
            return;
        }

        // const
        if (schema.Const is not null)
        {
            if (!NodeEqualsScalar(node, schema.Const))
            {
                errors.Add(new ValidationError(
                    InstancePath: instancePath,
                    Keyword:      "const",
                    Message:      "must be equal to constant",
                    Params:       new Dictionary<string, object?> { ["allowedValue"] = schema.Const }));
                return;
            }
        }

        // enum
        if (schema.Enum is not null)
        {
            if (!schema.Enum.Any(v => NodeEqualsScalar(node, v)))
            {
                errors.Add(new ValidationError(
                    InstancePath: instancePath,
                    Keyword:      "enum",
                    Message:      "must be equal to one of the allowed values",
                    Params:       new Dictionary<string, object?> { ["allowedValues"] = schema.Enum }));
                return;
            }
        }

        // string-specific keywords
        if (schema.Type == "string" && node is JsonValue sv && sv.TryGetValue<string>(out var s))
        {
            if (schema.MinLength is int min && s.Length < min)
            {
                errors.Add(new ValidationError(instancePath, "minLength",
                    "must NOT have fewer than " + min + " characters",
                    new Dictionary<string, object?> { ["limit"] = min }));
            }
            if (schema.MaxLength is int max && s.Length > max)
            {
                errors.Add(new ValidationError(instancePath, "maxLength",
                    "must NOT have more than " + max + " characters",
                    new Dictionary<string, object?> { ["limit"] = max }));
            }
            if (schema.Pattern is not null)
            {
                if (!Regex.IsMatch(s, schema.Pattern))
                {
                    errors.Add(new ValidationError(instancePath, "pattern",
                        "must match pattern \"" + schema.Pattern + "\"",
                        new Dictionary<string, object?> { ["pattern"] = schema.Pattern }));
                }
            }
            if (schema.Format == "date")
            {
                if (!Regex.IsMatch(s, "^\\d{4}-\\d{2}-\\d{2}$")
                    || !DateTime.TryParseExact(s, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    errors.Add(new ValidationError(instancePath, "format",
                        "must match format \"date\"",
                        new Dictionary<string, object?> { ["format"] = "date" }));
                }
            }
            else if (schema.Format == "date-time")
            {
                if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out _))
                {
                    errors.Add(new ValidationError(instancePath, "format",
                        "must match format \"date-time\"",
                        new Dictionary<string, object?> { ["format"] = "date-time" }));
                }
            }
        }

        // object-specific keywords
        if (schema.Type == "object" && node is JsonObject obj)
        {
            if (schema.Required is not null)
            {
                foreach (var required in schema.Required)
                {
                    if (!obj.ContainsKey(required))
                    {
                        errors.Add(new ValidationError(instancePath, "required",
                            "must have required property '" + required + "'",
                            new Dictionary<string, object?> { ["missingProperty"] = required }));
                    }
                }
            }
            if (schema.AdditionalPropertiesFalse)
            {
                var allowed = schema.Properties is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(schema.Properties.Keys, StringComparer.Ordinal);
                foreach (var kv in obj)
                {
                    if (!allowed.Contains(kv.Key))
                    {
                        errors.Add(new ValidationError(instancePath, "additionalProperties",
                            "must NOT have additional property '" + kv.Key + "'",
                            new Dictionary<string, object?> { ["additionalProperty"] = kv.Key }));
                    }
                }
            }
            if (schema.Properties is not null)
            {
                foreach (var (propName, propSchema) in schema.Properties)
                {
                    if (obj.TryGetPropertyValue(propName, out var child))
                    {
                        WalkSchema(child, propSchema,
                            instancePath + "/" + propName, errors);
                    }
                }
            }
        }

        // array-specific keywords
        if (schema.Type == "array" && node is JsonArray arr)
        {
            if (schema.MinItems is int minItems && arr.Count < minItems)
            {
                errors.Add(new ValidationError(instancePath, "minItems",
                    "must NOT have fewer than " + minItems + " items",
                    new Dictionary<string, object?> { ["limit"] = minItems }));
            }
            if (schema.Items is not null)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    WalkSchema(arr[i], schema.Items,
                        instancePath + "/" + i, errors);
                }
            }
        }
    }

    private static bool TypeMatches(JsonNode? node, string expected) => expected switch
    {
        "object"  => node is JsonObject,
        "array"   => node is JsonArray,
        "string"  => node is JsonValue v1 && v1.TryGetValue<string>(out _),
        "boolean" => node is JsonValue v2 && v2.TryGetValue<bool>(out _),
        "integer" => IsInteger(node),
        "number"  => IsNumber(node),
        _ => false,
    };

    private static bool IsInteger(JsonNode? node)
    {
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out _)) return false;
        // A JsonValue can be backed either by a parsed JsonElement
        // or by a raw .NET scalar assigned via `obj["x"] = 1`.
        // GetValue<JsonElement>() throws on the latter, so probe the
        // direct typed accessors first.
        if (v.TryGetValue<long>(out _)) return true;
        if (v.TryGetValue<int>(out _))  return true;
        try
        {
            var je = v.GetValue<JsonElement>();
            return je.ValueKind == JsonValueKind.Number && je.TryGetInt64(out _);
        }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsNumber(JsonNode? node)
    {
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out _)) return false;
        if (v.TryGetValue<double>(out _))  return true;
        if (v.TryGetValue<decimal>(out _)) return true;
        if (v.TryGetValue<long>(out _))    return true;
        if (v.TryGetValue<int>(out _))     return true;
        try
        {
            var je = v.GetValue<JsonElement>();
            return je.ValueKind == JsonValueKind.Number;
        }
        catch (InvalidOperationException) { return false; }
    }

    private static bool NodeEqualsScalar(JsonNode? node, object? scalar)
    {
        if (node is not JsonValue v) return false;
        switch (scalar)
        {
            case string ss:
                return v.TryGetValue<string>(out var s) && s == ss;
            case int ii:
                return v.TryGetValue<int>(out var i) && i == ii;
            case long ll:
                return v.TryGetValue<long>(out var l) && l == ll;
            case bool bb:
                return v.TryGetValue<bool>(out var b) && b == bb;
        }
        return false;
    }

    // ============================================================
    //  Cross-field gates
    // ============================================================

    // Seq 2 -- M1 doctrine: fact destination MUST be a local-tier
    // filesystem path. OneLake / Fabric URLs and onelake.* hostnames
    // are rejected. PS RecipeValidator.ps1: Test-RecipeOutputPathTier.
    private static readonly (string Keyword, string Pattern)[] OutputPathTierRejectors = new[]
    {
        ("abfssScheme",      "^abfss://"),
        ("onelakeScheme",    "^onelake://"),
        ("onelakeHost",      "\\.onelake\\."),
        ("fabricHost",       "fabric\\.microsoft\\.com"),
    };

    private static void ValidateOutputPathTier(JsonObject recipe, List<ValidationError> errors)
    {
        var path = GetString(recipe, "destinations", "fact", "path");
        if (string.IsNullOrEmpty(path)) return;
        foreach (var (kw, pat) in OutputPathTierRejectors)
        {
            if (Regex.IsMatch(path, pat, RegexOptions.IgnoreCase))
            {
                errors.Add(new ValidationError(
                    InstancePath: "/destinations/fact/path",
                    Keyword:      "m1OutputTier",
                    Message:      "destinations.fact.path must be a local filesystem path; OneLake / Fabric URLs are rejected by the M1 tier policy",
                    Params:       new Dictionary<string, object?>
                    {
                        ["rejectedBy"] = kw,
                        ["pattern"]    = pat,
                    }));
                return; // first match short-circuits
            }
        }
    }

    // Seq 3 -- startDate <= endDate when both parse cleanly.
    private static void ValidateQueryDateRange(JsonObject recipe, List<ValidationError> errors)
    {
        var startStr = GetString(recipe, "query", "startDate");
        var endStr   = GetString(recipe, "query", "endDate");
        if (string.IsNullOrEmpty(startStr) || string.IsNullOrEmpty(endStr)) return;
        if (!DateTime.TryParseExact(startStr, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) return;
        if (!DateTime.TryParseExact(endStr, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) return;
        if (start <= end) return;
        errors.Add(new ValidationError(
            InstancePath: "/query/endDate",
            Keyword:      "dateRange",
            Message:      "query.endDate must be on or after query.startDate",
            Params:       new Dictionary<string, object?>
            {
                ["startDate"] = startStr,
                ["endDate"]   = endStr,
            }));
    }

    // Seq 4 -- removed-switch detection. PS broker delegates to an
    // adapter helper (Test-ExtraArgumentsForRemovedSwitches) populated
    // from the bundled PAX manifest. The native broker keeps a static
    // deny-list for the switches the M1-era PAX no longer accepts.
    private static readonly (string Switch, string Message)[] RemovedSwitches = new[]
    {
        ("UseEOM",                  "-UseEOM is removed; reset query window via startDate/endDate"),
        ("OutputPathRollup",        "-OutputPathRollup is removed; use destinations.fact.path with rollup mode"),
        ("AppendRollup",            "-AppendRollup is removed; use destinations.fact.mode='append' with appendFile"),
    };

    private static void ValidateExtraArgumentsRemovedSwitches(JsonObject recipe, List<ValidationError> errors)
    {
        var extra = GetString(recipe, "advanced", "extraArguments");
        if (string.IsNullOrEmpty(extra)) return;
        foreach (var (sw, msg) in RemovedSwitches)
        {
            if (Regex.IsMatch(extra, "(^|\\s)-" + Regex.Escape(sw) + "($|\\s|=)", RegexOptions.IgnoreCase))
            {
                errors.Add(new ValidationError(
                    InstancePath: "/advanced/extraArguments",
                    Keyword:      "removedSwitch",
                    Message:      msg,
                    Params:       new Dictionary<string, object?> { ["switch"] = "-" + sw }));
            }
        }
    }

    // Seq 5 -- auth.mode <-> authProfileId binding.
    private static readonly HashSet<string> AppRegistrationModes =
        new(StringComparer.Ordinal) { "AppRegistrationSecret", "AppRegistrationCertificate" };

    private static void ValidateAuthProfileBinding(JsonObject recipe, List<ValidationError> errors)
    {
        var mode = GetString(recipe, "auth", "mode");
        if (string.IsNullOrEmpty(mode)) return;
        var hasProfile = TryGetProperty(recipe, out var _, "auth", "authProfileId");
        if (AppRegistrationModes.Contains(mode))
        {
            if (!hasProfile)
            {
                errors.Add(new ValidationError(
                    InstancePath: "/auth/authProfileId",
                    Keyword:      "required",
                    Message:      "auth.authProfileId is required when auth.mode is " + mode,
                    Params:       new Dictionary<string, object?> { ["mode"] = mode }));
            }
        }
        else
        {
            if (hasProfile)
            {
                errors.Add(new ValidationError(
                    InstancePath: "/auth/authProfileId",
                    Keyword:      "forbidden",
                    Message:      "auth.authProfileId is not allowed when auth.mode is " + mode,
                    Params:       new Dictionary<string, object?> { ["mode"] = mode }));
            }
        }
    }

    // Seq 6 -- executionMode x auth.mode matrix.
    private static readonly Dictionary<string, string[]> ExecutionModeAuthMatrix =
        new(StringComparer.Ordinal)
    {
        ["local-manual"]    = new[] { "WebLogin", "DeviceCode", "AppRegistrationSecret", "AppRegistrationCertificate" },
        ["local-scheduled"] = new[] { "AppRegistrationSecret", "AppRegistrationCertificate" },
        ["fabric-hosted"]   = new[] { "ManagedIdentity", "AppRegistrationSecret", "AppRegistrationCertificate" },
        ["azure-hosted"]    = new[] { "ManagedIdentity", "AppRegistrationSecret", "AppRegistrationCertificate" },
    };

    private static void ValidateExecutionModeAuthMatrix(JsonObject recipe, List<ValidationError> errors)
    {
        var execMode = GetString(recipe, "executionMode") ?? "local-manual"; // pre-AF default
        var authMode = GetString(recipe, "auth", "mode");
        if (string.IsNullOrEmpty(authMode)) return;
        if (!ExecutionModeAuthMatrix.TryGetValue(execMode, out var allowed)) return;
        if (allowed.Contains(authMode)) return;
        errors.Add(new ValidationError(
            InstancePath: "/auth/mode",
            Keyword:      "executionModeMismatch",
            Message:      "auth.mode '" + authMode + "' is not compatible with executionMode '" + execMode + "'",
            Params:       new Dictionary<string, object?>
            {
                ["executionMode"] = execMode,
                ["mode"]          = authMode,
                ["allowed"]       = allowed,
            }));
    }

    // Seq 7 -- secret-shape deny-list in trailer.
    private static readonly string[] SecretShapeFlags = new[]
    {
        "ClientSecret", "Password", "Pwd", "AppKey", "Secret",
        "Token", "AccessToken", "RefreshToken", "Bearer",
        "CertificatePassword", "CertPassword", "PfxPassword", "ApiKey",
    };

    private static void ValidateExtraArgumentsSecretShape(JsonObject recipe, List<ValidationError> errors)
    {
        var extra = GetString(recipe, "advanced", "extraArguments");
        if (string.IsNullOrEmpty(extra)) return;
        foreach (var flag in SecretShapeFlags)
        {
            if (Regex.IsMatch(extra, "(^|\\s)-" + Regex.Escape(flag) + "($|\\s|=|:)", RegexOptions.IgnoreCase))
            {
                errors.Add(new ValidationError(
                    InstancePath: "/advanced/extraArguments",
                    Keyword:      "secretShape",
                    Message:      "advanced.extraArguments must not contain secret-shaped flags; use auth profiles instead",
                    Params:       new Dictionary<string, object?> { ["switch"] = "-" + flag }));
            }
        }
    }

    // Seq 8 -- destinations.fact mode/path/appendFile mutex.
    private static void ValidateFactOutputMode(JsonObject recipe, List<ValidationError> errors)
    {
        var fact = GetObject(recipe, "destinations", "fact");
        if (fact is null) return;

        var modeExplicit = GetString(fact, "mode");
        var appendBehavior = GetString(fact, "appendBehavior");
        var hasPath = !string.IsNullOrEmpty(GetString(fact, "path"));
        var hasAppendFile = !string.IsNullOrEmpty(GetString(fact, "appendFile"));

        // Effective mode resolution (order: mode > appendBehavior > default).
        string effective;
        if (!string.IsNullOrEmpty(modeExplicit)) effective = modeExplicit!;
        else if (appendBehavior == "append") effective = "append";
        else effective = "outputPath";

        if (effective == "outputPath")
        {
            if (!hasPath && !hasAppendFile)
            {
                errors.Add(new ValidationError(
                    "/destinations/fact", "factOutputTargetRequired",
                    "destinations.fact must specify either path or appendFile",
                    new Dictionary<string, object?> { ["effectiveMode"] = effective }));
            }
            else if (!hasPath)
            {
                errors.Add(new ValidationError(
                    "/destinations/fact/path", "factPathRequired",
                    "destinations.fact.path is required when fact mode resolves to outputPath",
                    new Dictionary<string, object?> { ["effectiveMode"] = effective }));
            }
            if (hasAppendFile)
            {
                errors.Add(new ValidationError(
                    "/destinations/fact/appendFile", "factAppendFileForbidden",
                    "destinations.fact.appendFile is not allowed when fact mode resolves to outputPath",
                    new Dictionary<string, object?> { ["effectiveMode"] = effective }));
            }
        }
        else if (effective == "append")
        {
            if (!hasAppendFile)
            {
                errors.Add(new ValidationError(
                    "/destinations/fact/appendFile", "factAppendFileRequired",
                    "destinations.fact.appendFile is required when fact mode resolves to append",
                    new Dictionary<string, object?> { ["effectiveMode"] = effective }));
            }
            // Path is "inert" when mode is explicitly append; warn only
            // if user explicitly set mode='append' AND provided a path.
            if (modeExplicit == "append" && hasPath)
            {
                errors.Add(new ValidationError(
                    "/destinations/fact/path", "factPathInertUnderAppendMode",
                    "destinations.fact.path is ignored when mode is explicitly 'append'; remove it or change mode to 'outputPath'",
                    new Dictionary<string, object?> { ["effectiveMode"] = effective }));
            }
        }

        // appendBehavior present but explicit mode disagrees -> mismatch.
        if (!string.IsNullOrEmpty(modeExplicit)
            && !string.IsNullOrEmpty(appendBehavior)
            && ((modeExplicit == "outputPath" && appendBehavior == "append")
             || (modeExplicit == "append"     && appendBehavior == "fresh")))
        {
            errors.Add(new ValidationError(
                "/destinations/fact/appendBehavior", "factModeBehaviorMismatch",
                "destinations.fact.appendBehavior conflicts with explicit mode",
                new Dictionary<string, object?>
                {
                    ["mode"]           = modeExplicit,
                    ["appendBehavior"] = appendBehavior,
                }));
        }
    }

    // Seq 9 -- destinations.userInfo mode/path/appendFile mutex.
    private static void ValidateUserInfoOutputMode(JsonObject recipe, List<ValidationError> errors)
    {
        var ui = GetObject(recipe, "destinations", "userInfo");
        if (ui is null) return;
        var mode = GetString(ui, "mode");
        if (string.IsNullOrEmpty(mode)) return; // schema gate handles missing
        var hasPath = !string.IsNullOrEmpty(GetString(ui, "path"));
        var hasAppendFile = !string.IsNullOrEmpty(GetString(ui, "appendFile"));

        if (mode == "outputPath")
        {
            if (!hasPath)
            {
                errors.Add(new ValidationError(
                    "/destinations/userInfo/path", "userInfoPathRequired",
                    "destinations.userInfo.path is required when userInfo mode is outputPath",
                    new Dictionary<string, object?> { ["mode"] = mode }));
            }
            if (hasAppendFile)
            {
                errors.Add(new ValidationError(
                    "/destinations/userInfo/appendFile", "userInfoAppendFileForbidden",
                    "destinations.userInfo.appendFile is not allowed when userInfo mode is outputPath",
                    new Dictionary<string, object?> { ["mode"] = mode }));
            }
        }
        else if (mode == "append")
        {
            if (!hasAppendFile)
            {
                errors.Add(new ValidationError(
                    "/destinations/userInfo/appendFile", "userInfoAppendFileRequired",
                    "destinations.userInfo.appendFile is required when userInfo mode is append",
                    new Dictionary<string, object?> { ["mode"] = mode }));
            }
            if (hasPath)
            {
                errors.Add(new ValidationError(
                    "/destinations/userInfo/path", "userInfoPathForbidden",
                    "destinations.userInfo.path is not allowed when userInfo mode is append",
                    new Dictionary<string, object?> { ["mode"] = mode }));
            }
        }
    }

    // Seq 10 -- query shape (audit vs userInfoOnly).
    private static readonly string[] AuditFilterFields = new[]
    {
        "activityTypes", "userIds", "groupNames", "agentFilter", "promptFilter",
    };

    private static void ValidateQueryShape(JsonObject recipe, List<ValidationError> errors)
    {
        var query = GetObject(recipe, "query");
        if (query is null) return;
        var mode = GetString(query, "mode") ?? "audit";

        var hasStart  = !string.IsNullOrEmpty(GetString(query, "startDate"));
        var hasEnd    = !string.IsNullOrEmpty(GetString(query, "endDate"));
        var hasFact   = TryGetProperty(recipe, out var _, "destinations", "fact");
        var hasUserInfo = TryGetProperty(recipe, out var _, "destinations", "userInfo");

        if (mode == "audit")
        {
            // Rollup is OPTIONAL under audit. An operator may run PAX only to
            // pull raw audit data, so a missing processing.rollup is allowed.
            // When rollup is present it is mapped to -Rollup / -RollupPlusRaw at
            // runtime; when absent the runtime command simply omits it. Rollup
            // remains forbidden under userInfoOnly (handled below).
            if (!hasStart)
            {
                errors.Add(new ValidationError("/query/startDate", "startDateRequiredUnderAudit",
                    "query.startDate is required when query.mode is audit",
                    new Dictionary<string, object?> { ["queryMode"] = mode }));
            }
            if (!hasEnd)
            {
                errors.Add(new ValidationError("/query/endDate", "endDateRequiredUnderAudit",
                    "query.endDate is required when query.mode is audit",
                    new Dictionary<string, object?> { ["queryMode"] = mode }));
            }
            if (!hasFact)
            {
                errors.Add(new ValidationError("/destinations/fact", "factDestinationRequiredUnderAudit",
                    "destinations.fact is required when query.mode is audit",
                    new Dictionary<string, object?> { ["queryMode"] = mode }));
            }
        }
        else if (mode == "userInfoOnly")
        {
            var hasRollup = TryGetProperty(recipe, out var _, "processing", "rollup");
            if (hasRollup)
            {
                errors.Add(new ValidationError("/processing/rollup", "rollupForbiddenUnderUserInfoOnly",
                    "processing.rollup is not allowed when query.mode is userInfoOnly",
                    new Dictionary<string, object?> { ["queryMode"] = mode }));
            }
            var m365Include = GetBool(recipe, "ingredients", "m365Usage", "includeM365Usage");
            if (m365Include == true)
            {
                errors.Add(new ValidationError(
                    "/ingredients/m365Usage/includeM365Usage", "m365UsageForbiddenUnderUserInfoOnly",
                    "ingredients.m365Usage.includeM365Usage must be false when query.mode is userInfoOnly",
                    new Dictionary<string, object?> { ["queryMode"] = mode }));
            }
            if (TryGetProperty(recipe, out var _, "ingredients", "m365Usage", "includeCopilotInteraction"))
            {
                errors.Add(new ValidationError(
                    "/ingredients/m365Usage/includeCopilotInteraction", "includeCopilotInteractionForbiddenUnderUserInfoOnly",
                    "ingredients.m365Usage.includeCopilotInteraction is not allowed when query.mode is userInfoOnly",
                    new Dictionary<string, object?> { ["queryMode"] = mode }));
            }
            if (GetBool(recipe, "ingredients", "entraUserData", "includeUserInfo") != true)
            {
                errors.Add(new ValidationError(
                    "/ingredients/entraUserData/includeUserInfo", "userInfoOnlyRequiresIncludeUserInfoTrue",
                    "ingredients.entraUserData.includeUserInfo must be true when query.mode is userInfoOnly",
                    new Dictionary<string, object?> { ["queryMode"] = mode }));
            }
            foreach (var f in AuditFilterFields)
            {
                if (TryGetProperty(recipe, out var _, "query", f))
                {
                    errors.Add(new ValidationError("/query/" + f, "auditFilterForbiddenUnderUserInfoOnly",
                        "query." + f + " is not allowed when query.mode is userInfoOnly",
                        new Dictionary<string, object?>
                        {
                            ["queryMode"] = mode,
                            ["field"]     = f,
                        }));
                }
            }
            if (hasStart)
            {
                errors.Add(new ValidationError("/query/startDate", "userInfoOnlyForbidsStartDate",
                    "query.startDate is not allowed when query.mode is userInfoOnly",
                    new Dictionary<string, object?> { ["queryMode"] = mode }));
            }
            if (hasEnd)
            {
                errors.Add(new ValidationError("/query/endDate", "userInfoOnlyForbidsEndDate",
                    "query.endDate is not allowed when query.mode is userInfoOnly",
                    new Dictionary<string, object?> { ["queryMode"] = mode }));
            }
            if (!hasUserInfo)
            {
                errors.Add(new ValidationError("/destinations/userInfo", "userInfoRequiredUnderUserInfoOnly",
                    "destinations.userInfo is required when query.mode is userInfoOnly",
                    new Dictionary<string, object?> { ["queryMode"] = mode }));
            }
            if (hasFact)
            {
                errors.Add(new ValidationError("/destinations/fact", "userInfoOnlyForbidsFactDestination",
                    "destinations.fact is not allowed when query.mode is userInfoOnly",
                    new Dictionary<string, object?> { ["queryMode"] = mode }));
            }
        }
    }

    // Seq 11 -- activityTypes under rollup must be exactly ['CopilotInteraction'].
    private static void ValidateActivityTypesUnderRollup(JsonObject recipe, List<ValidationError> errors)
    {
        var rollup = GetString(recipe, "processing", "rollup");
        if (rollup is null || (rollup != "Rollup" && rollup != "RollupPlusRaw")) return;
        if (!TryGetProperty(recipe, out var node, "query", "activityTypes")) return;
        if (node is not JsonArray arr) return;
        var values = arr.OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => s is not null)
            .ToArray();
        if (values.Length == 1 && values[0] == "CopilotInteraction") return;
        errors.Add(new ValidationError(
            "/query/activityTypes", "activityTypesRollupConstraint",
            "query.activityTypes must be ['CopilotInteraction'] when processing.rollup is enabled",
            new Dictionary<string, object?>
            {
                ["activityTypes"] = values,
                ["rollup"]        = rollup,
            }));
    }

    // Seq 12 -- includeCopilotInteraction=false requires includeM365Usage=true.
    private static void ValidateM365UsageGate(JsonObject recipe, List<ValidationError> errors)
    {
        var includeCopilot = GetBool(recipe, "ingredients", "m365Usage", "includeCopilotInteraction");
        if (includeCopilot != false) return;
        var includeM365 = GetBool(recipe, "ingredients", "m365Usage", "includeM365Usage");
        if (includeM365 == true) return;
        errors.Add(new ValidationError(
            "/ingredients/m365Usage/includeCopilotInteraction", "excludeCopilotInteractionRequiresM365Usage",
            "ingredients.m365Usage.includeCopilotInteraction=false requires ingredients.m365Usage.includeM365Usage=true",
            new Dictionary<string, object?>
            {
                ["includeCopilotInteraction"] = false,
                ["includeM365Usage"]          = includeM365,
            }));
    }

    // Seq 13 -- under audit, destinations.userInfo requires includeUserInfo=true.
    private static void ValidateUserInfoChannelGate(JsonObject recipe, List<ValidationError> errors)
    {
        var queryMode = GetString(recipe, "query", "mode") ?? "audit";
        if (queryMode != "audit") return;
        if (!TryGetProperty(recipe, out var _, "destinations", "userInfo")) return;
        var includeUserInfo = GetBool(recipe, "ingredients", "entraUserData", "includeUserInfo");
        if (includeUserInfo == true) return;
        errors.Add(new ValidationError(
            "/destinations/userInfo", "userInfoChannelRequiresIncludeUserInfo",
            "destinations.userInfo requires ingredients.entraUserData.includeUserInfo=true under audit shape",
            new Dictionary<string, object?>
            {
                ["queryMode"]       = queryMode,
                ["includeUserInfo"] = includeUserInfo,
            }));
    }

    // Seq 14 -- agentFilter mode/agentIds mutex.
    private static void ValidateAgentFilterShape(JsonObject recipe, List<ValidationError> errors)
    {
        var filter = GetObject(recipe, "query", "agentFilter");
        if (filter is null) return;
        var mode = GetString(filter, "mode");
        if (string.IsNullOrEmpty(mode)) return;
        var hasIds = TryGetProperty(filter, out var _, "agentIds");
        switch (mode)
        {
            case "none":
                if (hasIds)
                    errors.Add(new ValidationError("/query/agentFilter/agentIds", "agentFilterAgentIdsForbiddenUnderNone",
                        "query.agentFilter.agentIds is not allowed when agentFilter.mode is 'none'",
                        new Dictionary<string, object?> { ["agentFilterMode"] = mode }));
                break;
            case "agentIds":
                if (!hasIds)
                    errors.Add(new ValidationError("/query/agentFilter/agentIds", "agentFilterAgentIdsRequiredUnderAgentIds",
                        "query.agentFilter.agentIds is required when agentFilter.mode is 'agentIds'",
                        new Dictionary<string, object?> { ["agentFilterMode"] = mode }));
                break;
            case "agentsOnly":
                if (hasIds)
                    errors.Add(new ValidationError("/query/agentFilter/agentIds", "agentFilterAgentIdsForbiddenUnderAgentsOnly",
                        "query.agentFilter.agentIds is not allowed when agentFilter.mode is 'agentsOnly'",
                        new Dictionary<string, object?> { ["agentFilterMode"] = mode }));
                break;
            case "excludeAgents":
                if (hasIds)
                    errors.Add(new ValidationError("/query/agentFilter/agentIds", "agentFilterAgentIdsForbiddenUnderExcludeAgents",
                        "query.agentFilter.agentIds is not allowed when agentFilter.mode is 'excludeAgents'",
                        new Dictionary<string, object?> { ["agentFilterMode"] = mode }));
                break;
        }
    }

    // Seq 15 -- unsupported switches in trailer.
    private static readonly (string Switch, string Hint)[] UnsupportedSwitches = new[]
    {
        ("RecordTypes",  "use query.activityTypes instead"),
        ("ServiceTypes", "use ingredients / query.activityTypes instead"),
        ("UseEOM",       "incompatible with M1; remove and reset query window"),
    };

    private static void ValidateExtraArgumentsUnsupportedSwitches(JsonObject recipe, List<ValidationError> errors)
    {
        var extra = GetString(recipe, "advanced", "extraArguments");
        if (string.IsNullOrEmpty(extra)) return;
        foreach (var (sw, hint) in UnsupportedSwitches)
        {
            if (Regex.IsMatch(extra, "(^|\\s)-" + Regex.Escape(sw) + "($|\\s|=)", RegexOptions.IgnoreCase))
            {
                errors.Add(new ValidationError(
                    "/advanced/extraArguments", "unsupportedSwitch",
                    "advanced.extraArguments uses unsupported switch -" + sw + "; " + hint,
                    new Dictionary<string, object?> { ["switch"] = "-" + sw }));
            }
        }
    }

    // Seq 16 -- structurally-owned switches in trailer.
    private static readonly string[] StructurallyOwnedSwitches = new[]
    {
        "OutputPath", "AppendFile", "OutputPathUserInfo", "AppendUserInfo",
        "IncludeUserInfo", "OnlyUserInfo", "IncludeM365Usage",
        "ExcludeCopilotInteraction", "ActivityTypes", "UserIds",
        "GroupNames", "AgentId", "AgentsOnly", "ExcludeAgents",
        "PromptFilter", "ClientCertificatePath", "StartDate", "EndDate",
        "Rollup", "RollupPlusRaw",
    };

    private static void ValidateExtraArgumentsStructurallyOwned(JsonObject recipe, List<ValidationError> errors)
    {
        var extra = GetString(recipe, "advanced", "extraArguments");
        if (string.IsNullOrEmpty(extra)) return;
        foreach (var sw in StructurallyOwnedSwitches)
        {
            if (Regex.IsMatch(extra, "(^|\\s)-" + Regex.Escape(sw) + "($|\\s|=)", RegexOptions.IgnoreCase))
            {
                errors.Add(new ValidationError(
                    "/advanced/extraArguments", "structurallyOwnedSwitch",
                    "advanced.extraArguments must not duplicate structured field; -" + sw + " is owned by another recipe field",
                    new Dictionary<string, object?> { ["switch"] = "-" + sw }));
            }
        }
    }

    // Seq 17 -- rollup blockers.
    private static readonly (string Switch, string Keyword, string RuleId, string Message)[] RollupBlockers = new[]
    {
        ("UseEOM",            "rollupBlockedByUseEOM",            "L2.ROLLUP.BLOCK_USE_EOM",            "processing.rollup cannot be combined with -UseEOM"),
        ("ExportWorkbook",    "rollupBlockedByExportWorkbook",    "L2.ROLLUP.BLOCK_EXPORT_WORKBOOK",    "processing.rollup cannot be combined with -ExportWorkbook"),
        ("OnlyUserInfo",      "rollupBlockedByOnlyUserInfo",      "L2.ROLLUP.BLOCK_ONLY_USER_INFO",     "processing.rollup cannot be combined with -OnlyUserInfo"),
        ("OnlyAgent365Info",  "rollupBlockedByOnlyAgent365Info",  "L2.ROLLUP.BLOCK_ONLY_AGENT365_INFO", "processing.rollup cannot be combined with -OnlyAgent365Info"),
        ("RAWInputCSV",       "rollupBlockedByRawInputCsv",       "L2.ROLLUP.BLOCK_RAW_INPUT_CSV",      "processing.rollup cannot be combined with -RAWInputCSV"),
    };

    private static void ValidateRollupBlockers(JsonObject recipe, List<ValidationError> errors)
    {
        var rollup = GetString(recipe, "processing", "rollup");
        if (rollup is null || (rollup != "Rollup" && rollup != "RollupPlusRaw")) return;
        var extra = GetString(recipe, "advanced", "extraArguments");
        if (!string.IsNullOrEmpty(extra))
        {
            foreach (var (sw, kw, ruleId, msg) in RollupBlockers)
            {
                if (Regex.IsMatch(extra, "(^|\\s)-" + Regex.Escape(sw) + "($|\\s|=)", RegexOptions.IgnoreCase))
                {
                    errors.Add(new ValidationError(
                        "/advanced/extraArguments", kw, msg,
                        new Dictionary<string, object?>
                        {
                            ["ruleId"] = ruleId,
                            ["switch"] = "-" + sw,
                        }));
                }
            }
            // conjunction: -ExcludeCopilotInteraction requires includeM365Usage=true
            if (Regex.IsMatch(extra, "(^|\\s)-ExcludeCopilotInteraction($|\\s|=)", RegexOptions.IgnoreCase))
            {
                var includeM365 = GetBool(recipe, "ingredients", "m365Usage", "includeM365Usage");
                if (includeM365 != true)
                {
                    errors.Add(new ValidationError(
                        "/ingredients/m365Usage/includeM365Usage", "rollupExcludeCopilotRequiresM365Usage",
                        "rollup with -ExcludeCopilotInteraction requires ingredients.m365Usage.includeM365Usage=true",
                        new Dictionary<string, object?>
                        {
                            ["ruleId"]          = "L2.ROLLUP.EXCLUDE_COPILOT_REQUIRES_M365_USAGE",
                            ["switch"]          = "-ExcludeCopilotInteraction",
                            ["includeM365Usage"] = includeM365,
                        }));
                }
            }
        }
    }

    // ============================================================
    //  Schema document (M1 / recipeSchemaVersion=1)
    // ============================================================

    // Schema modelled as a tree of SchemaNode. Only the keywords M1
    // exercises are present. Matches the structure of $Script:RecipeSchema
    // in RecipeValidator.ps1.
    private sealed class SchemaNode
    {
        public string?                          Type      { get; init; }
        public object?                          Const     { get; init; }
        public object[]?                        Enum      { get; init; }
        public string?                          Pattern   { get; init; }
        public string?                          Format    { get; init; }
        public int?                             MinLength { get; init; }
        public int?                             MaxLength { get; init; }
        public int?                             MinItems  { get; init; }
        public bool                             AdditionalPropertiesFalse { get; init; }
        public string[]?                        Required  { get; init; }
        public Dictionary<string, SchemaNode>?  Properties { get; init; }
        public SchemaNode?                      Items     { get; init; }
    }

    private static SchemaNode Str(int? min = null, int? max = null, string? pattern = null, string? format = null, object[]? @enum = null)
        => new() { Type = "string", MinLength = min, MaxLength = max, Pattern = pattern, Format = format, Enum = @enum };
    private static SchemaNode Boolean() => new() { Type = "boolean" };
    private static SchemaNode IntConst(int v) => new() { Type = "integer", Const = v };
    private static SchemaNode Obj(string[] required, Dictionary<string, SchemaNode> properties, bool additionalPropertiesFalse = true)
        => new() { Type = "object", Required = required, Properties = properties, AdditionalPropertiesFalse = additionalPropertiesFalse };
    private static SchemaNode Arr(SchemaNode items, int? minItems = null)
        => new() { Type = "array", Items = items, MinItems = minItems };

    private const string UuidPattern   = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";
    private const string UlidPattern   = "^[0-9A-HJKMNP-TV-Z]{26}$";
    private const string SemverPattern = "^\\d+\\.\\d+\\.\\d+$";
    private const string TemplateIdPattern = "^[a-z][a-z0-9-]{1,62}[a-z0-9]$";

    private static readonly SchemaNode RecipeSchemaRoot = Obj(
        required: new[] { "recipeId", "recipeSchemaVersion", "paxAdapterVersion", "identity", "ingredients", "query", "processing", "destinations", "auth" },
        properties: new(StringComparer.Ordinal)
        {
            ["recipeId"]            = Str(pattern: UlidPattern),
            ["recipeSchemaVersion"] = IntConst(1),
            ["paxAdapterVersion"]   = Str(pattern: SemverPattern),
            ["executionMode"]       = Str(@enum: new object[] { "local-manual", "local-scheduled", "fabric-hosted", "azure-hosted" }),
            ["createdAt"]           = Str(format: "date-time"),
            ["updatedAt"]           = Str(format: "date-time"),
            ["createdBy"] = Obj(
                required: new[] { "cookbookVersion", "bundledPaxVersion", "releaseChannel" },
                properties: new(StringComparer.Ordinal)
                {
                    ["cookbookVersion"]   = Str(min: 1),
                    ["bundledPaxVersion"] = Str(pattern: SemverPattern),
                    ["releaseChannel"]    = Str(min: 1),
                    ["fromTemplate"] = Obj(
                        required: new[] { "templateId", "templateVersion" },
                        properties: new(StringComparer.Ordinal)
                        {
                            ["templateId"]      = Str(pattern: TemplateIdPattern),
                            ["templateVersion"] = Str(pattern: SemverPattern),
                        }),
                }),
            ["identity"] = Obj(
                required: new[] { "name" },
                properties: new(StringComparer.Ordinal)
                {
                    ["name"] = Str(min: 1, max: 200),
                }),
            ["ingredients"] = Obj(
                required: Array.Empty<string>(),
                properties: new(StringComparer.Ordinal)
                {
                    ["m365Usage"] = Obj(
                        required: new[] { "includeM365Usage" },
                        properties: new(StringComparer.Ordinal)
                        {
                            ["includeM365Usage"]          = Boolean(),
                            ["includeCopilotInteraction"] = Boolean(),
                        }),
                    ["entraUserData"] = Obj(
                        required: new[] { "includeUserInfo" },
                        properties: new(StringComparer.Ordinal)
                        {
                            ["includeUserInfo"] = Boolean(),
                        }),
                }),
            ["query"] = Obj(
                required: Array.Empty<string>(),
                properties: new(StringComparer.Ordinal)
                {
                    ["mode"]          = Str(@enum: new object[] { "audit", "userInfoOnly" }),
                    ["startDate"]     = Str(format: "date"),
                    ["endDate"]       = Str(format: "date"),
                    ["activityTypes"] = Arr(Str(min: 1), minItems: 1),
                    ["userIds"]       = Arr(Str(min: 1), minItems: 1),
                    ["groupNames"]    = Arr(Str(min: 1), minItems: 1),
                    ["agentFilter"]   = Obj(
                        required: new[] { "mode" },
                        properties: new(StringComparer.Ordinal)
                        {
                            ["mode"]     = Str(@enum: new object[] { "none", "agentIds", "agentsOnly", "excludeAgents" }),
                            ["agentIds"] = Arr(Str(min: 1), minItems: 1),
                        }),
                    ["promptFilter"]  = Str(@enum: new object[] { "Prompt", "Response", "Both", "Null" }),
                }),
            ["processing"] = Obj(
                required: Array.Empty<string>(),
                properties: new(StringComparer.Ordinal)
                {
                    ["rollup"] = Str(@enum: new object[] { "Rollup", "RollupPlusRaw" }),
                }),
            ["destinations"] = Obj(
                required: Array.Empty<string>(),
                properties: new(StringComparer.Ordinal)
                {
                    ["fact"] = Obj(
                        required: Array.Empty<string>(),
                        properties: new(StringComparer.Ordinal)
                        {
                            ["mode"]           = Str(@enum: new object[] { "outputPath", "append" }),
                            ["path"]           = Str(min: 1),
                            ["appendFile"]     = Str(min: 1),
                            ["appendBehavior"] = Str(@enum: new object[] { "fresh", "append" }),
                        }),
                    ["userInfo"] = Obj(
                        required: new[] { "mode" },
                        properties: new(StringComparer.Ordinal)
                        {
                            ["mode"]       = Str(@enum: new object[] { "outputPath", "append" }),
                            ["path"]       = Str(min: 1),
                            ["appendFile"] = Str(min: 1),
                        }),
                }),
            ["auth"] = Obj(
                // tenantId is recipe content only for app-registration modes;
                // WebLogin / DeviceCode / ManagedIdentity resolve the tenant at
                // runtime/readiness (identity-by-prompt / -by-environment), so
                // only mode is structurally required here. tenantId pattern is
                // still validated when present. AppRegistration tenant binding,
                // if any, is procedural (Test-RecipeAuthProfileBinding parity),
                // not a blanket schema requirement.
                required: new[] { "mode" },
                properties: new(StringComparer.Ordinal)
                {
                    ["mode"]          = Str(@enum: new object[] { "WebLogin", "DeviceCode", "AppRegistrationSecret", "AppRegistrationCertificate", "ManagedIdentity" }),
                    ["tenantId"]      = Str(pattern: UuidPattern),
                    ["authProfileId"] = Str(pattern: UuidPattern),
                }),
            ["advanced"] = Obj(
                required: Array.Empty<string>(),
                properties: new(StringComparer.Ordinal)
                {
                    ["extraArguments"] = Str(),
                }),
        });

    // ============================================================
    //  Helpers for nested property access
    // ============================================================

    private static bool TryGetProperty(JsonObject root, out JsonNode? value, params string[] path)
    {
        JsonNode? cursor = root;
        foreach (var seg in path)
        {
            if (cursor is not JsonObject obj || !obj.TryGetPropertyValue(seg, out var next))
            {
                value = null;
                return false;
            }
            cursor = next;
        }
        value = cursor;
        return true;
    }

    private static string? GetString(JsonObject root, params string[] path)
    {
        if (!TryGetProperty(root, out var node, path)) return null;
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return null;
    }

    private static bool? GetBool(JsonObject root, params string[] path)
    {
        if (!TryGetProperty(root, out var node, path)) return null;
        if (node is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    private static JsonObject? GetObject(JsonObject root, params string[] path)
    {
        if (!TryGetProperty(root, out var node, path)) return null;
        return node as JsonObject;
    }
}
