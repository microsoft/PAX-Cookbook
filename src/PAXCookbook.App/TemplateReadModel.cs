using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PAXCookbook.App;

// Read-only native template read model (X4). Mirrors the read paths of the
// PowerShell oracle (app\broker\Routes\Templates.ps1) without porting any
// mutable behavior (no materialize, no install, no upload, no remote catalog):
//   - GET /api/v1/templates      -> bundled template summaries (sorted by id)
//   - GET /api/v1/templates/{id} -> full template body
//
// The catalog is built ONCE from the bundled static files under
// <appRoot>\templates\*.template.json (oracle: Read-TemplateCatalog). There is
// no per-request rescan. A missing templates directory yields an empty catalog
// (not an error), exactly like the oracle. Validation is deliberately minimal
// and read-only: parse the JSON, require an object root with a templateId that
// matches the id pattern, and require the filename to equal
// "<templateId>.template.json"; files that fail are skipped (not inserted).
internal sealed partial class TemplateReadModel
{
    // Oracle parity: $Script:TemplateIdPattern (lower-case, case-sensitive).
    [GeneratedRegex("^[a-z][a-z0-9-]{1,62}[a-z0-9]$")]
    private static partial Regex TemplateIdPattern();

    public static bool IsValidTemplateId(string id) => TemplateIdPattern().IsMatch(id);

    // Ascending id order so the Pantry list view is stable (oracle:
    // $TemplateCatalog.Keys | Sort-Object).
    private readonly SortedDictionary<string, JsonElement> _catalog;

    private TemplateReadModel(SortedDictionary<string, JsonElement> catalog)
    {
        _catalog = catalog;
    }

    public int Count => _catalog.Count;

    // Startup-only catalog load. Never throws on a bad file; bad files are
    // skipped so one malformed template cannot poison the operator catalog.
    public static TemplateReadModel Load(string appRoot)
    {
        var catalog = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        string templatesDir = Path.Combine(appRoot, "templates");

        if (!Directory.Exists(templatesDir))
        {
            // Missing templates directory -> empty catalog (not an error).
            return new TemplateReadModel(catalog);
        }

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (string file in Directory.EnumerateFiles(templatesDir, "*.template.json"))
        {
            JsonElement body;
            try
            {
                string raw = File.ReadAllText(file, utf8NoBom);
                using var doc = JsonDocument.Parse(raw);
                body = doc.RootElement.Clone();
            }
            catch
            {
                continue; // unparseable file: skip
            }

            if (body.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!body.TryGetProperty("templateId", out JsonElement idEl) ||
                idEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string id = idEl.GetString() ?? string.Empty;
            if (!IsValidTemplateId(id))
            {
                continue;
            }

            // Content check (oracle: filename MUST equal "<templateId>.template.json").
            string expectedName = id + ".template.json";
            if (!string.Equals(Path.GetFileName(file), expectedName, StringComparison.Ordinal))
            {
                continue;
            }

            // Dedupe: first valid file wins; a duplicate id is skipped.
            if (catalog.ContainsKey(id))
            {
                continue;
            }

            catalog[id] = body;
        }

        return new TemplateReadModel(catalog);
    }

    // GET /api/v1/templates — lightweight summaries. Oracle: Invoke-TemplatesList
    // + ConvertTo-TemplateSummary (9 projected fields).
    public object BuildListPayload()
    {
        var templates = new List<object>();
        foreach (KeyValuePair<string, JsonElement> kv in _catalog)
        {
            templates.Add(BuildSummary(kv.Value));
        }
        return new { templates };
    }

    // GET /api/v1/templates/{id} — full body. Oracle: Invoke-TemplateGet.
    public (int Status, object Body) GetDetail(string templateId)
    {
        if (!_catalog.TryGetValue(templateId, out JsonElement body))
        {
            return (404, new { error = "template_not_found", templateId });
        }
        return (200, new { template = body });
    }

    // Catalog lookup for the materialize path (X10). Returns the full bundled
    // template body so the materializer can read recipeDefaults, the version
    // metadata, and the compatibility floor. Oracle:
    // $Script:TemplateCatalog.ContainsKey($TemplateId) / $Script:TemplateCatalog[$TemplateId].
    public bool TryGetTemplate(string templateId, out JsonElement template) =>
        _catalog.TryGetValue(templateId, out template);

    // Oracle parity: ConvertTo-TemplateSummary.
    private static object BuildSummary(JsonElement t)
    {
        int guidanceCount = 0;
        if (t.TryGetProperty("manualGuidance", out JsonElement guidance) &&
            guidance.ValueKind == JsonValueKind.Array)
        {
            guidanceCount = guidance.GetArrayLength();
        }

        return new
        {
            templateId = GetStr(t, "templateId"),
            templateVersion = GetStr(t, "templateVersion"),
            templateSchemaVersion = GetInt(t, "templateSchemaVersion"),
            displayName = GetStr(t, "displayName"),
            shortDescription = GetStr(t, "shortDescription"),
            category = GetStr(t, "category"),
            minPaxScriptVersion = GetStr(t, "minPaxScriptVersion"),
            minCookbookVersion = GetStr(t, "minCookbookVersion"),
            manualGuidanceCount = guidanceCount,
        };
    }

    private static string GetStr(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.Number &&
        el.TryGetInt32(out int n)
            ? n
            : 0;
}
