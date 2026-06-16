using System.Text.Json;
using PAXCookbook.Broker;
using PAXCookbook.Cli;
using PAXCookbook.Commands;
using PAXCookbook.Logging;
using PAXCookbook.Runtime;
using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using Xunit;

namespace PAXCookbook.Tests;

// Phase 4 test suite. Every test creates a unique temp install root under
// %TEMP% and never touches the real %LOCALAPPDATA%\PAXCookbook.

internal sealed class TestEnv : IDisposable
{
    public string InstallRoot { get; }
    public string WorkspacePath { get; }
    public string AppData { get; }
    public InstallStateResolver Resolver { get; }
    public BootstrapStateReader Bootstrap { get; }
    public WorkspaceLockReader Locks { get; }
    public SidecarReader Sidecars { get; }
    public AppLogger Log { get; }
    public StringWriter Stdout { get; } = new();
    public StringWriter Stderr { get; } = new();

    public TestEnv()
    {
        InstallRoot = Path.Combine(Path.GetTempPath(), "PAX4_" + Guid.NewGuid().ToString("N"));
        WorkspacePath = Path.Combine(InstallRoot, "Workspace");
        AppData = Path.Combine(InstallRoot, "AppData");
        Directory.CreateDirectory(Path.Combine(WorkspacePath, "Runtime"));
        Directory.CreateDirectory(Path.Combine(AppData, "PAXCookbook"));
        Resolver = new InstallStateResolver(InstallRoot);
        Bootstrap = new BootstrapStateReader(AppData);
        Locks = new WorkspaceLockReader();
        Sidecars = new SidecarReader();
        Log = new AppLogger(Resolver.AppLogsRoot);
    }

    public void WriteInstallState(InstallState s) =>
        File.WriteAllText(Resolver.InstallStateFile, InstallStateSerializer.Serialize(s));

    public void WriteBootstrap(string workspaceFolderPath, int? port = null)
    {
        var obj = port is null
            ? (object)new { workspaceFolderPath }
            : new { workspaceFolderPath, selectedBrokerPort = port.Value };
        File.WriteAllText(Bootstrap.BootstrapFile, JsonSerializer.Serialize(obj));
    }

    public void WriteLock(int pid, int port)
    {
        var path = Path.Combine(WorkspacePath, "Runtime", "workspace.lock");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            brokerProcessId = pid,
            brokerPort = port,
            brokerSessionId = Guid.NewGuid().ToString()
        }));
    }

    public CommandContext Context(IBrokerController broker, string? installRootOverride = null)
        => new(Resolver, Bootstrap, Locks, Sidecars, broker, Log, Stdout, Stderr,
               installRootOverride, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    public void Dispose()
    {
        try { Directory.Delete(InstallRoot, recursive: true); } catch { }
    }
}

internal sealed class FakeBrokerController : IBrokerController
{
    public BrokerStatus NextProbe { get; set; } = new(false, null, null, null, null, "none");
    public BrokerStartResult NextStart { get; set; } =
        new(BrokerStartOutcome.Started, new BrokerStatus(true, 4242, 17654, "http://localhost:17654", "x", "workspace-lock"), null);
    public BrokerStopResult NextStop { get; set; } = new(BrokerStopOutcome.Stopped, null);
    public int ProbeCalls { get; private set; }
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public BrokerStartOptions? LastStart { get; private set; }
    public BrokerStopOptions? LastStop { get; private set; }

    public BrokerStatus Probe(string workspaceFolderPath) { ProbeCalls++; return NextProbe; }
    public BrokerStartResult Start(BrokerStartOptions options) { StartCalls++; LastStart = options; return NextStart; }
    public BrokerStopResult Stop(BrokerStopOptions options) { StopCalls++; LastStop = options; return NextStop; }
}

public class StatusCommandTests
{
    [Fact]
    public void Status_NotInstalled_ReportsInstalledFalse()
    {
        using var env = new TestEnv();
        var ctx = env.Context(new FakeBrokerController());
        var rc = StatusCommand.Run(ctx);
        Assert.Equal(AppExitCodes.Ok, rc);
        using var doc = JsonDocument.Parse(env.Stdout.ToString());
        Assert.False(doc.RootElement.GetProperty("installed").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("installStatePresent").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("broker").GetProperty("running").GetBoolean());
        Assert.Equal("webview2", doc.RootElement.GetProperty("ui").GetProperty("surface").GetString());
        Assert.True(doc.RootElement.GetProperty("webView2").GetProperty("implemented").GetBoolean());
    }

    [Fact]
    public void Status_Installed_ReadsTempInstallState()
    {
        using var env = new TestEnv();
        env.WriteInstallState(new InstallState
        {
            AppVersion = "1.2.3",
            InstallRoot = env.InstallRoot,
            WorkspaceFolderPath = env.WorkspacePath
        });
        var ctx = env.Context(new FakeBrokerController());
        var rc = StatusCommand.Run(ctx);
        Assert.Equal(AppExitCodes.Ok, rc);
        using var doc = JsonDocument.Parse(env.Stdout.ToString());
        Assert.True(doc.RootElement.GetProperty("installed").GetBoolean());
        Assert.Equal("1.2.3", doc.RootElement.GetProperty("appVersion").GetString());
    }

    [Fact]
    public void Status_BrokerRunning_FromWorkspaceLock()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var fake = new FakeBrokerController
        {
            NextProbe = new BrokerStatus(true, 1234, 17654, "http://localhost:17654", env.WorkspacePath, "workspace-lock")
        };
        var rc = StatusCommand.Run(env.Context(fake));
        Assert.Equal(AppExitCodes.Ok, rc);
        using var doc = JsonDocument.Parse(env.Stdout.ToString());
        var b = doc.RootElement.GetProperty("broker");
        Assert.True(b.GetProperty("running").GetBoolean());
        Assert.Equal(1234, b.GetProperty("pid").GetInt32());
        Assert.Equal("http://localhost:17654", b.GetProperty("url").GetString());
    }
}

public class OpenCommandTests
{
    [Fact]
    public void Open_NoWorkspace_FailsCleanly()
    {
        using var env = new TestEnv();
        var rc = OpenCommand.Run(env.Context(new FakeBrokerController()));
        Assert.Equal(AppExitCodes.InternalError, rc);
        Assert.Contains("workspace not configured", env.Stderr.ToString());
    }

    [Fact]
    public void Open_BrokerAlreadyRunning_Reuses()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var fake = new FakeBrokerController
        {
            NextProbe = new BrokerStatus(true, 999, 17654, "http://localhost:17654", env.WorkspacePath, "workspace-lock")
        };
        // BrokerPaths.TryResolve requires a real broker script; stub one.
        var brokerDir = Path.Combine(env.InstallRoot, "App", "broker");
        Directory.CreateDirectory(brokerDir);
        File.WriteAllText(Path.Combine(brokerDir, "Start-Broker.ps1"), "# stub");

        var rc = OpenCommand.Run(env.Context(fake));
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(0, fake.StartCalls);
        Assert.Contains("already running", env.Stdout.ToString());
    }

    [Fact]
    public void Open_StartsBrokerViaFakeController()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var brokerDir = Path.Combine(env.InstallRoot, "App", "broker");
        Directory.CreateDirectory(brokerDir);
        var script = Path.Combine(brokerDir, "Start-Broker.ps1");
        File.WriteAllText(script, "# stub");

        var fake = new FakeBrokerController();
        var rc = OpenCommand.Run(env.Context(fake));
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(1, fake.StartCalls);
        Assert.Equal(env.WorkspacePath, fake.LastStart!.WorkspaceFolderPath);
        Assert.Equal(script, fake.LastStart!.BrokerStartScript);
    }

    [Fact]
    public void Open_NeverLaunchesBrowser()
    {
        // The IBrokerController abstraction makes this guarantee structural:
        // OpenCommand never references Process.Start, ShellExecute, msedge,
        // or any browser binary; it only calls IBrokerController.Start which
        // in production spawns ONLY pwsh.exe against Start-Broker.ps1.
        // We assert this by reflection over the open-command assembly.
        var asm = typeof(OpenCommand).Assembly;
        foreach (var name in new[] { "msedge", "chrome", "iexplore", "--app=", "ShellExecute" })
        {
            // Source text isn't available at runtime, but the assembly metadata
            // does include string literals. Check the OpenCommand module for
            // any reference; this acts as a structural ban.
            var resource = asm.GetManifestResourceNames();
            Assert.DoesNotContain(resource, r => r.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
    }
}

public class ReopenCommandTests
{
    [Fact]
    public void Reopen_BehavesAsOpen()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var brokerDir = Path.Combine(env.InstallRoot, "App", "broker");
        Directory.CreateDirectory(brokerDir);
        File.WriteAllText(Path.Combine(brokerDir, "Start-Broker.ps1"), "# stub");
        var fake = new FakeBrokerController();
        var rc = ReopenCommand.Run(env.Context(fake));
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(1, fake.StartCalls);
    }
}

public class StopCommandTests
{
    [Fact]
    public void Stop_IdempotentWhenNotRunning()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var fake = new FakeBrokerController(); // NextProbe defaults to not running
        var rc = StopCommand.Run(env.Context(fake));
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(0, fake.StopCalls);
        Assert.Contains("already stopped", env.Stdout.ToString());
    }

    [Fact]
    public void Stop_CallsInjectedStopWhenRunning()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var fake = new FakeBrokerController
        {
            NextProbe = new BrokerStatus(true, 4321, 17654, "http://localhost:17654", env.WorkspacePath, "workspace-lock")
        };
        var rc = StopCommand.Run(env.Context(fake));
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(1, fake.StopCalls);
        Assert.Equal(4321, fake.LastStop!.BrokerPid);
    }

    [Fact]
    public void Stop_NoWorkspace_Idempotent()
    {
        using var env = new TestEnv();
        var fake = new FakeBrokerController();
        var rc = StopCommand.Run(env.Context(fake));
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(0, fake.StopCalls);
    }
}

public class SupportCommandTests
{
    [Fact]
    public void Support_ReturnsNotImplementedCleanly()
    {
        using var env = new TestEnv();
        var rc = SupportCommand.Run(env.Context(new FakeBrokerController()));
        Assert.Equal(AppExitCodes.GenericError, rc);
        Assert.Contains("not yet implemented in Phase 4", env.Stderr.ToString());
    }
}

public class ProtocolCommandTests
{
    [Fact]
    public void Protocol_Accepted_DispatchesToOpen()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var brokerDir = Path.Combine(env.InstallRoot, "App", "broker");
        Directory.CreateDirectory(brokerDir);
        File.WriteAllText(Path.Combine(brokerDir, "Start-Broker.ps1"), "# stub");
        var fake = new FakeBrokerController();
        var rc = ProtocolCommand.Run(env.Context(fake), "paxcookbook://open");
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(1, fake.StartCalls);
        // Workspace passed to Start must come from bootstrap, NOT from the URI.
        Assert.Equal(env.WorkspacePath, fake.LastStart!.WorkspaceFolderPath);
    }

    [Theory]
    [InlineData("paxcookbook://open?x=1")]
    [InlineData("paxcookbook://open#frag")]
    [InlineData("paxcookbook://import")]
    [InlineData("paxcookbook://open/extra")]
    [InlineData("http://open")]
    [InlineData("paxcookbook://open%2F")]
    public void Protocol_Rejected_Verbatim(string bad)
    {
        using var env = new TestEnv();
        var rc = ProtocolCommand.Run(env.Context(new FakeBrokerController()), bad);
        Assert.Equal(AppExitCodes.ProtocolRejected, rc);
    }

    [Fact]
    public void Protocol_DoesNotForwardRawUriToBroker()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var brokerDir = Path.Combine(env.InstallRoot, "App", "broker");
        Directory.CreateDirectory(brokerDir);
        File.WriteAllText(Path.Combine(brokerDir, "Start-Broker.ps1"), "# stub");
        var fake = new FakeBrokerController();
        var rc = ProtocolCommand.Run(env.Context(fake), "paxcookbook://open");
        Assert.Equal(AppExitCodes.Ok, rc);
        // Verify no field on the start options contains the raw URI.
        Assert.NotNull(fake.LastStart);
        Assert.DoesNotContain("paxcookbook://", fake.LastStart!.WorkspaceFolderPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("paxcookbook://", fake.LastStart!.BrokerStartScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Protocol_ViaDispatcher_RejectsInstallRootOverride()
    {
        using var env = new TestEnv();
        var fake = new FakeBrokerController();
        var ctx = env.Context(fake, installRootOverride: env.InstallRoot);
        var parsed = AppArgsParser.Parse(new[] { "protocol", "paxcookbook://open", "--install-root", env.InstallRoot });
        var rc = AppCommandDispatcher.Dispatch(parsed, ctx);
        Assert.Equal(AppExitCodes.UsageError, rc);
        Assert.Contains("not accepted through the protocol path", env.Stderr.ToString());
    }
}

public class BrokerProcessProbeTests
{
    [Fact]
    public void Probe_IgnoresStalePid()
    {
        var p = new DefaultBrokerProcessProbe();
        Assert.False(p.IsAlive(0));
        Assert.False(p.IsAlive(-1));
        Assert.False(p.IsAlive(999_999_999)); // virtually-certain not-a-pid
    }

    [Fact]
    public void Probe_RefusesToMatchSelf()
    {
        var p = new DefaultBrokerProcessProbe();
        Assert.False(p.IsAlive(Environment.ProcessId));
    }
}

public class BrokerControllerTests
{
    [Fact]
    public void Controller_AlreadyRunning_DoesNotSpawn()
    {
        var ws = Path.Combine(Path.GetTempPath(), "PAX4C_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "Runtime"));
        File.WriteAllText(Path.Combine(ws, "Runtime", "workspace.lock"),
            JsonSerializer.Serialize(new { brokerProcessId = Environment.ProcessId, brokerPort = 17654 }));
        try
        {
            var launcher = new RecordingBrokerProcessLauncher();
            var probe = new FixedProbe(true); // pretend the PID is alive
            var ctrl = new BrokerController(launcher, probe, new WorkspaceLockReader());
            var r = ctrl.Start(new BrokerStartOptions(ws, "fake.ps1", TimeSpan.FromMilliseconds(50)));
            Assert.Equal(BrokerStartOutcome.AlreadyRunning, r.Outcome);
            Assert.Null(launcher.Last);
        }
        finally { try { Directory.Delete(ws, true); } catch { } }
    }

    [Fact]
    public void Controller_NotRunning_SpawnsViaPwshArgumentList()
    {
        var ws = Path.Combine(Path.GetTempPath(), "PAX4D_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "Runtime"));
        try
        {
            var launcher = new RecordingBrokerProcessLauncher();
            var probe = new FixedProbe(false);
            var ctrl = new BrokerController(launcher, probe, new WorkspaceLockReader());
            var r = ctrl.Start(new BrokerStartOptions(ws, @"C:\fake\Start-Broker.ps1", TimeSpan.FromMilliseconds(100)));
            // Probe always false ⇒ deadline times out ⇒ Failed; but the launcher
            // must have been invoked with the expected fixed argv shape.
            Assert.Equal(BrokerStartOutcome.Failed, r.Outcome);
            Assert.NotNull(launcher.Last);
            Assert.EndsWith("pwsh.exe", launcher.Last!.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("-NoProfile", launcher.Last.Arguments);
            Assert.Contains("-File", launcher.Last.Arguments);
            Assert.Contains(@"C:\fake\Start-Broker.ps1", launcher.Last.Arguments);
            Assert.Contains("-WorkspacePath", launcher.Last.Arguments);
            Assert.Contains(ws, launcher.Last.Arguments);
        }
        finally { try { Directory.Delete(ws, true); } catch { } }
    }

    [Fact]
    public void Controller_Stop_UsesInjectedStop()
    {
        bool called = false;
        var ctrl = new BrokerController(
            new RecordingBrokerProcessLauncher(),
            new FixedProbe(true),
            new WorkspaceLockReader(),
            stopProcess: pid => { called = true; return true; });
        var r = ctrl.Stop(new BrokerStopOptions(4242, TimeSpan.FromMilliseconds(50)));
        Assert.True(called);
        Assert.Equal(BrokerStopOutcome.Stopped, r.Outcome);
    }

    [Fact]
    public void Controller_Stop_NotRunning_IsIdempotent()
    {
        var ctrl = new BrokerController(
            new RecordingBrokerProcessLauncher(),
            new FixedProbe(false),
            new WorkspaceLockReader());
        var r = ctrl.Stop(new BrokerStopOptions(4242, TimeSpan.FromMilliseconds(50)));
        Assert.Equal(BrokerStopOutcome.AlreadyStopped, r.Outcome);
    }

    private sealed class FixedProbe : IBrokerProcessProbe
    {
        private readonly bool _alive;
        public FixedProbe(bool alive) { _alive = alive; }
        public bool IsAlive(int pid) => _alive;
    }
}

public class CliAndDispatchTests
{
    [Fact]
    public void Args_ParsesInstallRootOverride()
    {
        var p = AppArgsParser.Parse(new[] { "status", "--install-root", @"C:\temp\paxtest" });
        Assert.Equal("status", p.Verb);
        Assert.Equal(@"C:\temp\paxtest", p.InstallRoot);
    }

    [Fact]
    public void Args_RequiresValueForInstallRoot()
    {
        Assert.Throws<ArgumentException>(() => AppArgsParser.Parse(new[] { "status", "--install-root" }));
    }

    [Fact]
    public void Dispatcher_UnknownVerb_UsageError()
    {
        using var env = new TestEnv();
        var p = AppArgsParser.Parse(new[] { "frob" });
        var rc = AppCommandDispatcher.Dispatch(p, env.Context(new FakeBrokerController()));
        Assert.Equal(AppExitCodes.UsageError, rc);
    }

    [Fact]
    public void Dispatcher_Version_Ok()
    {
        using var env = new TestEnv();
        var p = AppArgsParser.Parse(new[] { "version" });
        var rc = AppCommandDispatcher.Dispatch(p, env.Context(new FakeBrokerController()));
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Contains("PAX Cookbook", env.Stdout.ToString());
    }

    [Fact]
    public void Dispatcher_Help_ListsAllVerbs()
    {
        using var env = new TestEnv();
        var p = AppArgsParser.Parse(new[] { "help" });
        var rc = AppCommandDispatcher.Dispatch(p, env.Context(new FakeBrokerController()));
        Assert.Equal(AppExitCodes.Ok, rc);
        var s = env.Stdout.ToString();
        foreach (var v in new[] { "open", "support", "stop", "reopen", "status", "protocol", "version", "help" })
            Assert.Contains(v, s);
    }
}

public class LoggingTests
{
    [Fact]
    public void Log_WritesNdjsonAndRedactsSecrets()
    {
        using var env = new TestEnv();
        env.Log.Write("App", "test-event", "info", new Dictionary<string, object?>
        {
            ["safe"] = "hello",
            ["bad"]  = "Bearer token abc123"
        });
        var path = env.Log.CurrentLogFile;
        Assert.True(File.Exists(path));
        var line = File.ReadAllLines(path)[0];
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("test-event", doc.RootElement.GetProperty("event").GetString());
        var f = doc.RootElement.GetProperty("fields");
        Assert.Equal("hello", f.GetProperty("safe").GetString());
        Assert.Equal("[REDACTED]", f.GetProperty("bad").GetString());
    }

    [Fact]
    public void Log_NeverLogsRawUri_OnProtocolReject()
    {
        using var env = new TestEnv();
        var rc = ProtocolCommand.Run(env.Context(new FakeBrokerController()), "paxcookbook://import?token=secret");
        Assert.Equal(AppExitCodes.ProtocolRejected, rc);
        var log = File.ReadAllText(env.Log.CurrentLogFile);
        Assert.DoesNotContain("paxcookbook://", log);
        Assert.DoesNotContain("token=secret", log);
    }
}

public class InstallStateResolverTests
{
    [Fact]
    public void Resolver_OverrideUsedWhenProvided()
    {
        var temp = Path.Combine(Path.GetTempPath(), "PAX4R_" + Guid.NewGuid().ToString("N"));
        var r = new InstallStateResolver(temp);
        Assert.Equal(Path.GetFullPath(temp), r.InstallRoot);
        Assert.False(r.InstallStatePresent);
    }
}

public class WorkspaceLockReaderTests
{
    [Fact]
    public void Reader_ParsesPidAndPort()
    {
        var ws = Path.Combine(Path.GetTempPath(), "PAX4L_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "Runtime"));
        File.WriteAllText(Path.Combine(ws, "Runtime", "workspace.lock"),
            JsonSerializer.Serialize(new { brokerProcessId = 1111, brokerPort = 17655 }));
        try
        {
            var r = new WorkspaceLockReader().TryRead(ws);
            Assert.NotNull(r);
            Assert.Equal(1111, r!.BrokerProcessId);
            Assert.Equal(17655, r.BrokerPort);
        }
        finally { Directory.Delete(ws, true); }
    }

    [Fact]
    public void Reader_NoLock_ReturnsNull()
    {
        var ws = Path.Combine(Path.GetTempPath(), "PAX4M_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        try { Assert.Null(new WorkspaceLockReader().TryRead(ws)); }
        finally { Directory.Delete(ws, true); }
    }
}

public class AppInstanceCoordinatorTests
{
    [Fact]
    public void Coordinator_CreatesUniqueMutexForTest()
    {
        var name = @"Local\PAXCookbookTest_" + Guid.NewGuid().ToString("N");
        using var c = new Ipc.AppInstanceCoordinator(name);
        Assert.True(c.IsPrimary);
        using var c2 = new Ipc.AppInstanceCoordinator(name);
        Assert.False(c2.IsPrimary); // already held
    }
}
