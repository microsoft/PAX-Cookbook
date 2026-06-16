using System.Text.Json;
using System.Text.Json.Serialization;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-A -- atomic writer for <workspace>/Runtime/app-close-intent.json.
//
// Parity with Routes/BrokerCloseIntent.ps1:
//   * Marker fields are ordered: schemaVersion=1, intent,
//     createdUtc, expiresUtc (= createdUtc + 10 s), writtenUtc
//     (= createdUtc).
//   * UTC timestamps formatted with the "o" round-trip specifier
//     (matches PS ToString('o')).
//   * Atomic write: temp file (<path>.tmp) then File.Move with
//     overwrite=true. File.Move on Windows is atomic on the same
//     volume per NTFS guarantees, mirroring the PS Move-Item -Force.
//   * Missing $Script:RuntimeDir is reported as 500
//     runtime_dir_unavailable; missing parent directory is created
//     best-effort (parity with the PS branch that calls
//     New-Item -ItemType Directory before write).
//   * Unknown / missing intent returns 400 invalid_intent with the
//     same `allowed` allowlist the PS broker emits.
//   * The clock is injected so tests can produce deterministic
//     createdUtc / expiresUtc values without freezing real time.
public sealed class BrokerCloseIntentWriter
{
    public static readonly IReadOnlyList<string> AllowedIntents = new[]
    {
        "app-only-close",
        "stop-server",
    };

    private const int MarkerExpirySeconds = 10;
    private const int MaxBodyBytes        = 1024;

    private readonly string?            _runtimeDir;
    private readonly Func<DateTimeOffset> _clock;

    public BrokerCloseIntentWriter(string? runtimeDir, Func<DateTimeOffset>? clock = null)
    {
        _runtimeDir = runtimeDir;
        _clock      = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public int MaxRequestBytes => MaxBodyBytes;

    public CloseIntentResult Write(string? intent)
    {
        if (string.IsNullOrWhiteSpace(_runtimeDir))
        {
            return new CloseIntentResult(
                Ok:         false,
                StatusCode: 500,
                Error:      "runtime_dir_unavailable",
                Detail:     "Workspace runtime directory is not configured.",
                Marker:     null);
        }

        var trimmed = intent?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !AllowedIntents.Contains(trimmed, StringComparer.Ordinal))
        {
            return new CloseIntentResult(
                Ok:         false,
                StatusCode: 400,
                Error:      "invalid_intent",
                Detail:     "Unknown or missing intent.",
                Marker:     null);
        }

        try
        {
            if (!Directory.Exists(_runtimeDir))
            {
                Directory.CreateDirectory(_runtimeDir);
            }
        }
        catch (Exception ex)
        {
            return new CloseIntentResult(
                Ok:         false,
                StatusCode: 500,
                Error:      "runtime_dir_unavailable",
                Detail:     ex.Message,
                Marker:     null);
        }

        var nowUtc     = _clock().ToUniversalTime();
        var createdUtc = nowUtc.ToString("o");
        var expiresUtc = nowUtc.AddSeconds(MarkerExpirySeconds).ToString("o");
        var marker = new CloseIntentMarker(
            SchemaVersion: 1,
            Intent:        trimmed,
            CreatedUtc:    createdUtc,
            ExpiresUtc:    expiresUtc,
            WrittenUtc:    createdUtc);

        var finalPath = Path.Combine(_runtimeDir, "app-close-intent.json");
        var tempPath  = finalPath + ".tmp";

        try
        {
            // Ordered properties preserved by serializing through a
            // hand-built JsonObject equivalent. We use an explicit
            // dictionary to guarantee key order independent of the
            // CLR's property-reflection order for records.
            var ordered = new Dictionary<string, object>
            {
                ["schemaVersion"] = marker.SchemaVersion,
                ["intent"]        = marker.Intent,
                ["createdUtc"]    = marker.CreatedUtc,
                ["expiresUtc"]    = marker.ExpiresUtc,
                ["writtenUtc"]    = marker.WrittenUtc,
            };
            var json = JsonSerializer.Serialize(ordered,
                new JsonSerializerOptions
                {
                    WriteIndented        = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                });
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch { }
            return new CloseIntentResult(
                Ok:         false,
                StatusCode: 500,
                Error:      "marker_write_failed",
                Detail:     ex.Message,
                Marker:     null);
        }

        return new CloseIntentResult(
            Ok:         true,
            StatusCode: 202,
            Error:      null,
            Detail:     null,
            Marker:     marker);
    }
}
