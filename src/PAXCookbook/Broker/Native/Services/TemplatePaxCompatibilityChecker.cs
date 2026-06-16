using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-B2 -- semver gate that mirrors Test-TemplatePaxCompatibility
// (app\broker\Routes\TemplateValidator.ps1 ~line 339). Pure tuple
// comparison; no auto-upgrade, no caret/tilde, no pre-release. If
// either version string is malformed (not exactly three integer
// parts), the gate returns null (compatibility presumed) -- matching
// the PS helper's defensive bailouts.
public static class TemplatePaxCompatibilityChecker
{
    public static ValidationError? Check(string? minPaxScriptVersion, string bundledPaxVersion)
    {
        if (string.IsNullOrWhiteSpace(minPaxScriptVersion)) return null;
        if (!TryParse(minPaxScriptVersion, out var req)) return null;
        if (!TryParse(bundledPaxVersion,   out var cur)) return null;

        for (int i = 0; i < 3; i++)
        {
            if (cur[i] > req[i]) return null;
            if (cur[i] < req[i])
            {
                return new ValidationError(
                    InstancePath: "/minPaxScriptVersion",
                    Keyword:      "paxIncompatible",
                    Message:      "template requires bundled PAX >= " + minPaxScriptVersion +
                                  " but broker has " + bundledPaxVersion,
                    Params:       new Dictionary<string, object?>
                    {
                        ["requiredMin"] = minPaxScriptVersion,
                        ["bundled"]     = bundledPaxVersion,
                    });
            }
        }
        return null;
    }

    private static bool TryParse(string v, out int[] parts)
    {
        parts = new int[3];
        var split = v.Split('.');
        if (split.Length != 3) return false;
        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(split[i], out parts[i])) return false;
        }
        return true;
    }
}
