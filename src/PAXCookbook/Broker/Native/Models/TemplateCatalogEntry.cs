namespace PAXCookbook.Broker.Native.Models;

// Stage 3c -- in-memory projection of a static template document
// from app\templates\*.template.json. The PowerShell broker (via
// Read-TemplateCatalog at startup) loads every template file
// verbatim into $Script:TemplateCatalog and serves the JSON object
// directly. The native broker mirrors that contract: the list endpoint
// returns ConvertTo-TemplateSummary's narrow projection
// (Routes/Templates.ps1:177-201), and the detail endpoint returns the
// raw parsed JSON of the template file -- no transformation, no
// reshape.
//
// We keep both the narrow summary AND the raw root JsonElement on the
// catalog entry so the detail endpoint can stream the original body
// without re-reading the file.
public sealed record TemplateCatalogEntry(
    TemplateSummary Summary,
    System.Text.Json.JsonElement Document);

public sealed record TemplateSummary(
    string TemplateId,
    string TemplateVersion,
    int TemplateSchemaVersion,
    string DisplayName,
    string ShortDescription,
    string Category,
    string MinPaxScriptVersion,
    string MinCookbookVersion,
    int ManualGuidanceCount);
