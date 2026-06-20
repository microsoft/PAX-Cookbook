using System.Security.Cryptography;
using System.Text.Json;

namespace PAXCookbookSetup;

// Records what this install/update placed on disk so the running app can
// compare its installed payload against the published versions.json for the
// in-app self-updater (tier-2 SHA comparison). Written to
// "<installRoot>\App\installed-skus.json".
//
// Best-effort and additive: it is a plain, non-schema-strict sidecar (NOT the
// schema-validated install-state.json), so writing it never affects install
// success. The payload SHA is known only when the payload came from a real zip
// (the normal download path); other sources leave it null and the app falls
// back to version/build-timestamp comparison.
internal static class InstalledSkusWriter
{
    public static void Write(string installRoot, string appVersion, string? payloadZipPath)
    {
        string appRoot = Path.Combine(installRoot, "App");
        Directory.CreateDirectory(appRoot);

        string? payloadSha = null;
        if (!string.IsNullOrEmpty(payloadZipPath) && File.Exists(payloadZipPath))
        {
            using FileStream fs = File.OpenRead(payloadZipPath);
            payloadSha = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        var record = new
        {
            schemaVersion = 1,
            appVersion,
            payloadSha256 = payloadSha,
            recordedAtUtc = DateTime.UtcNow.ToString("o"),
        };

        string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(appRoot, "installed-skus.json"), json);
    }
}
