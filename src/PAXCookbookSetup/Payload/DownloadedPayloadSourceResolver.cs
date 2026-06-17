using System.IO.Compression;

namespace PAXCookbookSetup.Payload;

// Extracts a downloaded payload zip to a temp folder, with the same
// path-traversal guards as EmbeddedPayloadSourceResolver.
public sealed class DownloadedPayloadSourceResolver : IPayloadSourceResolver
{
    private readonly string _zipPath;
    private readonly string _tempBase;
    
    public DownloadedPayloadSourceResolver(string zipPath, string? tempBase = null)
    {
        _zipPath = zipPath;
        _tempBase = tempBase ?? Path.GetTempPath();
    }
    
    public PayloadSource Resolve()
    {
        if (!File.Exists(_zipPath))
            return new PayloadSource(false, null, "downloaded", null,
                $"Downloaded payload zip not found: {_zipPath}");
        
        string dest = Path.Combine(_tempBase,
            "PAXCookbookPayload_"
            + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ")
            + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        
        try { Directory.CreateDirectory(dest); }
        catch (Exception ex)
        {
            return new PayloadSource(false, null, "downloaded", null,
                $"Cannot create temp extraction dir: {ex.Message}");
        }
        
        string destFull = Path.GetFullPath(dest)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        
        try
        {
            using var stream = new FileStream(_zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            
            foreach (var e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.FullName)) continue;
                
                if (EmbeddedPayloadSourceResolver.TraversalCheck(e.FullName) is string reason)
                    return new PayloadSource(false, null, "downloaded", dest,
                        $"Refused unsafe zip entry '{e.FullName}': {reason}");
                
                string target = Path.GetFullPath(Path.Combine(dest, e.FullName));
                if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                    return new PayloadSource(false, null, "downloaded", dest,
                        $"Refused zip entry escaping extraction root: '{e.FullName}'");
                
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
        catch (Exception ex)
        {
            return new PayloadSource(false, null, "downloaded", dest,
                $"Extraction failed: {ex.Message}");
        }
        
        if (!File.Exists(Path.Combine(dest, "manifest.json")))
            return new PayloadSource(false, null, "downloaded", dest,
                "Extracted payload is missing manifest.json");
        
        return new PayloadSource(true, dest, "downloaded", dest, null);
    }
    
    // Best-effort cleanup of an extraction folder.
    public static bool TryCleanup(string? tempExtractionRoot)
    {
        if (string.IsNullOrEmpty(tempExtractionRoot)) return true;
        if (!Directory.Exists(tempExtractionRoot)) return true;
        try { Directory.Delete(tempExtractionRoot, recursive: true); return true; }
        catch { return false; }
    }
}
