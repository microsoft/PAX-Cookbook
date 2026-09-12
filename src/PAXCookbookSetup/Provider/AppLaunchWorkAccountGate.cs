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
    {
        // Clear any stale result before launching so a previous run cannot be
        // mistaken for this one.
        TryDeleteResult();

        string configPath = Path.Combine(ConfigDir, WamConfigFileName);
        var process = _launcher.Start(_appExePath, new[]
        {
            NativeTestFlag,
            ConfigFlag, configPath,
            ResultFlag, _resultFilePath,
        });

        // A real launch returns a process to await; a recording/test launcher
        // returns null and the result file is expected to be pre-seeded.
        if (process is not null)
        {
            try
            {
                if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        return ReadApprovedResult();
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

    private void TryDeleteResult()
    {
        try
        {
            if (File.Exists(_resultFilePath))
            {
                File.Delete(_resultFilePath);
            }
        }
        catch
        {
            // Best-effort.
        }
    }
}
