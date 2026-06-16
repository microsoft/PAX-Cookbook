using System.Text.Json;

namespace PAXCookbook.Runtime;

// Reads <workspace>\Runtime\workspace.lock written by the broker.
// Surfaces the broker PID + port. Read-only; never deletes the file.
public sealed record WorkspaceLock(
    int? BrokerProcessId,
    int? BrokerPort,
    string? BrokerSessionId,
    string LockFile);

public sealed class WorkspaceLockReader
{
    public WorkspaceLock? TryRead(string workspaceFolderPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceFolderPath)) return null;
        var lockFile = Path.Combine(workspaceFolderPath, "Runtime", "workspace.lock");
        if (!File.Exists(lockFile)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(lockFile));
            var root = doc.RootElement;
            int? pid = null;
            int? port = null;
            string? sid = null;
            if (root.TryGetProperty("brokerProcessId", out var p) &&
                p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pidV))
            {
                pid = pidV;
            }
            if (root.TryGetProperty("brokerPort", out var pp) &&
                pp.ValueKind == JsonValueKind.Number && pp.TryGetInt32(out var portV))
            {
                port = portV;
            }
            if (root.TryGetProperty("brokerSessionId", out var s) &&
                s.ValueKind == JsonValueKind.String)
            {
                sid = s.GetString();
            }
            return new WorkspaceLock(pid, port, sid, lockFile);
        }
        catch
        {
            return null;
        }
    }
}
