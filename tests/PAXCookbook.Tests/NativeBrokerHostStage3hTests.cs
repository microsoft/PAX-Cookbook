using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3h parity tests for the native broker's live scheduled-task
// PUT / DELETE routes. Each test uses an isolated
// Stage3hWorkspaceFixture (temp directory with recipes / cooks /
// scheduled_tasks / auth_profiles tables plus a <workspace>/Recipes/
// <id>.recipe.json file). The bundle is injected via
// NativeBrokerHost.WithStage3hServiceOverride so every route call
// runs through fakes only:
//
//   * FakeWindowsReAuthVerifier         -- queue of verdicts; records
//                                          (opClass, message). No
//                                          Windows Hello UI is ever
//                                          triggered.
//   * FakeRecipeProjectionHashComposer  -- canned hex digest; records
//                                          (recipeFilePath, paxScriptPath,
//                                          authProfile, executionMode,
//                                          paxScriptVersion).
//   * FakeCredentialSecretStore         -- records (authProfileId,
//                                          secret) Write calls. No
//                                          advapi32 P/Invoke ever
//                                          touches Windows Credential
//                                          Manager.
//   * FakeScheduledTaskRegistrar        -- records argv-shape
//                                          (Action, RecipeId,
//                                          ScheduledTaskId,
//                                          WorkspacePath,
//                                          RecurrenceJson). No
//                                          Register-PAXScheduledRecipe
//                                          .ps1 spawn; Task Scheduler
//                                          is never mutated.
//
// Doctrine carried over from the PowerShell broker:
//   * scheduleConfig re-auth = Windows UserConsentVerifier
//     (Hello / PIN / biometric). NOT WebAuthn. Only the verdict
//     literal "Verified" passes; any other value surfaces as a
//     401 reAuthRequired envelope with verificationResult set to
//     the raw verdict string.
//   * AppRegistrationSecret requires clientSecret on every PUT
//     (SEC-A). AppRegistrationCertificate skips the secret +
//     CredMan write entirely.
//   * PUT registrar argv NEVER carries clientSecret. The wrapper
//     reads the secret at fire-time from CredMan under target
//     "PAXCookbook.AuthProfile.<id>.ClientSecret".
//
// Tests share the "NativeBrokerHostPortBinding" xUnit collection
// with Stage 3a-3g so port-17654 binding is serialised.
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3hTests
{
    // Tripwire: this hash MUST equal the canonical app\resources\pax\
    // PAX_Purview_Audit_Log_Processor.ps1 contents. Stage 3h is a
    // BROKER-side change; the PAX script itself does not move.
    private const string PaxScriptBaselineHash =
        "1A9BC94783683AE1DA68EE6A86DE2106A96122B67B14EE20090E6687792E3878";

    // Valid Crockford-base32 ULIDs (uppercase, no I L O U). 26 chars.
    private const string SampleRecipeId         = "01HQRC7N5VRSXG8K9MZTABCDEF";
    private const string SampleRecipeIdAlt      = "01HQRC7N5VRSXG8K9MZTABCDEG";
    private const string SampleScheduledTaskId  = "01HQRC7N5VRSXG8K9MZTABZZZZ";
    private const string SampleAuthProfileId    = "01HQRC7N5VRSXG8K9MZTAPRO01";
    private const string SampleAuthProfileIdAlt = "01HQRC7N5VRSXG8K9MZTAPRO02";

    // 64-char lowercase hex digests for the fake projection hash
    // composer. Tests use distinct digests to exercise drift vs match.
    private const string FakeProjectionHashA =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string FakeProjectionHashB =
        "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private const string PaxScriptVersionA = "1.11.3";
    private const string PaxScriptVersionB = "1.11.4";

    private const string PromptPut =
        "Verify to register or update the Windows Scheduled Task for this recipe.";
    private const string PromptDelete =
        "Verify to unregister the Windows Scheduled Task for this recipe.";

    // ============================================================
    //  PUT -- per-operation Windows re-auth gating
    // ============================================================

    [Theory]
    [InlineData("Canceled",
        "Verification was canceled. Please try the operation again.")]
    [InlineData("NotConfiguredForUser",
        "Windows Hello / PIN is not configured for your account. Set it up in Windows Settings before performing this operation.")]
    [InlineData("DisabledByPolicy",
        "Windows Hello is disabled by policy on this machine. Contact your administrator.")]
    [InlineData("DeviceNotPresent",
        "No verification device is available. This appliance requires Windows Hello, PIN, or a fallback credential prompt.")]
    [InlineData("DeviceBusy",
        "The verification device is busy. Please try again in a moment.")]
    [InlineData("RetriesExhausted",
        "Too many failed verification attempts. Please wait and try again.")]
    [InlineData("ComInteropFailure",
        "Windows verification surface is unavailable. Restart the appliance and try again; if the problem persists, see the PAX Cookbook User Guide.")]
    [InlineData("Unknown",
        "Verification did not succeed. Please try the operation again.")]
    public async Task Put_returns_401_reAuthRequired_when_verdict_is_not_verified(
        string verdict, string expectedMessage)
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        var reauth    = new FakeWindowsReAuthVerifier();
        reauth.Enqueue(NonVerified(verdict));
        var hash      = new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA };
        var cred      = new FakeCredentialSecretStore();
        var registrar = new FakeScheduledTaskRegistrar();

        var bundle = fx.BuildBundle(reauth, hash, cred, registrar);

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 0 } });

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("reAuthRequired",
                doc.RootElement.GetProperty("code").GetString());
            Assert.Equal("scheduleConfig",
                doc.RootElement.GetProperty("opClass").GetString());
            Assert.Equal(verdict,
                doc.RootElement.GetProperty("verificationResult").GetString());
            Assert.Equal(expectedMessage,
                doc.RootElement.GetProperty("message").GetString());

            // The 401 short-circuits BEFORE the registrar, the hash
            // composer, or the credential store are touched.
            Assert.Empty(registrar.Calls);
            Assert.Empty(hash.Calls);
            Assert.Empty(cred.Writes);

            // The verifier saw exactly one VerifyAsync call with the
            // canonical scheduleConfig opClass and PUT prompt.
            Assert.Single(reauth.Calls);
            Assert.Equal("scheduleConfig", reauth.Calls[0].OpClass);
            Assert.Equal(PromptPut, reauth.Calls[0].Message);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_proceeds_past_reauth_gate_when_verdict_is_verified()
    {
        // A Verified verdict + missing recipe row should yield 404
        // recipe_not_found, which means the route progressed past
        // the re-auth gate. No registrar/hash/cred calls should
        // occur because the recipe lookup fails first.
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        var reauth    = new FakeWindowsReAuthVerifier();
        reauth.EnqueueVerified();
        var hash      = new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA };
        var cred      = new FakeCredentialSecretStore();
        var registrar = new FakeScheduledTaskRegistrar();

        var bundle = fx.BuildBundle(reauth, hash, cred, registrar);

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 0 } });

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_not_found",
                doc.RootElement.GetProperty("error").GetString());

            Assert.Single(reauth.Calls);
            Assert.Empty(registrar.Calls);
            Assert.Empty(hash.Calls);
            Assert.Empty(cred.Writes);
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  PUT -- recipe row / file gating
    // ============================================================

    [Fact]
    public async Task Put_returns_404_recipe_trashed_when_deleted_at_set()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Trashed recipe",
            deletedAt: "2026-05-28T08:00:00Z");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationSecret",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var hash   = new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA };
        var cred   = new FakeCredentialSecretStore();
        var registrar = new FakeScheduledTaskRegistrar();

        var bundle = fx.BuildBundle(reauth, hash, cred, registrar);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 0 } });

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_trashed",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_returns_412_recipe_invalid_when_recipe_file_missing()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "DB row only");
        // Deliberately DO NOT write the .recipe.json file.

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var hash   = new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA };
        var cred   = new FakeCredentialSecretStore();
        var registrar = new FakeScheduledTaskRegistrar();

        var bundle = fx.BuildBundle(reauth, hash, cred, registrar);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 0 } });

            Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_invalid",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("missing",
                doc.RootElement.GetProperty("detail")
                    .GetProperty("loaderStatus").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_returns_422_recipe_not_local_manual_for_other_execution_mode()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Scheduled exec mode");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            executionMode: "local-scheduled",
            authMode: "AppRegistrationSecret",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 0 } });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_not_local_manual",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("local-scheduled",
                doc.RootElement.GetProperty("executionMode").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_returns_422_recipe_auth_unsupported_when_mode_not_whitelisted()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "WebLogin recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "WebLogin",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 0 } });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_auth_unsupported",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("WebLogin",
                doc.RootElement.GetProperty("authMode").GetString());
            var allowed = doc.RootElement.GetProperty("allowed").EnumerateArray()
                .Select(e => e.GetString()).ToArray();
            Assert.Equal(new[] { "AppRegistrationSecret", "AppRegistrationCertificate" }, allowed);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_returns_422_recipe_invalid_when_recipe_missing_auth_mode()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "No auth.mode");
        // Custom recipe payload with NO auth object at all.
        var recipePath = Path.Combine(fx.WorkspaceFolderPath, "Recipes",
            SampleRecipeId + ".recipe.json");
        File.WriteAllText(recipePath, """
            {"recipeSchemaVersion":1,"name":"NoAuth","executionMode":"local-manual"}
            """, new UTF8Encoding(false));

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 0 } });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("recipe_invalid",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("recipe is missing auth.mode",
                doc.RootElement.GetProperty("detail").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_returns_422_auth_profile_missing_when_id_not_in_db()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe with unknown auth profile");
        // NB: deliberately do NOT seed any auth_profiles row.
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationSecret",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 0 } });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("auth_profile_missing",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(SampleAuthProfileId,
                doc.RootElement.GetProperty("authProfileId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  PUT -- SEC-A (clientSecret + Credential Manager)
    // ============================================================

    [Fact]
    public async Task Put_returns_422_auth_profile_secret_missing_when_secret_not_in_body()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Secret recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationSecret",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var cred   = new FakeCredentialSecretStore();
        var registrar = new FakeScheduledTaskRegistrar();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            cred,
            registrar);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // Body has valid recurrence but NO clientSecret.
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 30 } });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("auth_profile_secret_missing",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(SampleAuthProfileId,
                doc.RootElement.GetProperty("authProfileId").GetString());

            // CredMan + registrar untouched.
            Assert.Empty(cred.Writes);
            Assert.Empty(registrar.Calls);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_AppRegistrationCertificate_does_not_require_clientSecret_and_skips_credman_write()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Cert recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId,
            mode: "client_certificate_credential");
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var cred   = new FakeCredentialSecretStore();
        var hash   = new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA };
        var registrar = new FakeScheduledTaskRegistrar();

        var bundle = fx.BuildBundle(reauth, hash, cred, registrar);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // Body intentionally omits clientSecret.
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 30 } });

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            // secretRebound MUST be false for AppRegistrationCertificate.
            Assert.False(doc.RootElement.GetProperty("secretRebound").GetBoolean());

            // CredMan store is never touched for the certificate flow.
            Assert.Empty(cred.Writes);
            // Registrar IS invoked (the rest of the flow is unchanged).
            Assert.Single(registrar.Calls);
            Assert.Equal("register", registrar.Calls[0].Action);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_AppRegistrationSecret_writes_credman_with_auth_profile_id_and_exact_secret()
    {
        const string clientSecret = "T0p$ecret-Value-456!";
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Secret recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationSecret",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var cred   = new FakeCredentialSecretStore();
        var hash   = new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA };
        var registrar = new FakeScheduledTaskRegistrar();

        var bundle = fx.BuildBundle(reauth, hash, cred, registrar);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new
                {
                    recurrence   = new { kind = "daily", hour = 9, minute = 30 },
                    clientSecret = clientSecret,
                });

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // Exactly one Write recorded with verbatim (authProfileId, secret).
            Assert.Single(cred.Writes);
            Assert.Equal(SampleAuthProfileId, cred.Writes[0].AuthProfileId);
            Assert.Equal(clientSecret, cred.Writes[0].Secret);

            // The composed CredMan target name pattern (informational --
            // the production WindowsCredentialSecretStore composes the
            // exact same string from the same auth profile id).
            Assert.Equal(
                "PAXCookbook.AuthProfile." + SampleAuthProfileId + ".ClientSecret",
                cred.ComposeTarget(SampleAuthProfileId));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_returns_500_secret_write_failed_when_credstore_throws()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationSecret",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var cred   = new FakeCredentialSecretStore
        {
            WriteThrows = new InvalidOperationException("CredWriteW returned 1312"),
        };
        var registrar = new FakeScheduledTaskRegistrar();

        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            cred,
            registrar);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new
                {
                    recurrence   = new { kind = "daily", hour = 9, minute = 30 },
                    clientSecret = "x",
                });

            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("secret_write_failed",
                doc.RootElement.GetProperty("error").GetString());

            // Registrar untouched on credential failure.
            Assert.Empty(registrar.Calls);
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  PUT -- recurrence body validation (invalid_recurrence)
    // ============================================================

    [Fact]
    public async Task Put_returns_400_invalid_recurrence_when_kind_missing()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { hour = 9, minute = 0 } });

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_recurrence",
                doc.RootElement.GetProperty("error").GetString());
            var errs = doc.RootElement.GetProperty("errors").EnumerateArray()
                .Select(e => e.GetProperty("instancePath").GetString())
                .ToArray();
            Assert.Contains("/recurrence/kind", errs);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_returns_400_invalid_recurrence_when_hour_out_of_range()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 99, minute = 0 } });

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_recurrence",
                doc.RootElement.GetProperty("error").GetString());
            var hourErr = doc.RootElement.GetProperty("errors").EnumerateArray()
                .FirstOrDefault(e =>
                    e.GetProperty("instancePath").GetString() == "/recurrence/hour");
            Assert.NotEqual(default, hourErr);
            Assert.Equal("range", hourErr.GetProperty("keyword").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_returns_400_invalid_recurrence_when_weekly_missing_days_of_week()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "weekly", hour = 9, minute = 0 } });

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_recurrence",
                doc.RootElement.GetProperty("error").GetString());
            var dowErr = doc.RootElement.GetProperty("errors").EnumerateArray()
                .FirstOrDefault(e =>
                    e.GetProperty("instancePath").GetString() == "/recurrence/daysOfWeek");
            Assert.NotEqual(default, dowErr);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_returns_400_invalid_json_when_body_is_not_an_object()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var req = new HttpRequestMessage(HttpMethod.Put,
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task")
            {
                Content = new StringContent("[\"not\",\"an\",\"object\"]",
                    Encoding.UTF8, "application/json"),
            };
            using var resp = await http.SendAsync(req);

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("invalid_json",
                doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  PUT -- registrar argv shape + isolation
    // ============================================================

    [Fact]
    public async Task Put_registrar_request_carries_action_register_recipe_workspace_and_recurrence()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var registrar = new FakeScheduledTaskRegistrar();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            registrar);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 30 } });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            Assert.Single(registrar.Calls);
            var call = registrar.Calls[0];
            Assert.Equal("register", call.Action);
            Assert.Equal(SampleRecipeId, call.RecipeId);
            Assert.Matches(@"^[0-9A-HJKMNP-TV-Z]{26}$", call.ScheduledTaskId);
            Assert.Equal(fx.WorkspaceFolderPath, call.WorkspacePath);
            Assert.NotNull(call.RecurrenceJson);

            // RecurrenceJson is the normalized object, serialized.
            using var recDoc = JsonDocument.Parse(call.RecurrenceJson!);
            Assert.Equal("daily",
                recDoc.RootElement.GetProperty("kind").GetString());
            Assert.Equal(9, recDoc.RootElement.GetProperty("hour").GetInt32());
            Assert.Equal(30, recDoc.RootElement.GetProperty("minute").GetInt32());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public void Put_registrar_request_record_has_no_clientSecret_member()
    {
        // The ScheduledTaskRegistrarRequest record shape itself
        // guarantees the secret cannot ride along to the registrar.
        // This is a doctrine assertion, not a runtime branch -- it
        // catches an accidental future widening of the record.
        var members = typeof(ScheduledTaskRegistrarRequest)
            .GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("ClientSecret",  members);
        Assert.DoesNotContain("Secret",        members);
        Assert.DoesNotContain("Password",      members);
        Assert.Equal(new[] {
            "Action", "RecipeId", "ScheduledTaskId", "WorkspacePath", "RecurrenceJson",
        }, members);
    }

    [Fact]
    public async Task Put_returns_502_registrar_failed_and_skips_db_upsert_on_nonzero_exit()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var registrar = new FakeScheduledTaskRegistrar
        {
            CannedResult = new ScheduledTaskRegistrarResult(
                ExitCode: 7, Stdout: "stdout-text", Stderr: "stderr-text",
                LogPath: @"C:\logs\register.log", DurationMs: 245),
        };
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            registrar);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 30 } });

            Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("registrar_failed",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Equal(7,
                doc.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal("stdout-text",
                doc.RootElement.GetProperty("stdout").GetString());
            Assert.Equal("stderr-text",
                doc.RootElement.GetProperty("stderr").GetString());
            Assert.Equal(@"C:\logs\register.log",
                doc.RootElement.GetProperty("logPath").GetString());

            // No scheduled_tasks row was inserted because Upsert
            // never ran.
            Assert.Null(fx.ReadScheduledTaskRow(SampleRecipeId));
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  PUT -- DB upsert + response shape + ScheduledTaskId preservation
    // ============================================================

    [Fact]
    public async Task Put_success_upserts_row_and_response_carries_all_fields()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationSecret",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var hash   = new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA };
        var cred   = new FakeCredentialSecretStore();
        var registrar = new FakeScheduledTaskRegistrar
        {
            CannedResult = new ScheduledTaskRegistrarResult(
                ExitCode: 0, Stdout: "", Stderr: "",
                LogPath: null, DurationMs: 387),
        };
        var bundle = fx.BuildBundle(reauth, hash, cred, registrar);
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new
                {
                    recurrence   = new { kind = "daily", hour = 9, minute = 30 },
                    clientSecret = "secret-value",
                });

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);

            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(SampleRecipeId,
                doc.RootElement.GetProperty("recipeId").GetString());
            var scheduledTaskId =
                doc.RootElement.GetProperty("scheduledTaskId").GetString()!;
            Assert.Matches(@"^[0-9A-HJKMNP-TV-Z]{26}$", scheduledTaskId);
            Assert.Equal("PAXCookbook_" + SampleRecipeId,
                doc.RootElement.GetProperty("windowsTaskName").GetString());
            Assert.Equal(@"\PAX Cookbook\",
                doc.RootElement.GetProperty("windowsTaskPath").GetString());
            Assert.Equal(FakeProjectionHashA,
                doc.RootElement.GetProperty("recipeProjectionHash").GetString());
            Assert.Equal(PaxScriptVersionA,
                doc.RootElement.GetProperty("paxScriptVersion").GetString());
            Assert.True(doc.RootElement.GetProperty("secretRebound").GetBoolean());
            Assert.Equal(387,
                doc.RootElement.GetProperty("registrarDurationMs").GetInt32());
            // registeredAt is a real ISO-8601 timestamp.
            var registeredAt =
                doc.RootElement.GetProperty("registeredAt").GetString()!;
            Assert.True(DateTime.TryParse(registeredAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out _));

            // DB row was upserted with the same hash + version.
            var row = fx.ReadScheduledTaskRow(SampleRecipeId);
            Assert.NotNull(row);
            Assert.Equal(scheduledTaskId, row!.Value.ScheduledTaskId);
            Assert.Equal(FakeProjectionHashA, row.Value.RecipeProjectionHash);
            Assert.Equal(PaxScriptVersionA, row.Value.PaxScriptVersion);
            Assert.Equal(@"\PAX Cookbook\", row.Value.WindowsTaskPath);
            Assert.Equal("PAXCookbook_" + SampleRecipeId, row.Value.WindowsTaskName);

            // Hash composer was invoked exactly once with the
            // expected ambient state from the bundle.
            Assert.Single(hash.Calls);
            var hc = hash.Calls[0];
            Assert.Equal(
                Path.Combine(fx.WorkspaceFolderPath, "Recipes",
                    SampleRecipeId + ".recipe.json"),
                hc.RecipeFilePath);
            Assert.Equal(fx.FakePaxScriptPath,         hc.PaxScriptPath);
            Assert.Equal(SampleAuthProfileId,          hc.AuthProfile?.AuthProfileId);
            Assert.Equal("local-scheduled",            hc.ExecutionMode);
            Assert.Equal(PaxScriptVersionA,            hc.PaxScriptVersion);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_repeated_for_same_recipe_preserves_scheduledTaskId()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);

        var reauth    = new FakeWindowsReAuthVerifier();
        reauth.EnqueueVerified(); reauth.EnqueueVerified();
        var hash      = new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA };
        var cred      = new FakeCredentialSecretStore();
        var registrar = new FakeScheduledTaskRegistrar();
        var bundle = fx.BuildBundle(reauth, hash, cred, registrar);

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp1 = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 30 } });
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
            var id1 = (await ReadJsonAsync(resp1))
                .RootElement.GetProperty("scheduledTaskId").GetString();

            using var resp2 = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 10, minute = 0 } });
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
            var id2 = (await ReadJsonAsync(resp2))
                .RootElement.GetProperty("scheduledTaskId").GetString();

            Assert.Equal(id1, id2);
            // Each PUT spawned an independent registrar invocation.
            Assert.Equal(2, registrar.Calls.Count);
            Assert.Equal(id1, registrar.Calls[0].ScheduledTaskId);
            Assert.Equal(id1, registrar.Calls[1].ScheduledTaskId);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Put_returns_500_projection_failed_when_hash_composer_fails()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var hash   = new FakeRecipeProjectionHashComposer
        {
            NextResult = new RecipeProjectionHashResult(
                Ok: false, Sha256Hex: null,
                Error: "sidecar_spawn_failed: pwsh not found"),
        };
        var registrar = new FakeScheduledTaskRegistrar();
        var bundle = fx.BuildBundle(reauth, hash, new FakeCredentialSecretStore(), registrar);

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 30 } });

            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("projection_failed",
                doc.RootElement.GetProperty("error").GetString());
            Assert.Contains("sidecar_spawn_failed",
                doc.RootElement.GetProperty("detail").GetString());

            // Registrar never invoked because projection failed first.
            Assert.Empty(registrar.Calls);
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  DELETE
    // ============================================================

    [Theory]
    [InlineData("Canceled")]
    [InlineData("DeviceNotPresent")]
    [InlineData("DisabledByPolicy")]
    public async Task Delete_returns_401_reAuthRequired_when_verdict_is_not_verified(
        string verdict)
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedScheduledTaskRowAsync(
            SampleScheduledTaskId, SampleRecipeId, FakeProjectionHashA);

        var reauth = new FakeWindowsReAuthVerifier();
        reauth.Enqueue(NonVerified(verdict));
        var registrar = new FakeScheduledTaskRegistrar();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            registrar);

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task");

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("reAuthRequired",
                doc.RootElement.GetProperty("code").GetString());
            Assert.Equal(verdict,
                doc.RootElement.GetProperty("verificationResult").GetString());

            // Verifier was called with the DELETE prompt.
            Assert.Single(reauth.Calls);
            Assert.Equal("scheduleConfig", reauth.Calls[0].OpClass);
            Assert.Equal(PromptDelete,     reauth.Calls[0].Message);

            // Registrar untouched on 401.
            Assert.Empty(registrar.Calls);
            // Row preserved.
            Assert.NotNull(fx.ReadScheduledTaskRow(SampleRecipeId));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Delete_returns_404_task_not_found_when_no_scheduled_task_row()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        // Seed a recipe row but NO scheduled_tasks row.
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var registrar = new FakeScheduledTaskRegistrar();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            registrar);

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task");

            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("task_not_found",
                doc.RootElement.GetProperty("error").GetString());

            Assert.Empty(registrar.Calls);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Delete_success_invokes_unregister_and_removes_db_row()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedScheduledTaskRowAsync(
            SampleScheduledTaskId, SampleRecipeId, FakeProjectionHashA);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var registrar = new FakeScheduledTaskRegistrar
        {
            CannedResult = new ScheduledTaskRegistrarResult(
                ExitCode: 0, Stdout: "", Stderr: "",
                LogPath: null, DurationMs: 174),
        };
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            registrar);

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(SampleRecipeId,
                doc.RootElement.GetProperty("recipeId").GetString());
            Assert.Equal(SampleScheduledTaskId,
                doc.RootElement.GetProperty("scheduledTaskId").GetString());
            Assert.Equal(174,
                doc.RootElement.GetProperty("registrarDurationMs").GetInt32());

            // Registrar argv shape: Action=unregister, RecurrenceJson=null.
            Assert.Single(registrar.Calls);
            var call = registrar.Calls[0];
            Assert.Equal("unregister", call.Action);
            Assert.Equal(SampleRecipeId, call.RecipeId);
            Assert.Equal(SampleScheduledTaskId, call.ScheduledTaskId);
            Assert.Equal(fx.WorkspaceFolderPath, call.WorkspacePath);
            Assert.Null(call.RecurrenceJson);

            // DB row removed.
            Assert.Null(fx.ReadScheduledTaskRow(SampleRecipeId));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Delete_registrar_failure_returns_502_and_preserves_db_row()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedScheduledTaskRowAsync(
            SampleScheduledTaskId, SampleRecipeId, FakeProjectionHashA);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var registrar = new FakeScheduledTaskRegistrar
        {
            CannedResult = new ScheduledTaskRegistrarResult(
                ExitCode: 9, Stdout: "out", Stderr: "err",
                LogPath: null, DurationMs: 12),
        };
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            registrar);

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task");

            Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("registrar_failed",
                doc.RootElement.GetProperty("error").GetString());
            // Row preserved because DB delete was skipped.
            Assert.NotNull(fx.ReadScheduledTaskRow(SampleRecipeId));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Delete_does_not_broad_delete_other_scheduled_task_rows()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId,    "Recipe A");
        await fx.SeedRecipeRowAsync(SampleRecipeIdAlt, "Recipe B");
        await fx.SeedScheduledTaskRowAsync(
            SampleScheduledTaskId, SampleRecipeId, FakeProjectionHashA);
        await fx.SeedScheduledTaskRowAsync(
            "01HQRC7N5VRSXG8K9MZTABYYYY", SampleRecipeIdAlt, FakeProjectionHashB);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var registrar = new FakeScheduledTaskRegistrar();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            registrar);

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // Targeted row gone.
            Assert.Null(fx.ReadScheduledTaskRow(SampleRecipeId));
            // Sibling row survives intact.
            var sibling = fx.ReadScheduledTaskRow(SampleRecipeIdAlt);
            Assert.NotNull(sibling);
            Assert.Equal(FakeProjectionHashB, sibling!.Value.RecipeProjectionHash);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Delete_succeeds_even_when_recipe_row_is_trashed()
    {
        // Parity with PS broker -- the DELETE deliberately allows
        // trashed-recipe cleanup; the 404 fires only on missing
        // scheduled_tasks row, never on missing/trashed recipe row.
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Trashed",
            deletedAt: "2026-05-29T08:00:00Z");
        await fx.SeedScheduledTaskRowAsync(
            SampleScheduledTaskId, SampleRecipeId, FakeProjectionHashA);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var registrar = new FakeScheduledTaskRegistrar();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            registrar);

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.DeleteAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Null(fx.ReadScheduledTaskRow(SampleRecipeId));
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  GET single -- health composition with Stage 3h bundle attached
    // ============================================================

    [Fact]
    public async Task Get_after_put_reports_registered_true_with_matching_hash_and_no_stale_reason()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier(); reauth.EnqueueVerified();
        var hash   = new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA };
        var bundle = fx.BuildBundle(
            reauth, hash,
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var putResp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 0 } });
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

            using var getResp = await http.GetAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task");
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

            var doc = await ReadJsonAsync(getResp);
            Assert.True(doc.RootElement.GetProperty("registered").GetBoolean());
            var task = doc.RootElement.GetProperty("scheduledTask");
            Assert.Equal(FakeProjectionHashA,
                task.GetProperty("recipeProjectionHash").GetString());

            // staleReason is null when current hash matches registered hash.
            Assert.Equal(JsonValueKind.Null,
                doc.RootElement.GetProperty("staleReason").ValueKind);

            // health.projectionHashCurrent is the composer's reply.
            var health = doc.RootElement.GetProperty("health");
            Assert.Equal(FakeProjectionHashA,
                health.GetProperty("projectionHashCurrent").GetString());
            Assert.Equal(FakeProjectionHashA,
                health.GetProperty("projectionHashRegistered").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Get_after_delete_reports_registered_false()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);

        var reauth = new FakeWindowsReAuthVerifier();
        // Allow PUT, GET (GET does NOT call re-auth), DELETE.
        reauth.EnqueueVerified(); reauth.EnqueueVerified();
        var bundle = fx.BuildBundle(
            reauth,
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var putResp = await http.PutAsJsonAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task",
                new { recurrence = new { kind = "daily", hour = 9, minute = 0 } });
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

            using var delResp = await http.DeleteAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task");
            Assert.Equal(HttpStatusCode.OK, delResp.StatusCode);

            using var getResp = await http.GetAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task");
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
            var doc = await ReadJsonAsync(getResp);
            Assert.False(doc.RootElement.GetProperty("registered").GetBoolean());
            Assert.Equal(JsonValueKind.Null,
                doc.RootElement.GetProperty("scheduledTask").ValueKind);

            // With Stage 3h bundle attached, staleReason on the
            // unregistered-recipe path is null (NOT the deferred
            // sentinel).
            Assert.Equal(JsonValueKind.Null,
                doc.RootElement.GetProperty("staleReason").ValueKind);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Get_reports_stale_projection_changed_when_current_hash_differs()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);
        // Pre-seed a scheduled_tasks row registered with hash A.
        await fx.SeedScheduledTaskRowAsync(
            SampleScheduledTaskId, SampleRecipeId, FakeProjectionHashA,
            paxScriptVersion: PaxScriptVersionA);

        // Composer reports hash B on GET -> drift -> projection_changed.
        var hash = new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashB };
        var bundle = fx.BuildBundle(
            new FakeWindowsReAuthVerifier(), hash,
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.True(doc.RootElement.GetProperty("registered").GetBoolean());
            Assert.Equal("projection_changed",
                doc.RootElement.GetProperty("staleReason").GetString());
            Assert.Equal(FakeProjectionHashB,
                doc.RootElement.GetProperty("health")
                    .GetProperty("projectionHashCurrent").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Get_reports_stale_pax_version_changed_when_versions_differ()
    {
        await using var fx = await Stage3hWorkspaceFixture
            .CreateAsync(paxScriptVersion: PaxScriptVersionB);
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);
        // Row registered with version A; bundle says version B; hash matches.
        await fx.SeedScheduledTaskRowAsync(
            SampleScheduledTaskId, SampleRecipeId, FakeProjectionHashA,
            paxScriptVersion: PaxScriptVersionA);

        var bundle = fx.BuildBundle(
            new FakeWindowsReAuthVerifier(),
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("pax_version_changed",
                doc.RootElement.GetProperty("staleReason").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Get_reports_projection_hash_recompute_failed_when_composer_errors()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedAuthProfileAsync(SampleAuthProfileId);
        fx.WriteRecipeFile(SampleRecipeId,
            authMode: "AppRegistrationCertificate",
            authProfileId: SampleAuthProfileId);
        await fx.SeedScheduledTaskRowAsync(
            SampleScheduledTaskId, SampleRecipeId, FakeProjectionHashA);

        var hash = new FakeRecipeProjectionHashComposer
        {
            NextResult = new RecipeProjectionHashResult(
                Ok: false, Sha256Hex: null,
                Error: "sidecar_timeout: 30000ms"),
        };
        var bundle = fx.BuildBundle(
            new FakeWindowsReAuthVerifier(), hash,
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());

        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync(
                "/api/v1/recipes/" + SampleRecipeId + "/scheduled-task");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("projection_hash_recompute_failed",
                doc.RootElement.GetProperty("staleReason").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  Isolation guarantees
    // ============================================================

    [Fact]
    public void Stage3h_bundle_uses_fakes_only_in_tests()
    {
        // Type-identity tripwire: under the test fixture the bundle
        // is ALWAYS composed from the four test-only fake types and
        // NEVER from the production sidecar/advapi32 types. This
        // catches accidental wiring of WindowsReAuthSidecarVerifier
        // / RecipeProjectionHashSidecarComposer /
        // WindowsCredentialSecretStore /
        // WindowsScheduledTaskRegistrar into a test bundle.
        var fakeReauth    = new FakeWindowsReAuthVerifier();
        var fakeHash      = new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA };
        var fakeCred      = new FakeCredentialSecretStore();
        var fakeRegistrar = new FakeScheduledTaskRegistrar();
        Assert.IsType<FakeWindowsReAuthVerifier>(fakeReauth);
        Assert.IsType<FakeRecipeProjectionHashComposer>(fakeHash);
        Assert.IsType<FakeCredentialSecretStore>(fakeCred);
        Assert.IsType<FakeScheduledTaskRegistrar>(fakeRegistrar);
        // Negative checks -- production types must NOT match the
        // fake instance.
        Assert.IsNotType<WindowsReAuthSidecarVerifier>(fakeReauth);
        Assert.IsNotType<RecipeProjectionHashSidecarComposer>(fakeHash);
        Assert.IsNotType<WindowsCredentialSecretStore>(fakeCred);
    }

    [Fact]
    public void WindowsReAuthSidecarVerifier_BuildSidecarScript_is_bounded_and_escapes_single_quotes()
    {
        // Single-quote-bearing message must be doubled inside the
        // PS single-quoted literal so the sidecar script is still
        // valid PowerShell. Path is also escaped.
        var script = WindowsReAuthSidecarVerifier.BuildSidecarScript(
            windowsReAuthScriptPath: @"C:\It's\Auth\WindowsReAuth.ps1",
            message:                 "Verify it's safe",
            timeoutMs:               12345);

        Assert.Contains("$ErrorActionPreference='Stop';", script);
        // Path quote doubled: C:\It''s\Auth\WindowsReAuth.ps1
        Assert.Contains(". 'C:\\It''s\\Auth\\WindowsReAuth.ps1'", script);
        // Message quote doubled: 'Verify it''s safe'
        Assert.Contains("Invoke-WindowsReAuth -Message 'Verify it''s safe'",
            script);
        // Timeout literal preserved.
        Assert.Contains("-TimeoutMs 12345", script);
        // Verdict + failureDetail emission shape.
        Assert.Contains("ConvertTo-Json -Compress", script);
        Assert.Contains("Get-WindowsReAuthLastFailureDetail", script);
    }

    [Fact]
    public void PAX_baseline_hash_unchanged_by_stage3h()
    {
        var repoRoot   = ResolveRepoRoot();
        var paxScript  = Path.Combine(repoRoot, "app", "resources", "pax",
            "PAX_Purview_Audit_Log_Processor.ps1");
        if (!File.Exists(paxScript))
        {
            // Don't fail on machines that only have the test
            // project checked out without the rest of the tree.
            return;
        }
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paxScript)));
        Assert.Equal(PaxScriptBaselineHash, hash);
    }

    // ============================================================
    //  Cross-stage regression -- Stage 3c reads keep working with
    //  the Stage 3h bundle attached.
    // ============================================================

    [Fact]
    public async Task Stage3c_recipes_list_endpoint_still_serves_with_stage3h_bundle_attached()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe A");
        await fx.SeedRecipeRowAsync(SampleRecipeIdAlt, "Recipe B");

        var bundle = fx.BuildBundle(
            new FakeWindowsReAuthVerifier(),
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/recipes");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var recipes = doc.RootElement.GetProperty("recipes");
            Assert.Equal(JsonValueKind.Array, recipes.ValueKind);
            Assert.Equal(2, recipes.GetArrayLength());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Stage3g_scheduled_tasks_list_endpoint_still_serves_with_stage3h_bundle_attached()
    {
        await using var fx = await Stage3hWorkspaceFixture.CreateAsync();
        await fx.SeedRecipeRowAsync(SampleRecipeId, "Recipe");
        await fx.SeedScheduledTaskRowAsync(
            SampleScheduledTaskId, SampleRecipeId, FakeProjectionHashA);

        var bundle = fx.BuildBundle(
            new FakeWindowsReAuthVerifier(),
            new FakeRecipeProjectionHashComposer { NextHash = FakeProjectionHashA },
            new FakeCredentialSecretStore(),
            new FakeScheduledTaskRegistrar());
        await using var host = new NativeBrokerHost(fx.Options)
            .WithStage3hServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/scheduled-tasks");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var arr = doc.RootElement.GetProperty("scheduledTasks");
            Assert.Equal(1, arr.GetArrayLength());
            Assert.Equal(SampleScheduledTaskId,
                arr[0].GetProperty("scheduledTaskId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ============================================================
    //  Helpers
    // ============================================================

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }

    private static WindowsReAuthVerdict NonVerified(string verdict) =>
        new(Result: verdict, IsVerified: false,
            FailureDetail: verdict == "ComInteropFailure"
                ? "HRESULT 0x80004005: E_FAIL" : null);

    private static string ResolveRepoRoot()
    {
        // Walk up from the test bin/ directory until we find a
        // directory containing app/resources/pax. The
        // PAXCookbook.Tests assembly lives at
        // tests/PAXCookbook.Tests/bin/<Cfg>/<Tfm>/, so the repo
        // root is normally 4 levels up.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var probe = Path.Combine(dir.FullName, "app", "resources", "pax",
                "PAX_Purview_Audit_Log_Processor.ps1");
            if (File.Exists(probe)) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    // ============================================================
    //  Fixtures + Fakes
    // ============================================================

    private sealed class Stage3hWorkspaceFixture : IAsyncDisposable
    {
        public string Root                  { get; }
        public string WorkspaceFolderPath   { get; }
        public string DatabaseFilePath      { get; }
        public string FakePaxScriptPath     { get; }
        public string PaxScriptVersion      { get; }
        public NativeBrokerHostOptions Options { get; }

        private Stage3hWorkspaceFixture(
            string root, string workspace, string database,
            string fakePaxScriptPath, string paxScriptVersion,
            NativeBrokerHostOptions options)
        {
            Root                = root;
            WorkspaceFolderPath = workspace;
            DatabaseFilePath    = database;
            FakePaxScriptPath   = fakePaxScriptPath;
            PaxScriptVersion    = paxScriptVersion;
            Options             = options;
        }

        public static async Task<Stage3hWorkspaceFixture> CreateAsync(
            string paxScriptVersion = "1.11.3")
        {
            var root = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3h_" + Guid.NewGuid().ToString("N"));
            var workspace   = Path.Combine(root, "Workspace");
            var databaseDir = Path.Combine(workspace, "Database");
            var databaseFile = Path.Combine(databaseDir, "cookbook.sqlite");
            var recipesDir  = Path.Combine(workspace, "Recipes");
            Directory.CreateDirectory(databaseDir);
            Directory.CreateDirectory(recipesDir);

            await SeedSchemaAsync(databaseFile);

            // Stand-in PAX script path -- the fake hash composer does
            // not invoke it; the route only ever passes the path string
            // to the composer.
            var fakePaxScriptPath = Path.Combine(root, "FakePax.ps1");
            File.WriteAllText(fakePaxScriptPath,
                "# stage 3h test stand-in -- not executed\n");

            var options = new NativeBrokerHostOptions(
                PreferredPort:       17654,
                PortRangeStart:      17654,
                PortRangeEnd:        17664,
                WorkspaceFolderPath: workspace);

            return new Stage3hWorkspaceFixture(
                root, workspace, databaseFile,
                fakePaxScriptPath, paxScriptVersion, options);
        }

        public Stage3hServiceBundle BuildBundle(
            IWindowsReAuthVerifier reauth,
            IRecipeProjectionHashComposer hashComposer,
            ICredentialSecretStore credStore,
            IScheduledTaskRegistrar registrar)
        {
            var paths  = WorkspacePathResolver.Resolve(WorkspaceFolderPath)!;
            var reader = new RecipeFileReader(paths);
            return new Stage3hServiceBundle
            {
                ReAuth           = reauth,
                HashComposer     = hashComposer,
                CredStore        = credStore,
                Registrar        = registrar,
                RecipeReader     = reader,
                PaxScriptPath    = FakePaxScriptPath,
                PaxScriptVersion = PaxScriptVersion,
                WorkspacePath    = WorkspaceFolderPath,
                // Defaults from production:
                //   ScheduledTaskFolderPath = @"\PAX Cookbook\"
                //   ExecutionMode           = "local-scheduled"
                //   RegisteredByUser        = <DOMAIN>\<user>
            };
        }

        public async Task SeedRecipeRowAsync(
            string recipeId,
            string name,
            string? deletedAt = null)
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
INSERT INTO recipes (recipe_id, name, pax_adapter_version, recipe_schema_version,
                     source, file_path, file_hash, status, is_pinned,
                     created_at, updated_at, deleted_at)
VALUES ($id, $name, '1.0.0', 1, 'workspace',
        $file, 'hash', 'active', 0,
        '2026-05-27T08:00:00Z', '2026-05-27T08:00:00Z', $deleted);";
            cmd.Parameters.AddWithValue("$id",   recipeId);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$file",
                Path.Combine("Recipes", recipeId + ".recipe.json"));
            cmd.Parameters.AddWithValue("$deleted",
                (object?)deletedAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public async Task SeedAuthProfileAsync(
            string authProfileId,
            string mode = "client_secret_credential")
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
INSERT INTO auth_profiles (auth_profile_id, name, mode, tenant_id, client_id,
                           cred_man_target, cert_thumbprint, cert_store,
                           description, last_verified_at, last_verified_result,
                           created_at, updated_at)
VALUES ($id, $name, $mode, 'tenant-x', 'client-x',
        $target, NULL, NULL,
        NULL, NULL, NULL,
        '2026-05-27T08:00:00Z', '2026-05-27T08:00:00Z');";
            cmd.Parameters.AddWithValue("$id",     authProfileId);
            cmd.Parameters.AddWithValue("$name",   "stage3h auth profile");
            cmd.Parameters.AddWithValue("$mode",   mode);
            cmd.Parameters.AddWithValue("$target",
                "PAXCookbook.AuthProfile." + authProfileId + ".ClientSecret");
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public async Task SeedScheduledTaskRowAsync(
            string scheduledTaskId,
            string recipeId,
            string projectionHash,
            string paxScriptVersion = "1.11.3",
            string registeredAt    = "2026-05-27T08:00:00Z")
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
INSERT INTO scheduled_tasks (
    scheduled_task_id, recipe_id, windows_task_name, windows_task_path,
    recipe_projection_hash, pax_script_version,
    registered_at, registered_by_user, status, created_at, updated_at)
VALUES ($stid, $rid, $tname, '\PAX Cookbook\',
        $hash, $ver,
        $reg, 'TEST\User', 'active', $reg, $reg);";
            cmd.Parameters.AddWithValue("$stid",  scheduledTaskId);
            cmd.Parameters.AddWithValue("$rid",   recipeId);
            cmd.Parameters.AddWithValue("$tname", "PAXCookbook_" + recipeId);
            cmd.Parameters.AddWithValue("$hash",  projectionHash);
            cmd.Parameters.AddWithValue("$ver",   paxScriptVersion);
            cmd.Parameters.AddWithValue("$reg",   registeredAt);
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }

        public void WriteRecipeFile(
            string recipeId,
            string executionMode = "local-manual",
            string authMode      = "AppRegistrationCertificate",
            string? authProfileId = null)
        {
            var path = Path.Combine(WorkspaceFolderPath, "Recipes",
                recipeId + ".recipe.json");
            object recipe;
            if (authProfileId is null)
            {
                recipe = new
                {
                    recipeSchemaVersion = 1,
                    name                = "Stage 3h recipe",
                    executionMode,
                    auth                = new { mode = authMode },
                };
            }
            else
            {
                recipe = new
                {
                    recipeSchemaVersion = 1,
                    name                = "Stage 3h recipe",
                    executionMode,
                    auth                = new { mode = authMode, authProfileId },
                };
            }
            File.WriteAllText(path,
                JsonSerializer.Serialize(recipe,
                    new JsonSerializerOptions { WriteIndented = false }),
                new UTF8Encoding(false));
        }

        public (string ScheduledTaskId, string RecipeProjectionHash,
                string PaxScriptVersion, string WindowsTaskName,
                string WindowsTaskPath)? ReadScheduledTaskRow(string recipeId)
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
SELECT scheduled_task_id, recipe_projection_hash, pax_script_version,
       windows_task_name, windows_task_path
FROM scheduled_tasks WHERE recipe_id = $rid";
            cmd.Parameters.AddWithValue("$rid", recipeId);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read()) return null;
            return (
                rdr.GetString(0),
                rdr.GetString(1),
                rdr.GetString(2),
                rdr.GetString(3),
                rdr.GetString(4));
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
    recipe_version_id      TEXT,
    recipe_snapshot_json   TEXT,
    command_argv_json      TEXT,
    command_argv_redacted  TEXT,
    pax_script_path        TEXT,
    pax_script_version     TEXT,
    trigger                TEXT NOT NULL,
    schedule_id            TEXT,
    parent_cook_id         TEXT,
    cook_folder            TEXT,
    pid                    INTEGER,
    status                 TEXT NOT NULL,
    exit_code              INTEGER,
    started_at             TEXT,
    finished_at            TEXT,
    duration_seconds       REAL,
    error_class            TEXT,
    error_message          TEXT,
    summary_path           TEXT,
    created_at             TEXT NOT NULL,
    updated_at             TEXT NOT NULL
);
CREATE TABLE scheduled_tasks (
    scheduled_task_id        TEXT PRIMARY KEY,
    recipe_id                TEXT NOT NULL UNIQUE,
    windows_task_name        TEXT NOT NULL,
    windows_task_path        TEXT NOT NULL DEFAULT '\PAX Cookbook\',
    recipe_projection_hash   TEXT NOT NULL,
    pax_script_version       TEXT NOT NULL,
    registered_at            TEXT NOT NULL,
    registered_by_user       TEXT NOT NULL,
    last_imported_cook_id    TEXT,
    last_imported_at         TEXT,
    last_stale_check_at      TEXT,
    status                   TEXT NOT NULL DEFAULT 'active',
    created_at               TEXT NOT NULL,
    updated_at               TEXT NOT NULL
);
CREATE TABLE auth_profiles (
    auth_profile_id        TEXT PRIMARY KEY,
    name                   TEXT NOT NULL,
    mode                   TEXT NOT NULL,
    tenant_id              TEXT,
    client_id              TEXT,
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
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup -- temp dir survives even if a
                // file handle is still open. OS will GC it eventually.
            }
            return ValueTask.CompletedTask;
        }
    }

    // ----- Fakes -----------------------------------------------------

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
                // Default-deny: any test path that triggers VerifyAsync
                // without enqueuing a verdict should fail loudly.
                throw new InvalidOperationException(
                    "FakeWindowsReAuthVerifier.VerifyAsync invoked with empty queue. "
                    + "Enqueue a verdict before the request.");
            }
            return Task.FromResult(_verdicts.Dequeue());
        }
    }

    private sealed class FakeRecipeProjectionHashComposer : IRecipeProjectionHashComposer
    {
        public string? NextHash { get; set; }
        public RecipeProjectionHashResult? NextResult { get; set; }

        public List<HashComposerCall> Calls { get; } = new();

        public Task<RecipeProjectionHashResult> ComposeAsync(
            string recipeFilePath,
            string paxScriptPath,
            AuthProfileRow? authProfile,
            string executionMode,
            string paxScriptVersion,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new HashComposerCall(
                recipeFilePath, paxScriptPath, authProfile,
                executionMode, paxScriptVersion));

            if (NextResult is not null) return Task.FromResult(NextResult);
            if (NextHash is not null)
            {
                return Task.FromResult(new RecipeProjectionHashResult(
                    Ok: true, Sha256Hex: NextHash, Error: null));
            }
            return Task.FromResult(new RecipeProjectionHashResult(
                Ok: false, Sha256Hex: null,
                Error: "FakeRecipeProjectionHashComposer: no NextHash or NextResult configured"));
        }
    }

    private sealed record HashComposerCall(
        string RecipeFilePath,
        string PaxScriptPath,
        AuthProfileRow? AuthProfile,
        string ExecutionMode,
        string PaxScriptVersion);

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

    private sealed class FakeScheduledTaskRegistrar : IScheduledTaskRegistrar
    {
        public List<ScheduledTaskRegistrarRequest> Calls { get; } = new();
        public ScheduledTaskRegistrarResult CannedResult { get; set; } =
            new ScheduledTaskRegistrarResult(
                ExitCode: 0, Stdout: "", Stderr: "",
                LogPath: null, DurationMs: 0);

        public Task<ScheduledTaskRegistrarResult> InvokeAsync(
            ScheduledTaskRegistrarRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return Task.FromResult(CannedResult);
        }
    }
}
