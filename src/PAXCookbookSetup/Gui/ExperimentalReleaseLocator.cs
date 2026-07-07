using System.Text.Json;

namespace PAXCookbookSetup.Gui;

// Locates the newest GitHub PRE-RELEASE for the experimental channel and the
// two release assets the updater needs: the payload zip and the versions.json
// manifest. Mirrors PowerShell7Installer's pattern — a pure, unit-tested
// JSON selector plus a thin fetch that reuses the host-validated downloader.
//
// The "newest" pre-release is chosen by created_at (descending), NOT by version
// parsing, so experimental tags like v2.0.0-exp.2 never touch the semver
// comparator. Every asset URL is validated against PrereqDownloadHosts before
// it is returned, so a spoofed release body can never redirect a download off
// the GitHub CDN.
public static class ExperimentalReleaseLocator
{
    public const string ReleasesApiUrl =
        "https://api.github.com/repos/microsoft/PAX-Cookbook/releases?per_page=100";

    // GitHub Release asset names produced by tools\release\Build-Setup.ps1.
    private const string PayloadAssetName = "PAX_Cookbook_Payload.zip";
    private const string ManifestAssetName = "versions.json";

    public sealed record Located(string ManifestUrl, string PayloadUrl);

    // Pure selector (unit-tested): from the releases-list JSON, pick the newest
    // pre-release by created_at and return its payload + manifest asset URLs.
    // Returns null on malformed JSON, no pre-release, or a missing asset.
    public static Located? Parse(string? releasesJson)
    {
        if (string.IsNullOrWhiteSpace(releasesJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(releasesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            JsonElement newest = default;
            DateTimeOffset newestCreated = DateTimeOffset.MinValue;
            bool found = false;

            foreach (JsonElement rel in doc.RootElement.EnumerateArray())
            {
                if (rel.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // Pre-releases only; never a draft.
                if (!(rel.TryGetProperty("prerelease", out JsonElement pre)
                        && pre.ValueKind == JsonValueKind.True))
                {
                    continue;
                }
                if (rel.TryGetProperty("draft", out JsonElement draft)
                        && draft.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                DateTimeOffset created = DateTimeOffset.MinValue;
                if (rel.TryGetProperty("created_at", out JsonElement createdEl)
                        && createdEl.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(createdEl.GetString(), out DateTimeOffset parsed))
                {
                    created = parsed;
                }

                if (!found || created > newestCreated)
                {
                    newest = rel;
                    newestCreated = created;
                    found = true;
                }
            }

            if (!found)
            {
                return null;
            }

            string? payloadUrl = SelectAssetUrl(newest, PayloadAssetName);
            string? manifestUrl = SelectAssetUrl(newest, ManifestAssetName);
            if (payloadUrl is null || manifestUrl is null)
            {
                return null;
            }

            return new Located(manifestUrl, payloadUrl);
        }
        catch
        {
            return null;
        }
    }

    // Fetch the releases list through the host-validated downloader and select
    // the newest pre-release's assets. Returns null on any failure so the caller
    // can surface a clean "no experimental build found" message.
    public static Located? Locate(IPrereqDownloader downloader)
    {
        string? json = downloader.GetText(ReleasesApiUrl, "application/vnd.github+json");
        return Parse(json);
    }

    private static string? SelectAssetUrl(JsonElement release, string assetName)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets)
                || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (!asset.TryGetProperty("name", out JsonElement nameEl)
                    || nameEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            if (!string.Equals(nameEl.GetString(), assetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (asset.TryGetProperty("browser_download_url", out JsonElement urlEl)
                    && urlEl.ValueKind == JsonValueKind.String)
            {
                string? url = urlEl.GetString();
                if (PrereqDownloadHosts.IsAllowed(url))
                {
                    return url;
                }
            }
        }

        return null;
    }
}
