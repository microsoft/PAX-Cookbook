using System.Security.Cryptography;
using System.Text.Json;

namespace PAXCookbookSetup;

// Records what this install/update placed on disk so the running app can
// compare its installed payload against the published versions.json for the
// in-app self-updater (tier-2 SHA comparison). Written to
// "<installRoot>\installed-skus.json" — the install root, alongside
// install-state.json (NOT under App\), so it is easy to find and reason about.
//
// Best-effort and additive: it is a plain, non-schema-strict sidecar (NOT the
// schema-validated install-state.json), so writing it never affects install
// success. The payload SHA is known only when the payload came from a real zip
// (the normal download path); other sources leave it null and the app falls
// back to version comparison.
internal static class InstalledSkusWriter
{
    public static void Write(string installRoot, string appVersion,
                             string? payloadZipPath, SetupLogger? log = null)
    {
        Directory.CreateDirectory(installRoot);
        string path = Path.Combine(installRoot, "installed-skus.json");
        bool zipPresent = !string.IsNullOrEmpty(payloadZipPath) && File.Exists(payloadZipPath);

        log?.Write("installed-skus-writing", fields: new Dictionary<string, object?>
        {
            ["path"] = path,
            ["zipPath"] = payloadZipPath,
            ["zipPresent"] = zipPresent,
        });

        string? payloadSha = null;
        if (zipPresent)
        {
            using FileStream fs = File.OpenRead(payloadZipPath!);
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
        File.WriteAllText(path, json);

        // Verify the write: the file must now exist and round-trip to valid JSON
        // with a non-empty payloadSha256. A null SHA is legitimate only for
        // non-download sources (dev / embedded / offline cache); the normal
        // download install must record a real SHA, so a missing one is worth a
        // warning in the setup log.
        bool fileExists = File.Exists(path);
        bool shaPresent = false;
        try
        {
            if (fileExists)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                shaPresent = doc.RootElement.TryGetProperty("payloadSha256", out var p)
                    && p.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(p.GetString());
            }
        }
        catch
        {
            // fall through to the warning below
        }

        if (fileExists && shaPresent)
        {
            log?.Write("installed-skus-written", fields: new Dictionary<string, object?>
            {
                ["path"] = path,
                ["payloadSha256"] = payloadSha,
            });
        }
        else
        {
            log?.Write("installed-skus-verify-failed", "warn", new Dictionary<string, object?>
            {
                ["path"] = path,
                ["fileExists"] = fileExists,
                ["shaPresent"] = shaPresent,
                ["payloadSha256"] = payloadSha,
                ["zipPresent"] = zipPresent,
            });
        }
    }
}
