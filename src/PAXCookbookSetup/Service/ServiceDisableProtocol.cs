// PAX Cookbook - SERVICE DISABLE PROTOCOL (cycle 63R)
//
// WHAT THIS FILE IS. The ONE source authority for the service-DISABLE grammar.
// It is compiled into PAXCookbookSetup and COMPILE-LINKED into
// PAXCookbook.ServiceAdminHelper, so the non-elevated initiator, the elevated
// helper and both focused test classes read the SAME spellings from the SAME
// source. No project retypes a verb, an option name, a token count or a bound.
//
// THE TWO CLOSED GRAMMARS - exactly this, in exactly this order, nothing else:
//
//   service-disable
//       (no argument at all - exactly ONE token)
//
//   service-disable-elevated
//       --endpoint          <PAXCookbook.InitiatingUserIdentity.{32 uppercase hex}>
//       --initiator-pid     <decimal uint32, non-zero>
//       --initiator-created <decimal int64, positive Windows FILETIME>
//       (exactly SEVEN tokens)
//
// THE ELEVATED VOCABULARY IS DELIBERATELY IDENTICAL TO ENABLE'S. Exactly three
// values cross the boundary - an endpoint name, a process id and a creation
// FILETIME - and all three are COMPARED against kernel-derived facts. There is
// no parameter for a path, directory, file name, service name, display name,
// account, SID, executable, script, command, registry location, certificate
// value, thumbprint, store name, environment variable or secret, and there is no
// "force", "purge", "all" or owner-override option of any kind.
//
// DISABLE IS OWNER-BOUND AND HAS NO OVERRIDE. The grammar cannot express one,
// because the only identity that can authorize a disable is the SID the kernel
// reports for the live pipe connection.
//
// WHAT THIS FILE CANNOT DO, by construction. It is pure. It performs no file
// access, no process start, no elevation, no registry, certificate, key or
// credential access, no service control, no network access and no PAX or Bake
// work. It has no settable field, no delegate and no options object.
using System;
using System.Globalization;

namespace PAXCookbookSetup.Service;

/// <summary>
/// The fixed verbs, option spellings and bounds of the service-disable
/// operation. Both verbs are INTERNAL Setup operations: neither is a member of
/// <c>ArgParser.KnownVerbs</c> and neither appears in public help.
/// </summary>
internal static class ServiceDisableVerbs
{
    /// <summary>The NON-ELEVATED initiator verb. Takes no argument at all.</summary>
    internal const string InitiatorVerb = "service-disable";

    /// <summary>The ELEVATED helper verb. Accepts only the closed grammar above.</summary>
    internal const string ElevatedHelperVerb = "service-disable-elevated";

    /// <summary>Exactly one token: the verb, and nothing after it.</summary>
    internal const int InitiatorTokenCount = ServiceEnableVerbs.InitiatorTokenCount;

    /// <summary>Exactly seven tokens: the verb plus three option/value pairs.</summary>
    internal const int ElevatedTokenCount = ServiceEnableVerbs.ElevatedTokenCount;

    internal static bool IsInitiatorRequested(string? verb) =>
        string.Equals(verb, InitiatorVerb, StringComparison.OrdinalIgnoreCase);

    internal static bool IsElevatedHelperRequested(string? verb) =>
        string.Equals(verb, ElevatedHelperVerb, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for either internal service-disable verb.</summary>
    internal static bool IsRequested(string? verb) =>
        IsInitiatorRequested(verb) || IsElevatedHelperRequested(verb);
}

/// <summary>
/// The bounded outcomes one service-disable operation can reach. Zero is the
/// permanent, safe default so an uninitialised value can never read as success.
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data.
/// </summary>
public enum ServiceDisableOperationOutcome
{
    Unspecified = 0,

    /// <summary>Every closed footprint member was removed and proven absent.</summary>
    Completed = 1,

    /// <summary>
    /// Nothing to do: no anchor and NO footprint of any kind. Idempotent, and
    /// never confused with a successful removal.
    /// </summary>
    AlreadyDisabled = 2,

    UnsupportedPlatform = 3,

    /// <summary>The token count, order, spelling or value grammar did not hold.</summary>
    UsageRefused = 4,

    /// <summary>A mutation-free preflight check refused. Nothing was removed.</summary>
    PreflightRefused = 5,

    /// <summary>
    /// The validated anchor names a DIFFERENT owner than the live initiating
    /// user. There is no override; a different owner's installation is never
    /// disabled.
    /// </summary>
    OwnerMismatch = 6,

    /// <summary>The fixed service could not be stopped, or never reached Stopped.</summary>
    StopRefused = 7,

    /// <summary>The SCM entry could not be deleted, or absence could not be proven.</summary>
    RegistrationRemovalRefused = 8,

    /// <summary>A machine path did not match the exact expected closed footprint.</summary>
    FootprintRefused = 9,

    /// <summary>The final closed-footprint absence check did not hold.</summary>
    VerificationFailed = 10,

    /// <summary>
    /// Machine state cannot be proven closed. The anchor is PRESERVED and an
    /// attended, elevated recovery operation is the only remedy. Never a
    /// success and never automatically repaired.
    /// </summary>
    RecoveryRequired = 11,

    /// <summary>A bounded access or stability failure. Never a partial success.</summary>
    Unavailable = 12,
}

/// <summary>
/// The CLOSED disable parsers. Both are pure, both are total, and both return
/// false on the first violation without reporting which one - so a refusal
/// reveals nothing about the accepted shape.
/// </summary>
internal static class ServiceDisableProtocol
{
    /// <summary>The initiator grammar: exactly the verb, and nothing else.</summary>
    internal static bool TryParseInitiator(string[]? argv)
    {
        if (argv is null || argv.Length != ServiceDisableVerbs.InitiatorTokenCount)
        {
            return false;
        }

        string token = argv[0];
        return token is not null
            && token.Length > 0
            && token.Length <= ServiceEnableVerbs.MaxTokenLength
            && ServiceDisableVerbs.IsInitiatorRequested(token);
    }

    /// <summary>
    /// The elevated grammar. Order, count, spelling and value grammar are all
    /// required, and nothing is inferred or defaulted.
    /// </summary>
    internal static bool TryParseElevated(string[]? argv, out ServiceEnableElevatedArguments? parsed)
    {
        parsed = null;
        if (argv is null || argv.Length != ServiceDisableVerbs.ElevatedTokenCount)
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

        if (!ServiceDisableVerbs.IsElevatedHelperRequested(argv[0])
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
    /// Composes the CLOSED elevated argument vector. It is the single place the
    /// disable argument order is decided, so the launcher and the parser can
    /// never disagree.
    /// </summary>
    internal static string[] ComposeElevatedArguments(
        string endpointName, ServiceInitiatorProcessFacts initiator) => new[]
    {
        ServiceDisableVerbs.ElevatedHelperVerb,
        ServiceEnableVerbs.EndpointOption,
        endpointName,
        ServiceEnableVerbs.InitiatorProcessIdOption,
        initiator.ProcessId.ToString(CultureInfo.InvariantCulture),
        ServiceEnableVerbs.InitiatorCreatedOption,
        initiator.CreationFileTime.ToString(CultureInfo.InvariantCulture),
    };
}
