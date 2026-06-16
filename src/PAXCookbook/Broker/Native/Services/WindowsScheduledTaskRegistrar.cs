using System.Diagnostics;
using System.Text;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3g -- production registrar. Spawns app\install\Register-PAX
// ScheduledRecipe.ps1 as a child pwsh.exe process with non-secret
// argv only, mirroring app\broker\Routes\ScheduledTasks.ps1's
// Invoke-ScheduledTaskRegistrar verbatim:
//
//   * Start-Process -FilePath 'pwsh' -ArgumentList ... -NoNewWindow
//     -Wait -PassThru -RedirectStandardOutput ... -RedirectStandard
//     Error ...
//   * Stdout / stderr land in <workspace>\_tmp\scheduler\<action>_
//     <recipeId>_<stamp>.{out|err}.log.
//   * Retention prune to 32 most recent files.
//   * Exit 31 means 'registrar missing or spawn failure'.
//
// PUT/DELETE routes do NOT invoke this class in Stage 3g -- the
// surface is built ahead of Stage 3h so the abstraction is ready when
// the WebAuthn re-auth verifier, projection-hash composer, and
// Credential Manager writer land. Routes call this only when
// activated via a future toggle.
//
// SECURITY:
//   * BuildArgumentList NEVER places a client secret on argv. The
//     registrar reads the secret from Windows Credential Manager at
//     wrapper fire-time, not from the broker process.
//   * The PwshPath and RegistrarPath fields are configured paths,
//     not interpreted user input.
public sealed class WindowsScheduledTaskRegistrar : IScheduledTaskRegistrar
{
    // Maximum number of per-invocation log files we keep under
    // <workspace>\_tmp\scheduler\ before pruning the oldest. The
    // PS broker's Invoke-ScheduledTaskRegistrar uses 32. Reused
    // here verbatim so log churn matches.
    public const int LogRetentionLimit = 32;

    // Exit code reserved for 'registrar missing' / 'spawn failure'.
    // Matches the PS broker sentinel so consumers (Stage 3h registrar_
    // failed mapping) can detect the spawn-failure case structurally
    // without parsing stderr.
    public const int RegistrarSpawnFailureExitCode = 31;

    private readonly string _pwshPath;
    private readonly string _registrarPath;
    private readonly string _workspacePath;

    public WindowsScheduledTaskRegistrar(
        string pwshPath,
        string registrarPath,
        string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pwshPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(registrarPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        _pwshPath      = pwshPath;
        _registrarPath = registrarPath;
        _workspacePath = workspacePath;
    }

    // Path to pwsh.exe the registrar is spawned under. Exposed for
    // assertion in tests so the argv builder contract stays visible.
    public string PwshPath => _pwshPath;

    // Path to app\install\Register-PAXScheduledRecipe.ps1.
    public string RegistrarPath => _registrarPath;

    // Workspace path passed to the registrar via -WorkspacePath. Per
    // V1.S06c, the wrapper at fire-time reads the scheduled_tasks
    // row from this workspace.
    public string WorkspacePath => _workspacePath;

    // Build the non-secret argv pwsh.exe is invoked with. Pulled out
    // of InvokeAsync so tests can verify the argument shape WITHOUT
    // launching pwsh -- which is critical because Stage 3g must NOT
    // mutate real Windows Task Scheduler.
    public static IReadOnlyList<string> BuildArgumentList(
        string registrarPath,
        ScheduledTaskRegistrarRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrarPath);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Action))
            throw new ArgumentException("Action is required.", nameof(request));
        if (!string.Equals(request.Action, "register",   StringComparison.Ordinal)
            && !string.Equals(request.Action, "unregister", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Action must be 'register' or 'unregister'.",
                nameof(request));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RecipeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ScheduledTaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspacePath);

        var args = new List<string>
        {
            "-NoProfile",
            "-NoLogo",
            "-NonInteractive",
            "-File",
            registrarPath,
            "-Action",
            request.Action,
            "-WorkspacePath",
            request.WorkspacePath,
            "-RecipeId",
            request.RecipeId,
            "-ScheduledTaskId",
            request.ScheduledTaskId,
        };

        if (string.Equals(request.Action, "register", StringComparison.Ordinal))
        {
            args.Add("-RecurrenceJson");
            args.Add(request.RecurrenceJson ?? string.Empty);
        }

        return args;
    }

    public async Task<ScheduledTaskRegistrarResult> InvokeAsync(
        ScheduledTaskRegistrarRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_registrarPath))
        {
            return new ScheduledTaskRegistrarResult(
                ExitCode:    RegistrarSpawnFailureExitCode,
                Stdout:      string.Empty,
                Stderr:      "registrar_missing: " + _registrarPath,
                LogPath:     null,
                DurationMs:  0);
        }

        var stagingDir = Path.Combine(_workspacePath, "_tmp", "scheduler");
        try { Directory.CreateDirectory(stagingDir); }
        catch { /* best-effort; Process.Start failure surfaces below */ }

        var stamp   = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var prefix  = request.Action + "_" + request.RecipeId + "_" + stamp;
        var outPath = Path.Combine(stagingDir, prefix + ".out.log");
        var errPath = Path.Combine(stagingDir, prefix + ".err.log");

        var argv = BuildArgumentList(_registrarPath, request);
        var psi = new ProcessStartInfo
        {
            FileName               = _pwshPath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = _workspacePath,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var sw = Stopwatch.StartNew();
        int exit;
        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };
            if (!proc.Start())
            {
                sw.Stop();
                return new ScheduledTaskRegistrarResult(
                    ExitCode:    RegistrarSpawnFailureExitCode,
                    Stdout:      string.Empty,
                    Stderr:      "spawn_failed: pwsh.exe did not start.",
                    LogPath:     null,
                    DurationMs:  sw.ElapsedMilliseconds);
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            exit = proc.ExitCode;
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Best-effort persist the spawn error to errPath so the
            // log retention plumbing still has a record of the failure.
            try { File.WriteAllText(errPath, "spawn_failed: " + ex.Message); } catch { }
            return new ScheduledTaskRegistrarResult(
                ExitCode:    RegistrarSpawnFailureExitCode,
                Stdout:      string.Empty,
                Stderr:      "spawn_failed: " + ex.Message,
                LogPath:     File.Exists(errPath) ? errPath : null,
                DurationMs:  sw.ElapsedMilliseconds);
        }
        sw.Stop();

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        // Persist to staging files for parity with the PS broker. The
        // routes layer will surface logPath on registrar_failed so
        // operators can pull the full transcript out of band.
        try { File.WriteAllText(outPath, stdout); } catch { }
        try { File.WriteAllText(errPath, stderr); } catch { }

        // Retention prune to the most recent LogRetentionLimit files.
        try { PruneLogs(stagingDir, LogRetentionLimit); } catch { }

        return new ScheduledTaskRegistrarResult(
            ExitCode:    exit,
            Stdout:      stdout,
            Stderr:      stderr,
            LogPath:     File.Exists(errPath) ? errPath : null,
            DurationMs:  sw.ElapsedMilliseconds);
    }

    // Public so the test fixture can exercise the prune logic without
    // spawning pwsh.exe. The PS broker keeps the 32 most-recent files
    // by LastWriteTime; we match that exactly.
    public static int PruneLogs(string stagingDir, int keep)
    {
        if (string.IsNullOrWhiteSpace(stagingDir)) return 0;
        if (!Directory.Exists(stagingDir)) return 0;
        if (keep < 0) keep = 0;
        var files = new DirectoryInfo(stagingDir).GetFiles();
        if (files.Length <= keep) return 0;
        var ordered = files
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(keep)
            .ToArray();
        int removed = 0;
        foreach (var f in ordered)
        {
            try { f.Delete(); removed++; }
            catch { /* best-effort */ }
        }
        return removed;
    }
}
