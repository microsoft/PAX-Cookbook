using System.Text.Json;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S2B Phase 1 — compiled capability authority and the bounded capability
// route. These assert that a default build can never activate WAM (even with
// full environment configuration), that the capability body leaks no dynamic
// identifier, and that the locked allow-list is exact.
public sealed class ExperimentalWamCapabilityTests
{
    private const string TestTenant = "11111111-1111-1111-1111-111111111111";
    private const string TestClient = "22222222-2222-2222-2222-222222222222";
    private const string Capability = "/api/v1/broker/experimental/wam/capability";
    private const string Initiate = "/api/v1/broker/experimental/wam/initiate";
    private const string Status = "/api/v1/broker/experimental/wam/status";
    private const string Result = "/api/v1/broker/experimental/wam/result";

    private static ExperimentalWamOptions FullOptions() =>
        ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = TestTenant,
            ClientId = TestClient,
        });

    [Fact]
    public void CompileCapability_MatchesBuildMode()
    {
#if EXPERIMENTAL_WAM
        Assert.True(ExperimentalWamCapability.IsExperimentalAuthenticatorCompiled);
#else
        Assert.False(ExperimentalWamCapability.IsExperimentalAuthenticatorCompiled);
#endif
    }

    [Fact]
    public void DaemonEndpoint_FullConfig_ActivatesOnlyWhenCompiled()
    {
        ExperimentalWamDaemonEndpoint? endpoint = ExperimentalWamHost.TryCreateDaemonEndpoint(FullOptions());
#if EXPERIMENTAL_WAM
        Assert.NotNull(endpoint);
#else
        // A default build must NOT activate the daemon endpoint even with a fully
        // configured experimental runtime.
        Assert.Null(endpoint);
#endif
    }

    [Fact]
    public void DaemonEndpoint_IncompleteConfig_NeverActivates()
    {
        Assert.Null(ExperimentalWamHost.TryCreateDaemonEndpoint(ExperimentalWamOptions.Disabled));
        Assert.Null(ExperimentalWamHost.TryCreateDaemonEndpoint(ExperimentalWamHost.Resolve(_ => null)));
    }

    [Fact]
    public void Capability_ActiveEndpoint_ReturnsAvailableProviderId()
    {
        var endpoint = new ExperimentalWamDaemonEndpoint(FullOptions());
        (int status, object body) = ExperimentalWamRoutes.HandleCapability(endpoint);
        string json = JsonSerializer.Serialize(body);
        Assert.Equal(200, status);
        Assert.Contains("\"available\":true", json);
        Assert.Contains("\"providerId\":\"entra-wam\"", json);
    }

    [Fact]
    public void Capability_NullEndpoint_ReturnsUnavailable_NoProviderId()
    {
        (int status, object body) = ExperimentalWamRoutes.HandleCapability(null);
        string json = JsonSerializer.Serialize(body);
        Assert.Equal(200, status);
        Assert.Contains("\"available\":false", json);
        Assert.DoesNotContain("providerId", json);
    }

    [Fact]
    public void Capability_PipeReadinessFailure_ReportsUnavailable()
    {
        var endpoint = new ExperimentalWamDaemonEndpoint(FullOptions());
        ExperimentalWamDaemonEndpoint? active =
            ExperimentalWamActivation.ActivateIfPipeReady(endpoint, () => null, out _);
        (int _, object body) = ExperimentalWamRoutes.HandleCapability(active);
        Assert.Contains("\"available\":false", JsonSerializer.Serialize(body));
    }

    [Fact]
    public void Capability_Body_ContainsNoDynamicIdentifiers()
    {
        var endpoint = new ExperimentalWamDaemonEndpoint(FullOptions());
        string json = JsonSerializer.Serialize(ExperimentalWamRoutes.HandleCapability(endpoint).Body).ToLowerInvariant();
        foreach (string forbidden in new[] { TestTenant, TestClient, "tenant", "client", "resource", "salt", "pipe", "fingerprint", "token", "claim", "gate", "recipe", "account" })
        {
            Assert.DoesNotContain(forbidden.ToLowerInvariant(), json);
        }
    }

    [Fact]
    public void LockedAllowList_CapabilityGet_InitiatePost_StatusPost_Exact()
    {
        Assert.True(BrokerLock.IsRouteAllowedWhenLocked("GET", Capability));
        Assert.True(BrokerLock.IsRouteAllowedWhenLocked("POST", Initiate));
        Assert.True(BrokerLock.IsRouteAllowedWhenLocked("POST", Status));

        // The result path is never lock-bypass; method is exact; no prefix bypass.
        Assert.False(BrokerLock.IsRouteAllowedWhenLocked("POST", Result));
        Assert.False(BrokerLock.IsRouteAllowedWhenLocked("POST", Capability)); // capability is GET only
        Assert.False(BrokerLock.IsRouteAllowedWhenLocked("GET", Initiate));    // initiate is POST only
        Assert.False(BrokerLock.IsRouteAllowedWhenLocked("GET", "/api/v1/broker/experimental/wam/anything-else"));
    }
}
