using System.Globalization;
using System.Text.RegularExpressions;

namespace PAXCookbook.App;

// Read-only / non-mutating native port of the recipe validation pipeline
// (app\broker\Routes\RecipeValidator.ps1): the JSON-schema-subset walker plus
// the 17 cross-field gates, run in the same order as Test-RecipeAll. Emits
// AJV-shaped errors { instancePath, keyword, message, params } that match the
// oracle byte-for-byte. Pure computation: no I/O, no mutation, no PAX touch.
internal static class RecipeValidationModel
{
    // ---- AJV-shaped error helper ----
    private static Dictionary<string, object?> Err(string instancePath, string keyword, string message, Dictionary<string, object?>? prms = null) =>
        new()
        {
            ["instancePath"] = instancePath,
            ["keyword"] = keyword,
            ["message"] = message,
            ["params"] = prms ?? new Dictionary<string, object?>(),
        };

    private static bool Ci(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object?>? Dict(object? o) => o as Dictionary<string, object?>;
    private static bool IsDict(object? o) => o is Dictionary<string, object?>;
    private static List<object?>? Lst(object? o) => o as List<object?>;
    private static bool IsList(object? o) => o is List<object?>;

    private static Dictionary<string, object?>? Child(Dictionary<string, object?>? node, string key) =>
        node is not null && node.ContainsKey(key) ? Dict(node[key]) : null;

    // ---------------------------------------------------------------------
    // Schema model
    // ---------------------------------------------------------------------
    private sealed class SchemaNode
    {
        public string? Type;
        public bool HasConst;
        public object? Const;
        public string[]? Enum;
        public string? Pattern;
        public int? MinLength;
        public int? MaxLength;
        public string? Format;
        public string[]? Required;
        public Dictionary<string, SchemaNode>? Properties;
        public bool AdditionalPropertiesFalse;
        public SchemaNode? Items;
        public int? MinItems;
        public int? MaxItems;
    }

    private static Dictionary<string, SchemaNode> P(params (string Key, SchemaNode Node)[] items)
    {
        var d = new Dictionary<string, SchemaNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in items) { d[k] = v; }
        return d;
    }

    private static readonly (string Pattern, string Keyword)[] OutputPathRejectRules =
    {
        ("^abfss://", "onelake-abfss-uri"),
        ("^onelake://", "onelake-uri"),
        (@"\.onelake\.", "onelake-host"),
        (@"fabric\.microsoft\.com", "fabric-host"),
    };

    private static readonly SchemaNode RecipeSchema = BuildRecipeSchema();

    private static SchemaNode BuildRecipeSchema() => new()
    {
        Type = "object",
        AdditionalPropertiesFalse = true,
        Required = new[] { "recipeId", "recipeSchemaVersion", "paxAdapterVersion", "identity", "ingredients", "query", "processing", "destinations", "auth" },
        Properties = P(
            ("recipeId", new SchemaNode { Type = "string", Pattern = "^[0-9A-HJKMNP-TV-Z]{26}$" }),
            ("recipeSchemaVersion", new SchemaNode { Type = "integer", HasConst = true, Const = 1L }),
            ("paxAdapterVersion", new SchemaNode { Type = "string", Pattern = @"^\d+\.\d+\.\d+$" }),
            ("executionMode", new SchemaNode { Type = "string", Enum = new[] { "local-manual", "local-scheduled", "fabric-hosted", "azure-hosted" } }),
            ("createdAt", new SchemaNode { Type = "string", Format = "date-time" }),
            ("updatedAt", new SchemaNode { Type = "string", Format = "date-time" }),
            ("createdBy", new SchemaNode
            {
                Type = "object", AdditionalPropertiesFalse = true,
                Required = new[] { "cookbookVersion", "bundledPaxVersion", "releaseChannel" },
                Properties = P(
                    ("cookbookVersion", new SchemaNode { Type = "string", MinLength = 1 }),
                    ("bundledPaxVersion", new SchemaNode { Type = "string", Pattern = @"^\d+\.\d+\.\d+$" }),
                    ("releaseChannel", new SchemaNode { Type = "string", MinLength = 1 }),
                    ("fromTemplate", new SchemaNode
                    {
                        Type = "object", AdditionalPropertiesFalse = true,
                        Required = new[] { "templateId", "templateVersion" },
                        Properties = P(
                            ("templateId", new SchemaNode { Type = "string", Pattern = "^[a-z][a-z0-9-]{1,62}[a-z0-9]$" }),
                            ("templateVersion", new SchemaNode { Type = "string", Pattern = @"^\d+\.\d+\.\d+$" }))
                    }))
            }),
            ("identity", new SchemaNode
            {
                Type = "object", AdditionalPropertiesFalse = true, Required = new[] { "name" },
                Properties = P(("name", new SchemaNode { Type = "string", MinLength = 1, MaxLength = 200 }))
            }),
            ("ingredients", new SchemaNode
            {
                Type = "object", AdditionalPropertiesFalse = true, Required = new[] { "m365Usage", "entraUserData" },
                Properties = P(
                    ("m365Usage", new SchemaNode
                    {
                        Type = "object", AdditionalPropertiesFalse = true, Required = new[] { "includeM365Usage" },
                        Properties = P(
                            ("includeM365Usage", new SchemaNode { Type = "boolean" }),
                            ("includeCopilotInteraction", new SchemaNode { Type = "boolean" }))
                    }),
                    ("entraUserData", new SchemaNode
                    {
                        Type = "object", AdditionalPropertiesFalse = true, Required = new[] { "includeUserInfo" },
                        Properties = P(("includeUserInfo", new SchemaNode { Type = "boolean" }))
                    }))
            }),
            ("query", new SchemaNode
            {
                Type = "object", AdditionalPropertiesFalse = true,
                Properties = P(
                    ("startDate", new SchemaNode { Type = "string", Format = "date" }),
                    ("endDate", new SchemaNode { Type = "string", Format = "date" }),
                    // Optional audit date-range mode. 'previous-day' deliberately
                    // omits startDate/endDate so PAX queries the previous full UTC
                    // day; QueryShapeGate recognizes it (and the both-absent shape)
                    // and skips the date-required rule. 'custom'/absent require both.
                    ("dateMode", new SchemaNode { Type = "string", Enum = new[] { "previous-day", "custom" } }),
                    ("mode", new SchemaNode { Type = "string", Enum = new[] { "audit", "userInfoOnly" } }),
                    ("activityTypes", new SchemaNode { Type = "array", MinItems = 1, Items = new SchemaNode { Type = "string", MinLength = 1 } }),
                    ("userIds", new SchemaNode { Type = "array", MinItems = 1, Items = new SchemaNode { Type = "string", MinLength = 1 } }),
                    ("groupNames", new SchemaNode { Type = "array", MinItems = 1, Items = new SchemaNode { Type = "string", MinLength = 1 } }),
                    ("agentFilter", new SchemaNode
                    {
                        Type = "object", AdditionalPropertiesFalse = true, Required = new[] { "mode" },
                        Properties = P(
                            ("mode", new SchemaNode { Type = "string", Enum = new[] { "none", "agentIds", "agentsOnly", "excludeAgents" } }),
                            ("agentIds", new SchemaNode { Type = "array", MinItems = 1, Items = new SchemaNode { Type = "string", MinLength = 1 } }))
                    }),
                    ("promptFilter", new SchemaNode { Type = "string", Enum = new[] { "Prompt", "Response", "Both", "Null" } }))
            }),
            ("processing", new SchemaNode
            {
                Type = "object", AdditionalPropertiesFalse = true,
                Properties = P(
                    ("rollup", new SchemaNode { Type = "string", Enum = new[] { "Rollup", "RollupPlusRaw" } }),
                    ("dashboard", new SchemaNode { Type = "string", Enum = new[] { "aio", "aibv" } }))
            }),
            ("destinations", new SchemaNode
            {
                Type = "object", AdditionalPropertiesFalse = true,
                Properties = P(
                    ("fact", new SchemaNode
                    {
                        Type = "object", AdditionalPropertiesFalse = true,
                        Properties = P(
                            ("path", new SchemaNode { Type = "string", MinLength = 1 }),
                            ("mode", new SchemaNode { Type = "string", Enum = new[] { "outputPath", "append" } }),
                            ("appendBehavior", new SchemaNode { Type = "string", Enum = new[] { "fresh", "append" } }),
                            ("appendFile", new SchemaNode { Type = "string", MinLength = 1 }))
                    }),
                    ("userInfo", new SchemaNode
                    {
                        Type = "object", AdditionalPropertiesFalse = true, Required = new[] { "mode" },
                        Properties = P(
                            ("mode", new SchemaNode { Type = "string", Enum = new[] { "outputPath", "append" } }),
                            ("path", new SchemaNode { Type = "string", MinLength = 1 }),
                            ("appendFile", new SchemaNode { Type = "string", MinLength = 1 }))
                    }))
            }),
            ("auth", new SchemaNode
            {
                // tenantId is recipe content only for the app-registration modes (the operator
                // types it). For the interactive modes (WebLogin, DeviceCode) and ManagedIdentity
                // the tenant is resolved from sign-in / the bound identity at run time. The App*
                // tenantId requirement lives in AppRegistrationTenantGate; tenantId is still
                // pattern-validated whenever present. chefKeyId is an OPTIONAL reference to a Chef's
                // Key (CK-1, WCM-backed). Binding is a runtime-readiness concern, NOT a save-blocking
                // field: App-registration recipes need a bound Chef's Key to be READY but can be saved
                // without one; WebLogin/DeviceCode may optionally bind one for unattended scheduling.
                // No secret ever lives in a recipe.
                Type = "object", AdditionalPropertiesFalse = true, Required = new[] { "mode" },
                Properties = P(
                    ("mode", new SchemaNode { Type = "string", Enum = new[] { "WebLogin", "DeviceCode", "AppRegistrationSecret", "AppRegistrationCertificate", "ManagedIdentity" } }),
                    ("tenantId", new SchemaNode { Type = "string", Pattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$" }),
                    ("chefKeyId", new SchemaNode { Type = "string", Pattern = "^[A-Za-z0-9-]{1,64}$" }))
            }),
            ("advanced", new SchemaNode
            {
                Type = "object", AdditionalPropertiesFalse = true,
                Properties = P(("extraArguments", new SchemaNode { Type = "string" }))
            }),
            ("schedule", new SchemaNode
            {
                // Optional per-recipe schedule (X7). The recipe is the export/import unit, so a
                // scheduled recipe carries its own schedule. Optional at the recipe level (most
                // recipes have none); when present it must be well-formed. The shape mirrors the
                // Windows Task Scheduler registrar's Test-RecurrenceShape exactly (daily|weekly,
                // hour 0..23, minute 0..59, weekly daysOfWeek 0=Sunday..6=Saturday). The range and
                // weekly-daysOfWeek rules the schema subset cannot express are enforced in
                // ScheduleShapeGate. enabled + recurrence are required WITHIN the schedule object so
                // a half-formed schedule is rejected, while schedule itself stays optional. No secret
                // ever lives in a schedule (a bool, a ULID task id, recurrence ints, a timestamp).
                Type = "object", AdditionalPropertiesFalse = true,
                Required = new[] { "enabled", "recurrence" },
                Properties = P(
                    ("enabled", new SchemaNode { Type = "boolean" }),
                    ("scheduledTaskId", new SchemaNode { Type = "string", Pattern = "^[0-9A-HJKMNP-TV-Z]{26}$" }),
                    ("recurrence", new SchemaNode
                    {
                        Type = "object", AdditionalPropertiesFalse = true,
                        Required = new[] { "kind", "hour", "minute" },
                        Properties = P(
                            ("kind", new SchemaNode { Type = "string", Enum = new[] { "daily", "weekly" } }),
                            ("hour", new SchemaNode { Type = "integer" }),
                            ("minute", new SchemaNode { Type = "integer" }),
                            ("daysOfWeek", new SchemaNode { Type = "array", Items = new SchemaNode { Type = "integer" } }))
                    }),
                    ("updatedAt", new SchemaNode { Type = "string", Format = "date-time" }))
            }),
            ("importMetadata", new SchemaNode
            {
                Type = "object", AdditionalPropertiesFalse = true, Required = new[] { "source" },
                Properties = P(
                    ("source", new SchemaNode { Type = "string", Enum = new[] { "mini-kitchen-lite" } }),
                    ("importedAtUtc", new SchemaNode { Type = "string", Format = "date-time" }),
                    ("originalKind", new SchemaNode { Type = "string", Enum = new[] { "pax-cookbook-mini-recipe" } }),
                    ("originalSchemaVersion", new SchemaNode { Type = "string", MinLength = 1, MaxLength = 32 }),
                    ("originalIdentity", new SchemaNode
                    {
                        Type = "object", AdditionalPropertiesFalse = true,
                        Properties = P(
                            ("description", new SchemaNode { Type = "string", MaxLength = 4000 }),
                            ("tags", new SchemaNode { Type = "array", MaxItems = 64, Items = new SchemaNode { Type = "string", MinLength = 1, MaxLength = 64 } }))
                    }),
                    ("originalCreatedBy", new SchemaNode
                    {
                        Type = "object", AdditionalPropertiesFalse = true,
                        Properties = P(
                            ("tool", new SchemaNode { Type = "string", MinLength = 1, MaxLength = 128 }),
                            ("site", new SchemaNode { Type = "string", MinLength = 1, MaxLength = 512 }))
                    }),
                    ("compatibility", new SchemaNode
                    {
                        Type = "object", AdditionalPropertiesFalse = true,
                        Properties = P(("cookbookRecipeSchemaVersion", new SchemaNode { Type = "integer" }))
                    }),
                    ("commandPreview", new SchemaNode { Type = "string", MaxLength = 8000 }),
                    ("permissions", new SchemaNode { Type = "array", MaxItems = 64, Items = new SchemaNode { Type = "string", MinLength = 1, MaxLength = 256 } }),
                    ("importBehavior", new SchemaNode
                    {
                        Type = "object", AdditionalPropertiesFalse = true,
                        Properties = P(
                            ("state", new SchemaNode { Type = "string", Enum = new[] { "needsPrep" } }),
                            ("openInPrepStation", new SchemaNode { Type = "boolean" }))
                    }),
                    ("mappingWarnings", new SchemaNode
                    {
                        Type = "array", MaxItems = 128,
                        Items = new SchemaNode
                        {
                            Type = "object", AdditionalPropertiesFalse = true, Required = new[] { "code" },
                            Properties = P(
                                ("code", new SchemaNode { Type = "string", MinLength = 1, MaxLength = 64 }),
                                ("path", new SchemaNode { Type = "string", MaxLength = 256 }),
                                ("detail", new SchemaNode { Type = "string", MaxLength = 1024 }))
                        }
                    }))
            })),
    };

    // ---------------------------------------------------------------------
    // Schema walker (Test-RecipeSchemaNode)
    // ---------------------------------------------------------------------
    private static void WalkNode(object? node, SchemaNode schema, string instancePath, List<object> errors)
    {
        // ---- type ----
        if (schema.Type is not null)
        {
            bool actualOk = schema.Type switch
            {
                "object" => IsDict(node),
                "string" => node is string,
                "integer" => node is long,
                "number" => node is long or double,
                "boolean" => node is bool,
                "array" => IsList(node),
                "null" => node is null,
                _ => false,
            };
            if (!actualOk)
            {
                errors.Add(Err(instancePath, "type", "must be " + schema.Type, new() { ["type"] = schema.Type }));
                return;
            }
        }

        // ---- const ----
        if (schema.HasConst)
        {
            if (!ConstEquals(node, schema.Const))
            {
                errors.Add(Err(instancePath, "const", "must be equal to constant", new() { ["allowedValue"] = schema.Const }));
            }
        }

        // ---- enum ----
        if (schema.Enum is not null)
        {
            bool ok = node is string s && schema.Enum.Any(e => Ci(s, e));
            if (!ok)
            {
                errors.Add(Err(instancePath, "enum", "must be equal to one of the allowed values", new() { ["allowedValues"] = schema.Enum }));
            }
        }

        // ---- string-specific ----
        if (node is string str)
        {
            if (schema.MinLength is int min && str.Length < min)
            {
                errors.Add(Err(instancePath, "minLength", "must NOT have fewer than " + min + " characters", new() { ["limit"] = min }));
            }
            if (schema.MaxLength is int max && str.Length > max)
            {
                errors.Add(Err(instancePath, "maxLength", "must NOT have more than " + max + " characters", new() { ["limit"] = max }));
            }
            if (schema.Pattern is not null && !Regex.IsMatch(str, schema.Pattern, RegexOptions.IgnoreCase))
            {
                errors.Add(Err(instancePath, "pattern", "must match pattern \"" + schema.Pattern + "\"", new() { ["pattern"] = schema.Pattern }));
            }
            if (schema.Format is not null)
            {
                bool fmtOk = true;
                if (schema.Format == "date")
                {
                    if (!Regex.IsMatch(str, @"^\d{4}-\d{2}-\d{2}$"))
                    {
                        fmtOk = false;
                    }
                    else if (!DateTime.TryParseExact(str, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    {
                        fmtOk = false;
                    }
                }
                else if (schema.Format == "date-time")
                {
                    if (!DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                    {
                        fmtOk = false;
                    }
                }
                if (!fmtOk)
                {
                    errors.Add(Err(instancePath, "format", "must match format \"" + schema.Format + "\"", new() { ["format"] = schema.Format }));
                }
            }
        }

        // ---- object-specific ----
        if (Dict(node) is Dictionary<string, object?> obj)
        {
            var nodeKeys = obj.Keys.ToList();

            if (schema.Required is not null)
            {
                foreach (string missing in schema.Required)
                {
                    // List<string>.Contains is ordinal (case-sensitive), like the oracle.
                    if (!nodeKeys.Contains(missing))
                    {
                        errors.Add(Err(instancePath, "required", "must have required property '" + missing + "'", new() { ["missingProperty"] = missing }));
                    }
                }
            }

            Dictionary<string, SchemaNode>? childSchemas = schema.Properties;

            if (schema.AdditionalPropertiesFalse)
            {
                foreach (string k in nodeKeys)
                {
                    if (childSchemas is null || !childSchemas.ContainsKey(k))
                    {
                        errors.Add(Err(instancePath, "additionalProperties", "must NOT have additional property '" + k + "'", new() { ["additionalProperty"] = k }));
                    }
                }
            }

            if (childSchemas is not null)
            {
                foreach (string k in nodeKeys)
                {
                    if (childSchemas.TryGetValue(k, out SchemaNode? childSchema))
                    {
                        WalkNode(obj[k], childSchema, instancePath + "/" + k, errors);
                    }
                }
            }
        }

        // ---- array-specific ----
        if (Lst(node) is List<object?> arr)
        {
            int count = arr.Count;
            if (schema.MinItems is int mi && count < mi)
            {
                errors.Add(Err(instancePath, "minItems", "must NOT have fewer than " + mi + " items", new() { ["limit"] = mi }));
            }
            if (schema.MaxItems is int ma && count > ma)
            {
                errors.Add(Err(instancePath, "maxItems", "must NOT have more than " + ma + " items", new() { ["limit"] = ma }));
            }
            if (schema.Items is not null)
            {
                for (int i = 0; i < count; i++)
                {
                    WalkNode(arr[i], schema.Items, instancePath + "/" + i, errors);
                }
            }
        }
    }

    private static bool ConstEquals(object? node, object? constVal)
    {
        if (node is long nl && constVal is long cl) { return nl == cl; }
        if (node is string ns && constVal is string cs) { return Ci(ns, cs); }
        return Equals(node, constVal);
    }

    private static void SchemaErrors(object? recipe, List<object> errors) =>
        WalkNode(recipe, RecipeSchema, string.Empty, errors);

    // ---------------------------------------------------------------------
    // Gate 2: output-path tier (OneLake / Fabric ban)
    // ---------------------------------------------------------------------
    private static void TierGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        Dictionary<string, object?>? fact = Child(Child(recipe, "destinations"), "fact");
        if (fact is null || !fact.ContainsKey("path")) { return; }
        string path = JsonModel.Str(fact["path"]);
        if (path.Length == 0) { return; }
        foreach (var (pattern, keyword) in OutputPathRejectRules)
        {
            if (Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase))
            {
                errors.Add(Err("/destinations/fact/path", "m1OutputTier",
                    "OneLake / Fabric destinations are not supported in M1",
                    new() { ["rejectedBy"] = keyword, ["pattern"] = pattern }));
                break;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Gate 3: query date range
    // ---------------------------------------------------------------------
    private static void DateRangeGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        Dictionary<string, object?>? query = Child(recipe, "query");
        if (query is null || !query.ContainsKey("startDate") || !query.ContainsKey("endDate")) { return; }
        string s = JsonModel.Str(query["startDate"]);
        string e = JsonModel.Str(query["endDate"]);
        if (!Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$") || !Regex.IsMatch(e, @"^\d{4}-\d{2}-\d{2}$")) { return; }
        if (!DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime ds)) { return; }
        if (!DateTime.TryParseExact(e, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime de)) { return; }
        if (de <= ds)
        {
            errors.Add(Err("/query/endDate", "dateRange", "End date must be after the start date (the end date is exclusive).",
                new() { ["startDate"] = s, ["endDate"] = e }));
        }
    }

    private static string ExtraArguments(Dictionary<string, object?> recipe)
    {
        Dictionary<string, object?>? adv = Child(recipe, "advanced");
        if (adv is null || !adv.ContainsKey("extraArguments")) { return string.Empty; }
        return JsonModel.Str(adv["extraArguments"]);
    }

    // ---------------------------------------------------------------------
    // Gate 4: removed-switch trailer (adapter scan)
    // ---------------------------------------------------------------------
    private static void RemovedSwitchGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        string extra = ExtraArguments(recipe);
        if (string.IsNullOrWhiteSpace(extra)) { return; }
        try
        {
            PaxAdapter.ScanRemovedSwitches(extra);
        }
        catch (PaxAdapter.ProjectionException ex)
        {
            errors.Add(Err("/advanced/extraArguments", "removedSwitch", ex.Message));
        }
    }

    // ---------------------------------------------------------------------
    // Back-compat: deprecated auth field stripping (CK-2 / B2)
    //
    // chefKeyId (CK-1, WCM-backed) supersedes the deprecated auth.authProfileId
    // reference. An imported or legacy recipe may still carry authProfileId; the
    // closed schema (additionalProperties:false on auth) would otherwise reject
    // it. Strip the stray field so such recipes load/save cleanly. Safe no-op
    // when absent; no other recipe data is touched.
    // ---------------------------------------------------------------------
    internal static void StripDeprecatedAuthFields(Dictionary<string, object?> recipe)
    {
        if (recipe.TryGetValue("auth", out object? a) && a is Dictionary<string, object?> auth)
        {
            auth.Remove("authProfileId");
        }
    }

    // ---------------------------------------------------------------------
    // Gate 5: app-registration tenant declaration
    //
    // App-registration sign-in is non-interactive, so the tenant must be
    // declared in the recipe (it projects to -TenantId). Chef's Key binding is
    // NOT validated here: binding is a runtime-readiness concern (the preview /
    // readiness projection resolves the bound Chef's Key), never a save gate, so
    // a recipe can be saved without a chefKeyId. The interactive modes (WebLogin,
    // DeviceCode) and ManagedIdentity resolve the tenant at run time and may
    // optionally carry a chefKeyId, so nothing is required of them here.
    // ---------------------------------------------------------------------
    private static void AppRegistrationTenantGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        Dictionary<string, object?>? auth = Child(recipe, "auth");
        if (auth is null) { return; }
        string mode = auth.ContainsKey("mode") ? JsonModel.Str(auth["mode"]) : string.Empty;
        bool appMode = Ci(mode, "AppRegistrationSecret") || Ci(mode, "AppRegistrationCertificate");

        if (appMode)
        {
            bool hasTenant = auth.ContainsKey("tenantId") && !string.IsNullOrWhiteSpace(JsonModel.Str(auth["tenantId"]));
            if (!hasTenant)
            {
                errors.Add(Err("/auth/tenantId", "required",
                    "must have tenantId when auth.mode is AppRegistrationSecret or AppRegistrationCertificate (app-registration sign-in is non-interactive, so the tenant must be declared in the recipe; interactive modes resolve the tenant from sign-in at run time)",
                    new() { ["mode"] = mode }));
            }
        }
    }

    // ---------------------------------------------------------------------
    // Gate 6: execution-mode x auth-mode matrix
    // ---------------------------------------------------------------------
    private static readonly Dictionary<string, string[]> ExecutionModeAuthMatrix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["local-manual"] = new[] { "WebLogin", "DeviceCode", "AppRegistrationSecret", "AppRegistrationCertificate" },
        ["local-scheduled"] = new[] { "WebLogin", "DeviceCode", "AppRegistrationSecret", "AppRegistrationCertificate" },
        ["fabric-hosted"] = new[] { "ManagedIdentity", "AppRegistrationSecret", "AppRegistrationCertificate" },
        ["azure-hosted"] = new[] { "ManagedIdentity", "AppRegistrationSecret", "AppRegistrationCertificate" },
    };

    private static void ExecutionModeAuthMatrixGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        Dictionary<string, object?>? auth = Child(recipe, "auth");
        if (auth is null || !auth.ContainsKey("mode")) { return; }
        string mode = JsonModel.Str(auth["mode"]);
        string exec = "local-manual";
        if (recipe.ContainsKey("executionMode")) { exec = JsonModel.Str(recipe["executionMode"]); }
        if (!ExecutionModeAuthMatrix.TryGetValue(exec, out string[]? allowed)) { return; }
        if (!allowed.Contains(mode, StringComparer.OrdinalIgnoreCase))
        {
            string rationale = exec.ToLowerInvariant() switch
            {
                "local-manual" => "local-manual permits WebLogin, DeviceCode, AppRegistrationSecret, or AppRegistrationCertificate (interactive modes require a chef at the keyboard)",
                "local-scheduled" => "local-scheduled permits WebLogin, DeviceCode, AppRegistrationSecret, or AppRegistrationCertificate (a bound Chef's Key carries the scheduled-run identity; Managed Identity is not available on a local Windows desktop)",
                "fabric-hosted" => "fabric-hosted permits ManagedIdentity, AppRegistrationSecret, or AppRegistrationCertificate only (no interactive surface in Fabric runtimes)",
                "azure-hosted" => "azure-hosted permits ManagedIdentity, AppRegistrationSecret, or AppRegistrationCertificate only (no interactive surface in Azure-hosted runtimes)",
                _ => string.Empty,
            };
            errors.Add(Err("/auth/mode", "executionModeMismatch",
                $"auth.mode '{mode}' is not valid for executionMode '{exec}'. {rationale}.",
                new() { ["executionMode"] = exec, ["mode"] = mode, ["allowed"] = allowed }));
        }
    }

    // ---------------------------------------------------------------------
    // Gate 6a: per-recipe schedule shape (X7)
    //
    // Runs only when an OPTIONAL schedule block is present. Mirrors the Windows
    // Task Scheduler registrar's Test-RecurrenceShape
    // (app/install/Register-PAXScheduledRecipe.ps1) exactly so a recipe that the
    // schema accepts also satisfies the registrar at register time: daily|weekly,
    // hour 0..23, minute 0..59, and (weekly only) a 1..7-entry daysOfWeek array of
    // unique ints 0=Sunday..6=Saturday. daysOfWeek is ignored for a daily schedule,
    // matching the registrar. Scheduling is orthogonal to executionMode (no
    // cross-field rule is imposed here). A schedule carries no secret. The gate
    // never throws: a missing/non-object recurrence is left to the schema's
    // required/type errors.
    // ---------------------------------------------------------------------
    private const string ScheduledTaskIdPattern = "^[0-9A-HJKMNP-TV-Z]{26}$";

    private static void ScheduleShapeGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        Dictionary<string, object?>? schedule = Child(recipe, "schedule");
        if (schedule is null) { return; }

        // enabled must be a real JSON boolean (the schema also type-checks; this
        // adds the registrar-taxonomy keyword so a malformed enabled is explicit).
        if (schedule.ContainsKey("enabled") && schedule["enabled"] is not bool)
        {
            errors.Add(Err("/schedule/enabled", "scheduleEnabledInvalid",
                "schedule.enabled must be a boolean.",
                new() { ["type"] = schedule["enabled"]?.GetType().Name }));
        }

        // scheduledTaskId, when present, must be a 26-char Crockford base32 ULID.
        if (schedule.ContainsKey("scheduledTaskId"))
        {
            bool tidOk = schedule["scheduledTaskId"] is string tid
                && Regex.IsMatch(tid, ScheduledTaskIdPattern, RegexOptions.IgnoreCase);
            if (!tidOk)
            {
                errors.Add(Err("/schedule/scheduledTaskId", "scheduledTaskIdInvalid",
                    "schedule.scheduledTaskId must be a 26-character Crockford base32 ULID.",
                    new() { ["pattern"] = ScheduledTaskIdPattern }));
            }
        }

        Dictionary<string, object?>? recurrence = Child(schedule, "recurrence");
        if (recurrence is null)
        {
            // A missing or non-object recurrence is already rejected by the schema
            // (required/type). Guard here so the gate never dereferences null.
            return;
        }

        string kind = recurrence.ContainsKey("kind") ? JsonModel.Str(recurrence["kind"]) : string.Empty;
        if (recurrence.ContainsKey("kind") && !Ci(kind, "daily") && !Ci(kind, "weekly"))
        {
            errors.Add(Err("/schedule/recurrence/kind", "recurrenceKindInvalid",
                "schedule.recurrence.kind must be 'daily' or 'weekly'.",
                new() { ["kind"] = kind }));
        }

        if (recurrence.ContainsKey("hour")
            && (!JsonModel.TryInt(recurrence["hour"], out int hour) || hour < 0 || hour > 23))
        {
            errors.Add(Err("/schedule/recurrence/hour", "recurrenceHourOutOfRange",
                "schedule.recurrence.hour must be an integer in [0, 23].",
                new() { ["hour"] = recurrence["hour"] }));
        }

        if (recurrence.ContainsKey("minute")
            && (!JsonModel.TryInt(recurrence["minute"], out int minute) || minute < 0 || minute > 59))
        {
            errors.Add(Err("/schedule/recurrence/minute", "recurrenceMinuteOutOfRange",
                "schedule.recurrence.minute must be an integer in [0, 59].",
                new() { ["minute"] = recurrence["minute"] }));
        }

        // daysOfWeek: required + constrained for weekly; ignored for daily (mirrors
        // the registrar, which only reads daysOfWeek when kind == 'weekly').
        if (Ci(kind, "weekly"))
        {
            if (!recurrence.ContainsKey("daysOfWeek"))
            {
                errors.Add(Err("/schedule/recurrence/daysOfWeek", "weeklyDaysOfWeekMissing",
                    "schedule.recurrence.daysOfWeek is required when schedule.recurrence.kind='weekly'.",
                    new() { ["kind"] = kind }));
            }
            else if (Lst(recurrence["daysOfWeek"]) is not List<object?> days)
            {
                errors.Add(Err("/schedule/recurrence/daysOfWeek", "weeklyDaysOfWeekInvalid",
                    "schedule.recurrence.daysOfWeek must be an array of integers (0=Sunday..6=Saturday).",
                    new() { ["reason"] = "notArray" }));
            }
            else if (days.Count < 1 || days.Count > 7)
            {
                errors.Add(Err("/schedule/recurrence/daysOfWeek", "weeklyDaysOfWeekInvalid",
                    "schedule.recurrence.daysOfWeek must contain between 1 and 7 entries.",
                    new() { ["reason"] = "length", ["count"] = days.Count }));
            }
            else
            {
                var seen = new HashSet<int>();
                foreach (object? d in days)
                {
                    if (!JsonModel.TryInt(d, out int di) || di < 0 || di > 6)
                    {
                        errors.Add(Err("/schedule/recurrence/daysOfWeek", "weeklyDaysOfWeekInvalid",
                            "schedule.recurrence.daysOfWeek entries must be integers in [0, 6] (0=Sunday..6=Saturday).",
                            new() { ["reason"] = "entryOutOfRange", ["entry"] = d }));
                        break;
                    }
                    if (!seen.Add(di))
                    {
                        errors.Add(Err("/schedule/recurrence/daysOfWeek", "weeklyDaysOfWeekInvalid",
                            "schedule.recurrence.daysOfWeek entries must be unique.",
                            new() { ["reason"] = "duplicate", ["entry"] = di }));
                        break;
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // Gate 7: extraArguments secret-shape (validator denylist)
    // ---------------------------------------------------------------------
    private static readonly string[] SecretShapeFlags =
    {
        "ClientSecret", "Password", "Pwd", "AppKey", "Secret", "Token",
        "AccessToken", "RefreshToken", "Bearer", "CertificatePassword",
        "CertPassword", "PfxPassword", "ApiKey",
    };

    private static void SecretShapeGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        string extra = ExtraArguments(recipe);
        if (string.IsNullOrWhiteSpace(extra)) { return; }
        foreach (string name in SecretShapeFlags)
        {
            string pattern = @"(^|\s)-" + Regex.Escape(name) + @"($|\s|=|:)";
            if (Regex.IsMatch(extra, pattern, RegexOptions.IgnoreCase))
            {
                errors.Add(Err("/advanced/extraArguments", "secretShape",
                    $"advanced.extraArguments contains a secret-shape flag '-{name}'. " +
                    "Cookbook never accepts inline secret material in recipe extraArguments. " +
                    "Use a Chef's Key so the secret lives only in Windows Credential Manager, " +
                    "or remove the flag entirely if it is not needed."));
            }
        }
    }

    // ---------------------------------------------------------------------
    // Gate 8: fact output mode
    // ---------------------------------------------------------------------
    private static void FactOutputModeGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        Dictionary<string, object?>? fact = Child(Child(recipe, "destinations"), "fact");
        if (fact is null) { return; }

        bool hasMode = fact.ContainsKey("mode");
        bool hasBehavior = fact.ContainsKey("appendBehavior");
        bool hasPath = fact.ContainsKey("path");
        bool hasFile = fact.ContainsKey("appendFile");
        string mode = hasMode ? JsonModel.Str(fact["mode"]) : string.Empty;
        string behavior = hasBehavior ? JsonModel.Str(fact["appendBehavior"]) : string.Empty;
        string path = hasPath ? JsonModel.Str(fact["path"]) : string.Empty;
        string file = hasFile ? JsonModel.Str(fact["appendFile"]) : string.Empty;

        if (hasMode && hasBehavior)
        {
            string expectedBehavior = mode.ToLowerInvariant() switch
            {
                "outputpath" => "fresh",
                "append" => "append",
                _ => string.Empty,
            };
            if (expectedBehavior.Length > 0 && !Ci(behavior, expectedBehavior))
            {
                errors.Add(Err("/destinations/fact/appendBehavior", "factModeBehaviorMismatch",
                    $"destinations.fact.appendBehavior '{behavior}' contradicts destinations.fact.mode '{mode}'. " +
                    "mode='outputPath' requires appendBehavior='fresh' (or omit appendBehavior); " +
                    "mode='append' requires appendBehavior='append' (or omit appendBehavior). " +
                    "Cookbook prefers writing only 'mode' going forward; 'appendBehavior' is preserved only as a legacy alias.",
                    new() { ["mode"] = mode, ["appendBehavior"] = behavior, ["expectedAppendBehavior"] = expectedBehavior }));
                return;
            }
        }

        string effectiveMode;
        if (hasMode) { effectiveMode = mode; }
        else if (hasBehavior && Ci(behavior, "append")) { effectiveMode = "append"; }
        else { effectiveMode = "outputPath"; }

        if (!hasPath && !hasFile)
        {
            errors.Add(Err("/destinations/fact", "factOutputTargetRequired",
                "destinations.fact must contain at least one of {path, appendFile} so a fact-output target exists.",
                new() { ["effectiveMode"] = effectiveMode }));
            return;
        }

        if (Ci(effectiveMode, "outputPath"))
        {
            if (!hasPath || path.Length == 0)
            {
                errors.Add(Err("/destinations/fact/path", "factPathRequired",
                    "destinations.fact.path is required and must be non-empty when destinations.fact.mode='outputPath' (or appendBehavior is 'fresh'/absent).",
                    new() { ["effectiveMode"] = effectiveMode, ["mode"] = mode, ["appendBehavior"] = behavior }));
            }
            if (hasFile)
            {
                errors.Add(Err("/destinations/fact/appendFile", "factAppendFileForbidden",
                    "destinations.fact.appendFile must be absent when destinations.fact.mode='outputPath' (or appendBehavior is 'fresh'/absent).",
                    new() { ["effectiveMode"] = effectiveMode, ["mode"] = mode, ["appendBehavior"] = behavior }));
            }
        }
        else if (Ci(effectiveMode, "append"))
        {
            if (!hasFile || file.Length == 0)
            {
                errors.Add(Err("/destinations/fact/appendFile", "factAppendFileRequired",
                    "destinations.fact.appendFile is required and must be non-empty when destinations.fact.mode='append' (or appendBehavior='append').",
                    new() { ["effectiveMode"] = effectiveMode, ["mode"] = mode, ["appendBehavior"] = behavior }));
            }
            if (hasMode && hasPath)
            {
                errors.Add(Err("/destinations/fact/path", "factPathInertUnderAppendMode",
                    "destinations.fact.path is inert when destinations.fact.mode='append'; the adapter projects only -AppendFile. " +
                    "Remove path from the recipe so the shape matches the S26 contract (one of {path, appendFile} per mode).",
                    new() { ["effectiveMode"] = effectiveMode, ["mode"] = mode }));
            }
        }
    }

    // ---------------------------------------------------------------------
    // Gate 9: userInfo output mode
    // ---------------------------------------------------------------------
    private static void UserInfoOutputModeGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        Dictionary<string, object?>? ui = Child(Child(recipe, "destinations"), "userInfo");
        if (ui is null) { return; }

        bool hasMode = ui.ContainsKey("mode");
        bool hasPath = ui.ContainsKey("path");
        bool hasFile = ui.ContainsKey("appendFile");
        string mode = hasMode ? JsonModel.Str(ui["mode"]) : string.Empty;

        if (!hasMode || mode.Length == 0) { return; }

        if (Ci(mode, "outputPath"))
        {
            if (!hasPath)
            {
                errors.Add(Err("/destinations/userInfo/path", "userInfoPathRequired",
                    "destinations.userInfo.path is required when destinations.userInfo.mode='outputPath'.",
                    new() { ["mode"] = mode }));
            }
            if (hasFile)
            {
                errors.Add(Err("/destinations/userInfo/appendFile", "userInfoAppendFileForbidden",
                    "destinations.userInfo.appendFile must be absent when destinations.userInfo.mode='outputPath' (-OutputPathUserInfo and -AppendUserInfo are mutually exclusive).",
                    new() { ["mode"] = mode }));
            }
        }
        else if (Ci(mode, "append"))
        {
            if (!hasFile)
            {
                errors.Add(Err("/destinations/userInfo/appendFile", "userInfoAppendFileRequired",
                    "destinations.userInfo.appendFile is required when destinations.userInfo.mode='append'.",
                    new() { ["mode"] = mode }));
            }
            if (hasPath)
            {
                errors.Add(Err("/destinations/userInfo/path", "userInfoPathForbidden",
                    "destinations.userInfo.path must be absent when destinations.userInfo.mode='append' (-OutputPathUserInfo and -AppendUserInfo are mutually exclusive).",
                    new() { ["mode"] = mode }));
            }
        }
    }

    // ---------------------------------------------------------------------
    // Gate 10: query shape (audit vs userInfoOnly)
    // ---------------------------------------------------------------------
    private static void QueryShapeGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        Dictionary<string, object?>? query = Child(recipe, "query");
        if (query is null) { return; }
        string mode = query.ContainsKey("mode") ? JsonModel.Str(query["mode"]) : string.Empty;

        if (mode.Length == 0 || Ci(mode, "audit"))
        {
            // Rollup is OPTIONAL for audit-shape recipes (query.mode='audit' or
            // absent). An operator may run PAX only to pull raw audit data, so a
            // missing processing.rollup is allowed. When present it maps to
            // -Rollup / -RollupPlusRaw at runtime; when absent the command omits
            // it. Rollup remains forbidden under userInfoOnly (handled below).
            //
            // Date range (Previous Day): an audit recipe may deliberately omit
            // BOTH startDate and endDate to query the previous full UTC day —
            // either explicitly (query.dateMode='previous-day') or simply by
            // leaving both absent (absence is the signal PAX itself keys off via
            // $PSBoundParameters). That dateless shape is VALID and READY. A
            // half-filled range (exactly one of start/end) is still a footgun —
            // PAX would fill the missing side with '*' (unbounded) — so the
            // missing side is still required. A custom range (dateMode='custom'
            // or any single date present) requires both.
            bool hasStart = query.ContainsKey("startDate");
            bool hasEnd = query.ContainsKey("endDate");
            string dateMode = query.ContainsKey("dateMode") ? JsonModel.Str(query["dateMode"]) : string.Empty;
            bool previousDay = Ci(dateMode, "previous-day") || (!hasStart && !hasEnd);
            if (previousDay)
            {
                // Both-absent is the valid previous-day shape (no date error).
                // Block only a half-filled range — the missing side of a range
                // whose other side IS set.
                if (hasStart && !hasEnd)
                {
                    errors.Add(Err("/query/endDate", "endDateRequiredUnderAudit",
                        "query.endDate is required when query.startDate is set; a date range needs both sides. Omit both for previous-day mode.",
                        new() { ["queryMode"] = mode }));
                }
                if (hasEnd && !hasStart)
                {
                    errors.Add(Err("/query/startDate", "startDateRequiredUnderAudit",
                        "query.startDate is required when query.endDate is set; a date range needs both sides. Omit both for previous-day mode.",
                        new() { ["queryMode"] = mode }));
                }
            }
            else
            {
                if (!hasStart)
                {
                    errors.Add(Err("/query/startDate", "startDateRequiredUnderAudit",
                        "query.startDate is required for audit-shape recipes (query.mode='audit' or absent).",
                        new() { ["queryMode"] = mode }));
                }
                if (!hasEnd)
                {
                    errors.Add(Err("/query/endDate", "endDateRequiredUnderAudit",
                        "query.endDate is required for audit-shape recipes (query.mode='audit' or absent).",
                        new() { ["queryMode"] = mode }));
                }
            }
            bool hasFact = false;
            Dictionary<string, object?>? dest = Child(recipe, "destinations");
            if (dest is not null && dest.ContainsKey("fact")) { hasFact = true; }
            if (!hasFact)
            {
                errors.Add(Err("/destinations/fact", "factDestinationRequiredUnderAudit",
                    "destinations.fact is required for audit-shape recipes (query.mode='audit' or absent). The audit query must declare where its fact output is written.",
                    new() { ["queryMode"] = mode }));
            }
            return;
        }

        if (!Ci(mode, "userInfoOnly")) { return; }

        // ----- Shape 3 gating -----
        Dictionary<string, object?>? processing = Child(recipe, "processing");
        if (processing is not null && processing.ContainsKey("rollup"))
        {
            errors.Add(Err("/processing/rollup", "rollupForbiddenUnderUserInfoOnly",
                "processing.rollup must be absent when query.mode='userInfoOnly' (Shape 3 -- user-info-only runs skip the audit query and the rollup post-processor).",
                new() { ["queryMode"] = mode, ["rollup"] = JsonModel.Str(processing["rollup"]) }));
        }

        Dictionary<string, object?>? ing = Child(recipe, "ingredients");
        if (ing is not null)
        {
            Dictionary<string, object?>? m = Child(ing, "m365Usage");
            if (m is not null)
            {
                if (m.ContainsKey("includeM365Usage") && JsonModel.Bool(m["includeM365Usage"]))
                {
                    errors.Add(Err("/ingredients/m365Usage/includeM365Usage", "m365UsageForbiddenUnderUserInfoOnly",
                        "ingredients.m365Usage.includeM365Usage must be false when query.mode='userInfoOnly' (no audit query runs in Shape 3).",
                        new() { ["queryMode"] = mode }));
                }
                if (m.ContainsKey("includeCopilotInteraction"))
                {
                    errors.Add(Err("/ingredients/m365Usage/includeCopilotInteraction", "includeCopilotInteractionForbiddenUnderUserInfoOnly",
                        "ingredients.m365Usage.includeCopilotInteraction must be absent when query.mode='userInfoOnly' (no audit query runs).",
                        new() { ["queryMode"] = mode }));
                }
            }
            Dictionary<string, object?>? eud = Child(ing, "entraUserData");
            if (eud is not null)
            {
                bool hasIUI = eud.ContainsKey("includeUserInfo");
                bool iuiVal = hasIUI && JsonModel.Bool(eud["includeUserInfo"]);
                if (!hasIUI || !iuiVal)
                {
                    errors.Add(Err("/ingredients/entraUserData/includeUserInfo", "userInfoOnlyRequiresIncludeUserInfoTrue",
                        "ingredients.entraUserData.includeUserInfo must be true when query.mode='userInfoOnly' (Shape 3's whole purpose is to fetch user-info data).",
                        new() { ["queryMode"] = mode }));
                }
            }
        }

        foreach (string k in new[] { "activityTypes", "userIds", "groupNames", "agentFilter", "promptFilter" })
        {
            if (query.ContainsKey(k))
            {
                errors.Add(Err("/query/" + k, "auditFilterForbiddenUnderUserInfoOnly",
                    $"query.{k} must be absent when query.mode='userInfoOnly' (audit-only filter fields are not applicable to user-info-only runs).",
                    new() { ["queryMode"] = mode, ["field"] = k }));
            }
        }

        if (query.ContainsKey("startDate"))
        {
            errors.Add(Err("/query/startDate", "userInfoOnlyForbidsStartDate",
                "query.startDate must be absent when query.mode='userInfoOnly' (no audit query runs in Shape 3; user-info data has no date-range parameter).",
                new() { ["queryMode"] = mode }));
        }
        if (query.ContainsKey("endDate"))
        {
            errors.Add(Err("/query/endDate", "userInfoOnlyForbidsEndDate",
                "query.endDate must be absent when query.mode='userInfoOnly' (no audit query runs in Shape 3; user-info data has no date-range parameter).",
                new() { ["queryMode"] = mode }));
        }
        if (query.ContainsKey("dateMode"))
        {
            errors.Add(Err("/query/dateMode", "userInfoOnlyForbidsDateMode",
                "query.dateMode must be absent when query.mode='userInfoOnly' (the audit date-range mode does not apply to a user-info-only run).",
                new() { ["queryMode"] = mode }));
        }

        bool hasUserInfo = false;
        bool hasFactDest = false;
        Dictionary<string, object?>? dest2 = Child(recipe, "destinations");
        if (dest2 is not null)
        {
            if (dest2.ContainsKey("userInfo")) { hasUserInfo = true; }
            if (dest2.ContainsKey("fact")) { hasFactDest = true; }
        }
        if (!hasUserInfo)
        {
            errors.Add(Err("/destinations/userInfo", "userInfoRequiredUnderUserInfoOnly",
                "destinations.userInfo is required when query.mode='userInfoOnly' (Shape 3 must declare where user-info output is written; -OutputPathUserInfo or -AppendUserInfo).",
                new() { ["queryMode"] = mode }));
        }
        if (hasFactDest)
        {
            errors.Add(Err("/destinations/fact", "userInfoOnlyForbidsFactDestination",
                "destinations.fact must be absent when query.mode='userInfoOnly' (Shape 3 emits user-info output only; no audit/fact rows are produced).",
                new() { ["queryMode"] = mode }));
        }
    }

    // ---------------------------------------------------------------------
    // Gate 11: activityTypes under rollup
    // ---------------------------------------------------------------------
    private static void ActivityTypesUnderRollupGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        bool hasRollup = false;
        Dictionary<string, object?>? proc = Child(recipe, "processing");
        if (proc is not null && proc.ContainsKey("rollup"))
        {
            string rv = JsonModel.Str(proc["rollup"]);
            if (Ci(rv, "Rollup") || Ci(rv, "RollupPlusRaw")) { hasRollup = true; }
        }
        if (!hasRollup) { return; }

        Dictionary<string, object?>? query = Child(recipe, "query");
        if (query is null || !query.ContainsKey("activityTypes")) { return; }
        List<object?>? at = Lst(query["activityTypes"]);
        if (at is null) { return; }
        if (at.Count != 1 || !Ci(JsonModel.Str(at[0]), "CopilotInteraction"))
        {
            errors.Add(Err("/query/activityTypes", "activityTypesRollupConstraint",
                "query.activityTypes must equal exactly ['CopilotInteraction'] when processing.rollup is set. " +
                "PAX's rollup post-processor is CopilotInteraction-specific; other activity types would silently mislabel rolled-up rows. " +
                "Remove query.activityTypes to use the default, or change processing.rollup to omit the rollup post-processor.",
                new() { ["activityTypes"] = at, ["rollup"] = JsonModel.Str(proc!["rollup"]) }));
        }
    }

    // ---------------------------------------------------------------------
    // Gate 12: m365Usage gate
    // ---------------------------------------------------------------------
    private static void M365UsageGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        Dictionary<string, object?>? m = Child(Child(recipe, "ingredients"), "m365Usage");
        if (m is null || !m.ContainsKey("includeCopilotInteraction")) { return; }
        bool excludeCopilot = !JsonModel.Bool(m["includeCopilotInteraction"]);
        if (!excludeCopilot) { return; }
        bool includeM365 = m.ContainsKey("includeM365Usage") && JsonModel.Bool(m["includeM365Usage"]);
        if (!includeM365)
        {
            errors.Add(Err("/ingredients/m365Usage/includeCopilotInteraction", "excludeCopilotInteractionRequiresM365Usage",
                "ingredients.m365Usage.includeCopilotInteraction=false is only valid when ingredients.m365Usage.includeM365Usage=true " +
                "(excluding CopilotInteraction without the M365 usage bundle would leave the recipe with no audit data to fetch). " +
                "Either set includeM365Usage=true, or set includeCopilotInteraction=true (or remove the field).",
                new() { ["includeCopilotInteraction"] = false, ["includeM365Usage"] = includeM365 }));
        }
    }

    // ---------------------------------------------------------------------
    // Gate 13: userInfo channel gate (audit shape)
    // ---------------------------------------------------------------------
    private static void UserInfoChannelGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        string queryMode = string.Empty;
        Dictionary<string, object?>? q = Child(recipe, "query");
        if (q is not null && q.ContainsKey("mode")) { queryMode = JsonModel.Str(q["mode"]); }
        if (Ci(queryMode, "userInfoOnly")) { return; }

        bool hasUserInfo = false;
        Dictionary<string, object?>? dest = Child(recipe, "destinations");
        if (dest is not null && dest.ContainsKey("userInfo")) { hasUserInfo = true; }
        if (!hasUserInfo) { return; }

        bool includeUI = false;
        Dictionary<string, object?>? eud = Child(Child(recipe, "ingredients"), "entraUserData");
        if (eud is not null && eud.ContainsKey("includeUserInfo")) { includeUI = JsonModel.Bool(eud["includeUserInfo"]); }
        if (!includeUI)
        {
            errors.Add(Err("/destinations/userInfo", "userInfoChannelRequiresIncludeUserInfo",
                "destinations.userInfo is only valid under audit shape when ingredients.entraUserData.includeUserInfo=true " +
                "(no user-info data would be produced otherwise). Either set includeUserInfo=true, " +
                "remove destinations.userInfo, or change query.mode to 'userInfoOnly' for a Shape 3 user-info-only run.",
                new() { ["queryMode"] = queryMode, ["includeUserInfo"] = includeUI }));
        }
    }

    // ---------------------------------------------------------------------
    // Gate 14: agentFilter shape
    // ---------------------------------------------------------------------
    private static void AgentFilterShapeGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        Dictionary<string, object?>? af = Child(Child(recipe, "query"), "agentFilter");
        if (af is null) { return; }
        string mode = af.ContainsKey("mode") ? JsonModel.Str(af["mode"]) : string.Empty;
        bool hasIds = af.ContainsKey("agentIds");

        if (Ci(mode, "none"))
        {
            if (hasIds)
            {
                errors.Add(Err("/query/agentFilter/agentIds", "agentFilterAgentIdsForbiddenUnderNone",
                    "query.agentFilter.agentIds must be absent when query.agentFilter.mode='none' (no agent filtering is being applied, so the ids would be inert). Remove agentIds, or change mode to 'agentIds'.",
                    new() { ["agentFilterMode"] = mode }));
            }
        }
        else if (Ci(mode, "agentIds"))
        {
            if (!hasIds)
            {
                errors.Add(Err("/query/agentFilter/agentIds", "agentFilterAgentIdsRequiredUnderAgentIds",
                    "query.agentFilter.agentIds is required when query.agentFilter.mode='agentIds' (the id list IS the filter and is projected as -AgentId <values>). Add a non-empty agentIds array, or change mode to 'none', 'agentsOnly', or 'excludeAgents'.",
                    new() { ["agentFilterMode"] = mode }));
            }
        }
        else if (Ci(mode, "agentsOnly"))
        {
            if (hasIds)
            {
                errors.Add(Err("/query/agentFilter/agentIds", "agentFilterAgentIdsForbiddenUnderAgentsOnly",
                    "query.agentFilter.agentIds must be absent when query.agentFilter.mode='agentsOnly' (PAX -AgentsOnly is a parameterless switch). Remove agentIds, or change mode to 'agentIds' to filter to a specific list.",
                    new() { ["agentFilterMode"] = mode }));
            }
        }
        else if (Ci(mode, "excludeAgents"))
        {
            if (hasIds)
            {
                errors.Add(Err("/query/agentFilter/agentIds", "agentFilterAgentIdsForbiddenUnderExcludeAgents",
                    "query.agentFilter.agentIds must be absent when query.agentFilter.mode='excludeAgents' (PAX -ExcludeAgents is a parameterless switch). Remove agentIds, or change mode to 'agentIds' to filter to a specific list.",
                    new() { ["agentFilterMode"] = mode }));
            }
        }
    }

    // ---------------------------------------------------------------------
    // Gate 15: unsupported switches trailer
    // ---------------------------------------------------------------------
    private static readonly (string Switch, string Hint)[] UnsupportedSwitches =
    {
        ("RecordTypes", "RecordTypes is not in the Cookbook supported surface; use query.activityTypes for activity scoping."),
        ("ServiceTypes", "ServiceTypes is not in the Cookbook supported surface; use ingredients.m365Usage.includeM365Usage and query.activityTypes for service/activity scoping."),
        ("UseEOM", "UseEOM (Exchange Online Management mode) is incompatible with Cookbook's partitioned runtime contract and is not in the supported surface."),
    };

    private static void UnsupportedSwitchesGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        string extra = ExtraArguments(recipe);
        if (string.IsNullOrWhiteSpace(extra)) { return; }
        foreach (var (sw, hint) in UnsupportedSwitches)
        {
            string pattern = @"(^|\s)-" + Regex.Escape(sw) + @"($|\s|=)";
            if (Regex.IsMatch(extra, pattern, RegexOptions.IgnoreCase))
            {
                errors.Add(Err("/advanced/extraArguments", "unsupportedSwitch",
                    $"advanced.extraArguments contains unsupported switch '-{sw}'. {hint}",
                    new() { ["switch"] = "-" + sw }));
            }
        }
    }

    // ---------------------------------------------------------------------
    // Gate 16: structurally-owned switches trailer
    // ---------------------------------------------------------------------
    private static readonly string[] StructurallyOwnedSwitches =
    {
        "OutputPath", "AppendFile", "OutputPathUserInfo", "AppendUserInfo",
        "IncludeUserInfo", "OnlyUserInfo", "IncludeM365Usage", "ExcludeCopilotInteraction",
        "ActivityTypes", "UserIds", "GroupNames", "AgentId", "AgentsOnly", "ExcludeAgents",
        "PromptFilter", "ClientCertificatePath", "StartDate", "EndDate", "Rollup", "RollupPlusRaw",
    };

    private static void StructurallyOwnedGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        string extra = ExtraArguments(recipe);
        if (string.IsNullOrWhiteSpace(extra)) { return; }
        foreach (string name in StructurallyOwnedSwitches)
        {
            string pattern = @"(^|\s)-" + Regex.Escape(name) + @"($|\s|=)";
            if (Regex.IsMatch(extra, pattern, RegexOptions.IgnoreCase))
            {
                errors.Add(Err("/advanced/extraArguments", "structurallyOwnedSwitch",
                    $"advanced.extraArguments contains structurally-owned switch '-{name}'. " +
                    "This switch is owned by the structured recipe surface; supply it via the corresponding recipe field instead so the adapter projects exactly once and validation can verify the value.",
                    new() { ["switch"] = "-" + name }));
            }
        }
    }

    // ---------------------------------------------------------------------
    // Gate 17: rollup blockers
    // ---------------------------------------------------------------------
    private static readonly (string Switch, string Keyword, string RuleId)[] RollupBlockerSwitches =
    {
        ("UseEOM", "rollupBlockedByUseEOM", "L2.ROLLUP.NO_USEEOM"),
        ("ExportWorkbook", "rollupBlockedByExportWorkbook", "L2.ROLLUP.NO_EXPORTWORKBOOK"),
        ("OnlyUserInfo", "rollupBlockedByOnlyUserInfo", "L2.ROLLUP.NO_ONLYUSERINFO"),
        ("OnlyAgent365Info", "rollupBlockedByOnlyAgent365Info", "L2.ROLLUP.NO_ONLYAGENT365INFO"),
        ("RAWInputCSV", "rollupBlockedByRawInputCsv", "L2.ROLLUP.NO_RAWINPUTCSV"),
    };

    private static bool HasSwitch(string trailer, string name)
    {
        if (string.IsNullOrWhiteSpace(trailer)) { return false; }
        string pattern = @"(^|\s)-" + Regex.Escape(name) + @"($|\s|=)";
        return Regex.IsMatch(trailer, pattern, RegexOptions.IgnoreCase);
    }

    private static void RollupBlockersGate(Dictionary<string, object?> recipe, List<object> errors)
    {
        bool isRollup = false;
        Dictionary<string, object?>? proc = Child(recipe, "processing");
        if (proc is not null && proc.ContainsKey("rollup"))
        {
            string rv = JsonModel.Str(proc["rollup"]);
            if (Ci(rv, "Rollup") || Ci(rv, "RollupPlusRaw")) { isRollup = true; }
        }
        if (!isRollup) { return; }

        string extra = ExtraArguments(recipe);

        bool includeM365 = false;
        Dictionary<string, object?>? m365 = Child(Child(recipe, "ingredients"), "m365Usage");
        if (m365 is not null && m365.ContainsKey("includeM365Usage")) { includeM365 = JsonModel.Bool(m365["includeM365Usage"]); }

        foreach (var (sw, keyword, ruleId) in RollupBlockerSwitches)
        {
            if (HasSwitch(extra, sw))
            {
                errors.Add(Err("/advanced/extraArguments", keyword,
                    $"rollup runs cannot include -{sw} (mirrors the bundled PAX engine's rollup-blocker gate; rule {ruleId})",
                    new() { ["ruleId"] = ruleId, ["switch"] = "-" + sw }));
            }
        }

        if (HasSwitch(extra, "ExcludeCopilotInteraction") && !includeM365)
        {
            errors.Add(Err("/ingredients/m365Usage/includeM365Usage", "rollupExcludeCopilotRequiresM365Usage",
                "rollup runs that pass -ExcludeCopilotInteraction in advanced.extraArguments must also set ingredients.m365Usage.includeM365Usage = true (mirrors the bundled PAX engine's rollup-blocker gate; rule L2.ROLLUP.EXCLUDE_COPILOT_REQUIRES_M365_USAGE)",
                new() { ["ruleId"] = "L2.ROLLUP.EXCLUDE_COPILOT_REQUIRES_M365_USAGE", ["switch"] = "-ExcludeCopilotInteraction", ["includeM365Usage"] = includeM365 }));
        }
    }

    // ---------------------------------------------------------------------
    // Materialize body schema (X10). Oracle:
    // $Script:TemplateMaterializeBodySchema in app\broker\Routes\Templates.ps1.
    // Validated with the same AJV-shaped walker as the recipe schema so the
    // emitted errors match the oracle's materialize_body_invalid payload.
    // ---------------------------------------------------------------------
    private static readonly SchemaNode MaterializeBodySchema = BuildMaterializeBodySchema();

    private static SchemaNode BuildMaterializeBodySchema()
    {
        var guidPattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";

        var identity = new SchemaNode
        {
            Type = "object",
            AdditionalPropertiesFalse = true,
            Required = new[] { "name" },
            Properties = new Dictionary<string, SchemaNode>(StringComparer.Ordinal)
            {
                ["name"] = new SchemaNode { Type = "string", MinLength = 1, MaxLength = 200 },
            },
        };

        var auth = new SchemaNode
        {
            Type = "object",
            AdditionalPropertiesFalse = true,
            Required = new[] { "tenantId" },
            Properties = new Dictionary<string, SchemaNode>(StringComparer.Ordinal)
            {
                ["tenantId"] = new SchemaNode { Type = "string", Pattern = guidPattern },
            },
        };

        var query = new SchemaNode
        {
            Type = "object",
            AdditionalPropertiesFalse = true,
            Required = new[] { "startDate", "endDate" },
            Properties = new Dictionary<string, SchemaNode>(StringComparer.Ordinal)
            {
                ["startDate"] = new SchemaNode { Type = "string", Format = "date" },
                ["endDate"] = new SchemaNode { Type = "string", Format = "date" },
            },
        };

        var fact = new SchemaNode
        {
            Type = "object",
            AdditionalPropertiesFalse = true,
            Required = new[] { "path" },
            Properties = new Dictionary<string, SchemaNode>(StringComparer.Ordinal)
            {
                ["path"] = new SchemaNode { Type = "string", MinLength = 1 },
            },
        };

        var destinations = new SchemaNode
        {
            Type = "object",
            AdditionalPropertiesFalse = true,
            Required = new[] { "fact" },
            Properties = new Dictionary<string, SchemaNode>(StringComparer.Ordinal)
            {
                ["fact"] = fact,
            },
        };

        return new SchemaNode
        {
            Type = "object",
            AdditionalPropertiesFalse = true,
            Required = new[] { "identity", "auth", "query", "destinations" },
            Properties = new Dictionary<string, SchemaNode>(StringComparer.Ordinal)
            {
                ["identity"] = identity,
                ["auth"] = auth,
                ["query"] = query,
                ["destinations"] = destinations,
            },
        };
    }

    // X10: validate a template-materialize request body against the oracle's
    // dedicated body schema. Returns AJV-shaped errors (empty when valid).
    public static List<object> ValidateMaterializeBody(object? body)
    {
        var errors = new List<object>();
        WalkNode(body, MaterializeBodySchema, string.Empty, errors);
        return errors;
    }

    // ---------------------------------------------------------------------
    // Test-RecipeAll equivalent
    // ---------------------------------------------------------------------
    public static (bool Ok, List<object> Errors) ValidateAll(object? recipe)
    {
        // Back-compat tolerance (CK-2 / B2): an imported or legacy recipe may
        // still carry the deprecated auth.authProfileId reference, which chefKeyId
        // (CK-1) supersedes. Strip the stray field before the closed schema runs
        // so the recipe loads/saves cleanly rather than 400-ing on an unknown
        // property.
        if (recipe is Dictionary<string, object?> pre)
        {
            StripDeprecatedAuthFields(pre);
        }

        var errors = new List<object>();
        SchemaErrors(recipe, errors);

        if (Dict(recipe) is Dictionary<string, object?> r)
        {
            TierGate(r, errors);
            DateRangeGate(r, errors);
            RemovedSwitchGate(r, errors);
            AppRegistrationTenantGate(r, errors);
            ExecutionModeAuthMatrixGate(r, errors);
            ScheduleShapeGate(r, errors);
            SecretShapeGate(r, errors);
            FactOutputModeGate(r, errors);
            UserInfoOutputModeGate(r, errors);
            QueryShapeGate(r, errors);
            ActivityTypesUnderRollupGate(r, errors);
            M365UsageGate(r, errors);
            UserInfoChannelGate(r, errors);
            AgentFilterShapeGate(r, errors);
            UnsupportedSwitchesGate(r, errors);
            StructurallyOwnedGate(r, errors);
            RollupBlockersGate(r, errors);
        }

        return (errors.Count == 0, errors);
    }
}
