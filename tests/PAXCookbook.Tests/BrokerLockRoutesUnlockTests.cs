using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;
using Xunit;

namespace PAXCookbook.Tests;

// §4AA -- BrokerLockRoutes.Register is now seam-aware. When the host
// is composed with a Stage 3i-C bundle whose ReAuth seam is a fake
// verifier, the /api/v1/broker/unlock route drives the verifier
// instead of returning 501, and the response envelope discriminates
// Verified vs Canceled vs DeviceNotPresent vs verification-failed
// with a stable reason vocabulary the SPA's onPrimaryClick consumes.
//
// These tests NEVER hit the real WinRT UserConsentVerifier; the fake
// verifier dequeues a canned verdict per call. The PowerShell side
// of WindowsReAuthSidecarVerifier is not touched.
[Collection("NativeBrokerHostPortBinding")]
public class BrokerLockRoutesUnlockTests
{
    private static readonly DateTimeOffset FrozenClockUtc =
        new(2026, 5, 28, 18, 51, 0, TimeSpan.Zero);

    // -----------------------------------------------------------------
    // 1. Route no longer returns 501 when verifier is wired.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Unlock_post_does_not_return_501_when_verifier_wired()
    {
        await using var fx = await UnlockFixture.CreateAsync();
        var fake   = new FakeWindowsReAuthVerifier(); fake.EnqueueVerified();
        var bundle = UnlockFixture.BuildBundle(fake);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/broker/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.NotEqual(HttpStatusCode.NotImplemented, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    // -----------------------------------------------------------------
    // 2. Verified verdict -> 200 success envelope.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Verified_verdict_returns_200_success_envelope()
    {
        await using var fx = await UnlockFixture.CreateAsync();
        var fake   = new FakeWindowsReAuthVerifier(); fake.EnqueueVerified();
        var bundle = UnlockFixture.BuildBundle(fake);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/broker/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
            var root = doc.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.True(root.GetProperty("unlocked").GetBoolean());
            Assert.Equal("windows-reauth", root.GetProperty("method").GetString());
            Assert.Equal("Verified", root.GetProperty("verificationResult").GetString());
            Assert.Equal("Unlocked", root.GetProperty("state").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // -----------------------------------------------------------------
    // 3. Verified verdict transitions lock state to Unlocked.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Verified_verdict_transitions_lock_state_to_Unlocked()
    {
        await using var fx = await UnlockFixture.CreateAsync(enforceBrokerLock: true);
        var fake   = new FakeWindowsReAuthVerifier(); fake.EnqueueVerified();
        var bundle = UnlockFixture.BuildBundle(fake);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };

            using (var pre = await http.GetAsync("/api/v1/broker/lock-state"))
            {
                var d = JsonDocument.Parse(await pre.Content.ReadAsByteArrayAsync());
                Assert.Equal("Locked", d.RootElement.GetProperty("state").GetString());
            }

            using (var unlock = await http.PostAsync("/api/v1/broker/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json")))
            {
                Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
            }

            using (var post = await http.GetAsync("/api/v1/broker/lock-state"))
            {
                var d = JsonDocument.Parse(await post.Content.ReadAsByteArrayAsync());
                Assert.Equal("Unlocked", d.RootElement.GetProperty("state").GetString());
            }
        }
        finally { await host.StopAsync(); }
    }

    // -----------------------------------------------------------------
    // 4. Canceled verdict -> 401 envelope with reason=canceled.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Canceled_verdict_returns_401_canceled_envelope()
    {
        await using var fx = await UnlockFixture.CreateAsync();
        var fake   = new FakeWindowsReAuthVerifier();
        fake.Enqueue(new WindowsReAuthVerdict("Canceled", false, "user-canceled"));
        var bundle = UnlockFixture.BuildBundle(fake);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/broker/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
            var root = doc.RootElement;
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.False(root.GetProperty("unlocked").GetBoolean());
            Assert.Equal("canceled", root.GetProperty("reason").GetString());
            Assert.Equal("Canceled", root.GetProperty("verificationResult").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
            // Failure detail must NOT leak into the response body.
            Assert.False(root.TryGetProperty("failureDetail", out _));
            Assert.False(root.TryGetProperty("detail",        out _));
        }
        finally { await host.StopAsync(); }
    }

    // -----------------------------------------------------------------
    // 5. Canceled verdict leaves lock state Locked.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Canceled_verdict_leaves_lock_state_Locked()
    {
        await using var fx = await UnlockFixture.CreateAsync(enforceBrokerLock: true);
        var fake   = new FakeWindowsReAuthVerifier();
        fake.Enqueue(new WindowsReAuthVerdict("Canceled", false, null));
        var bundle = UnlockFixture.BuildBundle(fake);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            await http.PostAsync("/api/v1/broker/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            using var resp = await http.GetAsync("/api/v1/broker/lock-state");
            var d = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
            Assert.Equal("Locked", d.RootElement.GetProperty("state").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // -----------------------------------------------------------------
    // 6. DeviceNotPresent verdict -> 503 envelope.
    // -----------------------------------------------------------------

    [Fact]
    public async Task DeviceNotPresent_verdict_returns_503_device_not_present_envelope()
    {
        await using var fx = await UnlockFixture.CreateAsync();
        var fake   = new FakeWindowsReAuthVerifier();
        fake.Enqueue(new WindowsReAuthVerdict("DeviceNotPresent", false, null));
        var bundle = UnlockFixture.BuildBundle(fake);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/broker/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
            var root = doc.RootElement;
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.False(root.GetProperty("unlocked").GetBoolean());
            Assert.Equal("device-not-present", root.GetProperty("reason").GetString());
            Assert.Equal("DeviceNotPresent", root.GetProperty("verificationResult").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
        }
        finally { await host.StopAsync(); }
    }

    // -----------------------------------------------------------------
    // 7. Verifier exception -> 500 verification-failed; state unchanged.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Exception_in_VerifyAsync_returns_500_verification_failed_and_keeps_state_Locked()
    {
        await using var fx = await UnlockFixture.CreateAsync(enforceBrokerLock: true);
        var fake   = new ThrowingFakeVerifier();
        var bundle = UnlockFixture.BuildBundle(fake);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/broker/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync());
            var root = doc.RootElement;
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.False(root.GetProperty("unlocked").GetBoolean());
            Assert.Equal("verification-failed", root.GetProperty("reason").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));

            using var probe = await http.GetAsync("/api/v1/broker/lock-state");
            var d = JsonDocument.Parse(await probe.Content.ReadAsByteArrayAsync());
            Assert.Equal("Locked", d.RootElement.GetProperty("state").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // -----------------------------------------------------------------
    // 8. Route is reachable while locked (allowlist contract).
    // -----------------------------------------------------------------

    [Fact]
    public async Task Unlock_route_is_reachable_while_locked()
    {
        await using var fx = await UnlockFixture.CreateAsync(enforceBrokerLock: true);
        var fake   = new FakeWindowsReAuthVerifier();
        fake.Enqueue(new WindowsReAuthVerdict("Canceled", false, null));
        var bundle = UnlockFixture.BuildBundle(fake);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/broker/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            // The lock middleware would return 423 if the route were
            // blocked. A Canceled verdict surfaces as 401, proving the
            // request reached the handler.
            Assert.NotEqual((HttpStatusCode)423, resp.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Single(fake.Calls);
        }
        finally { await host.StopAsync(); }
    }

    // -----------------------------------------------------------------
    // 9. Verifier is invoked with the expected opClass and message.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Verifier_invoked_with_BrokerUnlock_opclass_and_authenticate_message()
    {
        await using var fx = await UnlockFixture.CreateAsync();
        var fake   = new FakeWindowsReAuthVerifier(); fake.EnqueueVerified();
        var bundle = UnlockFixture.BuildBundle(fake);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/broker/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Single(fake.Calls);
            Assert.Equal("BrokerUnlock", fake.Calls[0].OpClass);
            Assert.Equal("Authenticate to unlock PAX Cookbook.", fake.Calls[0].Message);
        }
        finally { await host.StopAsync(); }
    }

    // -----------------------------------------------------------------
    // 10. No real pwsh process is spawned by the route (fake verifier).
    // -----------------------------------------------------------------

    [Fact]
    public async Task Unlock_route_does_not_spawn_pwsh_when_fake_verifier_used()
    {
        var pwshBefore = System.Diagnostics.Process.GetProcessesByName("pwsh").Length
                       + System.Diagnostics.Process.GetProcessesByName("powershell").Length;

        await using var fx = await UnlockFixture.CreateAsync();
        var fake   = new FakeWindowsReAuthVerifier(); fake.EnqueueVerified();
        var bundle = UnlockFixture.BuildBundle(fake);
        await using var host = new NativeBrokerHost(fx.Options).WithStage3iCServiceOverride(bundle);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            await http.PostAsync("/api/v1/broker/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
        }
        finally { await host.StopAsync(); }

        var pwshAfter = System.Diagnostics.Process.GetProcessesByName("pwsh").Length
                      + System.Diagnostics.Process.GetProcessesByName("powershell").Length;
        Assert.Equal(pwshBefore, pwshAfter);
    }

    // =================================================================
    // Fakes -- private to this file
    // =================================================================

    private sealed class FakeWindowsReAuthVerifier : IWindowsReAuthVerifier
    {
        public List<(string OpClass, string Message)> Calls { get; } = new();
        private readonly Queue<WindowsReAuthVerdict> _verdicts = new();

        public void Enqueue(WindowsReAuthVerdict v) => _verdicts.Enqueue(v);

        public void EnqueueVerified() =>
            _verdicts.Enqueue(new WindowsReAuthVerdict("Verified", true, null));

        public Task<WindowsReAuthVerdict> VerifyAsync(
            string opClass, string message, CancellationToken cancellationToken = default)
        {
            Calls.Add((opClass, message));
            if (_verdicts.Count == 0)
            {
                throw new InvalidOperationException(
                    "FakeWindowsReAuthVerifier.VerifyAsync invoked with empty queue.");
            }
            return Task.FromResult(_verdicts.Dequeue());
        }
    }

    private sealed class ThrowingFakeVerifier : IWindowsReAuthVerifier
    {
        public Task<WindowsReAuthVerdict> VerifyAsync(
            string opClass, string message, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated verifier failure");
    }

    private sealed class NoopCredentialSecretStore : ICredentialSecretStore
    {
        public void Write(string authProfileId, string secret) { }
        public bool Exists(string authProfileId) => false;
        public void Delete(string authProfileId) { }
        public string ComposeTarget(string authProfileId) =>
            "PAXCookbook.AuthProfile." + authProfileId + ".ClientSecret";
    }

    private sealed class NoopCertificateProbe : ICertificateProbe
    {
        public bool Locate(string thumbprint, string store) => false;
    }

    private sealed class NoopCookProcessRegistry : ICookProcessRegistry
    {
        public bool TryGet(string cookId, out int processId) { processId = 0; return false; }
        public bool RequestStop(string cookId) => false;
        public bool ForceKill(string cookId)   => false;
        public void Register(string cookId, CookProcessHandle handle) { }
        public void Deregister(string cookId) { }
    }

    private sealed class NoopCookResumeSpawner : ICookResumeSpawner
    {
        public CookResumeSpawnResult Spawn(CookResumeSpawnRequest request) =>
            new(Outcome: "deferred", FailureCode: "noop-fake", FailureDetail: null);
    }

    // =================================================================
    // Fixture -- minimal workspace + AppRoot, mirrors Stage3d shape.
    // =================================================================

    private sealed class UnlockFixture : IAsyncDisposable
    {
        public string Root                 { get; }
        public string WorkspaceFolderPath  { get; }
        public NativeBrokerHostOptions Options { get; }

        private UnlockFixture(string root, string workspace, NativeBrokerHostOptions options)
        {
            Root                = root;
            WorkspaceFolderPath = workspace;
            Options             = options;
        }

        public static async Task<UnlockFixture> CreateAsync(bool enforceBrokerLock = false)
        {
            var root           = Path.Combine(Path.GetTempPath(),
                "PAXCookbookUnlock_" + Guid.NewGuid().ToString("N"));
            var workspace      = Path.Combine(root, "Workspace");
            var databaseDir    = Path.Combine(workspace, "Database");
            var databaseFile   = Path.Combine(databaseDir, "cookbook.sqlite");
            var appRoot        = Path.Combine(root, "AppRoot");
            var webRoot        = Path.Combine(appRoot, "web");
            var versionPath    = Path.Combine(appRoot, "VERSION.json");
            var templatesDir   = Path.Combine(appRoot, "templates");
            var paxScriptPath  = Path.Combine(appRoot, "resources", "pax", "fixture.ps1");

            Directory.CreateDirectory(databaseDir);
            Directory.CreateDirectory(webRoot);
            Directory.CreateDirectory(templatesDir);

            File.WriteAllText(versionPath,
                "{\"schemaVersion\":1,\"channel\":\"stable\","
                + "\"cookbook\":{\"version\":\"0.0.0-fixture\"},"
                + "\"paxScript\":{\"name\":\"PAX Fixture\","
                + "\"version\":\"0.0.0-fixture\","
                + "\"relativePath\":\"resources/pax/fixture.ps1\","
                + "\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"},"
                + "\"updateManifestUrl\":null}");
            File.WriteAllText(Path.Combine(webRoot, "index.html"),
                "<!doctype html><html><body></body></html>");

            await SeedEmptyDatabaseAsync(databaseFile);

            var options = new NativeBrokerHostOptions(
                PreferredPort:       17654,
                PortRangeStart:      17654,
                PortRangeEnd:        17664,
                WorkspaceFolderPath: workspace,
                WebRoot:             webRoot,
                AppRoot:             appRoot,
                VersionFilePath:     versionPath,
                TemplatesDir:        templatesDir,
                PaxScriptPath:       paxScriptPath,
                EnforceBrokerLock:   enforceBrokerLock);

            return new UnlockFixture(root, workspace, options);
        }

        public static Stage3iCServiceBundle BuildBundle(IWindowsReAuthVerifier verifier) =>
            new()
            {
                ReAuth        = verifier,
                CredStore     = new NoopCredentialSecretStore(),
                CertProbe     = new NoopCertificateProbe(),
                CookRegistry  = new NoopCookProcessRegistry(),
                ResumeSpawner = new NoopCookResumeSpawner(),
                Clock         = () => FrozenClockUtc,
            };

        public ValueTask DisposeAsync()
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch { /* best-effort cleanup */ }
            return ValueTask.CompletedTask;
        }

        private static async Task SeedEmptyDatabaseAsync(string databaseFile)
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
    recipe_id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    file_path TEXT NOT NULL,
    file_hash TEXT NOT NULL,
    status TEXT NOT NULL,
    is_pinned INTEGER NOT NULL,
    pax_adapter_version TEXT NOT NULL,
    recipe_schema_version INTEGER NOT NULL,
    source TEXT NOT NULL,
    source_ref TEXT,
    last_validated_at TEXT,
    last_validation_status TEXT,
    last_cooked_at TEXT,
    last_cook_id TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    deleted_at TEXT
);
CREATE TABLE cooks (
    cook_id TEXT PRIMARY KEY,
    recipe_id TEXT,
    status TEXT NOT NULL,
    exit_code INTEGER,
    started_at TEXT,
    ended_at TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);";
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }
    }
}
