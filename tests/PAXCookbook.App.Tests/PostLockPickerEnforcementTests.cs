using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle-29 post-Lock picker enforcement.
//
// Cycle 28 proved, at the daemon-initiation and native-acquisition boundaries,
// that the silent second unlock DID traverse WAM. The defect is that the
// production window bridge left WamAuthRequest.AcquisitionMode at its default
// UsePreferredAccount, so a stored preference was loaded, bound via WithAccount,
// and WAM then completed without rendering a picker even though
// Prompt.SelectAccount was applied.
//
// These tests pin the repair: every production session unlock forces account
// selection, and Force mode neither loads nor saves preferred-account state.
public sealed class PostLockPickerEnforcementTests
{
    private const string TestTenant = "11111111-1111-1111-1111-111111111111";
    private const string TestClient = "22222222-2222-2222-2222-222222222222";

    private static ExperimentalWamOptions Options() =>
        ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = TestTenant,
            ClientId = TestClient,
        });

    private sealed class CapturingAuthenticator : IExperimentalWamAuthenticator
    {
        private readonly WamInteractiveResult _result;
        internal List<WamAuthRequest> Requests { get; } = new();
        internal CapturingAuthenticator(WamInteractiveResult result) { _result = result; }
        public Task<WamInteractiveResult> AuthenticateAsync(WamAuthRequest request, IntPtr parentWindow, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_result);
        }
    }

    private static string LocateAppSourceDir()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "src", "PAXCookbook.App");
            if (Directory.Exists(candidate)) { return candidate; }
            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("src/PAXCookbook.App not found from test base dir");
    }

    private static string ReadAppSource(string fileName) =>
        File.ReadAllText(Path.Combine(LocateAppSourceDir(), fileName));

    // Doctrine comments legitimately NAME the forbidden APIs in order to forbid
    // them, so a raw text scan reports a false positive. Only executable source
    // is scanned.
    private static string StripComments(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        bool inBlock = false;
        foreach (string rawLine in source.Split('\n'))
        {
            string line = rawLine;
            if (inBlock)
            {
                int end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) { continue; }
                line = line.Substring(end + 2);
                inBlock = false;
            }

            int block = line.IndexOf("/*", StringComparison.Ordinal);
            if (block >= 0) { line = line.Substring(0, block); inBlock = true; }

            int slash = line.IndexOf("//", StringComparison.Ordinal);
            if (slash >= 0) { line = line.Substring(0, slash); }

            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    // ---- A: the production window bridge forces account selection ----------

    [Fact]
    public async Task WindowBridge_ProductionSessionAcquisition_UsesForceAccountSelection()
    {
        var authenticator = new CapturingAuthenticator(
            WamInteractiveResult.Success("acct-1", new[] { "User.Read" }, TestTenant, "obj-1"));
        var bridge = new ExperimentalWamWindowBridge(Options(), authenticator, () => new IntPtr(1234));
        WamNativeDescriptor descriptor = WamNativeDescriptor.ForSession();

        await bridge.AcquireAsync("req-1", descriptor, CancellationToken.None);

        Assert.Single(authenticator.Requests);
        Assert.Equal(WorkAccountAcquisitionMode.ForceAccountSelection, authenticator.Requests[0].AcquisitionMode);
    }

    [Fact]
    public async Task WindowBridge_EverySessionAcquisition_ForcesSelection_NotJustTheFirst()
    {
        var authenticator = new CapturingAuthenticator(
            WamInteractiveResult.Success("acct-1", new[] { "User.Read" }, TestTenant, "obj-1"));
        var bridge = new ExperimentalWamWindowBridge(Options(), authenticator, () => new IntPtr(1234));
        WamNativeDescriptor descriptor = WamNativeDescriptor.ForSession();

        await bridge.AcquireAsync("req-1", descriptor, CancellationToken.None);
        await bridge.AcquireAsync("req-2", descriptor, CancellationToken.None);

        Assert.Equal(2, authenticator.Requests.Count);
        Assert.All(authenticator.Requests,
            r => Assert.Equal(WorkAccountAcquisitionMode.ForceAccountSelection, r.AcquisitionMode));
    }

#if EXPERIMENTAL_WAM
    // ---- B: Force mode neither binds nor persists preferred state ----------

    [Fact]
    public void ForceMode_NeverBindsAPreferredAccount_EvenWhenOneIsStoredAndAvailable()
    {
        WorkAccountInteractiveAcquisitionPlan plan = WorkAccountInteractiveAcquisitionPlan.Create(
            WorkAccountAcquisitionMode.ForceAccountSelection,
            storedReference: "stored-handle",
            availableIdentifiers: new[] { "stored-handle" });

        Assert.False(plan.BindPreferredAccount);
        Assert.Null(plan.BoundIdentifier);
        Assert.True(plan.RequireAccountPicker);
    }

    [Fact]
    public void PreferredMode_StillBinds_SoTheForceModeAssertionIsNotVacuous()
    {
        WorkAccountInteractiveAcquisitionPlan plan = WorkAccountInteractiveAcquisitionPlan.Create(
            WorkAccountAcquisitionMode.UsePreferredAccount,
            storedReference: "stored-handle",
            availableIdentifiers: new[] { "stored-handle" });

        Assert.True(plan.BindPreferredAccount);
        Assert.Equal("stored-handle", plan.BoundIdentifier);
    }

    [Fact]
    public void ForceMode_NeverReplacesPreferredAccount_EvenAfterAFullyValidAcquisition()
    {
        WamInteractiveResult valid =
            WamInteractiveResult.Success("acct-1", new[] { "User.Read" }, TestTenant, "obj-1");

        Assert.False(WorkAccountPreferredAccountReplacementPolicy.ShouldReplace(
            valid, Options(), WorkAccountAcquisitionMode.ForceAccountSelection));
    }

    [Fact]
    public void PreferredMode_StillReplaces_SoTheForceModeSaveAssertionIsNotVacuous()
    {
        WamInteractiveResult valid =
            WamInteractiveResult.Success("acct-1", new[] { "User.Read" }, TestTenant, "obj-1");

        Assert.True(WorkAccountPreferredAccountReplacementPolicy.ShouldReplace(
            valid, Options(), WorkAccountAcquisitionMode.UsePreferredAccount));
    }

    [Fact]
    public void ForceMode_NeverReplaces_OnAFailedAcquisitionEither()
    {
        WamInteractiveResult failed = WamInteractiveResult.Failure(WamAcquireStatus.UserCancelled);

        Assert.False(WorkAccountPreferredAccountReplacementPolicy.ShouldReplace(
            failed, Options(), WorkAccountAcquisitionMode.ForceAccountSelection));
        Assert.False(WorkAccountPreferredAccountReplacementPolicy.ShouldReplace(
            failed, Options(), WorkAccountAcquisitionMode.UsePreferredAccount));
    }
#endif

    // ---- B: the mandatory prompt and the forbidden silent API -------------

    [Fact]
    public void Authenticator_AlwaysAppliesSelectAccountPrompt_AndNeverForcesLogin()
    {
        string source = StripComments(ReadAppSource("MsalEntraWamAuthenticator.cs"));

        Assert.Contains("WithPrompt(Prompt.SelectAccount)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Prompt.ForceLogin", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSources_ContainNoSilentAcquisitionApi()
    {
        foreach (string file in Directory.EnumerateFiles(LocateAppSourceDir(), "*.cs", SearchOption.AllDirectories))
        {
            string source = StripComments(File.ReadAllText(file));
            Assert.DoesNotContain("AcquireTokenSilent", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WindowBridge_HardCodesForceSelection_AndNeverTakesTheModeFromACaller()
    {
        string source = StripComments(ReadAppSource("ExperimentalWamWindowBridge.cs"));

        // The mode must be a hard-coded production decision.
        Assert.Contains("WorkAccountAcquisitionMode.ForceAccountSelection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("UsePreferredAccount", source, StringComparison.Ordinal);

        // and never a value the renderer, HTTP layer, configuration, or
        // environment can supply through a parameter or field.
        Assert.DoesNotContain("WorkAccountAcquisitionMode mode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkAccountAcquisitionMode acquisitionMode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkAccountAcquisitionMode _", source, StringComparison.Ordinal);
    }

    // ---- C: two acquisitions separated by an explicit Lock -----------------

    [Fact]
    public void TwoSessionRequests_AcrossAnExplicitLock_AreDistinctAndNonReusable()
    {
        var endpoint = new ExperimentalWamDaemonEndpoint(Options());

        string? first = endpoint.Initiate(WamAuthPurpose.SessionUnlock);
        string? second = endpoint.Initiate(WamAuthPurpose.SessionUnlock);

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.False(string.IsNullOrWhiteSpace(second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ATerminalRequest_CannotBeReplayedToUnlockAgain()
    {
        var endpoint = new ExperimentalWamDaemonEndpoint(Options());
        string? requestId = endpoint.Initiate(WamAuthPurpose.SessionUnlock);
        Assert.NotNull(requestId);

        WamNativeDescriptor first = endpoint.LookupDescriptor(requestId);
        Assert.True(first.Found);

        DaemonWamOutcome approved = endpoint.ApplyNativeResult(NeutralWamResult.Approved(requestId!));
        Assert.True(approved.Approved);

        // Replaying the same terminal request must not approve a second unlock.
        DaemonWamOutcome replay = endpoint.ApplyNativeResult(NeutralWamResult.Approved(requestId!));
        Assert.False(replay.Approved);
    }

    // ---- E: explicit different-account clearing still works ----------------

    [Fact]
    public void ExplicitDifferentAccountClear_IsStillDistinctFromSessionUnlock()
    {
        // Force mode must make the preferred file inert for session unlock, but it
        // must NOT remove the explicit clear path that "Use a different work
        // account" depends on.
        string source = ReadAppSource("MsalEntraWamAuthenticator.cs");

        Assert.Contains("ClearPreferredAccount", source, StringComparison.Ordinal);
        Assert.Contains("_preferredAccountStore.Clear()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LockAlone_DoesNotDeleteThePreferredAccountFile()
    {
        // The ruling permits the stored reference to remain on disk; it must simply
        // be unable to suppress the picker. A Lock-triggered delete would violate
        // the V5 step-11 byte-identical requirement.
        string source = ReadAppSource("BrokerLock.cs");

        Assert.DoesNotContain("ClearPreferredAccount", source, StringComparison.Ordinal);
        Assert.DoesNotContain("work-account-preferred", source, StringComparison.Ordinal);
    }
}
