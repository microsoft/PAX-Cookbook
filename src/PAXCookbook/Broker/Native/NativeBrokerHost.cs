using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Routes;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native;

// Option C native broker host. The Kestrel server runs in-process
// inside PAXCookbook.exe so there is no spawned pwsh.exe and no
// console window. Loopback only -- both 127.0.0.1 and ::1 are bound;
// the "localhost" hostname resolves to one of those on Windows and
// is required for WebAuthn origin parity with the PowerShell broker.
//
// Stage 3c adds the SQLite/workspace read surface (recipes, cooks,
// auth profiles, templates, expanded health, runtime/version) via the
// Routes/* and Services/* split. The host owns Kestrel lifecycle,
// port selection, the static + SPA fallback, and the route-
// registration glue -- everything else lives in the split files so
// this class stays focused.
public sealed record NativeBrokerHostOptions(
    int PreferredPort,
    int PortRangeStart,
    int PortRangeEnd,
    string WorkspaceFolderPath,
    string? WebRoot = null,
    string? AppRoot = null,
    string? VersionFilePath = null,
    string? TemplatesDir = null,
    string? PaxScriptPath = null,
    // Stage 3d -- lock-bypass middleware kill switch. Defaults to
    // false so the Stage 3a/3b/3c read-only surfaces remain reachable
    // without an explicit unlock; flipping it to true activates
    // BrokerLockService.IsRouteAllowedWhenLocked and returns 423 for
    // any /api/v1/* path not on the lock-bypass list while the broker
    // is Locked. The host is dormant in production today (not wired
    // into BrokerController), so this flag never affects the PowerShell
    // production path. Tests use it to validate the 423 envelope.
    bool EnforceBrokerLock = false,
    // Stage 3e -- absolute path to the PowerShell adapter module
    // (Adapter.psm1). Used only as a fallback when the per-recipe
    // ExecutionMode is local-manual and the orchestrator needs the
    // hidden one-shot pwsh sidecar to resolve Get-PaxInvocationPlan.
    // When null/empty the cook-start route is not registered.
    string? AdapterModulePath = null,
    // Stage 3e -- absolute path to pwsh.exe. The runner needs a fixed
    // binary so the spawn does not depend on PATH ordering and never
    // accidentally launches Windows PowerShell 5.1. When null/empty
    // the cook-start route is not registered.
    string? PwshPath = null)
{
    // Production default: <assembly-directory>\web (parity with the
    // PowerShell broker's $Script:WebRoot = Join-Path $AppRoot 'web').
    // Tests pass an explicit WebRoot fixture so they never read the
    // installed app's web shell. Stage 3c adds the AppRoot-bound
    // defaults (VERSION.json, templates/, PAX script) so the default
    // construction yields a fully wired host.
    public static NativeBrokerHostOptions Default(string workspaceFolderPath)
    {
        var appRoot = AppContext.BaseDirectory;
        return new(
            PreferredPort:       17654,
            PortRangeStart:      17654,
            PortRangeEnd:        17664,
            WorkspaceFolderPath: workspaceFolderPath,
            WebRoot:             Path.Combine(appRoot, "web"),
            AppRoot:             appRoot,
            VersionFilePath:     Path.Combine(appRoot, "VERSION.json"),
            TemplatesDir:        Path.Combine(appRoot, "templates"),
            PaxScriptPath:       Path.Combine(appRoot, "resources", "pax",
                                              "PAX_Purview_Audit_Log_Processor.ps1"),
            AdapterModulePath:   Path.Combine(appRoot, "broker", "Pax", "Adapter.psm1"),
            PwshPath:            ResolveDefaultPwshPath());
    }

    // Stage 3j -- production factory for the installed app layout.
    // The Default() factory above assumes AppContext.BaseDirectory
    // equals AppRoot, which is FALSE in production (PAXCookbook.exe
    // ships under %LOCALAPPDATA%\PAXCookbook\App\bin\ so the base
    // directory is the BIN folder, one level below AppRoot). The
    // production wiring path resolves the true AppRoot from
    // install-state.json and hands it here so every derived path
    // (web/, VERSION.json, templates/, resources/pax/<script>,
    // broker/Pax/Adapter.psm1) lands on the installed layout instead
    // of the bin folder.
    public static NativeBrokerHostOptions ForInstalledApp(
        string workspaceFolderPath,
        string appRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceFolderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appRoot);
        return new(
            PreferredPort:       17654,
            PortRangeStart:      17654,
            PortRangeEnd:        17664,
            WorkspaceFolderPath: workspaceFolderPath,
            WebRoot:             Path.Combine(appRoot, "web"),
            AppRoot:             appRoot,
            VersionFilePath:     Path.Combine(appRoot, "VERSION.json"),
            TemplatesDir:        Path.Combine(appRoot, "templates"),
            PaxScriptPath:       Path.Combine(appRoot, "resources", "pax",
                                              "PAX_Purview_Audit_Log_Processor.ps1"),
            AdapterModulePath:   Path.Combine(appRoot, "broker", "Pax", "Adapter.psm1"),
            PwshPath:            ResolveDefaultPwshPath());
    }

    // Locate pwsh.exe for the default production options. Probes the
    // canonical install paths first (Program Files\PowerShell\7\), then
    // falls back to PATH lookup. Returns an empty string when nothing
    // is found -- callers treat that as "cook-start route disabled".
    private static string ResolveDefaultPwshPath()
    {
        var pf  = Environment.GetEnvironmentVariable("ProgramFiles");
        var pfx = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(pf))  candidates.Add(Path.Combine(pf,  "PowerShell", "7", "pwsh.exe"));
        if (!string.IsNullOrEmpty(pfx)) candidates.Add(Path.Combine(pfx, "PowerShell", "7", "pwsh.exe"));
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var probe = Path.Combine(dir.Trim('"'), "pwsh.exe");
                if (File.Exists(probe)) return probe;
            }
            catch { }
        }
        return string.Empty;
    }
}

public sealed record NativeBrokerHostStartResult(int Port, string BaseUrl);

public sealed class NativeBrokerHost : IAsyncDisposable
{
    private const string BrokerImplementationName = "native";

    // Extension allowlist for the static surface. Superset of the
    // PowerShell broker map (StaticHandler.ps1 $Script:StaticMimeMap):
    // PS shipped .html .css .js .json .svg .png .ico .woff2
    // .webmanifest. The native host adds .mjs .jpg .jpeg .webp .woff
    // .map per Stage 3b implementation requirements so future SPA
    // assets do not regress. Any extension absent from this map
    // returns 404 -- the static surface is allowlist-only.
    private static readonly IReadOnlyDictionary<string, string> StaticMimeMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"]        = "text/html; charset=utf-8",
            [".css"]         = "text/css; charset=utf-8",
            [".js"]          = "application/javascript; charset=utf-8",
            [".mjs"]         = "application/javascript; charset=utf-8",
            [".json"]        = "application/json; charset=utf-8",
            [".map"]         = "application/json; charset=utf-8",
            [".svg"]         = "image/svg+xml",
            [".png"]         = "image/png",
            [".jpg"]         = "image/jpeg",
            [".jpeg"]        = "image/jpeg",
            [".webp"]        = "image/webp",
            [".ico"]         = "image/x-icon",
            [".woff"]        = "font/woff",
            [".woff2"]       = "font/woff2",
            [".webmanifest"] = "application/manifest+json; charset=utf-8",
        };

    private readonly NativeBrokerHostOptions _options;
    private WebApplication? _app;
    private int _port;
    private string? _resolvedWebRoot;
    private DateTimeOffset _startedAtUtc;
    private BrokerLockService? _lockService;

    // Stage 3h -- optional service bundle that activates live PUT +
    // DELETE behavior on the scheduled-task routes. Null by default
    // so existing Stage 3g fixtures keep their 501-fallback path
    // unchanged. Tests wire the bundle via WithStage3hServiceOverride
    // before StartAsync; production wiring (when the native host is
    // finally routed through BrokerController) will compose the
    // production bundle inside StartAsync from the options record.
    private Stage3hServiceBundle? _stage3hOverride;

    // Stage 3i-A -- optional override bundle for the broker-
    // lifecycle / pax-script-export / update / cook-readiness
    // routes. Null in production (host uses live HTTP clients,
    // UTC clock, and IHostApplicationLifetime-backed shutdown);
    // tests inject a bundle with FakeHttpMessageHandler instances,
    // a deterministic clock, and a stubbed shutdown coordinator.
    private Stage3iAServiceBundle? _stage3iAOverride;

    // Stage 3i-B1 -- optional override bundle for the recipe
    // mutation surface (POST/PUT/DELETE on /api/v1/recipes). Null in
    // production for now (NativeBrokerHost is not yet routed through
    // BrokerController); when present, tests inject a deterministic
    // clock + ULID factory + paxAdapterVersion + createdBy template
    // so envelopes are byte-stable. When absent, the wiring layer
    // synthesizes a production-style bundle from the loaded
    // VersionInfo at MapApiRoutes time, so the route family activates
    // automatically once VERSION.json is wired.
    private Stage3iB1ServiceBundle? _stage3iB1Override;

    // Stage 3i-B2 -- optional override bundle for the recipe preview
    // + template materialize surface (POST /api/v1/recipes/preview and
    // POST /api/v1/templates/{id}/materialize). Mirrors the Stage 3i-B1
    // shape and adds a PreviewPlanProvider seam so tests inject a
    // stub adapter instead of spawning a real pwsh sidecar. Null in
    // production -- the wiring layer composes a production bundle
    // from VersionInfo, options.PwshPath, options.AdapterModulePath
    // and options.PaxScriptPath at MapApiRoutes time.
    private Stage3iB2ServiceBundle? _stage3iB2Override;

    // Stage 3i-B3 -- optional override bundle for the recipe-takeout
    // surface (POST /api/v1/recipes/<ulid>/takeout, POST
    // /api/v1/recipe-takeout/validate, POST /api/v1/recipe-takeout/
    // import). Carries the deterministic clock + ULID factory +
    // envelope provenance + workspace-install-path fingerprint hint
    // + optional Chef's Key display-label lookup so tests can
    // exercise the three endpoints without any AuthProfilesStore
    // dependency. Null in production -- the wiring layer composes a
    // production bundle from VersionInfo + the workspace install
    // path at MapApiRoutes time.
    private Stage3iB3ServiceBundle? _stage3iB3Override;

    // Stage 3i-C -- optional override bundle for the auth-profile
    // mutation / secret bind/remove / structural-test surface and
    // for the cook stop/kill/resume surface. Carries the Windows
    // re-auth verifier, the CredMan secret store, the X.509 probe,
    // the cook process registry, and the cook resume spawner so
    // tests can exercise the four route families without any Hello
    // UI, real CredMan write, real cert store open, or real pwsh
    // cook spawn. Null in production at Stage 3i-C (NativeBrokerHost
    // is not yet wired through BrokerController); when Stage 3j
    // ships the BrokerController switchover, production will compose
    // a bundle from singletons (WindowsReAuthVerifier,
    // WindowsCredentialSecretStore, WindowsCertificateProbe,
    // InMemoryCookProcessRegistry, DeferredCookResumeSpawner) and
    // hand it here.
    private Stage3iCServiceBundle? _stage3iCOverride;

    public NativeBrokerHost(NativeBrokerHostOptions options)
    {
        _options = options;
    }

    // Stage 3h -- fluent setter for the test harness (and future
    // production wiring). MUST be called before StartAsync so the
    // route registration sees the bundle. Returns this for chaining.
    public NativeBrokerHost WithStage3hServiceOverride(
        Stage3hServiceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        _stage3hOverride = bundle;
        return this;
    }

    // Stage 3i-A -- fluent setter for the test harness. MUST be
    // called before StartAsync so MapApiRoutes composes the override
    // probes/downloader/clock. Returns this for chaining.
    public NativeBrokerHost WithStage3iAServiceOverride(
        Stage3iAServiceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        _stage3iAOverride = bundle;
        return this;
    }

    // Stage 3i-B1 -- fluent setter for the test harness. MUST be
    // called before StartAsync so MapApiRoutes composes the
    // mutation service with the deterministic clock + ULID factory +
    // provenance bundle. Returns this for chaining.
    public NativeBrokerHost WithStage3iB1ServiceOverride(
        Stage3iB1ServiceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        _stage3iB1Override = bundle;
        return this;
    }

    // Stage 3i-B2 -- fluent setter for the test harness. MUST be
    // called before StartAsync so MapApiRoutes composes the preview
    // + materialize services with the deterministic clock + ULID
    // factory + provenance bundle + IRecipePreviewPlanProvider stub.
    // Returns this for chaining.
    public NativeBrokerHost WithStage3iB2ServiceOverride(
        Stage3iB2ServiceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        _stage3iB2Override = bundle;
        return this;
    }

    // Stage 3i-B3 -- fluent setter for the test harness. MUST be
    // called before StartAsync so MapApiRoutes composes the export /
    // validate / import services with the deterministic clock + ULID
    // factory + envelope provenance + workspace-install-path
    // fingerprint hint + optional Chef's Key display-label lookup.
    // Returns this for chaining.
    public NativeBrokerHost WithStage3iB3ServiceOverride(
        Stage3iB3ServiceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        _stage3iB3Override = bundle;
        return this;
    }

    // Stage 3i-C -- fluent setter for the test harness. MUST be
    // called before StartAsync so MapApiRoutes composes the auth-
    // profile mutation / secret / test services and the cook control
    // service with the test's FakeWindowsReAuthVerifier,
    // FakeCredentialSecretStore, FakeCertificateProbe,
    // FakeCookProcessRegistry, and FakeCookResumeSpawner. Returns
    // this for chaining.
    public NativeBrokerHost WithStage3iCServiceOverride(
        Stage3iCServiceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        _stage3iCOverride = bundle;
        return this;
    }

    public int Port => _port;

    // No trailing slash. Consumers that compose paths use
    // BaseUrl + "/api/v1/health" idiomatically; the WebView2 host can
    // also navigate to BaseUrl directly to reach the SPA shell.
    public string? BaseUrl => _app is null ? null : "http://localhost:" + _port;

    // Canonicalized web root after StartAsync. Null when no usable
    // root was configured (static requests return 404 in that case).
    public string? WebRoot => _resolvedWebRoot;

    // Exposed for the test harness to drive lock-state transitions
    // deterministically without going through the deferred unlock
    // route. Production code paths in later stages will wire this
    // through dedicated routes / middleware rather than reaching in
    // directly. Null until the host has started.
    internal BrokerLockService? LockService => _lockService;

    public async Task<NativeBrokerHostStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        var port = SelectPort(_options.PreferredPort, _options.PortRangeStart, _options.PortRangeEnd);
        _startedAtUtc = DateTimeOffset.UtcNow;

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(NativeBrokerHost).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
        });

        builder.WebHost.UseKestrel(opts =>
        {
            opts.Listen(IPAddress.Loopback, port, l => l.Protocols = HttpProtocols.Http1AndHttp2);
            opts.Listen(IPAddress.IPv6Loopback, port, l => l.Protocols = HttpProtocols.Http1AndHttp2);
        });

        var app = builder.Build();

        // Stage 3d -- in-process broker lock service. Boot state is
        // Locked, parity with the PowerShell broker. The service has
        // no persisted dependency, so initializing it here is free.
        _lockService = new BrokerLockService();

        // Stage 3d -- lock-bypass middleware. Always registered so
        // the wiring is consistent; gated behind _options.EnforceBrokerLock
        // so existing Stage 3a/3b/3c tests that do not opt in keep
        // their pre-Stage-3d behavior. /api/v1/health is allowlisted
        // unconditionally (parity with the PowerShell broker, which
        // short-circuits health BEFORE the lock middleware).
        app.Use(async (ctx, next) =>
        {
            if (!_options.EnforceBrokerLock)
            {
                await next().ConfigureAwait(false);
                return;
            }

            var path = ctx.Request.Path.HasValue ? ctx.Request.Path.Value! : string.Empty;
            // Only the /api/v1/* surface is lock-gated. SPA + static
            // assets are reachable regardless of lock state (the SPA
            // shell needs to render the lock overlay before any
            // unlock attempt).
            if (!path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase))
            {
                await next().ConfigureAwait(false);
                return;
            }

            // Health is always reachable.
            if (string.Equals(path, "/api/v1/health", StringComparison.OrdinalIgnoreCase))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var state = _lockService.GetState();
            if (state == Models.BrokerLockStateKind.Unlocked)
            {
                // Bump activity on every successful authenticated
                // API request EXCEPT the lock-state poller. Poller
                // bumps would defeat the inactivity sweep.
                if (!string.Equals(path, "/api/v1/broker/lock-state",
                                   StringComparison.OrdinalIgnoreCase))
                {
                    _lockService.TouchActivity();
                }
                await next().ConfigureAwait(false);
                return;
            }

            // Locked. Allowlist check.
            if (BrokerLockService.IsRouteAllowedWhenLocked(ctx.Request.Method, path))
            {
                await next().ConfigureAwait(false);
                return;
            }

            // Locked + not on bypass list -> 423 brokerLocked.
            ctx.Response.StatusCode = StatusCodes.Status423Locked;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-store";
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                code            = "brokerLocked",
                message         = "Broker is locked. Unlock required before this operation.",
                attemptedMethod = ctx.Request.Method,
                attemptedPath   = path,
            });
            await ctx.Response.WriteAsync(body).ConfigureAwait(false);
        });

        // Stage 3f -- enable the WebSocket middleware before the
        // endpoint mapping so MapGet handlers can call
        // HttpContext.WebSockets.IsWebSocketRequest /
        // AcceptWebSocketAsync. KeepAliveInterval mirrors the
        // default Kestrel WS behavior; we have no per-frame ping
        // requirement in the M1 contract (the PS broker's CookLogWs
        // does not send keepalives either).
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        // Route ordering is significant. Specific endpoints first so
        // the catch-all MapFallback never shadows them.
        MapApiRoutes(app, port);
        _resolvedWebRoot = ResolveWebRoot(_options.WebRoot);
        MapStaticAndSpa(app, _resolvedWebRoot);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
        _port = port;
        return new NativeBrokerHostStartResult(port, "http://localhost:" + port);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            try
            {
                await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await _app.DisposeAsync().ConfigureAwait(false);
                _app = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    // Stage 3c switchboard. Each route family lives in Routes/* and
    // each shared service in Services/*. The host wires them up here
    // so the request-handling code stays out of NativeBrokerHost.
    private void MapApiRoutes(WebApplication app, int port)
    {
        // Workspace-derived state. Computed once per host so per-
        // request routes only do path arithmetic / DB reads against
        // the resolved paths. Null when the configured workspace
        // path is empty / whitespace -- routes that require workspace
        // state return controlled errors in that case.
        var workspacePaths = WorkspacePathResolver.Resolve(_options.WorkspaceFolderPath);

        // Health is always registered, even when AppRoot/workspace
        // are unconfigured -- it is the diagnostic surface the host
        // uses to detect "alive but degraded".
        HealthRoutes.Register(
            app:                  app,
            brokerImplementation: BrokerImplementationName,
            port:                 port,
            workspacePaths:       workspacePaths,
            brokerStartedAtUtc:   _startedAtUtc,
            utcNow:               () => DateTimeOffset.UtcNow);

        // AppRoot-bound routes (runtime/version, templates). When
        // AppRoot is null -- Stage 3a/3b tests that pre-date the
        // Stage 3c options -- skip these so the static + SPA surface
        // and the health route still work unmodified.
        VersionInfo? versionInfoForCook = null;
        TemplateCatalogReader? catalog = null;
        if (!string.IsNullOrWhiteSpace(_options.AppRoot))
        {
            var versionInfo = VersionInfoReader.Load(
                _options.VersionFilePath ?? string.Empty);
            versionInfoForCook = versionInfo;
            RuntimeRoutes.Register(
                app:                     app,
                versionInfo:             versionInfo,
                workspacePaths:          workspacePaths,
                port:                    port,
                brokerStartedAtUtc:      _startedAtUtc,
                appRoot:                 _options.AppRoot!,
                paxScriptAbsolutePath:   _options.PaxScriptPath ?? string.Empty,
                versionFileAbsolutePath: _options.VersionFilePath ?? string.Empty);

            catalog = string.IsNullOrWhiteSpace(_options.TemplatesDir)
                ? TemplateCatalogReader.Empty()
                : TemplateCatalogReader.Load(_options.TemplatesDir!);
            TemplateReadRoutes.Register(app, catalog);
        }

        // Workspace/DB-bound routes. Routes themselves return 500 /
        // 404 when cookbook.sqlite is missing so a workspace folder
        // that exists but has not been initialised yet is reported
        // as a structured error instead of a crash.
        if (workspacePaths is not null)
        {
            var sqlite = new SqliteWorkspaceReader(workspacePaths);
            RecipeReadRoutes.Register(app, sqlite);

            // Stage 3i-B1 -- recipe mutation surface (POST + PUT +
            // DELETE on /api/v1/recipes). Registered AFTER the read
            // routes so the verb-keyed router lands writes here while
            // GET continues to flow through RecipeReadRoutes. Wiring
            // is internally gated on workspace + provenance, so the
            // family stays unregistered (-> 404 via MapFallback) when
            // VERSION.json is missing/incomplete or the test forgot
            // to inject a bundle.
            var stage3iB1Bundle = _stage3iB1Override
                ?? Stage3iB1ServiceBundle.FromVersionInfo(versionInfoForCook);
            Stage3iB1Wiring.Register(app, workspacePaths, stage3iB1Bundle);

            // Stage 3i-B2 -- recipe preview + template materialize.
            // Registered AFTER 3i-B1 so the verb-keyed router exposes
            // POST /api/v1/recipes/preview and POST
            // /api/v1/templates/{id}/materialize alongside the
            // mutation family. The wiring is internally gated on
            // workspace + provenance + (planProvider OR pwsh-triplet)
            // and on (catalog non-null) so the two route families
            // activate independently when their preconditions hold.
            var stage3iB2Bundle = _stage3iB2Override
                ?? Stage3iB2ServiceBundle.FromVersionInfo(versionInfoForCook);
            Stage3iB2Wiring.Register(
                app:               app,
                workspacePaths:    workspacePaths,
                sqlite:            sqlite,
                catalog:           catalog,
                overrideBundle:    stage3iB2Bundle,
                pwshPath:          _options.PwshPath,
                adapterModulePath: _options.AdapterModulePath,
                paxScriptPath:     _options.PaxScriptPath);

            // Stage 3i-B3 -- recipe-takeout (export / validate /
            // import) surface. Registered after 3i-B2 so the verb-
            // keyed router exposes POST
            // /api/v1/recipes/<ulid>/takeout, POST
            // /api/v1/recipe-takeout/validate, and POST
            // /api/v1/recipe-takeout/import alongside the preview /
            // materialize family. The production bundle pulls
            // PaxAdapterVersion / BundledPaxVersion / CookbookVersion
            // / ReleaseChannel from VersionInfo and the workspace
            // install path from options.WorkspaceFolderPath; tests
            // inject an override bundle so the three endpoints run
            // with a deterministic clock + ULID factory + display-
            // label stub.
            var stage3iB3Bundle = _stage3iB3Override
                ?? Stage3iB3ServiceBundle.FromVersionInfo(
                       versionInfoForCook,
                       _options.WorkspaceFolderPath);
            Stage3iB3Wiring.Register(
                app:               app,
                workspacePaths:    workspacePaths,
                sqlite:            sqlite,
                overrideBundle:    stage3iB3Bundle);

            // Stage 3i-C -- auth-profile mutation + secret bind/remove
            // + structural-test + cook stop/kill/resume. Registered
            // AFTER 3i-B3 so the verb-keyed router exposes the four
            // new route families before the read-only AuthProfile
            // Read / CookRead surfaces. The wiring is internally
            // gated on workspacePaths + sqlite + the Stage3iC bundle;
            // when any are missing the families stay unregistered
            // (-> 404 via MapFallback) so the test harness must
            // inject a bundle to exercise them. Production wires
            // the bundle once Stage 3j flips NativeBrokerHost into
            // BrokerController; until then the override is null and
            // these routes are inactive in production.
            Stage3iCWiring.Register(
                app:               app,
                workspacePaths:    workspacePaths,
                sqlite:            sqlite,
                bundle:            _stage3iCOverride);

            CookReadRoutes.Register(app, sqlite);

            // Stage 3f -- specialized cook-view live-tail WS endpoint.
            // GET (Upgrade: websocket) /api/v1/cooks/<id>/log/ws.
            // Wire it next to CookReadRoutes because they share the
            // SqliteWorkspaceReader (status + cook_folder lookup) and
            // resolve the same cook.log path layout. Lifetime token
            // ensures the per-socket tailer loop ends on host shutdown.
            CookLogWebSocketRoutes.Register(app, sqlite, app.Lifetime);
            AuthProfileReadRoutes.Register(app, sqlite);

            // Stage 3g -- scheduled-task surface. GET routes are full
            // ports (list + single-recipe). PUT and DELETE return
            // controlled 501 sentinels (scheduled_task_put_deferred /
            // scheduled_task_delete_deferred) because the native
            // broker has no WebAuthn re-auth verifier, no projection-
            // hash composer, and no Credential Manager writer yet --
            // those land in Stage 3h. ScheduledTaskStore opens its
            // own per-call connections (Mode=ReadWrite) so the read
            // path can stamp last_stale_check_at without going
            // through SqliteWorkspaceReader's read-only handle.
            var scheduledTaskStore = new ScheduledTaskStore(workspacePaths.DatabaseFile);
            ScheduledTaskRoutes.Register(
                app:            app,
                reader:         sqlite,
                store:          scheduledTaskStore,
                stage3hBundle:  _stage3hOverride);

            // Stage 3e -- cook-start route. Requires:
            //   * workspacePaths (sqlite + Recipes/ + Cooks/)
            //   * versionInfo    (PAX baseline hash + script version)
            //   * PaxScriptPath  (the file to rehash + spawn)
            //   * AdapterModulePath + PwshPath (sidecar + child PSI)
            // If any of those is missing the route is intentionally
            // not registered -- a POST returns the unknown-/api 404.
            // This keeps the Stage 3a/3b fixtures (no AppRoot) and
            // workspace-only tests passing without an explicit opt-in.
            if (versionInfoForCook is not null
                && !string.IsNullOrWhiteSpace(_options.PaxScriptPath)
                && !string.IsNullOrWhiteSpace(_options.AdapterModulePath)
                && !string.IsNullOrWhiteSpace(_options.PwshPath))
            {
                var integrity = new PaxScriptIntegrityVerifier(
                    versionInfoForCook,
                    _options.PaxScriptPath!);
                var recipeReader = new RecipeFileReader(workspacePaths);
                var folders      = new CookFolderService(workspacePaths);
                var sidecar      = new PaxInvocationPlanProvider(
                    _options.PwshPath!,
                    _options.AdapterModulePath!);
                var runner       = new PaxProcessRunner(_options.PwshPath!);
                var writer       = new CookRowWriter(workspacePaths);
                var executor     = new CookExecutionService(
                    sqlite:      sqlite,
                    writer:      writer,
                    recipes:     recipeReader,
                    integrity:   integrity,
                    adapter:     sidecar,
                    folders:     folders,
                    runner:      runner,
                    versionInfo: versionInfoForCook,
                    // Stage 3j -- share the SAME registry the Stage
                    // 3i-C bundle carries so /stop and /kill on a
                    // running cook find the handle the executor
                    // populated at spawn time. The bundle is absent
                    // in Stage 3a-3i tests, so registry stays null
                    // and CookExecutionService falls back to the
                    // pre-Stage-3j no-op behavior (no register /
                    // deregister) -- existing fixtures see no diff.
                    registry:    _stage3iCOverride?.CookRegistry);
                CookExecutionRoutes.Register(app, executor);
            }
        }

        // Stage 3d -- broker lock + WebAuthn surface. Always
        // registered (no workspace dependency for the lock service;
        // the WebAuthn readiness reader handles a missing workspace
        // path by returning registered:false). The unlock route
        // accepts the production WindowsReAuthSidecarVerifier via
        // the Stage 3i-C bundle so the SPA's Authenticate button
        // drives a real Windows Hello / PIN prompt.
        BrokerLockRoutes.Register(app, _lockService!, _stage3iCOverride?.ReAuth);
        var readiness = new WebAuthnReadinessReader(_options.WorkspaceFolderPath);
        WebAuthnRoutes.Register(app, readiness, port);

        // Stage 3i-A -- broker lifecycle (close-intent + shutdown),
        // bundled PAX export, /updates/* family, and POST
        // /api/v1/cooks/readiness.
        Stage3iAWiring.Register(
            app:             app,
            workspacePaths:  workspacePaths,
            versionInfo:     versionInfoForCook,
            paxScriptPath:   _options.PaxScriptPath,
            overrideBundle:  _stage3iAOverride,
            appLifetime:     app.Lifetime);
    }

    // Static + SPA fallback. Mirrors StaticHandler.ps1 semantics for
    // the static surface (extension allowlist, GetFullPath traversal
    // guard, no-store cache header, GET-only) and adds an SPA
    // fallback for extensionless non-API GET/HEAD requests so client-
    // side router paths (e.g. /recipes/<ulid>/edit) hydrate index.html.
    // The /api/v1/* surface is explicitly guarded: any unmatched /api/
    // path returns 404 JSON, never index.html.
    private static void MapStaticAndSpa(WebApplication app, string? webRoot)
    {
        // Eager canonicalization once per host. If the root is
        // unconfigured or missing on disk we still register the
        // fallback so requests get a controlled 404 instead of an
        // unhandled crash; only /api/v1/health remains useful.
        string? rootCanonical = null;
        string? rootBoundary = null;
        string? indexPath = null;
        if (!string.IsNullOrWhiteSpace(webRoot) && Directory.Exists(webRoot))
        {
            rootCanonical = Path.GetFullPath(webRoot);
            rootBoundary = rootCanonical + Path.DirectorySeparatorChar;
            indexPath = Path.Combine(rootCanonical, "index.html");
        }

        app.MapFallback("{*path}", async ctx =>
        {
            // Explicit catch-all pattern. The default MapFallback
            // overload applies a :nonfile constraint that excludes
            // anything routing considers a file (path segments
            // containing a dot before the end), which would silently
            // drop /index.html, /assets/app.js, /favicon.ico, etc.
            // We want every unmatched GET to reach our handler so the
            // allowlist + traversal guard apply uniformly.
            await ServeStaticOrSpaAsync(ctx, rootCanonical, rootBoundary, indexPath)
                .ConfigureAwait(false);
        });
    }

    private static async Task ServeStaticOrSpaAsync(
        HttpContext ctx,
        string? rootCanonical,
        string? rootBoundary,
        string? indexPath)
    {
        var method = ctx.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            await WriteStaticErrorAsync(ctx, StatusCodes.Status404NotFound, "not_found");
            return;
        }

        var path = ctx.Request.Path.HasValue ? ctx.Request.Path.Value! : "/";

        // API guard: any /api/* path that reaches the fallback means
        // no API route matched. Return 404 JSON -- never SPA-fallback
        // an API request, that would mask broken integrations behind
        // a 200 index.html.
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await WriteApiNotFoundAsync(ctx);
            return;
        }

        if (rootCanonical is null || rootBoundary is null || indexPath is null)
        {
            // No usable web root configured. Controlled 404 with the
            // same envelope as a missing asset.
            await WriteStaticErrorAsync(ctx, StatusCodes.Status404NotFound, "not_found");
            return;
        }

        // Root path serves index.html (parity with StaticHandler.ps1).
        var rel = path == "/" ? "/index.html" : path;
        rel = rel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(rootCanonical, rel));
        }
        catch
        {
            await WriteStaticErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden");
            return;
        }

        // Strict descendant check. OrdinalIgnoreCase covers Windows
        // case-insensitive filesystems. Equality with the root itself
        // is rejected because directory-as-target is handled below.
        if (!candidate.StartsWith(rootBoundary, StringComparison.OrdinalIgnoreCase))
        {
            await WriteStaticErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden");
            return;
        }

        var ext = Path.GetExtension(candidate);
        if (!string.IsNullOrEmpty(ext))
        {
            // Typed-asset request. Must match the allowlist AND exist
            // on disk; otherwise 404 (no SPA fallback for a request
            // that explicitly asked for a typed asset).
            if (StaticMimeMap.TryGetValue(ext, out var mime) && File.Exists(candidate))
            {
                await WriteFileResponseAsync(ctx, candidate, mime);
                return;
            }
            await WriteStaticErrorAsync(ctx, StatusCodes.Status404NotFound, "not_found");
            return;
        }

        // Extensionless non-API GET/HEAD. If the candidate maps to a
        // real directory, do NOT list it and do NOT SPA-fallback --
        // return 404 (mirrors the PowerShell broker's Test-Path
        // -PathType Leaf check).
        if (Directory.Exists(candidate))
        {
            await WriteStaticErrorAsync(ctx, StatusCodes.Status404NotFound, "not_found");
            return;
        }

        // SPA fallback: serve index.html so the client-side router can
        // hydrate the route. index.html itself is allowlisted (.html
        // is in the MIME map) so the response shape matches a direct
        // GET of /index.html.
        if (File.Exists(indexPath))
        {
            await WriteFileResponseAsync(ctx, indexPath, StaticMimeMap[".html"]);
            return;
        }

        await WriteStaticErrorAsync(ctx, StatusCodes.Status404NotFound, "not_found");
    }

    private static async Task WriteFileResponseAsync(HttpContext ctx, string path, string mime)
    {
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = mime;
        ctx.Response.ContentLength = bytes.LongLength;
        // Parity with StaticHandler.ps1: Cache-Control: no-store on
        // every static response. The SPA shell is updated in lockstep
        // with the broker, so caching is actively undesirable.
        ctx.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsHead(ctx.Request.Method))
        {
            return;
        }
        await ctx.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteStaticErrorAsync(HttpContext ctx, int status, string message)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentLength = bytes.LongLength;
        ctx.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsHead(ctx.Request.Method))
        {
            return;
        }
        await ctx.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteApiNotFoundAsync(HttpContext ctx)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"not_found\"}");
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength = bytes.LongLength;
        ctx.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsHead(ctx.Request.Method))
        {
            return;
        }
        await ctx.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
    }

    // Web root resolution: an explicit non-empty path is canonicalized
    // (GetFullPath); empty/whitespace returns null so the static
    // handler 404s cleanly. Non-existent directories are returned
    // canonical -- the handler's Directory.Exists check at request
    // time produces the controlled error.
    internal static string? ResolveWebRoot(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        try
        {
            return Path.GetFullPath(configured);
        }
        catch
        {
            return null;
        }
    }

    // Port selection: prefer the configured preferred port; if it is
    // already bound, scan the range start..end for the first free
    // loopback port. Mirrors §6 (Start-Broker.ps1 fallback range
    // 17654-17664).
    internal static int SelectPort(int preferred, int rangeStart, int rangeEnd)
    {
        if (rangeStart < 1 || rangeEnd < rangeStart)
        {
            throw new ArgumentException("Invalid port range: " + rangeStart + ".." + rangeEnd);
        }

        var ordered = BuildSearchOrder(preferred, rangeStart, rangeEnd);
        foreach (var p in ordered)
        {
            if (IsLoopbackPortFree(p)) return p;
        }
        throw new InvalidOperationException(
            "No free loopback port available in range " + rangeStart + "-" + rangeEnd + ".");
    }

    private static IEnumerable<int> BuildSearchOrder(int preferred, int rangeStart, int rangeEnd)
    {
        var clampedPreferred = Math.Clamp(preferred, rangeStart, rangeEnd);
        yield return clampedPreferred;
        for (int p = rangeStart; p <= rangeEnd; p++)
        {
            if (p != clampedPreferred) yield return p;
        }
    }

    private static bool IsLoopbackPortFree(int port)
    {
        // Probe-bind: attempt to bind both IPv4 + IPv6 loopback on the
        // candidate port. Listener inspection (GetActiveTcpListeners)
        // alone misses sockets in TIME_WAIT and per-user reservations,
        // so probe-bind is the authoritative test. Sockets are
        // immediately closed -- no race-free guarantee, but Kestrel's
        // subsequent bind will surface any race as a startup error.
        return TryProbeBind(IPAddress.Loopback, port) &&
               TryProbeBind(IPAddress.IPv6Loopback, port);
    }

    private static bool TryProbeBind(IPAddress address, int port)
    {
        Socket? s = null;
        try
        {
            s = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            s.ExclusiveAddressUse = true;
            s.Bind(new IPEndPoint(address, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            s?.Close();
        }
    }
}
