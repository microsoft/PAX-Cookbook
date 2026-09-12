// PAX Cookbook - SERVICE-ENABLE FAILURE CONTRACT (cycle 67)
//
// WHY THIS FILE EXISTS. The first real attended service-enable failed with a
// bare process exit code 1 and NO bounded reason. Three separate defects made
// that inevitable:
//
//   1. ServiceEnableTransaction.Compensate OVERWROTE the original failure cause
//      with the DISPOSITION "Compensated", destroying the only record of why.
//   2. ServiceInitiatingUserIdentityChannelOutcome distinguishes preflight
//      refusal, compensation and recovery-required, but carries NO cause.
//   3. ServiceEnableElevatedDispatch collapsed every non-Completed outcome onto
//      one Setup exit code, and the coordinator collapsed nearly every channel
//      outcome onto AcknowledgementNotReceived.
//
// THE FIX IS A CLOSED, TWO-AXIS VOCABULARY. A CAUSE says WHY the operation
// failed. A DISPOSITION says WHAT WAS LEFT BEHIND. They are separate enums and
// are NEVER collapsed, because "compensated" is not a reason and "extraction
// refused" is not a state of the machine.
//
// WHAT THIS FILE IS COMPILED INTO. It is compiled into PAXCookbookSetup and
// COMPILE-LINKED into PAXCookbook.ServiceAdminHelper, exactly like
// ServiceEnableProtocol.cs, so the elevated helper, the non-elevated
// coordinator and both focused test classes read the same members, the same
// exit-code arithmetic and the same lowercase tokens from ONE source. No
// project retypes a code, a band or a token.
//
// THE EXISTING ServiceEnableOperationOutcome IS NOT TOUCHED. It CONFLATES cause
// and disposition (it contains Completed, Compensated and RecoveryRequired
// alongside real causes) and roughly 133 existing tests depend on its exact
// members. This file MAPS from it instead of mutating it.
//
// PRIVACY - FAIL CLOSED. Nothing here formats or forwards a path, a SID, an
// account, a native error code, a timestamp, a process identifier or an
// exception. The whole outward surface is one integer inside a documented band
// and two fixed lowercase tokens.
using System;
using PAXCookbook.Shared.ExitCodes;

namespace PAXCookbookSetup.Service;

/// <summary>
/// WHY one service-enable attempt failed. Every member names a CATEGORY, never
/// a value. Zero is <see cref="None"/> and is the permanent, safe default: a
/// cause of None is only ever truthful together with a disposition of
/// <see cref="ServiceEnableFailureDisposition.Completed"/>.
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data. No member carries data.
/// </summary>
public enum ServiceEnableFailureCause
{
    /// <summary>No failure. Only ever paired with a Completed disposition.</summary>
    None = 0,

    /// <summary>The configured signing policy does not permit enablement in this build.</summary>
    SigningPolicyRefused = 1,

    /// <summary>The embedded payload is absent, ambiguous or not internally consistent.</summary>
    PayloadRefused = 2,

    /// <summary>
    /// The fixed Program Files tree, the machine-wide runtime host, or a
    /// relevant path component did not satisfy the mutation-free preflight.
    /// </summary>
    ProgramFilesPreflightRefused = 3,

    /// <summary>
    /// Existing machine state - a staging or final directory, a registered
    /// service, an unexplained footprint, or the installation anchor - was
    /// refused. Never adopted and never repaired.
    /// </summary>
    ExistingStateRefused = 4,

    /// <summary>Extraction into the fixed staging or final location refused.</summary>
    ExtractionRefused = 5,

    /// <summary>Service creation, SID-type configuration or profile application refused.</summary>
    RegistrationRefused = 6,

    /// <summary>Final membership, byte or access-control verification did not reproduce the exact expected state.</summary>
    VerificationRefused = 7,

    /// <summary>One of the three machine data access-control profiles could not be applied or did not verify exactly.</summary>
    MachineDataProtectionRefused = 8,

    /// <summary>The service did not start, never reached Running, or failed one of the run-state proofs.</summary>
    StartabilityRefused = 9,

    /// <summary>
    /// A bounded access or stability failure, or a failure whose finer category
    /// is genuinely not known. It is NEVER a partial success and it is never a
    /// guess dressed up as a category.
    /// </summary>
    Unavailable = 10,

    // ---- CYCLE 71: THE POST-CREATE ENABLE STAGE ----------------------------
    //
    // FIVE distinct production refusals collapsed onto RegistrationRefused, so
    // the one line that crosses the process boundary could not tell them apart.
    // FOUR of them get their own cause here; the fifth - an existing service
    // that does not match exactly - belongs to the ALREADY EXISTING
    // ExistingStateRefused category and is reclassified onto it rather than
    // given a sixth name.
    //
    // ORDINALS 11 AND ABOVE LIVE IN A SEPARATE EXIT-CODE REGION. The legacy
    // ordinals 1-10 exactly saturated the cycle-67 bands, so these members must
    // never be fed through the legacy base-plus-ordinal arithmetic.

    /// <summary>CreateServiceW itself refused. The service was never registered.</summary>
    ServiceCreationRefused = 11,

    /// <summary>
    /// CONFIGURING the unrestricted service SID type refused. Distinct from
    /// VERIFYING it, which has always reported VerificationRefused.
    /// </summary>
    ServiceSidConfigurationRefused = 12,

    /// <summary>
    /// The fixed service SID could not be resolved for a service that was
    /// already proven present and already configured unrestricted.
    /// </summary>
    ServiceSidResolutionRefused = 13,

    /// <summary>
    /// The FINAL Program Files access-control profile could not be applied to
    /// every directory and file. Distinct from verifying it afterwards.
    /// </summary>
    ProgramFilesProtectionRefused = 14,

    // ---- CYCLE 74: THE STARTABILITY STAGE ---------------------------------
    //
    // SIX distinct activation refusals - StartServiceW itself, never reaching
    // Running, the process identity proof, the status document, the advancing
    // heartbeat and a forbidden child - collapsed onto the ONE
    // StartabilityRefused category, so the single line that crosses the process
    // boundary could not tell them apart. The attended cycle-73 run proved this
    // matters: it reached startability_refused, which is the LAST and WIDEST
    // category in the pipeline and therefore the least informative one.
    //
    // FIVE MORE come from StartServiceW's own DOCUMENTED return codes. They are
    // sub-classifications of "the start call refused" and each is grounded in
    // the published StartServiceW return-value table, not invented.
    //
    // StartabilityRefused (ordinal 9) is RETAINED so any already-emitted code
    // still DECODES, but no live branch maps to it after this cycle.

    /// <summary>
    /// StartServiceW refused and the reason has no finer documented category, or
    /// the attempt failed before StartServiceW could be called. THE CLOSED
    /// FALLBACK for the start call.
    /// </summary>
    ServiceStartRefused = 15,

    /// <summary>
    /// The SCM ACCEPTED the start and the service never reported Running inside
    /// the bounded wait. Distinct from
    /// <see cref="ServiceStartRequestTimeout"/>, which is a SYNCHRONOUS refusal
    /// of the start call itself.
    /// </summary>
    ServiceRunningTimeout = 16,

    /// <summary>The live process is not the fixed service identity, or is not in Session 0.</summary>
    ServiceProcessIdentityRefused = 17,

    /// <summary>
    /// The status document failed the closed schema, state, session or
    /// interactivity rules. RETAINED FOR BACKWARD DECODING ONLY - no live branch
    /// emits it after cycle 75, which replaced it with eight bounded causes.
    /// </summary>
    ServiceStatusDocumentRefused = 18,

    /// <summary>Two valid, strictly advancing heartbeat observations were not seen in the window.</summary>
    ServiceHeartbeatRefused = 19,

    /// <summary>A PAX or Bake child process was observed beneath the service process.</summary>
    ServiceForbiddenChildRefused = 20,

    /// <summary>ERROR_SERVICE_LOGON_FAILED. The account cannot log on as a service.</summary>
    ServiceStartLogonRefused = 21,

    /// <summary>ERROR_ACCESS_DENIED. The handle did not carry the SERVICE_START right.</summary>
    ServiceStartAccessRefused = 22,

    /// <summary>ERROR_PATH_NOT_FOUND. The service binary file could not be found.</summary>
    ServiceStartBinaryUnavailable = 23,

    /// <summary>ERROR_SERVICE_DEPENDENCY_FAIL or ERROR_SERVICE_DEPENDENCY_DELETED.</summary>
    ServiceStartDependencyRefused = 24,

    /// <summary>
    /// ERROR_SERVICE_REQUEST_TIMEOUT - a SYNCHRONOUS StartServiceW refusal. It
    /// is NEVER folded into <see cref="ServiceRunningTimeout"/>: StartServiceW
    /// returns as soon as the dispatcher reports the ServiceMain thread was
    /// created and does not wait for the first status update, so "the call
    /// refused" and "the service never ran" are different stages.
    /// </summary>
    ServiceStartRequestTimeout = 25,

    // ---- CYCLE 75: THE BOUNDED STATUS-READINESS STAGE ---------------------
    //
    // THE ATTENDED CYCLE-74 RUN reached service_status_document_refused, and
    // that single token is EQUALLY CONSISTENT with a missing document, a service
    // still "starting", a malformed or unreadable document, and a wrong
    // execution context. The cause was therefore INFERRED, never observed.
    // These eight causes exist so the NEXT attended run is decisive whichever
    // way it goes.
    //
    // THE FIRST TWO ARE DEADLINE causes and are distinguished by the LAST
    // TRANSIENT STATE actually observed inside the bounded window. The other six
    // are TERMINAL and are decided on a single observation with no further wait.
    //
    // ServiceStatusDocumentRefused (ordinal 18) is RETAINED so any
    // already-emitted code still DECODES, but no live branch maps to it after
    // this cycle.

    /// <summary>The bounded readiness window expired having observed ONLY an absent document.</summary>
    ServiceStatusMissingTimeout = 26,

    /// <summary>
    /// The bounded readiness window expired after at least one valid,
    /// correct-context "starting" document. Distinct from
    /// <see cref="ServiceStatusMissingTimeout"/> because the service was
    /// demonstrably coming up rather than never writing anything.
    /// </summary>
    ServiceStatusStartingTimeout = 27,

    /// <summary>The status document exceeded the hard size bound, or could not be read.</summary>
    ServiceStatusUnreadable = 28,

    /// <summary>The status document failed the strict UTF-8, JSON, property-set or value rules.</summary>
    ServiceStatusMalformed = 29,

    /// <summary>
    /// The status document reported a session other than 0, or an interactive
    /// host. THIS IS AN EXECUTION-CONTEXT REFUSAL and it OUTRANKS every
    /// lifecycle state; before cycle 75 a healthy "starting" was wrongly
    /// reported through this same channel.
    /// </summary>
    ServiceStatusWrongContext = 30,

    /// <summary>The status document reported the lifecycle state "stopped".</summary>
    ServiceStatusStopped = 31,

    /// <summary>The status document reported the lifecycle state "failed".</summary>
    ServiceStatusFailed = 32,

    /// <summary>The status document reported a state string this build does not recognise.</summary>
    ServiceStatusUnknownState = 33,
}

/// <summary>
/// WHAT WAS LEFT BEHIND by one service-enable attempt. It answers a different
/// question from <see cref="ServiceEnableFailureCause"/> and the two are never
/// merged. Zero is the permanent, safe default so an uninitialised value can
/// never read as success.
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data.
/// </summary>
public enum ServiceEnableFailureDisposition
{
    /// <summary>Not yet determined. Never success.</summary>
    None = 0,

    /// <summary>The attempt refused before anything durable was mutated.</summary>
    RefusedBeforeMutation = 1,

    /// <summary>The attempt failed and every piece of state it created was provably removed.</summary>
    Compensated = 2,

    /// <summary>
    /// Machine state cannot be proven closed. An attended, elevated recovery is
    /// the only remedy. Never a success and never automatically repaired.
    /// </summary>
    RecoveryRequired = 3,

    /// <summary>Applied and verified exactly. The ONLY success disposition.</summary>
    Completed = 4,
}

/// <summary>
/// One cause paired with one disposition. It is a plain snapshot: it holds no
/// handle, performs no I/O and grants no capability.
/// </summary>
internal readonly struct ServiceEnableFailureClassification
{
    internal ServiceEnableFailureClassification(
        ServiceEnableFailureCause cause, ServiceEnableFailureDisposition disposition)
    {
        Cause = cause;
        Disposition = disposition;
    }

    internal ServiceEnableFailureCause Cause { get; }

    internal ServiceEnableFailureDisposition Disposition { get; }

    /// <summary>The ONLY success shape: no cause at all, and a Completed disposition.</summary>
    internal bool IsCompleted =>
        Cause == ServiceEnableFailureCause.None
        && Disposition == ServiceEnableFailureDisposition.Completed;

    /// <summary>Carries the bounded type name only - never a token, code, path or identity.</summary>
    public override string ToString() => nameof(ServiceEnableFailureClassification);
}

/// <summary>
/// CYCLE 67. The narrow, read-only seam by which the elevated dispatch learns a
/// bound transaction's STICKY first cause and its FINAL disposition.
///
/// IT IS DELIBERATELY NOT PART OF IServiceIdentityBoundTransaction. That
/// interface is shared with the disable transaction, whose causes are a
/// different vocabulary; widening it would hand every bound transaction an
/// enable-shaped failure surface it has no reason to carry.
/// </summary>
internal interface IServiceEnableFailureClassified
{
    /// <summary>The FIRST failure cause observed. Once set it is never overwritten.</summary>
    ServiceEnableFailureCause FailureCause { get; }

    /// <summary>The FINAL disposition. Compensation may change this and may never change the cause.</summary>
    ServiceEnableFailureDisposition FailureDisposition { get; }
}

/// <summary>
/// THE ONE SOURCE AUTHORITY for the cause/disposition vocabulary, the helper
/// exit-code band, and the bounded Setup diagnostic tokens. It is PURE: no file
/// access, no process start, no elevation, no registry, no service control, no
/// network access, no PAX and no Bake.
/// </summary>
internal static class ServiceEnableFailureContract
{
    // ---- THE HELPER EXIT-CODE BAND ------------------------------------------
    //
    // A BOUNDED, DOCUMENTED, DELIBERATELY NON-CONTIGUOUS SET. It does NOT reuse
    // or overload any existing SetupExitCodes meaning. SetupExitCodes occupies
    // 0-3, 50-54, 60-62, 70-71, 80-83, 90-91 and 200.
    //
    // CYCLE 67 defined ONE contiguous window, [140, 180], and stated so here.
    // THAT STATEMENT IS NO LONGER TRUE AND HAS BEEN CORRECTED RATHER THAN LEFT
    // STANDING. The codes an enable helper can emit for a transaction outcome
    // are now exactly:
    //
    //     0                     the ONE success pair
    //     140                   the contradictory / unclassified pair
    //     151-180               the TEN LEGACY causes x three dispositions
    //     311-333, 411-433,     the EXTENSION causes x three dispositions
    //     511-533               (ordinals 11 and above)
    //
    // 1-139, 141-150, 181-310, 334-410, 434-510 and 534+ are NOT emitted and
    // decode fail-closed.
    //
    // WHY THE BANDS WERE NOT WIDENED OR RENUMBERED. The legacy bands 151-160,
    // 161-170 and 171-180 were EXACTLY SATURATED at ten causes. Appending an
    // eleventh cause to the same base-plus-ordinal arithmetic would have made
    // RefusedBeforeMutation emit 161 - which is already Compensated's
    // SigningPolicyRefused - and because FromExitCode tests the bands IN ORDER,
    // a genuine recovery_required would have decoded as compensated. An
    // operator would then have been told the machine was clean when it was not.
    // So the legacy region is FROZEN, every legacy pair keeps its EXACT code,
    // and new causes live in three SEPARATE regions spaced a full
    // <see cref="ExtendedRegionStride"/> apart.

    /// <summary>Zero is success, and NOTHING else ever maps to zero.</summary>
    internal const int SuccessExitCode = 0;

    /// <summary>
    /// The single code for a cause/disposition pair that is INTERNALLY
    /// CONTRADICTORY - for example a cause with no disposition, or a Completed
    /// disposition carrying a cause. It reads as recovery-required on the way
    /// back, because a contradiction is never proof that nothing happened.
    /// </summary>
    internal const int UnclassifiedExitCode = 140;

    /// <summary>LEGACY region. Codes 151-160. Nothing durable was mutated.</summary>
    internal const int RefusedBeforeMutationBase = 150;

    /// <summary>LEGACY region. Codes 161-170. Everything this attempt created was provably removed.</summary>
    internal const int CompensatedBase = 160;

    /// <summary>LEGACY region. Codes 171-180. Machine state cannot be proven closed.</summary>
    internal const int RecoveryRequiredBase = 170;

    /// <summary>The lowest ordinal a real cause can have.</summary>
    internal const int FirstCauseOrdinal = (int)ServiceEnableFailureCause.SigningPolicyRefused;

    /// <summary>
    /// The highest ordinal in the FROZEN LEGACY region. It is NOT the highest
    /// ordinal a cause can have - see <see cref="ExtendedLastCauseOrdinal"/>.
    /// Every ordinal at or below this value keeps its exact pre-cycle-71 code.
    /// </summary>
    internal const int LastCauseOrdinal = (int)ServiceEnableFailureCause.Unavailable;

    /// <summary>Inclusive first code of the LEGACY contiguous window.</summary>
    internal const int BandFirstExitCode = UnclassifiedExitCode;

    /// <summary>Inclusive last code of the LEGACY contiguous window.</summary>
    internal const int BandLastExitCode = RecoveryRequiredBase + LastCauseOrdinal;

    // ---- THE EXTENSION REGIONS (CYCLE 71) -----------------------------------

    /// <summary>
    /// The lowest ordinal that lives OUTSIDE the frozen legacy region. DERIVED
    /// from the enum, never hand-typed.
    /// </summary>
    internal const int ExtendedFirstCauseOrdinal =
        (int)ServiceEnableFailureCause.ServiceCreationRefused;

    /// <summary>
    /// The highest ordinal currently defined. DERIVED from the enum, never
    /// hand-typed, so adding a cause and forgetting this constant is a compile
    /// -time edit rather than a silent mapping hole.
    ///
    /// IT MUST BE REPOINTED TO THE FINAL DECLARED CAUSE EVERY TIME A CAUSE IS
    /// APPENDED. It bounds BOTH the encode range in <see cref="ToExitCode"/> and
    /// the decode slot-membership test in <see cref="IsInExtendedRegion"/>. A
    /// cause appended without repointing it does not fail to compile and does
    /// not fail a token test: its ordinals simply fall outside both bounds, so
    /// they encode to <see cref="UnclassifiedExitCode"/> and decode fail-closed
    /// to unavailable/recovery_required. The whole extension would be inert
    /// while looking healthy.
    /// </summary>
    internal const int ExtendedLastCauseOrdinal =
        (int)ServiceEnableFailureCause.ServiceStatusUnknownState;

    /// <summary>
    /// The spacing between extension region bases. THE BINDING INVARIANT: while
    /// <c>ExtendedLastCauseOrdinal &lt; ExtendedRegionStride</c> holds, two
    /// extension regions are ARITHMETICALLY INCAPABLE of overlapping, so the
    /// saturation trap that forced this design can never recur. Adding causes
    /// past ordinal 99 requires a new region layout, not another append.
    /// </summary>
    internal const int ExtendedRegionStride = 100;

    /// <summary>EXTENSION region. Base 300, so ordinals 11-33 emit 311-333.</summary>
    internal const int ExtendedRefusedBeforeMutationBase = 300;

    /// <summary>EXTENSION region. Base 400, so ordinals 11-33 emit 411-433.</summary>
    internal const int ExtendedCompensatedBase = 400;

    /// <summary>EXTENSION region. Base 500, so ordinals 11-33 emit 511-533.</summary>
    internal const int ExtendedRecoveryRequiredBase = 500;

    // ---- THE BOUNDED SETUP DIAGNOSTIC ---------------------------------------

    /// <summary>The fixed leading token of the single bounded Setup diagnostic line.</summary>
    internal const string DiagnosticPrefix = "service-enable";

    /// <summary>
    /// THE TOTAL cause/disposition to exit-code map. Every input pair, including
    /// values cast from outside the declared enums, produces exactly one code,
    /// and only the one success pair produces zero.
    ///
    /// TWO DISJOINT REGIONS. Ordinals 1-10 go through the FROZEN legacy
    /// arithmetic and are byte-for-byte what they were before cycle 71.
    /// Ordinals 11 and above go through the extension arithmetic. No ordinal
    /// reaches both, and the two regions cannot produce the same code.
    /// </summary>
    internal static int ToExitCode(
        ServiceEnableFailureCause cause, ServiceEnableFailureDisposition disposition)
    {
        if (cause == ServiceEnableFailureCause.None
            && disposition == ServiceEnableFailureDisposition.Completed)
        {
            return SuccessExitCode;
        }

        int ordinal = (int)cause;

        if (ordinal >= FirstCauseOrdinal && ordinal <= LastCauseOrdinal)
        {
            return disposition switch
            {
                ServiceEnableFailureDisposition.RefusedBeforeMutation => RefusedBeforeMutationBase + ordinal,
                ServiceEnableFailureDisposition.Compensated => CompensatedBase + ordinal,
                ServiceEnableFailureDisposition.RecoveryRequired => RecoveryRequiredBase + ordinal,
                _ => UnclassifiedExitCode,
            };
        }

        if (ordinal >= ExtendedFirstCauseOrdinal && ordinal <= ExtendedLastCauseOrdinal)
        {
            return disposition switch
            {
                ServiceEnableFailureDisposition.RefusedBeforeMutation =>
                    ExtendedRefusedBeforeMutationBase + ordinal,
                ServiceEnableFailureDisposition.Compensated => ExtendedCompensatedBase + ordinal,
                ServiceEnableFailureDisposition.RecoveryRequired =>
                    ExtendedRecoveryRequiredBase + ordinal,
                _ => UnclassifiedExitCode,
            };
        }

        // No cause (or an undefined cast) with a non-Completed disposition is a
        // contradiction, not a success and not a category.
        return UnclassifiedExitCode;
    }

    /// <summary>
    /// THE TOTAL inverse. It accepts ANY integer, because a process exit code is
    /// an untrusted observation, and it never guesses a specific cause.
    ///
    /// THE THREE LEGACY CODES ARE RECOGNISED DELIBERATELY. UsageError and
    /// UnsupportedWindowsVersion are returned by the enable helper strictly
    /// BEFORE the pipe exists, so classifying them as refused-before-mutation is
    /// a proven fact rather than an assumption. Every other unrecognised code -
    /// including GenericError, which this cycle removed from the enable helper's
    /// transaction paths - is treated as RECOVERY REQUIRED, so a stale or
    /// mismatched helper binary is loudly wrong instead of quietly "safe".
    /// </summary>
    internal static ServiceEnableFailureClassification FromExitCode(int exitCode)
    {
        if (exitCode == SuccessExitCode)
        {
            return new ServiceEnableFailureClassification(
                ServiceEnableFailureCause.None, ServiceEnableFailureDisposition.Completed);
        }

        if (exitCode > RefusedBeforeMutationBase
            && exitCode <= RefusedBeforeMutationBase + LastCauseOrdinal)
        {
            return new ServiceEnableFailureClassification(
                (ServiceEnableFailureCause)(exitCode - RefusedBeforeMutationBase),
                ServiceEnableFailureDisposition.RefusedBeforeMutation);
        }

        if (exitCode > CompensatedBase && exitCode <= CompensatedBase + LastCauseOrdinal)
        {
            return new ServiceEnableFailureClassification(
                (ServiceEnableFailureCause)(exitCode - CompensatedBase),
                ServiceEnableFailureDisposition.Compensated);
        }

        if (exitCode > RecoveryRequiredBase && exitCode <= RecoveryRequiredBase + LastCauseOrdinal)
        {
            return new ServiceEnableFailureClassification(
                (ServiceEnableFailureCause)(exitCode - RecoveryRequiredBase),
                ServiceEnableFailureDisposition.RecoveryRequired);
        }

        // THE EXTENSION REGIONS. Only the ordinals that actually exist decode;
        // a reserved-but-unassigned slot inside a region falls through to the
        // fail-closed default below exactly like any other undocumented code.
        if (IsInExtendedRegion(exitCode, ExtendedRefusedBeforeMutationBase))
        {
            return new ServiceEnableFailureClassification(
                (ServiceEnableFailureCause)(exitCode - ExtendedRefusedBeforeMutationBase),
                ServiceEnableFailureDisposition.RefusedBeforeMutation);
        }

        if (IsInExtendedRegion(exitCode, ExtendedCompensatedBase))
        {
            return new ServiceEnableFailureClassification(
                (ServiceEnableFailureCause)(exitCode - ExtendedCompensatedBase),
                ServiceEnableFailureDisposition.Compensated);
        }

        if (IsInExtendedRegion(exitCode, ExtendedRecoveryRequiredBase))
        {
            return new ServiceEnableFailureClassification(
                (ServiceEnableFailureCause)(exitCode - ExtendedRecoveryRequiredBase),
                ServiceEnableFailureDisposition.RecoveryRequired);
        }

        if (exitCode == SetupExitCodes.UsageError || exitCode == SetupExitCodes.UnsupportedWindowsVersion)
        {
            return new ServiceEnableFailureClassification(
                ServiceEnableFailureCause.Unavailable,
                ServiceEnableFailureDisposition.RefusedBeforeMutation);
        }

        return new ServiceEnableFailureClassification(
            ServiceEnableFailureCause.Unavailable, ServiceEnableFailureDisposition.RecoveryRequired);
    }

    /// <summary>
    /// True when the code falls inside the ASSIGNED slots of one extension
    /// region. Reserved-but-unassigned slots are deliberately excluded so a
    /// future cause cannot be decoded before it exists.
    /// </summary>
    private static bool IsInExtendedRegion(int exitCode, int regionBase) =>
        exitCode >= regionBase + ExtendedFirstCauseOrdinal
        && exitCode <= regionBase + ExtendedLastCauseOrdinal;

    /// <summary>
    /// True when the code is one this contract can actually emit. The valid set
    /// is DELIBERATELY NOT CONTIGUOUS: it is the legacy window [140, 180] plus
    /// the three assigned extension slices. Saying "one range" here would be a
    /// false comment, so it is not said.
    /// </summary>
    internal static bool IsInDocumentedBand(int exitCode) =>
        (exitCode >= BandFirstExitCode && exitCode <= BandLastExitCode)
        || IsInExtendedRegion(exitCode, ExtendedRefusedBeforeMutationBase)
        || IsInExtendedRegion(exitCode, ExtendedCompensatedBase)
        || IsInExtendedRegion(exitCode, ExtendedRecoveryRequiredBase);

    /// <summary>
    /// THE TOTAL map from the LEGACY conflated outcome to a CAUSE.
    ///
    /// PreflightRefused deliberately maps to Unavailable rather than to a
    /// guessed category: the preflight path always records the finer cause from
    /// its own bounded reason before this map could ever be consulted, so
    /// reaching Unavailable here means the finer reason was genuinely not
    /// available. Compensated and RecoveryRequired are DISPOSITIONS, not causes,
    /// and so they too carry no cause of their own.
    /// </summary>
    internal static ServiceEnableFailureCause CauseFor(ServiceEnableOperationOutcome outcome) => outcome switch
    {
        ServiceEnableOperationOutcome.Completed => ServiceEnableFailureCause.None,
        ServiceEnableOperationOutcome.SigningPolicyRefused => ServiceEnableFailureCause.SigningPolicyRefused,
        ServiceEnableOperationOutcome.AnchorConflict => ServiceEnableFailureCause.ExistingStateRefused,
        ServiceEnableOperationOutcome.AnchorPersistenceRefused => ServiceEnableFailureCause.ExistingStateRefused,
        ServiceEnableOperationOutcome.ExtractionRefused => ServiceEnableFailureCause.ExtractionRefused,
        ServiceEnableOperationOutcome.RegistrationRefused => ServiceEnableFailureCause.RegistrationRefused,
        ServiceEnableOperationOutcome.VerificationFailed => ServiceEnableFailureCause.VerificationRefused,
        ServiceEnableOperationOutcome.MachineDataProtectionRefused =>
            ServiceEnableFailureCause.MachineDataProtectionRefused,
        ServiceEnableOperationOutcome.StartabilityRefused => ServiceEnableFailureCause.StartabilityRefused,
        _ => ServiceEnableFailureCause.Unavailable,
    };

    /// <summary>
    /// True when the channel outcome was SPOKEN BY THE SERVER on the wire, so
    /// the helper's exit code and the channel outcome are two independent
    /// reports of the SAME server-side decision and must therefore agree.
    ///
    /// The four values that are excluded - Unspecified, EndpointUnavailable,
    /// MalformedRequest and AcknowledgementNotReceived, plus UnsupportedPlatform
    /// - are ALSO producible by the CLIENT with no server statement received, so
    /// requiring agreement on them would manufacture false mismatches. For those
    /// the helper exit code is the only report there is.
    /// </summary>
    internal static bool IsServerAuthoritative(ServiceInitiatingUserIdentityChannelOutcome outcome) => outcome switch
    {
        ServiceInitiatingUserIdentityChannelOutcome.Completed
            or ServiceInitiatingUserIdentityChannelOutcome.NoClientConnected
            or ServiceInitiatingUserIdentityChannelOutcome.ChallengeMismatch
            or ServiceInitiatingUserIdentityChannelOutcome.ClientIdentityUnavailable
            or ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch
            or ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict
            or ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused
            or ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired
            or ServiceInitiatingUserIdentityChannelOutcome.AcknowledgementFailed
            or ServiceInitiatingUserIdentityChannelOutcome.InitiatorNotBound
            or ServiceInitiatingUserIdentityChannelOutcome.ChallengeNotDelivered
            or ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused
            or ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated
            or ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired

            // CYCLE 89. Each of these is written on the wire by the SERVER, from
            // inside the payload phase, so the helper exit code and the channel
            // outcome remain two independent reports of one server decision.
            or ServiceInitiatingUserIdentityChannelOutcome.AnchorRequiredButUnavailable
            or ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed
            or ServiceInitiatingUserIdentityChannelOutcome.PayloadOversized
            or ServiceInitiatingUserIdentityChannelOutcome.PayloadDigestMismatch
            or ServiceInitiatingUserIdentityChannelOutcome.PayloadNotUtf8
            or ServiceInitiatingUserIdentityChannelOutcome.PayloadRefused
            or ServiceInitiatingUserIdentityChannelOutcome.InstallationOwnershipMismatch => true,

        // CYCLE 89, DELIBERATELY EXCLUDED. PayloadReadyNotDelivered is the case
        // in which the server could NOT write, so nothing was spoken; and
        // PayloadNotOffered is produced by the CLIENT alone when no payload-ready
        // line arrived. Demanding agreement on either would manufacture false
        // mismatches out of ordinary local failures.
        _ => false,
    };

    /// <summary>
    /// THE TOTAL map from a channel outcome to the DISPOSITION the helper's exit
    /// code must report for the two to agree. Unspecified and any future member
    /// fall to RecoveryRequired, so a vocabulary this build does not understand
    /// can never be read as "nothing happened".
    ///
    /// CYCLE 67R - THE ANCHOR-PERSISTENCE SIGNAL IS SPLIT. The anchor write
    /// creates the fixed machine directories BEFORE it writes, flushes,
    /// replaces and rereads, so a failure at or after that create leaves those
    /// directories durably present. Only refusals decided STRICTLY BEFORE the
    /// create keep <see cref="ServiceEnableFailureDisposition.RefusedBeforeMutation"/>;
    /// everything at or after it is
    /// <see cref="ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired"/>.
    /// Collapsing the two would tell an operator the machine is clean when it
    /// demonstrably is not, and the retry they would then perform permanently
    /// disqualifies the compensation from removing the residue it did not
    /// create.
    /// </summary>
    internal static ServiceEnableFailureDisposition ExpectedDispositionFor(
        ServiceInitiatingUserIdentityChannelOutcome outcome) => outcome switch
    {
        ServiceInitiatingUserIdentityChannelOutcome.Completed =>
            ServiceEnableFailureDisposition.Completed,

        ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated =>
            ServiceEnableFailureDisposition.Compensated,

        ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired
            or ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired
            or ServiceInitiatingUserIdentityChannelOutcome.AcknowledgementFailed =>
            ServiceEnableFailureDisposition.RecoveryRequired,

        ServiceInitiatingUserIdentityChannelOutcome.UnsupportedPlatform
            or ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable
            or ServiceInitiatingUserIdentityChannelOutcome.NoClientConnected
            or ServiceInitiatingUserIdentityChannelOutcome.MalformedRequest
            or ServiceInitiatingUserIdentityChannelOutcome.ChallengeMismatch
            or ServiceInitiatingUserIdentityChannelOutcome.ClientIdentityUnavailable
            or ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch
            or ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict
            or ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused
            or ServiceInitiatingUserIdentityChannelOutcome.AcknowledgementNotReceived
            or ServiceInitiatingUserIdentityChannelOutcome.InitiatorNotBound
            or ServiceInitiatingUserIdentityChannelOutcome.ChallengeNotDelivered
            or ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused =>
            ServiceEnableFailureDisposition.RefusedBeforeMutation,

        // CYCLE 89 - THE PAYLOAD PHASE IS ENTIRELY PRE-MUTATION, AND THAT IS A
        // CLAIM WITH A STRUCTURAL PROOF BEHIND IT, NOT A CONVENIENCE.
        //
        // Every one of these is decided inside RunPayloadPhase, whose single call
        // site sits AFTER the anchor has been read and classified and BEFORE the
        // mutation-free preflight, the anchor write and Apply. Nothing durable
        // can have been touched at any of these points:
        //   * the payload transaction binds ServiceAnchorCreationPolicy.NeverCreate,
        //     so the anchor write is not even attempted and no fixed machine
        //     directory can be created;
        //   * Preflight is by contract mutation-free and, for six of these nine,
        //     has not run at all;
        //   * Apply is unreachable from every one of them.
        // So refused_before_mutation is TRUE here in the strong sense the cycle-67R
        // ruling requires: an operator told nothing happened is being told the
        // truth, and the retry they then perform observes no residue.
        ServiceInitiatingUserIdentityChannelOutcome.AnchorRequiredButUnavailable
            or ServiceInitiatingUserIdentityChannelOutcome.PayloadReadyNotDelivered
            or ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed
            or ServiceInitiatingUserIdentityChannelOutcome.PayloadOversized
            or ServiceInitiatingUserIdentityChannelOutcome.PayloadDigestMismatch
            or ServiceInitiatingUserIdentityChannelOutcome.PayloadNotUtf8
            or ServiceInitiatingUserIdentityChannelOutcome.PayloadRefused
            or ServiceInitiatingUserIdentityChannelOutcome.InstallationOwnershipMismatch
            or ServiceInitiatingUserIdentityChannelOutcome.PayloadNotOffered =>
            ServiceEnableFailureDisposition.RefusedBeforeMutation,

        _ => ServiceEnableFailureDisposition.RecoveryRequired,
    };

    /// <summary>
    /// The fixed lowercase cause token. An undefined cast reads as
    /// <c>unavailable</c>, which is never a success token.
    /// </summary>
    internal static string CauseToken(ServiceEnableFailureCause cause) => cause switch
    {
        ServiceEnableFailureCause.None => "none",
        ServiceEnableFailureCause.SigningPolicyRefused => "signing_policy_refused",
        ServiceEnableFailureCause.PayloadRefused => "payload_refused",
        ServiceEnableFailureCause.ProgramFilesPreflightRefused => "program_files_preflight_refused",
        ServiceEnableFailureCause.ExistingStateRefused => "existing_state_refused",
        ServiceEnableFailureCause.ExtractionRefused => "extraction_refused",
        ServiceEnableFailureCause.RegistrationRefused => "registration_refused",
        ServiceEnableFailureCause.VerificationRefused => "verification_refused",
        ServiceEnableFailureCause.MachineDataProtectionRefused => "machine_data_protection_refused",
        ServiceEnableFailureCause.StartabilityRefused => "startability_refused",
        ServiceEnableFailureCause.ServiceCreationRefused => "service_creation_refused",
        ServiceEnableFailureCause.ServiceSidConfigurationRefused => "service_sid_configuration_refused",
        ServiceEnableFailureCause.ServiceSidResolutionRefused => "service_sid_resolution_refused",
        ServiceEnableFailureCause.ProgramFilesProtectionRefused => "program_files_protection_refused",
        ServiceEnableFailureCause.ServiceStartRefused => "service_start_refused",
        ServiceEnableFailureCause.ServiceRunningTimeout => "service_running_timeout",
        ServiceEnableFailureCause.ServiceProcessIdentityRefused => "service_process_identity_refused",
        ServiceEnableFailureCause.ServiceStatusDocumentRefused => "service_status_document_refused",
        ServiceEnableFailureCause.ServiceHeartbeatRefused => "service_heartbeat_refused",
        ServiceEnableFailureCause.ServiceForbiddenChildRefused => "service_forbidden_child_refused",
        ServiceEnableFailureCause.ServiceStartLogonRefused => "service_start_logon_refused",
        ServiceEnableFailureCause.ServiceStartAccessRefused => "service_start_access_refused",
        ServiceEnableFailureCause.ServiceStartBinaryUnavailable => "service_start_binary_unavailable",
        ServiceEnableFailureCause.ServiceStartDependencyRefused => "service_start_dependency_refused",
        ServiceEnableFailureCause.ServiceStartRequestTimeout => "service_start_request_timeout",
        ServiceEnableFailureCause.ServiceStatusMissingTimeout => "service_status_missing_timeout",
        ServiceEnableFailureCause.ServiceStatusStartingTimeout => "service_status_starting_timeout",
        ServiceEnableFailureCause.ServiceStatusUnreadable => "service_status_unreadable",
        ServiceEnableFailureCause.ServiceStatusMalformed => "service_status_malformed",
        ServiceEnableFailureCause.ServiceStatusWrongContext => "service_status_wrong_context",
        ServiceEnableFailureCause.ServiceStatusStopped => "service_status_stopped",
        ServiceEnableFailureCause.ServiceStatusFailed => "service_status_failed",
        ServiceEnableFailureCause.ServiceStatusUnknownState => "service_status_unknown_state",
        _ => "unavailable",
    };

    /// <summary>
    /// The fixed lowercase disposition token. An undefined cast reads as
    /// <c>none</c>, which is never a success token.
    /// </summary>
    internal static string DispositionToken(ServiceEnableFailureDisposition disposition) => disposition switch
    {
        ServiceEnableFailureDisposition.RefusedBeforeMutation => "refused_before_mutation",
        ServiceEnableFailureDisposition.Compensated => "compensated",
        ServiceEnableFailureDisposition.RecoveryRequired => "recovery_required",
        ServiceEnableFailureDisposition.Completed => "completed",
        _ => "none",
    };

    /// <summary>
    /// THE ONE bounded Setup diagnostic line, composed from fixed tokens only.
    /// It contains no path, no identity, no native status, no timestamp, no
    /// process identifier and no exception text, and it contains no line break -
    /// the single terminator is supplied by the one WriteLine call that emits
    /// it.
    /// </summary>
    internal static string FormatDiagnostic(
        ServiceEnableFailureCause cause, ServiceEnableFailureDisposition disposition) =>
        DiagnosticPrefix
        + " outcome=" + CauseToken(cause)
        + " disposition=" + DispositionToken(disposition);
}
