using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.Runtime;

// Writes <workspace>\Runtime\workspace.lock for the in-process
// native broker hosted inside PAXCookbook.exe. The on-disk shape
// mirrors the PowerShell broker's Write-WorkspaceLock payload
// (Start-Broker.ps1 ~line 1108) so external readers --
// WorkspaceLockReader, the bundled scheduler launcher, status JSON,
// support bundle assembly -- see the same envelope regardless of
// which broker implementation is running.
//
// Doctrine:
//   * Atomic write: payload is written to <lock>.tmp then File.Move
//     with overwrite, matching the PS broker's tmp+rename pattern.
//   * Best-effort removal on shutdown: Remove swallows IO errors.
//     The OS does not unlock the file on process death because the
//     native broker never opens it for read (no FileStream held),
//     so an orphaned workspace.lock from an OS-level kill is
//     reconciled by WorkspaceLockReader's IsAlive(pid) check.
//   * The workspace.lock.acquire sentinel that Start-Broker.ps1
//     holds with FileShare.None is intentionally NOT written. The
//     native broker lives inside PAXCookbook.exe, which enforces
//     single-instance ownership through MutexAppInstanceGate (Phase
//     7). Two PAXCookbook.exe processes cannot both reach the broker
//     start path simultaneously, so the per-workspace acquire file
//     is redundant.
//   * schemaVersion stays at 1 -- the PS broker's value -- because
//     the on-disk shape is byte-compatible.
//   * brokerSessionId is omitted (PS broker writes null for it
//     today). Stage 3j does not add a new column to the lock file.
public sealed class WorkspaceLockWriter
{
    private readonly string _workspaceFolderPath;
    private readonly string _appRoot;
    private readonly string _cookbookVersion;
    private readonly string _launchMode;

    public WorkspaceLockWriter(
        string workspaceFolderPath,
        string appRoot,
        string cookbookVersion,
        string launchMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceFolderPath);
        _workspaceFolderPath = workspaceFolderPath;
        _appRoot             = appRoot ?? string.Empty;
        _cookbookVersion     = cookbookVersion ?? string.Empty;
        _launchMode          = string.IsNullOrWhiteSpace(launchMode) ? "embedded" : launchMode;
    }

    public string LockFilePath
        => Path.Combine(_workspaceFolderPath, "Runtime", "workspace.lock");

    // Writes the lock atomically. Caller supplies the in-process
    // broker pid (Environment.ProcessId) and the chosen port. The
    // Runtime directory is created if missing -- the PS broker does
    // the same via New-Item -ItemType Directory -Force.
    public void Write(int brokerProcessId, int brokerPort)
    {
        var runtimeDir = Path.Combine(_workspaceFolderPath, "Runtime");
        Directory.CreateDirectory(runtimeDir);

        var lockFile = LockFilePath;
        var tmp = lockFile + ".tmp";

        var payload = new Dictionary<string, object?>
        {
            ["schemaVersion"]       = 1,
            ["machineName"]         = Environment.MachineName,
            ["windowsUserName"]     = ResolveWindowsUserName(),
            ["windowsUserSid"]      = ResolveWindowsUserSid(),
            ["brokerProcessId"]     = brokerProcessId,
            ["brokerPort"]          = brokerPort,
            ["launchTimestampUtc"]  = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["cookbookVersion"]     = _cookbookVersion,
            ["launchMode"]          = _launchMode,
            ["consoleWindowHandle"] = 0,
            ["appRoot"]             = _appRoot,
            ["workspaceRoot"]       = _workspaceFolderPath,
            ["logsPath"]            = Path.Combine(_workspaceFolderPath, "Logs"),
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        // UTF-8 without BOM, matching the PS broker's
        // [System.Text.UTF8Encoding]::new($false).
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(tmp, json, utf8NoBom);
        File.Move(tmp, lockFile, overwrite: true);
    }

    // Removes the lock file on broker stop. Swallows IO errors so a
    // missing / locked file never crashes the broker stop path.
    public void Remove()
    {
        try
        {
            var lockFile = LockFilePath;
            if (File.Exists(lockFile))
            {
                File.Delete(lockFile);
            }
        }
        catch
        {
            // ignored -- removal is best-effort.
        }
    }

    private static string ResolveWindowsUserName()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveWindowsUserSid()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
