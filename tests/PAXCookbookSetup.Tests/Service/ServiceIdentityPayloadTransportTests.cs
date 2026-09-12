using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 89 - BOUNDED POST-AUTHENTICATION PAYLOAD TRANSPORT (Setup only)
// ===========================================================================
//
// SCOPE, stated plainly. These tests run SAME-INTEGRITY: both ends of the pipe
// live in this one medium-integrity test process. They do NOT elevate, do NOT
// raise UAC, do NOT install or contact a service, do NOT open a certificate
// store, do NOT create, select, delete or re-ACL any certificate or private
// key, do NOT write %ProgramData%, do NOT touch the registry, do NOT run PAX
// and do NOT start a Bake. Every anchor is written to an OS TEMP directory
// through the disclosed internal seam.
//
// WHAT THEY PROVE. The ORDER of the payload phase, and that every step of it is
// unreachable until the step before it has passed. The transaction double below
// JOURNALS every call it receives, so "the parser was never reached" is an
// observed fact rather than an inference from a return value: a refusal that
// happened to be correct for the wrong reason would still show a non-zero
// AcceptPayload count and fail.
//
// THE LEGACY EXCHANGE IS THE CONTROL. A transaction that does not implement the
// narrow payload seam must produce EXACTLY the cycle-59 wire. That is asserted
// against the real production client and the real production server, not
// against a description of them.
public sealed class ServiceIdentityPayloadTransportTests : IDisposable
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Brief = TimeSpan.FromSeconds(3);

    private readonly string _serviceDirectory;

    public ServiceIdentityPayloadTransportTests()
    {
        _serviceDirectory = Path.Combine(
            Path.GetTempPath(), "paxcookbook-cycle89-payload", Guid.NewGuid().ToString("N"));
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

    // =======================================================================
    // FIXTURES
    // =======================================================================

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
    /// Writes a VALID anchor for THIS process's kernel SID, which is the
    /// precondition the payload phase requires and never creates for itself.
    /// </summary>
    private string SeedAnchorForCurrentUser()
    {
        string installationId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(_serviceDirectory);
        ServiceInstallationAnchorWriteState written = ServiceInstallationAnchorStore.WriteTo(
            _serviceDirectory,
            new ServiceInstallationAnchorDocument(
                installationId,
                CurrentSid(),
                ServiceInitiatingUserIdentityChannelContract.NowUtcTimestamp()));

        Assert.True(
            written is ServiceInstallationAnchorWriteState.Created
                or ServiceInstallationAnchorWriteState.AlreadyMatching);
        return installationId;
    }

    private static ServiceInitiatingUserIdentityChannelServer CreateServer(
        IServiceIdentityBoundTransaction transaction) =>
        CreateServer(transaction, CurrentInitiator());

    private static ServiceInitiatingUserIdentityChannelServer CreateServer(
        IServiceIdentityBoundTransaction transaction, ServiceInitiatorProcessFacts initiator)
    {
        ServiceInitiatingUserIdentityChannelServer? server =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                ServiceInitiatingUserIdentityChannelContract.NewEndpointName(),
                initiator,
                new WindowsServiceInitiatorIdentityResolver(),
                transaction,
                // Ownership promotion never mints an anchor.
                ServiceAnchorCreationPolicy.NeverCreate);
        Assert.NotNull(server);
        return server!;
    }

    /// <summary>
    /// The JOURNALLING payload transaction double. It records the ORDER and the
    /// COUNT of every call, because a refusal that reached the right verdict by
    /// the wrong route is still a defect.
    /// </summary>
    private sealed class JournallingPayloadTransaction : IServiceIdentityBoundPayloadTransaction
    {
        private readonly ServiceIdentityPayloadAcceptanceState _acceptance;
        private readonly ServiceIdentityBoundTransactionPreflightState _preflight;
        private readonly ServiceIdentityBoundTransactionResult _apply;

        internal JournallingPayloadTransaction(
            ServiceIdentityPayloadAcceptanceState acceptance = ServiceIdentityPayloadAcceptanceState.Accepted,
            ServiceIdentityBoundTransactionPreflightState preflight =
                ServiceIdentityBoundTransactionPreflightState.Proceed,
            ServiceIdentityBoundTransactionResult? apply = null)
        {
            _acceptance = acceptance;
            _preflight = preflight;
            _apply = apply ?? ServiceIdentityBoundTransactionResult.Completed();
        }

        internal List<string> Journal { get; } = new();

        internal int AcceptPayloadCalls { get; private set; }

        internal int PreflightCalls { get; private set; }

        internal int ApplyCalls { get; private set; }

        internal string? LastPayloadText { get; private set; }

        internal string? LastAnchorInstallationId { get; private set; }

        public ServiceIdentityPayloadAcceptanceState AcceptPayload(
            string payloadText, string anchorInstallationId)
        {
            AcceptPayloadCalls++;
            LastPayloadText = payloadText;
            LastAnchorInstallationId = anchorInstallationId;
            Journal.Add(nameof(AcceptPayload));
            return _acceptance;
        }

        public ServiceIdentityBoundTransactionPreflightState Preflight()
        {
            PreflightCalls++;
            Journal.Add(nameof(Preflight));
            return _preflight;
        }

        public ServiceIdentityBoundTransactionResult Apply(ServiceIdentityBoundTransactionContext context)
        {
            ApplyCalls++;
            Journal.Add(nameof(Apply));
            return _apply;
        }
    }

    /// <summary>
    /// A CHALLENGE-ONLY transaction, i.e. exactly the shape service-enable and
    /// service-disable bind. It deliberately does NOT implement the payload seam,
    /// and it journals so a payload leaking into it would be visible.
    /// </summary>
    private sealed class JournallingLegacyTransaction : IServiceIdentityBoundTransaction
    {
        internal List<string> Journal { get; } = new();

        public ServiceIdentityBoundTransactionPreflightState Preflight()
        {
            Journal.Add(nameof(Preflight));
            return ServiceIdentityBoundTransactionPreflightState.Proceed;
        }

        public ServiceIdentityBoundTransactionResult Apply(ServiceIdentityBoundTransactionContext context)
        {
            Journal.Add(nameof(Apply));
            return ServiceIdentityBoundTransactionResult.Completed();
        }
    }

    /// <summary>
    /// A RAW client that speaks the wire directly, so a test can transmit a frame
    /// the production client would never build. It reports every line it received,
    /// which is how "no payload-ready was ever offered" becomes an observation.
    /// </summary>
    private sealed class RawClient : IDisposable
    {
        private readonly NamedPipeClientStream _stream;

        internal RawClient(string endpointName)
        {
            _stream = new NamedPipeClientStream(
                ".", endpointName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            _stream.Connect((int)Bounded.TotalMilliseconds);
        }

        internal List<string> Received { get; } = new();

        internal string? ReadLine()
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (bytes.Count < 4096)
            {
                int read;
                try
                {
                    read = _stream.Read(one, 0, 1);
                }
                catch (Exception)
                {
                    return null;
                }
                if (read <= 0)
                {
                    return null;
                }
                if (one[0] == (byte)'\n')
                {
                    string line = Encoding.UTF8.GetString(bytes.ToArray());
                    Received.Add(line);
                    return line;
                }
                bytes.Add(one[0]);
            }
            return null;
        }

        internal void Write(byte[] bytes) => _stream.Write(bytes, 0, bytes.Length);

        internal void WriteLine(string line) =>
            Write(new UTF8Encoding(false).GetBytes(line + "\n"));

        /// <summary>Reads the challenge and echoes it, returning the challenge hex.</summary>
        internal string CompleteChallengeExchange()
        {
            string? offered = ReadLine();
            Assert.NotNull(offered);
            string[] fields = offered!.Split(' ');
            Assert.Equal(2, fields.Length);
            Assert.Equal(ServiceInitiatingUserIdentityChannelContract.ChallengeToken, fields[0]);
            WriteLine(ServiceInitiatingUserIdentityChannelContract.RequestToken + " " + fields[1]);
            return fields[1];
        }

        /// <summary>
        /// Observes whether ANYTHING arrives inside a brief window. Used to prove
        /// the server offered no payload-ready line rather than merely that the
        /// client ignored one.
        /// </summary>
        internal bool NothingArrivesWithin(TimeSpan window)
        {
            var one = new byte[1];
            using var cts = new CancellationTokenSource(window);
            try
            {
                int read = _stream.ReadAsync(one, 0, 1, cts.Token).GetAwaiter().GetResult();
                return read <= 0;
            }
            catch (Exception)
            {
                return true;
            }
        }

        public void Dispose() => _stream.Dispose();
    }

    private static byte[] MinimalPayload() => new UTF8Encoding(false).GetBytes("{\"a\":1}");

    private static void WriteFrame(RawClient client, string challenge, byte[] payload)
    {
        string digest = ServiceInitiatingUserIdentityChannelContract.ComputePayloadBinding(
            challenge.ToCharArray(), payload);
        client.WriteLine(payload.Length.ToString(CultureInfo.InvariantCulture));
        client.WriteLine(digest);
        client.Write(payload);
        client.WriteLine(ServiceInitiatingUserIdentityChannelContract.PayloadEndToken);
    }

    private static void WriteTrailer(RawClient client) =>
        client.WriteLine(ServiceInitiatingUserIdentityChannelContract.PayloadEndToken);

    // =======================================================================
    // 1. THE LEGACY EXCHANGE IS UNCHANGED
    // =======================================================================

    [Fact]
    public void A_challenge_only_transaction_carries_no_payload_seam_and_is_never_offered_one()
    {
        SeedAnchorForCurrentUser();
        var legacy = new JournallingLegacyTransaction();
        using ServiceInitiatingUserIdentityChannelServer server = CreateServer(legacy);

        Assert.True(server.HasBoundTransaction);
        Assert.False(server.HasBoundPayloadTransaction);

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        using var raw = new RawClient(server.EndpointName);
        raw.CompleteChallengeExchange();

        // The VERY NEXT line must be the cycle-59 acknowledgement. If a
        // payload-ready line had been inserted, this equality would fail.
        Assert.Equal(ServiceInitiatingUserIdentityChannelContract.AcknowledgementToken, raw.ReadLine());

        ServiceInitiatingUserIdentityBindResult result = bind.GetAwaiter().GetResult();
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "Preflight", "Apply" }, legacy.Journal);
    }

    [Fact]
    public void The_legacy_production_client_still_completes_against_a_challenge_only_transaction()
    {
        SeedAnchorForCurrentUser();
        using ServiceInitiatingUserIdentityChannelServer server =
            CreateServer(new JournallingLegacyTransaction());

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        ServiceInitiatingUserIdentityChannelOutcome client =
            ServiceInitiatingUserIdentityChannelClient.Request(server.EndpointName, Bounded);

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Completed, client);
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.Completed,
            bind.GetAwaiter().GetResult().Outcome);
    }

    // =======================================================================
    // 2. ONE VALID PAYLOAD REACHES THE FIXED TRANSACTION EXACTLY ONCE
    // =======================================================================

    [Fact]
    public void One_valid_payload_reaches_the_transaction_exactly_once_and_in_the_fixed_order()
    {
        string installationId = SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        using ServiceInitiatingUserIdentityChannelServer server = CreateServer(transaction);

        Assert.True(server.HasBoundPayloadTransaction);

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        ServiceInitiatingUserIdentityChannelOutcome client =
            ServiceInitiatingUserIdentityChannelClient.RequestWithPayload(
                server.EndpointName, MinimalPayload(), Bounded);

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Completed, client);
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.Completed,
            bind.GetAwaiter().GetResult().Outcome);

        Assert.Equal(1, transaction.AcceptPayloadCalls);
        Assert.Equal(1, transaction.PreflightCalls);
        Assert.Equal(1, transaction.ApplyCalls);
        Assert.Equal(new[] { "AcceptPayload", "Preflight", "Apply" }, transaction.Journal);

        // The installation id came from the VALIDATED ANCHOR, not from the wire.
        Assert.Equal(installationId, transaction.LastAnchorInstallationId);
        Assert.Equal("{\"a\":1}", transaction.LastPayloadText);
    }

    // =======================================================================
    // 3. NOTHING IS REACHED BEFORE KERNEL IDENTITY AND ANCHOR VALIDATION
    // =======================================================================

    [Fact]
    public void An_absent_anchor_refuses_before_payload_ready_and_the_parser_is_never_reached()
    {
        Directory.CreateDirectory(_serviceDirectory); // no anchor written
        var transaction = new JournallingPayloadTransaction();
        using ServiceInitiatingUserIdentityChannelServer server = CreateServer(transaction);

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        using var raw = new RawClient(server.EndpointName);
        raw.CompleteChallengeExchange();

        string? next = raw.ReadLine();
        Assert.NotNull(next);
        Assert.NotEqual(ServiceInitiatingUserIdentityChannelContract.PayloadReadyToken, next);
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorRequiredButUnavailable,
            ServiceInitiatingUserIdentityChannelContract.ParseRefusal(next));

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorRequiredButUnavailable,
            bind.GetAwaiter().GetResult().Outcome);

        Assert.Equal(0, transaction.AcceptPayloadCalls);
        Assert.Equal(0, transaction.PreflightCalls);
        Assert.Equal(0, transaction.ApplyCalls);
        Assert.Empty(transaction.Journal);
    }

    [Fact]
    public void A_conflicting_anchor_refuses_before_payload_ready_and_its_bytes_are_untouched()
    {
        Directory.CreateDirectory(_serviceDirectory);
        Assert.Equal(
            ServiceInstallationAnchorWriteState.Created,
            ServiceInstallationAnchorStore.WriteTo(
                _serviceDirectory,
                new ServiceInstallationAnchorDocument(
                    Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                    "S-1-5-21-1111111111-2222222222-3333333333-1001",
                    ServiceInitiatingUserIdentityChannelContract.NowUtcTimestamp())));

        string anchorPath = Path.Combine(
            _serviceDirectory, ServiceMachineStorageContract.InstallationAnchorFileName);
        byte[] before = File.ReadAllBytes(anchorPath);

        var transaction = new JournallingPayloadTransaction();
        using ServiceInitiatingUserIdentityChannelServer server = CreateServer(transaction);

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        using var raw = new RawClient(server.EndpointName);
        raw.CompleteChallengeExchange();
        string? next = raw.ReadLine();

        Assert.NotEqual(ServiceInitiatingUserIdentityChannelContract.PayloadReadyToken, next);
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict,
            bind.GetAwaiter().GetResult().Outcome);

        Assert.Equal(0, transaction.AcceptPayloadCalls);
        Assert.Equal(before, File.ReadAllBytes(anchorPath));
    }

    [Fact]
    public void An_admission_fact_mismatch_refuses_before_payload_ready()
    {
        SeedAnchorForCurrentUser();
        ServiceInitiatorProcessFacts real = CurrentInitiator();
        var transaction = new JournallingPayloadTransaction();

        // Same identity so the DACL still admits this process, but a PID the
        // kernel will contradict.
        using ServiceInitiatingUserIdentityChannelServer server = CreateServer(
            transaction, new ServiceInitiatorProcessFacts(real.ProcessId + 1, real.CreationFileTime));

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        ServiceInitiatingUserIdentityChannelOutcome client =
            ServiceInitiatingUserIdentityChannelClient.RequestWithPayload(
                server.EndpointName, MinimalPayload(), Bounded);

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch, client);
        Assert.Equal(0, transaction.AcceptPayloadCalls);
        Assert.Equal(0, transaction.PreflightCalls);
        Assert.Equal(0, transaction.ApplyCalls);

        bind.GetAwaiter().GetResult();
    }

    [Fact]
    public void The_client_discards_its_payload_unsent_when_payload_ready_is_never_offered()
    {
        Directory.CreateDirectory(_serviceDirectory); // absent anchor -> early refusal
        var transaction = new JournallingPayloadTransaction();
        using ServiceInitiatingUserIdentityChannelServer server = CreateServer(transaction);

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        ServiceInitiatingUserIdentityChannelOutcome client =
            ServiceInitiatingUserIdentityChannelClient.RequestWithPayload(
                server.EndpointName, MinimalPayload(), Bounded);

        // The bounded refusal reaches the caller AS ITSELF, and nothing in the
        // transaction was ever touched, so no payload byte was consumed.
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.AnchorRequiredButUnavailable, client);
        Assert.Equal(0, transaction.AcceptPayloadCalls);
        bind.GetAwaiter().GetResult();
    }

    [Fact]
    public void No_server_line_at_all_arrives_before_the_challenge_exchange_completes()
    {
        SeedAnchorForCurrentUser();
        using ServiceInitiatingUserIdentityChannelServer server =
            CreateServer(new JournallingPayloadTransaction());

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        using var raw = new RawClient(server.EndpointName);

        // The FIRST line is the challenge, never the payload-ready token.
        string? first = raw.ReadLine();
        Assert.NotNull(first);
        Assert.StartsWith(
            ServiceInitiatingUserIdentityChannelContract.ChallengeToken + " ", first!, StringComparison.Ordinal);

        // And with the echo withheld, no payload-ready line ever follows.
        Assert.True(raw.NothingArrivesWithin(Brief));

        bind.GetAwaiter().GetResult();
    }

    // =======================================================================
    // 4. EVERY MALFORMED FRAME CASE REFUSES
    // =======================================================================

    private ServiceInitiatingUserIdentityChannelOutcome RunRawFrame(
        JournallingPayloadTransaction transaction, Action<RawClient, string> writeFrame)
    {
        using ServiceInitiatingUserIdentityChannelServer server = CreateServer(transaction);

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        using (var raw = new RawClient(server.EndpointName))
        {
            string challenge = raw.CompleteChallengeExchange();
            Assert.Equal(ServiceInitiatingUserIdentityChannelContract.PayloadReadyToken, raw.ReadLine());
            try
            {
                writeFrame(raw, challenge);
            }
            catch (Exception)
            {
                // A peer that cannot finish writing is exactly one of the cases
                // under test; the server's verdict is what matters.
            }
        }

        return bind.GetAwaiter().GetResult().Outcome;
    }

    [Fact]
    public void A_zero_length_frame_is_refused()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed,
            RunRawFrame(transaction, (raw, challenge) =>
            {
                raw.WriteLine("0");
                raw.WriteLine(new string('A', 64));
            }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 7")]
    [InlineData("7 ")]
    [InlineData("+7")]
    [InlineData("-7")]
    [InlineData("07")]
    [InlineData("0x7")]
    [InlineData("7a")]
    [InlineData("seven")]
    [InlineData("9999999999999999999999999999999")]
    public void A_malformed_length_line_is_refused(string lengthLine)
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed,
            RunRawFrame(transaction, (raw, challenge) =>
            {
                raw.WriteLine(lengthLine);
                raw.WriteLine(new string('A', 64));
                raw.Write(MinimalPayload());
            }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    [Fact]
    public void A_length_above_the_derived_ceiling_is_refused_as_oversized()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        long tooBig = (long)ServiceInitiatingUserIdentityChannelContract.MaxPayloadBytes + 1;

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadOversized,
            RunRawFrame(transaction, (raw, challenge) =>
            {
                raw.WriteLine(tooBig.ToString(CultureInfo.InvariantCulture));
                raw.WriteLine(new string('A', 64));
            }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void A_malformed_digest_line_is_refused(string digestLine)
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        byte[] payload = MinimalPayload();

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed,
            RunRawFrame(transaction, (raw, challenge) =>
            {
                raw.WriteLine(payload.Length.ToString(CultureInfo.InvariantCulture));
                raw.WriteLine(digestLine);
                raw.Write(payload);
            }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    [Fact]
    public void A_lowercase_digest_is_refused_rather_than_normalised()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        byte[] payload = MinimalPayload();

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed,
            RunRawFrame(transaction, (raw, challenge) =>
            {
                raw.WriteLine(payload.Length.ToString(CultureInfo.InvariantCulture));
                raw.WriteLine(
                    ServiceInitiatingUserIdentityChannelContract
                        .ComputePayloadBinding(challenge.ToCharArray(), payload)
                        .ToLowerInvariant());
                raw.Write(payload);
            }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    [Fact]
    public void A_digest_that_does_not_bind_these_bytes_is_refused()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        byte[] payload = MinimalPayload();
        byte[] other = new UTF8Encoding(false).GetBytes("{\"a\":2}");

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadDigestMismatch,
            RunRawFrame(transaction, (raw, challenge) =>
            {
                raw.WriteLine(payload.Length.ToString(CultureInfo.InvariantCulture));
                raw.WriteLine(ServiceInitiatingUserIdentityChannelContract
                    .ComputePayloadBinding(challenge.ToCharArray(), other));
                raw.Write(payload);
                WriteTrailer(raw);
            }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    [Fact]
    public void A_digest_bound_to_a_different_challenge_is_refused()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        byte[] payload = MinimalPayload();

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadDigestMismatch,
            RunRawFrame(transaction, (raw, challenge) =>
            {
                char[] foreignChallenge = ServiceInitiatingUserIdentityChannelContract.NewEntropyHex();
                raw.WriteLine(payload.Length.ToString(CultureInfo.InvariantCulture));
                raw.WriteLine(ServiceInitiatingUserIdentityChannelContract
                    .ComputePayloadBinding(foreignChallenge, payload));
                raw.Write(payload);
                WriteTrailer(raw);
            }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    [Fact]
    public void A_truncated_payload_is_refused_and_never_parsed()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        byte[] payload = MinimalPayload();

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed,
            RunRawFrame(transaction, (raw, challenge) =>
            {
                raw.WriteLine(payload.Length.ToString(CultureInfo.InvariantCulture));
                raw.WriteLine(ServiceInitiatingUserIdentityChannelContract
                    .ComputePayloadBinding(challenge.ToCharArray(), payload));
                raw.Write(payload[..(payload.Length - 2)]);
            }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    [Fact]
    public void A_premature_disconnect_before_the_frame_is_refused()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed,
            RunRawFrame(transaction, (raw, challenge) => { /* say nothing at all */ }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    [Fact]
    public void A_duplicate_frame_is_refused_and_the_transaction_is_never_reached()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        byte[] payload = MinimalPayload();

        // The SECOND frame's length line lands exactly where the end-of-frame
        // trailer must be, so the refusal is a deterministic grammar failure and
        // not a timing-dependent observation.
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed,
            RunRawFrame(transaction, (raw, challenge) =>
            {
                string digest = ServiceInitiatingUserIdentityChannelContract
                    .ComputePayloadBinding(challenge.ToCharArray(), payload);
                raw.WriteLine(payload.Length.ToString(CultureInfo.InvariantCulture));
                raw.WriteLine(digest);
                raw.Write(payload);
                // No trailer: a whole second frame instead.
                raw.WriteLine(payload.Length.ToString(CultureInfo.InvariantCulture));
                raw.WriteLine(digest);
                raw.Write(payload);
                WriteTrailer(raw);
            }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    [Fact]
    public void A_frame_with_no_end_trailer_is_refused()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        byte[] payload = MinimalPayload();

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed,
            RunRawFrame(transaction, (raw, challenge) =>
            {
                raw.WriteLine(payload.Length.ToString(CultureInfo.InvariantCulture));
                raw.WriteLine(ServiceInitiatingUserIdentityChannelContract
                    .ComputePayloadBinding(challenge.ToCharArray(), payload));
                raw.Write(payload);
                raw.WriteLine("NOT-THE-END-TOKEN");
            }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    [Fact]
    public void Invalid_utf8_payload_bytes_are_refused_and_never_parsed()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        byte[] payload = { 0x7B, 0xC3, 0x28, 0x7D };

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadNotUtf8,
            RunRawFrame(transaction, (raw, challenge) =>
            {
                raw.WriteLine(payload.Length.ToString(CultureInfo.InvariantCulture));
                raw.WriteLine(ServiceInitiatingUserIdentityChannelContract
                    .ComputePayloadBinding(challenge.ToCharArray(), payload));
                raw.Write(payload);
                WriteTrailer(raw);
            }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    [Fact]
    public void A_byte_order_mark_is_refused_and_never_parsed()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction();
        byte[] payload = { 0xEF, 0xBB, 0xBF, 0x7B, 0x7D };

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadNotUtf8,
            RunRawFrame(transaction, (raw, challenge) =>
            {
                raw.WriteLine(payload.Length.ToString(CultureInfo.InvariantCulture));
                raw.WriteLine(ServiceInitiatingUserIdentityChannelContract
                    .ComputePayloadBinding(challenge.ToCharArray(), payload));
                raw.Write(payload);
                WriteTrailer(raw);
            }));
        Assert.Equal(0, transaction.AcceptPayloadCalls);
    }

    // =======================================================================
    // 5. THE ACCEPTANCE VERDICT DECIDES, AND NOTHING RUNS AFTER A REFUSAL
    // =======================================================================

    [Fact]
    public void An_installation_ownership_mismatch_refuses_before_the_preflight()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction(
            ServiceIdentityPayloadAcceptanceState.InstallationOwnershipMismatch);
        using ServiceInitiatingUserIdentityChannelServer server = CreateServer(transaction);

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        ServiceInitiatingUserIdentityChannelOutcome client =
            ServiceInitiatingUserIdentityChannelClient.RequestWithPayload(
                server.EndpointName, MinimalPayload(), Bounded);

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.InstallationOwnershipMismatch, client);
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.InstallationOwnershipMismatch,
            bind.GetAwaiter().GetResult().Outcome);

        Assert.Equal(1, transaction.AcceptPayloadCalls);
        Assert.Equal(0, transaction.PreflightCalls);
        Assert.Equal(0, transaction.ApplyCalls);
        Assert.Equal(new[] { "AcceptPayload" }, transaction.Journal);
    }

    [Fact]
    public void A_refused_payload_never_reaches_the_preflight()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction(
            ServiceIdentityPayloadAcceptanceState.Refused);
        using ServiceInitiatingUserIdentityChannelServer server = CreateServer(transaction);

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        ServiceInitiatingUserIdentityChannelOutcome client =
            ServiceInitiatingUserIdentityChannelClient.RequestWithPayload(
                server.EndpointName, MinimalPayload(), Bounded);

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.PayloadRefused, client);
        Assert.Equal(1, transaction.AcceptPayloadCalls);
        Assert.Equal(0, transaction.PreflightCalls);
        Assert.Equal(0, transaction.ApplyCalls);
        bind.GetAwaiter().GetResult();
    }

    [Fact]
    public void An_unspecified_acceptance_verdict_fails_closed()
    {
        SeedAnchorForCurrentUser();
        var transaction = new JournallingPayloadTransaction(
            ServiceIdentityPayloadAcceptanceState.Unspecified);
        using ServiceInitiatingUserIdentityChannelServer server = CreateServer(transaction);

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.PayloadRefused,
            ServiceInitiatingUserIdentityChannelClient.RequestWithPayload(
                server.EndpointName, MinimalPayload(), Bounded));

        Assert.Equal(0, transaction.PreflightCalls);
        bind.GetAwaiter().GetResult();
    }

    // =======================================================================
    // 6. THE PURE GRAMMAR AND BINDING
    // =======================================================================

    [Fact]
    public void The_transport_ceiling_is_pinned_to_the_request_contract_and_not_merely_retyped()
    {
        // THE PIN. The channel states 2097152 as a literal because it is
        // compile-linked into the elevated helper, where the request parser is
        // deliberately not in the closure. This assertion is what makes the
        // literal honest: it compiles BOTH types and fails the moment
        // MaxRequestChars moves, so the two can never drift apart silently.
        Assert.Equal(
            ServicePromotionRequestParser.MaxRequestChars * 4,
            ServiceInitiatingUserIdentityChannelContract.MaxPayloadBytes);
        Assert.Equal(524288, ServicePromotionRequestParser.MaxRequestChars);
        Assert.Equal(2097152, ServiceInitiatingUserIdentityChannelContract.MaxPayloadBytes);
    }

    [Theory]
    [InlineData("1", true, 1)]
    [InlineData("7", true, 7)]
    [InlineData("2097152", true, 2097152)]
    [InlineData("2097153", false, 0)]
    [InlineData("0", false, 0)]
    [InlineData("01", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("1 ", false, 0)]
    [InlineData("１", false, 0)]
    public void The_frame_length_grammar_is_closed(string line, bool expected, int expectedLength)
    {
        Assert.Equal(
            expected,
            ServiceInitiatingUserIdentityChannelContract.TryParsePayloadFrameLength(line, out int length));
        Assert.Equal(expectedLength, length);
    }

    [Fact]
    public void The_binding_changes_when_either_the_challenge_or_the_payload_changes()
    {
        char[] challengeA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA".ToCharArray();
        char[] challengeB = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB".ToCharArray();
        byte[] payloadA = new UTF8Encoding(false).GetBytes("{\"a\":1}");
        byte[] payloadB = new UTF8Encoding(false).GetBytes("{\"a\":2}");

        string baseline = ServiceInitiatingUserIdentityChannelContract.ComputePayloadBinding(challengeA, payloadA);

        Assert.Equal(64, baseline.Length);
        Assert.True(ServiceInitiatingUserIdentityChannelContract.IsCanonicalPayloadDigest(baseline));

        // POSITIVE CONTROL: the same inputs reproduce the same digest, so the
        // two inequalities below are measuring change and not merely randomness.
        Assert.Equal(
            baseline,
            ServiceInitiatingUserIdentityChannelContract.ComputePayloadBinding(challengeA, payloadA));

        Assert.NotEqual(
            baseline,
            ServiceInitiatingUserIdentityChannelContract.ComputePayloadBinding(challengeB, payloadA));
        Assert.NotEqual(
            baseline,
            ServiceInitiatingUserIdentityChannelContract.ComputePayloadBinding(challengeA, payloadB));
    }

    [Fact]
    public void The_binding_is_domain_separated_from_a_plain_sha256_of_the_payload()
    {
        char[] challenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA".ToCharArray();
        byte[] payload = new UTF8Encoding(false).GetBytes("{\"a\":1}");

        var plain = new StringBuilder();
        foreach (byte b in SHA256.HashData(payload))
        {
            plain.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        Assert.NotEqual(
            plain.ToString(),
            ServiceInitiatingUserIdentityChannelContract.ComputePayloadBinding(challenge, payload));
    }

    [Fact]
    public void A_null_or_empty_input_yields_no_binding_at_all()
    {
        Assert.Equal(
            string.Empty,
            ServiceInitiatingUserIdentityChannelContract.ComputePayloadBinding(null, MinimalPayload()));
        Assert.Equal(
            string.Empty,
            ServiceInitiatingUserIdentityChannelContract.ComputePayloadBinding(
                Array.Empty<char>(), MinimalPayload()));
        Assert.Equal(
            string.Empty,
            ServiceInitiatingUserIdentityChannelContract.ComputePayloadBinding(
                "AAAA".ToCharArray(), null));
    }

    [Fact]
    public void Every_new_payload_outcome_is_a_distinct_defined_enum_member()
    {
        var payloadOutcomes = new[]
        {
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

        Assert.Equal(payloadOutcomes.Length, new HashSet<int>(Array.ConvertAll(payloadOutcomes, o => (int)o)).Count);
        foreach (ServiceInitiatingUserIdentityChannelOutcome outcome in payloadOutcomes)
        {
            Assert.True(Enum.IsDefined(outcome));
            Assert.NotEqual(ServiceInitiatingUserIdentityChannelOutcome.Completed, outcome);

            // Every one of them survives the closed refusal round trip, so the
            // client can never flatten a payload refusal into a generic one.
            Assert.Equal(
                outcome,
                ServiceInitiatingUserIdentityChannelContract.ParseRefusal(
                    ServiceInitiatingUserIdentityChannelContract.RefusalToken + " " + outcome));
        }
    }

    [Fact]
    public void The_default_payload_acceptance_state_is_unspecified_and_never_accepts()
    {
        ServiceIdentityPayloadAcceptanceState defaulted = default;
        Assert.Equal(ServiceIdentityPayloadAcceptanceState.Unspecified, defaulted);
        Assert.NotEqual(ServiceIdentityPayloadAcceptanceState.Accepted, defaulted);
    }
}
