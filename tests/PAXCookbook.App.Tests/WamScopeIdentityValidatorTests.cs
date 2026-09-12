using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S3 — exact WAM scope + identity validation for the one-registration native
// WAM flow. Pure, deterministic; no live MSAL/WAM. Synthetic identifiers only.
// The single accepted data scope is Microsoft Graph User.Read.
public sealed class WamScopeIdentityValidatorTests
{
    private const string TestTenant = "11111111-1111-1111-1111-111111111111";
    private const string TestClient = "22222222-2222-2222-2222-222222222222";
    private const string OtherTenant = "33333333-3333-3333-3333-333333333333";

    private static ExperimentalWamOptions Options() =>
        ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = TestTenant,
            ClientId = TestClient,
        });

    private static WamInteractiveResult Result(string[] scopes, string tenant = TestTenant,
        string obj = "object-abc", string account = "account-1") =>
        WamInteractiveResult.Success(account, scopes, tenant, obj);

    [Fact]
    public void BareUserRead_IsValid()
    {
        var r = Result(new[] { "User.Read" });
        Assert.Equal(WamValidation.Valid, WamScopeIdentityValidator.Validate(r, Options()));
    }

    [Fact]
    public void GraphQualifiedUserRead_IsValid()
    {
        var r = Result(new[] { "https://graph.microsoft.com/User.Read" });
        Assert.Equal(WamValidation.Valid, WamScopeIdentityValidator.Validate(r, Options()));
    }

    [Fact]
    public void UserRead_WithCompanionProtocolScopes_IsValid()
    {
        // MSAL may return companion OIDC/protocol scopes alongside User.Read.
        var r = Result(new[] { "User.Read", "openid", "profile", "email", "offline_access" });
        Assert.Equal(WamValidation.Valid, WamScopeIdentityValidator.Validate(r, Options()));
    }

    [Theory]
    [InlineData("api://44444444-4444-4444-4444-444444444444/access_as_user")]
    [InlineData("22222222-2222-2222-2222-222222222222/access_as_user")]
    [InlineData("https://graph.microsoft.com/.default")]
    [InlineData(".default")]
    public void CustomScopeOrDefault_IsForbidden(string forbidden)
    {
        var r = Result(new[] { "User.Read", forbidden });
        Assert.Equal(WamValidation.ForbiddenScopePresent,
            WamScopeIdentityValidator.Validate(r, Options()));
    }

    [Fact]
    public void UnexpectedGraphPermission_IsRejected()
    {
        var r = Result(new[] { "User.Read", "Mail.Read" });
        Assert.Equal(WamValidation.UnexpectedScope,
            WamScopeIdentityValidator.Validate(r, Options()));
    }

    [Fact]
    public void NoUserRead_IsRejected()
    {
        var r = Result(new[] { "offline_access" });
        Assert.Equal(WamValidation.NoUserReadScope,
            WamScopeIdentityValidator.Validate(r, Options()));
    }

    [Fact]
    public void MultipleUserReadForms_IsRejected()
    {
        var r = Result(new[] { "User.Read", "https://graph.microsoft.com/User.Read" });
        Assert.Equal(WamValidation.MultipleUserReadScopes,
            WamScopeIdentityValidator.Validate(r, Options()));
    }

    [Fact]
    public void MalformedScope_IsRejected()
    {
        var r = Result(new[] { "User.Read", "  " });
        Assert.Equal(WamValidation.MalformedScope,
            WamScopeIdentityValidator.Validate(r, Options()));
    }

    [Fact]
    public void DisabledOptions_NeverValidate()
    {
        var r = Result(new[] { "User.Read" });
        Assert.NotEqual(WamValidation.Valid,
            WamScopeIdentityValidator.Validate(r, ExperimentalWamOptions.Disabled));
    }

    [Fact]
    public void MissingAccountBinding_IsRejected()
    {
        var r = Result(new[] { "User.Read" }, account: "");
        Assert.Equal(WamValidation.MissingAccountBinding,
            WamScopeIdentityValidator.Validate(r, Options()));
    }

    [Fact]
    public void MissingTenantClaim_IsRejected()
    {
        var r = Result(new[] { "User.Read" }, tenant: "");
        Assert.Equal(WamValidation.MissingTenantClaim,
            WamScopeIdentityValidator.Validate(r, Options()));
    }

    [Fact]
    public void TenantMismatch_IsRejected()
    {
        var r = Result(new[] { "User.Read" }, tenant: OtherTenant);
        Assert.Equal(WamValidation.TenantMismatch,
            WamScopeIdentityValidator.Validate(r, Options()));
    }

    [Fact]
    public void MissingObjectClaim_IsRejected()
    {
        var r = Result(new[] { "User.Read" }, obj: "");
        Assert.Equal(WamValidation.MissingObjectClaim,
            WamScopeIdentityValidator.Validate(r, Options()));
    }

    [Fact]
    public void NotSucceeded_IsRejected()
    {
        var r = WamInteractiveResult.Failure(WamAcquireStatus.UserCancelled);
        Assert.Equal(WamValidation.NotSucceeded,
            WamScopeIdentityValidator.Validate(r, Options()));
    }
}
