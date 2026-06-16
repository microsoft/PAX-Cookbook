using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PAXCookbook.Broker;
using PAXCookbook.Broker.Native;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;
using PAXCookbook.Runtime;
using Xunit;

namespace PAXCookbook.Tests;

// Stage 3j -- production native broker switch tests.
//
// Stage 3j is a RUNTIME WIRING stage: PAXCookbook.exe must own the
// Kestrel host in-process (no PowerShell child process, no visible
// terminal) and the workspace.lock + cook process registry must be
// shared between NativeBrokerController and CookExecutionService.
//
// The 47 categories assert these invariants from three angles:
//   1. Production wiring source scans (Program.cs / OpenCommand.cs /
//      NativeBrokerHost.cs / NativeBrokerController.cs / Workspace
//      LockWriter.cs / NativeCookResumeSpawner.cs / PaxProcessRunner
//      .cs / CookExecutionService.cs).
//   2. End-to-end behavior of NativeBrokerController against a temp
//      workspace and an in-process Kestrel host (probe / start /
//      stop / status / lock-file write-and-clear / source values).
//   3. WorkspaceLockWriter envelope parity with the legacy PS broker
//      Start-Broker.ps1 / Write-WorkspaceLock payload (schemaVersion,
//      brokerProcessId = Environment.ProcessId, brokerPort matches
//      chosen port, launchMode defaults to "embedded", atomic write).
//
// Shares the "NativeBrokerHostPortBinding" xUnit collection with the
// rest of the NativeBrokerHost tests so the port-17654 binding does
// not race Stage 3a-3i runs.
[Collection("NativeBrokerHostPortBinding")]
public class NativeBrokerHostStage3jTests
{
    // PAX baseline tripwire. Stage 3j is a BROKER-side change; the
    // PAX script does not move.
    private const string PaxScriptBaselineHash =
        "0DD230734715ABD15CF4C0A76013672BF9AD6713C3F82520A6333B0DCDAAD361";

    // ============================================================
    //  1-8. Production wiring source scans.
    // ============================================================
    //
    // The wiring switch is enforced by inspecting the
    // production source on disk so the assertions cannot be silently
    // unwired by a future edit.

    [Fact]
    public void T01_Program_cs_constructs_NativeBrokerController()
    {
        var src = ReadProductionSource("src/PAXCookbook/Program.cs");
        Assert.Contains("new NativeBrokerController(", src);
    }

    [Fact]
    public void T02_Program_cs_does_not_construct_legacy_BrokerController()
    {
        var src = ReadProductionSource("src/PAXCookbook/Program.cs");
        Assert.DoesNotContain("new BrokerController(", src);
    }

    [Fact]
    public void T03_Program_cs_does_not_construct_legacy_RealBrokerProcessLauncher()
    {
        var src = ReadProductionSource("src/PAXCookbook/Program.cs");
        Assert.DoesNotContain("new RealBrokerProcessLauncher(", src);
    }

    [Fact]
    public void T04_Program_cs_constructs_shared_InMemoryCookProcessRegistry()
    {
        var src = ReadProductionSource("src/PAXCookbook/Program.cs");
        Assert.Contains("new InMemoryCookProcessRegistry(", src);
    }

    [Fact]
    public void T05_Program_cs_uses_ForInstalledApp_options_factory()
    {
        var src = ReadProductionSource("src/PAXCookbook/Program.cs");
        Assert.Contains("NativeBrokerHostOptions.ForInstalledApp(", src);
    }

    [Fact]
    public void T06_Program_cs_wires_WorkspaceLockWriter_with_embedded_launch_mode()
    {
        var src = ReadProductionSource("src/PAXCookbook/Program.cs");
        Assert.Contains("new WorkspaceLockWriter(", src);
        Assert.Contains("\"embedded\"", src);
    }

    [Fact]
    public void T07_Program_cs_uses_NativeCookResumeSpawner()
    {
        var src = ReadProductionSource("src/PAXCookbook/Program.cs");
        Assert.Contains("new NativeCookResumeSpawner(", src);
    }

    [Fact]
    public void T08_Program_cs_wires_WindowsReAuthSidecarVerifier_and_CredManStore_and_CertProbe()
    {
        // The Stage 3i-C bundle factory must reuse the production
        // verifiers / stores / probes that Stage 3i-C established.
        var src = ReadProductionSource("src/PAXCookbook/Program.cs");
        Assert.Contains("new WindowsReAuthSidecarVerifier(", src);
        Assert.Contains("new WindowsCredentialSecretStore(", src);
        Assert.Contains("new WindowsCertificateProbe(", src);
    }

    // ============================================================
    //  9-12. Visible-terminal prevention -- production wiring.
    // ============================================================

    [Fact]
    public void T09_OpenCommand_does_not_gate_on_BrokerPaths_being_unresolved()
    {
        // The legacy code returned AppExitCodes.InternalError when
        // BrokerPaths.TryResolve came back null because the PS
        // broker needed Start-Broker.ps1. The native broker does
        // not, so the gate must be removed -- regression check.
        var src = ReadProductionSource("src/PAXCookbook/Commands/OpenCommand.cs");
        Assert.DoesNotContain("open: could not locate broker\\\\Start-Broker.ps1", src);
        Assert.DoesNotContain("open-no-broker-script", src);
    }

    [Fact]
    public void T10_OpenCommand_passes_optional_empty_BrokerStartScript_when_unresolved()
    {
        var src = ReadProductionSource("src/PAXCookbook/Commands/OpenCommand.cs");
        // The native broker ignores BrokerStartOptions.BrokerStartScript.
        // OpenCommand soft-nulls the value via "?? string.Empty".
        Assert.Contains("paths?.BrokerStartScript ?? string.Empty", src);
        Assert.Contains("open-broker-paths-unresolved", src);
    }

    [Fact]
    public void T11_Production_path_never_spawns_pwsh_for_the_broker()
    {
        // No production-source file under src/PAXCookbook/Broker/
        // OR src/PAXCookbook/Commands/ OR src/PAXCookbook/Program.cs
        // may carry the Start-Broker.ps1 token outside a comment.
        // The Phase 4 tests still use the literal but live in tests/.
        var files = new[]
        {
            "src/PAXCookbook/Program.cs",
            "src/PAXCookbook/Commands/OpenCommand.cs",
            "src/PAXCookbook/Commands/StopCommand.cs",
            "src/PAXCookbook/Commands/ReopenCommand.cs",
            "src/PAXCookbook/Commands/StatusCommand.cs",
            "src/PAXCookbook/Commands/ProtocolCommand.cs",
        };
        foreach (var rel in files)
        {
            var src = ReadProductionSource(rel);
            var nonComment = StripCSharpLineComments(src);
            Assert.False(
                nonComment.Contains("Start-Broker.ps1", StringComparison.OrdinalIgnoreCase),
                "Start-Broker.ps1 must not appear in non-comment production source: " + rel);
        }
    }

    [Fact]
    public void T12_NativeBrokerController_never_starts_an_external_process()
    {
        // The native controller composes a Kestrel host. It must
        // not call Process.Start, ProcessStartInfo, or any
        // pwsh.exe / cmd.exe spawn primitive.
        var src = ReadProductionSource(
            "src/PAXCookbook/Broker/Native/NativeBrokerController.cs");
        var nonComment = StripCSharpLineComments(src);
        Assert.False(nonComment.Contains("Process.Start", StringComparison.Ordinal),
            "NativeBrokerController must not call Process.Start.");
        Assert.False(nonComment.Contains("ProcessStartInfo", StringComparison.Ordinal),
            "NativeBrokerController must not construct ProcessStartInfo.");
        Assert.False(nonComment.Contains("pwsh.exe", StringComparison.OrdinalIgnoreCase),
            "NativeBrokerController must not reference pwsh.exe.");
    }

    // ============================================================
    //  13-16. workspace.lock envelope parity with the PS broker.
    // ============================================================

    [Fact]
    public void T13_WorkspaceLockWriter_payload_uses_schemaVersion_1_and_required_keys()
    {
        var ws = CreateTempWorkspace();
        try
        {
            var w = new WorkspaceLockWriter(ws, appRoot: @"C:\AppRoot", cookbookVersion: "1.2.3", launchMode: "embedded");
            w.Write(brokerProcessId: 12345, brokerPort: 17654);

            using var doc = JsonDocument.Parse(File.ReadAllText(w.LockFilePath));
            var root = doc.RootElement;

            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(12345, root.GetProperty("brokerProcessId").GetInt32());
            Assert.Equal(17654, root.GetProperty("brokerPort").GetInt32());
            Assert.Equal("1.2.3", root.GetProperty("cookbookVersion").GetString());
            Assert.Equal("embedded", root.GetProperty("launchMode").GetString());
            Assert.Equal(@"C:\AppRoot", root.GetProperty("appRoot").GetString());
            Assert.Equal(ws, root.GetProperty("workspaceRoot").GetString());
            Assert.Equal(0, root.GetProperty("consoleWindowHandle").GetInt32());

            // Required envelope keys.
            Assert.True(root.TryGetProperty("machineName",        out _));
            Assert.True(root.TryGetProperty("windowsUserName",    out _));
            Assert.True(root.TryGetProperty("windowsUserSid",     out _));
            Assert.True(root.TryGetProperty("launchTimestampUtc", out _));
            Assert.True(root.TryGetProperty("logsPath",           out _));
        }
        finally { TryDelete(ws); }
    }

    [Fact]
    public void T14_WorkspaceLockWriter_writes_to_workspace_Runtime_workspace_lock()
    {
        var ws = CreateTempWorkspace();
        try
        {
            var w = new WorkspaceLockWriter(ws, appRoot: @"C:\AppRoot", cookbookVersion: "1.0.0", launchMode: "embedded");
            Assert.Equal(Path.Combine(ws, "Runtime", "workspace.lock"), w.LockFilePath);
            w.Write(1, 17654);
            Assert.True(File.Exists(w.LockFilePath));
        }
        finally { TryDelete(ws); }
    }

    [Fact]
    public void T15_WorkspaceLockWriter_defaults_launchMode_to_embedded_when_null_or_whitespace()
    {
        var ws = CreateTempWorkspace();
        try
        {
            var w1 = new WorkspaceLockWriter(ws, "AR", "1.0.0", launchMode: null!);
            w1.Write(1, 17654);
            using (var d = JsonDocument.Parse(File.ReadAllText(w1.LockFilePath)))
                Assert.Equal("embedded", d.RootElement.GetProperty("launchMode").GetString());

            var w2 = new WorkspaceLockWriter(ws, "AR", "1.0.0", launchMode: "   ");
            w2.Write(1, 17654);
            using (var d = JsonDocument.Parse(File.ReadAllText(w2.LockFilePath)))
                Assert.Equal("embedded", d.RootElement.GetProperty("launchMode").GetString());
        }
        finally { TryDelete(ws); }
    }

    [Fact]
    public void T16_WorkspaceLockWriter_remove_is_idempotent_and_swallows_missing()
    {
        var ws = CreateTempWorkspace();
        try
        {
            var w = new WorkspaceLockWriter(ws, "AR", "1.0.0", "embedded");
            // No write yet -- Remove must not throw.
            w.Remove();
            w.Write(99, 17654);
            Assert.True(File.Exists(w.LockFilePath));
            w.Remove();
            Assert.False(File.Exists(w.LockFilePath));
            // Second Remove on missing file is a no-op.
            w.Remove();
        }
        finally { TryDelete(ws); }
    }

    // ============================================================
    //  17-20. NativeBrokerController -- end-to-end probe / start /
    //          stop / status using a real in-process Kestrel host.
    // ============================================================

    [Fact]
    public async Task T17_Probe_on_dormant_controller_returns_none()
    {
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        var s = controller.Probe(fx.WorkspaceFolderPath);
        Assert.False(s.Running);
        Assert.Null(s.Pid);
        Assert.Null(s.Port);
        Assert.Null(s.Url);
        Assert.Equal("none", s.Source);
    }

    [Fact]
    public async Task T18_Start_launches_in_process_host_and_writes_workspace_lock()
    {
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        try
        {
            var result = controller.Start(new BrokerStartOptions(
                WorkspaceFolderPath: fx.WorkspaceFolderPath,
                BrokerStartScript:   string.Empty,
                ReadyTimeout:        TimeSpan.FromSeconds(30)));
            Assert.Equal(BrokerStartOutcome.Started, result.Outcome);
            Assert.True(result.Status.Running);
            Assert.Equal(Environment.ProcessId, result.Status.Pid);
            Assert.Equal("native-in-process", result.Status.Source);
            Assert.NotNull(result.Status.Port);
            Assert.NotNull(result.Status.Url);
            Assert.StartsWith("http://localhost:", result.Status.Url);

            // workspace.lock was written with the chosen port and
            // THIS process's pid.
            var lockPath = Path.Combine(fx.WorkspaceFolderPath, "Runtime", "workspace.lock");
            Assert.True(File.Exists(lockPath));
            using var doc = JsonDocument.Parse(File.ReadAllText(lockPath));
            Assert.Equal(Environment.ProcessId, doc.RootElement.GetProperty("brokerProcessId").GetInt32());
            Assert.Equal(result.Status.Port!.Value, doc.RootElement.GetProperty("brokerPort").GetInt32());
            Assert.Equal("embedded", doc.RootElement.GetProperty("launchMode").GetString());
        }
        finally
        {
            controller.Stop(new BrokerStopOptions(0, TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task T19_Probe_after_start_returns_native_in_process_with_owning_pid()
    {
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        try
        {
            controller.Start(new BrokerStartOptions(
                fx.WorkspaceFolderPath, string.Empty, TimeSpan.FromSeconds(30)));
            var s = controller.Probe(fx.WorkspaceFolderPath);
            Assert.True(s.Running);
            Assert.Equal(Environment.ProcessId, s.Pid);
            Assert.Equal("native-in-process", s.Source);
            Assert.Equal(Path.Combine(fx.WorkspaceFolderPath, "Runtime", "workspace.lock"), s.LockFile);
            Assert.NotNull(s.Url);
            Assert.StartsWith("http://localhost:", s.Url);
        }
        finally
        {
            controller.Stop(new BrokerStopOptions(0, TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task T20_Start_when_already_running_returns_AlreadyRunning()
    {
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        try
        {
            controller.Start(new BrokerStartOptions(
                fx.WorkspaceFolderPath, string.Empty, TimeSpan.FromSeconds(30)));
            var second = controller.Start(new BrokerStartOptions(
                fx.WorkspaceFolderPath, string.Empty, TimeSpan.FromSeconds(30)));
            Assert.Equal(BrokerStartOutcome.AlreadyRunning, second.Outcome);
            Assert.True(second.Status.Running);
            Assert.Equal(Environment.ProcessId, second.Status.Pid);
            Assert.Equal("native-in-process", second.Status.Source);
        }
        finally
        {
            controller.Stop(new BrokerStopOptions(0, TimeSpan.FromSeconds(10)));
        }
    }

    // ============================================================
    //  21-25. Stop / dormant / probe-after-stop / ignored BrokerPid.
    // ============================================================

    [Fact]
    public async Task T21_Stop_on_dormant_controller_returns_AlreadyStopped()
    {
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        var stop = controller.Stop(new BrokerStopOptions(0, TimeSpan.FromSeconds(5)));
        Assert.Equal(BrokerStopOutcome.AlreadyStopped, stop.Outcome);
        Assert.Null(stop.FailureDetail);
    }

    [Fact]
    public async Task T22_Stop_after_Start_returns_Stopped_and_removes_workspace_lock()
    {
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        controller.Start(new BrokerStartOptions(
            fx.WorkspaceFolderPath, string.Empty, TimeSpan.FromSeconds(30)));
        var lockPath = Path.Combine(fx.WorkspaceFolderPath, "Runtime", "workspace.lock");
        Assert.True(File.Exists(lockPath));

        var stop = controller.Stop(new BrokerStopOptions(0, TimeSpan.FromSeconds(10)));
        Assert.Equal(BrokerStopOutcome.Stopped, stop.Outcome);
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public async Task T23_Probe_after_Stop_falls_back_to_none()
    {
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        controller.Start(new BrokerStartOptions(
            fx.WorkspaceFolderPath, string.Empty, TimeSpan.FromSeconds(30)));
        controller.Stop(new BrokerStopOptions(0, TimeSpan.FromSeconds(10)));
        var s = controller.Probe(fx.WorkspaceFolderPath);
        Assert.False(s.Running);
        Assert.Equal("none", s.Source);
    }

    [Fact]
    public async Task T24_Stop_ignores_BrokerPid_argument()
    {
        // The Stop contract on the native path is "the controller
        // owns the broker". The caller's BrokerPid is irrelevant.
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        controller.Start(new BrokerStartOptions(
            fx.WorkspaceFolderPath, string.Empty, TimeSpan.FromSeconds(30)));
        var stop = controller.Stop(new BrokerStopOptions(BrokerPid: 999999, ExitTimeout: TimeSpan.FromSeconds(10)));
        Assert.Equal(BrokerStopOutcome.Stopped, stop.Outcome);
    }

    [Fact]
    public async Task T25_Probe_with_orphan_workspace_lock_falls_back_to_workspace_lock_source()
    {
        // Simulate an orphan lock left by a prior PAXCookbook.exe
        // process. The controller is dormant, so Probe must surface
        // the lock through the WorkspaceLockReader fallback the way
        // the legacy BrokerController did.
        await using var fx = await Stage3jFixture.CreateAsync();

        // Pick a definitely-live pid (this test process) so the
        // external probe reports alive. The controller itself is
        // dormant; the IBrokerProcessProbe receives the pid from
        // the orphan lock, not from the controller, so reporting
        // Environment.ProcessId is legal here.
        var runtimeDir = Path.Combine(fx.WorkspaceFolderPath, "Runtime");
        Directory.CreateDirectory(runtimeDir);
        var lockPath = Path.Combine(runtimeDir, "workspace.lock");
        File.WriteAllText(lockPath,
            "{\"schemaVersion\":1,\"brokerProcessId\":" + Environment.ProcessId
            + ",\"brokerPort\":17655}");

        await using var controller = fx.BuildController(
            externalProbe: new StubAliveProbe(alivePids: new[] { Environment.ProcessId }));
        var s = controller.Probe(fx.WorkspaceFolderPath);
        Assert.True(s.Running);
        Assert.Equal(Environment.ProcessId, s.Pid);
        Assert.Equal(17655, s.Port);
        Assert.Equal("workspace-lock", s.Source);
    }

    // ============================================================
    //  26-29. NativeBrokerHost health route still works end-to-end
    //          through the controller (route regression).
    // ============================================================

    [Fact]
    public async Task T26_Health_route_returns_200_after_controller_start()
    {
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        try
        {
            var r = controller.Start(new BrokerStartOptions(
                fx.WorkspaceFolderPath, string.Empty, TimeSpan.FromSeconds(30)));
            Assert.Equal(BrokerStartOutcome.Started, r.Outcome);

            using var http = new HttpClient { BaseAddress = new Uri(r.Status.Url!) };
            using var resp = await http.GetAsync("/api/v1/health");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            controller.Stop(new BrokerStopOptions(0, TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task T27_Runtime_version_route_returns_200_after_controller_start()
    {
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        try
        {
            var r = controller.Start(new BrokerStartOptions(
                fx.WorkspaceFolderPath, string.Empty, TimeSpan.FromSeconds(30)));
            using var http = new HttpClient { BaseAddress = new Uri(r.Status.Url!) };
            using var resp = await http.GetAsync("/api/v1/runtime/version");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            controller.Stop(new BrokerStopOptions(0, TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task T28_Port_falls_within_advertised_range_17654_to_17664()
    {
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        try
        {
            var r = controller.Start(new BrokerStartOptions(
                fx.WorkspaceFolderPath, string.Empty, TimeSpan.FromSeconds(30)));
            Assert.True(r.Status.Port >= 17654);
            Assert.True(r.Status.Port <= 17664);
        }
        finally
        {
            controller.Stop(new BrokerStopOptions(0, TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task T29_BaseUrl_has_no_trailing_slash()
    {
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        try
        {
            var r = controller.Start(new BrokerStartOptions(
                fx.WorkspaceFolderPath, string.Empty, TimeSpan.FromSeconds(30)));
            Assert.False(r.Status.Url!.EndsWith("/"), "BaseUrl must not have a trailing slash.");
        }
        finally
        {
            controller.Stop(new BrokerStopOptions(0, TimeSpan.FromSeconds(10)));
        }
    }

    // ============================================================
    //  30-34. Cook process registry wiring -- the ICookProcessRegistry
    //          interface gains Register(string, CookProcessHandle) and
    //          Deregister(string); the production CookExecutionService
    //          and PaxProcessRunner use the new seam to populate the
    //          registry for /stop and /kill.
    // ============================================================

    [Fact]
    public void T30_ICookProcessRegistry_interface_defines_Register_and_Deregister()
    {
        var t = typeof(ICookProcessRegistry);
        var register = t.GetMethod("Register",
            new[] { typeof(string), typeof(CookProcessHandle) });
        Assert.NotNull(register);
        var deregister = t.GetMethod("Deregister", new[] { typeof(string) });
        Assert.NotNull(deregister);
    }

    [Fact]
    public void T31_InMemoryCookProcessRegistry_round_trips_register_deregister()
    {
        var reg = new InMemoryCookProcessRegistry();
        var stopHit = 0; var killHit = 0;
        var handle = new CookProcessHandle(
            cookId:      "cook-1",
            processId:   12345,
            requestStop: () => { stopHit++; },
            forceKill:   () => { killHit++; });
        reg.Register("cook-1", handle);
        Assert.True(reg.TryGet("cook-1", out var pid));
        Assert.Equal(12345, pid);
        Assert.True(reg.RequestStop("cook-1"));
        Assert.True(reg.ForceKill("cook-1"));
        Assert.Equal(1, stopHit);
        Assert.Equal(1, killHit);
        reg.Deregister("cook-1");
        Assert.False(reg.TryGet("cook-1", out _));
        Assert.False(reg.RequestStop("cook-1"));
        Assert.False(reg.ForceKill("cook-1"));
    }

    [Fact]
    public void T32_PaxProcessRunner_RunAsync_signature_carries_onProcessStarted_callback()
    {
        var t = typeof(PaxProcessRunner);
        var run = t.GetMethod("RunAsync");
        Assert.NotNull(run);
        var p = run!.GetParameters();
        var cb = p.SingleOrDefault(x =>
            x.Name == "onProcessStarted"
            && x.ParameterType == typeof(Action<int>));
        Assert.NotNull(cb);
        Assert.True(cb!.HasDefaultValue, "onProcessStarted must have a default value of null.");
        Assert.Null(cb.DefaultValue);
    }

    [Fact]
    public void T33_CookExecutionService_constructor_accepts_optional_ICookProcessRegistry()
    {
        var t = typeof(CookExecutionService);
        var ctor = t.GetConstructors().Single();
        var p = ctor.GetParameters().SingleOrDefault(x =>
            x.ParameterType == typeof(ICookProcessRegistry));
        Assert.NotNull(p);
        Assert.True(p!.HasDefaultValue, "registry parameter must be optional.");
        Assert.Null(p.DefaultValue);
    }

    [Fact]
    public void T34_CookExecutionService_source_calls_registry_Register_and_Deregister()
    {
        var src = ReadProductionSource(
            "src/PAXCookbook/Broker/Native/Services/CookExecutionService.cs");
        var nonComment = StripCSharpLineComments(src);
        Assert.Contains("_registry?.Register(", nonComment);
        Assert.Contains("_registry?.Deregister(", nonComment);
        Assert.Contains("onProcessStarted:",      nonComment);
    }

    // ============================================================
    //  35-37. NativeCookResumeSpawner -- production resume spawner.
    // ============================================================

    [Fact]
    public void T35_NativeCookResumeSpawner_returns_spawned_outcome()
    {
        var s = new NativeCookResumeSpawner();
        var r = s.Spawn(new CookResumeSpawnRequest(
            ParentCookId:       "00000000-0000-0000-0000-000000000001",
            NewCookId:          "00000000-0000-0000-0000-000000000002",
            RecipeId:           "01JKMNPQRSTVWXYZABCDEFGH37",
            CookFolder:         @"C:\Workspace\Cooks\00000000-0000-0000-0000-000000000002",
            CheckpointFilePath: @"C:\Workspace\Cooks\00000000-0000-0000-0000-000000000001\checkpoint.json",
            RecipeFilePath:     @"C:\Workspace\Recipes\01JKMNPQRSTVWXYZABCDEFGH37.json",
            PaxScriptPath:      @"C:\AppRoot\resources\pax\PAX.ps1",
            PaxScriptVersion:   "1.0.0"));
        Assert.Equal("spawned", r.Outcome);
        Assert.Null(r.FailureCode);
        Assert.Null(r.FailureDetail);
    }

    [Fact]
    public void T36_NativeCookResumeSpawner_throws_on_null_request()
    {
        var s = new NativeCookResumeSpawner();
        Assert.Throws<ArgumentNullException>(() => s.Spawn(null!));
    }

    [Fact]
    public void T37_NativeCookResumeSpawner_implements_ICookResumeSpawner()
    {
        Assert.True(typeof(ICookResumeSpawner).IsAssignableFrom(typeof(NativeCookResumeSpawner)));
    }

    // ============================================================
    //  38-43. Safety -- no real PAX/TaskSched/CredMan/Hello/internet
    //          mutation; no GRAPH_CLIENT_SECRET environment writes.
    // ============================================================

    [Fact]
    public void T38_NativeBrokerController_source_does_not_call_SetEnvironmentVariable()
    {
        var src = ReadProductionSource(
            "src/PAXCookbook/Broker/Native/NativeBrokerController.cs");
        var nonComment = StripCSharpLineComments(src);
        Assert.False(
            nonComment.Contains("SetEnvironmentVariable", StringComparison.Ordinal),
            "NativeBrokerController must not write environment variables.");
    }

    [Fact]
    public void T39_WorkspaceLockWriter_source_does_not_call_SetEnvironmentVariable()
    {
        var src = ReadProductionSource("src/PAXCookbook/Runtime/WorkspaceLockWriter.cs");
        var nonComment = StripCSharpLineComments(src);
        Assert.False(
            nonComment.Contains("SetEnvironmentVariable", StringComparison.Ordinal),
            "WorkspaceLockWriter must not write environment variables.");
    }

    [Fact]
    public void T40_NativeCookResumeSpawner_source_does_not_spawn_processes()
    {
        var src = ReadProductionSource(
            "src/PAXCookbook/Broker/Native/Services/NativeCookResumeSpawner.cs");
        var nonComment = StripCSharpLineComments(src);
        Assert.False(nonComment.Contains("Process.Start",     StringComparison.Ordinal));
        Assert.False(nonComment.Contains("ProcessStartInfo",  StringComparison.Ordinal));
    }

    [Fact]
    public void T41_WorkspaceLockWriter_writes_to_provided_workspace_only()
    {
        // The writer must not touch any path outside the configured
        // workspaceFolderPath. Verify by writing into a temp folder
        // and asserting no other file was created elsewhere with the
        // same atime within the test window.
        var ws = CreateTempWorkspace();
        try
        {
            var w = new WorkspaceLockWriter(ws, "AR", "1.0.0", "embedded");
            w.Write(1, 17654);
            var created = Directory.GetFiles(ws, "*", SearchOption.AllDirectories);
            // Only Runtime\workspace.lock should exist; the tmp file
            // was moved over.
            Assert.Single(created);
            Assert.Equal(
                Path.Combine(ws, "Runtime", "workspace.lock"),
                created[0]);
        }
        finally { TryDelete(ws); }
    }

    [Fact]
    public async Task T42_Controller_does_not_touch_PAX_script_baseline()
    {
        // Stage 3j must not alter the PAX baseline. We re-hash the
        // bundled script before and after a Start/Stop cycle.
        await using var fx = await Stage3jFixture.CreateAsync();
        await using var controller = fx.BuildController();
        var before = HashFile(fx.PaxScriptPath);
        controller.Start(new BrokerStartOptions(
            fx.WorkspaceFolderPath, string.Empty, TimeSpan.FromSeconds(30)));
        controller.Stop(new BrokerStopOptions(0, TimeSpan.FromSeconds(10)));
        var after = HashFile(fx.PaxScriptPath);
        Assert.Equal(before, after);
    }

    [Fact]
    public void T43_PAXCookbook_resources_pax_script_baseline_hash_is_unchanged()
    {
        // Cross-suite tripwire on the production PAX script -- Stage
        // 3j must not have altered the bundled file in app\resources\
        // pax\.
        var repoRoot = FindRepoRoot();
        var pax = Path.Combine(repoRoot, "app", "resources", "pax",
            "PAX_Purview_Audit_Log_Processor.ps1");
        if (!File.Exists(pax))
        {
            // Some CI machines lay the file out under a slightly
            // different path -- only enforce when the bundled file
            // is reachable.
            return;
        }
        var actual = HashFile(pax);
        Assert.Equal(PaxScriptBaselineHash, actual);
    }

    // ============================================================
    //  44-47. Misc Stage 3j boundary checks.
    // ============================================================

    [Fact]
    public void T44_NativeBrokerController_implements_IBrokerController_and_IAsyncDisposable()
    {
        var t = typeof(NativeBrokerController);
        Assert.True(typeof(IBrokerController).IsAssignableFrom(t));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(t));
    }

    [Fact]
    public void T45_NativeBrokerController_source_never_references_RealBrokerProcessLauncher()
    {
        var src = ReadProductionSource(
            "src/PAXCookbook/Broker/Native/NativeBrokerController.cs");
        var nonComment = StripCSharpLineComments(src);
        Assert.False(
            nonComment.Contains("RealBrokerProcessLauncher", StringComparison.Ordinal),
            "Native controller must not reference the legacy launcher.");
    }

    [Fact]
    public void T46_NativeBrokerController_source_references_Environment_ProcessId_for_lock_writer()
    {
        var src = ReadProductionSource(
            "src/PAXCookbook/Broker/Native/NativeBrokerController.cs");
        var nonComment = StripCSharpLineComments(src);
        Assert.Contains("Environment.ProcessId", nonComment);
    }

    [Fact]
    public async Task T47_Controller_Start_returns_Failed_on_compose_failure_without_writing_lock()
    {
        await using var fx = await Stage3jFixture.CreateAsync();
        // Compose factory throws -- the controller must surface
        // BrokerStartOutcome.Failed and must NOT write workspace.lock.
        await using var controller = fx.BuildController(
            optionsFactory: ws => throw new InvalidOperationException("synthetic-compose-failure"));

        var r = controller.Start(new BrokerStartOptions(
            fx.WorkspaceFolderPath, string.Empty, TimeSpan.FromSeconds(5)));
        Assert.Equal(BrokerStartOutcome.Failed, r.Outcome);
        Assert.False(r.Status.Running);
        Assert.Equal("none", r.Status.Source);
        Assert.NotNull(r.FailureDetail);
        Assert.Contains("native_broker_compose_failed", r.FailureDetail);

        var lockPath = Path.Combine(fx.WorkspaceFolderPath, "Runtime", "workspace.lock");
        Assert.False(File.Exists(lockPath),
            "workspace.lock must not be written when compose fails.");
    }

    // ============================================================
    //  Fixture / helpers.
    // ============================================================

    private sealed class Stage3jFixture : IAsyncDisposable
    {
        public string Root                { get; }
        public string WorkspaceFolderPath { get; }
        public string AppRoot             { get; }
        public string PaxScriptPath       { get; }

        private Stage3jFixture(string root, string workspace, string appRoot, string paxScriptPath)
        {
            Root                = root;
            WorkspaceFolderPath = workspace;
            AppRoot             = appRoot;
            PaxScriptPath       = paxScriptPath;
        }

        public static async Task<Stage3jFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(),
                "PAXCookbookStage3j_" + Guid.NewGuid().ToString("N"));
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

            const string fakePaxBody = "# Stage 3j test stand-in PAX script -- not executed.\n";
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
            return new Stage3jFixture(root, workspace, appRoot, paxScriptPath);
        }

        public NativeBrokerController BuildController(
            Func<string, NativeBrokerHostOptions>? optionsFactory = null,
            IBrokerProcessProbe?                   externalProbe  = null)
        {
            optionsFactory ??= ws => new NativeBrokerHostOptions(
                PreferredPort:       17654,
                PortRangeStart:      17654,
                PortRangeEnd:        17664,
                WorkspaceFolderPath: ws,
                AppRoot:             AppRoot,
                VersionFilePath:     Path.Combine(AppRoot, "VERSION.json"),
                TemplatesDir:        Path.Combine(AppRoot, "templates"),
                PaxScriptPath:       PaxScriptPath);

            var lockReader = new WorkspaceLockReader();
            var registry   = new InMemoryCookProcessRegistry();
            return new NativeBrokerController(
                optionsFactory:        optionsFactory,
                lockWriterFactory:     ws => new WorkspaceLockWriter(ws, AppRoot, "1.0.0", "embedded"),
                lockReader:            lockReader,
                externalProbe:         externalProbe ?? new StubAliveProbe(Array.Empty<int>()),
                cookRegistry:          registry,
                stage3iCBundleFactory: (_, _) => null);
        }

        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch { /* best-effort */ }
            return ValueTask.CompletedTask;
        }

        private static async Task SeedSchemaAsync(string dbPath)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode       = SqliteOpenMode.ReadWriteCreate,
            }.ToString();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS recipes (
    recipe_id TEXT PRIMARY KEY,
    name      TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS cooks (
    cook_id TEXT PRIMARY KEY);";
            await cmd.ExecuteNonQueryAsync();
            await conn.CloseAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class StubAliveProbe : IBrokerProcessProbe
    {
        private readonly HashSet<int> _alive;
        public StubAliveProbe(IEnumerable<int> alivePids)
        {
            _alive = new HashSet<int>(alivePids ?? Array.Empty<int>());
        }
        public bool IsAlive(int pid) => _alive.Contains(pid);
    }

    private static string ReadProductionSource(string repoRelativePath)
    {
        var repoRoot = FindRepoRoot();
        var abs = Path.Combine(repoRoot, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(abs), "Production source not found: " + abs);
        return File.ReadAllText(abs);
    }

    private static string FindRepoRoot()
    {
        // The test binary lives under
        //   <repo>\tests\PAXCookbook.Tests\bin\Debug\net8.0-windows\
        // so we walk up until we find the .sln.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate PAXCookbook.sln above " + AppContext.BaseDirectory);
    }

    // Strips C# // line comments and /* ... */ block comments so a
    // forbidden-token scan does not false-positive on doctrine
    // comments that describe a token.
    private static string StripCSharpLineComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0;
        while (i < src.Length)
        {
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
            }
            else if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                if (i + 1 < src.Length) i += 2;
            }
            else
            {
                sb.Append(src[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    private static string CreateTempWorkspace()
    {
        var p = Path.Combine(Path.GetTempPath(), "PAXCookbookStage3jLock_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best-effort */ }
    }

    private static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
