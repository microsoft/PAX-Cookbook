using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup;
using PAXCookbookSetup.Gui;
using PAXCookbookSetup.Provider;
using PAXCookbookSetup.Shell;
using Xunit;

[assembly: PAXCookbookSetup.Tests.SetupModuleEvidence]

namespace PAXCookbookSetup.Tests;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SetupModuleEvidenceAttribute : Xunit.Sdk.BeforeAfterTestAttribute
{
    private static readonly string Session = Guid.NewGuid().ToString("N");

    public override void Before(MethodInfo methodUnderTest)
    {
        string? destination = Environment.GetEnvironmentVariable("PAX_CYCLE169_UI_EVIDENCE");
        if (string.IsNullOrWhiteSpace(destination)) { return; }

        ModuleIdentity actual = Identify(typeof(WizardInstallRunner).Assembly);
        ModuleIdentity expected = new(
            Environment.GetEnvironmentVariable("PAX_CYCLE169_SETUP_LOCATION") ?? "",
            Environment.GetEnvironmentVariable("PAX_CYCLE169_SETUP_SHA256") ?? "",
            Environment.GetEnvironmentVariable("PAX_CYCLE169_SETUP_MVID") ?? "");
        ModuleIdentity wrongAssembly = Identify(typeof(SetupModuleEvidenceAttribute).Assembly);
        bool positive = Matches(actual, actual);
        bool wrongSha = Matches(actual, actual with { Sha256 = new string('0', 64) });
        bool wrongMvid = Matches(actual, actual with { Mvid = Guid.Empty.ToString("D") });
        bool wrongLocation = Matches(actual, actual with { Location = wrongAssembly.Location });
        bool wrongModule = Matches(wrongAssembly, actual);
        bool calibrated = positive && !wrongSha && !wrongMvid && !wrongLocation && !wrongModule;
        bool artifactMatches = Matches(actual, expected);
        string invocation = Guid.NewGuid().ToString("N");
        using Process process = Process.GetCurrentProcess();
        var evidence = new
        {
            universe = "Executing Setup assembly in this xUnit method invocation; theory arguments are not identified by this hook",
            predicate = "Nonempty runtime location, SHA256 and MVID equal expected module identity",
            session = Session,
            invocation,
            method = new { declaringType = methodUnderTest.DeclaringType?.FullName, name = methodUnderTest.Name },
            process = new { id = process.Id, startedUtc = process.StartTime.ToUniversalTime(), path = Environment.ProcessPath },
            recordedUtc = DateTime.UtcNow,
            actual,
            expected,
            wrongAssembly,
            controls = new { positive, wrongSha, wrongMvid, wrongLocation, wrongModule },
            calibrated,
            artifactMatches
        };
        Directory.CreateDirectory(destination);
        using (FileStream stream = new(Path.Combine(destination, "setup-module-" + invocation + ".json"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, evidence, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        }
        if (!calibrated || !artifactMatches)
        {
            throw new InvalidOperationException("Setup module evidence calibration or frozen artifact identity failed.");
        }
    }

    private static ModuleIdentity Identify(Assembly assembly)
    {
        using FileStream stream = File.OpenRead(assembly.Location);
        return new(assembly.Location, Convert.ToHexString(SHA256.HashData(stream)),
            assembly.ManifestModule.ModuleVersionId.ToString("D"));
    }

    private static bool Matches(ModuleIdentity actual, ModuleIdentity expected) =>
        !string.IsNullOrWhiteSpace(actual.Location) && actual.Sha256.Length == 64 &&
        Guid.TryParse(actual.Mvid, out Guid moduleId) && moduleId != Guid.Empty &&
        string.Equals(actual.Location, expected.Location, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal) &&
        string.Equals(actual.Mvid, expected.Mvid, StringComparison.OrdinalIgnoreCase);

    private sealed record ModuleIdentity(string Location, string Sha256, string Mvid);
}

[CollectionDefinition("Setup UI STA", DisableParallelization = true)]
public sealed class SetupUiStaCollection
{
}

// Batch 4 — the customer-visible Setup copy carries NO "experimental" word. The
// internal SetupChannel value "experimental" is unchanged (asserted below);
// only the DISPLAYED text changed. The single authorized customer-visible
// "Experimental" notice lives in the running app shell, never in Setup.
[Collection("Setup UI STA")]
public class SetupUiTextTests
{
    [Fact]
    public void SetupWindowTitle_IsBareProductTitle_NoMarker()
    {
        Assert.Equal("PAX Cookbook Setup", SetupUiText.SetupWindowTitle);
        Assert.DoesNotContain(
            "experimental",
            SetupUiText.SetupWindowTitle,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreReleaseSafetyStrip_UsesTestWording_NoExperimental()
    {
        Assert.Equal(
            "Test build \u2014 not for production",
            SetupUiText.PreReleaseSafetyStrip);
        Assert.DoesNotContain(
            "experimental",
            SetupUiText.PreReleaseSafetyStrip,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoPreReleaseBuildFound_UsesPreReleaseWording_NoExperimental()
    {
        Assert.Equal("No pre-release build was found. ", SetupUiText.NoPreReleaseBuildFound);
        Assert.DoesNotContain(
            "experimental",
            SetupUiText.NoPreReleaseBuildFound,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InternalSetupChannelTokens_AreUnchanged()
    {
        // The copy sweep must NOT have renamed the internal channel data values.
        Assert.Equal("stable", SetupChannel.Stable);
        Assert.Equal("experimental", SetupChannel.Experimental);
        Assert.Equal("internal", SetupChannel.Internal);
    }

    [Theory]
    [InlineData("2.0.0+commit.abcdef0", "2.0.0")]
    [InlineData("2.0.0-exp.4+build.exp4", "2.0.0-exp.4")]
    [InlineData("2.0.0-internal.7+build.vm7", "2.0.0-internal.7")]
    public void VersionDisplay_StripsOnlyProvenance_AndRetainsPrerelease(
        string informationalVersion,
        string expected)
    {
        Assert.Equal(
            expected,
            SetupWizardForm.FormatDisplayVersion(informationalVersion, new Version(9, 9, 9, 9)));
    }

    [Fact]
    public void VersionDisplay_MalformedInformationalVersion_UsesBoundedAssemblyFallback()
    {
        Assert.Equal(
            "2.1.3",
            SetupWizardForm.FormatDisplayVersion("not-a-version", new Version(2, 1, 3, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstallFinished_RequiresProviderCommit(bool helloAvailable)
        => RunSta(() => WithTempRoot(root =>
        {
            using var log = new SetupLogger(Path.Combine(root, "Logs"));
            using var form = NewWizard(root, log);
            var launcher = new RefusingLauncher();
            var controller = NewController(root, launcher, helloAvailable);
            SetField(form, "_signIn", controller);
            SetStep(form, "Progress");

            Invoke(form, "OnInstallFinished",
                new WizardInstallResult(true, SetupExitCodes.Ok, null),
                Array.Empty<NamedPrerequisiteResult>());

            string step = Field<object>(form, "_step").ToString()!;
            var selection = SessionProviderStore.Load(root);
            var close = Field<Button>(form, "_btnCancel");
            var next = Field<Button>(form, "_btnNext");
            WriteEvidence("commit-" + helloAvailable, new
            {
                helloAvailable, form.ExitCode, step, selection.Migrated,
                provider = selection.Provider.ToString(), launcher.Calls,
                close.Text, closeVisible = LocallyVisible(close),
                nextText = next.Text, nextVisible = LocallyVisible(next),
                completeVisible = LocallyVisible(Field<Panel>(form, "_panelComplete")),
                form.Visible
            });
            Assert.False(form.Visible);
            Assert.Equal(0, launcher.Calls);
            if (helloAvailable)
            {
                Assert.Equal(SetupExitCodes.Ok, form.ExitCode);
                Assert.Equal("Complete", step);
                Assert.True(LocallyVisible(Field<Panel>(form, "_panelComplete")));
                Assert.Equal("Finish", next.Text);
                Assert.True(LocallyVisible(next));
                Assert.False(selection.Migrated);
                Assert.Equal(SelectedSessionProvider.WindowsHello, selection.Provider);
            }
            else
            {
                Assert.Equal(SetupExitCodes.InstallFailed, form.ExitCode);
                Assert.Equal("Progress", step);
                Assert.False(LocallyVisible(Field<Panel>(form, "_panelComplete")));
                Assert.Equal("Close", close.Text);
                Assert.True(LocallyVisible(close));
                Assert.False(LocallyVisible(next));
                Assert.True(selection.Migrated);
                Assert.Contains("sign-in choice could not be saved", Field<TextBox>(form, "_progressLog").Text);
            }
        }));

    [Fact]
    public void UpdateFailure_ShowsBoundedCopy_WithoutStateGuarantee()
        => RunSta(() => WithTempRoot(root =>
        {
            using var log = new SetupLogger(Path.Combine(root, "Logs"));
            using var form = new UpdateForm(Path.Combine(root, "PAXCookbook"), log);
            const string sentinel = "SYNTHETIC_ONLY_RAW_ERROR_PRIVATE_PATH";
            Invoke(form, "OnFinished", new WizardInstallResult(false, SetupExitCodes.InstallFailed, sentinel));
            string text = Field<Label>(form, "_errorDetail").Text;
            WriteEvidence("update-failure", new { text, form.ExitCode, form.Visible });
            Assert.Equal("The update could not finish. Run PAX Cookbook Setup again. Contact your IT team if the problem continues.", text);
            Assert.DoesNotContain(sentinel, text);
            Assert.DoesNotContain("was not changed", text);
            Assert.Equal(SetupExitCodes.InstallFailed, form.ExitCode);
            Assert.True(LocallyVisible(Field<Panel>(form, "_errorPanel")));
            Assert.False(LocallyVisible(Field<Panel>(form, "_donePanel")));
            Assert.False(form.Visible);
        }));

    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(200)]
    public void UninstallFailure_UsesBoundedCopy_ThatFits(int percent)
        => RunSta(() => WithTempRoot(root =>
        {
            using var log = new SetupLogger(Path.Combine(root, "Logs"));
            using var form = new UninstallForm(root, ArgParser.Parse(new[] { "uninstall" }), log);
            Invoke(form, "OnFinished", SetupExitCodes.UninstallFailed);
            Label status = Field<Label>(form, "_progressStatus");
            float factor = percent / 100F;
            status.Size = new Size((int)(status.Width * factor), (int)(status.Height * factor));
            status.Font = new Font(status.Font.FontFamily, status.Font.Size * factor, status.Font.Style);
            bool Fits(Control control)
            {
                Size measured = MeasureControlText(control);
                return measured.Width <= control.ClientSize.Width && measured.Height <= control.ClientSize.Height;
            }
            using var calibration = new Label { Text = "Fits", Font = status.Font, Size = new Size(200, 100) };
            bool positive = Fits(calibration);
            calibration.Size = new Size(2, 2);
            bool negative = Fits(calibration);
            bool actual = Fits(status);
            WriteEvidence("uninstall-copy-" + percent, new
            {
                universe = "Hidden uninstall completion status label at simulated scale; no uninstall operation",
                predicate = "TextRenderer measured width and height fit the control client rectangle",
                percent, positive, negative, actual, status.Text, status.Width, status.Height,
                measured = MeasureControlText(status), form.ExitCode, form.Visible
            });
            Assert.True(positive);
            Assert.False(negative);
            Assert.True(actual);
            Assert.Equal("PAX Cookbook could not be removed. Contact your IT team.", status.Text);
            Assert.Equal(SetupExitCodes.UninstallFailed, form.ExitCode);
            Assert.True(LocallyVisible(Field<Button>(form, "_btnClose")));
            Assert.False(form.Visible);
        }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdateCleanup_BlocksCloseUntilAcknowledged(bool locked)
        => RunSta(() => WithTempRoot(root =>
        {
            using var log = new SetupLogger(Path.Combine(root, "Logs"));
            using var form = new UpdateForm(Path.Combine(root, "PAXCookbook"), log);
            using var context = new ApplicationContext();
            using var watchdog = new System.Windows.Forms.Timer { Interval = 15000 };
            string owned = Path.Combine(root, "owned");
            Directory.CreateDirectory(owned);
            string partial = Path.Combine(owned, "partial.tmp");
            File.WriteAllText(partial, "synthetic partial download");
            using var lease = new WizardInstallRunner.PayloadCleanupLease(owned);
            FileStream? held = locked ? File.Open(partial, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            Exception? failure = null;
            bool completed = false;
            async void Exercise(object? sender, EventArgs args)
            {
                Application.Idle -= Exercise;
                try
                {
                    Invoke(form, "OnFinished", new WizardInstallResult(true, SetupExitCodes.Ok, null) { Cleanup = lease });
                    var closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
                    Invoke(form, "OnFormClosing", closing);
                    bool Blocked(bool cancelled, bool openEnabled, bool doneVisible) => cancelled && !openEnabled && !doneVisible;
                    bool positive = Blocked(true, false, false);
                    bool negative = Blocked(false, true, true);
                    bool blocked = Blocked(closing.Cancel, Field<Button>(form, "_btnOpen").Enabled,
                        LocallyVisible(Field<Panel>(form, "_donePanel")));
                    Assert.True(positive);
                    Assert.False(negative);
                    Assert.True(blocked);
                    Assert.Equal("Retry cleanup", Field<Button>(form, "_btnCloseError").Text);
                    Assert.Equal(SetupExitCodes.InstallFailed, form.ExitCode);
                    await (System.Threading.Tasks.Task)Invoke(form, "RetryCleanupAsync")!;
                    bool retainedWhileHeld = lease.HasPendingCleanup;
                    Assert.Equal(locked, retainedWhileHeld);
                    held?.Dispose();
                    held = null;
                    await (System.Threading.Tasks.Task)Invoke(form, "RetryCleanupAsync")!;
                    closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
                    Invoke(form, "OnFormClosing", closing);
                    bool remainsAfterProductCleanup = Directory.Exists(owned);
                    WriteEvidence("update-cleanup-" + locked, new
                    {
                        universe = "Hidden UpdateForm completion and retry controls with a synthetic cleanup lease; no Shown or StartUpdate",
                        predicate = "closeCancelled && !openEnabled && !doneVisible", positive, negative, blocked,
                        locked, retainedWhileHeld, remainsAfterProductCleanup, closeCancelledAfterRetry = closing.Cancel, form.Visible
                    });
                    Assert.False(remainsAfterProductCleanup);
                    Assert.False(closing.Cancel);
                    Assert.Equal("Close", Field<Button>(form, "_btnCloseError").Text);
                    Assert.False(form.Visible);
                    completed = true;
                }
                catch (Exception ex) { failure = ex; }
                finally { context.ExitThread(); }
            }
            watchdog.Tick += (_, _) => context.ExitThread();
            Application.Idle += Exercise;
            try
            {
                watchdog.Start();
                Application.Run(context);
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                Assert.True(completed);
            }
            finally
            {
                Application.Idle -= Exercise;
                held?.Dispose();
            }
        }));

    [Fact]
    public void WorkTest_MissingNativeApp_ShowsFailure_AndKeepsInstallDisabled()
        => RunSta(() => WithTempRoot(root =>
        {
            using var log = new SetupLogger(Path.Combine(root, "Logs"));
            using var form = NewWizard(root, log);
            var launcher = new RefusingLauncher();
            string stage = Path.Combine(root, "stage");
            Directory.CreateDirectory(stage);
            WizardSignInPreparation? owner = null;
            var controller = new SignInMethodController(true, root, stage,
                () => owner!.ResolveNativeAppPath(), launcher, new FakeHello(false));
            byte[] zip = Phase11PayloadResolverTests.PreparedZip();
            owner = new WizardSignInPreparation(controller,
                (early, token) => WizardInstallRunner.PrepareAsync(Path.Combine(root, "PAXCookbook"), null, _ => { }, log,
                    new WizardInstallRunner.PreparationDependencies((owned, _) =>
                        System.Threading.Tasks.Task.FromResult(Phase11PayloadResolverTests.DeliverZip(owned, zip,
                            new ManifestVerifier.PayloadExpectation(Convert.ToHexString(SHA256.HashData(zip)), zip.Length, "1.0.0"))),
                        () => false), token, early),
                (_, _, _) => System.Threading.Tasks.Task.FromResult(new PrerequisiteCoordinatorResult(Array.Empty<NamedPrerequisiteResult>(), false)),
                (_, _) => throw new InvalidOperationException());
            string fixture = (string)typeof(SignInMethodControllerTests)
                .GetMethod("WriteVerifiedResult", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { stage })!;
            Assert.True(controller.ImportSetupResult(fixture));
            SetField(form, "_signIn", controller);
            SetField(form, "_signInStagingDir", stage);
            SetField(form, "_signInPreparation", owner);
            SetField(form, "_step", Enum.Parse(Field<object>(form, "_step").GetType(), "SignInMethod"));
            Field<RadioButton>(form, "_radioWork").Checked = true;
            Assert.True(controller.WorkVerified);
            Assert.False(owner.TestAsync(Path.Combine(stage, "result.json")).GetAwaiter().GetResult());
            Invoke(form, "RenderSignInPreparation");
            string text = Field<Label>(form, "_workStatusLabel").Text;
            WriteEvidence("missing-native-app", new
            {
                text, launcher.Calls, controller.WorkVerified, controller.NativeTestPassed,
                canContinue = controller.CanContinue(), installEnabled = Field<Button>(form, "_btnNext").Enabled,
                form.Visible
            });
            Assert.Equal(1, launcher.Calls);
            Assert.Equal("The sign-in test did not succeed. You can retry or choose Windows Hello.", text);
            Assert.False(controller.NativeTestPassed);
            Assert.False(controller.CanContinue());
            Assert.False(Field<Button>(form, "_btnNext").Enabled);
            Assert.True(Field<Button>(form, "_btnWorkImport").Enabled);
            Assert.True(Field<Button>(form, "_btnWorkTest").Enabled);
            Assert.False(form.Visible);
        }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hello_InvalidatedPayload_ShowsRetryOutsideWorkPanel(bool refuseCleanup)
        => RunSta(() => WithTempRoot(root =>
        {
            using var log = new SetupLogger(Path.Combine(root, "Logs"));
            using var form = NewWizard(root, log);
            using var context = new ApplicationContext();
            using var watchdog = new System.Windows.Forms.Timer { Interval = 15000 };
            string stage = Path.Combine(root, "stage");
            Directory.CreateDirectory(stage);
            var controller = NewController(root, new RefusingLauncher(), true);
            byte[] zip = Phase11PayloadResolverTests.PreparedZip();
            int downloads = 0;
            WizardInstallRunner.PreparedPayload? prepared = null;
            using var owner = new WizardSignInPreparation(controller, async (early, token) =>
            {
                var result = await WizardInstallRunner.PrepareAsync(Path.Combine(root, "PAXCookbook"), null, _ => { }, log,
                    new WizardInstallRunner.PreparationDependencies((owned, _) =>
                    {
                        downloads++;
                        return System.Threading.Tasks.Task.FromResult(Phase11PayloadResolverTests.DeliverZip(owned, zip,
                            new ManifestVerifier.PayloadExpectation(Convert.ToHexString(SHA256.HashData(zip)), zip.Length, "1.0.0")));
                    }, () => false), token, early);
                prepared = result.Payload;
                return result;
            }, (_, _, _) => System.Threading.Tasks.Task.FromResult(new PrerequisiteCoordinatorResult(Array.Empty<NamedPrerequisiteResult>(), false)),
                (_, _) => throw new InvalidOperationException("Install must refuse before mutation."));
            SetField(form, "_signIn", controller);
            SetField(form, "_signInPreparation", owner);
            SetField(form, "_signInStagingDir", stage);
            SetField(form, "_step", Enum.Parse(Field<object>(form, "_step").GetType(), "SignInMethod"));
            Field<RadioButton>(form, "_radioWork").Checked = true;
            string fixture = (string)typeof(SignInMethodControllerTests)
                .GetMethod("WriteVerifiedResult", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { stage })!;
            Assert.True(controller.ImportSetupResult(fixture));
            Exception? callbackFailure = null;
            bool timedOut = false;
            FileStream? held = null;
            watchdog.Tick += (_, _) => { timedOut = true; context.ExitThread(); };
            _ = form.Handle;
            form.BeginInvoke(new Action(async () =>
            {
                try
                {
                    Assert.False(await owner.TestAsync(Path.Combine(stage, "result.json")));
                    Field<RadioButton>(form, "_radioHello").Checked = true;
                    Assert.True(prepared!.VerifyCurrentArtifact());
                    string member = Path.Combine(prepared.PayloadRoot, "App/bin/member.txt");
                    File.AppendAllText(member, "changed");
                    Assert.False((await owner.InstallAsync()).Success);
                    Invoke(form, "RenderSignInPreparation");
                    var status = Field<Label>(form, "_helloAvailabilityLabel");
                    var retry = Field<Button>(form, "_btnCancel");
                    Assert.Equal("Setup files changed. Choose Retry, then Install.", status.Text);
                    Assert.Equal("Retry", retry.Text);
                    Assert.True(retry.Enabled);
                    Assert.True(status.Enabled);
                    Assert.False(Field<Panel>(form, "_signInWorkPanel").Enabled);
                    bool Fits(Control control)
                    {
                        Size measured = MeasureControlText(control);
                        return measured.Width <= control.ClientSize.Width && measured.Height <= control.ClientSize.Height;
                    }
                    using var negative = new Label { Text = status.Text, Font = status.Font, Size = new Size(2, 2) };
                    bool positiveControl = Fits(status), negativeControl = Fits(negative);
                    Assert.True(positiveControl);
                    Assert.False(negativeControl);
                    Assert.True(Fits(retry));
                    if (refuseCleanup)
                    {
                        held = new FileStream(member, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await (System.Threading.Tasks.Task)Invoke(form, "OnWorkTestAsync")!;
                        Assert.True(owner.RequiresFreshPreparation);
                        Assert.True(owner.CanRetryCleanup);
                        Assert.True(File.Exists(member));
                        Assert.False(Field<Button>(form, "_btnNext").Enabled);
                        Assert.Equal("Setup could not finish stopping. Choose Retry.", status.Text);
                        Assert.True(Fits(status));
                        held.Dispose();
                        held = null;
                    }
                    await (System.Threading.Tasks.Task)Invoke(form, "OnWorkTestAsync")!;
                    Assert.False(owner.RequiresFreshPreparation);
                    Assert.False(Directory.Exists(prepared.PayloadRoot));
                    Assert.Equal("Cancel", retry.Text);
                    Assert.Equal("Windows Hello is available on this device.", status.Text);
                    Assert.True(Field<Button>(form, "_btnNext").Enabled);
                    Assert.False(controller.NativeTestPassed);
                    Assert.True(controller.WorkVerified);
                    Assert.Equal(1, downloads);
                    WriteEvidence("hello-invalidated-retry-" + refuseCleanup, new
                    {
                        universe = "Hidden Hello-selected wizard, real OS-temp fake payload, retry handler and optional locked member",
                        predicate = "TextRenderer width and height fit the actual Hello status client rectangle",
                        refuseCleanup, downloads, positiveControl, negativeControl,
                        owner.RequiresFreshPreparation, controller.NativeTestPassed, controller.WorkVerified, status.Text
                    });
                }
                catch (Exception exception) { callbackFailure = exception; }
                finally { context.ExitThread(); }
            }));
            watchdog.Start();
            try
            {
                Application.Run(context);
                if (callbackFailure is not null) ExceptionDispatchInfo.Capture(callbackFailure).Throw();
                Assert.False(timedOut);
            }
            finally { watchdog.Stop(); held?.Dispose(); }
        }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationPending_MapsControls_AndCloseDefersStagingCleanup(bool close)
        => RunSta(() => WithTempRoot(root =>
        {
            using var log = new SetupLogger(Path.Combine(root, "Logs"));
            using var form = NewWizard(root, log);
            var entered = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new System.Threading.Tasks.TaskCompletionSource<WizardPreparationResult>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            var controller = NewController(root, new RefusingLauncher(), true);
            string stage = Path.Combine(root, "stage");
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "owned.txt"), "keep while busy");
            string neighbor = Path.Combine(root, "neighbor.txt");
            File.WriteAllText(neighbor, "keep");
            var owner = new WizardSignInPreparation(controller, (_, _) =>
            {
                entered.SetResult(true);
                return release.Task;
            }, (_, _, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException());
            SetField(form, "_signIn", controller);
            SetField(form, "_signInPreparation", owner);
            SetField(form, "_signInStagingDir", stage);
            SetField(form, "_step", Enum.Parse(Field<object>(form, "_step").GetType(), "SignInMethod"));
            owner.StateChanged += () => Invoke(form, "OnPreparationStateChanged");
            Field<RadioButton>(form, "_radioWork").Checked = true;
            string fixture = (string)typeof(SignInMethodControllerTests)
                .GetMethod("WriteVerifiedResult", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { stage })!;
            Assert.True(controller.ImportSetupResult(fixture));
            var running = owner.TestAsync(Path.Combine(stage, "result.json"));
            try
            {
                entered.Task.GetAwaiter().GetResult();
                Invoke(form, "RenderSignInPreparation");
                Assert.False(Field<Button>(form, "_btnBack").Enabled);
                Assert.False(Field<Button>(form, "_btnNext").Enabled);
                Assert.False(Field<Button>(form, "_btnWorkImport").Enabled);
                Assert.False(Field<Button>(form, "_btnWorkTest").Enabled);
                Assert.True(Field<RadioButton>(form, "_radioHello").Enabled);
                if (close) form.Dispose(); else Field<RadioButton>(form, "_radioHello").Checked = true;
                Assert.True(Directory.Exists(stage));
                release.SetResult(new WizardPreparationResult(null, SetupExitCodes.InstallFailed, "synthetic failure"));
                Assert.False(running.GetAwaiter().GetResult());
                if (!close)
                {
                    Invoke(form, "RenderSignInPreparation");
                    Assert.True(Field<Button>(form, "_btnNext").Enabled);
                    Assert.True(Field<Button>(form, "_btnBack").Enabled);
                }
                Assert.Equal(!close, Directory.Exists(stage));
                Assert.Equal("keep", File.ReadAllText(neighbor));
                WriteEvidence("pending-" + close, new { close, owner.IsBusy, owner.CanContinue, controller.NativeTestPassed });
            }
            finally
            {
                release.TrySetResult(new WizardPreparationResult(null, SetupExitCodes.InstallFailed, "synthetic failure"));
                running.GetAwaiter().GetResult();
            }
        }));

    [Fact]
    public void FormClosing_DrainsPreparationBeforeMessageLoopReturns()
    {
        static bool Drained(CloseLifetimeObservation observed) => observed.LoopRetained
            && observed.ControlsRetained && observed.LoggerScopeRetained && observed.StagingRetained
            && observed.PayloadRetained && observed.OperationCompleted && observed.PayloadDisposed
            && observed.StagingRemoved && observed.FormDisposed && observed.ClosedCount == 1
            && observed.NeighborRetained && !observed.TimedOut;

        CloseLifetimeObservation Measure(bool prematureLoopExit, string? cleanupFailure = null)
        {
            CloseLifetimeObservation? observation = null;
            RunSta(() => WithTempRoot(root =>
            {
                using var log = new SetupLogger(Path.Combine(root, "Logs"));
                using var form = NewWizard(root, log);
                using var context = new ApplicationContext();
                using var watchdog = new System.Windows.Forms.Timer { Interval = 10000 };
                var entered = new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new System.Threading.Tasks.TaskCompletionSource<WizardPreparationResult>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                var controller = NewController(root, new RefusingLauncher(), true);
                string stage = Path.Combine(root, "stage");
                string payloadRoot = Path.Combine(root, "payload");
                Directory.CreateDirectory(stage);
                Directory.CreateDirectory(payloadRoot);
                File.WriteAllText(Path.Combine(stage, "owned.txt"), "keep while busy");
                File.WriteAllText(Path.Combine(payloadRoot, "owned.txt"), "inert payload");
                string neighbor = Path.Combine(root, "neighbor.txt");
                File.WriteAllText(neighbor, "keep");
                var payload = (WizardInstallRunner.PreparedPayload)System.Runtime.CompilerServices.RuntimeHelpers
                    .GetUninitializedObject(typeof(WizardInstallRunner.PreparedPayload));
                SetField(payload, "_cleanup", new WizardInstallRunner.PayloadCleanupLease(payloadRoot));
                var owner = new WizardSignInPreparation(controller, (_, _) =>
                {
                    entered.TrySetResult(true);
                    return release.Task;
                }, (_, _, _) => throw new InvalidOperationException("Unexpected prerequisites."),
                    (_, _) => throw new InvalidOperationException("Unexpected install."));
                SetField(form, "_signIn", controller);
                SetField(form, "_signInPreparation", owner);
                SetField(form, "_signInStagingDir", stage);
                SetField(form, "_step", Enum.Parse(Field<object>(form, "_step").GetType(), "SignInMethod"));
                owner.StateChanged += () => Invoke(form, "OnPreparationStateChanged");
                Field<RadioButton>(form, "_radioWork").Checked = true;
                string fixture = (string)typeof(SignInMethodControllerTests)
                    .GetMethod("WriteVerifiedResult", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { stage })!;
                Assert.True(controller.ImportSetupResult(fixture));
                var running = owner.TestAsync(Path.Combine(stage, "result.json"));
                bool loopRetained = false, controlsRetained = false, loggerScopeRetained = false;
                bool stagingRetained = false, payloadRetained = false, timedOut = false;
                int closedCount = 0;
                bool cleanupRefused = false;
                FileStream? cleanupLock = null;
                Exception? callbackFailure = null;
                form.FormClosed += (_, _) => { closedCount++; context.ExitThread(); };
                Field<Label>(form, "_workStatusLabel").TextChanged += (_, _) =>
                {
                    if (Field<Label>(form, "_workStatusLabel").Text != "Setup could not finish stopping. Setup will stay open. Choose Retry.") return;
                    cleanupRefused = Application.MessageLoop && !form.IsDisposed && running.IsCompleted;
                    context.ExitThread();
                };
                watchdog.Tick += (_, _) => { timedOut = true; context.ExitThread(); };
                _ = form.Handle;
                form.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        SetField(form, "_installRunning", true);
                        form.Close();
                        Assert.False(form.IsDisposed);
                        SetField(form, "_installRunning", false);
                        form.Close();
                        if (form.IsDisposed) return;
                        form.Close();
                        if (prematureLoopExit) { context.ExitThread(); return; }
                        form.BeginInvoke(new Action(() =>
                        {
                            loopRetained = Application.MessageLoop && !running.IsCompleted;
                            controlsRetained = !form.IsDisposed && !Field<Button>(form, "_btnNext").IsDisposed;
                            log.Write("synthetic_close_pending");
                            loggerScopeRetained = true;
                            stagingRetained = File.Exists(Path.Combine(stage, "owned.txt"));
                            payloadRetained = !Field<bool>(payload, "_disposed")
                                && File.Exists(Path.Combine(payloadRoot, "owned.txt"));
                            if (cleanupFailure is not null)
                                cleanupLock = new FileStream(Path.Combine(cleanupFailure == "payload" ? payloadRoot : stage, "owned.txt"),
                                    FileMode.Open, FileAccess.Read, FileShare.Read);
                            release.TrySetResult(new WizardPreparationResult(payload, 0, "synthetic"));
                        }));
                    }
                    catch (Exception exception) { callbackFailure = exception; context.ExitThread(); }
                }));
                watchdog.Start();
                try
                {
                    Application.Run(context);
                    observation = new CloseLifetimeObservation(loopRetained, controlsRetained, loggerScopeRetained,
                        stagingRetained, payloadRetained, running.IsCompleted, Field<bool>(payload, "_disposed"),
                        !Directory.Exists(stage), form.IsDisposed, closedCount, File.ReadAllText(neighbor) == "keep", timedOut, cleanupRefused);
                    WriteEvidence("form-close-observation-" + prematureLoopExit + "-" + (cleanupFailure ?? "none"), observation);
                    if (callbackFailure is not null) ExceptionDispatchInfo.Capture(callbackFailure).Throw();
                }
                finally
                {
                    watchdog.Stop();
                    cleanupLock?.Dispose();
                    SetField(form, "_signInStagingDir", null!);
                    release.TrySetResult(new WizardPreparationResult(null, SetupExitCodes.InstallFailed, "synthetic teardown"));
                    owner.Dispose();
                }
            }));
            return observation!;
        }

        var negative = Measure(prematureLoopExit: true);
        var positive = Measure(prematureLoopExit: false);
        WriteEvidence("form-close-lifetime", new
        {
            universe = "Hidden fake-backed wizard: pending preparation, payload ownership, controls and logger within FormClosed-bound Application.Run",
            negative, positive, negativeAccepted = Drained(negative), positiveAccepted = Drained(positive)
        });
        Assert.False(Drained(negative));
        Assert.True(Drained(positive), JsonSerializer.Serialize(positive));
        foreach (string failure in new[] { "staging", "payload" })
        {
            var refused = Measure(prematureLoopExit: false, cleanupFailure: failure);
            bool retained = refused.CleanupRefused && refused.LoopRetained && refused.ControlsRetained
                && refused.LoggerScopeRetained && refused.StagingRetained && refused.PayloadRetained
                && refused.OperationCompleted && !refused.FormDisposed && refused.ClosedCount == 0
                && !refused.StagingRemoved && refused.NeighborRetained && !refused.TimedOut;
            WriteEvidence("form-close-cleanup-" + failure, new { universe = "Same hidden FormClosing loop with one owned file locked against deletion",
                failure, refused, acceptedAsDrained = Drained(refused), retained,
                positiveControl = Drained(positive), negativeControl = Drained(negative) });
            Assert.False(Drained(refused));
            Assert.True(retained, JsonSerializer.Serialize(refused));
        }
    }

    [Theory]
    [InlineData("child")]
    [InlineData("payload")]
    [InlineData("staging")]
    public void NativeChild_FormCloseFailure_RetryKeepsPumpAndReleasesAfterExit(string failure)
        => RunSta(() => WithTempRoot(root =>
        {
            using var log = new SetupLogger(Path.Combine(root, "Logs"));
            using var form = NewWizard(root, log);
            using var context = new ApplicationContext();
            using var watchdog = new System.Windows.Forms.Timer { Interval = 15000 };
            var child = new SignInMethodControllerTests.ControlledNativeChild
                { KillThrows = failure == "child", ExitOnKill = failure != "child" };
            var launcher = new SignInMethodControllerTests.ControlledNativeLauncher(child);
            var controller = NewController(root, launcher, true);
            string stage = Path.Combine(root, "stage"), payloadRoot = Path.Combine(root, "payload");
            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(payloadRoot);
            File.WriteAllText(Path.Combine(stage, "owned.txt"), "stage");
            File.WriteAllText(Path.Combine(payloadRoot, "owned.txt"), "payload");
            string neighbor = Path.Combine(root, "neighbor.txt");
            File.WriteAllText(neighbor, "keep");
            var payload = (WizardInstallRunner.PreparedPayload)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(WizardInstallRunner.PreparedPayload));
            SetField(payload, "_cleanup", new WizardInstallRunner.PayloadCleanupLease(payloadRoot));
            var owner = new WizardSignInPreparation(controller,
                (_, _) => System.Threading.Tasks.Task.FromResult(new WizardPreparationResult(payload, 0, "synthetic")),
                (_, _, _) => System.Threading.Tasks.Task.FromResult(new PrerequisiteCoordinatorResult(Array.Empty<NamedPrerequisiteResult>(), false)),
                (_, _) => throw new InvalidOperationException("Install forbidden."));
            SetField(form, "_signIn", controller);
            SetField(form, "_signInPreparation", owner);
            SetField(form, "_signInStagingDir", stage);
            SetField(form, "_step", Enum.Parse(Field<object>(form, "_step").GetType(), "SignInMethod"));
            owner.StateChanged += () => Invoke(form, "OnPreparationStateChanged");
            Field<RadioButton>(form, "_radioWork").Checked = true;
            string fixture = (string)typeof(SignInMethodControllerTests)
                .GetMethod("WriteVerifiedResult", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { stage })!;
            Assert.True(controller.ImportSetupResult(fixture));
            string resultPath = Path.Combine(stage, "result.json");
            var running = owner.TestAsync(resultPath);
            bool callbackWorked = false, retained = false, timedOut = false, retryQueued = false;
            int closed = 0;
            FileStream? held = null;
            Exception? callbackFailure = null;
            form.FormClosed += (_, _) => { closed++; context.ExitThread(); };
            watchdog.Tick += (_, _) => { timedOut = true; context.ExitThread(); };
            Field<Label>(form, "_workStatusLabel").TextChanged += (_, _) =>
            {
                if (!Field<Button>(form, "_btnCancel").Enabled || Field<Button>(form, "_btnCancel").Text != "Retry" || retryQueued) return;
                retryQueued = true;
                form.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        callbackWorked = Application.MessageLoop && !form.IsDisposed;
                        log.Write("synthetic_retry_callback");
                        retained = Directory.Exists(stage) && !Field<Button>(form, "_btnNext").Enabled
                            && !Field<Button>(form, "_btnWorkImport").Enabled && !controller.NativeTestPassed && running.IsCompleted;
                        if (failure == "child")
                        {
                            Assert.True(owner.HasOutstandingNativeChild);
                            Assert.False(child.Disposed);
                            Assert.True(File.Exists(Path.Combine(payloadRoot, "owned.txt")));
                            File.WriteAllText(resultPath, "{\"approved\":true}");
                        }
                        held?.Dispose();
                        held = null;
                        child.Exit.TrySetResult(true);
                        Invoke(form, "OnCancel");
                    }
                    catch (Exception exception) { callbackFailure = exception; context.ExitThread(); }
                }));
            };
            _ = form.Handle;
            form.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await child.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (failure != "child") held = new FileStream(Path.Combine(failure == "payload" ? payloadRoot : stage, "owned.txt"),
                        FileMode.Open, FileAccess.Read, FileShare.Read);
                    form.Close();
                }
                catch (Exception exception) { callbackFailure = exception; context.ExitThread(); }
            }));
            watchdog.Start();
            try
            {
                Application.Run(context);
                if (callbackFailure is not null) ExceptionDispatchInfo.Capture(callbackFailure).Throw();
                var observed = new NativeRetryObservation(callbackWorked, retained, child.Disposed, form.IsDisposed,
                    Directory.Exists(stage), Directory.Exists(payloadRoot), controller.NativeTestPassed, closed, timedOut);
                static bool Recovered(NativeRetryObservation value) => value.CallbackWorked && value.Retained
                    && value.ChildDisposed && value.FormDisposed && !value.StageExists && !value.PayloadExists
                    && !value.Approved && value.Closed == 1 && !value.TimedOut;
                var negative = observed with { CallbackWorked = false, Retained = false };
                WriteEvidence("native-retry-" + failure, new { universe = "Hidden fake-backed FormClosed-bound loop; owned child or locked payload/stage; actual footer Retry",
                    failure, observed, negative, positiveControl = Recovered(observed), negativeControl = Recovered(negative) });
                Assert.False(Recovered(negative));
                Assert.True(Recovered(observed), JsonSerializer.Serialize(observed));
                Assert.Equal("keep", File.ReadAllText(neighbor));
                Assert.Equal(1, launcher.Starts);
            }
            finally
            {
                watchdog.Stop();
                held?.Dispose();
                child.Exit.TrySetResult(true);
                SetField(form, "_signInStagingDir", null!);
                owner.Dispose();
            }
        }));

    private sealed record NativeRetryObservation(bool CallbackWorked, bool Retained, bool ChildDisposed,
        bool FormDisposed, bool StageExists, bool PayloadExists, bool Approved, int Closed, bool TimedOut);

    private sealed record CloseLifetimeObservation(bool LoopRetained, bool ControlsRetained,
        bool LoggerScopeRetained, bool StagingRetained, bool PayloadRetained, bool OperationCompleted,
        bool PayloadDisposed, bool StagingRemoved, bool FormDisposed, int ClosedCount, bool NeighborRetained, bool TimedOut,
        bool CleanupRefused);

    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(200)]
    public void WizardPanels_ScaledHiddenRender(int percent)
        => RunSta(() => WithTempRoot(root =>
        {
            var stopwatch = Stopwatch.StartNew();
            int stageIndex = 0;
            void Stage(string stage)
                => WriteEvidence($"render-timing-{percent}-{stageIndex++:D3}", new
                {
                    percent, stage, elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds
                });
            Stage("construct-start");
            using var log = new SetupLogger(Path.Combine(root, "Logs"));
            using var ownedFonts = new RenderFonts(() => Stage("fonts-disposed"));
            using var form = NewWizard(root, log);
            form.Disposed += (_, _) => Stage("form-disposed");
            var controller = NewController(root, new RefusingLauncher(), helloAvailable: true);
            SetField(form, "_signIn", controller);
            Field<RadioButton>(form, "_radioWork").Checked = true;
            Invoke(form, "RenderPrereq",
                PrerequisiteStatus.Missing(PrerequisiteKind.DotNet8DesktopRuntime, ".NET 8 Desktop Runtime"),
                PrerequisiteStatus.Missing(PrerequisiteKind.AspNetCoreRuntime, "ASP.NET Core 8 Runtime"),
                PrerequisiteStatus.Missing(PrerequisiteKind.PowerShell7, "PowerShell 7"),
                PrerequisiteStatus.Missing(PrerequisiteKind.Python, "Python"));
            Invoke(form, "SetWorkStatus", "Ask your IT team for a checked setup file.");
            string[] panels = { "_panelWelcome", "_panelPrereq", "_panelLocation", "_panelSignIn", "_panelProgress", "_panelComplete" };
            var allControls = Descendants(form).ToArray();
            Stage("font-snapshot-start");
            var originalFonts = allControls.ToDictionary(control => control, control => control.Font);
            var richFonts = new Dictionary<RichTextBox, Font[]>();
            foreach (RichTextBox rich in allControls.OfType<RichTextBox>())
            {
                richFonts[rich] = Enumerable.Range(0, rich.TextLength).Select(offset =>
                {
                    rich.Select(offset, 1);
                    Font original = rich.SelectionFont!;
                    ownedFonts.Add(original);
                    return original;
                }).ToArray();
                rich.Select(0, 0);
            }
            Stage("font-snapshot-end-geometry-start");
            float factor = percent / 100F;
            form.AutoScaleMode = AutoScaleMode.None;
            form.Scale(new SizeF(factor, factor));
            Stage("geometry-end-font-scaling-start");
            foreach (Control control in allControls)
            {
                if (control is RichTextBox rich)
                {
                    for (int offset = 0; offset < rich.TextLength;)
                    {
                        Font original = richFonts[rich][offset];
                        int end = offset + 1;
                        while (end < rich.TextLength && original.Equals(richFonts[rich][end])) { end++; }
                        var scaled = new Font(original.FontFamily, original.Size * factor, original.Style);
                        ownedFonts.Add(scaled);
                        rich.Select(offset, end - offset);
                        rich.SelectionFont = scaled;
                        offset = end;
                    }
                    rich.Select(0, 0);
                }
                else
                {
                    Font original = originalFonts[control];
                    var scaled = new Font(original.FontFamily, original.Size * factor, original.Style);
                    ownedFonts.Add(scaled);
                    control.Font = scaled;
                }
            }
            Stage("font-scaling-end-font-comparison-start");
            var fontComparisons = new List<object>();
            bool allFontsMatch = true;
            foreach (RichTextBox rich in allControls.OfType<RichTextBox>())
            {
                for (int offset = 0; offset < rich.TextLength; offset++)
                {
                    Font original = richFonts[rich][offset];
                    rich.Select(offset, 1);
                    Font actual = rich.SelectionFont!;
                    ownedFonts.Add(actual);
                    bool matches = ScaledFontMatches(original, actual, factor);
                    allFontsMatch &= matches;
                    fontComparisons.Add(new
                    {
                        controlIndex = Array.IndexOf(allControls, rich), offset,
                        expectedFamily = original.FontFamily.Name, expectedStyle = original.Style.ToString(),
                        expectedSize = original.Size * factor,
                        actualFamily = actual.FontFamily.Name, actualStyle = actual.Style.ToString(), actualSize = actual.Size,
                        matches
                    });
                }
                rich.Select(0, 0);
            }
            Font calibrationFont = originalFonts[allControls[0]];
            var matchingFont = new Font(calibrationFont.FontFamily, calibrationFont.Size * factor, calibrationFont.Style);
            var wrongSize = new Font(calibrationFont.FontFamily, matchingFont.Size + 1, matchingFont.Style);
            var wrongStyle = new Font(calibrationFont.FontFamily, matchingFont.Size, matchingFont.Style ^ FontStyle.Bold);
            var wrongFamily = new Font(calibrationFont.FontFamily.Name == FontFamily.GenericMonospace.Name
                ? FontFamily.GenericSansSerif : FontFamily.GenericMonospace, matchingFont.Size, matchingFont.Style);
            ownedFonts.AddRange(new[] { matchingFont, wrongSize, wrongStyle, wrongFamily });
            bool positiveFontControl = ScaledFontMatches(calibrationFont, matchingFont, factor);
            bool negativeSizeControl = ScaledFontMatches(calibrationFont, wrongSize, factor);
            bool negativeStyleControl = ScaledFontMatches(calibrationFont, wrongStyle, factor);
            bool negativeFamilyControl = ScaledFontMatches(calibrationFont, wrongFamily, factor);
            WriteEvidence("render-fonts-" + percent, new
            {
                universe = "Every original RichTextBox character, independently selected after run-based font scaling",
                predicate = "Exact original family and style; size equals original size times scale",
                percent, factor, allFontsMatch, positiveFontControl, negativeSizeControl, negativeStyleControl,
                negativeFamilyControl, fontComparisons
            });
            Assert.True(positiveFontControl);
            Assert.False(negativeSizeControl);
            Assert.False(negativeStyleControl);
            Assert.False(negativeFamilyControl);
            Assert.True(allFontsMatch);
            Stage("font-comparison-end-layout-calibration-start");

            using var calibration = new Panel { Size = new Size(200, 100) };
            using var first = new Label { Text = "Fits", Bounds = new Rectangle(0, 0, 100, 30) };
            using var second = new Label { Text = "Fits", Bounds = new Rectangle(0, 40, 100, 30) };
            calibration.Controls.Add(first);
            calibration.Controls.Add(second);
            var negative = LayoutIssues(calibration, "control").ToArray();
            second.Bounds = new Rectangle(0, 0, 4, 120);
            var positive = LayoutIssues(calibration, "control").ToArray();
            bool calibrated = negative.Length == 0 && positive.Any(issue => issue.EndsWith(":bounds"))
                && positive.Any(issue => issue.EndsWith(":text")) && positive.Any(issue => issue.EndsWith(":overlap"));
            var results = new List<object>();
            var issues = new List<string>();
            var captureHashes = new List<string>();
            bool pixelsCalibrated = true;
            foreach (string panelName in panels)
            {
                Stage(panelName + ":layout-start");
                foreach (string other in panels) { Field<Panel>(form, other).Visible = other == panelName; }
                Panel panel = Field<Panel>(form, panelName);
                panel.BringToFront();
                form.PerformLayout();
                panel.PerformLayout();
                Control originalParent = panel.Parent!;
                int originalIndex = originalParent.Controls.GetChildIndex(panel);
                Rectangle originalBounds = panel.Bounds;
                bool originalVisible = LocallyVisible(panel);
                Stage(panelName + ":metrics-before-start");
                string beforeMetrics = PanelMetrics(panel);
                Stage(panelName + ":metrics-before-end-host-start");
                using var host = new Panel
                {
                    ClientSize = originalParent.ClientSize, Padding = originalParent.Padding,
                    Font = originalParent.Font, ForeColor = originalParent.ForeColor,
                    BackColor = originalParent.BackColor, Visible = true
                };
                using var bitmap = new Bitmap(panel.ClientSize.Width, panel.ClientSize.Height);
                Rectangle[] textRegions;
                bool captureVisible;
                bool handlesCreated;
                try
                {
                    host.Controls.Add(panel);
                    panel.Visible = true;
                    host.CreateControl();
                    panel.CreateControl();
                    foreach (Control control in Descendants(panel)) { control.CreateControl(); }
                    host.PerformLayout();
                    panel.PerformLayout();
                    captureVisible = panel.Visible;
                    handlesCreated = panel.IsHandleCreated
                        && Descendants(panel).Where(control => control.Visible).All(control => control.IsHandleCreated);
                    textRegions = Descendants(panel)
                        .Where(control => control.Visible && control is Label or RichTextBox
                            && !string.IsNullOrWhiteSpace(control.Text))
                        .Select(control =>
                        {
                            Point location = control.Location;
                            for (Control? ancestor = control.Parent; ancestor != panel; ancestor = ancestor!.Parent)
                                location.Offset(ancestor!.Location);
                            var region = new Rectangle(location, control.ClientSize);
                            region.Inflate(-2, -2);
                            return Rectangle.Intersect(region, new Rectangle(Point.Empty, bitmap.Size));
                        }).ToArray();
                    Stage(panelName + ":host-end-draw-start");
                    panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, panel.ClientSize));
                    Stage(panelName + ":draw-end");
                }
                finally
                {
                    Stage(panelName + ":restore-start");
                    originalParent.Controls.Add(panel);
                    originalParent.Controls.SetChildIndex(panel, originalIndex);
                    panel.Bounds = originalBounds;
                    panel.Visible = originalVisible;
                    originalParent.PerformLayout();
                    panel.PerformLayout();
                    Stage(panelName + ":restore-end");
                }
                Stage(panelName + ":pixels-start");
                var pixels = MeasurePixels(bitmap, textRegions);
                Stage(panelName + ":pixels-actual-end");
                using var blank = new Bitmap(bitmap.Width, bitmap.Height);
                using (Graphics graphics = Graphics.FromImage(blank)) { graphics.Clear(Color.White); }
                var whiteNegative = MeasurePixels(blank, textRegions);
                using (Graphics graphics = Graphics.FromImage(blank)) { graphics.Clear(Color.DarkBlue); }
                var solidNegative = MeasurePixels(blank, textRegions);
                Stage(panelName + ":pixels-controls-end-metrics-start");
                bool pixelControls = pixels.Accepted && !whiteNegative.Accepted && !solidNegative.Accepted;
                pixelsCalibrated &= pixelControls;
                bool metricsUnchanged = beforeMetrics == PanelMetrics(panel);
                Stage(panelName + ":metrics-end-png-start");
                if (!metricsUnchanged) { issues.Add(panelName + ":capture-changed-layout"); }
                if (!captureVisible || !handlesCreated || !pixelControls) { issues.Add(panelName + ":capture"); }
                using var encoded = new MemoryStream();
                bitmap.Save(encoded, ImageFormat.Png);
                byte[] imageBytes = encoded.ToArray();
                string sha256 = Convert.ToHexString(SHA256.HashData(imageBytes));
                captureHashes.Add(sha256);
                string? destination = Environment.GetEnvironmentVariable("PAX_CYCLE169_UI_EVIDENCE");
                string? image = destination is null ? null : Path.Combine(destination, $"render-{percent}-{panelName}.png");
                if (image is not null) { Directory.CreateDirectory(destination!); File.WriteAllBytes(image, imageBytes); }
                Stage(panelName + ":png-end-layout-issues-start");
                var panelIssues = LayoutIssues(panel, panelName).ToArray();
                Stage(panelName + ":layout-issues-end-result-metrics-start");
                issues.AddRange(panelIssues);
                results.Add(new
                {
                    panelName, image, issues = panelIssues,
                    sha256, imageLength = imageBytes.Length, pixels, whiteNegative, solidNegative, pixelControls,
                    textRegions = textRegions.Select(region => new { region.X, region.Y, region.Width, region.Height }).ToArray(),
                    captureVisible, handlesCreated, metricsUnchanged,
                    bounds = new { panel.Left, panel.Top, panel.Width, panel.Height },
                    controls = Descendants(panel).Select(control => new
                    {
                        type = control.GetType().Name, control.Text,
                        control.Left, control.Top, control.Width, control.Height,
                        fontPoints = control.Font.Size, localVisible = LocallyVisible(control), control.Enabled,
                        measured = MeasureControlText(control)
                    }).ToArray()
                });
                Stage(panelName + ":result-metrics-end-panel-cleanup-start");
            }
            Stage("panels-cleanup-end-evidence-start");
            bool distinctCaptures = DistinctCaptures(captureHashes);
            bool duplicateControl = DistinctCaptures(Enumerable.Repeat(captureHashes[0], panels.Length).ToArray());
            WriteEvidence("render-" + percent, new
            {
                universe = "Constructed wizard panels and locally visible descendants; relative sibling bounds, TextRenderer label/button measurement, RichTextBox last-character position",
                pixelUniverse = "Every pixel in the captured panel client rectangle; text budget restricted to visible nonempty Label/RichTextBox interiors inset by two pixels",
                pixelPredicate = "Non-dominant pixels >= max(32,total/1000), non-dominant RGB<160 pixels inside text regions >= max(16,total/2000), distinct ARGB colors >= 8. The real capture is the positive; equal-size white and dark-blue solids are negatives.",
                distinctPredicate = "More than one capture and every SHA-256 distinct; real capture set positive, same-length repeated-first-hash set negative",
                mode = "Simulated 100/150/200 percent Scale plus scaled fonts; detached Panel.DrawToBitmap without showing the product Form; not actual monitor DPI or manual visual acceptance",
                runtime = Environment.Version.ToString(), pixelsCalibrated, distinctCaptures, duplicateControl,
                percent, form.DeviceDpi, form.Visible, calibrated, positive, negative, results, issues,
                limits = "No native picker, live installer, monitor transition, screen-reader, or screenshot visual-review claim. TextBox content scrolls; only its bounds are checked."
            });
            Stage("evidence-end-assertions-start");
            Assert.True(calibrated);
            Assert.True(pixelsCalibrated);
            Assert.True(distinctCaptures);
            Assert.False(duplicateControl);
            Assert.False(form.Visible);
            Assert.Empty(issues);
            Stage("assertions-end-form-cleanup-start");
        }));

    private sealed class RenderFonts(Action disposed) : List<Font>, IDisposable
    {
        public void Dispose()
        {
            foreach (Font font in this) { font.Dispose(); }
            disposed();
        }
    }

    private static bool ScaledFontMatches(Font original, Font actual, float factor)
        => original.FontFamily.Name == actual.FontFamily.Name && original.Style == actual.Style
            && original.Size * factor == actual.Size;

    private static bool DistinctCaptures(IReadOnlyCollection<string> hashes)
        => hashes.Count > 1 && hashes.Distinct(StringComparer.Ordinal).Count() == hashes.Count;

    private sealed record PixelObservation(int Total, int NonBackground, int DarkText, int Colors, bool Accepted);

    private static PixelObservation MeasurePixels(Bitmap bitmap, Rectangle[] textRegions)
    {
        var colors = new Dictionary<int, int>();
        var pixels = new int[bitmap.Width * bitmap.Height];
        BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (int row = 0; row < bitmap.Height; row++)
                Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), pixels, row * bitmap.Width, bitmap.Width);
        }
        finally { bitmap.UnlockBits(data); }
        foreach (int pixel in pixels) { colors[pixel] = colors.GetValueOrDefault(pixel) + 1; }
        var background = colors.MaxBy(pair => pair.Value);
        int nonBackground = pixels.Length - background.Value;
        int darkText = 0;
        for (int offset = 0; offset < pixels.Length; offset++)
        {
            int pixel = pixels[offset];
            if (pixel != background.Key && ((pixel >> 16) & 255) < 160
                && ((pixel >> 8) & 255) < 160 && (pixel & 255) < 160
                && textRegions.Any(region => region.Contains(offset % bitmap.Width, offset / bitmap.Width)))
                darkText++;
        }
        return new(pixels.Length, nonBackground, darkText, colors.Count,
            nonBackground >= Math.Max(32, pixels.Length / 1000)
            && darkText >= Math.Max(16, pixels.Length / 2000) && colors.Count >= 8);
    }

    private static string PanelMetrics(Panel panel)
        => JsonSerializer.Serialize(Descendants(panel).Select(control => new
        {
            control.Left, control.Top, control.Width, control.Height,
            fontPoints = control.Font.Size, localVisible = LocallyVisible(control), control.Enabled,
            measured = MeasureControlText(control)
        }));

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) { yield return descendant; }
        }
    }

    private static Size MeasureControlText(Control control)
    {
        int inset = control is RadioButton or CheckBox ? 22 : control is Button ? 12 : 0;
        return TextRenderer.MeasureText(control.Text, control.Font,
            new Size(Math.Max(1, control.ClientSize.Width - inset), int.MaxValue),
            control is ButtonBase ? TextFormatFlags.SingleLine : TextFormatFlags.WordBreak);
    }

    private static IEnumerable<string> LayoutIssues(Control parent, string path)
    {
        Control[] visible = parent.Controls.Cast<Control>().Where(control => LocallyVisible(control)).ToArray();
        for (int index = 0; index < visible.Length; index++)
        {
            Control control = visible[index];
            string name = path + "/" + index + ":" + control.GetType().Name;
            if (!parent.ClientRectangle.Contains(control.Bounds)) { yield return name + ":bounds"; }
            if (control is Label or ButtonBase)
            {
                Size measured = MeasureControlText(control);
                int inset = control is RadioButton or CheckBox ? 22 : control is Button ? 12 : 0;
                if (measured.Width > control.ClientSize.Width - inset || measured.Height > control.ClientSize.Height)
                    yield return name + ":text";
            }
            if (control is RichTextBox rich && rich.TextLength > 0)
            {
                Point last = rich.GetPositionFromCharIndex(rich.TextLength - 1);
                rich.Select(rich.TextLength - 1, 1);
                int lineHeight = rich.SelectionFont!.Height;
                rich.Select(0, 0);
                if (last.Y + lineHeight > rich.ClientSize.Height) { yield return name + ":text"; }
            }
            for (int other = index + 1; other < visible.Length; other++)
            {
                if (control.Width > 1 && control.Height > 1 && visible[other].Width > 1 && visible[other].Height > 1
                    && control.Bounds.IntersectsWith(visible[other].Bounds))
                    yield return name + "/" + other + ":overlap";
            }
            foreach (string issue in LayoutIssues(control, name)) { yield return issue; }
        }
    }

    private static SetupWizardForm NewWizard(string root, SetupLogger log)
        => new(Path.Combine(root, "PAXCookbook"), log, new RefusingShell(),
            new PrerequisiteDetector(new EmptyPrerequisiteProbe()));

    private static SignInMethodController NewController(string root, IProcessLauncher launcher, bool helloAvailable)
        => new(true, root, Path.Combine(root, "stage"), Path.Combine(root, "missing-app.exe"),
            launcher, new FakeHello(helloAvailable));

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Invoke(object target, string name, params object[] arguments)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void SetStep(SetupWizardForm form, string step)
        => Invoke(form, "ShowStep", Enum.Parse(Field<object>(form, "_step").GetType(), step));

    private static bool LocallyVisible(Control control)
    {
        MethodInfo method = typeof(Control).GetMethod("GetState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object visible = Enum.Parse(method.GetParameters()[0].ParameterType, "Visible");
        return (bool)method.Invoke(control, new[] { visible })!;
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Synthetic UI check exceeded its bounded STA join.");
        if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    private static void WithTempRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "pax-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void WriteEvidence(string name, object value)
    {
        string? destination = Environment.GetEnvironmentVariable("PAX_CYCLE169_UI_EVIDENCE");
        if (string.IsNullOrWhiteSpace(destination)) { return; }
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, name + ".json"),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class FakeHello(bool available) : IHelloSupportProbe
    {
        public bool IsWindowsHelloAvailable() => available;
    }

    private sealed class RefusingLauncher : IProcessLauncher
    {
        public int Calls { get; private set; }
        public LaunchRecord? Last => null;
        public Process? Start(string fileName, IList<string> arguments)
        {
            Calls++;
            throw new System.ComponentModel.Win32Exception(2);
        }
    }

    private sealed class RefusingShell : IShellOperations
    {
        public ShellApplyResult Install(string installRoot, string appVersion, bool createDesktopShortcut)
            => throw new InvalidOperationException("Unexpected shell install.");
        public ShellApplyResult Reconcile(string installRoot, string appVersion)
            => throw new InvalidOperationException("Unexpected shell reconcile.");
        public ShellApplyResult Repair(string installRoot, string appVersion)
            => throw new InvalidOperationException("Unexpected shell repair.");
        public ShellStatus Inspect(string installRoot)
            => throw new InvalidOperationException("Unexpected shell inspection.");
    }

    private sealed class EmptyPrerequisiteProbe : IPrerequisiteProbe
    {
        public string? ResolveOnPath(string exeName) => null;
        public bool FileExists(string path) => false;
        public IEnumerable<string> EnumerateDirectories(string parent, string pattern) => Array.Empty<string>();
        public string? GetEnvPath(string envVarName) => null;
        public string? RunVersion(string exePath, string arguments) => null;
        public string? ReadHklmString(string subKey, string? valueName) => null;
        public IEnumerable<string> EnumerateHklmSubKeyNames(string subKey) => Array.Empty<string>();
    }
}
