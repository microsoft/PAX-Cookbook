using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B2 -- materialize-body validator.
//
// Ports the $Script:TemplateMaterializeBodySchema check
// (app\broker\Routes\Templates.ps1 ~line 54) using a hand-coded walker
// rather than re-exposing RecipeValidator's WalkSchema internals. The
// schema is small and fixed (4 required object leaves) so the
// hand-coded form keeps Stage 3i-B2 changes scoped to new code and
// avoids touching RecipeValidator's surface.
//
// Error envelope matches AJV / RecipeValidator's ValidationError shape:
//   { instancePath, keyword, message, params }
//
// Coverage:
//   * type (object/string)
//   * additionalProperties=false at every level
//   * required (with AJV-compatible 'must have required property X'
//     message + missingProperty param)
//   * minLength / maxLength (string)
//   * pattern (string, with the surrounding double-quote rendering AJV
//     uses)
//   * format=date (string YYYY-MM-DD, plus actual parseability)
public static class TemplateMaterializeBodyValidator
{
    private const string GuidPattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";

    public static IReadOnlyList<ValidationError> Validate(JsonObject body)
    {
        var errors = new List<ValidationError>();
        WalkRoot(body, errors);
        return errors;
    }

    private static void WalkRoot(JsonObject body, List<ValidationError> errors)
    {
        RequireProperty(body, "", "identity",     errors);
        RequireProperty(body, "", "auth",         errors);
        RequireProperty(body, "", "query",        errors);
        RequireProperty(body, "", "destinations", errors);

        ForbidExtras(body, "", new[] { "identity", "auth", "query", "destinations" }, errors);

        if (body.TryGetPropertyValue("identity", out var idNode) && idNode is JsonObject id)
            WalkIdentity(id, errors);
        else if (idNode is not null && idNode is not JsonObject)
            errors.Add(TypeMismatch("/identity", "object"));

        if (body.TryGetPropertyValue("auth", out var authNode) && authNode is JsonObject auth)
            WalkAuth(auth, errors);
        else if (authNode is not null && authNode is not JsonObject)
            errors.Add(TypeMismatch("/auth", "object"));

        if (body.TryGetPropertyValue("query", out var qNode) && qNode is JsonObject query)
            WalkQuery(query, errors);
        else if (qNode is not null && qNode is not JsonObject)
            errors.Add(TypeMismatch("/query", "object"));

        if (body.TryGetPropertyValue("destinations", out var dNode) && dNode is JsonObject dest)
            WalkDestinations(dest, errors);
        else if (dNode is not null && dNode is not JsonObject)
            errors.Add(TypeMismatch("/destinations", "object"));
    }

    private static void WalkIdentity(JsonObject id, List<ValidationError> errors)
    {
        RequireProperty(id, "/identity", "name", errors);
        ForbidExtras(id, "/identity", new[] { "name" }, errors);
        if (id.TryGetPropertyValue("name", out var n))
        {
            CheckString(n, "/identity/name", minLength: 1, maxLength: 200, pattern: null, format: null, errors);
        }
    }

    private static void WalkAuth(JsonObject auth, List<ValidationError> errors)
    {
        RequireProperty(auth, "/auth", "tenantId", errors);
        ForbidExtras(auth, "/auth", new[] { "tenantId" }, errors);
        if (auth.TryGetPropertyValue("tenantId", out var t))
        {
            CheckString(t, "/auth/tenantId", minLength: null, maxLength: null, pattern: GuidPattern, format: null, errors);
        }
    }

    private static void WalkQuery(JsonObject q, List<ValidationError> errors)
    {
        RequireProperty(q, "/query", "startDate", errors);
        RequireProperty(q, "/query", "endDate",   errors);
        ForbidExtras(q, "/query", new[] { "startDate", "endDate" }, errors);
        if (q.TryGetPropertyValue("startDate", out var s))
            CheckString(s, "/query/startDate", minLength: null, maxLength: null, pattern: null, format: "date", errors);
        if (q.TryGetPropertyValue("endDate", out var e))
            CheckString(e, "/query/endDate", minLength: null, maxLength: null, pattern: null, format: "date", errors);
    }

    private static void WalkDestinations(JsonObject dest, List<ValidationError> errors)
    {
        RequireProperty(dest, "/destinations", "fact", errors);
        ForbidExtras(dest, "/destinations", new[] { "fact" }, errors);
        if (dest.TryGetPropertyValue("fact", out var factNode) && factNode is JsonObject fact)
        {
            RequireProperty(fact, "/destinations/fact", "path", errors);
            ForbidExtras(fact, "/destinations/fact", new[] { "path" }, errors);
            if (fact.TryGetPropertyValue("path", out var p))
                CheckString(p, "/destinations/fact/path", minLength: 1, maxLength: null, pattern: null, format: null, errors);
        }
        else if (factNode is not null && factNode is not JsonObject)
        {
            errors.Add(TypeMismatch("/destinations/fact", "object"));
        }
    }

    // ---------- helpers ----------

    private static void RequireProperty(JsonObject obj, string parentPath, string name, List<ValidationError> errors)
    {
        if (!obj.ContainsKey(name))
        {
            errors.Add(new ValidationError(
                InstancePath: parentPath,
                Keyword:      "required",
                Message:      "must have required property '" + name + "'",
                Params:       new Dictionary<string, object?> { ["missingProperty"] = name }));
        }
    }

    private static void ForbidExtras(JsonObject obj, string parentPath, string[] allowed, List<ValidationError> errors)
    {
        foreach (var kvp in obj)
        {
            if (Array.IndexOf(allowed, kvp.Key) < 0)
            {
                errors.Add(new ValidationError(
                    InstancePath: parentPath,
                    Keyword:      "additionalProperties",
                    Message:      "must NOT have additional properties",
                    Params:       new Dictionary<string, object?> { ["additionalProperty"] = kvp.Key }));
            }
        }
    }

    private static ValidationError TypeMismatch(string instancePath, string expected) =>
        new(InstancePath: instancePath,
            Keyword:      "type",
            Message:      "must be " + expected,
            Params:       new Dictionary<string, object?> { ["type"] = expected });

    private static void CheckString(JsonNode? node, string instancePath,
        int? minLength, int? maxLength, string? pattern, string? format,
        List<ValidationError> errors)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s))
        {
            errors.Add(TypeMismatch(instancePath, "string"));
            return;
        }
        if (minLength is int min && s.Length < min)
        {
            errors.Add(new ValidationError(instancePath, "minLength",
                "must NOT have fewer than " + min + " characters",
                new Dictionary<string, object?> { ["limit"] = min }));
        }
        if (maxLength is int max && s.Length > max)
        {
            errors.Add(new ValidationError(instancePath, "maxLength",
                "must NOT have more than " + max + " characters",
                new Dictionary<string, object?> { ["limit"] = max }));
        }
        if (pattern is not null && !Regex.IsMatch(s, pattern))
        {
            errors.Add(new ValidationError(instancePath, "pattern",
                "must match pattern \"" + pattern + "\"",
                new Dictionary<string, object?> { ["pattern"] = pattern }));
        }
        if (format == "date")
        {
            if (!Regex.IsMatch(s, "^\\d{4}-\\d{2}-\\d{2}$") ||
                !DateTime.TryParseExact(s, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                errors.Add(new ValidationError(instancePath, "format",
                    "must match format \"date\"",
                    new Dictionary<string, object?> { ["format"] = "date" }));
            }
        }
    }
}
