using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B3 -- port of RecipeTakeoutSanitizer.psm1.
//
// Pure helper: no I/O, no broker routes, no PAX invocation. Takes a
// source recipe JsonObject + provenance fields and returns a sanitized
// envelope JsonObject ready to be JSON-serialized to the SPA.
//
// Authority: app/broker/Modules/RecipeTakeoutSanitizer.psm1 (548 lines)
// plus the Decision Lock K-1..K-15 referenced therein. The C# port
// keeps the public method names + field names so the wire shape stays
// byte-identical to the PowerShell broker.
public sealed class RecipeTakeoutSanitizer
{
    public const int    TakeoutSchemaVersion       = 1;
    public const string TakeoutKindConstant        = "pax-cookbook.recipe-takeout";
    public const int    TakeoutStaleDateRangeDays  = 90;

    public static readonly IReadOnlyList<string> AppRegistrationModes = new[]
    {
        "AppRegistrationSecret",
        "AppRegistrationCertificate",
    };

    public static readonly IReadOnlyList<string> TakeoutExcludedCategories = new[]
    {
        "chef_key_secrets",
        "chef_key_binding_authProfileId",
        "credential_manager_target_names",
        "access_tokens",
        "tenant_audit_output",
        "bake_records",
        "cooks_folder_contents",
        "cookbook_sqlite",
        "logs",
        "runtime_lock_state",
        "update_trust_files",
        "source_recipeId_as_active_id",
    };

    // Forbidden secret-property names. Compared case-insensitively.
    public static readonly IReadOnlyList<string> TakeoutForbiddenSecretFields = new[]
    {
        "clientSecret", "secret", "password", "passphrase",
        "accessToken", "refreshToken", "bearerToken", "idToken",
        "apiKey", "api_key",
        "connectionString",
        "credentialTargetName",
        "certificateBase64", "certificatePfx", "privateKey",
    };

    // Forbidden artifact-property names. Compared case-insensitively.
    public static readonly IReadOnlyList<string> TakeoutForbiddenArtifactFields = new[]
    {
        "cookContext", "cookLog",
        "bakeOutputs", "bakeResults",
        "databaseRows", "tenantAuditData",
        "windowsCredentialManager",
        "logs", "sqliteDump", "cookbookSqlite", "runtimeLockState",
    };

    private static readonly Regex JwtRegex            = new(@"\beyJ[0-9A-Za-z_-]{8,}\.[0-9A-Za-z_-]{8,}\.[0-9A-Za-z_-]{8,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PemPrivateKeyRegex  = new(@"-----BEGIN[A-Z ]*PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AwsAccessKeyRegex   = new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled);

    // K-9: 8 lowercase hex chars of SHA-256(installRootPath).
    public static string? WorkspaceFingerprint(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return null;
        var bytes = Encoding.UTF8.GetBytes(workspacePath);
        var hash  = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    // K-2: sanitize a Chef's Key display label. Trim, strip control
    // characters, cap at 200 chars. Returns null when empty.
    public static string? SanitizeChefKeyLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var sb = new StringBuilder();
        foreach (var ch in label)
        {
            if (!char.IsControl(ch)) sb.Append(ch);
        }
        var clean = sb.ToString().Trim();
        if (clean.Length == 0) return null;
        if (clean.Length > 200) clean = clean[..200];
        return clean;
    }

    // Defense-in-depth: walk the tree, return the first offending
    // property name in the forbidden list (case-insensitive). Returns
    // null when clean.
    public static string? FindForbiddenFieldName(JsonNode? node, IEnumerable<string> forbidden)
    {
        if (node is null) return null;
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                foreach (var f in forbidden)
                {
                    if (string.Equals(kv.Key, f, StringComparison.OrdinalIgnoreCase))
                        return kv.Key;
                }
                var hit = FindForbiddenFieldName(kv.Value, forbidden);
                if (hit is not null) return hit;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var hit = FindForbiddenFieldName(item, forbidden);
                if (hit is not null) return hit;
            }
        }
        return null;
    }

    // Companion: scan all string values for known secret patterns.
    public static string? FindForbiddenSecretValue(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && s is not null)
        {
            if (JwtRegex.IsMatch(s))           return "jwt";
            if (PemPrivateKeyRegex.IsMatch(s)) return "pem_private_key";
            if (AwsAccessKeyRegex.IsMatch(s))  return "aws_access_key_id";
            return null;
        }
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                var hit = FindForbiddenSecretValue(kv.Value);
                if (hit is not null) return hit;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var hit = FindForbiddenSecretValue(item);
                if (hit is not null) return hit;
            }
        }
        return null;
    }

    // ----------------------------------------------------------------
    //  Envelope builder
    // ----------------------------------------------------------------

    public JsonObject BuildEnvelope(
        JsonObject       sourceRecipe,
        DateTimeOffset   exportedAtUtc,
        string?          cookbookVersion,
        string?          bundledPaxVersion,
        string?          releaseChannel,
        string?          workspaceInstallPath,
        string?          chefKeySourceLabel)
    {
        // 1. Defensive deep clone so the caller's object is not touched.
        var copy = (JsonObject)sourceRecipe.DeepClone();

        // 2. Capture source metadata BEFORE we mutate the copy.
        string? sourceId        = TryGetString(copy, "recipeId");
        string? sourceName      = TryGetString(copy, "identity", "name");
        string? sourceCreatedAt = TryGetString(copy, "createdAt");
        string? sourceUpdatedAt = TryGetString(copy, "updatedAt");
        JsonObject? sourceTemplate = null;
        if (copy["createdBy"] is JsonObject cb && cb["fromTemplate"] is JsonObject ft)
            sourceTemplate = (JsonObject)ft.DeepClone();

        // 3. Build sanitized recipe payload (allow-list, not strip-list).
        var payload = new JsonObject();
        string[] authoredKeys =
        {
            "recipeSchemaVersion","paxAdapterVersion","identity","ingredients",
            "query","processing","destinations","auth","advanced","executionMode",
            "createdBy",
        };
        foreach (var k in authoredKeys)
        {
            if (copy.TryGetPropertyValue(k, out var val) && val is not null)
                payload[k] = val.DeepClone();
        }

        // 4. Strip Chef's Key binding from the exported recipe payload.
        if (payload["auth"] is JsonObject authBlock && authBlock.ContainsKey("authProfileId"))
            authBlock.Remove("authProfileId");

        // 5. Build chefKey requirement block.
        string? authMode = null;
        if (payload["auth"] is JsonObject ab2 && ab2["mode"] is JsonValue mv && mv.TryGetValue<string>(out var mode))
            authMode = mode;
        var chefKey = new JsonObject();
        if (authMode is not null && AppRegistrationModes.Contains(authMode))
        {
            chefKey["requirement"] = "required";
            chefKey["mode"]        = authMode;
        }
        else
        {
            chefKey["requirement"] = "none";
        }
        var cleanLabel = SanitizeChefKeyLabel(chefKeySourceLabel);
        if (!string.IsNullOrEmpty(cleanLabel))
            chefKey["sourceDisplayLabel"] = cleanLabel;

        // 6. sourceRecipe metadata.
        var sourceRecipeBlock = new JsonObject();
        if (sourceId        is not null) sourceRecipeBlock["id"]        = sourceId;
        if (sourceName      is not null) sourceRecipeBlock["name"]      = sourceName;
        if (sourceCreatedAt is not null) sourceRecipeBlock["createdAt"] = sourceCreatedAt;
        if (sourceUpdatedAt is not null) sourceRecipeBlock["updatedAt"] = sourceUpdatedAt;
        if (sourceTemplate  is not null) sourceRecipeBlock["sourceTemplate"] = sourceTemplate;

        // 7. exportedBy block.
        var exportedBy = new JsonObject();
        if (!string.IsNullOrWhiteSpace(cookbookVersion))   exportedBy["cookbookVersion"]   = cookbookVersion;
        if (!string.IsNullOrWhiteSpace(bundledPaxVersion)) exportedBy["bundledPaxVersion"] = bundledPaxVersion;
        if (!string.IsNullOrWhiteSpace(releaseChannel))    exportedBy["releaseChannel"]    = releaseChannel;
        if (!string.IsNullOrWhiteSpace(workspaceInstallPath))
        {
            var fp = WorkspaceFingerprint(workspaceInstallPath);
            if (fp is not null) exportedBy["workspaceFingerprint"] = fp;
        }

        // 8. Build warnings.
        var warnings = BuildWarnings(payload, exportedAtUtc);

        // 9. Compose envelope. Order matches PS Get-RecipeTakeoutEnvelope.
        var envelope = new JsonObject
        {
            ["takeoutSchemaVersion"] = TakeoutSchemaVersion,
            ["kind"]                 = TakeoutKindConstant,
            ["exportedAtUtc"]        = exportedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
        };
        if (exportedBy.Count > 0) envelope["exportedBy"] = exportedBy;
        envelope["recipe"]   = payload;
        envelope["chefKey"]  = chefKey;
        if (sourceRecipeBlock.Count > 0) envelope["sourceRecipe"] = sourceRecipeBlock;
        envelope["warnings"] = warnings;
        var excludedArr = new JsonArray();
        foreach (var x in TakeoutExcludedCategories) excludedArr.Add(x);
        envelope["excluded"] = excludedArr;

        // 10. Defense-in-depth scans. Either of these tripping means
        //     the source recipe shape has drifted (the input recipe
        //     already failed save-time validation). Throw so the
        //     route emits 500 takeout_sanitization_failed.
        var offendingSecret = FindForbiddenFieldName(envelope, TakeoutForbiddenSecretFields);
        if (offendingSecret is not null)
            throw new InvalidOperationException("forbidden secret field name found at any depth: '" + offendingSecret + "'");
        var offendingArtifact = FindForbiddenFieldName(envelope, TakeoutForbiddenArtifactFields);
        if (offendingArtifact is not null)
            throw new InvalidOperationException("forbidden artifact field name found at any depth: '" + offendingArtifact + "'");
        var obviousSecret = FindForbiddenSecretValue(envelope);
        if (obviousSecret is not null)
            throw new InvalidOperationException("obvious secret value pattern detected: '" + obviousSecret + "'");

        return envelope;
    }

    // ----------------------------------------------------------------
    //  Warning helpers (mirror Get-RecipeTakeoutWarnings in PS)
    // ----------------------------------------------------------------

    public static JsonArray BuildWarnings(JsonObject recipe, DateTimeOffset exportedAtUtc)
    {
        var warnings = new JsonArray();

        // Path warnings (one entry per matching dest path).
        if (recipe["destinations"] is JsonObject dests)
        {
            foreach (var groupKey in new[] { "fact", "userInfo" })
            {
                if (dests[groupKey] is not JsonObject d) continue;
                foreach (var pf in new[] { "path", "appendFile", "outputPath" })
                {
                    if (d[pf] is not JsonValue pv) continue;
                    if (!pv.TryGetValue<string>(out var v) || string.IsNullOrWhiteSpace(v)) continue;
                    var pointer = "/destinations/" + groupKey + "/" + pf;
                    if (IsUncPath(v))
                        warnings.Add(WarningNode("path_unc_review_recommended", pointer));
                    else if (IsLocalAbsolutePath(v))
                        warnings.Add(WarningNode("path_local_absolute_needs_review", pointer));
                    if (IsUserSpecificPath(v))
                        warnings.Add(WarningNode("path_user_specific_review_recommended", pointer));
                }
            }
        }

        if (HasNonEmptyString(recipe, "auth", "tenantId"))
            warnings.Add(WarningNode("tenant_id_present_review_recommended", "/auth/tenantId"));
        if (HasNonEmptyArrayOrString(recipe, "query", "userIds"))
            warnings.Add(WarningNode("user_filter_values_present_review_recommended", "/query/userIds"));
        if (HasNonEmptyArrayOrString(recipe, "query", "groupNames"))
            warnings.Add(WarningNode("group_filter_values_present_review_recommended", "/query/groupNames"));
        if (HasNonEmptyArrayOrString(recipe, "query", "agentFilter", "agentIds"))
            warnings.Add(WarningNode("agent_filter_values_present_review_recommended", "/query/agentFilter/agentIds"));
        if (HasNonEmptyString(recipe, "advanced", "extraArguments"))
            warnings.Add(WarningNode("extra_arguments_present_review_recommended", "/advanced/extraArguments"));
        if (IsDateRangeStale(recipe, exportedAtUtc))
            warnings.Add(WarningNode("date_range_may_be_stale", "/query/endDate"));

        if (recipe["auth"] is JsonObject ab && ab["mode"] is JsonValue mv2
            && mv2.TryGetValue<string>(out var authMode)
            && AppRegistrationModes.Contains(authMode))
        {
            warnings.Add(WarningNode("chef_key_required_select_local_binding", "/auth/authProfileId"));
        }

        return warnings;
    }

    public static bool IsLocalAbsolutePath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return false;
        if (Regex.IsMatch(p, @"^[A-Za-z]:[\\/]")) return true;
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        if (p.StartsWith("/",  StringComparison.Ordinal)) return true;
        return false;
    }

    public static bool IsUncPath(string p) =>
        !string.IsNullOrWhiteSpace(p) && p.StartsWith(@"\\", StringComparison.Ordinal);

    public static bool IsUserSpecificPath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return false;
        string[] tokens = { "%USERPROFILE%","%LOCALAPPDATA%","%APPDATA%","%HOMEDRIVE%","%HOMEPATH%","%TEMP%","%TMP%" };
        foreach (var t in tokens)
        {
            if (p.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        if (p.StartsWith("~", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsDateRangeStale(JsonObject recipe, DateTimeOffset exportedAtUtc)
    {
        if (recipe["query"] is not JsonObject q) return false;
        if (q["endDate"] is not JsonValue ev) return false;
        if (!ev.TryGetValue<string>(out var endStr) || string.IsNullOrWhiteSpace(endStr)) return false;
        if (!DateTime.TryParse(endStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var endDt))
            return false;
        var delta = exportedAtUtc.UtcDateTime - endDt;
        return delta.TotalDays > TakeoutStaleDateRangeDays;
    }

    private static bool HasNonEmptyString(JsonObject root, params string[] path)
    {
        JsonNode? cursor = root;
        foreach (var seg in path)
        {
            if (cursor is not JsonObject obj || !obj.TryGetPropertyValue(seg, out var next)) return false;
            cursor = next;
        }
        return cursor is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s);
    }

    private static bool HasNonEmptyArrayOrString(JsonObject root, params string[] path)
    {
        JsonNode? cursor = root;
        foreach (var seg in path)
        {
            if (cursor is not JsonObject obj || !obj.TryGetPropertyValue(seg, out var next)) return false;
            cursor = next;
        }
        if (cursor is JsonArray arr) return arr.Count > 0;
        if (cursor is JsonValue v && v.TryGetValue<string>(out var s)) return !string.IsNullOrWhiteSpace(s);
        return false;
    }

    private static JsonObject WarningNode(string code, string path) =>
        new() { ["code"] = code, ["path"] = path };

    private static string? TryGetString(JsonObject root, params string[] path)
    {
        JsonNode? cursor = root;
        foreach (var seg in path)
        {
            if (cursor is not JsonObject obj || !obj.TryGetPropertyValue(seg, out var next)) return null;
            cursor = next;
        }
        return cursor is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
