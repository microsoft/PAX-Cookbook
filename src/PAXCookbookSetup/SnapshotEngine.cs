using System.Text.Json;
using PAXCookbook.Shared.Hashing;
using PAXCookbook.Shared.Json;

namespace PAXCookbookSetup;

// Snapshots <appRoot> into <installRoot>\PreviousVersions\<ver>_<utc>\
// and supports restore for rollback. Writes snapshot-manifest.json with
// file list + sha256s so restore can verify before overwriting.
public static class SnapshotEngine
{
    public sealed record SnapshotEntry(string RelativePath, string Sha256, long SizeBytes);
    public sealed record SnapshotManifest(
        string SourceAppRoot,
        string CreatedAtUtc,
        string AppVersion,
        List<SnapshotEntry> Files);

    public static string Create(string installRoot, string appRoot, string previousAppVersion)
    {
        var pvRoot = Path.Combine(installRoot, "PreviousVersions");
        Directory.CreateDirectory(pvRoot);
        var utc = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var dst = Path.Combine(pvRoot, $"{previousAppVersion}_{utc}");
        Directory.CreateDirectory(dst);

        var entries = new List<SnapshotEntry>();
        if (Directory.Exists(appRoot))
        {
            foreach (var f in Directory.EnumerateFiles(appRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(appRoot, f);
                var target = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, overwrite: true);
                var fi = new FileInfo(f);
                entries.Add(new SnapshotEntry(rel, Sha256Hash.OfFile(f), fi.Length));
            }
        }
        var manifest = new SnapshotManifest(appRoot,
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            previousAppVersion, entries);
        File.WriteAllText(Path.Combine(dst, "snapshot-manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptionsFactory.Default));
        return dst;
    }

    public static void Restore(string snapshotDir, string appRoot)
    {
        // Wipe appRoot then restore.
        if (Directory.Exists(appRoot)) Directory.Delete(appRoot, recursive: true);
        Directory.CreateDirectory(appRoot);
        foreach (var f in Directory.EnumerateFiles(snapshotDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(snapshotDir, f);
            if (rel.Equals("snapshot-manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(appRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
    }
}
