// PAX Cookbook - SERVICE-DISABLE COORDINATOR (cycle 63R, Setup only)
//
// WHAT THIS FILE IS. The NON-ELEVATED half of one bounded service-disable
// transaction: it validates the closed initiator grammar, resolves the FIXED
// SIBLING helper, requires the configured signing policy to permit this build,
// captures its own pid and creation FILETIME, mints a fresh rendezvous name,
// launches exactly one process with runas, runs the cycle-59 identity-channel
// client, and observes the helper's exit.
//
// WHAT IT IS NOT. It performs no removal, no SCM call, no ACL work and no
// Program Files or ProgramData access of any kind. Every machine mutation
// happens inside the elevated helper, behind the identity-bound transaction.
//
// IT IS NOT INVOKED THIS CYCLE. The verb is dispatched and the code path is
// complete and callable, but nothing in this cycle calls it, no UAC prompt was
// raised, and no service was stopped or deleted. Ordinary install, update,
// repair, apply-update, uninstall, status, version and help never reach it.
//
// DISABLE IS OWNER-BOUND, AND THIS HALF CANNOT OVERRIDE IT. The initiating
// user's identity is proven by the KERNEL on the live pipe connection inside the
// elevated helper. Nothing here supplies, asserts or influences that identity,
// and the grammar has no override option to offer.
//
// WHY THE SIGNING GATE APPLIES TO DISABLE TOO. Disable launches the SAME
// unsigned prerelease helper elevated. A build whose signing story is not the
// explicit prerelease opt-in must not raise a UAC prompt for it, whichever
// direction the operation runs.
//
// PRIVACY - FAIL CLOSED. The only value this file returns is a bounded outcome
// name. It never logs, and no result can carry a path, an endpoint name, a
// challenge, a SID, a pid, a creation FILETIME, a raw argument, a native error
// code or an exception message.
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using PAXCookbook.ServiceAdminHelper.Signing;

namespace PAXCookbookSetup.Service;

/// <summary>
/// The bounded outcome of one coordinated service-disable attempt. Zero is the
/// permanent, safe default so an uninitialised value can never read as success.
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data.
/// </summary>
public enum ServiceDisableCoordinatorOutcome
{
    Unspecified = 0,

    /// <summary>The elevated transaction completed and the helper exited cleanly.</summary>
    Completed = 1,

    UnsupportedPlatform = 2,

    /// <summary>The initiator grammar did not hold. Exactly the verb, nothing else.</summary>
    UsageRefused = 3,

    /// <summary>The configured signing policy does not permit this operation in this build.</summary>
    SigningPolicyRefused = 4,

    /// <summary>The fixed sibling helper could not be resolved unambiguously.</summary>
    HelperUnavailable = 5,

    /// <summary>This process could not read its own pid + creation FILETIME.</summary>
    InitiatorFactsUnavailable = 6,

    /// <summary>The closed launch specification could not be composed.</summary>
    LaunchSpecUnavailable = 7,

    /// <summary>The administrator approval prompt was declined. No client ever ran.</summary>
    ApprovalDeclined = 8,

    HelperLaunchFailed = 9,

    /// <summary>The helper exited before the transaction completed, cancelling the wait.</summary>
    HelperExitedBeforeTransaction = 10,

    TransactionTimedOut = 11,

    /// <summary>The helper refused, or never acknowledged.</summary>
    AcknowledgementNotReceived = 12,

    /// <summary>
    /// Machine state cannot be proven closed, or the installation belongs to a
    /// different owner. It is NEVER repaired automatically and NEVER overridden;
    /// an attended, elevated recovery operation is the only remedy.
    /// </summary>
    RecoveryRequired = 13,

    /// <summary>The helper did not exit inside its bounded observation window.</summary>
    HelperDidNotExit = 14,

    /// <summary>The transaction acknowledged but the helper reported a nonzero exit.</summary>
    HelperReportedFailure = 15,
}

/// <summary>
/// THE DEDICATED NON-ELEVATED SERVICE-DISABLE COORDINATOR. One grammar, one
/// helper, one transaction, one bounded outcome.
/// </summary>
internal static class ServiceDisableCoordinator
{
    /// <summary>
    /// The rendezvous budget, measured only AFTER the helper started. It matches
    /// enable's because the elevated disable transaction performs comparable
    /// bounded waits (stop, SCM absence, footprint verification), and it must
    /// stay STRICTLY SHORTER than the helper's own self-timeout.
    /// </summary>
    internal static readonly TimeSpan DefaultChannelTimeout = ServiceEnableCoordinator.DefaultChannelTimeout;

    /// <summary>How long the helper's own exit is observed after the transaction.</summary>
    internal static readonly TimeSpan DefaultHelperExitTimeout =
        ServiceEnableCoordinator.DefaultHelperExitTimeout;

    /// <summary>Granularity of the interleaved transaction / helper-exit wait.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>The fixed production entry point. It takes no path and no identity.</summary>
    internal static ServiceDisableCoordinatorOutcome Run(string[]? argv) =>
        Run(
            argv,
            new RealServiceEnableInitiatorFacts(),
            new RealServiceEnableHelperLocator(),
            new RealServiceEnableSigningGate(),
            new RealServiceAnchorElevatedLauncher(),
            new RealServiceAnchorChannelClient(),
            DefaultChannelTimeout,
            DefaultHelperExitTimeout);

    internal static ServiceDisableCoordinatorOutcome Run(
        string[]? argv,
        IServiceEnableInitiatorFacts facts,
        IServiceEnableHelperLocator locator,
        IServiceEnableSigningGate signing,
        IServiceAnchorElevatedLauncher launcher,
        IServiceAnchorChannelClient client,
        TimeSpan channelTimeout,
        TimeSpan helperExitTimeout)
    {
        if (facts is null || locator is null || signing is null || launcher is null || client is null)
        {
            return ServiceDisableCoordinatorOutcome.UnsupportedPlatform;
        }

        // 1. THE CLOSED INITIATOR GRAMMAR, first and cheapest.
        if (!ServiceDisableProtocol.TryParseInitiator(argv))
        {
            return ServiceDisableCoordinatorOutcome.UsageRefused;
        }

        if (!OperatingSystem.IsWindows())
        {
            return ServiceDisableCoordinatorOutcome.UnsupportedPlatform;
        }

        // 2. THE SIGNING GATE, BEFORE anything is resolved or launched.
        if (!ServiceEnableCoordinator.PolicyPermitsEnablement(signing.ResolveConfiguredPolicy()))
        {
            return ServiceDisableCoordinatorOutcome.SigningPolicyRefused;
        }

        // 3. THE FIXED SIBLING HELPER. No caller path, ever.
        ServiceAdminHelperLocationResult located = locator.ResolveSiblingHelper();
        if (located.Outcome != ServiceAdminHelperLocationOutcome.Resolved
            || string.IsNullOrEmpty(located.ResolvedPath))
        {
            return ServiceDisableCoordinatorOutcome.HelperUnavailable;
        }

        ServiceInitiatorProcessFacts initiator = facts.CaptureInitiator();
        if (!initiator.IsPresent)
        {
            return ServiceDisableCoordinatorOutcome.InitiatorFactsUnavailable;
        }

        string endpointName = ServiceInitiatingUserIdentityChannelContract.NewEndpointName();

        ServiceAnchorElevationLaunchSpec? spec =
            TryComposeLaunchSpec(located.ResolvedPath!, endpointName, initiator);
        if (spec is null)
        {
            return ServiceDisableCoordinatorOutcome.LaunchSpecUnavailable;
        }

        // NO CHANNEL CLOCK IS RUNNING YET. Human UAC response time is spent
        // entirely inside this call and cannot consume the rendezvous budget.
        ServiceAnchorElevatedLaunch launch = launcher.LaunchElevated(spec);
        if (launch.State == ServiceAnchorElevatedLaunchState.Declined)
        {
            return ServiceDisableCoordinatorOutcome.ApprovalDeclined;
        }
        if (launch.State != ServiceAnchorElevatedLaunchState.Started || launch.Process is null)
        {
            return ServiceDisableCoordinatorOutcome.HelperLaunchFailed;
        }

        using IServiceAnchorElevatedProcess helper = launch.Process;
        return RunTransaction(helper, client, endpointName, channelTimeout, helperExitTimeout);
    }

    /// <summary>
    /// The closed launch specification: the fixed sibling helper is launched
    /// DIRECTLY, so there is no host image and no assembly path argument.
    /// </summary>
    internal static ServiceAnchorElevationLaunchSpec? TryComposeLaunchSpec(
        string helperPath, string endpointName, ServiceInitiatorProcessFacts initiator)
    {
        if (string.IsNullOrEmpty(helperPath)
            || !initiator.IsPresent
            || !ServiceInitiatingUserIdentityChannelContract.IsCanonicalEndpointName(endpointName))
        {
            return null;
        }

        string[] arguments = ServiceDisableProtocol.ComposeElevatedArguments(endpointName, initiator);
        return arguments.Length == ServiceDisableVerbs.ElevatedTokenCount
            ? new ServiceAnchorElevationLaunchSpec(helperPath, arguments)
            : null;
    }

    private static ServiceDisableCoordinatorOutcome RunTransaction(
        IServiceAnchorElevatedProcess helper,
        IServiceAnchorChannelClient client,
        string endpointName,
        TimeSpan channelTimeout,
        TimeSpan helperExitTimeout)
    {
        Task<ServiceInitiatingUserIdentityChannelOutcome> transaction;
        try
        {
            transaction = Task.Run(() => client.Request(endpointName, channelTimeout));
        }
        catch (Exception)
        {
            return ServiceDisableCoordinatorOutcome.AcknowledgementNotReceived;
        }

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            bool completed;
            try
            {
                completed = transaction.Wait(PollInterval);
            }
            catch (Exception)
            {
                return ServiceDisableCoordinatorOutcome.AcknowledgementNotReceived;
            }

            if (completed)
            {
                break;
            }
            if (helper.HasExited)
            {
                return ServiceDisableCoordinatorOutcome.HelperExitedBeforeTransaction;
            }
            if (elapsed.Elapsed >= channelTimeout)
            {
                return ServiceDisableCoordinatorOutcome.TransactionTimedOut;
            }
        }

        ServiceInitiatingUserIdentityChannelOutcome outcome;
        try
        {
            outcome = transaction.Result;
        }
        catch (Exception)
        {
            return ServiceDisableCoordinatorOutcome.AcknowledgementNotReceived;
        }

        // An owner conflict and an unprovable closure are BOTH preserved as
        // recovery-required rather than flattened into a plain refusal: the
        // remedy is attended recovery, never a retry and never an override.
        if (outcome is ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict
            or ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired)
        {
            return ServiceDisableCoordinatorOutcome.RecoveryRequired;
        }

        if (outcome != ServiceInitiatingUserIdentityChannelOutcome.Completed)
        {
            return ServiceDisableCoordinatorOutcome.AcknowledgementNotReceived;
        }

        if (!helper.WaitForExit(helperExitTimeout))
        {
            return ServiceDisableCoordinatorOutcome.HelperDidNotExit;
        }

        return helper.ExitCode == PAXCookbook.Shared.ExitCodes.SetupExitCodes.Ok
            ? ServiceDisableCoordinatorOutcome.Completed
            : ServiceDisableCoordinatorOutcome.HelperReportedFailure;
    }
}
