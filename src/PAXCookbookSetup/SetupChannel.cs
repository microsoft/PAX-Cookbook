using System.Reflection;

namespace PAXCookbookSetup;

// Distribution channel for this installer, stamped into the assembly at build
// time (AssemblyMetadata "PaxChannel", set by tools\release\Build-Setup.ps1 via
// -p:PaxChannel=<stable|experimental>). The wizard reads it to decide whether to
// show the "(Experimental)" marker.
//
// Fail-safe: anything that is not EXACTLY "experimental" (case/space-insensitive)
// resolves to "stable" so a mis-stamp, a missing attribute, or a stray value can
// never mislabel a production installer as experimental.
public static class SetupChannel
{
    public const string Stable = "stable";
    public const string Experimental = "experimental";

    // Runtime channel read from this assembly's build-time stamp.
    public static string Resolve() => Normalize(ReadStamp());

    // Pure normalization core (unit-tested): collapse any input to exactly
    // "stable" or "experimental".
    public static string Normalize(string? raw)
        => string.Equals(raw?.Trim(), Experimental, StringComparison.OrdinalIgnoreCase)
            ? Experimental
            : Stable;

    public static bool IsExperimental(string? raw)
        => string.Equals(Normalize(raw), Experimental, StringComparison.Ordinal);

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
