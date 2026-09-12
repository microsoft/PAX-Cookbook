using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace PAXCookbook.App;

// Daemon-side experimental WAM endpoint (Track 1 / T1-S2A repair).
//
// Runs in the daemon process (the broker/lock authority). It mints single-use
// challenges bound to provider/purpose, and consumes a bounded NeutralWamResult
// delivered from the attached-window process over the NATIVE-ONLY IPC channel
// (never an HTTP route the renderer controls). It NEVER sees a token or a raw
// identity claim, only the neutral booleans and the challenge. On an approved,
// challenge-valid result it applies session unlock via BrokerLock.SetUnlocked
// exactly once.
internal enum DaemonWamReason
{
    Approved = 0,
    NotConfigured = 1,
    RequestNotFound = 2,
    ChallengeRejected = 3,
    NeutralResultRejected = 6,
}

internal enum ExperimentalWamRequestState
{
    Unknown = 0,
    Pending = 1,
    Approved = 2,
    Denied = 3,
}

internal readonly record struct DaemonWamOutcome(bool Approved, DaemonWamReason Reason, WamChallengeConsume ChallengeStatus);

internal sealed class ExperimentalWamDaemonEndpoint
{
    // A pending request lives at most this long before it is treated as expired.
    internal const int RequestTtlSeconds = 120;

    private readonly ExperimentalWamOptions _options;
    private readonly WamChallengeStore _challenges;
    private readonly SessionUnlockCoordinator _sessionCoordinator;
    private readonly Func<DateTime> _utcNow;

    private readonly object _gate = new();
    private readonly Dictionary<string, RequestRecord> _pending = new(StringComparer.Ordinal);

    internal ExperimentalWamDaemonEndpoint(
        ExperimentalWamOptions options,
        WamChallengeStore? challenges = null,
        SessionUnlockCoordinator? sessionCoordinator = null,
        Func<DateTime>? utcNow = null)
    {
        _options = options;
        _challenges = challenges ?? new WamChallengeStore();
        // A work-account WAM unlock records "work_account" provenance. Such a
        // session may use the app normally but cannot alter or remove the
        // identity-provider configuration (that requires a Windows Hello session).
        _sessionCoordinator = sessionCoordinator
            ?? new SessionUnlockCoordinator(() => BrokerLock.SetUnlocked("work_account"));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    // Initiate a request. The daemon mints the challenge and returns an OPAQUE
    // requestId (no security fields) or null when not configured.
    internal string? Initiate(WamAuthPurpose purpose)
    {
        if (_options is null || !_options.IsFullyConfigured)
        {
            return null;
        }

        lock (_gate)
        {
            PruneExpired(_utcNow());

            string? challenge = _challenges.Mint(purpose, AuthProviderIds.EntraWam);
            if (challenge is null)
            {
                return null;
            }

            string requestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _pending[requestId] = new PendingRequest(purpose, challenge,
                _utcNow().AddSeconds(RequestTtlSeconds));
            return requestId;
        }
    }

    // Non-consuming native descriptor lookup (Stage 1 of the two-stage pipe). The
    // HWND-owning window sends only the requestId; the daemon returns the minimal
    // authoritative descriptor (purpose). It NEVER marks the request consumed,
    // mints, or consumes a challenge, and it fails closed for an unknown,
    // expired, terminal, or consumed request. The challenge never crosses.
    internal WamNativeDescriptor LookupDescriptor(string? requestId)
    {
        if (_options is null || !_options.IsFullyConfigured)
        {
            return WamNativeDescriptor.NotFound(WamDescriptorReason.NotConfigured);
        }

        if (string.IsNullOrWhiteSpace(requestId))
        {
            return WamNativeDescriptor.NotFound(WamDescriptorReason.Malformed);
        }

        lock (_gate)
        {
            PruneExpired(_utcNow());

            if (!_pending.TryGetValue(requestId, out RequestRecord? record))
            {
                return WamNativeDescriptor.NotFound(WamDescriptorReason.Unknown);
            }

            // A terminal tombstone carries no purpose/challenge; a lookup after
            // any terminal transition (or expiry/prune) fails closed.
            if (record is not PendingRequest)
            {
                return WamNativeDescriptor.NotFound(WamDescriptorReason.Terminal);
            }

            return WamNativeDescriptor.ForSession();
        }
    }

    // Apply a bounded native result. THIS IS THE ONLY METHOD THAT GRANTS
    // AUTHORITY, and it is reachable only from the native IPC channel. Every
    // authority decision is daemon-owned: the challenge is validated against
    // daemon state keyed by requestId. The request is single-use and every
    // terminal outcome consumes it.
    internal DaemonWamOutcome ApplyNativeResult(NeutralWamResult result)
    {
        if (_options is null || !_options.IsFullyConfigured)
        {
            return new DaemonWamOutcome(false, DaemonWamReason.NotConfigured, WamChallengeConsume.Malformed);
        }

        if (result is null || string.IsNullOrWhiteSpace(result.RequestId))
        {
            return new DaemonWamOutcome(false, DaemonWamReason.RequestNotFound, WamChallengeConsume.Malformed);
        }

        lock (_gate)
        {
            PruneExpired(_utcNow());

            // A live request is a PendingRequest; a terminal tombstone (or an
            // unknown id) grants nothing and cannot be replayed. Expiry was
            // already resolved to a tombstone by PruneExpired above.
            if (!_pending.TryGetValue(result.RequestId, out RequestRecord? record) || record is not PendingRequest pending)
            {
                return new DaemonWamOutcome(false, DaemonWamReason.RequestNotFound, WamChallengeConsume.NotFound);
            }

            string requestId = result.RequestId;

            // Consume the DAEMON-OWNED challenge (provider/purpose come from
            // daemon state, not the window's message).
            WamChallengeConsume consume = _challenges.TryConsume(
                pending.Challenge, AuthProviderIds.EntraWam, pending.Purpose);
            if (consume != WamChallengeConsume.Ok)
            {
                // Replay / generation / malformed-challenge rejection: a bounded,
                // fail-closed Denied reason. The challenge is discarded and no
                // authority survives.
                TerminateLocked(requestId, pending.Challenge, ExperimentalWamRequestState.Denied,
                    WamRejectionReasonMap.FromDaemonReason(DaemonWamReason.ChallengeRejected), discardChallenge: true);
                return new DaemonWamOutcome(false, DaemonWamReason.ChallengeRejected, consume);
            }

            if (!result.IsApproved)
            {
                // Derive the bounded customer-safe reason from the native result
                // category (Cancelled / IdentityFailure / ScopeFailure / etc.),
                // fail-closed to Denied for any unmapped category. Only the bounded
                // enum is stored; the category itself never leaves this method.
                TerminateLocked(requestId, pending.Challenge, ExperimentalWamRequestState.Denied,
                    WamRejectionReasonMap.FromCategory(result.Category), discardChallenge: false);
                return new DaemonWamOutcome(false, DaemonWamReason.NeutralResultRejected, consume);
            }

            _sessionCoordinator.Unlock(new DecidedSessionProvider());
            TerminateLocked(requestId, pending.Challenge, ExperimentalWamRequestState.Approved,
                WamRejectionReason.None, discardChallenge: false);
            return new DaemonWamOutcome(true, DaemonWamReason.Approved, consume);
        }
    }

    // Replace a pending request with a minimal terminal tombstone under _gate.
    // The tombstone carries ONLY the bounded status and terminal timestamp — it
    // has no purpose, recipe, captured-generation, or challenge field, so no
    // request authority can survive in the terminal record or be reconstructed.
    // The challenge is discarded from the store when it was not already single-use
    // consumed by TryConsume.
    private void TerminateLocked(string requestId, string challenge, ExperimentalWamRequestState state,
        WamRejectionReason reason, bool discardChallenge)
    {
        if (discardChallenge)
        {
            _challenges.Discard(challenge);
        }

        _pending[requestId] = new TerminalTombstone(state, _utcNow(), reason);
    }

    // Lifecycle maintenance under _gate (decision A): abandoned Pending requests
    // past their TTL are replaced by a terminal Denied tombstone with the challenge
    // discarded (all authority destroyed), and terminal tombstones are pruned once
    // RequestTtlSeconds have elapsed since the terminal transition (after which
    // status returns Unknown and nothing can be reconstructed or replayed). No
    // request-count cap is used; lifecycle and the existing TTL are the only bounds.
    private void PruneExpired(DateTime now)
    {
        List<(string Key, string Challenge)>? expire = null;
        List<string>? remove = null;
        foreach (KeyValuePair<string, RequestRecord> kvp in _pending)
        {
            if (kvp.Value is PendingRequest p)
            {
                if (now > p.ExpiresUtc)
                {
                    (expire ??= new List<(string, string)>()).Add((kvp.Key, p.Challenge));
                }
            }
            else if (kvp.Value.TerminalUtc is DateTime terminal && now > terminal.AddSeconds(RequestTtlSeconds))
            {
                (remove ??= new List<string>()).Add(kvp.Key);
            }
        }

        if (expire is not null)
        {
            foreach ((string key, string challenge) in expire)
            {
                // Abandoned-Pending → terminal transition: the challenge (all
                // authority) is discarded and the bounded reason is Expired,
                // distinguishing a lifecycle timeout from an explicit denial.
                _challenges.Discard(challenge);
                _pending[key] = new TerminalTombstone(ExperimentalWamRequestState.Denied, now, WamRejectionReason.Expired);
            }
        }

        if (remove is not null)
        {
            foreach (string key in remove)
            {
                _pending.Remove(key);
            }
        }
    }

    // Bounded status projection: the coarse request state PLUS the customer-safe
    // rejection reason (never any token/claim/identifier/challenge/generation).
    // An unknown/absent request fails closed: Unknown state + the fail-closed
    // bounded Denied reason (never a permissive one).
    internal ExperimentalWamStatusReport GetStatusReport(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return new ExperimentalWamStatusReport(ExperimentalWamRequestState.Unknown, WamRejectionReason.Denied);
        }

        lock (_gate)
        {
            PruneExpired(_utcNow());
            return _pending.TryGetValue(requestId, out RequestRecord? record)
                ? new ExperimentalWamStatusReport(record.State, record.Reason)
                : new ExperimentalWamStatusReport(ExperimentalWamRequestState.Unknown, WamRejectionReason.Denied);
        }
    }

    // Test/diagnostic view of a request's bounded state (never exposes any
    // security field). Back-compat projection of GetStatusReport's state.
    internal ExperimentalWamRequestState GetStatus(string? requestId) => GetStatusReport(requestId).State;

    // A request record is either a live PendingRequest (which carries the full
    // request authority) or a TerminalTombstone (which carries ONLY the bounded
    // status and terminal timestamp). Replacing the PendingRequest with a
    // TerminalTombstone on any terminal transition makes authority destruction
    // STRUCTURAL: the terminal record has no field in which a purpose or
    // challenge could survive.
    private abstract class RequestRecord
    {
        internal abstract ExperimentalWamRequestState State { get; }

        internal abstract DateTime? TerminalUtc { get; }

        // The bounded, customer-safe rejection reason. A live pending request has
        // no rejection yet (None); a terminal tombstone carries its bounded reason.
        internal abstract WamRejectionReason Reason { get; }
    }

    private sealed class PendingRequest : RequestRecord
    {
        internal PendingRequest(WamAuthPurpose purpose, string challenge, DateTime expiresUtc)
        {
            Purpose = purpose;
            Challenge = challenge;
            ExpiresUtc = expiresUtc;
        }

        internal WamAuthPurpose Purpose { get; }

        internal string Challenge { get; }

        internal DateTime ExpiresUtc { get; }

        internal override ExperimentalWamRequestState State => ExperimentalWamRequestState.Pending;

        internal override DateTime? TerminalUtc => null;

        internal override WamRejectionReason Reason => WamRejectionReason.None;
    }

    // The minimal terminal record. It intentionally has NO purpose or challenge
    // field: once a request is terminal, only its bounded status, the terminal
    // timestamp, and the bounded customer-safe rejection reason survive, and the
    // request identifier remains solely as the dictionary key. The reason is a
    // bounded enum only — no purpose/challenge/generation/recipe/descriptor/salt/
    // account/identifier can enter this record.
    private sealed class TerminalTombstone : RequestRecord
    {
        private readonly ExperimentalWamRequestState _state;
        private readonly DateTime _terminalUtc;
        private readonly WamRejectionReason _reason;

        internal TerminalTombstone(ExperimentalWamRequestState state, DateTime terminalUtc, WamRejectionReason reason)
        {
            _state = state;
            _terminalUtc = terminalUtc;
            _reason = reason;
        }

        internal override ExperimentalWamRequestState State => _state;

        internal override DateTime? TerminalUtc => _terminalUtc;

        internal override WamRejectionReason Reason => _reason;
    }

    // Provider that carries the daemon's already-validated decision through the
    // existing neutral session coordinator so the lock authority stays in one place.
    private sealed class DecidedSessionProvider : ISessionUnlockProvider
    {
        public string ProviderId => AuthProviderIds.EntraWam;

        public SessionUnlockOutcome Authorize() => SessionUnlockOutcome.Grant;
    }
}
