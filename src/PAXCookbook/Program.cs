using PAXCookbook.Broker;
using PAXCookbook.Broker.Native;
using PAXCookbook.Broker.Native.Services;
using PAXCookbook.Cli;
using PAXCookbook.Commands;
using PAXCookbook.Ipc;
using PAXCookbook.Logging;
using PAXCookbook.Runtime;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Platform;
using PAXCookbook.WebView2;

namespace PAXCookbook;

internal static class Program
{
    // [STAThread] is required for any path that may run the WinForms
    // message loop (UI host on `open`/`reopen`/protocol-accepted).
    [STAThread]
    public static int Main(string[] args)
    {
        // Earliest possible guard: PAX Cookbook requires Windows 10 or later.
        // Runs before any argument parsing or window work, so it fires on the
        // shortcut path AND the manual dotnet+DLL path, and the user never
        // reaches the identity-verification step on an unsupported OS.
        if (!WindowsVersionGate.IsSupported())
        {
            UnsupportedOsNotice.Show();
            return AppExitCodes.UnsupportedWindowsVersion;
        }

        AppArgs parsed;
        try
        {
            parsed = AppArgsParser.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return AppExitCodes.UsageError;
        }

        var installState = new InstallStateResolver(parsed.InstallRoot);
        var bootstrap = new BootstrapStateReader();
        var locks = new WorkspaceLockReader();
        var sidecars = new SidecarReader();

        // Stage 3j -- production broker controller hosts Kestrel
        // in-process inside PAXCookbook.exe so the legacy
        // pwsh Start-Broker.ps1 child process is never spawned and
        // no visible PowerShell terminal appears. Both the Stage
        // 3i-C bundle and CookExecutionService receive the SAME
        // cookRegistry singleton so /stop and /kill on a running
        // cook can find the live handle the executor populated at
        // spawn time. The Stage 3i-C bundle is composed lazily per
        // Start() call (so the AppRoot baked into the verifier
        // paths reflects the install-state.json captured at the
        // moment of Start, not at process boot).
        var probe = new DefaultBrokerProcessProbe();
        var cookRegistry = new InMemoryCookProcessRegistry();
        var broker = new NativeBrokerController(
            optionsFactory: ws =>
            {
                var state = installState.TryReadInstallState();
                var appRoot = !string.IsNullOrWhiteSpace(state?.AppRoot)
                    ? state!.AppRoot
                    : AppContext.BaseDirectory;
                return NativeBrokerHostOptions.ForInstalledApp(ws, appRoot);
            },
            lockWriterFactory: ws =>
            {
                var state = installState.TryReadInstallState();
                var appRoot = !string.IsNullOrWhiteSpace(state?.AppRoot)
                    ? state!.AppRoot
                    : AppContext.BaseDirectory;
                var version = !string.IsNullOrWhiteSpace(state?.AppVersion)
                    ? state!.AppVersion
                    : string.Empty;
                return new WorkspaceLockWriter(
                    workspaceFolderPath: ws,
                    appRoot:             appRoot,
                    cookbookVersion:     version,
                    launchMode:          "embedded");
            },
            lockReader:    locks,
            externalProbe: probe,
            cookRegistry:  cookRegistry,
            stage3iCBundleFactory: (opts, registry) =>
            {
                // Bundle requires every Windows-side seam plus the
                // shared registry + native resume spawner. When any
                // production input is missing (PwshPath unresolved,
                // AppRoot empty) the bundle is null and the Stage
                // 3i-C route family stays unregistered -- the read
                // surface still works, only the mutation surface
                // refuses to register.
                if (string.IsNullOrWhiteSpace(opts.PwshPath)) return null;
                if (string.IsNullOrWhiteSpace(opts.AppRoot))  return null;
                var reAuthScript = Path.Combine(opts.AppRoot, "broker",
                                                 "Auth", "WindowsReAuth.ps1");
                return new Stage3iCServiceBundle
                {
                    ReAuth        = new WindowsReAuthSidecarVerifier(
                                        opts.PwshPath,
                                        reAuthScript),
                    CredStore     = new WindowsCredentialSecretStore(),
                    CertProbe     = new WindowsCertificateProbe(),
                    CookRegistry  = registry,
                    ResumeSpawner = new NativeCookResumeSpawner(),
                    Clock         = () => DateTimeOffset.UtcNow,
                };
            });

        var log = new AppLogger(installState.AppLogsRoot);

        // Phase 7 production wiring: real mutex gate, real per-user pipe
        // server/client, real Win32 close dialog + AUMID setter.
        var endpoint = new WindowsIdentitySidEndpointProvider();
        var ctx = new CommandContext(
            installState, bootstrap, locks, sidecars, broker, log,
            Console.Out, Console.Error, parsed.InstallRoot,
            webView2Detector: new WebView2RuntimeDetector(new RealRegistryProbe()),
            uiHost: new WebView2WinFormsHost(),
            webView2Data: WebView2DataPaths.FromLocalAppData(),
            gate: new MutexAppInstanceGate(),
            ipcClient: new NamedPipeIpcClient(),
            ipcServerFactory: _ => new NamedPipeIpcServer(endpoint.GetEndpointName()),
            ipcEndpoint: endpoint,
            aumid: new Win32AppUserModelIdSetter(),
            closeDialog: new Win32CloseDialogService());

        log.Write("App", "command-start", "info", new Dictionary<string, object?>
        {
            ["verb"] = parsed.Verb
        });
        int code;
        try
        {
            code = AppCommandDispatcher.Dispatch(parsed, ctx);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("internal error: " + ex.Message);
            log.Write("App", "command-internal-error", "error", new Dictionary<string, object?>
            {
                ["message"] = ex.Message
            });
            return AppExitCodes.InternalError;
        }
        log.Write("App", "command-end", "info", new Dictionary<string, object?>
        {
            ["verb"] = parsed.Verb,
            ["exitCode"] = code
        });
        return code;
    }
}
