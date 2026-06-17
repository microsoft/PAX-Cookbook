namespace PAXCookbookSetup.Gui;

// Host allow-list for prerequisite and payload downloads. Installers fetch
// ONLY from Microsoft / GitHub / python.org hosts. Every URL — whether
// hard-coded, returned by the GitHub API, or a redirect target — is validated
// against this list before any request is made. This is the SSRF / open-relay
// guard for the download seam (mirrors the Pantry download-proxy host
// validation already sanctioned in constraint 1).
public static class PrereqDownloadHosts
{
    // Exact hosts or parent suffixes (".x" matches host == x or *.x).
    private static readonly string[] AllowedSuffixes =
    {
        "github.com",
        "githubusercontent.com",
        "python.org",
        "microsoft.com",               // .NET 8 Desktop Runtime downloads
    };

    // True only for an absolute https URL whose host is GitHub or python.org.
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = uri.Host;
        foreach (var suffix in AllowedSuffixes)
        {
            if (string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase))
                return true;
            if (host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
