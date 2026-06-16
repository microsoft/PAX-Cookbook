using System.IO;

namespace PAXCookbookSetup.Payload;

// Resolves a payload source from a verified payload cache folder that
// the InstallVerb wrote alongside the install root during the original
// install. This allows the installed Setup\PAXCookbookSetup.exe — which
// has no embedded payload — to invoke repair/update without requiring
// the user to point --payload-root at an external folder, and without
// the temp self-handoff trying to access the (no longer present) public
// single-EXE artifact.
//
// Layout written by InstallVerb after a successful copy:
//
//   <installRoot>\
//     PayloadCache\
//       manifest.json
//       App\bin\PAXCookbook.exe
//       PAXCookbookSetup.exe
//       broker\...
//       web\...
//
// The cache is a verbatim copy of the payload root the install verb
// consumed. Manifest hashes/sizes are re-verified by the caller
// (PayloadManifestVerifier) before the payload is handed to repair.
//
// Standard uninstall (UninstallOperations.StandardRemovableSubdirs)
// removes PayloadCache alongside App / Setup / PreviousVersions /
// WebView2Data / Runtime. Workspace is never under PayloadCache.
public sealed class LocalCachePayloadSourceResolver : IPayloadSourceResolver
{
    public const string PayloadCacheDirName = "PayloadCache";

    private readonly string _installRoot;

    public LocalCachePayloadSourceResolver(string installRoot)
    {
        _installRoot = installRoot;
    }

    public static string CachePath(string installRoot)
        => Path.Combine(installRoot, PayloadCacheDirName);

    public static bool HasCache(string installRoot)
    {
        var cache = CachePath(installRoot);
        return Directory.Exists(cache)
               && File.Exists(Path.Combine(cache, "manifest.json"));
    }

    public PayloadSource Resolve()
    {
        if (string.IsNullOrWhiteSpace(_installRoot))
            return new PayloadSource(false, null, "local-cache", null,
                "install-root is empty");

        var cache = CachePath(_installRoot);
        if (!Directory.Exists(cache))
            return new PayloadSource(false, null, "local-cache", null,
                $"payload cache does not exist: {cache}");

        if (!File.Exists(Path.Combine(cache, "manifest.json")))
            return new PayloadSource(false, null, "local-cache", null,
                $"manifest.json not found in payload cache: {cache}");

        return new PayloadSource(true, Path.GetFullPath(cache),
            "local-cache", null, null);
    }
}
