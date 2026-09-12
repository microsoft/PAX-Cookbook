// PAX Cookbook - INITIATING-USER IDENTITY CHANNEL (cycle 58, Setup only)
//
// WHAT THIS FILE IS. The PRODUCTION named-pipe channel by which the ELEVATED
// Setup helper learns, from the Windows kernel, which NON-ELEVATED user
// initiated an installation, and records that user in the installation anchor.
// The elevated helper is the SERVER; the non-elevated initiating process is the
// CLIENT. This is the topology Cycle 57R4 measured attended and is implemented
// here directly, in product code, rather than in a harness script.
//
// THE ONE THING THIS CHANNEL EXISTS TO PREVENT. The approving administrator
// must NEVER become the installation owner. The anchor is written from the
// SID the kernel reports for the CONNECTED CLIENT, never from the elevated
// server's own identity and never from a value supplied on a command line.
//
// WHAT THIS FILE CANNOT DO, by construction. It never opens a certificate
// store or private key; never reads or writes an ACL, the registry or a
// credential vault; never creates, changes, starts or stops a service; never
// elevates and never requests elevation; never opens a network socket (the
// pipe is created with PIPE_REJECT_REMOTE_CLIENTS); never starts a process;
// never reads or writes the ownership ledger; never touches PAX and never
// starts a Bake. Its ONLY durable side effect is one installation-anchor write
// through ServiceInstallationAnchorStore.
//
// PRODUCTION CALLER (cycle 59). ServiceAnchorElevationCoordinator and
// ServiceAnchorElevatedHelperDispatch are this channel's first production
// callers. Neither is invoked by ordinary install, update or repair, and
// neither was invoked during the cycle that introduced them.
//
// THE BINDING SEQUENCE (cycle-59 INVERTED HANDSHAKE). Every step is ordered
// and every failure is terminal:
//   1. The NON-ELEVATED initiator creates ONLY a fresh CSPRNG endpoint NAME.
//      The command line it hands the elevated helper carries the endpoint
//      name, the initiator's process id and the initiator's creation FILETIME,
//      AND NOTHING ELSE. No SID and no secret ever crosses a command line.
//   2. The elevated helper - not the initiator - creates the FIRST pipe
//      instance. The endpoint name is NOT a secret; squatting is defeated by
//      FILE_FLAG_FIRST_PIPE_INSTANCE, which turns a pre-existing squatter into
//      a creation failure rather than a silent hijack.
//   3. The DACL is explicit and closed: protected (no inheritance) with
//      exactly ONE allow ACE, for the PID + creation-FILETIME BOUND INITIATOR
//      resolved from the kernel. It is NEVER the elevated helper's own account:
//      when a different administrator supplies credentials at the UAC prompt,
//      an ACE for the approver would lock the real initiating user out of its
//      own transaction. No world SID, no Authenticated Users, no fallback, and
//      no mandatory-label ACE - Cycle 57R4 MEASURED that a Medium-integrity
//      client connects to a High-integrity server pipe with no mandatory label
//      ACE supplied.
//   4. The client connects at SecurityIdentification impersonation level, and
//      never Impersonation or Delegation. The server can therefore learn WHO
//      the client is and can never act AS the client.
//   5. The transaction challenge is generated ONLY AFTER the client has
//      connected, is sent by the server, and is echoed by the client. It is
//      fresh, unpredictable and drawn from RandomNumberGenerator; nothing here
//      is sequential, derived from a PID, a tick count or an installation id.
//   6. Exactly ONE request is bound to ONE live connection. AwaitAndBind can
//      run only once per endpoint; a second call is refused, so a bound
//      endpoint is never reusable.
//   7. The client's PID and SID are taken from the LIVE CONNECTION
//      (GetNamedPipeClientProcessId, and the impersonated connection token).
//      Impersonation is reverted and the reversion is CHECKED. The bound
//      process is re-read AFTER connection as well as before pipe creation, so
//      a process that died or was recycled in between is refused.
//   8. The command-line admission facts are ADMISSION FILTERING ONLY. They are
//      never used as identity; they are only COMPARED against the
//      kernel-derived values, and any disagreement is a refusal.
//   9. ORDER IS BINDING: validate, then persist the anchor, THEN acknowledge.
//      The acknowledgement is written at exactly one place in this file, after
//      the store has already reported a durable result. A refusal token is not
//      an acknowledgement and uses a different, distinguishable line.
//  10. The connection stays live from WaitForConnection through the final
//      acknowledgement; the endpoint is disposed and the transaction material
//      is cleared in a finally block, on both the completion and refusal paths.
//
// IDEMPOTENCE ACROSS A LOST ACKNOWLEDGEMENT (cycle-59 fix). The anchor is read
// ONLY AFTER the kernel identity is established. A new installation id is
// created ONLY when the anchor is truly ABSENT; when a valid anchor already
// belongs to the SAME kernel-derived SID its existing installation id is
// REUSED, so a retry after a persisted anchor whose acknowledgement was lost
// succeeds idempotently instead of refusing forever. A valid anchor belonging
// to a DIFFERENT SID is still a hard conflict and its bytes are never touched.
//
// ADMINISTRATIVE RECOVERY IS A FUTURE OPERATION, DELIBERATELY NOT IMPLEMENTED
// HERE. This file has no reset, no delete and no repair authority, and must
// not acquire one incidentally. A later attended, elevated recovery operation
// MAY reset the anchor, but only after proving that no service, ownership
// ledger, credential, job or other owned state depends on it. Until then a
// conflicting or malformed anchor is preserved exactly as found and reported
// as a bounded recovery-required refusal.
//
// PRIVACY - FAIL CLOSED. No outcome, wire message or ToString() carries a SID,
// an installation id, a PID, a path, an account name, a challenge, an
// exception or a native status. The only value ever sent back is a bounded
// protocol token plus a bounded outcome name. No catch clause in this file
// binds an exception variable, so there is no value from which a message could
// ever be read.
//
// DISCLOSED LIMITATION. The challenge is held as a char[] and cleared, but the
// wire request is briefly a managed System.String while it is parsed. .NET
// strings are immutable and cannot be forcibly zeroed, so - unlike the byte and
// char buffers this file DOES own and clear - that transient string cannot be
// scrubbed from process memory. This is the same limitation already accepted by
// ServiceOwnershipLedgerReader.
using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

/// <summary>
/// Bounded outcome of one identity-channel transaction, shared by both ends.
/// Exactly one member, <see cref="Completed"/>, is success. Zero is the
/// permanent, safe default so an uninitialised value can never read as success.
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data.
/// </summary>
public enum ServiceInitiatingUserIdentityChannelOutcome
{
    Unspecified = 0,

    /// <summary>Identity bound, anchor persisted, acknowledgement delivered.</summary>
    Completed = 1,

    UnsupportedPlatform = 2,

    /// <summary>The endpoint could not be created, or has already bound its one request.</summary>
    EndpointUnavailable = 3,

    /// <summary>No client connected inside the bounded wait.</summary>
    NoClientConnected = 4,

    /// <summary>The request line was absent, oversized, undecodable or not the expected shape.</summary>
    MalformedRequest = 5,

    /// <summary>The request did not carry this endpoint's transaction challenge.</summary>
    ChallengeMismatch = 6,

    /// <summary>The kernel would not report the connected client's PID or SID.</summary>
    ClientIdentityUnavailable = 7,

    /// <summary>A command-line admission fact disagreed with the kernel-derived value.</summary>
    AdmissionFactMismatch = 8,

    /// <summary>A valid anchor already exists for a DIFFERENT installation or user.</summary>
    AnchorConflict = 9,

    /// <summary>The anchor could not be persisted, or existing state was refused.</summary>
    AnchorPersistenceRefused = 10,

    /// <summary>The anchor is durable but the acknowledgement could not be delivered.</summary>
    AcknowledgementFailed = 11,

    /// <summary>Client side: the server refused, or never sent an acknowledgement.</summary>
    AcknowledgementNotReceived = 12,

    /// <summary>
    /// The PID + creation-FILETIME bound initiator is gone, was recycled, or
    /// its identity changed between pipe creation and the live connection.
    /// </summary>
    InitiatorNotBound = 13,

    /// <summary>The post-connection challenge could not be delivered to the client.</summary>
    ChallengeNotDelivered = 14,

    /// <summary>
    /// CYCLE 62. A bound transaction's MUTATION-FREE preflight refused. It runs
    /// BEFORE the anchor is persisted, so nothing durable was touched.
    /// </summary>
    TransactionPreflightRefused = 15,

    /// <summary>
    /// CYCLE 62. A bound transaction failed and every piece of machine state it
    /// created was provably removed. Never a success.
    /// </summary>
    TransactionCompensated = 16,

    /// <summary>
    /// CYCLE 62. A bound transaction could not prove its machine state is
    /// closed. The anchor is PRESERVED and an attended, elevated recovery
    /// operation is the only remedy.
    /// </summary>
    TransactionRecoveryRequired = 17,

    /// <summary>
    /// CYCLE 67R. The anchor write reached the point at which the fixed machine
    /// directories are created and then failed. Durable state MAY have been left
    /// behind, so this is NEVER refused-before-mutation.
    ///
    /// It is a SEPARATE wire value from <see cref="AnchorPersistenceRefused"/>,
    /// which now means only the refusals decided BEFORE any directory could be
    /// created.
    /// </summary>
    AnchorPersistenceRecoveryRequired = 18,

    // ---- CYCLE 89: the BOUNDED POST-AUTHENTICATION PAYLOAD phase -----------
    //
    // Every value below is reachable ONLY when the bound transaction implements
    // IServiceIdentityBoundPayloadTransaction. A legacy enable or disable
    // endpoint can never speak any of them, which is what keeps the cycle-59
    // wire byte-for-byte unchanged.

    /// <summary>
    /// The ownership payload phase REQUIRES an anchor that already exists and
    /// already belongs to the kernel-derived SID. Absent, unreadable or refused
    /// anchor state stops here, BEFORE the payload-ready response is sent, and
    /// no anchor is ever created or repaired on this path.
    /// </summary>
    AnchorRequiredButUnavailable = 19,

    /// <summary>The payload-ready response could not be delivered to the client.</summary>
    PayloadReadyNotDelivered = 20,

    /// <summary>
    /// The framed payload was absent, mis-shaped, truncated, over-length,
    /// zero-length, non-decimal, overflowing, followed by trailing data, or
    /// arrived on a connection that closed early.
    /// </summary>
    PayloadFrameMalformed = 21,

    /// <summary>The frame's declared byte length exceeded the derived transport ceiling.</summary>
    PayloadOversized = 22,

    /// <summary>
    /// The frame digest did not equal the domain-separated, challenge-bound
    /// digest of the received bytes.
    /// </summary>
    PayloadDigestMismatch = 23,

    /// <summary>The payload bytes were not strict UTF-8, or carried a byte-order mark.</summary>
    PayloadNotUtf8 = 24,

    /// <summary>
    /// The payload decoded but the bound payload transaction refused it. No
    /// preflight ran and nothing was mutated.
    /// </summary>
    PayloadRefused = 25,

    /// <summary>
    /// The payload's installation ownership id did not equal the validated
    /// anchor's installation id. Refused BEFORE the mutation-free preflight.
    /// </summary>
    InstallationOwnershipMismatch = 26,

    /// <summary>Client side: the server never offered the payload-ready response.</summary>
    PayloadNotOffered = 27,
}

/// <summary>
/// CYCLE 89. The bounded verdict of the payload transaction's MUTATION-FREE
/// acceptance step - parse, contract validation and the installation-ownership
/// comparison. Zero is the permanent, safe default so an uninitialised value can
/// never admit a payload.
/// </summary>
internal enum ServiceIdentityPayloadAcceptanceState
{
    /// <summary>Fails closed. Never admits a payload.</summary>
    Unspecified = 0,

    /// <summary>The ONLY value that lets the ordered sequence reach the preflight.</summary>
    Accepted = 1,

    /// <summary>A bounded parse or contract refusal. Nothing was mutated.</summary>
    Refused = 2,

    /// <summary>
    /// The request's installation ownership id did not equal the anchor's. It is
    /// a SEPARATE value from <see cref="Refused"/> because the two read very
    /// differently in an audit trail: one is a malformed request, the other is a
    /// request aimed at somebody else's installation.
    /// </summary>
    InstallationOwnershipMismatch = 3,
}

/// <summary>
/// The bounded verdict of a bound transaction's MUTATION-FREE preflight. Zero
/// is the permanent, safe default so an uninitialised value can never authorize
/// a mutation.
/// </summary>
internal enum ServiceIdentityBoundTransactionPreflightState
{
    Unspecified = 0,

    /// <summary>The ONLY value that authorizes persisting the anchor and mutating machine state.</summary>
    Proceed = 1,

    /// <summary>A bounded refusal. Nothing was written and nothing needs undoing.</summary>
    Refused = 2,

    /// <summary>
    /// Existing machine state cannot be explained by this transaction. Never
    /// repaired automatically; an attended, elevated recovery is the remedy.
    /// </summary>
    RecoveryRequired = 3,
}

/// <summary>Bounded terminal state of a bound transaction's apply phase.</summary>
internal enum ServiceIdentityBoundTransactionState
{
    Unspecified = 0,

    /// <summary>Applied and verified exactly. The single acknowledgement may be sent.</summary>
    Completed = 1,

    /// <summary>Failed, and every piece of state this transaction created was provably removed.</summary>
    Compensated = 2,

    /// <summary>Failed, and closure could not be proven. The anchor is preserved.</summary>
    RecoveryRequired = 3,
}

/// <summary>
/// CYCLE 63R. The CLOSED anchor-creation policy. It is an ENUM, deliberately not
/// a delegate or a callback: a policy that could be supplied as code would let a
/// caller decide anchor behaviour inside the elevated window, which is exactly
/// the authority this boundary exists to withhold.
///
/// Zero is the permanent, safe default so an uninitialised value never authorizes
/// creating an ownership record.
/// </summary>
internal enum ServiceAnchorCreationPolicy
{
    Unspecified = 0,

    /// <summary>ENABLE. An absent anchor is created; a matching anchor is reused.</summary>
    CreateOrReuse = 1,

    /// <summary>
    /// DISABLE. An anchor is NEVER created. An absent anchor is reported as
    /// absent and the transaction decides what that means.
    /// </summary>
    NeverCreate = 2,
}

/// <summary>
/// CYCLE 63R. The TRUTHFUL anchor situation this attempt observed. Each value is
/// distinct because compensation and ownership rules depend on telling them
/// apart: an anchor this attempt CREATED may be removed by its own compensation,
/// while a REUSED or pre-existing one may not.
/// </summary>
internal enum ServiceAnchorPresence
{
    Unspecified = 0,

    /// <summary>No anchor existed, and none was created.</summary>
    Absent = 1,

    /// <summary>A valid anchor for THIS initiator already existed and was left untouched.</summary>
    MatchingPresent = 2,

    /// <summary>This very attempt created the anchor.</summary>
    CreatedThisAttempt = 3,

    /// <summary>A valid anchor for THIS initiator existed and was rewritten idempotently.</summary>
    Reused = 4,
}

/// <summary>
/// Everything a bound transaction is allowed to learn from the channel. It
/// carries the fixed service directory, the DURABLE anchor, and the single
/// ordering fact compensation depends on. It carries no SID beyond the anchor's
/// own, no pid, no challenge, no handle and no capability.
/// </summary>
internal readonly struct ServiceIdentityBoundTransactionContext
{
    internal ServiceIdentityBoundTransactionContext(
        string serviceDirectory, ServiceInstallationAnchorDocument anchor, bool anchorCreatedThisAttempt)
        : this(
            serviceDirectory,
            anchor,
            anchorCreatedThisAttempt,
            anchorCreatedThisAttempt ? ServiceAnchorPresence.CreatedThisAttempt : ServiceAnchorPresence.Reused)
    {
    }

    internal ServiceIdentityBoundTransactionContext(
        string serviceDirectory,
        ServiceInstallationAnchorDocument anchor,
        bool anchorCreatedThisAttempt,
        ServiceAnchorPresence presence)
    {
        ServiceDirectory = serviceDirectory;
        Anchor = anchor;
        AnchorCreatedThisAttempt = anchorCreatedThisAttempt;
        Presence = presence;
    }

    internal string ServiceDirectory { get; }

    internal ServiceInstallationAnchorDocument Anchor { get; }

    /// <summary>
    /// True ONLY when this attempt is the one that created the anchor. It is the
    /// sole authorization for removing the anchor during same-transaction
    /// compensation; a reused anchor is never removed.
    /// </summary>
    internal bool AnchorCreatedThisAttempt { get; }

    /// <summary>
    /// CYCLE 63R. The truthful anchor situation. Absent, matching-present,
    /// created-this-attempt and reused are four DIFFERENT facts and are never
    /// collapsed into one boolean.
    /// </summary>
    internal ServiceAnchorPresence Presence { get; }

    /// <summary>Carries the bounded type name only.</summary>
    public override string ToString() => nameof(ServiceIdentityBoundTransactionContext);
}

/// <summary>The bounded apply result. Never a path, identity or exception.</summary>
internal readonly struct ServiceIdentityBoundTransactionResult
{
    private ServiceIdentityBoundTransactionResult(ServiceIdentityBoundTransactionState state, bool anchorRemoved)
    {
        State = state;
        AnchorRemoved = anchorRemoved;
    }

    internal ServiceIdentityBoundTransactionState State { get; }

    /// <summary>
    /// True only when compensation removed an anchor THIS transaction created,
    /// after every other piece of created state was proven gone.
    /// </summary>
    internal bool AnchorRemoved { get; }

    internal static ServiceIdentityBoundTransactionResult Completed() =>
        new(ServiceIdentityBoundTransactionState.Completed, false);

    internal static ServiceIdentityBoundTransactionResult Compensated(bool anchorRemoved) =>
        new(ServiceIdentityBoundTransactionState.Compensated, anchorRemoved);

    internal static ServiceIdentityBoundTransactionResult RecoveryRequired() =>
        new(ServiceIdentityBoundTransactionState.RecoveryRequired, false);

    public override string ToString() => State.ToString();
}

/// <summary>
/// CYCLE 62. The ONE narrowly typed extension point by which an identity-bound
/// transaction may run inside this channel.
///
/// IT IS NOT A DELEGATE AND NOT AN ARBITRARY CALLBACK, deliberately. A delegate
/// would let any caller inject any code into the elevated, identity-bound
/// window; this interface has exactly two members with fixed, bounded shapes,
/// and exactly ONE production implementation is permitted - the fixed
/// service-enable transaction.
///
/// THE ORDERING THE CHANNEL ENFORCES, and which no implementation may reorder:
/// existing anchor read and classified, conflicting or malformed anchor
/// refused, THEN <see cref="Preflight"/> (mutation-free), THEN the anchor is
/// persisted or idempotently reused, THEN <see cref="Apply"/>, THEN - and only
/// after an exact final verification inside Apply - the single acknowledgement.
/// </summary>
internal interface IServiceIdentityBoundTransaction
{
    /// <summary>
    /// MUTATION-FREE. It must not write a file, create a directory, change an
    /// access-control list, or create, change, start, stop or delete a service.
    /// It runs BEFORE the anchor is persisted.
    /// </summary>
    ServiceIdentityBoundTransactionPreflightState Preflight();

    /// <summary>
    /// Runs only after the anchor is DURABLE. It owns its own compensation and
    /// must never report Completed unless its final verification reproduced the
    /// exact expected state.
    /// </summary>
    ServiceIdentityBoundTransactionResult Apply(ServiceIdentityBoundTransactionContext context);
}

/// <summary>
/// CYCLE 89. The ONE narrowly typed seam by which a bound transaction may
/// receive a POST-AUTHENTICATION PAYLOAD.
///
/// WHY IT IS A SEPARATE INTERFACE. Widening
/// <see cref="IServiceIdentityBoundTransaction"/> would hand the ordinary
/// service-enable and service-disable transactions a payload-shaped member they
/// have no reason to carry, and the channel would then have no structural way to
/// tell a payload transaction from a challenge-only one. Because the payload
/// phase is gated on THIS interface, enable and disable keep the cycle-59 wire
/// byte for byte: no payload-ready response is offered to them, no frame is read
/// from them, and no member below is reachable from them.
///
/// IT IS NOT A DELEGATE AND CARRIES NO CAPABILITY. It has exactly ONE member,
/// which takes the DECODED payload text and the installation id the channel
/// ALREADY validated from the anchor, and returns a bounded enum. It takes no
/// path, no SID, no store, no descriptor, no handle and no callback, and it can
/// return no path, byte, identity or exception.
///
/// EXACTLY ONE production implementation is permitted - the fixed ownership
/// promotion / depromotion transaction.
///
/// THE ORDERING THE CHANNEL ENFORCES, and which no implementation may reorder:
/// kernel identity bound, admission facts compared, anchor read and REQUIRED to
/// already exist for that same kernel SID, THEN the payload-ready response, THEN
/// exactly one framed payload validated for length and challenge-bound digest,
/// THEN <see cref="AcceptPayload"/>, THEN <c>Preflight</c>, THEN <c>Apply</c>,
/// THEN - and only then - the single acknowledgement.
/// </summary>
internal interface IServiceIdentityBoundPayloadTransaction : IServiceIdentityBoundTransaction
{
    /// <summary>
    /// MUTATION-FREE, and called AT MOST ONCE per endpoint. It parses the payload
    /// through the closed request contract and requires the request's
    /// installation ownership id to equal <paramref name="anchorInstallationId"/>,
    /// which the channel took from the ALREADY VALIDATED anchor and not from the
    /// wire. It must not write a file, create a directory, change an
    /// access-control list, open a certificate store or private key, or create,
    /// change, start, stop or delete a service.
    /// </summary>
    ServiceIdentityPayloadAcceptanceState AcceptPayload(string payloadText, string anchorInstallationId);
}

/// <summary>
/// CYCLE 68. The CLOSED result of recovering from an anchor write that failed
/// AT OR AFTER the single <c>Directory.CreateDirectory</c> inside
/// <c>ServiceInstallationAnchorStore.WriteTo</c>.
///
/// IT HAS EXACTLY TWO VALUES AND NEITHER OF THEM IS SUCCESS. This recovery runs
/// only on a path where the enablement has ALREADY failed; all it can decide is
/// whether the residue that failure may have left is provably gone.
///
/// ZERO IS <see cref="RecoveryRequired"/>, DELIBERATELY. The usual project
/// convention puts an <c>Unspecified</c> at zero, but a two-value closed result
/// has no room for one - and putting <see cref="Compensated"/> at zero would
/// make an uninitialised value read as "the machine was cleaned up", which is
/// the exact fail-OPEN default this whole cycle exists to prevent.
/// </summary>
internal enum ServiceAnchorPersistenceRecoveryState
{
    /// <summary>
    /// Closure could NOT be proven. Everything observed is PRESERVED exactly as
    /// found and an attended, elevated recovery is the only remedy. This is the
    /// zero value, so an uninitialised or defaulted result always fails closed.
    /// </summary>
    RecoveryRequired = 0,

    /// <summary>
    /// Every fixed machine-data directory THIS attempt is PROVEN to have created
    /// was removed, and its absence was reread from disk. Still never a success:
    /// the enablement itself failed.
    /// </summary>
    Compensated = 1,
}

/// <summary>
/// CYCLE 68. The ONE narrow seam by which a bound transaction may undo the fixed
/// machine-data directories that a FAILED anchor write may have created.
///
/// IT IS DELIBERATELY NOT PART OF <see cref="IServiceIdentityBoundTransaction"/>,
/// for exactly the reason <c>IServiceEnableFailureClassified</c> is not: that
/// interface is shared with the DISABLE transaction, which binds
/// <see cref="ServiceAnchorCreationPolicy.NeverCreate"/>, never calls the anchor
/// write and therefore has no post-create residue to undo. Widening the shared
/// interface would hand every bound transaction a deletion-shaped member it has
/// no reason to carry, and would force a second implementation into existence.
/// EXACTLY ONE production implementation is permitted - the fixed service-enable
/// transaction.
///
/// IT IS NOT A DELEGATE AND TAKES NO PATH. The only argument is the bounded
/// write state the store itself reported, so no caller can ever name what gets
/// removed. There is no recovery, reset or purge verb anywhere behind it, and
/// nothing behind it deletes recursively.
///
/// A transaction that does NOT implement this interface is not a failure: the
/// channel simply reports recovery-required, which is exactly the pre-cycle-68
/// behaviour.
/// </summary>
internal interface IServiceAnchorPersistenceFailureRecoverable
{
    /// <summary>
    /// Called AT MOST ONCE per transaction, only after the mutation-free
    /// preflight completed, and only for a write state for which
    /// <see cref="ServiceInstallationAnchorStore.MayHaveCreatedDirectories"/> is
    /// true. It must PRESERVE anything it cannot prove this attempt created, and
    /// it must never report <see cref="ServiceAnchorPersistenceRecoveryState.Compensated"/>
    /// unless an immediate reread proved the removal.
    /// </summary>
    ServiceAnchorPersistenceRecoveryState RecoverAfterAnchorPersistenceFailure(
        ServiceInstallationAnchorWriteState state);
}

/// <summary>
/// The server result's whole observable surface: a bounded outcome plus the two
/// ORDERING facts this cycle must be able to assert - whether the anchor was
/// persisted, and whether an acknowledgement was sent. Never a SID, PID,
/// installation id, path or exception, including through <see cref="ToString"/>.
/// </summary>
internal readonly struct ServiceInitiatingUserIdentityBindResult
{
    private ServiceInitiatingUserIdentityBindResult(
        ServiceInitiatingUserIdentityChannelOutcome outcome, bool anchorPersisted, bool acknowledgementSent)
    {
        Outcome = outcome;
        AnchorPersisted = anchorPersisted;
        AcknowledgementSent = acknowledgementSent;
    }

    internal ServiceInitiatingUserIdentityChannelOutcome Outcome { get; }

    internal bool AnchorPersisted { get; }

    internal bool AcknowledgementSent { get; }

    internal static ServiceInitiatingUserIdentityBindResult Refused(
        ServiceInitiatingUserIdentityChannelOutcome outcome, bool anchorPersisted = false) =>
        new(outcome, anchorPersisted, false);

    internal static ServiceInitiatingUserIdentityBindResult Completed() =>
        new(ServiceInitiatingUserIdentityChannelOutcome.Completed, true, true);

    public override string ToString() => Outcome.ToString();
}

/// <summary>
/// The fixed wire vocabulary and bounded grammars of the channel. Pure and
/// compile-time: it opens nothing and derives no identity.
/// </summary>
internal static class ServiceInitiatingUserIdentityChannelContract
{
    /// <summary>Prefix of every endpoint name. The suffix is always fresh random hex.</summary>
    internal const string EndpointNamePrefix = "PAXCookbook.InitiatingUserIdentity.";

    /// <summary>
    /// Sent by the SERVER once a client is connected, carrying the fresh
    /// per-transaction challenge. It exists because the cycle-59 ruling forbids
    /// any secret on a command line: the challenge is created after connection,
    /// travels only on the pipe, and is echoed back by the client.
    /// </summary>
    internal const string ChallengeToken = "PAXCOOKBOOK-ANCHOR-CHALLENGE-V1";

    internal const string RequestToken = "PAXCOOKBOOK-ANCHOR-REQUEST-V1";
    internal const string AcknowledgementToken = "PAXCOOKBOOK-ANCHOR-ACK-V1";
    internal const string RefusalToken = "PAXCOOKBOOK-ANCHOR-REFUSED-V1";

    /// <summary>
    /// CYCLE 89. Sent by the SERVER, and ONLY by an endpoint carrying a payload
    /// transaction, AFTER the kernel identity is bound, the admission facts have
    /// been compared and the anchor has been proven to already exist for that
    /// same kernel SID. The client MUST NOT transmit a payload before it has
    /// received this exact line: the byte that authorises transmission is emitted
    /// by the server after validation, never assumed by the client.
    /// </summary>
    internal const string PayloadReadyToken = "PAXCOOKBOOK-ANCHOR-PAYLOAD-READY-V1";

    /// <summary>
    /// CYCLE 89. The FIXED TRAILER that must follow the declared payload bytes,
    /// and the whole reason a duplicate or trailing frame is refused
    /// DETERMINISTICALLY rather than best-effort.
    ///
    /// WHY A TRAILER AND NOT A PEEK. The first implementation of this cycle used
    /// a non-consuming <c>PeekNamedPipe</c> to prove nothing followed the frame.
    /// MEASURED, that does not work here: this endpoint is created with
    /// <c>nOutBufferSize</c> and <c>nInBufferSize</c> both zero, so a peer's
    /// second frame BLOCKS in its own write call instead of sitting in a buffer
    /// the server could observe, and the peek reported zero available bytes while
    /// a duplicate frame was genuinely pending. A trailer has no such dependency:
    /// the byte after the payload is either this exact token or the frame is
    /// refused, and a second frame's length line can never spell it.
    /// </summary>
    internal const string PayloadEndToken = "PAXCOOKBOOK-ANCHOR-PAYLOAD-END-V1";

    /// <summary>
    /// CYCLE 89. The DOMAIN SEPARATOR of the payload frame digest. It exists so a
    /// digest computed for this frame can never be replayed as, or confused with,
    /// a digest computed over any other structure in the product - including the
    /// request contract's own <c>expectedRecipeSha256</c>, which is a plain
    /// SHA-256 over Recipe bytes with no domain and no channel binding.
    /// </summary>
    internal const string PayloadBindingDomain = "PAXCookbook.ServiceOwnership.PayloadFrame.v1";

    /// <summary>
    /// CYCLE 89. The transport byte ceiling: the closed request contract bounds a
    /// request at <c>ServicePromotionRequestParser.MaxRequestChars</c> (524288)
    /// UTF-16 CHARS, and the widest UTF-8 encoding of one char is 4 bytes, so a
    /// request the parser could legitimately accept occupies at most 2097152
    /// bytes.
    ///
    /// IT IS DELIBERATELY NOT A SMALLER "SENSIBLE" LIMIT. A tighter transport
    /// bound would silently refuse payloads the contract itself accepts, moving
    /// the real limit into the transport where no test of the contract would ever
    /// see it. The contract's own char check is UNCHANGED and still runs; this is
    /// a separate, strictly outer, byte-level guard.
    ///
    /// CYCLE 94 - IT IS NOW THE DIRECT DERIVATION CYCLE 89 ASKED FOR. Cycle 89
    /// wrote a retyped literal here and said exactly why: this file is
    /// COMPILE-LINKED into PAXCookbook.ServiceAdminHelper, the request parser was
    /// not in that closure, and the helper build failed with CS0103. It also said
    /// what would change that - "when the parser is deliberately linked into the
    /// helper, this literal should become the direct reference and the pin should
    /// stay." Pass C links the parser into the helper as an audited expansion, so
    /// the derivation is now spelled once, here, and the equality pin in
    /// ServiceIdentityPayloadTransportTests stays exactly where it was.
    /// </summary>
    internal const int MaxPayloadBytes = ServicePromotionRequestParser.MaxRequestChars * 4;

    /// <summary>
    /// Hard bound on each of the two frame HEADER lines, enforced before any
    /// parse. Both are short fixed-shape tokens; the bound is generous enough to
    /// be unambiguous and small enough that a hostile header can never drive an
    /// unbounded read.
    /// </summary>
    internal const int MaxPayloadHeaderBytes = 128;

    /// <summary>Bytes of entropy for the endpoint name and, separately, the challenge.</summary>
    internal const int EntropyBytes = 16;

    /// <summary>Hard bound on one request line, enforced before any parse.</summary>
    internal const int MaxRequestBytes = 512;

    /// <summary>Hard bound on one response line.</summary>
    internal const int MaxResponseBytes = 256;

    /// <summary>Fresh uppercase hex from a cryptographic RNG. Never sequential, never derived.</summary>
    internal static char[] NewEntropyHex()
    {
        byte[] entropy = RandomNumberGenerator.GetBytes(EntropyBytes);
        try
        {
            var hex = new char[entropy.Length * 2];
            for (int i = 0; i < entropy.Length; i++)
            {
                string pair = entropy[i].ToString("X2", CultureInfo.InvariantCulture);
                hex[i * 2] = pair[0];
                hex[(i * 2) + 1] = pair[1];
            }
            return hex;
        }
        finally
        {
            Array.Clear(entropy);
        }
    }

    /// <summary>
    /// Length-independent, early-exit-free comparison of the received challenge
    /// against the endpoint's own. A length difference is refused up front, and
    /// the loop always runs to completion so the number of matching leading
    /// characters is never observable through timing.
    /// </summary>
    internal static bool ChallengeMatches(char[]? expected, string? received)
    {
        if (expected is null || received is null || expected.Length == 0
            || expected.Length != received.Length)
        {
            return false;
        }

        int difference = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            difference |= expected[i] ^ received[i];
        }
        return difference == 0;
    }

    /// <summary>Exactly <c>yyyy-MM-ddTHH:mm:ssZ</c>, matching the anchor contract's grammar.</summary>
    internal static string NowUtcTimestamp() =>
        DateTimeOffset.UtcNow.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// CYCLE 89. The CLOSED decimal byte-length grammar of a payload frame:
    /// ASCII digits only, at least one, no sign, no whitespace, no radix prefix,
    /// no leading zero, and no value outside 1 .. <see cref="MaxPayloadBytes"/>.
    ///
    /// OVERFLOW IS REFUSED STRUCTURALLY, NOT ARITHMETICALLY. The digit count is
    /// bounded before a single digit is accumulated, so the accumulator can never
    /// wrap: a length line long enough to overflow a 32-bit integer is rejected
    /// as mis-shaped before any multiplication happens. ZERO is refused outright,
    /// so an "empty payload" can never be mistaken for an accepted one.
    /// </summary>
    internal static bool TryParsePayloadFrameLength(string? line, out int length)
    {
        length = 0;

        // MaxPayloadBytes is a 7-digit value today; the bound below is derived
        // from it rather than retyped, and it stops accumulation long before an
        // int could wrap.
        int maxDigits = MaxPayloadBytes.ToString(CultureInfo.InvariantCulture).Length;

        if (line is null || line.Length == 0 || line.Length > maxDigits)
        {
            return false;
        }
        if (line[0] == '0')
        {
            // A single "0" is a zero-length payload and a leading "0" is a
            // non-canonical spelling. Both are refused.
            return false;
        }

        int accumulated = 0;
        foreach (char c in line)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
            accumulated = (accumulated * 10) + (c - '0');
        }

        if (accumulated <= 0 || accumulated > MaxPayloadBytes)
        {
            return false;
        }

        length = accumulated;
        return true;
    }

    /// <summary>
    /// CYCLE 89. The DOMAIN-SEPARATED, CHALLENGE-BOUND digest of one payload
    /// frame, as uppercase SHA-256 hex.
    ///
    /// THE PREIMAGE IS UNAMBIGUOUS BY CONSTRUCTION. It is
    /// <c>domain 0x00 challenge 0x00 decimal-length 0x00 payload-bytes</c>. The
    /// NUL separators cannot occur inside the domain (a fixed ASCII literal), the
    /// challenge (fixed-length uppercase hex) or the decimal length (ASCII
    /// digits), so no two distinct (challenge, payload) pairs can produce the same
    /// preimage by sliding a boundary. Including the length as well as the bytes
    /// is redundant on purpose: it makes a truncated frame fail the digest even if
    /// an attacker could otherwise arrange a prefix collision in the transport.
    ///
    /// BINDING TO THE CHALLENGE IS THE POINT. The challenge is fresh, is generated
    /// only AFTER the client connects, and never crosses a command line, so a
    /// digest captured from one transaction is worthless in any other - the digest
    /// changes when EITHER the challenge OR the payload changes.
    /// </summary>
    internal static string ComputePayloadBinding(char[]? challenge, byte[]? payload)
    {
        if (challenge is null || challenge.Length == 0 || payload is null)
        {
            return string.Empty;
        }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        byte[] domainBytes = utf8.GetBytes(PayloadBindingDomain);
        byte[] challengeBytes = utf8.GetBytes(challenge);
        byte[] lengthBytes = utf8.GetBytes(payload.Length.ToString(CultureInfo.InvariantCulture));

        byte[] preimage = new byte[
            domainBytes.Length + 1 + challengeBytes.Length + 1 + lengthBytes.Length + 1 + payload.Length];

        try
        {
            int offset = 0;
            Buffer.BlockCopy(domainBytes, 0, preimage, offset, domainBytes.Length);
            offset += domainBytes.Length;
            preimage[offset++] = 0;
            Buffer.BlockCopy(challengeBytes, 0, preimage, offset, challengeBytes.Length);
            offset += challengeBytes.Length;
            preimage[offset++] = 0;
            Buffer.BlockCopy(lengthBytes, 0, preimage, offset, lengthBytes.Length);
            offset += lengthBytes.Length;
            preimage[offset++] = 0;
            Buffer.BlockCopy(payload, 0, preimage, offset, payload.Length);

            byte[] digest = SHA256.HashData(preimage);
            try
            {
                var hex = new StringBuilder(digest.Length * 2);
                foreach (byte b in digest)
                {
                    hex.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
                return hex.ToString();
            }
            finally
            {
                Array.Clear(digest);
            }
        }
        finally
        {
            // The preimage holds a copy of the challenge and of the whole payload.
            Array.Clear(preimage);
            Array.Clear(challengeBytes);
            Array.Clear(domainBytes);
            Array.Clear(lengthBytes);
        }
    }

    /// <summary>
    /// The exact uppercase 64-hex digest grammar of a frame's digest line. A
    /// lowercase or short-hex spelling is refused rather than normalised:
    /// capability is never granted by a lenient comparison.
    /// </summary>
    internal static bool IsCanonicalPayloadDigest(string? candidate)
    {
        if (candidate is null || candidate.Length != 64)
        {
            return false;
        }
        foreach (char c in candidate)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A fresh endpoint name, created by the NON-ELEVATED initiator. The name is
    /// not a secret - it is unpredictable so an attacker cannot pre-squat it,
    /// and FILE_FLAG_FIRST_PIPE_INSTANCE is what actually defeats squatting.
    /// </summary>
    internal static string NewEndpointName()
    {
        char[] entropy = NewEntropyHex();
        try
        {
            return EndpointNamePrefix + new string(entropy);
        }
        finally
        {
            Array.Clear(entropy);
        }
    }

    /// <summary>
    /// The CLOSED endpoint-name grammar: the exact fixed prefix followed by
    /// exactly <see cref="EntropyBytes"/> * 2 uppercase hex characters, and
    /// nothing else. Anything longer, shorter, lowercase, non-hex or
    /// differently prefixed is refused before it is ever used as a pipe name.
    /// </summary>
    internal static bool IsCanonicalEndpointName(string? candidate)
    {
        if (candidate is null
            || candidate.Length != EndpointNamePrefix.Length + (EntropyBytes * 2)
            || !candidate.StartsWith(EndpointNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = EndpointNamePrefix.Length; i < candidate.Length; i++)
        {
            char c = candidate[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// CYCLE 67R. THE ONE MAP from a FAILING anchor write to the value the
    /// server speaks on the wire. It is extracted from the server switch so the
    /// split between "decided before any directory could be created" and "may
    /// have left durable directories behind" exists in exactly one place and can
    /// be proven against <see cref="ServiceInstallationAnchorStore.MayHaveCreatedDirectories"/>.
    ///
    /// It is consulted ONLY for a write that did not succeed. The two success
    /// states are handled by the caller and, if they ever reached here, would
    /// fail closed rather than be reported as a benign refusal.
    /// </summary>
    internal static ServiceInitiatingUserIdentityChannelOutcome AnchorWriteFailureOutcomeFor(
        ServiceInstallationAnchorWriteState written) => written switch
    {
        ServiceInstallationAnchorWriteState.ConflictingIdentity =>
            ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict,

        // The store owns the mutation boundary; this map only reads it. Any
        // state that could have followed Directory.CreateDirectory - including
        // an undefined cast - is spoken as recovery-required.
        _ => ServiceInstallationAnchorStore.MayHaveCreatedDirectories(written)
            ? ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired
            : ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused,
    };

    /// <summary>
    /// Parses a bounded refusal line back to its outcome. Only members that are
    /// actually DEFINED on the enum are accepted, so a hostile or corrupted
    /// line can never widen the vocabulary. Returns Unspecified on anything
    /// unrecognised.
    /// </summary>
    internal static ServiceInitiatingUserIdentityChannelOutcome ParseRefusal(string? line)
    {
        if (line is null)
        {
            return ServiceInitiatingUserIdentityChannelOutcome.Unspecified;
        }

        string[] fields = line.Split(' ');
        if (fields.Length != 2 || !string.Equals(fields[0], RefusalToken, StringComparison.Ordinal))
        {
            return ServiceInitiatingUserIdentityChannelOutcome.Unspecified;
        }

        return Enum.TryParse(fields[1], ignoreCase: false, out ServiceInitiatingUserIdentityChannelOutcome parsed)
            && Enum.IsDefined(parsed)
            ? parsed
            : ServiceInitiatingUserIdentityChannelOutcome.Unspecified;
    }
}

/// <summary>
/// The SERVER end, owned by the elevated Setup helper. The endpoint is created
/// in <see cref="TryCreateForBoundInitiator"/> with a DACL that admits ONLY the
/// PID + creation-FILETIME bound initiator, and binds exactly ONE request.
/// </summary>
internal sealed class ServiceInitiatingUserIdentityChannelServer : IDisposable
{
    // WHY THE PIPE IS CREATED NATIVELY. The managed NamedPipeServerStream (and
    // NamedPipeServerStreamAcl) computes its pipe mode as
    // `(int)transmissionMode << 2 | (int)transmissionMode << 1`, which for a
    // byte-mode pipe is zero: it NEVER sets PIPE_REJECT_REMOTE_CLIENTS, and
    // PipeOptions has no member for it. The one honest way to satisfy the
    // remote-client rejection requirement is to call CreateNamedPipeW with the
    // exact flags and then adopt the resulting handle.
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint MaxInstancesOne = 1;
    private const uint DefaultTimeoutMilliseconds = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        internal int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipeW(
        string lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        uint nMaxInstances,
        uint nOutBufferSize,
        uint nInBufferSize,
        uint nDefaultTimeOut,
        ref SecurityAttributes lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle hNamedPipe, out uint clientProcessId);


    private readonly string _endpointName;
    private readonly string _serverIdentitySid;
    private readonly string _daclIdentitySid;
    private readonly ServiceInitiatorProcessFacts _initiator;
    private readonly IServiceInitiatorIdentityResolver _resolver;

    // CYCLE 62. Null for every cycle-59 caller, which is what keeps AwaitAndBind
    // behaviourally identical: with no bound transaction the channel takes the
    // exact cycle-59 path, and no preflight, apply or compensation step exists.
    private readonly IServiceIdentityBoundTransaction? _transaction;

    // CYCLE 63R. Fixed at creation time and never settable afterwards, so no
    // connected client can change whether an ownership record may be created.
    private readonly ServiceAnchorCreationPolicy _anchorPolicy;

    private NamedPipeServerStream? _endpoint;
    private char[]? _challenge;
    private bool _bound;

    // CYCLE 89. Set BEFORE the one framed payload is read, so the payload phase
    // is structurally single-shot: there is no code path that reads a second
    // frame, and none that runs the acceptance step twice.
    private bool _payloadBound;

    private ServiceInitiatingUserIdentityChannelServer(
        string endpointName,
        string serverIdentitySid,
        string daclIdentitySid,
        ServiceInitiatorProcessFacts initiator,
        IServiceInitiatorIdentityResolver resolver,
        IServiceIdentityBoundTransaction? transaction,
        ServiceAnchorCreationPolicy anchorPolicy,
        NamedPipeServerStream endpoint)
    {
        _endpointName = endpointName;
        _serverIdentitySid = serverIdentitySid;
        _daclIdentitySid = daclIdentitySid;
        _initiator = initiator;
        _resolver = resolver;
        _transaction = transaction;
        _anchorPolicy = anchorPolicy;
        _endpoint = endpoint;
    }

    /// <summary>The closed anchor-creation policy bound to this endpoint.</summary>
    internal ServiceAnchorCreationPolicy AnchorPolicy => _anchorPolicy;

    /// <summary>
    /// True only when a bound transaction will run inside this endpoint's one
    /// request. Exposed so the focused tests can prove a cycle-59 endpoint
    /// carries none.
    /// </summary>
    internal bool HasBoundTransaction => _transaction is not null;

    /// <summary>
    /// CYCLE 89. True only when the bound transaction implements the narrow
    /// payload seam. It is the SINGLE structural gate on the whole payload phase,
    /// exposed so the focused tests can prove that a service-enable or
    /// service-disable endpoint carries none and therefore keeps the cycle-59
    /// wire byte for byte.
    /// </summary>
    internal bool HasBoundPayloadTransaction => _transaction is IServiceIdentityBoundPayloadTransaction;

    /// <summary>The fresh, unpredictable endpoint name. Carries no identity.</summary>
    internal string EndpointName => _endpointName;

    /// <summary>
    /// The SID the single DACL ACE admits: the PID + creation-FILETIME bound
    /// INITIATOR, never the elevated approver. Exposed so the focused tests can
    /// prove which identity was used; it is never logged or persisted.
    /// </summary>
    internal string DaclIdentitySid => _daclIdentitySid;

    /// <summary>
    /// The EXPLICIT CLOSED DACL: protection on, inheritance off, exactly one
    /// allow ACE, for the bound initiator. No world SID, no Authenticated
    /// Users, no fallback and no ACE for the elevated account. Pure, so the
    /// focused tests can assert its whole shape without creating a pipe.
    /// </summary>
    internal static PipeSecurity BuildInitiatorOnlySecurity(SecurityIdentifier initiator)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(initiator, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// Fail-closed factory owned by the ELEVATED helper. It takes the endpoint
    /// NAME the non-elevated initiator generated, resolves the initiator's SID
    /// from the PID + creation-FILETIME bound process, and creates the FIRST
    /// pipe instance with a protected single-ACE DACL for THAT initiator and
    /// remote clients rejected.
    ///
    /// CYCLE-59 FIX (a). The DACL identity is deliberately NOT
    /// WindowsIdentity.GetCurrent(). Inside the elevated helper that is the
    /// APPROVING account, and when a different administrator supplies
    /// credentials at the UAC prompt an ACE for the approver would lock the
    /// real, standard initiating user out of its own transaction.
    ///
    /// Any failure returns null and leaves no endpoint behind. A squatted name
    /// fails here, at FILE_FLAG_FIRST_PIPE_INSTANCE.
    /// </summary>
    internal static ServiceInitiatingUserIdentityChannelServer? TryCreateForBoundInitiator(
        string endpointName,
        ServiceInitiatorProcessFacts initiator,
        IServiceInitiatorIdentityResolver resolver) =>
        TryCreateForBoundInitiator(endpointName, initiator, resolver, transaction: null);

    /// <summary>
    /// CYCLE 62. The same fail-closed factory, additionally binding ONE narrowly
    /// typed transaction to this endpoint's single request. The transaction is
    /// fixed at creation time and can never be replaced afterwards, so nothing
    /// can substitute a different one once a client has connected.
    /// </summary>
    internal static ServiceInitiatingUserIdentityChannelServer? TryCreateForBoundInitiator(
        string endpointName,
        ServiceInitiatorProcessFacts initiator,
        IServiceInitiatorIdentityResolver resolver,
        IServiceIdentityBoundTransaction? transaction) =>
        TryCreateForBoundInitiator(
            endpointName, initiator, resolver, transaction, ServiceAnchorCreationPolicy.CreateOrReuse);

    /// <summary>
    /// CYCLE 63R. The same fail-closed factory, additionally binding the CLOSED
    /// anchor-creation policy. Enable binds CreateOrReuse; disable binds
    /// NeverCreate, so a disable attempt can never mint an ownership record.
    /// </summary>
    internal static ServiceInitiatingUserIdentityChannelServer? TryCreateForBoundInitiator(
        string endpointName,
        ServiceInitiatorProcessFacts initiator,
        IServiceInitiatorIdentityResolver resolver,
        IServiceIdentityBoundTransaction? transaction,
        ServiceAnchorCreationPolicy anchorPolicy)
    {
        if (anchorPolicy == ServiceAnchorCreationPolicy.Unspecified)
        {
            return null;
        }

        if (!OperatingSystem.IsWindows()
            || resolver is null
            || !initiator.IsPresent
            || !ServiceInitiatingUserIdentityChannelContract.IsCanonicalEndpointName(endpointName))
        {
            return null;
        }

        // The server's OWN SID is kept for exactly ONE purpose: proving that
        // impersonation reverted. It is never the DACL identity, never compared
        // against the client, and never persisted.
        string? serverSid;
        try
        {
            using WindowsIdentity self = WindowsIdentity.GetCurrent();
            serverSid = self.User?.Value;
        }
        catch (Exception)
        {
            serverSid = null;
        }
        if (serverSid is null)
        {
            return null;
        }

        // FIRST of the TWO required reads of the bound process: before the pipe
        // exists. The second happens after the client connects.
        string? initiatorSid = resolver.TryResolveBoundInitiatorSid(initiator);
        if (initiatorSid is null || !ServiceInstallationAnchorContract.IsInitiatingUserSid(initiatorSid))
        {
            return null;
        }

        SecurityIdentifier initiatorIdentity;
        try
        {
            initiatorIdentity = new SecurityIdentifier(initiatorSid);
        }
        catch (Exception)
        {
            return null;
        }

        GCHandle pinnedDescriptor = default;
        try
        {
            byte[] descriptor = BuildInitiatorOnlySecurity(initiatorIdentity).GetSecurityDescriptorBinaryForm();
            pinnedDescriptor = GCHandle.Alloc(descriptor, GCHandleType.Pinned);

            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinnedDescriptor.AddrOfPinnedObject(),
                InheritHandle = 0,
            };

            SafePipeHandle handle = CreateNamedPipeW(
                @"\\.\pipe\" + endpointName,
                PipeAccessDuplex | FileFlagFirstPipeInstance | FileFlagOverlapped,
                PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients,
                MaxInstancesOne,
                nOutBufferSize: 0,
                nInBufferSize: 0,
                DefaultTimeoutMilliseconds,
                ref attributes);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }

            var endpoint = new NamedPipeServerStream(
                PipeDirection.InOut, isAsync: true, isConnected: false, handle);

            return new ServiceInitiatingUserIdentityChannelServer(
                endpointName, serverSid, initiatorSid, initiator, resolver, transaction, anchorPolicy, endpoint);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (pinnedDescriptor.IsAllocated)
            {
                pinnedDescriptor.Free();
            }
        }
    }

    /// <summary>Binds one request and persists to the ONE fixed machine location.</summary>
    internal ServiceInitiatingUserIdentityBindResult AwaitAndBind(TimeSpan timeout)
    {
        string? serviceDirectory = ServiceInstallationAnchorStore.TryResolveFixedServiceDirectory();
        return serviceDirectory is null
            ? ServiceInitiatingUserIdentityBindResult.Refused(
                ServiceInitiatingUserIdentityChannelOutcome.UnsupportedPlatform)
            : AwaitAndBindIn(serviceDirectory, timeout);
    }

    /// <summary>
    /// The disclosed internal test seam: identical logic against a supplied
    /// directory. See the ServiceInstallationAnchorStore file header - the seam
    /// is internal, takes a DIRECTORY only, and the fixed entry point above
    /// takes no argument at all.
    /// </summary>
    internal ServiceInitiatingUserIdentityBindResult AwaitAndBindIn(string serviceDirectory, TimeSpan timeout)
    {
        // ONE request per endpoint. A bound endpoint is never reusable.
        if (_bound || _endpoint is null)
        {
            return ServiceInitiatingUserIdentityBindResult.Refused(
                ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable);
        }
        _bound = true;

        NamedPipeServerStream endpoint = _endpoint;
        byte[]? requestBuffer = null;
        try
        {
            using var deadline = new CancellationTokenSource(timeout);

            try
            {
                endpoint.WaitForConnectionAsync(deadline.Token).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                return ServiceInitiatingUserIdentityBindResult.Refused(
                    ServiceInitiatingUserIdentityChannelOutcome.NoClientConnected);
            }

            // SECOND of the TWO required reads of the bound process, now that a
            // client is actually connected. A process that died, was recycled or
            // whose identity changed between pipe creation and connection is a
            // terminal refusal, not a retryable condition.
            string? reboundSid = _resolver.TryResolveBoundInitiatorSid(_initiator);
            if (reboundSid is null || !string.Equals(reboundSid, _daclIdentitySid, StringComparison.Ordinal))
            {
                return RefuseOnWire(
                    endpoint, ServiceInitiatingUserIdentityChannelOutcome.InitiatorNotBound, deadline.Token);
            }

            // ---- INVERTED HANDSHAKE: challenge is created AFTER connection ---
            //
            // Nothing secret has crossed a command line. The challenge exists
            // only from this point, only on this live connection, and only for
            // this one transaction.

            _challenge = ServiceInitiatingUserIdentityChannelContract.NewEntropyHex();
            if (!TryWriteLine(
                    endpoint,
                    ServiceInitiatingUserIdentityChannelContract.ChallengeToken + " " + new string(_challenge),
                    deadline.Token))
            {
                return ServiceInitiatingUserIdentityBindResult.Refused(
                    ServiceInitiatingUserIdentityChannelOutcome.ChallengeNotDelivered);
            }

            requestBuffer = new byte[ServiceInitiatingUserIdentityChannelContract.MaxRequestBytes];
            string? request = TryReadBoundedLine(endpoint, requestBuffer, deadline.Token);
            if (request is null)
            {
                return RefuseOnWire(
                    endpoint, ServiceInitiatingUserIdentityChannelOutcome.MalformedRequest, deadline.Token);
            }

            // Shape: <token> <echoed challenge>. Exactly two fields.
            string[] fields = request.Split(' ');
            if (fields.Length != 2
                || !string.Equals(fields[0], ServiceInitiatingUserIdentityChannelContract.RequestToken, StringComparison.Ordinal))
            {
                return RefuseOnWire(
                    endpoint, ServiceInitiatingUserIdentityChannelOutcome.MalformedRequest, deadline.Token);
            }

            if (!ServiceInitiatingUserIdentityChannelContract.ChallengeMatches(_challenge, fields[1]))
            {
                return RefuseOnWire(
                    endpoint, ServiceInitiatingUserIdentityChannelOutcome.ChallengeMismatch, deadline.Token);
            }

            // ---- kernel-derived identity, from the LIVE connection ----------

            uint kernelProcessId;
            if (!GetNamedPipeClientProcessId(endpoint.SafePipeHandle, out kernelProcessId) || kernelProcessId == 0)
            {
                return RefuseOnWire(
                    endpoint, ServiceInitiatingUserIdentityChannelOutcome.ClientIdentityUnavailable, deadline.Token);
            }

            string? kernelSid = TryReadClientSidThroughIdentification(endpoint);
            if (kernelSid is null || !ServiceInstallationAnchorContract.IsInitiatingUserSid(kernelSid))
            {
                return RefuseOnWire(
                    endpoint, ServiceInitiatingUserIdentityChannelOutcome.ClientIdentityUnavailable, deadline.Token);
            }

            // ---- admission facts are COMPARED, never used -------------------
            //
            // The command line carried a pid and a creation FILETIME; the DACL
            // identity was derived from that bound process. Both must agree with
            // what the kernel says about THIS connection.

            if (kernelProcessId != _initiator.ProcessId
                || !string.Equals(kernelSid, _daclIdentitySid, StringComparison.Ordinal))
            {
                return RefuseOnWire(
                    endpoint, ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch, deadline.Token);
            }

            // ---- read the anchor ONLY NOW, then persist, then acknowledge ----
            //
            // CYCLE-59 FIX (b). The anchor is read AFTER the kernel identity is
            // established, never before. A NEW installation id is minted ONLY
            // when the anchor is truly ABSENT; when a valid anchor already
            // belongs to this SAME kernel-derived SID its EXISTING id is reused,
            // so a retry after a lost acknowledgement is idempotent instead of a
            // permanent ConflictingIdentity refusal. A valid anchor for a
            // DIFFERENT SID is a hard conflict whose bytes are never touched,
            // and a malformed or unreadable anchor is never repaired.

            ServiceInstallationAnchorReadResult existing =
                ServiceInstallationAnchorStore.ReadFrom(serviceDirectory);

            string installationId;
            bool anchorWasAbsent = false;
            switch (existing.State)
            {
                case ServiceInstallationAnchorReadState.Absent:
                    installationId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
                    anchorWasAbsent = true;
                    break;
                case ServiceInstallationAnchorReadState.Validated:
                    ServiceInstallationAnchorDocument? current = existing.Validation?.Document;
                    if (current is null)
                    {
                        return RefuseOnWire(
                            endpoint, ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused, deadline.Token);
                    }
                    if (!string.Equals(current.InitiatingUserSid, kernelSid, StringComparison.Ordinal))
                    {
                        return RefuseOnWire(
                            endpoint, ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict, deadline.Token);
                    }
                    installationId = current.InstallationId;
                    break;

                default:
                    // Refused and Unavailable both stop here. Administrative
                    // recovery is a FUTURE attended, elevated operation; this
                    // cycle preserves the existing state exactly as found.
                    return RefuseOnWire(
                        endpoint, ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused, deadline.Token);
            }

            var proposed = new ServiceInstallationAnchorDocument(
                installationId,
                kernelSid,
                ServiceInitiatingUserIdentityChannelContract.NowUtcTimestamp());

            // ---- CYCLE 89 STEPS 5 TO 11: THE BOUNDED PAYLOAD PHASE -----------
            //
            // Reached ONLY when the bound transaction implements the narrow
            // payload seam. A cycle-59/62/63R enable or disable endpoint does not,
            // so it never offers a payload-ready response, never reads a frame,
            // and its wire bytes are exactly what they were before this cycle.
            //
            // ORDER IS THE WHOLE POINT, AND IT IS ALREADY HALF ENFORCED BY
            // POSITION. Everything above this line has already happened: the
            // single-use pipe, the challenge echo, the kernel-derived PID and SID,
            // the admission-fact comparison, and the anchor read and
            // classification. Nothing below is reachable without all of it.
            if (_transaction is IServiceIdentityBoundPayloadTransaction payloadTransaction)
            {
                // (5) THE ANCHOR MUST ALREADY EXIST AND ALREADY BE OURS.
                //
                // Reaching here with anchorWasAbsent == false means the switch
                // above took the Validated arm AND the anchor's initiating-user
                // SID equalled the kernel-derived SID - a different SID already
                // returned AnchorConflict, and a refused or unreadable anchor
                // already returned AnchorPersistenceRefused. So an ABSENT anchor
                // is the only remaining case, and ownership promotion refuses it
                // rather than minting one. This surface has no anchor create and
                // no anchor repair authority, and must never acquire one.
                if (anchorWasAbsent)
                {
                    return RefuseOnWire(
                        endpoint,
                        ServiceInitiatingUserIdentityChannelOutcome.AnchorRequiredButUnavailable,
                        deadline.Token);
                }

                // (6) THROUGH (11). The payload-ready response, the single framed
                // payload, the byte ceiling, the strict decode, the contract parse
                // and the installation-ownership comparison - in that order, and
                // all of them BEFORE the mutation-free preflight below.
                ServiceInitiatingUserIdentityChannelOutcome payloadOutcome = RunPayloadPhase(
                    endpoint, payloadTransaction, installationId, deadline.Token);

                if (payloadOutcome != ServiceInitiatingUserIdentityChannelOutcome.Completed)
                {
                    return RefuseOnWire(endpoint, payloadOutcome, deadline.Token);
                }
            }

            // ---- CYCLE 62 STEP 3: MUTATION-FREE PREFLIGHT, BEFORE PERSISTENCE
            //
            // The bound transaction gets its veto BEFORE the anchor is durable
            // and before any Program Files or SCM state can be touched. A
            // cycle-59 caller has no transaction and skips this entirely.
            if (_transaction is not null)
            {
                ServiceIdentityBoundTransactionPreflightState preflight;
                try
                {
                    preflight = _transaction.Preflight();
                }
                catch (Exception)
                {
                    preflight = ServiceIdentityBoundTransactionPreflightState.Refused;
                }

                if (preflight == ServiceIdentityBoundTransactionPreflightState.RecoveryRequired)
                {
                    return RefuseOnWire(
                        endpoint,
                        ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired,
                        deadline.Token);
                }

                if (preflight != ServiceIdentityBoundTransactionPreflightState.Proceed)
                {
                    return RefuseOnWire(
                        endpoint,
                        ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused,
                        deadline.Token);
                }
            }

            ServiceInstallationAnchorWriteState written =
                _anchorPolicy == ServiceAnchorCreationPolicy.NeverCreate
                    ? ServiceInstallationAnchorWriteState.AlreadyMatching
                    : ServiceInstallationAnchorStore.WriteTo(serviceDirectory, proposed);

            switch (written)
            {
                case ServiceInstallationAnchorWriteState.Created:
                case ServiceInstallationAnchorWriteState.AlreadyMatching:
                    break;

                default:
                    // CYCLE 67R. The failing state decides the wire value through
                    // the ONE map, so a failure that may have created the fixed
                    // machine directories can never be spoken as a refusal that
                    // touched nothing.
                    //
                    // CYCLE 68. A POST-CREATE failure additionally gets the ONE
                    // narrow recovery, EXACTLY ONCE, before the wire value is
                    // decided. This is the whole close of the cycle-67 B2
                    // window: Apply is unreachable from here, so without this
                    // call NOTHING could ever undo the directories the write may
                    // have created. Apply is still never called on this path and
                    // no acknowledgement is ever sent.
                    return RefuseOnWire(
                        endpoint,
                        ServiceInstallationAnchorStore.MayHaveCreatedDirectories(written)
                            ? AnchorPersistenceRecoveryOutcomeFor(written)
                            : ServiceInitiatingUserIdentityChannelContract.AnchorWriteFailureOutcomeFor(written),
                        deadline.Token);
            }

            // ---- CYCLE 62 STEP 5: APPLY, ONLY NOW THAT THE ANCHOR IS DURABLE
            //
            // The transaction owns its own compensation. It may remove the
            // anchor ONLY when this attempt is the one that created it, and only
            // after every other piece of state it created is proven gone.
            if (_transaction is not null)
            {
                bool anchorCreatedThisAttempt =
                    anchorWasAbsent && written == ServiceInstallationAnchorWriteState.Created;

                // CYCLE 63R. FOUR distinct facts, never collapsed. Under
                // NeverCreate nothing was written, so an absent anchor stays
                // absent and a valid one is reported as merely present.
                ServiceAnchorPresence presence =
                    _anchorPolicy == ServiceAnchorCreationPolicy.NeverCreate
                        ? (anchorWasAbsent
                            ? ServiceAnchorPresence.Absent
                            : ServiceAnchorPresence.MatchingPresent)
                        : (anchorCreatedThisAttempt
                            ? ServiceAnchorPresence.CreatedThisAttempt
                            : ServiceAnchorPresence.Reused);

                ServiceIdentityBoundTransactionResult applied;
                try
                {
                    applied = _transaction.Apply(
                        new ServiceIdentityBoundTransactionContext(
                            serviceDirectory, proposed, anchorCreatedThisAttempt, presence));
                }
                catch (Exception)
                {
                    applied = ServiceIdentityBoundTransactionResult.RecoveryRequired();
                }

                switch (applied.State)
                {
                    case ServiceIdentityBoundTransactionState.Completed:
                        break;

                    case ServiceIdentityBoundTransactionState.Compensated:
                        return RefuseOnWire(
                            endpoint,
                            ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated,
                            deadline.Token);

                    default:
                        return RefuseOnWire(
                            endpoint,
                            ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired,
                            deadline.Token);
                }
            }

            // THE ONE ACKNOWLEDGEMENT SITE. Reached only after the store has
            // already reported a durable anchor.
            if (!TryWriteLine(endpoint, ServiceInitiatingUserIdentityChannelContract.AcknowledgementToken, deadline.Token))
            {
                return ServiceInitiatingUserIdentityBindResult.Refused(
                    ServiceInitiatingUserIdentityChannelOutcome.AcknowledgementFailed, anchorPersisted: true);
            }

            return ServiceInitiatingUserIdentityBindResult.Completed();
        }
        catch (Exception)
        {
            return ServiceInitiatingUserIdentityBindResult.Refused(
                ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable);
        }
        finally
        {
            if (requestBuffer is not null)
            {
                Array.Clear(requestBuffer);
            }
            // Transaction material is destroyed on BOTH the completion and the
            // refusal path, and the endpoint is gone with it.
            Dispose();
        }
    }

    /// <summary>
    /// Impersonates the connected client at whatever level the CLIENT granted -
    /// which the client sets to SecurityIdentification - reads the SID from the
    /// impersonated token, then CHECKS that impersonation actually reverted by
    /// comparing the restored identity to the server's own.
    /// </summary>
    private string? TryReadClientSidThroughIdentification(NamedPipeServerStream endpoint)
    {
        string? clientSid = null;
        try
        {
            endpoint.RunAsClient(() =>
            {
                using WindowsIdentity impersonated = WindowsIdentity.GetCurrent();
                clientSid = impersonated.User?.Value;
            });
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            using WindowsIdentity restored = WindowsIdentity.GetCurrent();
            if (!string.Equals(restored.User?.Value, _serverIdentitySid, StringComparison.Ordinal))
            {
                // Impersonation did not revert. Fail closed rather than continue
                // running under someone else's identity.
                return null;
            }
        }
        catch (Exception)
        {
            return null;
        }

        return clientSid;
    }

    /// <summary>
    /// CYCLE 89. THE ONE ORDERED PAYLOAD PHASE. It is private, it is called from
    /// exactly one place, and that call site sits AFTER the kernel identity, the
    /// admission-fact comparison and the anchor validation, and BEFORE the
    /// mutation-free preflight. Every refusal below happens before any preflight,
    /// any anchor write and any Apply.
    ///
    /// IT IS SINGLE-SHOT BY CONSTRUCTION. <see cref="_payloadBound"/> is set
    /// before the first byte of the frame is read, so a second entry refuses; and
    /// the frame itself ends in a FIXED TRAILER, so a duplicate or trailing frame
    /// is refused deterministically. A second frame therefore cannot reach the
    /// parse step, cannot reach the acceptance step, and cannot reach the
    /// transaction at all.
    ///
    /// THE CLIENT MAY NOT SPEAK FIRST. The payload-ready line is emitted by the
    /// SERVER, after validation, and it is the only thing that authorises
    /// transmission; a client that transmits earlier is not being read, and the
    /// bytes it sent become trailing data that the peek refuses.
    ///
    /// EVERY TEMPORARY BUFFER IS CLEARED IN A FINALLY BLOCK, including on the
    /// refusal paths. The decoded payload is briefly a managed System.String,
    /// which .NET cannot forcibly zero - the same limitation this file already
    /// discloses for the request line.
    /// </summary>
    private ServiceInitiatingUserIdentityChannelOutcome RunPayloadPhase(
        NamedPipeServerStream endpoint,
        IServiceIdentityBoundPayloadTransaction transaction,
        string anchorInstallationId,
        CancellationToken token)
    {
        if (_payloadBound)
        {
            return ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed;
        }
        _payloadBound = true;

        byte[]? headerBuffer = null;
        byte[]? payloadBuffer = null;
        try
        {
            // (6) ONLY NOW may the client transmit.
            if (!TryWriteLine(
                    endpoint, ServiceInitiatingUserIdentityChannelContract.PayloadReadyToken, token))
            {
                return ServiceInitiatingUserIdentityChannelOutcome.PayloadReadyNotDelivered;
            }

            // (7a) EXACT DECIMAL BYTE LENGTH.
            headerBuffer = new byte[ServiceInitiatingUserIdentityChannelContract.MaxPayloadHeaderBytes];
            string? lengthLine = TryReadBoundedLine(endpoint, headerBuffer, token);
            if (!ServiceInitiatingUserIdentityChannelContract.TryParsePayloadFrameLength(
                    lengthLine, out int declaredLength))
            {
                // (8) A well-formed decimal that is simply TOO BIG is reported as
                // oversized rather than mis-shaped: the two read very differently
                // in an audit trail, and only one of them suggests a hostile peer.
                return IsWellFormedButOversizedLength(lengthLine)
                    ? ServiceInitiatingUserIdentityChannelOutcome.PayloadOversized
                    : ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed;
            }

            // (7b) UPPERCASE SHA-256 DIGEST, DOMAIN-SEPARATED AND CHALLENGE-BOUND.
            Array.Clear(headerBuffer);
            string? digestLine = TryReadBoundedLine(endpoint, headerBuffer, token);
            if (!ServiceInitiatingUserIdentityChannelContract.IsCanonicalPayloadDigest(digestLine))
            {
                return ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed;
            }

            // (7c) EXACTLY THAT MANY BYTES. A short read, an early disconnect or a
            // timeout all land here as a mis-shaped frame.
            payloadBuffer = new byte[declaredLength];
            if (!TryReadExactly(endpoint, payloadBuffer, declaredLength, token))
            {
                return ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed;
            }

            // INTEGRITY BEFORE STRUCTURE. The digest is checked immediately, so a
            // peer that corrupted or substituted the bytes is told exactly that,
            // rather than having its refusal reported as a framing problem.
            string expectedDigest = ServiceInitiatingUserIdentityChannelContract.ComputePayloadBinding(
                _challenge, payloadBuffer);
            if (!DigestMatches(expectedDigest, digestLine))
            {
                return ServiceInitiatingUserIdentityChannelOutcome.PayloadDigestMismatch;
            }

            // (7d) NOTHING MAY FOLLOW THE ONE FRAME. The trailer is the byte-exact
            // proof of that: a duplicate frame puts its LENGTH LINE here, which can
            // never spell the end token, so a second frame is refused before it can
            // be parsed and long before it could reach the transaction.
            Array.Clear(headerBuffer);
            string? endLine = TryReadBoundedLine(endpoint, headerBuffer, token);
            if (!string.Equals(
                    endLine, ServiceInitiatingUserIdentityChannelContract.PayloadEndToken, StringComparison.Ordinal))
            {
                return ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed;
            }

            // (9) STRICT UTF-8, NO BOM, NO REPLACEMENT CHARACTER.
            if (payloadBuffer.Length >= 3
                && payloadBuffer[0] == 0xEF && payloadBuffer[1] == 0xBB && payloadBuffer[2] == 0xBF)
            {
                return ServiceInitiatingUserIdentityChannelOutcome.PayloadNotUtf8;
            }

            string payloadText;
            try
            {
                var strict = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                payloadText = strict.GetString(payloadBuffer);
            }
            catch (Exception)
            {
                return ServiceInitiatingUserIdentityChannelOutcome.PayloadNotUtf8;
            }

            if (payloadText.Length > 0 && payloadText[0] == '\uFEFF')
            {
                return ServiceInitiatingUserIdentityChannelOutcome.PayloadNotUtf8;
            }

            // (10) AND (11). The closed contract parse and the installation-
            // ownership comparison, both inside the narrow seam, both
            // mutation-free, and both strictly before the preflight.
            ServiceIdentityPayloadAcceptanceState accepted;
            try
            {
                accepted = transaction.AcceptPayload(payloadText, anchorInstallationId);
            }
            catch (Exception)
            {
                accepted = ServiceIdentityPayloadAcceptanceState.Unspecified;
            }

            return accepted switch
            {
                ServiceIdentityPayloadAcceptanceState.Accepted =>
                    ServiceInitiatingUserIdentityChannelOutcome.Completed,
                ServiceIdentityPayloadAcceptanceState.InstallationOwnershipMismatch =>
                    ServiceInitiatingUserIdentityChannelOutcome.InstallationOwnershipMismatch,
                _ => ServiceInitiatingUserIdentityChannelOutcome.PayloadRefused,
            };
        }
        catch (Exception)
        {
            return ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed;
        }
        finally
        {
            if (headerBuffer is not null)
            {
                Array.Clear(headerBuffer);
            }
            if (payloadBuffer is not null)
            {
                Array.Clear(payloadBuffer);
            }
        }
    }

    /// <summary>
    /// True only for a line that is pure ASCII decimal with no leading zero and a
    /// bounded digit count, whose value exceeds the derived transport ceiling. It
    /// exists ONLY to choose between two bounded refusal tokens and can never
    /// admit a frame.
    /// </summary>
    private static bool IsWellFormedButOversizedLength(string? line)
    {
        // 18 digits stays inside a signed 64-bit accumulator with room to spare,
        // and anything longer is mis-shaped rather than merely oversized.
        const int MaxComparableDigits = 18;

        if (line is null || line.Length == 0 || line.Length > MaxComparableDigits || line[0] == '0')
        {
            return false;
        }

        long accumulated = 0;
        foreach (char c in line)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
            accumulated = (accumulated * 10) + (c - '0');
        }

        return accumulated > ServiceInitiatingUserIdentityChannelContract.MaxPayloadBytes;
    }

    /// <summary>
    /// Length-independent, early-exit-free digest comparison, for the same reason
    /// the challenge comparison is: the number of matching leading characters must
    /// never be observable through timing.
    /// </summary>
    private static bool DigestMatches(string expected, string? received)
    {
        if (expected.Length == 0 || received is null || expected.Length != received.Length)
        {
            return false;
        }

        int difference = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            difference |= expected[i] ^ received[i];
        }
        return difference == 0;
    }

    /// <summary>
    /// Reads EXACTLY <paramref name="count"/> bytes into the caller's buffer, or
    /// refuses. A short read, an end of stream, a cancelled deadline or a peer
    /// that disconnected mid-frame all return false; a partial buffer is never
    /// treated as a payload.
    /// </summary>
    private static bool TryReadExactly(PipeStream stream, byte[] buffer, int count, CancellationToken token)
    {
        if (count <= 0 || buffer.Length < count)
        {
            return false;
        }

        int filled = 0;
        try
        {
            while (filled < count)
            {
                int read = stream.ReadAsync(buffer, filled, count - filled, token).GetAwaiter().GetResult();
                if (read <= 0)
                {
                    return false;
                }
                filled += read;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return filled == count;
    }

    /// <summary>
    /// CYCLE 68. THE ONE SITE at which a post-anchor-write failure is handed to
    /// the bound transaction's narrow recovery seam.
    ///
    /// IT IS UNREACHABLE EXCEPT AFTER A POST-CREATE FAILURE, by construction:
    /// its single caller sits in the DEFAULT arm of the write switch - so the
    /// two success states can never arrive - behind
    /// <see cref="ServiceInstallationAnchorStore.MayHaveCreatedDirectories"/>,
    /// which is the store's own single statement of the mutation boundary. It
    /// runs after the mutation-free preflight has already returned Proceed,
    /// because the write it is reacting to is only attempted after that.
    ///
    /// IT FAILS CLOSED THREE WAYS. A transaction that does not implement the
    /// seam, a transaction that throws, and any result other than Compensated
    /// all speak the same recovery-required value. Only a proven, verified
    /// cleanup is spoken as a compensated transaction - and even that is never
    /// success: <c>Apply</c> is not reached and no acknowledgement is sent.
    /// </summary>
    private ServiceInitiatingUserIdentityChannelOutcome AnchorPersistenceRecoveryOutcomeFor(
        ServiceInstallationAnchorWriteState written)
    {
        if (_transaction is not IServiceAnchorPersistenceFailureRecoverable recoverable)
        {
            // A cycle-59 endpoint, or any transaction with no recovery seam,
            // keeps EXACTLY its pre-cycle-68 behaviour.
            return ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired;
        }

        ServiceAnchorPersistenceRecoveryState recovered;
        try
        {
            recovered = recoverable.RecoverAfterAnchorPersistenceFailure(written);
        }
        catch (Exception)
        {
            recovered = ServiceAnchorPersistenceRecoveryState.RecoveryRequired;
        }

        return recovered == ServiceAnchorPersistenceRecoveryState.Compensated
            ? ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated
            : ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired;
    }

    /// <summary>
    /// Emits a bounded refusal token. This is NOT an acknowledgement: it uses a
    /// different wire token and never reports success.
    /// </summary>
    private static ServiceInitiatingUserIdentityBindResult RefuseOnWire(
        NamedPipeServerStream endpoint,
        ServiceInitiatingUserIdentityChannelOutcome outcome,
        CancellationToken token)
    {
        TryWriteLine(
            endpoint,
            ServiceInitiatingUserIdentityChannelContract.RefusalToken + " " + outcome.ToString(),
            token);
        return ServiceInitiatingUserIdentityBindResult.Refused(outcome);
    }

    private static bool TryWriteLine(PipeStream stream, string line, CancellationToken token)
    {
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(line + "\n");
        try
        {
            if (bytes.Length > ServiceInitiatingUserIdentityChannelContract.MaxResponseBytes)
            {
                return false;
            }
            stream.WriteAsync(bytes, 0, bytes.Length, token).GetAwaiter().GetResult();
            stream.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    /// <summary>
    /// Reads at most one bounded, newline-terminated line into the CALLER'S
    /// buffer, which the caller clears. Strict UTF-8: undecodable bytes are a
    /// refusal, never a replacement character.
    /// </summary>
    internal static string? TryReadBoundedLine(PipeStream stream, byte[] buffer, CancellationToken token)
    {
        int filled = 0;
        try
        {
            while (filled < buffer.Length)
            {
                int read = stream.ReadAsync(buffer, filled, 1, token).GetAwaiter().GetResult();
                if (read <= 0)
                {
                    return null;
                }
                if (buffer[filled] == (byte)'\n')
                {
                    break;
                }
                filled++;
            }
        }
        catch (Exception)
        {
            return null;
        }

        if (filled == 0 || filled >= buffer.Length)
        {
            return null;
        }

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(buffer, 0, filled);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        NamedPipeServerStream? endpoint = _endpoint;
        _endpoint = null;
        try
        {
            endpoint?.Dispose();
        }
        catch (Exception)
        {
            // Best effort; the handle is released either way.
        }

        char[]? challenge = _challenge;
        _challenge = null;
        if (challenge is not null)
        {
            Array.Clear(challenge);
        }
    }
}

/// <summary>
/// The CLIENT end, running as the NON-ELEVATED initiating user. It connects at
/// SecurityIdentification so the elevated server can learn who it is and can
/// never act as it.
/// </summary>
internal static class ServiceInitiatingUserIdentityChannelClient
{
    /// <summary>
    /// Runs the client half of the INVERTED handshake: connect, receive the
    /// server's post-connection challenge, echo it, then wait for the single
    /// acknowledgement. The client supplies NO identity of its own - the server
    /// takes the pid and SID from the live connection - and it never learns or
    /// carries a secret across a command line.
    ///
    /// A bounded refusal line is parsed back to its bounded outcome so the
    /// caller can distinguish, for example, a recovery-required anchor conflict
    /// from a plain timeout. Nothing but the closed enum vocabulary is ever
    /// accepted from the wire.
    /// </summary>
    internal static ServiceInitiatingUserIdentityChannelOutcome Request(string endpointName, TimeSpan timeout) =>
        RequestCore(endpointName, payload: null, timeout);

    /// <summary>
    /// CYCLE 89. The SAME handshake, plus the bounded post-authentication payload
    /// phase. It is a separate entry point rather than an optional argument on
    /// <see cref="Request"/> so that no existing caller can acquire payload
    /// behaviour by accident.
    ///
    /// THE CLIENT NEVER SPEAKS FIRST. It transmits nothing until the SERVER has
    /// sent the payload-ready line, which the server sends only after it has bound
    /// the kernel identity, compared the admission facts and proven the anchor
    /// already exists for that same SID. If the payload-ready line does not
    /// arrive, the payload is DISCARDED UNSENT and the bounded refusal - or
    /// <see cref="ServiceInitiatingUserIdentityChannelOutcome.PayloadNotOffered"/>
    /// - is returned instead.
    /// </summary>
    internal static ServiceInitiatingUserIdentityChannelOutcome RequestWithPayload(
        string endpointName, byte[] payload, TimeSpan timeout) =>
        RequestCore(endpointName, payload, timeout);

    private static ServiceInitiatingUserIdentityChannelOutcome RequestCore(
        string endpointName, byte[]? payload, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ServiceInitiatingUserIdentityChannelOutcome.UnsupportedPlatform;
        }

        if (!ServiceInitiatingUserIdentityChannelContract.IsCanonicalEndpointName(endpointName))
        {
            return ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable;
        }

        if (payload is not null
            && (payload.Length == 0
                || payload.Length > ServiceInitiatingUserIdentityChannelContract.MaxPayloadBytes))
        {
            // Refused locally, so an unusable payload never reaches the elevated
            // boundary at all.
            return ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed;
        }

        byte[]? challengeBuffer = null;
        byte[]? responseBuffer = null;
        byte[]? readyBuffer = null;
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                endpointName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                // NEVER Impersonation, NEVER Delegation.
                TokenImpersonationLevel.Identification);

            int timeoutMilliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 1d, int.MaxValue);
            try
            {
                client.Connect(timeoutMilliseconds);
            }
            catch (Exception)
            {
                return ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable;
            }

            using var deadline = new CancellationTokenSource(timeout);

            // 1. Receive the challenge the server generated AFTER this
            //    connection was accepted.
            challengeBuffer = new byte[ServiceInitiatingUserIdentityChannelContract.MaxResponseBytes];
            string? offered = ServiceInitiatingUserIdentityChannelServer.TryReadBoundedLine(
                client, challengeBuffer, deadline.Token);

            string[] challengeFields = offered is null ? Array.Empty<string>() : offered.Split(' ');
            if (challengeFields.Length != 2
                || !string.Equals(
                    challengeFields[0],
                    ServiceInitiatingUserIdentityChannelContract.ChallengeToken,
                    StringComparison.Ordinal))
            {
                if (offered is null)
                {
                    return ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable;
                }

                // A refusal can arrive INSTEAD of the challenge - the server
                // re-checks the bound initiator before it generates one - so an
                // early bounded refusal must still reach the caller as itself
                // rather than being flattened into "no acknowledgement".
                ServiceInitiatingUserIdentityChannelOutcome early =
                    ServiceInitiatingUserIdentityChannelContract.ParseRefusal(offered);
                return early == ServiceInitiatingUserIdentityChannelOutcome.Unspecified
                    ? ServiceInitiatingUserIdentityChannelOutcome.AcknowledgementNotReceived
                    : early;
            }

            // 2. Echo it back, unchanged, as the whole request.
            byte[] requestBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetBytes(
                    ServiceInitiatingUserIdentityChannelContract.RequestToken + " " + challengeFields[1] + "\n");
            try
            {
                if (requestBytes.Length > ServiceInitiatingUserIdentityChannelContract.MaxRequestBytes)
                {
                    return ServiceInitiatingUserIdentityChannelOutcome.MalformedRequest;
                }
                client.WriteAsync(requestBytes, 0, requestBytes.Length, deadline.Token)
                    .GetAwaiter().GetResult();
                client.Flush();
            }
            catch (Exception)
            {
                return ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable;
            }
            finally
            {
                Array.Clear(requestBytes);
            }

            // 2b. CYCLE 89. WAIT FOR PERMISSION BEFORE TRANSMITTING ANYTHING.
            //
            // This whole block is skipped when there is no payload, so the legacy
            // enable and disable exchanges are byte for byte what they were.
            if (payload is not null)
            {
                readyBuffer = new byte[ServiceInitiatingUserIdentityChannelContract.MaxResponseBytes];
                string? ready = ServiceInitiatingUserIdentityChannelServer.TryReadBoundedLine(
                    client, readyBuffer, deadline.Token);

                if (!string.Equals(
                        ready,
                        ServiceInitiatingUserIdentityChannelContract.PayloadReadyToken,
                        StringComparison.Ordinal))
                {
                    // THE PAYLOAD IS DISCARDED UNSENT.
                    if (ready is null)
                    {
                        return ServiceInitiatingUserIdentityChannelOutcome.PayloadNotOffered;
                    }

                    ServiceInitiatingUserIdentityChannelOutcome refusedEarly =
                        ServiceInitiatingUserIdentityChannelContract.ParseRefusal(ready);
                    return refusedEarly == ServiceInitiatingUserIdentityChannelOutcome.Unspecified
                        ? ServiceInitiatingUserIdentityChannelOutcome.PayloadNotOffered
                        : refusedEarly;
                }

                if (!TryTransmitPayloadFrame(client, challengeFields[1], payload, deadline.Token))
                {
                    return ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable;
                }
            }

            // 3. Exactly one response: the acknowledgement, or a bounded refusal.
            responseBuffer = new byte[ServiceInitiatingUserIdentityChannelContract.MaxResponseBytes];
            string? response = ServiceInitiatingUserIdentityChannelServer.TryReadBoundedLine(
                client, responseBuffer, deadline.Token);

            if (string.Equals(
                    response,
                    ServiceInitiatingUserIdentityChannelContract.AcknowledgementToken,
                    StringComparison.Ordinal))
            {
                return ServiceInitiatingUserIdentityChannelOutcome.Completed;
            }

            ServiceInitiatingUserIdentityChannelOutcome refusal =
                ServiceInitiatingUserIdentityChannelContract.ParseRefusal(response);
            return refusal == ServiceInitiatingUserIdentityChannelOutcome.Unspecified
                ? ServiceInitiatingUserIdentityChannelOutcome.AcknowledgementNotReceived
                : refusal;
        }
        catch (Exception)
        {
            return ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable;
        }
        finally
        {
            if (challengeBuffer is not null)
            {
                Array.Clear(challengeBuffer);
            }
            if (readyBuffer is not null)
            {
                Array.Clear(readyBuffer);
            }
            if (responseBuffer is not null)
            {
                Array.Clear(responseBuffer);
            }
        }
    }

    /// <summary>
    /// Writes the ONE frame - exact decimal byte length, challenge-bound digest,
    /// exactly those bytes, then the fixed end-of-frame trailer - and nothing
    /// else. The digest is computed from the ECHOED challenge, so a frame built
    /// for one transaction cannot be replayed into another.
    /// </summary>
    private static bool TryTransmitPayloadFrame(
        PipeStream client, string echoedChallenge, byte[] payload, CancellationToken token)
    {
        char[] challenge = echoedChallenge.ToCharArray();
        byte[]? headerBytes = null;
        try
        {
            string digest = ServiceInitiatingUserIdentityChannelContract.ComputePayloadBinding(challenge, payload);
            if (digest.Length == 0)
            {
                return false;
            }

            headerBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
                payload.Length.ToString(CultureInfo.InvariantCulture) + "\n" + digest + "\n");

            byte[] trailerBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
                ServiceInitiatingUserIdentityChannelContract.PayloadEndToken + "\n");

            client.WriteAsync(headerBytes, 0, headerBytes.Length, token).GetAwaiter().GetResult();
            client.WriteAsync(payload, 0, payload.Length, token).GetAwaiter().GetResult();
            client.WriteAsync(trailerBytes, 0, trailerBytes.Length, token).GetAwaiter().GetResult();
            client.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Array.Clear(challenge);
            if (headerBytes is not null)
            {
                Array.Clear(headerBytes);
            }
        }
    }
}
