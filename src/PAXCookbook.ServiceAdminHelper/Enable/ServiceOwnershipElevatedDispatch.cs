// PAX Cookbook - ELEVATED OWNERSHIP DISPATCH (cycle 94 pass C, helper only)
//
// WHAT THIS FILE IS. The THIRD and FOURTH elevated entry points of the service
// administrative helper, and the last ones this feature will ever have. It
// parses the closed ownership grammar from the single shared protocol authority,
// creates the FIRST pipe instance for the PID + creation-FILETIME bound
// initiator, and runs exactly one identity-bound PAYLOAD transaction under the
// EXISTING bounded self-timeout.
//
// THE ANCHOR POLICY IS NeverCreate, AND THAT IS THE POINT. An ownership
// operation must never mint an installation ownership record: the anchor has to
// already exist, for the same kernel SID, before a payload is even offered. The
// policy is an ENUM fixed at endpoint creation time; it is not a delegate, not
// settable and not reachable by a connected client.
//
// THE VERB - NOT THE PAYLOAD - SELECTS THE OPERATION. Run() takes the CLOSED
// operation enum the caller resolved from the verb, requires the argument
// vector's own verb to map to exactly that operation, and fixes it into the
// transaction at construction time, before a single payload byte is read.
//
// IT IS NOT INVOKED THIS CYCLE. The code path is complete and callable; nothing
// in this cycle ran it, no UAC prompt was raised, no Program Files or ProgramData
// path was written or removed, no Service Control Manager call was made, no
// certificate store or private key was opened, and no PAX or Bake work occurred.
//
// WHAT CROSSES THE ELEVATED BOUNDARY IN AN ARGUMENT. Exactly three values: the
// endpoint name, the initiator's process id and the initiator's creation
// FILETIME. All three are COMPARED against kernel-derived facts and none is used
// as an identity. The request itself never travels in an argument: it arrives
// only through the length-and-digest-framed, challenge-bound payload phase, on a
// pipe whose DACL names the bound initiator alone.
//
// EXIT CODES ARE FIXED AND IDENTITY-FREE. Every outcome maps to an EXISTING
// Setup exit code through the EXISTING mapping, so this file introduces no new
// exit vocabulary.
using System;
using System.Runtime.Versioning;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Service;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>
/// The elevated ownership dispatch. <see cref="Run(string[], ServiceOwnershipElevatedOperation)"/>
/// is the fixed production entry point and takes only the process argument
/// vector plus the closed operation the verb already selected.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceOwnershipElevatedDispatch
{
    /// <summary>The fixed production entry point. It composes its own collaborators.</summary>
    internal static int Run(string[]? argv, ServiceOwnershipElevatedOperation operation)
    {
        if (!ServiceOwnershipElevatedProtocol.TryParseElevated(argv, operation, out ServiceEnableElevatedArguments? parsed)
            || parsed is null)
        {
            return SetupExitCodes.UsageError;
        }

        if (!OperatingSystem.IsWindows())
        {
            return SetupExitCodes.UnsupportedWindowsVersion;
        }

        return Run(
            argv,
            operation,
            new WindowsServiceInitiatorIdentityResolver(),
            new ServiceOwnershipElevatedTransaction(operation),
            ServiceEnableVerbs.HelperSelfTimeout);
    }

    /// <summary>
    /// The disclosed test seam. INTERNAL, and every collaborator is injected, so
    /// the focused tests can prove the whole ordering without elevating, touching
    /// ProgramData, opening a certificate store or querying the real Service
    /// Control Manager.
    /// </summary>
    internal static int Run(
        string[]? argv,
        ServiceOwnershipElevatedOperation operation,
        IServiceInitiatorIdentityResolver resolver,
        IServiceIdentityBoundPayloadTransaction transaction,
        TimeSpan selfTimeout)
    {
        if (!ServiceOwnershipElevatedProtocol.TryParseElevated(argv, operation, out ServiceEnableElevatedArguments? parsed)
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
