// PAX Cookbook - ELEVATED SERVICE-DISABLE DISPATCH (cycle 63R, helper only)
//
// WHAT THIS FILE IS. The SECOND - and last - elevated entry point of the service
// administrative helper. It parses the closed disable grammar from the single
// shared protocol authority, creates the FIRST pipe instance for the PID +
// creation-FILETIME bound initiator, and runs exactly one identity-bound
// service-disable transaction under a bounded self-timeout.
//
// THE ANCHOR POLICY IS NeverCreate, AND THAT IS THE POINT. A disable attempt
// must never mint an ownership record. The policy is an ENUM fixed at endpoint
// creation time; it is not a delegate, not settable and not reachable by a
// connected client.
//
// IT IS NOT INVOKED THIS CYCLE. The code path is complete and callable; nothing
// in this cycle ran it, no UAC prompt was raised, no Program Files or ProgramData
// path was written or removed, and no Service Control Manager call was made.
//
// WHAT CROSSES THE ELEVATED BOUNDARY. Exactly three values: the endpoint name,
// the initiator's process id and the initiator's creation FILETIME. All three
// are COMPARED against kernel-derived facts and none is used as an identity. No
// path, SID, account, service name, payload location, command, registry
// location, certificate, secret or override flag ever appears in an argument.
//
// EXIT CODES ARE FIXED AND IDENTITY-FREE. Every outcome maps to an EXISTING
// Setup exit code. The coordinator learns the DETAILED outcome from the
// channel's bounded refusal token, not from the exit code.
using System;
using System.Runtime.Versioning;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Service;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>
/// The elevated disable dispatch. <see cref="Run(string[])"/> is the fixed
/// production entry point and takes only the process argument vector.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceDisableElevatedDispatch
{
    /// <summary>The fixed production entry point. It composes its own collaborators.</summary>
    internal static int Run(string[]? argv)
    {
        if (!ServiceDisableProtocol.TryParseElevated(argv, out ServiceEnableElevatedArguments? parsed)
            || parsed is null)
        {
            return SetupExitCodes.UsageError;
        }

        if (!OperatingSystem.IsWindows())
        {
            return SetupExitCodes.UnsupportedWindowsVersion;
        }

        string? metadataDirectory = ServiceInstallationAnchorStore.TryResolveFixedServiceDirectory();
        if (metadataDirectory is null)
        {
            return SetupExitCodes.GenericError;
        }

        var serviceControl = new WindowsServiceControlManagerAdapter();
        var transaction = new ServiceDisableTransaction(
            metadataDirectory,
            new KnownFolderProgramFilesX64Resolver(),
            serviceControl,
            new EmbeddedServiceEnablePayloadSource(),
            new ServiceStartabilityCoordinator(
                serviceControl,
                new WindowsServiceRunningProcessVerifier(),
                new StrictServiceRuntimeDocumentVerifier(),
                new WindowsServiceChildProcessObserver(),
                new RealServiceStartabilityClock()));

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
        if (!ServiceDisableProtocol.TryParseElevated(argv, out ServiceEnableElevatedArguments? parsed)
            || parsed is null)
        {
            return SetupExitCodes.UsageError;
        }

        if (!OperatingSystem.IsWindows() || resolver is null || transaction is null)
        {
            return SetupExitCodes.UnsupportedWindowsVersion;
        }

        using ServiceInitiatingUserIdentityChannelServer? server =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                parsed.EndpointName,
                parsed.Initiator,
                resolver,
                transaction,
                ServiceAnchorCreationPolicy.NeverCreate);

        if (server is null)
        {
            return SetupExitCodes.GenericError;
        }

        ServiceInitiatingUserIdentityBindResult result = server.AwaitAndBind(selfTimeout);
        return ServiceEnableElevatedDispatch.MapExitCode(result.Outcome);
    }
}
