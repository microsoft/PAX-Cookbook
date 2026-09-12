using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S2A native request-binding + lifecycle tests — deterministic adversarial
// tests. No live WAM/MSAL/pipe/HWND: a fake authenticator, a fake native
// channel, and an injected clock stand in. The renderer is proven off the
// security-critical path, purpose/challenge are daemon-owned, and only a native
// result keyed by the opaque requestId can unlock the session.
public sealed class ExperimentalWamIntegrationTests
{
    private const string TestTenant = "11111111-1111-1111-1111-111111111111";
    private const string TestClient = "22222222-2222-2222-2222-222222222222";
    private const string Initiate = "/api/v1/broker/experimental/wam/initiate";
    private const string Status = "/api/v1/broker/experimental/wam/status";
    private const string Result = "/api/v1/broker/experimental/wam/result";
    private const string RequestType = "cookbook:experimental-wam-request";

    private static ExperimentalWamOptions Options() =>
        ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = TestTenant,
            ClientId = TestClient,
        });

    private static WamInteractiveResult ValidResult(string account = "acct-1") =>
        WamInteractiveResult.Success(account, new[] { "User.Read" }, TestTenant, "obj-1");

    private static string BareMessage(string requestId) =>
        $"{{\"type\":\"{RequestType}\",\"requestId\":\"{requestId}\"}}";

    private sealed class FakeAuthenticator : IExperimentalWamAuthenticator
    {
        private readonly WamInteractiveResult _result;
        internal WamAuthRequest? LastRequest { get; private set; }
        internal FakeAuthenticator(WamInteractiveResult result) { _result = result; }
        public Task<WamInteractiveResult> AuthenticateAsync(WamAuthRequest request, IntPtr parentWindow, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeChannel : IExperimentalWamNativeChannel
    {
        private readonly WamNativeDescriptor _descriptor;
        internal int LookupCount { get; private set; }
        internal int SubmitCount { get; private set; }
        internal NeutralWamResult? Submitted { get; private set; }
        internal FakeChannel(WamNativeDescriptor descriptor) { _descriptor = descriptor; }
        public bool TryLookup(string requestId, out WamNativeDescriptor descriptor)
        {
            LookupCount++;
            descriptor = _descriptor;
            return descriptor.Found;
        }
        public bool TrySubmit(NeutralWamResult result)
        {
            SubmitCount++;
            Submitted = result;
            return true;
        }
    }

    private static string LocateAppSourceDir()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "src", "PAXCookbook.App");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("src/PAXCookbook.App not found from test base dir");
    }

    // ---- locked-route reachability (preserved) --------------------------

    [Fact]
    public void LockedAllowList_ContainsExactInitiateAndStatus_NotResult()
    {
        Assert.True(BrokerLock.IsRouteAllowedWhenLocked("POST", Initiate));
        Assert.True(BrokerLock.IsRouteAllowedWhenLocked("POST", Status));
        Assert.False(BrokerLock.IsRouteAllowedWhenLocked("POST", Result));
        Assert.False(BrokerLock.IsRouteAllowedWhenLocked("POST", "/api/v1/broker/experimental/wam/anything-else"));
        Assert.False(BrokerLock.IsRouteAllowedWhenLocked("GET", Initiate));
    }

    // ---- forged HTTP result impossible; only native grants (preserved) --

    [Fact]
    public void ForgedRequestId_GrantsNothing()
    {
        int unlocks = 0;
        var endpoint = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(),
            new SessionUnlockCoordinator(() => unlocks++));

        DaemonWamOutcome outcome = endpoint.ApplyNativeResult(NeutralWamResult.Approved("forged-request-id"));

        Assert.False(outcome.Approved);
        Assert.Equal(DaemonWamReason.RequestNotFound, outcome.Reason);
        Assert.Equal(0, unlocks);
    }

    [Fact]
    public void HttpRoutes_InitiateAndStatus_DoNotGrant()
    {
        int unlocks = 0;
        var endpoint = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(),
            new SessionUnlockCoordinator(() => unlocks++));

        (int initStatus, _) = ExperimentalWamRoutes.HandleInitiate(endpoint,
            System.Text.Json.JsonDocument.Parse("{\"purpose\":\"session\"}").RootElement);
        (int statusStatus, _) = ExperimentalWamRoutes.HandleStatus(endpoint, "whatever");

        Assert.Equal(200, initStatus);
        Assert.Equal(200, statusStatus);
        Assert.Equal(0, unlocks);
    }

    [Fact]
    public void NativeChannel_SessionResult_Unlocks()
    {
        int unlocks = 0;
        var endpoint = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(),
            new SessionUnlockCoordinator(() => unlocks++));

        string requestId = endpoint.Initiate(WamAuthPurpose.SessionUnlock)!;
        string response = ExperimentalWamPipe.HandleServerMessage(
            endpoint, ExperimentalWamPipe.SerializeResultRequest(NeutralWamResult.Approved(requestId)));

        Assert.Contains("\"approved\":true", response);
        Assert.Equal(1, unlocks);
    }

    // ---- replay / expiry terminal (preserved) ---------------------------

    [Fact]
    public void Replay_SecondApply_IsTerminal()
    {
        int unlocks = 0;
        var endpoint = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(),
            new SessionUnlockCoordinator(() => unlocks++));

        string requestId = endpoint.Initiate(WamAuthPurpose.SessionUnlock)!;
        Assert.True(endpoint.ApplyNativeResult(NeutralWamResult.Approved(requestId)).Approved);
        DaemonWamOutcome replay = endpoint.ApplyNativeResult(NeutralWamResult.Approved(requestId));

        Assert.False(replay.Approved);
        Assert.Equal(DaemonWamReason.RequestNotFound, replay.Reason);
        Assert.Equal(1, unlocks);
    }

    [Fact]
    public void Expired_Request_IsTerminal()
    {
        DateTime now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        int unlocks = 0;
        var endpoint = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(() => now),
            new SessionUnlockCoordinator(() => unlocks++), () => now);

        string requestId = endpoint.Initiate(WamAuthPurpose.SessionUnlock)!;
        now = now.AddSeconds(ExperimentalWamDaemonEndpoint.RequestTtlSeconds + 1);
        DaemonWamOutcome outcome = endpoint.ApplyNativeResult(NeutralWamResult.Approved(requestId));

        Assert.False(outcome.Approved);
        Assert.Equal(0, unlocks);
    }

    // ---- session unlock exactly once (preserved) ------------------------

    [Fact]
    public void SessionApproval_UnlocksExactlyOnce()
    {
        int unlocks = 0;
        var endpoint = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(),
            new SessionUnlockCoordinator(() => unlocks++));

        string requestId = endpoint.Initiate(WamAuthPurpose.SessionUnlock)!;
        DaemonWamOutcome outcome = endpoint.ApplyNativeResult(NeutralWamResult.Approved(requestId));

        Assert.True(outcome.Approved);
        Assert.Equal(1, unlocks);
        Assert.Equal(ExperimentalWamRequestState.Approved, endpoint.GetStatus(requestId));
    }

    // ---- native descriptor: session lookup, non-consuming --------------

    [Fact]
    public void Descriptor_Session_IsFound_WithSessionPurpose()
    {
        var endpoint = new ExperimentalWamDaemonEndpoint(Options());
        string sReq = endpoint.Initiate(WamAuthPurpose.SessionUnlock)!;

        WamNativeDescriptor d = endpoint.LookupDescriptor(sReq);

        Assert.True(d.Found);
        Assert.Equal(WamAuthPurpose.SessionUnlock, d.Purpose);
    }

    [Fact]
    public void DescriptorLookup_IsNonConsuming_ButResultIsSingleUse()
    {
        var endpoint = new ExperimentalWamDaemonEndpoint(Options());
        string sReq = endpoint.Initiate(WamAuthPurpose.SessionUnlock)!;

        Assert.True(endpoint.LookupDescriptor(sReq).Found);
        Assert.True(endpoint.LookupDescriptor(sReq).Found); // repeated lookup still valid
        Assert.Equal(ExperimentalWamRequestState.Pending, endpoint.GetStatus(sReq));

        Assert.True(endpoint.ApplyNativeResult(NeutralWamResult.Approved(sReq)).Approved);
        Assert.False(endpoint.ApplyNativeResult(NeutralWamResult.Approved(sReq)).Approved); // single use
        Assert.False(endpoint.LookupDescriptor(sReq).Found); // consumed -> fail closed
    }

    [Fact]
    public void DescriptorLookup_Unknown_Expired_Consumed_FailClosed()
    {
        DateTime now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var endpoint = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(() => now),
            new SessionUnlockCoordinator(() => { }), () => now);

        Assert.Equal(WamDescriptorReason.Unknown, endpoint.LookupDescriptor("never-minted").Reason);

        string consumed = endpoint.Initiate(WamAuthPurpose.SessionUnlock)!;
        endpoint.ApplyNativeResult(NeutralWamResult.Approved(consumed));
        Assert.False(endpoint.LookupDescriptor(consumed).Found); // terminal/consumed

        string expiring = endpoint.Initiate(WamAuthPurpose.SessionUnlock)!;
        now = now.AddSeconds(ExperimentalWamDaemonEndpoint.RequestTtlSeconds + 1);
        Assert.False(endpoint.LookupDescriptor(expiring).Found); // expired -> fail closed
    }

    // ---- lifecycle: expiry -> terminal, tombstone -> pruned -------------

    [Fact]
    public void AbandonedPending_BecomesTerminal_ViaStatusMaintenance_LateResultGrantsNothing()
    {
        DateTime now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        int unlocks = 0;
        var endpoint = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(() => now),
            new SessionUnlockCoordinator(() => unlocks++), () => now);

        string sReq = endpoint.Initiate(WamAuthPurpose.SessionUnlock)!;
        Assert.Equal(ExperimentalWamRequestState.Pending, endpoint.GetStatus(sReq));

        now = now.AddSeconds(ExperimentalWamDaemonEndpoint.RequestTtlSeconds + 1);
        // Status maintenance transitions the abandoned request to terminal Denied.
        Assert.Equal(ExperimentalWamRequestState.Denied, endpoint.GetStatus(sReq));

        // A late native result grants nothing and cannot revive authority.
        Assert.False(endpoint.ApplyNativeResult(NeutralWamResult.Approved(sReq)).Approved);
        Assert.Equal(0, unlocks);
    }

    [Fact]
    public void TerminalTombstone_ObservableThenPrunedToUnknown()
    {
        DateTime now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var endpoint = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(() => now),
            new SessionUnlockCoordinator(() => { }), () => now);

        string sReq = endpoint.Initiate(WamAuthPurpose.SessionUnlock)!;
        endpoint.ApplyNativeResult(NeutralWamResult.Approved(sReq));
        Assert.Equal(ExperimentalWamRequestState.Approved, endpoint.GetStatus(sReq)); // tombstone observable

        now = now.AddSeconds(ExperimentalWamDaemonEndpoint.RequestTtlSeconds + 1);
        Assert.Equal(ExperimentalWamRequestState.Unknown, endpoint.GetStatus(sReq)); // pruned
    }

    // ---- terminal authority destruction (inspect the ACTUAL private record) --

    // Reflect over the endpoint's private _pending dictionary and return the
    // request record object for a requestId (or null when pruned/absent). This
    // inspects the REAL object graph, not a status or a source comment.
    private static object? TerminalRecord(ExperimentalWamDaemonEndpoint ep, string requestId)
    {
        FieldInfo f = typeof(ExperimentalWamDaemonEndpoint).GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (System.Collections.IDictionary)f.GetValue(ep)!;
        return dict.Contains(requestId) ? dict[requestId] : null;
    }

    // Prove that the terminal record carries NO reconstructable request authority:
    // it is the minimal tombstone type, it exposes no purpose/recipe/generation/
    // challenge/salt/descriptor member, and it retains no non-empty string field.
    private static void AssertTerminalRecordHasNoAuthority(object? record)
    {
        Assert.NotNull(record);
        Type t = record!.GetType();
        Assert.Equal("TerminalTombstone", t.Name);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (string authority in new[] { "Purpose", "RecipeId", "CapturedGeneration", "Challenge", "Salt", "IdentitySalt", "Descriptor", "ExpectedSessionFingerprint" })
        {
            Assert.Null(t.GetField(authority, all));
            Assert.Null(t.GetProperty(authority, all));
        }

        // No string field may retain a residual challenge/recipe/fingerprint value.
        foreach (FieldInfo fld in t.GetFields(all))
        {
            if (fld.FieldType == typeof(string))
            {
                Assert.True(string.IsNullOrEmpty((string?)fld.GetValue(record)),
                    $"terminal record string field {fld.Name} retained data");
            }
        }
    }

    [Fact]
    public void TerminalRecord_ApprovedSession_HasNoAuthority()
    {
        var ep = new ExperimentalWamDaemonEndpoint(Options());
        string s = ep.Initiate(WamAuthPurpose.SessionUnlock)!;
        Assert.True(ep.ApplyNativeResult(NeutralWamResult.Approved(s)).Approved);

        AssertTerminalRecordHasNoAuthority(TerminalRecord(ep, s));
        Assert.Equal(ExperimentalWamRequestState.Approved, ep.GetStatus(s)); // observable during retention
        Assert.False(ep.LookupDescriptor(s).Found);                          // lookup fails after terminal
        Assert.False(ep.ApplyNativeResult(NeutralWamResult.Approved(s)).Approved); // replay grants nothing
    }

    [Fact]
    public void TerminalRecord_DeniedNeutralResult_HasNoAuthority()
    {
        var ep = new ExperimentalWamDaemonEndpoint(Options());
        string s = ep.Initiate(WamAuthPurpose.SessionUnlock)!;
        Assert.False(ep.ApplyNativeResult(NeutralWamResult.Rejected(s, NeutralWamCategory.Denied)).Approved);

        AssertTerminalRecordHasNoAuthority(TerminalRecord(ep, s));
    }

    [Fact]
    public void TerminalRecord_ExpiredAbandoned_HasNoAuthority_ThenPrunes()
    {
        DateTime now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var ep = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(() => now),
            new SessionUnlockCoordinator(() => { }), () => now);
        string s = ep.Initiate(WamAuthPurpose.SessionUnlock)!;

        now = now.AddSeconds(ExperimentalWamDaemonEndpoint.RequestTtlSeconds + 1);
        Assert.Equal(ExperimentalWamRequestState.Denied, ep.GetStatus(s)); // abandoned -> terminal tombstone
        AssertTerminalRecordHasNoAuthority(TerminalRecord(ep, s));

        now = now.AddSeconds(ExperimentalWamDaemonEndpoint.RequestTtlSeconds + 1);
        Assert.Equal(ExperimentalWamRequestState.Unknown, ep.GetStatus(s)); // pruned
        Assert.Null(TerminalRecord(ep, s));
    }

    [Fact]
    public void ForgedRequestId_CreatesNoRecord()
    {
        var ep = new ExperimentalWamDaemonEndpoint(Options());
        Assert.False(ep.ApplyNativeResult(NeutralWamResult.Approved("forged-id")).Approved);
        Assert.Null(TerminalRecord(ep, "forged-id"));
    }

    // ---- renderer message: requestId-only, strict rejection -------------

    [Fact]
    public void RendererMessage_BareRequestId_Parsed()
    {
        Assert.Equal("req-1", ExperimentalWamWindowMessage.TryParseRequestId(BareMessage("req-1")));
    }

    [Theory]
    [InlineData("purpose", "\"session\"")]
    [InlineData("recipeId", "\"recipe-1\"")]
    [InlineData("generation", "5")]
    [InlineData("lockGeneration", "5")]
    [InlineData("provider", "\"entra-wam\"")]
    [InlineData("providerId", "\"entra-wam\"")]
    [InlineData("acquisitionMode", "\"silent\"")]
    public void RendererMessage_WithForbiddenField_Rejected(string field, string value)
    {
        string json = $"{{\"type\":\"{RequestType}\",\"requestId\":\"req-1\",\"{field}\":{value}}}";
        Assert.Null(ExperimentalWamWindowMessage.TryParseRequestId(json));
    }

    [Fact]
    public void RendererMessage_NonExperimental_NotHandled()
    {
        Assert.Null(ExperimentalWamWindowMessage.TryParseRequestId("\"cookbook:close-app\""));
        Assert.Null(ExperimentalWamWindowMessage.TryParseRequestId("{\"type\":\"other\"}"));
        Assert.Null(ExperimentalWamWindowMessage.TryParseRequestId($"{{\"type\":\"{RequestType}\"}}"));
    }

    // ---- window two-stage flow: descriptor drives acquisition -----------

    [Fact]
    public async Task WindowMessage_SessionDescriptor_AcquiresAndSubmitsApproved()
    {
        var fake = new FakeAuthenticator(ValidResult());
        var bridge = new ExperimentalWamWindowBridge(Options(), fake, () => (IntPtr)1);
        var channel = new FakeChannel(WamNativeDescriptor.ForSession());

        Task? t = ExperimentalWamWindowMessage.TryBeginHandle(BareMessage("req-1"), bridge, channel);
        Assert.NotNull(t);
        await t!;

        Assert.Equal(WamAuthPurpose.SessionUnlock, fake.LastRequest!.Purpose);
        Assert.Equal(1, channel.SubmitCount);
        Assert.NotNull(channel.Submitted);
        Assert.True(channel.Submitted!.IsApproved);
        Assert.Equal("req-1", channel.Submitted.RequestId);
    }

    [Fact]
    public async Task WindowMessage_LookupNotFound_NoAcquireNoSubmit()
    {
        var fake = new FakeAuthenticator(ValidResult());
        var bridge = new ExperimentalWamWindowBridge(Options(), fake, () => (IntPtr)1);
        var channel = new FakeChannel(WamNativeDescriptor.NotFound(WamDescriptorReason.Unknown));

        Task? t = ExperimentalWamWindowMessage.TryBeginHandle(BareMessage("req-1"), bridge, channel);
        Assert.NotNull(t);
        await t!;

        Assert.Equal(1, channel.LookupCount);
        Assert.Equal(0, channel.SubmitCount);
        Assert.Null(fake.LastRequest); // authenticator never invoked
    }

    // ---- window bridge (descriptor-driven) ------------------------------

    [Fact]
    public async Task Bridge_ZeroHwnd_FailsClosed()
    {
        var bridge = new ExperimentalWamWindowBridge(Options(), new FakeAuthenticator(ValidResult()), () => IntPtr.Zero);
        NeutralWamResult result = await bridge.AcquireAsync("req-1", WamNativeDescriptor.ForSession(), CancellationToken.None);
        Assert.False(result.IsApproved);
        Assert.Equal(NeutralWamCategory.ConfigurationFailure, result.Category);
    }

    [Fact]
    public async Task Bridge_NotFoundDescriptor_FailsClosed()
    {
        var bridge = new ExperimentalWamWindowBridge(Options(), new FakeAuthenticator(ValidResult()), () => (IntPtr)1);
        NeutralWamResult result = await bridge.AcquireAsync("req-1", WamNativeDescriptor.NotFound(WamDescriptorReason.Expired), CancellationToken.None);
        Assert.False(result.IsApproved);
        Assert.Equal(NeutralWamCategory.ConfigurationFailure, result.Category);
    }

    // ---- pipe: two-stage dispatch + containment -------------------------

    [Fact]
    public void Pipe_Lookup_ReturnsDescriptorResponse()
    {
        var endpoint = new ExperimentalWamDaemonEndpoint(Options());
        string sReq = endpoint.Initiate(WamAuthPurpose.SessionUnlock)!;

        string response = ExperimentalWamPipe.HandleServerMessage(endpoint, ExperimentalWamPipe.SerializeLookupRequest(sReq));
        WamNativeDescriptor? d = ExperimentalWamPipe.TryParseDescriptorResponse(response);

        Assert.NotNull(d);
        Assert.True(d!.Found);
        Assert.Equal(WamAuthPurpose.SessionUnlock, d.Purpose);
    }

    [Fact]
    public void Pipe_MalformedAndUnknownKind_NotApproved()
    {
        var endpoint = new ExperimentalWamDaemonEndpoint(Options());
        Assert.Contains("\"approved\":false", ExperimentalWamPipe.HandleServerMessage(endpoint, "not json"));
        Assert.Contains("\"approved\":false", ExperimentalWamPipe.HandleServerMessage(endpoint, "{}"));
        Assert.Contains("\"approved\":false", ExperimentalWamPipe.HandleServerMessage(endpoint, "{\"kind\":\"result\"}"));
        Assert.Contains("\"approved\":false", ExperimentalWamPipe.HandleServerMessage(endpoint, "{\"kind\":\"bogus\"}"));
    }

    [Fact]
    public void PipeResultSerialization_CarriesNoTokenOrRawClaim()
    {
        string json = ExperimentalWamPipe.SerializeResultRequest(NeutralWamResult.Approved("req-1"));
        string lower = json.ToLowerInvariant();
        Assert.DoesNotContain("token", lower);
        Assert.DoesNotContain("tenantclaim", lower);
        Assert.DoesNotContain("objectclaim", lower);
        Assert.DoesNotContain("accounthandle", lower);
        Assert.DoesNotContain("fingerprint", lower);
        Assert.Contains("requestid", lower);
        Assert.Contains("\"kind\":\"result\"", json);
    }

    [Fact]
    public void PipeDescriptorSerialization_CarriesNoTokenOrRawClaim()
    {
        string json = ExperimentalWamPipe.SerializeDescriptorResponse(WamNativeDescriptor.ForSession());
        string lower = json.ToLowerInvariant();
        Assert.DoesNotContain("token", lower);
        Assert.DoesNotContain("tenantclaim", lower);
        Assert.DoesNotContain("objectclaim", lower);
        Assert.DoesNotContain("accounthandle", lower);
        Assert.DoesNotContain("salthex", lower);
        Assert.Contains("found", lower);
    }

    // ---- fail-closed SID / pipe readiness -------------------------------

    [Fact]
    public void PipeSource_HasNoWorldSidFallback()
    {
        string text = File.ReadAllText(Path.Combine(LocateAppSourceDir(), "ExperimentalWamPipe.cs"));
        // No code path constructs a world-accessible SID (the removed fallback was
        // `new SecurityIdentifier(WellKnownSidType.WorldSid, null)`). The doc
        // comment may describe its ABSENCE, so scan for the concrete API only.
        Assert.DoesNotContain("WellKnownSidType", text);
        Assert.DoesNotContain("WorldSid, null", text);
    }

    [Fact]
    public void Activation_PipeFactoryNull_DisablesProvider()
    {
        var endpoint = new ExperimentalWamDaemonEndpoint(Options());
        ExperimentalWamDaemonEndpoint? active =
            ExperimentalWamActivation.ActivateIfPipeReady(endpoint, () => null, out ExperimentalWamPipeServer? server);
        Assert.Null(active);
        Assert.Null(server);
    }

    [Fact]
    public void Activation_PipeReady_AdvertisesProvider()
    {
        var endpoint = new ExperimentalWamDaemonEndpoint(Options());
        string pipeName = "PAXCookbook.ExperimentalWam.Test." + Guid.NewGuid().ToString("N");
        ExperimentalWamPipeServer? created = ExperimentalWamPipeServer.TryCreate(pipeName, endpoint);
        Assert.NotNull(created); // SID resolves in the test environment (fail-closed readiness proven)
        try
        {
            ExperimentalWamDaemonEndpoint? active =
                ExperimentalWamActivation.ActivateIfPipeReady(endpoint, () => created, out ExperimentalWamPipeServer? server);
            Assert.Same(endpoint, active);
            Assert.Same(created, server);
        }
        finally
        {
            created!.Dispose();
        }
    }

    // ---- WebView message origin (exact canonical origin) ----------------

    [Fact]
    public void WebMessageOrigin_ExactMatchOnly()
    {
        const string expected = "http://127.0.0.1:5000/index.html";
        Assert.True(WebMessageOrigin.IsSameOrigin("http://127.0.0.1:5000/", expected));
        Assert.True(WebMessageOrigin.IsSameOrigin("http://127.0.0.1:5000/app/page", expected));
        Assert.False(WebMessageOrigin.IsSameOrigin("http://127.0.0.1:5001/", expected)); // wrong port
        Assert.False(WebMessageOrigin.IsSameOrigin("https://127.0.0.1:5000/", expected)); // wrong scheme
        Assert.False(WebMessageOrigin.IsSameOrigin("http://evil.example/", expected));    // external
        Assert.False(WebMessageOrigin.IsSameOrigin("http://127.0.0.1.evil.example:5000/", expected)); // no suffix trick
        Assert.False(WebMessageOrigin.IsSameOrigin("not a uri", expected));               // malformed
        Assert.False(WebMessageOrigin.IsSameOrigin(null, expected));
    }

    // ---- host mapping (preserved) ---------------------------------------

    [Fact]
    public void Host_Resolve_FullEnv_IsConfigured()
    {
        var env = new Dictionary<string, string?>
        {
            [ExperimentalWamHost.EnabledEnvVar] = "1",
            [ExperimentalWamHost.ProviderEnvVar] = "entra-wam",
            [ExperimentalWamHost.TenantEnvVar] = TestTenant,
            [ExperimentalWamHost.ClientEnvVar] = TestClient,
        };
        ExperimentalWamOptions options = ExperimentalWamHost.Resolve(name => env.TryGetValue(name, out string? v) ? v : null);
        Assert.True(options.IsFullyConfigured);
        // The daemon endpoint activates only when the real authenticator was
        // compiled in; a default build stays inert even with full configuration.
#if EXPERIMENTAL_WAM
        Assert.NotNull(ExperimentalWamHost.TryCreateDaemonEndpoint(options));
#else
        Assert.Null(ExperimentalWamHost.TryCreateDaemonEndpoint(options));
#endif
    }

    [Fact]
    public void Host_Resolve_Disabled_IsClosed()
    {
        Assert.Null(ExperimentalWamHost.TryCreateDaemonEndpoint(ExperimentalWamHost.Resolve(_ => null)));
    }
}
