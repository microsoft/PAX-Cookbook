using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3h -- production IWindowsReAuthVerifier. Spawns a hidden
// one-shot pwsh.exe that dot-sources app\broker\Auth\WindowsReAuth.ps1
// and invokes Invoke-WindowsReAuth -Message <msg> -TimeoutMs <ms>.
// The PS function emits the verdict string ("Verified", "Canceled",
// "DeviceNotPresent", ...); the sidecar wraps it in a small JSON
// envelope and returns it through stdout. This pattern guarantees
// the native broker's re-auth policy and verdict semantics stay in
// strict lock-step with the PowerShell broker -- the same PS
// function is invoked end-to-end.
//
// Doctrine (mirrors PaxInvocationPlanProvider.cs):
//   * ProcessStartInfo: UseShellExecute=false, CreateNoWindow=true,
//     RedirectStandardOutput=true, RedirectStandardError=true.
//   * ArgumentList element-by-element (no shell parsing).
//   * Single-quote escape ' -> '' for embedded values in the PS
//     -Command body (no string interpolation at the C# layer).
//   * Bounded timeout. Default 120s -- Hello prompt is human-paced;
//     bumping past the 10s plan-resolver default is intentional.
//   * On timeout: kill the process tree, return ComInteropFailure
//     with FailureDetail = "sidecar_timeout".
//   * On crash / non-zero exit / unparseable stdout: same
//     ComInteropFailure mapping, FailureDetail records the cause.
//   * NO secrets ever appear on argv -- the WinRT verifier does
//     not take credentials. Sidecar only carries opClass + message.
public sealed class WindowsReAuthSidecarVerifier : IWindowsReAuthVerifier
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly string _pwshPath;
    private readonly string _windowsReAuthScriptPath;
    private readonly TimeSpan _timeout;

    public WindowsReAuthSidecarVerifier(
        string pwshPath,
        string windowsReAuthScriptPath,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pwshPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsReAuthScriptPath);
        _pwshPath = pwshPath;
        _windowsReAuthScriptPath = windowsReAuthScriptPath;
        _timeout = timeout ?? DefaultTimeout;
    }

    public string PwshPath => _pwshPath;
    public string WindowsReAuthScriptPath => _windowsReAuthScriptPath;
    public TimeSpan Timeout => _timeout;

    public async Task<WindowsReAuthVerdict> VerifyAsync(
        string opClass,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (!File.Exists(_pwshPath))
        {
            return Failure("pwsh_missing: " + _pwshPath);
        }
        if (!File.Exists(_windowsReAuthScriptPath))
        {
            return Failure("windows_reauth_script_missing: "
                + _windowsReAuthScriptPath);
        }

        var script = BuildSidecarScript(_windowsReAuthScriptPath, message,
            (int)_timeout.TotalMilliseconds);

        var psi = new ProcessStartInfo
        {
            FileName               = _pwshPath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) =>
            { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) =>
            { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };

        try
        {
            if (!proc.Start())
            {
                return Failure("spawn_failed: pwsh.exe did not start.");
            }
        }
        catch (Exception ex)
        {
            return Failure("spawn_failed: " + ex.Message);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked     = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (timeoutCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                return Failure("sidecar_timeout after "
                    + (int)_timeout.TotalMilliseconds + "ms");
            }
            throw;
        }

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        if (proc.ExitCode != 0)
        {
            return Failure("sidecar_exit_nonzero: code=" + proc.ExitCode
                + " stderr=" + stderr.Trim());
        }

        return Parse(stdout, stderr);
    }

    // Build the -Command body. The script:
    //   1. ErrorActionPreference = Stop -- any failure bubbles to a
    //      thrown PS error so the sidecar wrapper surfaces it.
    //   2. Dot-sources WindowsReAuth.ps1 (which Add-Types the WinRT
    //      bridge and defines Invoke-WindowsReAuth).
    //   3. Calls Invoke-WindowsReAuth with the message and timeout.
    //   4. Captures Get-WindowsReAuthLastFailureDetail only when the
    //      verdict is ComInteropFailure (the PS function only sets
    //      it in that path).
    //   5. Emits a single JSON object: { verdict, failureDetail }.
    // Single-quote escaping: ' -> '' for the message embedded in the
    // PS single-quoted string.
    internal static string BuildSidecarScript(
        string windowsReAuthScriptPath,
        string message,
        int timeoutMs)
    {
        // Inputs (script path + message) are dropped into single-quoted
        // PS literals via the standard ' -> '' escape, identical to
        // the Stage 3e PaxInvocationPlanProvider recipe.
        var pathEsc    = EscapeSingleQuoted(windowsReAuthScriptPath);
        var messageEsc = EscapeSingleQuoted(message);

        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference='Stop';");
        sb.Append(" . '").Append(pathEsc).Append("';");
        sb.Append(" $verdict = Invoke-WindowsReAuth -Message '")
          .Append(messageEsc)
          .Append("' -TimeoutMs ")
          .Append(timeoutMs)
          .Append(";");
        sb.Append(" $detail = if ($verdict -eq 'ComInteropFailure')");
        sb.Append(" { Get-WindowsReAuthLastFailureDetail } else { $null };");
        sb.Append(" [Console]::Out.Write((@{verdict=$verdict;");
        sb.Append(" failureDetail=$detail} | ConvertTo-Json -Compress));");

        return sb.ToString();
    }

    private static string EscapeSingleQuoted(string s) =>
        s.Replace("'", "''");

    private static WindowsReAuthVerdict Parse(string stdout, string stderr)
    {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0)
        {
            return Failure("sidecar_empty_stdout: stderr=" + stderr.Trim());
        }
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure("sidecar_stdout_not_object: " + trimmed);
            }
            if (!root.TryGetProperty("verdict", out var verdictEl)
                || verdictEl.ValueKind != JsonValueKind.String)
            {
                return Failure("sidecar_missing_verdict: " + trimmed);
            }
            var verdict = verdictEl.GetString() ?? string.Empty;
            string? detail = null;
            if (root.TryGetProperty("failureDetail", out var detailEl)
                && detailEl.ValueKind == JsonValueKind.String)
            {
                detail = detailEl.GetString();
            }
            return new WindowsReAuthVerdict(
                Result:        verdict,
                IsVerified:    verdict == "Verified",
                FailureDetail: detail);
        }
        catch (JsonException ex)
        {
            return Failure("sidecar_stdout_unparseable: "
                + ex.Message + " raw=" + trimmed);
        }
    }

    private static WindowsReAuthVerdict Failure(string detail) =>
        new(Result: "ComInteropFailure", IsVerified: false,
            FailureDetail: detail);
}
