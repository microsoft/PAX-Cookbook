using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Provider;

// Pre-unlock provider repair service. Reads authentication-provider health and
// executes explicit repair actions WITHOUT unlocking the app and WITHOUT
// authenticating through the currently selected (possibly broken) provider.
//
// It drives its available actions from the pure SetupRepairPlanner (no policy
// duplication), validates a target before switching, writes the provider
// selection LAST and transactionally, and never deletes customer tenant
// registrations (that is a separate explicit administrator Deprovision).
public sealed class ProviderRepairService
{
    private const string WamConfigFileName = "experimental-wam.json";
    private const string WamVerificationFileName = "experimental-wam-verification.json";
    private const string VerifiedOutcome = "verified";

    private readonly string _localAppDataBase;
    private readonly IHelloSupportProbe _hello;

    public ProviderRepairService(string localAppDataBase, IHelloSupportProbe helloSupportProbe)
    {
        _localAppDataBase = localAppDataBase ?? throw new ArgumentNullException(nameof(localAppDataBase));
        _hello = helloSupportProbe ?? throw new ArgumentNullException(nameof(helloSupportProbe));
    }

    private string ConfigDir => Path.Combine(_localAppDataBase, "PAXCookbook", "Config");
    private string WamConfigPath => Path.Combine(ConfigDir, WamConfigFileName);
    private string WamVerificationPath => Path.Combine(ConfigDir, WamVerificationFileName);

    public ProviderRepairStatus ReadStatus()
    {
        SessionProviderSelection selection = SessionProviderStore.Load(_localAppDataBase);
        bool configPresent = File.Exists(WamConfigPath);
        bool verified = configPresent && File.Exists(WamVerificationPath) && ReadVerified();
        return new ProviderRepairStatus(selection, _hello.IsWindowsHelloAvailable(), configPresent, verified);
    }

    public IReadOnlyList<ProviderRepairAction> AvailableActions(bool workAccountChannelEnabled, bool tenantAdminAuthorized)
    {
        ProviderRepairStatus s = ReadStatus();
        return SetupRepairPlanner.Plan(new ProviderRepairInputs
        {
            Selection = s.Selection,
            HelloAvailable = s.HelloAvailable,
            WorkAccountChannelEnabled = workAccountChannelEnabled,
            WorkAccountConfigPresent = s.WorkAccountConfigPresent,
            WorkAccountVerified = s.WorkAccountVerified,
            TenantAdminAuthorized = tenantAdminAuthorized,
        });
    }

    // Explicit switch to Windows Hello (never requires the work account). Fails
    // closed when Hello is unavailable; writes the selection last, transactionally.
    public ProviderSetupResult SwitchToWindowsHello()
    {
        if (!_hello.IsWindowsHelloAvailable())
        {
            return ProviderSetupResult.Fail(ProviderSetupStatus.HelloUnavailable);
        }

        var writer = new ProviderSelectionWriter(_localAppDataBase);
        writer.Snapshot();
        try
        {
            writer.WriteSelectionLast(SelectedSessionProvider.WindowsHello, ProviderSelectionSource.SetupRepair);
            return ProviderSetupResult.Ok(SelectedSessionProvider.WindowsHello);
        }
        catch
        {
            try { writer.Restore(); } catch { /* best-effort */ }
            return ProviderSetupResult.Fail(ProviderSetupStatus.Faulted);
        }
    }

    // Explicit switch to the work account via a supplied gate (ready + native
    // test). Never requires Windows Hello. Writes the selection last.
    public ProviderSetupResult SwitchToWorkAccount(IWorkAccountSetupGate gate)
    {
        if (gate is null) { throw new ArgumentNullException(nameof(gate)); }
        if (!gate.IsWorkAccountReady())
        {
            return ProviderSetupResult.Fail(ProviderSetupStatus.WorkAccountNotReady);
        }
        if (!gate.RunNativeWamAuthTest())
        {
            return ProviderSetupResult.Fail(ProviderSetupStatus.NativeTestFailed);
        }

        var writer = new ProviderSelectionWriter(_localAppDataBase);
        writer.Snapshot();
        try
        {
            writer.WriteSelectionLast(SelectedSessionProvider.WorkAccount, ProviderSelectionSource.SetupRepair);
            return ProviderSetupResult.Ok(SelectedSessionProvider.WorkAccount);
        }
        catch
        {
            try { writer.Restore(); } catch { /* best-effort */ }
            return ProviderSetupResult.Fail(ProviderSetupStatus.Faulted);
        }
    }

    // Removes ONLY the local work-account configuration + verification. Never
    // touches cloud objects and never changes the selected provider.
    public bool RemoveLocalWorkConfig()
    {
        bool removed = false;
        try
        {
            if (File.Exists(WamConfigPath)) { File.Delete(WamConfigPath); removed = true; }
            if (File.Exists(WamVerificationPath)) { File.Delete(WamVerificationPath); removed = true; }
            return removed;
        }
        catch
        {
            return removed;
        }
    }

    private bool ReadVerified()
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(WamVerificationPath));
            return doc.RootElement.TryGetProperty("outcome", out JsonElement o)
                && o.ValueKind == JsonValueKind.String
                && string.Equals(o.GetString(), VerifiedOutcome, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ProviderRepairStatus
{
    public ProviderRepairStatus(
        SessionProviderSelection selection,
        bool helloAvailable,
        bool workAccountConfigPresent,
        bool workAccountVerified)
    {
        Selection = selection;
        HelloAvailable = helloAvailable;
        WorkAccountConfigPresent = workAccountConfigPresent;
        WorkAccountVerified = workAccountVerified;
    }

    public SessionProviderSelection Selection { get; }
    public bool HelloAvailable { get; }
    public bool WorkAccountConfigPresent { get; }
    public bool WorkAccountVerified { get; }
}
