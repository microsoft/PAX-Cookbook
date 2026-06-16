using System.IO;

namespace PAXCookbookSetup.Verbs;

// Mirrors a verified payload tree into <installRoot>\PayloadCache so the
// installed Setup EXE can resolve a payload root for repair/update
// without --payload-root and without an embedded payload resource.
//
// Contract:
//   * cacheRoot is created empty before MirrorTree() is called.
//   * Every file under payloadRoot is copied to cacheRoot with the
//     same relative path (preserving directory structure).
//   * Hidden / system attributes are not propagated; the cache is a
//     plain directory of regular files.
//   * Source and destination must be different paths (the caller is
//     responsible for the self-overwrite guard).
//
// PayloadManifestVerifier.Verify(cacheRoot, manifest) is run by the
// caller after this method returns; any mismatch fails the install.
internal static class PayloadCacheWriter
{
    public static void MirrorTree(string payloadRoot, string cacheRoot)
    {
        var srcFull = Path.GetFullPath(payloadRoot);
        var dstFull = Path.GetFullPath(cacheRoot);

        if (!Directory.Exists(srcFull))
            throw new DirectoryNotFoundException($"payload root not found: {srcFull}");

        if (string.Equals(srcFull, dstFull, System.StringComparison.OrdinalIgnoreCase))
            throw new System.InvalidOperationException(
                "payload cache mirror requires source != destination");

        // Pre-create directory tree.
        foreach (var d in Directory.EnumerateDirectories(srcFull, "*",
                                                        SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcFull, d);
            Directory.CreateDirectory(Path.Combine(dstFull, rel));
        }

        foreach (var f in Directory.EnumerateFiles(srcFull, "*",
                                                   SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcFull, f);
            if (rel.Contains("..", System.StringComparison.Ordinal))
                throw new System.InvalidOperationException(
                    $"unsafe relative path in payload tree: {rel}");
            var dst = Path.Combine(dstFull, rel);
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir))
                Directory.CreateDirectory(dstDir);
            File.Copy(f, dst, overwrite: true);
        }
    }
}
