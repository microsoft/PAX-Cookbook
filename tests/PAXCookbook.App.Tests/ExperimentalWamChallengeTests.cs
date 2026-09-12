using System;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S2A — in-process WAM authorization challenge: single-use, purpose /
// provider bound, short expiry. Deterministic clock.
public sealed class ExperimentalWamChallengeTests
{
    private const string Wam = "entra-wam";

    [Fact]
    public void SessionChallenge_ConsumedOnce_ThenReplayFails()
    {
        var store = new WamChallengeStore();
        string? c = store.Mint(WamAuthPurpose.SessionUnlock);
        Assert.NotNull(c);

        Assert.Equal(WamChallengeConsume.Ok,
            store.TryConsume(c, Wam, WamAuthPurpose.SessionUnlock));

        // Replay / duplicate fails closed.
        Assert.Equal(WamChallengeConsume.NotFound,
            store.TryConsume(c, Wam, WamAuthPurpose.SessionUnlock));
    }

    [Fact]
    public void Challenge_WrongProvider_Rejected()
    {
        var store = new WamChallengeStore();
        string? c = store.Mint(WamAuthPurpose.SessionUnlock);
        Assert.Equal(WamChallengeConsume.WrongProvider,
            store.TryConsume(c, "windows-hello", WamAuthPurpose.SessionUnlock));
    }

    [Fact]
    public void Challenge_WrongProvider_ConsumesKnownChallenge_ThenCorrectProviderReplayFails()
    {
        var store = new WamChallengeStore();
        string? c = store.Mint(WamAuthPurpose.SessionUnlock);   // provider defaults to entra-wam
        // A wrong-provider attempt on a KNOWN challenge is terminal: it consumes it.
        Assert.Equal(WamChallengeConsume.WrongProvider,
            store.TryConsume(c, "windows-hello", WamAuthPurpose.SessionUnlock));
        // The same challenge under the correct provider can no longer be retried.
        Assert.Equal(WamChallengeConsume.NotFound,
            store.TryConsume(c, "entra-wam", WamAuthPurpose.SessionUnlock));
    }

    [Fact]
    public void Challenge_Expired_Rejected()
    {
        DateTime now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var store = new WamChallengeStore(() => now);
        string? c = store.Mint(WamAuthPurpose.SessionUnlock);

        now = now.AddSeconds(WamChallengeStore.ChallengeTtlSeconds + 1);
        Assert.Equal(WamChallengeConsume.Expired,
            store.TryConsume(c, Wam, WamAuthPurpose.SessionUnlock));
    }

    [Fact]
    public void Challenge_EmptyOrUnknown_FailsClosed()
    {
        var store = new WamChallengeStore();
        Assert.Equal(WamChallengeConsume.Malformed,
            store.TryConsume("", Wam, WamAuthPurpose.SessionUnlock));
        Assert.Equal(WamChallengeConsume.NotFound,
            store.TryConsume("deadbeef", Wam, WamAuthPurpose.SessionUnlock));
    }
}
