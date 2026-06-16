namespace PAXCookbook.Broker.Native.Services;

// Stage 3h -- service bundle wired into ScheduledTaskRoutes.Register
// to activate live PUT/DELETE behavior in the native broker. When
// the bundle is null the route preserves the Stage 3g controlled-501
// fallback so existing Stage 3g fixtures keep passing unchanged.
//
// Construction:
//   * Production:    NativeBrokerHost composes the bundle from
//                    NativeBrokerHostOptions.AppRoot + workspacePaths,
//                    using WindowsReAuthSidecarVerifier +
//                    RecipeProjectionHashSidecarComposer +
//                    WindowsCredentialSecretStore +
//                    WindowsScheduledTaskRegistrar.
//   * Tests:         Stage 3h fixture builds the bundle with fakes
//                    for all four service interfaces.
//
// Doctrine:
//   * The bundle carries ALL ambient state the route needs
//     (PaxScriptPath, PaxScriptVersion, WorkspacePath,
//     ScheduledTaskFolderPath, ExecutionMode, RegisteredByUser).
//     The route handler does NO direct filesystem or environment
//     lookups, so tests are deterministic.
//   * ScheduledTaskFolderPath defaults to @"\PAX Cookbook\" to match
//     $Script:ScheduledTaskFolderPath in the PS broker.
//   * ExecutionMode defaults to "local-scheduled" to match
//     $Script:ScheduledTaskExecutionMode -- the projection hash is
//     computed under this mode at PUT time so the wrapper's fire-
//     time recompute produces the same value.
//   * LockService is optional; when present the route bumps activity
//     after a Verified verdict (parity with Update-BrokerLockActivity
//     inside Invoke-BrokerLockReAuthForOp).
public sealed class Stage3hServiceBundle
{
    public required IWindowsReAuthVerifier ReAuth { get; init; }
    public required IRecipeProjectionHashComposer HashComposer { get; init; }
    public required ICredentialSecretStore CredStore { get; init; }
    public required IScheduledTaskRegistrar Registrar { get; init; }
    public required RecipeFileReader RecipeReader { get; init; }
    public required string PaxScriptPath { get; init; }
    public required string PaxScriptVersion { get; init; }
    public required string WorkspacePath { get; init; }
    public string ScheduledTaskFolderPath { get; init; } = @"\PAX Cookbook\";
    public string ExecutionMode { get; init; } = "local-scheduled";
    public string RegisteredByUser { get; init; } =
        Environment.UserDomainName + "\\" + Environment.UserName;
    public BrokerLockService? LockService { get; init; }
}
