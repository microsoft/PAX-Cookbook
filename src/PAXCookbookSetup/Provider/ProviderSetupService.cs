using System;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Provider;

// Setup-side orchestration for choosing the single selected session provider.
//
// Enforces the binding rules for a provider selection made during Setup or Setup
// Repair:
//   - Windows Hello may be selected only when Setup can determine Windows Hello
//     is available on this machine.
//   - The work account may be selected only after its durable configuration is
//     present and verified (ready) AND a single native-WAM authentication test
//     succeeds. Because stable Setup contains no MSAL, the native test is run by
//     the experimental app EXE behind the IWorkAccountSetupGate seam, never in
//     this process.
//   - The provider selection record is written LAST and transactionally; any
//     gate failure writes nothing and preserves the prior selection, and a write
//     fault rolls back to the exact prior record.
//
// The service never chooses a provider on the user's behalf and never silently
// falls back from one provider to the other.
public sealed class ProviderSetupService
{
    private readonly string _localAppDataBase;
    private readonly IHelloSupportProbe _hello;
    private readonly IWorkAccountSetupGate _work;

    public ProviderSetupService(
        string localAppDataBase,
        IHelloSupportProbe helloSupportProbe,
        IWorkAccountSetupGate workAccountGate)
    {
        _localAppDataBase = localAppDataBase ?? throw new ArgumentNullException(nameof(localAppDataBase));
        _hello = helloSupportProbe ?? throw new ArgumentNullException(nameof(helloSupportProbe));
        _work = workAccountGate ?? throw new ArgumentNullException(nameof(workAccountGate));
    }

    public SessionProviderSelection ReadSelection()
        => SessionProviderStore.Load(_localAppDataBase);

    // Selects Windows Hello. Fails closed if Setup cannot confirm Windows Hello
    // support; on success writes selectedProvider=windows_hello last.
    public ProviderSetupResult SelectWindowsHello(ProviderSelectionSource source)
    {
        if (!_hello.IsWindowsHelloAvailable())
        {
            return ProviderSetupResult.Fail(ProviderSetupStatus.HelloUnavailable);
        }

        return CommitLast(SelectedSessionProvider.WindowsHello, source);
    }

    // Selects the work account. Requires ready configuration + a successful
    // native-WAM test; on any gate failure writes nothing (prior selection
    // preserved). On success writes selectedProvider=work_account last.
    public ProviderSetupResult SelectWorkAccount(ProviderSelectionSource source)
    {
        if (!_work.IsWorkAccountReady())
        {
            return ProviderSetupResult.Fail(ProviderSetupStatus.WorkAccountNotReady);
        }

        if (!_work.RunNativeWamAuthTest())
        {
            return ProviderSetupResult.Fail(ProviderSetupStatus.NativeTestFailed);
        }

        return CommitLast(SelectedSessionProvider.WorkAccount, source);
    }

    private ProviderSetupResult CommitLast(SelectedSessionProvider provider, ProviderSelectionSource source)
    {
        var writer = new ProviderSelectionWriter(_localAppDataBase);
        writer.Snapshot();
        try
        {
            writer.WriteSelectionLast(provider, source);
            return ProviderSetupResult.Ok(provider);
        }
        catch
        {
            // Restore the exact prior record; never leave a partial selection.
            try { writer.Restore(); } catch { /* best-effort */ }
            return ProviderSetupResult.Fail(ProviderSetupStatus.Faulted);
        }
    }
}

// Setup's best-effort determination of whether Windows Hello can be used on this
// machine (the actual Hello gesture happens later in the app).
public interface IHelloSupportProbe
{
    bool IsWindowsHelloAvailable();
}

// The work-account prerequisites Setup must satisfy before selecting it. The
// native-WAM test runs in the experimental app EXE (stable Setup has no MSAL).
public interface IWorkAccountSetupGate
{
    // Durable WAM config present AND verification == verified (ready).
    bool IsWorkAccountReady();

    // Runs exactly one native-WAM authentication test; true on success.
    bool RunNativeWamAuthTest();
}

public enum ProviderSetupStatus
{
    Selected,
    HelloUnavailable,
    WorkAccountNotReady,
    NativeTestFailed,
    Faulted,
}

public sealed class ProviderSetupResult
{
    private ProviderSetupResult(ProviderSetupStatus status, SelectedSessionProvider? selected)
    {
        Status = status;
        Selected = selected;
    }

    public ProviderSetupStatus Status { get; }

    public SelectedSessionProvider? Selected { get; }

    public bool Succeeded => Status == ProviderSetupStatus.Selected;

    public static ProviderSetupResult Ok(SelectedSessionProvider provider)
        => new(ProviderSetupStatus.Selected, provider);

    public static ProviderSetupResult Fail(ProviderSetupStatus status)
        => new(status, selected: null);
}
