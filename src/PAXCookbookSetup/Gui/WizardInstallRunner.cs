using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Payload;
using PAXCookbookSetup.Shell;
using PAXCookbookSetup.Verbs;

namespace PAXCookbookSetup.Gui;

public sealed record WizardInstallResult(bool Success, int ExitCode, string? Error);

// Drives a PAX Cookbook install for the GUI wizard, reusing the SAME payload
// resolution + manifest verification + InstallVerb path as the CLI `install`
// verb (Program.RunPayloadVerb). The only differences are (a) it reports
// coarse progress through a callback for the Progress screen, (b) it downloads
// the payload from GitHub if not embedded, and (c) it returns a structured
// result instead of writing to the console.
//
// Prerequisite (PowerShell 7 / Python) installation is intentionally NOT
// performed here — that is wired into the wizard's Progress screen in a
// later slice. This runner only installs the PAX Cookbook application files
// and shell integration.
public static class WizardInstallRunner
{
    // Async entry point — downloads payload if needed, then installs.
    public static async Task<WizardInstallResult> RunAsync(
        string installRoot,
        string? payloadRootOverride,
        Action<string> progress,
        SetupLogger log,
        IShellOperations shellOps,
        CancellationToken cancel = default)
    {
        string? tempExtractRoot = null;
        string? downloadedZipPath = null;
        try
        {
            IPayloadSourceResolver resolver;
            if (!string.IsNullOrEmpty(payloadRootOverride))
            {
                resolver = new DirectoryPayloadSourceResolver(payloadRootOverride);
            }
            else if (EmbeddedPayloadSourceResolver.HasEmbeddedPayload())
            {
                progress("Extracting installation files…");
                resolver = new EmbeddedPayloadSourceResolver();
            }
            else
            {
                // Always pull the LATEST payload from GitHub so re-running Setup
                // over an existing install refreshes to the newest version
                // instead of reusing a stale local cache. The cache is used only
                // as an offline fallback when the download cannot be performed.
                var downloader = new PayloadDownloader(log, progress);
                var downloadResult = await downloader.DownloadAsync(cancel);

                if (downloadResult.Success && !string.IsNullOrEmpty(downloadResult.ZipPath))
                {
                    downloadedZipPath = downloadResult.ZipPath;
                    progress("Extracting installation files…");
                    resolver = new DownloadedPayloadSourceResolver(downloadedZipPath);
                }
                else if (LocalCachePayloadSourceResolver.HasCache(installRoot))
                {
                    log.Write("wizard-payload-download-fallback-cache", "warn",
                        new Dictionary<string, object?> { ["detail"] = downloadResult.Error });
                    progress("Using previously downloaded installation files…");
                    resolver = new LocalCachePayloadSourceResolver(installRoot);
                }
                else
                {
                    return Fail(SetupExitCodes.InstallFailed,
                        downloadResult.Error ?? "Unable to download PAX Cookbook. Please check your internet connection.");
                }
            }

            var src = resolver.Resolve();
            if (!src.Success || string.IsNullOrEmpty(src.PayloadRoot))
            {
                tempExtractRoot = src.TempExtractionRoot;
                return Fail(SetupExitCodes.InstallFailed, $"Could not prepare files: {src.Error}");
            }
            if (string.Equals(src.Origin, "embedded", StringComparison.Ordinal) ||
                string.Equals(src.Origin, "downloaded", StringComparison.Ordinal))
            {
                tempExtractRoot = src.TempExtractionRoot;
            }

            var payloadRoot = src.PayloadRoot!;
            var manifestPath = Path.Combine(payloadRoot, "manifest.json");
            if (!File.Exists(manifestPath))
                return Fail(SetupExitCodes.InstallFailed, "Installation payload is missing manifest.json.");

            Manifest m;
            try { m = ManifestSerializer.Deserialize(File.ReadAllText(manifestPath)); }
            catch (Exception ex) { return Fail(SetupExitCodes.InstallFailed, $"Payload manifest is invalid: {ex.Message}"); }

            if (string.Equals(src.Origin, "embedded", StringComparison.Ordinal) ||
                string.Equals(src.Origin, "local-cache", StringComparison.Ordinal) ||
                string.Equals(src.Origin, "downloaded", StringComparison.Ordinal))
            {
                var v = PayloadManifestVerifier.Verify(payloadRoot, m);
                if (!v.Ok)
                    return Fail(SetupExitCodes.InstallFailed,
                        "Installation payload failed verification: " + string.Join("; ", v.Errors));
            }

            progress("Installing application files…");
            var parsed = new ParsedArgs(
                Verb: "install", InstallRootOverride: installRoot, PayloadRoot: payloadRoot,
                Force: false, ReinstallSameVersion: false, AllowDowngrade: false,
                HandoffFromInstalled: false, HandoffFolder: null, DryRun: false,
                RemoveUserData: false, ConfirmRemoveUserData: false, Errors: new List<string>());

            int rc = InstallVerb.Run(parsed, m, payloadRoot, installRoot, log,
                                     shellOps: shellOps, progress: progress,
                                     payloadZipPath: downloadedZipPath);
            if (rc != SetupExitCodes.Ok)
                return Fail(rc, $"Installation failed (exit code {rc}). See the Setup log for details.");

            progress("Finishing up…");
            return new WizardInstallResult(true, SetupExitCodes.Ok, null);
        }
        catch (OperationCanceledException)
        {
            return Fail(SetupExitCodes.GenericError, "Installation was cancelled.");
        }
        catch (Exception ex)
        {
            log.Write("wizard-install-exception", "error",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
            return Fail(SetupExitCodes.GenericError, ex.Message);
        }
        finally
        {
            // Cleanup extraction temp folder
            if (!string.IsNullOrEmpty(tempExtractRoot))
            {
                EmbeddedPayloadSourceResolver.TryCleanup(tempExtractRoot);
                DownloadedPayloadSourceResolver.TryCleanup(tempExtractRoot);
            }
            // Cleanup downloaded zip
            PayloadDownloader.Cleanup(downloadedZipPath);
        }
    }

    // Sync wrapper for backwards compatibility
    public static WizardInstallResult Run(
        string installRoot,
        string? payloadRootOverride,
        Action<string> progress,
        SetupLogger log,
        IShellOperations shellOps)
    {
        return RunAsync(installRoot, payloadRootOverride, progress, log, shellOps, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static WizardInstallResult Fail(int code, string message)
        => new(false, code, message);
}
