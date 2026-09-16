using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Payload;
using PAXCookbookSetup.Shell;
using PAXCookbookSetup.Uninstall;
using PAXCookbookSetup.Verbs;

namespace PAXCookbookSetup.Gui;

public sealed record WizardInstallResult(bool Success, int ExitCode, string? Error)
{
    public WizardInstallRunner.PayloadCleanupLease? Cleanup { get; init; }
}
public sealed record WizardPreparationResult(
    WizardInstallRunner.PreparedPayload? Payload, int ExitCode, string? Error)
{
    public WizardInstallRunner.PayloadCleanupLease? Cleanup { get; init; }
    public bool Success => Payload is not null;
}

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
        => await RunAsync(
            () => PrepareAsync(installRoot, payloadRootOverride, progress, log, cancel),
            prepared => InstallPrepared(prepared, installRoot, progress, log, shellOps, cancel));

    internal static async Task<WizardInstallResult> RunAsync(
        Func<Task<WizardPreparationResult>> prepare,
        Func<PreparedPayload, WizardInstallResult> install)
    {
        var preparation = await prepare();
        var prepared = preparation.Payload;
        var cleanup = preparation.Cleanup ?? prepared?.Cleanup;
        WizardInstallResult result;
        try
        {
            result = prepared is null
                ? Fail(preparation.ExitCode, preparation.Error!)
                : install(prepared);
        }
        catch
        {
            result = Fail(SetupExitCodes.InstallFailed, "Setup could not finish.");
        }
        try { prepared?.Dispose(); }
        catch when (cleanup?.HasPendingCleanup == true) { }
        if (cleanup?.TryCleanup() == false)
            return Fail(SetupExitCodes.InstallFailed, "Setup cleanup is incomplete. Choose Retry cleanup.")
                with { Cleanup = cleanup };
        return result;
    }

    public static Task<WizardPreparationResult> PrepareAsync(
        string installRoot, string? payloadRootOverride, Action<string> progress,
        SetupLogger log, CancellationToken cancel = default, bool forEarlyExecution = false)
        => PrepareAsync(installRoot, payloadRootOverride, progress, log,
            new PreparationDependencies(
                (stagingRoot, token) => new PayloadDownloader(log, progress, stagingRoot).DownloadAsync(token),
                () => EmbeddedPayloadSourceResolver.HasEmbeddedPayload()), cancel, forEarlyExecution);

    internal sealed record PreparationDependencies(
        Func<string, CancellationToken, Task<PayloadDownloader.DownloadResult>> Download,
        Func<bool> HasEmbeddedPayload);

    internal static Task<WizardPreparationResult> PrepareAsync(
        string installRoot, string? payloadRootOverride, Action<string> progress,
        SetupLogger log, PreparationDependencies dependencies,
        CancellationToken cancel = default, bool forEarlyExecution = false)
        => PreparedPayload.PrepareAsync(installRoot, payloadRootOverride, progress, log,
            dependencies, cancel, forEarlyExecution);

    public static WizardInstallResult InstallPrepared(
        PreparedPayload prepared, string installRoot, Action<string> progress,
        SetupLogger log, IShellOperations shellOps, CancellationToken cancel = default)
        => prepared.Install(installRoot, progress, log, shellOps, cancel, null);

    internal static WizardInstallResult InstallPrepared(
        PreparedPayload prepared, string installRoot, Action<string> progress,
        SetupLogger log, IShellOperations shellOps, IAppStopper appStopper,
        CancellationToken cancel = default)
        => prepared.Install(installRoot, progress, log, shellOps, cancel, appStopper);

    public sealed class PayloadCleanupLease : IDisposable
    {
        private string? _ownedRoot;

        internal PayloadCleanupLease(string ownedRoot) => _ownedRoot = ownedRoot;

        public bool HasPendingCleanup => _ownedRoot is not null;

        public bool TryCleanup()
        {
            if (_ownedRoot is null) return true;
            if (!DownloadedPayloadSourceResolver.TryCleanup(_ownedRoot)) return false;
            _ownedRoot = null;
            return true;
        }

        public void Dispose()
        {
            if (!TryCleanup())
                throw new IOException("Prepared payload cleanup did not finish.");
        }
    }

    public sealed class PreparedPayload : IDisposable
    {
        private readonly Manifest _manifest;
        private readonly PayloadCleanupLease _cleanup;
        private readonly bool _verifyMembers;
        private readonly bool _preparedForEarlyExecution;
        private bool _disposed;
        private bool _invalidated;

        internal PayloadCleanupLease Cleanup => _cleanup;
        public string PayloadRoot { get; }
        public string Origin { get; }
        public string ManifestSha256 { get; }
        public string? DownloadedZipPath { get; }
        public string? DownloadedZipSha256 { get; }
        public ManifestVerifier.PayloadExpectation? Expectation { get; }
        public string? SourceUrl { get; }
        public bool PreparedForEarlyExecution => !_disposed && !_invalidated && _preparedForEarlyExecution;
        public bool IsInvalidated => _invalidated;
        public string TargetArch => _manifest.TargetArch;

        private PreparedPayload(string payloadRoot, string origin, Manifest manifest,
            byte[] manifestBytes, PayloadCleanupLease cleanup, bool verifyMembers,
            PayloadDownloader.DownloadResult? download, string? zipSha256, bool forEarlyExecution)
        {
            PayloadRoot = payloadRoot;
            Origin = origin;
            _manifest = manifest;
            ManifestSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manifestBytes));
            _cleanup = cleanup;
            _verifyMembers = verifyMembers;
            DownloadedZipPath = download?.ZipPath;
            DownloadedZipSha256 = zipSha256;
            Expectation = download?.Expectation;
            SourceUrl = download?.SourceUrl;
            _preparedForEarlyExecution = forEarlyExecution;
        }

        internal static async Task<WizardPreparationResult> PrepareAsync(
            string installRoot, string? payloadRootOverride, Action<string> progress,
            SetupLogger log, PreparationDependencies dependencies,
            CancellationToken cancel, bool forEarlyExecution)
        {
            var ownedRoot = Path.Combine(Path.GetTempPath(), "PAXCookbookPrepared_" + Guid.NewGuid().ToString("N"));
            var cleanup = new PayloadCleanupLease(ownedRoot);
            WizardPreparationResult Fail(int code, string message)
                => new(null, code, message) { Cleanup = cleanup };
            bool transferred = false;
            string? downloadedZipPath = null;
            PayloadDownloader.DownloadResult? download = null;
            try
            {
                cancel.ThrowIfCancellationRequested();
                Directory.CreateDirectory(ownedRoot);
                IPayloadSourceResolver resolver;
                if (!string.IsNullOrEmpty(payloadRootOverride))
                {
                    resolver = new DirectoryPayloadSourceResolver(payloadRootOverride);
                }
                else if (dependencies.HasEmbeddedPayload())
                {
                    progress("Extracting installation files…");
                    resolver = new EmbeddedPayloadSourceResolver(tempBase: ownedRoot);
                }
                else
                {
                    // Always pull the LATEST payload from GitHub so re-running Setup
                    // over an existing install refreshes to the newest version
                    // instead of reusing a stale local cache. The cache is used only
                    // as an offline fallback when the download cannot be performed.
                    var downloadResult = await dependencies.Download(ownedRoot, cancel);
                    cancel.ThrowIfCancellationRequested();

                    if (downloadResult.Success && !string.IsNullOrEmpty(downloadResult.ZipPath))
                    {
                        downloadedZipPath = downloadResult.ZipPath;
                        download = downloadResult;
                        progress("Extracting installation files…");
                        resolver = new DownloadedPayloadSourceResolver(downloadedZipPath, ownedRoot);
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
                        log.Write("wizard-payload-download-failed", "error",
                            new Dictionary<string, object?> { ["detail"] = downloadResult.Error });
                        return Fail(SetupExitCodes.InstallFailed,
                            "PAX Cookbook could not be downloaded. Check your internet connection and try again.");
                    }
                }

                var src = resolver.Resolve();
                cancel.ThrowIfCancellationRequested();
                if (!src.Success || string.IsNullOrEmpty(src.PayloadRoot))
                {
                    log.Write("wizard-payload-prepare-failed", "error",
                        new Dictionary<string, object?> { ["detail"] = src.Error });
                    return Fail(SetupExitCodes.InstallFailed,
                        "The installation files could not be prepared. Download PAX Cookbook Setup again.");
                }

                var payloadRoot = src.PayloadRoot!;
                var manifestPath = Path.Combine(payloadRoot, "manifest.json");
                if (!File.Exists(manifestPath))
                    return Fail(SetupExitCodes.InstallFailed,
                        "The installation files are incomplete or could not be read. Download PAX Cookbook Setup again.");

                Manifest m;
                byte[] manifestBytes;
                try
                {
                    manifestBytes = File.ReadAllBytes(manifestPath);
                    using var reader = new StreamReader(new MemoryStream(manifestBytes));
                    m = ManifestSerializer.Deserialize(reader.ReadToEnd());
                }
                catch (Exception ex)
                {
                    log.Write("wizard-payload-manifest-invalid", "error",
                        new Dictionary<string, object?> { ["detail"] = ex.Message });
                    return Fail(SetupExitCodes.InstallFailed,
                        "The installation files are incomplete or could not be read. Download PAX Cookbook Setup again.");
                }

                bool verifyMembers = forEarlyExecution ||
                    string.Equals(src.Origin, "embedded", StringComparison.Ordinal) ||
                    string.Equals(src.Origin, "local-cache", StringComparison.Ordinal) ||
                    string.Equals(src.Origin, "downloaded", StringComparison.Ordinal);
                var zipSha256 = downloadedZipPath is null ? null : ManifestVerifier.ComputeSha256(downloadedZipPath);
                if (forEarlyExecution &&
                    (string.Equals(src.Origin, "local-cache", StringComparison.Ordinal) ||
                     (string.Equals(src.Origin, "downloaded", StringComparison.Ordinal) &&
                      (string.IsNullOrWhiteSpace(download?.Expectation?.Sha256) ||
                       !string.Equals(zipSha256, download.Expectation.Sha256, StringComparison.OrdinalIgnoreCase)))))
                    return Fail(SetupExitCodes.InstallFailed,
                        "The installation files cannot be used for sign-in testing without verified release information.");

                if (verifyMembers)
                {
                    progress("Verifying download…");
                    var v = PayloadManifestVerifier.Verify(payloadRoot, m);
                    if (!v.Ok)
                    {
                        log.Write("wizard-payload-verification-failed", "error",
                            new Dictionary<string, object?> { ["detail"] = string.Join("; ", v.Errors) });
                        return Fail(SetupExitCodes.InstallFailed,
                            "The installation files could not be checked. Download PAX Cookbook Setup again.");
                    }
                }

                cancel.ThrowIfCancellationRequested();
                var prepared = new PreparedPayload(payloadRoot, src.Origin, m, manifestBytes,
                    cleanup, verifyMembers, download, zipSha256, forEarlyExecution);
                transferred = true;
                return new WizardPreparationResult(prepared, SetupExitCodes.Ok, null) { Cleanup = cleanup };
            }
            catch (OperationCanceledException)
            {
                return Fail(SetupExitCodes.GenericError, "Installation was cancelled.");
            }
            catch (Exception ex)
            {
                log.Write("wizard-install-exception", "error",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
                return Fail(SetupExitCodes.GenericError,
                    "Setup could not finish. Run Setup again. Contact your IT team if the problem continues.");
            }
            finally
            {
                if (!transferred)
                    cleanup.TryCleanup();
            }
        }

        public bool VerifyCurrentArtifact()
        {
            if (_disposed || _invalidated) return false;
            try
            {
                bool valid = string.Equals(
                    ManifestVerifier.ComputeSha256(Path.Combine(PayloadRoot, "manifest.json")),
                    ManifestSha256, StringComparison.OrdinalIgnoreCase) &&
                    (DownloadedZipPath is null || string.Equals(
                        ManifestVerifier.ComputeSha256(DownloadedZipPath), DownloadedZipSha256,
                        StringComparison.OrdinalIgnoreCase)) &&
                    (!_verifyMembers || PayloadManifestVerifier.Verify(PayloadRoot, _manifest).Ok);
                _invalidated = !valid;
                return valid;
            }
            catch
            {
                _invalidated = true;
                return false;
            }
        }

        public string GetValidatedAppExePath()
        {
            if (!PreparedForEarlyExecution || !VerifyCurrentArtifact())
                throw new InvalidDataException("The installation files must be prepared again.");
            try
            {
                string appPath = Path.GetFullPath(Path.Combine(PayloadRoot, _manifest.Payload.AppExe.RelativeInstallPath));
                string appDirectory = Path.GetDirectoryName(appPath)!;
                var declared = _manifest.Payload.Files.Select(member =>
                    Path.GetFullPath(Path.Combine(PayloadRoot, member.RelativeInstallPath)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                declared.Add(appPath);
                string nativeRuntime = _manifest.TargetArch switch
                {
                    "x64" => "runtimes/win-x64/native/msalruntime.dll",
                    "x86" => "runtimes/win-x86/native/msalruntime_x86.dll",
                    "arm64" => "runtimes/win-arm64/native/msalruntime_arm64.dll",
                    _ => throw new InvalidDataException("Unsupported sign-in runtime.")
                };
                string[] required =
                {
                    Path.ChangeExtension(appPath, ".dll"),
                    Path.ChangeExtension(appPath, ".deps.json"),
                    Path.ChangeExtension(appPath, ".runtimeconfig.json"),
                    Path.Combine(appDirectory, "Microsoft.Identity.Client.dll"),
                    Path.Combine(appDirectory, "Microsoft.Identity.Client.Broker.dll"),
                    Path.Combine(appDirectory, "Microsoft.Identity.Client.NativeInterop.dll"),
                    Path.Combine(appDirectory, "Microsoft.IdentityModel.Abstractions.dll"),
                    Path.GetFullPath(Path.Combine(appDirectory, nativeRuntime))
                };
                if (required.Any(path => !declared.Contains(path)) ||
                    Directory.EnumerateFiles(appDirectory, "*", SearchOption.AllDirectories)
                        .Any(path => !declared.Contains(Path.GetFullPath(path))))
                    throw new InvalidDataException("The sign-in runtime is not fully declared.");
                return appPath;
            }
            catch
            {
                _invalidated = true;
                throw;
            }
        }

        internal WizardInstallResult Install(string installRoot, Action<string> progress,
            SetupLogger log, IShellOperations shellOps, CancellationToken cancel, IAppStopper? appStopper)
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (_disposed)
                    return WizardInstallRunner.Fail(SetupExitCodes.InstallFailed, "The installation files have been released. Run Setup again.");
                progress("Verifying download…");
                if (!VerifyCurrentArtifact())
                    return WizardInstallRunner.Fail(SetupExitCodes.InstallFailed,
                        "The installation files changed. Run Setup again.");

                cancel.ThrowIfCancellationRequested();
                progress("Installing files…");
                cancel.ThrowIfCancellationRequested();
                var parsed = new ParsedArgs(
                    Verb: "install", InstallRootOverride: installRoot, PayloadRoot: PayloadRoot,
                    Force: false, ReinstallSameVersion: false, AllowDowngrade: false,
                    HandoffFromInstalled: false, HandoffFolder: null, DryRun: false,
                    RemoveUserData: false, ConfirmRemoveUserData: false, Errors: new List<string>());
                int rc = InstallVerb.Run(parsed, _manifest, PayloadRoot, installRoot, log,
                    shellOps: shellOps, progress: progress, appStopper: appStopper,
                    payloadZipPath: DownloadedZipPath);
                if (rc != SetupExitCodes.Ok)
                    return WizardInstallRunner.Fail(rc,
                        "Setup could not finish. Run Setup again. Contact your IT team if the problem continues.");
                progress("Finishing up…");
                return new WizardInstallResult(true, SetupExitCodes.Ok, null);
            }
            catch (OperationCanceledException)
            {
                return WizardInstallRunner.Fail(SetupExitCodes.GenericError, "Installation was cancelled.");
            }
            catch (Exception ex)
            {
                log.Write("wizard-install-exception", "error",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
                return WizardInstallRunner.Fail(SetupExitCodes.GenericError,
                    "Setup could not finish. Run Setup again. Contact your IT team if the problem continues.");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _cleanup.Dispose();
            _disposed = true;
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
