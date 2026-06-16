using System.Text.Json;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3c -- read-only catalog of bundled pantry templates from
// <appRoot>\templates\*.template.json. Mirrors the PowerShell broker's
// Read-TemplateCatalog (Start-Broker.ps1; called once at startup) and
// the per-route projections in Routes/Templates.ps1:
//
//   ConvertTo-TemplateSummary -> GET /api/v1/templates (list)
//   raw template document     -> GET /api/v1/templates/{id} (detail)
//
// Doctrine:
//   - Static file load only -- no SQLite, no per-request rescan.
//   - Templates with missing required fields are SKIPPED with a
//     LoadWarning recorded on the catalog; the broker never refuses
//     to start because of a malformed template file.
//   - Detail endpoint returns the parsed JsonElement verbatim, so
//     fields the summary projection doesn't surface (manualGuidance,
//     recipeDefaults, inputSchema, etc.) round-trip without
//     reshape.
public sealed class TemplateCatalogReader
{
    private readonly IReadOnlyDictionary<string, TemplateCatalogEntry> _entries;
    private readonly IReadOnlyList<string> _loadWarnings;

    private TemplateCatalogReader(
        IReadOnlyDictionary<string, TemplateCatalogEntry> entries,
        IReadOnlyList<string> loadWarnings)
    {
        _entries = entries;
        _loadWarnings = loadWarnings;
    }

    public IReadOnlyList<string> LoadWarnings => _loadWarnings;

    public IReadOnlyList<TemplateSummary> ListSummaries()
    {
        // Sorted by templateId for stable diffs (parity with
        // Routes/Templates.ps1: foreach id in keys | Sort-Object).
        return _entries.Values
            .Select(e => e.Summary)
            .OrderBy(s => s.TemplateId, StringComparer.Ordinal)
            .ToList();
    }

    public bool TryGetDocument(string templateId, out JsonElement document)
    {
        if (_entries.TryGetValue(templateId, out var entry))
        {
            document = entry.Document;
            return true;
        }
        document = default;
        return false;
    }

    // Empty catalog -- used when no templates directory is configured.
    public static TemplateCatalogReader Empty() =>
        new(new Dictionary<string, TemplateCatalogEntry>(StringComparer.Ordinal),
            Array.Empty<string>());

    public static TemplateCatalogReader Load(string templatesDir)
    {
        if (string.IsNullOrWhiteSpace(templatesDir) || !Directory.Exists(templatesDir))
        {
            return Empty();
        }

        var entries = new Dictionary<string, TemplateCatalogEntry>(StringComparer.Ordinal);
        var warnings = new List<string>();

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(templatesDir, "*.template.json",
                SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            warnings.Add("templates_enum_failed: " + ex.Message);
            return new TemplateCatalogReader(entries, warnings);
        }

        foreach (var path in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                warnings.Add(Path.GetFileName(path) + " read_failed: " + ex.Message);
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                warnings.Add(Path.GetFileName(path) + " parse_failed: " + ex.Message);
                continue;
            }

            // Clone the root element so it survives JsonDocument
            // disposal. The catalog holds the clones for the broker's
            // lifetime.
            JsonElement root = doc.RootElement.Clone();
            doc.Dispose();

            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings.Add(Path.GetFileName(path) + " not_object");
                continue;
            }

            var summary = TryProjectSummary(root, out var summaryError);
            if (summary is null)
            {
                warnings.Add(Path.GetFileName(path) + " summary_invalid: " + summaryError);
                continue;
            }

            if (entries.ContainsKey(summary.TemplateId))
            {
                warnings.Add(Path.GetFileName(path) + " duplicate_id: " + summary.TemplateId);
                continue;
            }

            entries[summary.TemplateId] = new TemplateCatalogEntry(summary, root);
        }

        return new TemplateCatalogReader(entries, warnings);
    }

    private static TemplateSummary? TryProjectSummary(JsonElement root, out string error)
    {
        // Required fields (per ConvertTo-TemplateSummary): templateId,
        // templateVersion, templateSchemaVersion, displayName,
        // shortDescription, category, minPaxScriptVersion,
        // minCookbookVersion. manualGuidanceCount is derived.
        string? GetStr(string name)
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
            return null;
        }

        int? GetInt(string name)
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var n))
            {
                return n;
            }
            return null;
        }

        var id = GetStr("templateId");
        if (string.IsNullOrWhiteSpace(id))
        {
            error = "missing_templateId";
            return null;
        }
        var ver = GetStr("templateVersion") ?? string.Empty;
        var schemaVer = GetInt("templateSchemaVersion") ?? 0;
        var displayName = GetStr("displayName") ?? string.Empty;
        var shortDesc   = GetStr("shortDescription") ?? string.Empty;
        var category    = GetStr("category") ?? string.Empty;
        var minPax      = GetStr("minPaxScriptVersion") ?? string.Empty;
        var minCookbook = GetStr("minCookbookVersion") ?? string.Empty;

        int guidanceCount = 0;
        if (root.TryGetProperty("manualGuidance", out var g)
            && g.ValueKind == JsonValueKind.Array)
        {
            guidanceCount = g.GetArrayLength();
        }

        error = string.Empty;
        return new TemplateSummary(
            TemplateId:            id!,
            TemplateVersion:       ver,
            TemplateSchemaVersion: schemaVer,
            DisplayName:           displayName,
            ShortDescription:      shortDesc,
            Category:              category,
            MinPaxScriptVersion:   minPax,
            MinCookbookVersion:    minCookbook,
            ManualGuidanceCount:   guidanceCount);
    }
}
