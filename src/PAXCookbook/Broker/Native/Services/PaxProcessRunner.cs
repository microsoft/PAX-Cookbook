using System.Diagnostics;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3e -- runs the PAX child process per the PowerShell
// supervisor's verbatim contract (Cook/Supervisor.ps1 ~line 255):
//
//   FileName              = pwsh.exe
//   UseShellExecute       = false
//   CreateNoWindow        = true
//   RedirectStandardOutput = true
//   RedirectStandardError  = true
//   RedirectStandardInput  = true
//   WorkingDirectory      = <cook folder>
//   ArgumentList          = '-NoProfile', '-NoLogo', '-Command',
//                            plan.SpawnArgv[3]
//
// stdout / stderr are tee'd to the supplied CookLogWriter; stderr is
// prefixed "[STDERR] " inside the writer. The runner does NOT touch
// SQLite; the orchestrator updates the cooks row in its terminal
// transition based on the runner's Result. The runner does NOT
// implement stall detection / cancel / kill / WebSocket streaming --
// those remain in the PowerShell supervisor and Stage 3f / 3g.
//
// Doctrine:
//   - The child process is started with InheritStdHandles=false (the
//     PSI defaults) so no console window inherits. CreateNoWindow=true
//     guarantees no flash.
//   - The runner only signals "PAX exited" -- it does not classify
//     the exit code beyond returning it. The orchestrator owns the
//     status mapping.
//   - If Start() throws (PSI rejected, file not found), the runner
//     returns Status=SpawnFailed with the exception message. The
//     orchestrator translates that to a 500 supervisor_spawn_failed
//     and writes the interrupted cook row.
public sealed class PaxProcessRunner
{
    private readonly string _pwshPath;

    public PaxProcessRunner(string pwshPath)
    {
        _pwshPath = pwshPath;
    }

    public async Task<PaxRunResult> RunAsync(
        PaxInvocationPlan plan,
        string            cookFolder,
        CookLogWriter     log,
        CancellationToken cancellationToken = default,
        Action<int>?      onProcessStarted  = null)
    {
        if (string.IsNullOrWhiteSpace(_pwshPath))
        {
            return PaxRunResult.SpawnFailed("pwsh_path_not_configured");
        }
        if (plan.SpawnArgv is null || plan.SpawnArgv.Length < 4)
        {
            return PaxRunResult.SpawnFailed("plan_spawnArgv_too_short");
        }

        var psi = new ProcessStartInfo
        {
            FileName               = _pwshPath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            WorkingDirectory       = cookFolder,
        };
        // Verbatim parity: pass SpawnArgv element-by-element. Element
        // 3 is the -Command expression; PowerShell handles its own
        // quoting via ArgumentList so we MUST NOT re-quote it.
        psi.ArgumentList.Add(plan.SpawnArgv[0]); // -NoProfile
        psi.ArgumentList.Add(plan.SpawnArgv[1]); // -NoLogo
        psi.ArgumentList.Add(plan.SpawnArgv[2]); // -Command
        psi.ArgumentList.Add(plan.SpawnArgv[3]); // <command expression>

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi };
        }
        catch (Exception ex)
        {
            return PaxRunResult.SpawnFailed("psi_construction_failed: " + ex.Message);
        }

        // Tee both streams into the shared-read cook.log writer. The
        // PS supervisor does the same via async drain + ConcurrentQueue;
        // here OutputDataReceived/ErrorDataReceived call the writer
        // directly because Stage 3e does not implement the WebSocket
        // streamer (Stage 3f) -- there is no queue to fan-out to.
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            log.WriteStdoutLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            log.WriteStderrLine(e.Data);
        };

        int pid;
        DateTimeOffset startedUtc;
        try
        {
            if (!proc.Start())
            {
                proc.Dispose();
                return PaxRunResult.SpawnFailed("Process.Start_returned_false");
            }
            pid = proc.Id;
            startedUtc = DateTimeOffset.UtcNow;
            // Stage 3j -- publish the OS pid to the caller as soon
            // as Process.Start() returns so CookExecutionService can
            // refresh its registry handle with the real pid. The
            // callback runs on the spawning thread; receivers must
            // not block. Exceptions in the callback are swallowed
            // to keep the spawn path resilient.
            if (onProcessStarted is not null)
            {
                try { onProcessStarted(pid); } catch { }
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            // Close stdin so the child sees an empty input stream and
            // does not block on a host that never types into it.
            proc.StandardInput.Close();
        }
        catch (Exception ex)
        {
            try { proc.Dispose(); } catch { }
            return PaxRunResult.SpawnFailed("start_failed: " + ex.Message);
        }

        try
        {
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }

        // Ensure async drains have flushed their final newline batch
        // to the log writer before we report the exit code.
        try { proc.WaitForExit(); } catch { }

        var exitCode = SafeExitCode(proc);
        var finishedUtc = DateTimeOffset.UtcNow;
        try { proc.Dispose(); } catch { }

        return PaxRunResult.Exited(pid, exitCode, startedUtc, finishedUtc);
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return int.MinValue; }
    }
}

public enum PaxRunStatus
{
    Exited,
    SpawnFailed,
}

public sealed record PaxRunResult(
    PaxRunStatus    Status,
    int?            Pid,
    int?            ExitCode,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? FinishedUtc,
    string?         Detail)
{
    public static PaxRunResult Exited(int pid, int exitCode,
        DateTimeOffset startedUtc, DateTimeOffset finishedUtc) =>
        new(PaxRunStatus.Exited, pid, exitCode, startedUtc, finishedUtc, null);

    public static PaxRunResult SpawnFailed(string detail) =>
        new(PaxRunStatus.SpawnFailed, null, null, null, null, detail);
}
