namespace PAXCookbook.Shared.Io;

// Path-traversal-safe combine. Rejects rooted, parent-relative, or
// otherwise out-of-root paths BEFORE any filesystem call.
public static class SafePath
{
    public static bool IsSafeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return false;
        if (Path.IsPathRooted(relative)) return false;
        // Disallow URI-style and drive-letter forms.
        if (relative.Contains(':')) return false;
        // Disallow null/control chars and explicit parent traversal.
        if (relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        foreach (var ch in relative)
        {
            if (char.IsControl(ch)) return false;
        }
        var segments = relative.Replace('/', '\\').Split('\\');
        foreach (var seg in segments)
        {
            if (seg == ".." || seg == ".") return false;
            if (string.IsNullOrWhiteSpace(seg)) return false;
        }
        return true;
    }

    public static string CombineUnderRoot(string root, string relative)
    {
        if (!IsSafeRelative(relative))
            throw new ArgumentException($"unsafe relative path: '{relative}'", nameof(relative));
        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relative));
        // Final containment check (defense-in-depth).
        var rootWithSep = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"path traversal: '{relative}' escapes root '{root}'");
        }
        return combined;
    }
}
