using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S2A — experimental Entra WAM configuration + explicit provider selection.
// Deterministic; no live WAM, tenant, or MSAL dependency. Test identifiers are
// obviously-synthetic placeholders, never the experimental registration.
public sealed class ExperimentalWamConfigAndSelectionTests
{
    private const string TestTenant = "11111111-1111-1111-1111-111111111111";
    private const string TestClient = "22222222-2222-2222-2222-222222222222";

    private static ExperimentalWamOptions FullyConfigured() =>
        ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = TestTenant,
            ClientId = TestClient,
        });

    // ---- configuration: disabled by default / fail closed --------------

    [Fact]
    public void Default_IsDisabled_AndNotConfigured()
    {
        Assert.False(ExperimentalWamOptions.Disabled.Enabled);
        Assert.False(ExperimentalWamOptions.Disabled.IsFullyConfigured);
    }

    [Fact]
    public void Create_Null_FailsClosed()
    {
        ExperimentalWamOptions options = ExperimentalWamOptions.Create(null);
        Assert.False(options.Enabled);
        Assert.False(options.IsFullyConfigured);
    }

    [Fact]
    public void Create_EnabledFalse_FailsClosed()
    {
        ExperimentalWamOptions options = ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = false,
            ProviderId = "entra-wam",
            TenantId = TestTenant,
            ClientId = TestClient,
        });
        Assert.False(options.IsFullyConfigured);
    }

    [Fact]
    public void Create_FullyConfigured_IsEnabledAndConfigured()
    {
        ExperimentalWamOptions options = FullyConfigured();
        Assert.True(options.Enabled);
        Assert.True(options.IsFullyConfigured);
        Assert.Equal(TestTenant, options.TenantId);
        Assert.Equal(TestClient, options.ClientId);
        // The single delegated sign-in scope is Microsoft Graph User.Read.
        Assert.Equal("User.Read", options.RequestScope);
    }

    [Theory]
    [InlineData(null, TestClient)]      // missing tenant
    [InlineData("", TestClient)]        // blank tenant
    [InlineData("not-a-guid", TestClient)] // malformed tenant
    [InlineData(TestTenant, null)]      // missing client
    [InlineData(TestTenant, "")]        // blank client
    [InlineData(TestTenant, "nope")]    // malformed client
    public void Create_PartialOrMalformed_FailsClosed(string? tenant, string? client)
    {
        ExperimentalWamOptions options = ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = tenant,
            ClientId = client,
        });
        Assert.False(options.IsFullyConfigured);
    }

    [Theory]
    [InlineData("windows-hello")]
    [InlineData("something-else")]
    [InlineData(null)]
    public void Create_EnabledWithWrongOrMissingProviderId_FailsClosed(string? providerId)
    {
        ExperimentalWamOptions options = ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = providerId,
            TenantId = TestTenant,
            ClientId = TestClient,
        });
        Assert.False(options.IsFullyConfigured);
    }

    // ---- provider selection: default / explicit / no fallback ----------

    [Fact]
    public void Select_NoRequest_DefaultsToWindowsHello()
    {
        AuthProviderSelection s = AuthProviderSelector.Select(null, ExperimentalWamOptions.Disabled);
        Assert.True(s.IsWindowsHello);
        Assert.Equal(AuthProviderSelectionReason.DefaultHello, s.Reason);
    }

    [Fact]
    public void Select_ExplicitWindowsHello_SelectsHello()
    {
        AuthProviderSelection s = AuthProviderSelector.Select("windows-hello", FullyConfigured());
        Assert.True(s.IsWindowsHello);
    }

    [Fact]
    public void Select_EntraWam_WhenFullyConfigured_SelectsEntraWam()
    {
        AuthProviderSelection s = AuthProviderSelector.Select("entra-wam", FullyConfigured());
        Assert.True(s.IsEntraWam);
        Assert.Equal(AuthProviderSelectionReason.ExperimentalSelected, s.Reason);
    }

    [Fact]
    public void Select_EntraWam_WhenDisabled_FailsClosed_NoHelloFallback()
    {
        AuthProviderSelection s = AuthProviderSelector.Select("entra-wam", ExperimentalWamOptions.Disabled);
        Assert.True(s.IsFailClosed);
        Assert.False(s.IsWindowsHello);   // no automatic fallback
        Assert.Equal(AuthProviderSelectionReason.ExperimentalNotEnabled, s.Reason);
    }

    [Fact]
    public void Select_UnknownProvider_FailsClosed()
    {
        AuthProviderSelection s = AuthProviderSelector.Select("some-unknown-provider", FullyConfigured());
        Assert.True(s.IsFailClosed);
        Assert.Equal(AuthProviderSelectionReason.UnknownProvider, s.Reason);
    }
}
