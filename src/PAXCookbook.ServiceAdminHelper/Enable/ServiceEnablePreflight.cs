// PAX Cookbook - SERVICE-ENABLE PREFLIGHT (cycle 62, helper only)
//
// WHAT THIS FILE IS. The MUTATION-FREE gate that runs BEFORE the installation
// anchor is persisted and therefore before any Program Files or Service Control
// Manager state can be touched. It answers exactly one question - "may this
// transaction proceed" - from already-gathered observations.
//
// IT WRITES NOTHING, EVER. It creates no directory, writes no file, changes no
// access-control list, starts no process, elevates nothing, and creates,
// changes, starts, stops or deletes no service. The evaluator below is PURE: it
// takes a snapshot of observations and returns a bounded verdict, so the
// decision is reproducible from its arguments alone and can be tested without
// a machine.
//
// THE ONE RULE THAT MATTERS MOST. An ABSENT anchor combined with ANY existing
// installation footprint - a final directory, a staging directory, or a
// registered service - is RecoveryRequired, never "clean up and continue". A
// footprint with no anchor means state this product cannot prove it owns, and
// silently adopting it is exactly how an installation would be hijacked.
//
// PRIVACY - FAIL CLOSED. Every refusal is a bounded token. Nothing here formats
// a path, an identity, a native status or an exception.
using System;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>Bounded preflight verdict. Zero can never authorize a mutation.</summary>
internal enum ServiceEnablePreflightVerdict
{
    Unspecified = 0,

    /// <summary>The ONLY value that authorizes persisting the anchor and mutating machine state.</summary>
    Proceed = 1,

    /// <summary>A bounded refusal. Nothing exists that needs recovering.</summary>
    Refused = 2,

    /// <summary>
    /// Existing machine state cannot be explained by this transaction. It is
    /// NEVER repaired automatically.
    /// </summary>
    RecoveryRequired = 3,
}

/// <summary>The bounded reason. It names a category, never a value.</summary>
internal enum ServiceEnablePreflightReason
{
    Unspecified = 0,
    Satisfied = 1,

    /// <summary>The configured signing policy does not permit enablement in this build.</summary>
    SigningPolicyRefused = 2,

    /// <summary>The embedded payload is absent, ambiguous or not internally consistent.</summary>
    PayloadUnverified = 3,

    /// <summary>FOLDERID_ProgramFilesX64 did not resolve to a usable fixed-path tree.</summary>
    ProgramFilesUnavailable = 4,

    /// <summary>The fixed machine-wide runtime host is absent or is not a regular file.</summary>
    RuntimeHostUnusable = 5,

    /// <summary>A relevant path component is a reparse point.</summary>
    ReparsePointRefused = 6,

    /// <summary>An identity outside the trusted machine set can rewrite the runtime host.</summary>
    RuntimeHostWritableByUntrustedPrincipal = 7,

    /// <summary>The staging directory exists in a state this transaction does not recognise.</summary>
    StagingStateRefused = 8,

    /// <summary>The final directory exists and does not match the manifest or the approved profile.</summary>
    FinalStateRefused = 9,

    /// <summary>A service with the fixed name exists and does not match the fixed configuration.</summary>
    ServiceStateRefused = 10,

    /// <summary>An installation footprint exists with NO anchor to explain it.</summary>
    UnexplainedExistingFootprint = 11,

    /// <summary>Existing anchor state is conflicting, malformed or unreadable.</summary>
    AnchorStateRefused = 12,

    /// <summary>A bounded access or stability failure. Never a partial success.</summary>
    Unavailable = 13,
}

/// <summary>The bounded preflight result.</summary>
internal readonly struct ServiceEnablePreflightResult
{
    private ServiceEnablePreflightResult(
        ServiceEnablePreflightVerdict verdict, ServiceEnablePreflightReason reason)
    {
        Verdict = verdict;
        Reason = reason;
    }

    internal ServiceEnablePreflightVerdict Verdict { get; }

    internal ServiceEnablePreflightReason Reason { get; }

    internal static ServiceEnablePreflightResult Proceed() =>
        new(ServiceEnablePreflightVerdict.Proceed, ServiceEnablePreflightReason.Satisfied);

    internal static ServiceEnablePreflightResult Refused(ServiceEnablePreflightReason reason) =>
        new(
            ServiceEnablePreflightVerdict.Refused,
            reason == ServiceEnablePreflightReason.Satisfied ? ServiceEnablePreflightReason.Unavailable : reason);

    internal static ServiceEnablePreflightResult RecoveryRequired(ServiceEnablePreflightReason reason) =>
        new(
            ServiceEnablePreflightVerdict.RecoveryRequired,
            reason == ServiceEnablePreflightReason.Satisfied ? ServiceEnablePreflightReason.Unavailable : reason);

    public override string ToString() => Verdict.ToString();
}

/// <summary>Bounded classification of an existing fixed directory.</summary>
internal enum ServiceEnableDirectoryState
{
    Unspecified = 0,

    /// <summary>Nothing exists at the fixed path.</summary>
    Absent = 1,

    /// <summary>It exists and reproduces the manifest membership, bytes and approved profile exactly.</summary>
    ExactMatch = 2,

    /// <summary>
    /// Staging only: it exists and is a remnant this transaction recognises, so
    /// it may be removed and rebuilt rather than adopted.
    /// </summary>
    RecoverableRemnant = 3,

    /// <summary>It exists and is not ours. NEVER overwritten and never adopted.</summary>
    Foreign = 4,

    /// <summary>A bounded access or stability failure. Never treated as absence.</summary>
    Unavailable = 5,
}

/// <summary>
/// The complete set of MUTATION-FREE observations the verdict is computed from.
/// It is a plain snapshot: it holds no handle, performs no I/O, and grants no
/// capability, so the evaluator below is a pure function of it.
/// </summary>
internal readonly struct ServiceEnablePreflightObservations
{
    internal ServiceEnablePreflightObservations(
        bool signingPolicyPermits,
        bool payloadVerified,
        bool programFilesResolved,
        bool runtimeHostIsRegularFile,
        bool anyRelevantPathComponentIsReparsePoint,
        bool runtimeHostHasUntrustedWriteGrant,
        ServiceEnableDirectoryState staging,
        ServiceEnableDirectoryState final,
        ServiceQueryState servicePresence,
        bool serviceConfigurationMatchesExactly,
        bool anchorAbsent,
        bool anchorMatchesThisInitiator)
    {
        SigningPolicyPermits = signingPolicyPermits;
        PayloadVerified = payloadVerified;
        ProgramFilesResolved = programFilesResolved;
        RuntimeHostIsRegularFile = runtimeHostIsRegularFile;
        AnyRelevantPathComponentIsReparsePoint = anyRelevantPathComponentIsReparsePoint;
        RuntimeHostHasUntrustedWriteGrant = runtimeHostHasUntrustedWriteGrant;
        Staging = staging;
        Final = final;
        ServicePresence = servicePresence;
        ServiceConfigurationMatchesExactly = serviceConfigurationMatchesExactly;
        AnchorAbsent = anchorAbsent;
        AnchorMatchesThisInitiator = anchorMatchesThisInitiator;
    }

    internal bool SigningPolicyPermits { get; }

    internal bool PayloadVerified { get; }

    internal bool ProgramFilesResolved { get; }

    internal bool RuntimeHostIsRegularFile { get; }

    internal bool AnyRelevantPathComponentIsReparsePoint { get; }

    internal bool RuntimeHostHasUntrustedWriteGrant { get; }

    internal ServiceEnableDirectoryState Staging { get; }

    internal ServiceEnableDirectoryState Final { get; }

    internal ServiceQueryState ServicePresence { get; }

    internal bool ServiceConfigurationMatchesExactly { get; }

    internal bool AnchorAbsent { get; }

    /// <summary>
    /// True only when a VALID anchor exists whose immutable identity matches
    /// this transaction's initiator. A conflicting or malformed anchor is
    /// neither absent nor matching.
    /// </summary>
    internal bool AnchorMatchesThisInitiator { get; }

    /// <summary>Carries the bounded type name only.</summary>
    public override string ToString() => nameof(ServiceEnablePreflightObservations);
}

/// <summary>
/// The PURE preflight evaluator. It performs no I/O of any kind, holds no state
/// and accepts no delegate.
/// </summary>
internal static class ServiceEnablePreflight
{
    internal static ServiceEnablePreflightResult Evaluate(ServiceEnablePreflightObservations observations)
    {
        // ORDER MATTERS. The cheapest, most absolute refusals come first, so a
        // build that may not enable a service never even reasons about machine
        // state, and a payload that does not verify is refused before any path
        // question is asked.
        if (!observations.SigningPolicyPermits)
        {
            return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.SigningPolicyRefused);
        }

        if (!observations.PayloadVerified)
        {
            return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.PayloadUnverified);
        }

        if (!observations.ProgramFilesResolved)
        {
            return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.ProgramFilesUnavailable);
        }

        if (!observations.RuntimeHostIsRegularFile)
        {
            return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.RuntimeHostUnusable);
        }

        if (observations.AnyRelevantPathComponentIsReparsePoint)
        {
            return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.ReparsePointRefused);
        }

        if (observations.RuntimeHostHasUntrustedWriteGrant)
        {
            return ServiceEnablePreflightResult.Refused(
                ServiceEnablePreflightReason.RuntimeHostWritableByUntrustedPrincipal);
        }

        // THE UNEXPLAINED-FOOTPRINT RULE, evaluated before any state is judged
        // acceptable. An installation footprint with no anchor is state this
        // product cannot prove it owns, and adopting it is how an installation
        // would be hijacked.
        if (observations.AnchorAbsent && HasAnyFootprint(observations))
        {
            return ServiceEnablePreflightResult.RecoveryRequired(
                ServiceEnablePreflightReason.UnexplainedExistingFootprint);
        }

        // An anchor that is neither absent nor a match for this initiator means
        // the channel's own classification did not hold. Fail closed.
        if (!observations.AnchorAbsent && !observations.AnchorMatchesThisInitiator)
        {
            return ServiceEnablePreflightResult.RecoveryRequired(
                ServiceEnablePreflightReason.AnchorStateRefused);
        }

        // A bounded access failure is NEVER read as absence.
        if (observations.Staging == ServiceEnableDirectoryState.Unavailable
            || observations.Staging == ServiceEnableDirectoryState.Unspecified)
        {
            return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.Unavailable);
        }

        if (observations.Final == ServiceEnableDirectoryState.Unavailable
            || observations.Final == ServiceEnableDirectoryState.Unspecified)
        {
            return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.Unavailable);
        }

        if (observations.ServicePresence is not ServiceQueryState.Absent and not ServiceQueryState.Present)
        {
            return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.Unavailable);
        }

        // Staging may be absent or a remnant this transaction recognises. It is
        // never adopted as though it were a finished installation.
        if (observations.Staging is not ServiceEnableDirectoryState.Absent
            and not ServiceEnableDirectoryState.RecoverableRemnant)
        {
            return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.StagingStateRefused);
        }

        // Final must be absent or an EXACT match. Foreign is never overwritten.
        if (observations.Final is not ServiceEnableDirectoryState.Absent
            and not ServiceEnableDirectoryState.ExactMatch)
        {
            return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.FinalStateRefused);
        }

        // A service carrying the fixed name that does not match the fixed
        // configuration is someone else's. It is refused and never touched.
        if (observations.ServicePresence == ServiceQueryState.Present
            && !observations.ServiceConfigurationMatchesExactly)
        {
            return ServiceEnablePreflightResult.Refused(ServiceEnablePreflightReason.ServiceStateRefused);
        }

        return ServiceEnablePreflightResult.Proceed();
    }

    /// <summary>
    /// True when ANY installation footprint exists: a final directory, a staging
    /// directory, or a registered service. Used to decide whether an absent
    /// anchor is a clean first enable or an unexplained state.
    /// </summary>
    internal static bool HasAnyFootprint(ServiceEnablePreflightObservations observations) =>
        observations.Final != ServiceEnableDirectoryState.Absent
        || observations.Staging != ServiceEnableDirectoryState.Absent
        || observations.ServicePresence != ServiceQueryState.Absent;
}
