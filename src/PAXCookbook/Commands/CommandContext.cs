using PAXCookbook.Broker;
using PAXCookbook.Ipc;
using PAXCookbook.Logging;
using PAXCookbook.Runtime;
using PAXCookbook.WebView2;

namespace PAXCookbook.Commands;

// Dependency container shared by all command handlers. Tests construct
// this manually with fakes; production builds it from real components.
public sealed class CommandContext
{
    public InstallStateResolver InstallState { get; }
    public BootstrapStateReader Bootstrap { get; }
    public WorkspaceLockReader Locks { get; }
    public SidecarReader Sidecars { get; }
    public IBrokerController Broker { get; }
    public AppLogger Log { get; }
    public TextWriter Stdout { get; }
    public TextWriter Stderr { get; }
    public string? InstallRootOverride { get; }
    public TimeSpan BrokerReadyTimeout { get; }
    public TimeSpan BrokerStopTimeout { get; }
    public IWebView2RuntimeDetector WebView2Detector { get; }
    public IUiHost UiHost { get; }
    public WebView2DataPaths WebView2Data { get; }
    // Phase 7 additions. Defaults preserve Phase 4/6 test behavior:
    //   - AlwaysPrimaryGate => no IPC needed
    //   - null IpcServerFactory => primary path skips listener
    //   - NullIpcClient => secondary forward is never attempted
    //   - RecordingAumidSetter => no Win32 call
    //   - FixedChoiceCloseDialog(Cancel) => never used outside host
    public IAppInstanceGate Gate { get; }
    public IIpcClient IpcClient { get; }
    public Func<IIpcVerbHandler, IIpcServer>? IpcServerFactory { get; }
    public IIpcEndpointNameProvider IpcEndpoint { get; }
    public IAppUserModelIdSetter Aumid { get; }
    public ICloseDialogService CloseDialog { get; }

    public CommandContext(
        InstallStateResolver installState,
        BootstrapStateReader bootstrap,
        WorkspaceLockReader locks,
        SidecarReader sidecars,
        IBrokerController broker,
        AppLogger log,
        TextWriter stdout,
        TextWriter stderr,
        string? installRootOverride,
        TimeSpan? brokerReadyTimeout = null,
        TimeSpan? brokerStopTimeout = null,
        IWebView2RuntimeDetector? webView2Detector = null,
        IUiHost? uiHost = null,
        WebView2DataPaths? webView2Data = null,
        IAppInstanceGate? gate = null,
        IIpcClient? ipcClient = null,
        Func<IIpcVerbHandler, IIpcServer>? ipcServerFactory = null,
        IIpcEndpointNameProvider? ipcEndpoint = null,
        IAppUserModelIdSetter? aumid = null,
        ICloseDialogService? closeDialog = null)
    {
        InstallState = installState;
        Bootstrap = bootstrap;
        Locks = locks;
        Sidecars = sidecars;
        Broker = broker;
        Log = log;
        Stdout = stdout;
        Stderr = stderr;
        InstallRootOverride = installRootOverride;
        BrokerReadyTimeout = brokerReadyTimeout ?? TimeSpan.FromSeconds(20);
        BrokerStopTimeout = brokerStopTimeout ?? TimeSpan.FromSeconds(10);
        WebView2Detector = webView2Detector ?? new AlwaysPresentWebView2Detector();
        UiHost = uiHost ?? new NullUiHost();
        WebView2Data = webView2Data ?? WebView2DataPaths.FromLocalAppData();
        Gate = gate ?? new AlwaysPrimaryGate();
        IpcClient = ipcClient ?? new NullIpcClient();
        IpcServerFactory = ipcServerFactory;
        IpcEndpoint = ipcEndpoint ?? new FixedEndpointProvider("PAXCookbook.test");
        Aumid = aumid ?? new RecordingAumidSetter();
        CloseDialog = closeDialog ?? new FixedChoiceCloseDialog(CloseChoice.Cancel);
    }
}

// Default no-op IPC client for tests / non-IPC code paths.
public sealed class NullIpcClient : IIpcClient
{
    public int Calls { get; private set; }
    public string? LastEndpoint { get; private set; }
    public string? LastVerb { get; private set; }
    public IpcClientForwardResult NextResult { get; set; } =
        new(IpcClientOutcome.NoPrimary, null, "no-primary");
    public IpcClientForwardResult Forward(string endpointName, string verb, TimeSpan timeout)
    {
        Calls++;
        LastEndpoint = endpointName;
        LastVerb = verb;
        return NextResult;
    }
}
