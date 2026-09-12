using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Provider;

// Pure provider-selection lifecycle policy for fresh install, update, repair,
// reinstall, and uninstall. These are deterministic decision functions with no
// side effects; the Setup verbs apply them.
//
// Binding rules encoded here:
//   - Fresh install writes the selection only after a target-provider success
//     (handled by ProviderSetupService); with no record present the Setup
//     selection step runs.
//   - Update / reinstall PRESERVE the existing selection and WAM configuration,
//     never re-run consent or provider testing automatically, and NEVER reset to
//     Windows Hello. A malformed record routes to Setup Repair (recovery),
//     never a silent default.
//   - Uninstall follows the existing workspace/config preservation doctrine and
//     NEVER deletes customer tenant registrations (a distinct explicit admin
//     Deprovision action).
public static class ProviderLifecyclePolicy
{
    // Whether uninstall removes the LOCAL provider selection record. Standard
    // uninstall preserves local config (like Workspace/Logs); Full uninstall
    // removes per-user local data including this record. Neither ever deletes a
    // customer tenant registration.
    public static bool RemovesProviderRecordOnUninstall(UninstallMode mode)
        => mode == UninstallMode.Full;

    // Uninstall NEVER deletes customer tenant registrations, in any mode.
    public const bool UninstallDeletesTenantRegistrations = false;

    // Update/reinstall never re-runs consent or the native-WAM test.
    public const bool UpdateRerunsConsentOrNativeTest = false;

    // Update/reinstall never resets the selected provider to Windows Hello.
    public const bool UpdateResetsToWindowsHello = false;

    // Decision for a fresh install (a brand-new install root with no record).
    public static ProviderLifecycleDecision ForFreshInstall(bool recordPresent)
        => recordPresent ? ProviderLifecycleDecision.PreserveExisting
                         : ProviderLifecycleDecision.FreshSelectionRequired;

    // Decision for update / repair / reinstall over an existing install. The
    // existing selection is preserved unless the record is malformed (recovery).
    // A missing record is treated as the historic Windows-Hello migration by the
    // store and is preserved as-is (never a forced reset).
    public static ProviderLifecycleDecision ForUpgradeOrReinstall(SessionProviderSelection existing)
        => existing.RecoveryRequired
            ? ProviderLifecycleDecision.RecoveryRequired
            : ProviderLifecycleDecision.PreserveExisting;
}

public enum UninstallMode
{
    Standard,
    Full,
}

public enum ProviderLifecycleDecision
{
    PreserveExisting,
    RecoveryRequired,
    FreshSelectionRequired,
}
