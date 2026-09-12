using System.Reflection;

namespace PAXCookbookSetup;

// Distribution channel for this installer, stamped into the assembly at build
// time (AssemblyMetadata "PaxChannel", set by tools\release\Build-Setup.ps1 via
// -p:PaxChannel=<stable|experimental|internal>). The wizard reads it to decide whether to
// show the "(Experimental)" marker.
//
// Fail-safe: the three recognized values are preserved; anything else resolves
// to stable. Only exact experimental metadata enables pre-release chrome.
public static class SetupChannel
{
    public const string Stable = "stable";
    public const string Experimental = "experimental";
    public const string Internal = "internal";

    // Runtime channel read from this assembly's build-time stamp.
    public static string Resolve() => Normalize(ReadStamp());

    // Pure normalization core (unit-tested): preserve a recognized channel and
    // collapse missing or unknown input to stable.
    public static string Normalize(string? raw)
    {
        var normalized = raw?.Trim();
        if (string.Equals(normalized, Experimental, StringComparison.OrdinalIgnoreCase))
            return Experimental;
        if (string.Equals(normalized, Internal, StringComparison.OrdinalIgnoreCase))
            return Internal;
        return Stable;
    }

    public static bool IsExperimental(string? raw)
        => string.Equals(Normalize(raw), Experimental, StringComparison.Ordinal);

    public static bool IsInternal(string? raw)
        => string.Equals(Normalize(raw), Internal, StringComparison.Ordinal);

    private static string? ReadStamp()
    {
        foreach (AssemblyMetadataAttribute a in Assembly
                     .GetExecutingAssembly()
                     .GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(a.Key, "PaxChannel", StringComparison.OrdinalIgnoreCase))
            {
                return a.Value;
            }
        }

        return null;
    }
}
