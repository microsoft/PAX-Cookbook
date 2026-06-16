namespace PAXCookbook.Shared.Versioning;

// Minimal semantic-version parse/compare. Accepts "MAJOR.MINOR.PATCH" with
// optional fourth ".REVISION" segment (System.Version style) for
// File/Setup version compatibility. Pre-release / build-metadata suffixes
// are accepted in input but stripped for comparison in this Phase 2
// scaffold; richer SemVer 2.0 handling is deferred to a later phase if
// needed.
public readonly record struct SemVer(int Major, int Minor, int Patch, int Revision)
    : IComparable<SemVer>
{
    public static bool TryParse(string? input, out SemVer value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var s = input.Trim();
        // strip leading "v"
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);
        // strip pre-release / build metadata
        var dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s.Substring(0, dash);

        var parts = s.Split('.');
        if (parts.Length < 2 || parts.Length > 4) return false;

        int major = 0, minor = 0, patch = 0, revision = 0;
        if (!int.TryParse(parts[0], out major) || major < 0) return false;
        if (!int.TryParse(parts[1], out minor) || minor < 0) return false;
        if (parts.Length >= 3)
        {
            if (!int.TryParse(parts[2], out patch) || patch < 0) return false;
        }
        if (parts.Length == 4)
        {
            if (!int.TryParse(parts[3], out revision) || revision < 0) return false;
        }

        value = new SemVer(major, minor, patch, revision);
        return true;
    }

    public static SemVer Parse(string input)
    {
        if (!TryParse(input, out var v))
            throw new FormatException($"Invalid version string: '{input}'");
        return v;
    }

    public int CompareTo(SemVer other)
    {
        int c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);     if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);     if (c != 0) return c;
        return Revision.CompareTo(other.Revision);
    }

    public override string ToString()
        => Revision != 0
            ? $"{Major}.{Minor}.{Patch}.{Revision}"
            : $"{Major}.{Minor}.{Patch}";

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
}

public enum VersionRelation
{
    Older,
    Equal,
    Newer
}

public static class VersionCompare
{
    // Compares an installed version to a candidate setup version.
    // Per update-downgrade-rollback-contract.md, downgrade is allowed by
    // default at the comparison level; policy enforcement (prompt,
    // --allow-downgrade) lives in PAXCookbookSetup.
    public static VersionRelation Compare(SemVer installed, SemVer candidate)
    {
        int c = candidate.CompareTo(installed);
        if (c < 0) return VersionRelation.Older;
        if (c > 0) return VersionRelation.Newer;
        return VersionRelation.Equal;
    }
}
