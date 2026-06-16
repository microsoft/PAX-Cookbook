using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-A -- update manifest schema parser + version comparator.
//
// Parity with app/broker/Update/Manifest.psm1:
//   * Top-level allowlist: schemaVersion, channel, releaseTimestamp,
//     latestCookbook, includedPaxScript, compatibility.
//   * Required: schemaVersion, channel, releaseTimestamp,
//     latestCookbook.
//   * latestCookbook required: version, packageUrl, sha256.
//                  optional: releaseNotesUrl.
//   * includedPaxScript required: name, version, relativePath,
//     sha256.
//   * compatibility optional, but if present accepts only
//     minCookbookVersionForPaxScript and
//     minimumCompatibleInstallerVersion.
//   * schemaVersion: integer 1 only.
//   * sha256: ^[0-9A-Fa-f]{64}$.
//   * Unknown keys cause unknown_field rejection at the top level.
//     The PS parser is stricter inside nested objects too; we
//     mirror that for latestCookbook / includedPaxScript /
//     compatibility.
public sealed class UpdateManifestParser
{
    private static readonly Regex Sha256Hex = new(
        "^[0-9A-Fa-f]{64}$", RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedTop = new(StringComparer.Ordinal)
    {
        "schemaVersion", "channel", "releaseTimestamp",
        "latestCookbook", "includedPaxScript", "compatibility",
    };
    private static readonly HashSet<string> RequiredTop = new(StringComparer.Ordinal)
    {
        "schemaVersion", "channel", "releaseTimestamp", "latestCookbook",
    };
    private static readonly HashSet<string> AllowedLatestCookbook = new(StringComparer.Ordinal)
    {
        "version", "packageUrl", "sha256", "releaseNotesUrl",
    };
    private static readonly HashSet<string> RequiredLatestCookbook = new(StringComparer.Ordinal)
    {
        "version", "packageUrl", "sha256",
    };
    private static readonly HashSet<string> AllowedIncludedPaxScript = new(StringComparer.Ordinal)
    {
        "name", "version", "relativePath", "sha256",
    };
    private static readonly HashSet<string> AllowedCompatibility = new(StringComparer.Ordinal)
    {
        "minCookbookVersionForPaxScript", "minimumCompatibleInstallerVersion",
    };

    public ParseOutcome Parse(string rawJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson);
        }
        catch (Exception ex)
        {
            return ParseOutcome.Invalid("manifest_unparseable",
                "Manifest JSON could not be parsed: " + ex.Message);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ParseOutcome.Invalid("manifest_not_object",
                    "Manifest root must be a JSON object.");
            }

            // Top-level required / unknown checks.
            var seenTop = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in root.EnumerateObject())
            {
                seenTop.Add(prop.Name);
                if (!AllowedTop.Contains(prop.Name))
                {
                    return ParseOutcome.Invalid("unknown_field",
                        "Unknown top-level field \"" + prop.Name + "\".");
                }
            }
            foreach (var required in RequiredTop)
            {
                if (!seenTop.Contains(required))
                {
                    return ParseOutcome.Invalid("missing_field",
                        "Missing required field \"" + required + "\".");
                }
            }

            if (!root.TryGetProperty("schemaVersion", out var sv) ||
                sv.ValueKind != JsonValueKind.Number ||
                !sv.TryGetInt32(out var svInt) ||
                svInt != 1)
            {
                return ParseOutcome.Invalid("unsupported_schema_version",
                    "Only schemaVersion=1 is supported.");
            }

            // latestCookbook
            if (!root.TryGetProperty("latestCookbook", out var lc) ||
                lc.ValueKind != JsonValueKind.Object)
            {
                return ParseOutcome.Invalid("type_mismatch",
                    "latestCookbook must be an object.");
            }
            var seenLc = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in lc.EnumerateObject())
            {
                seenLc.Add(prop.Name);
                if (!AllowedLatestCookbook.Contains(prop.Name))
                {
                    return ParseOutcome.Invalid("unknown_field",
                        "Unknown field latestCookbook.\"" + prop.Name + "\".");
                }
            }
            foreach (var required in RequiredLatestCookbook)
            {
                if (!seenLc.Contains(required))
                {
                    return ParseOutcome.Invalid("missing_field",
                        "Missing required field latestCookbook." + required + ".");
                }
            }
            var lcVersion    = lc.GetProperty("version").GetString();
            var lcPackageUrl = lc.GetProperty("packageUrl").GetString();
            var lcSha256     = lc.GetProperty("sha256").GetString();
            if (string.IsNullOrWhiteSpace(lcVersion))
            {
                return ParseOutcome.Invalid("type_mismatch",
                    "latestCookbook.version must be a non-empty string.");
            }
            if (string.IsNullOrWhiteSpace(lcPackageUrl))
            {
                return ParseOutcome.Invalid("type_mismatch",
                    "latestCookbook.packageUrl must be a non-empty string.");
            }
            if (string.IsNullOrEmpty(lcSha256) || !Sha256Hex.IsMatch(lcSha256))
            {
                return ParseOutcome.Invalid("invalid_sha256",
                    "latestCookbook.sha256 must be 64-char lower/upper hex.");
            }

            // includedPaxScript (optional).
            string? includedPaxName        = null;
            string? includedPaxVersion     = null;
            string? includedPaxRelativePath = null;
            string? includedPaxSha256      = null;
            if (root.TryGetProperty("includedPaxScript", out var ips))
            {
                if (ips.ValueKind != JsonValueKind.Object)
                {
                    return ParseOutcome.Invalid("type_mismatch",
                        "includedPaxScript must be an object.");
                }
                foreach (var prop in ips.EnumerateObject())
                {
                    if (!AllowedIncludedPaxScript.Contains(prop.Name))
                    {
                        return ParseOutcome.Invalid("unknown_field",
                            "Unknown field includedPaxScript.\"" + prop.Name + "\".");
                    }
                }
                if (ips.TryGetProperty("name", out var n))         includedPaxName         = n.GetString();
                if (ips.TryGetProperty("version", out var v))      includedPaxVersion      = v.GetString();
                if (ips.TryGetProperty("relativePath", out var r)) includedPaxRelativePath = r.GetString();
                if (ips.TryGetProperty("sha256", out var s))
                {
                    var sha = s.GetString();
                    if (!string.IsNullOrEmpty(sha) && !Sha256Hex.IsMatch(sha))
                    {
                        return ParseOutcome.Invalid("invalid_sha256",
                            "includedPaxScript.sha256 must be 64-char hex.");
                    }
                    includedPaxSha256 = sha;
                }
            }

            // compatibility (optional).
            string? minCookbookForPax    = null;
            string? minCompatibleInstaller = null;
            if (root.TryGetProperty("compatibility", out var cmp))
            {
                if (cmp.ValueKind != JsonValueKind.Object)
                {
                    return ParseOutcome.Invalid("type_mismatch",
                        "compatibility must be an object.");
                }
                foreach (var prop in cmp.EnumerateObject())
                {
                    if (!AllowedCompatibility.Contains(prop.Name))
                    {
                        return ParseOutcome.Invalid("unknown_field",
                            "Unknown field compatibility.\"" + prop.Name + "\".");
                    }
                }
                if (cmp.TryGetProperty("minCookbookVersionForPaxScript", out var p1))
                    minCookbookForPax = p1.GetString();
                if (cmp.TryGetProperty("minimumCompatibleInstallerVersion", out var p2))
                    minCompatibleInstaller = p2.GetString();
            }

            var channel = root.GetProperty("channel").GetString() ?? string.Empty;

            // Build a snapshot dictionary preserving the PS hashtable
            // shape so the download route can read the bits without
            // re-parsing JSON.
            var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"]     = 1,
                ["channel"]           = channel,
                ["latestCookbook"]    = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["version"]    = lcVersion,
                    ["packageUrl"] = lcPackageUrl,
                    ["sha256"]     = lcSha256,
                },
                ["includedPaxScript"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"]         = includedPaxName,
                    ["version"]      = includedPaxVersion,
                    ["relativePath"] = includedPaxRelativePath,
                    ["sha256"]       = includedPaxSha256,
                },
                ["compatibility"]     = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["minCookbookVersionForPaxScript"]    = minCookbookForPax,
                    ["minimumCompatibleInstallerVersion"] = minCompatibleInstaller,
                },
            };

            return new ParseOutcome(
                Ok:                       true,
                Error:                    null,
                Message:                  null,
                Channel:                  channel,
                LatestCookbookVersion:    lcVersion,
                LatestPackageUrl:         lcPackageUrl,
                LatestSha256:             lcSha256,
                IncludedPaxName:          includedPaxName,
                IncludedPaxVersion:       includedPaxVersion,
                IncludedPaxRelativePath:  includedPaxRelativePath,
                IncludedPaxSha256:        includedPaxSha256,
                MinCookbookForPax:        minCookbookForPax,
                MinCompatibleInstaller:   minCompatibleInstaller,
                Snapshot:                 snapshot);
        }
    }

    public static int CompareVersion(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right)) return 0;
        if (string.IsNullOrEmpty(left)) return -1;
        if (string.IsNullOrEmpty(right)) return 1;
        if (Version.TryParse(left, out var lv) && Version.TryParse(right, out var rv))
        {
            return lv.CompareTo(rv);
        }
        return string.CompareOrdinal(left, right);
    }
}

public sealed record ParseOutcome(
    bool                           Ok,
    string?                        Error,
    string?                        Message,
    string?                        Channel,
    string?                        LatestCookbookVersion,
    string?                        LatestPackageUrl,
    string?                        LatestSha256,
    string?                        IncludedPaxName,
    string?                        IncludedPaxVersion,
    string?                        IncludedPaxRelativePath,
    string?                        IncludedPaxSha256,
    string?                        MinCookbookForPax,
    string?                        MinCompatibleInstaller,
    IDictionary<string, object?>?  Snapshot)
{
    public static ParseOutcome Invalid(string error, string message) =>
        new(false, error, message,
            null, null, null, null, null, null, null, null, null, null, null);
}
