using System;
using System.IO;
using System.Text.Json;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Provider;

// Real work-account setup gate. Because stable Setup contains no MSAL, the
// native-WAM authentication test is performed by the experimental app EXE, which
// Setup launches with a bounded provider-native-test flag and whose result it
// reads from a small result file. Setup itself never loads MSAL.
//
// Readiness (durable WAM config present + verification recorded) is read from the
// per-user Config folder; the app re-validates authoritatively (fingerprint,
// tenant, scope) when it runs the test, so this check is a fast pre-gate only.
//
// Every dependency is injected so the gate is fully unit-testable without
// spawning a real process: a RecordingProcessLauncher plus a pre-seeded result
// file exercises the exact success/failure branches.
public sealed class AppLaunchWorkAccountGate : IWorkAccountSetupGate
{
    // Bounded flags the app understands; opens the one-shot native-WAM test.
    // These MUST match PAXCookbook.App ProviderNativeTestMode exactly.
    public const string NativeTestFlag = "--provider-native-test";
    public const string ConfigFlag = "--config";
    public const string ResultFlag = "--result";

    private const string WamConfigFileName = "experimental-wam.json";
    private const string WamVerificationFileName = "experimental-wam-verification.json";
    private const string VerifiedOutcome = "verified";

    private readonly string _localAppDataBase;
    private readonly string _appExePath;
    private readonly IProcessLauncher _launcher;
    private readonly string _resultFilePath;
    private readonly TimeSpan _timeout;

    private readonly object _childSync = new();
    private INativeChildProcess? _child;
    private Task? _cleanupAttempt;
    private bool _running;
    private static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(1);

    internal bool HasOutstandingNativeChild { get { lock (_childSync) return _child is not null; } }

    internal async Task<bool> RetryNativeCleanupAsync()
    {
        lock (_childSync)
        {
            if (_running) return false;
        }
        return await CleanupNativeChildAsync().ConfigureAwait(false);
    }

    private async Task<bool> CleanupNativeChildAsync()
    {
        Task attempt;
        lock (_childSync)
        {
            if (_child is null) return true;
            if (_cleanupAttempt is null || _cleanupAttempt.IsCompleted)
            {
                var child = _child;
                _cleanupAttempt = Task.Run(async () =>
                {
                    if (!child.HasExited)
                    {
                        try { child.Kill(); } catch { }
                        await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                });
            }
            attempt = _cleanupAttempt;
        }
        try { await attempt.WaitAsync(CleanupWait).ConfigureAwait(false); }
        catch { return false; }
        lock (_childSync)
        {
            if (_child is null) return true;
            try
            {
                if (!_child.HasExited || !TryDeleteResult()) return false;
                _child.Dispose();
                _child = null;
                _cleanupAttempt = null;
                return true;
            }
            catch { return false; }
        }
    }

    public AppLaunchWorkAccountGate(
        string localAppDataBase,
        string appExePath,
        IProcessLauncher launcher,
        string resultFilePath,
        TimeSpan? timeout = null)
    {
        _localAppDataBase = localAppDataBase ?? throw new ArgumentNullException(nameof(localAppDataBase));
        _appExePath = appExePath ?? throw new ArgumentNullException(nameof(appExePath));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _resultFilePath = resultFilePath ?? throw new ArgumentNullException(nameof(resultFilePath));
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    private string ConfigDir => Path.Combine(_localAppDataBase, "PAXCookbook", "Config");

    public bool IsWorkAccountReady()
    {
        string configPath = Path.Combine(ConfigDir, WamConfigFileName);
        string verificationPath = Path.Combine(ConfigDir, WamVerificationFileName);
        if (!File.Exists(configPath) || !File.Exists(verificationPath))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(verificationPath));
            return doc.RootElement.TryGetProperty("outcome", out JsonElement outcome)
                && outcome.ValueKind == JsonValueKind.String
                && string.Equals(outcome.GetString(), VerifiedOutcome, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public bool RunNativeWamAuthTest()
        => RunNativeWamAuthTestAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task<bool> RunNativeWamAuthTestAsync(CancellationToken cancel)
    {
        lock (_childSync)
        {
            if (_running || _child is not null) return false;
            _running = true;
        }
        try
        {
            if (cancel.IsCancellationRequested || !TryDeleteResult()) return false;
            string configPath = Path.Combine(ConfigDir, WamConfigFileName);
            var arguments = new[]
            {
                NativeTestFlag,
                ConfigFlag, configPath,
                ResultFlag, _resultFilePath,
            };
            var process = _launcher is INativeChildLauncher nativeLauncher
                ? nativeLauncher.StartNativeChild(_appExePath, arguments)
                : _launcher.Start(_appExePath, arguments) is { } started ? new NativeChildProcess(started) : null;
            lock (_childSync) _child = process;
            if (process is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(_timeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                    if (!process.HasExited) throw new InvalidOperationException();
                }
                catch
                {
                    await CleanupNativeChildAsync().ConfigureAwait(false);
                    return false;
                }
                lock (_childSync)
                {
                    process.Dispose();
                    _child = null;
                }
            }
            return !cancel.IsCancellationRequested && ReadApprovedResult();
        }
        finally { lock (_childSync) _running = false; }
    }

    private bool ReadApprovedResult()
    {
        if (!File.Exists(_resultFilePath))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_resultFilePath));
            return doc.RootElement.TryGetProperty("approved", out JsonElement approved)
                && approved.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private bool TryDeleteResult()
    {
        try
        {
            if (File.Exists(_resultFilePath))
            {
                File.Delete(_resultFilePath);
            }
            return !File.Exists(_resultFilePath);
        }
        catch
        {
            return false;
        }
    }
}
