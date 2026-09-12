using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// cycle-02r5 Batch 2 — issued-identity extraction repair (the exact defense the
// prior bug lacked: an MSAL-shape-independent decision test).
//
// These tests are UNGATED (they compile and run in BOTH the stable and the
// /p:ExperimentalWam=true builds) because the pure seam under test —
// WorkAccountIssuedIdentity.Extract — and the authoritative WamScopeIdentityValidator
// have no MSAL dependency. Every identifier here is SYNTHETIC: no real tenant,
// object id, account handle, token, UPN, or photo appears.
//
// The matrix proves the exact bug is gone: the decision is made from the ISSUED
// (resource) tenant + the ISSUED oid, empty/whitespace is treated as MISSING
// (never null-only coalescing), a resource-tenant GUEST whose home tenant differs
// from the issued tenant is ACCEPTED, and every rejection grants nothing
// (including zero daemon-unlock effect, proven through the window bridge).
public sealed class WorkAccountIssuedIdentityTests
{
    private const string ConfiguredTenant = "11111111-1111-1111-1111-111111111111";
    private const string ConfiguredClient = "22222222-2222-2222-2222-222222222222";
    private const string OtherTenant = "33333333-3333-3333-3333-333333333333";

    // A synthetic opaque preferred-account handle shaped "<object>.<homeTenant>".
    // Its embedded HOME tenant is deliberately DIFFERENT from the configured
    // (resource) tenant to model a cross-tenant guest.
    private const string GuestHandle =
        "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.99999999-9999-9999-9999-999999999999";
    private const string IssuedOid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private static ExperimentalWamOptions Options() =>
        ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = ConfiguredTenant,
            ClientId = ConfiguredClient,
        });

    private static WamInteractiveResult Extract(
        string? issuedTenant,
        string? oid,
        string? handle,
        string[]? scopes) =>
        WorkAccountIssuedIdentity.Extract(issuedTenant, oid, handle, scopes, Options());

    // ---- accepted shapes ----------------------------------------------------

    [Fact]
    public void MatchingIssuedTenant_Oid_Handle_UserRead_IsAccepted()
    {
        WamInteractiveResult r = Extract(ConfiguredTenant, IssuedOid, GuestHandle, new[] { "User.Read" });
        Assert.True(r.Succeeded);
        Assert.Equal(WamAcquireStatus.Succeeded, r.Status);
        // The sanitized identity carries the ISSUED tenant + ISSUED oid, and the
        // opaque handle verbatim (so later Ordinal cache matching still works).
        Assert.Equal(ConfiguredTenant, r.TenantClaim);
        Assert.Equal(IssuedOid, r.ObjectClaim);
        Assert.Equal(GuestHandle, r.AccountHandle);
    }

    [Fact]
    public void DifferentHomeTenant_ButMatchingIssuedTenant_IsAccepted_ResourceTenantGuest()
    {
        // The handle encodes home tenant 9999...; the issued tenant is the
        // configured resource tenant. The OLD code read the tenant from the home
        // component and wrongly rejected this guest — the new code accepts it.
        WamInteractiveResult r = Extract(ConfiguredTenant, IssuedOid, GuestHandle, new[] { "User.Read" });
        Assert.True(r.Succeeded);
        Assert.Equal(ConfiguredTenant, r.TenantClaim);
    }

    [Fact]
    public void EmptyHomeTenantComponent_ButMatchingIssuedTenant_IsAccepted()
    {
        // Model an account whose HOME tenant component would have been empty (the
        // OLD null-only coalescing never fell back and produced a spurious
        // IdentityFailure). With the issued tenant matching, this is now accepted.
        const string handleWithEmptyHomeTenant = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb.";
        WamInteractiveResult r = Extract(ConfiguredTenant, IssuedOid, handleWithEmptyHomeTenant, new[] { "User.Read" });
        Assert.True(r.Succeeded);
        Assert.Equal(ConfiguredTenant, r.TenantClaim);
    }

    [Fact]
    public void UserRead_WithCompanionProtocolScopes_IsAccepted()
    {
        WamInteractiveResult r = Extract(
            ConfiguredTenant, IssuedOid, GuestHandle,
            new[] { "User.Read", "openid", "profile", "email", "offline_access" });
        Assert.True(r.Succeeded);
    }

    [Fact]
    public void GraphQualifiedUserRead_IsAccepted()
    {
        WamInteractiveResult r = Extract(
            ConfiguredTenant, IssuedOid, GuestHandle,
            new[] { "https://graph.microsoft.com/User.Read" });
        Assert.True(r.Succeeded);
    }

    // ---- identity rejections (all -> IdentityFailure, grant nothing) --------

    [Fact]
    public void MismatchingIssuedTenant_IsIdentityFailure()
    {
        WamInteractiveResult r = Extract(OtherTenant, IssuedOid, GuestHandle, new[] { "User.Read" });
        AssertGrantsNothing(r, WamAcquireStatus.IdentityFailure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankIssuedTenant_IsIdentityFailure(string? issuedTenant)
    {
        WamInteractiveResult r = Extract(issuedTenant, IssuedOid, GuestHandle, new[] { "User.Read" });
        AssertGrantsNothing(r, WamAcquireStatus.IdentityFailure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOid_IsIdentityFailure(string? oid)
    {
        WamInteractiveResult r = Extract(ConfiguredTenant, oid, GuestHandle, new[] { "User.Read" });
        AssertGrantsNothing(r, WamAcquireStatus.IdentityFailure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingAccountHandle_IsIdentityFailure(string? handle)
    {
        WamInteractiveResult r = Extract(ConfiguredTenant, IssuedOid, handle, new[] { "User.Read" });
        AssertGrantsNothing(r, WamAcquireStatus.IdentityFailure);
    }

    // ---- scope rejections (all -> ScopeFailure, grant nothing) --------------

    [Fact]
    public void UnexpectedScope_IsScopeFailure()
    {
        WamInteractiveResult r = Extract(ConfiguredTenant, IssuedOid, GuestHandle, new[] { "User.Read", "Mail.Read" });
        AssertGrantsNothing(r, WamAcquireStatus.ScopeFailure);
    }

    [Fact]
    public void DuplicateUserRead_IsScopeFailure()
    {
        WamInteractiveResult r = Extract(
            ConfiguredTenant, IssuedOid, GuestHandle,
            new[] { "User.Read", "https://graph.microsoft.com/User.Read" });
        AssertGrantsNothing(r, WamAcquireStatus.ScopeFailure);
    }

    [Theory]
    [InlineData(".default")]
    [InlineData("https://graph.microsoft.com/.default")]
    [InlineData("api://44444444-4444-4444-4444-444444444444/access_as_user")]
    public void ForbiddenScope_IsScopeFailure(string forbidden)
    {
        WamInteractiveResult r = Extract(ConfiguredTenant, IssuedOid, GuestHandle, new[] { "User.Read", forbidden });
        AssertGrantsNothing(r, WamAcquireStatus.ScopeFailure);
    }

    [Fact]
    public void MalformedScope_IsScopeFailure()
    {
        WamInteractiveResult r = Extract(ConfiguredTenant, IssuedOid, GuestHandle, new[] { "User.Read", "  " });
        AssertGrantsNothing(r, WamAcquireStatus.ScopeFailure);
    }

    [Fact]
    public void NoScopes_IsScopeFailure()
    {
        Assert.Equal(WamAcquireStatus.ScopeFailure,
            Extract(ConfiguredTenant, IssuedOid, GuestHandle, Array.Empty<string>()).Status);
        Assert.Equal(WamAcquireStatus.ScopeFailure,
            Extract(ConfiguredTenant, IssuedOid, GuestHandle, null).Status);
    }

    // A rejected extraction must NEVER present as an acquisition (so no downstream
    // preferred-save / photo-fetch / profile-publish / daemon-approval can run):
    // the pure seam itself holds no side-effect seams, so a non-succeeded result
    // is by construction inert.
    private static void AssertGrantsNothing(WamInteractiveResult r, WamAcquireStatus expected)
    {
        Assert.False(r.Succeeded);
        Assert.Equal(expected, r.Status);
        Assert.Equal(string.Empty, r.AccountHandle);
        Assert.Equal(string.Empty, r.TenantClaim);
        Assert.Equal(string.Empty, r.ObjectClaim);
        Assert.Empty(r.GrantedScopes);
    }

    // ---- daemon-unlock dimension: rejection yields NO approval ---------------

    private sealed class FixedAuthenticator : IExperimentalWamAuthenticator
    {
        private readonly WamInteractiveResult _result;
        internal FixedAuthenticator(WamInteractiveResult result) { _result = result; }
        public Task<WamInteractiveResult> AuthenticateAsync(
            WamAuthRequest request, IntPtr parentWindow, CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }

    [Fact]
    public async Task WindowBridge_MismatchedIssuedTenant_GrantsNoDaemonUnlock()
    {
        // A sanitized result carrying a WRONG issued tenant (what the fixed
        // extraction would reject, but proven here at the bridge that also
        // re-validates) must produce a rejected NeutralWamResult — never Approved.
        var wrongTenant = WamInteractiveResult.Success(
            GuestHandle, new[] { "User.Read" }, OtherTenant, IssuedOid);
        var bridge = new ExperimentalWamWindowBridge(
            Options(), new FixedAuthenticator(wrongTenant), () => (IntPtr)1);

        NeutralWamResult result = await bridge.AcquireAsync(
            "req-guard", WamNativeDescriptor.ForSession(), CancellationToken.None);

        Assert.False(result.IsApproved);
        Assert.Equal(NeutralWamCategory.IdentityFailure, result.Category);
    }

    [Fact]
    public async Task WindowBridge_UnexpectedScope_GrantsNoDaemonUnlock()
    {
        var badScope = WamInteractiveResult.Success(
            GuestHandle, new[] { "User.Read", "Mail.Read" }, ConfiguredTenant, IssuedOid);
        var bridge = new ExperimentalWamWindowBridge(
            Options(), new FixedAuthenticator(badScope), () => (IntPtr)1);

        NeutralWamResult result = await bridge.AcquireAsync(
            "req-guard", WamNativeDescriptor.ForSession(), CancellationToken.None);

        Assert.False(result.IsApproved);
        Assert.Equal(NeutralWamCategory.ScopeFailure, result.Category);
    }

    [Fact]
    public async Task WindowBridge_ValidIssuedTenant_Approves()
    {
        // Positive control: a correctly issued-tenant + User.Read result unlocks.
        var valid = WamInteractiveResult.Success(
            GuestHandle, new[] { "User.Read" }, ConfiguredTenant, IssuedOid);
        var bridge = new ExperimentalWamWindowBridge(
            Options(), new FixedAuthenticator(valid), () => (IntPtr)1);

        NeutralWamResult result = await bridge.AcquireAsync(
            "req-ok", WamNativeDescriptor.ForSession(), CancellationToken.None);

        Assert.True(result.IsApproved);
    }
}
