using System.Collections.Concurrent;
using PAXCookbook.Shared.Logging;
using PAXCookbook.Shared.Security;

namespace PAXCookbook.Logging;

// Minimal App NDJSON logger.
//
// Writes one JSON event per line to:
//   <installRoot>\Logs\App\app-<UTC-yyyyMMdd>.log
//
// Sanitizes every field via LogSanitizer.Redact. Never logs raw protocol
// URIs, tokens, cookies, or response bodies. Thread-safe (lock-per-file).
public sealed class AppLogger
{
    private readonly string _logDir;
    private readonly string _sessionId;
    private readonly object _gate = new();

    public AppLogger(string logDir, string? sessionId = null)
    {
        _logDir = logDir;
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
    }

    public string SessionId => _sessionId;
    public string LogDir => _logDir;
    public string CurrentLogFile =>
        Path.Combine(_logDir, "app-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".log");

    public void Write(string component, string eventName, string level = "info",
                      IDictionary<string, object?>? fields = null)
    {
        var safe = SanitizeFields(fields);
        var ev = new NdjsonLogEvent
        {
            Component = component,
            SessionId = _sessionId,
            Event = eventName,
            Level = level,
            Fields = safe
        };
        var line = NdjsonLogWriter.Serialize(ev);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_logDir);
                File.AppendAllText(CurrentLogFile, line + Environment.NewLine);
            }
            catch
            {
                // Best-effort. Never let logging break command flow.
            }
        }
    }

    private static Dictionary<string, object?>? SanitizeFields(IDictionary<string, object?>? f)
    {
        if (f is null || f.Count == 0) return null;
        var result = new Dictionary<string, object?>(f.Count);
        foreach (var kv in f)
        {
            object? v = kv.Value;
            if (v is string s) v = LogSanitizer.Redact(s);
            result[kv.Key] = v;
        }
        return result;
    }
}
