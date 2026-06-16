using System.Text.Json;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3c -- one-shot read of app\VERSION.json at broker startup.
// Mirrors the $Script:CookbookVersion / $Script:ReleaseChannel /
// $Script:PaxScript* / $Script:UpdateManifestUrl state populated by
// Test-BundledPaxIntegrity in Start-Broker.ps1 at startup. The
// PowerShell broker reads VERSION.json ONCE and caches it; the native
// broker matches that contract (no per-request rescan).
//
// Doctrine:
//   - Missing file -> IsAvailable=false, LoadError=string. The runtime
//     route surfaces that as 500 with a structured payload (parity with
//     the PowerShell broker's "pax_script_unavailable" / startup
//     refusal pattern).
//   - Malformed JSON -> IsAvailable=false, LoadError=JsonException.Message.
//   - Schema is the documented shape:
//       { schemaVersion, channel, cookbook:{version,...},
//         paxScript:{name,version,relativePath,sha256},
//         updateManifestUrl }
//     Missing optional fields surface as null on the projection.
public static class VersionInfoReader
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas         = false,
        ReadCommentHandling         = JsonCommentHandling.Disallow,
    };

    public static VersionInfo Load(string versionFilePath)
    {
        if (string.IsNullOrWhiteSpace(versionFilePath))
        {
            return Failure("version_file_path_empty");
        }
        if (!File.Exists(versionFilePath))
        {
            return Failure("version_file_missing");
        }

        string text;
        try
        {
            text = File.ReadAllText(versionFilePath);
        }
        catch (Exception ex)
        {
            return Failure("version_file_read_failed: " + ex.Message);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling     = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            return Failure("version_file_malformed: " + ex.Message);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure("version_file_not_object");
            }

            string? channel = GetOptionalString(root, "channel");
            string? cookbookVersion = null;
            if (root.TryGetProperty("cookbook", out var cookbook)
                && cookbook.ValueKind == JsonValueKind.Object)
            {
                cookbookVersion = GetOptionalString(cookbook, "version");
            }

            BundledPaxInfo? pax = null;
            if (root.TryGetProperty("paxScript", out var paxObj)
                && paxObj.ValueKind == JsonValueKind.Object)
            {
                pax = new BundledPaxInfo(
                    Name:         GetOptionalString(paxObj, "name") ?? string.Empty,
                    Version:      GetOptionalString(paxObj, "version") ?? string.Empty,
                    RelativePath: GetOptionalString(paxObj, "relativePath") ?? string.Empty,
                    Sha256:       GetOptionalString(paxObj, "sha256") ?? string.Empty);
            }

            string? updateUrl = GetOptionalString(root, "updateManifestUrl");

            return new VersionInfo(
                IsAvailable:       true,
                CookbookVersion:   cookbookVersion,
                ReleaseChannel:    channel,
                BundledPax:        pax,
                UpdateManifestUrl: updateUrl,
                LoadError:         null);
        }
    }

    private static VersionInfo Failure(string reason) =>
        new(IsAvailable: false,
            CookbookVersion: null,
            ReleaseChannel: null,
            BundledPax: null,
            UpdateManifestUrl: null,
            LoadError: reason);

    private static string? GetOptionalString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        return v.GetString();
    }
}
