#if EXPERIMENTAL_WAM
using System;
using System.IO;
using System.Text;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// Batch 2 — protected preferred-account continuity (EXPERIMENTAL_WAM only).
//
// Every account reference here is SYNTHETIC: no real HomeAccountId, UPN, tenant,
// object id, client id, token, or photo appears. The DPAPI round trip runs under
// the test user's CurrentUser scope against an isolated temp localAppData base,
// so nothing touches the real install. The tests prove: the protected round trip,
// that the persisted ciphertext never contains the plaintext reference, the exact
// resolution branches (unique/zero/multiple/no-preference/malformed), that the
// stored reference is replaced ONLY after a fully validated authorization, that
// the native different-account clear removes the reference and acks truthfully,
// and (by source assertion) that no silent acquisition path exists.
public sealed class WorkAccountPreferredAccountTests
{
    // A synthetic HomeAccountId.Identifier shape: "<object>.<tenant>".
    private const string SyntheticReference =
        "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    private const string SyntheticTenant = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string SyntheticClient = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    private static string NewIsolatedBase()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pax_pref_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string baseDir)
    {
        try { Directory.Delete(baseDir, recursive: true); } catch { }
    }

    // ---- fakes --------------------------------------------------------------

    private sealed class RecordingSink : IWorkAccountProfilePresentationSink
    {
        internal int PublishCount { get; private set; }
        internal int ClearCount { get; private set; }
        public void Publish(WorkAccountProfilePresentation presentation) => PublishCount++;
        public void Clear() => ClearCount++;
    }

    private sealed class FailingClearStore : IWorkAccountPreferredAccountStore
    {
        public bool TrySave(string accountReference) => false;
        public string? TryLoad() => null;
        public bool Clear() => false; // clear failure
    }

    private sealed class RecordingStore : IWorkAccountPreferredAccountStore
    {
        internal int SaveCount { get; private set; }
        public bool TrySave(string accountReference) { SaveCount++; return true; }
        public string? TryLoad() => null;
        public bool Clear() => true;
    }

    private sealed class RecordingPhotoFetcher : IWorkAccountProfilePhotoFetcher
    {
        internal int FetchCount { get; private set; }
        public System.Threading.Tasks.Task<WorkAccountPhotoFetchResult> FetchAsync(
            string accessToken, System.Threading.CancellationToken cancellationToken)
        {
            FetchCount++;
            return System.Threading.Tasks.Task.FromResult(
                WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.NotFound));
        }
    }

    // ---- DPAPI store round trip + no-plaintext ------------------------------

    [Fact]
    public void Store_ProtectUnprotect_RoundTrips()
    {
        string baseDir = NewIsolatedBase();
        try
        {
            var store = new WorkAccountPreferredAccountStore(baseDir);
            Assert.True(store.TrySave(SyntheticReference));
            Assert.Equal(SyntheticReference, store.TryLoad());
        }
        finally { Cleanup(baseDir); }
    }

    [Fact]
    public void Store_PersistedCiphertext_DoesNotContainPlaintextReference()
    {
        string baseDir = NewIsolatedBase();
        try
        {
            var store = new WorkAccountPreferredAccountStore(baseDir);
            Assert.True(store.TrySave(SyntheticReference));

            string path = WorkAccountPreferredAccountStore.ResolvePreferredPath(baseDir);
            byte[] onDisk = File.ReadAllBytes(path);

            byte[] utf8 = Encoding.UTF8.GetBytes(SyntheticReference);
            byte[] utf16 = Encoding.Unicode.GetBytes(SyntheticReference);

            Assert.False(ContainsSequence(onDisk, utf8),
                "persisted blob contains the UTF8 plaintext reference.");
            Assert.False(ContainsSequence(onDisk, utf16),
                "persisted blob contains the UTF16 plaintext reference.");

            // And the ciphertext is not trivially the raw plaintext.
            Assert.NotEqual(utf8.Length, onDisk.Length);
        }
        finally { Cleanup(baseDir); }
    }

    [Fact]
    public void Store_MissingFile_TryLoadReturnsNull_ForcesSelect()
    {
        string baseDir = NewIsolatedBase();
        try
        {
            var store = new WorkAccountPreferredAccountStore(baseDir);
            Assert.Null(store.TryLoad());

            WorkAccountAccountResolution decision =
                WorkAccountPreferredAccountResolver.Resolve(store.TryLoad(), Array.Empty<string>());
            Assert.False(decision.BindToAccount);
        }
        finally { Cleanup(baseDir); }
    }

    [Fact]
    public void Store_MalformedBlob_TryLoadReturnsNull_ForcesSelect()
    {
        string baseDir = NewIsolatedBase();
        try
        {
            string path = WorkAccountPreferredAccountStore.ResolvePreferredPath(baseDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 }); // not DPAPI

            var store = new WorkAccountPreferredAccountStore(baseDir);
            Assert.Null(store.TryLoad()); // undecryptable -> no preference

            WorkAccountAccountResolution decision =
                WorkAccountPreferredAccountResolver.Resolve(store.TryLoad(), new[] { SyntheticReference });
            Assert.False(decision.BindToAccount);
        }
        finally { Cleanup(baseDir); }
    }

    // ---- pure resolution branches -------------------------------------------

    [Fact]
    public void Resolver_NoStoredReference_ForcesSelect()
    {
        Assert.False(WorkAccountPreferredAccountResolver.Resolve(null, new[] { SyntheticReference }).BindToAccount);
        Assert.False(WorkAccountPreferredAccountResolver.Resolve("   ", new[] { SyntheticReference }).BindToAccount);
    }

    [Fact]
    public void Resolver_ZeroMatches_ForcesSelect()
    {
        var decision = WorkAccountPreferredAccountResolver.Resolve(
            SyntheticReference,
            new[] { "dddddddd-dddd-dddd-dddd-dddddddddddd.eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee" });
        Assert.False(decision.BindToAccount);
    }

    [Fact]
    public void Resolver_MultipleMatches_ForcesSelect()
    {
        var decision = WorkAccountPreferredAccountResolver.Resolve(
            SyntheticReference,
            new[] { SyntheticReference, SyntheticReference });
        Assert.False(decision.BindToAccount);
    }

    [Fact]
    public void Resolver_UniqueMatch_BindsThatAccount()
    {
        var decision = WorkAccountPreferredAccountResolver.Resolve(
            SyntheticReference,
            new[]
            {
                "dddddddd-dddd-dddd-dddd-dddddddddddd.eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
                SyntheticReference,
            });
        Assert.True(decision.BindToAccount);
        Assert.Equal(SyntheticReference, decision.BoundIdentifier);
    }

    [Fact]
    public void AcquisitionPlan_EveryBranchRequiresAccountPicker()
    {
        WorkAccountInteractiveAcquisitionPlan preferred = WorkAccountInteractiveAcquisitionPlan.Create(
            WorkAccountAcquisitionMode.UsePreferredAccount,
            SyntheticReference,
            new[] { SyntheticReference });
        WorkAccountInteractiveAcquisitionPlan noPreference = WorkAccountInteractiveAcquisitionPlan.Create(
            WorkAccountAcquisitionMode.UsePreferredAccount,
            null,
            new[] { SyntheticReference });
        WorkAccountInteractiveAcquisitionPlan zeroMatches = WorkAccountInteractiveAcquisitionPlan.Create(
            WorkAccountAcquisitionMode.UsePreferredAccount,
            SyntheticReference,
            Array.Empty<string>());
        WorkAccountInteractiveAcquisitionPlan multipleMatches = WorkAccountInteractiveAcquisitionPlan.Create(
            WorkAccountAcquisitionMode.UsePreferredAccount,
            SyntheticReference,
            new[] { SyntheticReference, SyntheticReference });
        WorkAccountInteractiveAcquisitionPlan forceSelect = WorkAccountInteractiveAcquisitionPlan.Create(
            WorkAccountAcquisitionMode.ForceAccountSelection,
            SyntheticReference,
            new[] { SyntheticReference });

        Assert.True(preferred.BindPreferredAccount);
        Assert.All(
            new[] { preferred, noPreference, zeroMatches, multipleMatches, forceSelect },
            plan => Assert.True(plan.RequireAccountPicker));
        Assert.All(
            new[] { noPreference, zeroMatches, multipleMatches, forceSelect },
            plan => Assert.False(plan.BindPreferredAccount));
    }

    // ---- replacement policy: only after validated success -------------------

    [Fact]
    public void ReplacementPolicy_ReplacesOnlyAfterValidatedSuccess()
    {
        ExperimentalWamOptions options = ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = ExperimentalWamOptions.EntraWamProviderId,
            TenantId = SyntheticTenant,
            ClientId = SyntheticClient,
        });
        Assert.True(options.IsFullyConfigured);

        // Fully valid, tenant-matched, single User.Read -> replace.
        // The mode is stated explicitly: these cases pin the VALIDATION semantics,
        // which only apply on the preferred-account path. Force-selection
        // suppression is covered separately in PostLockPickerEnforcementTests.
        WamInteractiveResult valid = WamInteractiveResult.Success(
            SyntheticReference, new[] { "User.Read" }, SyntheticTenant, "obj-synthetic");
        Assert.True(WorkAccountPreferredAccountReplacementPolicy.ShouldReplace(
            valid, options, WorkAccountAcquisitionMode.UsePreferredAccount));

        // Tenant mismatch -> never replace.
        WamInteractiveResult wrongTenant = WamInteractiveResult.Success(
            SyntheticReference, new[] { "User.Read" }, SyntheticClient, "obj-synthetic");
        Assert.False(WorkAccountPreferredAccountReplacementPolicy.ShouldReplace(
            wrongTenant, options, WorkAccountAcquisitionMode.UsePreferredAccount));

        // Unexpected scope -> never replace.
        WamInteractiveResult badScope = WamInteractiveResult.Success(
            SyntheticReference, new[] { "User.Read", "Mail.Read" }, SyntheticTenant, "obj-synthetic");
        Assert.False(WorkAccountPreferredAccountReplacementPolicy.ShouldReplace(
            badScope, options, WorkAccountAcquisitionMode.UsePreferredAccount));

        // Not-succeeded (failure/cancel shape) -> never replace.
        WamInteractiveResult failed = WamInteractiveResult.Failure(WamAcquireStatus.UserCancelled);
        Assert.False(WorkAccountPreferredAccountReplacementPolicy.ShouldReplace(
            failed, options, WorkAccountAcquisitionMode.UsePreferredAccount));
    }

    // ---- native different-account clear -------------------------------------

    [Fact]
    public void ClearPreferredAccount_RemovesReference_ClearsSink_ReturnsCleared()
    {
        string baseDir = NewIsolatedBase();
        try
        {
            var store = new WorkAccountPreferredAccountStore(baseDir);
            Assert.True(store.TrySave(SyntheticReference));
            Assert.NotNull(store.TryLoad());

            var sink = new RecordingSink();
            var authenticator = new MsalEntraWamAuthenticator(
                new NoopPhotoFetcher(), sink, store);

            WorkAccountPreferredClearAck ack = authenticator.ClearPreferredAccount();

            Assert.Equal(WorkAccountPreferredClearAck.Cleared, ack);
            Assert.Null(store.TryLoad()); // reference removed
            Assert.Equal(1, sink.ClearCount); // in-memory presentation cleared
        }
        finally { Cleanup(baseDir); }
    }

    [Fact]
    public void ClearPreferredAccount_StoreFailure_ReturnsFailed()
    {
        var authenticator = new MsalEntraWamAuthenticator(new FailingClearStore());
        Assert.Equal(WorkAccountPreferredClearAck.Failed, authenticator.ClearPreferredAccount());
    }

    // ---- no silent unlock / interactive binding (source assertion) ----------

    [Fact]
    public void Acquisition_UsesExactlyOneCommonPickerAuthority_AndBindsInteractively()
    {
        string src = ReadAppSource("MsalEntraWamAuthenticator.cs");
        string store = ReadAppSource("WorkAccountPreferredAccountStore.cs");

        // No silent-acquisition CALL anywhere in the acquisition path or the
        // store. The call form (with the opening paren) is asserted so the
        // doctrine wording in comments is not what is being matched.
        Assert.DoesNotContain("AcquireTokenSilent(", src);
        Assert.DoesNotContain("AcquireTokenSilent(", store);
        Assert.DoesNotContain(".WithPrompt(Prompt.ForceLogin)", src);

        // Interactive binding for the unique preferred account, and explicit
        // account selection for every branch.
        Assert.Contains(".WithAccount(", src);
        Assert.Equal(1, CountOccurrences(src, ".WithPrompt(Prompt.SelectAccount)"));
        Assert.Contains("AcquireTokenInteractive", src);
        Assert.Equal(1, CountOccurrences(
            src,
            "AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount"));
        Assert.Equal(0, CountOccurrences(src, "AadAuthorityAudience.AzureAdMultipleOrgs"));
        Assert.Equal(0, CountOccurrences(src, ".WithTenantId("));
        Assert.Equal(0, CountOccurrences(src, ".WithPrompt(Prompt.ForceLogin)"));
        Assert.Equal(0, CountOccurrences(src, "AcquireTokenSilent("));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task InvalidTenantOrScope_HasZeroProfileSideEffects(bool badScope)
    {
        var photo = new RecordingPhotoFetcher();
        var sink = new RecordingSink();
        var store = new RecordingStore();
        var authenticator = new MsalEntraWamAuthenticator(photo, sink, store);
        var request = new WamAuthRequest(
            WamAuthPurpose.SessionUnlock,
            SyntheticTenant,
            SyntheticClient,
            ExperimentalWamOptions.GraphUserReadScope);
        WamInteractiveResult sanitized = WamInteractiveResult.Success(
            SyntheticReference,
            badScope ? new[] { "User.Read", "Mail.Read" } : new[] { "User.Read" },
            badScope ? SyntheticTenant : SyntheticClient,
            "synthetic-object");

        WamInteractiveResult result = await authenticator.CompleteValidatedAcquisitionAsync(
            sanitized,
            request,
            "synthetic-access-token",
            "synthetic@example.invalid",
            System.Threading.CancellationToken.None);

        Assert.Equal(
            badScope ? WamAcquireStatus.ScopeFailure : WamAcquireStatus.IdentityFailure,
            result.Status);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(0, photo.FetchCount);
        Assert.Equal(0, sink.PublishCount);
    }

    // ---- helpers ------------------------------------------------------------

    private sealed class NoopPhotoFetcher : IWorkAccountProfilePhotoFetcher
    {
        public System.Threading.Tasks.Task<WorkAccountPhotoFetchResult> FetchAsync(
            string accessToken, System.Threading.CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.FromResult(
                WorkAccountPhotoFetchResult.Failure(WorkAccountPhotoOutcome.NotFound));
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) { return true; }
        }
        return false;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string ReadAppSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
            {
                return File.ReadAllText(Path.Combine(dir.FullName, "src", "PAXCookbook.App", fileName));
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root (PAXCookbook.sln).");
    }
}
#endif
