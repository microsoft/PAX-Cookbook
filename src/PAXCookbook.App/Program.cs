using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.App;

// PAX Cookbook native runtime — Office-grade EXE shell (X1 skeleton).
//
// This is the smallest safe slice of the native runtime port:
//   - PAX Cookbook.exe .NET app shell.
//   - In-process ASP.NET Core Kestrel broker skeleton.
//   - Loopback-only bind using the frozen port behavior (preferred-first, then scan).
//   - Session-token middleware skeleton.
//   - /api/v1/health route parity with the PowerShell oracle (structural stub).
//
// The PowerShell broker (app\broker\Start-Broker.ps1) remains the immutable parity
// oracle. This app does not launch, host, or depend on any PowerShell runtime.
internal static class Program
{
    private const string AppName = "PAX Cookbook";
    private const string RuntimeKind = "dotnet-kestrel";

    // Mirrors app/VERSION.json cookbook.version. Stubbed as a constant in X1.
    private const string AppVersion = "1.0.0";

    // Frozen loopback port behavior (oracle: app\broker\Start-Broker.ps1).
    private const int PreferredPort = 17654;
    private const int PortRangeStart = 17654;
    private const int PortRangeEnd = 17664;

    private const string LoopbackTransport = "loopback-http";
    private const string LoopbackBindAddress = "127.0.0.1";

    // Recipe file-association extensions. Mirrors
    // PAXCookbook.Shared.ProductConstants.PaxLiteFileExtension /
    // PaxFullFileExtension (the App project does not reference Shared).
    private const string PaxLiteExtension = ".paxlite";
    private const string PaxFullExtension = ".pax";

    private static readonly DateTime StartedAtUtc = DateTime.UtcNow;

    // Oracle static MIME map (app\broker\Http\StaticHandler.ps1). Unknown
    // extensions are intentionally NOT served (404), matching the oracle.
    private static readonly IReadOnlyDictionary<string, string> StaticMimeMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".js"] = "application/javascript; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".ico"] = "image/x-icon",
            [".woff2"] = "font/woff2",
            [".webmanifest"] = "application/manifest+json; charset=utf-8",
        };

    [STAThread]
    public static int Main(string[] args)
    {
        // WDAC fix: the app is launched through the Microsoft-signed dotnet.exe
        // host (dotnet.exe "PAX Cookbook.dll") instead of the former wscript.exe
        // + launch.vbs hidden launcher, because strict corporate WDAC policies
        // block Windows Script Host. dotnet.exe is a console application, so hide
        // and detach its console window as the very first thing the process does
        // — in well under a millisecond, so no blank terminal flashes.
        ConsoleWindowHelper.HideConsoleWindow();

        // --headless runs the long-lived background broker daemon: the
        // in-process Kestrel broker with a system-tray presence but no WebView2
        // window, so scheduled bakes fire in the background and the user can open
        // the UI or stop the broker from the tray. The HKCU Run key starts this
        // at login. --no-window is the older test-harness flag: broker only, no
        // window AND no tray, blocking in WaitForShutdown for the smoke driver.
        // Both suppress the WebView2 window (noWindow); only --headless adds the
        // tray daemon loop (headlessDaemon).
        bool headlessDaemon = args.Any(a =>
            string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
        bool noWindow = headlessDaemon || args.Any(a =>
            string.Equals(a, "--no-window", StringComparison.OrdinalIgnoreCase));

        // Narrowly-scoped test-only seam. When this command-line flag is
        // present the broker starts the lock machine Unlocked instead of the
        // default Locked boot state, so the automated smoke harness can exercise
        // the unlocked read surface and the lock-activity bump behavior. This is
        // NOT an HTTP endpoint and NOT a product force-unlock: it is unavailable
        // in normal runtime (the desktop launcher never passes it) and uses the
        // same BrokerLock.SetUnlocked() path the WebAuthn ceremony uses.
        bool bootUnlockedSeam = args.Any(a =>
            string.Equals(a, "--test-seam-boot-unlocked", StringComparison.OrdinalIgnoreCase));

        // Narrowly-scoped test-only cook-preparation seam (X15). The public cook
        // route always stops at the no-child boundary (501). This flag lets the
        // smoke harness drive the pre-spawn preparation pipeline (cook-folder
        // files + cook row, status='running', started_at NULL) and assert it,
        // still WITHOUT spawning the PAX engine. Not an HTTP endpoint, never
        // passed by the desktop launcher.
        bool cookPrepareSeam = args.Any(a =>
            string.Equals(a, "--test-seam-cook-prepare", StringComparison.OrdinalIgnoreCase));

        // Narrowly-scoped test-only manual-cook re-auth seam (X16). The manual
        // cook route fails closed at gate 10 (401 reAuthRequired) unless a fresh
        // Windows Hello verdict is satisfied. There is no scriptable WebAuthn
        // ceremony in the smoke harness, so this CLI-only flag stands in for a
        // Verified verdict. It is NOT an HTTP endpoint, NOT a product force-unlock,
        // NOT a lock-state bypass, and is never passed by the desktop launcher;
        // its use is logged as a discovery marker on stdout below.
        bool manualCookReAuthSeam = args.Any(a =>
            string.Equals(a, "--test-seam-manual-cook-reauth-verified", StringComparison.OrdinalIgnoreCase));

        // Test-only override for the cook child interpreter path (X16). Lets the
        // smoke harness point the supervisor at a known pwsh (or an invalid path
        // to exercise the spawn-failure branch) without depending on the test
        // host's PATH. Empty means "use the production `pwsh` resolution".
        string? cookPwshPathOverride = ResolveCookPwshPathOverride(args);

        // Test-only window-mode self-close delay (X16C-GATE-FIX3). When > 0, the
        // native shell schedules a single form.Close() after this many
        // milliseconds, driving the same teardown path as clicking the window's
        // X button so the automated smoke can verify clean process exit and exe
        // lock release without a human at the keyboard. Not an HTTP route; never
        // passed by the desktop launcher. <= 0 means "no self-close".
        int selfCloseAfterMs = ResolveSelfCloseAfterMsOverride(args);

        // Diagnostic taskbar-identity override (--test-seam-aumid <id>). Null in
        // normal operation; the launcher never passes it. Used to rule out a
        // stale per-AUMID shell icon cache during taskbar-icon diagnosis.
        string? testSeamAumid = ResolveTestSeamAumidOverride(args);

        // Live taskbar-icon diagnostic seam (--test-seam-icon-diagnostics). When
        // present, the process opens a real native window whose icon wiring is
        // identical to the product shell, reads back exactly what Windows holds
        // for that window (WM_GETICON handles, class icons, window-level
        // AppUserModelID, the executable's associated/embedded icons, the tray
        // icon, and the on-disk shortcut/pin inventory), writes the evidence to
        // an output folder, and exits. It never acquires the PAX engine, never
        // runs a WebAuthn ceremony, and never modifies any shortcut it inspects.
        // The desktop launcher never passes it.
        bool iconDiagnosticsSeam = args.Any(a =>
            string.Equals(a, "--test-seam-icon-diagnostics", StringComparison.OrdinalIgnoreCase));
        string? iconDiagnosticsOutOverride = ResolveIconDiagnosticsOutOverride(args);

        // Test-only override for the pre-spawn disk-space hard floor (oracle
        // $Script:MinFreeDiskBytesForCook). Lets the smoke harness force a 507
        // insufficient_disk_space verdict deterministically. < 0 means "use the
        // production default".
        long cookMinFreeBytes = ResolveCookMinFreeBytesOverride(args);

        // CK-3 test-only credential-injection seam. When
        // --test-seam-cook-credential-env <chefKeyId|NONE> is present, the process
        // resolves the named Chef's Key, exercises the gate-14 bake-time resolution
        // (the App-registration 501 is gone) and the child-only GRAPH_* injection
        // builder against an in-memory ProcessStartInfo, prints discovery markers to
        // stdout, and exits BEFORE acquiring the engine, starting Kestrel, opening a
        // window, taking the single-instance lock, spawning pwsh, or contacting
        // Graph. It NEVER prints the client secret -- only a presence flag, a
        // length, and a match boolean against an expected SHA-256 the smoke
        // supplies. The desktop launcher never passes it.
        string? cookCredentialEnvSeamChefKeyId = ResolveSeamValueArg(args, "--test-seam-cook-credential-env");
        if (cookCredentialEnvSeamChefKeyId is not null)
        {
            string seamMode = ResolveSeamValueArg(args, "--test-seam-cook-credential-mode") ?? string.Empty;
            string? seamExpectSha = ResolveSeamValueArg(args, "--test-seam-cook-credential-expect-sha256");
            return RunCookCredentialEnvSeam(cookCredentialEnvSeamChefKeyId, seamMode, seamExpectSha);
        }

        // CK-match test seam (App-registration auth-revert + CK-type validation).
        // Given a recipe JSON file, drives the REAL save-path mismatch decision
        // (ChefKeyModel.TryGetRecipeModeMismatch, shared by the recipe create /
        // update routes) and the REAL read-only readiness projection
        // (RecipeReadinessModel.Handle) against a bound Chef's Key resolved from
        // the per-user WCM vault. Metadata only -- it NEVER reads a secret, NEVER
        // persists a recipe, NEVER spawns pwsh, NEVER acquires the engine, NEVER
        // starts Kestrel, and NEVER contacts a tenant. Prints discovery markers
        // and exits. The desktop launcher never passes it.
        string? ckMatchSeamFile = ResolveSeamValueArg(args, "--test-seam-ckmatch-file");
        if (ckMatchSeamFile is not null)
        {
            return RunCkMatchSeam(ckMatchSeamFile, args);
        }

        // CK-4 Telegram-notification test seam. Exercises the metadata-only
        // message builders, the Device Code stdout parser, the secret-free
        // settings projection, the getUpdates chat-id extractor, and the
        // swallow-all send primitive WITHOUT a real network call, a running
        // broker, an acquired engine, or any PAX spawn. It NEVER sends a real
        // Telegram message (the notify-throws case injects a throwing fake sender
        // to prove non-propagation) and NEVER prints a bot token. The desktop
        // launcher never passes it.
        string? telegramSeamKind = ResolveSeamValueArg(args, "--test-seam-telegram");
        if (telegramSeamKind is not null)
        {
            return RunTelegramNotifierSeam(args, telegramSeamKind);
        }

        // V1 cook-window-decision test seam. Prints whether a given recipe auth
        // mode allocates an interactive console window for the PAX child
        // (CreateNoWindow=false for WebLogin's MSAL/WAM browser sign-in; headless
        // for every other / unbound mode) by calling the SAME production helper
        // the supervisor uses to set ProcessStartInfo.CreateNoWindow. It resolves
        // nothing, spawns nothing, opens no window, and contacts no service; it
        // just prints discovery markers and exits BEFORE acquiring the engine,
        // starting Kestrel, or taking any lock. The desktop launcher never passes
        // it.
        string? cookWindowDecisionSeamMode = ResolveSeamValueArg(args, "--test-seam-cook-window-decision");
        if (cookWindowDecisionSeamMode is not null)
        {
            return RunCookWindowDecisionSeam(cookWindowDecisionSeamMode);
        }

        // Resume-command test seam. Prints the projected PAX resume command for a
        // given checkpoint path (and optional --test-seam-resume-force) using the
        // SAME pure builder the resume route uses, with NO Chef's Key resolved (so
        // no auth tail and no credential read). It resolves nothing from Windows
        // Credential Manager, spawns nothing, opens no window, starts no host, and
        // contacts no service; it prints discovery markers and exits BEFORE
        // acquiring the engine or taking any lock. The desktop launcher never
        // passes it.
        string? resumeCommandSeamPath = ResolveSeamValueArg(args, "--test-seam-resume-command");
        if (resumeCommandSeamPath is not null)
        {
            bool resumeSeamForce = args.Any(a =>
                string.Equals(a, "--test-seam-resume-force", StringComparison.OrdinalIgnoreCase));
            string resumeSeamCommand = RecipeReadModel.TestSeamBuildResumeCommand(resumeCommandSeamPath, resumeSeamForce);
            Console.WriteLine($"V1RESUME_CHECKPOINT={resumeCommandSeamPath}");
            Console.WriteLine($"V1RESUME_FORCE={(resumeSeamForce ? "true" : "false")}");
            Console.WriteLine($"V1RESUME_PAX_COMMAND={resumeSeamCommand}");
            return 0;
        }

        // V1 console-hide fallback test seam. Proves the best-effort
        // ShowWindow(SW_HIDE) console-hide fallback (used to hide the WebLogin
        // child's console window) never throws on a zero handle and the bounded
        // poll returns within budget without blocking. It spawns nothing, opens no
        // window, contacts no service, and reads no secret. The desktop launcher
        // never passes it.
        if (args.Any(a => string.Equals(a, "--test-seam-hide-console-fallback", StringComparison.OrdinalIgnoreCase)))
        {
            return RunHideConsoleFallbackSeam();
        }

        // X7a.2 scheduled-run one-shot. A per-user Windows Scheduled Task
        // (registered in X7a.3) fires "PAX Cookbook.exe --run-scheduled-recipe
        // <recipeId> --workspace <ws> --approot <app>" to run exactly ONE
        // scheduled cook headlessly. This handler is fully self-contained and
        // returns BEFORE the single-instance guard, the appRoot-fatal resolution,
        // the Kestrel/UI-port host, and the WinForms window are ever reached: a
        // scheduled run opens NO window, takes NO single-instance mutex, and
        // starts NO HTTP server. It runs the cook through the SINGLE sanctioned
        // cook pipeline (constraint 8) via RecipeReadModel.StartScheduledCook,
        // which authorizes the run by the recipe's ENABLED schedule plus its bound
        // Chef's Key — the Brian-approved constraint-10 modification waives the
        // per-operation Windows Hello step-up for SCHEDULED cooks ONLY; the manual
        // cook route is untouched. The desktop launcher never passes this flag.
        //
        // DEPRECATED (V2 two-process): prefer --bake, which DELEGATES the cook to
        // the running headless daemon (one broker, one cook supervisor, shared
        // logging/history). --run-scheduled-recipe spawns a STANDALONE cook
        // outside the daemon — a "ghost run" relative to the two-process model. It
        // is retained ONLY for backward compatibility with existing Task Scheduler
        // jobs registered before --bake existed; it prints a switch-to-bake warning
        // (below) and should be removed once those tasks are re-registered to use
        // --bake.
        string? runScheduledRecipeId = ResolveSeamValueArg(args, "--run-scheduled-recipe");
        if (runScheduledRecipeId is not null)
        {
            return RunScheduledRecipeOneShot(args, runScheduledRecipeId);
        }

        // --bake <recipeId> launch mode (Windows Task Scheduler integration with
        // the two-process daemon broker). Task Scheduler is the alarm clock; the
        // headless daemon is the single execution engine. This one-shot CLI
        // DELEGATES the cook to an already-running daemon and never runs PAX
        // itself — the rule is "no bake without the daemon, no ghost runs". Like
        // --run-scheduled-recipe it returns from Main BEFORE the single-instance
        // guard, the appRoot-fatal resolution, the Kestrel/UI host, and the
        // WinForms window, so --bake opens NO window, starts NO broker, and takes
        // NO mutex. Evaluated here — before the --headless / attach logic below —
        // so --bake takes priority over --headless if both are somehow present.
        // The desktop launcher never passes it; only a Task Scheduler action does.
        string? bakeRecipeId = ResolveSeamValueArg(args, "--bake");
        if (bakeRecipeId is not null)
        {
            return RunBakeViaDaemon(args, bakeRecipeId);
        }

        // Observe-only capture of a double-clicked recipe file (.paxlite/.pax).
        // Computed BEFORE the single-instance guard so even a secondary instance
        // logs what the shell handed it. The end-to-end import handoff is a
        // documented blocker (see slice report); nothing here imports or
        // navigates.
        FileOpenRequest? fileOpenRequest = ResolveFileOpenRequest(args);

        string workspacePath = ResolveWorkspacePath(args);
        string runtimeDir = Path.Combine(workspacePath, "Runtime");
        Directory.CreateDirectory(runtimeDir);

        // Ensure the workspace index database exists with the full schema before
        // any route can serve a write. The oracle created cookbook.sqlite on
        // first launch; the native broker must do the same or the first recipe
        // save on a fresh install fails with persist_failed (the read routes
        // tolerate a missing database, the write routes do not).
        WorkspaceDatabase.EnsureInitialized(workspacePath);

        string tokenFile = Path.Combine(runtimeDir, "broker.token");
        string importHandoffDir = ImportHandoffQueue.ResolveDir(workspacePath);

        // Single-instance guard (window mode only). One running window per
        // workspace: a second launch must not start a second Kestrel server or
        // take a second workspace lock. The guard is keyed by the workspace path
        // so isolated-workspace test runs never collide with each other or with
        // a real install. Headless mode (--no-window) is exempt so the smoke
        // harness can run several broker instances against isolated workspaces.
        //
        // When a second instance finds the mutex already held it signals the
        // running instance's restore event (so the running window comes to the
        // foreground) and exits cleanly BEFORE selecting a port or starting the
        // host — never a second server, never a second lock. The named objects
        // are Local\ (per-session) and carry no payload, so this is not an
        // unauthenticated control surface.
        Mutex? singleInstanceMutex = null;
        EventWaitHandle? restoreSignal = null;
        if (!noWindow && !iconDiagnosticsSeam)
        {
            string instanceKey = ComputeSingleInstanceKey(workspacePath);
            string mutexName = $"Local\\PAXCookbook.App.SingleInstance.{instanceKey}";
            string restoreName = $"Local\\PAXCookbook.App.Restore.{instanceKey}";

            singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
            if (!createdNew)
            {
                // Another instance owns this workspace. If this launch carried a
                // file to open, stage it as a one-time ticket FIRST so the
                // running window can pick it up, THEN wake that window and exit.
                // The restore signal stays payload-free; the path crosses only
                // as a locally-staged ticket, never through the event.
                string secondaryHandoffState = "no-request";
                if (fileOpenRequest is FileOpenRequest secondaryRequest)
                {
                    if (!secondaryRequest.Exists)
                    {
                        secondaryHandoffState = "missing-file-secondary";
                    }
                    else
                    {
                        string? stagedId = ImportHandoffQueue.Enqueue(importHandoffDir, secondaryRequest);
                        secondaryHandoffState = stagedId is null
                            ? "queue-failed-secondary"
                            : "queued-ticket-secondary";
                    }
                }

                // Another instance owns this workspace. Wake it and exit.
                try
                {
                    if (EventWaitHandle.TryOpenExisting(restoreName, out EventWaitHandle? existing) &&
                        existing is not null)
                    {
                        existing.Set();
                        existing.Dispose();
                    }
                }
                catch
                {
                    // Best-effort: even if the signal fails the second instance
                    // must still exit without starting a server.
                }

                singleInstanceMutex.Dispose();
                singleInstanceMutex = null;
                WriteFileOpenMarkers(fileOpenRequest, secondaryHandoffState);
                Console.WriteLine("X16C_SINGLE_INSTANCE=secondary-exit");
                return 0;
            }

            restoreSignal = new EventWaitHandle(false, EventResetMode.AutoReset, restoreName);
        }

        // Resolve the install-tree app/ assets that the native runtime serves.
        // The PowerShell broker remains the immutable parity oracle; this app
        // only READS app\web\ (SPA shell) and app\VERSION.json (read-only
        // version metadata). It never reads, copies, or invokes the PAX engine.
        string? appRoot = ResolveAppRoot(args);
        if (appRoot is null)
        {
            Console.Error.WriteLine(
                "FATAL: could not locate the install-tree app/ root (expected app\\web\\index.html). " +
                "Pass --approot <path-to-app> or run from within the repository.");
            return 4;
        }
        string webRoot = Path.GetFullPath(Path.Combine(appRoot, "web"));
        string versionFile = Path.Combine(appRoot, "VERSION.json");
        string iconPath = Path.Combine(webRoot, "images", "pax-cookbook-app-icon.ico");
        VersionInfo versionInfo = LoadVersionInfo(versionFile);

        // Two-process broker coordination (V2). Before this process selects a
        // port or builds the in-process Kestrel broker, see whether a background
        // broker daemon is already serving (broker.port + identity-verified
        // health probe over loopback).
        //   * Window mode: attach a window to the running broker instead of
        //     starting a second one (a second broker would clobber the shared
        //     <workspace>\Runtime\broker.token the daemon owns). Open the WebView2
        //     against the daemon's URL with NO window-owned tray, then exit
        //     WITHOUT stopping the daemon — it keeps serving in the background.
        //   * Headless daemon mode (--headless): a second daemon is redundant, so
        //     exit cleanly and leave the running broker as the single owner.
        // The --no-window smoke harness is deliberately exempt: it runs several
        // isolated brokers and must never consult or write the shared port file.
        //
        // This UI/attach path NEVER runs a bake: it only opens (or attaches) a
        // WebView2 window, and a cook starts only when the user clicks the gated
        // Bake control inside that window. A CLI bake is triggered EXCLUSIVELY by
        // --bake <recipeId> (handled by the early return above, before this
        // block), so a normal launch — with or without a running daemon — can
        // never auto-bake.
        if (!noWindow && !iconDiagnosticsSeam)
        {
            int? attachPort = BrokerDetection.TryGetRunningBrokerPort();
            if (attachPort is int daemonPort)
            {
                // Stage any double-clicked file as a one-time ticket the running
                // broker will pick up, then open an attached window against it.
                string attachHandoffState = "no-request";
                if (fileOpenRequest is FileOpenRequest attachRequest)
                {
                    if (!attachRequest.Exists)
                    {
                        attachHandoffState = "missing-file";
                    }
                    else
                    {
                        string? attachStagedId = ImportHandoffQueue.Enqueue(importHandoffDir, attachRequest);
                        attachHandoffState = attachStagedId is null ? "queue-failed" : "queued-ticket";
                    }
                }
                WriteFileOpenMarkers(fileOpenRequest, attachHandoffState);

                string attachUrl = $"http://localhost:{daemonPort}/";
                string attachUserData = Path.Combine(workspacePath, "WebView2");
                Console.WriteLine("X2V_TWO_PROCESS=on");
                Console.WriteLine("X2V_ROLE=attach-window");
                Console.WriteLine($"X2V_ATTACH_PORT={daemonPort}");
                Console.WriteLine($"X2_APP_URL={attachUrl}");
                int attachExit = 0;
                try
                {
                    WebViewShell.Run(attachUrl, AppName, iconPath, attachUserData, selfCloseAfterMs, restoreSignal, testSeamAumid, importHandoffDir, showTray: false);
                }
                catch (WebView2RuntimeMissingException ex)
                {
                    Console.Error.WriteLine(
                        "FATAL: the Microsoft Edge WebView2 Runtime is required to run PAX Cookbook but was not found. " +
                        "Install the Evergreen WebView2 Runtime from https://developer.microsoft.com/microsoft-edge/webview2/ and relaunch. " +
                        $"Detail: {ex.Message}");
                    attachExit = 3;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"FATAL: application window terminated: {ex.Message}");
                    attachExit = 1;
                }

                // The attached window has closed. This process started no broker,
                // so it does NOT stop the daemon: the background broker keeps
                // serving. Release the single-instance reservation and exit.
                try
                {
                    singleInstanceMutex?.ReleaseMutex();
                    singleInstanceMutex?.Dispose();
                    restoreSignal?.Dispose();
                }
                catch
                {
                    // Best-effort; the OS releases the named objects on exit.
                }
                Environment.Exit(attachExit);
                return attachExit;
            }
        }
        else if (headlessDaemon)
        {
            int? existingPort = BrokerDetection.TryGetRunningBrokerPort();
            if (existingPort is int)
            {
                // A broker is already serving; a second daemon would duplicate
                // it. Exit cleanly and leave the running broker as the single
                // owner of the workspace token and the port file.
                Console.WriteLine("X2V_TWO_PROCESS=on");
                Console.WriteLine("X2V_ROLE=daemon-redundant-exit");
                return 0;
            }
        }

        // Engine acquisition anchor. The managed PAX engine and its
        // install-state metadata live under %LOCALAPPDATA%\PAXCookbook. A
        // narrowly-scoped test-only override (--engine-localappdata <path>)
        // lets the smoke harness point the acquisition-state reader at an
        // isolated fixture so it never reads or writes the operator's real
        // profile. Acquisition state is read-only: nothing under this anchor is
        // created, repaired, copied, downloaded, mutated, or invoked.
        string? engineLocalAppDataOverride = ResolveEngineLocalAppDataOverride(args);
        string engineLocalAppDataBase = EngineAcquisition.ResolveLocalAppDataBase(engineLocalAppDataOverride);

        // First-launch engine auto-acquisition. When the per-user managed engine
        // has not been acquired yet, silently activate the approved engine that
        // ships in the install tree so a fresh install is ready to cook with no
        // acquisition prompt. Skipped under the test-only --engine-localappdata
        // override so the acquisition-flow smokes still exercise the pending
        // state against their isolated fixtures. appRoot is non-null here (an
        // earlier guard returns when it cannot be located).
        if (engineLocalAppDataOverride is null)
        {
            EngineBundleAutoAcquire.TryAcquireFromBundle(versionInfo, appRoot, engineLocalAppDataBase);
        }

        // X14 acquisition staging anchor. The oracle (installer) puts work
        // dirs under <install-root>\Updates; the native runtime defaults to
        // <engineLocalAppDataBase>\PAXCookbook\Updates. A --updates-dir
        // <path> override lets the smoke harness isolate work dirs from the
        // real profile.
        EngineAcquisitionRoutesX14.SetUpdatesDirOverride(ResolveUpdatesDirOverride(args));

        int port = SelectLoopbackPort();
        if (port == 0)
        {
            Console.Error.WriteLine(
                $"FATAL: no free loopback port in range {PortRangeStart}-{PortRangeEnd}. Broker refuses to start.");
            return 2;
        }

        // 256-bit per-launch session token, base64url unpadded, written to the
        // workspace Runtime sidecar (oracle path: <workspace>\Runtime\broker.token).
        string sessionToken = GenerateSessionToken();
        File.WriteAllText(tokenFile, sessionToken, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Browser-owned WebAuthn / broker-lock foundation (X3). The lock state
        // machine (BrokerLock) is process-static; the WebAuthn service owns the
        // challenge store and the credential store at
        // <workspace>\Auth\webauthn-credentials.json. The broker always boots
        // Locked (BrokerLock default state).
        var webAuthn = new WebAuthnService(workspacePath, port);

        // Bundled template catalog (X4). Loaded ONCE at startup from the
        // read-only install tree (app\templates\*.template.json). No per-request
        // rescan, no remote catalog, no mutation.
        TemplateReadModel templateModel = TemplateReadModel.Load(appRoot);

        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Bind 127.0.0.1 only. Never all interfaces, never ephemeral.
            options.Listen(IPAddress.Loopback, port);
        });

        WebApplication app = builder.Build();

        // Session-token middleware. Only /api/v1/* (except health) is gated.
        // The SPA static surface is unauthenticated by design — it must load
        // before the token is captured into sessionStorage (oracle parity:
        // app\broker\Http\StaticHandler.ps1).
        app.Use(async (context, next) =>
        {
            string path = context.Request.Path.Value ?? string.Empty;

            // /api/v1/health is unauthenticated and lock-bypass by design (safe GET).
            if (IsHealthPath(path))
            {
                await next();
                return;
            }

            // Every other /api/v1/* route requires a valid Bearer session token.
            if (path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasValidBearer(context.Request, sessionToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"error\":\"invalid_token\"}");
                    return;
                }
            }

            await next();
        });

        // CSRF + broker-lock gate (X3). Runs AFTER the Bearer middleware (we
        // never leak lock-state to unauthenticated callers) and BEFORE static
        // serving + the route table. Parity with the oracle dispatch order
        // (app\broker\Start-Broker.ps1 Invoke-RequestHandler):
        //   4. CSRF: state-changing verbs require X-Cookbook-Request: 1.
        //   5. Lock-bypass allow-list routes (lock-state / lock / unlock /
        //      webauthn/*) proceed even while Locked — that is how the operator
        //      unlocks.
        //   7. Any other /api/v1/* route while Locked returns 423 brokerLocked.
        // The 423 status (NOT 409) is intentional and load-bearing: the SPA's
        // lock overlay (app\web\assets\api.js) renders ONLY on a 423 with
        // body.code === 'brokerLocked'. 409 is reserved for acquisitionRequired,
        // a different feature not ported in this slice.
        app.Use(async (context, next) =>
        {
            string path = context.Request.Path.Value ?? string.Empty;

            if (!path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase) || IsHealthPath(path))
            {
                await next();
                return;
            }

            string method = context.Request.Method;

            // State-changing verbs require the CSRF marker header. The SPA sets
            // X-Cookbook-Request: 1 on every stateful fetch (api.js).
            if (HttpMethods.IsPost(method) || HttpMethods.IsPut(method) ||
                HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method))
            {
                string csrf = context.Request.Headers["X-Cookbook-Request"].ToString();
                if (csrf != "1")
                {
                    await WriteJsonAsync(context, StatusCodes.Status403Forbidden,
                        new { error = "csrf_required" });
                    return;
                }
            }

            // Lock-bypass allow-list routes proceed regardless of lock state and
            // do NOT bump the inactivity anchor (otherwise SPA lock-state polling
            // would keep the broker unlocked indefinitely).
            if (BrokerLock.IsRouteAllowedWhenLocked(method, path))
            {
                await next();
                return;
            }

            if (BrokerLock.GetState() == "Locked")
            {
                await WriteJsonAsync(context, 423, new
                {
                    code = "brokerLocked",
                    message = "The appliance is locked. Verify with Windows Hello / PIN to unlock.",
                    attemptedMethod = method,
                    attemptedPath = path,
                });
                return;
            }

            // Unlocked + an authenticated, non-bypass route. Run the route
            // first, then refresh the inactivity anchor ONLY when the route
            // succeeded (status < 400). Failed or erroring reads do not keep the
            // session alive, and the lock-bypass routes handled above never bump
            // — so lock-state polling, a rejected bad-token request, or a 423/
            // 5xx cannot hold the broker unlocked indefinitely. Idle sessions
            // still re-lock after the BrokerLock inactivity timeout.
            await next();
            if (context.Response.StatusCode < 400)
            {
                BrokerLock.BumpActivity();
            }
        });

        // Static SPA serving for every non-/api path. Mirrors the oracle's
        // StaticHandler.ps1: GET-only, "/" -> index.html, canonicalized-
        // descendant traversal guard (403), unknown extension (404), missing
        // file (404), index.html token bootstrap injection, Cache-Control:
        // no-store on every response.
        app.Use(async (context, next) =>
        {
            string path = context.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            await ServeStaticAsync(context, webRoot, sessionToken);
        });

        app.MapGet("/api/v1/health",
            () => Results.Json(BuildHealthPayload(port, workspacePath), statusCode: StatusCodes.Status200OK));

        // Protected placeholder retained from X1 so the token middleware can be
        // exercised in isolation.
        app.MapGet("/api/v1/_x1/ping",
            () => Results.Json(new { ok = true }, statusCode: StatusCodes.Status200OK));

        // Read-only route parity (X2). These return authoritative read state
        // only. None of them mutate broker state, touch SQLite, acquire the PAX
        // engine, or invoke the PAX script.
        app.MapGet("/api/v1/runtime/version",
            () => Results.Json(BuildRuntimeVersionPayload(versionInfo, port), statusCode: StatusCodes.Status200OK));

        // Engine acquisition state (X14). Reports manifest-driven acquisition
        // state by reading install-state.json metadata and re-hashing the
        // managed engine file when present. Read-only: it never acquires,
        // downloads, copies, repairs, or invokes the engine.
        app.MapGet("/api/v1/setup/acquire-pax/state",
            () => Results.Json(
                EngineAcquisitionRoutesX14.BuildStatePayload(versionInfo, engineLocalAppDataBase),
                statusCode: StatusCodes.Status200OK));

        // X14 acquisition actions. Each route validates VERSION.json policy,
        // fetches and verifies the approved-engine manifest, stages bytes
        // through a per-attempt work directory, hashes the bytes, atomically
        // writes the canonical PAX script, and merges the paxAcquisition
        // block in install-state.json. All failures funnel through
        // InstallStateWriter.WriteFailure so the lastAttemptError block stays
        // in sync with the HTTP response.
        app.MapPost("/api/v1/setup/acquire-pax/download",
            (Func<HttpContext, Task<IResult>>)((ctx) =>
                EngineAcquisitionRoutesX14.HandleDownloadAsync(ctx, versionInfo, engineLocalAppDataBase)));
        app.MapPost("/api/v1/setup/acquire-pax/upload",
            (Func<HttpContext, Task<IResult>>)((ctx) =>
                EngineAcquisitionRoutesX14.HandleUploadAsync(ctx, versionInfo, engineLocalAppDataBase)));
        app.MapPost("/api/v1/setup/acquire-pax/upload-bytes",
            (Func<HttpContext, Task<IResult>>)((ctx) =>
                EngineAcquisitionRoutesX14.HandleUploadBytesAsync(ctx, versionInfo, engineLocalAppDataBase)));
        app.MapPost("/api/v1/setup/acquire-pax/cancel",
            () => EngineAcquisitionRoutesX14.HandleCancel(versionInfo, engineLocalAppDataBase));

        // Read-only recipe routes (X4). The list is the SQLite index projection;
        // the detail merges the SQLite row with the authoritative recipe file.
        // Both open the workspace read-only and never mutate state, never create
        // the database, never repair a file, and never touch the PAX engine. A
        // workspace with no database yields a real empty list.
        app.MapGet("/api/v1/recipes",
            () => Results.Json(RecipeReadModel.ListActive(workspacePath), statusCode: StatusCodes.Status200OK));

        app.MapGet("/api/v1/recipes/{id}", (string id) =>
        {
            if (!RecipeReadModel.IsValidRecipeId(id))
            {
                return Results.Json(new { error = "invalid_recipe_id", recipeId = id },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            (int status, object body) = RecipeReadModel.GetDetail(workspacePath, id);
            return Results.Json(body, statusCode: status);
        });

        // Read-only template routes (X4). The catalog is loaded once at startup
        // from the bundled, read-only app\templates files.
        app.MapGet("/api/v1/templates",
            () => Results.Json(templateModel.BuildListPayload(), statusCode: StatusCodes.Status200OK));

        app.MapGet("/api/v1/templates/{id}", (string id) =>
        {
            if (!TemplateReadModel.IsValidTemplateId(id))
            {
                return Results.Json(new { error = "invalid_template_id" },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            (int status, object body) = templateModel.GetDetail(id);
            return Results.Json(body, statusCode: status);
        });

        // Pantry repository viewer. Read-only GET that calls GitHub's public
        // REST API server-side for the repo's rendered README HTML + metadata
        // (parameterized by owner/repo only — never an arbitrary URL) and
        // returns one combined JSON document the SPA renders natively. Bearer +
        // lock gated upstream like every other /api/v1 route (no exemption).
        // owner/repo are charset-validated with no ".." traversal in
        // PantryProxy.IsValidSegment; each outbound call is a single GET, no
        // redirects, no cookies, byte-capped, time-bounded, to api.github.com
        // only. It never runs PAX, mutates state, or reads a secret.
        app.MapGet("/api/v1/pantry/repo",
            (Func<HttpContext, Task<IResult>>)((ctx) =>
                PantryProxy.HandleAsync(ctx, versionInfo)));

        // Pantry repository contents (file explorer). Read-only GET that returns
        // a single directory listing from GitHub's Contents API so the SPA can
        // render a lazily-expanded file tree. Same posture as the repo route:
        // Bearer + lock gated, owner/repo + the optional path validated (no
        // "..", no leading "/", no breakout/shell/control characters) and then
        // percent-encoded per segment before interpolation into a fixed
        // api.github.com/repos/{o}/{r}/contents/{path} URL; single GET, no
        // redirects/cookies/credentials, byte-capped. Each file's download URL
        // is validated to an https github.com / githubusercontent host so the
        // SPA only renders a trusted GitHub link.
        app.MapGet("/api/v1/pantry/repo-contents",
            (Func<HttpContext, Task<IResult>>)((ctx) =>
                PantryProxy.HandleContentsAsync(ctx, versionInfo)));

        // Pantry file download proxy. Read-only GET that fetches one file from
        // GitHub and streams it back so the SPA can preview or save it in-app
        // (the README, file tree, and file previews never leave the appliance
        // window). Same Bearer + lock-gated posture as the other Pantry routes;
        // the ?url= value is validated server-side to a trusted GitHub https
        // host (PantryProxy IsSafeDownloadUrl) before any outbound request, so
        // it can never become an open relay. The body is streamed straight
        // through (never buffered, no byte cap, 5-minute ceiling); the caller's
        // bearer token is never forwarded upstream; redirects are not followed;
        // no cookies/credentials are sent. It never runs PAX, mutates state, or
        // reads a secret.
        app.MapGet("/api/v1/pantry/download",
            (Func<HttpContext, Task<IResult>>)((ctx) =>
                PantryProxy.HandleDownloadAsync(ctx, versionInfo)));

        // Mutable recipe/template routes that remain unported return a bounded
        // 501 via the /api/* MapFallback (never a fabricated success), so the
        // SPA surfaces an honest "not implemented" rather than a fake 2xx.
        // Bearer + CSRF + the lock gate are all enforced upstream before any of
        // them run.

        // Recipe create (X7) — the first native write route. Validates with the
        // X6 validator, assigns the server-managed fields, writes exactly one
        // recipe file, and inserts exactly one recipe row. It never invokes PAX,
        // never reads or mutates the PAX bytes, and never touches cook /
        // scheduler / notification state. Matching the live oracle, this route
        // is NOT re-auth gated (only the auth-profile / updates / cooks routes
        // call Invoke-BrokerLockReAuthForOp); the bearer token, CSRF header, and
        // broker lock are all enforced upstream before this runs.
        app.MapPost("/api/v1/recipes", async (HttpContext context) =>
        {
            object? createBody = await JsonModel.ReadBodyAsync(context);
            (int status, object responseBody) =
                RecipeCreateModel.Handle(workspacePath, versionInfo, createBody);
            return Results.Json(responseBody, statusCode: status);
        });

        // Read-only / non-persisting recipe validation + PAX preview (X6).
        // Validates a stored-recipe lookup OR an in-memory draft and projects
        // the authoritative PAX invocation plan. This route never inserts a
        // recipe row, writes a recipe file, creates a cook, or invokes PAX; the
        // only fills it performs live in the request's in-memory value tree. The
        // bundled PAX script path is a pure string the projection renders into
        // the preview command — the file itself is never read or executed.
        string paxScriptPath = Path.Combine(appRoot, "resources", "pax", "PAX_Purview_Audit_Log_Processor.ps1");
        app.MapPost("/api/v1/recipes/preview", async (HttpContext context) =>
        {
            object? previewBody = await JsonModel.ReadBodyAsync(context);
            (int status, object responseBody) =
                RecipePreviewModel.Handle(workspacePath, paxScriptPath, versionInfo, previewBody);
            return Results.Json(responseBody, statusCode: status);
        });

        // Read-only / non-persisting recipe readiness (UX6). Runs the same
        // validate + projection pipeline as the preview route and layers
        // authoritative requirement state on top (PAX engine acquisition,
        // sign-in / Chef's Key, output destination) so the Mini-Kitchen builder
        // can show whether a recipe could run and what is still missing. This
        // route never invokes PAX, never spawns a process, never creates a cook
        // or bake, never writes a recipe row or file, never reads or mutates the
        // PAX bytes, never contacts a tenant or Microsoft Graph, and never reads
        // a secret. Bearer token, CSRF header, and the broker lock gate are all
        // enforced upstream before this runs.
        app.MapPost("/api/v1/recipes/readiness", async (HttpContext context) =>
        {
            object? readinessBody = await JsonModel.ReadBodyAsync(context);
            (int status, object responseBody) =
                RecipeReadinessModel.Handle(
                    workspacePath, paxScriptPath, versionInfo, engineLocalAppDataBase, readinessBody);
            return Results.Json(responseBody, statusCode: status);
        });

        // Recipe update (X8) — replaces the body of an existing, non-deleted
        // recipe. Re-validates with the X6 validator, preserves the server-owned
        // provenance leaves (createdAt from the index row; createdBy /
        // importMetadata from the on-disk document), writes the recipe file in
        // place, and updates exactly one recipe row. Matching the live oracle,
        // this route is NOT re-auth gated (Invoke-RecipesRoute dispatches PUT
        // straight to Invoke-RecipeUpdate with no Invoke-BrokerLockReAuthForOp);
        // the bearer token, CSRF header, and broker lock are all enforced
        // upstream before this runs.
        app.MapPut("/api/v1/recipes/{id}", async (string id, HttpContext context) =>
        {
            if (!RecipeReadModel.IsValidRecipeId(id))
            {
                return Results.Json(new { error = "invalid_recipe_id", recipeId = id },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            object? updateBody = await JsonModel.ReadBodyAsync(context);
            (int status, object responseBody) =
                RecipeUpdateModel.Handle(workspacePath, versionInfo, id, updateBody);
            return Results.Json(responseBody, statusCode: status);
        });

        // Recipe delete (X8) — soft delete. Moves the recipe file into
        // Recipes\_trash with a timestamped name, stamps the index row's
        // deleted_at, and retains the row. Matching the live oracle, this route
        // is NOT re-auth gated (Invoke-RecipesRoute dispatches DELETE straight to
        // Invoke-RecipeDelete with no Invoke-BrokerLockReAuthForOp); the bearer
        // token, CSRF header, and broker lock are all enforced upstream.
        app.MapDelete("/api/v1/recipes/{id}", (string id) =>
        {
            if (!RecipeReadModel.IsValidRecipeId(id))
            {
                return Results.Json(new { error = "invalid_recipe_id", recipeId = id },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            (int status, object responseBody) =
                RecipeDeleteModel.Handle(workspacePath, id);
            return Results.Json(responseBody, statusCode: status);
        });

        app.MapPost("/api/v1/templates/{id}/materialize", async (string id, HttpContext context) =>
        {
            if (!TemplateReadModel.IsValidTemplateId(id))
            {
                return Results.Json(new { error = "invalid_template_id" },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            object? materializeBody = await JsonModel.ReadBodyAsync(context);
            (int status, object responseBody) =
                TemplateMaterializeModel.Handle(templateModel, workspacePath, versionInfo, id, materializeBody);
            return Results.Json(responseBody, statusCode: status);
        });

        // File-open import handoff. When the Windows shell hands the EXE a
        // double-clicked .paxlite / .pax file, the path is staged on disk as a
        // one-time, expiring ticket (see ImportHandoffQueue). These two routes
        // let the React app discover and consume a pending ticket. They are
        // authenticated, CSRF-protected, and lock-gated by the SAME upstream
        // middleware as every other /api/v1/* route — so they are unreachable
        // until the user has unlocked through the normal Windows Hello / lock
        // ceremony on the legacy shell. The absolute file path is NEVER
        // projected: pending lists carry only id / kind / fileName, and consume
        // returns the .paxlite TEXT (for the in-browser importer) or, for .pax,
        // an unsupported marker with no content. Neither route invokes PAX,
        // reads or mutates the PAX bytes, fabricates a success, auto-bakes, or
        // touches cook / scheduler state.
        app.MapGet("/api/v1/import-handoff/pending",
            () => Results.Json(new { pending = ImportHandoffQueue.ListPending(importHandoffDir) },
                statusCode: StatusCodes.Status200OK));

        app.MapPost("/api/v1/import-handoff/consume", async (HttpContext context) =>
        {
            object? consumeBody = await JsonModel.ReadBodyAsync(context);
            (int status, object responseBody) =
                ImportHandoffQueue.HandleConsume(importHandoffDir, consumeBody);
            return Results.Json(responseBody, statusCode: status);
        });

        // Native file/folder picker for SPA path inputs (Item 7). Shows a Windows
        // OpenFileDialog / FolderBrowserDialog on a dedicated STA thread and returns
        // the chosen path. UI helper only: Bearer, CSRF, and the broker lock (423
        // when Locked) are enforced upstream by the middleware; it is intentionally
        // NOT on the lock allow-list. No Windows Hello step-up, no secret, no tenant
        // data, no PAX invocation — just a path string.
        app.MapPost("/api/v1/browse-path", async (HttpContext context) =>
        {
            object? browseBody = await JsonModel.ReadBodyAsync(context);
            (int status, object responseBody) = BrowsePathModel.Handle(browseBody);
            return Results.Json(responseBody, statusCode: status);
        });

        // Open an output file's containing folder in File Explorer (Item 3D/3E
        // "Open folder"). It opens a FOLDER only — never executes a file — and
        // the target must already exist on disk. Same gating as browse-path:
        // Bearer, CSRF, and the broker lock (423 when Locked) are enforced
        // upstream; not on the lock allow-list; no Windows Hello step-up, no
        // secret, no tenant data, no PAX invocation. See OpenPathModel.
        app.MapPost("/api/v1/open-path", async (HttpContext context) =>
        {
            object? openBody = await JsonModel.ReadBodyAsync(context);
            (int status, object responseBody) = OpenPathModel.Handle(openBody);
            return Results.Json(responseBody, statusCode: status);
        });

        // Open an output or log FILE in the user's default app (the "Open log"
        // buttons on Last Bake + the Bakes detail). It opens only inert document
        // types from a closed allowlist (.log/.txt/.csv/.json/.xml/.html/.pdf) —
        // never an executable / script — via the shell's registered default
        // handler (UseShellExecute=true), and the file must already exist. Same
        // gating as open-path: Bearer, CSRF, and the broker lock (423 when
        // Locked) are enforced upstream; not on the lock allow-list; no Windows
        // Hello step-up, no secret, no tenant data, no PAX invocation. See
        // OpenFileModel.
        app.MapPost("/api/v1/open-file", async (HttpContext context) =>
        {
            object? openFileBody = await JsonModel.ReadBodyAsync(context);
            (int status, object responseBody) = OpenFileModel.Handle(openFileBody);
            return Results.Json(responseBody, statusCode: status);
        });

        // Graceful broker shutdown (V2 "Exit"). The SPA's close dialog "Exit"
        // posts here BEFORE telling the native shell to close the window, so
        // exiting stops EVERYTHING — including a separate background broker
        // daemon that an attached window does not own (an attached window's
        // process exit would otherwise leave the daemon serving). The 200 is
        // returned immediately and the actual stop is scheduled on a background
        // task so the caller reliably receives the response before the host
        // goes away.
        //   * Daemon (--headless): TrayIconHost.RequestExit() ends the tray
        //     message loop the SAME way the tray's Exit item does — it stops the
        //     in-process broker (ApplicationStopping clears the session token +
        //     releases the port file) and lets the daemon process exit.
        //   * Combined window / smoke host (no tray loop): RequestExit() returns
        //     false, so fall back to StopApplication(); the window close
        //     (cookbook:close-app) still tears the window down on its own.
        // Same gating as every other state-changing route: Bearer + CSRF + the
        // broker lock (423 when Locked) are enforced upstream by the middleware;
        // it is deliberately NOT on BrokerLock.AllowedWhenLocked, so a locked
        // session cannot stop the broker remotely. It reads no secret, runs no
        // PAX, starts no cook, and touches no engine bytes / install-state.
        app.MapPost("/api/v1/shutdown", () =>
        {
            _ = Task.Run(async () =>
            {
                // Let the 200 flush to the caller before the host stops.
                try { await Task.Delay(200).ConfigureAwait(false); }
                catch { /* ignore */ }
                try
                {
                    if (!TrayIconHost.RequestExit())
                    {
                        app.Lifetime.StopApplication();
                    }
                }
                catch
                {
                    try { app.Lifetime.StopApplication(); }
                    catch { /* Best-effort: nothing more can be done here. */ }
                }
            });
            return Results.Json(new { ok = true }, statusCode: 200);
        });

        // Recipe pin / unpin (X11) — a deliberate native v1 product feature, NOT
        // an oracle-parity port (the live PowerShell oracle has no pin/unpin
        // route; this is a sanctioned divergence built on the dormant
        // recipes.is_pinned column). Each route is a row-only mutation: it sets
        // is_pinned and bumps updated_at on a real state transition, performs no
        // write on an idempotent no-op, and never rewrites the recipe file,
        // changes its hash, invokes PAX, or touches cook / scheduler /
        // notification state. Like the recipe CRUD / materialize routes these
        // are NOT re-auth gated; the bearer token, CSRF header, and broker lock
        // are all enforced upstream before they run.
        app.MapPost("/api/v1/recipes/{id}/pin", (string id) =>
        {
            if (!RecipeReadModel.IsValidRecipeId(id))
            {
                return Results.Json(new { error = "invalid_recipe_id", recipeId = id },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            (int status, object responseBody) =
                RecipePinModel.Handle(workspacePath, id, pinned: true);
            return Results.Json(responseBody, statusCode: status);
        });

        app.MapPost("/api/v1/recipes/{id}/unpin", (string id) =>
        {
            if (!RecipeReadModel.IsValidRecipeId(id))
            {
                return Results.Json(new { error = "invalid_recipe_id", recipeId = id },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            (int status, object responseBody) =
                RecipePinModel.Handle(workspacePath, id, pinned: false);
            return Results.Json(responseBody, statusCode: status);
        });

        // Chef's Keys (CK-1) — Windows Credential Manager-backed credential CRUD
        // for the four sign-in types (WebLogin, DeviceCode, AppReg-Certificate,
        // AppReg-Secret). PAX Cookbook never stores credential material itself:
        // every create / read / update / delete is funnelled through
        // WindowsCredentialStore (per-user WCM vault, target
        // PAXCookbook:ChefKey:<id>). These routes never invoke PAX, never spawn a
        // process, never create a cook / bake, never read or mutate the PAX
        // bytes, and never contact Microsoft Graph. The list / detail DTO carries
        // metadata + a hasSecret flag ONLY — the client secret is write-only and
        // is never returned (constraint 14). The /test route is LOCAL/structural
        // only (no PAX, no interactive sign-in, no Graph call). The bearer token,
        // CSRF header, and broker lock are all enforced upstream before any of
        // these run, exactly like the recipe CRUD routes.
        app.MapGet("/api/v1/chef-keys", () =>
        {
            (int status, object body) = ChefKeyModel.List();
            return Results.Json(body, statusCode: status);
        });

        app.MapGet("/api/v1/chef-keys/{id}", (string id) =>
        {
            (int status, object body) = ChefKeyModel.Get(id);
            return Results.Json(body, statusCode: status);
        });

        app.MapPost("/api/v1/chef-keys", async (HttpContext context) =>
        {
            object? createBody = await JsonModel.ReadBodyAsync(context);
            (int status, object body) = ChefKeyModel.Create(createBody);
            return Results.Json(body, statusCode: status);
        });

        app.MapPut("/api/v1/chef-keys/{id}", async (string id, HttpContext context) =>
        {
            object? updateBody = await JsonModel.ReadBodyAsync(context);
            (int status, object body) = ChefKeyModel.Update(id, updateBody);
            return Results.Json(body, statusCode: status);
        });

        app.MapDelete("/api/v1/chef-keys/{id}", (string id) =>
        {
            (int status, object body) = ChefKeyModel.Delete(id);
            return Results.Json(body, statusCode: status);
        });

        app.MapPost("/api/v1/chef-keys/{id}/test", (string id) =>
        {
            (int status, object body) = ChefKeyModel.Test(id);
            return Results.Json(body, statusCode: status);
        });

        // --- CK-4 Settings → Notifications (Telegram) -------------------------
        // The product notification feature: opt-in bake completion / failure
        // summaries and a real-time Device Code sign-in relay to the user's own
        // Telegram bot. The bot token + chat id live in the per-user Windows
        // Credential Manager vault (target PAXCookbook:Settings:Telegram); there
        // is no embedded bot token and no external bot is referenced.
        //
        // These routes sit behind the same Bearer + CSRF + broker-lock gate as
        // the Chef's Keys and recipe routes (enforced upstream). Constraint 14:
        // the bot token is write-only — accepted on PUT, read server-side by the
        // test / resolve paths, and NEVER returned by any route. GET / PUT carry
        // only { enabled, chatId, chatIdSet, tokenSet, provider }.
        app.MapGet("/api/v1/settings/notifications", () =>
        {
            (int status, object body) = NotificationSettingsModel.Get();
            return Results.Json(body, statusCode: status);
        });

        app.MapPut("/api/v1/settings/notifications", async (HttpContext context) =>
        {
            object? putBody = await JsonModel.ReadBodyAsync(context);
            (int status, object body) = NotificationSettingsModel.Put(putBody);
            return Results.Json(body, statusCode: status);
        });

        app.MapPost("/api/v1/settings/notifications/test", () =>
        {
            (int status, object body) = NotificationSettingsModel.Test();
            return Results.Json(body, statusCode: status);
        });

        app.MapPost("/api/v1/settings/notifications/resolve-chat-id", () =>
        {
            (int status, object body) = NotificationSettingsModel.ResolveChatId();
            return Results.Json(body, statusCode: status);
        });

        // Settings → Startup (V2 two-process auto-start toggle). Same Bearer +
        // CSRF + broker-lock gate as the other settings routes (enforced
        // upstream). GET reads the per-user HKCU Run value; POST writes/removes
        // it. The written command uses THIS process's exe path + the broker's
        // resolved workspace/approot, identical to the installer's launch line,
        // so the toggle, installer, and uninstaller all act on the same value.
        app.MapGet("/api/v1/settings/autostart", () =>
        {
            (int status, object body) = AutoStartSettingsModel.Get(workspacePath, appRoot);
            return Results.Json(body, statusCode: status);
        });

        app.MapPost("/api/v1/settings/autostart", async (HttpContext context) =>
        {
            object? autostartBody = await JsonModel.ReadBodyAsync(context);
            (int status, object body) = AutoStartSettingsModel.Set(autostartBody, workspacePath, appRoot);
            return Results.Json(body, statusCode: status);
        });

        // --- X3 auth / lock / WebAuthn foundation -----------------------------
        // All of the routes below are reachable while the broker is Locked
        // (lock-bypass allow-list); that is how the operator unlocks. Bearer +
        // CSRF are still enforced upstream for the POST routes.

        // GET /api/v1/broker/lock-state — fresh snapshot (triggers the lazy
        // inactivity sweep; does NOT bump the activity anchor).
        app.MapGet("/api/v1/broker/lock-state",
            () => Results.Json(BrokerLock.GetSnapshot(), statusCode: StatusCodes.Status200OK));

        // POST /api/v1/broker/lock — idempotent lock.
        app.MapPost("/api/v1/broker/lock", () =>
        {
            BrokerLock.SetLocked();
            return Results.Json(BrokerLock.GetSnapshot(), statusCode: StatusCodes.Status200OK);
        });

        // POST /api/v1/broker/unlock — legacy broker-owned Windows Hello (WinRT
        // UserConsentVerifier). Intentionally NOT implemented in the native
        // runtime: hosting a WinRT consent dialog from inside the WebView2
        // window is exactly the ownership hazard X3 avoids. This path returns an
        // honest 501 and never fabricates a verified verdict. The supported
        // unlock path is the browser-owned WebAuthn ceremony below.
        app.MapPost("/api/v1/broker/unlock", () => Results.Json(new
        {
            error = "not_implemented_x3",
            message = "Broker-owned Windows Hello (WinRT) is not implemented in the native runtime. Use the browser-owned WebAuthn unlock ceremony at /api/v1/broker/webauthn/unlock.",
            verificationPath = "webauthn",
        }, statusCode: StatusCodes.Status501NotImplemented));

        app.MapGet("/api/v1/broker/webauthn/status", () =>
        {
            WebAuthnResponse r = webAuthn.GetStatus();
            return Results.Json(r.Body, statusCode: r.Status);
        });

        app.MapPost("/api/v1/broker/webauthn/unlock-challenge", () =>
        {
            WebAuthnResponse r = webAuthn.NewUnlockChallenge();
            return Results.Json(r.Body, statusCode: r.Status);
        });

        app.MapPost("/api/v1/broker/webauthn/bootstrap-register-challenge", () =>
        {
            WebAuthnResponse r = webAuthn.NewBootstrapRegisterChallenge();
            return Results.Json(r.Body, statusCode: r.Status);
        });

        app.MapPost("/api/v1/broker/webauthn/unlock", async (HttpContext context) =>
        {
            JsonElement? body = await ReadJsonBodyAsync(context);
            WebAuthnResponse r = webAuthn.Unlock(body);
            return Results.Json(r.Body, statusCode: r.Status);
        });

        app.MapPost("/api/v1/broker/webauthn/bootstrap-register-unlock", async (HttpContext context) =>
        {
            JsonElement? body = await ReadJsonBodyAsync(context);
            WebAuthnResponse r = webAuthn.BootstrapRegisterUnlock(body);
            return Results.Json(r.Body, statusCode: r.Status);
        });

        // Manual-cook re-auth step-up (X16B). A real browser-owned WebAuthn
        // ceremony that authorizes exactly one manual cook of a named recipe.
        // These routes are NOT lock-bypass: a step-up presupposes an already
        // unlocked broker session, so the Bearer token, CSRF marker, and broker
        // lock gates all apply upstream. The challenge route mints a single-use
        // purpose-tagged challenge; the verify route validates the ES256
        // assertion and, on success, records a single-use in-memory
        // authorization the cook route consumes at gate 10. No authorization is
        // ever returned to the client, persisted, or logged.
        app.MapPost("/api/v1/broker/reauth/manual-cook/challenge", () =>
        {
            WebAuthnResponse r = webAuthn.NewManualCookChallenge();
            return Results.Json(r.Body, statusCode: r.Status);
        });

        app.MapPost("/api/v1/broker/reauth/manual-cook/verify", async (HttpContext context) =>
        {
            JsonElement? body = await ReadJsonBodyAsync(context);
            WebAuthnResponse r = webAuthn.VerifyManualCook(body);
            return Results.Json(r.Body, statusCode: r.Status);
        });

        // Manual cook-start (X16). The public route runs the full native
        // cook-start pipeline AND launches the single sanctioned PAX engine
        // child: gates 1..9 (recipe / acquisition / busy), gate 10 (per-operation
        // manualCook re-auth — 401 reAuthRequired unless satisfied), a bounded
        // 501 for App-registration recipes (secret-at-spawn is out of scope),
        // then gates 11..18 (folder + files + row) and the supervised spawn
        // (201 on success, bounded 500 on spawn failure). The test-only
        // --test-seam-cook-prepare flag still drives the X15 pre-spawn
        // preparation and returns a bounded 200 cook_prepared_no_child WITHOUT
        // spawning. Bearer, CSRF, and broker-lock gates have all run upstream.
        IResult CookStartHandler(HttpContext ctx, string id)
        {
            EngineAcquisitionResult engine = EngineAcquisition.Resolve(versionInfo, engineLocalAppDataBase);
            string method = ctx.Request.Method;
            string path = ctx.Request.Path.Value ?? string.Empty;
            if (cookPrepareSeam)
            {
                (int prepStatus, object prepBody) = RecipeReadModel.PrepareCookStart(
                    workspacePath, versionInfo, engine, id, persist: true, method, path, cookMinFreeBytes);
                return Results.Json(prepBody, statusCode: prepStatus);
            }
            (int status, object body) = RecipeReadModel.StartManualCook(
                workspacePath, versionInfo, engine, id, method, path, cookMinFreeBytes,
                manualCookReAuthSeam, cookPwshPathOverride);
            return Results.Json(body, statusCode: status);
        }

        // Scheduled cook-start (V2 two-process). The daemon-side endpoint the
        // --bake CLI (Windows Task Scheduler → daemon delegation) calls. It runs
        // the SAME single cook pipeline (constraint 8) as the manual route with
        // ONE difference: CookKind.Scheduled WAIVES gate 10 (the per-operation
        // Windows Hello step-up) and REPLACES it with the scheduled-auth gate (the
        // recipe must have an ENABLED schedule, created earlier while the app was
        // unlocked and Hello-verified). EVERY other gate is identical to the manual
        // route — Bearer + CSRF + broker-lock (423 when Locked) all run upstream,
        // and recipe validation (QueryShapeGate / DateRangeGate), acquisition,
        // busy, disk, path, Chef's Key resolution, and the pre-spawn SHA re-verify
        // all still apply. This is the approved constraint-10 modification (X7)
        // extended to a loopback HTTP route (Brian-directed): the gate-10 waiver,
        // previously reachable ONLY via the --run-scheduled-recipe one-shot, is now
        // also reachable here.
        //
        // SECURITY — this is NOT a gate-10 bypass for manual cooks:
        //   * The React UI's Bake button still calls the MANUAL route (gate 10
        //     enforced). Nothing in the SPA calls this route.
        //   * Defense-in-depth: this route requires an explicit X-Cookbook-Scheduled
        //     marker header that the --bake CLI sends and the React brokerBridge
        //     never sends, so a stock SPA fetch is refused 403 before the pipeline.
        //   * The real boundary is the scheduled-auth gate: only a recipe the user
        //     ALREADY authorized for scheduling (enabled schedule) can run here; an
        //     unscheduled recipe is refused 409 recipe_not_scheduled. So this route
        //     can never run an arbitrary recipe without Windows Hello.
        IResult CookStartScheduledHandler(HttpContext ctx, string id)
        {
            // Defense-in-depth scheduled-only marker (see route comment). The
            // --bake CLI sends X-Cookbook-Scheduled: 1; the React brokerBridge does
            // not. Bearer + CSRF + lock have already run upstream.
            if (ctx.Request.Headers["X-Cookbook-Scheduled"].ToString() != "1")
            {
                return Results.Json(
                    new { error = "scheduled_marker_required", message = "This route is reachable only from the scheduled-bake launcher." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            EngineAcquisitionResult engine = EngineAcquisition.Resolve(versionInfo, engineLocalAppDataBase);
            string method = ctx.Request.Method;
            string path = ctx.Request.Path.Value ?? string.Empty;
            (int status, object body) = RecipeReadModel.StartScheduledCookViaHttp(
                workspacePath, versionInfo, engine, id, method, path, cookMinFreeBytes,
                cookPwshPathOverride);
            return Results.Json(body, statusCode: status);
        }
        // the Bakes surface (constraint 13). It does NOT require an acquired
        // engine — a running cook already had one, and cancellation is lifecycle
        // control, not execution (constraints 8/9: it spawns nothing and is not a
        // second way to run PAX). The read model validates the id, reads the
        // authoritative status, and only when the cook is running AND supervised
        // by THIS broker kills the live process tree through the in-process
        // cancellation registry; it never kills by a stored pid. Bearer, CSRF,
        // and broker-lock gates have all run upstream, so the effective refusal
        // order is 401 -> 403 -> 423 -> (404 cook_not_found / 409 cook_not_running
        // / 202 canceling / 409 cook_not_supervised). Both /stop and /kill map
        // here — there is no separate kill path. All bodies are metadata-only.
        IResult CookStopHandler(string id)
        {
            (int status, object body) = RecipeReadModel.RequestCookStop(workspacePath, id);
            return Results.Json(body, statusCode: status);
        }

        // Cook history read surface (X2). Read-only projections of the cooks
        // index + the managed cook-folder sentinels. Bearer auth is enforced by
        // the token middleware and the broker lock is enforced upstream (these
        // GETs are NOT on the lock-bypass allow-list, so they require an
        // unlocked session). They never spawn a child, never read or mutate the
        // PAX bytes, never read a secret, and only ever surface the redacted
        // command projection.
        app.MapGet("/api/v1/cooks",
            () => Results.Json(RecipeReadModel.ListCooks(workspacePath), statusCode: StatusCodes.Status200OK));

        app.MapGet("/api/v1/cooks/{cookId}", (string cookId) =>
        {
            (int status, object body) = RecipeReadModel.GetCookDetail(workspacePath, cookId);
            return Results.Json(body, statusCode: status);
        });

        app.MapGet("/api/v1/cooks/{cookId}/log", (string cookId, HttpContext context) =>
        {
            RecipeReadModel.CookLogResult log = RecipeReadModel.GetCookLog(workspacePath, cookId);
            context.Response.StatusCode = log.Status;
            context.Response.ContentType = log.ContentType;
            context.Response.Headers["Cache-Control"] = "no-store";
            return context.Response.WriteAsync(log.Text);
        });

        app.MapPost("/api/v1/recipes/{id}/cook", CookStartHandler);
        app.MapPost("/api/v1/recipes/{id}/cook/scheduled", CookStartScheduledHandler);
        app.MapPost("/api/v1/cooks/{id}/stop", CookStopHandler);
        app.MapPost("/api/v1/cooks/{id}/kill", CookStopHandler);

        // Resume-from-checkpoint recovery cook (Slice B). Runs a PAX -Resume
        // recovery pass through the SINGLE sanctioned cook execution mechanism:
        // RecipeReadModel.StartResumeCook funnels into the same SpawnAndSupervise
        // the manual and scheduled cook paths use, so there is one execution
        // mechanism, not a second channel. A resume is a one-time recovery action,
        // NOT a recipe - there is no recipes-table row, no recipe validation, and
        // no recipe-list entry. It still enforces the engine acquisition gate (the
        // engine SHA is re-verified immediately before spawn, constraint 6) and the
        // per-operation Windows Hello step-up (gate 10, keyed to the resume
        // sentinel; fails closed with 401 reAuthRequired). Bearer, CSRF, and
        // broker-lock gates have all run upstream. The request body is
        // { checkpointPath, force?, chefKeyId? }: force defaults to false, and a
        // missing / empty chefKeyId means "restore the saved sign-in from the
        // checkpoint" (no auth switches, no credential injection). The checkpoint
        // path is non-secret provenance; the contents are never read here.
        app.MapPost("/api/v1/resume-cook", async (HttpContext ctx) =>
        {
            EngineAcquisitionResult engine = EngineAcquisition.Resolve(versionInfo, engineLocalAppDataBase);
            JsonElement? body = await ReadJsonBodyAsync(ctx);

            string? checkpointPath = null;
            bool force = false;
            string? chefKeyId = null;
            string? dashboard = null;
            if (body is JsonElement b && b.ValueKind == JsonValueKind.Object)
            {
                if (b.TryGetProperty("checkpointPath", out JsonElement cpEl) &&
                    cpEl.ValueKind == JsonValueKind.String)
                {
                    checkpointPath = cpEl.GetString();
                }
                if (b.TryGetProperty("force", out JsonElement forceEl))
                {
                    if (forceEl.ValueKind == JsonValueKind.True) { force = true; }
                    else if (forceEl.ValueKind == JsonValueKind.False) { force = false; }
                }
                if (b.TryGetProperty("chefKeyId", out JsonElement ckEl) &&
                    ckEl.ValueKind == JsonValueKind.String)
                {
                    chefKeyId = ckEl.GetString();
                }
                if (b.TryGetProperty("dashboard", out JsonElement dashEl) &&
                    dashEl.ValueKind == JsonValueKind.String)
                {
                    dashboard = dashEl.GetString();
                }
            }

            (int status, object respBody) = RecipeReadModel.StartResumeCook(
                workspacePath, versionInfo, engine, checkpointPath, force, chefKeyId,
                dashboard, manualCookReAuthSeam, cookPwshPathOverride);
            return Results.Json(respBody, statusCode: status);
        });

        // Scheduled-task routes (X7a.3). PUT persists the recipe's `schedule`
        // block and registers a per-user Windows Scheduled Task whose action is
        // the X7a.2 native one-shot ("PAX Cookbook.exe --run-scheduled-recipe
        // <id> --workspace <ws> --approot <app>"); DELETE removes the task and
        // clears the schedule; GET is a read-only probe / drift report. These are
        // stateful mutations (PUT/DELETE), so the bearer token, CSRF header, and
        // broker lock are all enforced upstream -- they are NOT on the lock-bypass
        // allow-list. They are deliberately NOT engine-gated: scheduling does not
        // need the engine resolved (the scheduled one-shot resolves + SHA-verifies
        // the engine at FIRE time, constraints 6/8). All *-ScheduledTask cmdlet
        // usage stays isolated to the sanctioned PowerShell registrar
        // (Decision 2); this model only shells `Start-Process pwsh -File
        // <registrar>` and never reads a secret -- the bound Chef's Key is read
        // from Windows Credential Manager at fire time (CK-3). Every body is
        // metadata-only (constraint 14).
        app.MapPut("/api/v1/recipes/{id}/scheduled-task", async (string id, HttpContext context) =>
        {
            if (!RecipeReadModel.IsValidRecipeId(id))
            {
                return Results.Json(new { error = "invalid_recipe_id", recipeId = id },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            object? scheduleBody = await JsonModel.ReadBodyAsync(context);
            (int status, object body) = RecipeScheduleModel.PutScheduledTask(
                workspacePath, appRoot!, id, scheduleBody,
                registrarPathOverride: null, taskFolderOverride: null, pwshOverride: null);
            return Results.Json(body, statusCode: status);
        });

        app.MapDelete("/api/v1/recipes/{id}/scheduled-task", (string id) =>
        {
            if (!RecipeReadModel.IsValidRecipeId(id))
            {
                return Results.Json(new { error = "invalid_recipe_id", recipeId = id },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            (int status, object body) = RecipeScheduleModel.DeleteScheduledTask(
                workspacePath, appRoot!, id,
                registrarPathOverride: null, taskFolderOverride: null, pwshOverride: null);
            return Results.Json(body, statusCode: status);
        });

        app.MapGet("/api/v1/recipes/{id}/scheduled-task", (string id) =>
        {
            if (!RecipeReadModel.IsValidRecipeId(id))
            {
                return Results.Json(new { error = "invalid_recipe_id", recipeId = id },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            (int status, object body) = RecipeScheduleModel.GetScheduledTask(
                workspacePath, appRoot!, id,
                registrarPathOverride: null, taskFolderOverride: null, pwshOverride: null);
            return Results.Json(body, statusCode: status);
        });

        // Bounded catch-all for any other /api/* surface (mutable verbs, routes
        // not yet ported). Returns a stable 501 token rather than crashing the
        // SPA shell. The token middleware has already enforced Bearer auth for
        // /api/v1/* by the time this runs.
        app.MapFallback(context =>
        {
            string path = context.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                context.Response.ContentType = "application/json";
                context.Response.Headers["Cache-Control"] = "no-store";
                return context.Response.WriteAsync(
                    "{\"error\":\"not_implemented_x4\",\"message\":\"This route is not implemented in the X4 read-only runtime slice.\",\"slice\":\"V1_OFFICE_GRADE_X4_RECIPE_READ_MODEL_AND_LOCK_ACTIVITY_WIRING\"}");
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            return context.Response.WriteAsync("not_found");
        });

        // Two-process port-file ownership (V2). An owning broker (the --headless
        // daemon, or a window that started its own broker because no daemon was
        // running) advertises its loopback port in the per-user broker.port file
        // and holds an exclusive write lock on it so a later launch attaches
        // instead of starting a second broker. The handle is assigned AFTER the
        // broker is serving (below) and released on shutdown. The --no-window
        // smoke harness never owns it (it runs isolated brokers).
        FileStream? portFileHandle = null;

        // Graceful shutdown clears the per-session token sidecar.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                if (File.Exists(tokenFile))
                {
                    File.Delete(tokenFile);
                }
            }
            catch
            {
                // Best-effort cleanup; never block shutdown.
            }

            // Release the port-file lock so a stale port is not left advertised.
            try
            {
                BrokerDetection.ReleasePortFile(portFileHandle);
                portFileHandle = null;
            }
            catch
            {
                // Best-effort cleanup; never block shutdown.
            }
        });

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: broker host failed to start: {ex.Message}");
            return 1;
        }

        // The in-process broker is now serving. If this process owns its broker
        // (the daemon, or a standalone window with no daemon running), publish
        // the port and take the exclusive port-file lock so concurrent launches
        // detect and attach to THIS broker. Done only after the health endpoint
        // is live so a detector never reads a port that is not yet answering.
        bool ownsPortFile = headlessDaemon || (!noWindow && !iconDiagnosticsSeam);
        if (ownsPortFile)
        {
            portFileHandle = BrokerDetection.AcquirePortFile(port);
            Console.WriteLine($"X2V_PORT_FILE={BrokerDetection.PortFilePath()}");
            Console.WriteLine($"X2V_PORT_FILE_OWNED={(portFileHandle is null ? "0" : "1")}");
        }

        // Test-only boot-unlocked seam (command-line flag, no HTTP surface). The
        // broker boots Locked by default; this flag flips it to Unlocked using
        // the same SetUnlocked() path the WebAuthn ceremony uses, so the smoke
        // harness can exercise the unlocked read surface and lock-activity bump.
        if (bootUnlockedSeam)
        {
            BrokerLock.SetUnlocked();
        }

        // Startup reconciliation (X2). Heals any cook left 'running' when the
        // broker exited mid-cook. It never kills a process and never deletes a
        // log or an output; it only transitions the index row and repairs the
        // sentinel. Runs once, here, before the read surface is exercised.
        int reconciledCooks = RecipeReadModel.ReconcileCooksAtStartup(workspacePath);

        // Discovery lines for tooling/smoke harnesses (loopback only). The X1_
        // lines are preserved verbatim; X2_ lines add the SPA + shell surface.
        Console.WriteLine($"X1_BROKER_APP={AppName}");
        Console.WriteLine($"X1_BROKER_RUNTIME={RuntimeKind}");
        Console.WriteLine($"X1_BROKER_PORT={port}");
        Console.WriteLine($"X1_BROKER_WORKSPACE={workspacePath}");
        Console.WriteLine($"X1_BROKER_TOKEN_FILE={tokenFile}");
        Console.WriteLine($"X1_BROKER_URL=http://127.0.0.1:{port}/api/v1/health");
        Console.WriteLine($"X2_STATIC_ROOT={webRoot}");
        Console.WriteLine($"X2_APP_URL=http://localhost:{port}/");
        Console.WriteLine($"X2_VERSION_FILE={versionFile}");
        Console.WriteLine($"X2_ICON_FILE={iconPath}");
        Console.WriteLine($"X2_WEBVIEW2_MODE={(noWindow ? "headless" : "window")}");
        Console.WriteLine($"X3_LOCK_BOOT_STATE={BrokerLock.GetState()}");
        Console.WriteLine($"X3_LOCK_STATE_URL=http://127.0.0.1:{port}/api/v1/broker/lock-state");
        Console.WriteLine($"X3_WEBAUTHN_STATUS_URL=http://127.0.0.1:{port}/api/v1/broker/webauthn/status");
        Console.WriteLine($"X3_CREDENTIAL_STORE={Path.Combine(workspacePath, "Auth", "webauthn-credentials.json")}");
        Console.WriteLine($"X4_TEST_SEAM_BOOT_UNLOCKED={(bootUnlockedSeam ? "1" : "0")}");
        Console.WriteLine($"X4_TEMPLATE_CATALOG_COUNT={templateModel.Count}");
        Console.WriteLine($"X13_ENGINE_ACQUISITION_GATE=on");
        Console.WriteLine($"X13_ENGINE_LOCALAPPDATA_OVERRIDE={(engineLocalAppDataOverride is null ? "0" : "1")}");
        Console.WriteLine($"X13_ENGINE_LOCALAPPDATA_BASE={engineLocalAppDataBase}");
        Console.WriteLine($"X15_COOK_PREPARE_GATE=on");
        Console.WriteLine($"X15_TEST_SEAM_COOK_PREPARE={(cookPrepareSeam ? "1" : "0")}");
        Console.WriteLine($"X15_TEST_SEAM_COOK_MIN_FREE_BYTES={cookMinFreeBytes}");
        Console.WriteLine($"X16_MANUAL_COOK_SUPERVISOR=on");
        Console.WriteLine($"X16_TEST_SEAM_MANUAL_COOK_REAUTH_VERIFIED={(manualCookReAuthSeam ? "1" : "0")}");
        Console.WriteLine($"X16_TEST_SEAM_COOK_PWSH_PATH_OVERRIDE={(cookPwshPathOverride is null ? "0" : "1")}");
        Console.WriteLine($"X16B_MANUAL_COOK_REAL_REAUTH=on");
        Console.WriteLine($"X16B_REAUTH_CHALLENGE_URL=http://127.0.0.1:{port}/api/v1/broker/reauth/manual-cook/challenge");
        Console.WriteLine($"X16B_REAUTH_VERIFY_URL=http://127.0.0.1:{port}/api/v1/broker/reauth/manual-cook/verify");
        Console.WriteLine($"X16C_TEST_SEAM_CLOSE_AFTER_MS={selfCloseAfterMs}");
        Console.WriteLine($"X16C_SINGLE_INSTANCE={(noWindow ? "exempt-headless" : "primary")}");
        Console.WriteLine($"X16C_AUMID={(testSeamAumid is null ? "PAXCookbook.Local.v1" : testSeamAumid)}");
        Console.WriteLine($"X2C_COOK_READ_ROUTES=on");
        Console.WriteLine($"X2C_STARTUP_RECONCILE={reconciledCooks}");

        // Cold-start primary: if this launch carried a double-clicked file,
        // stage it as a one-time, locally-staged import ticket. The window host
        // (below) navigates the React app to the Import Recipe state only AFTER
        // the in-process broker lock reads Unlocked — never here, and never as a
        // fabricated success. The absolute path stays on disk inside the ticket;
        // it is never navigated, printed as a payload, or sent over HTTP.
        string primaryHandoffState = "no-request";
        if (fileOpenRequest is FileOpenRequest primaryRequest)
        {
            if (!primaryRequest.Exists)
            {
                primaryHandoffState = "missing-file";
            }
            else
            {
                string? stagedId = ImportHandoffQueue.Enqueue(importHandoffDir, primaryRequest);
                primaryHandoffState = stagedId is null ? "queue-failed" : "queued-ticket";
            }
        }
        WriteFileOpenMarkers(fileOpenRequest, primaryHandoffState);

        // Live taskbar-icon diagnostic short-circuit. Runs BEFORE the SPA shell /
        // headless host loop so it never depends on engine acquisition or a
        // WebAuthn ceremony. The in-process Kestrel host that StartAsync above
        // brought up is loopback-only and harmless during the brief diagnostic;
        // it is torn down cleanly afterward so the process exits and releases the
        // executable lock.
        Console.WriteLine($"X16C_ICON_DIAGNOSTICS={(iconDiagnosticsSeam ? "1" : "0")}");
        if (iconDiagnosticsSeam)
        {
            string diagAumid = string.IsNullOrWhiteSpace(testSeamAumid)
                ? "PAXCookbook.Local.v1"
                : testSeamAumid;
            string diagOutDir = string.IsNullOrWhiteSpace(iconDiagnosticsOutOverride)
                ? Path.Combine(workspacePath, "IconDiagnostics")
                : Path.GetFullPath(iconDiagnosticsOutOverride);
            string exeSelfPath = Environment.ProcessPath ?? iconPath;
            Console.WriteLine($"X16C_ICON_DIAGNOSTICS_OUT={diagOutDir}");
            try
            {
                IconDiagnostics.Run(iconPath, exeSelfPath, diagAumid, diagOutDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL: icon diagnostic terminated: {ex.Message}");
                StopBrokerHost(app);
                return 1;
            }

            StopBrokerHost(app);
            return 0;
        }

        if (noWindow)
        {
            if (headlessDaemon)
            {
                // Background broker daemon: the in-process Kestrel host runs with
                // a system-tray presence but no window, so scheduled bakes fire
                // in the background. The tray loop blocks until the user chooses
                // Exit, which stops the broker; then the port file is released.
                // The HKCU Run key launches this at login.
                Console.WriteLine("X2V_TWO_PROCESS=on");
                Console.WriteLine("X2V_ROLE=daemon");
                try
                {
                    TrayIconHost.Run(
                        iconPath,
                        "PAX Cookbook is running in the background",
                        () => StopBrokerHost(app));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"FATAL: background daemon terminated: {ex.Message}");
                    StopBrokerHost(app);
                    BrokerDetection.ReleasePortFile(portFileHandle);
                    portFileHandle = null;
                    return 1;
                }

                // Tray Exit chosen: the broker is stopped; release the port file
                // so a fresh launch can claim ownership.
                BrokerDetection.ReleasePortFile(portFileHandle);
                portFileHandle = null;
                return 0;
            }

            // Headless smoke mode (--no-window): serve Kestrel only, no tray.
            // Used by the automated smoke harness, which cannot drive a desktop
            // WebView2 control. The shell window is exercised manually.
            try
            {
                app.WaitForShutdown();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FATAL: broker host terminated: {ex.Message}");
                return 1;
            }

            return 0;
        }

        // Window mode: host the SPA inside a native WebView2 control. No Edge
        // --app= shell-out, no PowerShell launcher. localhost (not 127.0.0.1)
        // is the navigation origin so boot.js's UX-1H6 origin guard does not
        // trigger a redirect; Chromium falls back to 127.0.0.1 transport.
        string url = $"http://localhost:{port}/";
        string webView2UserData = Path.Combine(workspacePath, "WebView2");
        try
        {
            WebViewShell.Run(url, AppName, iconPath, webView2UserData, selfCloseAfterMs, restoreSignal, testSeamAumid, importHandoffDir);
        }
        catch (WebView2RuntimeMissingException ex)
        {
            Console.Error.WriteLine(
                "FATAL: the Microsoft Edge WebView2 Runtime is required to run PAX Cookbook but was not found. " +
                "Install the Evergreen WebView2 Runtime from https://developer.microsoft.com/microsoft-edge/webview2/ and relaunch. " +
                $"Detail: {ex.Message}");
            StopBrokerHost(app);
            BrokerDetection.ReleasePortFile(portFileHandle);
            portFileHandle = null;
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: application window terminated: {ex.Message}");
            StopBrokerHost(app);
            BrokerDetection.ReleasePortFile(portFileHandle);
            portFileHandle = null;
            return 1;
        }

        // The window has closed (operator clicked X / FormClosing tore down the
        // embedded WebView2 on the UI thread). Stop the in-process Kestrel host
        // with a bounded timeout so a stuck stop can never hold the process —
        // and therefore the exclusive write lock on the executable — open.
        StopBrokerHost(app);

        // This window owned its broker (no daemon was running at launch), so
        // release the port file it published.
        BrokerDetection.ReleasePortFile(portFileHandle);
        portFileHandle = null;

        // Release the single-instance reservation now that the window is gone so
        // a relaunch is admitted immediately. The mutex is kept alive until here
        // so the GC cannot finalize and abandon it while the window is running.
        try
        {
            singleInstanceMutex?.ReleaseMutex();
            singleInstanceMutex?.Dispose();
            restoreSignal?.Dispose();
        }
        catch
        {
            // Best-effort; the OS releases the named objects on process exit.
        }

        // Deterministic exit. A WebView2-hosting desktop process can retain
        // non-background worker threads from the embedded browser stack even
        // after the window and host are torn down; left to chance they keep the
        // process alive and the exe write-locked, blocking rebuild/update/
        // install. The graceful teardown above has already run (WebView2
        // disposed on the UI thread; Kestrel stopped; ApplicationStopping
        // cleared the session-token sidecar), so exiting now is a clean,
        // bounded last step rather than a shortcut around shutdown.
        Environment.Exit(0);
        return 0;
    }

    // Stops the in-process broker host with a bounded timeout. A hung StopAsync
    // must never block process exit (and thus the exe lock release), so the wait
    // is capped; any failure is swallowed because the window has already closed.
    private static void StopBrokerHost(WebApplication app)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            app.StopAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort; the window has already closed.
        }
    }

    // Workspace resolution: an explicit --workspace <path> always wins. When it
    // is absent — a launch via .paxlite/.pax file association or a paxcookbook://
    // protocol activation (the shell supplies only the file/URI, never our flags),
    // or a plain repo/dev run — default to the canonical PRODUCTION workspace
    // %LOCALAPPDATA%\PAXCookbook\Workspace. There is intentionally no dev-only
    // fallback: every flagless launch must land in the real workspace.
    // The folder name mirrors PAXCookbook.Shared.ProductConstants.WorkspaceFolderName
    // ("Workspace"); the literal is kept self-contained here because the App
    // project deliberately does not reference Shared (see the file-association
    // extension constants near the top of this file).
    private static string ResolveWorkspacePath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--workspace", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "PAXCookbook", "Workspace");
    }

    // Captures a double-clicked recipe file path handed to the EXE by the
    // Windows shell. The .paxlite / .pax associations register the open
    // command as `"<exe>" "%1"`, so the shell passes the file path as a bare
    // positional argument (no verb). This resolver finds the first positional
    // token whose extension is one of our recipe extensions, classifies it,
    // and reports whether it exists on disk.
    //
    // This is OBSERVE-ONLY: it does not import the file, does not navigate the
    // SPA, and does not place anything on the IPC pipe. The end-to-end handoff
    // (authenticated import-staging + deep-link into the Import Recipe surface)
    // is documented as a deliberate blocker — see the slice report — because it
    // would require a new IPC verb and a payload channel that the frozen IPC
    // contract forbids without re-approval. The protocol verb token is
    // explicitly excluded so a paxcookbook:// URI is never misread as a file.
    private static FileOpenRequest? ResolveFileOpenRequest(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("-", StringComparison.Ordinal)) continue;
            if (i > 0 && string.Equals(args[i - 1], "protocol", StringComparison.OrdinalIgnoreCase))
                continue;

            bool isLite = a.EndsWith(
                PaxLiteExtension, StringComparison.OrdinalIgnoreCase);
            bool isFull = !isLite && a.EndsWith(
                PaxFullExtension, StringComparison.OrdinalIgnoreCase);
            if (!isLite && !isFull) continue;

            string full;
            try { full = Path.GetFullPath(a); }
            catch { full = a; }

            return new FileOpenRequest(
                Path: full,
                Kind: isLite ? "paxlite" : "pax",
                Exists: File.Exists(full));
        }

        return null;
    }

    // Emits the observe-only file-open discovery markers consumed by the smoke
    // harness. handoffState is an honest description of what happens next:
    // "not-implemented" (primary instance — import handoff is a documented
    // blocker) or "lost-secondary-instance" (a second launch can only wake the
    // running window; the payload-free single-instance signal cannot carry the
    // path, so the double-clicked file is dropped).
    private static void WriteFileOpenMarkers(FileOpenRequest? req, string handoffState)
    {
        Console.WriteLine($"FILE_ASSOC_OPEN_REQUEST={(req is null ? "0" : "1")}");
        if (req is FileOpenRequest r)
        {
            Console.WriteLine($"FILE_ASSOC_OPEN_KIND={r.Kind}");
            Console.WriteLine($"FILE_ASSOC_OPEN_EXISTS={(r.Exists ? "1" : "0")}");
            Console.WriteLine($"FILE_ASSOC_OPEN_PATH={r.Path}");
            Console.WriteLine($"FILE_ASSOC_OPEN_HANDOFF={handoffState}");
        }
    }

    // Derives a stable, filesystem-case-insensitive key from the workspace path
    // for naming the single-instance mutex and restore event. The key is the
    // first 8 bytes of SHA-256(lowercased full path) as hex, so two launches
    // that target the same workspace share a reservation while isolated test
    // workspaces never collide.
    private static string ComputeSingleInstanceKey(string workspacePath)
    {
        string normalized = Path.GetFullPath(workspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 8);
    }

    // Test-only engine acquisition anchor override. Explicit
    // --engine-localappdata <path> redirects the managed-engine /
    // install-state reader to an isolated fixture. Not an HTTP endpoint, not
    // passed by the desktop launcher; used only by the automated smoke harness
    // so acquisition-state tests never touch the operator's real profile.
    private static string? ResolveEngineLocalAppDataOverride(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--engine-localappdata", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        return null;
    }

    // Test-only X14 acquisition staging override. Explicit --updates-dir
    // <path> redirects the per-attempt work-dir root used by the
    // download / local-file / upload-bytes acquisition routes.
    private static string? ResolveUpdatesDirOverride(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--updates-dir", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }
        return null;
    }

    // Test-only override for the pre-spawn disk-space hard floor used by the
    // cook-start preparation pipeline (X15). Explicit
    // --test-seam-cook-min-free-bytes <n> forces the required-free-bytes floor
    // (mirroring the oracle $Script:MinFreeDiskBytesForCook override) so the
    // smoke harness can drive a deterministic 507 insufficient_disk_space.
    // Returns -1 when absent ("use the production default").
    private static long ResolveCookMinFreeBytesOverride(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--test-seam-cook-min-free-bytes", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                {
                    return parsed;
                }
            }
        }
        return -1;
    }

    // Test-only resolution of the cook child interpreter path. Accepts either
    // "--test-seam-cook-pwsh-path <value>" or "--test-seam-cook-pwsh-path=<value>".
    // Returns null (production `pwsh` resolution) when the flag is absent.
    private static string? ResolveCookPwshPathOverride(string[] args)
    {
        const string flag = "--test-seam-cook-pwsh-path";
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : null;
            }
            if (args[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i].Substring(flag.Length + 1);
            }
        }
        return null;
    }

    // Generic "--flag value" / "--flag=value" reader for the CK-3 credential-env
    // seam. Returns null when the flag is absent; returns the value (possibly
    // empty, e.g. when the flag is the last token) when present.
    private static string? ResolveSeamValueArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : string.Empty;
            }
            if (args[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i].Substring(flag.Length + 1);
            }
        }
        return null;
    }

    // Reads the file named by a seam value arg and returns its full text, or null
    // when the flag is absent or the file cannot be read. Used by the CK-4 seam so
    // JSON / free-text inputs are passed by (space-free) path rather than inline,
    // avoiding command-line quoting issues. Test-only.
    private static string? ReadSeamFileArg(string[] args, string flag)
    {
        string? path = ResolveSeamValueArg(args, flag);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    // V1 cook-window-decision test seam body. Prints the supervisor's window
    // decision for the given recipe auth mode using the SAME production helper
    // (RecipeReadModel.RequiresInteractiveWindowForAuthMode) that sets
    // ProcessStartInfo.CreateNoWindow / WindowStyle at spawn time: WebLogin needs
    // an interactive console window for MSAL/WAM browser sign-in
    // (CreateNoWindow=false, WindowStyle=Hidden so the allocated console starts
    // hidden); DeviceCode, App-registration cert/secret, and any unknown / empty
    // mode run headless (CreateNoWindow=true, WindowStyle=Normal). It resolves
    // nothing, spawns nothing, opens no window, and contacts no service.
    private static int RunCookWindowDecisionSeam(string authMode)
    {
        bool requiresWindow = RecipeReadModel.RequiresInteractiveWindowForAuthMode(authMode);
        Console.WriteLine($"V1WIN_AUTH_MODE={authMode}");
        Console.WriteLine($"V1WIN_REQUIRES_INTERACTIVE_WINDOW={(requiresWindow ? "true" : "false")}");
        Console.WriteLine($"V1WIN_CREATE_NO_WINDOW={(requiresWindow ? "false" : "true")}");
        Console.WriteLine($"V1WIN_WINDOW_STYLE={(requiresWindow ? "Hidden" : "Normal")}");
        return 0;
    }

    // X7a.2 scheduled-run one-shot body. Runs exactly ONE scheduled cook through
    // the SINGLE sanctioned cook pipeline (constraint 8) and exits with a
    // status-mapped code. It is HEADLESS: it opens NO window, takes NO
    // single-instance mutex, and starts NO Kestrel / UI-port HTTP server (it
    // returned from Main before any of those were reached). It resolves its own
    // dependencies (workspace / appRoot / version / engine), then calls
    // RecipeReadModel.StartScheduledCook, which (a) WAIVES the per-operation
    // Windows Hello step-up (gate 10) — the Brian-approved constraint-10
    // modification, scheduled path ONLY — and instead requires the recipe to have
    // an enabled schedule, and (b) blocks until the cook has fully finalized
    // (joinSupervisor=true), so this process never exits early and orphans the
    // pwsh child. Every stdout marker and refusal token is secret-free
    // (constraint 14): only the recipe/cook id, the terminal cook status, and a
    // bounded error token are printed — never a secret, tenant, or command.
    //
    // Exit codes: 0 = cook completed; 4 = appRoot unresolved; 5 = malformed
    // recipe id; 6 = the cook was refused before running (e.g. recipe_not_scheduled,
    // recipe_invalid, acquisitionRequired, recipe_busy, spawn failure); 7 = the
    // cook ran but reached a non-completed terminal state (errored / interrupted /
    // canceled).
    private static int RunScheduledRecipeOneShot(string[] args, string recipeId)
    {
        // Discovery marker on entry (the smoke captures redirected stdout even
        // though a Task-fired WinExe has no visible console).
        Console.WriteLine($"X7_RUN_SCHEDULED_RECIPE={recipeId}");

        // DEPRECATION warning (V2). This path spawns a standalone cook outside the
        // daemon. Prefer --bake, which delegates to the running daemon's single
        // cook supervisor. Retained for backward compatibility with existing Task
        // Scheduler jobs; secret-free, never blocks the run.
        Console.WriteLine(
            "Warning: --run-scheduled-recipe runs outside the daemon. " +
            "Consider switching to --bake for daemon-managed scheduled runs.");

        // a. Validate the recipe-id shape (ULID) up front. A malformed id never
        //    touches the workspace / engine.
        if (!RecipeReadModel.IsValidRecipeId(recipeId))
        {
            Console.WriteLine("X7_SCHEDULED_RUN=refused token=invalid_recipe_id");
            return 5;
        }

        // b. Resolve dependencies. This handler is self-contained because it
        //    returned from Main before the shared workspace / appRoot / engine
        //    resolution.
        string workspacePath = ResolveWorkspacePath(args);
        string? appRoot = ResolveAppRoot(args);
        if (appRoot is null)
        {
            Console.Error.WriteLine(
                "FATAL: could not locate the install-tree app/ root (expected app\\web\\index.html). " +
                "Pass --approot <path-to-app>.");
            Console.WriteLine("X7_SCHEDULED_RUN=refused token=approot_unresolved");
            return 4;
        }

        // A scheduled run can fire before the daemon has ever opened this
        // workspace. Ensure the index database exists with the full schema so
        // the read below sees a real (possibly empty) recipes table rather than
        // a missing file. Idempotent on an already-initialized workspace.
        WorkspaceDatabase.EnsureInitialized(workspacePath);

        VersionInfo versionInfo = LoadVersionInfo(Path.Combine(appRoot, "VERSION.json"));
        string engineLocalAppDataBase = EngineAcquisition.ResolveLocalAppDataBase(
            ResolveEngineLocalAppDataOverride(args));
        EngineAcquisitionResult engine = EngineAcquisition.Resolve(versionInfo, engineLocalAppDataBase);
        long cookMinFreeBytes = ResolveCookMinFreeBytesOverride(args);
        string? cookPwshPathOverride = ResolveCookPwshPathOverride(args);

        // c. Run the scheduled cook. joinSupervisor=true inside StartScheduledCook
        //    means this returns only AFTER the cook has fully finalized.
        (int status, object body) = RecipeReadModel.StartScheduledCook(
            workspacePath, versionInfo, engine, recipeId, cookMinFreeBytes, cookPwshPathOverride);

        // d. Any non-201 is a refusal BEFORE/INSTEAD of a successful run
        //    (recipe_not_scheduled, recipe_invalid, acquisitionRequired,
        //    recipe_busy, spawn failure, ...). Print the bounded, secret-free
        //    machine token only.
        if (status != 201)
        {
            string token = ReadStringMember(body, "error") ?? "refused";
            Console.WriteLine($"X7_SCHEDULED_RUN=refused status={status} token={token}");
            return 6;
        }

        // e. 201 — the cook ran to a terminal state (joinSupervisor waited, so the
        //    row is already terminal; StartScheduledCook surfaced it on the body).
        //    Map the terminal status to the process exit code.
        string? cookId = ReadStringMember(body, "cookId");
        string terminalStatus = ReadStringMember(body, "status") ?? "unknown";
        Console.WriteLine(
            $"X7_SCHEDULED_RUN={terminalStatus} cookId={cookId ?? "unknown"} trigger=scheduled");
        return string.Equals(terminalStatus, "completed", StringComparison.OrdinalIgnoreCase) ? 0 : 7;
    }

    // Reads a single named string property from an anonymous cook-result body.
    // Used ONLY by the scheduled one-shot to surface the secret-free cookId /
    // terminal status / bounded error token; it is never used to print any other
    // field, so no secret can leak through it (constraint 14). Returns null when
    // the property is absent or not a string.
    private static string? ReadStringMember(object body, string name)
    {
        try
        {
            return body.GetType().GetProperty(name)?.GetValue(body) as string;
        }
        catch
        {
            return null;
        }
    }

    // --bake <recipeId> one-shot body (Task Scheduler -> daemon delegation).
    //
    // Detects an already-running daemon, authenticates with the daemon's OWN
    // session token (read from the per-user workspace sidecar — the SAME file the
    // broker validates against, so this is authentication, NOT a bypass), POSTs
    // the cook to the daemon's SCHEDULED-cook route (Bearer + CSRF +
    // X-Cookbook-Scheduled marker), then polls the read-only cook-detail route
    // until the cook reaches a terminal state. It NEVER runs PAX itself, NEVER
    // starts a broker, and NEVER bypasses the broker's auth/validation pipeline:
    // a refused cook returns the broker's bounded, secret-free error and maps to
    // exit 1. No daemon => the bake is skipped (exit 2); nothing is started.
    //
    // SECURITY: the recipe id is validated to the 26-char Crockford-base32 ULID
    // shape (RecipeReadModel.IsValidRecipeId) BEFORE it touches the network, so it
    // cannot carry a path-traversal or URL-injection payload into the request
    // path. The loopback origin is 127.0.0.1 (never localhost) so a hosts-file
    // entry cannot redirect the request off the loopback interface; redirects are
    // not followed and cookies/default credentials are off. The token is read from
    // a per-user, ACL-protected path, sent only to the identity-verified loopback
    // daemon, and never written to stdout (constraint 14 — every printed line is a
    // secret-free id / status / bounded error token).
    //
    // GATE MODEL: the daemon's /cook/scheduled route WAIVES gate 10 (the
    // per-operation Windows Hello step-up — an unattended Task Scheduler fire has
    // no human to verify) but enforces EVERY other gate: Bearer + CSRF + the
    // broker-lock gate (Locked daemon -> 423 -> exit 1), recipe validation, the
    // scheduled-auth gate (the recipe must have an ENABLED schedule, so an
    // unscheduled recipe is refused 409 -> exit 1), acquisition, busy, disk, path,
    // Chef's Key resolution, and the pre-spawn SHA re-verify. The gate-10 waiver
    // is NOT a manual-cook bypass: the React Bake button still uses the manual
    // route with gate 10, and only a recipe the user already authorized for
    // scheduling can run here.
    //
    // Exit codes (deterministic): 0 = bake completed; 1 = bake failed / errored /
    // interrupted / canceled / refused / lost connection; 2 = no daemon running
    // (bake skipped); 130 = Ctrl+C (monitoring detached; the bake keeps running in
    // the daemon — we never POST a stop).
    private static int RunBakeViaDaemon(string[] args, string recipeId)
    {
        Console.WriteLine("BAKE_MODE=on");

        // 1. Validate the recipe-id shape up front. A malformed / empty id never
        //    reaches the network and cannot inject into the request path.
        if (string.IsNullOrWhiteSpace(recipeId) || !RecipeReadModel.IsValidRecipeId(recipeId))
        {
            Console.Error.WriteLine("Usage: PAX Cookbook.exe --bake <recipe-id>");
            Console.WriteLine("BAKE=refused token=invalid_recipe_id");
            return 1;
        }

        // 2. Detect a running daemon (port file + identity-verified loopback
        //    health probe — the SAME detection the attach window uses). No daemon
        //    => skip the bake; do NOT start a broker, open a window, or run PAX.
        if (BrokerDetection.TryGetRunningBrokerPort() is not int daemonPort)
        {
            Console.WriteLine(
                "PAX Cookbook broker is not running. Scheduled bake skipped. " +
                "Ensure 'Start PAX Cookbook at login' is enabled in Settings.");
            Console.WriteLine("BAKE=skipped token=no_daemon");
            return 2;
        }

        // 3. Read the daemon's session token from the per-user workspace sidecar
        //    (<workspace>\Runtime\broker.token) — the SAME file the broker
        //    validates against. Same user + same default workspace => same file,
        //    so the CLI authenticates exactly like an attached UI window.
        string workspacePath = ResolveWorkspacePath(args);
        string tokenFile = Path.Combine(workspacePath, "Runtime", "broker.token");
        string token;
        try { token = File.ReadAllText(tokenFile).Trim(); }
        catch { token = string.Empty; }
        if (token.Length == 0)
        {
            Console.Error.WriteLine("Could not read the broker session token; the daemon may be shutting down.");
            Console.WriteLine("BAKE=failed token=no_token");
            return 1;
        }

        string baseUrl = $"http://{LoopbackBindAddress}:{daemonPort}";
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + token);
        // The cook POST is state-changing, so the broker requires the CSRF marker.
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Cookbook-Request", "1");
        // Scheduled-only marker — the daemon's /cook/scheduled route requires it
        // (defense-in-depth so the React UI, which never sends it, cannot reach
        // the gate-10-waived scheduled path).
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Cookbook-Scheduled", "1");

        // 4. POST the cook to the daemon's SCHEDULED-cook route
        //    (POST /api/v1/recipes/{id}/cook/scheduled). Unlike the manual route,
        //    this waives gate 10 (Windows Hello) — an unattended Task Scheduler
        //    fire has no human to verify — but keeps Bearer + CSRF + lock + the
        //    scheduled-auth gate (the recipe must have an enabled schedule) + full
        //    recipe validation. The daemon owns every gate; any non-201 is the
        //    broker refusing, mapped to exit 1. A 404 with NO broker error body
        //    means the running daemon is an OLDER build without this route.
        string cookId;
        try
        {
            using var postContent = new StringContent(string.Empty);
            using HttpResponseMessage post =
                http.PostAsync($"{baseUrl}/api/v1/recipes/{recipeId}/cook/scheduled", postContent)
                    .GetAwaiter().GetResult();
            string postBody = post.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if ((int)post.StatusCode != 201)
            {
                string? errField = ExtractJsonStringField(postBody, "error");
                // A 404 with no broker error JSON = the route does not exist on the
                // running daemon (an older build). Tell the user to restart so the
                // daemon picks up the new scheduled route. A 404 WITH an error body
                // (e.g. "not_found") is the broker reporting an unknown recipe.
                if ((int)post.StatusCode == 404 && errField is null)
                {
                    Console.WriteLine(
                        "The running broker does not support scheduled bakes. " +
                        "Please restart PAX Cookbook to update the broker.");
                    Console.WriteLine("BAKE=failed status=404 token=route_unsupported");
                    return 1;
                }
                if ((int)post.StatusCode == 404)
                {
                    Console.WriteLine($"Recipe not found: {recipeId}");
                }
                string errToken = errField
                    ?? ExtractJsonStringField(postBody, "code")
                    ?? "cook_refused";
                Console.WriteLine($"Bake failed: {errToken}");
                Console.WriteLine($"BAKE=failed status={(int)post.StatusCode} token={errToken}");
                return 1;
            }
            string? cid = ExtractJsonStringField(postBody, "cookId");
            if (string.IsNullOrEmpty(cid))
            {
                Console.WriteLine("Bake failed: no_cook_id");
                Console.WriteLine("BAKE=failed token=no_cook_id");
                return 1;
            }
            cookId = cid;
        }
        catch (Exception)
        {
            // Timeout / socket error contacting the daemon for the POST.
            Console.WriteLine("Bake failed: broker_unreachable");
            Console.WriteLine("BAKE=failed token=post_failed");
            return 1;
        }

        Console.WriteLine($"BAKE_STARTED cookId={cookId}");

        // Ctrl+C => stop polling and exit WITHOUT cancelling the cook (we never
        // POST a stop). The bake keeps running in the daemon. Task-Scheduler runs
        // never hit this; it is a convenience for an interactive CLI caller.
        bool interrupted = false;
        ConsoleCancelEventHandler onCancel = (_, e) => { interrupted = true; e.Cancel = true; };
        Console.CancelKeyPress += onCancel;
        try
        {
            // 5. Poll the read-only cook-detail route (GET /api/v1/cooks/{cookId})
            //    until the cook reaches a terminal state.
            while (true)
            {
                // Sleep ~10 s in 100 ms slices so a Ctrl+C is observed promptly.
                for (int i = 0; i < 100 && !interrupted; i++)
                {
                    System.Threading.Thread.Sleep(100);
                }
                if (interrupted)
                {
                    Console.WriteLine("Bake monitoring interrupted; the bake continues in the background.");
                    Console.WriteLine("BAKE=detached token=ctrl_c");
                    return 130;
                }

                string status;
                try
                {
                    using HttpResponseMessage poll =
                        http.GetAsync($"{baseUrl}/api/v1/cooks/{cookId}").GetAwaiter().GetResult();
                    string pollBody = poll.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!poll.IsSuccessStatusCode)
                    {
                        Console.WriteLine("Lost connection to broker during bake");
                        Console.WriteLine($"BAKE=failed status={(int)poll.StatusCode} token=poll_failed");
                        return 1;
                    }
                    status = ExtractJsonStringField(pollBody, "status") ?? "unknown";
                }
                catch (Exception)
                {
                    Console.WriteLine("Lost connection to broker during bake");
                    Console.WriteLine("BAKE=failed token=poll_unreachable");
                    return 1;
                }

                switch (status)
                {
                    case "completed":
                        Console.WriteLine("Bake completed: completed");
                        Console.WriteLine($"BAKE=completed cookId={cookId}");
                        return 0;
                    case "canceled":
                        Console.WriteLine("Bake was cancelled");
                        Console.WriteLine($"BAKE=canceled cookId={cookId}");
                        return 1;
                    case "errored":
                    case "interrupted":
                        Console.WriteLine($"Bake failed: {status}");
                        Console.WriteLine($"BAKE=failed cookId={cookId} token={status}");
                        return 1;
                    default:
                        // 'running' or a brief 'unknown' right after the 201:
                        // keep polling. A dead daemon surfaces as a failed poll
                        // above, so this loop is bounded by the daemon's liveness.
                        continue;
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    // Extracts a single top-level string property from a JSON object body, or
    // null when the body is not an object / the property is absent or not a
    // string. Used ONLY to read the secret-free cookId / status / error token
    // from a broker response (constraint 14 — no other field is read or printed).
    private static string? ExtractJsonStringField(string json, string field)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(field, out JsonElement el)
                && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
        }
        catch
        {
            // Non-JSON / malformed body: no field.
        }
        return null;
    }

    // V1 console-hide fallback test seam body. Proves the best-effort
    // ShowWindow(SW_HIDE) fallback (used to hide the WebLogin child's console
    // window) is safe: it never throws on a zero handle and the bounded poll
    // returns within its budget without blocking. It spawns nothing, opens no
    // window, contacts no service, and reads no secret -- it only exercises the
    // helper's safety primitives against synthetic inputs.
    private static int RunHideConsoleFallbackSeam()
    {
        Console.WriteLine("V1HIDE_SEAM=1");

        // A zero handle is a safe no-op (false, no throw).
        bool zeroResult = CookConsoleWindow.TryHideWindowSafe(0);
        Console.WriteLine($"V1HIDE_ZERO_HANDLE_RESULT={(zeroResult ? "true" : "false")}");

        // A never-resolving handle source must return false within ~budget and
        // never throw or hang.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool boundedResult = CookConsoleWindow.PollAndHideBounded(() => 0, 300, 50);
        sw.Stop();
        Console.WriteLine($"V1HIDE_BOUNDED_RESULT={(boundedResult ? "true" : "false")}");
        Console.WriteLine($"V1HIDE_BOUNDED_ELAPSED_MS={sw.ElapsedMilliseconds}");
        Console.WriteLine("V1HIDE_NO_THROW=1");
        Console.WriteLine("V1HIDE_SEAM_DONE=1");
        return 0;
    }

    // CK-3 credential-injection test seam body. Resolves the named Chef's Key,
    // drives the gate-14 bake-time resolution (the App-registration 501 is gone)
    // and the child-only GRAPH_* injection builder against an in-memory
    // ProcessStartInfo, and prints discovery markers. It NEVER spawns pwsh, NEVER
    // acquires the engine, NEVER starts Kestrel, NEVER contacts Graph, and NEVER
    // prints the client secret -- only a presence flag, a length, and a match
    // boolean against an expected SHA-256 the smoke supplies. A chefKeyId of
    // "NONE" (case-insensitive) or empty means "unbound", used to assert the
    // bounded resolve-or-error path that replaced the 501.
    private static int RunCookCredentialEnvSeam(string chefKeyId, string recipeMode, string? expectSecretSha256)
    {
        bool unbound = string.IsNullOrEmpty(chefKeyId) ||
            string.Equals(chefKeyId, "NONE", StringComparison.OrdinalIgnoreCase);
        string? boundId = unbound ? null : chefKeyId;

        Console.WriteLine("CK3_SEAM=1");
        Console.WriteLine($"CK3_SEAM_CHEFKEYID={(unbound ? "NONE" : chefKeyId)}");
        Console.WriteLine($"CK3_SEAM_MODE={recipeMode}");

        // Gate-14 bake-time resolution: a bounded status + row/secret presence, no
        // secret material. AppReg + bound usable key => 200/hasRow=1; AppReg +
        // unbound/missing/secret-less => 412/hasRow=0; interactive => 200/hasRow=0.
        (int resolveStatus, bool hasRow, bool resolvedHasSecret) =
            RecipeReadModel.TestSeamResolveChefKeyForProjection(recipeMode, boundId);
        Console.WriteLine($"CK3_RESOLVE_STATUS={resolveStatus}");
        Console.WriteLine($"CK3_RESOLVE_HASROW={(hasRow ? 1 : 0)}");
        Console.WriteLine($"CK3_RESOLVE_HASSECRET={(resolvedHasSecret ? 1 : 0)}");

        // Child-only injection against an in-memory ProcessStartInfo (UseShellExecute
        // = false makes psi.Environment a per-child copy of the broker env).
        ChefKeyModel.ChefKeyResolved? ck = unbound ? null : ChefKeyModel.ResolveForRecipe(boundId);
        Console.WriteLine($"CK3_CK_AUTHTYPE={(ck?.AuthType is { Length: > 0 } t ? t : "none")}");

        var psi = new System.Diagnostics.ProcessStartInfo { UseShellExecute = false };
        CookCredentialInjection.CredentialInjectionOutcome outcome =
            CookCredentialInjection.ApplyChildCredentialEnv(psi.Environment, ck);
        Console.WriteLine($"CK3_INJECT_OUTCOME={outcome}");

        var graphKeys = new List<string>();
        foreach (KeyValuePair<string, string?> kv in psi.Environment)
        {
            if (kv.Key.StartsWith("GRAPH_", StringComparison.Ordinal)) { graphKeys.Add(kv.Key); }
        }
        graphKeys.Sort(StringComparer.Ordinal);
        Console.WriteLine($"CK3_GRAPH_KEYS={string.Join(",", graphKeys)}");
        Console.WriteLine($"CK3_GRAPH_TENANT_ID={ChildEnvValue(psi, CookCredentialInjection.GraphTenantId)}");
        Console.WriteLine($"CK3_GRAPH_CLIENT_ID={ChildEnvValue(psi, CookCredentialInjection.GraphClientId)}");
        Console.WriteLine($"CK3_GRAPH_CLIENT_CERT_THUMBPRINT={ChildEnvValue(psi, CookCredentialInjection.GraphClientCertThumbprint)}");

        // Secret: presence + length + match ONLY. The value is never printed.
        bool secretPresent =
            psi.Environment.TryGetValue(CookCredentialInjection.GraphClientSecret, out string? secretVal) &&
            secretVal is not null;
        Console.WriteLine($"CK3_GRAPH_CLIENT_SECRET_PRESENT={(secretPresent ? 1 : 0)}");
        Console.WriteLine($"CK3_GRAPH_CLIENT_SECRET_LEN={(secretPresent ? secretVal!.Length : 0)}");
        string matchMarker = "na";
        if (secretPresent && !string.IsNullOrEmpty(expectSecretSha256))
        {
            string actualSha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secretVal!)));
            matchMarker = string.Equals(actualSha, expectSecretSha256, StringComparison.OrdinalIgnoreCase) ? "1" : "0";
        }
        Console.WriteLine($"CK3_GRAPH_CLIENT_SECRET_MATCH={matchMarker}");

        // Child-only proof: the broker's OWN process environment must never carry
        // GRAPH_* (the helper never calls Environment.SetEnvironmentVariable).
        Console.WriteLine($"CK3_BROKER_ENV_SECRET={(Environment.GetEnvironmentVariable(CookCredentialInjection.GraphClientSecret) is null ? "null" : "present")}");
        Console.WriteLine($"CK3_BROKER_ENV_TENANT={(Environment.GetEnvironmentVariable(CookCredentialInjection.GraphTenantId) is null ? "null" : "present")}");
        Console.WriteLine($"CK3_BROKER_ENV_CLIENT={(Environment.GetEnvironmentVariable(CookCredentialInjection.GraphClientId) is null ? "null" : "present")}");

        // Post-spawn scrub: GRAPH_CLIENT_SECRET removed from the parent dict.
        CookCredentialInjection.ScrubSecretEnv(psi.Environment);
        Console.WriteLine($"CK3_POST_SCRUB_SECRET_PRESENT={(psi.Environment.ContainsKey(CookCredentialInjection.GraphClientSecret) ? 1 : 0)}");

        Console.WriteLine("CK3_SEAM_DONE=1");
        return 0;
    }

    // CK-match test seam body. Drives the REAL save-path mismatch decision and
    // the REAL read-only readiness projection against a recipe whose bound
    // Chef's Key is resolved from the per-user WCM vault. Metadata only -- no
    // secret read, no persist, no spawn, no engine acquisition, no Kestrel, no
    // tenant contact.
    private static int RunCkMatchSeam(string recipeFilePath, string[] args)
    {
        Console.WriteLine("CKMATCH_SEAM=1");
        try
        {
            string json = File.ReadAllText(recipeFilePath);
            if (JsonModel.Parse(json) is not Dictionary<string, object?> recipe)
            {
                Console.WriteLine("CKMATCH_PARSE=invalid_json");
                Console.WriteLine("CKMATCH_SEAM_DONE=1");
                return 0;
            }

            // 2C: the save-path mismatch decision the create / update routes use.
            bool saveReject = ChefKeyModel.TryGetRecipeModeMismatch(recipe, out string saveMode, out string saveCkType);
            Console.WriteLine($"CKMATCH_SAVE_REJECT={(saveReject ? 1 : 0)}");
            Console.WriteLine($"CKMATCH_SAVE_RECIPE_MODE={saveMode}");
            Console.WriteLine($"CKMATCH_SAVE_CK_TYPE={(saveCkType.Length > 0 ? saveCkType : "none")}");

            // 2B: the read-only readiness projection. A fresh parse is passed as
            // the body because Handle fills server-managed draft fields in place.
            string? appRoot = ResolveAppRoot(args);
            if (appRoot is null)
            {
                Console.WriteLine("CKMATCH_READINESS=approot_unresolved");
                Console.WriteLine("CKMATCH_SEAM_DONE=1");
                return 0;
            }
            VersionInfo versionInfo = LoadVersionInfo(Path.Combine(appRoot, "VERSION.json"));
            string paxScriptPath = Path.Combine(appRoot, "resources", "pax", "PAX_Purview_Audit_Log_Processor.ps1");
            string engineBase = EngineAcquisition.ResolveLocalAppDataBase(ResolveEngineLocalAppDataOverride(args));
            string workspace = ResolveSeamValueArg(args, "--workspace") ?? Path.GetTempPath();

            object? readinessBody = JsonModel.Parse(json);
            (int rStatus, object rBody) =
                RecipeReadinessModel.Handle(workspace, paxScriptPath, versionInfo, engineBase, readinessBody);
            Console.WriteLine($"CKMATCH_READINESS_HTTP={rStatus}");

            string bodyJson = System.Text.Json.JsonSerializer.Serialize(rBody);
            using var doc = System.Text.Json.JsonDocument.Parse(bodyJson);
            System.Text.Json.JsonElement root = doc.RootElement;
            string status = root.TryGetProperty("status", out System.Text.Json.JsonElement st)
                ? (st.GetString() ?? string.Empty) : string.Empty;
            Console.WriteLine($"CKMATCH_READINESS_STATUS={status}");
            bool readinessOk = root.TryGetProperty("ok", out System.Text.Json.JsonElement okEl) && okEl.GetBoolean();
            Console.WriteLine($"CKMATCH_READINESS_OK={(readinessOk ? 1 : 0)}");

            bool authFound = false;
            bool authMet = true;
            string authDetail = string.Empty;
            if (root.TryGetProperty("requirements", out System.Text.Json.JsonElement reqs) &&
                reqs.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (System.Text.Json.JsonElement req in reqs.EnumerateArray())
                {
                    if (req.TryGetProperty("id", out System.Text.Json.JsonElement idEl) &&
                        idEl.GetString() == "auth")
                    {
                        authFound = true;
                        authMet = req.TryGetProperty("met", out System.Text.Json.JsonElement metEl) && metEl.GetBoolean();
                        authDetail = req.TryGetProperty("detail", out System.Text.Json.JsonElement detEl)
                            ? (detEl.GetString() ?? string.Empty) : string.Empty;
                    }
                }
            }
            Console.WriteLine($"CKMATCH_READINESS_AUTH_FOUND={(authFound ? 1 : 0)}");
            Console.WriteLine($"CKMATCH_READINESS_AUTH_MET={(authMet ? 1 : 0)}");
            Console.WriteLine($"CKMATCH_READINESS_AUTH_DETAIL={authDetail}");

            // Diagnostics when the draft did not project (so a needs-setup
            // envelope can never be mistaken for an auth pass in the smoke).
            if (!readinessOk)
            {
                if (root.TryGetProperty("errors", out System.Text.Json.JsonElement errsEl) &&
                    errsEl.ValueKind == System.Text.Json.JsonValueKind.Array && errsEl.GetArrayLength() > 0)
                {
                    Console.WriteLine($"CKMATCH_READINESS_ERR0={errsEl[0].GetString()}");
                }
                if (root.TryGetProperty("details", out System.Text.Json.JsonElement detsEl) &&
                    detsEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    string raw = detsEl.GetRawText();
                    Console.WriteLine($"CKMATCH_READINESS_DETAILS={(raw.Length > 400 ? raw.Substring(0, 400) : raw)}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CKMATCH_ERROR={ex.GetType().Name}:{ex.Message}");
        }
        Console.WriteLine("CKMATCH_SEAM_DONE=1");
        return 0;
    }

    private static string ChildEnvValue(System.Diagnostics.ProcessStartInfo psi, string key) =>
        psi.Environment.TryGetValue(key, out string? v) && v is not null ? v : string.Empty;

    // CK-4 Telegram-notification test seam body. Exercises the metadata-only
    // builders, the Device Code parser, the secret-free settings projection, the
    // getUpdates chat-id extractor, and the swallow-all send primitive. It NEVER
    // makes a real network call (notify-throws injects a throwing fake sender),
    // NEVER spawns pwsh, NEVER acquires the engine, NEVER starts Kestrel, and
    // NEVER prints a bot token. Markers are written to stdout for the smoke.
    private static int RunTelegramNotifierSeam(string[] args, string kind)
    {
        Console.WriteLine("CK4_SEAM=1");
        Console.WriteLine($"CK4_SEAM_KIND={kind}");

        switch (kind.ToLowerInvariant())
        {
            case "build-completion":
            case "build-failure":
            {
                string? factPath = ResolveSeamValueArg(args, "--test-seam-telegram-fact-path");
                long? factSize = null;
                if (long.TryParse(
                        ResolveSeamValueArg(args, "--test-seam-telegram-fact-size"),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedSize))
                {
                    factSize = parsedSize;
                }

                bool success = kind.Equals("build-completion", StringComparison.OrdinalIgnoreCase);
                var meta = new TelegramNotifier.BakeNotificationMetadata(
                    RecipeName: "CK4 Smoke Recipe",
                    Status: success ? "completed" : "failed",
                    ExitCode: success ? 0 : 1,
                    DurationSeconds: 12.5,
                    Trigger: "manual",
                    OutputPath: factPath,
                    OutputSizeBytes: factSize,
                    FailureReason: success ? null : "nonzero_exit");

                string msg = success
                    ? TelegramNotifier.BuildBakeCompletionMessage(meta)
                    : TelegramNotifier.BuildBakeFailureMessage(meta);

                // Fenced so the smoke can extract the (multi-line) message verbatim.
                Console.WriteLine("CK4_MSG_BEGIN");
                Console.WriteLine(msg);
                Console.WriteLine("CK4_MSG_END");
                break;
            }
            case "build-devicecode":
            {
                string msg = TelegramNotifier.BuildDeviceCodeMessage(
                    "https://microsoft.com/devicelogin", "CK4DEVCODE");
                Console.WriteLine("CK4_MSG_BEGIN");
                Console.WriteLine(msg);
                Console.WriteLine("CK4_MSG_END");
                break;
            }
            case "parse-devicecode":
            {
                string line = ReadSeamFileArg(args, "--test-seam-telegram-line-file")
                    ?? ResolveSeamValueArg(args, "--test-seam-telegram-line")
                    ?? string.Empty;
                bool ok = TelegramNotifier.TryParseDeviceCodePrompt(line, out string url, out string code);
                Console.WriteLine($"CK4_PARSE_OK={(ok ? 1 : 0)}");
                Console.WriteLine($"CK4_PARSE_URL={url}");
                Console.WriteLine($"CK4_PARSE_CODE={code}");
                break;
            }
            case "notify-throws":
            {
                // Inject a sender that always throws; the swallow-all primitive
                // must return SendFailed rather than propagate the exception.
                TelegramNotifier.NotifyOutcome outcome = TelegramNotifier.SendWrapped(
                    new ThrowingTelegramSender(), "ck4-seam-fake-token", "123456789", "seam message");
                Console.WriteLine($"CK4_NOTIFY_OUTCOME={outcome}");
                Console.WriteLine("CK4_NOTIFY_RETURNED=1");
                break;
            }
            case "settings-projection":
            {
                string userName = ReadSeamFileArg(args, "--test-seam-telegram-username-file")
                    ?? ResolveSeamValueArg(args, "--test-seam-telegram-username")
                    ?? string.Empty;
                bool hasSecret = string.Equals(
                    ResolveSeamValueArg(args, "--test-seam-telegram-hassecret"), "1", StringComparison.Ordinal);
                Dictionary<string, object?> resp = TelegramNotifier.BuildSettingsResponse(userName, hasSecret);
                string json = System.Text.Encoding.UTF8.GetString(JsonModel.SerializeToUtf8Bytes(resp));
                Console.WriteLine("CK4_PROJECTION_BEGIN");
                Console.WriteLine(json);
                Console.WriteLine("CK4_PROJECTION_END");
                break;
            }
            case "extract-chatid":
            {
                string body = ReadSeamFileArg(args, "--test-seam-telegram-updates-file")
                    ?? ResolveSeamValueArg(args, "--test-seam-telegram-updates")
                    ?? string.Empty;
                string? chatId = TelegramNotifier.ExtractNewestChatId(body);
                Console.WriteLine($"CK4_CHATID={chatId ?? "none"}");
                break;
            }
            default:
                Console.WriteLine("CK4_SEAM_UNKNOWN_KIND=1");
                break;
        }

        Console.WriteLine("CK4_SEAM_DONE=1");
        return 0;
    }

    // Forced-failure sender for the CK-4 notify-throws seam. Proves the
    // swallow-all send primitive never propagates a sender exception.
    private sealed class ThrowingTelegramSender : TelegramNotifier.ITelegramSender
    {
        public bool Send(string botToken, string chatId, string text) =>
            throw new InvalidOperationException("seam: forced sender failure");
    }

    // Test-only resolution of the window-mode self-close delay (X16C-GATE-FIX3).
    // Accepts "--test-seam-close-after-ms <n>". Returns 0 (no self-close) when
    // the flag is absent or unparseable.
    private static int ResolveSelfCloseAfterMsOverride(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--test-seam-close-after-ms", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                    parsed > 0)
                {
                    return parsed;
                }
            }
        }
        return 0;
    }

    // Diagnostic taskbar-identity seam. Accepts "--test-seam-aumid <id>" and
    // returns the override AppUserModelID, or null when the flag is absent or
    // empty. Launching under a fresh, never-before-seen AUMID forces Windows to
    // rasterize the taskbar icon from the executable with no cached per-AUMID
    // entry, which separates a stale shell icon cache from a defective asset.
    // The desktop launcher never passes this; it is for operator/CI diagnosis.
    private static string? ResolveTestSeamAumidOverride(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--test-seam-aumid", StringComparison.OrdinalIgnoreCase))
            {
                string candidate = args[i + 1];
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    // Optional output-folder override for the icon diagnostic seam
    // (--test-seam-icon-diagnostics-out <dir>). When absent the diagnostic
    // writes under <workspace>\IconDiagnostics. The desktop launcher never
    // passes it; it is for operator/CI runs that pin the artifact location.
    private static string? ResolveIconDiagnosticsOutOverride(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--test-seam-icon-diagnostics-out", StringComparison.OrdinalIgnoreCase))
            {
                string candidate = args[i + 1];
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
    // from the executable directory (and the current directory) looking for the
    // install-tree app\ folder identified by app\web\index.html. Read-only.
    private static string? ResolveAppRoot(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--approot", StringComparison.OrdinalIgnoreCase))
            {
                string explicitRoot = Path.GetFullPath(args[i + 1]);
                return IsAppRoot(explicitRoot) ? explicitRoot : null;
            }
        }

        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "app");
                if (IsAppRoot(candidate))
                {
                    return Path.GetFullPath(candidate);
                }

                dir = dir.Parent;
            }
        }

        return null;
    }

    private static bool IsAppRoot(string appRoot)
    {
        return File.Exists(Path.Combine(appRoot, "web", "index.html"));
    }

    // Read-only snapshot of app\VERSION.json. The PAX engine SHA is read
    // verbatim from VERSION.json; it is never recomputed and the engine bytes
    // are never read.
    private static VersionInfo LoadVersionInfo(string versionFile)
    {
        string cookbookVersion = "0.0.0";
        string releaseChannel = "unknown";
        string paxVersion = "unknown";
        string paxSha256 = "unknown";
        string paxRelativePath = "unknown";
        string paxAcquisitionPolicy = "embedded";
        string? engineManifestUrl = null;
        string? engineManifestTrustAnchorThumbprint = null;
        string manifestSignaturePolicy = "required";

        try
        {
            using FileStream fs = File.OpenRead(versionFile);
            using var doc = System.Text.Json.JsonDocument.Parse(fs);
            System.Text.Json.JsonElement root = doc.RootElement;

            if (root.TryGetProperty("channel", out var channel) && channel.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                releaseChannel = channel.GetString() ?? releaseChannel;
            }

            if (root.TryGetProperty("cookbook", out var cookbook) && cookbook.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (cookbook.TryGetProperty("version", out var cv) && cv.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    cookbookVersion = cv.GetString() ?? cookbookVersion;
                }
            }

            if (root.TryGetProperty("paxScript", out var pax) && pax.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (pax.TryGetProperty("version", out var pv) && pv.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    paxVersion = pv.GetString() ?? paxVersion;
                }
                if (pax.TryGetProperty("sha256", out var ph) && ph.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    paxSha256 = ph.GetString() ?? paxSha256;
                }
                if (pax.TryGetProperty("relativePath", out var pr) && pr.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    paxRelativePath = pr.GetString() ?? paxRelativePath;
                }
                if (pax.TryGetProperty("acquisitionPolicy", out var pap) && pap.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    paxAcquisitionPolicy = pap.GetString() ?? paxAcquisitionPolicy;
                }
                if (pax.TryGetProperty("engineManifestUrl", out var emu) && emu.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string? s = emu.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) { engineManifestUrl = s; }
                }
                if (pax.TryGetProperty("engineManifestTrustAnchorThumbprint", out var emta) && emta.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string? s = emta.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) { engineManifestTrustAnchorThumbprint = s; }
                }
                if (pax.TryGetProperty("manifestSignaturePolicy", out var msp) && msp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string? s = msp.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) { manifestSignaturePolicy = s; }
                }
            }
        }
        catch
        {
            // VERSION.json missing or malformed: fall back to safe defaults so
            // the read routes still return a bounded, non-crashing body.
        }

        return new VersionInfo(cookbookVersion, releaseChannel, paxVersion, paxSha256,
            paxRelativePath, paxAcquisitionPolicy,
            engineManifestUrl, engineManifestTrustAnchorThumbprint, manifestSignaturePolicy);
    }

    // Static SPA serving — oracle parity with app\broker\Http\StaticHandler.ps1.
    private static async Task ServeStaticAsync(HttpContext context, string webRoot, string sessionToken)
    {
        context.Response.Headers["Cache-Control"] = "no-store";

        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await WriteStaticErrorAsync(context, StatusCodes.Status404NotFound, "not_found");
            return;
        }

        string rel = context.Request.Path.Value ?? string.Empty;
        if (string.IsNullOrEmpty(rel) || rel == "/")
        {
            rel = "/index.html";
        }
        rel = rel.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

        string rootCanonical = Path.GetFullPath(webRoot);
        string rootBoundary = rootCanonical + Path.DirectorySeparatorChar;

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(rootCanonical, rel));
        }
        catch
        {
            await WriteStaticErrorAsync(context, StatusCodes.Status403Forbidden, "forbidden");
            return;
        }

        // The canonicalized path must be a strict descendant of the web root.
        if (!candidate.StartsWith(rootBoundary, StringComparison.OrdinalIgnoreCase))
        {
            await WriteStaticErrorAsync(context, StatusCodes.Status403Forbidden, "forbidden");
            return;
        }

        string ext = Path.GetExtension(candidate).ToLowerInvariant();
        if (!StaticMimeMap.TryGetValue(ext, out string? contentType))
        {
            await WriteStaticErrorAsync(context, StatusCodes.Status404NotFound, "not_found");
            return;
        }

        if (!File.Exists(candidate))
        {
            await WriteStaticErrorAsync(context, StatusCodes.Status404NotFound, "not_found");
            return;
        }

        // Server-side token bootstrap for index.html (oracle parity). The token
        // is injected into an inline <script> in <head>, immediately before the
        // boot.js script tag, so boot.js adopts it into sessionStorage without
        // depending on a URL fragment.
        bool isIndexHtml = ext == ".html" &&
            string.Equals(Path.GetFileName(candidate), "index.html", StringComparison.OrdinalIgnoreCase);

        if (isIndexHtml && sessionToken.Length > 0)
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            string html = await File.ReadAllTextAsync(candidate, utf8NoBom);

            const string anchor = "<script src=\"assets/boot.js";
            string injected = "<script id=\"cookbook-token-bootstrap\">window.__cookbookBootstrapToken='" +
                              sessionToken + "';</script>\n    ";

            int anchorIdx = html.IndexOf(anchor, StringComparison.Ordinal);
            if (anchorIdx >= 0)
            {
                html = html[..anchorIdx] + injected + html[anchorIdx..];
            }
            else
            {
                // React (Vite) index.html uses <script type="module" ...> and
                // has no boot.js anchor. Inject the same bootstrap script before
                // the first <script tag so the React bridge can adopt the token,
                // falling back to just before </head> if there is no script tag.
                int scriptIdx = html.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
                if (scriptIdx >= 0)
                {
                    html = html[..scriptIdx] + injected + html[scriptIdx..];
                }
                else
                {
                    int headEndIdx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                    if (headEndIdx >= 0)
                    {
                        html = html[..headEndIdx] + injected + html[headEndIdx..];
                    }
                }
            }

            byte[] htmlBytes = utf8NoBom.GetBytes(html);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = contentType;
            context.Response.ContentLength = htmlBytes.LongLength;
            await context.Response.Body.WriteAsync(htmlBytes);
            return;
        }

        byte[] bytes = await File.ReadAllBytesAsync(candidate);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.LongLength;
        await context.Response.Body.WriteAsync(bytes);
    }

    private static async Task WriteStaticErrorAsync(HttpContext context, int status, string message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength = bytes.LongLength;
        await context.Response.Body.WriteAsync(bytes);
    }

    // Writes a JSON body from middleware (Cache-Control: no-store, oracle
    // parity with Write-JsonResponse). Used by the CSRF + lock-gate middleware.
    private static Task WriteJsonAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.Headers["Cache-Control"] = "no-store";
        return context.Response.WriteAsJsonAsync(body);
    }

    // Reads and parses a request body as JSON. Returns null on an empty body or
    // malformed JSON; the WebAuthn route handlers map null to a bounded 400
    // (oracle parity with Read-RequestJson).
    private static async Task<JsonElement?> ReadJsonBodyAsync(HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            string raw = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    // GET /api/v1/runtime/version — read-only runtime metadata. Values are read
    // verbatim from app\VERSION.json at startup; nothing is recomputed.
    private static object BuildRuntimeVersionPayload(VersionInfo v, int port)
    {
        return new
        {
            cookbookVersion = v.CookbookVersion,
            releaseChannel = v.ReleaseChannel,
            bundledPax = new
            {
                version = v.PaxVersion,
                sha256 = v.PaxSha256,
                relativePath = v.PaxRelativePath,
                integrity = "ok",
            },
            runtime = new
            {
                brokerProcessId = Environment.ProcessId,
                brokerPort = port,
                startedAtUtc = StartedAtUtc.ToString("o"),
                transport = LoopbackTransport,
                bindAddress = LoopbackBindAddress,
            },
            x2 = new
            {
                slice = "V1_OFFICE_GRADE_X2_CORE_READ_ROUTES_AND_WEBVIEW2_SPA",
                note = "Read-only runtime metadata. The extended oracle surface (manifest alignment, host diagnostics, updateReadiness) is deferred to a later slice.",
                deferredSections = new[] { "manifest", "host", "paths", "updateReadiness" },
            },
        };
    }

    // GET /api/v1/setup/acquire-pax/state is served by EngineAcquisition
    // (EngineAcquisitionState.cs), which detects whether an approved managed
    // PAX engine is present and valid. State detection is read-only.

    // Preferred port first, then ascending scan across the frozen range.
    private static int SelectLoopbackPort()
    {
        if (IsPortBindable(PreferredPort))
        {
            return PreferredPort;
        }

        for (int p = PortRangeStart; p <= PortRangeEnd; p++)
        {
            if (p == PreferredPort)
            {
                continue;
            }

            if (IsPortBindable(p))
            {
                return p;
            }
        }

        return 0;
    }

    private static bool IsPortBindable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static string GenerateSessionToken()
    {
        byte[] raw = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(raw)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool IsHealthPath(string path)
    {
        return string.Equals(path, "/api/v1/health", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasValidBearer(HttpRequest request, string expected)
    {
        string auth = request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(auth))
        {
            return false;
        }

        const string prefix = "Bearer ";
        if (!auth.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string provided = auth[prefix.Length..].Trim();
        if (provided.Length == 0)
        {
            return false;
        }

        byte[] providedBytes = Encoding.UTF8.GetBytes(provided);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);

        // Constant-time comparison to avoid token-timing oracles.
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static object BuildHealthPayload(int port, string workspacePath)
    {
        DateTime now = DateTime.UtcNow;
        int uptimeSeconds = (int)Math.Max(0, (now - StartedAtUtc).TotalSeconds);

        return new
        {
            ok = true,
            status = "ok",
            app = AppName,
            runtime = RuntimeKind,
            port,
            session = "active",
            version = AppVersion,
            utc = now.ToString("o"),
            startedAtUtc = StartedAtUtc.ToString("o"),
            uptimeSeconds,
            activeCooks = 0,
            workspaceFolderPath = workspacePath,
            x1 = new
            {
                slice = "V1_OFFICE_GRADE_X1_DOTNET_APP_AND_KESTREL_HEALTH_SKELETON",
                parityOracle = "powershell-broker",
                note = "Native runtime skeleton. Health is a structural-parity stub; the full oracle health surface (recentErrors, brokerSession, packageTrust counters, time-anomaly classification, dbSizeBytes) is deferred to later slices.",
                stubbedFields = new[]
                {
                    "activeCooks",
                    "version",
                    "session",
                    "brokerSession",
                    "recentErrors",
                    "dbSizeBytes",
                    "timeAnomaly"
                }
            }
        };
    }
}

// Read-only snapshot of app\VERSION.json values used by the X2 read routes.
internal sealed record VersionInfo(
    string CookbookVersion,
    string ReleaseChannel,
    string PaxVersion,
    string PaxSha256,
    string PaxRelativePath,
    string PaxAcquisitionPolicy,
    string? EngineManifestUrl,
    string? EngineManifestTrustAnchorThumbprint,
    string ManifestSignaturePolicy);

// Observe-only capture of a double-clicked recipe file handed to the EXE by
// the Windows shell via the .paxlite / .pax associations. Kind is "paxlite"
// or "pax"; Exists reflects an on-disk presence check at startup.
internal readonly record struct FileOpenRequest(
    string Path, string Kind, bool Exists);
