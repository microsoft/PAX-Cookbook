using System.Diagnostics;
using PAXCookbook.Runtime;

namespace PAXCookbook.Broker;

// Injectable broker process orchestrator. Production implementation
// uses System.Diagnostics.Process; tests inject a fake.
public interface IBrokerController
{
    BrokerStatus Probe(string workspaceFolderPath);
    BrokerStartResult Start(BrokerStartOptions options);
    BrokerStopResult Stop(BrokerStopOptions options);
}

// Process launcher abstraction so unit tests can verify the broker is
// invoked with the expected fixed path + ArgumentList, without spawning
// a real pwsh.exe.
public sealed record BrokerLaunchRecord(string FileName, IReadOnlyList<string> Arguments);

public interface IBrokerProcessLauncher
{
    int? Start(string fileName, IList<string> arguments);
    BrokerLaunchRecord? Last { get; }
}

public sealed class RealBrokerProcessLauncher : IBrokerProcessLauncher
{
    public BrokerLaunchRecord? Last { get; private set; }

    public int? Start(string fileName, IList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        Last = new BrokerLaunchRecord(fileName, arguments.ToList());
        try
        {
            var p = Process.Start(psi);
            return p?.Id;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class RecordingBrokerProcessLauncher : IBrokerProcessLauncher
{
    public BrokerLaunchRecord? Last { get; private set; }
    public int FakePid { get; set; } = 99999;

    public int? Start(string fileName, IList<string> arguments)
    {
        Last = new BrokerLaunchRecord(fileName, arguments.ToList());
        return FakePid;
    }
}

public sealed class BrokerController : IBrokerController
{
    private readonly IBrokerProcessLauncher _launcher;
    private readonly IBrokerProcessProbe _probe;
    private readonly WorkspaceLockReader _lockReader;
    private readonly Func<DateTime> _now;
    private readonly Func<int, bool>? _stopProcess;
    private readonly string _pwshPath;

    public BrokerController(
        IBrokerProcessLauncher launcher,
        IBrokerProcessProbe probe,
        WorkspaceLockReader lockReader,
        Func<DateTime>? now = null,
        Func<int, bool>? stopProcess = null,
        string? pwshPath = null)
    {
        _launcher = launcher;
        _probe = probe;
        _lockReader = lockReader;
        _now = now ?? (() => DateTime.UtcNow);
        _stopProcess = stopProcess;
        _pwshPath = pwshPath ?? ResolvePwshPath();
    }

    public BrokerStatus Probe(string workspaceFolderPath)
    {
        var wl = _lockReader.TryRead(workspaceFolderPath);
        if (wl is null)
            return new BrokerStatus(false, null, null, null, null, "none");

        var running = wl.BrokerProcessId is int pid && _probe.IsAlive(pid);
        string? url = wl.BrokerPort is int port
            ? "http://localhost:" + port
            : null;
        return new BrokerStatus(
            Running: running,
            Pid: wl.BrokerProcessId,
            Port: wl.BrokerPort,
            Url: running ? url : null,
            LockFile: wl.LockFile,
            Source: running ? "workspace-lock" : "process-probe");
    }

    public BrokerStartResult Start(BrokerStartOptions options)
    {
        // 1. If broker is already alive, reuse.
        var pre = Probe(options.WorkspaceFolderPath);
        if (pre.Running)
            return new BrokerStartResult(BrokerStartOutcome.AlreadyRunning, pre, null);

        // 2. Spawn pwsh -NoProfile -ExecutionPolicy Bypass -File <Start-Broker.ps1>
        //    -WorkspacePath <ws>.
        //    Fixed script path (resolved by BrokerPaths). Workspace path is the
        //    operator-selected workspace from the bootstrap pointer or
        //    --install-root override, NEVER a value reaching us from the
        //    protocol verb. Args use ArgumentList, not string concatenation.
        var args = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", options.BrokerStartScript,
            "-WorkspacePath", options.WorkspaceFolderPath
        };
        var spawnedPid = _launcher.Start(_pwshPath, args);
        if (spawnedPid is null)
        {
            return new BrokerStartResult(
                BrokerStartOutcome.Failed,
                new BrokerStatus(false, null, null, null, null, "none"),
                "Failed to spawn pwsh.exe for broker.");
        }

        // 3. Poll for workspace.lock + responsive PID.
        var deadline = _now() + options.ReadyTimeout;
        BrokerStatus last = pre;
        while (_now() < deadline)
        {
            last = Probe(options.WorkspaceFolderPath);
            if (last.Running) return new BrokerStartResult(BrokerStartOutcome.Started, last, null);
            Thread.Sleep(200);
        }
        return new BrokerStartResult(
            BrokerStartOutcome.Failed,
            last,
            "Broker did not become ready within " + options.ReadyTimeout.TotalSeconds + " s.");
    }

    public BrokerStopResult Stop(BrokerStopOptions options)
    {
        if (options.BrokerPid <= 0 || !_probe.IsAlive(options.BrokerPid))
            return new BrokerStopResult(BrokerStopOutcome.AlreadyStopped, null);

        try
        {
            if (_stopProcess is not null)
            {
                if (!_stopProcess(options.BrokerPid))
                    return new BrokerStopResult(BrokerStopOutcome.Failed, "Injected stop returned false.");
            }
            else
            {
                using var p = Process.GetProcessById(options.BrokerPid);
                if (!p.HasExited) p.Kill(entireProcessTree: false);
                p.WaitForExit((int)options.ExitTimeout.TotalMilliseconds);
                if (!p.HasExited)
                    return new BrokerStopResult(BrokerStopOutcome.Failed, "Broker did not exit in time.");
            }
            return new BrokerStopResult(BrokerStopOutcome.Stopped, null);
        }
        catch (Exception ex)
        {
            return new BrokerStopResult(BrokerStopOutcome.Failed, ex.Message);
        }
    }

    private static string ResolvePwshPath()
    {
        // Prefer pwsh.exe on PATH; fall back to the standard install location.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        var pf = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrEmpty(pf))
        {
            var fixedPath = Path.Combine(pf, "PowerShell", "7", "pwsh.exe");
            if (File.Exists(fixedPath)) return fixedPath;
        }
        return "pwsh.exe";
    }
}
