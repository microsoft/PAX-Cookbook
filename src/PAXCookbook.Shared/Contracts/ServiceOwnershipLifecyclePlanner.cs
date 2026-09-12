using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace PAXCookbook.Shared.Contracts;

// ---------------------------------------------------------------------------
// SERVICE OWNERSHIP LIFECYCLE PLANNER - cycle 38b.
//
// WHAT THIS IS. A pure, portable, deterministic FUNCTION from
//   (a validated ownership-ledger result, the identities the caller expects,
//    one bounded credential observation, one bounded job relation)
// to an ORDERED LIST OF SYMBOLIC ACTIONS, or to a bounded refusal.
//
// It is compiled into PAXCookbook.Shared (used by Setup) and LINKED into
// PAXCookbook.Service, which references neither Shared nor App. It must therefore
// stay ENTIRELY SELF-CONTAINED: portable framework namespaces only, and no
// dependency on any type in this assembly other than the schema types of
// ServiceOwnershipLedgerContract, which are linked alongside it.
//
// WHAT THIS IS NOT. It opens no certificate store, touches no private key, reads or
// writes no permission list, reads no registry, reads no credential vault, installs
// or starts no service, elevates nothing, reads or writes no machine data directory,
// opens no socket, starts no executable, composes no path, and performs no file
// access of any kind. It holds NO DELEGATE, accepts NO CALLBACK and takes NO
// STRATEGY OBJECT, because any of those would smuggle arbitrary behaviour back in
// through a door the symbolic action vocabulary exists to close.
//
// THERE IS NO EXECUTOR. Nothing in the product consumes a plan in this cycle.
// Deciding WHAT SHOULD HAPPEN is deliberately separated from making it happen, and
// only the first half exists.
//
// THE ACTION VOCABULARY IS DELIBERATELY NARROW. Every action names a step over the
// LEDGER, or over state that was ALREADY CAPTURED and ALREADY VALIDATED. There is no
// action that grants access, applies a permission, imports a certificate, starts a
// service, runs a Cook, deletes a referenced certificate, or carries an arbitrary
// command - and there is a test that proves no such name exists anywhere in the
// assembly. A referenced certificate is NEVER deleted by any plan: the ledger
// records a borrowed permission, and unwinding it means restoring the permission,
// never destroying the credential it was borrowed from.
//
// WHY THE MATRIX IS TWO DIMENSIONAL (ruling A2). The document-level
// transactionState and the target entry's lifecycleState are INDEPENDENT facts. The
// schema's InProgress outcome collapses several genuinely different situations into
// a single value, so branching on InProgress would silently merge a ledger that had
// not yet touched the credential with one that had. The planner therefore branches
// on BOTH raw dimensions and NEVER on any ledger outcome. Only six pairs are
// COHERENT (cycle 82 added the sixth, Done+Active); every other reachable pair means
// the two records disagree about what happened, and disagreement is never a licence
// to touch a permission - it is recorded as stale for a human.
//
// RESTORE SEMANTICS (ruling E1). There is ONE state-agnostic action,
// RestoreCapturedPriorDacl. A FUTURE executor interprets it from the VALIDATED entry
// it is planning over:
//   * priorDaclState "absent"  -> remove the owned grant and restore NO payload;
//   * priorDaclState "empty"   -> restore an explicitly empty permission state;
//   * priorDaclState "present" -> restore the validated captured bytes.
// The planner never carries raw permission bytes in an action and never interprets
// them. It cannot: no action in the vocabulary has a payload of any kind.
//
// REASONS THAT ARE DEFENDED BUT PARSER-UNREACHABLE IN THIS CYCLE. These are declared
// so the vocabulary is closed and so a later schema change cannot quietly widen
// behaviour, not because a caller can reach them today:
//   * EntryAmbiguous            - the schema refuses duplicate entry ids first.
//   * CapturedStateBindingInvalid - the schema refuses a mismatched binding first.
//   * KeyIdentityMismatch       - a key-identity divergence is an OBSERVATION that
//                                 yields MarkStale, not a refusal (ruling C1).
//   * UnsupportedLifecycle      - "foreign" entries are parser-refused, and a
//                                 populated "idle" ledger is refused too. Amended
//                                 cycle 82: "active" entries are NO LONGER
//                                 parser-refused, but every active pair other than
//                                 Done+Active is incoherent and is handled by the
//                                 stale gate before this refusal is reachable.
//   * RecoveryCannotBeProven    - reserved for a future executor that can discover
//                                 mid-flight that a proven state stopped being
//                                 provable; the planner never guesses on its behalf.
// ---------------------------------------------------------------------------

/// <summary>The operation a caller is asking the planner to reason about. Zero always refuses.</summary>
public enum ServiceOwnershipPlannerOperation
{
    Unspecified = 0,
    AssessPromotion = 1,
    Verify = 2,
    RecoverInterrupted = 3,
    UnpublishJob = 4,
    RestoreCredential = 5,
    DisableCleanup = 6,
}

/// <summary>
/// What the caller OBSERVED about the credential's current permission state,
/// expressed as a closed classification rather than as data. Zero always refuses.
/// </summary>
public enum ServiceOwnershipCredentialObservation
{
    Unspecified = 0,
    NotApplicable = 1,
    Unavailable = 2,
    MatchesCapturedPriorState = 3,
    MatchesRecordedGrant = 4,
    Diverged = 5,
    KeyIdentityMismatch = 6,
}

/// <summary>How the target promoted job relates to the target entry. Zero always refuses.</summary>
public enum ServiceOwnershipJobRelation
{
    Unspecified = 0,
    NotApplicable = 1,
    JobNotAssociated = 2,
    OtherJobsRemain = 3,
    FinalAssociatedJob = 4,
}

/// <summary>The three shapes a plan can take.</summary>
public enum ServiceOwnershipPlanOutcome
{
    NoAction = 0,
    PlanAvailable = 1,
    Refused = 2,
}

/// <summary>
/// Closed, bounded refusal vocabulary. No path, message, exception text, identifier
/// or certificate byte is representable here or anywhere else in a plan.
/// </summary>
public enum ServiceOwnershipPlanRefusalReason
{
    None = 0,
    InvalidRequest = 1,
    LedgerRefused = 2,
    LedgerAbsent = 3,
    EntryNotFound = 4,
    EntryAmbiguous = 5,
    InstallationOwnershipMismatch = 6,
    OwningPrincipalMismatch = 7,
    ServicePrincipalMismatch = 8,
    PromotionNotAuthorized = 9,
    ObservationUnavailable = 10,
    KeyIdentityMismatch = 11,
    StateMismatch = 12,
    CapturedStateBindingInvalid = 13,
    JobNotAssociated = 14,
    StaleManualActionRequired = 15,
    UnsupportedLifecycle = 16,
    RecoveryCannotBeProven = 17,
}

/// <summary>
/// The closed set of SYMBOLIC steps a plan may contain. Every member names a step
/// over the ledger or over already-captured, already-validated state. No member
/// grants, applies, imports, starts, runs, deletes a referenced credential, or
/// carries a command or a payload. Zero always refuses.
/// </summary>
public enum ServiceOwnershipPlanAction
{
    Unspecified = 0,
    RemoveAbandonedIntentEntry = 1,
    PersistLedger = 2,
    BeginRestore = 3,
    RestoreCapturedPriorDacl = 4,
    VerifyCapturedPriorDacl = 5,
    MarkRestored = 6,
    RemoveRestoredEntry = 7,
    RemoveJobAssociation = 8,
    MarkStale = 9,
}

/// <summary>
/// An immutable planning request. The constructor is PRIVATE: a request exists only
/// if one of the operation-specific factories accepted every field, which is how
/// irrelevant and contradictory combinations are refused before the planner runs.
///
/// The ledger result is consumed as the schema's OWN result type, whose constructors
/// are inaccessible outside the contract. A caller therefore cannot fabricate an
/// "accepted" verdict and hand it to the planner; it must come from the validator.
/// Per ruling D1 the planner accepts ANY result - accepted or refused - because
/// deciding what to do about a refused ledger is itself part of the decision.
/// </summary>
public sealed class ServiceOwnershipLifecyclePlanRequest
{
    private ServiceOwnershipLifecyclePlanRequest(
        ServiceOwnershipPlannerOperation operation,
        ServiceOwnershipLedgerValidationResult ledgerResult,
        string expectedInstallationOwnershipId,
        string expectedOwningUserSid,
        string expectedServiceSid,
        string targetEntryId,
        string targetPromotedJobId,
        ServiceOwnershipCredentialObservation observation,
        ServiceOwnershipJobRelation jobRelation)
    {
        Operation = operation;
        LedgerResult = ledgerResult;
        ExpectedInstallationOwnershipId = expectedInstallationOwnershipId;
        ExpectedOwningUserSid = expectedOwningUserSid;
        ExpectedServiceSid = expectedServiceSid;
        TargetEntryId = targetEntryId;
        TargetPromotedJobId = targetPromotedJobId;
        Observation = observation;
        JobRelation = jobRelation;
    }

    public ServiceOwnershipPlannerOperation Operation { get; }

    public ServiceOwnershipLedgerValidationResult LedgerResult { get; }

    public string ExpectedInstallationOwnershipId { get; }

    public string ExpectedOwningUserSid { get; }

    public string ExpectedServiceSid { get; }

    /// <summary>Empty for operations that address the whole ledger rather than one entry.</summary>
    public string TargetEntryId { get; }

    /// <summary>Empty for every operation except <c>UnpublishJob</c>.</summary>
    public string TargetPromotedJobId { get; }

    public ServiceOwnershipCredentialObservation Observation { get; }

    public ServiceOwnershipJobRelation JobRelation { get; }

    /// <summary>
    /// Promotion assessment addresses the whole ledger, needs no target and consults
    /// no observation, because promotion is not authorized.
    /// </summary>
    public static ServiceOwnershipLifecyclePlanRequest? ForAssessPromotion(
        ServiceOwnershipLedgerValidationResult? ledgerResult,
        string? expectedInstallationOwnershipId,
        string? expectedOwningUserSid,
        string? expectedServiceSid)
    {
        if (!IsUsable(ledgerResult, expectedInstallationOwnershipId, expectedOwningUserSid, expectedServiceSid))
        {
            return null;
        }

        return new ServiceOwnershipLifecyclePlanRequest(
            ServiceOwnershipPlannerOperation.AssessPromotion,
            ledgerResult!,
            expectedInstallationOwnershipId!,
            expectedOwningUserSid!,
            expectedServiceSid!,
            string.Empty,
            string.Empty,
            ServiceOwnershipCredentialObservation.NotApplicable,
            ServiceOwnershipJobRelation.NotApplicable);
    }

    /// <summary>
    /// Read-only classification of one entry. It takes NO observation and NO job
    /// relation, so a caller cannot even express a state-changing verification.
    /// </summary>
    public static ServiceOwnershipLifecyclePlanRequest? ForVerify(
        ServiceOwnershipLedgerValidationResult? ledgerResult,
        string? expectedInstallationOwnershipId,
        string? expectedOwningUserSid,
        string? expectedServiceSid,
        string? targetEntryId)
    {
        if (!IsUsable(ledgerResult, expectedInstallationOwnershipId, expectedOwningUserSid, expectedServiceSid)
            || !IsToken(targetEntryId))
        {
            return null;
        }

        return new ServiceOwnershipLifecyclePlanRequest(
            ServiceOwnershipPlannerOperation.Verify,
            ledgerResult!,
            expectedInstallationOwnershipId!,
            expectedOwningUserSid!,
            expectedServiceSid!,
            targetEntryId!,
            string.Empty,
            ServiceOwnershipCredentialObservation.NotApplicable,
            ServiceOwnershipJobRelation.NotApplicable);
    }

    public static ServiceOwnershipLifecyclePlanRequest? ForRecoverInterrupted(
        ServiceOwnershipLedgerValidationResult? ledgerResult,
        string? expectedInstallationOwnershipId,
        string? expectedOwningUserSid,
        string? expectedServiceSid,
        string? targetEntryId,
        ServiceOwnershipCredentialObservation observation) =>
        ForEntryOperation(
            ServiceOwnershipPlannerOperation.RecoverInterrupted, ledgerResult,
            expectedInstallationOwnershipId, expectedOwningUserSid, expectedServiceSid,
            targetEntryId, observation);

    public static ServiceOwnershipLifecyclePlanRequest? ForRestoreCredential(
        ServiceOwnershipLedgerValidationResult? ledgerResult,
        string? expectedInstallationOwnershipId,
        string? expectedOwningUserSid,
        string? expectedServiceSid,
        string? targetEntryId,
        ServiceOwnershipCredentialObservation observation) =>
        ForEntryOperation(
            ServiceOwnershipPlannerOperation.RestoreCredential, ledgerResult,
            expectedInstallationOwnershipId, expectedOwningUserSid, expectedServiceSid,
            targetEntryId, observation);

    public static ServiceOwnershipLifecyclePlanRequest? ForDisableCleanup(
        ServiceOwnershipLedgerValidationResult? ledgerResult,
        string? expectedInstallationOwnershipId,
        string? expectedOwningUserSid,
        string? expectedServiceSid,
        string? targetEntryId,
        ServiceOwnershipCredentialObservation observation) =>
        ForEntryOperation(
            ServiceOwnershipPlannerOperation.DisableCleanup, ledgerResult,
            expectedInstallationOwnershipId, expectedOwningUserSid, expectedServiceSid,
            targetEntryId, observation);

    /// <summary>
    /// The only factory that carries a job id, and the only one that carries a job
    /// relation. A relation of <c>NotApplicable</c> is contradictory here and is
    /// refused, exactly as an observation of <c>NotApplicable</c> is.
    /// </summary>
    public static ServiceOwnershipLifecyclePlanRequest? ForUnpublishJob(
        ServiceOwnershipLedgerValidationResult? ledgerResult,
        string? expectedInstallationOwnershipId,
        string? expectedOwningUserSid,
        string? expectedServiceSid,
        string? targetEntryId,
        string? targetPromotedJobId,
        ServiceOwnershipCredentialObservation observation,
        ServiceOwnershipJobRelation jobRelation)
    {
        if (!IsUsable(ledgerResult, expectedInstallationOwnershipId, expectedOwningUserSid, expectedServiceSid)
            || !IsToken(targetEntryId)
            || !IsToken(targetPromotedJobId)
            || !IsDefinite(observation)
            || !IsActionable(jobRelation))
        {
            return null;
        }

        return new ServiceOwnershipLifecyclePlanRequest(
            ServiceOwnershipPlannerOperation.UnpublishJob,
            ledgerResult!,
            expectedInstallationOwnershipId!,
            expectedOwningUserSid!,
            expectedServiceSid!,
            targetEntryId!,
            targetPromotedJobId!,
            observation,
            jobRelation);
    }

    private static ServiceOwnershipLifecyclePlanRequest? ForEntryOperation(
        ServiceOwnershipPlannerOperation operation,
        ServiceOwnershipLedgerValidationResult? ledgerResult,
        string? expectedInstallationOwnershipId,
        string? expectedOwningUserSid,
        string? expectedServiceSid,
        string? targetEntryId,
        ServiceOwnershipCredentialObservation observation)
    {
        if (!IsUsable(ledgerResult, expectedInstallationOwnershipId, expectedOwningUserSid, expectedServiceSid)
            || !IsToken(targetEntryId)
            || !IsDefinite(observation))
        {
            return null;
        }

        return new ServiceOwnershipLifecyclePlanRequest(
            operation,
            ledgerResult!,
            expectedInstallationOwnershipId!,
            expectedOwningUserSid!,
            expectedServiceSid!,
            targetEntryId!,
            string.Empty,
            observation,
            ServiceOwnershipJobRelation.NotApplicable);
    }

    private static bool IsUsable(
        ServiceOwnershipLedgerValidationResult? ledgerResult,
        string? expectedInstallationOwnershipId,
        string? expectedOwningUserSid,
        string? expectedServiceSid) =>
        ledgerResult is not null
        && IsToken(expectedInstallationOwnershipId)
        && ServiceOwnershipLedgerContract.IsUserOwnerSid(expectedOwningUserSid)
        && ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(expectedServiceSid)
        && !string.Equals(expectedOwningUserSid, expectedServiceSid, StringComparison.Ordinal);

    private static bool IsToken(string? value) =>
        ServiceOwnershipLedgerContract.IsValidBoundedToken(
            value, ServiceOwnershipLedgerContract.MaxStringLength);

    // An out-of-range cast, Unspecified, and NotApplicable are all refused. Only a
    // DEFINITE statement about the credential is an acceptable basis for a plan.
    private static bool IsDefinite(ServiceOwnershipCredentialObservation observation) =>
        observation is ServiceOwnershipCredentialObservation.Unavailable
            or ServiceOwnershipCredentialObservation.MatchesCapturedPriorState
            or ServiceOwnershipCredentialObservation.MatchesRecordedGrant
            or ServiceOwnershipCredentialObservation.Diverged
            or ServiceOwnershipCredentialObservation.KeyIdentityMismatch;

    private static bool IsActionable(ServiceOwnershipJobRelation relation) =>
        relation is ServiceOwnershipJobRelation.JobNotAssociated
            or ServiceOwnershipJobRelation.OtherJobsRemain
            or ServiceOwnershipJobRelation.FinalAssociatedJob;
}

/// <summary>
/// An immutable plan. The action list is ordered by the planner alone and is exposed
/// as a genuinely read-only collection, so a caller cannot reorder, extend, or
/// truncate a sequence whose ORDER is the safety property.
///
/// Nothing here can leak an identifier: the type declares no string-shaped member at
/// all, and <see cref="ToString"/> renders only closed enum values and a count.
/// </summary>
public sealed class ServiceOwnershipLifecyclePlan
{
    private ServiceOwnershipLifecyclePlan(
        ServiceOwnershipPlannerOperation operation,
        ServiceOwnershipPlanOutcome outcome,
        ServiceOwnershipPlanRefusalReason refusalReason,
        ServiceOwnershipLedgerOutcome? observedLedgerOutcome,
        ReadOnlyCollection<ServiceOwnershipPlanAction> actions)
    {
        Operation = operation;
        Outcome = outcome;
        RefusalReason = refusalReason;
        ObservedLedgerOutcome = observedLedgerOutcome;
        Actions = actions;
    }

    public ServiceOwnershipPlannerOperation Operation { get; }

    public ServiceOwnershipPlanOutcome Outcome { get; }

    public ServiceOwnershipPlanRefusalReason RefusalReason { get; }

    /// <summary>
    /// The bounded ledger classification the plan was formed against, or null when no
    /// ledger result was usable. This is the whole of what <c>Verify</c> reports.
    /// </summary>
    public ServiceOwnershipLedgerOutcome? ObservedLedgerOutcome { get; }

    public IReadOnlyList<ServiceOwnershipPlanAction> Actions { get; }

    public override string ToString() =>
        "ServiceOwnershipLifecyclePlan(operation=" + Operation
        + ", outcome=" + Outcome
        + ", refusalReason=" + RefusalReason
        + ", observedLedgerOutcome=" + (ObservedLedgerOutcome.HasValue
            ? ObservedLedgerOutcome.Value.ToString()
            : "none")
        + ", actionCount=" + Actions.Count.ToString(CultureInfo.InvariantCulture) + ")";

    internal static ServiceOwnershipLifecyclePlan Refused(
        ServiceOwnershipPlannerOperation operation,
        ServiceOwnershipPlanRefusalReason reason,
        ServiceOwnershipLedgerOutcome? observed) =>
        new(operation, ServiceOwnershipPlanOutcome.Refused, reason, observed,
            ServiceOwnershipLifecyclePlanner.NoActions);

    internal static ServiceOwnershipLifecyclePlan Nothing(
        ServiceOwnershipPlannerOperation operation,
        ServiceOwnershipLedgerOutcome? observed) =>
        new(operation, ServiceOwnershipPlanOutcome.NoAction,
            ServiceOwnershipPlanRefusalReason.None, observed,
            ServiceOwnershipLifecyclePlanner.NoActions);

    internal static ServiceOwnershipLifecyclePlan Available(
        ServiceOwnershipPlannerOperation operation,
        ServiceOwnershipLedgerOutcome? observed,
        ReadOnlyCollection<ServiceOwnershipPlanAction> actions) =>
        new(operation, ServiceOwnershipPlanOutcome.PlanAvailable,
            ServiceOwnershipPlanRefusalReason.None, observed, actions);
}

/// <summary>
/// The planner. One public entry point, no state, no configuration, no I/O, and no
/// way to widen it at run time: there is no environment variable, switch, file or
/// machine key that can add an action or relax a refusal.
/// </summary>
public static class ServiceOwnershipLifecyclePlanner
{
    // ---- the fixed action sequences ----------------------------------------
    //
    // These are shared immutable instances. Sharing is safe precisely because a
    // ReadOnlyCollection over a private array cannot be mutated by any caller.

    internal static readonly ReadOnlyCollection<ServiceOwnershipPlanAction> NoActions =
        Array.AsReadOnly(Array.Empty<ServiceOwnershipPlanAction>());

    /// <summary>The credential was never touched: drop the abandoned intent and save.</summary>
    private static readonly ReadOnlyCollection<ServiceOwnershipPlanAction> AbandonIntent =
        Array.AsReadOnly(new[]
        {
            ServiceOwnershipPlanAction.RemoveAbandonedIntentEntry,
            ServiceOwnershipPlanAction.PersistLedger,
        });

    /// <summary>
    /// The credential WAS changed and must be put back. The ledger records the
    /// intent to restore BEFORE the restore happens and records success only AFTER
    /// the restore is verified, so a crash at any point leaves a recoverable record
    /// rather than a lie.
    /// </summary>
    private static readonly ReadOnlyCollection<ServiceOwnershipPlanAction> FullRollback =
        Array.AsReadOnly(new[]
        {
            ServiceOwnershipPlanAction.BeginRestore,
            ServiceOwnershipPlanAction.PersistLedger,
            ServiceOwnershipPlanAction.RestoreCapturedPriorDacl,
            ServiceOwnershipPlanAction.VerifyCapturedPriorDacl,
            ServiceOwnershipPlanAction.MarkRestored,
            ServiceOwnershipPlanAction.PersistLedger,
            ServiceOwnershipPlanAction.RemoveRestoredEntry,
            ServiceOwnershipPlanAction.PersistLedger,
        });

    /// <summary>The restore already happened but was never recorded: finish the bookkeeping.</summary>
    private static readonly ReadOnlyCollection<ServiceOwnershipPlanAction> FinalizeRestore =
        Array.AsReadOnly(new[]
        {
            ServiceOwnershipPlanAction.MarkRestored,
            ServiceOwnershipPlanAction.PersistLedger,
            ServiceOwnershipPlanAction.RemoveRestoredEntry,
            ServiceOwnershipPlanAction.PersistLedger,
        });

    /// <summary>Everything is already done and recorded: retire the entry.</summary>
    private static readonly ReadOnlyCollection<ServiceOwnershipPlanAction> RemoveRestored =
        Array.AsReadOnly(new[]
        {
            ServiceOwnershipPlanAction.RemoveRestoredEntry,
            ServiceOwnershipPlanAction.PersistLedger,
        });

    /// <summary>
    /// The records disagree, or the credential no longer matches anything the ledger
    /// captured. Record that and stop. This is the ONLY thing the planner ever does
    /// with an unexplained state, and it deliberately touches no permission.
    /// </summary>
    private static readonly ReadOnlyCollection<ServiceOwnershipPlanAction> GoStale =
        Array.AsReadOnly(new[]
        {
            ServiceOwnershipPlanAction.MarkStale,
            ServiceOwnershipPlanAction.PersistLedger,
        });

    /// <summary>Other jobs still reference the credential: drop only the association.</summary>
    private static readonly ReadOnlyCollection<ServiceOwnershipPlanAction> DropJobAssociation =
        Array.AsReadOnly(new[]
        {
            ServiceOwnershipPlanAction.RemoveJobAssociation,
            ServiceOwnershipPlanAction.PersistLedger,
        });

    /// <summary>
    /// The single entry point. It never throws: every unusable, contradictory or
    /// unrecognised input maps to a bounded refusal.
    /// </summary>
    public static ServiceOwnershipLifecyclePlan Plan(ServiceOwnershipLifecyclePlanRequest? request)
    {
        // ---- precedence 1: the request itself ------------------------------
        if (request is null)
        {
            return ServiceOwnershipLifecyclePlan.Refused(
                ServiceOwnershipPlannerOperation.Unspecified,
                ServiceOwnershipPlanRefusalReason.InvalidRequest,
                null);
        }

        ServiceOwnershipPlannerOperation operation = request.Operation;
        if (!IsKnownOperation(operation))
        {
            return ServiceOwnershipLifecyclePlan.Refused(
                ServiceOwnershipPlannerOperation.Unspecified,
                ServiceOwnershipPlanRefusalReason.InvalidRequest,
                null);
        }

        ServiceOwnershipLedgerValidationResult result = request.LedgerResult;
        if (result is null)
        {
            return ServiceOwnershipLifecyclePlan.Refused(
                operation, ServiceOwnershipPlanRefusalReason.InvalidRequest, null);
        }

        ServiceOwnershipLedgerOutcome observed = result.Outcome;

        // ---- precedence 2: a refused ledger, for EVERY operation -----------
        //
        // This wins even for AssessPromotion. A ledger that cannot be trusted is not
        // a basis for any decision, including the decision that promotion is refused
        // for a different reason.
        if (result.IsRefused)
        {
            return ServiceOwnershipLifecyclePlan.Refused(
                operation, ServiceOwnershipPlanRefusalReason.LedgerRefused, observed);
        }

        // ---- precedence 3: operation-specific -------------------------------

        // The planner refuses promotion everywhere, always, with zero actions, because
        // no promotion transition and no promotion executor is authorized. No intent is
        // ever recorded, so there is never anything to roll back from a promotion
        // assessment.
        if (operation == ServiceOwnershipPlannerOperation.AssessPromotion)
        {
            return ServiceOwnershipLifecyclePlan.Refused(
                operation, ServiceOwnershipPlanRefusalReason.PromotionNotAuthorized, observed);
        }

        if (observed == ServiceOwnershipLedgerOutcome.Absent)
        {
            return PlanForAbsentLedger(operation, observed);
        }

        if (observed == ServiceOwnershipLedgerOutcome.ValidEmpty)
        {
            return PlanForEmptyLedger(operation, observed);
        }

        ServiceOwnershipLedgerDocument? document = result.Document;
        if (document is null)
        {
            // Unreachable: an accepted, non-absent result always carries a document.
            return ServiceOwnershipLifecyclePlan.Refused(
                operation, ServiceOwnershipPlanRefusalReason.InvalidRequest, observed);
        }

        // ---- ownership and identity gates, before any matrix ----------------
        //
        // The principal gates are DOCUMENT-WIDE on purpose. A ledger that contains
        // even one entry belonging to another user, or naming another service, is
        // not a ledger this caller may act on at all.
        if (!Same(document.InstallationOwnershipId, request.ExpectedInstallationOwnershipId))
        {
            return ServiceOwnershipLifecyclePlan.Refused(
                operation, ServiceOwnershipPlanRefusalReason.InstallationOwnershipMismatch, observed);
        }

        foreach (ServiceOwnershipLedgerEntry entry in document.Entries)
        {
            if (!Same(entry.OwningUserSid, request.ExpectedOwningUserSid))
            {
                return ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.OwningPrincipalMismatch, observed);
            }
        }

        foreach (ServiceOwnershipLedgerEntry entry in document.Entries)
        {
            if (!Same(entry.ServiceSid, request.ExpectedServiceSid))
            {
                return ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.ServicePrincipalMismatch, observed);
            }
        }

        ServiceOwnershipLedgerEntry? target = null;
        int matches = 0;
        foreach (ServiceOwnershipLedgerEntry entry in document.Entries)
        {
            if (Same(entry.EntryId, request.TargetEntryId))
            {
                matches++;
                target = entry;
            }
        }

        if (matches == 0)
        {
            return ServiceOwnershipLifecyclePlan.Refused(
                operation, ServiceOwnershipPlanRefusalReason.EntryNotFound, observed);
        }

        if (matches > 1)
        {
            // Defended, but parser-unreachable: duplicate entry ids are refused by
            // the schema before a document is ever produced.
            return ServiceOwnershipLifecyclePlan.Refused(
                operation, ServiceOwnershipPlanRefusalReason.EntryAmbiguous, observed);
        }

        // ---- stale: ANY stale entry poisons the WHOLE ledger ----------------
        //
        // Not just the stale entry, and not just the target. A ledger containing an
        // unexplained record is not a safe basis for changing anything, so every
        // mutation-capable operation stops and asks for a human.
        if (observed == ServiceOwnershipLedgerOutcome.Stale)
        {
            return operation == ServiceOwnershipPlannerOperation.Verify
                ? ServiceOwnershipLifecyclePlan.Nothing(operation, observed)
                : ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.StaleManualActionRequired, observed);
        }

        return PlanFromMatrix(
            operation, request, document.TransactionState, target!.LifecycleState, observed);
    }

    private static ServiceOwnershipLifecyclePlan PlanForAbsentLedger(
        ServiceOwnershipPlannerOperation operation,
        ServiceOwnershipLedgerOutcome observed) => operation switch
        {
            // Nothing was ever recorded, so there is nothing to verify, recover, or
            // clean up. That is a clean machine, not an error.
            ServiceOwnershipPlannerOperation.Verify =>
                ServiceOwnershipLifecyclePlan.Nothing(operation, observed),
            ServiceOwnershipPlannerOperation.RecoverInterrupted =>
                ServiceOwnershipLifecyclePlan.Nothing(operation, observed),
            ServiceOwnershipPlannerOperation.DisableCleanup =>
                ServiceOwnershipLifecyclePlan.Nothing(operation, observed),

            // These two were asked to act on a specific record. The absence of the
            // ledger contradicts the request, so it is refused rather than silently
            // treated as success.
            ServiceOwnershipPlannerOperation.UnpublishJob =>
                ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.LedgerAbsent, observed),
            ServiceOwnershipPlannerOperation.RestoreCredential =>
                ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.LedgerAbsent, observed),

            _ => ServiceOwnershipLifecyclePlan.Refused(
                operation, ServiceOwnershipPlanRefusalReason.PromotionNotAuthorized, observed),
        };

    private static ServiceOwnershipLifecyclePlan PlanForEmptyLedger(
        ServiceOwnershipPlannerOperation operation,
        ServiceOwnershipLedgerOutcome observed) => operation switch
        {
            ServiceOwnershipPlannerOperation.Verify =>
                ServiceOwnershipLifecyclePlan.Nothing(operation, observed),
            ServiceOwnershipPlannerOperation.RecoverInterrupted =>
                ServiceOwnershipLifecyclePlan.Nothing(operation, observed),
            ServiceOwnershipPlannerOperation.DisableCleanup =>
                ServiceOwnershipLifecyclePlan.Nothing(operation, observed),

            // The ledger EXISTS and is valid; the requested entry simply is not in
            // it. That is a different fact from "no ledger at all".
            ServiceOwnershipPlannerOperation.UnpublishJob =>
                ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.EntryNotFound, observed),
            ServiceOwnershipPlannerOperation.RestoreCredential =>
                ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.EntryNotFound, observed),

            _ => ServiceOwnershipLifecyclePlan.Refused(
                operation, ServiceOwnershipPlanRefusalReason.PromotionNotAuthorized, observed),
        };

    // ---- THE TWO-DIMENSIONAL MATRIX ----------------------------------------
    //
    // Both dimensions are read RAW from the validated document. The broad InProgress
    // outcome is never consulted here, and the pairs are pinned individually rather
    // than collapsed into lifecycle-only helper logic, because the whole point is
    // that the same lifecycleState means different things under different
    // transaction states.
    private static ServiceOwnershipLifecyclePlan PlanFromMatrix(
        ServiceOwnershipPlannerOperation operation,
        ServiceOwnershipLifecyclePlanRequest request,
        ServiceOwnershipTransactionState transaction,
        ServiceOwnershipLifecycleState lifecycle,
        ServiceOwnershipLedgerOutcome observed)
    {
        bool coherent = IsCoherentPair(transaction, lifecycle);

        if (operation == ServiceOwnershipPlannerOperation.Verify)
        {
            // Read-only, always. It classifies and never repairs, finalises,
            // restores, or removes anything.
            return coherent
                ? ServiceOwnershipLifecyclePlan.Nothing(operation, observed)
                : ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.StateMismatch, observed);
        }

        // Contradictory pairs: the two records disagree about what happened. Record
        // it and stop. NO permission is restored on a state nobody can explain.
        //
        // THIS GATE RUNS BEFORE THE UnpublishJob JOB-RELATION SWITCH ON PURPOSE
        // (cycle 38c, option A). The job relation is CALLER-SUPPLIED and is NOT
        // cross-checked against the entry, so on a state nobody can explain it is not
        // trusted either: UnpublishJob is forced to MarkStale exactly like every
        // recovery operation, and no association is removed. A future executor must
        // re-derive the relation from the validated entry, and may only do so on a
        // COHERENT pair, where the switch below still applies unchanged.
        if (!coherent)
        {
            return ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale);
        }

        if (operation == ServiceOwnershipPlannerOperation.UnpublishJob)
        {
            switch (request.JobRelation)
            {
                case ServiceOwnershipJobRelation.JobNotAssociated:
                    return ServiceOwnershipLifecyclePlan.Refused(
                        operation, ServiceOwnershipPlanRefusalReason.JobNotAssociated, observed);

                case ServiceOwnershipJobRelation.OtherJobsRemain:
                    // The credential is still in use. Drop the association only, and
                    // never unwind a permission another job still depends on.
                    return ServiceOwnershipLifecyclePlan.Available(
                        operation, observed, DropJobAssociation);

                case ServiceOwnershipJobRelation.FinalAssociatedJob:
                    // The last user is going away, so the same proven unwind applies.
                    // A referenced certificate is still NEVER deleted.
                    break;

                default:
                    return ServiceOwnershipLifecyclePlan.Refused(
                        operation, ServiceOwnershipPlanRefusalReason.InvalidRequest, observed);
            }
        }

        ServiceOwnershipCredentialObservation observation = request.Observation;

        // Nothing is provable, so nothing is planned. Never guess.
        if (observation == ServiceOwnershipCredentialObservation.Unavailable)
        {
            return ServiceOwnershipLifecyclePlan.Refused(
                operation, ServiceOwnershipPlanRefusalReason.ObservationUnavailable, observed);
        }

        // PREPARING + INTENDED. The intent was written but the credential may or may
        // not have been touched before the interruption.
        if (transaction == ServiceOwnershipTransactionState.Preparing
            && lifecycle == ServiceOwnershipLifecycleState.Intended)
        {
            return observation switch
            {
                // Untouched: the intent is simply abandoned.
                ServiceOwnershipCredentialObservation.MatchesCapturedPriorState =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, AbandonIntent),

                // It WAS applied after all: unwind it properly.
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, FullRollback),

                ServiceOwnershipCredentialObservation.Diverged =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),
                ServiceOwnershipCredentialObservation.KeyIdentityMismatch =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),

                _ => ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.InvalidRequest, observed),
            };
        }

        // CREDENTIAL-MUTATED + INTENDED. The document asserts the credential WAS
        // changed. An observation of "still the captured prior state" therefore
        // CONTRADICTS the document, and a contradiction is never a licence to act.
        if (transaction == ServiceOwnershipTransactionState.CredentialMutated
            && lifecycle == ServiceOwnershipLifecycleState.Intended)
        {
            return observation switch
            {
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, FullRollback),

                ServiceOwnershipCredentialObservation.MatchesCapturedPriorState =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),
                ServiceOwnershipCredentialObservation.Diverged =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),
                ServiceOwnershipCredentialObservation.KeyIdentityMismatch =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),

                _ => ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.InvalidRequest, observed),
            };
        }

        // RESTORING + RESTORING. A restore was already begun and interrupted.
        if (transaction == ServiceOwnershipTransactionState.Restoring
            && lifecycle == ServiceOwnershipLifecycleState.Restoring)
        {
            return observation switch
            {
                // The restore had not yet taken effect: do the whole thing.
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, FullRollback),

                // The restore DID take effect before the interruption; only the
                // bookkeeping is missing.
                ServiceOwnershipCredentialObservation.MatchesCapturedPriorState =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, FinalizeRestore),

                ServiceOwnershipCredentialObservation.Diverged =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),
                ServiceOwnershipCredentialObservation.KeyIdentityMismatch =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),

                _ => ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.InvalidRequest, observed),
            };
        }

        // RESTORING + RESTORED and DONE + RESTORED. The permission is already back;
        // only the entry is still present. Anything OTHER than "the prior state is
        // what is there now" means the world moved under us.
        if ((transaction == ServiceOwnershipTransactionState.Restoring
                || transaction == ServiceOwnershipTransactionState.Done)
            && lifecycle == ServiceOwnershipLifecycleState.Restored)
        {
            return observation switch
            {
                ServiceOwnershipCredentialObservation.MatchesCapturedPriorState =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, RemoveRestored),

                ServiceOwnershipCredentialObservation.MatchesRecordedGrant =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),
                ServiceOwnershipCredentialObservation.Diverged =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),
                ServiceOwnershipCredentialObservation.KeyIdentityMismatch =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),

                _ => ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.InvalidRequest, observed),
            };
        }

        // DONE + ACTIVE (cycle 82, D2). The promotion COMPLETED and the grant is
        // live. This is the ONLY pair in the matrix where the credential is supposed
        // to still be mutated, so "the recorded grant is what is there now" is the
        // HEALTHY reading, not a symptom.
        //
        // NOTHING ROUTINE MAY TEAR DOWN A HEALTHY ACTIVE GRANT. RecoverInterrupted
        // is a routine recovery pass, so on a proven healthy grant it plans NOTHING.
        // Only an operation that EXPLICITLY asks for the grant to end - restore,
        // disable cleanup, or unpublishing the final associated job - performs the
        // existing full restoration sequence, unchanged.
        if (transaction == ServiceOwnershipTransactionState.Done
            && lifecycle == ServiceOwnershipLifecycleState.Active)
        {
            return observation switch
            {
                ServiceOwnershipCredentialObservation.MatchesRecordedGrant =>
                    operation == ServiceOwnershipPlannerOperation.RecoverInterrupted
                        ? ServiceOwnershipLifecyclePlan.Nothing(operation, observed)
                        : ServiceOwnershipLifecyclePlan.Available(operation, observed, FullRollback),

                // The grant is gone, or the world moved: the document and the
                // credential disagree, so nothing is restored on a state nobody can
                // explain.
                ServiceOwnershipCredentialObservation.MatchesCapturedPriorState =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),
                ServiceOwnershipCredentialObservation.Diverged =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),
                ServiceOwnershipCredentialObservation.KeyIdentityMismatch =>
                    ServiceOwnershipLifecyclePlan.Available(operation, observed, GoStale),

                _ => ServiceOwnershipLifecyclePlan.Refused(
                    operation, ServiceOwnershipPlanRefusalReason.InvalidRequest, observed),
            };
        }

        // Unreachable: IsCoherentPair enumerates exactly the six pairs handled
        // above. Kept as a terminal refusal so a future pair cannot fall through
        // into a permissive default.
        return ServiceOwnershipLifecyclePlan.Refused(
            operation, ServiceOwnershipPlanRefusalReason.UnsupportedLifecycle, observed);
    }

    /// <summary>
    /// The six COHERENT pairs, enumerated explicitly. Every other pair the parser
    /// can produce means the document and the entry disagree. Cycle 82 added
    /// EXACTLY ONE pair, Done+Active; the five that came before are untouched, so
    /// an interrupted promotion sitting at CredentialMutated+Intended still plans a
    /// full rollback exactly as it did.
    /// </summary>
    private static bool IsCoherentPair(
        ServiceOwnershipTransactionState transaction,
        ServiceOwnershipLifecycleState lifecycle) =>
        (transaction == ServiceOwnershipTransactionState.Preparing
            && lifecycle == ServiceOwnershipLifecycleState.Intended)
        || (transaction == ServiceOwnershipTransactionState.CredentialMutated
            && lifecycle == ServiceOwnershipLifecycleState.Intended)
        || (transaction == ServiceOwnershipTransactionState.Restoring
            && lifecycle == ServiceOwnershipLifecycleState.Restoring)
        || (transaction == ServiceOwnershipTransactionState.Restoring
            && lifecycle == ServiceOwnershipLifecycleState.Restored)
        || (transaction == ServiceOwnershipTransactionState.Done
            && lifecycle == ServiceOwnershipLifecycleState.Restored)
        || (transaction == ServiceOwnershipTransactionState.Done
            && lifecycle == ServiceOwnershipLifecycleState.Active);

    private static bool IsKnownOperation(ServiceOwnershipPlannerOperation operation) =>
        operation is ServiceOwnershipPlannerOperation.AssessPromotion
            or ServiceOwnershipPlannerOperation.Verify
            or ServiceOwnershipPlannerOperation.RecoverInterrupted
            or ServiceOwnershipPlannerOperation.UnpublishJob
            or ServiceOwnershipPlannerOperation.RestoreCredential
            or ServiceOwnershipPlannerOperation.DisableCleanup;

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);
}
