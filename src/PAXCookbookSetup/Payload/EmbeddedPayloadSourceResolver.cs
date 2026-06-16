using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace PAXCookbookSetup.Payload;

// Reads the PAXCookbook.Payload.zip embedded resource from an assembly
// and extracts it to a fresh temp folder. Refuses any zip entry that:
//   * has an absolute path (rooted, drive letter, UNC prefix);
//   * contains ".." segments or any path that escapes the destination
//     after canonicalization;
//   * resolves outside the destination root after Path.GetFullPath.
//
// Extraction creates a brand-new directory under %TEMP% named
//   PAXCookbookPayload_<UTC>_<random>\
// so we never overwrite an existing folder.
public sealed class EmbeddedPayloadSourceResolver : IPayloadSourceResolver
{
    public const string ResourceLogicalName = "PAXCookbook.Payload.zip";
    private readonly Func<Stream?> _open;
    private readonly string _tempBase;

    public EmbeddedPayloadSourceResolver(Assembly? carrier = null, string? tempBase = null)
        : this(BuildOpener(carrier), tempBase) { }

    // Test seam: supply a custom stream opener so unit tests can feed
    // synthetic zip contents without an embedded resource.
    public EmbeddedPayloadSourceResolver(Func<Stream?> openResource, string? tempBase = null)
    {
        _open = openResource;
        _tempBase = tempBase ?? Path.GetTempPath();
    }

    private static Func<Stream?> BuildOpener(Assembly? carrier)
    {
        var a = carrier ?? Assembly.GetExecutingAssembly();
        return () => a.GetManifestResourceStream(ResourceLogicalName);
    }

    public static bool HasEmbeddedPayload(Assembly? carrier = null)
    {
        var a = carrier ?? Assembly.GetExecutingAssembly();
        foreach (var n in a.GetManifestResourceNames())
            if (string.Equals(n, ResourceLogicalName, StringComparison.Ordinal)) return true;
        return false;
    }

    public PayloadSource Resolve()
    {
        Stream? stream = null;
        try { stream = _open(); }
        catch (Exception ex)
        {
            return new PayloadSource(false, null, "embedded", null,
                $"embedded payload resource open failed: {ex.Message}");
        }
        if (stream is null)
            return new PayloadSource(false, null, "embedded", null,
                $"embedded payload resource not found: {ResourceLogicalName}");

        string dest = Path.Combine(_tempBase,
            "PAXCookbookPayload_"
            + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ")
            + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try { Directory.CreateDirectory(dest); }
        catch (Exception ex)
        {
            stream.Dispose();
            return new PayloadSource(false, null, "embedded", null,
                $"cannot create temp extraction dir: {ex.Message}");
        }

        string destFull = Path.GetFullPath(dest)
                             .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var warnings = new List<string>();
        try
        {
            using (stream)
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
            {
                foreach (var e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.FullName)) continue;
                    if (TraversalCheck(e.FullName) is string reason)
                        return new PayloadSource(false, null, "embedded", dest,
                            $"refused unsafe zip entry '{e.FullName}': {reason}");

                    string target = Path.GetFullPath(Path.Combine(dest, e.FullName));
                    if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                        return new PayloadSource(false, null, "embedded", dest,
                            $"refused zip entry escaping extraction root: '{e.FullName}'");

                    // Directory entries
                    if (e.FullName.EndsWith("/", StringComparison.Ordinal) ||
                        e.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    var parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);

                    using var fs = new FileStream(target, FileMode.CreateNew,
                                                  FileAccess.Write, FileShare.None);
                    using var es = e.Open();
                    es.CopyTo(fs);
                }
            }
        }
        catch (Exception ex)
        {
            return new PayloadSource(false, null, "embedded", dest,
                $"extraction failed: {ex.Message}");
        }

        if (!File.Exists(Path.Combine(dest, "manifest.json")))
            return new PayloadSource(false, null, "embedded", dest,
                "extracted payload is missing manifest.json");

        return new PayloadSource(true, dest, "embedded", dest, null, warnings);
    }

    // Best-effort cleanup of an extraction folder created by Resolve().
    // Returns true on full delete, false if anything was left behind.
    public static bool TryCleanup(string? tempExtractionRoot)
    {
        if (string.IsNullOrEmpty(tempExtractionRoot)) return true;
        if (!Directory.Exists(tempExtractionRoot)) return true;
        try { Directory.Delete(tempExtractionRoot, recursive: true); return true; }
        catch { return false; }
    }

    // Returns null if the entry name is safe; otherwise an error reason.
    public static string? TraversalCheck(string name)
    {
        if (Path.IsPathRooted(name)) return "absolute path";
        if (name.Contains(':')) return "contains drive letter / stream marker";
        if (name.StartsWith(@"\\", StringComparison.Ordinal)) return "UNC prefix";
        // Normalize separators and check each segment.
        var parts = name.Replace('\\', '/').Split('/');
        foreach (var p in parts)
        {
            if (p == "..") return "parent-directory segment";
            if (p == "." )  continue;
        }
        return null;
    }
}
