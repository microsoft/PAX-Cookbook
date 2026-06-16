using System.Text.Json;
using PAXCookbook.Shared.Json;
using PAXCookbook.Shared.Logging;
using PAXCookbook.Shared.Security;

namespace PAXCookbookSetup;

// Minimal NDJSON setup logger. Writes one event per line to
// <logsDir>\setup-<utcDate>.log. Sanitizes free-form 'detail' fields
// via the shared LogSanitizer before write.
public sealed class SetupLogger : IDisposable
{
    private readonly string _logFile;
    private readonly string _sessionId;
    private readonly object _gate = new();

    public SetupLogger(string logsDir)
    {
        Directory.CreateDirectory(logsDir);
        var day = DateTime.UtcNow.ToString("yyyyMMdd");
        _logFile = Path.Combine(logsDir, $"setup-{day}.log");
        _sessionId = Guid.NewGuid().ToString();
    }

    public void Write(string eventName, string level = "info",
                      IDictionary<string, object?>? fields = null)
    {
        var safeFields = new Dictionary<string, object?>();
        if (fields is not null)
        {
            foreach (var kv in fields)
            {
                var v = kv.Value is string s ? LogSanitizer.Redact(s) : kv.Value;
                safeFields[kv.Key] = v;
            }
        }
        var evt = new NdjsonLogEvent
        {
            Ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Component = "setup",
            Pid = Environment.ProcessId,
            SessionId = _sessionId,
            Event = eventName,
            Level = level,
            Fields = safeFields
        };
        var line = NdjsonLogWriter.Serialize(evt);
        lock (_gate)
        {
            // Defensive: someone might have deleted the directory between
            // construction and now (especially during cleanup paths).
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
            File.AppendAllText(_logFile, line + "\n");
        }
    }

    public string LogFilePath => _logFile;
    public string SessionId => _sessionId;
    public void Dispose() { }
}
