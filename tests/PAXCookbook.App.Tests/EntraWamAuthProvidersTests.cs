using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S2A — experimental Entra WAM session-unlock provider, exercised through the
// neutral coordinator with spy side effects. No live WAM/MSAL; sanitized results
// are supplied directly.
public sealed class EntraWamAuthProvidersTests
{
    private const string TestTenant = "11111111-1111-1111-1111-111111111111";
    private const string TestClient = "22222222-2222-2222-2222-222222222222";

    private static ExperimentalWamOptions Options() =>
        ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = TestTenant,
            ClientId = TestClient,
        });

    private static WamInteractiveResult ValidResult(string account = "acct-1") =>
        WamInteractiveResult.Success(
            account,
            new[] { "User.Read" },
            TestTenant,
            "object-abc");

    // ---- session unlock -------------------------------------------------

    [Fact]
    public void Session_Approval_AppliesUnlockExactlyOnce()
    {
        int unlocks = 0;
        var coordinator = new SessionUnlockCoordinator(applyUnlock: () => unlocks++);
        var provider = new EntraWamSessionUnlockProvider(Options(), ValidResult(), challengeValidated: true);

        SessionUnlockOutcome outcome = coordinator.Unlock(provider);

        Assert.True(outcome.Approved);
        Assert.Equal(1, unlocks);
        Assert.Equal(EntraWamProviderReason.Approved, provider.LastReason);
    }

    [Fact]
    public void Session_ChallengeNotValidated_Denies_NoUnlock()
    {
        int unlocks = 0;
        var coordinator = new SessionUnlockCoordinator(applyUnlock: () => unlocks++);
        var provider = new EntraWamSessionUnlockProvider(Options(), ValidResult(), challengeValidated: false);

        SessionUnlockOutcome outcome = coordinator.Unlock(provider);

        Assert.False(outcome.Approved);
        Assert.Equal(0, unlocks);
        Assert.Equal(EntraWamProviderReason.ChallengeRejected, provider.LastReason);
    }

    [Fact]
    public void Session_NotConfigured_Denies()
    {
        var provider = new EntraWamSessionUnlockProvider(
            ExperimentalWamOptions.Disabled, ValidResult(), challengeValidated: true);
        Assert.False(provider.Authorize().Approved);
        Assert.Equal(EntraWamProviderReason.NotConfigured, provider.LastReason);
    }

    [Fact]
    public void Session_AcquireFailed_Denies()
    {
        var provider = new EntraWamSessionUnlockProvider(
            Options(), WamInteractiveResult.Failure(WamAcquireStatus.UserCancelled),
            challengeValidated: true);
        Assert.False(provider.Authorize().Approved);
        Assert.Equal(EntraWamProviderReason.AcquireFailed, provider.LastReason);
    }
}
