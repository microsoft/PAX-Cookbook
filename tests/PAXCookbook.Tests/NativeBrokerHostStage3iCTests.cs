using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3i-C parity tests for the native broker's auth-profile
// mutation surface (POST/PUT/DELETE /api/v1/auth/profiles[/{id}]),
// auth-profile secret bind/remove surface, auth-profile structural
// test surface, and cook stop/kill/resume surface.
//
// Shares the "NativeBrokerHostPortBinding" xUnit collection with the
// rest of the NativeBrokerHost tests so port-17654 binding is
// serialised across Stage 3a-3i runs.
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3iCTests
{
    // PAX baseline tripwire. Stage 3i-C is a BROKER-side change;
    // the PAX script does not move.
    private const string PaxScriptBaselineHash =
        "007AD1A7F6D40B40E873C684D10B2A79B4D1DD03A1900ADE19B6E482CC10C728";

    // Deterministic ids for envelope byte-stability across runs.
    private const string FactoryAuthProfileId = "12345678-1234-1234-1234-123456789abc";
    private const string FactoryNewCookId     = "abcdef01-2345-6789-abcd-ef0123456789";
    private const string ExistingProfileSecret = "11111111-2222-3333-4444-555555555555";
    private const string ExistingProfileCert   = "66666666-7777-8888-9999-aaaaaaaaaaaa";

    // Lowercase GUIDs distinct from Stage 3i-A/B for cross-suite
    // safety. cookId space is separate from auth-profile space.
    private const string ParentCookIdInterrupted = "00000000-1111-2222-3333-444444444444";
    private const string CookIdActive            = "55555555-6666-7777-8888-999999999999";
    private const string RecipeId                = "01JKMNPQRSTVWXYZABCDEFGH37";

    private const string ValidTenantId   = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string ValidClientId   = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string ValidThumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";

    private static readonly DateTimeOffset FrozenClockUtc =
        new(2026, 5, 27, 17, 32, 19, TimeSpan.FromTicks(0));
    private static readonly string FrozenClockUtcIso = "2026-05-27T17:32:19.000Z";

    // ============================================================
    //  POST /api/v1/auth/profiles  (create)
    // ============================================================

    [Fact]
    public async Task PostCreate_secret_mode_returns_201_and_persists_row()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var creds  = new FakeCredentialSecretStore();
        var bundle = fx.BuildCBundle(
            reauth: reauth, creds: creds,
            newAuthProfileId: () => FactoryAuthProfileId);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = "{\"mode\":\"AppRegistrationSecret\",\"name\":\"Tenant A\",\"tenantId\":\""
                + ValidTenantId + "\",\"clientId\":\"" + ValidClientId + "\",\"description\":\"primary\"}";
            using var resp = await http.PostAsync("/api/v1/auth/profiles",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

            var doc  = await ReadJsonAsync(resp);
            var root = doc.RootElement;
            Assert.Equal(FactoryAuthProfileId, root.GetProperty("authProfileId").GetString());

            var profile = root.GetProperty("authProfile");
            Assert.Equal("Tenant A", profile.GetProperty("name").GetString());
            Assert.Equal("AppRegistrationSecret", profile.GetProperty("mode").GetString());
            Assert.Equal(ValidTenantId, profile.GetProperty("tenantId").GetString());
            Assert.Equal(ValidClientId, profile.GetProperty("clientId").GetString());
            Assert.Equal(
                "PAXCookbook.AuthProfile." + FactoryAuthProfileId + ".ClientSecret",
                profile.GetProperty("credManTarget").GetString());

            // Verify single re-auth call landed with the right opClass.
            Assert.Single(reauth.Calls);
            Assert.Equal("profileMutation", reauth.Calls[0].OpClass);

            // Verify row landed on disk.
            var row = fx.GetAuthProfile(FactoryAuthProfileId);
            Assert.NotNull(row);
            Assert.Equal("Tenant A", row!.Name);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostCreate_without_reauth_returns_401_reAuthRequired()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier();
        reauth.Enqueue(new WindowsReAuthVerdict("Canceled", false, null));
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = "{\"mode\":\"AppRegistrationSecret\",\"name\":\"X\",\"tenantId\":\""
                + ValidTenantId + "\",\"clientId\":\"" + ValidClientId + "\"}";
            using var resp = await http.PostAsync("/api/v1/auth/profiles",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("reAuthRequired", doc.RootElement.GetProperty("code").GetString());
            Assert.Equal("profileMutation", doc.RootElement.GetProperty("opClass").GetString());
            Assert.Equal("Canceled", doc.RootElement.GetProperty("verificationResult").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostCreate_with_empty_body_returns_400_invalid_json()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/auth/profiles",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_json", doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostCreate_with_missing_mode_returns_422_validation_failed()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = "{\"name\":\"X\",\"tenantId\":\"" + ValidTenantId
                + "\",\"clientId\":\"" + ValidClientId + "\"}";
            using var resp = await http.PostAsync("/api/v1/auth/profiles",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("auth_profile_invalid", doc.RootElement.GetProperty("error").GetString());
            var errors = doc.RootElement.GetProperty("errors");
            Assert.True(errors.GetArrayLength() >= 1);
            Assert.Contains(
                Enumerable.Range(0, errors.GetArrayLength())
                    .Select(i => errors[i].GetProperty("instancePath").GetString()),
                p => p == "/mode");
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostCreate_with_duplicate_name_returns_409_name_in_use()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileSecret, "Duplicate Name",
            mode: AuthProfileModes.AppRegistrationSecret);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = "{\"mode\":\"AppRegistrationSecret\",\"name\":\"Duplicate Name\",\"tenantId\":\""
                + ValidTenantId + "\",\"clientId\":\"" + ValidClientId + "\"}";
            using var resp = await http.PostAsync("/api/v1/auth/profiles",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("auth_profile_name_in_use", doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("Duplicate Name", doc.RootElement.GetProperty("name").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  PUT /api/v1/auth/profiles/{id}
    // ============================================================

    [Fact]
    public async Task PutUpdate_happy_path_returns_200_and_updates_row()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileSecret, "Old Name",
            mode: AuthProfileModes.AppRegistrationSecret);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = "{\"name\":\"New Name\",\"tenantId\":\"" + ValidTenantId
                + "\",\"clientId\":\"" + ValidClientId + "\",\"description\":\"updated desc\"}";
            using var resp = await http.PutAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret,
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal(ExistingProfileSecret,
                doc.RootElement.GetProperty("authProfileId").GetString());
            Assert.Equal("New Name",
                doc.RootElement.GetProperty("authProfile").GetProperty("name").GetString());
            Assert.Equal("updated desc",
                doc.RootElement.GetProperty("authProfile").GetProperty("description").GetString());

            var row = fx.GetAuthProfile(ExistingProfileSecret);
            Assert.Equal("New Name", row!.Name);
            Assert.Equal("updated desc", row.Description);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PutUpdate_unknown_id_returns_404_not_found()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = "{\"name\":\"X\",\"tenantId\":\"" + ValidTenantId
                + "\",\"clientId\":\"" + ValidClientId + "\"}";
            using var resp = await http.PutAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret,
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("auth_profile_not_found",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PutUpdate_with_mode_change_returns_422_immutable()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileSecret, "Original",
            mode: AuthProfileModes.AppRegistrationSecret);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = "{\"mode\":\"AppRegistrationCertificate\",\"name\":\"Original\",\"tenantId\":\""
                + ValidTenantId + "\",\"clientId\":\"" + ValidClientId + "\"}";
            using var resp = await http.PutAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret,
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("auth_profile_invalid",
                doc.RootElement.GetProperty("error").GetString());
            var errors = doc.RootElement.GetProperty("errors");
            Assert.Contains(
                Enumerable.Range(0, errors.GetArrayLength())
                    .Select(i => (errors[i].GetProperty("instancePath").GetString(),
                                  errors[i].GetProperty("keyword").GetString())),
                t => t == ("/mode", "immutable"));
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  DELETE /api/v1/auth/profiles/{id}
    // ============================================================

    [Fact]
    public async Task DeleteProfile_happy_path_returns_200_and_removes_row_and_credential()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileSecret, "To be deleted",
            mode: AuthProfileModes.AppRegistrationSecret);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var creds  = new FakeCredentialSecretStore();
        creds.ExistingTargets.Add(ExistingProfileSecret);
        var bundle = fx.BuildCBundle(reauth: reauth, creds: creds);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal(ExistingProfileSecret,
                doc.RootElement.GetProperty("authProfileId").GetString());
            Assert.True(doc.RootElement.GetProperty("deleted").GetBoolean());
            Assert.Equal(JsonValueKind.Null,
                doc.RootElement.GetProperty("credentialDeleteFailed").ValueKind);

            Assert.Null(fx.GetAuthProfile(ExistingProfileSecret));
            Assert.DoesNotContain(ExistingProfileSecret, creds.ExistingTargets);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task DeleteProfile_unknown_id_returns_404_not_found()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("auth_profile_not_found",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/auth/profiles/{id}/secret  (bind)
    // ============================================================

    [Fact]
    public async Task PostSecretBind_happy_path_writes_credential_and_returns_200()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileSecret, "Bindable",
            mode: AuthProfileModes.AppRegistrationSecret);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var creds  = new FakeCredentialSecretStore();
        var bundle = fx.BuildCBundle(reauth: reauth, creds: creds);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = "{\"clientSecret\":\"S3CR3T-value\"}";
            using var resp = await http.PostAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret + "/secret",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal(ExistingProfileSecret,
                doc.RootElement.GetProperty("authProfileId").GetString());
            Assert.Equal(
                "PAXCookbook.AuthProfile." + ExistingProfileSecret + ".ClientSecret",
                doc.RootElement.GetProperty("credManTarget").GetString());
            Assert.True(doc.RootElement.GetProperty("bound").GetBoolean());

            Assert.Single(creds.Writes);
            Assert.Equal(ExistingProfileSecret, creds.Writes[0].AuthProfileId);
            Assert.Equal("S3CR3T-value", creds.Writes[0].Secret);
            Assert.Single(reauth.Calls);
            Assert.Equal("secretBind", reauth.Calls[0].OpClass);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostSecretBind_on_cert_mode_returns_422_mode_mismatch()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileCert, "Cert profile",
            mode: AuthProfileModes.AppRegistrationCertificate,
            certThumbprint: ValidThumbprint,
            certStore: AuthProfileModes.DefaultCertStore);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var body = "{\"clientSecret\":\"x\"}";
            using var resp = await http.PostAsync(
                "/api/v1/auth/profiles/" + ExistingProfileCert + "/secret",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("auth_profile_mode_mismatch",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("AppRegistrationCertificate",
                doc.RootElement.GetProperty("currentMode").GetString());
            Assert.Equal("AppRegistrationSecret",
                doc.RootElement.GetProperty("requiredMode").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostSecretBind_without_clientSecret_returns_400_client_secret_required()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileSecret, "Bindable",
            mode: AuthProfileModes.AppRegistrationSecret);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret + "/secret",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("client_secret_required",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostSecretBind_oversized_secret_returns_400_client_secret_too_long()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileSecret, "Bindable",
            mode: AuthProfileModes.AppRegistrationSecret);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            var huge = new string('x', AuthProfileSecretService.MaxSecretLength + 1);
            var body = "{\"clientSecret\":\"" + huge + "\"}";
            using var resp = await http.PostAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret + "/secret",
                new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("client_secret_too_long",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(AuthProfileSecretService.MaxSecretLength,
                doc.RootElement.GetProperty("maxLength").GetInt32());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostSecretBind_unknown_id_returns_404()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret + "/secret",
                new StringContent("{\"clientSecret\":\"x\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("auth_profile_not_found",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  DELETE /api/v1/auth/profiles/{id}/secret  (remove)
    // ============================================================

    [Fact]
    public async Task DeleteSecret_when_credential_present_returns_present()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileSecret, "Bound profile",
            mode: AuthProfileModes.AppRegistrationSecret);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var creds  = new FakeCredentialSecretStore();
        creds.ExistingTargets.Add(ExistingProfileSecret);
        var bundle = fx.BuildCBundle(reauth: reauth, creds: creds);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret + "/secret");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("present",
                doc.RootElement.GetProperty("removed").GetString());
            Assert.DoesNotContain(ExistingProfileSecret, creds.ExistingTargets);
            Assert.Equal("secretRemove", reauth.Calls[0].OpClass);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task DeleteSecret_when_credential_absent_returns_absent()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileSecret, "Unbound profile",
            mode: AuthProfileModes.AppRegistrationSecret);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var creds  = new FakeCredentialSecretStore(); // empty
        var bundle = fx.BuildCBundle(reauth: reauth, creds: creds);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret + "/secret");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("absent",
                doc.RootElement.GetProperty("removed").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task DeleteSecret_on_cert_mode_returns_422_mode_mismatch()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileCert, "Cert",
            mode: AuthProfileModes.AppRegistrationCertificate,
            certThumbprint: ValidThumbprint,
            certStore: AuthProfileModes.DefaultCertStore);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync(
                "/api/v1/auth/profiles/" + ExistingProfileCert + "/secret");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("auth_profile_mode_mismatch",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/auth/profiles/{id}/test  (structural test)
    // ============================================================

    [Fact]
    public async Task PostTest_secret_mode_with_credential_returns_structural_ok()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileSecret, "Tested",
            mode: AuthProfileModes.AppRegistrationSecret);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var creds  = new FakeCredentialSecretStore();
        creds.ExistingTargets.Add(ExistingProfileSecret);
        var bundle = fx.BuildCBundle(reauth: reauth, creds: creds);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret + "/test",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("structural_ok",
                doc.RootElement.GetProperty("detail").GetString());
            Assert.Equal("structural",
                doc.RootElement.GetProperty("validationKind").GetString());
            Assert.Equal("profileTest", reauth.Calls[0].OpClass);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostTest_secret_mode_without_credential_returns_secret_missing()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileSecret, "Unbound tested",
            mode: AuthProfileModes.AppRegistrationSecret);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);  // no credentials seeded
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret + "/test",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("secret_missing",
                doc.RootElement.GetProperty("detail").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostTest_cert_mode_with_valid_thumbprint_returns_structural_ok()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileCert, "Cert tested",
            mode: AuthProfileModes.AppRegistrationCertificate,
            certThumbprint: ValidThumbprint,
            certStore: AuthProfileModes.DefaultCertStore);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var probe  = new FakeCertificateProbe();
        probe.Set(ValidThumbprint, AuthProfileModes.DefaultCertStore, hit: true);
        var bundle = fx.BuildCBundle(reauth: reauth, certProbe: probe);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/auth/profiles/" + ExistingProfileCert + "/test",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("structural_ok",
                doc.RootElement.GetProperty("detail").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostTest_cert_mode_not_found_in_store_returns_cert_not_found()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedAuthProfileAsync(ExistingProfileCert, "Missing cert",
            mode: AuthProfileModes.AppRegistrationCertificate,
            certThumbprint: ValidThumbprint,
            certStore: AuthProfileModes.DefaultCertStore);
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var probe  = new FakeCertificateProbe();
        probe.Set(ValidThumbprint, AuthProfileModes.DefaultCertStore, hit: false);
        var bundle = fx.BuildCBundle(reauth: reauth, certProbe: probe);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/auth/profiles/" + ExistingProfileCert + "/test",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("cert_not_found",
                doc.RootElement.GetProperty("detail").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostTest_unknown_id_returns_404()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/auth/profiles/" + ExistingProfileSecret + "/test",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("auth_profile_not_found",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/cooks/{id}/stop  (cooperative cancel)
    // ============================================================

    [Fact]
    public async Task PostStop_for_registered_cook_returns_202_accepted_no_reauth_call()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier(); // NO verdict enqueued
        var registry = new FakeCookProcessRegistry();
        registry.Register(CookIdActive, processId: 4242);
        var bundle = fx.BuildCBundle(reauth: reauth, cookRegistry: registry);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/cooks/" + CookIdActive + "/stop",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal(CookIdActive,
                doc.RootElement.GetProperty("cookId").GetString());
            Assert.Equal("stop",
                doc.RootElement.GetProperty("accepted").GetString());

            // STOP must not invoke re-auth.
            Assert.Empty(reauth.Calls);
            Assert.Single(registry.StopRequests);
            Assert.Equal(CookIdActive, registry.StopRequests[0]);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostStop_for_unregistered_cook_returns_404_not_active()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier();
        var registry = new FakeCookProcessRegistry(); // empty
        var bundle = fx.BuildCBundle(reauth: reauth, cookRegistry: registry);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/cooks/" + CookIdActive + "/stop",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("cook_not_active",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostStop_with_malformed_cookId_returns_400_invalid_cook_id()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/cooks/not-a-guid/stop",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_cook_id",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/cooks/{id}/kill  (force kill)
    // ============================================================

    [Fact]
    public async Task PostKill_for_registered_cook_returns_202_kill_no_reauth_call()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier();
        var registry = new FakeCookProcessRegistry();
        registry.Register(CookIdActive, processId: 4242);
        var bundle = fx.BuildCBundle(reauth: reauth, cookRegistry: registry);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/cooks/" + CookIdActive + "/kill",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("kill",
                doc.RootElement.GetProperty("accepted").GetString());
            Assert.Empty(reauth.Calls);
            Assert.Single(registry.KillRequests);
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  POST /api/v1/cooks/{id}/resume
    // ============================================================

    [Fact]
    public async Task PostResume_happy_path_returns_201_spawned_envelope()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var recipeFile = await fx.SeedRecipeAsync(RecipeId, "Resumed recipe");
        var cookFolder = fx.MakeCookFolder(ParentCookIdInterrupted);
        await File.WriteAllTextAsync(Path.Combine(cookFolder, "checkpoint.json"),
            "{\"checkpointVersion\":1}");
        await fx.SeedInterruptedCookAsync(
            cookId:          ParentCookIdInterrupted,
            recipeId:        RecipeId,
            cookFolder:      cookFolder,
            errorClass:      CookClosureReasons.CancelStop,
            paxScriptPath:   fx.PaxScriptPath,
            paxScriptVersion: "1.0.0");

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var spawner = new FakeCookResumeSpawner
        {
            Result = new CookResumeSpawnResult("spawned", null, null),
        };
        var bundle = fx.BuildCBundle(
            reauth:        reauth,
            resumeSpawner: spawner,
            newCookId:     () => FactoryNewCookId);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/cooks/" + ParentCookIdInterrupted + "/resume",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal(ParentCookIdInterrupted,
                doc.RootElement.GetProperty("parentCookId").GetString());
            Assert.Equal(FactoryNewCookId,
                doc.RootElement.GetProperty("cookId").GetString());
            Assert.Equal(RecipeId,
                doc.RootElement.GetProperty("recipeId").GetString());

            Assert.Single(spawner.SpawnRequests);
            Assert.Equal(ParentCookIdInterrupted,
                spawner.SpawnRequests[0].ParentCookId);
            Assert.Equal(FactoryNewCookId,
                spawner.SpawnRequests[0].NewCookId);
            Assert.Equal(recipeFile,
                spawner.SpawnRequests[0].RecipeFilePath);
            Assert.Equal("manualCook", reauth.Calls[0].OpClass);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostResume_without_reauth_returns_401_reAuthRequired()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier();
        reauth.Enqueue(new WindowsReAuthVerdict("Canceled", false, null));
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/cooks/" + ParentCookIdInterrupted + "/resume",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("reAuthRequired",
                doc.RootElement.GetProperty("code").GetString());
            Assert.Equal("manualCook",
                doc.RootElement.GetProperty("opClass").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostResume_unknown_cook_returns_404_not_found()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/cooks/" + ParentCookIdInterrupted + "/resume",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("cook_not_found",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostResume_with_cancel_kill_closure_returns_409_not_resumable()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedRecipeAsync(RecipeId, "Recipe");
        var cookFolder = fx.MakeCookFolder(ParentCookIdInterrupted);
        await File.WriteAllTextAsync(Path.Combine(cookFolder, "checkpoint.json"), "{}");
        await fx.SeedInterruptedCookAsync(
            cookId:          ParentCookIdInterrupted,
            recipeId:        RecipeId,
            cookFolder:      cookFolder,
            errorClass:      CookClosureReasons.CancelKill,
            paxScriptPath:   fx.PaxScriptPath,
            paxScriptVersion: "1.0.0");
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/cooks/" + ParentCookIdInterrupted + "/resume",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("cook_not_resumable",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("closure_reason_not_resumable",
                doc.RootElement.GetProperty("reason").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostResume_with_missing_checkpoint_returns_410_vanished()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedRecipeAsync(RecipeId, "Recipe");
        var cookFolder = fx.MakeCookFolder(ParentCookIdInterrupted);
        // intentionally NO checkpoint.json
        await fx.SeedInterruptedCookAsync(
            cookId:          ParentCookIdInterrupted,
            recipeId:        RecipeId,
            cookFolder:      cookFolder,
            errorClass:      CookClosureReasons.CancelStop,
            paxScriptPath:   fx.PaxScriptPath,
            paxScriptVersion: "1.0.0");
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildCBundle(reauth: reauth);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/cooks/" + ParentCookIdInterrupted + "/resume",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Gone, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("cook_resume_checkpoint_vanished",
                doc.RootElement.GetProperty("error").GetString());
            Assert.EndsWith("checkpoint.json",
                doc.RootElement.GetProperty("checkpointPath").GetString()!);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PostResume_with_deferred_spawner_returns_501_controlled_deferral()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        await fx.SeedRecipeAsync(RecipeId, "Recipe");
        var cookFolder = fx.MakeCookFolder(ParentCookIdInterrupted);
        await File.WriteAllTextAsync(Path.Combine(cookFolder, "checkpoint.json"), "{}");
        await fx.SeedInterruptedCookAsync(
            cookId:          ParentCookIdInterrupted,
            recipeId:        RecipeId,
            cookFolder:      cookFolder,
            errorClass:      CookClosureReasons.CancelStop,
            paxScriptPath:   fx.PaxScriptPath,
            paxScriptVersion: "1.0.0");
        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        // Use the PRODUCTION DeferredCookResumeSpawner to confirm
        // its envelope shape lands.
        var bundle = fx.BuildCBundle(
            reauth:        reauth,
            resumeSpawner: new DeferredCookResumeSpawner(),
            newCookId:     () => FactoryNewCookId);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(
                "/api/v1/cooks/" + ParentCookIdInterrupted + "/resume",
                new StringContent("", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("cook_resume_spawn_deferred_native_stage3i",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(ParentCookIdInterrupted,
                doc.RootElement.GetProperty("parentCookId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  Cross-stage regression
    // ============================================================

    [Fact]
    public void PaxScript_baseline_hash_unchanged()
    {
        var paxScript = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "app", "resources", "pax", "PAX_Purview_Audit_Log_Processor.ps1");
        if (!File.Exists(paxScript))
        {
            // Allow CI runs that don't bundle the app/ directory to
            // skip the tripwire -- the smoke-test surface will catch
            // a real drift.
            return;
        }
        using var stream = File.OpenRead(paxScript);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        Assert.Equal(PaxScriptBaselineHash, hash);
    }

    [Fact]
    public async Task With_null_override_routes_fall_through_to_404()
    {
        await using var fx = await Stage3iCFixture.CreateAsync();
        // NO WithStage3iCServiceOverride -- the Stage 3i-C wiring is
        // gated and must NOT register any routes when bundle is null.
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };

            // Each Stage 3i-C route family.
            string[] paths =
            {
                "/api/v1/auth/profiles",
                "/api/v1/auth/profiles/" + ExistingProfileSecret,
                "/api/v1/auth/profiles/" + ExistingProfileSecret + "/secret",
                "/api/v1/auth/profiles/" + ExistingProfileSecret + "/test",
                "/api/v1/cooks/" + CookIdActive + "/stop",
                "/api/v1/cooks/" + CookIdActive + "/kill",
                "/api/v1/cooks/" + CookIdActive + "/resume",
            };
            foreach (var path in paths)
            {
                using var resp = await http.PostAsync(path,
                    new StringContent("", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            }
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  Helpers
    // ============================================================

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return JsonDocument.Parse(bytes);
    }

    // ============================================================
    //  Fixture
    // ============================================================

    private sealed class Stage3iCFixture : IAsyncDisposable
    {
        public string Root                { get; }
        public string WorkspaceFolderPath { get; }
        public string DatabaseFilePath    { get; }
        public string AppRoot             { get; }
        public string PaxScriptPath       { get; }
        public NativeBrokerHostOptions Options { get; }

        private Stage3iCFixture(
            string root, string workspace, string database, string appRoot,
            string paxScriptPath,
            NativeBrokerHostOptions options)
        {
            Root                = root;
            WorkspaceFolderPath = workspace;
            DatabaseFilePath    = database;
            AppRoot             = appRoot;
            PaxScriptPath       = paxScriptPath;
            Options             = options;
        }

        public static async Task<Stage3iCFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3iC_" + Guid.NewGuid().ToString("N"));
            var workspace     = Path.Combine(root, "Workspace");
            var databaseDir   = Path.Combine(workspace, "Database");
            var databaseFile  = Path.Combine(databaseDir, "cookbook.sqlite");
            var recipesDir    = Path.Combine(workspace, "Recipes");
            var cooksDir      = Path.Combine(workspace, "Cooks");
            var appRoot       = Path.Combine(root, "AppRoot");
            var templatesDir  = Path.Combine(appRoot, "templates");
            var paxResDir     = Path.Combine(appRoot, "resources", "pax");
            var paxScriptPath = Path.Combine(paxResDir, "PAX_test.ps1");
            var versionPath   = Path.Combine(appRoot, "VERSION.json");

            Directory.CreateDirectory(databaseDir);
            Directory.CreateDirectory(recipesDir);
            Directory.CreateDirectory(cooksDir);
            Directory.CreateDirectory(templatesDir);
            Directory.CreateDirectory(paxResDir);

            const string fakePaxBody = "# Stage 3i-C test stand-in PAX script -- not executed.\n";
            File.WriteAllText(paxScriptPath, fakePaxBody, new UTF8Encoding(false));
            var paxSha = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(fakePaxBody)));

            File.WriteAllText(versionPath,
                "{"
                + "\"schemaVersion\":1,"
                + "\"channel\":\"stable\","
                + "\"cookbook\":{\"version\":\"1.0.0\"},"
                + "\"paxScript\":{"
                +     "\"name\":\"PAX Test\","
                +     "\"version\":\"1.0.0\","
                +     "\"relativePath\":\"resources/pax/PAX_test.ps1\","
                +     "\"sha256\":\"" + paxSha + "\"},"
                + "\"updateManifestUrl\":null"
                + "}");

            await SeedSchemaAsync(databaseFile);

            var options = new NativeBrokerHostOptions(
                PreferredPort:       17654,
                PortRangeStart:      17654,
                PortRangeEnd:        17664,
                WorkspaceFolderPath: workspace,
                AppRoot:             appRoot,
                VersionFilePath:     versionPath,
                TemplatesDir:        templatesDir,
                PaxScriptPath:       paxScriptPath);

            return new Stage3iCFixture(
                root, workspace, databaseFile, appRoot, paxScriptPath, options);
        }

        public Stage3iCServiceBundle BuildCBundle(
            FakeWindowsReAuthVerifier?  reauth          = null,
            FakeCredentialSecretStore?  creds           = null,
            FakeCertificateProbe?       certProbe       = null,
            FakeCookProcessRegistry?    cookRegistry    = null,
            ICookResumeSpawner?         resumeSpawner   = null,
            Func<string>?               newAuthProfileId = null,
            Func<string>?               newCookId       = null)
        {
            return new Stage3iCServiceBundle
            {
                ReAuth           = reauth          ?? new FakeWindowsReAuthVerifier(),
                CredStore        = creds           ?? new FakeCredentialSecretStore(),
                CertProbe        = certProbe       ?? new FakeCertificateProbe(),
                CookRegistry     = cookRegistry    ?? new FakeCookProcessRegistry(),
                ResumeSpawner    = resumeSpawner   ?? new FakeCookResumeSpawner(),
                Clock            = () => FrozenClockUtc,
                NewAuthProfileId = newAuthProfileId,
                NewCookId        = newCookId,
            };
        }

        public string MakeCookFolder(string cookId)
        {
            var folder = Path.Combine(WorkspaceFolderPath, "Cooks", cookId);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public async Task SeedAuthProfileAsync(
            string authProfileId,
            string name,
            string mode             = "AppRegistrationSecret",
            string tenantId         = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            string clientId         = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            string? credManTarget   = null,
            string? certThumbprint  = null,
            string? certStore       = null,
            string? description     = null)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseFilePath,
                Mode       = SqliteOpenMode.ReadWrite,
            }.ToString();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO auth_profiles
    (auth_profile_id, name, mode, tenant_id, client_id,
     cred_man_target, cert_thumbprint, cert_store, description,
     last_verified_at, last_verified_result,
     created_at, updated_at)
VALUES
    ($id, $name, $mode, $tenant, $client,
     $target, $thumb, $store, $desc,
     NULL, NULL,
     $created, $updated);";
            cmd.Parameters.AddWithValue("$id",      authProfileId);
            cmd.Parameters.AddWithValue("$name",    name);
            cmd.Parameters.AddWithValue("$mode",    mode);
            cmd.Parameters.AddWithValue("$tenant",  tenantId);
            cmd.Parameters.AddWithValue("$client",  clientId);
            cmd.Parameters.AddWithValue("$target",  (object?)credManTarget ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$thumb",   (object?)certThumbprint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$store",   (object?)certStore ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$desc",    (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", FrozenClockUtcIso);
            cmd.Parameters.AddWithValue("$updated", FrozenClockUtcIso);
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public AuthProfileRow? GetAuthProfile(string authProfileId)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseFilePath,
                Mode       = SqliteOpenMode.ReadOnly,
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT auth_profile_id, name, mode, tenant_id, client_id,
       cred_man_target, cert_thumbprint, cert_store, description,
       last_verified_at, last_verified_result,
       created_at, updated_at
FROM auth_profiles
WHERE auth_profile_id = $id;";
            cmd.Parameters.AddWithValue("$id", authProfileId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new AuthProfileRow(
                AuthProfileId:      r.GetString(0),
                Name:               r.GetString(1),
                Mode:               r.GetString(2),
                TenantId:           r.GetString(3),
                ClientId:           r.GetString(4),
                CredManTarget:      r.IsDBNull(5)  ? null : r.GetString(5),
                CertThumbprint:     r.IsDBNull(6)  ? null : r.GetString(6),
                CertStore:          r.IsDBNull(7)  ? null : r.GetString(7),
                Description:        r.IsDBNull(8)  ? null : r.GetString(8),
                LastVerifiedAt:     r.IsDBNull(9)  ? null : r.GetString(9),
                LastVerifiedResult: r.IsDBNull(10) ? null : r.GetString(10),
                CreatedAt:          r.GetString(11),
                UpdatedAt:          r.GetString(12));
        }

        public async Task<string> SeedRecipeAsync(string recipeId, string name)
        {
            var filePath = Path.Combine(WorkspaceFolderPath, "Recipes",
                recipeId + ".recipe.json");
            var disk = "{\"recipeId\":\"" + recipeId + "\",\"recipeSchemaVersion\":1,"
                + "\"paxAdapterVersion\":\"1.0.0\",\"identity\":{\"name\":\"" + name + "\"}}";
            File.WriteAllText(filePath, disk, new UTF8Encoding(false));

            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseFilePath,
                Mode       = SqliteOpenMode.ReadWrite,
            }.ToString();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO recipes
    (recipe_id, name, pax_adapter_version, recipe_schema_version,
     source, file_path, file_hash, status, is_pinned,
     created_at, updated_at, deleted_at)
VALUES
    ($id, $name, '1.0.0', 1, 'workspace',
     $file, 'deadbeef', 'ready', 0,
     $now, $now, NULL);";
            cmd.Parameters.AddWithValue("$id",   recipeId);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$file", filePath);
            cmd.Parameters.AddWithValue("$now",  FrozenClockUtcIso);
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
            return filePath;
        }

        public async Task SeedInterruptedCookAsync(
            string  cookId,
            string  recipeId,
            string  cookFolder,
            string  errorClass,
            string  paxScriptPath,
            string  paxScriptVersion)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = DatabaseFilePath,
                Mode       = SqliteOpenMode.ReadWrite,
            }.ToString();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO cooks
    (cook_id, recipe_id, status, exit_code, pid, cook_folder,
     pax_script_path, pax_script_version, trigger,
     started_at, finished_at, duration_seconds,
     error_class, error_message,
     created_at, updated_at, summary_path, parent_cook_id)
VALUES
    ($id, $recipeId, 'interrupted', NULL, NULL, $folder,
     $pax, $paxVersion, 'manual',
     $now, $now, 0,
     $errClass, NULL,
     $now, $now, NULL, NULL);";
            cmd.Parameters.AddWithValue("$id",         cookId);
            cmd.Parameters.AddWithValue("$recipeId",   recipeId);
            cmd.Parameters.AddWithValue("$folder",     cookFolder);
            cmd.Parameters.AddWithValue("$pax",        paxScriptPath);
            cmd.Parameters.AddWithValue("$paxVersion", paxScriptVersion);
            cmd.Parameters.AddWithValue("$errClass",   errorClass);
            cmd.Parameters.AddWithValue("$now",        FrozenClockUtcIso);
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        private static async Task SeedSchemaAsync(string databaseFile)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = databaseFile,
                Mode       = SqliteOpenMode.ReadWriteCreate,
            }.ToString();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE recipes (
    recipe_id              TEXT PRIMARY KEY,
    name                   TEXT NOT NULL,
    description            TEXT,
    tags_json              TEXT NOT NULL DEFAULT '[]',
    pax_adapter_version    TEXT NOT NULL,
    recipe_schema_version  INTEGER NOT NULL,
    source                 TEXT NOT NULL,
    source_ref             TEXT,
    file_path              TEXT NOT NULL UNIQUE,
    file_hash              TEXT NOT NULL,
    status                 TEXT NOT NULL DEFAULT 'draft',
    is_pinned              INTEGER NOT NULL DEFAULT 0,
    last_validated_at      TEXT,
    last_validation_status TEXT,
    last_cooked_at         TEXT,
    last_cook_id           TEXT,
    created_at             TEXT NOT NULL,
    updated_at             TEXT NOT NULL,
    deleted_at             TEXT
);
CREATE TABLE cooks (
    cook_id                TEXT PRIMARY KEY,
    recipe_id              TEXT,
    status                 TEXT NOT NULL,
    exit_code              INTEGER,
    pid                    INTEGER,
    cook_folder            TEXT NOT NULL,
    pax_script_path        TEXT NOT NULL,
    pax_script_version     TEXT NOT NULL,
    trigger                TEXT NOT NULL,
    started_at             TEXT,
    finished_at            TEXT,
    duration_seconds       REAL,
    error_class            TEXT,
    error_message          TEXT,
    created_at             TEXT NOT NULL,
    updated_at             TEXT NOT NULL,
    summary_path           TEXT,
    parent_cook_id         TEXT
);
CREATE TABLE auth_profiles (
    auth_profile_id        TEXT PRIMARY KEY,
    name                   TEXT NOT NULL,
    mode                   TEXT NOT NULL,
    tenant_id              TEXT NOT NULL,
    client_id              TEXT NOT NULL,
    cred_man_target        TEXT,
    cert_thumbprint        TEXT,
    cert_store             TEXT,
    description            TEXT,
    last_verified_at       TEXT,
    last_verified_result   TEXT,
    created_at             TEXT NOT NULL,
    updated_at             TEXT NOT NULL
);";
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch { /* best-effort */ }
            return ValueTask.CompletedTask;
        }
    }

    // ============================================================
    //  Fakes
    // ============================================================

    private sealed class FakeWindowsReAuthVerifier : IWindowsReAuthVerifier
    {
        public List<(string OpClass, string Message)> Calls { get; } = new();

        private readonly Queue<WindowsReAuthVerdict> _verdicts = new();

        public void Enqueue(WindowsReAuthVerdict verdict) => _verdicts.Enqueue(verdict);

        public void EnqueueVerified() => _verdicts.Enqueue(
            new WindowsReAuthVerdict("Verified", true, null));

        public Task<WindowsReAuthVerdict> VerifyAsync(
            string opClass, string message,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((opClass, message));
            if (_verdicts.Count == 0)
            {
                throw new InvalidOperationException(
                    "FakeWindowsReAuthVerifier.VerifyAsync invoked with empty queue. "
                    + "Enqueue a verdict before the request.");
            }
            return Task.FromResult(_verdicts.Dequeue());
        }
    }

    private sealed class FakeCredentialSecretStore : ICredentialSecretStore
    {
        public List<(string AuthProfileId, string Secret)> Writes { get; } = new();
        public HashSet<string> ExistingTargets { get; } = new();
        public Exception? WriteThrows { get; set; }

        public void Write(string authProfileId, string secret)
        {
            if (WriteThrows is not null) throw WriteThrows;
            Writes.Add((authProfileId, secret));
            ExistingTargets.Add(authProfileId);
        }

        public bool Exists(string authProfileId) =>
            ExistingTargets.Contains(authProfileId);

        public void Delete(string authProfileId) =>
            ExistingTargets.Remove(authProfileId);

        public string ComposeTarget(string authProfileId) =>
            "PAXCookbook.AuthProfile." + authProfileId + ".ClientSecret";
    }

    private sealed class FakeCertificateProbe : ICertificateProbe
    {
        private readonly Dictionary<(string Thumbprint, string Store), bool> _hits =
            new(StringTupleComparer.Instance);

        public void Set(string thumbprint, string store, bool hit) =>
            _hits[(thumbprint, store)] = hit;

        public bool Locate(string thumbprint, string store) =>
            _hits.TryGetValue((thumbprint, store), out var hit) && hit;

        private sealed class StringTupleComparer
            : IEqualityComparer<(string Thumbprint, string Store)>
        {
            public static readonly StringTupleComparer Instance = new();
            public bool Equals(
                (string Thumbprint, string Store) x,
                (string Thumbprint, string Store) y) =>
                StringComparer.OrdinalIgnoreCase.Equals(x.Thumbprint, y.Thumbprint)
                && StringComparer.Ordinal.Equals(x.Store, y.Store);
            public int GetHashCode((string Thumbprint, string Store) v) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(v.Thumbprint),
                    StringComparer.Ordinal.GetHashCode(v.Store));
        }
    }

    private sealed class FakeCookProcessRegistry : ICookProcessRegistry
    {
        private readonly Dictionary<string, int> _processes = new(StringComparer.Ordinal);
        public List<string> StopRequests { get; } = new();
        public List<string> KillRequests { get; } = new();

        public void Register(string cookId, int processId) =>
            _processes[cookId] = processId;

        // Stage 3j -- production interface gained Register(string,
        // CookProcessHandle) + Deregister. The fake records the
        // handle's pid in the same backing dictionary so the existing
        // TryGet/RequestStop/ForceKill tests keep observing the
        // pre-Stage-3j semantics; the handle's delegates are not
        // invoked here because the Stage 3i-C tests assert against
        // the recorded Stop/Kill request lists, not delegate calls.
        public void Register(string cookId, CookProcessHandle handle)
        {
            _processes[cookId] = handle?.ProcessId ?? 0;
        }

        public void Deregister(string cookId)
        {
            _processes.Remove(cookId);
        }

        public bool TryGet(string cookId, out int processId)
        {
            if (_processes.TryGetValue(cookId, out processId)) return true;
            processId = 0;
            return false;
        }

        public bool RequestStop(string cookId)
        {
            if (!_processes.ContainsKey(cookId)) return false;
            StopRequests.Add(cookId);
            return true;
        }

        public bool ForceKill(string cookId)
        {
            if (!_processes.ContainsKey(cookId)) return false;
            KillRequests.Add(cookId);
            return true;
        }
    }

    private sealed class FakeCookResumeSpawner : ICookResumeSpawner
    {
        public List<CookResumeSpawnRequest> SpawnRequests { get; } = new();
        public CookResumeSpawnResult Result { get; set; } =
            new CookResumeSpawnResult("spawned", null, null);

        public CookResumeSpawnResult Spawn(CookResumeSpawnRequest request)
        {
            SpawnRequests.Add(request);
            return Result;
        }
    }
}
