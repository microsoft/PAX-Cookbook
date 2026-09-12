using System;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

// ---------------------------------------------------------------------------
// FIXED OWNER-IDENTITY ADAPTER - cycle 92 pass B, OWNER-CONTINUITY BOUND IN
// CYCLE 96. NON-LIVE.
//
// WHAT THIS IS. The production implementation of the promotion transaction's
// owner-identity port. The owning user SID is derived ONLY from the
// already-validated installation anchor document. It is NEVER the elevated
// helper's own identity, never a caller-supplied SID, never an environment
// value, and never a path.
//
// WHAT CYCLE 96 CHANGED, AND WHY IT MATTERED. Until cycle 96 this port took NO
// constructor argument. It re-read the anchor UNBOUND and returned whatever
// initiating-user SID it happened to find, so the identity the channel had
// already authenticated played no part in the answer. The channel always held
// that identity - the anchor it hands the transaction is built from the
// kernel-derived SID and the validated installation id - and the port simply
// discarded it. Now the port is CONSTRUCTED FROM that exact validated document
// and retains only its two bounded fields. Its one read is a REREAD, and the
// reread anchor must match BOTH fixed fields exactly before any SID is returned.
// The SID it returns is the CONSTRUCTOR-FIXED one, never the reread one, so a
// value that was merely observed can never become the answer.
//
// HOW IT IS SPLIT (the cycle-39/49 precedent). There is NO injectable native
// seam, because a seam is itself a capability:
//   * ServiceOwnershipOwnerIdentityInterpreter - a PURE interpreter over a small
//     set of BOUNDED OBSERVED ANCHOR FACTS. Zero I/O, exhaustively unit-tested,
//     and the sole home of every decision.
//   * ServiceOwnershipFixedOwnerIdentityPort.ObserveFixedOwner() - the FIXED
//     SHIM, taking NO parameter at all, so a caller can never redirect what it
//     reads. Its one read goes through the certified anchor store.
//
// THE SID SHAPE RULE IS THE LEDGER'S, DELIBERATELY. The ownership ledger records
// this SID, and its own certified transition facts apply
// ServiceOwnershipLedgerContract.IsUserOwnerSid to it. Applying a BROADER rule
// here would only move the refusal one step later while making the two ends
// disagree, so the same certified pair is applied at both.
//
// PRIVACY. Nothing but a bounded state token and, on success, the anchor's own
// recorded owner SID leaves this file. No path, no reason, no exception text.
// ---------------------------------------------------------------------------

/// <summary>
/// BOUNDED OBSERVED ANCHOR FACTS. Never a path, never an environment value,
/// never a delegate: only whether the certified anchor REREAD produced a
/// validated, accepted document, the two bounded values that document recorded,
/// and the two the port's constructor fixed from the already-validated anchor.
///
/// The observed pair and the expected pair are carried SEPARATELY on purpose.
/// Collapsing them would make the comparison unrepresentable, which is exactly
/// the state this type was in before cycle 96.
/// </summary>
internal readonly struct ServiceOwnershipObservedAnchorFacts
{
    private readonly string? observedInstallationId;
    private readonly string? observedInitiatingUserSid;
    private readonly string? expectedInstallationId;
    private readonly string? expectedInitiatingUserSid;

    internal ServiceOwnershipObservedAnchorFacts(
        bool anchorValidated,
        bool anchorAccepted,
        bool documentPresent,
        string? observedInstallationId,
        string? observedInitiatingUserSid,
        string? expectedInstallationId,
        string? expectedInitiatingUserSid)
    {
        AnchorValidated = anchorValidated;
        AnchorAccepted = anchorAccepted;
        DocumentPresent = documentPresent;
        this.observedInstallationId = observedInstallationId;
        this.observedInitiatingUserSid = observedInitiatingUserSid;
        this.expectedInstallationId = expectedInstallationId;
        this.expectedInitiatingUserSid = expectedInitiatingUserSid;
    }

    /// <summary>The anchor read reported the Validated state, not Absent/Refused/Unavailable.</summary>
    internal bool AnchorValidated { get; }

    /// <summary>The certified anchor validator accepted the document.</summary>
    internal bool AnchorAccepted { get; }

    internal bool DocumentPresent { get; }

    /// <summary>The installation id the REREAD anchor recorded.</summary>
    internal string ObservedInstallationId => observedInstallationId ?? string.Empty;

    /// <summary>The SID the REREAD anchor recorded. Never a caller assertion.</summary>
    internal string ObservedInitiatingUserSid => observedInitiatingUserSid ?? string.Empty;

    /// <summary>The installation id the port's constructor fixed. Never re-derived.</summary>
    internal string ExpectedInstallationId => expectedInstallationId ?? string.Empty;

    /// <summary>The SID the port's constructor fixed. The ONLY value that can be returned.</summary>
    internal string ExpectedInitiatingUserSid => expectedInitiatingUserSid ?? string.Empty;
}

/// <summary>
/// PURE interpreter over the bounded anchor facts. It performs no I/O of any
/// kind, holds no state and accepts no delegate, so every decision is
/// reproducible from its arguments alone.
///
/// THE REFUSALS ARE DELIBERATELY DIFFERENT. An anchor that produced nothing is
/// Unavailable, while an anchor that produced something which is not a user
/// owner is NotUserShaped. An anchor that now names a DIFFERENT installation, or
/// a DIFFERENT user, gets its own token, because those two are the
/// ownership-substitution cases and reporting them as merely "unavailable" would
/// hide the only failure mode that matters here.
/// </summary>
internal static class ServiceOwnershipOwnerIdentityInterpreter
{
    internal static ServiceOwnershipOwnerIdentityObservation Interpret(
        ServiceOwnershipObservedAnchorFacts facts)
    {
        if (!facts.AnchorValidated || !facts.AnchorAccepted || !facts.DocumentPresent)
        {
            return ServiceOwnershipOwnerIdentityObservation.Failure(
                ServiceOwnershipOwnerIdentityState.Unavailable);
        }

        string expectedSid = facts.ExpectedInitiatingUserSid;
        string expectedInstallation = facts.ExpectedInstallationId;
        string observedSid = facts.ObservedInitiatingUserSid;
        string observedInstallation = facts.ObservedInstallationId;

        if (expectedSid.Length == 0
            || expectedInstallation.Length == 0
            || observedSid.Length == 0
            || observedInstallation.Length == 0)
        {
            return ServiceOwnershipOwnerIdentityObservation.Failure(
                ServiceOwnershipOwnerIdentityState.Unavailable);
        }

        // THE INSTALLATION MUST STILL BE THE SAME ONE. A reread anchor that names
        // another installation is not this transaction's anchor, whatever else it
        // may say.
        if (!string.Equals(observedInstallation, expectedInstallation, StringComparison.Ordinal))
        {
            return ServiceOwnershipOwnerIdentityObservation.Failure(
                ServiceOwnershipOwnerIdentityState.InstallationMismatch);
        }

        // AND THE OWNER MUST STILL BE THE SAME ONE. Same installation, different
        // user, is precisely the shape an ownership takeover would have.
        if (!string.Equals(observedSid, expectedSid, StringComparison.Ordinal))
        {
            return ServiceOwnershipOwnerIdentityObservation.Failure(
                ServiceOwnershipOwnerIdentityState.OwnerMismatch);
        }

        // Both gates are applied to the CONSTRUCTOR-FIXED value: canonical SID
        // STRING form, and the user-owner shape. The certified user-owner rule
        // already refuses every well-known and per-service principal, so a service
        // SID can never be observed here as an owner.
        if (!ServiceOwnershipLedgerContract.IsCanonicalSidString(expectedSid)
            || !ServiceOwnershipLedgerContract.IsUserOwnerSid(expectedSid))
        {
            return ServiceOwnershipOwnerIdentityObservation.Failure(
                ServiceOwnershipOwnerIdentityState.NotUserShaped);
        }

        // THE ANSWER IS THE FIXED VALUE, NOT THE OBSERVED ONE. The two were just
        // proven equal, and returning the fixed copy means a merely observed value
        // can never become the owner.
        return ServiceOwnershipOwnerIdentityObservation.Observed(expectedSid);
    }
}

/// <summary>
/// The FIXED SHIM. It is CONSTRUCTED from the ONE validated installation anchor
/// document the channel already authenticated, and retains only that document's
/// two bounded fields. Its ONE entry point takes NO parameter, so nothing about
/// what it reads is caller-controlled; its single read goes through the certified
/// installation anchor store and is a REREAD of the fixed anchor. Every decision
/// that follows belongs to the pure interpreter above.
/// </summary>
internal sealed class ServiceOwnershipFixedOwnerIdentityPort : IServiceOwnershipOwnerIdentityPort
{
    private readonly string fixedInstallationId;
    private readonly string fixedInitiatingUserSid;

    /// <summary>
    /// The ONE constructor. It accepts the CLOSED validated anchor document and
    /// nothing else - no raw SID, no installation-id string, no path, no delegate,
    /// no callback, no strategy and no environment value.
    /// </summary>
    internal ServiceOwnershipFixedOwnerIdentityPort(ServiceInstallationAnchorDocument anchor)
    {
        fixedInstallationId = anchor is null ? string.Empty : anchor.InstallationId;
        fixedInitiatingUserSid = anchor is null ? string.Empty : anchor.InitiatingUserSid;
    }

    public ServiceOwnershipOwnerIdentityObservation ObserveFixedOwner()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ServiceOwnershipOwnerIdentityObservation.Failure(
                ServiceOwnershipOwnerIdentityState.Unavailable);
        }

        ServiceInstallationAnchorReadResult read = ServiceInstallationAnchorStore.Read();
        ServiceInstallationAnchorValidationResult? validation = read.Validation;

        var facts = new ServiceOwnershipObservedAnchorFacts(
            read.State == ServiceInstallationAnchorReadState.Validated,
            validation is not null && validation.IsAccepted,
            validation?.Document is not null,
            validation?.Document?.InstallationId,
            validation?.Document?.InitiatingUserSid,
            fixedInstallationId,
            fixedInitiatingUserSid);

        return ServiceOwnershipOwnerIdentityInterpreter.Interpret(facts);
    }
}
