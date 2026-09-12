using System;
using System.Collections.Generic;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

// ---------------------------------------------------------------------------
// DETERMINISTIC PROMOTION / DEPROMOTION EXECUTOR - cycle 88. NON-LIVE.
//
// WHAT THIS IS. The ordered transaction that turns an ACCEPTED promotion request
// into an ACTIVE ownership ledger entry, and the recovery pass that unwinds one.
// It decides ORDER and REFUSAL; it performs no effect itself. Every effect goes
// through a PURPOSE-SPECIFIC port declared below.
//
// NOTHING IN THE PRODUCT CALLS IT. There is no Program.cs dispatch, no elevated
// verb, no coordinator, no Settings or Setup wiring, and no production
// composition root: every port is constructor-injected and the only thing that
// supplies a set is the test project. The one port with a real production
// implementation here is the fixed service-SID port, and constructing it does
// nothing until Observe() is called.
//
// WHY THE PORTS ARE PURPOSE-SPECIFIC. There is deliberately NO generic
// filesystem, certificate, ACL, registry, process or command interface. Each
// port names ONE bounded operation, and none of them accepts an arbitrary path,
// SID, store, provider, descriptor, command, executable, callback or strategy.
// The two values that DO travel between ports - the key handle and the captured
// prior descriptor - are CLOSED types whose constructors are private, so they
// can only be produced by the port that observed them and can never be
// fabricated by a caller.
//
// THE SERVICE-SID INVOCATION. ServiceOwnershipFixedServiceSidPort.Observe()
// carries the ONE qualified ServiceSidResolver.ResolveFixedServiceSid()
// invocation this file is authorized to contain. There is no second call site
// and no alias, static import, method group or reflective lookup.
//
// THE SEQUENCE, AND WHY SUCCESS COMES LAST. Steps (a) through (n) below are the
// AUTHORIZED cycle-88 phase sequence, letter for letter, and NO step may report
// success early:
//   (a) require an accepted request (and a bounded entry id)
//   (b) require an accepted current ledger
//   (c) require the bounded owner identity
//   (d) resolve the fixed service SID exactly once
//   (e) obtain the ONE eligible certificate observation, producing the key handle
//   (f) validate the prior descriptor and the ABSENCE of a service ACE
//   (g) persist Preparing + Intended
//   (h) apply the approved grant, BOUND TO THE SNAPSHOT (f) CAPTURED
//   (i) require MatchesRecordedGrant
//   (j) persist CredentialMutated + Intended
//   (k) persist and verify the promoted Recipe
//   (l) persist Done + Active
//   (m) require a FINAL MatchesRecordedGrant
//   (n) report bounded success
//
// WHAT CYCLE 90 PASS A CHANGED INSIDE STEP (f), AND WHY. The credential
// observation is judged against a CERTIFIED ServiceOwnershipLedgerEntry, which
// Setup cannot construct: the entry type has one assembly-internal constructor
// in Shared, no public factory, and Shared grants no InternalsVisibleTo. The
// only lawful source is the certified transition authority. So (f) now composes
// Preparing + Intended EXACTLY ONCE - without persisting it - and hands that
// composed entry to the observation port. Step (g) then persists THAT SAME
// already-composed serialization; the authority is never invoked a second time
// for this operation.
//
// THE ORDERING CONSEQUENCE, AUTHORIZED AND PINNED BY TEST. Because composition
// now precedes the observation, a composition that cannot be produced refuses
// BEFORE any credential is observed. When the composition AND the credential
// state would both be invalid, TransitionRefused WINS - there is no certified
// entry to observe. Both refusals stop at CompletedSteps 5 with zero credential
// calls for the composition case, zero persistence calls, and no reachable ACL
// mutation. A failure to persist the already-composed intent is the first
// refusal that reports CompletedSteps 6.
//
// A failure at or after (g) unwinds in reverse: the captured descriptor is
// restored, the promoted Recipe is compensated, and if the restore cannot be
// PROVEN the entry is marked STALE rather than removed, so the evidence needed
// to finish the job by hand survives.
//
// WHAT CYCLE 96 CHANGED AT (h), AND WHY. Step (f) captures the prior descriptor
// and the credential observation confirms it; (h) used to hand the apply port
// only the grant PLAN, so the port took a FRESH, UNBOUND read and granted on top
// of whatever it found. An administrator change landing between (f) and (h) was
// therefore granted over, and the unwind then wrote the STALE captured bytes
// back, destroying that change. Now the EXACT captured object travels to (h),
// the port refuses unless the machine still holds those bytes, and a refusal
// there unwinds with descriptorWasApplied FALSE - so no restoration is attempted
// and the administrator's descriptor is left exactly as it was found.
//
// WHY VERIFYING (i) BEFORE RECORDING (j) IS SAFE. The durable marker written at
// (g) - Preparing + Intended - already spans the entire window in which the
// grant may or may not have been applied, because (h) mutates the credential
// while that marker is the only thing on disk. Recovery from Preparing +
// Intended therefore ALREADY has to tolerate "the grant may be applied".
// Verifying before recording lengthens that window in wall-clock time but adds
// no NEW durable state and no new recovery obligation. Placing the promoted
// Recipe at (k) rather than earlier is strictly tighter: it SHORTENS the window
// in which a persisted Recipe can exist without a completed transaction, and it
// still precedes (l), so an ACTIVE entry always has its Recipe.
//
// PRIVACY. No path, byte, SID, thumbprint, exception text or native status
// escapes through any result or ToString() - only bounded outcome tokens.
// ---------------------------------------------------------------------------

// =========================================================================
// BOUNDED OBSERVATION VOCABULARIES
// =========================================================================

/// <summary>Bounded fixed-owner observation state. Zero always refuses.</summary>
internal enum ServiceOwnershipOwnerIdentityState
{
    Unspecified = 0,
    Observed = 1,
    Unavailable = 2,
    NotUserShaped = 3,

    /// <summary>CYCLE 96. The reread anchor named a DIFFERENT installation.</summary>
    InstallationMismatch = 4,

    /// <summary>CYCLE 96. Same installation, DIFFERENT initiating user.</summary>
    OwnerMismatch = 5,
}

/// <summary>Bounded service-SID observation state. Zero always refuses.</summary>
internal enum ServiceOwnershipServiceSidState
{
    Unspecified = 0,
    Resolved = 1,
    Unresolved = 2,
    NotServiceShaped = 3,
}

/// <summary>Bounded certificate-facts observation state. Zero always refuses.</summary>
internal enum ServiceOwnershipCertificateFactsState
{
    Unspecified = 0,
    Observed = 1,
    NotFound = 2,
    Ambiguous = 3,
    PrivateKeyUnavailable = 4,
    UnsupportedProvider = 5,
}

/// <summary>Bounded prior-descriptor capture state. Zero always refuses.</summary>
internal enum ServiceOwnershipPriorDescriptorState
{
    Unspecified = 0,
    Captured = 1,
    Unavailable = 2,
    UnsupportedShape = 3,
    ServiceAceAlreadyPresent = 4,
}

/// <summary>Bounded descriptor-apply state. Zero always refuses.</summary>
internal enum ServiceOwnershipDescriptorApplyState
{
    Unspecified = 0,
    Applied = 1,
    Refused = 2,
    Failed = 3,
}

/// <summary>Bounded descriptor-restore state. Zero always refuses.</summary>
internal enum ServiceOwnershipDescriptorRestoreState
{
    Unspecified = 0,

    /// <summary>The captured descriptor is back AND was verified byte for byte.</summary>
    RestoredAndVerified = 1,

    /// <summary>The restore was attempted but could not be proven.</summary>
    NotProven = 2,

    /// <summary>The restore failed outright.</summary>
    Failed = 3,
}

/// <summary>Bounded ledger persistence state. Zero always refuses.</summary>
internal enum ServiceOwnershipLedgerPersistState
{
    Unspecified = 0,

    /// <summary>Durably written, reread and verified.</summary>
    Persisted = 1,

    /// <summary>Refused before any byte reached disk.</summary>
    Refused = 2,

    /// <summary>Reached disk, failed verification, and the prior state was put back.</summary>
    RolledBack = 3,

    /// <summary>Reached disk, failed verification, and the rollback could not be proven.</summary>
    RecoveryRequired = 4,
}

/// <summary>Bounded promoted-Recipe port outcome. Zero always refuses.</summary>
internal enum ServiceOwnershipPromotedRecipePortOutcome
{
    Unspecified = 0,
    Completed = 1,
    Refused = 2,
}

// =========================================================================
// CLOSED VALUES THAT TRAVEL BETWEEN PORTS
// =========================================================================

/// <summary>
/// A bounded fixed-owner observation. The only constructor is private, so a
/// caller cannot assert an identity it did not observe.
/// </summary>
internal readonly struct ServiceOwnershipOwnerIdentityObservation
{
    private readonly string? sid;

    private ServiceOwnershipOwnerIdentityObservation(ServiceOwnershipOwnerIdentityState state, string? sid)
    {
        State = state;
        this.sid = sid;
    }

    internal ServiceOwnershipOwnerIdentityState State { get; }

    internal string OwningUserSid => sid ?? string.Empty;

    internal bool IsObserved => State == ServiceOwnershipOwnerIdentityState.Observed;

    internal static ServiceOwnershipOwnerIdentityObservation Failure(
        ServiceOwnershipOwnerIdentityState state) => new(state, null);

    internal static ServiceOwnershipOwnerIdentityObservation Observed(string owningUserSid) =>
        new(ServiceOwnershipOwnerIdentityState.Observed, owningUserSid);

    public override string ToString() => State.ToString();
}

/// <summary>A bounded service-SID observation.</summary>
internal readonly struct ServiceOwnershipServiceSidObservation
{
    private readonly string? sid;

    private ServiceOwnershipServiceSidObservation(ServiceOwnershipServiceSidState state, string? sid)
    {
        State = state;
        this.sid = sid;
    }

    internal ServiceOwnershipServiceSidState State { get; }

    internal string ServiceSid => sid ?? string.Empty;

    internal bool IsResolved => State == ServiceOwnershipServiceSidState.Resolved;

    internal static ServiceOwnershipServiceSidObservation Failure(
        ServiceOwnershipServiceSidState state) => new(state, null);

    internal static ServiceOwnershipServiceSidObservation Resolved(string serviceSid) =>
        new(ServiceOwnershipServiceSidState.Resolved, serviceSid);

    public override string ToString() => State.ToString();
}

/// <summary>
/// An OPAQUE handle to the one private key the observed certificate uses. It
/// carries NO path: only the bounded identifiers the ledger itself records. Its
/// constructor is private and only the certificate-facts observation can produce
/// one, so no caller can point a later port at a key it did not observe.
/// </summary>
internal readonly struct ServiceOwnershipPromotionKeyHandle
{
    private ServiceOwnershipPromotionKeyHandle(
        string keyIdentity,
        string providerUniqueName,
        ServiceOwnershipPrivateKeyProviderKind providerKind,
        ServiceOwnershipKeyStorageRoot keyStorageRoot,
        ServiceOwnershipDescriptorFormat descriptorFormat)
    {
        KeyIdentity = keyIdentity;
        ProviderUniqueName = providerUniqueName;
        ProviderKind = providerKind;
        KeyStorageRoot = keyStorageRoot;
        DescriptorFormat = descriptorFormat;
    }

    internal string KeyIdentity { get; }

    internal string ProviderUniqueName { get; }

    internal ServiceOwnershipPrivateKeyProviderKind ProviderKind { get; }

    internal ServiceOwnershipKeyStorageRoot KeyStorageRoot { get; }

    internal ServiceOwnershipDescriptorFormat DescriptorFormat { get; }

    internal bool IsUsable =>
        !string.IsNullOrEmpty(KeyIdentity)
        && !string.IsNullOrEmpty(ProviderUniqueName)
        && ProviderKind != ServiceOwnershipPrivateKeyProviderKind.Unspecified
        && KeyStorageRoot != ServiceOwnershipKeyStorageRoot.Unspecified
        && DescriptorFormat != ServiceOwnershipDescriptorFormat.Unspecified;

    internal static ServiceOwnershipPromotionKeyHandle Create(
        string keyIdentity,
        string providerUniqueName,
        ServiceOwnershipPrivateKeyProviderKind providerKind,
        ServiceOwnershipKeyStorageRoot keyStorageRoot,
        ServiceOwnershipDescriptorFormat descriptorFormat) =>
        new(keyIdentity, providerUniqueName, providerKind, keyStorageRoot, descriptorFormat);

    /// <summary>Carries no identifier: only whether a handle is usable.</summary>
    public override string ToString() => IsUsable ? "Usable" : "Unusable";
}

/// <summary>The bounded facts observed about the ONE named certificate.</summary>
internal readonly struct ServiceOwnershipCertificateFacts
{
    private ServiceOwnershipCertificateFacts(
        ServiceOwnershipCertificateFactsState state,
        string thumbprintSha1,
        ServiceOwnershipCredentialKind credentialKind,
        ServiceOwnershipProvenance provenance,
        ServiceOwnershipRightsProfileId rightsProfileId,
        ServiceOwnershipGrantMechanism grantMechanism,
        ServiceOwnershipPromotionKeyHandle key)
    {
        State = state;
        ThumbprintSha1 = thumbprintSha1;
        CredentialKind = credentialKind;
        Provenance = provenance;
        RightsProfileId = rightsProfileId;
        GrantMechanism = grantMechanism;
        Key = key;
    }

    internal ServiceOwnershipCertificateFactsState State { get; }

    internal string ThumbprintSha1 { get; }

    internal ServiceOwnershipCredentialKind CredentialKind { get; }

    internal ServiceOwnershipProvenance Provenance { get; }

    internal ServiceOwnershipRightsProfileId RightsProfileId { get; }

    internal ServiceOwnershipGrantMechanism GrantMechanism { get; }

    internal ServiceOwnershipPromotionKeyHandle Key { get; }

    internal bool IsObserved =>
        State == ServiceOwnershipCertificateFactsState.Observed && Key.IsUsable;

    internal static ServiceOwnershipCertificateFacts Failure(
        ServiceOwnershipCertificateFactsState state) =>
        new(state, string.Empty, ServiceOwnershipCredentialKind.Unspecified,
            ServiceOwnershipProvenance.Unspecified, ServiceOwnershipRightsProfileId.Unspecified,
            ServiceOwnershipGrantMechanism.Unspecified, default);

    internal static ServiceOwnershipCertificateFacts Observed(
        string thumbprintSha1,
        ServiceOwnershipCredentialKind credentialKind,
        ServiceOwnershipProvenance provenance,
        ServiceOwnershipRightsProfileId rightsProfileId,
        ServiceOwnershipGrantMechanism grantMechanism,
        ServiceOwnershipPromotionKeyHandle key) =>
        new(ServiceOwnershipCertificateFactsState.Observed, thumbprintSha1, credentialKind,
            provenance, rightsProfileId, grantMechanism, key);

    public override string ToString() => State.ToString();
}

/// <summary>
/// The captured prior descriptor. It is the ONLY thing that can be handed to the
/// restore port, and its constructor is private, so a restore can never be
/// pointed at bytes nobody captured.
/// </summary>
internal readonly struct ServiceOwnershipCapturedPriorDescriptor
{
    private ServiceOwnershipCapturedPriorDescriptor(
        ServiceOwnershipPriorDescriptorState state,
        ServiceOwnershipPriorDaclState daclState,
        string bytesBase64,
        string sha256)
    {
        State = state;
        DaclState = daclState;
        BytesBase64 = bytesBase64;
        Sha256 = sha256;
    }

    internal ServiceOwnershipPriorDescriptorState State { get; }

    internal ServiceOwnershipPriorDaclState DaclState { get; }

    /// <summary>The exact Base64 the ledger records, so a restore is byte-exact.</summary>
    internal string BytesBase64 { get; }

    /// <summary>Uppercase SHA-256 hex of the decoded descriptor.</summary>
    internal string Sha256 { get; }

    internal bool IsCaptured => State == ServiceOwnershipPriorDescriptorState.Captured;

    internal static ServiceOwnershipCapturedPriorDescriptor Failure(
        ServiceOwnershipPriorDescriptorState state) =>
        new(state, ServiceOwnershipPriorDaclState.Unspecified, string.Empty, string.Empty);

    internal static ServiceOwnershipCapturedPriorDescriptor Captured(
        ServiceOwnershipPriorDaclState daclState, string bytesBase64, string sha256) =>
        new(ServiceOwnershipPriorDescriptorState.Captured, daclState, bytesBase64, sha256);

    /// <summary>Carries no descriptor byte.</summary>
    public override string ToString() => State.ToString();
}

/// <summary>
/// The closed grant plan. It can only be built from a RESOLVED service-SID
/// observation plus an observed key handle, so no caller can ask the apply port
/// to grant an arbitrary principal.
/// </summary>
internal readonly struct ServiceOwnershipPromotionGrantPlan
{
    private ServiceOwnershipPromotionGrantPlan(
        string serviceSid,
        ServiceOwnershipPromotionKeyHandle key,
        ServiceOwnershipRightsMask approvedMask,
        int rightsPolicyVersion)
    {
        ServiceSid = serviceSid;
        Key = key;
        ApprovedMask = approvedMask;
        RightsPolicyVersion = rightsPolicyVersion;
    }

    internal string ServiceSid { get; }

    internal ServiceOwnershipPromotionKeyHandle Key { get; }

    /// <summary>Always the contract's approved mask. There is no other value.</summary>
    internal ServiceOwnershipRightsMask ApprovedMask { get; }

    internal int RightsPolicyVersion { get; }

    internal bool IsUsable => ServiceSid.Length > 0 && Key.IsUsable;

    internal static bool TryCreate(
        ServiceOwnershipServiceSidObservation serviceSid,
        ServiceOwnershipPromotionKeyHandle key,
        out ServiceOwnershipPromotionGrantPlan plan)
    {
        plan = default;
        if (!serviceSid.IsResolved || serviceSid.ServiceSid.Length == 0 || !key.IsUsable)
        {
            return false;
        }

        plan = new ServiceOwnershipPromotionGrantPlan(
            serviceSid.ServiceSid,
            key,
            new ServiceOwnershipRightsMask(ServiceOwnershipLedgerContract.ApprovedRightsProfileMask),
            ServiceOwnershipLedgerContract.RightsPolicyVersion);
        return true;
    }

    /// <summary>Carries no SID.</summary>
    public override string ToString() => IsUsable ? "Usable" : "Unusable";
}

// =========================================================================
// THE PURPOSE-SPECIFIC PORTS
// =========================================================================
//
// Every one of these names ONE bounded operation. None takes a path, a raw SID,
// a store name, a provider name, a descriptor, a command, an executable, a
// callback or a strategy.

/// <summary>Observes the FIXED initiating user identity. No inputs.</summary>
internal interface IServiceOwnershipOwnerIdentityPort
{
    ServiceOwnershipOwnerIdentityObservation ObserveFixedOwner();
}

/// <summary>Resolves the FIXED service SID. No inputs.</summary>
internal interface IServiceOwnershipServiceSidPort
{
    ServiceOwnershipServiceSidObservation ObserveFixedServiceSid();
}

/// <summary>Observes the bounded facts of the ONE certificate the request names.</summary>
internal interface IServiceOwnershipCertificateFactsPort
{
    ServiceOwnershipCertificateFacts ObserveCertificateFacts(string normalizedThumbprintSha1);
}

/// <summary>Captures the prior descriptor of the observed key. Reads only.</summary>
internal interface IServiceOwnershipPriorDescriptorPort
{
    ServiceOwnershipCapturedPriorDescriptor CapturePriorDescriptor(
        ServiceOwnershipPromotionKeyHandle key);
}

/// <summary>
/// Applies the ONE approved descriptor described by the closed grant plan, bound
/// to the descriptor snapshot step (f) captured.
///
/// CYCLE 96. The captured snapshot is REQUIRED, not optional. Without it an
/// implementation could only ever grant on top of whatever the machine happened
/// to hold when it ran, which is exactly how an administrator change made between
/// (f) and (h) used to be granted over and then reverted by compensation.
/// </summary>
internal interface IServiceOwnershipApprovedDescriptorPort
{
    ServiceOwnershipDescriptorApplyState ApplyApprovedDescriptor(
        ServiceOwnershipPromotionGrantPlan plan,
        ServiceOwnershipCapturedPriorDescriptor captured);
}

/// <summary>Puts a CAPTURED descriptor back. It can restore nothing else.</summary>
internal interface IServiceOwnershipDescriptorRestorePort
{
    ServiceOwnershipDescriptorRestoreState RestoreCapturedDescriptor(
        ServiceOwnershipPromotionKeyHandle key,
        ServiceOwnershipCapturedPriorDescriptor captured);
}

/// <summary>
/// Observes how the credential currently stands, judged against a CERTIFIED
/// ledger entry. The entry is the only input: no path, SID, provider name,
/// descriptor byte or free-form fact can reach an implementation, and an entry
/// can only be obtained from the certified transition authority.
/// </summary>
internal interface IServiceOwnershipCredentialObservationPort
{
    ServiceOwnershipCredentialObservation ObserveCredential(ServiceOwnershipLedgerEntry entry);
}

/// <summary>Durably persists an already-serialized, already-accepted ledger.</summary>
internal interface IServiceOwnershipLedgerPersistencePort
{
    ServiceOwnershipLedgerPersistState Persist(
        ServiceOwnershipLedgerSerializationResult serialized,
        ServiceOwnershipLedgerOutcome expectedOutcome,
        int expectedGeneration);
}

/// <summary>Persists and compensates the ONE promoted Recipe the request carries.</summary>
internal interface IServiceOwnershipPromotedRecipePort
{
    ServiceOwnershipPromotedRecipePortOutcome PersistPromotedRecipe(ServicePromotionRequest request);

    ServiceOwnershipPromotedRecipePortOutcome CompensatePromotedRecipe(string promotedJobId);
}

/// <summary>
/// The ONE production service-SID port. Its Observe method carries the single
/// qualified resolver invocation this file is authorized to contain.
/// </summary>
internal sealed class ServiceOwnershipFixedServiceSidPort : IServiceOwnershipServiceSidPort
{
    public ServiceOwnershipServiceSidObservation ObserveFixedServiceSid()
    {
        ServiceSidResolution resolution = ServiceSidResolver.ResolveFixedServiceSid();

        if (!resolution.IsResolved)
        {
            return ServiceOwnershipServiceSidObservation.Failure(
                ServiceOwnershipServiceSidState.Unresolved);
        }

        return ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(resolution.Sid)
            ? ServiceOwnershipServiceSidObservation.Resolved(resolution.Sid)
            : ServiceOwnershipServiceSidObservation.Failure(
                ServiceOwnershipServiceSidState.NotServiceShaped);
    }
}

// =========================================================================
// BOUNDED TRANSACTION RESULTS
// =========================================================================

/// <summary>Bounded promotion outcome. Zero always refuses.</summary>
internal enum ServiceOwnershipPromotionExecutorOutcome
{
    Unspecified = 0,

    /// <summary>Every step through the FINAL credential observation succeeded.</summary>
    Promoted = 1,

    InvalidRequest = 2,
    OwnerIdentityUnavailable = 3,
    ServiceSidUnavailable = 4,
    CertificateFactsUnavailable = 5,
    PriorDescriptorUnavailable = 6,

    /// <summary>The credential did not match the captured prior state before any change.</summary>
    PreconditionObservationMismatch = 7,

    /// <summary>A ledger transition could not be composed from the current state.</summary>
    TransitionRefused = 8,

    /// <summary>A ledger state could not be durably persisted.</summary>
    LedgerPersistenceRefused = 9,

    /// <summary>The promoted Recipe could not be persisted.</summary>
    PromotedRecipeRefused = 10,

    /// <summary>The approved descriptor could not be applied.</summary>
    DescriptorApplyRefused = 11,

    /// <summary>The credential did not observe as the recorded grant after the apply.</summary>
    GrantObservationMismatch = 12,

    /// <summary>The transaction unwound and the captured prior state was PROVEN restored.</summary>
    RolledBack = 13,

    /// <summary>
    /// The transaction failed and the prior state could NOT be proven restored, so
    /// the entry was marked stale and a human must finish the job.
    /// </summary>
    RecoveryRequired = 14,
}

/// <summary>Bounded depromotion outcome. Zero always refuses.</summary>
internal enum ServiceOwnershipDepromotionExecutorOutcome
{
    Unspecified = 0,

    /// <summary>The planner's actions were executed to completion.</summary>
    Depromoted = 1,

    InvalidRequest = 2,

    /// <summary>The lifecycle planner refused to produce a plan.</summary>
    PlanRefused = 3,

    /// <summary>The planner had nothing to do.</summary>
    NoAction = 4,

    /// <summary>A planned action has no executor here, so nothing was attempted.</summary>
    UnsupportedPlannedAction = 5,

    /// <summary>A ledger transition could not be composed.</summary>
    TransitionRefused = 6,

    /// <summary>A ledger state could not be durably persisted.</summary>
    LedgerPersistenceRefused = 7,

    /// <summary>The captured descriptor could not be proven restored.</summary>
    RestoreNotProven = 8,

    /// <summary>The entry was marked stale because recovery could not be proven.</summary>
    MarkedStale = 9,
}

/// <summary>Bounded promotion result. Carries no path, SID, byte or exception text.</summary>
internal sealed class ServiceOwnershipPromotionResult
{
    private ServiceOwnershipPromotionResult(
        ServiceOwnershipPromotionExecutorOutcome outcome,
        ServiceOwnershipPromotionExecutorOutcome triggeringFailure,
        int completedSteps,
        bool priorDescriptorRestored,
        bool entryMarkedStale)
    {
        Outcome = outcome;
        TriggeringFailure = triggeringFailure;
        CompletedSteps = completedSteps;
        PriorDescriptorRestored = priorDescriptorRestored;
        EntryMarkedStale = entryMarkedStale;
    }

    internal ServiceOwnershipPromotionExecutorOutcome Outcome { get; }

    /// <summary>
    /// The bounded reason the sequence stopped. It equals <see cref="Outcome"/>
    /// unless the transaction unwound, in which case Outcome describes the STATE
    /// OF THE MACHINE and this describes WHY the unwind happened.
    /// </summary>
    internal ServiceOwnershipPromotionExecutorOutcome TriggeringFailure { get; }

    /// <summary>How far the ordered sequence got. Success is only ever the full count.</summary>
    internal int CompletedSteps { get; }

    internal bool PriorDescriptorRestored { get; }

    internal bool EntryMarkedStale { get; }

    internal bool IsPromoted => Outcome == ServiceOwnershipPromotionExecutorOutcome.Promoted;

    internal static ServiceOwnershipPromotionResult Refused(
        ServiceOwnershipPromotionExecutorOutcome outcome, int completedSteps) =>
        new(outcome, outcome, completedSteps, false, false);

    internal static ServiceOwnershipPromotionResult Unwound(
        ServiceOwnershipPromotionExecutorOutcome outcome,
        ServiceOwnershipPromotionExecutorOutcome triggeringFailure,
        int completedSteps,
        bool priorDescriptorRestored,
        bool entryMarkedStale) =>
        new(outcome, triggeringFailure, completedSteps, priorDescriptorRestored, entryMarkedStale);

    internal static ServiceOwnershipPromotionResult Promoted(int completedSteps) =>
        new(ServiceOwnershipPromotionExecutorOutcome.Promoted,
            ServiceOwnershipPromotionExecutorOutcome.Promoted, completedSteps, false, false);

    public override string ToString() => Outcome.ToString();
}

/// <summary>Bounded depromotion result.</summary>
internal sealed class ServiceOwnershipDepromotionResult
{
    private ServiceOwnershipDepromotionResult(
        ServiceOwnershipDepromotionExecutorOutcome outcome,
        IReadOnlyList<ServiceOwnershipPlanAction> executedActions)
    {
        Outcome = outcome;
        ExecutedActions = executedActions;
    }

    internal ServiceOwnershipDepromotionExecutorOutcome Outcome { get; }

    /// <summary>The planner actions this pass actually carried out, in order.</summary>
    internal IReadOnlyList<ServiceOwnershipPlanAction> ExecutedActions { get; }

    internal bool IsDepromoted =>
        Outcome == ServiceOwnershipDepromotionExecutorOutcome.Depromoted;

    internal static ServiceOwnershipDepromotionResult Create(
        ServiceOwnershipDepromotionExecutorOutcome outcome,
        List<ServiceOwnershipPlanAction> executedActions) =>
        new(outcome, executedActions.AsReadOnly());

    public override string ToString() => Outcome.ToString();
}

/// <summary>
/// The deterministic promotion / depromotion transaction. Every effect is a
/// constructor-injected purpose-specific port; this type performs none itself.
/// </summary>
internal sealed class ServiceOwnershipPromotionExecutor
{
    /// <summary>Steps (a) through (n). Success is only ever reported at the full count.</summary>
    internal const int TotalPromotionSteps = 14;

    private readonly IServiceOwnershipOwnerIdentityPort ownerIdentity;
    private readonly IServiceOwnershipServiceSidPort serviceSid;
    private readonly IServiceOwnershipCertificateFactsPort certificateFacts;
    private readonly IServiceOwnershipPriorDescriptorPort priorDescriptor;
    private readonly IServiceOwnershipApprovedDescriptorPort approvedDescriptor;
    private readonly IServiceOwnershipDescriptorRestorePort descriptorRestore;
    private readonly IServiceOwnershipCredentialObservationPort credential;
    private readonly IServiceOwnershipLedgerPersistencePort ledger;
    private readonly IServiceOwnershipPromotedRecipePort promotedRecipe;

    internal ServiceOwnershipPromotionExecutor(
        IServiceOwnershipOwnerIdentityPort ownerIdentity,
        IServiceOwnershipServiceSidPort serviceSid,
        IServiceOwnershipCertificateFactsPort certificateFacts,
        IServiceOwnershipPriorDescriptorPort priorDescriptor,
        IServiceOwnershipApprovedDescriptorPort approvedDescriptor,
        IServiceOwnershipDescriptorRestorePort descriptorRestore,
        IServiceOwnershipCredentialObservationPort credential,
        IServiceOwnershipLedgerPersistencePort ledger,
        IServiceOwnershipPromotedRecipePort promotedRecipe)
    {
        this.ownerIdentity = ownerIdentity;
        this.serviceSid = serviceSid;
        this.certificateFacts = certificateFacts;
        this.priorDescriptor = priorDescriptor;
        this.approvedDescriptor = approvedDescriptor;
        this.descriptorRestore = descriptorRestore;
        this.credential = credential;
        this.ledger = ledger;
        this.promotedRecipe = promotedRecipe;
    }

    // -------------------------------------------------------------------
    // PROMOTION
    // -------------------------------------------------------------------

    internal ServiceOwnershipPromotionResult Promote(
        ServicePromotionRequest? request,
        ServiceOwnershipLedgerValidationResult? currentLedger,
        string? entryId,
        string? utcTimestamp)
    {
        try
        {
            return PromoteCore(request, currentLedger, entryId, utcTimestamp);
        }
        catch (Exception)
        {
            return ServiceOwnershipPromotionResult.Refused(
                ServiceOwnershipPromotionExecutorOutcome.InvalidRequest, 0);
        }
    }

    private ServiceOwnershipPromotionResult PromoteCore(
        ServicePromotionRequest? request,
        ServiceOwnershipLedgerValidationResult? currentLedger,
        string? entryId,
        string? utcTimestamp)
    {
        // (a) REQUIRE AN ACCEPTED REQUEST.
        if (request is null
            || !ServiceOwnershipLedgerContract.IsValidBoundedToken(
                entryId, ServiceOwnershipLedgerContract.MaxStringLength))
        {
            return Refuse(ServiceOwnershipPromotionExecutorOutcome.InvalidRequest, 0);
        }

        // (b) REQUIRE AN ACCEPTED CURRENT LEDGER.
        if (currentLedger is null
            || currentLedger.IsRefused
            || currentLedger.Outcome is not (ServiceOwnershipLedgerOutcome.Absent
                or ServiceOwnershipLedgerOutcome.ValidEmpty))
        {
            return Refuse(ServiceOwnershipPromotionExecutorOutcome.InvalidRequest, 1);
        }

        string stamp = utcTimestamp ?? string.Empty;

        // (c) REQUIRE THE BOUNDED OWNER IDENTITY.
        ServiceOwnershipOwnerIdentityObservation owner = ownerIdentity.ObserveFixedOwner();
        if (!owner.IsObserved)
        {
            return Refuse(ServiceOwnershipPromotionExecutorOutcome.OwnerIdentityUnavailable, 2);
        }

        // (d) RESOLVE THE FIXED SERVICE SID EXACTLY ONCE.
        ServiceOwnershipServiceSidObservation service = serviceSid.ObserveFixedServiceSid();
        if (!service.IsResolved)
        {
            return Refuse(ServiceOwnershipPromotionExecutorOutcome.ServiceSidUnavailable, 3);
        }

        // (e) OBTAIN THE ONE ELIGIBLE CERTIFICATE OBSERVATION.
        ServiceOwnershipCertificateFacts facts =
            certificateFacts.ObserveCertificateFacts(request.CertificateThumbprintSha1);
        if (!facts.IsObserved
            || !string.Equals(
                facts.ThumbprintSha1, request.CertificateThumbprintSha1, StringComparison.Ordinal))
        {
            return Refuse(ServiceOwnershipPromotionExecutorOutcome.CertificateFactsUnavailable, 4);
        }

        if (!ServiceOwnershipPromotionGrantPlan.TryCreate(
                service, facts.Key, out ServiceOwnershipPromotionGrantPlan plan))
        {
            return Refuse(ServiceOwnershipPromotionExecutorOutcome.CertificateFactsUnavailable, 4);
        }

        // (f) VALIDATE THE PRIOR DESCRIPTOR AND THE ABSENCE OF A SERVICE ACE. The
        // capture refuses ServiceAceAlreadyPresent outright.
        ServiceOwnershipCapturedPriorDescriptor captured =
            priorDescriptor.CapturePriorDescriptor(facts.Key);
        if (!captured.IsCaptured)
        {
            return Refuse(ServiceOwnershipPromotionExecutorOutcome.PriorDescriptorUnavailable, 5);
        }

        if (!ServiceOwnershipTransitionFacts.TryCreate(
                entryId,
                request.ExpectedInstallationOwnershipId,
                owner.OwningUserSid,
                service.ServiceSid,
                facts.CredentialKind,
                facts.ThumbprintSha1,
                facts.Provenance,
                facts.Key.ProviderKind,
                facts.RightsProfileId,
                facts.Key.KeyIdentity,
                facts.GrantMechanism,
                plan.ApprovedMask,
                plan.RightsPolicyVersion,
                captured.DaclState,
                captured.BytesBase64,
                captured.Sha256,
                request.PromotedJobId,
                facts.Key.ProviderUniqueName,
                facts.Key.KeyStorageRoot,
                facts.Key.DescriptorFormat,
                out ServiceOwnershipTransitionFacts? transitionFacts))
        {
            return Refuse(ServiceOwnershipPromotionExecutorOutcome.TransitionRefused, 5);
        }

        // (f) COMPOSE Preparing + Intended EXACTLY ONCE - AND DO NOT PERSIST IT.
        // The certified entry this produces is the ONLY thing Setup can hand to the
        // credential observation port, because the entry type cannot be constructed
        // outside Shared. Composition therefore has to precede the observation, so a
        // composition refusal now OUTRANKS a credential mismatch: with no certified
        // entry there is nothing to observe. Nothing durable has happened yet.
        if (!TryComposeBeginPromotionIntent(
                currentLedger, transitionFacts!, entryId!, request.PromotedJobId,
                request.OperationId, stamp,
                out ServiceOwnershipTransitionResult? composedIntent,
                out ServiceOwnershipLedgerEntry? intendedEntry))
        {
            return Refuse(ServiceOwnershipPromotionExecutorOutcome.TransitionRefused, 5);
        }

        // (f) REQUIRE THE CREDENTIAL TO STILL BE THE CAPTURED PRIOR STATE, judged
        // against that certified entry. No ACL is reachable before (g) succeeds.
        if (credential.ObserveCredential(intendedEntry!)
            != ServiceOwnershipCredentialObservation.MatchesCapturedPriorState)
        {
            return Refuse(ServiceOwnershipPromotionExecutorOutcome.PreconditionObservationMismatch, 5);
        }

        // (g) PERSIST THE ALREADY-COMPOSED Preparing + Intended. These are the SAME
        // bytes the observation above was judged against - the authority is never
        // invoked a second time for this operation. This is the durable marker that
        // covers the whole window in which the grant may or may not have been applied.
        if (!TryPersistComposedTransition(
                composedIntent!, out ServiceOwnershipLedgerValidationResult? afterIntent))
        {
            return Refuse(ServiceOwnershipPromotionExecutorOutcome.LedgerPersistenceRefused, 6);
        }

        // (h) APPLY THE APPROVED GRANT, BOUND TO THE SNAPSHOT (f) CAPTURED. The
        // EXACT captured object is handed over, so the port can require the
        // current descriptor to still be that snapshot before it writes anything.
        // A refusal here means NO write was attempted, so the unwind below is a
        // pure refusal: this transaction changed nothing, and restoring the
        // captured bytes would destroy whatever change caused the refusal.
        if (approvedDescriptor.ApplyApprovedDescriptor(plan, captured)
            != ServiceOwnershipDescriptorApplyState.Applied)
        {
            return Unwind(
                ServiceOwnershipPromotionExecutorOutcome.DescriptorApplyRefused,
                7, facts.Key, captured, afterIntent!, entryId!, request, stamp,
                descriptorWasApplied: false);
        }

        // (i) REQUIRE MatchesRecordedGrant. The grant is PROVEN before it is
        // recorded, so a recorded mutation never outruns an observed one. The entry
        // comes from the state (g) actually persisted, not from a recomposition.
        if (!TryFindEntry(afterIntent!, entryId!, out ServiceOwnershipLedgerEntry? persistedIntentEntry)
            || credential.ObserveCredential(persistedIntentEntry!)
                != ServiceOwnershipCredentialObservation.MatchesRecordedGrant)
        {
            return Unwind(
                ServiceOwnershipPromotionExecutorOutcome.GrantObservationMismatch,
                8, facts.Key, captured, afterIntent!, entryId!, request, stamp,
                descriptorWasApplied: true);
        }

        // (j) PERSIST CredentialMutated + Intended.
        if (!TryTransitionAndPersist(
                ServiceOwnershipTransitionOperation.RecordCredentialMutated,
                afterIntent!, null, entryId!, string.Empty,
                request.OperationId, stamp,
                out ServiceOwnershipLedgerValidationResult? afterMutation))
        {
            return Unwind(
                ServiceOwnershipPromotionExecutorOutcome.LedgerPersistenceRefused,
                9, facts.Key, captured, afterIntent!, entryId!, request, stamp,
                descriptorWasApplied: true);
        }

        // (k) PERSIST AND VERIFY THE PROMOTED RECIPE. It lands AFTER the grant is
        // proven and recorded, and BEFORE (l), so an ACTIVE entry always has one.
        if (promotedRecipe.PersistPromotedRecipe(request)
            != ServiceOwnershipPromotedRecipePortOutcome.Completed)
        {
            return Unwind(
                ServiceOwnershipPromotionExecutorOutcome.PromotedRecipeRefused,
                10, facts.Key, captured, afterMutation!, entryId!, request, stamp,
                descriptorWasApplied: true);
        }

        // (l) PERSIST Done + Active.
        if (!TryTransitionAndPersist(
                ServiceOwnershipTransitionOperation.CompletePromotion,
                afterMutation!, null, entryId!, string.Empty,
                request.OperationId, stamp,
                out ServiceOwnershipLedgerValidationResult? afterComplete))
        {
            return Unwind(
                ServiceOwnershipPromotionExecutorOutcome.LedgerPersistenceRefused,
                11, facts.Key, captured, afterMutation!, entryId!, request, stamp,
                descriptorWasApplied: true);
        }

        // (m) REQUIRE A FINAL MatchesRecordedGrant. NO SUCCESS BEFORE THIS PASSES.
        if (!TryFindEntry(afterComplete!, entryId!, out ServiceOwnershipLedgerEntry? activeEntry)
            || credential.ObserveCredential(activeEntry!)
                != ServiceOwnershipCredentialObservation.MatchesRecordedGrant)
        {
            return Unwind(
                ServiceOwnershipPromotionExecutorOutcome.GrantObservationMismatch,
                12, facts.Key, captured, afterComplete!, entryId!, request, stamp,
                descriptorWasApplied: true);
        }

        // (n) REPORT BOUNDED SUCCESS. ONLY NOW.
        return ServiceOwnershipPromotionResult.Promoted(TotalPromotionSteps);
    }

    private static ServiceOwnershipPromotionResult Refuse(
        ServiceOwnershipPromotionExecutorOutcome outcome, int completedSteps) =>
        ServiceOwnershipPromotionResult.Refused(outcome, completedSteps);

    /// <summary>
    /// Reverse-order compensation. The descriptor goes back first, then the
    /// promoted Recipe is compensated, and the ledger records the truth: the entry
    /// is reaped only when the restore was PROVEN, and marked STALE otherwise so
    /// the captured bytes needed to finish by hand survive.
    /// </summary>
    private ServiceOwnershipPromotionResult Unwind(
        ServiceOwnershipPromotionExecutorOutcome failure,
        int completedSteps,
        ServiceOwnershipPromotionKeyHandle key,
        ServiceOwnershipCapturedPriorDescriptor captured,
        ServiceOwnershipLedgerValidationResult ledgerState,
        string entryId,
        ServicePromotionRequest request,
        string stamp,
        bool descriptorWasApplied)
    {
        bool restored = !descriptorWasApplied;
        if (descriptorWasApplied)
        {
            restored = descriptorRestore.RestoreCapturedDescriptor(key, captured)
                == ServiceOwnershipDescriptorRestoreState.RestoredAndVerified;
        }

        promotedRecipe.CompensatePromotedRecipe(request.PromotedJobId);

        if (!restored)
        {
            // THE RESTORE COULD NOT BE PROVEN. Nothing is removed.
            bool marked = TryTransitionAndPersist(
                ServiceOwnershipTransitionOperation.MarkStale,
                ledgerState, null, entryId, string.Empty, request.OperationId, stamp, out _);

            return ServiceOwnershipPromotionResult.Unwound(
                ServiceOwnershipPromotionExecutorOutcome.RecoveryRequired,
                failure, completedSteps, false, marked);
        }

        bool reaped = TryReapEntry(ledgerState, entryId, request.OperationId, stamp);

        return ServiceOwnershipPromotionResult.Unwound(
            reaped
                ? ServiceOwnershipPromotionExecutorOutcome.RolledBack
                : ServiceOwnershipPromotionExecutorOutcome.RecoveryRequired,
            failure, completedSteps, true, false);
    }

    /// <summary>
    /// Removes the entry through the transition the lifecycle model defines for the
    /// state it is ACTUALLY in. An intended entry is dropped directly; an active one
    /// must walk the restore chain first, because dropping an active entry would
    /// discard the record of a grant that once existed.
    /// </summary>
    private bool TryReapEntry(
        ServiceOwnershipLedgerValidationResult ledgerState,
        string entryId,
        string operationId,
        string stamp)
    {
        ServiceOwnershipLifecycleState lifecycle = ServiceOwnershipLifecycleState.Unspecified;
        if (ledgerState.Document is not null)
        {
            foreach (ServiceOwnershipLedgerEntry entry in ledgerState.Document.Entries)
            {
                if (string.Equals(entry.EntryId, entryId, StringComparison.Ordinal))
                {
                    lifecycle = entry.LifecycleState;
                    break;
                }
            }
        }

        if (lifecycle == ServiceOwnershipLifecycleState.Intended)
        {
            return TryTransitionAndPersist(
                ServiceOwnershipTransitionOperation.RemoveAbandonedIntentEntry,
                ledgerState, null, entryId, string.Empty, operationId, stamp, out _);
        }

        if (lifecycle != ServiceOwnershipLifecycleState.Active)
        {
            return false;
        }

        if (!TryTransitionAndPersist(
                ServiceOwnershipTransitionOperation.BeginRestore,
                ledgerState, null, entryId, string.Empty, operationId, stamp,
                out ServiceOwnershipLedgerValidationResult? restoring))
        {
            return false;
        }

        if (!TryTransitionAndPersist(
                ServiceOwnershipTransitionOperation.MarkRestored,
                restoring!, null, entryId, string.Empty, operationId, stamp,
                out ServiceOwnershipLedgerValidationResult? restoredState))
        {
            return false;
        }

        return TryTransitionAndPersist(
            ServiceOwnershipTransitionOperation.RemoveRestoredEntry,
            restoredState!, null, entryId, string.Empty, operationId, stamp, out _);
    }

    // -------------------------------------------------------------------
    // DEPROMOTION AND RECOVERY - planner actions only
    // -------------------------------------------------------------------

    internal ServiceOwnershipDepromotionResult ExecutePlan(
        ServiceOwnershipLifecyclePlan? plan,
        ServiceOwnershipLedgerValidationResult? currentLedger,
        ServiceOwnershipPromotionKeyHandle key,
        ServiceOwnershipCapturedPriorDescriptor captured,
        string? entryId,
        string? promotedJobId,
        string? operationId,
        string? utcTimestamp)
    {
        var executed = new List<ServiceOwnershipPlanAction>();

        try
        {
            if (plan is null || currentLedger is null || currentLedger.IsRefused)
            {
                return ServiceOwnershipDepromotionResult.Create(
                    ServiceOwnershipDepromotionExecutorOutcome.InvalidRequest, executed);
            }
            if (plan.Outcome == ServiceOwnershipPlanOutcome.Refused)
            {
                return ServiceOwnershipDepromotionResult.Create(
                    ServiceOwnershipDepromotionExecutorOutcome.PlanRefused, executed);
            }
            if (plan.Outcome == ServiceOwnershipPlanOutcome.NoAction || plan.Actions.Count == 0)
            {
                return ServiceOwnershipDepromotionResult.Create(
                    ServiceOwnershipDepromotionExecutorOutcome.NoAction, executed);
            }

            string stamp = utcTimestamp ?? string.Empty;
            string entry = entryId ?? string.Empty;
            string job = promotedJobId ?? string.Empty;
            string operation = operationId ?? string.Empty;
            ServiceOwnershipLedgerValidationResult state = currentLedger;

            foreach (ServiceOwnershipPlanAction action in plan.Actions)
            {
                switch (action)
                {
                    case ServiceOwnershipPlanAction.BeginRestore:
                        if (!TryTransitionAndPersist(
                                ServiceOwnershipTransitionOperation.BeginRestore,
                                state, null, entry, string.Empty, operation, stamp, out state!))
                        {
                            return Fail(ServiceOwnershipDepromotionExecutorOutcome.LedgerPersistenceRefused, executed);
                        }
                        break;

                    case ServiceOwnershipPlanAction.RestoreCapturedPriorDacl:
                    case ServiceOwnershipPlanAction.VerifyCapturedPriorDacl:
                        if (descriptorRestore.RestoreCapturedDescriptor(key, captured)
                            != ServiceOwnershipDescriptorRestoreState.RestoredAndVerified)
                        {
                            bool marked = TryTransitionAndPersist(
                                ServiceOwnershipTransitionOperation.MarkStale,
                                state, null, entry, string.Empty, operation, stamp, out _);

                            executed.Add(ServiceOwnershipPlanAction.MarkStale);
                            return ServiceOwnershipDepromotionResult.Create(
                                marked
                                    ? ServiceOwnershipDepromotionExecutorOutcome.MarkedStale
                                    : ServiceOwnershipDepromotionExecutorOutcome.RestoreNotProven,
                                executed);
                        }
                        break;

                    case ServiceOwnershipPlanAction.MarkRestored:
                        if (!TryTransitionAndPersist(
                                ServiceOwnershipTransitionOperation.MarkRestored,
                                state, null, entry, string.Empty, operation, stamp, out state!))
                        {
                            return Fail(ServiceOwnershipDepromotionExecutorOutcome.LedgerPersistenceRefused, executed);
                        }
                        break;

                    case ServiceOwnershipPlanAction.RemoveRestoredEntry:
                        promotedRecipe.CompensatePromotedRecipe(job);
                        if (!TryTransitionAndPersist(
                                ServiceOwnershipTransitionOperation.RemoveRestoredEntry,
                                state, null, entry, string.Empty, operation, stamp, out state!))
                        {
                            return Fail(ServiceOwnershipDepromotionExecutorOutcome.LedgerPersistenceRefused, executed);
                        }
                        break;

                    case ServiceOwnershipPlanAction.RemoveAbandonedIntentEntry:
                        promotedRecipe.CompensatePromotedRecipe(job);
                        if (!TryTransitionAndPersist(
                                ServiceOwnershipTransitionOperation.RemoveAbandonedIntentEntry,
                                state, null, entry, string.Empty, operation, stamp, out state!))
                        {
                            return Fail(ServiceOwnershipDepromotionExecutorOutcome.LedgerPersistenceRefused, executed);
                        }
                        break;

                    case ServiceOwnershipPlanAction.RemoveJobAssociation:
                        promotedRecipe.CompensatePromotedRecipe(job);
                        if (!TryTransitionAndPersist(
                                ServiceOwnershipTransitionOperation.RemoveJobAssociation,
                                state, null, entry, job, operation, stamp, out state!))
                        {
                            return Fail(ServiceOwnershipDepromotionExecutorOutcome.LedgerPersistenceRefused, executed);
                        }
                        break;

                    case ServiceOwnershipPlanAction.MarkStale:
                        if (!TryTransitionAndPersist(
                                ServiceOwnershipTransitionOperation.MarkStale,
                                state, null, entry, string.Empty, operation, stamp, out state!))
                        {
                            return Fail(ServiceOwnershipDepromotionExecutorOutcome.LedgerPersistenceRefused, executed);
                        }
                        executed.Add(action);
                        return ServiceOwnershipDepromotionResult.Create(
                            ServiceOwnershipDepromotionExecutorOutcome.MarkedStale, executed);

                    case ServiceOwnershipPlanAction.PersistLedger:
                        // Every branch above already persisted through the one port.
                        break;

                    default:
                        return Fail(
                            ServiceOwnershipDepromotionExecutorOutcome.UnsupportedPlannedAction, executed);
                }

                executed.Add(action);
            }

            return ServiceOwnershipDepromotionResult.Create(
                ServiceOwnershipDepromotionExecutorOutcome.Depromoted, executed);
        }
        catch (Exception)
        {
            return ServiceOwnershipDepromotionResult.Create(
                ServiceOwnershipDepromotionExecutorOutcome.InvalidRequest, executed);
        }
    }

    private static ServiceOwnershipDepromotionResult Fail(
        ServiceOwnershipDepromotionExecutorOutcome outcome,
        List<ServiceOwnershipPlanAction> executed) =>
        ServiceOwnershipDepromotionResult.Create(outcome, executed);

    // -------------------------------------------------------------------
    // THE ONE COMPOSE STEP AND THE ONE PERSIST STEP
    // -------------------------------------------------------------------

    /// <summary>
    /// The ONE place this file asks the certified authority for anything. It
    /// composes, validates and serializes; it persists nothing and it never
    /// invents a ledger byte of its own.
    /// </summary>
    private static bool TryComposeTransition(
        ServiceOwnershipTransitionOperation operation,
        ServiceOwnershipLedgerValidationResult source,
        ServiceOwnershipTransitionFacts? facts,
        string entryId,
        string promotedJobId,
        string operationId,
        string stamp,
        out ServiceOwnershipTransitionResult? composed)
    {
        composed = null;

        if (!ServiceOwnershipTransitionRequest.TryCreate(
                operation, source, facts, entryId, promotedJobId, operationId, stamp,
                out ServiceOwnershipTransitionRequest? request))
        {
            return false;
        }

        ServiceOwnershipTransitionResult transition =
            ServiceOwnershipTransitionAuthority.Apply(request);
        if (!transition.IsTransitioned
            || transition.Accepted is null
            || transition.Serialized is null)
        {
            return false;
        }

        composed = transition;
        return true;
    }

    /// <summary>
    /// Composes Preparing + Intended ONCE and hands back BOTH the composed result
    /// and its certified entry. The shape is re-proven independently here - one
    /// entry, the requested entry id, Intended, and exactly the requested promoted
    /// job id - so the entry handed to the observation port is never something the
    /// caller merely hoped for.
    /// </summary>
    private static bool TryComposeBeginPromotionIntent(
        ServiceOwnershipLedgerValidationResult source,
        ServiceOwnershipTransitionFacts facts,
        string entryId,
        string promotedJobId,
        string operationId,
        string stamp,
        out ServiceOwnershipTransitionResult? composed,
        out ServiceOwnershipLedgerEntry? intended)
    {
        composed = null;
        intended = null;

        if (!TryComposeTransition(
                ServiceOwnershipTransitionOperation.BeginPromotionIntent,
                source, facts, entryId, string.Empty, operationId, stamp,
                out ServiceOwnershipTransitionResult? candidate))
        {
            return false;
        }

        ServiceOwnershipLedgerDocument? document = candidate!.Accepted!.Document;
        if (document is null
            || document.TransactionState != ServiceOwnershipTransactionState.Preparing
            || document.Entries.Count != 1)
        {
            return false;
        }

        ServiceOwnershipLedgerEntry entry = document.Entries[0];
        if (!string.Equals(entry.EntryId, entryId, StringComparison.Ordinal)
            || entry.LifecycleState != ServiceOwnershipLifecycleState.Intended
            || entry.AssociatedPromotedJobIds.Count != 1
            || !string.Equals(entry.AssociatedPromotedJobIds[0], promotedJobId, StringComparison.Ordinal))
        {
            return false;
        }

        composed = candidate;
        intended = entry;
        return true;
    }

    /// <summary>
    /// The ONE place a ledger byte is ever handed to the persistence port. It
    /// persists an ALREADY-COMPOSED result and composes nothing, so the bytes that
    /// reach disk are always the exact bytes that were validated upstream.
    /// </summary>
    private bool TryPersistComposedTransition(
        ServiceOwnershipTransitionResult composed,
        out ServiceOwnershipLedgerValidationResult? next)
    {
        next = null;

        if (!composed.IsTransitioned
            || composed.Accepted is null
            || composed.Serialized is null)
        {
            return false;
        }

        if (ledger.Persist(composed.Serialized, composed.LedgerOutcome, composed.Generation)
            != ServiceOwnershipLedgerPersistState.Persisted)
        {
            return false;
        }

        next = composed.Accepted;
        return true;
    }

    /// <summary>
    /// The certified entry an accepted ledger state carries for one entry id. It
    /// reads an already-validated document and can never build one.
    /// </summary>
    private static bool TryFindEntry(
        ServiceOwnershipLedgerValidationResult state,
        string entryId,
        out ServiceOwnershipLedgerEntry? entry)
    {
        entry = null;

        if (state.Document is null)
        {
            return false;
        }

        foreach (ServiceOwnershipLedgerEntry candidate in state.Document.Entries)
        {
            if (string.Equals(candidate.EntryId, entryId, StringComparison.Ordinal))
            {
                entry = candidate;
                return true;
            }
        }

        return false;
    }

    private bool TryTransitionAndPersist(
        ServiceOwnershipTransitionOperation operation,
        ServiceOwnershipLedgerValidationResult source,
        ServiceOwnershipTransitionFacts? facts,
        string entryId,
        string promotedJobId,
        string operationId,
        string stamp,
        out ServiceOwnershipLedgerValidationResult? next)
    {
        next = null;

        return TryComposeTransition(
                   operation, source, facts, entryId, promotedJobId, operationId, stamp,
                   out ServiceOwnershipTransitionResult? composed)
               && TryPersistComposedTransition(composed!, out next);
    }
}
