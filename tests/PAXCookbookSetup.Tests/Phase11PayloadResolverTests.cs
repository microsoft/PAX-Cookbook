using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Gui;
using PAXCookbookSetup.Payload;
using PAXCookbookSetup.Shell;
using PAXCookbookSetup.Uninstall;
using Xunit;
using Xunit.Abstractions;

namespace PAXCookbookSetup.Tests;

// Phase 11 — single-EXE setup payload bundle. Unit tests for the
// payload source resolver layer that lets PAXCookbookSetup.exe install
// without an external `--payload-root` directory.
public class Phase11PayloadResolverTests
{
    private readonly ITestOutputHelper _output;

    public Phase11PayloadResolverTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedPayload_RunAsync_RetainsCleanupUntilRetry(bool preparationSucceeds)
    {
        var observations = new List<object>();
        foreach (bool locked in new[] { false, true })
        {
            string root = Path.Combine(Path.GetTempPath(), "p11-wrapper-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using var log = new SetupLogger(Path.Combine(root, "Logs"));
            FileStream? held = null;
            WizardInstallResult? result = null;
            try
            {
                byte[] zip = PreparedZip();
                var preparation = await WizardInstallRunner.PrepareAsync(Path.Combine(root, "install"), null, _ => { }, log,
                    new WizardInstallRunner.PreparationDependencies((owned, _) => Task.FromResult(DeliverZip(owned, zip,
                        new ManifestVerifier.PayloadExpectation(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)), zip.Length, "1.0.0"))),
                        () => false), forEarlyExecution: true);
                Assert.True(preparation.Success);
                string payloadRoot = preparation.Payload!.PayloadRoot;
                if (locked) held = File.Open(Path.Combine(payloadRoot, "App/bin/PAXCookbook.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!preparationSucceeds) preparation = preparation with { Payload = null, ExitCode = SetupExitCodes.InstallFailed, Error = "Synthetic failure" };
                int installs = 0;
                result = await WizardInstallRunner.RunAsync(() => Task.FromResult(preparation), _ =>
                {
                    installs++;
                    return new WizardInstallResult(true, SetupExitCodes.Ok, null);
                });
                bool Retained(bool success, bool pending, bool exists) => !success && pending && exists;
                bool positiveControl = Retained(false, true, true);
                bool negativeControl = Retained(true, false, false);
                bool retained = Retained(result.Success, result.Cleanup?.HasPendingCleanup == true, Directory.Exists(payloadRoot));
                bool retryWhileHeld = result.Cleanup?.TryCleanup() ?? true;
                held?.Dispose();
                held = null;
                bool retryAfterRelease = result.Cleanup?.TryCleanup() ?? true;
                bool remainsAfterProductCleanup = Directory.Exists(payloadRoot);
                observations.Add(new { locked, preparationSucceeds, positiveControl, negativeControl, retained,
                    result.Success, result.ExitCode, installs, retryWhileHeld, retryAfterRelease, remainsAfterProductCleanup });
                Assert.True(positiveControl);
                Assert.False(negativeControl);
                Assert.Equal(locked, retained);
                Assert.Equal(preparationSucceeds && !locked, result.Success);
                Assert.Equal(preparationSucceeds ? 1 : 0, installs);
                Assert.Equal(!locked, retryWhileHeld);
                Assert.True(retryAfterRelease);
                Assert.False(remainsAfterProductCleanup);
            }
            finally
            {
                held?.Dispose();
                result?.Cleanup?.TryCleanup();
                TryRm(root);
            }
        }
        _output.WriteLine("WRAPPER_CLEANUP_EVIDENCE=" + System.Text.Json.JsonSerializer.Serialize(new
        {
            universe = "RunAsync with synthetic verified payload and fake install; unlocked and locked payload members",
            predicate = "!success && pendingCleanup && ownedDirectoryExists", observations
        }));
    }

    // ---------- DirectoryPayloadSourceResolver ----------

    [Fact]
    public void Directory_Empty_Fails()
    {
        var r = new DirectoryPayloadSourceResolver("").Resolve();
        Assert.False(r.Success);
        Assert.Equal("directory", r.Origin);
    }

    [Fact]
    public void Directory_Missing_Fails()
    {
        var r = new DirectoryPayloadSourceResolver(
            Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"))).Resolve();
        Assert.False(r.Success);
    }

    [Fact]
    public void Directory_NoManifest_Fails()
    {
        var d = Path.Combine(Path.GetTempPath(), "p11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        try
        {
            var r = new DirectoryPayloadSourceResolver(d).Resolve();
            Assert.False(r.Success);
            Assert.Contains("manifest.json", r.Error ?? "");
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Directory_WithManifest_Succeeds()
    {
        var d = Path.Combine(Path.GetTempPath(), "p11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "manifest.json"), "{}");
        try
        {
            var r = new DirectoryPayloadSourceResolver(d).Resolve();
            Assert.True(r.Success);
            Assert.Equal("directory", r.Origin);
            Assert.Equal(Path.GetFullPath(d), r.PayloadRoot);
        }
        finally { Directory.Delete(d, true); }
    }

    // ---------- EmbeddedPayloadSourceResolver ----------

    [Fact]
    public void Embedded_NullStream_Fails()
    {
        var r = new EmbeddedPayloadSourceResolver(() => null).Resolve();
        Assert.False(r.Success);
        Assert.Equal("embedded", r.Origin);
    }

    [Fact]
    public void Embedded_HappyPath_Extracts_ManifestPresent()
    {
        var zip = BuildZip(entries: new[]
        {
            ("manifest.json", "{\"product\":\"PAXCookbook\"}"),
            ("App/bin/PAXCookbook.exe", "fake-app-bytes"),
            ("PAXCookbookSetup.exe", "fake-setup-bytes"),
        });
        var tempBase = NewTempBase();
        try
        {
            var r = new EmbeddedPayloadSourceResolver(() => new MemoryStream(zip), tempBase).Resolve();
            Assert.True(r.Success, r.Error);
            Assert.Equal("embedded", r.Origin);
            Assert.True(Directory.Exists(r.PayloadRoot));
            Assert.True(File.Exists(Path.Combine(r.PayloadRoot!, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(r.PayloadRoot!, "App", "bin", "PAXCookbook.exe")));
            Assert.True(File.Exists(Path.Combine(r.PayloadRoot!, "PAXCookbookSetup.exe")));
            // Cleanup
            Assert.True(EmbeddedPayloadSourceResolver.TryCleanup(r.TempExtractionRoot));
            Assert.False(Directory.Exists(r.PayloadRoot));
        }
        finally { TryRm(tempBase); }
    }

    [Fact]
    public void Embedded_RejectsParentTraversal()
    {
        var zip = BuildZip(new[]
        {
            ("manifest.json", "{}"),
            ("../escape.txt",  "nope"),
        });
        var tempBase = NewTempBase();
        try
        {
            var r = new EmbeddedPayloadSourceResolver(() => new MemoryStream(zip), tempBase).Resolve();
            Assert.False(r.Success);
            Assert.Contains("..", r.Error ?? "");
            EmbeddedPayloadSourceResolver.TryCleanup(r.TempExtractionRoot);
        }
        finally { TryRm(tempBase); }
    }

    [Fact]
    public void Embedded_RejectsAbsolutePath()
    {
        // Cannot easily build a ZipArchive entry with a rooted name via
        // ZipArchive.CreateEntry (it accepts the name as-is). Use an
        // OS-style absolute path which Path.IsPathRooted will catch.
        var zip = BuildZip(new[]
        {
            ("manifest.json", "{}"),
            (@"C:\evil.txt",  "nope"),
        });
        var tempBase = NewTempBase();
        try
        {
            var r = new EmbeddedPayloadSourceResolver(() => new MemoryStream(zip), tempBase).Resolve();
            Assert.False(r.Success);
            Assert.True((r.Error ?? "").Contains("absolute") || (r.Error ?? "").Contains("drive"));
            EmbeddedPayloadSourceResolver.TryCleanup(r.TempExtractionRoot);
        }
        finally { TryRm(tempBase); }
    }

    [Fact]
    public void Embedded_MissingManifest_Fails()
    {
        var zip = BuildZip(new[] { ("just-a-file.txt", "hi") });
        var tempBase = NewTempBase();
        try
        {
            var r = new EmbeddedPayloadSourceResolver(() => new MemoryStream(zip), tempBase).Resolve();
            Assert.False(r.Success);
            Assert.Contains("manifest.json", r.Error ?? "");
            EmbeddedPayloadSourceResolver.TryCleanup(r.TempExtractionRoot);
        }
        finally { TryRm(tempBase); }
    }

    [Fact]
    public void TraversalCheck_Helpers()
    {
        Assert.NotNull(EmbeddedPayloadSourceResolver.TraversalCheck(@"..\evil.txt"));
        Assert.NotNull(EmbeddedPayloadSourceResolver.TraversalCheck("../evil.txt"));
        Assert.NotNull(EmbeddedPayloadSourceResolver.TraversalCheck("a/b/../c"));
        Assert.NotNull(EmbeddedPayloadSourceResolver.TraversalCheck(@"C:\foo.txt"));
        Assert.NotNull(EmbeddedPayloadSourceResolver.TraversalCheck(@"\\server\share"));
        Assert.Null(EmbeddedPayloadSourceResolver.TraversalCheck("manifest.json"));
        Assert.Null(EmbeddedPayloadSourceResolver.TraversalCheck("App/bin/PAXCookbook.exe"));
        Assert.Null(EmbeddedPayloadSourceResolver.TraversalCheck(@"Setup\dep.dll"));
    }

    // ---------- PayloadManifestVerifier ----------

    [Fact]
    public void Verifier_DetectsMissing_HashMismatch_SizeMismatch()
    {
        var d = Path.Combine(Path.GetTempPath(), "p11v-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(d, "App", "bin"));
        Directory.CreateDirectory(Path.Combine(d, "Setup"));
        try
        {
            var appExeBytes = Encoding.UTF8.GetBytes("hello-app");
            File.WriteAllBytes(Path.Combine(d, "App", "bin", "PAXCookbook.exe"), appExeBytes);
            var setupBytes = Encoding.UTF8.GetBytes("hello-setup");
            File.WriteAllBytes(Path.Combine(d, "PAXCookbookSetup.exe"), setupBytes);
            var ok = new Manifest
            {
                Payload = new ManifestPayload
                {
                    AppExe = new ManifestAppExe
                    {
                        RelativeInstallPath = "App\\bin\\PAXCookbook.exe",
                        Sha256 = Sha256Hex(appExeBytes),
                        SizeBytes = appExeBytes.Length
                    },
                    SetupExe = new ManifestSetupExe
                    {
                        Name = "PAXCookbookSetup.exe",
                        Sha256 = Sha256Hex(setupBytes),
                        SizeBytes = setupBytes.Length
                    }
                }
            };
            var v = PayloadManifestVerifier.Verify(d, ok);
            Assert.True(v.Ok, string.Join("; ", v.Errors));

            // Bad hash:
            var bad = ok with { Payload = ok.Payload with { AppExe = ok.Payload.AppExe with { Sha256 = new string('0',64) } } };
            var v2 = PayloadManifestVerifier.Verify(d, bad);
            Assert.False(v2.Ok);
            Assert.Contains(v2.Errors, e => e.Contains("sha256"));

            // Bad size:
            var bad2 = ok with { Payload = ok.Payload with { AppExe = ok.Payload.AppExe with { SizeBytes = 999999 } } };
            var v3 = PayloadManifestVerifier.Verify(d, bad2);
            Assert.False(v3.Ok);
            Assert.Contains(v3.Errors, e => e.Contains("size"));

            // Missing required file: the App EXE is always required. (The Setup
            // EXE is metadata only and is not shipped in the payload, so deleting
            // it would NOT be an error in the bootstrapper model.)
            File.Delete(Path.Combine(d, "App", "bin", "PAXCookbook.exe"));
            var v4 = PayloadManifestVerifier.Verify(d, ok);
            Assert.False(v4.Ok);
            Assert.Contains(v4.Errors, e => e.Contains("missing"));
        }
        finally { TryRm(d); }
    }

    [Fact]
    public async System.Threading.Tasks.Task PreparedPayload_DirectoryEarlyVerification_Controls()
    {
        var source = NewTempBase();
        try
        {
            var appBytes = Encoding.UTF8.GetBytes("synthetic app, never executable");
            var manifest = new Manifest
            {
                Payload = new ManifestPayload
                {
                    AppExe = new ManifestAppExe
                    {
                        RelativeInstallPath = "App/bin/PAXCookbook.exe",
                        Sha256 = Sha256Hex(appBytes), SizeBytes = appBytes.Length
                    }
                }
            };
            var appPath = Path.Combine(source, "App", "bin", "PAXCookbook.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(appPath)!);
            File.WriteAllBytes(appPath, appBytes);
            File.WriteAllText(Path.Combine(source, "manifest.json"), ManifestSerializer.Serialize(manifest));
            using var log = new SetupLogger(Path.Combine(source, "logs"));
            var installRoot = Path.Combine(source, "not-installed");
            var positive = await WizardInstallRunner.PrepareAsync(installRoot, source,
                _ => { }, log, forEarlyExecution: true);
            Assert.True(positive.Success, positive.Error);
            Assert.Equal(source, positive.Payload!.PayloadRoot);
            Assert.True(positive.Payload.PreparedForEarlyExecution);
            positive.Payload.Dispose();
            Assert.True(File.Exists(appPath));
            Assert.False(Directory.Exists(installRoot));
            File.WriteAllText(appPath, "tampered");
            var negative = await WizardInstallRunner.PrepareAsync(installRoot, source,
                _ => { }, log, forEarlyExecution: true);
            Assert.False(negative.Success);
            Assert.Null(negative.Payload);
            Assert.False(Directory.Exists(installRoot));
            var legacy = await WizardInstallRunner.PrepareAsync(installRoot, source, _ => { }, log);
            Assert.True(legacy.Success, legacy.Error);
            using var legacyOwner = legacy.Payload!;
            Assert.False(legacyOwner.PreparedForEarlyExecution);
            _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = "directory early-execution preparation using synthetic manifest and app bytes",
                predicate = "PrepareAsync.Success", positiveControl = positive.Success,
                negativeControl = negative.Success, sourcePreserved = File.Exists(appPath),
                installRootCreated = Directory.Exists(installRoot), legacyPreparation = legacy.Success
            }));
        }
        finally { TryRm(source); }
    }

    [Theory]
    [InlineData("app")]
    [InlineData("member")]
    [InlineData("manifest")]
    [InlineData("zip")]
    public async Task PreparedPayload_DeliveredZip_TamperPermanentlyRefuses_ThenFreshOwnerInstalls(string changedPart)
    {
        var testRoot = NewTempBase();
        WizardInstallRunner.PreparedPayload? prepared = null;
        try
        {
            using var log = new SetupLogger(Path.Combine(testRoot, "logs"));
            var installRoot = Path.Combine(testRoot, "fresh-install");
            var zip = PreparedZip();
            var expectation = new ManifestVerifier.PayloadExpectation(Sha256Hex(zip), zip.Length, "1.0.0");
            int downloads = 0;
            var dependencies = new WizardInstallRunner.PreparationDependencies((stage, _) =>
            {
                downloads++;
                return Task.FromResult(DeliverZip(stage, zip, expectation));
            }, () => false);
            var preparation = await WizardInstallRunner.PrepareAsync(installRoot, null, _ => { },
                log, dependencies, forEarlyExecution: true);
            Assert.True(preparation.Success, preparation.Error);
            prepared = preparation.Payload!;
            Assert.Equal("downloaded", prepared.Origin);
            Assert.Same(expectation, prepared.Expectation);
            Assert.Equal("https://example.invalid/synthetic-payload.zip", prepared.SourceUrl);
            Assert.True(prepared.PreparedForEarlyExecution);
            Assert.Equal(expectation.Sha256, prepared.DownloadedZipSha256, ignoreCase: true);
            Assert.False(Directory.Exists(installRoot));
            Assert.Equal(SyntheticApp, File.ReadAllText(Path.Combine(prepared.PayloadRoot, "App/bin/PAXCookbook.exe")));
            var sourceRoot = prepared.PayloadRoot;
            var changedPath = changedPart switch
            {
                "app" => Path.Combine(sourceRoot, "App/bin/PAXCookbook.exe"),
                "member" => Path.Combine(sourceRoot, "App/bin/member.txt"),
                "manifest" => Path.Combine(sourceRoot, "manifest.json"),
                _ => prepared.DownloadedZipPath!
            };
            var originalBytes = File.ReadAllBytes(changedPath);
            var changedBytes = (byte[])originalBytes.Clone();
            changedBytes[changedBytes.Length - 1] ^= 1;
            File.WriteAllBytes(changedPath, changedBytes);
            var shell = new PreparedShell();
            var stopper = new RecordingAppStopper();
            var negative = WizardInstallRunner.InstallPrepared(prepared, installRoot, _ => { }, log, shell, stopper);
            Assert.False(negative.Success);
            Assert.Empty(shell.InstallRoots);
            Assert.Empty(stopper.Calls);
            Assert.False(Directory.Exists(installRoot));
            File.WriteAllBytes(changedPath, originalBytes);
            Assert.False(WizardInstallRunner.InstallPrepared(prepared, installRoot, _ => { }, log, shell, stopper).Success);
            Assert.True(prepared.IsInvalidated);
            Assert.False(prepared.PreparedForEarlyExecution);
            Assert.Equal(1, downloads);
            prepared.Dispose();
            var fresh = await WizardInstallRunner.PrepareAsync(installRoot, null, _ => { },
                log, dependencies, forEarlyExecution: true);
            Assert.True(fresh.Success, fresh.Error);
            prepared = fresh.Payload!;
            Assert.NotEqual(sourceRoot, prepared.PayloadRoot);
            sourceRoot = prepared.PayloadRoot;
            var positive = WizardInstallRunner.InstallPrepared(prepared, installRoot, _ => { }, log, shell, stopper);
            Assert.True(positive.Success, positive.Error);
            Assert.Single(shell.InstallRoots);
            Assert.Equal(SyntheticApp, File.ReadAllText(Path.Combine(installRoot, "App/bin/PAXCookbook.exe")));
            Assert.Equal(SyntheticMember, File.ReadAllText(Path.Combine(installRoot, "App/bin/member.txt")));
            Assert.Equal(sourceRoot, prepared.PayloadRoot);
            Assert.True(Directory.Exists(sourceRoot));
            Assert.True(File.Exists(prepared.DownloadedZipPath));
            Assert.Equal(2, downloads);
            var borrowedAgain = WizardInstallRunner.InstallPrepared(prepared, installRoot, _ => { }, log, shell, stopper);
            Assert.True(borrowedAgain.Success, borrowedAgain.Error);
            Assert.Single(stopper.Calls);
            Assert.Equal(2, downloads);
            _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = new { origin = prepared.Origin, changedPart, sourceRoot, installRoot },
                predicate = "InstallPrepared.Success", negativeControl = negative.Success,
                positiveControl = positive.Success, borrowedAgain = borrowedAgain.Success,
                downloads, shellCalls = shell.InstallRoots.Count, stopperCalls = stopper.Calls.Count,
                sourceStillOwned = Directory.Exists(sourceRoot)
            }));
        }
        finally { prepared?.Dispose(); TryRm(testRoot); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("size-only")]
    [InlineData("wrong-sha")]
    public async Task PreparedPayload_EarlyDownload_RequiresActualReleaseSha(string missingInformation)
    {
        var testRoot = NewTempBase();
        try
        {
            using var log = new SetupLogger(Path.Combine(testRoot, "logs"));
            var zip = PreparedZip();
            var actual = new ManifestVerifier.PayloadExpectation(Sha256Hex(zip), zip.Length, "1.0.0");
            var unavailable = missingInformation switch
            {
                "missing" => null,
                "size-only" => actual with { Sha256 = null },
                _ => actual with { Sha256 = new string('0', 64) }
            };
            async Task<WizardPreparationResult> Prepare(ManifestVerifier.PayloadExpectation? expectation, bool early)
                => await WizardInstallRunner.PrepareAsync(Path.Combine(testRoot, "absent"), null, _ => { }, log,
                    new WizardInstallRunner.PreparationDependencies(
                        (stage, _) => Task.FromResult(DeliverZip(stage, zip, expectation)), () => false),
                    forEarlyExecution: early);
            var positive = await Prepare(actual, true);
            Assert.True(positive.Success, positive.Error);
            using var positiveOwner = positive.Payload!;
            Assert.Same(actual, positiveOwner.Expectation);
            var negative = await Prepare(unavailable, true);
            Assert.False(negative.Success);
            Assert.Null(negative.Payload);
            var legacy = await Prepare(null, false);
            Assert.True(legacy.Success, legacy.Error);
            using var legacyOwner = legacy.Payload!;
            Assert.Null(legacyOwner.Expectation);
            Assert.False(legacyOwner.PreparedForEarlyExecution);
            Assert.NotEqual(positiveOwner.PayloadRoot, legacyOwner.PayloadRoot);
            Assert.NotEqual(positiveOwner.DownloadedZipPath, legacyOwner.DownloadedZipPath);
            var legacyInstall = WizardInstallRunner.InstallPrepared(legacyOwner, Path.Combine(testRoot, "legacy-install"),
                _ => { }, log, new PreparedShell(), new RecordingAppStopper());
            Assert.True(legacyInstall.Success, legacyInstall.Error);
            legacyOwner.Dispose();
            Assert.True(File.Exists(positiveOwner.DownloadedZipPath));
            Assert.True(Directory.Exists(positiveOwner.PayloadRoot));
            _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = new { origin = "downloaded", missingInformation },
                predicate = "PrepareAsync(forEarlyExecution: true).Success",
                positiveControl = positive.Success, negativeControl = negative.Success,
                legacyOptionalExpectation = legacy.Success, legacyInstall = legacyInstall.Success,
                legacyEarlyExecution = legacyOwner.PreparedForEarlyExecution,
                separateOwnerPreserved = Directory.Exists(positiveOwner.PayloadRoot)
            }));
        }
        finally { TryRm(testRoot); }
    }

    [Theory]
    [InlineData("download-failure")]
    [InlineData("cancel-return")]
    [InlineData("cancel-throw")]
    [InlineData("cancel-after-resolve")]
    [InlineData("traversal")]
    [InlineData("missing-manifest")]
    [InlineData("bad-member")]
    public async Task PreparedPayload_FailureCleanup_IsOwnedOnly(string failure)
    {
        var testRoot = NewTempBase();
        try
        {
            using var log = new SetupLogger(Path.Combine(testRoot, "logs"));
            var zip = PreparedZip();
            var externalZip = Path.Combine(testRoot, "external.zip");
            File.WriteAllBytes(externalZip, zip);
            var cacheInstall = Path.Combine(testRoot, "existing");
            var cache = LocalCachePayloadSourceResolver.CachePath(cacheInstall);
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "manifest.json"), "external cache sentinel");
            var source = Path.Combine(testRoot, "custom-source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "neighbor.txt"), "preserve");
            var observations = new List<object>();
            foreach (bool injectFailure in new[] { false, true })
            {
                using var cancel = new CancellationTokenSource();
                string? ownedRoot = null;
                bool ownedRootExisted = false;
                int downloads = 0;
                var dependencies = new WizardInstallRunner.PreparationDependencies((stage, _) =>
                {
                    ownedRoot = stage;
                    ownedRootExisted = Directory.Exists(stage);
                    downloads++;
                    File.WriteAllText(Path.Combine(stage, "partial.download"), "partial");
                    if (injectFailure && failure is "cancel-return" or "cancel-throw")
                    {
                        cancel.Cancel();
                        if (failure == "cancel-throw") throw new OperationCanceledException(cancel.Token);
                    }
                    if (injectFailure && failure is "download-failure" or "cancel-return")
                        return Task.FromResult(new PayloadDownloader.DownloadResult(false, null, "synthetic failure"));
                    if (injectFailure && failure == "traversal")
                        return Task.FromResult(DeliverZip(stage, BuildZip(new[]
                        {
                            ("partial.txt", "extracted before refusal"), ("../escape.txt", "refuse")
                        }), null));
                    if (injectFailure && failure == "missing-manifest")
                        return Task.FromResult(DeliverZip(stage, BuildZip(new[] { ("partial.txt", "no manifest") }), null));
                    if (injectFailure && failure == "bad-member")
                        return Task.FromResult(DeliverZip(stage, PreparedZip("changed"), null));
                    return Task.FromResult(new PayloadDownloader.DownloadResult(true, externalZip, null));
                }, () => false);
                Action<string> progress = text =>
                {
                    if (injectFailure && failure == "cancel-after-resolve" && text.StartsWith("Verifying", StringComparison.Ordinal))
                        cancel.Cancel();
                };
                var preparation = await WizardInstallRunner.PrepareAsync(
                    injectFailure && failure.StartsWith("cancel", StringComparison.Ordinal) ? cacheInstall : Path.Combine(testRoot, "absent"),
                    null, progress, log, dependencies, cancel.Token);
                Assert.Equal(!injectFailure, preparation.Success);
                if (injectFailure && failure.StartsWith("cancel", StringComparison.Ordinal))
                {
                    Assert.Equal(SetupExitCodes.GenericError, preparation.ExitCode);
                    Assert.Equal("Installation was cancelled.", preparation.Error);
                }
                Assert.NotNull(ownedRoot);
                Assert.True(ownedRootExisted);
                if (!injectFailure)
                {
                    Assert.StartsWith(ownedRoot!, preparation.Payload!.PayloadRoot, StringComparison.OrdinalIgnoreCase);
                    Assert.True(Directory.Exists(ownedRoot));
                    preparation.Payload.Dispose();
                }
                Assert.False(Directory.Exists(ownedRoot));
                Assert.Equal(zip, File.ReadAllBytes(externalZip));
                Assert.Equal("preserve", File.ReadAllText(Path.Combine(source, "neighbor.txt")));
                Assert.Equal("external cache sentinel", File.ReadAllText(Path.Combine(cache, "manifest.json")));
                Assert.Equal(1, downloads);
                observations.Add(new
                {
                    injectFailure, preparation.Success, preparation.ExitCode, ownedRoot, ownedRootExisted,
                    ownedRootRemains = Directory.Exists(ownedRoot), downloads,
                    externalSourcePreserved = File.ReadAllBytes(externalZip).AsSpan().SequenceEqual(zip),
                    cachePreserved = File.ReadAllText(Path.Combine(cache, "manifest.json")) == "external cache sentinel"
                });
            }
            _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = new { failure, externalZip, source, cache },
                predicate = "PrepareAsync.Success and Directory.Exists(ownedRoot) before/after cleanup",
                controls = observations
            }));
        }
        finally { TryRm(testRoot); }
    }

    [Fact]
    public async Task PreparedPayload_DisposedOwner_RefusesReuse()
    {
        var testRoot = NewTempBase();
        WizardInstallRunner.PreparedPayload? prepared = null;
        try
        {
            using var log = new SetupLogger(Path.Combine(testRoot, "logs"));
            var zip = PreparedZip();
            var preparation = await WizardInstallRunner.PrepareAsync(Path.Combine(testRoot, "absent"), null,
                _ => { }, log, new WizardInstallRunner.PreparationDependencies(
                    (stage, _) => Task.FromResult(DeliverZip(stage, zip,
                        new ManifestVerifier.PayloadExpectation(Sha256Hex(zip), zip.Length, "1.0.0"))), () => false),
                forEarlyExecution: true);
            Assert.True(preparation.Success, preparation.Error);
            prepared = preparation.Payload!;
            var shell = new PreparedShell();
            var stopper = new RecordingAppStopper();
            var positive = WizardInstallRunner.InstallPrepared(prepared, Path.Combine(testRoot, "installed"),
                _ => { }, log, shell, stopper);
            Assert.True(positive.Success, positive.Error);
            Assert.True(Directory.Exists(prepared.PayloadRoot));
            prepared.Dispose();
            prepared.Dispose();
            Assert.False(Directory.Exists(prepared.PayloadRoot));
            Assert.False(prepared.PreparedForEarlyExecution);
            var refusedRoot = Path.Combine(testRoot, "refused");
            var negative = WizardInstallRunner.InstallPrepared(prepared, refusedRoot, _ => { }, log, shell, stopper);
            Assert.False(negative.Success);
            Assert.Single(shell.InstallRoots);
            Assert.Empty(stopper.Calls);
            Assert.False(Directory.Exists(refusedRoot));
            _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = "one prepared downloaded owner before and after disposal",
                predicate = "InstallPrepared.Success", positiveControl = positive.Success,
                negativeControl = negative.Success, shellCalls = shell.InstallRoots.Count,
                refusedRootCreated = Directory.Exists(refusedRoot)
            }));
        }
        finally { prepared?.Dispose(); TryRm(testRoot); }
    }

    [Fact]
    public async Task PreparedPayload_CacheFallback_IsLegacyOnly_AndPreCancellationDoesNotDownload()
    {
        var testRoot = NewTempBase();
        try
        {
            using var log = new SetupLogger(Path.Combine(testRoot, "logs"));
            var zip = PreparedZip();
            var delivered = DeliverZip(testRoot, zip, null);
            var existing = Path.Combine(testRoot, "existing");
            var cache = LocalCachePayloadSourceResolver.CachePath(existing);
            ZipFile.ExtractToDirectory(delivered.ZipPath!, cache);
            var manifestBefore = File.ReadAllBytes(Path.Combine(cache, "manifest.json"));
            int downloads = 0;
            var dependencies = new WizardInstallRunner.PreparationDependencies((_, _) =>
            {
                downloads++;
                return Task.FromResult(new PayloadDownloader.DownloadResult(false, null, "synthetic offline"));
            }, () => false);
            var positive = await WizardInstallRunner.PrepareAsync(existing, null, _ => { }, log, dependencies);
            Assert.True(positive.Success, positive.Error);
            Assert.Equal("local-cache", positive.Payload!.Origin);
            Assert.False(positive.Payload.PreparedForEarlyExecution);
            positive.Payload.Dispose();
            var negative = await WizardInstallRunner.PrepareAsync(existing, null, _ => { }, log, dependencies,
                forEarlyExecution: true);
            Assert.False(negative.Success);
            int beforeCancelledCall = downloads;
            var cancelled = await WizardInstallRunner.PrepareAsync(existing, null, _ => { }, log, dependencies,
                new CancellationToken(true));
            Assert.False(cancelled.Success);
            Assert.Equal(beforeCancelledCall, downloads);
            Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(cache, "manifest.json")));
            _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                universe = new { cache, delivery = "failed synthetic download" },
                predicate = "PrepareAsync.Success", positiveControl = positive.Success,
                negativeControl = negative.Success, cancelled = cancelled.Success,
                beforeCancelledCall, downloads
            }));
        }
        finally { TryRm(testRoot); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("app")]
    [InlineData("dependency")]
    [InlineData("undeclared")]
    public async Task PreparedPayload_NativePath_RequiresDeclaredVerifiedRuntime(string defect)
    {
        string testRoot = NewTempBase();
        try
        {
            using var log = new SetupLogger(Path.Combine(testRoot, "logs"));
            byte[] zip = PreparedZip(includeNativeDependencies: defect != "missing");
            var preparation = await WizardInstallRunner.PrepareAsync(Path.Combine(testRoot, "absent"), null,
                _ => { }, log, new WizardInstallRunner.PreparationDependencies((stage, _) =>
                    Task.FromResult(DeliverZip(stage, zip,
                        new ManifestVerifier.PayloadExpectation(Sha256Hex(zip), zip.Length, "1.0.0"))), () => false),
                forEarlyExecution: true);
            Assert.True(preparation.Success, preparation.Error);
            using var prepared = preparation.Payload!;
            string appPath = Path.GetFullPath(Path.Combine(prepared.PayloadRoot, "App/bin/PAXCookbook.exe"));
            if (defect != "missing")
                Assert.Equal(appPath, prepared.GetValidatedAppExePath());
            string changed = defect switch
            {
                "app" => appPath,
                "dependency" => Path.Combine(prepared.PayloadRoot, "App/bin/runtimes/win-x64/native/msalruntime.dll"),
                _ => Path.Combine(prepared.PayloadRoot, "App/bin/undeclared.dll")
            };
            if (defect != "missing") File.WriteAllText(changed, "tampered");
            Assert.Throws<InvalidDataException>(() => prepared.GetValidatedAppExePath());
            Assert.True(prepared.IsInvalidated);
            Assert.False(prepared.VerifyCurrentArtifact());
        }
        finally { TryRm(testRoot); }
    }

    // ---------- helpers ----------

    private const string SyntheticApp = "synthetic app, never executable";
    private const string SyntheticMember = "synthetic payload member";

    internal static byte[] PreparedZip(string member = SyntheticMember, bool includeNativeDependencies = true)
    {
        var appBytes = Encoding.UTF8.GetBytes(SyntheticApp);
        var memberBytes = Encoding.UTF8.GetBytes(SyntheticMember);
        var manifest = new Manifest
        {
            AppVersion = "1.0.0", SetupVersion = "1.0.0", Channel = "experimental",
            Payload = new ManifestPayload
            {
                AppExe = new ManifestAppExe
                {
                    RelativeInstallPath = "App/bin/PAXCookbook.exe",
                    Sha256 = Sha256Hex(appBytes), SizeBytes = appBytes.Length
                },
                Files = new List<ManifestFile>
                {
                    new()
                    {
                        RelativeInstallPath = "App/bin/member.txt",
                        Sha256 = Sha256Hex(memberBytes), SizeBytes = memberBytes.Length
                    }
                }
            }
        };
        var entries = new List<(string Name, string Body)>
        {
            ("App/bin/PAXCookbook.exe", SyntheticApp), ("App/bin/member.txt", member)
        };
        if (includeNativeDependencies)
        {
            string[] dependencies =
            {
                "PAXCookbook.dll", "PAXCookbook.deps.json", "PAXCookbook.runtimeconfig.json",
                "Microsoft.Identity.Client.dll", "Microsoft.Identity.Client.Broker.dll",
                "Microsoft.Identity.Client.NativeInterop.dll", "Microsoft.IdentityModel.Abstractions.dll",
                "runtimes/win-x64/native/msalruntime.dll"
            };
            foreach (string dependency in dependencies)
            {
                string path = "App/bin/" + dependency;
                entries.Add((path, SyntheticMember));
                manifest.Payload.Files.Add(new ManifestFile
                {
                    RelativeInstallPath = path, Sha256 = Sha256Hex(memberBytes), SizeBytes = memberBytes.Length
                });
            }
        }
        entries.Add(("manifest.json", ManifestSerializer.Serialize(manifest)));
        return BuildZip(entries.ToArray());
    }

    internal static PayloadDownloader.DownloadResult DeliverZip(string stage, byte[] zip,
        ManifestVerifier.PayloadExpectation? expectation)
    {
        var zipPath = Path.Combine(stage, "PAXCookbook_Payload.zip");
        File.WriteAllBytes(zipPath, zip);
        return new PayloadDownloader.DownloadResult(true, zipPath, null)
        {
            Expectation = expectation, SourceUrl = "https://example.invalid/synthetic-payload.zip"
        };
    }

    internal sealed class PreparedShell : IShellOperations
    {
        public List<string> InstallRoots { get; } = new();
        public ShellApplyResult Install(string installRoot, string appVersion, bool createDesktopShortcut)
        {
            InstallRoots.Add(installRoot);
            return new ShellApplyResult(0, false, false, "", "");
        }
        public ShellApplyResult Reconcile(string installRoot, string appVersion) => throw new InvalidOperationException();
        public ShellApplyResult Repair(string installRoot, string appVersion) => throw new InvalidOperationException();
        public ShellStatus Inspect(string installRoot) => throw new InvalidOperationException();
    }

    private static byte[] BuildZip((string Name, string Body)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (n, b) in entries)
            {
                var e = z.CreateEntry(n);
                using var es = e.Open();
                var bytes = Encoding.UTF8.GetBytes(b);
                es.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }

    private static string NewTempBase()
    {
        var d = Path.Combine(Path.GetTempPath(), "p11rb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void TryRm(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }

    private static string Sha256Hex(byte[] b)
    {
        using var s = System.Security.Cryptography.SHA256.Create();
        var h = s.ComputeHash(b);
        return Convert.ToHexString(h).ToLowerInvariant();
    }
}
