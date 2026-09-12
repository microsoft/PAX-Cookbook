// PAX Cookbook - ELEVATED ANCHOR HELPER DISPATCH (cycle 59, Setup only)
//
// WHAT THIS FILE IS. The ONE fixed elevated Setup entry point that performs a
// single installation-anchor transaction, plus the CLOSED grammar of the only
// arguments it will ever accept.
//
// THE CLOSED GRAMMAR - exactly this, in exactly this order, and nothing else:
//
//     service-anchor-bind-elevated
//         --endpoint         <PAXCookbook.InitiatingUserIdentity.{32 uppercase hex}>
//         --initiator-pid    <decimal uint32, non-zero>
//         --initiator-created <decimal int64, positive Windows FILETIME>
//
// Exactly SEVEN tokens. An unknown option, a duplicated option, a missing
// option, an out-of-order option, a malformed value, an oversized value or any
// extra token is a usage refusal that happens BEFORE any machine access.
//
// WHAT IT WILL NEVER ACCEPT, and why that list is exhaustive. There is no
// parameter here for a caller-supplied path, directory, file name, service
// name, executable, script, registry location, certificate value, thumbprint,
// store name, command, environment variable, output file, account, SID, token
// or secret. The elevated boundary's whole vocabulary is the three values
// above, all of which are compared and none of which is used as an identity.
//
// NO SECRET ON THE COMMAND LINE. The endpoint name is not a secret - squatting
// is defeated by FILE_FLAG_FIRST_PIPE_INSTANCE when THIS process creates the
// first pipe instance. The challenge is created only after a client connects,
// and never appears in any argument.
//
// THE APPROVER IS NEVER THE OWNER. The pid and creation FILETIME name the
// non-elevated initiator; its SID is resolved from the kernel and used ONLY for
// the pipe DACL and for comparison. The anchor is written from the SID the
// kernel reports for the LIVE connection. Do not persist or log the pid, the
// creation-time hint, the provisional DACL identity, or the elevated approver
// identity. The anchor intentionally persists only the SID independently
// derived from the live pipe connection.
//
// ADMINISTRATIVE RECOVERY IS A FUTURE OPERATION. This dispatch has no reset,
// no delete and no repair authority and must not acquire one incidentally. A
// later attended, elevated recovery operation MAY reset the anchor, but only
// after proving that no service, ownership ledger, credential, job or other
// owned state depends on it. Until then a conflicting, malformed, partial or
// unreadable anchor is preserved exactly as found and reported as a bounded
// recovery-required refusal.
//
// BOUNDED SELF-TIMEOUT. The transaction runs under a fixed deadline, so an
// elevated helper can never wait indefinitely for a client that will not come.
//
// EXIT CODES ARE FIXED AND IDENTITY-FREE. Every outcome maps to an EXISTING
// Setup exit code; no new public exit-code surface is introduced and no native
// status, path, account or identity is ever exposed through the exit code. The
// coordinator learns the DETAILED outcome from the channel's bounded refusal
// token, not from the exit code, so the coarse mapping loses nothing.
//
// WHAT THIS FILE CANNOT DO, by construction. It opens no certificate store or
// private key; reads or writes no ACL, registry key or credential vault;
// creates, changes, starts or stops no service; opens no socket; starts no
// process; reads or writes no ownership ledger; touches no PAX and starts no
// Bake. Its only durable side effect is one installation-anchor write.
using System;
using System.Globalization;
using PAXCookbook.Shared.ExitCodes;

namespace PAXCookbookSetup.Service;

/// <summary>
/// The fixed verbs and option spellings of the anchor operation. Both verbs are
/// INTERNAL Setup operations: neither is a member of
/// <c>ArgParser.KnownVerbs</c> and neither appears in public help.
/// </summary>
internal static class ServiceAnchorElevationVerbs
{
    /// <summary>The NON-ELEVATED initiator verb. Takes no argument at all.</summary>
    internal const string InitiatorVerb = "service-anchor-bind";

    /// <summary>The ELEVATED helper verb. Accepts only the closed grammar above.</summary>
    internal const string ElevatedHelperVerb = "service-anchor-bind-elevated";

    internal const string EndpointOption = "--endpoint";
    internal const string InitiatorProcessIdOption = "--initiator-pid";
    internal const string InitiatorCreatedOption = "--initiator-created";

    /// <summary>Exactly seven tokens: the verb plus three option/value pairs.</summary>
    internal const int ElevatedTokenCount = 7;

    /// <summary>
    /// Hard per-token bound, applied BEFORE any parse. Every legal token is far
    /// shorter, so anything longer is a cheap refusal rather than work.
    /// </summary>
    internal const int MaxTokenLength = 128;

    /// <summary>
    /// The helper's own bounded deadline. It deliberately exceeds the
    /// coordinator's rendezvous budget so the initiator, not the helper, is the
    /// one that gives up first - while still guaranteeing that no elevated
    /// helper ever waits indefinitely.
    /// </summary>
    internal static readonly TimeSpan HelperSelfTimeout = TimeSpan.FromSeconds(60);

    internal static bool IsInitiatorRequested(string? verb) =>
        string.Equals(verb, InitiatorVerb, StringComparison.OrdinalIgnoreCase);

    internal static bool IsElevatedHelperRequested(string? verb) =>
        string.Equals(verb, ElevatedHelperVerb, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for either internal anchor verb.</summary>
    internal static bool IsRequested(string? verb) =>
        IsInitiatorRequested(verb) || IsElevatedHelperRequested(verb);
}

/// <summary>
/// The parsed, fully validated elevated arguments. It is only ever constructed
/// by a successful parse, so its presence is itself proof the grammar held.
/// </summary>
internal sealed class ServiceAnchorElevatedArguments
{
    internal ServiceAnchorElevatedArguments(string endpointName, ServiceInitiatorProcessFacts initiator)
    {
        EndpointName = endpointName;
        Initiator = initiator;
    }

    internal string EndpointName { get; }

    internal ServiceInitiatorProcessFacts Initiator { get; }

    /// <summary>Carries the bounded type name only - never the endpoint, pid or FILETIME.</summary>
    public override string ToString() => nameof(ServiceAnchorElevatedArguments);
}

/// <summary>
/// The elevated helper dispatch. <see cref="TryParse"/> is pure and is the sole
/// gate; <see cref="Run"/> performs the transaction and maps it to an exit code.
/// </summary>
internal static class ServiceAnchorElevatedHelperDispatch
{
    /// <summary>
    /// THE CLOSED PARSE. Order, count, spelling and value grammar are all
    /// required, and nothing is inferred or defaulted. Returns false on the
    /// first violation without reporting which one, so the refusal reveals
    /// nothing about the accepted shape.
    /// </summary>
    internal static bool TryParse(string[]? argv, out ServiceAnchorElevatedArguments? parsed)
    {
        parsed = null;
        if (argv is null || argv.Length != ServiceAnchorElevationVerbs.ElevatedTokenCount)
        {
            return false;
        }

        foreach (string token in argv)
        {
            if (token is null || token.Length == 0 || token.Length > ServiceAnchorElevationVerbs.MaxTokenLength)
            {
                return false;
            }
        }

        if (!ServiceAnchorElevationVerbs.IsElevatedHelperRequested(argv[0])
            || !string.Equals(argv[1], ServiceAnchorElevationVerbs.EndpointOption, StringComparison.Ordinal)
            || !string.Equals(argv[3], ServiceAnchorElevationVerbs.InitiatorProcessIdOption, StringComparison.Ordinal)
            || !string.Equals(argv[5], ServiceAnchorElevationVerbs.InitiatorCreatedOption, StringComparison.Ordinal))
        {
            return false;
        }

        if (!ServiceInitiatingUserIdentityChannelContract.IsCanonicalEndpointName(argv[2]))
        {
            return false;
        }

        // NumberStyles.None rejects a sign, a thousands separator, whitespace
        // and hex, so only bare decimal digits are ever accepted.
        if (!uint.TryParse(argv[4], NumberStyles.None, CultureInfo.InvariantCulture, out uint processId)
            || processId == 0)
        {
            return false;
        }

        if (!long.TryParse(argv[6], NumberStyles.None, CultureInfo.InvariantCulture, out long creationFileTime)
            || creationFileTime <= 0)
        {
            return false;
        }

        parsed = new ServiceAnchorElevatedArguments(
            argv[2], new ServiceInitiatorProcessFacts(processId, creationFileTime));
        return true;
    }

    /// <summary>
    /// THE FIXED MAPPING from bounded channel outcome to Setup exit code. It is
    /// total, uses only existing exit codes, and exposes no identity or native
    /// detail. Success is the ONLY zero.
    /// </summary>
    internal static int MapExitCode(ServiceInitiatingUserIdentityChannelOutcome outcome) => outcome switch
    {
        ServiceInitiatingUserIdentityChannelOutcome.Completed => SetupExitCodes.Ok,
        ServiceInitiatingUserIdentityChannelOutcome.UnsupportedPlatform => SetupExitCodes.UnsupportedWindowsVersion,
        _ => SetupExitCodes.GenericError,
    };

    /// <summary>
    /// Runs the ONE transaction. Every terminal path destroys the rendezvous
    /// material and closes the endpoint, because the server disposes itself in
    /// its own finally block and this method disposes it again defensively.
    /// </summary>
    internal static int Run(string[]? argv) =>
        Run(argv, new WindowsServiceInitiatorIdentityResolver(), ServiceAnchorElevationVerbs.HelperSelfTimeout);

    internal static int Run(
        string[]? argv, IServiceInitiatorIdentityResolver resolver, TimeSpan selfTimeout)
    {
        if (!TryParse(argv, out ServiceAnchorElevatedArguments? parsed) || parsed is null)
        {
            return SetupExitCodes.UsageError;
        }

        if (!OperatingSystem.IsWindows() || resolver is null)
        {
            return SetupExitCodes.UnsupportedWindowsVersion;
        }

        // The helper creates the FIRST pipe instance itself, with a DACL for the
        // PID + creation-FILETIME bound initiator. A squatted endpoint name, a
        // dead or recycled initiator, or an unresolvable initiator SID all fail
        // right here, before anything durable is touched.
        using ServiceInitiatingUserIdentityChannelServer? server =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                parsed.EndpointName, parsed.Initiator, resolver);

        if (server is null)
        {
            return SetupExitCodes.GenericError;
        }

        ServiceInitiatingUserIdentityBindResult result = server.AwaitAndBind(selfTimeout);
        return MapExitCode(result.Outcome);
    }
}
