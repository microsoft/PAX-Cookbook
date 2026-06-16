namespace PAXCookbook.App;

// In-memory, single-use manual-cook re-authorization slot (X16B).
//
// A successful browser-owned WebAuthn step-up (POST
// /api/v1/broker/reauth/manual-cook/verify) grants exactly one authorization
// to start a manual cook for a specific recipe. The cook route consumes it at
// gate 10. The grant lives only in process memory — it is never written to
// disk, SQLite, a log, a token, or the response body, so it cannot be
// satisfied by request data alone.
//
// An authorization is valid only while ALL of the following hold:
//   - it has not been consumed (single use),
//   - it has not expired (short TTL),
//   - the broker lock generation is unchanged since the grant (any lock event
//     — explicit lock, inactivity timeout, or time-anomaly re-lock — bumps the
//     generation and silently invalidates the grant),
//   - the recipe being cooked matches the recipe the step-up authorized.
// Process restart drops the slot entirely (memory only).
internal static class ManualCookReAuth
{
    // Authorization lifetime. Deliberately short: the step-up authorizes a
    // single cook the operator is starting right now, not a session window.
    internal const int AuthorizationTtlSeconds = 120;

    private static readonly object Gate = new();

    private static bool _present;
    private static string _recipeId = string.Empty;
    private static long _lockGeneration;
    private static DateTime _expiresUtc;

    // Record a verified manual-cook authorization. Overwrites any prior slot
    // (only the most recent step-up is honored).
    internal static void Grant(string recipeId, long lockGeneration)
    {
        lock (Gate)
        {
            _present = true;
            _recipeId = recipeId;
            _lockGeneration = lockGeneration;
            _expiresUtc = DateTime.UtcNow.AddSeconds(AuthorizationTtlSeconds);
        }
    }

    // Attempt to consume the authorization for a specific recipe at the current
    // lock generation. Returns (true, "ok") and clears the slot on success;
    // otherwise returns (false, reason) without authorizing the cook. The slot
    // is cleared on expiry and lock-generation mismatch (both terminal); a
    // recipe mismatch leaves a still-valid slot intact for its correct recipe.
    internal static (bool Ok, string Reason) TryConsume(string recipeId, long currentLockGeneration)
    {
        lock (Gate)
        {
            if (!_present)
            {
                return (false, "no_authorization");
            }

            if (DateTime.UtcNow > _expiresUtc)
            {
                Clear();
                return (false, "expired");
            }

            if (_lockGeneration != currentLockGeneration)
            {
                Clear();
                return (false, "lock_changed");
            }

            if (!string.Equals(_recipeId, recipeId, StringComparison.Ordinal))
            {
                return (false, "recipe_mismatch");
            }

            Clear();
            return (true, "ok");
        }
    }

    // Drop any pending authorization (e.g. on an explicit broker lock).
    internal static void Invalidate()
    {
        lock (Gate)
        {
            Clear();
        }
    }

    // Caller holds Gate.
    private static void Clear()
    {
        _present = false;
        _recipeId = string.Empty;
        _lockGeneration = 0;
        _expiresUtc = DateTime.MinValue;
    }
}
