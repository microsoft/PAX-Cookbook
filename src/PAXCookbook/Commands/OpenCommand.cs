using PAXCookbook.Broker;
using PAXCookbook.Ipc;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.WebView2;

namespace PAXCookbook.Commands;

// open — Phase 7: single-instance gate first, then detector, then
// broker, then UI host with close coordinator + controller sink.
//
// Flow:
//   1. Resolve workspace (install-state then bootstrap).
//   2. Try to acquire the primary mutex.
//      - SECONDARY: forward "open" over IPC; exit AnotherInstanceHandled.
//      - PRIMARY: proceed.
//   3. Set AUMID before any window creation.
//   4. Pre-flight WebView2 runtime; exit WebView2RuntimeMissing if absent.
//   5. Resolve broker; probe; start if not running.
//   6. Start IPC server (if a factory is supplied) bound to a verb
//      handler that drives the UI controller + broker stop.
//   7. Launch UI host (blocking on message loop). On exit, dispose IPC
//      and release the primary handle.
public static class OpenCommand
{
    internal const string ForwardVerb = "open";

    public static int Run(CommandContext ctx)
        => RunWithForwardVerb(ctx, ForwardVerb);

    internal static int RunWithForwardVerb(CommandContext ctx, string forwardVerb)
    {
        var workspace = ResolveWorkspace(ctx);
        if (workspace is null)
        {
            ctx.Stderr.WriteLine("open: workspace not configured; run launcher\\Start-PAXCookbook.ps1 once to seed the workspace.");
            ctx.Log.Write("App", "open-no-workspace", "warn");
            return AppExitCodes.InternalError;
        }

        var gate = ctx.Gate.TryAcquirePrimary();
        if (gate.Role == InstanceRole.Secondary)
        {
            var fwd = ctx.IpcClient.Forward(ctx.IpcEndpoint.GetEndpointName(), forwardVerb, TimeSpan.FromSeconds(2));
            ctx.Log.Write("App", "ipc-forward-attempt", "info", new Dictionary<string, object?>
            {
                ["verb"] = forwardVerb,
                ["outcome"] = fwd.Outcome.ToString()
            });
            switch (fwd.Outcome)
            {
                case IpcClientOutcome.Accepted:
                    ctx.Stdout.WriteLine("open: existing instance focused.");
                    return AppExitCodes.AnotherInstanceHandled;
                case IpcClientOutcome.NoPrimary:
                    ctx.Stderr.WriteLine("open: another instance is starting but did not accept IPC.");
                    return AppExitCodes.IpcConnectFailed;
                case IpcClientOutcome.Timeout:
                    return AppExitCodes.IpcTimeout;
                case IpcClientOutcome.VerbFailed:
                    return AppExitCodes.IpcRejected;
                default:
                    return AppExitCodes.IpcMalformedResponse;
            }
        }

        using var primaryHandle = gate.PrimaryHandle;
        if (gate.StaleRecovered)
        {
            ctx.Log.Write("App", "stale-instance-recovered", "warn");
        }

        bool aumidOk = ctx.Aumid.TrySet();
        ctx.Log.Write("App", "aumid-set", "info", new Dictionary<string, object?>
        {
            ["aumid"] = ctx.Aumid.Aumid,
            ["ok"] = aumidOk
        });

        var detect = ctx.WebView2Detector.Detect();
        ctx.Log.Write("App", "webview2-detect", "info", new Dictionary<string, object?>
        {
            ["present"] = detect.Status == WebView2RuntimeStatus.Present,
            ["pv"] = detect.Pv,
            ["sources"] = detect.Sources
        });
        if (detect.Status != WebView2RuntimeStatus.Present)
        {
            ctx.Stderr.WriteLine("open: Microsoft Edge WebView2 Runtime is required but was not detected. " +
                                 "Install the Evergreen WebView2 Runtime from https://developer.microsoft.com/microsoft-edge/webview2/ and try again.");
            return AppExitCodes.WebView2RuntimeMissing;
        }

        // Stage 3j -- BrokerPaths used to be an InternalError gate
        // here because the legacy BrokerController + Real launcher
        // needed an absolute path to Start-Broker.ps1 to spawn pwsh.
        // The native broker hosts Kestrel in-process and never
        // reads BrokerStartScript, so a missing or unresolved
        // broker\Start-Broker.ps1 is tolerated. We still resolve
        // the paths object so any future caller that depends on
        // BrokerPaths.BrokerStartScript receives a stable empty
        // string instead of NullReferenceException.
        var paths = BrokerPaths.TryResolve(ctx.InstallState.InstallRoot);
        var brokerStartScript = paths?.BrokerStartScript ?? string.Empty;
        if (paths is null)
        {
            ctx.Log.Write("App", "open-broker-paths-unresolved", "info", new Dictionary<string, object?>
            {
                ["installRoot"] = ctx.InstallState.InstallRoot,
                ["note"]        = "native broker ignores BrokerStartScript"
            });
        }

        BrokerStatus running;
        var pre = ctx.Broker.Probe(workspace);
        if (pre.Running)
        {
            ctx.Stdout.WriteLine("open: broker already running at " + pre.Url);
            ctx.Log.Write("App", "open-reused", "info", new Dictionary<string, object?>
            {
                ["pid"] = pre.Pid,
                ["port"] = pre.Port
            });
            running = pre;
        }
        else
        {
            ctx.Log.Write("App", "open-broker-start-attempt", "info", new Dictionary<string, object?>
            {
                ["script"] = brokerStartScript
            });
            var result = ctx.Broker.Start(new BrokerStartOptions(workspace, brokerStartScript, ctx.BrokerReadyTimeout));
            if (result.Outcome is not (BrokerStartOutcome.Started or BrokerStartOutcome.AlreadyRunning))
            {
                ctx.Stderr.WriteLine("open: failed to start broker: " + result.FailureDetail);
                ctx.Log.Write("App", "open-broker-failed", "error", new Dictionary<string, object?>
                {
                    ["detail"] = result.FailureDetail
                });
                return AppExitCodes.InternalError;
            }
            ctx.Stdout.WriteLine("open: broker " +
                (result.Outcome == BrokerStartOutcome.Started ? "started" : "reused") +
                " at " + result.Status.Url);
            ctx.Log.Write("App", "open-broker-started", "info", new Dictionary<string, object?>
            {
                ["pid"] = result.Status.Pid,
                ["port"] = result.Status.Port,
                ["outcome"] = result.Outcome.ToString()
            });
            running = result.Status;
        }

        if (string.IsNullOrWhiteSpace(running.Url) || running.Port is null)
        {
            ctx.Stderr.WriteLine("open: broker reported no URL; cannot launch UI.");
            ctx.Log.Write("App", "open-broker-no-url", "error");
            return AppExitCodes.InternalError;
        }

        var sink = new UiWindowControllerSink();
        var coord = new CloseGestureCoordinator(
            ctx.CloseDialog,
            ctx.Broker,
            () => ResolveWorkspace(ctx),
            ctx.BrokerStopTimeout,
            ctx.Log);

        IIpcServer? server = null;
        if (ctx.IpcServerFactory is not null)
        {
            var handler = new PrimaryVerbHandler(sink, ctx.Broker, () => ResolveWorkspace(ctx), ctx.BrokerStopTimeout, ctx.Log);
            try
            {
                server = ctx.IpcServerFactory(handler);
                server.Start(handler);
                ctx.Log.Write("App", "ipc-server-started", "info", new Dictionary<string, object?>
                {
                    ["endpoint"] = server.EndpointName
                });
            }
            catch (Exception ex)
            {
                server = null;
                ctx.Log.Write("App", "ipc-server-failed", "warn", new Dictionary<string, object?>
                {
                    ["detail"] = ex.Message
                });
            }
        }

        try
        {
            var launch = new UiHostLaunchRequest(
                BrokerUrl: running.Url!,
                BrokerPort: running.Port!.Value,
                UserDataFolder: ctx.WebView2Data.UserDataFolder,
                WindowTitle: "PAX Cookbook",
                ControllerSink: sink,
                CloseCoordinator: coord);
            ctx.Log.Write("App", "ui-host-launch", "info", new Dictionary<string, object?>
            {
                ["port"] = launch.BrokerPort,
                ["userDataFolder"] = launch.UserDataFolder
            });
            var ui = ctx.UiHost.Launch(launch);
            if (ui.Outcome != UiHostOutcome.Launched)
            {
                ctx.Stderr.WriteLine("open: UI host failed: " + ui.FailureDetail);
                ctx.Log.Write("App", "ui-host-failed", "error", new Dictionary<string, object?>
                {
                    ["detail"] = ui.FailureDetail
                });
                return AppExitCodes.InternalError;
            }
            return AppExitCodes.Ok;
        }
        finally
        {
            try { server?.Dispose(); } catch { }
        }
    }

    internal static string? ResolveWorkspace(CommandContext ctx)
    {
        var state = ctx.InstallState.TryReadInstallState();
        if (!string.IsNullOrWhiteSpace(state?.WorkspaceFolderPath))
            return state!.WorkspaceFolderPath;
        var boot = ctx.Bootstrap.TryRead();
        if (!string.IsNullOrWhiteSpace(boot?.WorkspaceFolderPath))
            return boot!.WorkspaceFolderPath;
        return null;
    }
}

// Primary-side IPC verb handler. Dispatches allowlisted verbs to the
// UI controller (focus / silent close) and to the broker controller
// (stop). Never invokes any user-facing dialog.
internal sealed class PrimaryVerbHandler : IIpcVerbHandler
{
    private readonly UiWindowControllerSink _sink;
    private readonly IBrokerController _broker;
    private readonly Func<string?> _workspaceLookup;
    private readonly TimeSpan _stopTimeout;
    private readonly Logging.AppLogger _log;

    public PrimaryVerbHandler(UiWindowControllerSink sink, IBrokerController broker, Func<string?> workspaceLookup, TimeSpan stopTimeout, Logging.AppLogger log)
    {
        _sink = sink;
        _broker = broker;
        _workspaceLookup = workspaceLookup;
        _stopTimeout = stopTimeout;
        _log = log;
    }

    public IpcResponse Handle(IpcRequest request)
    {
        _log.Write("App", "ipc-verb-received", "info", new Dictionary<string, object?>
        {
            ["verb"] = request.Verb
        });
        switch (request.Verb)
        {
            case IpcAllowlist.Open:
            case IpcAllowlist.Reopen:
                _sink.Get()?.FocusWindow();
                return new IpcResponse(request.Id, true, IpcResponseCodes.Ok, null);

            case IpcAllowlist.Stop:
                _sink.Get()?.CloseWindowSilently();
                var ws = _workspaceLookup();
                if (!string.IsNullOrWhiteSpace(ws))
                {
                    var probe = _broker.Probe(ws!);
                    if (probe.Running && probe.Pid is int pid)
                    {
                        _broker.Stop(new BrokerStopOptions(pid, _stopTimeout));
                    }
                }
                return new IpcResponse(request.Id, true, IpcResponseCodes.Ok, null);

            case IpcAllowlist.StatusQuery:
                return new IpcResponse(request.Id, true, IpcResponseCodes.Ok, null);

            default:
                return new IpcResponse(request.Id, false, IpcResponseCodes.UnknownVerb, null);
        }
    }
}
