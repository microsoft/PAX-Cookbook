// PAX Cookbook - CLOSED OWNERSHIP ELEVATED PROTOCOL (cycle 94, pass C)
//
// WHAT THIS FILE IS. The ONE source authority for the ownership-promotion and
// ownership-depromotion elevated grammars. It is compiled into PAXCookbookSetup
// and COMPILE-LINKED into PAXCookbook.ServiceAdminHelper, so the non-elevated
// initiator, the elevated helper and the focused tests read the SAME spellings
// from the SAME source. No project retypes a verb, an option name or a token
// count.
//
// THE TWO CLOSED GRAMMARS - exactly this, in exactly this order, nothing else:
//
//   ownership-promote-elevated
//       --endpoint          <PAXCookbook.InitiatingUserIdentity.{32 uppercase hex}>
//       --initiator-pid     <decimal uint32, non-zero>
//       --initiator-created <decimal int64, positive Windows FILETIME>
//       (exactly SEVEN tokens)
//
//   ownership-depromote-elevated
//       (the same three option/value pairs, exactly SEVEN tokens)
//
// THE VERB - NOT THE PAYLOAD - SELECTS THE OPERATION. Parsing a verb yields a
// CLOSED operation enum, and that enum is fixed into the transaction at
// construction time, BEFORE a single payload byte exists. Nothing downstream
// ever infers promotion or depromotion from a document's shape, its property
// set, or any other content signal.
//
// THE ELEVATED VOCABULARY IS DELIBERATELY IDENTICAL TO ENABLE'S AND DISABLE'S.
// Exactly three values cross the boundary - an endpoint name, a process id and a
// creation FILETIME - and all three are COMPARED against kernel-derived facts.
// There is no parameter for a location, a name, an identity, a credential, a
// selector or an override of any kind, and there is no way to add one without
// changing this file.
//
// WHAT THIS FILE CANNOT DO, by construction. It is pure. It performs no file
// access, no process start, no elevation, no registry, certificate, key or
// credential access, no service control, no network access and no PAX or Bake
// work. It has no settable field, no delegate and no options object.
using System;
using System.Globalization;

namespace PAXCookbookSetup.Service;

/// <summary>
/// The CLOSED elevated ownership operation. It is an ENUM, deliberately not a
/// string, a flag set or a callback: an operation that could be spelled freely
/// would let a caller widen the elevated vocabulary from outside this file.
///
/// Zero is the permanent, safe default, so an uninitialised value can never
/// authorize either operation.
/// </summary>
internal enum ServiceOwnershipElevatedOperation
{
    Unspecified = 0,

    /// <summary>Grant the fixed service identity the approved rights on ONE key.</summary>
    Promote = 1,

    /// <summary>Unwind ONE previously promoted job.</summary>
    Depromote = 2,
}

/// <summary>
/// The fixed verbs and bounds of the two elevated ownership operations. Both are
/// INTERNAL Setup operations: neither is a member of <c>ArgParser.KnownVerbs</c>
/// and neither appears in public help.
/// </summary>
internal static class ServiceOwnershipElevatedVerbs
{
    /// <summary>The ELEVATED promotion verb. Accepts only the closed grammar above.</summary>
    internal const string PromoteElevatedVerb = "ownership-promote-elevated";

    /// <summary>The ELEVATED depromotion verb. Accepts only the closed grammar above.</summary>
    internal const string DepromoteElevatedVerb = "ownership-depromote-elevated";

    /// <summary>Exactly seven tokens: the verb plus three option/value pairs.</summary>
    internal const int ElevatedTokenCount = ServiceEnableVerbs.ElevatedTokenCount;

    internal static bool IsPromoteRequested(string? verb) =>
        string.Equals(verb, PromoteElevatedVerb, StringComparison.OrdinalIgnoreCase);

    internal static bool IsDepromoteRequested(string? verb) =>
        string.Equals(verb, DepromoteElevatedVerb, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The verb-to-operation map. It is TOTAL and CLOSED: anything that is not
    /// one of the two exact spellings yields Unspecified, which authorizes
    /// nothing.
    /// </summary>
    internal static ServiceOwnershipElevatedOperation OperationFor(string? verb)
    {
        if (IsPromoteRequested(verb))
        {
            return ServiceOwnershipElevatedOperation.Promote;
        }

        return IsDepromoteRequested(verb)
            ? ServiceOwnershipElevatedOperation.Depromote
            : ServiceOwnershipElevatedOperation.Unspecified;
    }
}

/// <summary>
/// The CLOSED elevated parser. It is pure and total, and it returns false on the
/// first violation without reporting which one - so a refusal reveals nothing
/// about the accepted shape.
/// </summary>
internal static class ServiceOwnershipElevatedProtocol
{
    /// <summary>
    /// The elevated grammar. Order, count, spelling and value grammar are all
    /// required, and nothing is inferred or defaulted. The CALLER states which
    /// operation it is dispatching, and the parse succeeds only if the argument
    /// vector's own verb maps to exactly that operation - so a promote dispatch
    /// can never be reached with a depromote argument vector or the reverse.
    /// </summary>
    internal static bool TryParseElevated(
        string[]? argv,
        ServiceOwnershipElevatedOperation expectedOperation,
        out ServiceEnableElevatedArguments? parsed)
    {
        parsed = null;

        if (expectedOperation == ServiceOwnershipElevatedOperation.Unspecified)
        {
            return false;
        }

        if (argv is null || argv.Length != ServiceOwnershipElevatedVerbs.ElevatedTokenCount)
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

        if (ServiceOwnershipElevatedVerbs.OperationFor(argv[0]) != expectedOperation)
        {
            return false;
        }

        if (!string.Equals(argv[1], ServiceEnableVerbs.EndpointOption, StringComparison.Ordinal)
            || !string.Equals(argv[3], ServiceEnableVerbs.InitiatorProcessIdOption, StringComparison.Ordinal)
            || !string.Equals(argv[5], ServiceEnableVerbs.InitiatorCreatedOption, StringComparison.Ordinal))
        {
            return false;
        }

        if (!ServiceInitiatingUserIdentityChannelContract.IsCanonicalEndpointName(argv[2]))
        {
            return false;
        }

        // NumberStyles.None rejects a sign, a thousands separator, whitespace and
        // hex, so only bare decimal digits are ever accepted.
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
}
