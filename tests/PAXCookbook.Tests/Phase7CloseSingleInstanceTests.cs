using System.IO.Pipes;
using System.Text.Json;
using PAXCookbook.Broker;
using PAXCookbook.Commands;
using PAXCookbook.Ipc;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.WebView2;
using Xunit;

namespace PAXCookbook.Tests;

// Phase 7 focused tests.

// --- helpers shared by the Phase 7 suites ---

internal sealed class FakeAppInstanceGate : IAppInstanceGate
{
    public InstanceRole NextRole { get; set; } = InstanceRole.Primary;
    public bool StaleNext { get; set; }
    public int Calls { get; private set; }
    public int ActiveHandles { get; private set; }
    public GateAcquireResult TryAcquirePrimary()
    {
        Calls++;
        if (NextRole == InstanceRole.Secondary)
            return new GateAcquireResult(InstanceRole.Secondary, false, null);
        ActiveHandles++;
        return new GateAcquireResult(InstanceRole.Primary, StaleNext, new Handle(this));
    }
    private sealed class Handle : IDisposable
    {
        private readonly FakeAppInstanceGate _g;
        public Handle(FakeAppInstanceGate g) => _g = g;
        public void Dispose() => _g.ActiveHandles--;
    }
}

internal sealed class FakeIpcClient : IIpcClient
{
    public IpcClientForwardResult NextResult { get; set; } =
        new(IpcClientOutcome.Accepted, new IpcResponse("x", true, IpcResponseCodes.Ok, null), null);
    public int Calls { get; private set; }
    public string? LastVerb { get; private set; }
    public string? LastEndpoint { get; private set; }
    public IpcClientForwardResult Forward(string endpointName, string verb, TimeSpan timeout)
    {
        Calls++;
        LastEndpoint = endpointName;
        LastVerb = verb;
        return NextResult;
    }
}

internal sealed class RecordingUiHostP7 : IUiHost
{
    public int Calls { get; private set; }
    public UiHostLaunchRequest? Last { get; private set; }
    public UiHostResult Next { get; set; } = new(UiHostOutcome.Launched, null);
    public IUiWindowController? InstalledController { get; private set; }
    public UiHostResult Launch(UiHostLaunchRequest request)
    {
        Calls++;
        Last = request;
        if (request.ControllerSink is not null)
        {
            InstalledController = new StubController();
            request.ControllerSink.Set(InstalledController);
        }
        return Next;
    }
    private sealed class StubController : IUiWindowController
    {
        public int FocusCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public void FocusWindow() => FocusCalls++;
        public void CloseWindowSilently() => CloseCalls++;
    }
}

// =============================================================================
// 1. CloseGestureCoordinator (Stage 3k two-button revision)
// =============================================================================
public class CloseGestureCoordinatorTests
{
    private static (CloseGestureCoordinator coord, FakeBrokerController broker, FixedChoiceCloseDialog dialog, TestEnv env)
        Build(CloseChoice choice, bool brokerRunning)
    {
        var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var broker = new FakeBrokerController
        {
            NextProbe = brokerRunning
                ? new BrokerStatus(true, 1234, 17654, "http://localhost:17654", env.WorkspacePath, "workspace-lock")
                : new BrokerStatus(false, null, null, null, null, "none")
        };
        var dialog = new FixedChoiceCloseDialog(choice);
        var coord = new CloseGestureCoordinator(dialog, broker, () => env.WorkspacePath, TimeSpan.FromSeconds(2), env.Log);
        return (coord, broker, dialog, env);
    }

    [Fact]
    public void Cancel_KeepsCloseCancelled_NoBrokerStop()
    {
        var (coord, broker, dlg, env) = Build(CloseChoice.Cancel, brokerRunning: true);
        using (env)
        {
            var r = coord.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);
            Assert.Equal(CloseGestureOutcome.CancelClose, r.Outcome);
            Assert.Equal(1, dlg.Calls);
            Assert.Equal(0, broker.StopCalls);
        }
    }

    [Fact]
    public void ClosePaxCookbook_StopsBrokerExactlyOnce()
    {
        var (coord, broker, _, env) = Build(CloseChoice.ClosePaxCookbook, brokerRunning: true);
        using (env)
        {
            var r = coord.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);
            Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
            Assert.Equal(1, broker.StopCalls);
            Assert.Equal(1234, broker.LastStop!.BrokerPid);
        }
    }

    [Fact]
    public void ClosePaxCookbook_BrokerAlreadyStopped_IsIdempotent()
    {
        var (coord, broker, _, env) = Build(CloseChoice.ClosePaxCookbook, brokerRunning: false);
        using (env)
        {
            var r = coord.Handle(CloseTrigger.TitleBarX, IntPtr.Zero);
            Assert.Equal(CloseGestureOutcome.ClosedWithBrokerStopped, r.Outcome);
            Assert.Equal(0, broker.StopCalls);
        }
    }
}

// =============================================================================
// 2. StopCommand UI/broker coordination + idempotence
// =============================================================================
public class StopCommandPhase7Tests
{
    [Fact]
    public void Stop_NoPrimaryNoBroker_IsIdempotent()
    {
        using var env = new TestEnv();
        var fake = new FakeBrokerController();
        var ctx = env.Context(fake);
        var rc = StopCommand.Run(ctx);
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Contains("nothing to stop", env.Stdout.ToString());
    }

    [Fact]
    public void Stop_NoPrimaryBrokerRunning_StopsBrokerDirectly()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var fake = new FakeBrokerController
        {
            NextProbe = new BrokerStatus(true, 4321, 17654, "http://localhost:17654", env.WorkspacePath, "workspace-lock")
        };
        var ctx = env.Context(fake);
        var rc = StopCommand.Run(ctx);
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(1, fake.StopCalls);
        Assert.Equal(4321, fake.LastStop!.BrokerPid);
    }

    [Fact]
    public void Stop_PrimaryRunning_ForwardsViaIpcAndDoesNotProbeBrokerDirectly()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var fake = new FakeBrokerController();
        var gate = new FakeAppInstanceGate { NextRole = InstanceRole.Secondary };
        var client = new FakeIpcClient();
        var ctx = new CommandContext(
            env.Resolver, env.Bootstrap, env.Locks, env.Sidecars, fake, env.Log,
            env.Stdout, env.Stderr, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            gate: gate, ipcClient: client);

        var rc = StopCommand.Run(ctx);
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(1, client.Calls);
        Assert.Equal(IpcAllowlist.Stop, client.LastVerb);
        Assert.Equal(0, fake.ProbeCalls);
        Assert.Equal(0, fake.StopCalls);
        Assert.Contains("forwarded", env.Stdout.ToString());
    }
}

// =============================================================================
// 3. OpenCommand single-instance forwarding
// =============================================================================
public class OpenCommandPhase7Tests
{
    private static CommandContext BuildCtx(TestEnv env, IBrokerController broker, IAppInstanceGate gate, IIpcClient client, IUiHost? uiHost = null)
        => new(env.Resolver, env.Bootstrap, env.Locks, env.Sidecars, broker, env.Log,
               env.Stdout, env.Stderr, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
               uiHost: uiHost, gate: gate, ipcClient: client);

    [Fact]
    public void Open_SecondaryForwardsAcceptedAndExitsAnotherInstanceHandled()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var fake = new FakeBrokerController();
        var gate = new FakeAppInstanceGate { NextRole = InstanceRole.Secondary };
        var client = new FakeIpcClient();
        var ui = new RecordingUiHostP7();
        var ctx = BuildCtx(env, fake, gate, client, ui);

        var rc = OpenCommand.Run(ctx);
        Assert.Equal(AppExitCodes.AnotherInstanceHandled, rc);
        Assert.Equal(1, client.Calls);
        Assert.Equal(IpcAllowlist.Open, client.LastVerb);
        Assert.Equal(0, ui.Calls);
        Assert.Equal(0, fake.ProbeCalls);
        Assert.Equal(0, fake.StartCalls);
    }

    [Fact]
    public void Reopen_SecondaryForwardsReopenVerb()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var fake = new FakeBrokerController();
        var gate = new FakeAppInstanceGate { NextRole = InstanceRole.Secondary };
        var client = new FakeIpcClient();
        var ui = new RecordingUiHostP7();
        var ctx = BuildCtx(env, fake, gate, client, ui);

        var rc = ReopenCommand.Run(ctx);
        Assert.Equal(AppExitCodes.AnotherInstanceHandled, rc);
        Assert.Equal(IpcAllowlist.Reopen, client.LastVerb);
    }

    [Fact]
    public void Protocol_AcceptedDispatchesThroughOpen_SecondaryForwards()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var fake = new FakeBrokerController();
        var gate = new FakeAppInstanceGate { NextRole = InstanceRole.Secondary };
        var client = new FakeIpcClient();
        var ui = new RecordingUiHostP7();
        var ctx = BuildCtx(env, fake, gate, client, ui);

        var rc = ProtocolCommand.Run(ctx, "paxcookbook://open");
        Assert.Equal(AppExitCodes.AnotherInstanceHandled, rc);
        Assert.Equal(IpcAllowlist.Open, client.LastVerb);
        // Raw URI must NOT appear on stdout.
        Assert.DoesNotContain("paxcookbook://open", env.Stdout.ToString());
    }

    [Fact]
    public void Open_PrimaryPath_SetsAumidBeforeUiLaunch_AndPassesCoordinatorPlusSink()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        Directory.CreateDirectory(Path.Combine(env.InstallRoot, "App", "broker"));
        File.WriteAllText(Path.Combine(env.InstallRoot, "App", "broker", "Start-Broker.ps1"), "# stub");
        var fake = new FakeBrokerController();
        var gate = new FakeAppInstanceGate { NextRole = InstanceRole.Primary };
        var client = new FakeIpcClient(); // unused on primary path
        var ui = new RecordingUiHostP7();
        var aumid = new RecordingAumidSetter();
        var ctx = new CommandContext(
            env.Resolver, env.Bootstrap, env.Locks, env.Sidecars, fake, env.Log,
            env.Stdout, env.Stderr, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            uiHost: ui, gate: gate, ipcClient: client, aumid: aumid);

        var rc = OpenCommand.Run(ctx);
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(1, ui.Calls);
        Assert.Equal(1, aumid.Calls);                                  // AUMID set
        Assert.NotNull(ui.Last!.ControllerSink);                       // sink wired
        Assert.NotNull(ui.Last!.CloseCoordinator);                     // coordinator wired
        Assert.Equal("PAX Cookbook", ui.Last!.WindowTitle);            // title preserved
        Assert.NotNull(ui.InstalledController);                        // host installed a controller into sink
    }
}

// =============================================================================
// 4. IPC framing + allowlist + malformed-message rejection
// =============================================================================
public class IpcFrameTests
{
    [Fact]
    public void Write_Then_Read_RoundTrips()
    {
        var ms = new MemoryStream();
        IpcFrame.WriteRequest(ms, new IpcRequest("open", "id-1", "2026-05-26T00:00:00Z"));
        ms.Position = 0;
        var r = IpcFrame.ReadRequest(ms);
        Assert.Equal(IpcFrame.ReadError.Ok, r.Error);
        Assert.Equal("open", r.Request!.Verb);
    }

    [Fact]
    public void Read_LengthExceeded_IsRejected()
    {
        var ms = new MemoryStream();
        // Write 5000-byte length (over the 4096 cap).
        ms.Write(new byte[] { 0x88, 0x13, 0, 0 }, 0, 4);
        ms.Position = 0;
        var r = IpcFrame.ReadRequest(ms);
        Assert.Equal(IpcFrame.ReadError.LengthExceeded, r.Error);
    }

    [Fact]
    public void Read_BadJson_IsRejected()
    {
        var ms = new MemoryStream();
        var bad = System.Text.Encoding.UTF8.GetBytes("not-json");
        ms.Write(BitConverter.GetBytes((uint)bad.Length), 0, 4);
        ms.Write(bad, 0, bad.Length);
        ms.Position = 0;
        var r = IpcFrame.ReadRequest(ms);
        Assert.Equal(IpcFrame.ReadError.BadJson, r.Error);
    }

    [Fact]
    public void Read_BadShape_MissingVerb_IsRejected()
    {
        var ms = new MemoryStream();
        var json = System.Text.Encoding.UTF8.GetBytes("{\"id\":\"x\"}");
        ms.Write(BitConverter.GetBytes((uint)json.Length), 0, 4);
        ms.Write(json, 0, json.Length);
        ms.Position = 0;
        var r = IpcFrame.ReadRequest(ms);
        Assert.Equal(IpcFrame.ReadError.BadShape, r.Error);
    }
}

public class IpcAllowlistTests
{
    [Fact]
    public void Allowlist_HasExpectedVerbsOnly()
    {
        Assert.Contains(IpcAllowlist.Open,        IpcAllowlist.Verbs);
        Assert.Contains(IpcAllowlist.Reopen,      IpcAllowlist.Verbs);
        Assert.Contains(IpcAllowlist.Stop,        IpcAllowlist.Verbs);
        Assert.Contains(IpcAllowlist.StatusQuery, IpcAllowlist.Verbs);
        Assert.DoesNotContain("delete-everything", IpcAllowlist.Verbs);
        Assert.DoesNotContain("install", IpcAllowlist.Verbs);
    }
}

// =============================================================================
// 5. Real per-user named-pipe server end-to-end (small subset)
// =============================================================================
public class NamedPipeIpcServerTests
{
    private sealed class EchoHandler : IIpcVerbHandler
    {
        public IpcResponse Handle(IpcRequest r) =>
            new(r.Id, true, IpcResponseCodes.Ok, "echo:" + r.Verb);
    }

    [Fact]
    public void Server_RoundTrip_AcceptedVerb_ReturnsOk()
    {
        var name = "PAXCookbookTest." + Guid.NewGuid().ToString("N");
        using var srv = new NamedPipeIpcServer(name);
        srv.Start(new EchoHandler());
        var client = new NamedPipeIpcClient();
        var r = client.Forward(name, IpcAllowlist.Open, TimeSpan.FromSeconds(3));
        Assert.Equal(IpcClientOutcome.Accepted, r.Outcome);
        Assert.True(r.Response!.Ok);
        Assert.Equal(IpcResponseCodes.Ok, r.Response.Code);
    }

    [Fact]
    public void Server_UnknownVerb_IsRejected()
    {
        var name = "PAXCookbookTest." + Guid.NewGuid().ToString("N");
        using var srv = new NamedPipeIpcServer(name);
        srv.Start(new EchoHandler());
        var client = new NamedPipeIpcClient();
        var r = client.Forward(name, "delete-everything", TimeSpan.FromSeconds(3));
        Assert.Equal(IpcClientOutcome.VerbFailed, r.Outcome);
        Assert.Equal(IpcResponseCodes.UnknownVerb, r.Response!.Code);
    }

    [Fact]
    public void Client_NoPrimary_ReturnsNoPrimary()
    {
        var name = "PAXCookbookTest." + Guid.NewGuid().ToString("N");
        var client = new NamedPipeIpcClient();
        var r = client.Forward(name, IpcAllowlist.Open, TimeSpan.FromMilliseconds(250));
        Assert.Equal(IpcClientOutcome.NoPrimary, r.Outcome);
    }

    [Fact]
    public void Server_FrameLengthExceeded_IsRejected()
    {
        var name = "PAXCookbookTest." + Guid.NewGuid().ToString("N");
        using var srv = new NamedPipeIpcServer(name);
        srv.Start(new EchoHandler());

        using var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
        c.Connect(2000);
        // Write an oversize length (5000) and stop. Server should respond with bad-frame or length-exceeded.
        c.Write(new byte[] { 0x88, 0x13, 0, 0 }, 0, 4);
        c.Flush();
        var resp = IpcFrame.ReadResponse(c);
        Assert.NotNull(resp);
        Assert.False(resp!.Ok);
        Assert.Equal(IpcResponseCodes.LengthExceeded, resp.Code);
    }
}

// =============================================================================
// 6. Mutex gate + stale recovery
// =============================================================================
public class MutexGateTests
{
    [Fact]
    public void FirstCaller_BecomesPrimary_SecondIsSecondary()
    {
        var name = @"Local\PAXCookbookTest." + Guid.NewGuid().ToString("N");
        var g1 = new MutexAppInstanceGate(name);
        var first = g1.TryAcquirePrimary();
        using (first.PrimaryHandle)
        {
            Assert.Equal(InstanceRole.Primary, first.Role);
            // Probe from a different thread: mutexes are thread-owned, so
            // the second probe must come from a non-owning thread to see
            // Secondary.
            GateAcquireResult second = default!;
            var t = new Thread(() =>
            {
                var g2 = new MutexAppInstanceGate(name);
                second = g2.TryAcquirePrimary();
            });
            t.Start();
            t.Join();
            Assert.Equal(InstanceRole.Secondary, second.Role);
            Assert.Null(second.PrimaryHandle);
        }
    }
}

// =============================================================================
// 7. AUMID setter is called and exposes PAXCookbook.App.v1 by default
// =============================================================================
public class AumidTests
{
    [Fact]
    public void RecordingSetter_DefaultsToPAXCookbookAppV1()
    {
        var s = new RecordingAumidSetter();
        Assert.Equal("PAXCookbook.App.v1", s.Aumid);
        Assert.True(s.TrySet());
        Assert.Equal(1, s.Calls);
    }

    [Fact]
    public void Open_CallsAumidSetter_BeforeUiLaunch()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        Directory.CreateDirectory(Path.Combine(env.InstallRoot, "App", "broker"));
        File.WriteAllText(Path.Combine(env.InstallRoot, "App", "broker", "Start-Broker.ps1"), "# stub");

        var setter = new OrderingAumidSetter();
        var ui = new OrderingUiHost(setter);
        var ctx = new CommandContext(
            env.Resolver, env.Bootstrap, env.Locks, env.Sidecars,
            new FakeBrokerController(), env.Log,
            env.Stdout, env.Stderr, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            uiHost: ui, aumid: setter);

        var rc = OpenCommand.Run(ctx);
        Assert.Equal(AppExitCodes.Ok, rc);
        Assert.Equal(1, setter.Calls);
        Assert.True(ui.AumidWasSetBeforeLaunch);
    }

    private sealed class OrderingAumidSetter : IAppUserModelIdSetter
    {
        public string Aumid => "PAXCookbook.App.v1";
        public int Calls { get; private set; }
        public bool Set { get; private set; }
        public bool TrySet() { Calls++; Set = true; return true; }
    }

    private sealed class OrderingUiHost : IUiHost
    {
        private readonly OrderingAumidSetter _s;
        public bool AumidWasSetBeforeLaunch { get; private set; }
        public OrderingUiHost(OrderingAumidSetter s) => _s = s;
        public UiHostResult Launch(UiHostLaunchRequest request)
        {
            AumidWasSetBeforeLaunch = _s.Set;
            request.ControllerSink?.Set(new Stub());
            return new(UiHostOutcome.Launched, null);
        }
        private sealed class Stub : IUiWindowController
        {
            public void FocusWindow() { }
            public void CloseWindowSilently() { }
        }
    }
}

// =============================================================================
// 8. status JSON exposes singleInstance + aumid
// =============================================================================
public class StatusCommandPhase7Tests
{
    [Fact]
    public void Status_ReportsSingleInstanceAndAumid()
    {
        using var env = new TestEnv();
        env.WriteBootstrap(env.WorkspacePath);
        var ctx = new CommandContext(
            env.Resolver, env.Bootstrap, env.Locks, env.Sidecars,
            new FakeBrokerController(), env.Log, env.Stdout, env.Stderr, null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            gate: new FakeAppInstanceGate { NextRole = InstanceRole.Secondary },
            aumid: new RecordingAumidSetter());
        var rc = StatusCommand.Run(ctx);
        Assert.Equal(AppExitCodes.Ok, rc);
        using var doc = JsonDocument.Parse(env.Stdout.ToString());
        var ui = doc.RootElement.GetProperty("ui");
        Assert.True(ui.GetProperty("singleInstance").GetBoolean());
        Assert.Equal("PAXCookbook.App.v1", ui.GetProperty("aumid").GetString());
    }
}

// =============================================================================
// 9. Structural guards: no Edge / no registry writes / no Start Menu / no git
//    (asserted directly against PAXCookbook source tree)
// =============================================================================
public class Phase7StructuralTests
{
    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PAXCookbook.sln")))
            d = d.Parent;
        if (d is null) throw new InvalidOperationException("repo root not found");
        return d.FullName;
    }

    private static IEnumerable<string> AppCsFiles()
    {
        var root = FindRepoRoot();
        return Directory.EnumerateFiles(Path.Combine(root, "src", "PAXCookbook"), "*.cs", SearchOption.AllDirectories);
    }

    [Theory]
    [InlineData("msedge.exe")]
    [InlineData("chrome.exe")]
    [InlineData("--app=")]
    [InlineData("shell:AppsFolder")]
    [InlineData("start microsoft-edge")]
    public void NoBrowserLaunchStringsInPAXCookbookSources(string banned)
    {
        foreach (var f in AppCsFiles())
        {
            var content = File.ReadAllText(f);
            Assert.False(content.Contains(banned, StringComparison.OrdinalIgnoreCase),
                $"banned string '{banned}' found in {f}");
        }
    }

    [Theory]
    [InlineData("Registry.SetValue")]
    [InlineData("CreateSubKey")]
    [InlineData("HKEY_CURRENT_USER\\Software\\Classes\\paxcookbook")]
    [InlineData("HKEY_LOCAL_MACHINE\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall")]
    public void NoProtocolOrStartMenuRegistryWritesInPAXCookbookSources(string banned)
    {
        foreach (var f in AppCsFiles())
        {
            var content = File.ReadAllText(f);
            Assert.False(content.Contains(banned, StringComparison.OrdinalIgnoreCase),
                $"forbidden registry write '{banned}' found in {f}");
        }
    }

    [Fact]
    public void SetupCsproj_StillDoesNotReferenceWebView2()
    {
        var root = FindRepoRoot();
        var csproj = Path.Combine(root, "src", "PAXCookbookSetup", "PAXCookbookSetup.csproj");
        Assert.True(File.Exists(csproj));
        var c = File.ReadAllText(csproj);
        Assert.DoesNotContain("Microsoft.Web.WebView2", c);
    }

    [Fact]
    public void Phase7Verifier_HasNoGitCommandInvocations()
    {
        var root = FindRepoRoot();
        var v = Path.Combine(root, "_temp", "phase_7_native_close_single_instance_and_window_identity", "verify_phase_7_close_identity.ps1");
        if (!File.Exists(v)) return;
        var c = File.ReadAllText(v);
        // Look for the external-command invocation form. Assembled at
        // runtime so this assertion does not match itself.
        string n1 = "& " + "git ";
        string n2 = "& " + "git." + "exe";
        Assert.DoesNotContain(n1, c);
        Assert.DoesNotContain(n2, c);
    }
}
