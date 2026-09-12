using System.Collections.Generic;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Provider;

// Pure planner for the pre-unlock Setup Repair experience.
//
// Given the observed provider/config/verification/OS health, it enumerates the
// repair actions Setup may OFFER. It never selects or switches a provider on the
// user's behalf: every returned action is an explicit choice the operator must
// confirm, and switching to a target still requires that target to validate at
// execution time. Repairing one provider never requires authenticating through
// the other, and never requires unlocking the app.
public static class SetupRepairPlanner
{
    public static IReadOnlyList<ProviderRepairAction> Plan(ProviderRepairInputs inputs)
    {
        var actions = new List<ProviderRepairAction>();
        bool recovery = inputs.Selection.RecoveryRequired;
        SelectedSessionProvider current = inputs.Selection.Provider;

        // Repairing the current provider is always available (it also drives
        // recovery of a malformed selection record).
        actions.Add(ProviderRepairAction.RepairCurrentProvider);

        // Explicit switch to Windows Hello — offered whenever Setup can confirm
        // Hello is available and Hello is not already the (valid) selection.
        if (inputs.HelloAvailable && (recovery || current != SelectedSessionProvider.WindowsHello))
        {
            actions.Add(ProviderRepairAction.SwitchToWindowsHello);
        }

        // Explicit switch to the work account — offered only on the pilot channel
        // and only when the work account is not already the (valid) selection.
        // Provisioning/verify/native-test happen within the switch flow.
        if (inputs.WorkAccountChannelEnabled && (recovery || current != SelectedSessionProvider.WorkAccount))
        {
            actions.Add(ProviderRepairAction.SwitchToWorkAccount);
        }

        // Removing broken local work-account configuration is offered only when
        // such local configuration actually exists. It never touches cloud
        // objects.
        if (inputs.WorkAccountChannelEnabled && inputs.WorkAccountConfigPresent)
        {
            actions.Add(ProviderRepairAction.RemoveBrokenWorkAccountConfig);
        }

        // Verify / deprovision the customer tenant setup — offered only when
        // there is local configuration to act on AND the operator is authorized
        // (a tenant administrator with an Azure CLI session).
        if (inputs.WorkAccountChannelEnabled && inputs.WorkAccountConfigPresent && inputs.TenantAdminAuthorized)
        {
            actions.Add(ProviderRepairAction.VerifyOrDeprovisionTenant);
        }

        return actions;
    }
}

// Observed authentication-provider health that Setup Repair reads before
// offering actions. All fields are bounded/non-secret.
public sealed class ProviderRepairInputs
{
    public required SessionProviderSelection Selection { get; init; }

    // Whether Setup can confirm Windows Hello is available on this machine.
    public bool HelloAvailable { get; init; }

    // Whether this Setup channel exposes the work-account provider at all
    // (stable is Hello-only until the work-account release gates are approved).
    public bool WorkAccountChannelEnabled { get; init; }

    // Whether durable local work-account configuration exists.
    public bool WorkAccountConfigPresent { get; init; }

    // Whether the local work-account configuration is verified (ready).
    public bool WorkAccountVerified { get; init; }

    // Whether the operator has an authorized tenant-admin Azure CLI session.
    public bool TenantAdminAuthorized { get; init; }
}

public enum ProviderRepairAction
{
    RepairCurrentProvider,
    SwitchToWindowsHello,
    SwitchToWorkAccount,
    RemoveBrokenWorkAccountConfig,
    VerifyOrDeprovisionTenant,
}
