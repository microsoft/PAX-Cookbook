using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 58 - INITIATING-USER IDENTITY CHANNEL (Setup only)
// ===========================================================================
//
// SCOPE, stated plainly. These tests run SAME-INTEGRITY: both ends live in
// this one medium-integrity test process. They do NOT elevate, do NOT trigger
// UAC, do NOT use runas, and do NOT re-run the Cycle 57R4 attended
// cross-integrity measurement, which is accepted as settled. What they prove
// here is the part that is integrity-independent and that Cycle 57R4 could not
// prove, because 57R4's implementation lived in a throwaway script: that the
// PRODUCTION channel takes its PID and SID from the live kernel connection,
// refuses a disagreeing command-line hint, and acknowledges ONLY after the
// anchor is durable.
//
// The anchor is written to an OS TEMP directory through the disclosed internal
// seam. Nothing here touches %ProgramData%, installs or contacts a service,
// mutates a certificate or key, opens a network socket, spawns a process, runs
// PAX or starts a Bake.
public sealed class ServiceInitiatingUserIdentityChannelTests : IDisposable
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(15);

    private readonly string _serviceDirectory;

    public ServiceInitiatingUserIdentityChannelTests()
    {
        _serviceDirectory = Path.Combine(
            Path.GetTempPath(), "paxcookbook-cycle58-channel", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_serviceDirectory))
            {
                Directory.Delete(_serviceDirectory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort temp cleanup.
        }
    }

    private string AnchorPath =>
        Path.Combine(_serviceDirectory, ServiceMachineStorageContract.InstallationAnchorFileName);

    private static string CurrentSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Assert.NotNull(identity.User);
        return identity.User!.Value;
    }

    private static ServiceInitiatorProcessFacts CurrentInitiator()
    {
        ServiceInitiatorProcessFacts facts = ServiceInitiatorProcessBinding.CaptureCurrent();
        Assert.True(facts.IsPresent);
        return facts;
    }

    /// <summary>
    /// A server bound to THIS process through the real resolver, i.e. the same
    /// shape the elevated helper builds after parsing its closed arguments.
    /// </summary>
    private static ServiceInitiatingUserIdentityChannelServer CreateBoundServer()
    {
        ServiceInitiatingUserIdentityChannelServer? server =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                ServiceInitiatingUserIdentityChannelContract.NewEndpointName(),
                CurrentInitiator(),
                new WindowsServiceInitiatorIdentityResolver());
        Assert.NotNull(server);
        return server!;
    }

    /// <summary>
    /// Answers the bound-initiator question with a scripted sequence, so a test
    /// can make the FIRST (pre-pipe) answer differ from the SECOND
    /// (post-connection) answer.
    /// </summary>
    private sealed class ScriptedInitiatorIdentityResolver : IServiceInitiatorIdentityResolver
    {
        private readonly string?[] _answers;
        private int _calls;

        internal ScriptedInitiatorIdentityResolver(params string?[] answers) => _answers = answers;

        internal int Calls => _calls;

        public string? TryResolveBoundInitiatorSid(ServiceInitiatorProcessFacts initiator)
        {
            int index = Math.Min(_calls, _answers.Length - 1);
            _calls++;
            return _answers[index];
        }
    }

    // ---- the endpoint is created by the HELPER, and squatting fails ---------

    [Fact]
    public void Endpoint_names_are_fresh_and_the_helper_creates_the_first_pipe_instance()
    {
        string firstName = ServiceInitiatingUserIdentityChannelContract.NewEndpointName();
        string secondName = ServiceInitiatingUserIdentityChannelContract.NewEndpointName();

        Assert.StartsWith(
            ServiceInitiatingUserIdentityChannelContract.EndpointNamePrefix, firstName, StringComparison.Ordinal);
        Assert.NotEqual(firstName, secondName);
        Assert.True(ServiceInitiatingUserIdentityChannelContract.IsCanonicalEndpointName(firstName));
        Assert.True(ServiceInitiatingUserIdentityChannelContract.IsCanonicalEndpointName(secondName));

        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer();

        // The endpoint is LISTENABLE the moment the helper's factory returns,
        // before any client has been told anything, so a plain client connect
        // succeeds against a server that has not yet called WaitForConnection.
        using var probe = new NamedPipeClientStream(
            ".", server.EndpointName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        probe.Connect(2000);
        Assert.True(probe.IsConnected);
    }

    [Fact]
    public void A_squatted_endpoint_name_fails_creation_rather_than_being_hijacked()
    {
        using ServiceInitiatingUserIdentityChannelServer first = CreateBoundServer();

        // FILE_FLAG_FIRST_PIPE_INSTANCE: a second creation of the SAME name is a
        // creation FAILURE, never a silently shared endpoint. This is what makes
        // it safe for the endpoint name to travel on a command line.
        ServiceInitiatingUserIdentityChannelServer? squatter =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                first.EndpointName, CurrentInitiator(), new WindowsServiceInitiatorIdentityResolver());

        Assert.Null(squatter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PAXCookbook.InitiatingUserIdentity.")]
    [InlineData("PAXCookbook.InitiatingUserIdentity.0011223344556677889AABBCCDDEEFF")]
    [InlineData("PAXCookbook.InitiatingUserIdentity.0011223344556677889AABBCCDDEEFF00")]
    [InlineData("PAXCookbook.InitiatingUserIdentity.0011223344556677889aabbccddeeff")]
    [InlineData("PAXCookbook.InitiatingUserIdentity.0011223344556677889AABBCCDDEEFG")]
    [InlineData("SomethingElse.0011223344556677889AABBCCDDEEFF")]
    public void A_non_canonical_endpoint_name_is_refused_before_it_is_ever_used(string candidate)
    {
        Assert.False(ServiceInitiatingUserIdentityChannelContract.IsCanonicalEndpointName(candidate));
        Assert.Null(ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
            candidate, CurrentInitiator(), new WindowsServiceInitiatorIdentityResolver()));
    }

    // ---- CYCLE-59 FIX (a): the DACL admits the INITIATOR, not the approver ---

    [Fact]
    public void The_dacl_is_one_protected_allow_ace_for_the_supplied_initiator_only()
    {
        // A deliberately FOREIGN, well-formed SID that is certainly not this
        // test process, standing in for a standard user whose installation an
        // administrator approved with different credentials.
        var foreign = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001");
        Assert.NotEqual(CurrentSid(), foreign.Value);

        PipeSecurity security = ServiceInitiatingUserIdentityChannelServer.BuildInitiatorOnlySecurity(foreign);

        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));

        PipeAccessRule only = Assert.IsType<PipeAccessRule>(Assert.Single(rules));
        Assert.Equal(foreign, only.IdentityReference);
        Assert.Equal(AccessControlType.Allow, only.AccessControlType);
        Assert.Equal(PipeAccessRights.ReadWrite, only.PipeAccessRights & PipeAccessRights.ReadWrite);
        Assert.True(security.AreAccessRulesProtected);

        // The elevated approver's own account is NOT anywhere in the descriptor.
        Assert.DoesNotContain(
            CurrentSid(),
            security.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_dacl_identity_comes_from_the_bound_initiator_resolver_not_from_the_elevated_account()
    {
        // The resolver, not WindowsIdentity.GetCurrent(), decides. Proven
        // non-vacuously by returning a SID this process does not have: under the
        // cycle-58 behaviour the DACL identity would be the current (approving)
        // account instead.
        const string boundInitiatorSid = "S-1-5-21-1444444444-2555555555-3666666666-1002";
        Assert.NotEqual(CurrentSid(), boundInitiatorSid);

        var resolver = new ScriptedInitiatorIdentityResolver(boundInitiatorSid);
        using ServiceInitiatingUserIdentityChannelServer? server =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                ServiceInitiatingUserIdentityChannelContract.NewEndpointName(), CurrentInitiator(), resolver);

        Assert.NotNull(server);
        Assert.Equal(boundInitiatorSid, server!.DaclIdentitySid);
        Assert.NotEqual(CurrentSid(), server.DaclIdentitySid);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public void An_initiator_whose_sid_cannot_be_resolved_never_gets_an_endpoint()
    {
        Assert.Null(ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
            ServiceInitiatingUserIdentityChannelContract.NewEndpointName(),
            CurrentInitiator(),
            new ScriptedInitiatorIdentityResolver(new string?[] { null })));

        // A pid + FILETIME pair that names no live process instance is equally
        // refused, through the REAL resolver.
        ServiceInitiatorProcessFacts real = CurrentInitiator();
        Assert.Null(ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
            ServiceInitiatingUserIdentityChannelContract.NewEndpointName(),
            new ServiceInitiatorProcessFacts(real.ProcessId, real.CreationFileTime + 1),
            new WindowsServiceInitiatorIdentityResolver()));
    }

    // ---- happy path: kernel identity, persist, THEN acknowledge -------------

    [Fact]
    public void A_matching_request_binds_the_kernel_identity_persists_the_anchor_then_acknowledges()
    {
        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer();

        string endpointName = server.EndpointName;
        Assert.False(File.Exists(AnchorPath));

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        ServiceInitiatingUserIdentityChannelOutcome clientOutcome =
            ServiceInitiatingUserIdentityChannelClient.Request(endpointName, Bounded);

        // ORDERING PROOF. Request returns only after the acknowledgement line
        // has been read, so the anchor must already be on disk at this instant.
        bool anchorPresentWhenAcknowledged = File.Exists(AnchorPath);

        ServiceInitiatingUserIdentityBindResult result = bind.GetAwaiter().GetResult();

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Completed, clientOutcome);
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Completed, result.Outcome);
        Assert.True(result.AnchorPersisted);
        Assert.True(result.AcknowledgementSent);
        Assert.True(anchorPresentWhenAcknowledged);

        // The persisted SID is the KERNEL-derived client identity.
        ServiceInstallationAnchorReadResult read = ServiceInstallationAnchorStore.ReadFrom(_serviceDirectory);
        Assert.Equal(ServiceInstallationAnchorReadState.Validated, read.State);
        Assert.Equal(CurrentSid(), read.Validation!.Document!.InitiatingUserSid);
        Assert.True(ServiceInstallationAnchorContract.IsCanonicalInstallationId(
            read.Validation.Document.InstallationId));
    }

    // ---- CYCLE-59 FIX (b): a retry after a persisted anchor is IDEMPOTENT ----

    [Fact]
    public void A_retry_for_the_same_kernel_sid_reuses_the_existing_installation_id()
    {
        // First transaction: the anchor is created.
        using (ServiceInitiatingUserIdentityChannelServer first = CreateBoundServer())
        {
            Task<ServiceInitiatingUserIdentityBindResult> bind =
                Task.Run(() => first.AwaitAndBindIn(_serviceDirectory, Bounded));
            Assert.Equal(
                ServiceInitiatingUserIdentityChannelOutcome.Completed,
                ServiceInitiatingUserIdentityChannelClient.Request(first.EndpointName, Bounded));
            Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Completed, bind.GetAwaiter().GetResult().Outcome);
        }

        ServiceInstallationAnchorReadResult afterFirst = ServiceInstallationAnchorStore.ReadFrom(_serviceDirectory);
        string originalId = afterFirst.Validation!.Document!.InstallationId;
        byte[] bytesAfterFirst = File.ReadAllBytes(AnchorPath);

        // SECOND transaction against the SAME on-disk state. That state is
        // byte-for-byte what a retry meets after an anchor was persisted but its
        // acknowledgement was lost - the case cycle 58 turned into a PERMANENT
        // ConflictingIdentity refusal by minting a new installation id on every
        // attempt.
        using (ServiceInitiatingUserIdentityChannelServer second = CreateBoundServer())
        {
            Task<ServiceInitiatingUserIdentityBindResult> bind =
                Task.Run(() => second.AwaitAndBindIn(_serviceDirectory, Bounded));
            Assert.Equal(
                ServiceInitiatingUserIdentityChannelOutcome.Completed,
                ServiceInitiatingUserIdentityChannelClient.Request(second.EndpointName, Bounded));

            ServiceInitiatingUserIdentityBindResult retry = bind.GetAwaiter().GetResult();
            Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Completed, retry.Outcome);
            Assert.True(retry.AnchorPersisted);
            Assert.True(retry.AcknowledgementSent);
        }

        ServiceInstallationAnchorReadResult afterRetry = ServiceInstallationAnchorStore.ReadFrom(_serviceDirectory);
        Assert.Equal(ServiceInstallationAnchorReadState.Validated, afterRetry.State);
        Assert.Equal(originalId, afterRetry.Validation!.Document!.InstallationId);
        Assert.Equal(CurrentSid(), afterRetry.Validation.Document.InitiatingUserSid);

        // Idempotent means IDEMPOTENT: the accepted retry rewrote nothing.
        Assert.Equal(bytesAfterFirst, File.ReadAllBytes(AnchorPath));
    }

    // ---- CYCLE-59 FIX (c): a DIFFERENT SID still conflicts, bytes untouched --

    [Fact]
    public void An_existing_anchor_for_another_sid_conflicts_and_its_bytes_are_never_changed()
    {
        var foreign = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            "S-1-5-21-999999999-888888888-777777777-1500",
            "2026-01-01T00:00:00Z");
        Assert.NotEqual(CurrentSid(), foreign.InitiatingUserSid);
        Assert.Equal(
            ServiceInstallationAnchorWriteState.Created,
            ServiceInstallationAnchorStore.WriteTo(_serviceDirectory, foreign));

        byte[] before = File.ReadAllBytes(AnchorPath);

        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer();
        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        ServiceInitiatingUserIdentityChannelOutcome clientOutcome =
            ServiceInitiatingUserIdentityChannelClient.Request(server.EndpointName, Bounded);

        ServiceInitiatingUserIdentityBindResult result = bind.GetAwaiter().GetResult();

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict, result.Outcome);
        Assert.False(result.AnchorPersisted);
        Assert.False(result.AcknowledgementSent);

        // The bounded refusal reaches the client as its own outcome, so a
        // coordinator can report recovery-required instead of a bare failure.
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict, clientOutcome);

        // BYTE-FOR-BYTE unchanged. Existing state is preserved, never repaired.
        Assert.Equal(before, File.ReadAllBytes(AnchorPath));
    }

    [Fact]
    public void One_endpoint_binds_exactly_one_request_and_is_never_reusable()
    {
        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer();
        string endpointName = server.EndpointName;

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));
        ServiceInitiatingUserIdentityChannelClient.Request(endpointName, Bounded);
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Completed, bind.GetAwaiter().GetResult().Outcome);

        ServiceInitiatingUserIdentityBindResult second = server.AwaitAndBindIn(_serviceDirectory, Bounded);
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable, second.Outcome);

        // The endpoint is gone, so a later client cannot reach it at all.
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable,
            ServiceInitiatingUserIdentityChannelClient.Request(endpointName, TimeSpan.FromMilliseconds(500)));
    }

    // ---- admission facts are compared, never used ---------------------------

    [Fact]
    public void A_kernel_process_id_that_disagrees_with_the_admission_fact_is_refused()
    {
        // Admitted for a pid that is NOT the process that will actually connect,
        // while the resolver keeps answering with this process's real SID so
        // creation succeeds and the DACL still admits the test client. Only the
        // pid comparison can fail.
        ServiceInitiatorProcessFacts real = CurrentInitiator();
        var mismatched = new ServiceInitiatorProcessFacts(real.ProcessId + 1, real.CreationFileTime);

        using ServiceInitiatingUserIdentityChannelServer? server =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                ServiceInitiatingUserIdentityChannelContract.NewEndpointName(),
                mismatched,
                new ScriptedInitiatorIdentityResolver(CurrentSid(), CurrentSid()));
        Assert.NotNull(server);

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server!.AwaitAndBindIn(_serviceDirectory, Bounded));

        ServiceInitiatingUserIdentityChannelOutcome clientOutcome =
            ServiceInitiatingUserIdentityChannelClient.Request(server!.EndpointName, Bounded);

        ServiceInitiatingUserIdentityBindResult result = bind.GetAwaiter().GetResult();

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch, result.Outcome);
        Assert.False(result.AnchorPersisted);
        Assert.False(result.AcknowledgementSent);
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch, clientOutcome);
        Assert.False(File.Exists(AnchorPath));
    }

    [Fact]
    public void A_dacl_built_for_a_different_identity_denies_this_process_entirely()
    {
        // THIS IS WHY FIX (a) MATTERS, demonstrated rather than argued. When the
        // endpoint's single ACE names someone other than the process that will
        // connect, the connect is DENIED outright - so a DACL built from the
        // elevated APPROVER's account would lock the real, standard initiating
        // user out of its own transaction, exactly as this test locks out the
        // test process.
        const string otherSid = "S-1-5-21-1212121212-3434343434-1515151515-1700";
        Assert.NotEqual(CurrentSid(), otherSid);

        using ServiceInitiatingUserIdentityChannelServer? server =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                ServiceInitiatingUserIdentityChannelContract.NewEndpointName(),
                CurrentInitiator(),
                new ScriptedInitiatorIdentityResolver(new string?[] { otherSid }));
        Assert.NotNull(server);
        Assert.Equal(otherSid, server!.DaclIdentitySid);

        TimeSpan brief = TimeSpan.FromSeconds(2);
        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, brief));

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable,
            ServiceInitiatingUserIdentityChannelClient.Request(server.EndpointName, brief));

        ServiceInitiatingUserIdentityBindResult result = bind.GetAwaiter().GetResult();
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.NoClientConnected, result.Outcome);
        Assert.False(result.AnchorPersisted);
        Assert.False(result.AcknowledgementSent);
        Assert.False(File.Exists(AnchorPath));
    }

    [Fact]
    public void An_initiator_that_stops_being_bound_after_connection_is_refused()
    {
        // The pre-pipe answer succeeds; the post-connection answer does not.
        // Ruling 1 requires BOTH reads, so this must refuse.
        var resolver = new ScriptedInitiatorIdentityResolver(new string?[] { CurrentSid(), null });

        using ServiceInitiatingUserIdentityChannelServer? server =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                ServiceInitiatingUserIdentityChannelContract.NewEndpointName(), CurrentInitiator(), resolver);
        Assert.NotNull(server);

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server!.AwaitAndBindIn(_serviceDirectory, Bounded));

        ServiceInitiatingUserIdentityChannelOutcome clientOutcome =
            ServiceInitiatingUserIdentityChannelClient.Request(server!.EndpointName, Bounded);

        ServiceInitiatingUserIdentityBindResult result = bind.GetAwaiter().GetResult();

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.InitiatorNotBound, result.Outcome);
        Assert.False(result.AnchorPersisted);
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.InitiatorNotBound, clientOutcome);
        Assert.Equal(2, resolver.Calls);
        Assert.False(File.Exists(AnchorPath));
    }

    // ---- challenge and shape refusals ---------------------------------------

    [Fact]
    public void A_request_echoing_the_wrong_challenge_is_refused_and_nothing_is_persisted()
    {
        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer();

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        using (var raw = new NamedPipeClientStream(
            ".", server.EndpointName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
        {
            raw.Connect(5000);

            // Read the server's post-connection challenge, then deliberately
            // echo a different one.
            byte[] buffer = new byte[ServiceInitiatingUserIdentityChannelContract.MaxResponseBytes];
            string? offered = ServiceInitiatingUserIdentityChannelServer.TryReadBoundedLine(
                raw, buffer, CancellationToken.None);
            Assert.NotNull(offered);
            Assert.StartsWith(
                ServiceInitiatingUserIdentityChannelContract.ChallengeToken, offered!, StringComparison.Ordinal);

            string wrong = new('0', ServiceInitiatingUserIdentityChannelContract.EntropyBytes * 2);
            Assert.DoesNotContain(wrong, offered, StringComparison.Ordinal);

            byte[] echo = new UTF8Encoding(false).GetBytes(
                ServiceInitiatingUserIdentityChannelContract.RequestToken + " " + wrong + "\n");
            raw.Write(echo, 0, echo.Length);
            raw.Flush();

            ServiceInitiatingUserIdentityBindResult refused = bind.GetAwaiter().GetResult();
            Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.ChallengeMismatch, refused.Outcome);
            Assert.False(refused.AnchorPersisted);
            Assert.False(refused.AcknowledgementSent);
        }

        Assert.False(File.Exists(AnchorPath));
    }

    [Fact]
    public void A_malformed_request_line_is_refused_and_nothing_is_persisted()
    {
        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer();

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        using (var raw = new NamedPipeClientStream(
            ".", server.EndpointName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
        {
            raw.Connect(5000);

            byte[] buffer = new byte[ServiceInitiatingUserIdentityChannelContract.MaxResponseBytes];
            Assert.NotNull(ServiceInitiatingUserIdentityChannelServer.TryReadBoundedLine(
                raw, buffer, CancellationToken.None));

            byte[] garbage = new UTF8Encoding(false).GetBytes("HELLO\n");
            raw.Write(garbage, 0, garbage.Length);
            raw.Flush();
        }

        ServiceInitiatingUserIdentityBindResult result = bind.GetAwaiter().GetResult();

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.MalformedRequest, result.Outcome);
        Assert.False(result.AnchorPersisted);
        Assert.False(result.AcknowledgementSent);
        Assert.False(File.Exists(AnchorPath));
    }

    [Fact]
    public void A_refusal_line_only_ever_parses_back_to_a_defined_bounded_outcome()
    {
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict,
            ServiceInitiatingUserIdentityChannelContract.ParseRefusal(
                ServiceInitiatingUserIdentityChannelContract.RefusalToken + " AnchorConflict"));

        foreach (string? hostile in new string?[]
        {
            null,
            string.Empty,
            "SOMETHING-ELSE AnchorConflict",
            ServiceInitiatingUserIdentityChannelContract.RefusalToken,
            ServiceInitiatingUserIdentityChannelContract.RefusalToken + " NotAnOutcome",
            ServiceInitiatingUserIdentityChannelContract.RefusalToken + " 9999",
            ServiceInitiatingUserIdentityChannelContract.RefusalToken + " anchorconflict",
            ServiceInitiatingUserIdentityChannelContract.RefusalToken + " AnchorConflict extra",
        })
        {
            Assert.Equal(
                ServiceInitiatingUserIdentityChannelOutcome.Unspecified,
                ServiceInitiatingUserIdentityChannelContract.ParseRefusal(hostile));
        }
    }

    // ---- bounded, identity-free surface -------------------------------------

    [Fact]
    public void A_default_bind_result_is_unspecified_and_leaks_nothing_through_to_string()
    {
        var result = default(ServiceInitiatingUserIdentityBindResult);

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Unspecified, result.Outcome);
        Assert.False(result.AnchorPersisted);
        Assert.False(result.AcknowledgementSent);
        Assert.Equal("Unspecified", result.ToString());
    }

    [Fact]
    public void The_challenge_comparison_is_length_checked_and_exact()
    {
        char[] expected = "ABCD".ToCharArray();

        Assert.True(ServiceInitiatingUserIdentityChannelContract.ChallengeMatches(expected, "ABCD"));
        Assert.False(ServiceInitiatingUserIdentityChannelContract.ChallengeMatches(expected, "ABCE"));
        Assert.False(ServiceInitiatingUserIdentityChannelContract.ChallengeMatches(expected, "ABC"));
        Assert.False(ServiceInitiatingUserIdentityChannelContract.ChallengeMatches(expected, "ABCDE"));
        Assert.False(ServiceInitiatingUserIdentityChannelContract.ChallengeMatches(expected, null));
        Assert.False(ServiceInitiatingUserIdentityChannelContract.ChallengeMatches(null, "ABCD"));
    }

    // =======================================================================
    // CYCLE 67 - THE CHANNEL CARRIES A DISPOSITION AND NEVER A CAUSE
    // =======================================================================
    //
    // WHY THIS SECTION EXISTS. The channel DOES distinguish preflight refusal,
    // compensation and recovery-required - but it says nothing at all about
    // WHY. That is precisely why the helper's bounded exit code is required as
    // a SECOND, INDEPENDENT report, and why the two must be cross-checked
    // rather than one of them trusted.

    [Fact]
    public void Every_channel_outcome_maps_to_exactly_one_expected_disposition()
    {
        foreach (ServiceInitiatingUserIdentityChannelOutcome outcome
                 in Enum.GetValues<ServiceInitiatingUserIdentityChannelOutcome>())
        {
            ServiceEnableFailureDisposition expected =
                ServiceEnableFailureContract.ExpectedDispositionFor(outcome);

            // The map is TOTAL and never leaves a value unclassified.
            Assert.NotEqual(ServiceEnableFailureDisposition.None, expected);

            // Completed is the ONLY outcome that may expect a Completed
            // disposition, so no refusal can be mistaken for a success.
            Assert.Equal(
                outcome == ServiceInitiatingUserIdentityChannelOutcome.Completed,
                expected == ServiceEnableFailureDisposition.Completed);
        }

        // An UNDEFINED value - a corrupted or hostile wire token that somehow
        // survived parsing - fails closed to recovery-required.
        Assert.Equal(
            ServiceEnableFailureDisposition.RecoveryRequired,
            ServiceEnableFailureContract.ExpectedDispositionFor(
                (ServiceInitiatingUserIdentityChannelOutcome)9999));
        Assert.Equal(
            ServiceEnableFailureDisposition.RecoveryRequired,
            ServiceEnableFailureContract.ExpectedDispositionFor(
                ServiceInitiatingUserIdentityChannelOutcome.Unspecified));
    }

    [Fact]
    public void The_three_transaction_outcomes_are_three_distinct_dispositions()
    {
        Assert.Equal(
            ServiceEnableFailureDisposition.RefusedBeforeMutation,
            ServiceEnableFailureContract.ExpectedDispositionFor(
                ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused));

        Assert.Equal(
            ServiceEnableFailureDisposition.Compensated,
            ServiceEnableFailureContract.ExpectedDispositionFor(
                ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated));

        Assert.Equal(
            ServiceEnableFailureDisposition.RecoveryRequired,
            ServiceEnableFailureContract.ExpectedDispositionFor(
                ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired));
    }

    [Fact]
    public void One_channel_outcome_is_compatible_with_every_cause_so_the_channel_names_none()
    {
        // TransactionCompensated is a SINGLE channel value, yet ten different
        // causes are equally consistent with it. That is the whole reason a
        // second, independent report is required: the channel cannot name why.
        var codes = new HashSet<int>();
        foreach (ServiceEnableFailureCause cause in Enum.GetValues<ServiceEnableFailureCause>())
        {
            if (cause == ServiceEnableFailureCause.None)
            {
                continue;
            }

            int code = ServiceEnableFailureContract.ToExitCode(
                cause,
                ServiceEnableFailureContract.ExpectedDispositionFor(
                    ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated));

            Assert.True(codes.Add(code));
            Assert.Equal(cause, ServiceEnableFailureContract.FromExitCode(code).Cause);
        }

        // CYCLE 71. DERIVED FROM SOURCE, never hand-typed. The literal 10 that
        // stood here silently stopped counting the moment a cause was added, and
        // it is precisely the shape of assertion that hides a mapping hole.
        Assert.Equal(
            Enum.GetValues<ServiceEnableFailureCause>().Length - 1,
            codes.Count);
    }

    [Fact]
    public void Only_server_spoken_outcomes_are_cross_checked()
    {
        // These five are ALSO producible by the CLIENT with no server statement
        // received, so demanding agreement on them would manufacture false
        // mismatches out of ordinary local failures.
        foreach (ServiceInitiatingUserIdentityChannelOutcome clientLocal in new[]
        {
            ServiceInitiatingUserIdentityChannelOutcome.Unspecified,
            ServiceInitiatingUserIdentityChannelOutcome.UnsupportedPlatform,
            ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable,
            ServiceInitiatingUserIdentityChannelOutcome.MalformedRequest,
            ServiceInitiatingUserIdentityChannelOutcome.AcknowledgementNotReceived,
        })
        {
            Assert.False(ServiceEnableFailureContract.IsServerAuthoritative(clientLocal));
        }

        // Everything the SERVER writes on the wire, plus the acknowledgement, is
        // cross-checked against the helper's exit code.
        foreach (ServiceInitiatingUserIdentityChannelOutcome serverSpoken in new[]
        {
            ServiceInitiatingUserIdentityChannelOutcome.Completed,
            ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict,
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused,
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired,
            ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch,
            ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused,
            ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated,
            ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired,
        })
        {
            Assert.True(ServiceEnableFailureContract.IsServerAuthoritative(serverSpoken));
        }
    }

    // =======================================================================
    // CYCLE 67R - refused_before_mutation IS A CLAIM, NOT A CONVENIENCE
    // =======================================================================
    //
    // WHY THIS SECTION EXISTS. WriteTo creates the fixed machine directories
    // BEFORE it writes, flushes, replaces and rereads. Every failure at or after
    // that create leaves those directories durably present. Reporting such a
    // failure as refused_before_mutation is not merely imprecise: it tells an
    // operator the machine is clean, and the retry they then perform observes
    // the directories PRESENT, which permanently disqualifies the compensation
    // from ever removing them.

    [Fact]
    public void A_failing_write_that_may_have_created_directories_is_never_spoken_as_a_pre_mutation_refusal()
    {
        foreach (ServiceInstallationAnchorWriteState written
                 in Enum.GetValues<ServiceInstallationAnchorWriteState>())
        {
            if (written is ServiceInstallationAnchorWriteState.Created
                or ServiceInstallationAnchorWriteState.AlreadyMatching)
            {
                // The two success states never reach the failure map.
                continue;
            }

            ServiceInitiatingUserIdentityChannelOutcome spoken =
                ServiceInitiatingUserIdentityChannelContract.AnchorWriteFailureOutcomeFor(written);

            ServiceEnableFailureDisposition reported =
                ServiceEnableFailureContract.ExpectedDispositionFor(spoken);

            if (ServiceInstallationAnchorStore.MayHaveCreatedDirectories(written))
            {
                Assert.NotEqual(ServiceEnableFailureDisposition.RefusedBeforeMutation, reported);
                Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, reported);
            }
            else
            {
                // A genuinely pre-create refusal keeps its truthful, benign
                // reading - the split must not turn every anchor refusal into a
                // false recovery-required.
                Assert.Equal(ServiceEnableFailureDisposition.RefusedBeforeMutation, reported);
            }
        }
    }

    [Fact]
    public void The_post_create_anchor_value_is_a_distinct_server_spoken_recovery_required_wire_token()
    {
        const ServiceInitiatingUserIdentityChannelOutcome postCreate =
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired;

        // It is a SEPARATE value, not a rename of the benign one.
        Assert.NotEqual(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused, postCreate);

        // The server speaks it, so the helper exit code must be cross-checked
        // against it rather than accepted unopposed.
        Assert.True(ServiceEnableFailureContract.IsServerAuthoritative(postCreate));
        Assert.Equal(
            ServiceEnableFailureDisposition.RecoveryRequired,
            ServiceEnableFailureContract.ExpectedDispositionFor(postCreate));

        // The benign value is still benign for the paths that earned it.
        Assert.Equal(
            ServiceEnableFailureDisposition.RefusedBeforeMutation,
            ServiceEnableFailureContract.ExpectedDispositionFor(
                ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused));

        // It survives the bounded wire round trip and widens nothing.
        Assert.Equal(
            postCreate,
            ServiceInitiatingUserIdentityChannelContract.ParseRefusal(
                ServiceInitiatingUserIdentityChannelContract.RefusalToken + " " + postCreate));
    }

    [Fact]
    public void Only_an_enumerated_allow_list_may_claim_refused_before_mutation()
    {
        // THE ALLOW-LIST IS THE POINT. Every value here was justified one at a
        // time as decided before anything durable was touched. A NEW channel
        // value must be justified into this list deliberately; it can never
        // inherit a benign reading by accident.
        var decidedBeforeAnyMutation = new HashSet<ServiceInitiatingUserIdentityChannelOutcome>
        {
            ServiceInitiatingUserIdentityChannelOutcome.UnsupportedPlatform,
            ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable,
            ServiceInitiatingUserIdentityChannelOutcome.NoClientConnected,
            ServiceInitiatingUserIdentityChannelOutcome.MalformedRequest,
            ServiceInitiatingUserIdentityChannelOutcome.ChallengeMismatch,
            ServiceInitiatingUserIdentityChannelOutcome.ClientIdentityUnavailable,
            ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch,
            ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict,
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused,
            ServiceInitiatingUserIdentityChannelOutcome.AcknowledgementNotReceived,
            ServiceInitiatingUserIdentityChannelOutcome.InitiatorNotBound,
            ServiceInitiatingUserIdentityChannelOutcome.ChallengeNotDelivered,
            ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused,

            // CYCLE 89, JUSTIFIED ONE AT A TIME. Every value below is decided
            // inside the payload phase, whose one call site sits AFTER the anchor
            // read and classification and BEFORE the mutation-free preflight, the
            // anchor write and Apply. The payload transaction additionally binds
            // NeverCreate, so the anchor write is never even attempted and no
            // fixed machine directory can be created on any of these paths.
            ServiceInitiatingUserIdentityChannelOutcome.AnchorRequiredButUnavailable,
            ServiceInitiatingUserIdentityChannelOutcome.PayloadReadyNotDelivered,
            ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed,
            ServiceInitiatingUserIdentityChannelOutcome.PayloadOversized,
            ServiceInitiatingUserIdentityChannelOutcome.PayloadDigestMismatch,
            ServiceInitiatingUserIdentityChannelOutcome.PayloadNotUtf8,
            ServiceInitiatingUserIdentityChannelOutcome.PayloadRefused,
            ServiceInitiatingUserIdentityChannelOutcome.InstallationOwnershipMismatch,
            ServiceInitiatingUserIdentityChannelOutcome.PayloadNotOffered,
        };

        foreach (ServiceInitiatingUserIdentityChannelOutcome outcome
                 in Enum.GetValues<ServiceInitiatingUserIdentityChannelOutcome>())
        {
            bool claimsNothingWasMutated =
                ServiceEnableFailureContract.ExpectedDispositionFor(outcome)
                == ServiceEnableFailureDisposition.RefusedBeforeMutation;

            Assert.Equal(decidedBeforeAnyMutation.Contains(outcome), claimsNothingWasMutated);
        }
    }

    // =======================================================================
    // CYCLE 68 - THE POST-ANCHOR-WRITE FAILURE IS NOW REACHABLE BY COMPENSATION
    // =======================================================================
    //
    // THE WINDOW THIS SECTION CLOSES. WriteTo creates the fixed machine
    // directories BEFORE it writes. On a create-then-fail path the channel
    // returns on the wire WITHOUT ever calling Apply, so Compensate - and with
    // it every cycle-67 compensation step - is unreachable. Cycle 67 disclosed
    // that as DEFERRED. These tests pin the ONE narrow call that closes it, and
    // pin just as hard that it can never be reached any other way.

    /// <summary>
    /// Forces a POST-CREATE anchor-write failure without touching any machine
    /// location: a DIRECTORY is placed at the fixed same-directory temporary
    /// leaf, so <c>Directory.CreateDirectory</c> succeeds and the very next
    /// statement - opening the temporary file - cannot.
    /// </summary>
    private void ForcePostCreateAnchorWriteFailure()
    {
        Directory.CreateDirectory(
            Path.Combine(_serviceDirectory, ServiceInstallationAnchorStore.TemporaryLeafFileName));
    }

    /// <summary>
    /// A bound transaction that can ALSO recover from a post-anchor-write
    /// failure. It journals every step so a skipped Apply, a repeated recovery
    /// or a silent no-op is visible rather than assumed.
    /// </summary>
    private sealed class RecoverableTransaction
        : IServiceIdentityBoundTransaction, IServiceAnchorPersistenceFailureRecoverable
    {
        private readonly ServiceAnchorPersistenceRecoveryState _recovery;
        private readonly bool _throwOnRecovery;

        internal RecoverableTransaction(
            ServiceAnchorPersistenceRecoveryState recovery, bool throwOnRecovery = false)
        {
            _recovery = recovery;
            _throwOnRecovery = throwOnRecovery;
        }

        internal List<string> Steps { get; } = new();

        internal List<ServiceInstallationAnchorWriteState> RecoveredStates { get; } = new();

        public ServiceIdentityBoundTransactionPreflightState Preflight()
        {
            Steps.Add("preflight");
            return ServiceIdentityBoundTransactionPreflightState.Proceed;
        }

        public ServiceIdentityBoundTransactionResult Apply(ServiceIdentityBoundTransactionContext context)
        {
            Steps.Add("apply");
            return ServiceIdentityBoundTransactionResult.Completed();
        }

        public ServiceAnchorPersistenceRecoveryState RecoverAfterAnchorPersistenceFailure(
            ServiceInstallationAnchorWriteState state)
        {
            Steps.Add("recover");
            RecoveredStates.Add(state);
            if (_throwOnRecovery)
            {
                throw new InvalidOperationException("scripted recovery failure");
            }
            return _recovery;
        }
    }

    /// <summary>A bound transaction with NO recovery capability at all.</summary>
    private sealed class UnrecoverableTransaction : IServiceIdentityBoundTransaction
    {
        internal List<string> Steps { get; } = new();

        public ServiceIdentityBoundTransactionPreflightState Preflight()
        {
            Steps.Add("preflight");
            return ServiceIdentityBoundTransactionPreflightState.Proceed;
        }

        public ServiceIdentityBoundTransactionResult Apply(ServiceIdentityBoundTransactionContext context)
        {
            Steps.Add("apply");
            return ServiceIdentityBoundTransactionResult.Completed();
        }
    }

    private ServiceInitiatingUserIdentityChannelServer CreateBoundServer(
        IServiceIdentityBoundTransaction? transaction,
        ServiceAnchorCreationPolicy policy = ServiceAnchorCreationPolicy.CreateOrReuse)
    {
        ServiceInitiatingUserIdentityChannelServer? server =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                ServiceInitiatingUserIdentityChannelContract.NewEndpointName(),
                CurrentInitiator(),
                new WindowsServiceInitiatorIdentityResolver(),
                transaction,
                policy);
        Assert.NotNull(server);
        return server!;
    }

    private ServiceInitiatingUserIdentityBindResult RunOneTransaction(
        ServiceInitiatingUserIdentityChannelServer server,
        out ServiceInitiatingUserIdentityChannelOutcome clientOutcome)
    {
        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));
        clientOutcome = ServiceInitiatingUserIdentityChannelClient.Request(server.EndpointName, Bounded);
        return bind.GetAwaiter().GetResult();
    }

    [Fact]
    public void A_post_create_write_failure_invokes_the_recovery_exactly_once_and_never_applies()
    {
        ForcePostCreateAnchorWriteFailure();

        var transaction = new RecoverableTransaction(ServiceAnchorPersistenceRecoveryState.Compensated);
        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer(transaction);

        ServiceInitiatingUserIdentityBindResult result =
            RunOneTransaction(server, out ServiceInitiatingUserIdentityChannelOutcome clientOutcome);

        // A RECOVERED post-create failure is spoken as a COMPENSATED transaction.
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated, result.Outcome);
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated, clientOutcome);

        // EXACTLY ONCE, with the exact state the store reported.
        Assert.Equal(new[] { "preflight", "recover" }, transaction.Steps);
        Assert.Equal(
            ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate,
            Assert.Single(transaction.RecoveredStates));

        // APPLY NEVER RAN, NO ANCHOR IS DURABLE, AND NO ACKNOWLEDGEMENT WAS SENT.
        Assert.DoesNotContain("apply", transaction.Steps);
        Assert.False(result.AnchorPersisted);
        Assert.False(result.AcknowledgementSent);
        Assert.False(File.Exists(AnchorPath));

        // AND IT IS STILL A FAILURE. Compensated never means enablement worked.
        Assert.NotEqual(ServiceInitiatingUserIdentityChannelOutcome.Completed, result.Outcome);
        Assert.NotEqual(
            ServiceEnableFailureDisposition.Completed,
            ServiceEnableFailureContract.ExpectedDispositionFor(result.Outcome));
        Assert.Equal(
            ServiceEnableFailureDisposition.Compensated,
            ServiceEnableFailureContract.ExpectedDispositionFor(result.Outcome));
    }

    [Fact]
    public void A_recovery_that_cannot_prove_closure_is_spoken_as_recovery_required()
    {
        ForcePostCreateAnchorWriteFailure();

        var transaction = new RecoverableTransaction(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired);
        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer(transaction);

        ServiceInitiatingUserIdentityBindResult result =
            RunOneTransaction(server, out ServiceInitiatingUserIdentityChannelOutcome clientOutcome);

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired, result.Outcome);
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired, clientOutcome);
        Assert.Equal(new[] { "preflight", "recover" }, transaction.Steps);
        Assert.False(result.AcknowledgementSent);
    }

    [Fact]
    public void A_throwing_recovery_is_contained_and_fails_closed()
    {
        ForcePostCreateAnchorWriteFailure();

        var transaction = new RecoverableTransaction(
            ServiceAnchorPersistenceRecoveryState.Compensated, throwOnRecovery: true);
        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer(transaction);

        ServiceInitiatingUserIdentityBindResult result =
            RunOneTransaction(server, out ServiceInitiatingUserIdentityChannelOutcome clientOutcome);

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired, result.Outcome);
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired, clientOutcome);
        Assert.Equal(new[] { "preflight", "recover" }, transaction.Steps);
        Assert.False(result.AcknowledgementSent);
    }

    [Fact]
    public void A_transaction_with_no_recovery_capability_can_never_be_read_as_compensated()
    {
        ForcePostCreateAnchorWriteFailure();

        var transaction = new UnrecoverableTransaction();
        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer(transaction);

        ServiceInitiatingUserIdentityBindResult result =
            RunOneTransaction(server, out _);

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired, result.Outcome);
        Assert.Equal(new[] { "preflight" }, transaction.Steps);
        Assert.False(result.AcknowledgementSent);
    }

    [Fact]
    public void A_cycle_59_endpoint_with_no_bound_transaction_is_unchanged_by_this_cycle()
    {
        ForcePostCreateAnchorWriteFailure();

        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer(transaction: null);
        Assert.False(server.HasBoundTransaction);

        ServiceInitiatingUserIdentityBindResult result =
            RunOneTransaction(server, out ServiceInitiatingUserIdentityChannelOutcome clientOutcome);

        // EXACTLY the cycle-67 behaviour: a post-create failure with no bound
        // transaction is still spoken as recovery-required, never as compensated
        // and never as a benign refusal.
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired, result.Outcome);
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired, clientOutcome);
        Assert.False(result.AnchorPersisted);
        Assert.False(result.AcknowledgementSent);
    }

    [Fact]
    public void A_pre_create_anchor_refusal_never_invokes_the_recovery()
    {
        // A VALID anchor for a DIFFERENT SID is refused BEFORE WriteTo is ever
        // called, so nothing could have been created and no recovery may run.
        var foreign = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            "S-1-5-21-999999999-888888888-777777777-1500",
            "2026-01-01T00:00:00Z");
        Assert.Equal(
            ServiceInstallationAnchorWriteState.Created,
            ServiceInstallationAnchorStore.WriteTo(_serviceDirectory, foreign));
        byte[] before = File.ReadAllBytes(AnchorPath);

        var transaction = new RecoverableTransaction(ServiceAnchorPersistenceRecoveryState.Compensated);
        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer(transaction);

        ServiceInitiatingUserIdentityBindResult result = RunOneTransaction(server, out _);

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict, result.Outcome);
        Assert.Empty(transaction.RecoveredStates);
        Assert.DoesNotContain("recover", transaction.Steps);
        Assert.DoesNotContain("apply", transaction.Steps);
        Assert.Equal(before, File.ReadAllBytes(AnchorPath));
    }

    [Fact]
    public void A_refused_existing_anchor_state_never_invokes_the_recovery()
    {
        // Bytes that EXIST and do not validate are refused before WriteTo, and
        // are never repaired, overwritten or handed to a removal path.
        Directory.CreateDirectory(_serviceDirectory);
        File.WriteAllBytes(AnchorPath, new byte[] { 0x6E, 0x6F, 0x74, 0x6A, 0x73, 0x6F, 0x6E });
        byte[] before = File.ReadAllBytes(AnchorPath);

        var transaction = new RecoverableTransaction(ServiceAnchorPersistenceRecoveryState.Compensated);
        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer(transaction);

        ServiceInitiatingUserIdentityBindResult result = RunOneTransaction(server, out _);

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused, result.Outcome);
        Assert.Equal(
            ServiceEnableFailureDisposition.RefusedBeforeMutation,
            ServiceEnableFailureContract.ExpectedDispositionFor(result.Outcome));
        Assert.DoesNotContain("recover", transaction.Steps);
        Assert.Equal(before, File.ReadAllBytes(AnchorPath));
    }

    [Fact]
    public void The_disable_never_create_policy_reaches_neither_the_anchor_write_nor_the_recovery()
    {
        var transaction = new RecoverableTransaction(ServiceAnchorPersistenceRecoveryState.Compensated);
        using ServiceInitiatingUserIdentityChannelServer server =
            CreateBoundServer(transaction, ServiceAnchorCreationPolicy.NeverCreate);

        Assert.Equal(ServiceAnchorCreationPolicy.NeverCreate, server.AnchorPolicy);

        ServiceInitiatingUserIdentityBindResult result = RunOneTransaction(server, out _);

        // NeverCreate substitutes AlreadyMatching, so WriteTo is never called,
        // no anchor is minted, and the recovery is structurally unreachable.
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "preflight", "apply" }, transaction.Steps);
        Assert.DoesNotContain("recover", transaction.Steps);
        Assert.False(File.Exists(AnchorPath));
    }

    [Fact]
    public void The_recovery_is_wired_at_exactly_one_site_and_only_behind_the_mutation_boundary()
    {
        string channel = ReadRepoText("src/PAXCookbookSetup/Service/ServiceInitiatingUserIdentityChannel.cs");

        // POSITIVE CONTROL. The matcher fires on a sample that genuinely
        // contains the construct, so a measured count means something.
        Assert.Equal(2, CountOccurrences("a.Foo(); b.Foo();", ".Foo("));

        // ONE dispatch to the transaction, reached through ONE private helper.
        Assert.Equal(1, CountOccurrences(channel, ".RecoverAfterAnchorPersistenceFailure("));
        Assert.Equal(2, CountOccurrences(channel, "AnchorPersistenceRecoveryOutcomeFor("));

        // ONE anchor-write call site and ONE Apply call site in the whole file.
        Assert.Equal(1, CountOccurrences(channel, "ServiceInstallationAnchorStore.WriteTo("));
        Assert.Equal(1, CountOccurrences(channel, "_transaction.Apply("));

        int write = channel.IndexOf(
            "ServiceInstallationAnchorStore.WriteTo(serviceDirectory, proposed)", StringComparison.Ordinal);
        int recovery = channel.IndexOf(
            "AnchorPersistenceRecoveryOutcomeFor(written)", StringComparison.Ordinal);
        int apply = channel.IndexOf("_transaction.Apply(", StringComparison.Ordinal);

        Assert.True(write >= 0);
        Assert.True(recovery > write);
        Assert.True(apply > recovery);

        // THE GUARD IS TEXTUALLY BETWEEN THE WRITE AND THE RECOVERY, so the one
        // call can only be reached for a state the store itself classifies as
        // possibly having created the fixed machine directories.
        string between = channel[write..recovery];
        Assert.Contains(
            "ServiceInstallationAnchorStore.MayHaveCreatedDirectories(written)",
            between,
            StringComparison.Ordinal);

        // NEGATIVE CONTROL. The slice really is a strict subset of the file.
        Assert.True(between.Length < channel.Length);

        static int CountOccurrences(string text, string token)
        {
            int count = 0;
            for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
            {
                count++;
            }
            return count;
        }

        static string ReadRepoText(string relative)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null
                   && !File.Exists(Path.Combine(directory.FullName, "PAXCookbook.sln")))
            {
                directory = directory.Parent;
            }
            Assert.NotNull(directory);

            string full = Path.Combine(
                directory!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), "missing source: " + relative);
            return File.ReadAllText(full);
        }
    }
}
