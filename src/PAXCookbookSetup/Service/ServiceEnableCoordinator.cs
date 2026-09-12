// PAX Cookbook - SERVICE-ENABLE COORDINATOR (cycle 62, Setup only)
//
// WHAT THIS FILE IS. The NON-ELEVATED half of one bounded service-enablement
// transaction: it validates the closed initiator grammar, resolves the FIXED
// SIBLING helper, requires the configured signing policy to permit this build,
// captures its own pid and creation FILETIME, mints a fresh rendezvous name,
// launches exactly one process with runas, runs the cycle-59 identity-channel
// client, and observes the helper's exit.
//
// WHAT IT IS NOT. It performs no extraction, no SCM call, no ACL work and no
// Program Files or ProgramData access of any kind. Every machine mutation
// happens inside the elevated helper, behind the identity-bound transaction.
//
// IT IS NOT INVOKED THIS CYCLE. The verb is dispatched and the code path is
// complete and callable, but nothing in this cycle calls it, no UAC prompt was
// raised, and no service was created. Ordinary install, update, repair,
// apply-update, uninstall, status, version and help never reach it.
//
// WHY THIS IS NOT CYCLE 59's COORDINATOR. Cycle 59 relaunches THIS Setup image
// elevated. This coordinator launches a DIFFERENT file - the fixed sibling
// self-contained helper PE - and only from the installed framework-dependent
// dotnet-hosted Setup shape. It accepts no caller-provided path.
//
// TRUST - STATE IT PLAINLY. Resolving and launching the sibling helper proves
// NOTHING about the helper's trustworthiness. The installed Setup directory is
// under %LOCALAPPDATA% and is USER-WRITABLE, so the helper is exactly as
// trustworthy as its outer Authenticode signature - which, in a prerelease
// build, does not exist. Enablement is therefore permitted ONLY under the
// explicit PrereleaseUnsignedHelper compile-time opt-in; a default or GA build
// resolves SigningPolicyUnavailable and refuses BEFORE any elevation request.
//
// THE HUMAN MUST NOT CONSUME THE PIPE BUDGET. No channel timeout starts until
// Process.Start has RETURNED successfully, and a DECLINED prompt returns
// immediately without ever starting a client.
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
/// The bounded outcome of one coordinated service-enable attempt. Zero is the
/// permanent, safe default so an uninitialised value can never read as success.
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data.
/// </summary>
public enum ServiceEnableCoordinatorOutcome
{
    Unspecified = 0,

    /// <summary>
    /// The elevated transaction completed and the helper exited cleanly. It
    /// means the payload is installed and the service is REGISTERED - it does
    /// NOT mean the service has started, and nothing here starts it.
    /// </summary>
    Completed = 1,

    UnsupportedPlatform = 2,

    /// <summary>The initiator grammar did not hold. Exactly the verb, nothing else.</summary>
    UsageRefused = 3,

    /// <summary>The configured signing policy does not permit enablement in this build.</summary>
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
    /// Machine state cannot be proven closed, or existing state conflicts. It is
    /// NEVER repaired automatically; an attended, elevated recovery operation is
    /// the only remedy.
    /// </summary>
    RecoveryRequired = 13,

    /// <summary>The helper did not exit inside its bounded observation window.</summary>
    HelperDidNotExit = 14,

    /// <summary>The transaction acknowledged but the helper reported a nonzero exit.</summary>
    HelperReportedFailure = 15,
}

/// <summary>
/// CYCLE 67. The bounded outcome PLUS the two-axis classification the bare
/// outcome enum cannot express. It is the ONLY thing Setup needs in order to
/// emit its single bounded diagnostic line, and it carries no path, endpoint,
/// challenge, SID, pid, native status or exception.
/// </summary>
internal readonly struct ServiceEnableCoordinatorResult
{
    internal ServiceEnableCoordinatorResult(
        ServiceEnableCoordinatorOutcome outcome, ServiceEnableFailureClassification classification)
    {
        Outcome = outcome;
        Classification = classification;
    }

    internal ServiceEnableCoordinatorOutcome Outcome { get; }

    internal ServiceEnableFailureClassification Classification { get; }

    internal ServiceEnableFailureCause Cause => Classification.Cause;

    internal ServiceEnableFailureDisposition Disposition => Classification.Disposition;

    /// <summary>Carries the bounded type name only.</summary>
    public override string ToString() => nameof(ServiceEnableCoordinatorResult);
}

/// <summary>
/// The fixed sibling helper location, behind an interface ONLY so the focused
/// tests can prove the coordinator refuses a missing or ambiguous helper without
/// an installed product. Production always uses
/// <see cref="RealServiceEnableHelperLocator"/>, which accepts no path.
/// </summary>
internal interface IServiceEnableHelperLocator
{
    ServiceAdminHelperLocationResult ResolveSiblingHelper();
}

internal sealed class RealServiceEnableHelperLocator : IServiceEnableHelperLocator
{
    public ServiceAdminHelperLocationResult ResolveSiblingHelper() =>
        ServiceAdminHelperLocationResolver.TryResolveSiblingHelper();
}

/// <summary>
/// The configured signing policy, behind an interface ONLY so the focused tests
/// can prove BOTH the default refusal and the prerelease allowance from a single
/// build. Production always uses <see cref="RealServiceEnableSigningGate"/>,
/// which takes no argument and reads compile-time facts alone.
/// </summary>
internal interface IServiceEnableSigningGate
{
    ServiceHelperSigningPolicyState ResolveConfiguredPolicy();
}

internal sealed class RealServiceEnableSigningGate : IServiceEnableSigningGate
{
    public ServiceHelperSigningPolicyState ResolveConfiguredPolicy() =>
        ServiceHelperSigningPolicy.ResolveConfiguredPolicy();
}

/// <summary>
/// This process's own pid + creation FILETIME, behind an interface ONLY so the
/// focused tests can prove the exact values that cross the command line.
/// </summary>
internal interface IServiceEnableInitiatorFacts
{
    ServiceInitiatorProcessFacts CaptureInitiator();
}

internal sealed class RealServiceEnableInitiatorFacts : IServiceEnableInitiatorFacts
{
    public ServiceInitiatorProcessFacts CaptureInitiator() => ServiceInitiatorProcessBinding.CaptureCurrent();
}

/// <summary>
/// THE DEDICATED NON-ELEVATED SERVICE-ENABLE COORDINATOR. One grammar, one
/// helper, one transaction, one bounded outcome.
/// </summary>
internal static class ServiceEnableCoordinator
{
    /// <summary>
    /// How long the rendezvous may take, measured only AFTER the helper started.
    /// CYCLE 63R raises it to 180 seconds: the elevated transaction now starts
    /// the service and proves startability inside this window. It must remain
    /// STRICTLY SHORTER than ServiceEnableVerbs.HelperSelfTimeout (240 s) so the
    /// initiator gives up first and no elevated helper is left orphaned.
    /// </summary>
    internal static readonly TimeSpan DefaultChannelTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// How long the helper's own exit is observed after the transaction. It is
    /// deliberately UNCHANGED at 60 seconds: it measures process teardown, not
    /// transaction work.
    /// </summary>
    internal static readonly TimeSpan DefaultHelperExitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Granularity of the interleaved transaction / helper-exit wait.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The ONLY signing-policy state that permits enablement. GA states are
    /// deliberately absent: a build whose signing story is not the explicit
    /// prerelease opt-in refuses before it ever asks for elevation.
    /// </summary>
    internal static bool PolicyPermitsEnablement(ServiceHelperSigningPolicyState policy) =>
        policy == ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed;

    /// <summary>The fixed production entry point. It takes no path and no identity.</summary>
    internal static ServiceEnableCoordinatorOutcome Run(string[]? argv) => RunDetailed(argv).Outcome;

    /// <summary>
    /// CYCLE 67. The fixed production entry point that also reports the bounded
    /// cause and disposition, so Setup can emit its one diagnostic line.
    /// </summary>
    internal static ServiceEnableCoordinatorResult RunDetailed(string[]? argv) =>
        RunDetailed(
            argv,
            new RealServiceEnableInitiatorFacts(),
            new RealServiceEnableHelperLocator(),
            new RealServiceEnableSigningGate(),
            new RealServiceAnchorElevatedLauncher(),
            new RealServiceAnchorChannelClient(),
            DefaultChannelTimeout,
            DefaultHelperExitTimeout);

    internal static ServiceEnableCoordinatorOutcome Run(
        string[]? argv,
        IServiceEnableInitiatorFacts facts,
        IServiceEnableHelperLocator locator,
        IServiceEnableSigningGate signing,
        IServiceAnchorElevatedLauncher launcher,
        IServiceAnchorChannelClient client,
        TimeSpan channelTimeout,
        TimeSpan helperExitTimeout) =>
        RunDetailed(argv, facts, locator, signing, launcher, client, channelTimeout, helperExitTimeout)
            .Outcome;

    internal static ServiceEnableCoordinatorResult RunDetailed(
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
            return RefusedBeforeMutation(
                ServiceEnableCoordinatorOutcome.UnsupportedPlatform, ServiceEnableFailureCause.Unavailable);
        }

        // 1. THE CLOSED INITIATOR GRAMMAR, first and cheapest.
        if (!ServiceEnableProtocol.TryParseInitiator(argv))
        {
            return RefusedBeforeMutation(
                ServiceEnableCoordinatorOutcome.UsageRefused, ServiceEnableFailureCause.Unavailable);
        }

        if (!OperatingSystem.IsWindows())
        {
            return RefusedBeforeMutation(
                ServiceEnableCoordinatorOutcome.UnsupportedPlatform, ServiceEnableFailureCause.Unavailable);
        }

        // 2. THE SIGNING GATE, BEFORE anything is resolved or launched. A build
        //    that may not enable a service must never reach a UAC prompt.
        if (!PolicyPermitsEnablement(signing.ResolveConfiguredPolicy()))
        {
            return RefusedBeforeMutation(
                ServiceEnableCoordinatorOutcome.SigningPolicyRefused,
                ServiceEnableFailureCause.SigningPolicyRefused);
        }

        // 3. THE FIXED SIBLING HELPER. No caller path, ever.
        ServiceAdminHelperLocationResult located = locator.ResolveSiblingHelper();
        if (located.Outcome != ServiceAdminHelperLocationOutcome.Resolved
            || string.IsNullOrEmpty(located.ResolvedPath))
        {
            return RefusedBeforeMutation(
                ServiceEnableCoordinatorOutcome.HelperUnavailable, ServiceEnableFailureCause.Unavailable);
        }

        ServiceInitiatorProcessFacts initiator = facts.CaptureInitiator();
        if (!initiator.IsPresent)
        {
            return RefusedBeforeMutation(
                ServiceEnableCoordinatorOutcome.InitiatorFactsUnavailable,
                ServiceEnableFailureCause.Unavailable);
        }

        // The initiator creates ONLY the rendezvous NAME. It never creates the
        // pipe - the elevated helper does - and it never creates the challenge.
        string endpointName = ServiceInitiatingUserIdentityChannelContract.NewEndpointName();

        ServiceAnchorElevationLaunchSpec? spec =
            TryComposeLaunchSpec(located.ResolvedPath!, endpointName, initiator);
        if (spec is null)
        {
            return RefusedBeforeMutation(
                ServiceEnableCoordinatorOutcome.LaunchSpecUnavailable, ServiceEnableFailureCause.Unavailable);
        }

        // NO CHANNEL CLOCK IS RUNNING YET. Human UAC response time is spent
        // entirely inside this call and cannot consume the rendezvous budget.
        ServiceAnchorElevatedLaunch launch = launcher.LaunchElevated(spec);
        if (launch.State == ServiceAnchorElevatedLaunchState.Declined)
        {
            // Return IMMEDIATELY, without starting any client.
            return RefusedBeforeMutation(
                ServiceEnableCoordinatorOutcome.ApprovalDeclined, ServiceEnableFailureCause.Unavailable);
        }
        if (launch.State != ServiceAnchorElevatedLaunchState.Started || launch.Process is null)
        {
            return RefusedBeforeMutation(
                ServiceEnableCoordinatorOutcome.HelperLaunchFailed, ServiceEnableFailureCause.Unavailable);
        }

        using IServiceAnchorElevatedProcess helper = launch.Process;
        return RunTransaction(helper, client, endpointName, channelTimeout, helperExitTimeout);
    }

    /// <summary>
    /// Every refusal reached BEFORE the elevated helper could touch anything. It
    /// is a PROVEN pre-mutation state, not an assumption: no process has been
    /// started, or the one that was started never received an approval.
    /// </summary>
    private static ServiceEnableCoordinatorResult RefusedBeforeMutation(
        ServiceEnableCoordinatorOutcome outcome, ServiceEnableFailureCause cause) =>
        new(
            outcome,
            new ServiceEnableFailureClassification(
                cause, ServiceEnableFailureDisposition.RefusedBeforeMutation));

    /// <summary>
    /// The closed launch specification: the fixed sibling helper is launched
    /// DIRECTLY, so there is no host image and no assembly path argument. The
    /// argument vector comes from the single shared protocol authority.
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

        string[] arguments = ServiceEnableProtocol.ComposeElevatedArguments(endpointName, initiator);
        return arguments.Length == ServiceEnableVerbs.ElevatedTokenCount
            ? new ServiceAnchorElevationLaunchSpec(helperPath, arguments)
            : null;
    }

    private static ServiceEnableCoordinatorResult RunTransaction(
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
            return Unresolved(ServiceEnableCoordinatorOutcome.AcknowledgementNotReceived);
        }

        // The clock starts HERE, after a successful launch. The wait ends early
        // if the helper dies, so a helper that fails before creating its pipe
        // cannot hold the initiator for the whole rendezvous budget.
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
                return Unresolved(ServiceEnableCoordinatorOutcome.AcknowledgementNotReceived);
            }

            if (completed)
            {
                break;
            }
            if (helper.HasExited)
            {
                // The helper is already gone, so its bounded code is readable
                // and is the only report of what it did.
                return new ServiceEnableCoordinatorResult(
                    ServiceEnableCoordinatorOutcome.HelperExitedBeforeTransaction,
                    ReadHelperClassification(helper));
            }
            if (elapsed.Elapsed >= channelTimeout)
            {
                return Unresolved(ServiceEnableCoordinatorOutcome.TransactionTimedOut);
            }
        }

        ServiceInitiatingUserIdentityChannelOutcome outcome;
        try
        {
            outcome = transaction.Result;
        }
        catch (Exception)
        {
            return Unresolved(ServiceEnableCoordinatorOutcome.AcknowledgementNotReceived);
        }

        // ---- CYCLE 67: THE HELPER'S EXIT IS ALWAYS OBSERVED, BOUNDEDLY -----
        //
        // Before this cycle the exit code was read ONLY after a Completed
        // channel outcome, so every refusal discarded the one value that could
        // have named the cause. It is now read on EVERY path, inside the SAME
        // bounded budget that already existed.
        if (!helper.WaitForExit(helperExitTimeout))
        {
            return Unresolved(ServiceEnableCoordinatorOutcome.HelperDidNotExit);
        }

        int exitCode;
        try
        {
            exitCode = helper.ExitCode;
        }
        catch (Exception)
        {
            return Unresolved(ServiceEnableCoordinatorOutcome.RecoveryRequired);
        }

        ServiceEnableFailureClassification reported = ServiceEnableFailureContract.FromExitCode(exitCode);

        // ---- THE CROSS-CHECK ------------------------------------------------
        //
        // The channel outcome and the exit code are two INDEPENDENT reports of
        // the same server-side decision. Neither is authority on its own, and a
        // disagreement is never resolved by preferring one of them: it becomes
        // recovery-required, because a broken verification chain is exactly the
        // situation in which a guessed cause would be most dangerous.

        // ZERO IS SUCCESS ONLY. A zero exit without a Completed channel outcome
        // is a contradiction, not a success and not a benign refusal.
        if (exitCode == ServiceEnableFailureContract.SuccessExitCode
            && outcome != ServiceInitiatingUserIdentityChannelOutcome.Completed)
        {
            return Unresolved(ServiceEnableCoordinatorOutcome.RecoveryRequired);
        }

        if (outcome == ServiceInitiatingUserIdentityChannelOutcome.Completed)
        {
            // A Completed transaction with a NONZERO helper exit is a failure,
            // and it keeps the helper's own bounded cause and disposition.
            return exitCode == ServiceEnableFailureContract.SuccessExitCode
                ? new ServiceEnableCoordinatorResult(
                    ServiceEnableCoordinatorOutcome.Completed,
                    new ServiceEnableFailureClassification(
                        ServiceEnableFailureCause.None, ServiceEnableFailureDisposition.Completed))
                : new ServiceEnableCoordinatorResult(
                    ServiceEnableCoordinatorOutcome.HelperReportedFailure, reported);
        }

        // Only outcomes the SERVER actually spoke on the wire are cross-checked.
        // A client-local outcome has no server statement to agree with, so the
        // helper's exit code is the only report there is.
        if (ServiceEnableFailureContract.IsServerAuthoritative(outcome)
            && reported.Disposition != ServiceEnableFailureContract.ExpectedDispositionFor(outcome))
        {
            return Unresolved(ServiceEnableCoordinatorOutcome.RecoveryRequired);
        }

        // Agreement. Every state that means "machine state may not be closed" is
        // preserved as its own signal rather than flattened into a plain
        // acknowledgement failure, because the remedy is an attended recovery
        // and not a retry.
        bool recovery =
            outcome is ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict
                or ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired
                or ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired
            || reported.Disposition == ServiceEnableFailureDisposition.RecoveryRequired;

        return new ServiceEnableCoordinatorResult(
            recovery
                ? ServiceEnableCoordinatorOutcome.RecoveryRequired
                : ServiceEnableCoordinatorOutcome.AcknowledgementNotReceived,
            reported);
    }

    /// <summary>
    /// A bounded result for a path where the helper never produced a readable,
    /// trustworthy code. It never claims that nothing was mutated.
    /// </summary>
    private static ServiceEnableCoordinatorResult Unresolved(ServiceEnableCoordinatorOutcome outcome) =>
        new(
            outcome,
            new ServiceEnableFailureClassification(
                ServiceEnableFailureCause.Unavailable,
                ServiceEnableFailureDisposition.RecoveryRequired));

    private static ServiceEnableFailureClassification ReadHelperClassification(
        IServiceAnchorElevatedProcess helper)
    {
        try
        {
            return ServiceEnableFailureContract.FromExitCode(helper.ExitCode);
        }
        catch (Exception)
        {
            return new ServiceEnableFailureClassification(
                ServiceEnableFailureCause.Unavailable,
                ServiceEnableFailureDisposition.RecoveryRequired);
        }
    }
}
