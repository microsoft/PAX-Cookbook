using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using PAXCookbook.ServiceAdminHelper.Signing;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 62 - NON-ELEVATED SERVICE-ENABLE COORDINATOR (Setup side)
// ===========================================================================
//
// SCOPE, stated plainly. Every test here uses FAKES. Nothing in this file
// elevates, triggers a UAC prompt, uses runas, starts ANY process, resolves or
// launches the real service administrative helper, extracts anything into
// Program Files, writes %ProgramData%, queries or mutates the Service Control
// Manager, starts stops or deletes a service, opens a certificate store,
// private key or credential vault, reads or writes an ACL or the registry,
// opens a socket, runs PAX or starts a Bake. The coordinator's REAL launcher,
// REAL helper locator and REAL signing gate are never constructed; all three
// are injected.
//
// WHAT THIS CLASS OWNS. The SETUP SIDE of the assembly boundary: the initiator
// grammar, fixed sibling helper resolution, the signing-policy gate, the exact
// closed launch arguments, UAC decline, the "no client before a successful
// launch" rule, the bounded wait and exit mapping, and the proof that ordinary
// Setup verbs never reach any of it. The elevated transaction lives in the
// helper assembly and is proven by ServiceEnableTransactionTests.
//
// DISCLOSED LIMITATION, recorded once. These tests run at a single integrity
// level and under a single account, so they cannot prove what Windows does when
// a DIFFERENT administrator approves the prompt. What they prove is the part
// that is account-independent: exactly what the coordinator launches, exactly
// what crosses the command line, and exactly when it refuses.
public sealed class ServiceEnableCoordinatorTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);

    private static readonly ServiceInitiatorProcessFacts SampleInitiator = new(4321, 133_000_000_000_000_000L);

    private const string SampleHelperPath =
        @"C:\Users\someone\AppData\Local\PAXCookbook\Setup\PAXCookbookServiceAdminHelper.exe";

    // ---- injected collaborators --------------------------------------------

    private sealed class FakeInitiatorFacts : IServiceEnableInitiatorFacts
    {
        private readonly ServiceInitiatorProcessFacts _facts;

        internal FakeInitiatorFacts(ServiceInitiatorProcessFacts facts) => _facts = facts;

        internal int Captures;

        public ServiceInitiatorProcessFacts CaptureInitiator()
        {
            Interlocked.Increment(ref Captures);
            return _facts;
        }
    }

    private sealed class FakeHelperLocator : IServiceEnableHelperLocator
    {
        private readonly ServiceAdminHelperLocationResult _result;

        internal FakeHelperLocator(ServiceAdminHelperLocationResult result) => _result = result;

        internal int Resolutions;

        public ServiceAdminHelperLocationResult ResolveSiblingHelper()
        {
            Interlocked.Increment(ref Resolutions);
            return _result;
        }
    }

    private sealed class FakeSigningGate : IServiceEnableSigningGate
    {
        private readonly ServiceHelperSigningPolicyState _state;

        internal FakeSigningGate(ServiceHelperSigningPolicyState state) => _state = state;

        internal int Queries;

        public ServiceHelperSigningPolicyState ResolveConfiguredPolicy()
        {
            Interlocked.Increment(ref Queries);
            return _state;
        }
    }

    private sealed class FakeElevatedProcess : IServiceAnchorElevatedProcess
    {
        private readonly int _exitCode;
        private bool _exited;

        internal FakeElevatedProcess(int exitCode, bool alreadyExited)
        {
            _exitCode = exitCode;
            _exited = alreadyExited;
        }

        internal bool Disposed { get; private set; }

        public bool HasExited => _exited;

        public bool WaitForExit(TimeSpan timeout)
        {
            _exited = true;
            return true;
        }

        public int ExitCode => _exitCode;

        public void Dispose() => Disposed = true;
    }

    private sealed class NeverExitingProcess : IServiceAnchorElevatedProcess
    {
        public bool HasExited => false;

        public bool WaitForExit(TimeSpan timeout) => false;

        public int ExitCode => int.MinValue;

        public void Dispose()
        {
        }
    }

    private sealed class FakeLauncher : IServiceAnchorElevatedLauncher
    {
        private readonly Func<ServiceAnchorElevatedLaunch> _result;

        internal FakeLauncher(Func<ServiceAnchorElevatedLaunch> result) => _result = result;

        internal List<ServiceAnchorElevationLaunchSpec> Launched { get; } = new();

        public ServiceAnchorElevatedLaunch LaunchElevated(ServiceAnchorElevationLaunchSpec spec)
        {
            Launched.Add(spec);
            return _result();
        }
    }

    private sealed class FakeChannelClient : IServiceAnchorChannelClient
    {
        private readonly ServiceInitiatingUserIdentityChannelOutcome _outcome;

        internal FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome outcome) => _outcome = outcome;

        internal int Requests;

        internal string? LastEndpointName;

        public ServiceInitiatingUserIdentityChannelOutcome Request(string endpointName, TimeSpan timeout)
        {
            Interlocked.Increment(ref Requests);
            LastEndpointName = endpointName;
            return _outcome;
        }
    }

    private static FakeLauncher StartedLauncher(int exitCode = SetupExitCodes.Ok) =>
        new(() => ServiceAnchorElevatedLaunch.Started(new FakeElevatedProcess(exitCode, alreadyExited: false)));

    private static ServiceEnableCoordinatorOutcome Run(
        string[] argv,
        IServiceEnableHelperLocator? locator = null,
        IServiceEnableSigningGate? signing = null,
        IServiceAnchorElevatedLauncher? launcher = null,
        IServiceAnchorChannelClient? client = null,
        ServiceInitiatorProcessFacts? initiator = null) =>
        ServiceEnableCoordinator.Run(
            argv,
            new FakeInitiatorFacts(initiator ?? SampleInitiator),
            locator ?? new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
            signing ?? new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
            launcher ?? StartedLauncher(),
            client ?? new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed),
            Bounded,
            Bounded);

    // =======================================================================
    // THE CLOSED INITIATOR GRAMMAR
    // =======================================================================

    [Fact]
    public void The_initiator_verb_takes_exactly_itself_and_nothing_else()
    {
        Assert.True(ServiceEnableProtocol.TryParseInitiator(new[] { ServiceEnableVerbs.InitiatorVerb }));
        Assert.Equal(1, ServiceEnableVerbs.InitiatorTokenCount);
    }

    [Fact]
    public void A_non_conforming_initiator_argument_vector_is_refused()
    {
        var rejected = new[]
        {
            Array.Empty<string>(),                                        // missing
            new[] { "service-enable", "service-enable" },                  // duplicated
            new[] { "service-enable", "--endpoint" },                      // extra
            new[] { "--endpoint", "service-enable" },                      // reordered
            new[] { "service-enabled" },                                  // prohibited near-miss
            new[] { "service-enable-elevated" },                          // the ELEVATED verb is not the initiator
            new[] { string.Empty },
            new[] { " service-enable" },
            new[] { "service-enable " },
        };

        foreach (string[] argv in rejected)
        {
            Assert.False(ServiceEnableProtocol.TryParseInitiator(argv));
            Assert.Equal(ServiceEnableCoordinatorOutcome.UsageRefused, Run(argv));
        }
    }

    [Fact]
    public void An_oversized_initiator_token_is_refused_before_any_work()
    {
        string oversized = new('a', ServiceEnableVerbs.MaxTokenLength + 1);
        Assert.False(ServiceEnableProtocol.TryParseInitiator(new[] { oversized }));

        var locator = new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath));
        var signing = new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed);
        var launcher = StartedLauncher();
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        Assert.Equal(
            ServiceEnableCoordinatorOutcome.UsageRefused,
            Run(new[] { oversized }, locator, signing, launcher, client));

        // Nothing was resolved, nothing was asked about signing, nothing launched.
        Assert.Equal(0, locator.Resolutions);
        Assert.Equal(0, signing.Queries);
        Assert.Empty(launcher.Launched);
        Assert.Equal(0, client.Requests);
    }

    [Fact]
    public void A_null_initiator_argument_vector_is_refused()
    {
        Assert.False(ServiceEnableProtocol.TryParseInitiator(null));
    }

    // =======================================================================
    // THE SIGNING GATE RUNS BEFORE ANYTHING IS RESOLVED OR LAUNCHED
    // =======================================================================

    [Fact]
    public void Only_the_explicit_prerelease_allowance_permits_enablement()
    {
        foreach (ServiceHelperSigningPolicyState state in Enum.GetValues<ServiceHelperSigningPolicyState>())
        {
            if (state == ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed)
            {
                continue;
            }

            Assert.False(ServiceEnableCoordinator.PolicyPermitsEnablement(state));

            var locator = new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath));
            var launcher = StartedLauncher();
            var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

            Assert.Equal(
                ServiceEnableCoordinatorOutcome.SigningPolicyRefused,
                Run(
                    new[] { ServiceEnableVerbs.InitiatorVerb },
                    locator,
                    new FakeSigningGate(state),
                    launcher,
                    client));

            // A build that may not enable a service NEVER reaches a UAC prompt
            // and never even resolves the helper it would have launched.
            Assert.Equal(0, locator.Resolutions);
            Assert.Empty(launcher.Launched);
            Assert.Equal(0, client.Requests);
        }
    }

    [Fact]
    public void The_prerelease_allowance_is_the_only_permitting_state()
    {
        Assert.True(ServiceEnableCoordinator.PolicyPermitsEnablement(
            ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed));

        int permitting = Enum.GetValues<ServiceHelperSigningPolicyState>()
            .Count(ServiceEnableCoordinator.PolicyPermitsEnablement);
        Assert.Equal(1, permitting);
    }

    // =======================================================================
    // FIXED SIBLING HELPER RESOLUTION
    // =======================================================================

    [Fact]
    public void An_unresolvable_sibling_helper_refuses_before_any_launch()
    {
        foreach (ServiceAdminHelperLocationOutcome outcome
                 in Enum.GetValues<ServiceAdminHelperLocationOutcome>())
        {
            if (outcome == ServiceAdminHelperLocationOutcome.Resolved)
            {
                continue;
            }

            var launcher = StartedLauncher();
            var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

            Assert.Equal(
                ServiceEnableCoordinatorOutcome.HelperUnavailable,
                Run(
                    new[] { ServiceEnableVerbs.InitiatorVerb },
                    new FakeHelperLocator(ServiceAdminHelperLocationResult.Refused(outcome)),
                    signing: null,
                    launcher,
                    client));

            Assert.Empty(launcher.Launched);
            Assert.Equal(0, client.Requests);
        }
    }

    [Fact]
    public void The_launched_file_is_exactly_the_resolved_sibling_helper_and_never_setup()
    {
        var launcher = StartedLauncher();
        Assert.Equal(
            ServiceEnableCoordinatorOutcome.Completed,
            Run(new[] { ServiceEnableVerbs.InitiatorVerb }, launcher: launcher));

        ServiceAnchorElevationLaunchSpec spec = Assert.Single(launcher.Launched);
        Assert.Equal(SampleHelperPath, spec.FileName);

        // It is the FIXED helper leaf, launched DIRECTLY. There is no dotnet
        // host image and no managed assembly path argument anywhere.
        Assert.Equal(
            ServiceAdminHelperLocationContract.HelperFileName,
            System.IO.Path.GetFileName(spec.FileName));
        Assert.DoesNotContain(
            spec.Arguments,
            a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            spec.Arguments,
            a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    // =======================================================================
    // THE CLOSED ELEVATED ARGUMENT SET
    // =======================================================================

    [Fact]
    public void The_coordinator_launches_the_fixed_helper_verb_with_the_closed_argument_set()
    {
        var launcher = StartedLauncher();
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        Assert.Equal(
            ServiceEnableCoordinatorOutcome.Completed,
            Run(new[] { ServiceEnableVerbs.InitiatorVerb }, launcher: launcher, client: client));

        ServiceAnchorElevationLaunchSpec spec = Assert.Single(launcher.Launched);
        Assert.Equal(ServiceEnableVerbs.ElevatedTokenCount, spec.Arguments.Count);
        Assert.Equal(ServiceEnableVerbs.ElevatedHelperVerb, spec.Arguments[0]);
        Assert.Equal(ServiceEnableVerbs.EndpointOption, spec.Arguments[1]);
        Assert.Equal(ServiceEnableVerbs.InitiatorProcessIdOption, spec.Arguments[3]);
        Assert.Equal(ServiceEnableVerbs.InitiatorCreatedOption, spec.Arguments[5]);

        Assert.Equal(SampleInitiator.ProcessId.ToString(CultureInfo.InvariantCulture), spec.Arguments[4]);
        Assert.Equal(
            SampleInitiator.CreationFileTime.ToString(CultureInfo.InvariantCulture), spec.Arguments[6]);

        // The endpoint the client used is the endpoint that was handed over.
        Assert.Equal(spec.Arguments[2], client.LastEndpointName);
        Assert.True(ServiceInitiatingUserIdentityChannelContract.IsCanonicalEndpointName(spec.Arguments[2]));

        // The arguments the coordinator composes are accepted by the helper's
        // own closed parser, which is the only thing that will ever read them.
        Assert.True(ServiceEnableProtocol.TryParseElevated(
            spec.Arguments.ToArray(), out ServiceEnableElevatedArguments? parsed));
        Assert.NotNull(parsed);
        Assert.Equal(spec.Arguments[2], parsed!.EndpointName);
        Assert.Equal(SampleInitiator.ProcessId, parsed.Initiator.ProcessId);
        Assert.Equal(SampleInitiator.CreationFileTime, parsed.Initiator.CreationFileTime);
    }

    [Fact]
    public void No_path_service_name_account_command_or_secret_ever_crosses_the_command_line()
    {
        var launcher = StartedLauncher();
        Assert.Equal(
            ServiceEnableCoordinatorOutcome.Completed,
            Run(new[] { ServiceEnableVerbs.InitiatorVerb }, launcher: launcher));

        ServiceAnchorElevationLaunchSpec spec = Assert.Single(launcher.Launched);

        foreach (string argument in spec.Arguments)
        {
            Assert.DoesNotContain('\\', argument);
            Assert.DoesNotContain('/', argument);
            Assert.DoesNotContain(':', argument);
            Assert.DoesNotContain('"', argument);
            Assert.DoesNotContain(' ', argument);
            Assert.DoesNotContain("S-1-", argument, StringComparison.Ordinal);
            Assert.DoesNotContain("%", argument, StringComparison.Ordinal);
        }

        // No fixed machine identity, location or artifact name appears.
        string joined = string.Join('\u0000', spec.Arguments);
        foreach (string forbidden in new[]
        {
            "PAXCookbookService", "PAXCookbook Machine Service", "NT SERVICE",
            "Program Files", "ProgramData", "dotnet", "PAXCookbook.Service.dll",
            "installation-anchor.json", "service-payload-manifest.json",
            "HKLM", "HKEY", "Software", "cmd", "powershell",
        })
        {
            Assert.DoesNotContain(forbidden, joined, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Each_launch_uses_a_fresh_unpredictable_endpoint_name()
    {
        var first = StartedLauncher();
        var second = StartedLauncher();

        Run(new[] { ServiceEnableVerbs.InitiatorVerb }, launcher: first);
        Run(new[] { ServiceEnableVerbs.InitiatorVerb }, launcher: second);

        Assert.NotEqual(
            Assert.Single(first.Launched).Arguments[2],
            Assert.Single(second.Launched).Arguments[2]);
    }

    [Fact]
    public void A_launch_spec_is_refused_when_the_endpoint_or_initiator_is_not_canonical()
    {
        Assert.Null(ServiceEnableCoordinator.TryComposeLaunchSpec(
            SampleHelperPath, "not-a-canonical-endpoint", SampleInitiator));
        Assert.Null(ServiceEnableCoordinator.TryComposeLaunchSpec(
            SampleHelperPath, ServiceInitiatingUserIdentityChannelContract.NewEndpointName(), default));
        Assert.Null(ServiceEnableCoordinator.TryComposeLaunchSpec(
            string.Empty, ServiceInitiatingUserIdentityChannelContract.NewEndpointName(), SampleInitiator));
    }

    // =======================================================================
    // UAC DECLINE, AND NO CLIENT BEFORE A SUCCESSFUL LAUNCH
    // =======================================================================

    [Fact]
    public void A_declined_approval_returns_immediately_and_no_client_transaction_runs()
    {
        var launcher = new FakeLauncher(ServiceAnchorElevatedLaunch.Declined);
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        Assert.Equal(
            ServiceEnableCoordinatorOutcome.ApprovalDeclined,
            Run(new[] { ServiceEnableVerbs.InitiatorVerb }, launcher: launcher, client: client));

        Assert.Single(launcher.Launched);
        Assert.Equal(0, client.Requests);
    }

    [Fact]
    public void A_failed_launch_runs_no_client_transaction()
    {
        var launcher = new FakeLauncher(ServiceAnchorElevatedLaunch.Failed);
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        Assert.Equal(
            ServiceEnableCoordinatorOutcome.HelperLaunchFailed,
            Run(new[] { ServiceEnableVerbs.InitiatorVerb }, launcher: launcher, client: client));

        Assert.Equal(0, client.Requests);
    }

    // =======================================================================
    // BOUNDED WAIT AND EXIT MAPPING
    // =======================================================================

    [Fact]
    public void A_helper_that_dies_before_the_transaction_ends_the_wait_early()
    {
        var launcher = new FakeLauncher(() =>
            ServiceAnchorElevatedLaunch.Started(new FakeElevatedProcess(SetupExitCodes.GenericError, alreadyExited: true)));

        var blocking = new BlockingChannelClient();
        Assert.Equal(
            ServiceEnableCoordinatorOutcome.HelperExitedBeforeTransaction,
            Run(new[] { ServiceEnableVerbs.InitiatorVerb }, launcher: launcher, client: blocking));

        blocking.Release();
    }

    private sealed class BlockingChannelClient : IServiceAnchorChannelClient
    {
        private readonly ManualResetEventSlim _gate = new(false);

        internal void Release() => _gate.Set();

        public ServiceInitiatingUserIdentityChannelOutcome Request(string endpointName, TimeSpan timeout)
        {
            _gate.Wait(TimeSpan.FromSeconds(30));
            return ServiceInitiatingUserIdentityChannelOutcome.Completed;
        }
    }

    [Fact]
    public void A_helper_that_never_exits_after_a_completed_transaction_is_bounded()
    {
        var launcher = new FakeLauncher(() =>
            ServiceAnchorElevatedLaunch.Started(new NeverExitingProcess()));

        Assert.Equal(
            ServiceEnableCoordinatorOutcome.HelperDidNotExit,
            Run(new[] { ServiceEnableVerbs.InitiatorVerb }, launcher: launcher));
    }

    [Fact]
    public void A_nonzero_helper_exit_after_a_completed_transaction_is_a_failure()
    {
        Assert.Equal(
            ServiceEnableCoordinatorOutcome.HelperReportedFailure,
            Run(new[] { ServiceEnableVerbs.InitiatorVerb }, launcher: StartedLauncher(SetupExitCodes.GenericError)));
    }

    [Theory]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict,
        ServiceEnableCoordinatorOutcome.RecoveryRequired)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired,
        ServiceEnableCoordinatorOutcome.RecoveryRequired)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated,
        ServiceEnableCoordinatorOutcome.AcknowledgementNotReceived)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused,
        ServiceEnableCoordinatorOutcome.AcknowledgementNotReceived)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch,
        ServiceEnableCoordinatorOutcome.AcknowledgementNotReceived)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.NoClientConnected,
        ServiceEnableCoordinatorOutcome.AcknowledgementNotReceived)]
    public void A_bounded_channel_outcome_maps_to_its_bounded_coordinator_outcome(
        ServiceInitiatingUserIdentityChannelOutcome channelOutcome,
        ServiceEnableCoordinatorOutcome expected)
    {
        // CYCLE 67. The helper now supplies an AGREEING bounded exit code. A
        // disagreeing one is a distinct case, proven separately below.
        int agreeing = ServiceEnableFailureContract.ToExitCode(
            ServiceEnableFailureCause.Unavailable,
            ServiceEnableFailureContract.ExpectedDispositionFor(channelOutcome));

        Assert.Equal(
            expected,
            Run(
                new[] { ServiceEnableVerbs.InitiatorVerb },
                launcher: StartedLauncher(agreeing),
                client: new FakeChannelClient(channelOutcome)));
    }

    [Fact]
    public void Only_a_completed_transaction_and_a_zero_helper_exit_report_completed()
    {
        foreach (ServiceInitiatingUserIdentityChannelOutcome outcome
                 in Enum.GetValues<ServiceInitiatingUserIdentityChannelOutcome>())
        {
            ServiceEnableCoordinatorOutcome result =
                Run(new[] { ServiceEnableVerbs.InitiatorVerb }, client: new FakeChannelClient(outcome));

            if (outcome == ServiceInitiatingUserIdentityChannelOutcome.Completed)
            {
                Assert.Equal(ServiceEnableCoordinatorOutcome.Completed, result);
            }
            else
            {
                Assert.NotEqual(ServiceEnableCoordinatorOutcome.Completed, result);
            }
        }
    }

    [Fact]
    public void The_elevated_process_handle_is_always_disposed()
    {
        var process = new FakeElevatedProcess(SetupExitCodes.Ok, alreadyExited: false);
        var launcher = new FakeLauncher(() => ServiceAnchorElevatedLaunch.Started(process));

        Run(new[] { ServiceEnableVerbs.InitiatorVerb }, launcher: launcher);
        Assert.True(process.Disposed);
    }

    // =======================================================================
    // ORDINARY SETUP NEVER REACHES EITHER SERVICE-ENABLE VERB
    // =======================================================================

    [Fact]
    public void No_ordinary_setup_verb_or_flag_matches_either_service_enable_verb()
    {
        foreach (string verb in ArgParser.KnownVerbs)
        {
            Assert.False(ServiceEnableVerbs.IsRequested(verb));
            Assert.False(ServiceEnableVerbs.IsInitiatorRequested(verb));
            Assert.False(ServiceEnableVerbs.IsElevatedHelperRequested(verb));
        }

        foreach (string token in new[]
        {
            "install", "update", "repair", "apply-update", "uninstall", "status", "version", "help",
            "--install-root", "--payload-root", "--force", "--quiet", "--silent", "--dry-run",
            "provider-status", "provider-repair", "", "service-enable-extra", "service", "enable",
            "service-anchor-bind", "service-anchor-bind-elevated",
        })
        {
            Assert.False(ServiceEnableVerbs.IsRequested(token));
        }
    }

    [Fact]
    public void The_service_enable_verbs_are_not_public_setup_verbs()
    {
        Assert.DoesNotContain(ServiceEnableVerbs.InitiatorVerb, ArgParser.KnownVerbs);
        Assert.DoesNotContain(ServiceEnableVerbs.ElevatedHelperVerb, ArgParser.KnownVerbs);
    }

    [Fact]
    public void The_two_internal_verb_families_are_distinct_spellings()
    {
        Assert.NotEqual(ServiceEnableVerbs.InitiatorVerb, ServiceAnchorElevationVerbs.InitiatorVerb);
        Assert.NotEqual(ServiceEnableVerbs.ElevatedHelperVerb, ServiceAnchorElevationVerbs.ElevatedHelperVerb);
        Assert.False(ServiceAnchorElevationVerbs.IsRequested(ServiceEnableVerbs.InitiatorVerb));
        Assert.False(ServiceAnchorElevationVerbs.IsRequested(ServiceEnableVerbs.ElevatedHelperVerb));
        Assert.False(ServiceEnableVerbs.IsRequested(ServiceAnchorElevationVerbs.InitiatorVerb));
        Assert.False(ServiceEnableVerbs.IsRequested(ServiceAnchorElevationVerbs.ElevatedHelperVerb));
    }

    // =======================================================================
    // BOUNDED SURFACES LEAK NOTHING
    // =======================================================================

    [Fact]
    public void A_default_outcome_is_unspecified_and_is_never_success()
    {
        Assert.Equal(ServiceEnableCoordinatorOutcome.Unspecified, default(ServiceEnableCoordinatorOutcome));
        Assert.Equal(ServiceEnableOperationOutcome.Unspecified, default(ServiceEnableOperationOutcome));
        Assert.NotEqual(ServiceEnableCoordinatorOutcome.Completed, default(ServiceEnableCoordinatorOutcome));
    }

    [Fact]
    public void The_launch_spec_to_string_carries_no_path_or_argument()
    {
        ServiceAnchorElevationLaunchSpec? spec = ServiceEnableCoordinator.TryComposeLaunchSpec(
            SampleHelperPath, ServiceInitiatingUserIdentityChannelContract.NewEndpointName(), SampleInitiator);
        Assert.NotNull(spec);

        string text = spec!.ToString();
        Assert.DoesNotContain(SampleHelperPath, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--endpoint", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parsed_elevated_arguments_to_string_carries_no_endpoint_pid_or_filetime()
    {
        string endpoint = ServiceInitiatingUserIdentityChannelContract.NewEndpointName();
        Assert.True(ServiceEnableProtocol.TryParseElevated(
            ServiceEnableProtocol.ComposeElevatedArguments(endpoint, SampleInitiator),
            out ServiceEnableElevatedArguments? parsed));

        string text = parsed!.ToString();
        Assert.DoesNotContain(endpoint, text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SampleInitiator.ProcessId.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SampleInitiator.CreationFileTime.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Initiator_facts_are_captured_from_this_process_and_never_supplied_by_a_caller()
    {
        var facts = new FakeInitiatorFacts(SampleInitiator);
        ServiceEnableCoordinator.Run(
            new[] { ServiceEnableVerbs.InitiatorVerb },
            facts,
            new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
            new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
            StartedLauncher(),
            new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed),
            Bounded,
            Bounded);

        Assert.Equal(1, facts.Captures);
    }

    [Fact]
    public void An_initiator_whose_own_process_facts_are_unavailable_refuses_before_any_launch()
    {
        var launcher = StartedLauncher();
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        Assert.Equal(
            ServiceEnableCoordinatorOutcome.InitiatorFactsUnavailable,
            ServiceEnableCoordinator.Run(
                new[] { ServiceEnableVerbs.InitiatorVerb },
                new FakeInitiatorFacts(default),
                new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
                new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
                launcher,
                client,
                Bounded,
                Bounded));

        Assert.Empty(launcher.Launched);
        Assert.Equal(0, client.Requests);
    }

    // =======================================================================
    // CYCLE 67 - THE BOUNDED HELPER EXIT-CODE CONTRACT
    // =======================================================================
    //
    // WHY THIS SECTION EXISTS. The first real attended service-enable returned
    // exit code 1 and NOTHING else. One generic code stood for every possible
    // cause, so the failure could not be named even after the fact.

    private static ServiceEnableFailureCause[] RealCauses() =>
        Enum.GetValues<ServiceEnableFailureCause>()
            .Where(c => c != ServiceEnableFailureCause.None)
            .ToArray();

    private static ServiceEnableFailureDisposition[] FailingDispositions() => new[]
    {
        ServiceEnableFailureDisposition.RefusedBeforeMutation,
        ServiceEnableFailureDisposition.Compensated,
        ServiceEnableFailureDisposition.RecoveryRequired,
    };

    [Fact]
    public void The_cause_and_disposition_to_exit_code_mapping_is_total_and_one_to_one()
    {
        var seen = new Dictionary<int, string>();

        // The ONE success pair is the ONLY zero.
        Assert.Equal(
            ServiceEnableFailureContract.SuccessExitCode,
            ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.None, ServiceEnableFailureDisposition.Completed));
        seen[ServiceEnableFailureContract.SuccessExitCode] = "none/completed";

        foreach (ServiceEnableFailureCause cause in RealCauses())
        {
            foreach (ServiceEnableFailureDisposition disposition in FailingDispositions())
            {
                int code = ServiceEnableFailureContract.ToExitCode(cause, disposition);

                Assert.NotEqual(ServiceEnableFailureContract.SuccessExitCode, code);
                Assert.True(
                    ServiceEnableFailureContract.IsInDocumentedBand(code),
                    cause + "/" + disposition + " produced out-of-band code " + code);

                string key = cause + "/" + disposition;
                Assert.False(seen.ContainsKey(code), "code " + code + " is shared by " + key);
                seen[code] = key;

                // The code round-trips back to the SAME pair, so a coordinator
                // reading it recovers exactly what the helper meant.
                ServiceEnableFailureClassification back =
                    ServiceEnableFailureContract.FromExitCode(code);
                Assert.Equal(cause, back.Cause);
                Assert.Equal(disposition, back.Disposition);
            }
        }

        // CYCLE 71. DERIVED FROM SOURCE, never hand-typed: one success pair plus
        // every real cause crossed with every failing disposition. A hand-typed
        // literal here silently stopped counting the moment a cause was added.
        Assert.Equal(1 + (RealCauses().Length * FailingDispositions().Length), seen.Count);
    }

    [Fact]
    public void Every_pair_including_contradictory_ones_maps_and_only_success_is_zero()
    {
        foreach (ServiceEnableFailureCause cause in Enum.GetValues<ServiceEnableFailureCause>())
        {
            foreach (ServiceEnableFailureDisposition disposition
                     in Enum.GetValues<ServiceEnableFailureDisposition>())
            {
                int code = ServiceEnableFailureContract.ToExitCode(cause, disposition);

                bool isSuccessPair =
                    cause == ServiceEnableFailureCause.None
                    && disposition == ServiceEnableFailureDisposition.Completed;

                Assert.Equal(isSuccessPair, code == ServiceEnableFailureContract.SuccessExitCode);
                Assert.True(isSuccessPair || ServiceEnableFailureContract.IsInDocumentedBand(code));
            }
        }

        // A CONTRADICTORY pair never reads back as "nothing happened".
        ServiceEnableFailureClassification contradiction =
            ServiceEnableFailureContract.FromExitCode(ServiceEnableFailureContract.UnclassifiedExitCode);
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, contradiction.Disposition);
    }

    [Fact]
    public void The_bounded_band_never_collides_with_an_existing_setup_exit_code()
    {
        int[] existing =
        {
            SetupExitCodes.Ok, SetupExitCodes.GenericError, SetupExitCodes.UsageError,
            SetupExitCodes.InternalError, SetupExitCodes.InstallFailed, SetupExitCodes.UpdateFailed,
            SetupExitCodes.RepairFailed, SetupExitCodes.UninstallFailed, SetupExitCodes.DowngradeBlocked,
            SetupExitCodes.RollbackPerformed, SetupExitCodes.RollbackFailed,
            SetupExitCodes.IntegrityCheckFailed, SetupExitCodes.UninstallPartialFailure,
            SetupExitCodes.UninstallAppExeLocked, SetupExitCodes.WebView2RuntimeMissing,
            SetupExitCodes.WebView2DetectionAmbiguous, SetupExitCodes.UnsupportedWindowsVersion,
            SetupExitCodes.TestIsolationViolation, SetupExitCodes.HandoffRequired,
            SetupExitCodes.HandoffFailed, SetupExitCodes.NotImplementedInPhase2,
        };

        foreach (ServiceEnableFailureCause cause in RealCauses())
        {
            foreach (ServiceEnableFailureDisposition disposition in FailingDispositions())
            {
                int code = ServiceEnableFailureContract.ToExitCode(cause, disposition);
                Assert.DoesNotContain(code, existing);
            }
        }

        Assert.DoesNotContain(ServiceEnableFailureContract.UnclassifiedExitCode, existing);
    }

    [Fact]
    public void An_unrecognised_helper_exit_code_is_never_read_as_nothing_happened()
    {
        // GenericError is exactly what the old helper returned for EVERY failure.
        // A stale or mismatched helper must be loudly wrong, not quietly "safe".
        foreach (int stale in new[] { SetupExitCodes.GenericError, 3, 50, 91, 200, -1, int.MaxValue })
        {
            ServiceEnableFailureClassification classification =
                ServiceEnableFailureContract.FromExitCode(stale);
            Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, classification.Disposition);
            Assert.Equal(ServiceEnableFailureCause.Unavailable, classification.Cause);
        }

        // The two codes the enable helper can only reach BEFORE the pipe exists
        // are provably pre-mutation.
        foreach (int early in new[] { SetupExitCodes.UsageError, SetupExitCodes.UnsupportedWindowsVersion })
        {
            Assert.Equal(
                ServiceEnableFailureDisposition.RefusedBeforeMutation,
                ServiceEnableFailureContract.FromExitCode(early).Disposition);
        }
    }

    // =======================================================================
    // CYCLE 71 - THE EXIT-CODE MAP IS EXTENDED WITHOUT WIDENING A SINGLE BAND
    // =======================================================================
    //
    // WHY A NAIVE APPEND WAS FORBIDDEN. The cycle-67 bands 151-160, 161-170 and
    // 171-180 were EXACTLY SATURATED at ten causes. Appending an eleventh cause
    // to the same arithmetic would have made RefusedBeforeMutation emit 161 -
    // already Compensated's SigningPolicyRefused - and because FromExitCode
    // tests the bands IN ORDER, a genuine recovery_required would have decoded
    // as compensated. That is the single most dangerous silent misread this
    // contract can produce, so the legacy region is FROZEN and new causes live
    // in separate, generously spaced extension regions.

    /// <summary>
    /// EVERY legacy cause/disposition pair, pinned to the EXACT numeric code it
    /// produced before this cycle. These literals are deliberately hand-written:
    /// a pin that derived itself from the code under test would pin nothing.
    /// </summary>
    public static TheoryData<ServiceEnableFailureCause, ServiceEnableFailureDisposition, int> LegacyPins()
    {
        var data = new TheoryData<ServiceEnableFailureCause, ServiceEnableFailureDisposition, int>();
        (ServiceEnableFailureCause Cause, int Refused, int Compensated, int Recovery)[] rows =
        {
            (ServiceEnableFailureCause.SigningPolicyRefused, 151, 161, 171),
            (ServiceEnableFailureCause.PayloadRefused, 152, 162, 172),
            (ServiceEnableFailureCause.ProgramFilesPreflightRefused, 153, 163, 173),
            (ServiceEnableFailureCause.ExistingStateRefused, 154, 164, 174),
            (ServiceEnableFailureCause.ExtractionRefused, 155, 165, 175),
            (ServiceEnableFailureCause.RegistrationRefused, 156, 166, 176),
            (ServiceEnableFailureCause.VerificationRefused, 157, 167, 177),
            (ServiceEnableFailureCause.MachineDataProtectionRefused, 158, 168, 178),
            (ServiceEnableFailureCause.StartabilityRefused, 159, 169, 179),
            (ServiceEnableFailureCause.Unavailable, 160, 170, 180),
        };

        foreach ((ServiceEnableFailureCause cause, int refused, int compensated, int recovery) in rows)
        {
            data.Add(cause, ServiceEnableFailureDisposition.RefusedBeforeMutation, refused);
            data.Add(cause, ServiceEnableFailureDisposition.Compensated, compensated);
            data.Add(cause, ServiceEnableFailureDisposition.RecoveryRequired, recovery);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LegacyPins))]
    public void Every_legacy_pair_keeps_its_exact_pre_cycle_seventy_one_exit_code(
        ServiceEnableFailureCause cause, ServiceEnableFailureDisposition disposition, int pinned)
    {
        Assert.Equal(pinned, ServiceEnableFailureContract.ToExitCode(cause, disposition));

        ServiceEnableFailureClassification back = ServiceEnableFailureContract.FromExitCode(pinned);
        Assert.Equal(cause, back.Cause);
        Assert.Equal(disposition, back.Disposition);
    }

    [Fact]
    public void The_two_fixed_legacy_boundary_codes_keep_their_exact_meaning()
    {
        Assert.Equal(0, ServiceEnableFailureContract.SuccessExitCode);
        Assert.Equal(140, ServiceEnableFailureContract.UnclassifiedExitCode);
        Assert.Equal(150, ServiceEnableFailureContract.RefusedBeforeMutationBase);
        Assert.Equal(160, ServiceEnableFailureContract.CompensatedBase);
        Assert.Equal(170, ServiceEnableFailureContract.RecoveryRequiredBase);

        Assert.Equal(
            0,
            ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.None, ServiceEnableFailureDisposition.Completed));

        ServiceEnableFailureClassification zero = ServiceEnableFailureContract.FromExitCode(0);
        Assert.Equal(ServiceEnableFailureCause.None, zero.Cause);
        Assert.Equal(ServiceEnableFailureDisposition.Completed, zero.Disposition);
    }

    [Fact]
    public void The_map_is_a_total_bijection_over_every_documented_pair()
    {
        // FORWARD: every documented pair produces exactly one code, and no code
        // is produced by two pairs.
        var codeToPair = new Dictionary<int, string>();

        codeToPair[ServiceEnableFailureContract.ToExitCode(
            ServiceEnableFailureCause.None, ServiceEnableFailureDisposition.Completed)] =
            ServiceEnableFailureCause.None + "/" + ServiceEnableFailureDisposition.Completed;

        foreach (ServiceEnableFailureCause cause in RealCauses())
        {
            foreach (ServiceEnableFailureDisposition disposition in FailingDispositions())
            {
                int code = ServiceEnableFailureContract.ToExitCode(cause, disposition);
                string pair = cause + "/" + disposition;

                Assert.False(
                    codeToPair.ContainsKey(code),
                    "code " + code + " decodes to two pairs: " + pair + " and "
                        + (codeToPair.TryGetValue(code, out string? other) ? other : "?"));
                codeToPair[code] = pair;
            }
        }

        // INVERSE: every produced code decodes back to the SAME pair. Together
        // with the injectivity above this is a bijection over the documented set.
        foreach (KeyValuePair<int, string> entry in codeToPair)
        {
            ServiceEnableFailureClassification back =
                ServiceEnableFailureContract.FromExitCode(entry.Key);
            Assert.Equal(entry.Value, back.Cause + "/" + back.Disposition);
        }

        // DERIVED, never hand-typed.
        Assert.Equal(1 + (RealCauses().Length * FailingDispositions().Length), codeToPair.Count);

        // The FULL CARTESIAN PRODUCT, including contradictory pairs, is total:
        // every input produces a code and only the one success pair produces 0.
        int pairsExamined = 0;
        foreach (ServiceEnableFailureCause cause in Enum.GetValues<ServiceEnableFailureCause>())
        {
            foreach (ServiceEnableFailureDisposition disposition
                     in Enum.GetValues<ServiceEnableFailureDisposition>())
            {
                pairsExamined++;
                int code = ServiceEnableFailureContract.ToExitCode(cause, disposition);
                bool isSuccessPair =
                    cause == ServiceEnableFailureCause.None
                    && disposition == ServiceEnableFailureDisposition.Completed;

                Assert.Equal(isSuccessPair, code == ServiceEnableFailureContract.SuccessExitCode);
                Assert.True(
                    isSuccessPair || ServiceEnableFailureContract.IsInDocumentedBand(code),
                    cause + "/" + disposition + " produced undocumented code " + code);
            }
        }

        Assert.Equal(
            Enum.GetValues<ServiceEnableFailureCause>().Length
                * Enum.GetValues<ServiceEnableFailureDisposition>().Length,
            pairsExamined);
    }

    [Fact]
    public void No_new_cause_may_reuse_a_legacy_code_or_any_reserved_setup_exit_code()
    {
        int[] reservedSetupCodes =
        {
            SetupExitCodes.Ok, SetupExitCodes.GenericError, SetupExitCodes.UsageError,
            SetupExitCodes.InternalError, SetupExitCodes.InstallFailed, SetupExitCodes.UpdateFailed,
            SetupExitCodes.RepairFailed, SetupExitCodes.UninstallFailed, SetupExitCodes.DowngradeBlocked,
            SetupExitCodes.RollbackPerformed, SetupExitCodes.RollbackFailed,
            SetupExitCodes.IntegrityCheckFailed, SetupExitCodes.UninstallPartialFailure,
            SetupExitCodes.UninstallAppExeLocked, SetupExitCodes.WebView2RuntimeMissing,
            SetupExitCodes.WebView2DetectionAmbiguous, SetupExitCodes.UnsupportedWindowsVersion,
            SetupExitCodes.TestIsolationViolation, SetupExitCodes.HandoffRequired,
            SetupExitCodes.HandoffFailed, SetupExitCodes.NotImplementedInPhase2,
        };

        // Every code the legacy region can emit, derived from the pinned rows.
        var legacyCodes = new HashSet<int>();
        foreach (object[] row in LegacyPins())
        {
            legacyCodes.Add((int)row[2]);
        }

        Assert.Equal(30, legacyCodes.Count);

        ServiceEnableFailureCause[] extensionCauses = RealCauses()
            .Where(c => (int)c > (int)ServiceEnableFailureCause.Unavailable)
            .ToArray();

        // The cycle genuinely ADDED causes; a zero here would make this test vacuous.
        Assert.NotEmpty(extensionCauses);

        foreach (ServiceEnableFailureCause cause in extensionCauses)
        {
            foreach (ServiceEnableFailureDisposition disposition in FailingDispositions())
            {
                int code = ServiceEnableFailureContract.ToExitCode(cause, disposition);

                Assert.DoesNotContain(code, reservedSetupCodes);
                Assert.DoesNotContain(code, legacyCodes);
                Assert.NotEqual(ServiceEnableFailureContract.UnclassifiedExitCode, code);

                // No new code may land inside the frozen legacy window at all.
                Assert.False(
                    code >= 140 && code <= 180,
                    cause + "/" + disposition + " landed inside the frozen legacy window: " + code);

                Assert.True(ServiceEnableFailureContract.IsInDocumentedBand(code));
            }
        }
    }

    [Fact]
    public void An_undocumented_or_gap_code_is_always_fail_closed()
    {
        // Every integer in a generous sweep either decodes to a DOCUMENTED pair
        // that round-trips, or is fail-closed as unavailable/recovery_required.
        // Nothing in between, and no gap value is ever readable as success or as
        // "nothing happened".
        var documented = new HashSet<int> { ServiceEnableFailureContract.SuccessExitCode };
        foreach (ServiceEnableFailureCause cause in RealCauses())
        {
            foreach (ServiceEnableFailureDisposition disposition in FailingDispositions())
            {
                documented.Add(ServiceEnableFailureContract.ToExitCode(cause, disposition));
            }
        }

        int gapsExamined = 0;
        for (int code = -5; code <= 700; code++)
        {
            if (documented.Contains(code))
            {
                continue;
            }

            ServiceEnableFailureClassification classification =
                ServiceEnableFailureContract.FromExitCode(code);

            // The two codes the enable helper can only reach BEFORE the pipe
            // exists stay provably pre-mutation; everything else is recovery.
            if (code == SetupExitCodes.UsageError || code == SetupExitCodes.UnsupportedWindowsVersion)
            {
                Assert.Equal(ServiceEnableFailureCause.Unavailable, classification.Cause);
                Assert.Equal(
                    ServiceEnableFailureDisposition.RefusedBeforeMutation, classification.Disposition);
                continue;
            }

            gapsExamined++;
            Assert.Equal(ServiceEnableFailureCause.Unavailable, classification.Cause);
            Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, classification.Disposition);
            Assert.False(classification.IsCompleted);
        }

        Assert.True(gapsExamined > 600, "the gap sweep examined too few values to be meaningful");

        // Explicit spot pins on the most dangerous neighbours of the real codes.
        foreach (int gap in new[] { 139, 141, 150, 181, 199, int.MinValue, int.MaxValue })
        {
            ServiceEnableFailureClassification c = ServiceEnableFailureContract.FromExitCode(gap);
            Assert.Equal(ServiceEnableFailureCause.Unavailable, c.Cause);
            Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, c.Disposition);
        }
    }

    [Fact]
    public void Every_cause_has_a_distinct_lowercase_token_and_the_new_ones_are_not_the_default()
    {
        ServiceEnableFailureCause[] all = Enum.GetValues<ServiceEnableFailureCause>();

        // DERIVED counts on both sides: a member that forgot its token would
        // collapse onto the fail-closed default and shrink the distinct count.
        Assert.Equal(
            all.Length,
            all.Select(ServiceEnableFailureContract.CauseToken)
                .Distinct(StringComparer.Ordinal)
                .Count());

        foreach (ServiceEnableFailureCause cause in all)
        {
            string token = ServiceEnableFailureContract.CauseToken(cause);
            Assert.Matches("^[a-z][a-z_]*$", token);
            Assert.Equal(token.ToLowerInvariant(), token);
        }

        // Only the genuine Unavailable member may carry the fail-closed token.
        Assert.Single(all.Where(c => ServiceEnableFailureContract.CauseToken(c) == "unavailable"));
        Assert.Equal(
            "unavailable",
            ServiceEnableFailureContract.CauseToken(ServiceEnableFailureCause.Unavailable));

        // An UNDEFINED cast still reads as the fail-closed token.
        Assert.Equal(
            "unavailable", ServiceEnableFailureContract.CauseToken((ServiceEnableFailureCause)9999));
        Assert.Equal(
            "none",
            ServiceEnableFailureContract.DispositionToken((ServiceEnableFailureDisposition)9999));
    }

    // =======================================================================
    // CYCLE 67 - THE CHANNEL / HELPER CROSS-CHECK
    // =======================================================================

    [Fact]
    public void An_agreeing_helper_exit_code_carries_its_cause_to_the_coordinator()
    {
        foreach (ServiceEnableFailureCause cause in RealCauses())
        {
            int code = ServiceEnableFailureContract.ToExitCode(
                cause, ServiceEnableFailureDisposition.Compensated);

            ServiceEnableCoordinatorResult result = ServiceEnableCoordinator.RunDetailed(
                new[] { ServiceEnableVerbs.InitiatorVerb },
                new FakeInitiatorFacts(SampleInitiator),
                new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
                new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
                StartedLauncher(code),
                new FakeChannelClient(
                    ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated),
                Bounded,
                Bounded);

            Assert.Equal(ServiceEnableCoordinatorOutcome.AcknowledgementNotReceived, result.Outcome);
            Assert.Equal(cause, result.Cause);
            Assert.Equal(ServiceEnableFailureDisposition.Compensated, result.Disposition);
        }
    }

    [Theory]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated,
        ServiceEnableFailureDisposition.RefusedBeforeMutation)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated,
        ServiceEnableFailureDisposition.RecoveryRequired)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused,
        ServiceEnableFailureDisposition.Compensated)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired,
        ServiceEnableFailureDisposition.RefusedBeforeMutation)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict,
        ServiceEnableFailureDisposition.Compensated)]
    public void A_disagreeing_helper_exit_code_becomes_recovery_required_and_never_a_guessed_cause(
        ServiceInitiatingUserIdentityChannelOutcome channelOutcome,
        ServiceEnableFailureDisposition helperDisposition)
    {
        Assert.NotEqual(
            ServiceEnableFailureContract.ExpectedDispositionFor(channelOutcome), helperDisposition);

        int disagreeing = ServiceEnableFailureContract.ToExitCode(
            ServiceEnableFailureCause.StartabilityRefused, helperDisposition);

        ServiceEnableCoordinatorResult result = ServiceEnableCoordinator.RunDetailed(
            new[] { ServiceEnableVerbs.InitiatorVerb },
            new FakeInitiatorFacts(SampleInitiator),
            new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
            new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
            StartedLauncher(disagreeing),
            new FakeChannelClient(channelOutcome),
            Bounded,
            Bounded);

        Assert.Equal(ServiceEnableCoordinatorOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, result.Disposition);

        // The helper's cause is NOT adopted: a broken verification chain never
        // yields a specific cause.
        Assert.NotEqual(ServiceEnableFailureCause.StartabilityRefused, result.Cause);
        Assert.Equal(ServiceEnableFailureCause.Unavailable, result.Cause);
    }

    [Fact]
    public void A_zero_helper_exit_without_a_completed_channel_outcome_is_a_failure()
    {
        foreach (ServiceInitiatingUserIdentityChannelOutcome outcome
                 in Enum.GetValues<ServiceInitiatingUserIdentityChannelOutcome>())
        {
            if (outcome == ServiceInitiatingUserIdentityChannelOutcome.Completed)
            {
                continue;
            }

            ServiceEnableCoordinatorResult result = ServiceEnableCoordinator.RunDetailed(
                new[] { ServiceEnableVerbs.InitiatorVerb },
                new FakeInitiatorFacts(SampleInitiator),
                new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
                new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
                StartedLauncher(SetupExitCodes.Ok),
                new FakeChannelClient(outcome),
                Bounded,
                Bounded);

            Assert.NotEqual(ServiceEnableCoordinatorOutcome.Completed, result.Outcome);
            Assert.Equal(ServiceEnableCoordinatorOutcome.RecoveryRequired, result.Outcome);
            Assert.False(result.Classification.IsCompleted);
        }
    }

    // =======================================================================
    // CYCLE 67R - A POST-CREATE ANCHOR FAILURE IS AN ATTENDED RECOVERY
    // =======================================================================

    [Fact]
    public void A_post_create_anchor_persistence_failure_is_preserved_as_recovery_required()
    {
        int agreeing = ServiceEnableFailureContract.ToExitCode(
            ServiceEnableFailureCause.Unavailable,
            ServiceEnableFailureContract.ExpectedDispositionFor(
                ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired));

        ServiceEnableCoordinatorResult result = ServiceEnableCoordinator.RunDetailed(
            new[] { ServiceEnableVerbs.InitiatorVerb },
            new FakeInitiatorFacts(SampleInitiator),
            new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
            new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
            StartedLauncher(agreeing),
            new FakeChannelClient(
                ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired),
            Bounded,
            Bounded);

        // It is NOT flattened into an acknowledgement failure a caller might
        // retry: the remedy is an attended, elevated recovery.
        Assert.Equal(ServiceEnableCoordinatorOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, result.Disposition);
    }

    [Fact]
    public void A_helper_that_calls_a_post_create_anchor_failure_benign_breaks_the_cross_check()
    {
        // THE DEFECT THIS PINS. If the helper reports refused_before_mutation
        // for a write that may have created the fixed machine directories, the
        // two independent reports DISAGREE and the only honest answer is
        // recovery-required - never the helper's benign claim.
        int benign = ServiceEnableFailureContract.ToExitCode(
            ServiceEnableFailureCause.Unavailable,
            ServiceEnableFailureDisposition.RefusedBeforeMutation);

        ServiceEnableCoordinatorResult result = ServiceEnableCoordinator.RunDetailed(
            new[] { ServiceEnableVerbs.InitiatorVerb },
            new FakeInitiatorFacts(SampleInitiator),
            new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
            new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
            StartedLauncher(benign),
            new FakeChannelClient(
                ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired),
            Bounded,
            Bounded);

        Assert.Equal(ServiceEnableCoordinatorOutcome.RecoveryRequired, result.Outcome);
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, result.Disposition);
        Assert.NotEqual(ServiceEnableFailureDisposition.RefusedBeforeMutation, result.Disposition);
    }

    [Fact]
    public void The_benign_anchor_refusal_keeps_its_benign_reading()
    {
        // The split must not turn every pre-create anchor refusal into a false
        // recovery-required. This is the control that keeps option (a) honest.
        int agreeing = ServiceEnableFailureContract.ToExitCode(
            ServiceEnableFailureCause.Unavailable,
            ServiceEnableFailureContract.ExpectedDispositionFor(
                ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused));

        ServiceEnableCoordinatorResult result = ServiceEnableCoordinator.RunDetailed(
            new[] { ServiceEnableVerbs.InitiatorVerb },
            new FakeInitiatorFacts(SampleInitiator),
            new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
            new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
            StartedLauncher(agreeing),
            new FakeChannelClient(
                ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused),
            Bounded,
            Bounded);

        Assert.Equal(ServiceEnableCoordinatorOutcome.AcknowledgementNotReceived, result.Outcome);
        Assert.Equal(ServiceEnableFailureDisposition.RefusedBeforeMutation, result.Disposition);
    }

    [Fact]
    public void A_completed_channel_outcome_with_a_nonzero_helper_exit_keeps_the_helpers_cause()
    {
        int code = ServiceEnableFailureContract.ToExitCode(
            ServiceEnableFailureCause.MachineDataProtectionRefused,
            ServiceEnableFailureDisposition.RecoveryRequired);

        ServiceEnableCoordinatorResult result = ServiceEnableCoordinator.RunDetailed(
            new[] { ServiceEnableVerbs.InitiatorVerb },
            new FakeInitiatorFacts(SampleInitiator),
            new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
            new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
            StartedLauncher(code),
            new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed),
            Bounded,
            Bounded);

        Assert.Equal(ServiceEnableCoordinatorOutcome.HelperReportedFailure, result.Outcome);
        Assert.Equal(ServiceEnableFailureCause.MachineDataProtectionRefused, result.Cause);
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, result.Disposition);
    }

    [Fact]
    public void A_pre_helper_refusal_reports_a_pre_mutation_disposition()
    {
        ServiceEnableCoordinatorResult declined = ServiceEnableCoordinator.RunDetailed(
            new[] { ServiceEnableVerbs.InitiatorVerb },
            new FakeInitiatorFacts(SampleInitiator),
            new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
            new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
            new FakeLauncher(ServiceAnchorElevatedLaunch.Declined),
            new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed),
            Bounded,
            Bounded);

        Assert.Equal(ServiceEnableCoordinatorOutcome.ApprovalDeclined, declined.Outcome);
        Assert.Equal(ServiceEnableFailureDisposition.RefusedBeforeMutation, declined.Disposition);

        ServiceEnableCoordinatorResult signing = ServiceEnableCoordinator.RunDetailed(
            new[] { ServiceEnableVerbs.InitiatorVerb },
            new FakeInitiatorFacts(SampleInitiator),
            new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
            new FakeSigningGate(ServiceHelperSigningPolicyState.SigningPolicyUnavailable),
            StartedLauncher(),
            new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed),
            Bounded,
            Bounded);

        Assert.Equal(ServiceEnableCoordinatorOutcome.SigningPolicyRefused, signing.Outcome);
        Assert.Equal(ServiceEnableFailureCause.SigningPolicyRefused, signing.Cause);
        Assert.Equal(ServiceEnableFailureDisposition.RefusedBeforeMutation, signing.Disposition);
    }

    // =======================================================================
    // CYCLE 67 - THE ONE BOUNDED SETUP DIAGNOSTIC LINE
    // =======================================================================

    [Fact]
    public void The_diagnostic_is_exactly_one_bounded_line_of_fixed_lowercase_tokens()
    {
        foreach (ServiceEnableFailureCause cause in Enum.GetValues<ServiceEnableFailureCause>())
        {
            foreach (ServiceEnableFailureDisposition disposition
                     in Enum.GetValues<ServiceEnableFailureDisposition>())
            {
                string line = ServiceEnableFailureContract.FormatDiagnostic(cause, disposition);

                // EXACTLY ONE line: no embedded terminator of any kind.
                Assert.DoesNotContain('\n', line);
                Assert.DoesNotContain('\r', line);

                Assert.StartsWith("service-enable outcome=", line, StringComparison.Ordinal);
                Assert.Contains(" disposition=", line, StringComparison.Ordinal);

                // Fixed lowercase tokens only - no path, identity or free text.
                string[] fields = line.Split(' ');
                Assert.Equal(3, fields.Length);
                Assert.Equal("service-enable", fields[0]);
                Assert.Equal(
                    "outcome=" + ServiceEnableFailureContract.CauseToken(cause), fields[1]);
                Assert.Equal(
                    "disposition=" + ServiceEnableFailureContract.DispositionToken(disposition),
                    fields[2]);

                Assert.Equal(line.ToLowerInvariant(), line);
                Assert.DoesNotContain('\\', line);
                Assert.DoesNotContain(':', line);
                Assert.DoesNotContain("s-1-", line, StringComparison.Ordinal);
            }
        }

        // Every token is DISTINCT, so two causes can never read as one.
        Assert.Equal(
            Enum.GetValues<ServiceEnableFailureCause>().Length,
            Enum.GetValues<ServiceEnableFailureCause>()
                .Select(ServiceEnableFailureContract.CauseToken)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            Enum.GetValues<ServiceEnableFailureDisposition>().Length,
            Enum.GetValues<ServiceEnableFailureDisposition>()
                .Select(ServiceEnableFailureContract.DispositionToken)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void Setup_emits_the_one_bounded_line_for_service_enable_and_none_for_any_other_verb()
    {
        string program = ReadRepoText("src/PAXCookbookSetup/Program.cs");

        // POSITIVE CONTROL. The matcher must fire on a sample that genuinely
        // contains the construct, so a measured count is real.
        Assert.Equal(
            2,
            CountOccurrences(
                "Console.Error.WriteLine(a); Console.Error.WriteLine(b);", "Console.Error.WriteLine("));

        // The bounded formatter is referenced EXACTLY ONCE in the whole of
        // Setup's entry point. Ordinary verbs keep their own pre-existing
        // diagnostics untouched - this cycle adds no output to any of them - and
        // none of them can reach the service-enable line.
        Assert.Equal(1, CountOccurrences(program, "ServiceEnableFailureContract.FormatDiagnostic("));
        Assert.Equal(1, CountOccurrences(program, "ServiceEnableCoordinator.RunDetailed(args)"));

        // The one reference lives INSIDE the service-enable initiator block: it
        // appears after the initiator guard and before the NEXT verb guard.
        int enableGuard = program.IndexOf(
            "ServiceEnableVerbs.IsInitiatorRequested(args[0])", StringComparison.Ordinal);
        int nextVerbGuard = program.IndexOf(
            "ServiceDisableVerbs.IsInitiatorRequested(args[0])", StringComparison.Ordinal);
        int diagnostic = program.IndexOf(
            "ServiceEnableFailureContract.FormatDiagnostic(", StringComparison.Ordinal);

        Assert.True(enableGuard >= 0);
        Assert.True(nextVerbGuard > enableGuard);
        Assert.InRange(diagnostic, enableGuard, nextVerbGuard);

        // EXACTLY ONE stderr write exists inside that block, so the verb emits
        // one line and never two.
        string enableBlock = program[enableGuard..nextVerbGuard];
        Assert.Equal(1, CountOccurrences(enableBlock, "Console.Error."));
        Assert.Equal(1, CountOccurrences(enableBlock, "Console.Error.WriteLine("));

        // The ordinary verb dispatcher - everything from `static int Run(` to the
        // end of the file - can never reach the service-enable diagnostic.
        int ordinaryDispatch = program.IndexOf("static int Run(string[] argv)", StringComparison.Ordinal);
        Assert.True(ordinaryDispatch > nextVerbGuard);
        Assert.Equal(
            0,
            CountOccurrences(program[ordinaryDispatch..], "ServiceEnableFailureContract"));
        Assert.Equal(0, CountOccurrences(program[ordinaryDispatch..], "service-enable outcome="));

        // NEGATIVE CONTROL. The block slice really is a strict subset of the
        // file, so the counts above are not measuring the whole document.
        Assert.True(enableBlock.Length < program.Length);
        Assert.True(CountOccurrences(program, "Console.Error.WriteLine(") > 1);

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

    // =======================================================================
    // CYCLE 68 - THE BOUNDED LINE FOR A POST-ANCHOR-WRITE FAILURE
    // =======================================================================
    //
    // The original failure cause is UNKNOWN and is deliberately NOT guessed, so
    // the cause token stays the honest `unavailable`. What DOES change is the
    // disposition: it now distinguishes a residue that was provably cleaned up
    // from one that could not be proven closed.

    [Fact]
    public void A_recovered_post_create_anchor_failure_reads_unavailable_and_compensated()
    {
        int agreeing = ServiceEnableFailureContract.ToExitCode(
            ServiceEnableFailureCause.Unavailable,
            ServiceEnableFailureContract.ExpectedDispositionFor(
                ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated));

        ServiceEnableCoordinatorResult result = ServiceEnableCoordinator.RunDetailed(
            new[] { ServiceEnableVerbs.InitiatorVerb },
            new FakeInitiatorFacts(SampleInitiator),
            new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
            new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
            StartedLauncher(agreeing),
            new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated),
            Bounded,
            Bounded);

        Assert.Equal(ServiceEnableFailureCause.Unavailable, result.Cause);
        Assert.Equal(ServiceEnableFailureDisposition.Compensated, result.Disposition);
        Assert.Equal(
            "service-enable outcome=unavailable disposition=compensated",
            ServiceEnableFailureContract.FormatDiagnostic(result.Cause, result.Disposition));

        // COMPENSATED IS NEVER SUCCESS.
        Assert.NotEqual(ServiceEnableCoordinatorOutcome.Completed, result.Outcome);
        Assert.False(result.Classification.IsCompleted);
    }

    [Fact]
    public void An_unrecovered_post_create_anchor_failure_reads_unavailable_and_recovery_required()
    {
        int agreeing = ServiceEnableFailureContract.ToExitCode(
            ServiceEnableFailureCause.Unavailable,
            ServiceEnableFailureContract.ExpectedDispositionFor(
                ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired));

        ServiceEnableCoordinatorResult result = ServiceEnableCoordinator.RunDetailed(
            new[] { ServiceEnableVerbs.InitiatorVerb },
            new FakeInitiatorFacts(SampleInitiator),
            new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath)),
            new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed),
            StartedLauncher(agreeing),
            new FakeChannelClient(
                ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired),
            Bounded,
            Bounded);

        Assert.Equal(ServiceEnableFailureCause.Unavailable, result.Cause);
        Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, result.Disposition);
        Assert.Equal(
            "service-enable outcome=unavailable disposition=recovery_required",
            ServiceEnableFailureContract.FormatDiagnostic(result.Cause, result.Disposition));

        Assert.Equal(ServiceEnableCoordinatorOutcome.RecoveryRequired, result.Outcome);
        Assert.False(result.Classification.IsCompleted);
    }

    [Fact]
    public void The_two_post_create_dispositions_are_distinguishable_on_the_wire_and_in_the_line()
    {
        // The whole point of the cycle: an operator can tell "we cleaned it up"
        // from "go look at the machine" WITHOUT either line ever claiming the
        // enablement worked.
        string compensated = ServiceEnableFailureContract.FormatDiagnostic(
            ServiceEnableFailureCause.Unavailable, ServiceEnableFailureDisposition.Compensated);
        string recovery = ServiceEnableFailureContract.FormatDiagnostic(
            ServiceEnableFailureCause.Unavailable, ServiceEnableFailureDisposition.RecoveryRequired);

        Assert.NotEqual(compensated, recovery);
        Assert.DoesNotContain("completed", compensated, StringComparison.Ordinal);
        Assert.DoesNotContain("completed", recovery, StringComparison.Ordinal);
        Assert.DoesNotContain("refused_before_mutation", compensated, StringComparison.Ordinal);
        Assert.DoesNotContain("refused_before_mutation", recovery, StringComparison.Ordinal);

        Assert.NotEqual(
            ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.Unavailable, ServiceEnableFailureDisposition.Compensated),
            ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.Unavailable, ServiceEnableFailureDisposition.RecoveryRequired));
    }

    // =======================================================================
    // CYCLE 74 - THE STARTABILITY CAUSES, AND THE ORDINAL TRAP THAT GUARDS THEM
    // =======================================================================
    //
    // THE TRAP, STATED BEFORE THE TESTS. ToExitCode and FromExitCode both bound
    // the extension region by ExtendedLastCauseOrdinal. APPENDING CAUSES WITHOUT
    // REPOINTING THAT CONSTANT does not fail to compile and does not fail a
    // token test: the new ordinals simply encode to UnclassifiedExitCode and
    // decode fail-closed to unavailable/recovery_required. The whole cycle would
    // silently do nothing while looking healthy. The first test below is the
    // guard against exactly that, and it is DERIVED from the enum so it cannot
    // drift.

    /// <summary>
    /// The causes this cycle added, paired with the EXACT lowercase token each
    /// must carry. The tokens are hand-written literals on purpose: a token list
    /// derived from the code under test would pin nothing at all.
    /// </summary>
    public static TheoryData<ServiceEnableFailureCause, string> CycleSeventyFourTokens() => new()
    {
        { ServiceEnableFailureCause.ServiceStartRefused, "service_start_refused" },
        { ServiceEnableFailureCause.ServiceRunningTimeout, "service_running_timeout" },
        { ServiceEnableFailureCause.ServiceProcessIdentityRefused, "service_process_identity_refused" },
        { ServiceEnableFailureCause.ServiceStatusDocumentRefused, "service_status_document_refused" },
        { ServiceEnableFailureCause.ServiceHeartbeatRefused, "service_heartbeat_refused" },
        { ServiceEnableFailureCause.ServiceForbiddenChildRefused, "service_forbidden_child_refused" },
        { ServiceEnableFailureCause.ServiceStartLogonRefused, "service_start_logon_refused" },
        { ServiceEnableFailureCause.ServiceStartAccessRefused, "service_start_access_refused" },
        { ServiceEnableFailureCause.ServiceStartBinaryUnavailable, "service_start_binary_unavailable" },
        { ServiceEnableFailureCause.ServiceStartDependencyRefused, "service_start_dependency_refused" },
        { ServiceEnableFailureCause.ServiceStartRequestTimeout, "service_start_request_timeout" },
    };

    /// <summary>
    /// CYCLE 75. The eight bounded status-readiness causes, paired with the
    /// EXACT lowercase token each must carry. Hand-written literals on purpose.
    /// </summary>
    public static TheoryData<ServiceEnableFailureCause, string> CycleSeventyFiveTokens() => new()
    {
        { ServiceEnableFailureCause.ServiceStatusMissingTimeout, "service_status_missing_timeout" },
        { ServiceEnableFailureCause.ServiceStatusStartingTimeout, "service_status_starting_timeout" },
        { ServiceEnableFailureCause.ServiceStatusUnreadable, "service_status_unreadable" },
        { ServiceEnableFailureCause.ServiceStatusMalformed, "service_status_malformed" },
        { ServiceEnableFailureCause.ServiceStatusWrongContext, "service_status_wrong_context" },
        { ServiceEnableFailureCause.ServiceStatusStopped, "service_status_stopped" },
        { ServiceEnableFailureCause.ServiceStatusFailed, "service_status_failed" },
        { ServiceEnableFailureCause.ServiceStatusUnknownState, "service_status_unknown_state" },
    };

    [Theory]
    [MemberData(nameof(CycleSeventyFiveTokens))]
    public void Every_cycle_75_cause_round_trips_through_its_own_exit_code(
        ServiceEnableFailureCause cause, string token)
    {
        int ordinal = (int)cause;

        // It is genuinely NEW: past every cycle-71 and cycle-74 ordinal.
        Assert.True(ordinal > (int)ServiceEnableFailureCause.ServiceStartRequestTimeout);
        Assert.InRange(
            ordinal,
            ServiceEnableFailureContract.ExtendedFirstCauseOrdinal,
            ServiceEnableFailureContract.ExtendedLastCauseOrdinal);

        Assert.Equal(token, ServiceEnableFailureContract.CauseToken(cause));
        Assert.Matches("^[a-z][a-z_]*$", token);

        foreach (ServiceEnableFailureDisposition disposition in FailingDispositions())
        {
            int code = ServiceEnableFailureContract.ToExitCode(cause, disposition);

            // THE ORDINAL TRAP. An un-repointed ExtendedLastCauseOrdinal makes
            // exactly this assertion fail, because the new ordinals fall outside
            // the encode range and collapse onto the contradictory code.
            Assert.NotEqual(ServiceEnableFailureContract.UnclassifiedExitCode, code);
            Assert.NotEqual(ServiceEnableFailureContract.SuccessExitCode, code);
            Assert.True(ServiceEnableFailureContract.IsInDocumentedBand(code));

            ServiceEnableFailureClassification back = ServiceEnableFailureContract.FromExitCode(code);
            Assert.Equal(cause, back.Cause);
            Assert.Equal(disposition, back.Disposition);
            Assert.False(back.IsCompleted);

            Assert.Equal(
                "service-enable outcome=" + token + " disposition="
                    + ServiceEnableFailureContract.DispositionToken(disposition),
                ServiceEnableFailureContract.FormatDiagnostic(cause, disposition));
        }
    }

    [Fact]
    public void The_retained_wide_status_cause_still_decodes_but_is_no_longer_produced()
    {
        // BACKWARD DECODING ONLY. An already-emitted service_status_document_refused
        // code must still decode to its own pair even though no live branch
        // emits it after cycle 75.
        Assert.Equal(
            "service_status_document_refused",
            ServiceEnableFailureContract.CauseToken(ServiceEnableFailureCause.ServiceStatusDocumentRefused));
        Assert.Equal(18, (int)ServiceEnableFailureCause.ServiceStatusDocumentRefused);

        foreach (ServiceEnableFailureDisposition disposition in FailingDispositions())
        {
            int code = ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.ServiceStatusDocumentRefused, disposition);
            ServiceEnableFailureClassification back = ServiceEnableFailureContract.FromExitCode(code);
            Assert.Equal(ServiceEnableFailureCause.ServiceStatusDocumentRefused, back.Cause);
            Assert.Equal(disposition, back.Disposition);
        }

        // Its cycle-74 numeric mappings are UNCHANGED by cycle 75.
        Assert.Equal(
            318,
            ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.ServiceStatusDocumentRefused,
                ServiceEnableFailureDisposition.RefusedBeforeMutation));
        Assert.Equal(
            418,
            ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.ServiceStatusDocumentRefused,
                ServiceEnableFailureDisposition.Compensated));
        Assert.Equal(
            518,
            ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.ServiceStatusDocumentRefused,
                ServiceEnableFailureDisposition.RecoveryRequired));

        // And every OTHER cycle-74 cause keeps its EXACT numeric mapping too.
        var pinned = new Dictionary<ServiceEnableFailureCause, int>
        {
            [ServiceEnableFailureCause.ServiceStartRefused] = 15,
            [ServiceEnableFailureCause.ServiceRunningTimeout] = 16,
            [ServiceEnableFailureCause.ServiceProcessIdentityRefused] = 17,
            [ServiceEnableFailureCause.ServiceStatusDocumentRefused] = 18,
            [ServiceEnableFailureCause.ServiceHeartbeatRefused] = 19,
            [ServiceEnableFailureCause.ServiceForbiddenChildRefused] = 20,
            [ServiceEnableFailureCause.ServiceStartLogonRefused] = 21,
            [ServiceEnableFailureCause.ServiceStartAccessRefused] = 22,
            [ServiceEnableFailureCause.ServiceStartBinaryUnavailable] = 23,
            [ServiceEnableFailureCause.ServiceStartDependencyRefused] = 24,
            [ServiceEnableFailureCause.ServiceStartRequestTimeout] = 25,
        };

        foreach (KeyValuePair<ServiceEnableFailureCause, int> row in pinned)
        {
            Assert.Equal(row.Value, (int)row.Key);
            Assert.Equal(
                300 + row.Value,
                ServiceEnableFailureContract.ToExitCode(
                    row.Key, ServiceEnableFailureDisposition.RefusedBeforeMutation));
            Assert.Equal(
                400 + row.Value,
                ServiceEnableFailureContract.ToExitCode(
                    row.Key, ServiceEnableFailureDisposition.Compensated));
            Assert.Equal(
                500 + row.Value,
                ServiceEnableFailureContract.ToExitCode(
                    row.Key, ServiceEnableFailureDisposition.RecoveryRequired));
        }
    }

    [Fact]
    public void The_extension_last_ordinal_constant_tracks_the_final_declared_cause()
    {
        // THE SINGLE MOST IMPORTANT ASSERTION IN THIS FILE. Both the encode range
        // and the decode slot-membership test are bounded by this constant. If a
        // cause is appended and the constant is not repointed, the new ordinals
        // fall outside both and the whole vocabulary extension is inert.
        int highestDeclared = Enum.GetValues<ServiceEnableFailureCause>().Cast<int>().Max();

        Assert.Equal(highestDeclared, ServiceEnableFailureContract.ExtendedLastCauseOrdinal);

        // THE BINDING INVARIANT. While it holds, two extension regions are
        // ARITHMETICALLY INCAPABLE of overlapping.
        Assert.True(
            ServiceEnableFailureContract.ExtendedLastCauseOrdinal
                < ServiceEnableFailureContract.ExtendedRegionStride,
            "the extension regions can now overlap: ExtendedLastCauseOrdinal="
                + ServiceEnableFailureContract.ExtendedLastCauseOrdinal
                + " stride=" + ServiceEnableFailureContract.ExtendedRegionStride);

        // The extension region starts exactly where the FROZEN legacy region
        // ends, so no ordinal can fall between the two and be silently dropped.
        Assert.Equal(
            ServiceEnableFailureContract.LastCauseOrdinal + 1,
            ServiceEnableFailureContract.ExtendedFirstCauseOrdinal);
        Assert.Equal(10, ServiceEnableFailureContract.LastCauseOrdinal);
        Assert.Equal(11, ServiceEnableFailureContract.ExtendedFirstCauseOrdinal);

        // Every real cause is in exactly ONE of the two regions.
        foreach (ServiceEnableFailureCause cause in RealCauses())
        {
            int ordinal = (int)cause;
            bool legacy =
                ordinal >= ServiceEnableFailureContract.FirstCauseOrdinal
                && ordinal <= ServiceEnableFailureContract.LastCauseOrdinal;
            bool extension =
                ordinal >= ServiceEnableFailureContract.ExtendedFirstCauseOrdinal
                && ordinal <= ServiceEnableFailureContract.ExtendedLastCauseOrdinal;

            Assert.True(legacy ^ extension, cause + " is in neither region, or in both");
        }
    }

    [Theory]
    [MemberData(nameof(CycleSeventyFourTokens))]
    public void Every_cycle_74_cause_ordinal_is_inside_the_extension_region(
        ServiceEnableFailureCause cause, string token)
    {
        int ordinal = (int)cause;

        Assert.InRange(
            ordinal,
            ServiceEnableFailureContract.ExtendedFirstCauseOrdinal,
            ServiceEnableFailureContract.ExtendedLastCauseOrdinal);

        // It is genuinely NEW: past every ordinal the frozen legacy region can
        // represent, and past every cycle-71 ordinal.
        Assert.True(ordinal > (int)ServiceEnableFailureCause.Unavailable);
        Assert.True(ordinal > (int)ServiceEnableFailureCause.ProgramFilesProtectionRefused);

        Assert.Equal(token, ServiceEnableFailureContract.CauseToken(cause));
        Assert.Matches("^[a-z][a-z_]*$", token);

        foreach (ServiceEnableFailureDisposition disposition in FailingDispositions())
        {
            int code = ServiceEnableFailureContract.ToExitCode(cause, disposition);

            // It must NOT collapse onto the contradictory code - that is exactly
            // what an un-repointed ExtendedLastCauseOrdinal would produce.
            Assert.NotEqual(ServiceEnableFailureContract.UnclassifiedExitCode, code);
            Assert.NotEqual(ServiceEnableFailureContract.SuccessExitCode, code);
            Assert.True(ServiceEnableFailureContract.IsInDocumentedBand(code));

            ServiceEnableFailureClassification back = ServiceEnableFailureContract.FromExitCode(code);
            Assert.Equal(cause, back.Cause);

            // A finer CAUSE never changes DISPOSITION semantics.
            Assert.Equal(disposition, back.Disposition);
            Assert.False(back.IsCompleted);

            // The bounded line an operator reads.
            Assert.Equal(
                "service-enable outcome=" + token + " disposition="
                    + ServiceEnableFailureContract.DispositionToken(disposition),
                ServiceEnableFailureContract.FormatDiagnostic(cause, disposition));
        }
    }

    [Fact]
    public void The_first_and_last_extension_ordinals_encode_and_decode_correctly()
    {
        var first = (ServiceEnableFailureCause)ServiceEnableFailureContract.ExtendedFirstCauseOrdinal;
        var last = (ServiceEnableFailureCause)ServiceEnableFailureContract.ExtendedLastCauseOrdinal;

        // Both boundary ordinals are REAL declared members, not reserved slots.
        Assert.True(Enum.IsDefined(first));
        Assert.True(Enum.IsDefined(last));
        Assert.NotEqual(first, last);

        (int Base, ServiceEnableFailureDisposition Disposition)[] regions =
        {
            (ServiceEnableFailureContract.ExtendedRefusedBeforeMutationBase,
                ServiceEnableFailureDisposition.RefusedBeforeMutation),
            (ServiceEnableFailureContract.ExtendedCompensatedBase,
                ServiceEnableFailureDisposition.Compensated),
            (ServiceEnableFailureContract.ExtendedRecoveryRequiredBase,
                ServiceEnableFailureDisposition.RecoveryRequired),
        };

        foreach ((int regionBase, ServiceEnableFailureDisposition disposition) in regions)
        {
            foreach (ServiceEnableFailureCause boundary in new[] { first, last })
            {
                int expected = regionBase + (int)boundary;
                Assert.Equal(expected, ServiceEnableFailureContract.ToExitCode(boundary, disposition));

                ServiceEnableFailureClassification back =
                    ServiceEnableFailureContract.FromExitCode(expected);
                Assert.Equal(boundary, back.Cause);
                Assert.Equal(disposition, back.Disposition);
            }
        }

        // The three region bases are still a full stride apart.
        Assert.Equal(
            ServiceEnableFailureContract.ExtendedRefusedBeforeMutationBase
                + ServiceEnableFailureContract.ExtendedRegionStride,
            ServiceEnableFailureContract.ExtendedCompensatedBase);
        Assert.Equal(
            ServiceEnableFailureContract.ExtendedCompensatedBase
                + ServiceEnableFailureContract.ExtendedRegionStride,
            ServiceEnableFailureContract.ExtendedRecoveryRequiredBase);
    }

    [Fact]
    public void No_extension_slot_overlaps_any_legacy_mapping()
    {
        // Every code the FROZEN legacy region can emit, plus its whole window.
        var legacyCodes = new HashSet<int>();
        foreach (object[] row in LegacyPins())
        {
            legacyCodes.Add((int)row[2]);
        }

        ServiceEnableFailureCause[] extensionCauses = RealCauses()
            .Where(c => (int)c >= ServiceEnableFailureContract.ExtendedFirstCauseOrdinal)
            .ToArray();

        // DERIVED, never hand-typed: every real cause at or past the first
        // extension ordinal. An earlier version pinned the literal 15 and would
        // have had to be edited by hand every time a cause was appended.
        Assert.Equal(
            ServiceEnableFailureContract.ExtendedLastCauseOrdinal
                - ServiceEnableFailureContract.ExtendedFirstCauseOrdinal + 1,
            extensionCauses.Length);
        Assert.True(extensionCauses.Length > 0, "no extension cause exists, so nothing was proven");

        var extensionCodes = new HashSet<int>();
        foreach (ServiceEnableFailureCause cause in extensionCauses)
        {
            foreach (ServiceEnableFailureDisposition disposition in FailingDispositions())
            {
                int code = ServiceEnableFailureContract.ToExitCode(cause, disposition);

                Assert.DoesNotContain(code, legacyCodes);
                Assert.False(
                    code >= ServiceEnableFailureContract.BandFirstExitCode
                        && code <= ServiceEnableFailureContract.BandLastExitCode,
                    cause + "/" + disposition + " landed inside the frozen legacy window: " + code);

                Assert.True(extensionCodes.Add(code), "extension code " + code + " is emitted twice");
            }
        }

        Assert.Equal(extensionCauses.Length * FailingDispositions().Length, extensionCodes.Count);
        Assert.Empty(extensionCodes.Intersect(legacyCodes));
    }

    [Fact]
    public void Unused_extension_slots_remain_fail_closed()
    {
        var assigned = new HashSet<int> { ServiceEnableFailureContract.SuccessExitCode };
        foreach (ServiceEnableFailureCause cause in RealCauses())
        {
            foreach (ServiceEnableFailureDisposition disposition in FailingDispositions())
            {
                assigned.Add(ServiceEnableFailureContract.ToExitCode(cause, disposition));
            }
        }

        int[] bases =
        {
            ServiceEnableFailureContract.ExtendedRefusedBeforeMutationBase,
            ServiceEnableFailureContract.ExtendedCompensatedBase,
            ServiceEnableFailureContract.ExtendedRecoveryRequiredBase,
        };

        int slotsExamined = 0;
        foreach (int regionBase in bases)
        {
            // The WHOLE region, including the reserved head below the first
            // ordinal and the reserved tail above the last one.
            for (int offset = 0; offset < ServiceEnableFailureContract.ExtendedRegionStride; offset++)
            {
                int code = regionBase + offset;
                if (assigned.Contains(code))
                {
                    continue;
                }

                slotsExamined++;
                Assert.False(ServiceEnableFailureContract.IsInDocumentedBand(code));

                ServiceEnableFailureClassification back = ServiceEnableFailureContract.FromExitCode(code);
                Assert.Equal(ServiceEnableFailureCause.Unavailable, back.Cause);
                Assert.Equal(ServiceEnableFailureDisposition.RecoveryRequired, back.Disposition);
                Assert.False(back.IsCompleted);
            }
        }

        // DERIVED, never hand-typed: three regions of one stride each, minus the
        // slots the EXTENSION causes actually occupy. The ten LEGACY causes emit
        // into 151-180 and occupy no extension slot at all - an earlier version
        // of this line wrongly subtracted them and expected 225 instead of 255.
        int extensionCauseCount = RealCauses()
            .Count(c => (int)c >= ServiceEnableFailureContract.ExtendedFirstCauseOrdinal);

        Assert.Equal(
            (bases.Length * ServiceEnableFailureContract.ExtendedRegionStride)
                - (extensionCauseCount * FailingDispositions().Length),
            slotsExamined);
        Assert.True(slotsExamined > 0, "no reserved slot was examined, so nothing was proven");
    }

    [Fact]
    public void The_exhaustive_enum_product_is_the_only_source_of_truth_for_the_mapping()
    {
        // DERIVED FROM THE ENUMS, NEVER FROM A HAND-MAINTAINED LIST. A future
        // member appended without mapping support fails HERE: it encodes to the
        // contradictory code, which does not round-trip back to its own pair.
        ServiceEnableFailureCause[] causes = Enum.GetValues<ServiceEnableFailureCause>();
        ServiceEnableFailureDisposition[] dispositions = Enum.GetValues<ServiceEnableFailureDisposition>();

        var codeToPair = new Dictionary<int, string>();
        int representablePairs = 0;

        foreach (ServiceEnableFailureCause cause in causes)
        {
            foreach (ServiceEnableFailureDisposition disposition in dispositions)
            {
                int code = ServiceEnableFailureContract.ToExitCode(cause, disposition);

                bool isSuccessPair =
                    cause == ServiceEnableFailureCause.None
                    && disposition == ServiceEnableFailureDisposition.Completed;

                // Only the ONE success pair is zero. Every other pair is either a
                // documented failure pair or the contradictory code.
                Assert.Equal(isSuccessPair, code == ServiceEnableFailureContract.SuccessExitCode);

                bool documentedPair =
                    isSuccessPair
                    || (cause != ServiceEnableFailureCause.None
                        && disposition != ServiceEnableFailureDisposition.None
                        && disposition != ServiceEnableFailureDisposition.Completed);

                if (!documentedPair)
                {
                    // A CONTRADICTION - a cause with no disposition, a Completed
                    // disposition carrying a cause, or None paired with a
                    // failure - is the one code that is allowed to be shared.
                    Assert.Equal(ServiceEnableFailureContract.UnclassifiedExitCode, code);
                    continue;
                }

                representablePairs++;

                string pair = cause + "/" + disposition;
                Assert.False(
                    codeToPair.ContainsKey(code),
                    "code " + code + " is claimed by " + pair + " and by "
                        + (codeToPair.TryGetValue(code, out string? other) ? other : "?")
                        + " - this is what an un-repointed ExtendedLastCauseOrdinal looks like");
                codeToPair[code] = pair;

                Assert.True(
                    ServiceEnableFailureContract.IsInDocumentedBand(code) || isSuccessPair,
                    pair + " produced undocumented code " + code);

                // EXACT ROUND TRIP. This is the assertion an unmapped appended
                // member cannot survive.
                ServiceEnableFailureClassification back = ServiceEnableFailureContract.FromExitCode(code);
                Assert.Equal(cause, back.Cause);
                Assert.Equal(disposition, back.Disposition);
                Assert.Equal(isSuccessPair, back.IsCompleted);

                // Every cause carries a DISTINCT, non-default token unless it is
                // genuinely the Unavailable member.
                string token = ServiceEnableFailureContract.CauseToken(cause);
                if (cause != ServiceEnableFailureCause.Unavailable)
                {
                    Assert.NotEqual("unavailable", token);
                }
            }
        }

        // DERIVED on both sides: one success pair plus every real cause crossed
        // with every failing disposition.
        Assert.Equal(1 + (RealCauses().Length * FailingDispositions().Length), representablePairs);
        Assert.Equal(representablePairs, codeToPair.Count);

        // A member added without a token collapses onto the fail-closed default
        // and shrinks the distinct count.
        Assert.Equal(
            causes.Length,
            causes.Select(ServiceEnableFailureContract.CauseToken).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            dispositions.Length,
            dispositions.Select(ServiceEnableFailureContract.DispositionToken)
                .Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_legacy_and_cycle_71_vocabulary_is_unchanged_by_cycle_74()
    {
        // Retained for backward compatibility: an ALREADY-EMITTED
        // startability_refused code must still decode, even though no live
        // branch produces it after this cycle.
        Assert.Equal(
            "startability_refused",
            ServiceEnableFailureContract.CauseToken(ServiceEnableFailureCause.StartabilityRefused));
        Assert.Equal(9, (int)ServiceEnableFailureCause.StartabilityRefused);

        foreach (ServiceEnableFailureDisposition disposition in FailingDispositions())
        {
            int code = ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.StartabilityRefused, disposition);
            ServiceEnableFailureClassification back = ServiceEnableFailureContract.FromExitCode(code);
            Assert.Equal(ServiceEnableFailureCause.StartabilityRefused, back.Cause);
            Assert.Equal(disposition, back.Disposition);
        }

        // Unavailable is still the ONE fail-closed member and still owns its token.
        Assert.Equal(
            "unavailable", ServiceEnableFailureContract.CauseToken(ServiceEnableFailureCause.Unavailable));
        Assert.Equal(10, (int)ServiceEnableFailureCause.Unavailable);

        // The cycle-71 members keep their exact ordinals and tokens.
        Assert.Equal(11, (int)ServiceEnableFailureCause.ServiceCreationRefused);
        Assert.Equal(12, (int)ServiceEnableFailureCause.ServiceSidConfigurationRefused);
        Assert.Equal(13, (int)ServiceEnableFailureCause.ServiceSidResolutionRefused);
        Assert.Equal(14, (int)ServiceEnableFailureCause.ProgramFilesProtectionRefused);
        Assert.Equal(
            311,
            ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.ServiceCreationRefused,
                ServiceEnableFailureDisposition.RefusedBeforeMutation));
        Assert.Equal(
            414,
            ServiceEnableFailureContract.ToExitCode(
                ServiceEnableFailureCause.ProgramFilesProtectionRefused,
                ServiceEnableFailureDisposition.Compensated));

        // The machine-data cause is untouched by the startability split.
        Assert.Equal(
            "machine_data_protection_refused",
            ServiceEnableFailureContract.CauseToken(
                ServiceEnableFailureCause.MachineDataProtectionRefused));
        Assert.Equal(8, (int)ServiceEnableFailureCause.MachineDataProtectionRefused);
    }

    // =======================================================================
    // CYCLE 75 - THE EXHAUSTIVE EXTERNAL-CONTRACT LOCK
    // =======================================================================
    //
    // WHY THIS REGION EXISTS SEPARATELY FROM EVERYTHING ABOVE. The tests above
    // prove the mapping is INTERNALLY CONSISTENT - total, injective, round
    // -tripping. Internal consistency is preserved by a renumbering that changes
    // every code at once, so it cannot by itself prove the OUTWARD numbers an
    // already-shipped operator or log parser depends on are unchanged.
    //
    // These tests pin the ABSOLUTE integers with hand-written literals, and then
    // close the set: the union of every hand-written pin table must be EXACTLY
    // the set of declared causes. A future cause appended without a pin does not
    // merely go unproven - it FAILS the closure test below.

    /// <summary>
    /// EVERY cycle-71 pair, pinned to its EXACT numeric code. Hand-written
    /// literals: a pin derived from the code under test would pin nothing.
    /// </summary>
    public static TheoryData<ServiceEnableFailureCause, ServiceEnableFailureDisposition, int>
        CycleSeventyOnePins() => BuildPins(new[]
        {
            (ServiceEnableFailureCause.ServiceCreationRefused, 311, 411, 511),
            (ServiceEnableFailureCause.ServiceSidConfigurationRefused, 312, 412, 512),
            (ServiceEnableFailureCause.ServiceSidResolutionRefused, 313, 413, 513),
            (ServiceEnableFailureCause.ProgramFilesProtectionRefused, 314, 414, 514),
        });

    /// <summary>EVERY cycle-74 pair, pinned to its EXACT numeric code.</summary>
    public static TheoryData<ServiceEnableFailureCause, ServiceEnableFailureDisposition, int>
        CycleSeventyFourPins() => BuildPins(new[]
        {
            (ServiceEnableFailureCause.ServiceStartRefused, 315, 415, 515),
            (ServiceEnableFailureCause.ServiceRunningTimeout, 316, 416, 516),
            (ServiceEnableFailureCause.ServiceProcessIdentityRefused, 317, 417, 517),
            (ServiceEnableFailureCause.ServiceStatusDocumentRefused, 318, 418, 518),
            (ServiceEnableFailureCause.ServiceHeartbeatRefused, 319, 419, 519),
            (ServiceEnableFailureCause.ServiceForbiddenChildRefused, 320, 420, 520),
            (ServiceEnableFailureCause.ServiceStartLogonRefused, 321, 421, 521),
            (ServiceEnableFailureCause.ServiceStartAccessRefused, 322, 422, 522),
            (ServiceEnableFailureCause.ServiceStartBinaryUnavailable, 323, 423, 523),
            (ServiceEnableFailureCause.ServiceStartDependencyRefused, 324, 424, 524),
            (ServiceEnableFailureCause.ServiceStartRequestTimeout, 325, 425, 525),
        });

    /// <summary>EVERY cycle-75 pair, pinned to its EXACT numeric code.</summary>
    public static TheoryData<ServiceEnableFailureCause, ServiceEnableFailureDisposition, int>
        CycleSeventyFivePins() => BuildPins(new[]
        {
            (ServiceEnableFailureCause.ServiceStatusMissingTimeout, 326, 426, 526),
            (ServiceEnableFailureCause.ServiceStatusStartingTimeout, 327, 427, 527),
            (ServiceEnableFailureCause.ServiceStatusUnreadable, 328, 428, 528),
            (ServiceEnableFailureCause.ServiceStatusMalformed, 329, 429, 529),
            (ServiceEnableFailureCause.ServiceStatusWrongContext, 330, 430, 530),
            (ServiceEnableFailureCause.ServiceStatusStopped, 331, 431, 531),
            (ServiceEnableFailureCause.ServiceStatusFailed, 332, 432, 532),
            (ServiceEnableFailureCause.ServiceStatusUnknownState, 333, 433, 533),
        });

    private static TheoryData<ServiceEnableFailureCause, ServiceEnableFailureDisposition, int> BuildPins(
        (ServiceEnableFailureCause Cause, int Refused, int Compensated, int Recovery)[] rows)
    {
        var data = new TheoryData<ServiceEnableFailureCause, ServiceEnableFailureDisposition, int>();
        foreach ((ServiceEnableFailureCause cause, int refused, int compensated, int recovery) in rows)
        {
            data.Add(cause, ServiceEnableFailureDisposition.RefusedBeforeMutation, refused);
            data.Add(cause, ServiceEnableFailureDisposition.Compensated, compensated);
            data.Add(cause, ServiceEnableFailureDisposition.RecoveryRequired, recovery);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CycleSeventyOnePins))]
    [MemberData(nameof(CycleSeventyFourPins))]
    [MemberData(nameof(CycleSeventyFivePins))]
    public void Every_extension_pair_keeps_its_exact_pinned_exit_code(
        ServiceEnableFailureCause cause, ServiceEnableFailureDisposition disposition, int pinned)
    {
        Assert.Equal(pinned, ServiceEnableFailureContract.ToExitCode(cause, disposition));

        ServiceEnableFailureClassification back = ServiceEnableFailureContract.FromExitCode(pinned);
        Assert.Equal(cause, back.Cause);
        Assert.Equal(disposition, back.Disposition);
        Assert.False(back.IsCompleted);
        Assert.True(ServiceEnableFailureContract.IsInDocumentedBand(pinned));
    }

    [Fact]
    public void The_hand_written_pin_tables_cover_every_declared_cause_with_nothing_left_over()
    {
        // THE CLOSURE TEST. This is the assertion a future cause appended
        // WITHOUT a hand-written pin cannot survive. Everything else in this
        // file derives its expectations from the enum, so an unmapped member
        // would quietly widen every derived count and still pass.
        var pinnedCauses = new HashSet<ServiceEnableFailureCause>();
        var pinnedCodes = new Dictionary<int, string>();

        foreach (TheoryData<ServiceEnableFailureCause, ServiceEnableFailureDisposition, int> table
                 in new[] { LegacyPins(), CycleSeventyOnePins(), CycleSeventyFourPins(), CycleSeventyFivePins() })
        {
            foreach (object?[] row in table)
            {
                var cause = (ServiceEnableFailureCause)row[0]!;
                var disposition = (ServiceEnableFailureDisposition)row[1]!;
                int code = (int)row[2]!;

                pinnedCauses.Add(cause);

                string pair = cause + "/" + disposition;
                Assert.False(
                    pinnedCodes.ContainsKey(code),
                    "pinned code " + code + " is claimed by " + pair + " and by "
                        + (pinnedCodes.TryGetValue(code, out string? other) ? other : "?"));
                pinnedCodes[code] = pair;
            }
        }

        // DERIVED FROM THE ENUM on one side, HAND-WRITTEN on the other. The two
        // sets must be equal in BOTH directions.
        var declared = new HashSet<ServiceEnableFailureCause>(RealCauses());

        Assert.Empty(declared.Except(pinnedCauses));
        Assert.Empty(pinnedCauses.Except(declared));
        Assert.Equal(declared.Count, pinnedCauses.Count);

        // Three dispositions per cause, and no pinned code shared by two pairs.
        Assert.Equal(declared.Count * FailingDispositions().Length, pinnedCodes.Count);
        Assert.True(declared.Count > 0, "no cause was examined, so nothing was proven");
    }

    [Fact]
    public void Every_disposition_token_is_pinned_to_its_exact_literal()
    {
        Assert.Equal(
            "refused_before_mutation",
            ServiceEnableFailureContract.DispositionToken(
                ServiceEnableFailureDisposition.RefusedBeforeMutation));
        Assert.Equal(
            "compensated",
            ServiceEnableFailureContract.DispositionToken(ServiceEnableFailureDisposition.Compensated));
        Assert.Equal(
            "recovery_required",
            ServiceEnableFailureContract.DispositionToken(
                ServiceEnableFailureDisposition.RecoveryRequired));
        Assert.Equal(
            "completed",
            ServiceEnableFailureContract.DispositionToken(ServiceEnableFailureDisposition.Completed));
        Assert.Equal(
            "none",
            ServiceEnableFailureContract.DispositionToken(ServiceEnableFailureDisposition.None));

        // FAIL CLOSED. An undefined cast never reads as a success token.
        Assert.Equal(
            "none", ServiceEnableFailureContract.DispositionToken((ServiceEnableFailureDisposition)9999));

        // DERIVED: exactly five declared members, so a sixth appended without a
        // literal above fails the pinned-count check.
        Assert.Equal(5, Enum.GetValues<ServiceEnableFailureDisposition>().Length);
    }

    /// <summary>
    /// EVERY channel outcome paired with the disposition the helper exit code
    /// must report, and whether the server SPOKE it. Hand-written on purpose.
    /// </summary>
    public static TheoryData<ServiceInitiatingUserIdentityChannelOutcome, ServiceEnableFailureDisposition, bool>
        ChannelOutcomeContract() => new()
    {
        { ServiceInitiatingUserIdentityChannelOutcome.Unspecified, ServiceEnableFailureDisposition.RecoveryRequired, false },
        { ServiceInitiatingUserIdentityChannelOutcome.Completed, ServiceEnableFailureDisposition.Completed, true },
        { ServiceInitiatingUserIdentityChannelOutcome.UnsupportedPlatform, ServiceEnableFailureDisposition.RefusedBeforeMutation, false },
        { ServiceInitiatingUserIdentityChannelOutcome.EndpointUnavailable, ServiceEnableFailureDisposition.RefusedBeforeMutation, false },
        { ServiceInitiatingUserIdentityChannelOutcome.NoClientConnected, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.MalformedRequest, ServiceEnableFailureDisposition.RefusedBeforeMutation, false },
        { ServiceInitiatingUserIdentityChannelOutcome.ChallengeMismatch, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.ClientIdentityUnavailable, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.AcknowledgementFailed, ServiceEnableFailureDisposition.RecoveryRequired, true },
        { ServiceInitiatingUserIdentityChannelOutcome.AcknowledgementNotReceived, ServiceEnableFailureDisposition.RefusedBeforeMutation, false },
        { ServiceInitiatingUserIdentityChannelOutcome.InitiatorNotBound, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.ChallengeNotDelivered, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.TransactionCompensated, ServiceEnableFailureDisposition.Compensated, true },
        { ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired, ServiceEnableFailureDisposition.RecoveryRequired, true },
        { ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRecoveryRequired, ServiceEnableFailureDisposition.RecoveryRequired, true },

        // CYCLE 89. The bounded payload phase. Every one is decided before the
        // preflight, the anchor write and Apply, so all nine are pre-mutation.
        // Two are NOT server-spoken: PayloadReadyNotDelivered is the case where
        // the server could not write at all, and PayloadNotOffered is produced
        // only by the client when no payload-ready line arrived.
        { ServiceInitiatingUserIdentityChannelOutcome.AnchorRequiredButUnavailable, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.PayloadReadyNotDelivered, ServiceEnableFailureDisposition.RefusedBeforeMutation, false },
        { ServiceInitiatingUserIdentityChannelOutcome.PayloadFrameMalformed, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.PayloadOversized, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.PayloadDigestMismatch, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.PayloadNotUtf8, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.PayloadRefused, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.InstallationOwnershipMismatch, ServiceEnableFailureDisposition.RefusedBeforeMutation, true },
        { ServiceInitiatingUserIdentityChannelOutcome.PayloadNotOffered, ServiceEnableFailureDisposition.RefusedBeforeMutation, false },
    };

    [Theory]
    [MemberData(nameof(ChannelOutcomeContract))]
    public void Every_channel_outcome_keeps_its_exact_disposition_and_authority(
        ServiceInitiatingUserIdentityChannelOutcome outcome,
        ServiceEnableFailureDisposition expectedDisposition,
        bool serverAuthoritative)
    {
        Assert.Equal(
            expectedDisposition, ServiceEnableFailureContract.ExpectedDispositionFor(outcome));
        Assert.Equal(
            serverAuthoritative, ServiceEnableFailureContract.IsServerAuthoritative(outcome));

        // Only the ONE completed outcome may carry the ONE success disposition.
        Assert.Equal(
            outcome == ServiceInitiatingUserIdentityChannelOutcome.Completed,
            expectedDisposition == ServiceEnableFailureDisposition.Completed);
    }

    [Fact]
    public void The_channel_outcome_table_covers_every_declared_member_with_nothing_left_over()
    {
        // THE SECOND CLOSURE TEST. A channel outcome appended without an entry
        // above falls to the RecoveryRequired default in ExpectedDispositionFor
        // and to false in IsServerAuthoritative - both plausible - so only this
        // set-equality assertion can catch it.
        var covered = new HashSet<ServiceInitiatingUserIdentityChannelOutcome>();
        foreach (object?[] row in ChannelOutcomeContract())
        {
            var outcome = (ServiceInitiatingUserIdentityChannelOutcome)row[0]!;
            Assert.True(covered.Add(outcome), outcome + " is listed twice");
        }

        var declared = new HashSet<ServiceInitiatingUserIdentityChannelOutcome>(
            Enum.GetValues<ServiceInitiatingUserIdentityChannelOutcome>());

        Assert.Empty(declared.Except(covered));
        Assert.Empty(covered.Except(declared));
        Assert.Equal(declared.Count, covered.Count);

        // FAIL CLOSED for a value cast from outside the vocabulary entirely.
        Assert.Equal(
            ServiceEnableFailureDisposition.RecoveryRequired,
            ServiceEnableFailureContract.ExpectedDispositionFor(
                (ServiceInitiatingUserIdentityChannelOutcome)9999));
        Assert.False(
            ServiceEnableFailureContract.IsServerAuthoritative(
                (ServiceInitiatingUserIdentityChannelOutcome)9999));
    }

    [Fact]
    public void The_bounded_diagnostic_shape_is_identical_for_every_declared_pair()
    {
        // FORMATTING IS AN EXTERNAL CONTRACT. Cycle 75 added eight causes and
        // must not have changed the shape of the ONE line an operator reads.
        foreach (ServiceEnableFailureCause cause in Enum.GetValues<ServiceEnableFailureCause>())
        {
            foreach (ServiceEnableFailureDisposition disposition
                     in Enum.GetValues<ServiceEnableFailureDisposition>())
            {
                string line = ServiceEnableFailureContract.FormatDiagnostic(cause, disposition);

                Assert.Equal(
                    "service-enable outcome=" + ServiceEnableFailureContract.CauseToken(cause)
                        + " disposition=" + ServiceEnableFailureContract.DispositionToken(disposition),
                    line);

                Assert.Equal(3, line.Split(' ').Length);
                Assert.Equal(line, line.ToLowerInvariant());
                Assert.DoesNotContain('\r', line);
                Assert.DoesNotContain('\n', line);
                Assert.DoesNotContain('\t', line);
                Assert.DoesNotContain('/', line);
                Assert.DoesNotContain('\\', line);
                Assert.DoesNotContain(':', line);
                Assert.DoesNotContain("s-1-", line, StringComparison.Ordinal);
                Assert.Matches(
                    "^service-enable outcome=[a-z][a-z_]* disposition=[a-z][a-z_]*$", line);
            }
        }
    }
}
