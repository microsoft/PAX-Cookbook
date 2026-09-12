// PAX Cookbook - SERVICE ENABLE PROTOCOL (cycle 62)
//
// WHAT THIS FILE IS. The ONE source authority for the service-enablement
// grammar. It is compiled into PAXCookbookSetup and COMPILE-LINKED into
// PAXCookbook.ServiceAdminHelper, so the non-elevated initiator, the elevated
// helper and both focused test classes read the SAME spellings from the SAME
// source. No project retypes a verb, an option name, a token count or a bound.
//
// THE TWO CLOSED GRAMMARS - exactly this, in exactly this order, nothing else:
//
//   service-enable
//       (no argument at all - exactly ONE token)
//
//   service-enable-elevated
//       --endpoint          <PAXCookbook.InitiatingUserIdentity.{32 uppercase hex}>
//       --initiator-pid     <decimal uint32, non-zero>
//       --initiator-created <decimal int64, positive Windows FILETIME>
//       (exactly SEVEN tokens)
//
// WHAT NEITHER GRAMMAR WILL EVER ACCEPT, and why that list is exhaustive. There
// is no parameter here for a caller-supplied path, directory, file name,
// service name, display name, account, SID, executable, script, command,
// registry location, certificate value, thumbprint, store name, environment
// variable, output file, payload location or secret. The elevated boundary's
// whole vocabulary is the three values above; all three are COMPARED against
// kernel-derived facts and none of them is ever used as an identity.
//
// NO SECRET ON A COMMAND LINE. The endpoint name is not a secret - squatting is
// defeated by FILE_FLAG_FIRST_PIPE_INSTANCE when the elevated helper creates
// the first pipe instance. The transaction challenge is minted only after a
// client connects and never appears in any argument.
//
// WHAT THIS FILE CANNOT DO, by construction. It is pure. It performs no file
// access, no process start, no elevation, no registry, certificate, key or
// credential access, no service control, no network access and no PAX or Bake
// work. It has no settable field, no delegate and no options object.
//
// PRIVACY - FAIL CLOSED. Every refusal is a bounded token. Nothing here returns
// or formats a path, an identity, a native status or an exception.
using System;
using System.Globalization;

namespace PAXCookbookSetup.Service;

/// <summary>
/// The fixed verbs, option spellings and bounds of the service-enable
/// operation. Both verbs are INTERNAL Setup operations: neither is a member of
/// <c>ArgParser.KnownVerbs</c> and neither appears in public help.
/// </summary>
internal static class ServiceEnableVerbs
{
    /// <summary>The NON-ELEVATED initiator verb. Takes no argument at all.</summary>
    internal const string InitiatorVerb = "service-enable";

    /// <summary>The ELEVATED helper verb. Accepts only the closed grammar above.</summary>
    internal const string ElevatedHelperVerb = "service-enable-elevated";

    internal const string EndpointOption = "--endpoint";
    internal const string InitiatorProcessIdOption = "--initiator-pid";
    internal const string InitiatorCreatedOption = "--initiator-created";

    /// <summary>Exactly one token: the verb, and nothing after it.</summary>
    internal const int InitiatorTokenCount = 1;

    /// <summary>Exactly seven tokens: the verb plus three option/value pairs.</summary>
    internal const int ElevatedTokenCount = 7;

    /// <summary>
    /// Hard per-token bound, applied BEFORE any parse. Every legal token is far
    /// shorter, so anything longer is a cheap refusal rather than work.
    /// </summary>
    internal const int MaxTokenLength = 128;

    /// <summary>
    /// The helper's own bounded deadline. CYCLE 63R raises it to 240 seconds
    /// because the elevated transaction now also starts the service and proves
    /// startability - an SCM start wait plus a 45 second advancing-heartbeat
    /// proof. It deliberately EXCEEDS the coordinator's 180 second rendezvous
    /// budget so the initiator, not the helper, gives up first, while still
    /// guaranteeing no elevated helper waits forever. This ordering
    /// (self-timeout &gt; channel timeout) is a binding invariant.
    /// </summary>
    internal static readonly TimeSpan HelperSelfTimeout = TimeSpan.FromSeconds(240);

    internal static bool IsInitiatorRequested(string? verb) =>
        string.Equals(verb, InitiatorVerb, StringComparison.OrdinalIgnoreCase);

    internal static bool IsElevatedHelperRequested(string? verb) =>
        string.Equals(verb, ElevatedHelperVerb, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for either internal service-enable verb.</summary>
    internal static bool IsRequested(string? verb) =>
        IsInitiatorRequested(verb) || IsElevatedHelperRequested(verb);
}

/// <summary>
/// The parsed, fully validated elevated arguments. It is only ever constructed
/// by a successful parse, so its presence is itself proof the grammar held.
/// </summary>
internal sealed class ServiceEnableElevatedArguments
{
    internal ServiceEnableElevatedArguments(string endpointName, ServiceInitiatorProcessFacts initiator)
    {
        EndpointName = endpointName;
        Initiator = initiator;
    }

    internal string EndpointName { get; }

    internal ServiceInitiatorProcessFacts Initiator { get; }

    /// <summary>Carries the bounded type name only - never the endpoint, pid or FILETIME.</summary>
    public override string ToString() => nameof(ServiceEnableElevatedArguments);
}

/// <summary>
/// The bounded outcomes one service-enable operation can reach. Zero is the
/// permanent, safe default so an uninitialised value can never read as success.
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data.
/// </summary>
public enum ServiceEnableOperationOutcome
{
    Unspecified = 0,

    /// <summary>
    /// Payload extracted, service registered, machine data protected, service
    /// STARTED, and the running state, process identity, Session 0, status
    /// document and ADVANCING heartbeat all proven.
    /// </summary>
    Completed = 1,

    UnsupportedPlatform = 2,

    /// <summary>The token count, order, spelling or value grammar did not hold.</summary>
    UsageRefused = 3,

    /// <summary>The configured signing policy does not permit enablement in this build.</summary>
    SigningPolicyRefused = 4,

    /// <summary>A mutation-free preflight check refused. Nothing was written.</summary>
    PreflightRefused = 5,

    /// <summary>A valid anchor exists for a DIFFERENT installation or user.</summary>
    AnchorConflict = 6,

    /// <summary>The anchor could not be made durable, or existing anchor state was refused.</summary>
    AnchorPersistenceRefused = 7,

    /// <summary>Extraction into the fixed staging or final location refused.</summary>
    ExtractionRefused = 8,

    /// <summary>Service creation, SID-type configuration or configuration verification refused.</summary>
    RegistrationRefused = 9,

    /// <summary>Final membership, byte or access-control verification did not reproduce the exact expected state.</summary>
    VerificationFailed = 10,

    /// <summary>The attempt failed and every piece of state it created was provably removed.</summary>
    Compensated = 11,

    /// <summary>
    /// Machine state cannot be proven closed. The anchor is PRESERVED and an
    /// attended, elevated recovery operation is the only remedy. Never a
    /// success and never automatically repaired.
    /// </summary>
    RecoveryRequired = 12,

    /// <summary>A bounded access or stability failure. Never a partial success.</summary>
    Unavailable = 13,

    /// <summary>
    /// CYCLE 63RR. One of the three machine data access-control profiles - the
    /// metadata directory, the installation anchor or the runtime directory -
    /// could not be applied, or did not verify EXACTLY. A pre-existing object
    /// with the wrong owner or DACL lands here and is never repaired.
    /// </summary>
    MachineDataProtectionRefused = 14,

    /// <summary>
    /// CYCLE 63RR. The service did not start, never reached Running, ran under
    /// the wrong token identity or outside Session 0, published no valid status
    /// document, produced no ADVANCING heartbeat, or had a forbidden child
    /// process. Registration succeeding is never enough.
    /// </summary>
    StartabilityRefused = 15,
}

/// <summary>
/// The CLOSED parsers. Both are pure, both are total, and both return false on
/// the first violation without reporting which one - so a refusal reveals
/// nothing about the accepted shape.
/// </summary>
internal static class ServiceEnableProtocol
{
    /// <summary>
    /// The initiator grammar: exactly the verb, and nothing else. An extra
    /// token is a usage refusal rather than a silently ignored argument.
    /// </summary>
    internal static bool TryParseInitiator(string[]? argv)
    {
        if (argv is null || argv.Length != ServiceEnableVerbs.InitiatorTokenCount)
        {
            return false;
        }

        string token = argv[0];
        return token is not null
            && token.Length > 0
            && token.Length <= ServiceEnableVerbs.MaxTokenLength
            && ServiceEnableVerbs.IsInitiatorRequested(token);
    }

    /// <summary>
    /// The elevated grammar. Order, count, spelling and value grammar are all
    /// required, and nothing is inferred or defaulted.
    /// </summary>
    internal static bool TryParseElevated(string[]? argv, out ServiceEnableElevatedArguments? parsed)
    {
        parsed = null;
        if (argv is null || argv.Length != ServiceEnableVerbs.ElevatedTokenCount)
        {
            return false;
        }

        foreach (string token in argv)
        {
            if (token is null || token.Length == 0 || token.Length > ServiceEnableVerbs.MaxTokenLength)
            {
                return false;
            }
        }

        if (!ServiceEnableVerbs.IsElevatedHelperRequested(argv[0])
            || !string.Equals(argv[1], ServiceEnableVerbs.EndpointOption, StringComparison.Ordinal)
            || !string.Equals(argv[3], ServiceEnableVerbs.InitiatorProcessIdOption, StringComparison.Ordinal)
            || !string.Equals(argv[5], ServiceEnableVerbs.InitiatorCreatedOption, StringComparison.Ordinal))
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

        parsed = new ServiceEnableElevatedArguments(
            argv[2], new ServiceInitiatorProcessFacts(processId, creationFileTime));
        return true;
    }

    /// <summary>
    /// Composes the CLOSED elevated argument vector for a given endpoint and
    /// bound initiator. It is the single place the argument order is decided,
    /// so the launcher and the parser can never disagree.
    /// </summary>
    internal static string[] ComposeElevatedArguments(
        string endpointName, ServiceInitiatorProcessFacts initiator) => new[]
    {
        ServiceEnableVerbs.ElevatedHelperVerb,
        ServiceEnableVerbs.EndpointOption,
        endpointName,
        ServiceEnableVerbs.InitiatorProcessIdOption,
        initiator.ProcessId.ToString(CultureInfo.InvariantCulture),
        ServiceEnableVerbs.InitiatorCreatedOption,
        initiator.CreationFileTime.ToString(CultureInfo.InvariantCulture),
    };
}
