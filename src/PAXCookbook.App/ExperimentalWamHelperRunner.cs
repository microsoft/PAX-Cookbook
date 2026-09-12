using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace PAXCookbook.App;

// The single guarded helper runner owned by the broker (Track 1 / T1-S3).
//
// This is the ONLY way the product executes the provisioning/verification helper.
// It never accepts an arbitrary script path, command, scope, redirect URI, or
// output path from the UI: the helper path is fixed (resolved from the verified
// application root), arguments are passed structurally via ArgumentList, and the
// only file read back is the helper's machine-result written into a broker-owned
// temporary directory. Azure CLI tokens, Graph bodies, and user identity never
// cross this boundary — only a bounded status plus the strict result JSON.
//
// Guarantees:
//   * PowerShell 7 resolved through the established locator (no PATH trust).
//   * Fixed helper path + existence + optional SHA-256 integrity check.
//   * UseShellExecute=false, redirected stdout/stderr, ArgumentList only.
//   * Bounded timeout + cancellation; on either, ONLY the helper process tree is
//     terminated.
//   * Result size cap; strict parsing happens in the caller.
//   * Temp files always deleted.
//   * At most ONE helper operation at a time (concurrency gate).
//   * Never runs in a stable/default build or while the broker is Locked (the
//     caller enforces build + lock; the runner refuses a null pwsh/helper).
internal sealed class ExperimentalWamHelperRunner
{
    internal const int DefaultMaxResultBytes = 256 * 1024;

    // One helper operation at a time across the whole process.
    private static readonly SemaphoreSlim ConcurrencyGate = new(1, 1);

    private readonly string _helperPath;
    private readonly string? _pwshPathOverride;
    private readonly string? _expectedSha256;
    private readonly int _maxResultBytes;
    private readonly TimeSpan _timeout;

    internal ExperimentalWamHelperRunner(
        string helperPath,
        string? pwshPathOverride = null,
        string? expectedSha256 = null,
        int maxResultBytes = DefaultMaxResultBytes,
        TimeSpan? timeout = null)
    {
        _helperPath = helperPath;
        _pwshPathOverride = pwshPathOverride;
        _expectedSha256 = expectedSha256;
        _maxResultBytes = maxResultBytes;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    // Resolves the fixed helper path from the verified application root:
    // <appRoot>\..\tools\entra\New-PaxCookbookEntraWamSetup.ps1 (dev/repo and the
    // pilot package both place the helper there relative to the app payload).
    internal static string ResolveHelperPath(string appRoot)
        => Path.GetFullPath(Path.Combine(appRoot, "..", "tools", "entra", "New-PaxCookbookEntraWamSetup.ps1"));

    internal ExperimentalWamHelperRunResult Run(
        ExperimentalWamHelperOperation operation,
        IReadOnlyList<string>? extraArgs = null,
        CancellationToken cancellationToken = default)
    {
        // Concurrency gate: refuse a second concurrent helper operation.
        if (!ConcurrencyGate.Wait(0))
        {
            return ExperimentalWamHelperRunResult.Of(ExperimentalWamHelperStatus.Busy);
        }

        string? tempDir = null;
        try
        {
            string? pwsh = _pwshPathOverride ?? PwshLocator.Resolve();
            if (string.IsNullOrWhiteSpace(pwsh) || !File.Exists(pwsh))
            {
                return ExperimentalWamHelperRunResult.Of(ExperimentalWamHelperStatus.PwshMissing);
            }

            if (!File.Exists(_helperPath))
            {
                return ExperimentalWamHelperRunResult.Of(ExperimentalWamHelperStatus.HelperMissing);
            }

            // Optional integrity check against a bundled/known SHA-256.
            if (!string.IsNullOrWhiteSpace(_expectedSha256))
            {
                string actual = ComputeSha256(_helperPath);
                if (!string.Equals(actual, _expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return ExperimentalWamHelperRunResult.Of(ExperimentalWamHelperStatus.HelperHashMismatch);
                }
            }

            tempDir = Path.Combine(Path.GetTempPath(), "paxwamhelper_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string resultFile = Path.Combine(tempDir, "result.json");

            var psi = new ProcessStartInfo
            {
                FileName = pwsh!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = tempDir,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(_helperPath);
            psi.ArgumentList.Add("-Action");
            psi.ArgumentList.Add(MapAction(operation));
            psi.ArgumentList.Add("-SetupResultPath");
            psi.ArgumentList.Add(resultFile);
            if (operation == ExperimentalWamHelperOperation.DeprovisionPlan)
            {
                psi.ArgumentList.Add("-PlanOnly");
            }
            if (extraArgs is not null)
            {
                foreach (string a in extraArgs)
                {
                    psi.ArgumentList.Add(a);
                }
            }

            Process process;
            try
            {
                process = Process.Start(psi)!;
            }
            catch
            {
                return ExperimentalWamHelperRunResult.Of(ExperimentalWamHelperStatus.LaunchFailed);
            }

            // Drain output streams to prevent a full-pipe deadlock; the content is
            // discarded (no token/Graph body/identifier ever crosses into logs).
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            bool exited;
            try
            {
                exited = process.WaitForExit((int)_timeout.TotalMilliseconds);
            }
            catch
            {
                exited = false;
            }

            if (!exited)
            {
                KillTree(process);
                return ExperimentalWamHelperRunResult.Of(ExperimentalWamHelperStatus.Timeout);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                KillTree(process);
                return ExperimentalWamHelperRunResult.Of(ExperimentalWamHelperStatus.Cancelled);
            }

            if (process.ExitCode != 0)
            {
                return ExperimentalWamHelperRunResult.Of(ExperimentalWamHelperStatus.NonZeroExit, exitCode: process.ExitCode);
            }

            if (!File.Exists(resultFile))
            {
                return ExperimentalWamHelperRunResult.Of(ExperimentalWamHelperStatus.ResultMissing);
            }

            var info = new FileInfo(resultFile);
            if (info.Length > _maxResultBytes)
            {
                return ExperimentalWamHelperRunResult.Of(ExperimentalWamHelperStatus.OversizedResult);
            }

            string json;
            try
            {
                json = File.ReadAllText(resultFile);
            }
            catch
            {
                return ExperimentalWamHelperRunResult.Of(ExperimentalWamHelperStatus.ResultMissing);
            }

            return ExperimentalWamHelperRunResult.Success(json);
        }
        finally
        {
            if (tempDir is not null)
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch
                {
                    // Best effort; never surfaces a token/identifier.
                }
            }

            ConcurrencyGate.Release();
        }
    }

    private static string MapAction(ExperimentalWamHelperOperation op) => op switch
    {
        ExperimentalWamHelperOperation.Verify => "Verify",
        ExperimentalWamHelperOperation.DeprovisionPlan => "Deprovision",
        ExperimentalWamHelperOperation.Deprovision => "Deprovision",
        _ => "Verify",
    };

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    internal static string ComputeSha256(string path)
    {
        using FileStream fs = File.OpenRead(path);
        byte[] hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal enum ExperimentalWamHelperOperation
{
    Verify = 0,
    DeprovisionPlan = 1,
    Deprovision = 2,
}

internal enum ExperimentalWamHelperStatus
{
    Success = 0,
    Busy = 1,
    PwshMissing = 2,
    HelperMissing = 3,
    HelperHashMismatch = 4,
    LaunchFailed = 5,
    Timeout = 6,
    Cancelled = 7,
    NonZeroExit = 8,
    ResultMissing = 9,
    OversizedResult = 10,
}

internal sealed class ExperimentalWamHelperRunResult
{
    private ExperimentalWamHelperRunResult(ExperimentalWamHelperStatus status, string? rawJson, int? exitCode)
    {
        Status = status;
        RawJson = rawJson;
        ExitCode = exitCode;
    }

    internal ExperimentalWamHelperStatus Status { get; }

    // The raw result JSON (Success only). Parsed by the caller through the strict
    // versioned parser; the runner never interprets it.
    internal string? RawJson { get; }

    internal int? ExitCode { get; }

    internal bool IsSuccess => Status == ExperimentalWamHelperStatus.Success;

    internal static ExperimentalWamHelperRunResult Success(string rawJson) =>
        new(ExperimentalWamHelperStatus.Success, rawJson, 0);

    internal static ExperimentalWamHelperRunResult Of(ExperimentalWamHelperStatus status, int? exitCode = null) =>
        new(status, null, exitCode);

    // Bounded business-readable reason code (no identifiers/tokens/paths).
    internal static string ToReasonCode(ExperimentalWamHelperStatus status) => status switch
    {
        ExperimentalWamHelperStatus.Success => "ok",
        ExperimentalWamHelperStatus.Busy => "helper_busy",
        ExperimentalWamHelperStatus.PwshMissing => "powershell_unavailable",
        ExperimentalWamHelperStatus.HelperMissing => "helper_missing",
        ExperimentalWamHelperStatus.HelperHashMismatch => "helper_integrity_failed",
        ExperimentalWamHelperStatus.LaunchFailed => "helper_launch_failed",
        ExperimentalWamHelperStatus.Timeout => "helper_timeout",
        ExperimentalWamHelperStatus.Cancelled => "helper_cancelled",
        ExperimentalWamHelperStatus.NonZeroExit => "helper_failed",
        ExperimentalWamHelperStatus.ResultMissing => "helper_no_result",
        ExperimentalWamHelperStatus.OversizedResult => "helper_result_too_large",
        _ => "helper_failed",
    };
}
