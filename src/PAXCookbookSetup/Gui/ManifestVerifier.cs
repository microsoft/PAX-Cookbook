using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace PAXCookbookSetup.Gui;

// Fetches the repo "versions.json" manifest and exposes the SHA-256 + size it
// declares for the current payload. The Setup EXE is decoupled from the payload
// version: it reads the expected hash from this manifest at runtime instead of
// embedding it, so a payload update only requires updating the manifest in the
// repo — the signed Setup EXE never has to change.
//
// Graceful degradation: if the manifest cannot be fetched or parsed (offline,
// corporate proxy, GitHub blocked), TryFetchAsync returns null and the caller
// falls back to the zip-integrity check only.
public sealed class ManifestVerifier
{
    public const string ManifestUrl =
        "https://raw.githubusercontent.com/microsoft/PAX-Cookbook/main/versions.json";

    private readonly SetupLogger _log;

    public ManifestVerifier(SetupLogger log) => _log = log;

    // Expected payload values parsed from versions.json. Any field may be null
    // when the manifest omits it; callers verify only the fields that are set.
    public sealed record PayloadExpectation(string? Sha256, long? Size, string? Version);

    // Downloads and parses versions.json. Returns null (never throws, except on
    // cancellation) so the install can degrade to zip-integrity-only checking.
    public async Task<PayloadExpectation?> TryFetchAsync(CancellationToken cancel)
    {
        if (!PrereqDownloadHosts.IsAllowed(ManifestUrl))
        {
            _log.Write("manifest-url-disallowed", "warning",
                new Dictionary<string, object?> { ["url"] = ManifestUrl });
            return null;
        }

        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            using var resp = await http.GetAsync(ManifestUrl,
                HttpCompletionOption.ResponseContentRead, cancel);

            // Reject redirects rather than chasing them (SSRF hygiene); a missing
            // or moved manifest simply degrades to integrity-only verification.
            if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400)
            {
                _log.Write("manifest-fetch-redirect", "warning",
                    new Dictionary<string, object?> { ["status"] = (int)resp.StatusCode });
                return null;
            }
            if (!resp.IsSuccessStatusCode)
            {
                _log.Write("manifest-fetch-failed", "warning",
                    new Dictionary<string, object?> { ["status"] = (int)resp.StatusCode });
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(cancel);
            var parsed = Parse(json);
            if (parsed is null)
            {
                _log.Write("manifest-parse-failed", "warning");
                return null;
            }

            _log.Write("manifest-fetched", "info",
                new Dictionary<string, object?>
                {
                    ["version"] = parsed.Version,
                    ["sha256"] = parsed.Sha256,
                    ["size"] = parsed.Size
                });
            return parsed;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Write("manifest-fetch-exception", "warning",
                new Dictionary<string, object?>
                {
                    ["exception"] = ex.GetType().Name,
                    ["message"] = ex.Message
                });
            return null;
        }
    }

    // Parses versions.json text. Returns null if the structure is missing the
    // current.payload node or carries neither a sha256 nor a size. Tolerant of
    // extra/unknown fields and of either field being absent.
    public static PayloadExpectation? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("current", out var cur) || cur.ValueKind != JsonValueKind.Object)
                return null;
            if (!cur.TryGetProperty("payload", out var pay) || pay.ValueKind != JsonValueKind.Object)
                return null;

            string? sha = pay.TryGetProperty("sha256", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : null;
            long? size = pay.TryGetProperty("size", out var z) && z.ValueKind == JsonValueKind.Number
                          && z.TryGetInt64(out var zl)
                ? zl : (long?)null;
            string? ver = cur.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

            if (string.IsNullOrWhiteSpace(sha) && size is null) return null;
            return new PayloadExpectation(
                string.IsNullOrWhiteSpace(sha) ? null : sha!.Trim().ToLowerInvariant(),
                size,
                string.IsNullOrWhiteSpace(ver) ? null : ver);
        }
        catch
        {
            return null;
        }
    }

    // SHA-256 of a file as lowercase hex.
    public static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
