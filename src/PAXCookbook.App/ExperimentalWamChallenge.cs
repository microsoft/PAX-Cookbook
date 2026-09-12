using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace PAXCookbook.App;

// In-process WAM authorization challenge (Track 1 / T1-S2A).
//
// The experimental Entra WAM provider runs in the SAME process as the broker
// (the WAM-owning process IS the daemon), so raw tokens never cross a process
// boundary. This challenge does NOT move a token; it hardens the internal
// authorization the way the browser-owned WebAuthn ceremony is already
// challenge-bound: an authorization result is only honored if it presents a
// challenge that the broker minted for the exact purpose and provider, and that
// has not expired or already been consumed.
//
// POL-1 (documented, not overclaimed): because this is a same-user in-process
// mechanism, same-SID malware can tamper with the same-user process/transport.
// This challenge is therefore REPLAY-RESISTANT but is NOT Hello-equivalent and
// NOT same-SID-malware-resistant. It relies on no inherited secret and no
// inherited handle trust claim.
internal enum WamChallengeConsume
{
    Ok = 0,
    NotFound = 1,      // absent, already consumed (replay), or duplicate
    Expired = 2,
    WrongProvider = 3,
    WrongPurpose = 4,
    Malformed = 7,
}

internal sealed class WamChallengeStore
{
    // Deliberately short: a challenge authorizes a single authorization the
    // operator is performing right now, not a session window.
    internal const int ChallengeTtlSeconds = 120;

    private readonly object _gate = new();
    private readonly Dictionary<string, PendingWamChallenge> _pending = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _utcNow;

    internal WamChallengeStore(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    // Mint a single-use challenge bound to the provider and purpose. Returns null
    // for a malformed request (a blank provider).
    internal string? Mint(WamAuthPurpose purpose, string providerId = AuthProviderIds.EntraWam)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        string challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var pending = new PendingWamChallenge(
            providerId,
            purpose,
            _utcNow().AddSeconds(ChallengeTtlSeconds));

        lock (_gate)
        {
            _pending[challenge] = pending;
        }

        return challenge;
    }

    // Consume a challenge exactly once for the exact provider and purpose it was
    // minted for. Any mismatch, expiry, or replay fails closed and authorizes
    // nothing. The challenge is removed on any terminal outcome (success, expiry,
    // purpose/provider mismatch) so it cannot be retried.
    internal WamChallengeConsume TryConsume(
        string? challenge,
        string providerId,
        WamAuthPurpose purpose)
    {
        if (string.IsNullOrWhiteSpace(challenge))
        {
            return WamChallengeConsume.Malformed;
        }

        lock (_gate)
        {
            // Look the KNOWN challenge up FIRST. Provider mismatch (and every
            // other mismatch) is terminal for a known challenge: it is removed
            // here, so a wrong-provider attempt consumes it and a later
            // correct-provider replay of the same challenge fails NotFound.
            if (!_pending.TryGetValue(challenge, out PendingWamChallenge pending))
            {
                // Absent, or already consumed (replay/duplicate).
                return WamChallengeConsume.NotFound;
            }

            if (_utcNow() > pending.ExpiresUtc)
            {
                _pending.Remove(challenge);
                return WamChallengeConsume.Expired;
            }

            if (!string.Equals(providerId, pending.ProviderId, StringComparison.Ordinal))
            {
                _pending.Remove(challenge);
                return WamChallengeConsume.WrongProvider;
            }

            if (pending.Purpose != purpose)
            {
                _pending.Remove(challenge);
                return WamChallengeConsume.WrongPurpose;
            }

            // Success: single use — remove so it can never be replayed.
            _pending.Remove(challenge);
            return WamChallengeConsume.Ok;
        }
    }

    // Explicitly destroy a challenge without any success semantics. Used when a
    // pending request is abandoned/expired or reaches any non-consuming terminal
    // outcome: the authority-bearing challenge material is removed so it can
    // never later be consumed. This is deliberately NOT modeled as a successful
    // TryConsume, so abandonment is never misrepresented as authorization.
    internal void Discard(string? challenge)
    {
        if (string.IsNullOrWhiteSpace(challenge))
        {
            return;
        }

        lock (_gate)
        {
            _pending.Remove(challenge);
        }
    }

    private readonly struct PendingWamChallenge
    {
        internal PendingWamChallenge(
            string providerId,
            WamAuthPurpose purpose,
            DateTime expiresUtc)
        {
            ProviderId = providerId;
            Purpose = purpose;
            ExpiresUtc = expiresUtc;
        }

        internal string ProviderId { get; }

        internal WamAuthPurpose Purpose { get; }

        internal DateTime ExpiresUtc { get; }
    }
}
