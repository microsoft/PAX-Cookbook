using PAXCookbook.Broker.Native;
using PAXCookbook.Broker.Native.Services;
using PAXCookbook.Runtime;

namespace PAXCookbook.Broker;

// Stage 3j -- production IBrokerController that hosts the .NET
// Kestrel broker inside PAXCookbook.exe instead of spawning a
// PowerShell sidecar. Replaces the legacy BrokerController +
// RealBrokerProcessLauncher pair on the production wiring path; the
// legacy classes remain on disk for the Phase 4 test fixtures that
// exercise the launcher argv contract via fakes.
//
// Doctrine:
//   * One host per controller instance. Start() composes a new
//     NativeBrokerHost from the captured options factory, populates
//     the Stage 3i-C bundle (shared registry + native resume
//     spawner), awaits StartAsync, writes workspace.lock with the
//     in-process pid + chosen port, and returns the standard
//     BrokerStartResult. Subsequent Start() calls return
//     AlreadyRunning until Stop().
//   * Probe() short-circuits to the in-memory host state when the
//     controller owns a running host (source="native-in-process",
//     pid=Environment.ProcessId). When the controller is dormant it
//     falls back to WorkspaceLockReader so an orphan lock left by a
//     prior PAXCookbook.exe process still surfaces in the support
//     bundle. The fallback preserves the legacy "external broker
//     already running" semantics for the StatusCommand snapshot.
//   * Stop() disposes the host, removes the workspace lock file,
//     and returns Stopped. The BrokerStopOptions.BrokerPid argument
//     is intentionally ignored on the native path -- the controller
//     authoritatively owns the broker lifecycle and refuses to kill
//     arbitrary external pids on behalf of the caller. When the
//     controller is dormant Stop() returns AlreadyStopped.
//   * The workspace.lock.acquire sentinel is not written. Phase 7's
//     MutexAppInstanceGate ensures only one PAXCookbook.exe per
//     workspace can hold the broker; the per-workspace acquire file
//     would be redundant.
//   * The cook process registry instance is captured at construct
//     time and handed BOTH to the Stage 3i-C bundle (so the
//     stop/kill/resume routes see populated entries) AND, via the
//     bundle factory's closure over it, to CookExecutionService (so
//     RunAsync calls Register/Deregister). The host and the executor
//     share the same singleton.
public sealed class NativeBrokerController : IBrokerController, IAsyncDisposable
{
    private readonly Func<string, NativeBrokerHostOptions> _optionsFactory;
    private readonly Func<string, WorkspaceLockWriter>     _lockWriterFactory;
    private readonly WorkspaceLockReader                   _lockReader;
    private readonly IBrokerProcessProbe                   _externalProbe;
    private readonly ICookProcessRegistry                  _cookRegistry;
    private readonly Func<NativeBrokerHostOptions, ICookProcessRegistry, Stage3iCServiceBundle?> _stage3iCBundleFactory;
    private readonly object _gate = new();

    private NativeBrokerHost?    _host;
    private WorkspaceLockWriter? _activeLockWriter;
    private string?              _activeWorkspace;
    private int                  _activePort;

    public NativeBrokerController(
        Func<string, NativeBrokerHostOptions> optionsFactory,
        Func<string, WorkspaceLockWriter>     lockWriterFactory,
        WorkspaceLockReader                   lockReader,
        IBrokerProcessProbe                   externalProbe,
        ICookProcessRegistry                  cookRegistry,
        Func<NativeBrokerHostOptions, ICookProcessRegistry, Stage3iCServiceBundle?> stage3iCBundleFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentNullException.ThrowIfNull(lockWriterFactory);
        ArgumentNullException.ThrowIfNull(lockReader);
        ArgumentNullException.ThrowIfNull(externalProbe);
        ArgumentNullException.ThrowIfNull(cookRegistry);
        ArgumentNullException.ThrowIfNull(stage3iCBundleFactory);
        _optionsFactory        = optionsFactory;
        _lockWriterFactory     = lockWriterFactory;
        _lockReader            = lockReader;
        _externalProbe         = externalProbe;
        _cookRegistry          = cookRegistry;
        _stage3iCBundleFactory = stage3iCBundleFactory;
    }

    public BrokerStatus Probe(string workspaceFolderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceFolderPath);

        NativeBrokerHost? localHost;
        string?           ownedWorkspace;
        int               ownedPort;
        lock (_gate)
        {
            localHost      = _host;
            ownedWorkspace = _activeWorkspace;
            ownedPort      = _activePort;
        }

        // Owning controller: short-circuit to in-memory status. The
        // pid we report is THIS PAXCookbook.exe process so status
        // snapshots stay consistent with what the OS sees, not a
        // synthetic id.
        if (localHost is not null
            && localHost.BaseUrl is not null
            && string.Equals(ownedWorkspace, workspaceFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return new BrokerStatus(
                Running:  true,
                Pid:      Environment.ProcessId,
                Port:     ownedPort,
                Url:      localHost.BaseUrl,
                LockFile: BuildLockFilePath(workspaceFolderPath),
                Source:   "native-in-process");
        }

        // Dormant controller -- fall back to workspace.lock so an
        // orphaned lock from a prior process is reported the same
        // way the legacy BrokerController did.
        var wl = _lockReader.TryRead(workspaceFolderPath);
        if (wl is null)
            return new BrokerStatus(false, null, null, null, null, "none");
        var alive = wl.BrokerProcessId is int pid && _externalProbe.IsAlive(pid);
        string? url = wl.BrokerPort is int port ? "http://localhost:" + port : null;
        return new BrokerStatus(
            Running:  alive,
            Pid:      wl.BrokerProcessId,
            Port:     wl.BrokerPort,
            Url:      alive ? url : null,
            LockFile: wl.LockFile,
            Source:   alive ? "workspace-lock" : "process-probe");
    }

    public BrokerStartResult Start(BrokerStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkspaceFolderPath);

        NativeBrokerHost? existing;
        string?           existingWorkspace;
        int               existingPort;
        lock (_gate)
        {
            existing          = _host;
            existingWorkspace = _activeWorkspace;
            existingPort      = _activePort;
        }
        if (existing is not null && existing.BaseUrl is not null)
        {
            var status = new BrokerStatus(
                Running:  true,
                Pid:      Environment.ProcessId,
                Port:     existingPort,
                Url:      existing.BaseUrl,
                LockFile: BuildLockFilePath(existingWorkspace ?? options.WorkspaceFolderPath),
                Source:   "native-in-process");
            return new BrokerStartResult(
                Outcome:       BrokerStartOutcome.AlreadyRunning,
                Status:        status,
                FailureDetail: null);
        }

        NativeBrokerHostOptions hostOptions;
        WorkspaceLockWriter     lockWriter;
        Stage3iCServiceBundle?  stage3iCBundle;
        try
        {
            hostOptions    = _optionsFactory(options.WorkspaceFolderPath);
            lockWriter     = _lockWriterFactory(options.WorkspaceFolderPath);
            stage3iCBundle = _stage3iCBundleFactory(hostOptions, _cookRegistry);
        }
        catch (Exception ex)
        {
            return FailedStart("native_broker_compose_failed: " + ex.Message);
        }

        NativeBrokerHost host = new(hostOptions);
        if (stage3iCBundle is not null)
        {
            host.WithStage3iCServiceOverride(stage3iCBundle);
        }

        NativeBrokerHostStartResult started;
        try
        {
            // Bounded wait so a hung Kestrel start cannot wedge the
            // command surface. Clamp a sane floor so even a
            // misconfigured BrokerStartOptions cannot time out
            // instantly.
            var floor   = TimeSpan.FromSeconds(5);
            var timeout = options.ReadyTimeout < floor ? floor : options.ReadyTimeout;
            using var cts = new CancellationTokenSource(timeout);
            started = host.StartAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            DisposeHostQuietly(host);
            return FailedStart("native_broker_start_timeout: Kestrel host did not signal ready within ReadyTimeout.");
        }
        catch (Exception ex)
        {
            DisposeHostQuietly(host);
            return FailedStart("native_broker_start_failed: " + ex.Message);
        }

        // Workspace lock is written AFTER the host is up so an
        // external reader never observes a lock pointing at a
        // half-started broker. Failure here tears the host back down
        // and surfaces a structured error.
        try
        {
            lockWriter.Write(Environment.ProcessId, started.Port);
        }
        catch (Exception ex)
        {
            DisposeHostQuietly(host);
            return FailedStart("native_broker_lock_write_failed: " + ex.Message);
        }

        lock (_gate)
        {
            _host             = host;
            _activeLockWriter = lockWriter;
            _activeWorkspace  = options.WorkspaceFolderPath;
            _activePort       = started.Port;
        }

        var runningStatus = new BrokerStatus(
            Running:  true,
            Pid:      Environment.ProcessId,
            Port:     started.Port,
            Url:      started.BaseUrl,
            LockFile: BuildLockFilePath(options.WorkspaceFolderPath),
            Source:   "native-in-process");
        return new BrokerStartResult(
            Outcome:       BrokerStartOutcome.Started,
            Status:        runningStatus,
            FailureDetail: null);
    }

    public BrokerStopResult Stop(BrokerStopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        NativeBrokerHost?    host;
        WorkspaceLockWriter? writer;
        lock (_gate)
        {
            host              = _host;
            writer            = _activeLockWriter;
            _host             = null;
            _activeLockWriter = null;
            _activeWorkspace  = null;
            _activePort       = 0;
        }

        if (host is null)
        {
            return new BrokerStopResult(
                Outcome:       BrokerStopOutcome.AlreadyStopped,
                FailureDetail: null);
        }

        Exception? stopException = null;
        try
        {
            var floor   = TimeSpan.FromSeconds(5);
            var timeout = options.ExitTimeout < floor ? floor : options.ExitTimeout;
            using var cts = new CancellationTokenSource(timeout);
            host.StopAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            stopException = ex;
        }
        finally
        {
            DisposeHostQuietly(host);
            writer?.Remove();
        }

        if (stopException is not null)
        {
            return new BrokerStopResult(
                Outcome:       BrokerStopOutcome.Failed,
                FailureDetail: "native_broker_stop_failed: " + stopException.Message);
        }

        return new BrokerStopResult(
            Outcome:       BrokerStopOutcome.Stopped,
            FailureDetail: null);
    }

    public async ValueTask DisposeAsync()
    {
        NativeBrokerHost?    host;
        WorkspaceLockWriter? writer;
        lock (_gate)
        {
            host              = _host;
            writer            = _activeLockWriter;
            _host             = null;
            _activeLockWriter = null;
            _activeWorkspace  = null;
            _activePort       = 0;
        }
        if (host is not null)
        {
            try { await host.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        writer?.Remove();
    }

    private static BrokerStartResult FailedStart(string failureDetail)
    {
        var status = new BrokerStatus(
            Running:  false,
            Pid:      null,
            Port:     null,
            Url:      null,
            LockFile: null,
            Source:   "none");
        return new BrokerStartResult(
            Outcome:       BrokerStartOutcome.Failed,
            Status:        status,
            FailureDetail: failureDetail);
    }

    private static void DisposeHostQuietly(NativeBrokerHost host)
    {
        // Stage 5 AB: bound the dispose with a hard timeout. The
        // native host's DisposeAsync internally awaits Kestrel's
        // shutdown and any pending request drains; in the
        // close-shutdown path the WebView2 sockets must already be
        // gone by the time we get here (the WinForms host disposes
        // WebView2 before invoking broker.Stop on a background
        // thread), but a defensive cap prevents a wedged controller
        // from holding the caller forever if something else regresses
        // and re-introduces the deadlock. Process exit is the
        // ultimate safety net.
        try
        {
            var t = host.DisposeAsync().AsTask();
            t.Wait(TimeSpan.FromSeconds(5));
        }
        catch { }
    }

    private static string BuildLockFilePath(string workspaceFolderPath)
        => Path.Combine(workspaceFolderPath, "Runtime", "workspace.lock");
}
