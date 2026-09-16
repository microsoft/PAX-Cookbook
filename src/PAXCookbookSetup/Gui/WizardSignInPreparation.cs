using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Provider;

namespace PAXCookbookSetup.Gui;

public sealed class WizardSignInPreparation : IDisposable
{
    public const string PreparingMessage = "Getting PAX Cookbook ready\u2026";
    public const string PreparationFailureMessage = "PAX Cookbook could not be prepared. Try again or choose Windows Hello.";
    public const string RuntimeFailureMessage = "Required software could not be installed. Try again or choose Windows Hello.";
    public const string NativeFailureMessage = "The sign-in test did not succeed. You can retry or choose Windows Hello.";
    private readonly SignInMethodController _signIn;
    private readonly Func<bool, CancellationToken, Task<WizardPreparationResult>> _prepare;
    private readonly Func<WizardInstallRunner.PreparedPayload?, bool, CancellationToken, Task<PrerequisiteCoordinatorResult>> _prerequisites;
    private readonly Func<WizardInstallRunner.PreparedPayload, CancellationToken, Task<WizardInstallResult>> _install;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancel;
    private WizardInstallRunner.PreparedPayload? _payload;
    private WizardInstallRunner.PayloadCleanupLease? _cleanupPending;
    private WizardInstallRunner.PayloadCleanupLease? _prerequisiteCleanup;
    private bool _disposed;
    private Task _activeOperation = Task.CompletedTask;
    private Task? _stopTask;
    private bool _cleanupRunning;
    private bool _cleanupFailed;

    public WizardSignInPreparation(SignInMethodController signIn,
        Func<bool, CancellationToken, Task<WizardPreparationResult>> prepare,
        Func<WizardInstallRunner.PreparedPayload?, bool, CancellationToken, Task<PrerequisiteCoordinatorResult>> prerequisites,
        Func<WizardInstallRunner.PreparedPayload, CancellationToken, Task<WizardInstallResult>> install)
    {
        _signIn = signIn;
        _prepare = prepare;
        _prerequisites = prerequisites;
        _install = install;
    }

    public event Action? StateChanged;
    public string Status { get; private set; } = string.Empty;
    public bool IsBusy { get { lock (_sync) return _cancel is not null || _cleanupRunning; } }
    public bool HasOutstandingNativeChild => _signIn.HasOutstandingNativeChild;
    public bool CanReleaseStaging => !IsBusy && !HasOutstandingNativeChild && !_cleanupFailed;
    public bool CanRetryCleanup => !IsBusy && (HasOutstandingNativeChild || _cleanupFailed || RequiresFreshPreparation);
    public bool CanImport => !_disposed && CanReleaseStaging && _signIn.Choice == SignInProviderChoice.WorkAccount;
    public bool CanTest => CanImport && _signIn.WorkConfigStaged && _signIn.WorkVerified;
    public bool CanContinue => !_disposed && CanReleaseStaging && _signIn.CanContinue();
    public bool RequiresFreshPreparation { get; private set; }
    public IReadOnlyList<NamedPrerequisiteResult> PrerequisiteResults { get; private set; } = Array.Empty<NamedPrerequisiteResult>();

    public string ResolveNativeAppPath()
    {
        lock (_sync)
        {
            if (_disposed || _cancel?.IsCancellationRequested == true)
                throw new OperationCanceledException();
            return (_payload ?? throw new InvalidOperationException("Payload is not prepared.")).GetValidatedAppExePath();
        }
    }

    public void ChooseWindowsHello()
    {
        _signIn.ChooseWindowsHello();
        Cancel();
    }

    public void Cancel()
    {
        _signIn.InvalidateNativeTest();
        lock (_sync) _cancel?.Cancel();
        StateChanged?.Invoke();
    }

    public Task<bool> TestAsync(string resultPath)
    {
        lock (_sync)
        {
            if (!CanTest) return Task.FromResult(false);
            var operation = RunTestAsync(resultPath);
            _activeOperation = operation;
            return operation;
        }
    }

    private async Task<bool> RunTestAsync(string resultPath)
    {
        if (!CanTest) return false;
        using var operation = Begin();
        if (operation is null) return false;
        _signIn.InvalidateNativeTest();
        SetStatus(PreparingMessage);
        try
        {
            return await Task.Run(async () =>
            {
                if (_payload?.IsInvalidated == true)
                {
                    _payload.Dispose();
                    _payload = null;
                }
                if (!await EnsurePrepared(true, operation.Token).ConfigureAwait(false))
                {
                    SetStatus(PreparationFailureMessage);
                    return false;
                }
                var prerequisites = await _prerequisites(_payload, true, operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                if (prerequisites.IsCancelled)
                {
                    SetStatus(RuntimeFailureMessage);
                    return false;
                }
                SetStatus("Complete the Windows work-account sign-in in the window that appears\u2026");
                bool approved = await _signIn.RunNativeTestAsync(resultPath, operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                SetStatus(approved ? "Work-account sign-in test succeeded. Click Install to finish." : NativeFailureMessage);
                return approved;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _signIn.InvalidateNativeTest();
            SetStatus(NativeFailureMessage);
            return false;
        }
        catch
        {
            _signIn.InvalidateNativeTest();
            RequiresFreshPreparation = _payload?.IsInvalidated == true;
            SetStatus(RequiresFreshPreparation ? PreparationFailureMessage : NativeFailureMessage);
            return false;
        }
        finally { End(); }
    }

    public Task<WizardInstallResult> InstallAsync()
    {
        lock (_sync)
        {
            if (!CanContinue) return Task.FromResult(Failure("Choose an available sign-in method before installing."));
            var operation = RunInstallAsync();
            _activeOperation = operation;
            return operation;
        }
    }

    private async Task<WizardInstallResult> RunInstallAsync()
    {
        if (!CanContinue) return Failure("Choose an available sign-in method before installing.");
        using var operation = Begin();
        if (operation is null) return Failure("Setup is already working.");
        try
        {
            return await Task.Run(async () =>
            {
                if (_payload is not null && !_payload.VerifyCurrentArtifact()) return ChangedArtifact();
                var prerequisites = await _prerequisites(_payload, false, operation.Token).ConfigureAwait(false);
                PrerequisiteResults = prerequisites.Results;
                operation.Token.ThrowIfCancellationRequested();
                if (prerequisites.IsCancelled) return Failure(RuntimeFailureMessage);
                if (!await EnsurePrepared(false, operation.Token).ConfigureAwait(false))
                    return Failure(PreparationFailureMessage);
                var result = await _install(_payload!, operation.Token).ConfigureAwait(false);
                if (_payload!.IsInvalidated) return ChangedArtifact();
                return result;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Failure("Installation was cancelled."); }
        catch { return Failure("Setup could not finish. Run Setup again."); }
        finally { End(); }
    }

    private WizardInstallResult ChangedArtifact()
    {
        _signIn.InvalidateNativeTest();
        RequiresFreshPreparation = true;
        SetStatus(PreparationFailureMessage);
        return Failure(PreparationFailureMessage);
    }

    private async Task<bool> EnsurePrepared(bool early, CancellationToken cancel)
    {
        if (_payload is not null) return !early || _payload.PreparedForEarlyExecution;
        var result = await _prepare(early, cancel).ConfigureAwait(false);
        var returned = result.Payload;
        try
        {
            lock (_sync)
            {
                _cleanupPending = result.Cleanup ?? returned?.Cleanup;
                cancel.ThrowIfCancellationRequested();
                if (_disposed) throw new OperationCanceledException(cancel);
                _payload = returned;
                returned = null;
                if (_payload is not null)
                {
                    _cleanupPending = null;
                    RequiresFreshPreparation = false;
                }
            }
            return result.Success;
        }
        finally
        {
            lock (_sync)
            {
                try { returned?.Dispose(); }
                finally
                {
                    _cleanupFailed = _cleanupPending?.HasPendingCleanup == true;
                    if (!_cleanupFailed) _cleanupPending = null;
                }
            }
        }
    }

    private CancellationTokenSource? Begin()
    {
        lock (_sync)
        {
            if (_disposed || _cancel is not null) return null;
            _cancel = new CancellationTokenSource();
        }
        StateChanged?.Invoke();
        return _cancel;
    }

    private void End()
    {
        try
        {
            lock (_sync)
            {
                _cancel = null;
                if (HasOutstandingNativeChild)
                    Status = "Close the sign-in window, then choose Retry.";
                else if (_disposed) ReleasePayloads();
            }
        }
        finally { StateChanged?.Invoke(); }
    }

    internal PrerequisiteCoordinatorResult RunPrerequisites(Func<string, PrerequisiteCoordinatorResult> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "PAXSetup_" + Guid.NewGuid().ToString("N"));
        var cleanup = new WizardInstallRunner.PayloadCleanupLease(root);
        lock (_sync)
        {
            if (_disposed || _cancel is null || _prerequisiteCleanup is not null)
                throw new InvalidOperationException("Prerequisite preparation is not available.");
            _prerequisiteCleanup = cleanup;
        }
        try { return run(root); }
        finally
        {
            bool cleaned = cleanup.TryCleanup();
            lock (_sync)
            {
                if (cleaned) _prerequisiteCleanup = null;
                else _cleanupFailed = true;
            }
            if (!cleaned) throw new IOException("Prerequisite cleanup is incomplete.");
        }
    }

    private void ReleasePayloads()
    {
        try
        {
            _payload?.Dispose();
            _payload = null;
            _cleanupPending?.Dispose();
            _cleanupPending = null;
            _prerequisiteCleanup?.Dispose();
            _prerequisiteCleanup = null;
            _cleanupFailed = false;
        }
        catch { _cleanupFailed = true; throw; }
    }

    public async Task<bool> RetryCleanupAsync()
    {
        lock (_sync)
        {
            if (IsBusy) return false;
            _cleanupRunning = true;
        }
        try
        {
            if (!await _signIn.RetryNativeCleanupAsync().ConfigureAwait(false)) return false;
            bool resetPreparation = await Task.Run(() =>
            {
                lock (_sync)
                {
                    bool reset = RequiresFreshPreparation;
                    if (_disposed || _cleanupFailed || reset)
                    {
                        ReleasePayloads();
                        RequiresFreshPreparation = false;
                    }
                    return reset;
                }
            }).ConfigureAwait(false);
            SetStatus(resetPreparation ? "Choose Install to prepare fresh setup files." : "Sign-in stopped.");
            return true;
        }
        catch { return false; }
        finally
        {
            lock (_sync) _cleanupRunning = false;
            StateChanged?.Invoke();
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
        StateChanged?.Invoke();
    }

    private static WizardInstallResult Failure(string message) => new(false, SetupExitCodes.InstallFailed, message);

    public Task StopAsync()
    {
        lock (_sync)
        {
            if (_stopTask is null || _stopTask.IsFaulted || _stopTask.IsCanceled)
                _stopTask = StopCoreAsync();
            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        try { Dispose(); }
        catch when (_cleanupFailed) { }
        try { await _activeOperation.ConfigureAwait(false); } catch { }
        if (!await RetryCleanupAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Sign-in cleanup is incomplete.");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _signIn.InvalidateNativeTest();
            _cancel?.Cancel();
            if (_cancel is null && !_cleanupRunning && !HasOutstandingNativeChild) ReleasePayloads();
        }
    }
}