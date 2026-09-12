// PAX Cookbook - ELEVATED SERVICE-ENABLE DISPATCH (cycle 62, helper only)
//
// WHAT THIS FILE IS. The ONE elevated entry point of the service administrative
// helper. It parses the closed grammar from the single shared protocol
// authority, creates the FIRST pipe instance for the PID + creation-FILETIME
// bound initiator, and runs exactly one identity-bound service-enable
// transaction under a bounded self-timeout.
//
// IT IS NOT INVOKED THIS CYCLE. The code path is complete and callable; nothing
// in this cycle ran it, no UAC prompt was raised, no Program Files or ProgramData
// path was written, and no Service Control Manager call was made.
//
// WHAT CROSSES THE ELEVATED BOUNDARY. Exactly three values: the endpoint name,
// the initiator's process id and the initiator's creation FILETIME. All three
// are COMPARED against kernel-derived facts and none is used as an identity. No
// path, SID, account, service name, payload location, command, registry
// location, certificate or secret ever appears in an argument.
//
// THE APPROVER IS NEVER THE OWNER. Ownership comes exclusively from the SID the
// kernel reports for the LIVE pipe connection. The pid, the creation-time hint,
// the provisional DACL identity and the elevated approver identity are never
// persisted and never logged.
//
// EXIT CODES ARE FIXED AND IDENTITY-FREE. CYCLE 67 moves every transaction
// outcome onto the dedicated bounded band in ServiceEnableFailureContract, so
// one exit code now names exactly one cause/disposition pair instead of
// collapsing every failure onto a single generic code. No native status, path,
// SID, account, timestamp or exception is ever forwarded, and the coordinator
// still learns the SERVER-SPOKEN outcome from the channel's bounded refusal
// token - the exit code is a SECOND, INDEPENDENT report that must AGREE with
// it, never an authority that replaces it.
//
// WHAT THIS FILE CANNOT DO, by construction. It starts no process, opens no
// certificate store, private key or credential vault, reads or writes no
// registry key, opens no socket, touches no PAX and starts no Bake. The ONLY
// service control it can reach is the fixed SCM adapter's StartServiceW and, on
// compensation, its single SERVICE_CONTROL_STOP - both through the transaction
// it composes, and never through a shell, sc.exe, WMI or ServiceController.
using System;
using System.Runtime.Versioning;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Service;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>
/// The elevated dispatch. <see cref="Run(string[])"/> is the fixed production
/// entry point and takes only the process argument vector.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceEnableElevatedDispatch
{
    /// <summary>
    /// CYCLE 67. The TOTAL composition of the SERVER-SIDE channel outcome with
    /// the bound transaction's STICKY cause and FINAL disposition.
    ///
    /// The channel outcome is authoritative for the DISPOSITION - it is the
    /// value the server actually spoke - and the transaction is authoritative
    /// for the CAUSE. Any disagreement, including a Completed channel outcome
    /// over a transaction that did not report Completed, resolves to
    /// recovery-required rather than to a guessed cause.
    /// </summary>
    internal static ServiceEnableFailureClassification Classify(
        ServiceInitiatingUserIdentityChannelOutcome outcome,
        ServiceEnableFailureCause transactionCause,
        ServiceEnableFailureDisposition transactionDisposition)
    {
        switch (outcome)
        {
            case ServiceInitiatingUserIdentityChannelOutcome.Completed:
                // A cycle-59 endpoint carries NO bound transaction, so None/None
                // is the truthful "no transaction ran" shape and is accepted.
                return transactionCause == ServiceEnableFailureCause.None
                    && (transactionDisposition == ServiceEnableFailureDisposition.Completed
                        || transactionDisposition == ServiceEnableFailureDisposition.None)
                    ? new ServiceEnableFailureClassification(
                        ServiceEnableFailureCause.None, ServiceEnableFailureDisposition.Completed)
                    : new ServiceEnableFailureClassification(
                        ServiceEnableFailureCause.Unavailable,
                        ServiceEnableFailureDisposition.RecoveryRequired);

            case ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused:
            case ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated:
            case ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired:
                return new ServiceEnableFailureClassification(
                    NeverNone(transactionCause),
                    ServiceEnableFailureContract.ExpectedDispositionFor(outcome));

            default:
                // Everything else refused BEFORE or AROUND the transaction, so
                // the transaction has no cause to contribute.
                return new ServiceEnableFailureClassification(
                    ServiceEnableFailureCause.Unavailable,
                    ServiceEnableFailureContract.ExpectedDispositionFor(outcome));
        }
    }

    /// <summary>A missing cause on a failing path is Unavailable, never None.</summary>
    private static ServiceEnableFailureCause NeverNone(ServiceEnableFailureCause cause) =>
        cause == ServiceEnableFailureCause.None ? ServiceEnableFailureCause.Unavailable : cause;

    /// <summary>
    /// THE FIXED MAPPING to the cycle-67 bounded helper exit-code band. It is
    /// total and exposes no identity, path, native status or exception. Success
    /// is the ONLY zero.
    /// </summary>
    internal static int MapExitCode(
        ServiceInitiatingUserIdentityChannelOutcome outcome,
        ServiceEnableFailureCause transactionCause,
        ServiceEnableFailureDisposition transactionDisposition)
    {
        ServiceEnableFailureClassification classification =
            Classify(outcome, transactionCause, transactionDisposition);
        return ServiceEnableFailureContract.ToExitCode(classification.Cause, classification.Disposition);
    }

    /// <summary>
    /// The channel-only overload, for an endpoint with no bound transaction to
    /// contribute a cause.
    /// </summary>
    internal static int MapExitCode(ServiceInitiatingUserIdentityChannelOutcome outcome) =>
        MapExitCode(outcome, ServiceEnableFailureCause.None, ServiceEnableFailureDisposition.None);

    /// <summary>The fixed production entry point. It composes its own collaborators.</summary>
    internal static int Run(string[]? argv)
    {
        if (!ServiceEnableProtocol.TryParseElevated(argv, out ServiceEnableElevatedArguments? parsed)
            || parsed is null)
        {
            return SetupExitCodes.UsageError;
        }

        if (!OperatingSystem.IsWindows())
        {
            return SetupExitCodes.UnsupportedWindowsVersion;
        }

        string? serviceDirectory = ServiceInstallationAnchorStore.TryResolveFixedServiceDirectory();
        if (serviceDirectory is null)
        {
            // CYCLE 67. Composing the fixed machine location cannot mutate
            // anything, so this is provably a refusal before any mutation.
            return ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.Unavailable,
                ServiceEnableFailureDisposition.RefusedBeforeMutation);
        }

        // ONE SCM adapter instance is shared by the transaction and the
        // activation stage, so registration, the start and any compensating
        // stop all travel through the single P/Invoke site.
        var serviceControl = new WindowsServiceControlManagerAdapter();

        var transaction = new ServiceEnableTransaction(
            serviceDirectory,
            new KnownFolderProgramFilesX64Resolver(),
            new WindowsServiceEnableSecurityAdapter(),
            serviceControl,
            new EmbeddedServiceEnablePayloadSource(),
            new RealServiceEnableSigningPolicySource(),
            new ServiceEnableActivationStage(
                new WindowsServiceMachineDataSecurityAdapter(),
                new ServiceStartabilityCoordinator(
                    serviceControl,
                    new WindowsServiceRunningProcessVerifier(),
                    new StrictServiceRuntimeDocumentVerifier(),
                    new WindowsServiceChildProcessObserver(),
                    new RealServiceStartabilityClock()),
                serviceControl));

        return Run(
            argv,
            new WindowsServiceInitiatorIdentityResolver(),
            transaction,
            ServiceEnableVerbs.HelperSelfTimeout);
    }

    /// <summary>
    /// The disclosed test seam. INTERNAL, and every collaborator is injected, so
    /// the focused tests can prove the whole ordering without elevating,
    /// touching Program Files or querying the real Service Control Manager.
    /// </summary>
    internal static int Run(
        string[]? argv,
        IServiceInitiatorIdentityResolver resolver,
        IServiceIdentityBoundTransaction transaction,
        TimeSpan selfTimeout)
    {
        if (!ServiceEnableProtocol.TryParseElevated(argv, out ServiceEnableElevatedArguments? parsed)
            || parsed is null)
        {
            return SetupExitCodes.UsageError;
        }

        if (!OperatingSystem.IsWindows() || resolver is null || transaction is null)
        {
            return SetupExitCodes.UnsupportedWindowsVersion;
        }

        // The helper creates the FIRST pipe instance itself, with a DACL for the
        // PID + creation-FILETIME bound initiator. A squatted endpoint name, a
        // dead or recycled initiator, or an unresolvable initiator SID all fail
        // right here, before anything durable is touched.
        using ServiceInitiatingUserIdentityChannelServer? server =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                parsed.EndpointName, parsed.Initiator, resolver, transaction);

        if (server is null)
        {
            // CYCLE 67. The pipe never existed, so nothing durable was touched.
            return ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.Unavailable,
                ServiceEnableFailureDisposition.RefusedBeforeMutation);
        }

        ServiceInitiatingUserIdentityBindResult result = server.AwaitAndBind(selfTimeout);

        // CYCLE 67. The bound transaction contributes its STICKY cause and its
        // FINAL disposition; the channel outcome contributes the disposition the
        // server actually spoke. A transaction that does not expose the narrow
        // classification seam contributes nothing rather than a guess.
        var classified = transaction as IServiceEnableFailureClassified;
        return MapExitCode(
            result.Outcome,
            classified?.FailureCause ?? ServiceEnableFailureCause.None,
            classified?.FailureDisposition ?? ServiceEnableFailureDisposition.None);
    }
}
