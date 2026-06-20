using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Payload;
using PAXCookbookSetup.Shell;
using PAXCookbookSetup.Uninstall;

namespace PAXCookbookSetup.Verbs;

public static class InstallVerb
{
    // How long to wait for a running PAX Cookbook to close before installing.
    private const int AppStopTimeoutMs = 15_000;

    public static int Run(ParsedArgs args, Manifest m, string payloadRoot,
                          string installRoot, SetupLogger log,
                          IPayloadOperations? payloadOps = null,
                          IShellOperations? shellOps = null,
                          Action<string>? progress = null,
                          IAppStopper? appStopper = null,
                          string? payloadZipPath = null)
    {
        payloadOps ??= DefaultPayloadOperations.Instance;
        appStopper ??= new RealAppStopper();
        log.Write("install-begin", fields: new Dictionary<string, object?>
        {
            ["installRoot"] = installRoot, ["appVersion"] = m.AppVersion
        });

        var validation = ManifestValidator.Validate(m, payloadRoot);
        if (!validation.Ok)
        {
            log.Write("install-manifest-invalid", "error",
                new Dictionary<string, object?> { ["errors"] = string.Join("; ", validation.Errors) });
            return SetupExitCodes.InstallFailed;
        }

        // Reinstall / upgrade support: a running PAX Cookbook from this install
        // (its window, tray icon, and in-process broker — all hosted in one
        // dotnet.exe under WDAC) holds locks on App\bin, which is what makes a
        // naive reinstall fail. Close it first. This is expected behaviour, not
        // an error: a stop that cannot complete never fails the install on its
        // own — the copy step retries and, if a file is still locked, reports
        // exactly which one.
        if (Directory.Exists(installRoot))
        {
            progress?.Invoke("Closing PAX Cookbook\u2026");
            try
            {
                var stop = appStopper.TryStop(installRoot, AppStopTimeoutMs);
                log.Write("install-app-stop", fields: new Dictionary<string, object?>
                {
                    ["invoked"] = stop.Invoked,
                    ["exeFound"] = stop.ExeFound,
                    ["exited"] = stop.Exited,
                    ["exitCode"] = stop.ExitCode,
                    ["detail"] = stop.Detail
                });
            }
            catch (Exception ex)
            {
                log.Write("install-app-stop-error", "warn",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
            }
        }

        // Stale-state safety: remove any pre-existing installed-skus.json (a
        // generated sidecar at the install root, never part of the payload) so a
        // failed install can never leave a stale payload SHA behind. The success
        // path rewrites it at the end with the SHA of the payload just installed.
        try
        {
            string staleSkus = Path.Combine(installRoot, "installed-skus.json");
            if (File.Exists(staleSkus)) { File.Delete(staleSkus); }
        }
        catch (Exception ex)
        {
            log.Write("installed-skus-prewipe-failed", "warn",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
        }

        var appRoot = Path.Combine(installRoot, "App");
        var binRoot = Path.Combine(appRoot, "bin");
        Directory.CreateDirectory(binRoot);
        Directory.CreateDirectory(Path.Combine(installRoot, "Setup"));
        Directory.CreateDirectory(Path.Combine(installRoot, "Logs", "Setup"));
        Directory.CreateDirectory(Path.Combine(installRoot, "Logs", "App"));
        Directory.CreateDirectory(Path.Combine(installRoot, "Logs", "Broker"));
        Directory.CreateDirectory(Path.Combine(installRoot, "WebView2Data"));
        Directory.CreateDirectory(Path.Combine(installRoot, "PreviousVersions"));

        try
        {
            payloadOps.Copy(m, payloadRoot, installRoot, appRoot);
            payloadOps.VerifyInstalled(m, installRoot);
        }
        catch (Exception ex)
        {
            log.Write("install-copy-failed", "error",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
            return SetupExitCodes.InstallFailed;
        }

        // Remove stale binaries from an older version: files under App\bin that
        // are not part of the new payload (old DLLs that would otherwise shadow
        // the new ones). Strictly scoped to App\bin — user data (Workspace,
        // database, Logs, install-state, recipes, bake history) is never
        // touched. Best-effort: a failure here never fails an otherwise-good
        // install, and a stale file that is still locked is simply left.
        try
        {
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                NormalizeRel(m.Payload.AppExe.RelativeInstallPath),
            };
            foreach (var f in m.Payload.Files)
                expected.Add(NormalizeRel(f.RelativeInstallPath));

            var binDirAbs = Path.Combine(appRoot, "bin");
            int staleRemoved = 0;
            if (Directory.Exists(binDirAbs))
            {
                foreach (var file in Directory.EnumerateFiles(
                             binDirAbs, "*", SearchOption.AllDirectories))
                {
                    var rel = NormalizeRel(Path.GetRelativePath(installRoot, file));
                    if (!expected.Contains(rel))
                    {
                        try { File.Delete(file); staleRemoved++; }
                        catch { /* locked stale file left in place; harmless */ }
                    }
                }
            }
            log.Write("install-stale-cleaned",
                fields: new Dictionary<string, object?> { ["removed"] = staleRemoved });
        }
        catch (Exception ex)
        {
            log.Write("install-stale-clean-failed", "warn",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
        }

        // The framework-dependent Setup runtime (Setup\PAXCookbookSetup.dll +
        // its .runtimeconfig.json / .deps.json / PAXCookbook.Shared.dll) ships in
        // the payload and is copied + hash-verified by PayloadCopier above. The
        // unsigned Setup apphost EXE is NO LONGER self-copied into the install
        // tree: Add/Remove Programs, repair, and upgrade all run the DLL through
        // the Microsoft-signed dotnet.exe host, which is WDAC-safe (the same
        // launch model as the app). Running the unsigned EXE was blocked by WDAC,
        // which is why uninstall did nothing on locked-down machines.

        // Phase 12 (Mode B failure repair): write the payload to a
        // verified local cache under <installRoot>\PayloadCache so the
        // installed PAXCookbookSetup.exe (which carries no embedded
        // payload) can still repair/update without --payload-root.
        // Standard uninstall removes PayloadCache; full uninstall takes
        // the whole install root, so no cleanup divergence.
        try
        {
            var cacheRoot = LocalCachePayloadSourceResolver.CachePath(installRoot);
            var normPayload = Path.GetFullPath(payloadRoot);
            var normCache = Path.GetFullPath(cacheRoot);
            if (string.Equals(normPayload, normCache, StringComparison.OrdinalIgnoreCase))
            {
                // Repair/update routed through the cache itself; the
                // cache IS the payload, so just re-verify it in place.
                var v0 = PayloadManifestVerifier.Verify(cacheRoot, m);
                if (!v0.Ok)
                {
                    log.Write("install-payload-cache-verify-failed", "error",
                        new Dictionary<string, object?>
                        {
                            ["origin"] = "self",
                            ["errors"] = string.Join("; ", v0.Errors)
                        });
                    return SetupExitCodes.InstallFailed;
                }
                log.Write("install-payload-cache-skip-self",
                    fields: new Dictionary<string, object?>
                    {
                        ["cacheRoot"] = cacheRoot
                    });
            }
            else
            {
                if (Directory.Exists(cacheRoot))
                    Directory.Delete(cacheRoot, recursive: true);
                Directory.CreateDirectory(cacheRoot);
                payloadOps.WritePayloadCache(payloadRoot, cacheRoot);

                var v = PayloadManifestVerifier.Verify(cacheRoot, m);
                if (!v.Ok)
                {
                    log.Write("install-payload-cache-verify-failed", "error",
                        new Dictionary<string, object?>
                        {
                            ["origin"] = "fresh",
                            ["cacheRoot"] = cacheRoot,
                            ["errors"] = string.Join("; ", v.Errors)
                        });
                    return SetupExitCodes.InstallFailed;
                }
                log.Write("install-payload-cache-written",
                    fields: new Dictionary<string, object?>
                    {
                        ["cacheRoot"] = cacheRoot
                    });
            }
        }
        catch (Exception ex)
        {
            log.Write("install-payload-cache-failed", "error",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
            return SetupExitCodes.InstallFailed;
        }

        // Strip the Mark of the Web (Zone.Identifier alternate data stream) from
        // every installed file. The Setup EXE and payload come from an internet
        // download, so the self-installed Setup copy (File.Copy preserves the
        // ADS) and any MOTW-tainted payload files can carry the internet-zone
        // mark; enterprise security policy then blocks the unsigned, internet-
        // sourced PAX Cookbook.exe on launch. Stripping the whole tree (App +
        // Setup + PayloadCache) before the app is ever launched prevents that
        // block. Best-effort: the Zone.Identifier ADS does not affect file
        // content/SHA, so a failure here never fails the install.
        try
        {
            var stripped = MarkOfTheWeb.StripTree(installRoot);
            log.Write("install-motw-stripped",
                fields: new Dictionary<string, object?> { ["filesVisited"] = stripped });
        }
        catch (Exception ex)
        {
            log.Write("install-motw-strip-failed", "warn",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
        }

        var prior = InstallStateStore.TryLoad(installRoot);
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var state = new InstallState
        {
            AppVersion = m.AppVersion,
            SetupVersion = m.SetupVersion,
            AppExeVersion = m.AppVersion,
            // Preserve the original first-install time across reinstalls/upgrades.
            InstalledAtUtc = !string.IsNullOrEmpty(prior?.InstalledAtUtc) ? prior!.InstalledAtUtc : now,
            UpdatedAtUtc = now,
            InstallRoot = installRoot,
            AppRoot = appRoot,
            BinRoot = binRoot,
            AppExe = Path.Combine(installRoot, m.Payload.AppExe.RelativeInstallPath),
            // Preserve the user's recorded workspace location across upgrades so
            // their recipes and bake history continue to resolve.
            WorkspaceFolderPath = prior?.WorkspaceFolderPath ?? "",
            WebView2RuntimeStatus = new WebView2RuntimeStatus { DetectedAtUtc = now },
            WebView2UserDataFolder = Path.Combine(installRoot, "WebView2Data"),
            LastOperation = new LastOperation
            {
                Kind = "install", Status = "ok", At = now, ExitCode = 0
            }
        };
        InstallStateStore.Save(installRoot, state);

        // Phase 8: Windows shell identity (shortcuts + protocol + ARP).
        if (shellOps is not null)
        {
            try
            {
                var sr = shellOps.Install(installRoot, m.AppVersion,
                                          createDesktopShortcut: false);
                log.Write("install-shell-registered",
                    fields: new Dictionary<string, object?>
                    {
                        ["shortcutsCreated"] = sr.ShortcutsCreated,
                        ["protocolRegistered"] = sr.ProtocolRegistered,
                        ["uninstallRegistered"] = sr.UninstallRegistered,
                        ["fileAssociationsRegistered"] = sr.FileAssociationsRegistered,
                        ["autoStartRegistered"] = sr.AutoStartRegistered
                    });
            }
            catch (Exception ex)
            {
                // Non-fatal: file payload is installed; shell decoration
                // failure is logged and reported by status verb.
                log.Write("install-shell-failed", "warn",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
            }
        }

        // Record what was installed (payload SHA + app version) for the in-app
        // self-updater. This is the LAST write into the install tree, so the SHA
        // it records reflects the payload that was just copied. It lives HERE
        // (the shared GUI-wizard + CLI install chokepoint), not only in the CLI
        // dispatcher, so a normal double-click install produces the file too.
        // Best-effort: a failure never fails an otherwise-good install.
        try
        {
            InstalledSkusWriter.Write(installRoot, m.AppVersion, payloadZipPath, log);
        }
        catch (Exception ex)
        {
            log.Write("installed-skus-write-failed", "warn",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
        }

        log.Write("install-complete", fields: new Dictionary<string, object?>
        {
            ["appVersion"] = m.AppVersion
        });
        return SetupExitCodes.Ok;
    }

    // Normalize an install-relative path for case-insensitive comparison against
    // the manifest (which records paths with backslashes).
    private static string NormalizeRel(string rel)
        => rel.Replace('/', '\\').TrimStart('\\');
}

public interface IPayloadOperations
{
    void Copy(Manifest m, string payloadRoot, string installRoot, string appRoot);
    void VerifyInstalled(Manifest m, string installRoot);
    void WritePayloadCache(string payloadRoot, string cacheRoot);
}

public sealed class DefaultPayloadOperations : IPayloadOperations
{
    public static readonly DefaultPayloadOperations Instance = new();
    public void Copy(Manifest m, string payloadRoot, string installRoot, string appRoot)
        => PayloadCopier.Copy(m, payloadRoot, installRoot, appRoot);
    public void VerifyInstalled(Manifest m, string installRoot)
        => PayloadCopier.VerifyInstalled(m, installRoot);
    public void WritePayloadCache(string payloadRoot, string cacheRoot)
        => PayloadCacheWriter.MirrorTree(payloadRoot, cacheRoot);
}
