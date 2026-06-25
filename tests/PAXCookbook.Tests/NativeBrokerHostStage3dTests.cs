using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PAXCookbook.Broker.Native;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3d parity tests for the native broker's broker-lock + WebAuthn
// readiness surface. Each test uses an isolated Stage3dWorkspaceFixture
// (temp directory) and either:
//   - omits EnforceBrokerLock        -> Stage 3a/3b/3c behavior (no
//                                       middleware enforcement).
//   - sets   EnforceBrokerLock=true  -> lock-bypass middleware active;
//                                       protected routes return 423
//                                       while Locked.
//
// Shared port-binding collection with Stage 3a/3b/3c.
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3dTests
{
    private const string PaxScriptBaselineHash =
        "007AD1A7F6D40B40E873C684D10B2A79B4D1DD03A1900ADE19B6E482CC10C728";

    // ---------- 1. Lock-state shape -- boot is Locked ----------

    [Fact]
    public async Task LockState_returns_Locked_on_boot_with_zero_remaining_seconds()
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/broker/lock-state");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal("Locked", root.GetProperty("state").GetString());
            Assert.True(root.TryGetProperty("lastActivityUtc", out var lastAct));
            Assert.False(string.IsNullOrWhiteSpace(lastAct.GetString()));
            Assert.Equal(15, root.GetProperty("inactivityTimeoutMinutes").GetInt32());
            Assert.Equal(0, root.GetProperty("inactivityRemainingSeconds").GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("timeAnomaly").ValueKind);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 2. POST /lock is idempotent and returns 200 ----------

    [Fact]
    public async Task Lock_post_is_idempotent_and_returns_locked_snapshot()
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };

            using (var r1 = await http.PostAsync("/api/v1/broker/lock", new StringContent("")))
            {
                Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
                var b1 = await r1.Content.ReadAsStringAsync();
                using var d1 = JsonDocument.Parse(b1);
                Assert.True(d1.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("Locked", d1.RootElement.GetProperty("state").GetString());
            }

            using (var r2 = await http.PostAsync("/api/v1/broker/lock", new StringContent("")))
            {
                Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
                var b2 = await r2.Content.ReadAsStringAsync();
                using var d2 = JsonDocument.Parse(b2);
                Assert.True(d2.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("Locked", d2.RootElement.GetProperty("state").GetString());
            }
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 3. POST /unlock returns 503 device-not-present when no verifier wired ----------

    // §4AA -- when the Stage 3iC service bundle is NOT installed (no
    // Stage3iCServiceOverride), BrokerLockRoutes.Register receives a
    // null IWindowsReAuthVerifier and the unlock route returns a
    // deterministic 503 device-not-present envelope. Production
    // wires the WindowsReAuthSidecarVerifier via Program.cs and that
    // path is exercised end-to-end by BrokerLockRoutesUnlockTests.
    [Fact]
    public async Task Unlock_post_returns_503_device_not_present_when_no_verifier_wired()
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync("/api/v1/broker/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.False(root.GetProperty("unlocked").GetBoolean());
            Assert.Equal("device-not-present", root.GetProperty("reason").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
            Assert.Equal("DeviceNotPresent", root.GetProperty("verificationResult").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 4. WebAuthn status: no credentials file -> registered:false ----------

    [Fact]
    public async Task WebAuthnStatus_reports_unregistered_when_credentials_file_missing()
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/broker/webauthn/status");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.False(root.GetProperty("registered").GetBoolean());
            Assert.Equal(0, root.GetProperty("credentialIds").GetArrayLength());
            var origins = root.GetProperty("acceptedOrigins");
            Assert.Equal(2, origins.GetArrayLength());
            Assert.Equal("http://127.0.0.1:" + start.Port, origins[0].GetString());
            Assert.Equal("http://localhost:" + start.Port, origins[1].GetString());
            Assert.Equal("auto", root.GetProperty("rpId").GetString());
            var algs = root.GetProperty("supportedAlgs");
            Assert.Equal(1, algs.GetArrayLength());
            Assert.Equal(-7, algs[0].GetInt32());
            Assert.Equal("required", root.GetProperty("userVerification").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 5. WebAuthn status: credentials file present -> registered:true ----------

    [Fact]
    public async Task WebAuthnStatus_reports_registered_when_credentials_file_present()
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync();
        // Seed two credentials in the file the readiness service reads.
        var authDir = Path.Combine(fx.WorkspaceFolderPath, "Auth");
        Directory.CreateDirectory(authDir);
        File.WriteAllText(Path.Combine(authDir, "webauthn-credentials.json"),
            "{\"schemaVersion\":1,\"credentials\":[" +
            "{\"credentialId\":\"AAA-credential-one\"}," +
            "{\"credentialId\":\"BBB-credential-two\"}" +
            "]}");

        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/broker/webauthn/status");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("registered").GetBoolean());
            var ids = root.GetProperty("credentialIds");
            Assert.Equal(2, ids.GetArrayLength());
            Assert.Equal("AAA-credential-one", ids[0].GetString());
            Assert.Equal("BBB-credential-two", ids[1].GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 6-11. WebAuthn deferred POSTs all return controlled 501 ----------

    [Fact]
    public async Task WebAuthn_unlock_challenge_post_returns_controlled_501()
        => await AssertWebAuthnDeferred501("/api/v1/broker/webauthn/unlock-challenge");

    [Fact]
    public async Task WebAuthn_unlock_post_returns_controlled_501()
        => await AssertWebAuthnDeferred501("/api/v1/broker/webauthn/unlock");

    [Fact]
    public async Task WebAuthn_bootstrap_register_challenge_post_returns_controlled_501()
        => await AssertWebAuthnDeferred501("/api/v1/broker/webauthn/bootstrap-register-challenge");

    [Fact]
    public async Task WebAuthn_bootstrap_register_unlock_post_returns_controlled_501()
        => await AssertWebAuthnDeferred501("/api/v1/broker/webauthn/bootstrap-register-unlock");

    [Fact]
    public async Task WebAuthn_register_challenge_post_returns_controlled_501()
        => await AssertWebAuthnDeferred501("/api/v1/broker/webauthn/register-challenge");

    [Fact]
    public async Task WebAuthn_register_post_returns_controlled_501()
        => await AssertWebAuthnDeferred501("/api/v1/broker/webauthn/register");

    private static async Task AssertWebAuthnDeferred501(string path)
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.PostAsync(path,
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal("not_implemented", root.GetProperty("error").GetString());
            Assert.Equal("webauthnVerifierUnavailable", root.GetProperty("code").GetString());
            Assert.Equal("NotPortedNative", root.GetProperty("verificationResult").GetString());
            Assert.Equal(path, root.GetProperty("endpoint").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 12. close-intent / shutdown ----------
    //
    // Originally Stage 3d asserted these routes returned 404 because
    // they had not yet been ported. Stage 3i-A registered the full
    // native ports of POST /api/v1/broker/close-intent and POST
    // /api/v1/broker/shutdown, so the original "404 not registered"
    // assertion has been retired. Active coverage of both routes now
    // lives in NativeBrokerHostStage3iATests:
    //   * CloseIntent_writes_marker_and_returns_202_for_valid_intent
    //   * CloseIntent_returns_400_for_invalid_intent
    //   * CloseIntent_returns_413_for_oversized_body
    //   * Shutdown_returns_202_and_calls_shutdown_coordinator
    //   * Shutdown_other_methods_return_404
    //   * CloseIntent_other_methods_return_404

    // ---------- 13-15. EnforceBrokerLock=true gates protected routes ----------

    [Fact]
    public async Task EnforceBrokerLock_returns_423_brokerLocked_on_protected_route_when_Locked()
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync(enforceBrokerLock: true);
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/recipes");
            Assert.Equal((HttpStatusCode)423, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal("brokerLocked", root.GetProperty("code").GetString());
            Assert.Equal("GET", root.GetProperty("attemptedMethod").GetString());
            Assert.Equal("/api/v1/recipes", root.GetProperty("attemptedPath").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task EnforceBrokerLock_does_not_block_health_route_when_Locked()
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync(enforceBrokerLock: true);
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/health");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task EnforceBrokerLock_allows_lock_state_and_lock_routes_when_Locked()
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync(enforceBrokerLock: true);
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };

            using (var r1 = await http.GetAsync("/api/v1/broker/lock-state"))
            {
                Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
            }
            using (var r2 = await http.PostAsync("/api/v1/broker/lock", new StringContent("")))
            {
                Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
            }
            using (var r3 = await http.GetAsync("/api/v1/broker/webauthn/status"))
            {
                Assert.Equal(HttpStatusCode.OK, r3.StatusCode);
            }
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 16. EnforceBrokerLock + Unlocked -> protected route passes ----------

    [Fact]
    public async Task EnforceBrokerLock_when_Unlocked_allows_protected_route_through()
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync(enforceBrokerLock: true);
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            // Drive the in-process unlock via the test-only hook --
            // production code does NOT do this, the WinRT verifier
            // (or a future wired sidecar) does.
            Assert.NotNull(host.LockService);
            host.LockService!.TransitionToUnlocked();

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/recipes");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 17. Stage 3c read-only surface unchanged without opt-in ----------

    [Fact]
    public async Task Stage3c_recipes_still_works_without_EnforceBrokerLock()
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/recipes");
            // Note: workspace is seeded with no recipes so endpoint
            // returns an empty list, NOT 423 -- proving the lock
            // middleware does not enforce when EnforceBrokerLock=false.
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("recipes", out _));
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 18. Stage 3a health envelope still announces native ----------

    [Fact]
    public async Task Stage3a_health_envelope_still_reports_broker_native()
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/health");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("native", doc.RootElement.GetProperty("broker").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 19. Unknown /api/v1/... still returns JSON 404 envelope ----------

    [Fact]
    public async Task Unknown_api_path_under_v1_returns_json_not_found_envelope()
    {
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/no-such-thing");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 20. Lock-state polling does not bump activity (no unintended unlock-stay) ----------

    [Fact]
    public async Task Lock_state_polling_does_not_bump_activity_window()
    {
        // Use a short inactivity to keep the test cheap; the service
        // is constructed via the host so we drive activity through it.
        await using var fx = await Stage3dWorkspaceFixture.CreateAsync(enforceBrokerLock: true);
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            Assert.NotNull(host.LockService);
            host.LockService!.TransitionToUnlocked();

            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var r1 = await http.GetAsync("/api/v1/broker/lock-state");
            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
            var b1 = await r1.Content.ReadAsStringAsync();
            using var d1 = JsonDocument.Parse(b1);
            var firstActivity = d1.RootElement.GetProperty("lastActivityUtc").GetString();

            await Task.Delay(50);
            using var r2 = await http.GetAsync("/api/v1/broker/lock-state");
            var b2 = await r2.Content.ReadAsStringAsync();
            using var d2 = JsonDocument.Parse(b2);
            var secondActivity = d2.RootElement.GetProperty("lastActivityUtc").GetString();

            Assert.Equal(firstActivity, secondActivity);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 21. No-pwsh tripwire: no pwsh process started by Stage 3d routes ----------

    [Fact]
    public async Task No_pwsh_process_started_by_any_Stage3d_route()
    {
        var pwshBefore = System.Diagnostics.Process.GetProcessesByName("pwsh").Length
                       + System.Diagnostics.Process.GetProcessesByName("powershell").Length;

        await using var fx = await Stage3dWorkspaceFixture.CreateAsync(enforceBrokerLock: true);
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // Hit every Stage 3d endpoint at least once.
            await http.GetAsync("/api/v1/broker/lock-state");
            await http.PostAsync("/api/v1/broker/lock", new StringContent(""));
            await http.PostAsync("/api/v1/broker/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            await http.GetAsync("/api/v1/broker/webauthn/status");
            await http.PostAsync("/api/v1/broker/webauthn/unlock-challenge",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            await http.PostAsync("/api/v1/broker/webauthn/unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            await http.PostAsync("/api/v1/broker/webauthn/bootstrap-register-challenge",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            await http.PostAsync("/api/v1/broker/webauthn/bootstrap-register-unlock",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            await http.PostAsync("/api/v1/broker/webauthn/register-challenge",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            await http.PostAsync("/api/v1/broker/webauthn/register",
                new StringContent("{}", Encoding.UTF8, "application/json"));
        }
        finally { await host.StopAsync(); }

        var pwshAfter = System.Diagnostics.Process.GetProcessesByName("pwsh").Length
                      + System.Diagnostics.Process.GetProcessesByName("powershell").Length;
        Assert.Equal(pwshBefore, pwshAfter);
    }

    // ---------- 22. PAX script hash unchanged ----------

    [Fact]
    public void PAX_script_hash_unchanged_at_stage_3d()
    {
        var repoRoot = FindRepoRoot();
        var paxPath = Path.Combine(repoRoot,
            "app", "resources", "pax", "PAX_Purview_Audit_Log_Processor.ps1");
        Assert.True(File.Exists(paxPath), "PAX script must exist at " + paxPath);
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(paxPath);
        var hex = Convert.ToHexString(sha.ComputeHash(fs));
        Assert.Equal(PaxScriptBaselineHash, hex);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "PAXCookbook.sln"))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent ?? string.Empty;
        }
        throw new InvalidOperationException("Could not locate repo root.");
    }

    // ---------- Stage 3d test fixture: temp workspace + AppRoot only ----------

    private sealed class Stage3dWorkspaceFixture : IAsyncDisposable
    {
        public string Root { get; }
        public string WorkspaceFolderPath { get; }
        public string AppRoot { get; }
        public NativeBrokerHostOptions Options { get; }

        private Stage3dWorkspaceFixture(string root, string workspace, string appRoot,
            NativeBrokerHostOptions options)
        {
            Root = root;
            WorkspaceFolderPath = workspace;
            AppRoot = appRoot;
            Options = options;
        }

        public static async Task<Stage3dWorkspaceFixture> CreateAsync(bool enforceBrokerLock = false)
        {
            var root = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3d_" + Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "Workspace");
            var databaseDir = Path.Combine(workspace, "Database");
            var databaseFile = Path.Combine(databaseDir, "cookbook.sqlite");
            var appRoot = Path.Combine(root, "AppRoot");
            var webRoot = Path.Combine(appRoot, "web");
            var versionFilePath = Path.Combine(appRoot, "VERSION.json");
            var templatesDir = Path.Combine(appRoot, "templates");
            var paxScriptPath = Path.Combine(appRoot, "resources", "pax", "fixture.ps1");

            Directory.CreateDirectory(databaseDir);
            Directory.CreateDirectory(appRoot);
            Directory.CreateDirectory(webRoot);
            Directory.CreateDirectory(templatesDir);

            File.WriteAllText(versionFilePath,
                "{\"schemaVersion\":1,\"channel\":\"stable\"," +
                "\"cookbook\":{\"version\":\"0.0.0-fixture\"}," +
                "\"paxScript\":{\"name\":\"PAX Fixture\"," +
                "\"version\":\"0.0.0-fixture\"," +
                "\"relativePath\":\"resources/pax/fixture.ps1\"," +
                "\"sha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}," +
                "\"updateManifestUrl\":null}");
            File.WriteAllText(Path.Combine(webRoot, "index.html"),
                "<!doctype html><html><body></body></html>");

            // Empty cookbook.sqlite so Stage 3c read-only routes work
            // (recipes table queryable but returns empty list).
            await SeedEmptyDatabaseAsync(databaseFile);

            var options = new NativeBrokerHostOptions(
                PreferredPort:       17654,
                PortRangeStart:      17654,
                PortRangeEnd:        17664,
                WorkspaceFolderPath: workspace,
                WebRoot:             webRoot,
                AppRoot:             appRoot,
                VersionFilePath:     versionFilePath,
                TemplatesDir:        templatesDir,
                PaxScriptPath:       paxScriptPath,
                EnforceBrokerLock:   enforceBrokerLock);

            return new Stage3dWorkspaceFixture(root, workspace, appRoot, options);
        }

        private static async Task SeedEmptyDatabaseAsync(string databaseFile)
        {
            var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = databaseFile,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            }.ToString();
            await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
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
    pid INTEGER,
    cook_folder TEXT NOT NULL,
    pax_script_path TEXT NOT NULL,
    pax_script_version TEXT NOT NULL,
    trigger TEXT NOT NULL,
    started_at TEXT,
    finished_at TEXT,
    duration_seconds REAL,
    error_class TEXT,
    error_message TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    summary_path TEXT,
    closure_reason TEXT,
    closure_evidence_json TEXT,
    parent_cook_id TEXT
);
CREATE TABLE auth_profiles (
    auth_profile_id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    auth_method TEXT NOT NULL,
    tenant_id TEXT,
    client_id TEXT,
    credential_alias TEXT,
    last_used_at TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    deleted_at TEXT
);";
            await cmd.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // Cleanup is best-effort -- temp directory will be
                // garbage-collected by the OS even if a handle is
                // still open.
            }
            return ValueTask.CompletedTask;
        }
    }
}
