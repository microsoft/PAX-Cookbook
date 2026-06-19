using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PAXCookbook.Broker.Native;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3c parity tests for the native broker's SQLite/workspace read
// surface. Each test uses an isolated WorkspaceFixture (temp directory
// with a freshly seeded cookbook.sqlite and a synthetic app-root tree
// containing VERSION.json + a tiny templates/ directory). The real
// installed workspace and the real app/ directory are never touched.
//
// Tests share the "NativeBrokerHostPortBinding" xUnit collection with
// Stage 3a/3b so the three classes serialise port-17654 binding.
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3cTests
{
    private const string PaxScriptBaselineHash =
        "1A9BC94783683AE1DA68EE6A86DE2106A96122B67B14EE20090E6687792E3878";

    // ---------- 1. Health (expanded envelope) ----------

    [Fact]
    public async Task Health_returns_expanded_envelope_with_db_info()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/health");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal("ok", root.GetProperty("status").GetString());
            Assert.Equal("native", root.GetProperty("broker").GetString());
            Assert.Equal(start.Port, root.GetProperty("port").GetInt32());
            Assert.True(root.GetProperty("pid").GetInt32() > 0);
            Assert.Equal(fx.WorkspaceFolderPath,
                root.GetProperty("workspaceFolderPath").GetString());
            Assert.Equal(fx.DatabaseFilePath,
                root.GetProperty("databaseFilePath").GetString());
            Assert.True(root.GetProperty("databaseFileExists").GetBoolean());
            Assert.True(root.GetProperty("dbSizeBytes").GetInt64() > 0);
            Assert.True(root.TryGetProperty("startedAtUtc", out _));
            Assert.True(root.TryGetProperty("uptimeSeconds", out _));
            Assert.True(root.TryGetProperty("brokerSession", out var session));
            Assert.Equal("native_stage_3c_partial",
                session.GetProperty("startupClassification").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 2. Runtime/version ----------

    [Fact]
    public async Task Runtime_version_returns_bundled_pax_info_from_version_json()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/runtime/version");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal(WorkspaceFixture.FixtureCookbookVersion,
                root.GetProperty("cookbookVersion").GetString());
            Assert.Equal(WorkspaceFixture.FixtureChannel,
                root.GetProperty("releaseChannel").GetString());
            var pax = root.GetProperty("bundledPax");
            Assert.Equal(WorkspaceFixture.FixturePaxName,
                pax.GetProperty("name").GetString());
            Assert.Equal(WorkspaceFixture.FixturePaxVersion,
                pax.GetProperty("version").GetString());
            Assert.Equal(WorkspaceFixture.FixturePaxRelativePath,
                pax.GetProperty("relativePath").GetString());
            Assert.Equal(WorkspaceFixture.FixturePaxSha256,
                pax.GetProperty("sha256").GetString());
            var paths = root.GetProperty("paths");
            Assert.Equal(fx.WorkspaceFolderPath,
                paths.GetProperty("workspace").GetString());
            Assert.Equal(fx.DatabaseFilePath,
                paths.GetProperty("database").GetString());
            Assert.Equal("native",
                root.GetProperty("runtime").GetProperty("implementation").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Runtime_version_returns_500_when_version_file_missing()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        // Delete VERSION.json after fixture creation so the route hits
        // the failure path.
        File.Delete(fx.VersionFilePath);
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/runtime/version");
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("version_info_unavailable", body);
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 3. Recipes ----------

    [Fact]
    public async Task Recipes_list_returns_seeded_rows_in_created_at_desc_order()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/recipes");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var arr = doc.RootElement.GetProperty("recipes");
            // Seed: 2 non-deleted recipes, 1 soft-deleted; deleted is
            // filtered out by the WHERE deleted_at IS NULL clause.
            Assert.Equal(2, arr.GetArrayLength());
            // Order: r-newer first (later created_at), r-older second.
            Assert.Equal("r-newer", arr[0].GetProperty("recipeId").GetString());
            Assert.Equal("r-older", arr[1].GetProperty("recipeId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Recipe_get_returns_meta_with_deferred_recipeFileLoad_marker()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/recipes/r-newer");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var meta = doc.RootElement.GetProperty("meta");
            Assert.Equal("r-newer", meta.GetProperty("recipeId").GetString());
            Assert.Equal("Newer Recipe", meta.GetProperty("name").GetString());
            Assert.Equal(1, meta.GetProperty("recipeSchemaVersion").GetInt32());
            var def = doc.RootElement.GetProperty("recipeFileLoad");
            Assert.True(def.GetProperty("deferred").GetBoolean());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Recipe_get_returns_404_for_unknown_id()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/recipes/r-does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Contains("recipe_not_found", await resp.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Recipes_list_returns_500_when_database_file_missing()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        File.Delete(fx.DatabaseFilePath);
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/recipes");
            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            Assert.Contains("workspace_database_unavailable",
                await resp.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 4. Cooks ----------

    [Fact]
    public async Task Cooks_list_returns_seeded_rows_in_created_at_desc_order()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/cooks");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var arr = doc.RootElement.GetProperty("cooks");
            Assert.Equal(2, arr.GetArrayLength());
            Assert.Equal("c-newer", arr[0].GetProperty("cookId").GetString());
            Assert.Equal("c-older", arr[1].GetProperty("cookId").GetString());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Cook_get_returns_metadata_with_deferred_enrichment_marker()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/cooks/c-newer");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var cook = doc.RootElement.GetProperty("cook");
            Assert.Equal("c-newer", cook.GetProperty("cookId").GetString());
            Assert.Equal("succeeded", cook.GetProperty("status").GetString());
            Assert.Equal(0, cook.GetProperty("exitCode").GetInt32());
            Assert.True(doc.RootElement.GetProperty("cookEnrichment")
                .GetProperty("deferred").GetBoolean());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Cook_get_returns_404_for_unknown_id()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/cooks/c-does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Contains("cook_not_found", await resp.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Cook_log_returns_log_text_when_present()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/cooks/c-newer/log");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/plain; charset=utf-8",
                resp.Content.Headers.ContentType?.ToString());
            Assert.Equal("no-store", resp.Headers.CacheControl?.ToString());
            Assert.Equal(WorkspaceFixture.FixtureLogBody,
                await resp.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Cook_log_returns_404_when_log_file_missing()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        // c-older's cook folder is seeded without a cook.log file.
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/cooks/c-older/log");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Contains("cook_log_not_found", await resp.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 5. Auth profiles ----------

    [Fact]
    public async Task Auth_profiles_list_returns_metadata_only_no_secrets()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/auth/profiles");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var arr = doc.RootElement.GetProperty("authProfiles");
            Assert.Equal(2, arr.GetArrayLength());
            // Sort by name COLLATE NOCASE: "alpha profile" then "Beta Profile".
            Assert.Equal("ap-alpha", arr[0].GetProperty("authProfileId").GetString());
            Assert.Equal("ap-beta", arr[1].GetProperty("authProfileId").GetString());
            // Cred-man lookup key is the LOOKUP KEY (not the secret),
            // exposed verbatim per PS broker contract.
            Assert.Equal("PAXCookbook|ap-alpha|clientSecret",
                arr[0].GetProperty("credManTarget").GetString());
            // No secret material in payload.
            Assert.DoesNotContain("\"clientSecret\"", body);
            Assert.DoesNotContain("\"password\"", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BEGIN PRIVATE KEY", body);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Auth_profile_get_returns_single_profile()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/auth/profiles/ap-alpha");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var ap = doc.RootElement.GetProperty("authProfile");
            Assert.Equal("ap-alpha", ap.GetProperty("authProfileId").GetString());
            Assert.Equal("alpha profile", ap.GetProperty("name").GetString());
            Assert.Equal("client_secret_credential", ap.GetProperty("mode").GetString());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 6. Templates ----------

    [Fact]
    public async Task Templates_list_returns_summaries_sorted_by_id()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/templates");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var arr = doc.RootElement.GetProperty("templates");
            Assert.Equal(2, arr.GetArrayLength());
            Assert.Equal("alpha-template", arr[0].GetProperty("templateId").GetString());
            Assert.Equal("beta-template", arr[1].GetProperty("templateId").GetString());
            Assert.Equal(2, arr[0].GetProperty("manualGuidanceCount").GetInt32());
            Assert.Equal(0, arr[1].GetProperty("manualGuidanceCount").GetInt32());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Template_get_returns_raw_document_with_full_fields()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/templates/alpha-template");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Assert.Equal("alpha-template", root.GetProperty("templateId").GetString());
            // Raw doc passes through fields not surfaced in the summary.
            Assert.Equal(2, root.GetProperty("manualGuidance").GetArrayLength());
            Assert.True(root.TryGetProperty("recipeDefaults", out _));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Template_get_returns_404_for_unknown_id()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/templates/does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Contains("template_not_found", await resp.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 7. API surface guards ----------

    [Fact]
    public async Task Post_to_read_only_api_returns_method_not_allowed_or_404()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // /api/v1/cooks is a read-only listing surface. Minimal
            // API maps only GET; POST surfaces as 405. 404 is also
            // acceptable -- both indicate "not a mutation surface".
            using var resp = await http.PostAsJsonAsync("/api/v1/cooks", new { name = "new" });
            Assert.True(
                resp.StatusCode is HttpStatusCode.MethodNotAllowed
                                or HttpStatusCode.NotFound,
                "POST to read-only route returned " + resp.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Unknown_api_path_returns_json_404_not_spa_html()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/api/v1/no-such-endpoint");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Equal("application/json; charset=utf-8",
                resp.Content.Headers.ContentType?.ToString());
            Assert.Contains("\"error\":\"not_found\"",
                await resp.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 8. Static + SPA surface still intact from Stage 3b ----------

    [Fact]
    public async Task Static_spa_fallback_still_returns_index_html_for_extensionless_route()
    {
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            using var resp = await http.GetAsync("/recipes/some-ulid/edit");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/html; charset=utf-8",
                resp.Content.Headers.ContentType?.ToString());
            Assert.Contains(WorkspaceFixture.FixtureIndexHtml,
                await resp.Content.ReadAsStringAsync());
        }
        finally { await host.StopAsync(); }
    }

    // ---------- 9. PAX hash tripwire ----------

    [Fact]
    public void Pax_script_hash_unchanged_after_stage_3c()
    {
        var paxRel = Path.Combine("app", "resources", "pax",
            "PAX_Purview_Audit_Log_Processor.ps1");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? located = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, paxRel);
            if (File.Exists(candidate))
            {
                located = candidate;
                break;
            }
            dir = dir.Parent;
        }
        Assert.NotNull(located);
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(located!);
        var hash = Convert.ToHexString(sha.ComputeHash(stream));
        Assert.Equal(PaxScriptBaselineHash, hash);
    }

    // ---------- 10. No pwsh process launched ----------
    //
    // Asserts that exercising every Stage 3c route on a running
    // NativeBrokerHost does not spawn a PowerShell sub-process. The
    // earlier version of this test counted machine-wide pwsh.exe /
    // powershell.exe PIDs before and after the run; on a developer
    // machine that count can drop while the test is in flight
    // (unrelated background pwsh terminals exit), failing the
    // assertion even though the host launched zero PowerShell
    // processes. The current version scopes the probe to children
    // of the test runner's own process via the Win32
    // CreateToolhelp32Snapshot enumeration, which is deterministic
    // and unaffected by ambient PowerShell terminals.
    //
    // The intent is unchanged: starting NativeBrokerHost must not
    // launch a PowerShell broker, must not invoke broker\Start-Broker.ps1,
    // and must not produce any visible terminal. The scoped probe
    // catches any Process.Start of pwsh.exe / powershell.exe from
    // anywhere inside the host or its handlers because such a child
    // process would have the test runner as its parent.

    [Fact]
    public async Task Starting_native_host_does_not_launch_pwsh_process()
    {
        var ownPid = Environment.ProcessId;
        var pwshChildrenBefore = CountChildPowerShellProcesses(ownPid);
        await using var fx = await WorkspaceFixture.CreateAsync();
        await using var host = new NativeBrokerHost(fx.Options);
        var start = await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(start.BaseUrl) };
            // Hit every Stage 3c route to make sure none of them shell
            // out to PowerShell on the request path.
            _ = await http.GetAsync("/api/v1/health");
            _ = await http.GetAsync("/api/v1/runtime/version");
            _ = await http.GetAsync("/api/v1/recipes");
            _ = await http.GetAsync("/api/v1/recipes/r-newer");
            _ = await http.GetAsync("/api/v1/cooks");
            _ = await http.GetAsync("/api/v1/cooks/c-newer");
            _ = await http.GetAsync("/api/v1/cooks/c-newer/log");
            _ = await http.GetAsync("/api/v1/auth/profiles");
            _ = await http.GetAsync("/api/v1/auth/profiles/ap-alpha");
            _ = await http.GetAsync("/api/v1/templates");
            _ = await http.GetAsync("/api/v1/templates/alpha-template");
            var pwshChildrenAfter = CountChildPowerShellProcesses(ownPid);
            Assert.Equal(pwshChildrenBefore, pwshChildrenAfter);
        }
        finally { await host.StopAsync(); }
    }

    // Win32 toolhelp child-process probe. Scoped to children of the
    // supplied parent PID so the assertion is unaffected by ambient
    // pwsh terminals on the machine. Returns the count of immediate
    // child processes whose image name is pwsh.exe or powershell.exe.
    private static int CountChildPowerShellProcesses(int parentPid)
    {
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0u);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
        {
            throw new InvalidOperationException(
                "CreateToolhelp32Snapshot failed; Win32 error " +
                Marshal.GetLastWin32Error());
        }
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref entry))
            {
                return 0;
            }
            int count = 0;
            do
            {
                if ((int)entry.th32ParentProcessID == parentPid)
                {
                    var name = entry.szExeFile ?? string.Empty;
                    if (string.Equals(name, "pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "powershell.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }
            }
            while (Process32NextW(snap, ref entry));
            return count;
        }
        finally
        {
            CloseHandle(snap);
        }
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002u;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int  pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // ----------------- Fixture -----------------
    //
    // Builds an isolated temp workspace with:
    //   <root>/Workspace/Database/cookbook.sqlite (seeded)
    //   <root>/Workspace/Cooks/c-newer/cook.log
    //   <root>/AppRoot/VERSION.json
    //   <root>/AppRoot/web/index.html
    //   <root>/AppRoot/templates/alpha-template.template.json
    //   <root>/AppRoot/templates/beta-template.template.json
    //
    // Everything dispose-cleans up on test completion.
    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        public const string FixtureCookbookVersion   = "9.9.9-fixture";
        public const string FixtureChannel           = "stable";
        public const string FixturePaxName           = "PAX Fixture";
        public const string FixturePaxVersion        = "0.0.0-fixture";
        public const string FixturePaxRelativePath   = "resources/pax/fixture.ps1";
        public const string FixturePaxSha256         = "0000000000000000000000000000000000000000000000000000000000000000";
        public const string FixtureLogBody           = "fixture cook log line one\nfixture cook log line two\n";
        public const string FixtureIndexHtml         = "<!doctype html><html><body><div id=\"app\"></div></body></html>";

        public string Root { get; }
        public string WorkspaceFolderPath { get; }
        public string DatabaseFilePath { get; }
        public string VersionFilePath { get; }
        public string AppRoot { get; }
        public string TemplatesDir { get; }
        public string WebRoot { get; }
        public NativeBrokerHostOptions Options { get; }

        private WorkspaceFixture(string root, string workspaceFolderPath, string databaseFilePath,
            string versionFilePath, string appRoot, string templatesDir, string webRoot,
            NativeBrokerHostOptions options)
        {
            Root = root;
            WorkspaceFolderPath = workspaceFolderPath;
            DatabaseFilePath = databaseFilePath;
            VersionFilePath = versionFilePath;
            AppRoot = appRoot;
            TemplatesDir = templatesDir;
            WebRoot = webRoot;
            Options = options;
        }

        public static async Task<WorkspaceFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3c_" + Guid.NewGuid().ToString("N"));
            var workspace   = Path.Combine(root, "Workspace");
            var databaseDir = Path.Combine(workspace, "Database");
            var databaseFile = Path.Combine(databaseDir, "cookbook.sqlite");
            var cooksDir    = Path.Combine(workspace, "Cooks");
            var recipesDir  = Path.Combine(workspace, "Recipes");
            var runtimeDir  = Path.Combine(workspace, "Runtime");
            var appRoot     = Path.Combine(root, "AppRoot");
            var templatesDir = Path.Combine(appRoot, "templates");
            var webRoot     = Path.Combine(appRoot, "web");
            var versionFilePath = Path.Combine(appRoot, "VERSION.json");

            Directory.CreateDirectory(databaseDir);
            Directory.CreateDirectory(recipesDir);
            Directory.CreateDirectory(runtimeDir);
            Directory.CreateDirectory(Path.Combine(cooksDir, "c-newer"));
            Directory.CreateDirectory(Path.Combine(cooksDir, "c-older"));
            Directory.CreateDirectory(appRoot);
            Directory.CreateDirectory(templatesDir);
            Directory.CreateDirectory(webRoot);

            // Cook log for c-newer; c-older has no log file (404 path).
            File.WriteAllText(
                Path.Combine(cooksDir, "c-newer", "cook.log"),
                FixtureLogBody,
                new UTF8Encoding(false));

            // VERSION.json
            File.WriteAllText(versionFilePath,
                "{" +
                "\"schemaVersion\":1," +
                "\"channel\":\"" + FixtureChannel + "\"," +
                "\"cookbook\":{\"version\":\"" + FixtureCookbookVersion + "\"}," +
                "\"paxScript\":{" +
                    "\"name\":\"" + FixturePaxName + "\"," +
                    "\"version\":\"" + FixturePaxVersion + "\"," +
                    "\"relativePath\":\"" + FixturePaxRelativePath + "\"," +
                    "\"sha256\":\"" + FixturePaxSha256 + "\"}," +
                "\"updateManifestUrl\":null" +
                "}");

            // Templates -- alpha has manualGuidance + recipeDefaults
            // so we can validate the pass-through detail endpoint.
            File.WriteAllText(Path.Combine(templatesDir, "alpha-template.template.json"),
                "{" +
                "\"templateId\":\"alpha-template\"," +
                "\"templateVersion\":\"1.0.0\"," +
                "\"templateSchemaVersion\":1," +
                "\"displayName\":\"Alpha\"," +
                "\"shortDescription\":\"alpha desc\"," +
                "\"category\":\"audit\"," +
                "\"minPaxScriptVersion\":\"1.0.0\"," +
                "\"minCookbookVersion\":\"1.0.0\"," +
                "\"manualGuidance\":[\"step one\",\"step two\"]," +
                "\"recipeDefaults\":{\"name\":\"Alpha default\"}" +
                "}");
            File.WriteAllText(Path.Combine(templatesDir, "beta-template.template.json"),
                "{" +
                "\"templateId\":\"beta-template\"," +
                "\"templateVersion\":\"1.0.0\"," +
                "\"templateSchemaVersion\":1," +
                "\"displayName\":\"Beta\"," +
                "\"shortDescription\":\"beta desc\"," +
                "\"category\":\"audit\"," +
                "\"minPaxScriptVersion\":\"1.0.0\"," +
                "\"minCookbookVersion\":\"1.0.0\"" +
                "}");

            File.WriteAllText(Path.Combine(webRoot, "index.html"), FixtureIndexHtml);

            await SeedDatabaseAsync(databaseFile);

            var options = new NativeBrokerHostOptions(
                PreferredPort:       17654,
                PortRangeStart:      17654,
                PortRangeEnd:        17664,
                WorkspaceFolderPath: workspace,
                WebRoot:             webRoot,
                AppRoot:             appRoot,
                VersionFilePath:     versionFilePath,
                TemplatesDir:        templatesDir,
                PaxScriptPath:       Path.Combine(appRoot, "resources", "pax", "fixture.ps1"));

            return new WorkspaceFixture(root, workspace, databaseFile, versionFilePath,
                appRoot, templatesDir, webRoot, options);
        }

        // Seed M1 schema columns referenced by Stage 3c routes. The
        // PowerShell broker's migrations create more columns and more
        // tables; here we only seed what the SELECT statements in
        // SqliteWorkspaceReader read.
        private static async Task SeedDatabaseAsync(string databaseFile)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = databaseFile,
                Mode       = SqliteOpenMode.ReadWriteCreate,
            }.ToString();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();

            using (var cmd = conn.CreateCommand())
            {
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
    mode TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    client_id TEXT NOT NULL,
    cred_man_target TEXT,
    cert_thumbprint TEXT,
    cert_store TEXT,
    description TEXT,
    last_verified_at TEXT,
    last_verified_result TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);";
                await cmd.ExecuteNonQueryAsync();
            }

            // Seed rows. created_at ordering matters for list tests.
            string SeedRecipe(string id, string name, string createdAt, string? deletedAt) =>
                "INSERT INTO recipes (recipe_id, name, file_path, file_hash, status, is_pinned, " +
                "pax_adapter_version, recipe_schema_version, source, source_ref, last_validated_at, " +
                "last_validation_status, last_cooked_at, last_cook_id, created_at, updated_at, " +
                "deleted_at) VALUES ($id,$name,$file_path,$file_hash,'active',0,'1.0.0',1,'manual'," +
                "NULL,NULL,NULL,NULL,NULL,$created_at,$updated_at," +
                (deletedAt is null ? "NULL" : "$deleted_at") + ");";

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SeedRecipe("r-newer", "Newer Recipe", "2026-05-22T10:00:00Z", null);
                cmd.Parameters.AddWithValue("$id", "r-newer");
                cmd.Parameters.AddWithValue("$name", "Newer Recipe");
                cmd.Parameters.AddWithValue("$file_path", @"C:\Workspace\Recipes\r-newer.pantry.json");
                cmd.Parameters.AddWithValue("$file_hash", "deadbeef");
                cmd.Parameters.AddWithValue("$created_at", "2026-05-22T10:00:00Z");
                cmd.Parameters.AddWithValue("$updated_at", "2026-05-22T10:00:00Z");
                await cmd.ExecuteNonQueryAsync();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SeedRecipe("r-older", "Older Recipe", "2026-05-20T10:00:00Z", null);
                cmd.Parameters.AddWithValue("$id", "r-older");
                cmd.Parameters.AddWithValue("$name", "Older Recipe");
                cmd.Parameters.AddWithValue("$file_path", @"C:\Workspace\Recipes\r-older.pantry.json");
                cmd.Parameters.AddWithValue("$file_hash", "cafef00d");
                cmd.Parameters.AddWithValue("$created_at", "2026-05-20T10:00:00Z");
                cmd.Parameters.AddWithValue("$updated_at", "2026-05-20T10:00:00Z");
                await cmd.ExecuteNonQueryAsync();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SeedRecipe("r-deleted", "Deleted Recipe", "2026-05-21T10:00:00Z",
                    "2026-05-21T11:00:00Z");
                cmd.Parameters.AddWithValue("$id", "r-deleted");
                cmd.Parameters.AddWithValue("$name", "Deleted Recipe");
                cmd.Parameters.AddWithValue("$file_path", @"C:\Workspace\Recipes\r-deleted.pantry.json");
                cmd.Parameters.AddWithValue("$file_hash", "feedface");
                cmd.Parameters.AddWithValue("$created_at", "2026-05-21T10:00:00Z");
                cmd.Parameters.AddWithValue("$updated_at", "2026-05-21T11:00:00Z");
                cmd.Parameters.AddWithValue("$deleted_at", "2026-05-21T11:00:00Z");
                await cmd.ExecuteNonQueryAsync();
            }

            // Cooks. cook_folder is stored as a workspace-relative
            // path with forward slashes (e.g. "Cooks/<cookId>"),
            // matching CookFolderService.ToWorkspaceRelative. The
            // log read route resolves it against the workspace root.
            string CookInsert =
                "INSERT INTO cooks (cook_id, recipe_id, status, exit_code, pid, cook_folder, " +
                "pax_script_path, pax_script_version, trigger, started_at, finished_at, " +
                "duration_seconds, error_class, error_message, created_at, updated_at, " +
                "summary_path, parent_cook_id) VALUES " +
                "($id,$recipe_id,$status,$exit_code,NULL,$cook_folder,$pax_script_path," +
                "'0.0.0-fixture','manual',$started_at,$finished_at,$duration,NULL,NULL," +
                "$created_at,$updated_at,NULL,NULL);";

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CookInsert;
                cmd.Parameters.AddWithValue("$id", "c-newer");
                cmd.Parameters.AddWithValue("$recipe_id", "r-newer");
                cmd.Parameters.AddWithValue("$status", "succeeded");
                cmd.Parameters.AddWithValue("$exit_code", 0);
                cmd.Parameters.AddWithValue("$cook_folder", "Cooks/c-newer");
                cmd.Parameters.AddWithValue("$pax_script_path",
                    @"C:\Workspace\AppRoot\resources\pax\fixture.ps1");
                cmd.Parameters.AddWithValue("$started_at", "2026-05-22T11:00:00Z");
                cmd.Parameters.AddWithValue("$finished_at", "2026-05-22T11:00:30Z");
                cmd.Parameters.AddWithValue("$duration", 30.0);
                cmd.Parameters.AddWithValue("$created_at", "2026-05-22T11:00:00Z");
                cmd.Parameters.AddWithValue("$updated_at", "2026-05-22T11:00:30Z");
                await cmd.ExecuteNonQueryAsync();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = CookInsert;
                cmd.Parameters.AddWithValue("$id", "c-older");
                cmd.Parameters.AddWithValue("$recipe_id", "r-older");
                cmd.Parameters.AddWithValue("$status", "succeeded");
                cmd.Parameters.AddWithValue("$exit_code", 0);
                cmd.Parameters.AddWithValue("$cook_folder", "Cooks/c-older");
                cmd.Parameters.AddWithValue("$pax_script_path",
                    @"C:\Workspace\AppRoot\resources\pax\fixture.ps1");
                cmd.Parameters.AddWithValue("$started_at", "2026-05-20T11:00:00Z");
                cmd.Parameters.AddWithValue("$finished_at", "2026-05-20T11:00:15Z");
                cmd.Parameters.AddWithValue("$duration", 15.0);
                cmd.Parameters.AddWithValue("$created_at", "2026-05-20T11:00:00Z");
                cmd.Parameters.AddWithValue("$updated_at", "2026-05-20T11:00:15Z");
                await cmd.ExecuteNonQueryAsync();
            }

            // Auth profiles. Two -- alpha (lowercase) sorts before
            // beta under NOCASE collation.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO auth_profiles (auth_profile_id, name, mode, tenant_id, client_id, " +
                    "cred_man_target, cert_thumbprint, cert_store, description, last_verified_at, " +
                    "last_verified_result, created_at, updated_at) VALUES " +
                    "($id,$name,$mode,$tenant,$client,$cred,NULL,NULL,$desc,NULL,NULL," +
                    "$created_at,$updated_at);";
                cmd.Parameters.AddWithValue("$id", "ap-alpha");
                cmd.Parameters.AddWithValue("$name", "alpha profile");
                cmd.Parameters.AddWithValue("$mode", "client_secret_credential");
                cmd.Parameters.AddWithValue("$tenant", "tenant-alpha");
                cmd.Parameters.AddWithValue("$client", "client-alpha");
                cmd.Parameters.AddWithValue("$cred", "PAXCookbook|ap-alpha|clientSecret");
                cmd.Parameters.AddWithValue("$desc", "alpha desc");
                cmd.Parameters.AddWithValue("$created_at", "2026-05-22T09:00:00Z");
                cmd.Parameters.AddWithValue("$updated_at", "2026-05-22T09:00:00Z");
                await cmd.ExecuteNonQueryAsync();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO auth_profiles (auth_profile_id, name, mode, tenant_id, client_id, " +
                    "cred_man_target, cert_thumbprint, cert_store, description, last_verified_at, " +
                    "last_verified_result, created_at, updated_at) VALUES " +
                    "($id,$name,$mode,$tenant,$client,NULL,$thumb,$store,NULL,NULL,NULL," +
                    "$created_at,$updated_at);";
                cmd.Parameters.AddWithValue("$id", "ap-beta");
                cmd.Parameters.AddWithValue("$name", "Beta Profile");
                cmd.Parameters.AddWithValue("$mode", "client_certificate_credential");
                cmd.Parameters.AddWithValue("$tenant", "tenant-beta");
                cmd.Parameters.AddWithValue("$client", "client-beta");
                cmd.Parameters.AddWithValue("$thumb", "DEADBEEF");
                cmd.Parameters.AddWithValue("$store", "CurrentUser");
                cmd.Parameters.AddWithValue("$created_at", "2026-05-21T09:00:00Z");
                cmd.Parameters.AddWithValue("$updated_at", "2026-05-21T09:00:00Z");
                await cmd.ExecuteNonQueryAsync();
            }

            await conn.CloseAsync();
            // Microsoft.Data.Sqlite caches the SQLitePCLRaw provider's
            // handle; ensure the writer drops its handle so the
            // ReadOnly opens in the routes can see the file.
            SqliteConnection.ClearAllPools();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup -- temp dir survival is harmless.
            }
            return ValueTask.CompletedTask;
        }
    }
}
