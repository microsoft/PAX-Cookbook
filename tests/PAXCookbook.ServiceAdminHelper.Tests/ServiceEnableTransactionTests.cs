using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PAXCookbook.ServiceAdminHelper.Enable;
using PAXCookbook.ServiceAdminHelper.Payload;
using PAXCookbook.ServiceAdminHelper.Signing;
using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbook.ServiceAdminHelper.Tests;

// ===========================================================================
// CYCLE 62 - ELEVATED SERVICE-ENABLE TRANSACTION (helper side)
// ===========================================================================
//
// SCOPE, stated plainly. Every collaborator is INJECTED and every destination
// lives in an OS TEMP directory. Nothing in this file elevates, triggers a UAC
// prompt, uses runas, starts ANY process, launches the helper, Setup, the App,
// the service, PAX or a Bake, touches the real Program Files or ProgramData
// tree, queries or mutates the real Service Control Manager, starts stops or
// deletes a real service, opens a certificate store, private key or credential
// vault, reads or writes the registry, or opens a socket. The REAL known-folder
// resolver, the REAL Windows security adapter and the REAL SCM adapter are
// never constructed.
//
// THE IDENTITY CHANNEL IS DRIVEN SAME-INTEGRITY. Both ends live in this one
// medium-integrity test process, exactly as the cycle-58 channel tests do. That
// cannot prove cross-integrity UAC behaviour and does not try to; what it proves
// is the ORDERING that is integrity-independent - preflight before the anchor is
// persisted, apply only after it is durable, and the single acknowledgement only
// after apply verified.
//
// WHAT THIS CLASS OWNS. The HELPER SIDE of the assembly boundary: the elevated
// grammar, transaction ordering, payload and path preflight, anchor persistence,
// extraction, access control, SCM registration and verification, idempotence,
// compensation and recovery-required behaviour. The non-elevated coordinator
// lives in the Setup assembly and is proven by ServiceEnableCoordinatorTests.
public sealed class ServiceEnableTransactionTests : IDisposable
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(15);

    private readonly string _sandbox;
    private readonly string _programFiles;
    private readonly string _serviceDirectory;

    public ServiceEnableTransactionTests()
    {
        _sandbox = Path.Combine(
            Path.GetTempPath(), "paxcookbook-cycle62-enable", Guid.NewGuid().ToString("N"));
        _programFiles = Path.Combine(_sandbox, "ProgramFiles");
        _serviceDirectory = Path.Combine(_sandbox, "ProgramData", "PAXCookbook", "Service");
        Directory.CreateDirectory(_programFiles);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandbox))
            {
                Directory.Delete(_sandbox, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort temp cleanup.
        }
    }

    // =======================================================================
    // FAKES - nothing here can reach a real machine surface
    // =======================================================================

    private sealed class FakeProgramFilesResolver : IServiceEnableProgramFilesResolver
    {
        private readonly string? _path;

        internal FakeProgramFilesResolver(string? path) => _path = path;

        public string? TryResolveProgramFilesX64() => _path;
    }

    private sealed class FakePayloadSource : IServiceEnablePayloadSource
    {
        private readonly byte[] _bytes;
        private readonly ServicePayloadVerificationResult? _forced;

        internal FakePayloadSource(byte[] bytes, ServicePayloadVerificationResult? forced = null)
        {
            _bytes = bytes;
            _forced = forced;
        }

        internal int Verifications;

        public ServicePayloadVerificationResult VerifyPayload(out ReadOnlyMemory<byte> archiveBytes)
        {
            Interlocked.Increment(ref Verifications);
            if (_forced.HasValue)
            {
                archiveBytes = ReadOnlyMemory<byte>.Empty;
                return _forced.Value;
            }

            ServicePayloadVerificationResult result = ServicePayloadResource.InspectBytes(_bytes);
            archiveBytes = result.IsVerified ? _bytes : ReadOnlyMemory<byte>.Empty;
            return result;
        }
    }

    private sealed class FakeSigningSource : IServiceEnableSigningPolicySource
    {
        private readonly ServiceHelperSigningPolicyState _state;

        internal FakeSigningSource(ServiceHelperSigningPolicyState state) => _state = state;

        public ServiceHelperSigningPolicyState ResolveConfiguredPolicy() => _state;
    }

    /// <summary>
    /// An in-memory access-control adapter. It records every application and
    /// verification so ordering can be asserted, and it can be scripted to fail
    /// at any one step.
    /// </summary>
    private sealed class FakeSecurityAdapter : IServiceEnableSecurityAdapter
    {
        internal List<string> StagingApplied { get; } = new();

        internal List<string> FinalApplied { get; } = new();

        internal List<string> FinalVerified { get; } = new();

        internal HashSet<string> Protected { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal string? ServiceSidSeen { get; private set; }

        internal bool FailStaging { get; set; }

        internal bool FailFinalApply { get; set; }

        internal bool FailFinalVerify { get; set; }

        internal bool RuntimeHostUntrusted { get; set; }

        public bool TryApplyStagingProtection(string directoryPath)
        {
            StagingApplied.Add(directoryPath);
            if (FailStaging)
            {
                return false;
            }
            Protected.Add(directoryPath);
            return true;
        }

        public bool TryApplyFinalProtection(string path, bool isDirectory, string serviceSid)
        {
            ServiceSidSeen = serviceSid;
            FinalApplied.Add(path);
            return !FailFinalApply;
        }

        public bool VerifyFinalProtection(string path, bool isDirectory, string serviceSid)
        {
            FinalVerified.Add(path);
            return !FailFinalVerify;
        }

        public bool VerifyStagingProtection(string directoryPath) =>
            !FailStaging && Protected.Contains(directoryPath);

        public bool RuntimeHostHasUntrustedWriteGrant(string filePath) => RuntimeHostUntrusted;
    }

    /// <summary>
    /// An in-memory Service Control Manager. It NEVER touches the real SCM, and
    /// it deliberately exposes no start or control member, so a production path
    /// that tried to start the service could not compile against it.
    /// </summary>
    private sealed class FakeServiceControlManager : IServiceControlManagerAdapter
    {
        private ServiceConfigurationSnapshot? _existing;
        private uint _sidType = ServiceEnableRegistrationContract.ServiceSidTypeNone;

        internal List<string> Calls { get; } = new();

        internal bool FailCreate { get; set; }

        internal bool FailSidTypeChange { get; set; }

        internal bool FailDelete { get; set; }

        internal bool QueryUnavailable { get; set; }

        internal string? ResolvedSid { get; set; } = "S-1-5-80-1234567890-1234567890-1234567890-1234567890-1234567890";

        internal bool Exists => _existing.HasValue;

        internal uint SidType => _sidType;

        internal void Seed(ServiceConfigurationSnapshot snapshot, uint sidType)
        {
            _existing = snapshot;
            _sidType = sidType;
        }

        public ServiceQueryState QueryConfiguration(string serviceName, out ServiceConfigurationSnapshot snapshot)
        {
            Calls.Add("Query:" + serviceName);
            snapshot = default;
            if (QueryUnavailable)
            {
                return ServiceQueryState.Unavailable;
            }
            if (_existing is null)
            {
                return ServiceQueryState.Absent;
            }
            snapshot = _existing.Value;
            return ServiceQueryState.Present;
        }

        public bool TryCreateService(ServiceConfigurationSnapshot desired)
        {
            Calls.Add("Create:" + desired.ServiceName);
            if (FailCreate)
            {
                return false;
            }
            _existing = desired;
            _sidType = ServiceEnableRegistrationContract.ServiceSidTypeNone;
            return true;
        }

        public bool TrySetUnrestrictedServiceSidType(string serviceName)
        {
            Calls.Add("SetSidType:" + serviceName);
            if (FailSidTypeChange || _existing is null)
            {
                return false;
            }
            _sidType = ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted;
            return true;
        }

        public bool TryQueryServiceSidType(string serviceName, out uint sidType)
        {
            Calls.Add("QuerySidType:" + serviceName);
            sidType = _sidType;
            return _existing is not null;
        }

        public string? TryResolveServiceSid(string serviceName)
        {
            Calls.Add("ResolveSid:" + serviceName);
            return _existing is null ? null : ResolvedSid;
        }

        public bool TryDeleteService(string serviceName)
        {
            Calls.Add("Delete:" + serviceName);
            if (FailDelete)
            {
                return false;
            }
            _existing = null;
            _sidType = ServiceEnableRegistrationContract.ServiceSidTypeNone;
            return true;
        }

        // CYCLE 63R, MECHANICAL COMPILE FIX ONLY. The interface gained three
        // members, so this double must declare them or nothing in this assembly
        // compiles. They RECORD the call and change no state, so every existing
        // assertion - including the ones proving the cycle-62 transaction starts
        // nothing - keeps exactly its previous meaning and strength.
        public ServiceStartAttemptResult TryStartService(string serviceName)
        {
            Calls.Add("Start:" + serviceName);
            return ServiceStartAttemptResult.Refused;
        }

        public ServiceQueryState QueryStatus(string serviceName, out ServiceStatusSnapshot snapshot)
        {
            Calls.Add("QueryStatus:" + serviceName);
            snapshot = default;
            return _existing is null ? ServiceQueryState.Absent : ServiceQueryState.Present;
        }

        public bool TryStopService(string serviceName)
        {
            Calls.Add("Stop:" + serviceName);
            return false;
        }
    }

    /// <summary>Records the exact order in which the channel drives a transaction.</summary>
    private sealed class RecordingTransaction : IServiceIdentityBoundTransaction
    {
        private readonly Func<string, bool> _anchorExists;
        private readonly ServiceIdentityBoundTransactionPreflightState _preflight;
        private readonly ServiceIdentityBoundTransactionResult _apply;
        private readonly string _serviceDirectory;

        internal RecordingTransaction(
            string serviceDirectory,
            Func<string, bool> anchorExists,
            ServiceIdentityBoundTransactionPreflightState preflight,
            ServiceIdentityBoundTransactionResult apply)
        {
            _serviceDirectory = serviceDirectory;
            _anchorExists = anchorExists;
            _preflight = preflight;
            _apply = apply;
        }

        internal List<string> Steps { get; } = new();

        internal bool AnchorPresentAtPreflight { get; private set; }

        internal bool AnchorPresentAtApply { get; private set; }

        internal bool? AnchorCreatedThisAttempt { get; private set; }

        public ServiceIdentityBoundTransactionPreflightState Preflight()
        {
            Steps.Add("preflight");
            AnchorPresentAtPreflight = _anchorExists(_serviceDirectory);
            return _preflight;
        }

        public ServiceIdentityBoundTransactionResult Apply(ServiceIdentityBoundTransactionContext context)
        {
            Steps.Add("apply");
            AnchorPresentAtApply = _anchorExists(context.ServiceDirectory);
            AnchorCreatedThisAttempt = context.AnchorCreatedThisAttempt;
            return _apply;
        }
    }

    // =======================================================================
    // SANDBOX HELPERS
    // =======================================================================

    private string AnchorPath =>
        Path.Combine(_serviceDirectory, ServiceMachineStorageContract.InstallationAnchorFileName);

    private ServiceEnableFixedPaths ResolvePaths()
    {
        Assert.Equal(
            ServiceEnablePathResolutionState.Resolved,
            ServiceEnableFixedPathResolver.TryResolve(
                new FakeProgramFilesResolver(_programFiles), out ServiceEnableFixedPaths paths));
        return paths;
    }

    private void CreateRuntimeHost()
    {
        ServiceEnableFixedPaths paths = ResolvePaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RuntimeHost)!);
        File.WriteAllBytes(paths.RuntimeHost, new byte[] { 0x4D, 0x5A });
    }

    private ServiceEnableTransaction CreateTransaction(
        FakeSecurityAdapter security,
        FakeServiceControlManager scm,
        FakePayloadSource? payload = null,
        ServiceHelperSigningPolicyState signing = ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed,
        string? programFilesOverride = null) =>
        new(
            _serviceDirectory,
            new FakeProgramFilesResolver(programFilesOverride ?? _programFiles),
            security,
            scm,
            payload ?? new FakePayloadSource(BuildCanonicalArchive()),
            new FakeSigningSource(signing));

    private static string CurrentSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Assert.NotNull(identity.User);
        return identity.User!.Value;
    }

    private void WriteAnchor(string sid, string installationId)
    {
        Directory.CreateDirectory(_serviceDirectory);
        var document = new ServiceInstallationAnchorDocument(
            installationId, sid, "2026-08-17T00:00:00Z");
        Assert.Equal(
            ServiceInstallationAnchorWriteState.Created,
            ServiceInstallationAnchorStore.WriteTo(_serviceDirectory, document));
    }

    // ---- deterministic archive construction --------------------------------

    private static byte[] MemberContent(string name) =>
        Encoding.ASCII.GetBytes(new string('B', 256) + name);

    private static string Sha256Upper(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static List<(string Name, byte[] Bytes)> CanonicalMembers() =>
        ServicePayloadArchiveFormat.RequiredMemberNames
            .Select(name => (Name: name, Bytes: MemberContent(name)))
            .ToList();

    private static string BuildManifestJson(List<(string Name, byte[] Bytes)> members)
    {
        var entries = members.Select(m =>
            "    {\n"
            + "      \"name\": \"" + m.Name + "\",\n"
            + "      \"sizeBytes\": " + m.Bytes.LongLength.ToString(CultureInfo.InvariantCulture) + ",\n"
            + "      \"sha256\": \"" + Sha256Upper(m.Bytes) + "\"\n"
            + "    }");

        return "{\n"
            + "  \"schemaVersion\": " + ServicePayloadArchiveFormat.SchemaVersion.ToString(CultureInfo.InvariantCulture) + ",\n"
            + "  \"targetOs\": \"" + ServicePayloadArchiveFormat.TargetOs + "\",\n"
            + "  \"targetArch\": \"" + ServicePayloadArchiveFormat.TargetArch + "\",\n"
            + "  \"files\": [\n"
            + string.Join(",\n", entries) + "\n"
            + "  ]\n"
            + "}\n";
    }

    private static byte[] BuildCanonicalArchive()
    {
        List<(string Name, byte[] Bytes)> members = CanonicalMembers();
        byte[] manifest = new UTF8Encoding(false).GetBytes(BuildManifestJson(members));

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in ServicePayloadArchiveFormat.OrderedEntryNames)
            {
                ZipArchiveEntry entry =
                    archive.CreateEntry(name, ServicePayloadArchiveFormat.FixedCompressionLevel);
                entry.LastWriteTime = ServicePayloadArchiveFormat.FixedEntryTimestamp;

                byte[] bytes = string.Equals(
                    name, ServicePayloadArchiveFormat.ManifestEntryName, StringComparison.Ordinal)
                    ? manifest
                    : members.First(m => string.Equals(m.Name, name, StringComparison.Ordinal)).Bytes;

                using Stream stream = entry.Open();
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        byte[] result = buffer.ToArray();
        Assert.True(ServicePayloadResource.InspectBytes(result).IsVerified);
        return result;
    }

    private static IReadOnlyList<ServicePayloadManifestFileEntryFacts> CanonicalFacts() =>
        CanonicalMembers()
            .Select(m => new ServicePayloadManifestFileEntryFacts(m.Name, m.Bytes.LongLength, Sha256Upper(m.Bytes)))
            .ToList();

    // =======================================================================
    // THE CLOSED ELEVATED GRAMMAR
    // =======================================================================

    private static string[] ValidElevatedArgv() =>
        ServiceEnableProtocol.ComposeElevatedArguments(
            ServiceInitiatingUserIdentityChannelContract.NewEndpointName(),
            new ServiceInitiatorProcessFacts(4321, 133_000_000_000_000_000L));

    [Fact]
    public void The_canonical_elevated_argument_vector_parses()
    {
        string[] argv = ValidElevatedArgv();
        Assert.Equal(ServiceEnableVerbs.ElevatedTokenCount, argv.Length);
        Assert.True(ServiceEnableProtocol.TryParseElevated(argv, out ServiceEnableElevatedArguments? parsed));
        Assert.NotNull(parsed);
        Assert.Equal(argv[2], parsed!.EndpointName);
        Assert.Equal(4321u, parsed.Initiator.ProcessId);
        Assert.Equal(133_000_000_000_000_000L, parsed.Initiator.CreationFileTime);
    }

    [Fact]
    public void An_argument_vector_with_an_extra_missing_or_reordered_token_is_refused()
    {
        string[] valid = ValidElevatedArgv();

        Assert.False(ServiceEnableProtocol.TryParseElevated(null, out _));
        Assert.False(ServiceEnableProtocol.TryParseElevated(Array.Empty<string>(), out _));
        Assert.False(ServiceEnableProtocol.TryParseElevated(valid.Take(5).ToArray(), out _));
        Assert.False(ServiceEnableProtocol.TryParseElevated(valid.Append("extra").ToArray(), out _));

        string[] reordered = (string[])valid.Clone();
        (reordered[1], reordered[3]) = (reordered[3], reordered[1]);
        Assert.False(ServiceEnableProtocol.TryParseElevated(reordered, out _));

        string[] duplicated = (string[])valid.Clone();
        duplicated[3] = ServiceEnableVerbs.EndpointOption;
        Assert.False(ServiceEnableProtocol.TryParseElevated(duplicated, out _));
    }

    [Theory]
    [InlineData(0, "service-enable")]
    [InlineData(0, "service-enable-elevated-extra")]
    [InlineData(1, "--endpoint-name")]
    [InlineData(3, "--pid")]
    [InlineData(5, "--created")]
    public void A_misspelled_verb_or_option_is_refused(int index, string replacement)
    {
        string[] argv = ValidElevatedArgv();
        argv[index] = replacement;
        Assert.False(ServiceEnableProtocol.TryParseElevated(argv, out _));
    }

    [Theory]
    [InlineData(2, "PAXCookbook.InitiatingUserIdentity.lowercasehexlowercasehex000000")]
    [InlineData(2, "PAXCookbook.InitiatingUserIdentity.")]
    [InlineData(4, "0")]
    [InlineData(4, "-1")]
    [InlineData(4, "+5")]
    [InlineData(4, "0x10")]
    [InlineData(4, " 5")]
    [InlineData(4, "1,000")]
    [InlineData(6, "0")]
    [InlineData(6, "-1")]
    [InlineData(6, "1.5")]
    public void A_malformed_value_is_refused(int index, string replacement)
    {
        string[] argv = ValidElevatedArgv();
        argv[index] = replacement;
        Assert.False(ServiceEnableProtocol.TryParseElevated(argv, out _));
    }

    [Fact]
    public void An_oversized_token_is_refused_before_any_parse()
    {
        string[] argv = ValidElevatedArgv();
        argv[2] = new string('A', ServiceEnableVerbs.MaxTokenLength + 1);
        Assert.False(ServiceEnableProtocol.TryParseElevated(argv, out _));
    }

    [Fact]
    public void The_elevated_grammar_admits_no_path_name_account_command_or_secret_parameter()
    {
        // The whole vocabulary is three option spellings. Anything that looks
        // like a path, a service name, an account or a command is rejected as an
        // unknown option rather than silently ignored.
        foreach (string hostile in new[]
        {
            "--path", "--directory", "--service-name", "--account", "--sid", "--command",
            "--registry", "--certificate", "--thumbprint", "--store", "--payload", "--output",
            "--env", "--password", "--secret", "--install-root",
        })
        {
            string[] argv = ValidElevatedArgv();
            argv[1] = hostile;
            Assert.False(ServiceEnableProtocol.TryParseElevated(argv, out _));
        }
    }

    [Fact]
    public void The_dispatch_refuses_a_non_conforming_argument_vector_with_a_usage_exit_code()
    {
        Assert.Equal(SetupExitCodes.UsageError, ServiceEnableElevatedDispatch.Run(Array.Empty<string>()));
        Assert.Equal(SetupExitCodes.UsageError, ServiceEnableElevatedDispatch.Run(new[] { "service-enable" }));
    }

    [Fact]
    public void Only_a_completed_channel_outcome_maps_to_a_zero_exit_code()
    {
        foreach (ServiceInitiatingUserIdentityChannelOutcome outcome
                 in Enum.GetValues<ServiceInitiatingUserIdentityChannelOutcome>())
        {
            int mapped = ServiceEnableElevatedDispatch.MapExitCode(outcome);
            if (outcome == ServiceInitiatingUserIdentityChannelOutcome.Completed)
            {
                Assert.Equal(SetupExitCodes.Ok, mapped);
            }
            else
            {
                Assert.NotEqual(SetupExitCodes.Ok, mapped);
            }
        }
    }

    // =======================================================================
    // IDENTITY-BOUND TRANSACTION ORDERING
    // =======================================================================

    private ServiceInitiatingUserIdentityChannelServer CreateBoundServer(
        IServiceIdentityBoundTransaction? transaction)
    {
        ServiceInitiatorProcessFacts initiator = ServiceInitiatorProcessBinding.CaptureCurrent();
        Assert.True(initiator.IsPresent);

        ServiceInitiatingUserIdentityChannelServer? server =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                ServiceInitiatingUserIdentityChannelContract.NewEndpointName(),
                initiator,
                new WindowsServiceInitiatorIdentityResolver(),
                transaction);
        Assert.NotNull(server);
        return server!;
    }

    [Fact]
    public void A_cycle_fifty_nine_endpoint_carries_no_bound_transaction()
    {
        using ServiceInitiatingUserIdentityChannelServer server =
            ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(
                ServiceInitiatingUserIdentityChannelContract.NewEndpointName(),
                ServiceInitiatorProcessBinding.CaptureCurrent(),
                new WindowsServiceInitiatorIdentityResolver())!;

        Assert.False(server.HasBoundTransaction);
    }

    [Fact]
    public void Preflight_runs_before_the_anchor_is_persisted_and_apply_only_after()
    {
        var transaction = new RecordingTransaction(
            _serviceDirectory,
            _ => File.Exists(AnchorPath),
            ServiceIdentityBoundTransactionPreflightState.Proceed,
            ServiceIdentityBoundTransactionResult.Completed());

        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer(transaction);
        Assert.True(server.HasBoundTransaction);

        string endpoint = server.EndpointName;
        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));

        ServiceInitiatingUserIdentityChannelOutcome clientOutcome =
            ServiceInitiatingUserIdentityChannelClient.Request(endpoint, Bounded);
        bool anchorPresentWhenAcknowledged = File.Exists(AnchorPath);

        ServiceInitiatingUserIdentityBindResult result = bind.GetAwaiter().GetResult();

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Completed, clientOutcome);
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "preflight", "apply" }, transaction.Steps);
        Assert.False(transaction.AnchorPresentAtPreflight);
        Assert.True(transaction.AnchorPresentAtApply);
        Assert.True(anchorPresentWhenAcknowledged);
        Assert.True(transaction.AnchorCreatedThisAttempt);
    }

    [Fact]
    public void A_refusing_preflight_stops_before_the_anchor_is_ever_written()
    {
        var transaction = new RecordingTransaction(
            _serviceDirectory,
            _ => File.Exists(AnchorPath),
            ServiceIdentityBoundTransactionPreflightState.Refused,
            ServiceIdentityBoundTransactionResult.Completed());

        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer(transaction);
        string endpoint = server.EndpointName;

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));
        ServiceInitiatingUserIdentityChannelOutcome clientOutcome =
            ServiceInitiatingUserIdentityChannelClient.Request(endpoint, Bounded);
        ServiceInitiatingUserIdentityBindResult result = bind.GetAwaiter().GetResult();

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused, result.Outcome);
        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused, clientOutcome);
        Assert.Equal(new[] { "preflight" }, transaction.Steps);
        Assert.False(result.AnchorPersisted);
        Assert.False(File.Exists(AnchorPath));
    }

    [Fact]
    public void A_preflight_that_requires_recovery_is_reported_as_recovery_required()
    {
        var transaction = new RecordingTransaction(
            _serviceDirectory,
            _ => File.Exists(AnchorPath),
            ServiceIdentityBoundTransactionPreflightState.RecoveryRequired,
            ServiceIdentityBoundTransactionResult.Completed());

        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer(transaction);
        string endpoint = server.EndpointName;

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));
        ServiceInitiatingUserIdentityChannelClient.Request(endpoint, Bounded);
        ServiceInitiatingUserIdentityBindResult result = bind.GetAwaiter().GetResult();

        Assert.Equal(
            ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired, result.Outcome);
        Assert.False(File.Exists(AnchorPath));
    }

    [Fact]
    public void A_compensated_apply_is_never_acknowledged_as_success()
    {
        var transaction = new RecordingTransaction(
            _serviceDirectory,
            _ => File.Exists(AnchorPath),
            ServiceIdentityBoundTransactionPreflightState.Proceed,
            ServiceIdentityBoundTransactionResult.Compensated(anchorRemoved: true));

        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer(transaction);
        string endpoint = server.EndpointName;

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));
        ServiceInitiatingUserIdentityChannelOutcome clientOutcome =
            ServiceInitiatingUserIdentityChannelClient.Request(endpoint, Bounded);
        ServiceInitiatingUserIdentityBindResult result = bind.GetAwaiter().GetResult();

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated, result.Outcome);
        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated, clientOutcome);
        Assert.False(result.AcknowledgementSent);
        Assert.Equal(new[] { "preflight", "apply" }, transaction.Steps);
    }

    [Fact]
    public void A_retry_over_an_existing_matching_anchor_reports_the_anchor_was_not_created_this_attempt()
    {
        WriteAnchor(CurrentSid(), Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture));

        var transaction = new RecordingTransaction(
            _serviceDirectory,
            _ => File.Exists(AnchorPath),
            ServiceIdentityBoundTransactionPreflightState.Proceed,
            ServiceIdentityBoundTransactionResult.Completed());

        using ServiceInitiatingUserIdentityChannelServer server = CreateBoundServer(transaction);
        string endpoint = server.EndpointName;

        Task<ServiceInitiatingUserIdentityBindResult> bind =
            Task.Run(() => server.AwaitAndBindIn(_serviceDirectory, Bounded));
        ServiceInitiatingUserIdentityChannelClient.Request(endpoint, Bounded);
        ServiceInitiatingUserIdentityBindResult result = bind.GetAwaiter().GetResult();

        Assert.Equal(ServiceInitiatingUserIdentityChannelOutcome.Completed, result.Outcome);
        Assert.True(transaction.AnchorPresentAtPreflight);
        Assert.False(transaction.AnchorCreatedThisAttempt);
    }

    // =======================================================================
    // FIXED PATHS - PROGRAM FILES x64 ONLY
    // =======================================================================

    [Fact]
    public void Every_destination_is_composed_from_the_one_resolved_program_files_base()
    {
        ServiceEnableFixedPaths paths = ResolvePaths();

        Assert.Equal(Path.Combine(_programFiles, "PAXCookbook"), paths.Root);
        Assert.Equal(Path.Combine(_programFiles, "PAXCookbook", "Service"), paths.Final);
        Assert.Equal(Path.Combine(_programFiles, "PAXCookbook", "Service.staging"), paths.Staging);
        Assert.Equal(Path.Combine(_programFiles, "dotnet", "dotnet.exe"), paths.RuntimeHost);
        Assert.NotEqual(paths.Final, paths.Staging);
    }

    [Fact]
    public void An_unresolvable_program_files_base_refuses_and_composes_nothing()
    {
        Assert.Equal(
            ServiceEnablePathResolutionState.ProgramFilesUnavailable,
            ServiceEnableFixedPathResolver.TryResolve(new FakeProgramFilesResolver(null), out _));
        Assert.Equal(
            ServiceEnablePathResolutionState.ProgramFilesUnavailable,
            ServiceEnableFixedPathResolver.TryResolve(new FakeProgramFilesResolver("   "), out _));
    }

    [Fact]
    public void The_fixed_path_contract_names_are_the_only_leaf_names_used()
    {
        Assert.Equal("PAXCookbook", ServiceEnableFixedPathContract.RootFolderName);
        Assert.Equal("Service", ServiceEnableFixedPathContract.FinalFolderName);
        Assert.Equal("Service.staging", ServiceEnableFixedPathContract.StagingFolderName);
        Assert.Equal("dotnet", ServiceEnableFixedPathContract.DotnetFolderName);
        Assert.Equal("dotnet.exe", ServiceEnableFixedPathContract.DotnetHostFileName);
    }

    // =======================================================================
    // PREFLIGHT - PURE, AND MUTATION-FREE BY CONSTRUCTION
    // =======================================================================

    private static ServiceEnablePreflightObservations CleanFirstEnable() =>
        new(
            signingPolicyPermits: true,
            payloadVerified: true,
            programFilesResolved: true,
            runtimeHostIsRegularFile: true,
            anyRelevantPathComponentIsReparsePoint: false,
            runtimeHostHasUntrustedWriteGrant: false,
            staging: ServiceEnableDirectoryState.Absent,
            final: ServiceEnableDirectoryState.Absent,
            servicePresence: ServiceQueryState.Absent,
            serviceConfigurationMatchesExactly: false,
            anchorAbsent: true,
            anchorMatchesThisInitiator: false);

    private static ServiceEnablePreflightObservations IdempotentRetry() =>
        new(
            signingPolicyPermits: true,
            payloadVerified: true,
            programFilesResolved: true,
            runtimeHostIsRegularFile: true,
            anyRelevantPathComponentIsReparsePoint: false,
            runtimeHostHasUntrustedWriteGrant: false,
            staging: ServiceEnableDirectoryState.Absent,
            final: ServiceEnableDirectoryState.ExactMatch,
            servicePresence: ServiceQueryState.Present,
            serviceConfigurationMatchesExactly: true,
            anchorAbsent: false,
            anchorMatchesThisInitiator: true);

    [Fact]
    public void A_clean_first_enable_proceeds()
    {
        ServiceEnablePreflightResult result = ServiceEnablePreflight.Evaluate(CleanFirstEnable());
        Assert.Equal(ServiceEnablePreflightVerdict.Proceed, result.Verdict);
        Assert.Equal(ServiceEnablePreflightReason.Satisfied, result.Reason);
    }

    [Fact]
    public void An_exactly_matching_existing_state_with_a_matching_anchor_proceeds_idempotently()
    {
        ServiceEnablePreflightResult result = ServiceEnablePreflight.Evaluate(IdempotentRetry());
        Assert.Equal(ServiceEnablePreflightVerdict.Proceed, result.Verdict);
    }

    [Fact]
    public void A_build_whose_signing_policy_refuses_never_proceeds()
    {
        ServiceEnablePreflightObservations o = CleanFirstEnable();
        var refused = new ServiceEnablePreflightObservations(
            signingPolicyPermits: false,
            o.PayloadVerified, o.ProgramFilesResolved, o.RuntimeHostIsRegularFile,
            o.AnyRelevantPathComponentIsReparsePoint, o.RuntimeHostHasUntrustedWriteGrant,
            o.Staging, o.Final, o.ServicePresence, o.ServiceConfigurationMatchesExactly,
            o.AnchorAbsent, o.AnchorMatchesThisInitiator);

        ServiceEnablePreflightResult result = ServiceEnablePreflight.Evaluate(refused);
        Assert.Equal(ServiceEnablePreflightVerdict.Refused, result.Verdict);
        Assert.Equal(ServiceEnablePreflightReason.SigningPolicyRefused, result.Reason);
    }

    [Fact]
    public void An_unverified_payload_never_proceeds()
    {
        ServiceEnablePreflightObservations o = CleanFirstEnable();
        var refused = new ServiceEnablePreflightObservations(
            o.SigningPolicyPermits, payloadVerified: false, o.ProgramFilesResolved,
            o.RuntimeHostIsRegularFile, o.AnyRelevantPathComponentIsReparsePoint,
            o.RuntimeHostHasUntrustedWriteGrant, o.Staging, o.Final, o.ServicePresence,
            o.ServiceConfigurationMatchesExactly, o.AnchorAbsent, o.AnchorMatchesThisInitiator);

        Assert.Equal(
            ServiceEnablePreflightReason.PayloadUnverified,
            ServiceEnablePreflight.Evaluate(refused).Reason);
    }

    [Fact]
    public void A_missing_or_non_regular_runtime_host_never_proceeds()
    {
        ServiceEnablePreflightObservations o = CleanFirstEnable();
        var refused = new ServiceEnablePreflightObservations(
            o.SigningPolicyPermits, o.PayloadVerified, o.ProgramFilesResolved,
            runtimeHostIsRegularFile: false, o.AnyRelevantPathComponentIsReparsePoint,
            o.RuntimeHostHasUntrustedWriteGrant, o.Staging, o.Final, o.ServicePresence,
            o.ServiceConfigurationMatchesExactly, o.AnchorAbsent, o.AnchorMatchesThisInitiator);

        Assert.Equal(
            ServiceEnablePreflightReason.RuntimeHostUnusable,
            ServiceEnablePreflight.Evaluate(refused).Reason);
    }

    [Fact]
    public void A_reparse_point_on_any_relevant_component_never_proceeds()
    {
        ServiceEnablePreflightObservations o = CleanFirstEnable();
        var refused = new ServiceEnablePreflightObservations(
            o.SigningPolicyPermits, o.PayloadVerified, o.ProgramFilesResolved,
            o.RuntimeHostIsRegularFile, anyRelevantPathComponentIsReparsePoint: true,
            o.RuntimeHostHasUntrustedWriteGrant, o.Staging, o.Final, o.ServicePresence,
            o.ServiceConfigurationMatchesExactly, o.AnchorAbsent, o.AnchorMatchesThisInitiator);

        Assert.Equal(
            ServiceEnablePreflightReason.ReparsePointRefused,
            ServiceEnablePreflight.Evaluate(refused).Reason);
    }

    [Fact]
    public void A_runtime_host_an_untrusted_principal_can_rewrite_never_proceeds()
    {
        ServiceEnablePreflightObservations o = CleanFirstEnable();
        var refused = new ServiceEnablePreflightObservations(
            o.SigningPolicyPermits, o.PayloadVerified, o.ProgramFilesResolved,
            o.RuntimeHostIsRegularFile, o.AnyRelevantPathComponentIsReparsePoint,
            runtimeHostHasUntrustedWriteGrant: true, o.Staging, o.Final, o.ServicePresence,
            o.ServiceConfigurationMatchesExactly, o.AnchorAbsent, o.AnchorMatchesThisInitiator);

        Assert.Equal(
            ServiceEnablePreflightReason.RuntimeHostWritableByUntrustedPrincipal,
            ServiceEnablePreflight.Evaluate(refused).Reason);
    }

    [Fact]
    public void An_absent_anchor_with_any_existing_footprint_requires_recovery()
    {
        ServiceEnablePreflightObservations o = CleanFirstEnable();

        var withFinal = new ServiceEnablePreflightObservations(
            o.SigningPolicyPermits, o.PayloadVerified, o.ProgramFilesResolved,
            o.RuntimeHostIsRegularFile, o.AnyRelevantPathComponentIsReparsePoint,
            o.RuntimeHostHasUntrustedWriteGrant, ServiceEnableDirectoryState.Absent,
            ServiceEnableDirectoryState.ExactMatch, ServiceQueryState.Absent,
            serviceConfigurationMatchesExactly: false, anchorAbsent: true,
            anchorMatchesThisInitiator: false);

        var withService = new ServiceEnablePreflightObservations(
            o.SigningPolicyPermits, o.PayloadVerified, o.ProgramFilesResolved,
            o.RuntimeHostIsRegularFile, o.AnyRelevantPathComponentIsReparsePoint,
            o.RuntimeHostHasUntrustedWriteGrant, ServiceEnableDirectoryState.Absent,
            ServiceEnableDirectoryState.Absent, ServiceQueryState.Present,
            serviceConfigurationMatchesExactly: true, anchorAbsent: true,
            anchorMatchesThisInitiator: false);

        var withStaging = new ServiceEnablePreflightObservations(
            o.SigningPolicyPermits, o.PayloadVerified, o.ProgramFilesResolved,
            o.RuntimeHostIsRegularFile, o.AnyRelevantPathComponentIsReparsePoint,
            o.RuntimeHostHasUntrustedWriteGrant, ServiceEnableDirectoryState.RecoverableRemnant,
            ServiceEnableDirectoryState.Absent, ServiceQueryState.Absent,
            serviceConfigurationMatchesExactly: false, anchorAbsent: true,
            anchorMatchesThisInitiator: false);

        foreach (ServiceEnablePreflightObservations observations in new[] { withFinal, withService, withStaging })
        {
            Assert.True(ServiceEnablePreflight.HasAnyFootprint(observations));
            ServiceEnablePreflightResult result = ServiceEnablePreflight.Evaluate(observations);
            Assert.Equal(ServiceEnablePreflightVerdict.RecoveryRequired, result.Verdict);
            Assert.Equal(ServiceEnablePreflightReason.UnexplainedExistingFootprint, result.Reason);
        }
    }

    [Fact]
    public void A_foreign_final_directory_is_refused_and_never_adopted()
    {
        ServiceEnablePreflightObservations o = IdempotentRetry();
        var foreign = new ServiceEnablePreflightObservations(
            o.SigningPolicyPermits, o.PayloadVerified, o.ProgramFilesResolved,
            o.RuntimeHostIsRegularFile, o.AnyRelevantPathComponentIsReparsePoint,
            o.RuntimeHostHasUntrustedWriteGrant, ServiceEnableDirectoryState.Absent,
            ServiceEnableDirectoryState.Foreign, ServiceQueryState.Absent,
            serviceConfigurationMatchesExactly: false, anchorAbsent: false,
            anchorMatchesThisInitiator: true);

        ServiceEnablePreflightResult result = ServiceEnablePreflight.Evaluate(foreign);
        Assert.NotEqual(ServiceEnablePreflightVerdict.Proceed, result.Verdict);
        Assert.Equal(ServiceEnablePreflightReason.FinalStateRefused, result.Reason);
    }

    [Fact]
    public void A_mismatched_service_with_the_fixed_name_is_refused_and_never_touched()
    {
        ServiceEnablePreflightObservations o = IdempotentRetry();
        var mismatched = new ServiceEnablePreflightObservations(
            o.SigningPolicyPermits, o.PayloadVerified, o.ProgramFilesResolved,
            o.RuntimeHostIsRegularFile, o.AnyRelevantPathComponentIsReparsePoint,
            o.RuntimeHostHasUntrustedWriteGrant, ServiceEnableDirectoryState.Absent,
            ServiceEnableDirectoryState.ExactMatch, ServiceQueryState.Present,
            serviceConfigurationMatchesExactly: false, anchorAbsent: false,
            anchorMatchesThisInitiator: true);

        Assert.Equal(
            ServiceEnablePreflightReason.ServiceStateRefused,
            ServiceEnablePreflight.Evaluate(mismatched).Reason);
    }

    [Fact]
    public void An_unavailable_service_query_is_never_treated_as_absence()
    {
        ServiceEnablePreflightObservations o = CleanFirstEnable();
        var unavailable = new ServiceEnablePreflightObservations(
            o.SigningPolicyPermits, o.PayloadVerified, o.ProgramFilesResolved,
            o.RuntimeHostIsRegularFile, o.AnyRelevantPathComponentIsReparsePoint,
            o.RuntimeHostHasUntrustedWriteGrant, ServiceEnableDirectoryState.Absent,
            ServiceEnableDirectoryState.Absent, ServiceQueryState.Unavailable,
            serviceConfigurationMatchesExactly: false, anchorAbsent: true,
            anchorMatchesThisInitiator: false);

        Assert.NotEqual(
            ServiceEnablePreflightVerdict.Proceed, ServiceEnablePreflight.Evaluate(unavailable).Verdict);
    }

    [Fact]
    public void A_default_preflight_result_is_never_proceed()
    {
        Assert.Equal(ServiceEnablePreflightVerdict.Unspecified, default(ServiceEnablePreflightResult).Verdict);
        Assert.NotEqual(ServiceEnablePreflightVerdict.Proceed, default(ServiceEnablePreflightResult).Verdict);
    }

    // =======================================================================
    // THE TRANSACTION'S OWN PREFLIGHT - MUTATION-FREE
    // =======================================================================

    [Fact]
    public void The_transaction_preflight_writes_nothing()
    {
        CreateRuntimeHost();
        var security = new FakeSecurityAdapter();
        var scm = new FakeServiceControlManager();
        ServiceEnableTransaction transaction = CreateTransaction(security, scm);

        ServiceIdentityBoundTransactionPreflightState state = transaction.Preflight();

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, state);
        ServiceEnableFixedPaths paths = ResolvePaths();
        Assert.False(Directory.Exists(paths.Root));
        Assert.False(Directory.Exists(paths.Final));
        Assert.False(Directory.Exists(paths.Staging));
        Assert.False(File.Exists(AnchorPath));
        Assert.Empty(security.StagingApplied);
        Assert.Empty(security.FinalApplied);
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("SetSidType:", StringComparison.Ordinal));
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_transaction_preflight_refuses_a_build_the_signing_policy_does_not_permit()
    {
        CreateRuntimeHost();
        ServiceEnableTransaction transaction = CreateTransaction(
            new FakeSecurityAdapter(),
            new FakeServiceControlManager(),
            signing: ServiceHelperSigningPolicyState.SigningPolicyUnavailable);

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Refused, transaction.Preflight());
        Assert.Equal(
            ServiceEnablePreflightReason.SigningPolicyRefused, transaction.LastPreflight.Reason);
    }

    [Fact]
    public void The_transaction_preflight_refuses_an_unverified_payload()
    {
        CreateRuntimeHost();
        ServiceEnableTransaction transaction = CreateTransaction(
            new FakeSecurityAdapter(),
            new FakeServiceControlManager(),
            new FakePayloadSource(
                Array.Empty<byte>(),
                ServicePayloadVerificationResult.Refused(ServicePayloadVerificationOutcome.ResourceMissing)));

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Refused, transaction.Preflight());
        Assert.Equal(ServiceEnablePreflightReason.PayloadUnverified, transaction.LastPreflight.Reason);
    }

    [Fact]
    public void The_transaction_preflight_refuses_when_the_runtime_host_is_absent()
    {
        // Deliberately do NOT create dotnet.exe.
        ServiceEnableTransaction transaction = CreateTransaction(
            new FakeSecurityAdapter(), new FakeServiceControlManager());

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Refused, transaction.Preflight());
        Assert.Equal(ServiceEnablePreflightReason.RuntimeHostUnusable, transaction.LastPreflight.Reason);
    }

    [Fact]
    public void The_transaction_preflight_refuses_a_runtime_host_an_untrusted_principal_can_rewrite()
    {
        CreateRuntimeHost();
        var security = new FakeSecurityAdapter { RuntimeHostUntrusted = true };
        ServiceEnableTransaction transaction = CreateTransaction(security, new FakeServiceControlManager());

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Refused, transaction.Preflight());
        Assert.Equal(
            ServiceEnablePreflightReason.RuntimeHostWritableByUntrustedPrincipal,
            transaction.LastPreflight.Reason);
    }

    [Fact]
    public void The_transaction_preflight_requires_recovery_for_a_footprint_with_no_anchor()
    {
        CreateRuntimeHost();
        ServiceEnableFixedPaths paths = ResolvePaths();
        Directory.CreateDirectory(paths.Final);

        ServiceEnableTransaction transaction = CreateTransaction(
            new FakeSecurityAdapter(), new FakeServiceControlManager());

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.RecoveryRequired, transaction.Preflight());
        Assert.Equal(
            ServiceEnablePreflightReason.UnexplainedExistingFootprint, transaction.LastPreflight.Reason);
        Assert.True(Directory.Exists(paths.Final));
    }

    // =======================================================================
    // EXTRACTION
    // =======================================================================

    [Fact]
    public void Extraction_creates_only_the_fixed_locations_and_lands_every_frozen_member_once()
    {
        CreateRuntimeHost();
        ServiceEnableFixedPaths paths = ResolvePaths();
        var security = new FakeSecurityAdapter();

        ServiceEnableExtractionResult result =
            ServiceEnableExtractor.ExtractVerifiedArchive(BuildCanonicalArchive(), paths, security);

        Assert.Equal(ServiceEnableExtractionState.Extracted, result.State);
        Assert.True(Directory.Exists(paths.Final));
        Assert.False(Directory.Exists(paths.Staging));

        var actual = Directory
            .GetFiles(paths.Final, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(paths.Final, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        string[] expected = ServicePayloadArchiveFormat.OrderedEntryNames
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(ServicePayloadArchiveFormat.RequiredMemberNames.Count + 1, actual.Length);
    }

    [Fact]
    public void The_staging_protection_is_applied_before_any_member_is_written()
    {
        CreateRuntimeHost();
        ServiceEnableFixedPaths paths = ResolvePaths();
        var security = new FakeSecurityAdapter();

        ServiceEnableExtractor.ExtractVerifiedArchive(BuildCanonicalArchive(), paths, security);

        Assert.Contains(paths.Staging, security.StagingApplied);
        Assert.Equal(paths.Staging, security.StagingApplied[0]);
    }

    [Fact]
    public void A_staging_protection_failure_stops_before_a_single_byte_is_written()
    {
        CreateRuntimeHost();
        ServiceEnableFixedPaths paths = ResolvePaths();
        var security = new FakeSecurityAdapter { FailStaging = true };

        ServiceEnableExtractionResult result =
            ServiceEnableExtractor.ExtractVerifiedArchive(BuildCanonicalArchive(), paths, security);

        Assert.NotEqual(ServiceEnableExtractionState.Extracted, result.State);
        Assert.False(Directory.Exists(paths.Final));
        if (Directory.Exists(paths.Staging))
        {
            Assert.Empty(Directory.GetFiles(paths.Staging, "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public void Every_extracted_member_reproduces_its_declared_size_and_hash()
    {
        CreateRuntimeHost();
        ServiceEnableFixedPaths paths = ResolvePaths();

        Assert.Equal(
            ServiceEnableExtractionState.Extracted,
            ServiceEnableExtractor.ExtractVerifiedArchive(
                BuildCanonicalArchive(), paths, new FakeSecurityAdapter()).State);

        foreach (ServicePayloadManifestFileEntryFacts facts in CanonicalFacts())
        {
            string path = Path.Combine(paths.Final, facts.Name.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path));
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(facts.SizeBytes, bytes.LongLength);
            Assert.Equal(facts.Sha256, Sha256Upper(bytes));
        }

        Assert.True(ServiceEnableExtractor.VerifyExactMembership(paths.Final, CanonicalFacts()));
    }

    [Fact]
    public void An_archive_that_does_not_verify_is_never_extracted()
    {
        CreateRuntimeHost();
        ServiceEnableFixedPaths paths = ResolvePaths();

        ServiceEnableExtractionResult result = ServiceEnableExtractor.ExtractVerifiedArchive(
            new byte[] { 1, 2, 3, 4 }, paths, new FakeSecurityAdapter());

        Assert.Equal(ServiceEnableExtractionState.ArchiveRefused, result.State);
        Assert.False(Directory.Exists(paths.Final));
        Assert.False(Directory.Exists(paths.Staging));
    }

    [Fact]
    public void An_existing_final_directory_is_never_overwritten_by_a_promotion()
    {
        CreateRuntimeHost();
        ServiceEnableFixedPaths paths = ResolvePaths();
        Directory.CreateDirectory(paths.Final);
        string foreign = Path.Combine(paths.Final, "someone-elses-file.txt");
        File.WriteAllText(foreign, "not ours");

        ServiceEnableExtractionResult result = ServiceEnableExtractor.ExtractVerifiedArchive(
            BuildCanonicalArchive(), paths, new FakeSecurityAdapter());

        Assert.NotEqual(ServiceEnableExtractionState.Extracted, result.State);
        Assert.True(File.Exists(foreign));
        Assert.Equal("not ours", File.ReadAllText(foreign));
    }

    [Fact]
    public void A_reparse_point_on_the_destination_chain_is_detected()
    {
        CreateRuntimeHost();
        ServiceEnableFixedPaths paths = ResolvePaths();

        // A real directory is not a reparse point; a genuine junction would be.
        Directory.CreateDirectory(paths.Root);
        Assert.False(ServiceEnableExtractor.AnyComponentIsReparsePoint(paths.Root, _programFiles));

        // Creating a symbolic link needs SeCreateSymbolicLinkPrivilege, which a
        // non-elevated test process without Developer Mode does not hold. When it
        // is unavailable the positive case above still runs; the negative case is
        // asserted only when a real reparse point could actually be created.
        string linkTarget = Path.Combine(_sandbox, "link-target");
        Directory.CreateDirectory(linkTarget);
        string link = Path.Combine(_sandbox, "a-link");
        try
        {
            Directory.CreateSymbolicLink(link, linkTarget);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.True(ServiceEnableExtractor.AnyComponentIsReparsePoint(link, _sandbox));
    }

    [Fact]
    public void Membership_verification_refuses_an_unexpected_sibling()
    {
        CreateRuntimeHost();
        ServiceEnableFixedPaths paths = ResolvePaths();

        Assert.Equal(
            ServiceEnableExtractionState.Extracted,
            ServiceEnableExtractor.ExtractVerifiedArchive(
                BuildCanonicalArchive(), paths, new FakeSecurityAdapter()).State);

        File.WriteAllText(Path.Combine(paths.Final, "unexpected-sibling.dll"), "x");
        Assert.False(ServiceEnableExtractor.VerifyExactMembership(paths.Final, CanonicalFacts()));
    }

    [Fact]
    public void Membership_verification_refuses_a_mutated_member()
    {
        CreateRuntimeHost();
        ServiceEnableFixedPaths paths = ResolvePaths();

        Assert.Equal(
            ServiceEnableExtractionState.Extracted,
            ServiceEnableExtractor.ExtractVerifiedArchive(
                BuildCanonicalArchive(), paths, new FakeSecurityAdapter()).State);

        string victim = Path.Combine(paths.Final, ServiceEnableRegistrationContract.ServiceAssemblyFileName);
        File.WriteAllBytes(victim, Encoding.ASCII.GetBytes("tampered"));
        Assert.False(ServiceEnableExtractor.VerifyExactMembership(paths.Final, CanonicalFacts()));
    }

    // =======================================================================
    // SERVICE REGISTRATION - FIXED, VERIFIED, AND NEVER STARTED
    // =======================================================================

    [Fact]
    public void The_expected_configuration_is_the_fixed_product_identity()
    {
        ServiceEnableFixedPaths paths = ResolvePaths();
        ServiceConfigurationSnapshot expected =
            ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final);

        Assert.Equal(ServiceIdentityContract.ServiceName, expected.ServiceName);
        Assert.Equal(ServiceIdentityContract.ServiceDisplayName, expected.DisplayName);
        Assert.Equal(ServiceIdentityContract.QualifiedServiceAccountName, expected.AccountName);
        Assert.Equal(ServiceEnableRegistrationContract.ServiceTypeOwnProcess, expected.ServiceType);
        Assert.Equal(ServiceEnableRegistrationContract.StartTypeAutomatic, expected.StartType);
        Assert.Equal(ServiceEnableRegistrationContract.ErrorControlNormal, expected.ErrorControl);
        Assert.False(expected.HasDependencies);
        Assert.False(expected.HasLoadOrderGroup);
        Assert.False(expected.HasTag);

        // The image path is the fixed runtime host followed by the fixed
        // assembly, and carries NO caller-supplied argument.
        Assert.Equal(
            "\"" + paths.RuntimeHost + "\" \""
            + Path.Combine(paths.Final, ServiceEnableRegistrationContract.ServiceAssemblyFileName) + "\"",
            expected.BinaryPath);
    }

    [Fact]
    public void Configuration_verification_compares_the_account_string_and_never_translates_it_to_a_sid()
    {
        ServiceEnableFixedPaths paths = ResolvePaths();
        ServiceConfigurationSnapshot expected =
            ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final);

        var renamedAccount = new ServiceConfigurationSnapshot(
            expected.ServiceName, expected.DisplayName, expected.BinaryPath,
            @"NT SERVICE\SomethingElse", expected.ServiceType, expected.StartType,
            expected.ErrorControl, false, false, false);

        Assert.True(expected.MatchesExactly(expected));
        Assert.False(expected.MatchesExactly(renamedAccount));
        Assert.Equal(@"NT SERVICE\PAXCookbookService", expected.AccountName);
    }

    [Theory]
    [InlineData("displayName")]
    [InlineData("binaryPath")]
    [InlineData("serviceType")]
    [InlineData("startType")]
    [InlineData("errorControl")]
    [InlineData("dependencies")]
    [InlineData("loadOrderGroup")]
    [InlineData("tag")]
    public void Any_single_configuration_difference_fails_exact_verification(string field)
    {
        ServiceEnableFixedPaths paths = ResolvePaths();
        ServiceConfigurationSnapshot e = ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final);

        var mutated = new ServiceConfigurationSnapshot(
            e.ServiceName,
            field == "displayName" ? "Another Display Name" : e.DisplayName,
            field == "binaryPath" ? @"C:\somewhere\else.exe" : e.BinaryPath,
            e.AccountName,
            field == "serviceType" ? 0x20u : e.ServiceType,
            field == "startType" ? 3u : e.StartType,
            field == "errorControl" ? 3u : e.ErrorControl,
            field == "dependencies",
            field == "loadOrderGroup",
            field == "tag");

        Assert.False(e.MatchesExactly(mutated));
    }

    [Fact]
    public void Registration_creates_sets_the_unrestricted_sid_type_and_verifies_before_resolving_the_sid()
    {
        CreateRuntimeHost();
        var security = new FakeSecurityAdapter();
        var scm = new FakeServiceControlManager();
        ServiceEnableTransaction transaction = CreateTransaction(security, scm);

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());

        var context = new ServiceIdentityBoundTransactionContext(
            _serviceDirectory,
            new ServiceInstallationAnchorDocument(
                Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), CurrentSid(), "2026-08-17T00:00:00Z"),
            anchorCreatedThisAttempt: true);

        WriteAnchor(context.Anchor.InitiatingUserSid, context.Anchor.InstallationId);

        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Completed, result.State);
        Assert.True(scm.Exists);
        Assert.Equal(ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted, scm.SidType);

        int create = scm.Calls.FindIndex(c => c.StartsWith("Create:", StringComparison.Ordinal));
        int setSid = scm.Calls.FindIndex(c => c.StartsWith("SetSidType:", StringComparison.Ordinal));
        int querySid = scm.Calls.FindIndex(c => c.StartsWith("QuerySidType:", StringComparison.Ordinal));
        int resolve = scm.Calls.FindIndex(c => c.StartsWith("ResolveSid:", StringComparison.Ordinal));

        Assert.True(create >= 0);
        Assert.True(setSid > create);
        Assert.True(querySid > setSid);
        Assert.True(resolve > querySid);

        // The service SID is granted rights only AFTER it was resolved.
        Assert.Equal(scm.ResolvedSid, security.ServiceSidSeen);
        Assert.NotEmpty(security.FinalApplied);
        Assert.NotEmpty(security.FinalVerified);

        // NOTHING started the service.
        Assert.DoesNotContain(scm.Calls, c => c.Contains("Start", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(scm.Calls, c => c.Contains("Control", StringComparison.OrdinalIgnoreCase));
    }

    // CYCLE 63RR. THE EXACT SCM SURFACE.
    //
    // This replaces the cycle-62 assertion that the adapter exposed NO member
    // whose name contained Start, Control or Stop. That premise is obsolete:
    // cycle 63R deliberately added StartServiceW, QueryServiceStatusEx and
    // ControlService(SERVICE_CONTROL_STOP) because registration alone never
    // proved the service starts. A name-shaped prohibition is also weak - it
    // would have accepted a "Begin"/"Halt"/"Signal" member. What replaces it is
    // strictly stronger: the EXACT nine-member interface, pinned by name, count,
    // return type, parameter count, parameter type and out shape; the EXACT
    // eleven approved native declarations, one each; the exact call shapes of
    // the three added calls; and an executable guard proving every forbidden
    // mechanism is absent from the whole Enable production surface.
    //
    // INSTRUMENT SCOPE AND LIMITS, stated so no reader over-reads it. The
    // reflection half inspects the COMPILED interface, so it is exact. The
    // source half reads the checked-in C# with comments, string literals and
    // character literals REMOVED - the file headers legitimately name every
    // forbidden mechanism in prose, so an un-stripped scan would be guaranteed
    // to fail and an un-calibrated one guaranteed to pass. The stripper is
    // CALIBRATED in-test against a positive control containing every forbidden
    // token, so a zero here is a measured zero and not a silent one. It does not
    // prove runtime behaviour, and nothing in this test starts, stops, queries
    // or contacts any real service.
    [Fact]
    public void The_service_control_adapter_exposes_exactly_the_nine_approved_members_and_no_other_mechanism()
    {
        // ---- 1. THE EXACT INTERFACE SURFACE -----------------------------
        (string Name, Type Return, Type[] Parameters, int OutIndex)[] approved =
        {
            ("QueryConfiguration", typeof(ServiceQueryState),
                new[] { typeof(string), typeof(ServiceConfigurationSnapshot) }, 1),
            ("TryCreateService", typeof(bool),
                new[] { typeof(ServiceConfigurationSnapshot) }, -1),
            ("TrySetUnrestrictedServiceSidType", typeof(bool), new[] { typeof(string) }, -1),
            ("TryQueryServiceSidType", typeof(bool), new[] { typeof(string), typeof(uint) }, 1),
            ("TryResolveServiceSid", typeof(string), new[] { typeof(string) }, -1),
            ("TryDeleteService", typeof(bool), new[] { typeof(string) }, -1),
            ("TryStartService", typeof(ServiceStartAttemptResult), new[] { typeof(string) }, -1),
            ("QueryStatus", typeof(ServiceQueryState),
                new[] { typeof(string), typeof(ServiceStatusSnapshot) }, 1),
            ("TryStopService", typeof(bool), new[] { typeof(string) }, -1),
        };

        System.Reflection.MethodInfo[] members = typeof(IServiceControlManagerAdapter).GetMethods();

        Assert.Equal(9, approved.Length);
        Assert.Equal(approved.Length, members.Length);
        Assert.Equal(
            approved.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            members.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // NO OVERLOADS: every declared name occurs exactly once.
        Assert.Equal(approved.Length, members.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count());

        foreach ((string name, Type expectedReturn, Type[] expectedParameters, int outIndex) in approved)
        {
            System.Reflection.MethodInfo member = Assert.Single(members, m => m.Name == name);
            Assert.Equal(expectedReturn, member.ReturnType);

            System.Reflection.ParameterInfo[] parameters = member.GetParameters();
            Assert.Equal(expectedParameters.Length, parameters.Length);

            for (int i = 0; i < parameters.Length; i++)
            {
                bool expectedOut = i == outIndex;
                Assert.Equal(expectedOut, parameters[i].IsOut);
                Assert.Equal(expectedOut, parameters[i].ParameterType.IsByRef);
                Assert.Equal(
                    expectedParameters[i],
                    expectedOut ? parameters[i].ParameterType.GetElementType() : parameters[i].ParameterType);
            }
        }

        // The stop member carries NO control-code parameter, so no alternate
        // control code is representable through the adapter's own API.
        Assert.Equal(
            new[] { typeof(string) },
            typeof(IServiceControlManagerAdapter).GetMethod("TryStopService")!
                .GetParameters().Select(p => p.ParameterType).ToArray());

        // ---- 2. THE EXACT NATIVE DECLARATIONS ---------------------------
        string[] approvedNative =
        {
            "OpenSCManagerW", "OpenServiceW", "CreateServiceW", "QueryServiceConfigW",
            "ChangeServiceConfig2W", "QueryServiceConfig2W", "DeleteService", "StartServiceW",
            "QueryServiceStatusEx", "ControlService", "CloseServiceHandle",
        };

        string adapterRelative =
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceControlManagerAdapter.cs";
        string adapterCode = StripCommentsAndLiterals(ReadRepoText(adapterRelative));
        string adapterFlat = System.Text.RegularExpressions.Regex.Replace(adapterCode, @"\s+", " ");

        string[] declared = System.Text.RegularExpressions.Regex
            .Matches(adapterCode, @"\bstatic\s+extern\s+[^\(;]*?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(")
            .Select(m => m.Groups["name"].Value)
            .ToArray();

        Assert.Equal(approvedNative.Length, declared.Length);
        Assert.Equal(
            approvedNative.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            declared.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(declared.Length, declared.Distinct(StringComparer.Ordinal).Count());

        // ---- 3. THE EXACT SHAPE OF THE THREE ADDED CALLS ----------------
        //
        // Statement granularity: whitespace is collapsed first, so a wrapped
        // call cannot hide from a substring pin.
        Assert.Contains("StartServiceW(service, 0, IntPtr.Zero)", adapterFlat, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(adapterFlat, "StartServiceW"));

        Assert.Contains(
            "QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, (uint)size, out _)",
            adapterFlat,
            StringComparison.Ordinal);
        Assert.Contains("private const uint ScStatusProcessInfo = 0;", adapterFlat, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(adapterFlat, "QueryServiceStatusEx"));

        Assert.Contains(
            "ControlService(service, ServiceControlStop, ref status)", adapterFlat, StringComparison.Ordinal);
        Assert.Contains(
            "private const uint ServiceControlStop = 0x00000001;", adapterFlat, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(adapterFlat, "ControlService("));

        // SERVICE_CONTROL_STOP is the ONLY control code that exists in source.
        string[] controlCodes = System.Text.RegularExpressions.Regex
            .Matches(adapterCode, @"\bServiceControl(?!Manager)[A-Z][A-Za-z0-9_]*")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "ServiceControlStop" }, controlCodes);

        // Neither an arbitrary machine nor an alternate SCM database is ever
        // named: every connection is the local default database.
        Assert.Equal(
            CountOccurrences(adapterFlat, "OpenSCManagerW("),
            CountOccurrences(adapterFlat, "OpenSCManagerW(null, null, ") + 1);

        // Every one of the nine members guards the FIXED service name first.
        Assert.True(CountOccurrences(adapterFlat, "IsFixedServiceName") >= approved.Length);

        // ---- 4. THE EXECUTABLE GUARD, OVER THE WHOLE ENABLE SURFACE -----
        string[] enableSources =
        {
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceControlManagerAdapter.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceDisableElevatedDispatch.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceDisableTransaction.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableAclProfile.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableActivationStage.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableElevatedDispatch.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableExtractor.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableFixedPaths.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnablePreflight.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceEnableTransaction.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceMachineDataAclProfile.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceRunningProcessVerifier.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceRuntimeDocumentVerifier.cs",
            "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceStartabilityCoordinator.cs",
        };

        string[] forbidden =
        {
            "sc.exe", "wmic", "powershell.exe", "pwsh.exe", "cmd.exe",
            "Get-Service", "Start-Service", "Stop-Service", "New-Service", "Set-Service",
            "Remove-Service", "Restart-Service",
            "System.ServiceProcess", "ServiceController",
            "System.Management", "ManagementObject", "ManagementClass", "Win32_Service",
            "Process.Start", "ProcessStartInfo", "UseShellExecute", "ShellExecute",
            "TerminateProcess", "GenerateConsoleCtrlEvent", "PROCESS_TERMINATE", "PROCESS_ALL_ACCESS",
            ".Kill(", "ExitProcess",
            "ControlServiceExW", "StartServiceCtrlDispatcher",
            "ServiceControlPause", "ServiceControlContinue", "ServiceControlInterrogate",
            "ServiceControlShutdown", "ServiceControlParamChange", "ServiceControlUserDefined",
            "SERVICE_CONTROL_PAUSE", "SERVICE_CONTROL_CONTINUE", "SERVICE_CONTROL_INTERROGATE",
            "SERVICE_CONTROL_SHUTDOWN",
        };

        // POSITIVE CONTROL. Every token must FIRE on a sample that genuinely
        // contains it, so a clean scan below is a measured zero rather than a
        // broken matcher. Each token is placed in real code position, not in a
        // comment or a literal, because those are exactly what gets stripped.
        foreach (string token in forbidden)
        {
            string sample = StripCommentsAndLiterals("class C { void M() { var x = " + token + "; } }");
            Assert.True(
                sample.Contains(token, StringComparison.Ordinal),
                "forbidden-token instrument failed to detect " + token);
        }

        // NEGATIVE CONTROL. The stripper must actually remove prose and
        // literals, or the scan below would be guaranteed to fail rather than
        // guaranteed to pass.
        string stripped = StripCommentsAndLiterals(
            "// sc.exe and ServiceController\n/* Win32_Service */ var a = \"powershell.exe\"; var b = @\"wmic\";");
        foreach (string token in new[] { "sc.exe", "ServiceController", "Win32_Service", "powershell.exe", "wmic" })
        {
            Assert.False(
                stripped.Contains(token, StringComparison.Ordinal),
                "stripper left " + token + " behind");
        }

        foreach (string relative in enableSources)
        {
            string code = StripCommentsAndLiterals(ReadRepoText(relative));
            foreach (string token in forbidden)
            {
                Assert.False(
                    code.Contains(token, StringComparison.OrdinalIgnoreCase),
                    relative + " reaches a forbidden mechanism: " + token);
            }
        }

        // ---- LOCAL INSTRUMENTS ------------------------------------------
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
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PAXCookbook.sln")))
            {
                directory = directory.Parent;
            }
            Assert.NotNull(directory);

            string full = Path.Combine(
                directory!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), "missing source: " + relative);
            return File.ReadAllText(full);
        }

        // Removes line comments, block comments, string literals (normal,
        // verbatim and interpolated) and character literals, leaving executable
        // identifiers behind. Prose and literals are exactly where the forbidden
        // mechanisms are legitimately NAMED, so they must not be scanned.
        static string StripCommentsAndLiterals(string source)
        {
            var output = new StringBuilder(source.Length);
            int i = 0;
            while (i < source.Length)
            {
                char c = source[i];

                if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        i++;
                    }
                    continue;
                }

                if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                    {
                        i++;
                    }
                    i = Math.Min(i + 2, source.Length);
                    continue;
                }

                if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
                {
                    i += 2;
                    while (i < source.Length)
                    {
                        if (source[i] == '"')
                        {
                            if (i + 1 < source.Length && source[i + 1] == '"')
                            {
                                i += 2;
                                continue;
                            }
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }

                if (c == '"')
                {
                    i++;
                    while (i < source.Length && source[i] != '"')
                    {
                        i += source[i] == '\\' ? 2 : 1;
                    }
                    i++;
                    continue;
                }

                if (c == '\'')
                {
                    i++;
                    while (i < source.Length && source[i] != '\'')
                    {
                        i += source[i] == '\\' ? 2 : 1;
                    }
                    i++;
                    continue;
                }

                output.Append(c);
                i++;
            }

            return output.ToString();
        }
    }

    // =======================================================================
    // ACCESS CONTROL PROFILES
    // =======================================================================

    [Fact]
    public void The_staging_profile_admits_only_the_two_administrative_identities()
    {
        IReadOnlyList<KeyValuePair<string, System.Security.AccessControl.FileSystemRights>> expected =
            ServiceEnableAclProfile.ExpectedStagingAces();

        Assert.Equal(2, expected.Count);
        Assert.Contains(expected, e => e.Key == ServiceEnableAclProfile.LocalSystemSid);
        Assert.Contains(expected, e => e.Key == ServiceEnableAclProfile.BuiltinAdministratorsSid);
        Assert.All(expected, e => Assert.Equal(ServiceEnableAclProfile.AdministrativeRights, e.Value));
    }

    [Fact]
    public void The_final_profile_adds_read_and_execute_for_the_service_and_nothing_else()
    {
        const string serviceSid = "S-1-5-80-1-2-3-4-5";
        IReadOnlyList<KeyValuePair<string, System.Security.AccessControl.FileSystemRights>> expected =
            ServiceEnableAclProfile.ExpectedFinalAces(serviceSid);

        Assert.Equal(3, expected.Count);
        KeyValuePair<string, System.Security.AccessControl.FileSystemRights> service =
            Assert.Single(expected, e => e.Key == serviceSid);
        Assert.Equal(ServiceEnableAclProfile.ServiceRights, service.Value);

        // The service may RUN what it was installed with and may never REWRITE it.
        Assert.Equal(
            System.Security.AccessControl.FileSystemRights.ReadAndExecute
            | System.Security.AccessControl.FileSystemRights.Synchronize,
            ServiceEnableAclProfile.ServiceRights);
        Assert.NotEqual(
            ServiceEnableAclProfile.AdministrativeRights, ServiceEnableAclProfile.ServiceRights);
    }

    [Fact]
    public void The_owner_of_every_installed_object_is_builtin_administrators()
    {
        Assert.Equal("S-1-5-32-544", ServiceEnableAclProfile.BuiltinAdministratorsSid);
        Assert.Equal("S-1-5-18", ServiceEnableAclProfile.LocalSystemSid);

        System.Security.AccessControl.DirectorySecurity staging =
            ServiceEnableAclProfile.BuildStagingDirectorySecurity();
        Assert.Equal(
            ServiceEnableAclProfile.BuiltinAdministratorsSid,
            ((SecurityIdentifier)staging.GetOwner(typeof(SecurityIdentifier))!).Value);
        Assert.True(staging.AreAccessRulesProtected);
    }

    [Fact]
    public void A_profile_with_one_extra_grant_fails_exact_verification()
    {
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetOwner(ServiceEnableAclProfile.BuiltinAdministrators);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            ServiceEnableAclProfile.LocalSystem,
            ServiceEnableAclProfile.AdministrativeRights,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit
            | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            ServiceEnableAclProfile.BuiltinAdministrators,
            ServiceEnableAclProfile.AdministrativeRights,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit
            | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));

        Assert.True(ServiceEnableAclProfile.MatchesProfile(
            security, ServiceEnableAclProfile.ExpectedStagingAces()));

        // One extra allow ACE for Everyone breaks it.
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new SecurityIdentifier("S-1-1-0"),
            System.Security.AccessControl.FileSystemRights.ReadAndExecute,
            System.Security.AccessControl.InheritanceFlags.None,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));

        Assert.False(ServiceEnableAclProfile.MatchesProfile(
            security, ServiceEnableAclProfile.ExpectedStagingAces()));
    }

    [Fact]
    public void A_runtime_host_writable_by_a_non_administrator_is_reported_untrusted()
    {
        var security = new System.Security.AccessControl.FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new SecurityIdentifier("S-1-5-32-545"),   // BUILTIN\Users
            System.Security.AccessControl.FileSystemRights.WriteData,
            System.Security.AccessControl.AccessControlType.Allow));

        Assert.True(ServiceEnableAclProfile.HasUntrustedWriteGrant(
            security.GetAccessRules(true, true, typeof(SecurityIdentifier))));

        var safe = new System.Security.AccessControl.FileSecurity();
        safe.SetAccessRuleProtection(true, false);
        safe.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new SecurityIdentifier("S-1-5-32-545"),
            System.Security.AccessControl.FileSystemRights.ReadAndExecute,
            System.Security.AccessControl.AccessControlType.Allow));

        Assert.False(ServiceEnableAclProfile.HasUntrustedWriteGrant(
            safe.GetAccessRules(true, true, typeof(SecurityIdentifier))));
    }

    // =======================================================================
    // IDEMPOTENCE AFTER A LOST ACKNOWLEDGEMENT
    // =======================================================================

    [Fact]
    public void A_retry_over_exactly_matching_state_completes_without_creating_anything_again()
    {
        CreateRuntimeHost();
        var security = new FakeSecurityAdapter();
        var scm = new FakeServiceControlManager();
        ServiceEnableTransaction first = CreateTransaction(security, scm);

        var anchor = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), CurrentSid(), "2026-08-17T00:00:00Z");
        WriteAnchor(anchor.InitiatingUserSid, anchor.InstallationId);
        var context = new ServiceIdentityBoundTransactionContext(_serviceDirectory, anchor, true);

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, first.Preflight());
        Assert.Equal(ServiceIdentityBoundTransactionState.Completed, first.Apply(context).State);

        var retrySecurity = new FakeSecurityAdapter();
        ServiceEnableTransaction retry = CreateTransaction(retrySecurity, scm);
        var retryContext = new ServiceIdentityBoundTransactionContext(
            _serviceDirectory, anchor, anchorCreatedThisAttempt: false);

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, retry.Preflight());
        Assert.Equal(ServiceIdentityBoundTransactionState.Completed, retry.Apply(retryContext).State);

        // The service was created exactly ONCE across both attempts.
        Assert.Equal(1, scm.Calls.Count(c => c.StartsWith("Create:", StringComparison.Ordinal)));
        Assert.True(scm.Exists);
        Assert.True(File.Exists(AnchorPath));
    }

    // =======================================================================
    // COMPENSATION AND RECOVERY-REQUIRED
    // =======================================================================

    [Fact]
    public void A_failed_service_creation_compensates_everything_it_created_including_the_anchor()
    {
        CreateRuntimeHost();
        var security = new FakeSecurityAdapter();
        var scm = new FakeServiceControlManager { FailCreate = true };
        ServiceEnableTransaction transaction = CreateTransaction(security, scm);

        var anchor = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), CurrentSid(), "2026-08-17T00:00:00Z");
        WriteAnchor(anchor.InitiatingUserSid, anchor.InstallationId);
        var context = new ServiceIdentityBoundTransactionContext(_serviceDirectory, anchor, true);

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        Assert.True(result.AnchorRemoved);

        ServiceEnableFixedPaths paths = ResolvePaths();
        Assert.False(Directory.Exists(paths.Final));
        Assert.False(Directory.Exists(paths.Staging));
        Assert.False(scm.Exists);
        Assert.False(File.Exists(AnchorPath));
    }

    [Fact]
    public void A_reused_anchor_is_never_removed_by_compensation()
    {
        CreateRuntimeHost();
        var scm = new FakeServiceControlManager { FailCreate = true };
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);

        var anchor = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), CurrentSid(), "2026-08-17T00:00:00Z");
        WriteAnchor(anchor.InitiatingUserSid, anchor.InstallationId);
        var context = new ServiceIdentityBoundTransactionContext(
            _serviceDirectory, anchor, anchorCreatedThisAttempt: false);

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        Assert.False(result.AnchorRemoved);
        Assert.True(File.Exists(AnchorPath));
    }

    [Fact]
    public void A_compensation_that_cannot_delete_the_service_it_created_requires_recovery()
    {
        CreateRuntimeHost();
        var scm = new FakeServiceControlManager { FailSidTypeChange = true, FailDelete = true };
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);

        var anchor = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), CurrentSid(), "2026-08-17T00:00:00Z");
        WriteAnchor(anchor.InitiatingUserSid, anchor.InstallationId);
        var context = new ServiceIdentityBoundTransactionContext(_serviceDirectory, anchor, true);

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);
        Assert.False(result.AnchorRemoved);

        // The anchor is PRESERVED so an attended recovery has something to work from.
        Assert.True(File.Exists(AnchorPath));
    }

    [Fact]
    public void A_final_access_control_verification_failure_compensates_rather_than_reporting_success()
    {
        CreateRuntimeHost();
        var security = new FakeSecurityAdapter { FailFinalVerify = true };
        var scm = new FakeServiceControlManager();
        ServiceEnableTransaction transaction = CreateTransaction(security, scm);

        var anchor = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), CurrentSid(), "2026-08-17T00:00:00Z");
        WriteAnchor(anchor.InitiatingUserSid, anchor.InstallationId);
        var context = new ServiceIdentityBoundTransactionContext(_serviceDirectory, anchor, true);

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.NotEqual(ServiceIdentityBoundTransactionState.Completed, result.State);
        Assert.False(scm.Exists);
    }

    [Fact]
    public void Every_anchor_removal_condition_is_required()
    {
        var all = new ServiceEnableAnchorRemovalFacts(true, true, true, true, true, true, true, true);
        Assert.True(ServiceEnableTransaction.AnchorRemovalAuthorized(all));

        for (int i = 0; i < 8; i++)
        {
            var facts = new ServiceEnableAnchorRemovalFacts(
                i != 0, i != 1, i != 2, i != 3, i != 4, i != 5, i != 6, i != 7);
            Assert.False(ServiceEnableTransaction.AnchorRemovalAuthorized(facts));
        }
    }

    [Fact]
    public void The_narrow_anchor_removal_primitive_refuses_a_non_matching_identity()
    {
        WriteAnchor(CurrentSid(), Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture));

        var other = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), CurrentSid(), "2026-08-17T00:00:00Z");

        Assert.Equal(
            ServiceInstallationAnchorRemovalState.IdentityMismatch,
            ServiceInstallationAnchorStore.RemoveAnchorCreatedThisTransactionFrom(_serviceDirectory, other));
        Assert.True(File.Exists(AnchorPath));
    }

    [Fact]
    public void The_narrow_anchor_removal_primitive_removes_only_an_exactly_matching_anchor()
    {
        string installationId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        WriteAnchor(CurrentSid(), installationId);

        var exact = new ServiceInstallationAnchorDocument(
            installationId, CurrentSid(), "2026-08-17T00:00:00Z");

        Assert.Equal(
            ServiceInstallationAnchorRemovalState.Removed,
            ServiceInstallationAnchorStore.RemoveAnchorCreatedThisTransactionFrom(_serviceDirectory, exact));
        Assert.False(File.Exists(AnchorPath));

        // A second removal is a bounded already-absent, not a failure.
        Assert.Equal(
            ServiceInstallationAnchorRemovalState.AlreadyAbsent,
            ServiceInstallationAnchorStore.RemoveAnchorCreatedThisTransactionFrom(_serviceDirectory, exact));
    }

    [Fact]
    public void The_narrow_anchor_removal_primitive_never_removes_state_that_does_not_validate()
    {
        Directory.CreateDirectory(_serviceDirectory);
        File.WriteAllText(AnchorPath, "{ not a valid anchor");

        var any = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), CurrentSid(), "2026-08-17T00:00:00Z");

        Assert.Equal(
            ServiceInstallationAnchorRemovalState.IdentityMismatch,
            ServiceInstallationAnchorStore.RemoveAnchorCreatedThisTransactionFrom(_serviceDirectory, any));
        Assert.True(File.Exists(AnchorPath));
    }

    [Fact]
    public void The_ownership_ledger_is_only_ever_checked_for_existence()
    {
        // The fixed name is read from the storage contract and never composed
        // here; nothing in this cycle opens, parses or enumerates the ledger.
        Assert.Equal("ownership-ledger.json", ServiceMachineStorageContract.OwnershipLedgerFileName);
        Assert.Equal(
            "installation-anchor.json" + ".tmp", ServiceInstallationAnchorStore.TemporaryLeafFileName);
    }

    // =======================================================================
    // BOUNDED SURFACES LEAK NOTHING
    // =======================================================================

    [Fact]
    public void Bounded_results_carry_only_their_token()
    {
        Assert.Equal(
            nameof(ServiceEnableFixedPaths),
            ResolvePaths().ToString());
        Assert.Equal(
            nameof(ServiceConfigurationSnapshot),
            ServiceConfigurationSnapshot.Expected(@"C:\x\dotnet.exe", @"C:\y").ToString());
        Assert.Equal(
            nameof(ServiceIdentityBoundTransactionContext),
            new ServiceIdentityBoundTransactionContext(
                _serviceDirectory,
                new ServiceInstallationAnchorDocument("a", "b", "c"),
                false).ToString());
        Assert.Equal(
            ServiceEnableExtractionState.Unavailable.ToString(),
            ServiceEnableExtractionResult.Refused(ServiceEnableExtractionState.Unavailable).ToString());
    }

    [Fact]
    public void A_default_transaction_result_is_never_completed()
    {
        Assert.Equal(
            ServiceIdentityBoundTransactionState.Unspecified,
            default(ServiceIdentityBoundTransactionResult).State);
        Assert.False(default(ServiceIdentityBoundTransactionResult).AnchorRemoved);
        Assert.Equal(ServiceEnableOperationOutcome.Unspecified, default(ServiceEnableOperationOutcome));
    }

    // =======================================================================
    // CYCLE 67 - THE CAUSE SURVIVES COMPENSATION
    // =======================================================================
    //
    // WHY THIS SECTION EXISTS. The first real attended service-enable failed
    // with a bare exit code and NO bounded reason, because Compensate
    // OVERWROTE the original cause with the disposition "Compensated". These
    // tests assert the two axes SEPARATELY: FailureCause says WHY, and
    // FailureDisposition says WHAT WAS LEFT BEHIND. Neither may erase the other.

    private string MachineRootDirectory => Path.GetDirectoryName(_serviceDirectory)!;

    /// <summary>
    /// Runs preflight with the fixed machine data directories in whatever state
    /// the caller staged, THEN persists the anchor - which is exactly the order
    /// the channel enforces, and the only order in which the ownership probe is
    /// meaningful.
    /// </summary>
    private ServiceIdentityBoundTransactionContext PreflightThenPersistAnchor(
        ServiceEnableTransaction transaction,
        ServiceIdentityBoundTransactionPreflightState expected =
            ServiceIdentityBoundTransactionPreflightState.Proceed)
    {
        Assert.Equal(expected, transaction.Preflight());

        var anchor = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            CurrentSid(),
            "2026-08-17T00:00:00Z");
        WriteAnchor(anchor.InitiatingUserSid, anchor.InstallationId);

        return new ServiceIdentityBoundTransactionContext(_serviceDirectory, anchor, true);
    }

    [Fact]
    public void A_refused_extraction_keeps_its_cause_through_a_successful_compensation()
    {
        CreateRuntimeHost();
        var scm = new FakeServiceControlManager();
        ServiceEnableTransaction transaction =
            CreateTransaction(new FakeSecurityAdapter { FailStaging = true }, scm);

        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);

        // The LEGACY conflated field still reads Compensated - roughly 133 tests
        // depend on that - and the SEPARATE cause survived it.
        Assert.Equal(ServiceEnableOperationOutcome.Compensated, transaction.LastOutcome);
        Assert.Equal(ServiceEnableFailureCause.ExtractionRefused, transaction.FailureCause);
        Assert.Equal(ServiceEnableFailureDisposition.Compensated, transaction.FailureDisposition);
    }

    [Fact]
    public void A_refused_registration_keeps_its_cause_through_a_successful_compensation()
    {
        CreateRuntimeHost();
        var scm = new FakeServiceControlManager { FailCreate = true };
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);

        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        Assert.Equal(ServiceEnableOperationOutcome.Compensated, transaction.LastOutcome);

        // CYCLE 71. This seam fails CreateServiceW, which is now classified as
        // its OWN cause instead of the collapsed RegistrationRefused category.
        Assert.Equal(ServiceEnableFailureCause.ServiceCreationRefused, transaction.FailureCause);
        Assert.Equal(ServiceEnableFailureDisposition.Compensated, transaction.FailureDisposition);
    }

    [Fact]
    public void A_failed_verification_keeps_its_cause_through_a_successful_compensation()
    {
        CreateRuntimeHost();
        var scm = new FakeServiceControlManager();
        ServiceEnableTransaction transaction =
            CreateTransaction(new FakeSecurityAdapter { FailFinalVerify = true }, scm);

        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        Assert.Equal(ServiceEnableFailureCause.VerificationRefused, transaction.FailureCause);
        Assert.Equal(ServiceEnableFailureDisposition.Compensated, transaction.FailureDisposition);
    }

    [Fact]
    public void A_refused_registration_keeps_its_cause_through_a_FAILED_compensation()
    {
        CreateRuntimeHost();
        var scm = new FakeServiceControlManager { FailSidTypeChange = true, FailDelete = true };
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);

        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);

        // A FAILED compensation overwrites the legacy field with RecoveryRequired
        // and STILL may not erase why the attempt failed.
        Assert.Equal(ServiceEnableOperationOutcome.RecoveryRequired, transaction.LastOutcome);

        // CYCLE 71. This seam fails the SID-TYPE CONFIGURATION call, which is now
        // classified apart from creation, resolution and verification.
        Assert.Equal(ServiceEnableFailureCause.ServiceSidConfigurationRefused, transaction.FailureCause);
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, transaction.FailureDisposition);
    }

    [Fact]
    public void A_failed_verification_keeps_its_cause_through_a_FAILED_compensation()
    {
        CreateRuntimeHost();
        var scm = new FakeServiceControlManager { FailDelete = true };
        ServiceEnableTransaction transaction =
            CreateTransaction(new FakeSecurityAdapter { FailFinalVerify = true }, scm);

        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);
        Assert.Equal(ServiceEnableFailureCause.VerificationRefused, transaction.FailureCause);
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, transaction.FailureDisposition);
    }

    // =======================================================================
    // CYCLE 71 - THE POST-CREATE ENABLE STAGE IS NO LONGER ONE BLIND CATEGORY
    // =======================================================================
    //
    // WHY THIS SECTION EXISTS. FIVE distinct production refusals in Apply all
    // collapsed onto the single cause RegistrationRefused, so the one bounded
    // line that crosses the process boundary could not distinguish them:
    //
    //   L771  !TryCreateService(expected)                     service creation
    //   L779  presence/MatchesExactly mismatch                EXISTING service
    //   L786  !TrySetUnrestrictedServiceSidType               SID-TYPE CONFIG
    //   L812  TryResolveServiceSid empty                      SID RESOLUTION
    //   L819  !ApplyFinalProtectionEverywhere                 final Program Files ACL
    //
    // (L790-L800 already failed as VerificationFailed, so SID-type VERIFICATION
    // was already distinct from SID-type CONFIGURATION and is untouched here.)
    //
    // WHAT THESE TESTS DELIBERATELY DO NOT DO. They do NOT encode, assert, hint
    // or narrow WHICH branch occurred during any live attended run. Each test
    // drives exactly ONE branch through an established injected seam and asserts
    // only that branch's own classification.
    //
    // SCOPE, unchanged from the rest of this file: every collaborator is
    // injected, every destination is an OS TEMP directory, no process starts, no
    // real SCM is touched, no service is created, started, stopped or deleted,
    // and no PAX or Bake runs.

    /// <summary>
    /// Asserts the bounded outward surface for one classified transaction: the
    /// exact cause, the exact disposition, and that the ONE process-boundary
    /// line is a single line of fixed lowercase tokens carrying no native value.
    /// </summary>
    private static void AssertBoundedClassification(
        ServiceEnableTransaction transaction,
        ServiceEnableFailureCause expectedCause,
        ServiceEnableFailureDisposition expectedDisposition)
    {
        Assert.Equal(expectedCause, transaction.FailureCause);
        Assert.Equal(expectedDisposition, transaction.FailureDisposition);

        // The cause and the disposition are INDEPENDENT axes: a disposition
        // value can never be read back as a cause.
        Assert.NotEqual(ServiceEnableFailureCause.None, transaction.FailureCause);

        string line = ServiceEnableFailureContract.FormatDiagnostic(
            transaction.FailureCause, transaction.FailureDisposition);

        // EXACTLY ONE line after process-boundary formatting.
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Single(line.Split('\n'));

        // EXACTLY the two documented fields and no third field.
        string[] fields = line.Split(' ');
        Assert.Equal(3, fields.Length);
        Assert.Equal("service-enable", fields[0]);
        Assert.Equal("outcome=" + ServiceEnableFailureContract.CauseToken(expectedCause), fields[1]);
        Assert.Equal(
            "disposition=" + ServiceEnableFailureContract.DispositionToken(expectedDisposition),
            fields[2]);

        // NO NATIVE VALUE LEAKAGE. Every token is fixed lowercase letters and
        // underscores, so a Win32 status, HRESULT, PID, SID, path or timestamp
        // cannot be hiding inside the line.
        Assert.Equal(line.ToLowerInvariant(), line);
        Assert.DoesNotContain(line, c => char.IsDigit(c));
        Assert.DoesNotContain('\\', line);
        Assert.DoesNotContain('/', line);
        Assert.DoesNotContain(':', line);
        Assert.DoesNotContain('%', line);
        Assert.DoesNotContain("s-1-", line, StringComparison.Ordinal);
        Assert.DoesNotContain("0x", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_service_creation_refusal_is_classified_as_service_creation_and_not_as_bare_registration()
    {
        CreateRuntimeHost();
        var scm = new FakeServiceControlManager { FailCreate = true };
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);

        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        AssertBoundedClassification(
            transaction,
            ServiceEnableFailureCause.ServiceCreationRefused,
            ServiceEnableFailureDisposition.Compensated);

        // COMPENSATION BEHAVIOUR: creation never succeeded, so nothing was
        // created and nothing may be deleted.
        Assert.False(scm.Exists);
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));

        // NO CAUSE OVERWRITE. The legacy conflated field was overwritten by the
        // disposition, exactly as before; the cause axis survived it intact.
        Assert.Equal(ServiceEnableOperationOutcome.Compensated, transaction.LastOutcome);
        Assert.Equal(ServiceEnableFailureCause.ServiceCreationRefused, transaction.FailureCause);
    }

    [Fact]
    public void An_existing_mismatched_service_is_classified_as_existing_state_and_is_never_touched()
    {
        // CYCLE 71, RULING 2. This is a BOUNDED CLASSIFICATION CORRECTION, not
        // purely additive observability: this branch's reported cause and its
        // exit code CHANGE from registration_refused to existing_state_refused.
        // The BEHAVIOUR - never modify, never adopt, never delete - is asserted
        // here to be unchanged.
        CreateRuntimeHost();
        var scm = new FakeServiceControlManager();
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);

        // Preflight observes an ABSENT service, so the refusal under test is the
        // Apply-time mismatch branch and not the preflight's ServiceStateRefused.
        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);

        // A foreign service with the fixed name appears between preflight and
        // apply. Only the display name differs, so presence is Present and
        // MatchesExactly is false - exactly the L779 branch.
        ServiceEnableFixedPaths paths = ResolvePaths();
        ServiceConfigurationSnapshot expected =
            ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final);
        var foreign = new ServiceConfigurationSnapshot(
            expected.ServiceName, "A Foreign Display Name", expected.BinaryPath,
            expected.AccountName, expected.ServiceType, expected.StartType,
            expected.ErrorControl, false, false, false);
        scm.Seed(foreign, ServiceEnableRegistrationContract.ServiceSidTypeNone);

        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        // THE DISPOSITION IS RecoveryRequired, AND THAT IS THE PRE-EXISTING,
        // CORRECT BEHAVIOUR - not something this cycle introduced. Compensation
        // may only remove the anchor when the fixed service is ABSENT
        // (AnchorRemovalAuthorized requires FixedServiceAbsent). A foreign
        // service bearing the fixed name is still present and may never be
        // deleted to make room, so machine state cannot be proven closed. Cycle
        // 71 changed ONLY the cause on this branch, never the disposition.
        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);
        AssertBoundedClassification(
            transaction,
            ServiceEnableFailureCause.ExistingStateRefused,
            ServiceEnableFailureDisposition.RecoveryRequired);

        // THE ANCHOR IS PRESERVED, which is the whole point of refusing to
        // report a clean compensation here.
        Assert.True(File.Exists(AnchorPath));

        // BEHAVIOUR UNCHANGED. The foreign service is NEVER deleted, never
        // reconfigured, never adopted and never has its SID type changed.
        Assert.True(scm.Exists);
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("SetSidType:", StringComparison.Ordinal));
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("Start:", StringComparison.Ordinal));
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("Stop:", StringComparison.Ordinal));
        Assert.Equal(ServiceEnableRegistrationContract.ServiceSidTypeNone, scm.SidType);

        // The seeded configuration is byte-for-byte what it was: nothing adopted it.
        Assert.Equal(
            ServiceQueryState.Present,
            scm.QueryConfiguration(
                ServiceIdentityContract.ServiceName, out ServiceConfigurationSnapshot after));
        Assert.True(foreign.MatchesExactly(after));
        Assert.False(expected.MatchesExactly(after));
    }

    [Fact]
    public void A_sid_type_configuration_refusal_is_classified_apart_from_sid_type_verification()
    {
        CreateRuntimeHost();
        var scm = new FakeServiceControlManager { FailSidTypeChange = true };
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);

        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        AssertBoundedClassification(
            transaction,
            ServiceEnableFailureCause.ServiceSidConfigurationRefused,
            ServiceEnableFailureDisposition.Compensated);

        // CONFIGURATION is not VERIFICATION. The already-distinct verification
        // branch reports its own cause and must not be reachable from here.
        Assert.NotEqual(ServiceEnableFailureCause.VerificationRefused, transaction.FailureCause);
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("QuerySidType:", StringComparison.Ordinal));

        // COMPENSATION BEHAVIOUR: this attempt DID create the service, so this
        // attempt deletes it, and proves it absent.
        Assert.Contains(scm.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));
        Assert.Contains(scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.False(scm.Exists);

        Assert.Equal(ServiceEnableOperationOutcome.Compensated, transaction.LastOutcome);
        Assert.Equal(ServiceEnableFailureCause.ServiceSidConfigurationRefused, transaction.FailureCause);
    }

    [Fact]
    public void A_service_sid_resolution_refusal_is_classified_apart_from_sid_type_configuration()
    {
        CreateRuntimeHost();

        // The NARROWEST possible seam for this branch: the service is created
        // and its SID type is configured and verified successfully, and only the
        // fixed SID resolution returns nothing.
        var scm = new FakeServiceControlManager { ResolvedSid = null };
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);

        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        AssertBoundedClassification(
            transaction,
            ServiceEnableFailureCause.ServiceSidResolutionRefused,
            ServiceEnableFailureDisposition.Compensated);

        // The earlier stages genuinely SUCCEEDED, so this is the resolution
        // branch and not an unrelated earlier failure wearing its name.
        Assert.Contains(scm.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));
        Assert.Contains(scm.Calls, c => c.StartsWith("SetSidType:", StringComparison.Ordinal));
        Assert.Contains(scm.Calls, c => c.StartsWith("QuerySidType:", StringComparison.Ordinal));
        Assert.Contains(scm.Calls, c => c.StartsWith("ResolveSid:", StringComparison.Ordinal));

        // The final protection profile was NEVER applied, because no SID existed
        // to grant rights to.
        Assert.Contains(scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.False(scm.Exists);

        Assert.Equal(ServiceEnableOperationOutcome.Compensated, transaction.LastOutcome);
        Assert.Equal(ServiceEnableFailureCause.ServiceSidResolutionRefused, transaction.FailureCause);
    }

    [Fact]
    public void A_final_program_files_protection_refusal_is_classified_as_its_own_cause()
    {
        CreateRuntimeHost();

        // THE REQUIRED SEAM. FailFinalApply fails ONLY
        // IServiceEnableSecurityAdapter.TryApplyFinalProtection, which is
        // reached only from ApplyFinalProtectionEverywhere. No unrelated earlier
        // operation is failed to simulate this branch.
        var security = new FakeSecurityAdapter { FailFinalApply = true };
        var scm = new FakeServiceControlManager();
        ServiceEnableTransaction transaction = CreateTransaction(security, scm);

        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);
        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        AssertBoundedClassification(
            transaction,
            ServiceEnableFailureCause.ProgramFilesProtectionRefused,
            ServiceEnableFailureDisposition.Compensated);

        // The branch really is the FINAL APPLY: the apply was attempted with the
        // resolved service SID, and the final VERIFY was never reached.
        Assert.NotEmpty(security.FinalApplied);
        Assert.Empty(security.FinalVerified);
        Assert.Equal(scm.ResolvedSid, security.ServiceSidSeen);

        // It is distinct from the staging profile and from the verification cause.
        Assert.NotEqual(ServiceEnableFailureCause.VerificationRefused, transaction.FailureCause);
        Assert.NotEqual(ServiceEnableFailureCause.ExtractionRefused, transaction.FailureCause);

        Assert.Contains(scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.False(scm.Exists);

        Assert.Equal(ServiceEnableOperationOutcome.Compensated, transaction.LastOutcome);
        Assert.Equal(ServiceEnableFailureCause.ProgramFilesProtectionRefused, transaction.FailureCause);
    }

    [Fact]
    public void The_five_post_create_branches_report_five_distinct_causes()
    {
        // The whole point of the cycle: the five branches that were ONE category
        // are now five, and no two of them can be confused on the wire.
        ServiceEnableFailureCause[] branchCauses =
        {
            ServiceEnableFailureCause.ServiceCreationRefused,
            ServiceEnableFailureCause.ExistingStateRefused,
            ServiceEnableFailureCause.ServiceSidConfigurationRefused,
            ServiceEnableFailureCause.ServiceSidResolutionRefused,
            ServiceEnableFailureCause.ProgramFilesProtectionRefused,
        };

        Assert.Equal(branchCauses.Length, branchCauses.Distinct().Count());
        Assert.Equal(
            branchCauses.Length,
            branchCauses.Select(ServiceEnableFailureContract.CauseToken)
                .Distinct(StringComparer.Ordinal).Count());

        // None of them silently reads back as the fail-closed default.
        foreach (ServiceEnableFailureCause cause in branchCauses)
        {
            Assert.NotEqual("unavailable", ServiceEnableFailureContract.CauseToken(cause));
            Assert.NotEqual("registration_refused", ServiceEnableFailureContract.CauseToken(cause));
        }

        // registration_refused REMAINS a member for compatibility, and is not
        // removed by this cycle.
        Assert.Equal(
            "registration_refused",
            ServiceEnableFailureContract.CauseToken(ServiceEnableFailureCause.RegistrationRefused));
    }

    [Fact]
    public void A_preflight_refusal_carries_its_own_finer_cause_and_a_pre_mutation_disposition()
    {
        // SIGNING POLICY - refused before anything is even looked at.
        ServiceEnableTransaction signing = CreateTransaction(
            new FakeSecurityAdapter(),
            new FakeServiceControlManager(),
            signing: ServiceHelperSigningPolicyState.SigningPolicyUnavailable);

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Refused, signing.Preflight());
        Assert.Equal(ServiceEnableFailureCause.SigningPolicyRefused, signing.FailureCause);
        Assert.Equal(ServiceEnableFailureDisposition.RefusedBeforeMutation, signing.FailureDisposition);

        // PAYLOAD - the embedded archive does not verify.
        ServiceEnableTransaction payload = CreateTransaction(
            new FakeSecurityAdapter(),
            new FakeServiceControlManager(),
            new FakePayloadSource(
                Array.Empty<byte>(),
                ServicePayloadVerificationResult.Refused(ServicePayloadVerificationOutcome.ResourceMissing)));

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Refused, payload.Preflight());
        Assert.Equal(ServiceEnableFailureCause.PayloadRefused, payload.FailureCause);
        Assert.Equal(ServiceEnableFailureDisposition.RefusedBeforeMutation, payload.FailureDisposition);

        // PROGRAM FILES - the fixed machine-wide runtime host is not a regular file.
        ServiceEnableTransaction programFiles =
            CreateTransaction(new FakeSecurityAdapter(), new FakeServiceControlManager());

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Refused, programFiles.Preflight());
        Assert.Equal(
            ServiceEnableFailureCause.ProgramFilesPreflightRefused, programFiles.FailureCause);
        Assert.Equal(
            ServiceEnableFailureDisposition.RefusedBeforeMutation, programFiles.FailureDisposition);
    }

    [Fact]
    public void An_unexplained_existing_footprint_is_an_existing_state_cause_requiring_recovery()
    {
        CreateRuntimeHost();
        ServiceEnableFixedPaths paths = ResolvePaths();
        Directory.CreateDirectory(paths.Staging);

        ServiceEnableTransaction transaction =
            CreateTransaction(new FakeSecurityAdapter(), new FakeServiceControlManager());

        Assert.Equal(
            ServiceIdentityBoundTransactionPreflightState.RecoveryRequired, transaction.Preflight());
        Assert.Equal(ServiceEnableFailureCause.ExistingStateRefused, transaction.FailureCause);
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, transaction.FailureDisposition);
    }

    [Fact]
    public void The_first_cause_is_sticky_and_a_success_can_never_carry_one()
    {
        // Every preflight reason maps to exactly one bounded cause, and none of
        // them maps to None - a refusal always names a category.
        foreach (ServiceEnablePreflightReason reason in Enum.GetValues<ServiceEnablePreflightReason>())
        {
            Assert.NotEqual(
                ServiceEnableFailureCause.None,
                ServiceEnableTransaction.CauseForPreflightReason(reason));
        }

        // A completed transaction carries NO cause and the Completed disposition.
        CreateRuntimeHost();
        ServiceEnableTransaction transaction =
            CreateTransaction(new FakeSecurityAdapter(), new FakeServiceControlManager());

        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);
        Assert.Equal(
            ServiceIdentityBoundTransactionState.Completed, transaction.Apply(context).State);
        Assert.Equal(ServiceEnableFailureCause.None, transaction.FailureCause);
        Assert.Equal(ServiceEnableFailureDisposition.Completed, transaction.FailureDisposition);
    }

    // =======================================================================
    // CYCLE 67 - MACHINE DATA DIRECTORY OWNERSHIP AND COMPENSATION CLOSURE
    // =======================================================================
    //
    // THE RESIDUE THIS SECTION EXISTS TO PREVENT. The first attended failure
    // left C:\ProgramData\PAXCookbook and C:\ProgramData\PAXCookbook\Service
    // behind - both EMPTY, both still carrying INHERITED access control -
    // because compensation stopped at the anchor. Emptiness is NEVER treated as
    // proof of ownership here: the ONLY authority to remove either directory is
    // the mutation-free preflight probe that saw it ABSENT.

    [Fact]
    public void Directories_this_attempt_created_are_removed_and_the_footprint_is_zero()
    {
        CreateRuntimeHost();

        // Both fixed machine data directories are ABSENT when preflight probes.
        Assert.False(Directory.Exists(MachineRootDirectory));
        Assert.False(Directory.Exists(_serviceDirectory));

        var scm = new FakeServiceControlManager { FailCreate = true };
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);
        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);

        // Persisting the anchor created BOTH, exactly as it does in production.
        Assert.True(Directory.Exists(MachineRootDirectory));
        Assert.True(Directory.Exists(_serviceDirectory));

        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        Assert.True(result.AnchorRemoved);
        Assert.Equal(ServiceEnableFailureDisposition.Compensated, transaction.FailureDisposition);

        // ZERO FIXED FOOTPRINT - every fixed location this attempt could have
        // touched is proven absent.
        ServiceEnableFixedPaths paths = ResolvePaths();
        Assert.False(File.Exists(AnchorPath));
        Assert.False(Directory.Exists(_serviceDirectory));
        Assert.False(Directory.Exists(MachineRootDirectory));
        Assert.False(Directory.Exists(paths.Final));
        Assert.False(Directory.Exists(paths.Staging));
        Assert.False(scm.Exists);
    }

    [Fact]
    public void A_pre_existing_empty_metadata_directory_is_preserved()
    {
        CreateRuntimeHost();

        // PRE-EXISTING, and empty. Emptiness must not make it removable.
        Directory.CreateDirectory(_serviceDirectory);

        var scm = new FakeServiceControlManager { FailCreate = true };
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);
        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);

        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        Assert.True(result.AnchorRemoved);
        Assert.False(transaction.MetadataDirectoryCreatedThisTransaction);
        Assert.False(transaction.MachineRootCreatedThisTransaction);

        Assert.False(File.Exists(AnchorPath));
        Assert.True(Directory.Exists(_serviceDirectory));
        Assert.True(Directory.Exists(MachineRootDirectory));
    }

    [Fact]
    public void A_pre_existing_empty_machine_root_is_preserved_while_its_new_child_is_removed()
    {
        CreateRuntimeHost();

        // The ROOT pre-exists and is empty; the metadata child does not exist.
        Directory.CreateDirectory(MachineRootDirectory);
        Assert.False(Directory.Exists(_serviceDirectory));

        var scm = new FakeServiceControlManager { FailCreate = true };
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);
        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);

        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.Compensated, result.State);
        Assert.True(transaction.MetadataDirectoryCreatedThisTransaction is false);
        Assert.False(Directory.Exists(_serviceDirectory));
        Assert.True(Directory.Exists(MachineRootDirectory));
    }

    [Fact]
    public void An_unknown_sibling_in_the_machine_root_blocks_its_removal_and_requires_recovery()
    {
        CreateRuntimeHost();

        var scm = new FakeServiceControlManager { FailCreate = true };
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);
        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);

        // An unrelated sibling appears beneath the machine root. The root is no
        // longer empty, so it may not be removed, and unrelated state is never
        // touched to make room.
        string stranger = Path.Combine(MachineRootDirectory, "SomethingElse");
        Directory.CreateDirectory(stranger);

        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);

        // CYCLE 71. The seam is still the CreateServiceW refusal; only its
        // classification became finer. The residue behaviour is unchanged.
        Assert.Equal(ServiceEnableFailureCause.ServiceCreationRefused, transaction.FailureCause);
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, transaction.FailureDisposition);

        Assert.True(Directory.Exists(MachineRootDirectory));
        Assert.True(Directory.Exists(stranger));
    }

    [Fact]
    public void An_unknown_sibling_beside_the_anchor_blocks_the_anchor_and_therefore_both_parents()
    {
        CreateRuntimeHost();

        var scm = new FakeServiceControlManager { FailCreate = true };
        ServiceEnableTransaction transaction = CreateTransaction(new FakeSecurityAdapter(), scm);
        ServiceIdentityBoundTransactionContext context = PreflightThenPersistAnchor(transaction);

        File.WriteAllText(Path.Combine(_serviceDirectory, "stray.txt"), "x");

        ServiceIdentityBoundTransactionResult result = transaction.Apply(context);

        // A FAILED anchor removal must block BOTH parent removals.
        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);
        Assert.False(result.AnchorRemoved);
        Assert.True(File.Exists(AnchorPath));
        Assert.True(Directory.Exists(_serviceDirectory));
        Assert.True(Directory.Exists(MachineRootDirectory));
    }

    [Fact]
    public void A_reparse_point_can_never_be_removed_by_compensation()
    {
        // A real junction cannot be created here without assuming a privilege
        // this test process must not assume, so the GUARD ITSELF is asserted
        // directly. It is the single predicate the removal path consults.
        Assert.True(ServiceEnableTransaction.DirectoryAttributesPermitRemoval(FileAttributes.Directory));
        Assert.False(ServiceEnableTransaction.DirectoryAttributesPermitRemoval(
            FileAttributes.Directory | FileAttributes.ReparsePoint));
        Assert.False(ServiceEnableTransaction.DirectoryAttributesPermitRemoval(
            FileAttributes.ReparsePoint));
        Assert.False(ServiceEnableTransaction.DirectoryAttributesPermitRemoval(
            FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Hidden));
    }

    [Fact]
    public void A_metadata_directory_outside_the_exact_fixed_shape_is_never_removed()
    {
        CreateRuntimeHost();

        // The same parent, but NOT the fixed "Service" leaf. The path check must
        // refuse it outright rather than deleting whatever is there.
        string wrongShape = Path.Combine(MachineRootDirectory, "NotService");

        var scm = new FakeServiceControlManager { FailCreate = true };
        var transaction = new ServiceEnableTransaction(
            wrongShape,
            new FakeProgramFilesResolver(_programFiles),
            new FakeSecurityAdapter(),
            scm,
            new FakePayloadSource(BuildCanonicalArchive()),
            new FakeSigningSource(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed));

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());

        var anchor = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            CurrentSid(),
            "2026-08-17T00:00:00Z");
        Assert.Equal(
            ServiceInstallationAnchorWriteState.Created,
            ServiceInstallationAnchorStore.WriteTo(wrongShape, anchor));

        ServiceIdentityBoundTransactionResult result = transaction.Apply(
            new ServiceIdentityBoundTransactionContext(wrongShape, anchor, true));

        Assert.Equal(ServiceIdentityBoundTransactionState.RecoveryRequired, result.State);
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, transaction.FailureDisposition);
        Assert.True(Directory.Exists(wrongShape));
    }

    [Fact]
    public void Ownership_is_probed_before_persistence_and_never_inferred_from_emptiness()
    {
        CreateRuntimeHost();
        ServiceEnableTransaction transaction =
            CreateTransaction(new FakeSecurityAdapter(), new FakeServiceControlManager());

        // BEFORE the probe there is no ownership claim at all.
        Assert.False(transaction.MachineDataOwnershipProbed);
        Assert.False(transaction.MetadataDirectoryCreatedThisTransaction);
        Assert.False(transaction.MachineRootCreatedThisTransaction);

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());

        Assert.True(transaction.MachineDataOwnershipProbed);
        Assert.True(transaction.MetadataDirectoryAbsentAtPreflight);
        Assert.True(transaction.MachineRootAbsentAtPreflight);

        // A REUSED anchor means this attempt created nothing, even though both
        // directories were absent when the probe ran in a previous attempt's
        // world. Ownership requires BOTH facts, never one.
        var anchor = new ServiceInstallationAnchorDocument(
            Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            CurrentSid(),
            "2026-08-17T00:00:00Z");
        WriteAnchor(anchor.InitiatingUserSid, anchor.InstallationId);

        transaction.Apply(new ServiceIdentityBoundTransactionContext(
            _serviceDirectory, anchor, anchorCreatedThisAttempt: false));

        Assert.False(transaction.MetadataDirectoryCreatedThisTransaction);
        Assert.False(transaction.MachineRootCreatedThisTransaction);
        Assert.True(Directory.Exists(_serviceDirectory));
    }

    // =======================================================================
    // CYCLE 68 - THE POST-ANCHOR-WRITE FAILURE RECOVERY
    // =======================================================================
    //
    // WHY THIS SECTION EXISTS. ServiceInstallationAnchorStore.WriteTo runs its
    // single Directory.CreateDirectory as the FIRST durable statement of the
    // write. When persistence then fails, the channel returns on the wire
    // BEFORE Apply - so Compensate, and therefore the cycle-67 compensation
    // steps 7-9, can NEVER run for that path. Cycle 67 disclosed exactly that
    // as DEFERRED (knownRisk B2). This section proves the narrow, fail-closed
    // close of it.
    //
    // WHAT IT IS NOT, and what these tests pin. It is not a recovery, reset or
    // purge verb. It takes NO path and NO deletion target. It deletes nothing
    // recursively. It removes at most the two FIXED machine-data directories,
    // and only ones the mutation-free preflight PROVED absent before anything
    // could create them. NEITHER of its two result values ever means
    // enablement succeeded.

    /// <summary>
    /// A transaction whose MUTATION-FREE preflight has completed with both fixed
    /// machine-data directories proven ABSENT - the exact state the channel is
    /// in at the moment it calls WriteTo.
    /// </summary>
    private ServiceEnableTransaction PreflightWithMachineDataAbsent(
        FakeServiceControlManager? scm = null)
    {
        CreateRuntimeHost();
        ServiceEnableTransaction transaction =
            CreateTransaction(new FakeSecurityAdapter(), scm ?? new FakeServiceControlManager());

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());
        Assert.True(transaction.MachineDataOwnershipProbed);
        Assert.True(transaction.MetadataDirectoryAbsentAtPreflight);
        Assert.True(transaction.MachineRootAbsentAtPreflight);
        return transaction;
    }

    /// <summary>
    /// The EXACT residue shape the first attended enable attempt left behind:
    /// both fixed machine-data directories present and EMPTY, created inside the
    /// failed anchor-write window, with nothing else anywhere.
    /// </summary>
    private void CreateObservedEmptyResidue()
    {
        Directory.CreateDirectory(_serviceDirectory);
        Assert.Empty(Directory.GetFileSystemEntries(_serviceDirectory));
        Assert.Single(Directory.GetFileSystemEntries(MachineRootDirectory));
    }

    /// <summary>
    /// An all-true residue snapshot, or the same snapshot with exactly ONE fact
    /// forced false. It is what makes the authorization predicate provably
    /// non-vacuous: every single clause is shown to be load bearing.
    /// </summary>
    private static ServiceEnableAnchorResidueFacts ResidueFacts(int falseIndex = -1)
    {
        bool F(int index) => index != falseIndex;

        return new ServiceEnableAnchorResidueFacts(
            machineDataProbeCompleted: F(0),
            metadataDirectoryAbsentAtPreflight: F(1),
            fixedServiceAbsent: F(2),
            finalPathAbsent: F(3),
            stagingPathAbsent: F(4),
            anchorPathAbsent: F(5),
            anchorTemporaryPathAbsent: F(6),
            ownershipLedgerPathAbsent: F(7),
            runtimePathAbsent: F(8),
            metadataDirectoryIsRealDirectory: F(9),
            metadataDirectoryIsNotReparsePoint: F(10),
            metadataDirectoryIsEmpty: F(11),
            metadataDirectoryHasExactFixedParent: F(12),
            machineRootHoldsOnlyMetadataDirectory: F(13),
            machineRootAbsentAtPreflight: F(14),
            machineRootIsRealDirectory: F(15),
            machineRootIsNotReparsePoint: F(16),
            machineRootIsExpectedShape: F(17));
    }

    private const int ResidueFactCount = 18;

    private const int MetadataGateFactCount = 14;

    // ---- the closed result type --------------------------------------------

    [Fact]
    public void The_recovery_result_is_closed_and_its_default_is_never_a_clean_compensation()
    {
        // EXACTLY TWO values, and NEITHER of them means enablement succeeded.
        Assert.Equal(2, Enum.GetValues<ServiceAnchorPersistenceRecoveryState>().Length);
        Assert.Contains(
            ServiceAnchorPersistenceRecoveryState.Compensated,
            Enum.GetValues<ServiceAnchorPersistenceRecoveryState>());
        Assert.Contains(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            Enum.GetValues<ServiceAnchorPersistenceRecoveryState>());

        // ZERO IS THE FAIL-CLOSED VALUE. An uninitialised or default-constructed
        // result can never read as "the machine was cleaned up".
        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            default(ServiceAnchorPersistenceRecoveryState));
        Assert.Equal(0, (int)ServiceAnchorPersistenceRecoveryState.RecoveryRequired);
    }

    // ---- the authorization predicate is total and every clause is load bearing

    [Fact]
    public void Every_single_residue_fact_is_load_bearing_for_the_metadata_removal()
    {
        Assert.True(ServiceEnableTransaction.AnchorResidueMetadataRemovalAuthorized(ResidueFacts()));

        for (int i = 0; i < MetadataGateFactCount; i++)
        {
            Assert.False(
                ServiceEnableTransaction.AnchorResidueMetadataRemovalAuthorized(ResidueFacts(i)));
            Assert.False(
                ServiceEnableTransaction.AnchorResidueMachineRootRemovalAuthorized(ResidueFacts(i)));
        }

        // The four ROOT-ONLY facts block the root and leave the metadata gate
        // open, so a root that may not be removed never blocks removing the
        // child this attempt did create.
        for (int i = MetadataGateFactCount; i < ResidueFactCount; i++)
        {
            Assert.True(
                ServiceEnableTransaction.AnchorResidueMetadataRemovalAuthorized(ResidueFacts(i)));
            Assert.False(
                ServiceEnableTransaction.AnchorResidueMachineRootRemovalAuthorized(ResidueFacts(i)));
        }
    }

    [Fact]
    public void A_default_residue_snapshot_authorizes_absolutely_nothing()
    {
        var empty = default(ServiceEnableAnchorResidueFacts);

        Assert.False(ServiceEnableTransaction.AnchorResidueMetadataRemovalAuthorized(empty));
        Assert.False(ServiceEnableTransaction.AnchorResidueMachineRootRemovalAuthorized(empty));
        Assert.Equal(nameof(ServiceEnableAnchorResidueFacts), empty.ToString());
    }

    // ---- the exact observed residue shape IS removable -----------------------

    [Fact]
    public void The_exact_observed_empty_directory_residue_is_removed_and_both_paths_end_absent()
    {
        ServiceEnableTransaction transaction = PreflightWithMachineDataAbsent();
        CreateObservedEmptyResidue();

        ServiceAnchorPersistenceRecoveryState recovered =
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate);

        Assert.Equal(ServiceAnchorPersistenceRecoveryState.Compensated, recovered);
        Assert.False(Directory.Exists(_serviceDirectory));
        Assert.False(File.Exists(_serviceDirectory));
        Assert.False(Directory.Exists(MachineRootDirectory));
        Assert.False(File.Exists(MachineRootDirectory));

        // COMPENSATED IS NEVER SUCCESS. The attempt still failed, and the
        // bounded classification says so.
        Assert.Equal(ServiceEnableFailureDisposition.Compensated, transaction.FailureDisposition);
        Assert.NotEqual(ServiceEnableFailureDisposition.Completed, transaction.FailureDisposition);
        Assert.Equal(ServiceEnableFailureCause.Unavailable, transaction.FailureCause);
        Assert.NotEqual(ServiceEnableFailureCause.None, transaction.FailureCause);
        Assert.Equal(ServiceEnableOperationOutcome.Compensated, transaction.LastOutcome);
    }

    [Fact]
    public void A_pre_existing_machine_root_is_preserved_while_the_created_child_is_removed()
    {
        // The root exists BEFORE the probe runs, so this attempt provably did
        // not create it. A pre-existing directory is ALWAYS preserved, and that
        // is still a clean compensation.
        Directory.CreateDirectory(MachineRootDirectory);

        CreateRuntimeHost();
        ServiceEnableTransaction transaction =
            CreateTransaction(new FakeSecurityAdapter(), new FakeServiceControlManager());
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());
        Assert.True(transaction.MetadataDirectoryAbsentAtPreflight);
        Assert.False(transaction.MachineRootAbsentAtPreflight);

        Directory.CreateDirectory(_serviceDirectory);

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.Compensated,
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));

        Assert.False(Directory.Exists(_serviceDirectory));
        Assert.True(Directory.Exists(MachineRootDirectory));
    }

    [Fact]
    public void A_pre_existing_metadata_directory_is_always_preserved_and_forces_recovery()
    {
        Directory.CreateDirectory(_serviceDirectory);

        CreateRuntimeHost();
        ServiceEnableTransaction transaction =
            CreateTransaction(new FakeSecurityAdapter(), new FakeServiceControlManager());
        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());
        Assert.False(transaction.MetadataDirectoryAbsentAtPreflight);

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));

        Assert.True(Directory.Exists(_serviceDirectory));
        Assert.True(Directory.Exists(MachineRootDirectory));
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, transaction.FailureDisposition);
    }

    // ---- everything that must BLOCK the removal ------------------------------

    [Fact]
    public void An_anchor_in_the_metadata_directory_blocks_the_removal()
    {
        ServiceEnableTransaction transaction = PreflightWithMachineDataAbsent();
        WriteAnchor(CurrentSid(), Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture));

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));

        Assert.True(File.Exists(AnchorPath));
        Assert.True(Directory.Exists(_serviceDirectory));
        Assert.True(Directory.Exists(MachineRootDirectory));
    }

    [Fact]
    public void The_fixed_anchor_temporary_leaf_blocks_the_removal()
    {
        ServiceEnableTransaction transaction = PreflightWithMachineDataAbsent();
        Directory.CreateDirectory(_serviceDirectory);
        File.WriteAllBytes(
            Path.Combine(_serviceDirectory, ServiceInstallationAnchorStore.TemporaryLeafFileName),
            new byte[] { 0x7B, 0x7D });

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));

        Assert.True(Directory.Exists(_serviceDirectory));
    }

    [Fact]
    public void An_ownership_ledger_path_blocks_the_removal()
    {
        ServiceEnableTransaction transaction = PreflightWithMachineDataAbsent();
        Directory.CreateDirectory(_serviceDirectory);
        File.WriteAllBytes(
            Path.Combine(_serviceDirectory, ServiceMachineStorageContract.OwnershipLedgerFileName),
            new byte[] { 0x7B, 0x7D });

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));

        Assert.True(Directory.Exists(_serviceDirectory));
    }

    [Fact]
    public void A_runtime_path_blocks_the_removal()
    {
        ServiceEnableTransaction transaction = PreflightWithMachineDataAbsent();
        Directory.CreateDirectory(transaction.RuntimeDirectory);

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));

        Assert.True(Directory.Exists(transaction.RuntimeDirectory));
        Assert.True(Directory.Exists(_serviceDirectory));
    }

    [Fact]
    public void An_unknown_sibling_of_the_metadata_directory_blocks_the_removal()
    {
        ServiceEnableTransaction transaction = PreflightWithMachineDataAbsent();
        Directory.CreateDirectory(_serviceDirectory);
        Directory.CreateDirectory(Path.Combine(MachineRootDirectory, "SomethingElse"));

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));

        // NOTHING is removed - not the unknown sibling, and not the empty
        // metadata directory whose parent it makes unexplainable.
        Assert.True(Directory.Exists(Path.Combine(MachineRootDirectory, "SomethingElse")));
        Assert.True(Directory.Exists(_serviceDirectory));
    }

    [Fact]
    public void A_registered_service_blocks_the_removal()
    {
        var scm = new FakeServiceControlManager();
        ServiceEnableTransaction transaction = PreflightWithMachineDataAbsent(scm);
        CreateObservedEmptyResidue();

        ServiceEnableFixedPaths paths = ResolvePaths();
        scm.Seed(
            ServiceConfigurationSnapshot.Expected(paths.RuntimeHost, paths.Final),
            ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted);

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));

        Assert.True(Directory.Exists(_serviceDirectory));
        Assert.True(Directory.Exists(MachineRootDirectory));
    }

    [Fact]
    public void A_program_files_final_or_staging_footprint_blocks_the_removal()
    {
        ServiceEnableFixedPaths paths = ResolvePaths();

        ServiceEnableTransaction onFinal = PreflightWithMachineDataAbsent();
        CreateObservedEmptyResidue();
        Directory.CreateDirectory(paths.Final);

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            onFinal.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));
        Assert.True(Directory.Exists(_serviceDirectory));

        Directory.Delete(paths.Final);
        Directory.Delete(_serviceDirectory);
        Directory.Delete(MachineRootDirectory);

        ServiceEnableTransaction onStaging = PreflightWithMachineDataAbsent();
        CreateObservedEmptyResidue();
        Directory.CreateDirectory(paths.Staging);

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            onStaging.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));
        Assert.True(Directory.Exists(_serviceDirectory));
    }

    [Fact]
    public void A_reparse_point_is_never_removed_by_the_anchor_persistence_recovery()
    {
        // A real junction cannot be created without assuming a privilege this
        // test process must not assume, so the PREDICATE the removal path
        // consults is asserted directly, on BOTH gates.
        Assert.False(ServiceEnableTransaction.AnchorResidueMetadataRemovalAuthorized(ResidueFacts(10)));
        Assert.False(ServiceEnableTransaction.AnchorResidueMachineRootRemovalAuthorized(ResidueFacts(16)));

        Assert.True(ServiceEnableTransaction.DirectoryAttributesPermitRemoval(FileAttributes.Directory));
        Assert.False(ServiceEnableTransaction.DirectoryAttributesPermitRemoval(
            FileAttributes.Directory | FileAttributes.ReparsePoint));
    }

    [Fact]
    public void A_metadata_directory_with_the_wrong_parent_shape_is_never_removed()
    {
        CreateRuntimeHost();

        // The same parent, but NOT the fixed "Service" leaf. Nothing at that
        // path may be deleted, however empty it looks.
        string wrongShape = Path.Combine(MachineRootDirectory, "NotService");

        var transaction = new ServiceEnableTransaction(
            wrongShape,
            new FakeProgramFilesResolver(_programFiles),
            new FakeSecurityAdapter(),
            new FakeServiceControlManager(),
            new FakePayloadSource(BuildCanonicalArchive()),
            new FakeSigningSource(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed));

        Assert.Equal(ServiceIdentityBoundTransactionPreflightState.Proceed, transaction.Preflight());
        Directory.CreateDirectory(wrongShape);

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));

        Assert.True(Directory.Exists(wrongShape));
    }

    [Fact]
    public void A_recovery_without_a_completed_machine_data_probe_removes_nothing()
    {
        CreateRuntimeHost();

        // NO Preflight call at all, so the mutation-free probe never ran and no
        // absence was ever captured.
        ServiceEnableTransaction transaction =
            CreateTransaction(new FakeSecurityAdapter(), new FakeServiceControlManager());
        Assert.False(transaction.MachineDataOwnershipProbed);

        CreateObservedEmptyResidue();

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));

        Assert.True(Directory.Exists(_serviceDirectory));
        Assert.True(Directory.Exists(MachineRootDirectory));
    }

    [Fact]
    public void A_deletion_failure_becomes_recovery_required_and_never_a_clean_compensation()
    {
        ServiceEnableTransaction transaction = PreflightWithMachineDataAbsent();
        CreateObservedEmptyResidue();

        // A READ-ONLY directory passes every observation - it is a real,
        // non-reparse, empty, exactly-shaped directory - and then fails at the
        // delete itself. That is the failure mode a predicate cannot foresee.
        var info = new DirectoryInfo(_serviceDirectory);
        info.Attributes |= FileAttributes.ReadOnly;
        try
        {
            Assert.Equal(
                ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
                transaction.RecoverAfterAnchorPersistenceFailure(
                    ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));

            Assert.True(Directory.Exists(_serviceDirectory));
            Assert.True(Directory.Exists(MachineRootDirectory));
            Assert.Equal(
                ServiceEnableFailureDisposition.RecoveryRequired, transaction.FailureDisposition);
        }
        finally
        {
            info.Refresh();
            if (Directory.Exists(_serviceDirectory))
            {
                info.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }

    // ---- fail closed on every state that is not a defined post-create failure

    [Fact]
    public void An_undefined_write_state_fails_closed_and_removes_nothing()
    {
        ServiceEnableTransaction transaction = PreflightWithMachineDataAbsent();
        CreateObservedEmptyResidue();

        // MayHaveCreatedDirectories reports TRUE for an undefined cast - it
        // never reads an unknown vocabulary as proof that nothing was mutated -
        // and the recovery still refuses to act on a state it cannot name.
        Assert.True(ServiceInstallationAnchorStore.MayHaveCreatedDirectories(
            (ServiceInstallationAnchorWriteState)9999));

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            transaction.RecoverAfterAnchorPersistenceFailure(
                (ServiceInstallationAnchorWriteState)9999));

        Assert.True(Directory.Exists(_serviceDirectory));
        Assert.True(Directory.Exists(MachineRootDirectory));
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, transaction.FailureDisposition);
    }

    [Fact]
    public void Neither_a_pre_create_refusal_nor_a_success_state_may_drive_the_recovery()
    {
        foreach (ServiceInstallationAnchorWriteState state
                 in Enum.GetValues<ServiceInstallationAnchorWriteState>())
        {
            bool postCreateFailure =
                ServiceInstallationAnchorStore.MayHaveCreatedDirectories(state)
                && state is not ServiceInstallationAnchorWriteState.Created
                && state is not ServiceInstallationAnchorWriteState.AlreadyMatching;

            if (postCreateFailure)
            {
                continue;
            }

            ServiceEnableTransaction transaction = PreflightWithMachineDataAbsent();
            CreateObservedEmptyResidue();

            Assert.Equal(
                ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
                transaction.RecoverAfterAnchorPersistenceFailure(state));

            // NOTHING is deleted for a state that is not a post-create failure.
            Assert.True(Directory.Exists(_serviceDirectory));
            Assert.True(Directory.Exists(MachineRootDirectory));

            Directory.Delete(_serviceDirectory);
            Directory.Delete(MachineRootDirectory);
        }
    }

    [Fact]
    public void A_verification_failure_leaves_the_persisted_anchor_and_requires_recovery()
    {
        // VerificationFailed is a POST-CREATE state whose bytes were replaced,
        // so an anchor is present and the removal must refuse it. The recovery
        // must never delete the very evidence an attended recovery needs.
        ServiceEnableTransaction transaction = PreflightWithMachineDataAbsent();
        WriteAnchor(CurrentSid(), Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture));
        byte[] before = File.ReadAllBytes(AnchorPath);

        Assert.True(ServiceInstallationAnchorStore.MayHaveCreatedDirectories(
            ServiceInstallationAnchorWriteState.VerificationFailed));
        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.RecoveryRequired,
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.VerificationFailed));

        Assert.Equal(before, File.ReadAllBytes(AnchorPath));
    }

    // ---- the recovery never touches Program Files or the SCM -----------------

    [Fact]
    public void The_recovery_performs_no_service_control_and_no_program_files_mutation()
    {
        var scm = new FakeServiceControlManager();
        ServiceEnableTransaction transaction = PreflightWithMachineDataAbsent(scm);
        scm.Calls.Clear();
        CreateObservedEmptyResidue();

        ServiceEnableFixedPaths paths = ResolvePaths();

        Assert.Equal(
            ServiceAnchorPersistenceRecoveryState.Compensated,
            transaction.RecoverAfterAnchorPersistenceFailure(
                ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate));

        // The ONLY service-control call it may make is the read-only query that
        // proves the fixed service is absent.
        Assert.All(scm.Calls, call => Assert.StartsWith("Query:", call, StringComparison.Ordinal));
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("Create:", StringComparison.Ordinal));
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("Start:", StringComparison.Ordinal));
        Assert.DoesNotContain(scm.Calls, c => c.StartsWith("Stop:", StringComparison.Ordinal));

        Assert.False(Directory.Exists(paths.Root));
        Assert.True(Directory.Exists(_programFiles));
    }
}
