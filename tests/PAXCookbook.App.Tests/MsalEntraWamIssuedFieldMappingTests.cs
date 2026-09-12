#if EXPERIMENTAL_WAM
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// cycle-02r5 Batch 2 — field-mapping proof for the REAL MSAL glue plus
// side-effect containment (EXPERIMENTAL_WAM only, because it constructs a real
// Microsoft.Identity.Client AuthenticationResult carrying a synthetic
// ClaimsPrincipal). This is the exact defense the prior bug lacked: a test that
// pins the field mapping so a future change from ISSUED to HOME values (or a
// null-only regression) fails immediately.
//
// Every value is SYNTHETIC: no real tenant, object id, oid, account handle,
// token, UPN, or photo appears. The ClaimsPrincipal is injected directly into the
// AuthenticationResult constructor; no real id/access token bytes are used.
public sealed class MsalEntraWamIssuedFieldMappingTests
{
    private const string ConfiguredTenant = "11111111-1111-1111-1111-111111111111";
    private const string ConfiguredClient = "22222222-2222-2222-2222-222222222222";
    private const string HomeTenant = "99999999-9999-9999-9999-999999999999";
    private const string IssuedOid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string HomeObject = "dddddddd-dddd-dddd-dddd-dddddddddddd";
    private const string Handle =
        "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.99999999-9999-9999-9999-999999999999";

    // A synthetic IAccount whose HOME identity (tenant + object) is deliberately
    // DIFFERENT from the issued values, so a test that accidentally read the home
    // values instead of the issued ones would fail.
    private sealed class SyntheticAccount : IAccount
    {
        public string Username => "guest@example.invalid";
        public string Environment => "login.microsoftonline.com";
        public AccountId HomeAccountId { get; } = new(Handle, HomeObject, HomeTenant);
    }

    private static AuthenticationResult BuildResult(string issuedTenant, string? oidClaimType, string? oidValue)
    {
        var claims = new List<Claim>();
        if (oidClaimType is not null && oidValue is not null)
        {
            claims.Add(new Claim(oidClaimType, oidValue));
        }

        var claimsPrincipal = new ClaimsPrincipal(new ClaimsIdentity(claims));

        // MSAL public test constructor. The ClaimsPrincipal is injected directly;
        // idToken is passed empty (no synthetic token bytes are fabricated).
        return new AuthenticationResult(
            accessToken: "synthetic-access-token",
            isExtendedLifeTimeToken: false,
            uniqueId: HomeObject,
            expiresOn: DateTimeOffset.UtcNow.AddHours(1),
            extendedExpiresOn: DateTimeOffset.UtcNow.AddHours(1),
            tenantId: issuedTenant,
            account: new SyntheticAccount(),
            idToken: string.Empty,
            scopes: new[] { "User.Read" },
            correlationId: Guid.NewGuid(),
            tokenType: "Bearer",
            authenticationResultMetadata: null,
            claimsPrincipal: claimsPrincipal,
            spaAuthCode: null,
            additionalResponseParameters: null);
    }

    private static ExperimentalWamOptions Options() =>
        ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = ConfiguredTenant,
            ClientId = ConfiguredClient,
        });

    // ---- the field-mapping pin ----------------------------------------------

    [Fact]
    public void ReadIssuedFields_ReadsIssuedTenantAndOid_NotHomeValues()
    {
        AuthenticationResult result = BuildResult(ConfiguredTenant, "oid", IssuedOid);
        (string? issuedTenant, string? oid, string? handle, IReadOnlyList<string> scopes) =
            MsalEntraWamAuthenticator.ReadIssuedFields(result);

        // Issued tenant (result.TenantId), NOT HomeAccountId.TenantId.
        Assert.Equal(ConfiguredTenant, issuedTenant);
        Assert.NotEqual(HomeTenant, issuedTenant);

        // Issued oid claim, NOT HomeAccountId.ObjectId / result.UniqueId.
        Assert.Equal(IssuedOid, oid);
        Assert.NotEqual(HomeObject, oid);

        // Opaque preferred-account handle preserved verbatim.
        Assert.Equal(Handle, handle);
        Assert.Contains("User.Read", scopes);
    }

    [Fact]
    public void ReadIssuedFields_FallsBackToObjectIdentifierUri_WhenShortOidAbsent()
    {
        AuthenticationResult result = BuildResult(
            ConfiguredTenant, "http://schemas.microsoft.com/identity/claims/objectidentifier", IssuedOid);
        (_, string? oid, _, _) = MsalEntraWamAuthenticator.ReadIssuedFields(result);
        Assert.Equal(IssuedOid, oid);
    }

    [Fact]
    public void ReadIssuedFields_NoOidClaim_YieldsNullOrEmpty()
    {
        AuthenticationResult result = BuildResult(ConfiguredTenant, oidClaimType: null, oidValue: null);
        (_, string? oid, _, _) = MsalEntraWamAuthenticator.ReadIssuedFields(result);
        Assert.True(string.IsNullOrEmpty(oid));
    }

    // End-to-end: the constructed MSAL result feeds the pure seam and yields an
    // accepted, ISSUED-identity-sanitized result (a resource-tenant guest whose
    // home tenant differs is accepted).
    [Fact]
    public void ConstructedResult_FeedsExtract_AcceptedWithIssuedIdentity()
    {
        AuthenticationResult result = BuildResult(ConfiguredTenant, "oid", IssuedOid);
        (string? issuedTenant, string? oid, string? handle, IReadOnlyList<string> scopes) =
            MsalEntraWamAuthenticator.ReadIssuedFields(result);

        WamInteractiveResult sanitized = WorkAccountIssuedIdentity.Extract(
            issuedTenant, oid, handle, scopes, Options());

        Assert.True(sanitized.Succeeded);
        Assert.Equal(ConfiguredTenant, sanitized.TenantClaim);
        Assert.Equal(IssuedOid, sanitized.ObjectClaim);
        Assert.Equal(Handle, sanitized.AccountHandle);
    }

    // A personal MSA whose issued tenant is NOT the configured resource tenant is
    // correctly rejected (IdentityFailure), even though the field reads succeed.
    [Fact]
    public void ConstructedResult_WrongIssuedTenant_IsIdentityFailure()
    {
        AuthenticationResult result = BuildResult(HomeTenant, "oid", IssuedOid);
        (string? issuedTenant, string? oid, string? handle, IReadOnlyList<string> scopes) =
            MsalEntraWamAuthenticator.ReadIssuedFields(result);

        WamInteractiveResult sanitized = WorkAccountIssuedIdentity.Extract(
            issuedTenant, oid, handle, scopes, Options());

        Assert.False(sanitized.Succeeded);
        Assert.Equal(WamAcquireStatus.IdentityFailure, sanitized.Status);
    }

    // ---- zero side effects on every rejection at the chokepoint -------------

    private sealed class RecordingStore : IWorkAccountPreferredAccountStore
    {
        internal int SaveCount { get; private set; }
        public bool TrySave(string accountReference) { SaveCount++; return true; }
        public string? TryLoad() => null;
        public bool Clear() => true;
    }

    private sealed class RecordingPhotoFetcher : IWorkAccountProfilePhotoFetcher
    {
        internal int FetchCount { get; private set; }
        public Task<WorkAccountPhotoFetchResult> FetchAsync(string accessToken, CancellationToken cancellationToken)
        {
            FetchCount++;
            return Task.FromResult(WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.NotFound));
        }
    }

    private sealed class RecordingSink : IWorkAccountProfilePresentationSink
    {
        internal int PublishCount { get; private set; }
        public void Publish(WorkAccountProfilePresentation presentation) => PublishCount++;
        public void Clear() { }
    }

    // Expected status is passed as an int because WamAcquireStatus is internal and
    // cannot appear in a public xUnit theory signature (CS0051).
    public static IEnumerable<object[]> RejectionShapes()
    {
        // scopes, sanitized tenant, sanitized object, expected status (int).
        yield return new object[] { new[] { "User.Read" }, ConfiguredClient, "obj", (int)WamAcquireStatus.IdentityFailure }; // tenant mismatch
        yield return new object[] { new[] { "User.Read" }, "", "obj", (int)WamAcquireStatus.IdentityFailure };               // blank tenant
        yield return new object[] { new[] { "User.Read" }, ConfiguredTenant, "", (int)WamAcquireStatus.IdentityFailure };    // missing object
        yield return new object[] { new[] { "User.Read", "Mail.Read" }, ConfiguredTenant, "obj", (int)WamAcquireStatus.ScopeFailure };
        yield return new object[] { new[] { "User.Read", "https://graph.microsoft.com/User.Read" }, ConfiguredTenant, "obj", (int)WamAcquireStatus.ScopeFailure };
        yield return new object[] { new[] { "User.Read", ".default" }, ConfiguredTenant, "obj", (int)WamAcquireStatus.ScopeFailure };
        yield return new object[] { new[] { "User.Read", "api://x/access_as_user" }, ConfiguredTenant, "obj", (int)WamAcquireStatus.ScopeFailure };
        yield return new object[] { new[] { "User.Read", "  " }, ConfiguredTenant, "obj", (int)WamAcquireStatus.ScopeFailure };
    }

    [Theory]
    [MemberData(nameof(RejectionShapes))]
    public async Task CompleteValidatedAcquisition_EveryRejection_HasZeroSideEffects(
        string[] scopes, string tenant, string obj, int expected)
    {
        var store = new RecordingStore();
        var photo = new RecordingPhotoFetcher();
        var sink = new RecordingSink();
        var authenticator = new MsalEntraWamAuthenticator(photo, sink, store);
        var request = new WamAuthRequest(
            WamAuthPurpose.SessionUnlock, ConfiguredTenant, ConfiguredClient,
            ExperimentalWamOptions.GraphUserReadScope);
        WamInteractiveResult sanitized = WamInteractiveResult.Success(Handle, scopes, tenant, obj);

        WamInteractiveResult result = await authenticator.CompleteValidatedAcquisitionAsync(
            sanitized, request, "synthetic-access-token", "guest@example.invalid", CancellationToken.None);

        Assert.Equal(expected, (int)result.Status);
        Assert.Equal(0, store.SaveCount);   // zero preferred-save
        Assert.Equal(0, photo.FetchCount);  // zero photo-fetch
        Assert.Equal(0, sink.PublishCount); // zero profile-publish
    }
}
#endif
