using System.Diagnostics;
using System.Reflection;
using PAXCookbook.Shared;
using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Paths;
using PAXCookbook.Shared.Platform;
using PAXCookbookSetup;
using PAXCookbookSetup.Gui;
using PAXCookbookSetup.Payload;
using PAXCookbookSetup.Shell;
using PAXCookbookSetup.Verbs;

// Double-click with NO arguments -> the GUI installer wizard. Any argument
// (a verb or flag) -> the verb-based CLI used by scripted/silent installs.
// Because this is a WinExe, the CLI path first reattaches stdout/stderr to
// the launching terminal so interactive verb output stays visible.

// Earliest possible guard: PAX Cookbook requires Windows 10 or later. This
// runs before any staging/install work so a pre-Win10 machine is kicked out
// immediately instead of installing into a state the app can never run in.
if (!WindowsVersionGate.IsSupported())
{
    UnsupportedOsNotice.Show();
    return SetupExitCodes.UnsupportedWindowsVersion;
}

if (args.Length == 0)
{
    // GUI wizard: hide the dotnet.exe host console window so no blank terminal
    // flashes (the bootstrapper apphost has no console, but the installed Setup
    // run as `dotnet.exe PAXCookbookSetup.dll` would).
    ConsoleWindowHelper.HideConsoleWindow();
    return WizardLauncher.Run();
}
// FIX 3 / WDAC: the installed Setup runs via the Microsoft-signed dotnet.exe
// host (dotnet.exe "PAXCookbookSetup.dll" <verb>) instead of the former
// wscript.exe + uninstall.vbs hidden launcher. For an interactive uninstall the
// only UI is the GUI confirm/progress dialog, so hide the console window — no
// blank terminal flashes. A scripted/silent run (--quiet/--silent) or a
// CLI-output verb keeps its console attached to the launching terminal.
if (IsGuiSetupInvocation(args))
{
    ConsoleWindowHelper.HideConsoleWindow();
}
else
{
    NativeConsole.AttachToParent();
}
return Run(args);

// True when this Setup invocation is an interactive Add/Remove Programs action
// (uninstall or repair) whose dotnet.exe host console should be hidden so no
// blank terminal flashes. --quiet/--silent (scripted) and CLI-output verbs are
// excluded so their terminal output is preserved.
static bool IsGuiSetupInvocation(string[] a)
{
    foreach (var x in a)
    {
        if (string.Equals(x, "--quiet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(x, "--silent", StringComparison.OrdinalIgnoreCase))
            return false;
    }
    return a.Length > 0
        && (string.Equals(a[0], "uninstall", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a[0], "repair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a[0], "apply-update", StringComparison.OrdinalIgnoreCase))
        && Environment.UserInteractive;
}

static int Run(string[] argv)
{
    var parsed = ArgParser.Parse(argv);

    if (parsed.Verb == "help") return PrintHelp(0);
    if (parsed.Verb == "version") return PrintVersion();

    if (parsed.Errors.Count > 0)
    {
        Console.Error.WriteLine("usage error:");
        foreach (var e in parsed.Errors) Console.Error.WriteLine("  " + e);
        return SetupExitCodes.UsageError;
    }

    var installRoot = parsed.InstallRootOverride ?? AppPaths.InstallRoot();
    var logsDir = Path.Combine(installRoot, "Logs", "Setup");
    using var log = new SetupLogger(logsDir);
    var runningExe = Process.GetCurrentProcess().MainModule?.FileName
                     ?? typeof(Program).Assembly.Location;
    // The handoff decision keys off the MANAGED assembly location, not the
    // process EXE: the installed Setup runs as the signed dotnet.exe host (its
    // MainModule is dotnet.exe, OUTSIDE the install root) with this DLL as its
    // argument. Assembly.Location is <installRoot>\Setup\PAXCookbookSetup.dll for
    // the installed copy, and empty for the self-contained single-file
    // bootstrapper (which is never run from under the install root).
    var runningManaged = typeof(Program).Assembly.Location;
    log.Write("setup-start", fields: new Dictionary<string, object?>
    {
        ["verb"] = parsed.Verb, ["installRoot"] = installRoot,
        ["pid"] = Environment.ProcessId, ["runningExe"] = runningExe,
        ["runningManaged"] = runningManaged,
        ["handoffFromInstalled"] = parsed.HandoffFromInstalled
    });

    try
    {
        switch (parsed.Verb)
        {
            case "status":
                return StatusVerb.Run(installRoot, log, Console.Out, BuildShellOperations());

            // Visible in-place update wizard, launched by the running app's
            // broker ("Update now"). It runs in its OWN detached process and
            // reuses the proven download + install path with visible progress,
            // The visible in-place update wizard, launched by the running app's
            // broker ("Update now"). It MUST go through self-handoff exactly
            // like uninstall: PayloadCopier overwrites the installed Setup\
            // files (PAXCookbook.Shared.dll, the Setup DLL's siblings), which
            // THIS running Setup process loads and therefore LOCKS. Relaunching
            // from a %TEMP% copy (which loads its DLLs from temp) frees those
            // locks so the copy can replace every file in the install root —
            // App\bin AND Setup\ — without "the file is in use" (exit 50).
            case "apply-update":
            case "uninstall":
            case "install":
            case "update":
            case "repair":
                // --dry-run is not implemented for any verb. Refuse
                // immediately here — before SelfHandoff — so we never
                // spawn a child Setup instance whose only job is to
                // refuse the same flag.
                if (parsed.DryRun)
                {
                    log.Write("dryrun-refused", "warn",
                        new Dictionary<string, object?>
                        {
                            ["verb"] = parsed.Verb,
                            ["installRoot"] = installRoot
                        });
                    Console.Error.WriteLine(
                        $"{parsed.Verb}: --dry-run is not yet implemented. Refusing to run.");
                    return SetupExitCodes.UsageError;
                }

                // Interactive uninstall (Add/Remove Programs or the Start Menu
                // shortcut) shows a confirmation + progress GUI. A scripted
                // uninstall (--quiet/--silent), a non-interactive session, a
                // full uninstall (--remove-user-data, an advanced/scripted op),
                // or the test/E2E harness (PAXCOOKBOOK_TEST_NO_SHELL=1) keeps
                // the console CLI. The marker is set BEFORE handoff so
                // BuildHandoffArgs forwards it to the temp child that actually
                // performs (and displays) the uninstall.
                if (parsed.Verb == "uninstall"
                    && !parsed.HandoffFromInstalled
                    && !parsed.GuiUninstall
                    && !parsed.Quiet
                    && !parsed.RemoveUserData
                    && Environment.UserInteractive
                    && !TestShellGate.IsActive())
                {
                    parsed = parsed with { GuiUninstall = true };
                }

                // Live self-handoff: if we are running from the installed
                // Setup path AND the caller did not already pass the
                // handoff markers, copy ourselves to %TEMP% and spawn the
                // temp copy with --handoff-from-installed. Uninstall must
                // also go through handoff because the installed Setup EXE
                // is one of the files being removed (uninstall-contract
                // §3.1 + setup-self-handoff-contract §10).
                if (SelfHandoff.ShouldHandOff(runningManaged, installRoot, parsed.HandoffFromInstalled))
                {
                    var result = HandoffRunner.Run(parsed, runningManaged, installRoot,
                                                   Path.GetTempPath(),
                                                   new RealProcessLauncher(), log);
                    return result.ExitCode;
                }

                // If we ARE the handed-off temp copy, enforce marker validity.
                if (parsed.HandoffFromInstalled)
                {
                    var mv = SelfHandoff.ValidateMarkers(runningManaged, parsed, Path.GetTempPath());
                    if (!mv.Ok)
                    {
                        log.Write("handoff-marker-rejected", "error",
                            fields: new Dictionary<string, object?> { ["detail"] = mv.Error });
                        return SetupExitCodes.HandoffFailed;
                    }
                    log.Write("handoff-marker-accepted");
                }

                int rc;
                if (parsed.Verb == "uninstall")
                {
                    rc = parsed.GuiUninstall
                        ? UninstallGuiHost.Run(installRoot, parsed, log)
                        : UninstallVerb.Run(installRoot, parsed, log, Console.Out);
                }
                else if (parsed.Verb == "apply-update")
                {
                    // Runs from the %TEMP% handoff copy now, so the install-root
                    // Setup\ files are free to be overwritten by the copy below.
                    rc = UpdateGuiHost.Run(installRoot, log);
                }
                else
                {
                    rc = RunPayloadVerb(parsed, installRoot, log);
                }

                // If we ran as the temp handoff copy, schedule cleanup of
                // our own temp folder. Best-effort delete; MoveFileEx
                // fallback when we cannot delete the running EXE.
                if (parsed.HandoffFromInstalled && !string.IsNullOrEmpty(parsed.HandoffFolder))
                {
                    SelfHandoff.CleanupTempFolder(parsed.HandoffFolder!,
                        new Win32DeferredDeleter(), log);
                }
                return rc;

            default:
                Console.Error.WriteLine($"unknown verb: {parsed.Verb}");
                return SetupExitCodes.UsageError;
        }
    }
    catch (Exception ex)
    {
        log.Write("setup-unhandled", "error",
            new Dictionary<string, object?> { ["detail"] = ex.Message });
        Console.Error.WriteLine($"unhandled: {ex.Message}");
        return SetupExitCodes.GenericError;
    }
    finally
    {
        log.Write("setup-end");
    }
}

static int RunPayloadVerb(ParsedArgs parsed, string installRoot, SetupLogger log)
{
    // Phase 11 / Phase 12 — resolve payload root in this order:
    //   1. --payload-root <dir>  (dev loop or external installer).
    //   2. Embedded payload zip resource (single-EXE public artifact).
    //   3. Local payload cache under <installRoot>\PayloadCache
    //      (written by InstallVerb so the installed Setup EXE — which
    //      ships WITHOUT an embedded payload — can repair/update
    //      without the user supplying --payload-root).
    //   4. Fail clearly.
    //
    // Embedded extraction goes to a throwaway temp folder which is
    // deleted in finally. Local cache and directory resolvers point
    // at on-disk locations that we do not own at this layer; we never
    // delete them here.
    string? tempExtractRoot = null;
    string? downloadedZip = null;
    try
    {
        // A CLI `install` mirrors the GUI wizard: when bootstrapping (no
        // --payload-root) it checks for and installs the required prerequisites
        // (.NET 8 Desktop Runtime, PowerShell 7, Python) before installing.
        // --quiet skips this (scripted installs manage prerequisites
        // separately); an explicit --payload-root is a dev/test/scripted path
        // whose caller already manages the environment.
        if (parsed.Verb == "install" && !parsed.Quiet && string.IsNullOrEmpty(parsed.PayloadRoot))
        {
            int prereqRc = CliPrerequisites.Ensure(log);
            if (prereqRc != SetupExitCodes.Ok)
                return prereqRc;
        }

        IPayloadSourceResolver resolver;
        if (!string.IsNullOrEmpty(parsed.PayloadRoot))
        {
            resolver = new DirectoryPayloadSourceResolver(parsed.PayloadRoot!);
        }
        else if (EmbeddedPayloadSourceResolver.HasEmbeddedPayload())
        {
            resolver = new EmbeddedPayloadSourceResolver();
        }
        else
        {
            // Always pull the LATEST payload from the GitHub Release (verified
            // against versions.json) so re-running Setup — install, update, or an
            // Add/Remove Programs repair — refreshes to the newest version
            // instead of reusing a stale local cache. The local cache is kept
            // only as an OFFLINE fallback when the download cannot be performed,
            // so repair still works without a network connection.
            Action<string> dlProgress = parsed.Quiet ? (_ => { }) : ConsoleDownloadProgress;
            var downloader = new PayloadDownloader(log, dlProgress);
            var dl = downloader.DownloadAsync().GetAwaiter().GetResult();
            if (!parsed.Quiet) Console.WriteLine();
            if (dl.Success && !string.IsNullOrEmpty(dl.ZipPath))
            {
                downloadedZip = dl.ZipPath;
                resolver = new DownloadedPayloadSourceResolver(downloadedZip);
            }
            else if (LocalCachePayloadSourceResolver.HasCache(installRoot))
            {
                log.Write("payload-download-fallback-cache", "warn",
                    new Dictionary<string, object?>
                    {
                        ["installRoot"] = installRoot,
                        ["detail"] = dl.Error
                    });
                resolver = new LocalCachePayloadSourceResolver(installRoot);
            }
            else
            {
                Console.Error.WriteLine("unable to download PAX Cookbook: " +
                    (dl.Error ?? "unknown error"));
                log.Write("payload-download-failed", "error",
                    new Dictionary<string, object?>
                    {
                        ["installRoot"] = installRoot,
                        ["detail"] = dl.Error
                    });
                return SetupExitCodes.InstallFailed;
            }
        }

        var src = resolver.Resolve();
        if (!src.Success || string.IsNullOrEmpty(src.PayloadRoot))
        {
            tempExtractRoot = src.TempExtractionRoot;
            Console.Error.WriteLine($"payload resolution failed ({src.Origin}): {src.Error}");
            log.Write("payload-resolve-failed", "error",
                new Dictionary<string, object?> { ["origin"] = src.Origin, ["detail"] = src.Error });
            return SetupExitCodes.InstallFailed;
        }
        if (string.Equals(src.Origin, "embedded", StringComparison.Ordinal) ||
            string.Equals(src.Origin, "downloaded", StringComparison.Ordinal))
            tempExtractRoot = src.TempExtractionRoot;
        var payloadRoot = src.PayloadRoot!;
        log.Write("payload-resolved", fields: new Dictionary<string, object?>
        {
            ["origin"] = src.Origin, ["payloadRoot"] = payloadRoot
        });

        var manifestPath = Path.Combine(payloadRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"manifest.json not found in payload root: {payloadRoot}");
            return SetupExitCodes.InstallFailed;
        }
        Manifest m;
        try { m = ManifestSerializer.Deserialize(File.ReadAllText(manifestPath)); }
        catch (Exception ex)
        {
            log.Write("manifest-parse-failed", "error",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
            return SetupExitCodes.InstallFailed;
        }

        // For embedded extraction, local-cache, and downloaded resolution,
        // verify the staged files match the manifest before we proceed
        // (defence-in-depth on top of the existing ManifestValidator invoked
        // inside each verb). Directory mode is trusted because the caller
        // explicitly pointed us at a payload root.
        if (string.Equals(src.Origin, "embedded", StringComparison.Ordinal) ||
            string.Equals(src.Origin, "local-cache", StringComparison.Ordinal) ||
            string.Equals(src.Origin, "downloaded", StringComparison.Ordinal))
        {
            var v = PayloadManifestVerifier.Verify(payloadRoot, m);
            if (!v.Ok)
            {
                Console.Error.WriteLine($"{src.Origin} payload verification failed:");
                foreach (var e in v.Errors) Console.Error.WriteLine("  " + e);
                log.Write("payload-verify-failed", "error",
                    new Dictionary<string, object?>
                    {
                        ["origin"] = src.Origin,
                        ["errors"] = string.Join("; ", v.Errors)
                    });
                return SetupExitCodes.InstallFailed;
            }
        }

        // Stale-state safety for update / repair: remove any pre-existing
        // installed-skus.json BEFORE running the verb so a stale SHA can never
        // survive. (The install verb handles its own pre-wipe + write internally,
        // because the GUI wizard reaches InstallVerb WITHOUT going through this
        // dispatcher.) It is a generated sidecar at the install root, never part
        // of the payload/manifest.
        if (parsed.Verb == "update" || parsed.Verb == "repair")
        {
            try
            {
                string staleSkus = Path.Combine(installRoot, "installed-skus.json");
                if (File.Exists(staleSkus)) File.Delete(staleSkus);
            }
            catch (Exception ex)
            {
                log.Write("installed-skus-prewipe-failed", "warn",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
            }
        }

        int verbRc = parsed.Verb switch
        {
            "install" => InstallVerb.Run(parsed, m, payloadRoot, installRoot, log,
                                         shellOps: BuildShellOperations(),
                                         progress: parsed.Quiet ? null : Console.WriteLine,
                                         payloadZipPath: downloadedZip),
            "update"  => UpdateVerb.Run(parsed, m, payloadRoot, installRoot, log, shellOps: BuildShellOperations()),
            "repair"  => RepairVerb.Run(parsed, m, payloadRoot, installRoot, log, shellOps: BuildShellOperations()),
            _         => SetupExitCodes.UsageError
        };

        // Record what was installed for update / repair (install records it
        // inside InstallVerb so the GUI wizard path is covered too). Best-effort:
        // a failure never fails an otherwise-good update. The writer logs and
        // self-verifies the install-root file.
        if (verbRc == SetupExitCodes.Ok &&
            (parsed.Verb == "update" || parsed.Verb == "repair"))
        {
            try
            {
                InstalledSkusWriter.Write(installRoot, m.AppVersion, downloadedZip, log);
            }
            catch (Exception ex)
            {
                log.Write("installed-skus-write-failed", "warn",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
            }
        }

        return verbRc;
    }
    finally
    {
        if (!string.IsNullOrEmpty(tempExtractRoot))
        {
            EmbeddedPayloadSourceResolver.TryCleanup(tempExtractRoot);
            DownloadedPayloadSourceResolver.TryCleanup(tempExtractRoot);
            log.Write("payload-temp-cleanup",
                fields: new Dictionary<string, object?>
                {
                    ["path"] = tempExtractRoot,
                    ["deleted"] = !Directory.Exists(tempExtractRoot)
                });
        }
        PayloadDownloader.Cleanup(downloadedZip);
    }
}

// Renders PayloadDownloader progress on a single console line for a CLI
// install (carriage-return overwrite, padded to clear the previous line).
static void ConsoleDownloadProgress(string message)
    => Console.Write("\r" + message.PadRight(64));

// Builds the production ShellOperations using Win32 shortcut + HKCU
// registry writers. Returns a fresh instance each call so per-verb
// state is isolated. Shared with the GUI wizard via ShellOperationsFactory.
static IShellOperations BuildShellOperations() => ShellOperationsFactory.Build();

static int PrintHelp(int code)
{
    Console.WriteLine($"{ProductConstants.SetupExeName} verbs:");
    Console.WriteLine("  install   [--payload-root <dir>] [--install-root <dir>] [--quiet]");
    Console.WriteLine("                                  (without --payload-root: downloads the payload from GitHub and");
    Console.WriteLine("                                   installs prerequisites; --quiet suppresses output + prereqs)");
    Console.WriteLine("  update    [--payload-root <dir>] [--install-root <dir>] [--allow-downgrade]");
    Console.WriteLine("  repair    [--payload-root <dir>] [--install-root <dir>] [--force]");
    Console.WriteLine("                                  (--payload-root optional when Setup ships with an embedded payload)");
    Console.WriteLine("  status    [--install-root <dir>]");
    Console.WriteLine("  uninstall [--install-root <dir>]");
    Console.WriteLine("                                  (interactive: shows a confirmation dialog;");
    Console.WriteLine("                                   add --quiet for a scripted/silent uninstall)");
    Console.WriteLine("                                  (preserves Workspace + Logs by default)");
    Console.WriteLine("  uninstall --remove-user-data --confirm-remove-user-data");
    Console.WriteLine("                                  (full uninstall: also removes Workspace + per-user data)");
    Console.WriteLine("  version");
    Console.WriteLine("  help");
    Console.WriteLine("flags:");
    Console.WriteLine("  --force, --reinstall-same-version, --allow-downgrade,");
    Console.WriteLine("  --handoff-from-installed, --handoff-folder <dir>, --dry-run,");
    Console.WriteLine("  --remove-user-data, --confirm-remove-user-data, --quiet");
    return code;
}

static int PrintVersion()
{
    var v = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    Console.WriteLine($"{ProductConstants.SetupExeName} {v}");
    return 0;
}
