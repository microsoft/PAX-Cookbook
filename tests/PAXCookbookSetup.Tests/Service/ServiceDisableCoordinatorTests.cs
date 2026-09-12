using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using PAXCookbook.ServiceAdminHelper.Signing;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 63RR - NON-ELEVATED SERVICE-DISABLE COORDINATOR (Setup side)
// ===========================================================================
//
// SCOPE, stated plainly. Every test here uses FAKES. Nothing in this file
// elevates, triggers a UAC prompt, uses runas, starts ANY process, resolves or
// launches the real service administrative helper, removes anything from
// Program Files, writes or deletes %ProgramData%, queries or mutates the
// Service Control Manager, stops or deletes a service, opens a certificate
// store, private key or credential vault, reads or writes an ACL or the
// registry, opens a socket, runs PAX or starts a Bake. The coordinator's REAL
// launcher, REAL helper locator and REAL signing gate are never constructed.
//
// WHAT THIS CLASS OWNS. The SETUP SIDE of the disable boundary: the closed
// initiator grammar, the signing gate, fixed sibling helper resolution, the
// exact closed launch arguments, UAC decline, the "no client before a
// successful launch" rule, the bounded wait and exit mapping, the mapping of an
// owner conflict onto RecoveryRequired, and the proof that the grammar cannot
// express an owner override. The elevated removal transaction lives in the
// helper assembly and is proven by ServiceStartAndDisableTransactionTests.
//
// DISCLOSED LIMITATION, recorded once. These tests run at a single integrity
// level and under a single account, so they cannot prove what Windows does when
// a DIFFERENT administrator approves the prompt. What they prove is the part
// that is account-independent: exactly what the coordinator launches, exactly
// what crosses the command line, and exactly when it refuses.
public sealed class ServiceDisableCoordinatorTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);

    private static readonly ServiceInitiatorProcessFacts SampleInitiator = new(8765, 133_100_000_000_000_000L);

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

    private static FakeLauncher StartedLauncher(int exitCode = SetupExitCodes.Ok) =>
        new(() => ServiceAnchorElevatedLaunch.Started(new FakeElevatedProcess(exitCode, alreadyExited: false)));

    private static ServiceDisableCoordinatorOutcome Run(
        string[] argv,
        IServiceEnableHelperLocator? locator = null,
        IServiceEnableSigningGate? signing = null,
        IServiceAnchorElevatedLauncher? launcher = null,
        IServiceAnchorChannelClient? client = null,
        ServiceInitiatorProcessFacts? initiator = null) =>
        ServiceDisableCoordinator.Run(
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
    public void The_disable_initiator_verb_takes_exactly_itself_and_nothing_else()
    {
        Assert.True(ServiceDisableProtocol.TryParseInitiator(new[] { ServiceDisableVerbs.InitiatorVerb }));
        Assert.Equal(1, ServiceDisableVerbs.InitiatorTokenCount);
        Assert.Equal("service-disable", ServiceDisableVerbs.InitiatorVerb);
        Assert.Equal("service-disable-elevated", ServiceDisableVerbs.ElevatedHelperVerb);
    }

    [Fact]
    public void A_non_conforming_disable_initiator_argument_vector_is_refused()
    {
        var rejected = new[]
        {
            Array.Empty<string>(),
            new[] { "service-disable", "service-disable" },
            new[] { "service-disable", "--force" },
            new[] { "--force", "service-disable" },
            new[] { "service-disabled" },
            new[] { "service-disable-elevated" },
            new[] { "service-enable" },
            new[] { string.Empty },
            new[] { " service-disable" },
            new[] { "service-disable " },
        };

        foreach (string[] argv in rejected)
        {
            Assert.False(ServiceDisableProtocol.TryParseInitiator(argv));
            Assert.Equal(ServiceDisableCoordinatorOutcome.UsageRefused, Run(argv));
        }
    }

    [Fact]
    public void A_null_or_oversized_disable_token_is_refused_before_any_work()
    {
        Assert.False(ServiceDisableProtocol.TryParseInitiator(null));

        string oversized = new('a', ServiceEnableVerbs.MaxTokenLength + 1);
        Assert.False(ServiceDisableProtocol.TryParseInitiator(new[] { oversized }));

        var locator = new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath));
        var signing = new FakeSigningGate(ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed);
        var launcher = StartedLauncher();
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        Assert.Equal(
            ServiceDisableCoordinatorOutcome.UsageRefused,
            Run(new[] { oversized }, locator, signing, launcher, client));

        Assert.Equal(0, locator.Resolutions);
        Assert.Equal(0, signing.Queries);
        Assert.Empty(launcher.Launched);
        Assert.Equal(0, client.Requests);
    }

    // =======================================================================
    // DISABLE IS OWNER-BOUND AND THE GRAMMAR CANNOT EXPRESS AN OVERRIDE
    // =======================================================================

    [Fact]
    public void The_disable_grammar_has_no_owner_override_and_no_identity_parameter()
    {
        string endpoint = ServiceInitiatingUserIdentityChannelContract.NewEndpointName();
        string[] arguments = ServiceDisableProtocol.ComposeElevatedArguments(endpoint, SampleInitiator);

        Assert.Equal(ServiceDisableVerbs.ElevatedTokenCount, arguments.Length);
        Assert.Equal(7, arguments.Length);
        Assert.Equal(ServiceDisableVerbs.ElevatedHelperVerb, arguments[0]);
        Assert.Equal(ServiceEnableVerbs.EndpointOption, arguments[1]);
        Assert.Equal(ServiceEnableVerbs.InitiatorProcessIdOption, arguments[3]);
        Assert.Equal(ServiceEnableVerbs.InitiatorCreatedOption, arguments[5]);

        string joined = string.Join('\u0000', arguments);
        foreach (string forbidden in new[]
        {
            "--force", "--purge", "--all", "--owner", "--sid", "--user", "--takeover", "--override",
            "--yes", "--service", "--path", "--machine", "S-1-", "NT SERVICE", "PAXCookbookService",
            "ProgramData", "Program Files", "installation-anchor.json", "HKLM", "HKEY",
        })
        {
            Assert.DoesNotContain(forbidden, joined, StringComparison.OrdinalIgnoreCase);
        }

        // An extra token of ANY kind is refused; there is no room for a flag.
        Assert.False(ServiceDisableProtocol.TryParseElevated(
            arguments.Concat(new[] { "--force" }).ToArray(), out _));
    }

    [Theory]
    [InlineData(0, "service-disable-elevate")]
    [InlineData(0, "service-enable-elevated")]
    [InlineData(1, "--endpoints")]
    [InlineData(3, "--initiator-pids")]
    [InlineData(5, "--initiator-createds")]
    public void A_misspelled_disable_verb_or_option_is_refused(int index, string replacement)
    {
        string[] arguments = ServiceDisableProtocol.ComposeElevatedArguments(
            ServiceInitiatingUserIdentityChannelContract.NewEndpointName(), SampleInitiator);
        arguments[index] = replacement;

        Assert.False(ServiceDisableProtocol.TryParseElevated(arguments, out _));
    }

    [Theory]
    [InlineData(2, "not-an-endpoint")]
    [InlineData(4, "0")]
    [InlineData(4, "-1")]
    [InlineData(4, "0x10")]
    [InlineData(6, "0")]
    [InlineData(6, "-5")]
    public void A_malformed_disable_value_is_refused(int index, string replacement)
    {
        string[] arguments = ServiceDisableProtocol.ComposeElevatedArguments(
            ServiceInitiatingUserIdentityChannelContract.NewEndpointName(), SampleInitiator);
        arguments[index] = replacement;

        Assert.False(ServiceDisableProtocol.TryParseElevated(arguments, out _));
    }

    [Fact]
    public void The_canonical_disable_argument_vector_round_trips_through_the_closed_parser()
    {
        string endpoint = ServiceInitiatingUserIdentityChannelContract.NewEndpointName();
        Assert.True(ServiceDisableProtocol.TryParseElevated(
            ServiceDisableProtocol.ComposeElevatedArguments(endpoint, SampleInitiator),
            out ServiceEnableElevatedArguments? parsed));

        Assert.NotNull(parsed);
        Assert.Equal(endpoint, parsed!.EndpointName);
        Assert.Equal(SampleInitiator.ProcessId, parsed.Initiator.ProcessId);
        Assert.Equal(SampleInitiator.CreationFileTime, parsed.Initiator.CreationFileTime);

        // The ENABLE parser must not accept a DISABLE vector, or one verb could
        // silently perform the other operation.
        Assert.False(ServiceEnableProtocol.TryParseElevated(
            ServiceDisableProtocol.ComposeElevatedArguments(endpoint, SampleInitiator), out _));
    }

    // =======================================================================
    // THE SIGNING GATE RUNS BEFORE ANYTHING IS RESOLVED OR LAUNCHED
    // =======================================================================

    [Fact]
    public void Only_the_explicit_prerelease_allowance_permits_a_disable()
    {
        foreach (ServiceHelperSigningPolicyState state in Enum.GetValues<ServiceHelperSigningPolicyState>())
        {
            if (state == ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed)
            {
                continue;
            }

            var locator = new FakeHelperLocator(ServiceAdminHelperLocationResult.Resolved(SampleHelperPath));
            var launcher = StartedLauncher();
            var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

            Assert.Equal(
                ServiceDisableCoordinatorOutcome.SigningPolicyRefused,
                Run(
                    new[] { ServiceDisableVerbs.InitiatorVerb },
                    locator,
                    new FakeSigningGate(state),
                    launcher,
                    client));

            // A build that may not disable a service NEVER reaches a UAC prompt.
            Assert.Equal(0, locator.Resolutions);
            Assert.Empty(launcher.Launched);
            Assert.Equal(0, client.Requests);
        }
    }

    // =======================================================================
    // FIXED SIBLING HELPER RESOLUTION AND THE CLOSED LAUNCH
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
                ServiceDisableCoordinatorOutcome.HelperUnavailable,
                Run(
                    new[] { ServiceDisableVerbs.InitiatorVerb },
                    new FakeHelperLocator(ServiceAdminHelperLocationResult.Refused(outcome)),
                    signing: null,
                    launcher,
                    client));

            Assert.Empty(launcher.Launched);
            Assert.Equal(0, client.Requests);
        }
    }

    [Fact]
    public void The_coordinator_launches_the_fixed_sibling_helper_with_the_closed_disable_verb()
    {
        var launcher = StartedLauncher();
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        Assert.Equal(
            ServiceDisableCoordinatorOutcome.Completed,
            Run(new[] { ServiceDisableVerbs.InitiatorVerb }, launcher: launcher, client: client));

        ServiceAnchorElevationLaunchSpec spec = Assert.Single(launcher.Launched);
        Assert.Equal(SampleHelperPath, spec.FileName);
        Assert.Equal(
            ServiceAdminHelperLocationContract.HelperFileName,
            System.IO.Path.GetFileName(spec.FileName));

        Assert.Equal(ServiceDisableVerbs.ElevatedTokenCount, spec.Arguments.Count);
        Assert.Equal(ServiceDisableVerbs.ElevatedHelperVerb, spec.Arguments[0]);
        Assert.Equal(
            SampleInitiator.ProcessId.ToString(CultureInfo.InvariantCulture), spec.Arguments[4]);
        Assert.Equal(
            SampleInitiator.CreationFileTime.ToString(CultureInfo.InvariantCulture), spec.Arguments[6]);
        Assert.Equal(spec.Arguments[2], client.LastEndpointName);
        Assert.True(ServiceInitiatingUserIdentityChannelContract.IsCanonicalEndpointName(spec.Arguments[2]));

        // Launched DIRECTLY: no host image and no managed assembly argument.
        Assert.DoesNotContain(spec.Arguments, a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(spec.Arguments, a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_path_service_name_account_command_or_secret_ever_crosses_the_disable_command_line()
    {
        var launcher = StartedLauncher();
        Assert.Equal(
            ServiceDisableCoordinatorOutcome.Completed,
            Run(new[] { ServiceDisableVerbs.InitiatorVerb }, launcher: launcher));

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
    }

    [Fact]
    public void Each_disable_launch_uses_a_fresh_unpredictable_endpoint_name()
    {
        var first = StartedLauncher();
        var second = StartedLauncher();

        Run(new[] { ServiceDisableVerbs.InitiatorVerb }, launcher: first);
        Run(new[] { ServiceDisableVerbs.InitiatorVerb }, launcher: second);

        Assert.NotEqual(
            Assert.Single(first.Launched).Arguments[2],
            Assert.Single(second.Launched).Arguments[2]);
    }

    [Fact]
    public void A_disable_launch_spec_is_refused_when_the_endpoint_or_initiator_is_not_canonical()
    {
        Assert.Null(ServiceDisableCoordinator.TryComposeLaunchSpec(
            SampleHelperPath, "not-a-canonical-endpoint", SampleInitiator));
        Assert.Null(ServiceDisableCoordinator.TryComposeLaunchSpec(
            SampleHelperPath, ServiceInitiatingUserIdentityChannelContract.NewEndpointName(), default));
        Assert.Null(ServiceDisableCoordinator.TryComposeLaunchSpec(
            string.Empty, ServiceInitiatingUserIdentityChannelContract.NewEndpointName(), SampleInitiator));
    }

    // =======================================================================
    // UAC DECLINE, BOUNDED WAIT AND EXIT MAPPING
    // =======================================================================

    [Fact]
    public void A_declined_disable_approval_returns_immediately_and_no_client_runs()
    {
        var launcher = new FakeLauncher(ServiceAnchorElevatedLaunch.Declined);
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        Assert.Equal(
            ServiceDisableCoordinatorOutcome.ApprovalDeclined,
            Run(new[] { ServiceDisableVerbs.InitiatorVerb }, launcher: launcher, client: client));

        Assert.Single(launcher.Launched);
        Assert.Equal(0, client.Requests);
    }

    [Fact]
    public void A_failed_disable_launch_runs_no_client_transaction()
    {
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        Assert.Equal(
            ServiceDisableCoordinatorOutcome.HelperLaunchFailed,
            Run(
                new[] { ServiceDisableVerbs.InitiatorVerb },
                launcher: new FakeLauncher(ServiceAnchorElevatedLaunch.Failed),
                client: client));

        Assert.Equal(0, client.Requests);
    }

    [Fact]
    public void A_helper_that_dies_before_the_disable_transaction_ends_the_wait_early()
    {
        var launcher = new FakeLauncher(() => ServiceAnchorElevatedLaunch.Started(
            new FakeElevatedProcess(SetupExitCodes.GenericError, alreadyExited: true)));

        var blocking = new BlockingChannelClient();
        Assert.Equal(
            ServiceDisableCoordinatorOutcome.HelperExitedBeforeTransaction,
            Run(new[] { ServiceDisableVerbs.InitiatorVerb }, launcher: launcher, client: blocking));

        blocking.Release();
    }

    [Fact]
    public void A_helper_that_never_exits_after_a_completed_disable_is_bounded()
    {
        Assert.Equal(
            ServiceDisableCoordinatorOutcome.HelperDidNotExit,
            Run(
                new[] { ServiceDisableVerbs.InitiatorVerb },
                launcher: new FakeLauncher(() =>
                    ServiceAnchorElevatedLaunch.Started(new NeverExitingProcess()))));
    }

    [Fact]
    public void A_nonzero_helper_exit_after_a_completed_disable_is_a_failure()
    {
        Assert.Equal(
            ServiceDisableCoordinatorOutcome.HelperReportedFailure,
            Run(
                new[] { ServiceDisableVerbs.InitiatorVerb },
                launcher: StartedLauncher(SetupExitCodes.GenericError)));
    }

    [Theory]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict,
        ServiceDisableCoordinatorOutcome.RecoveryRequired)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.TransactionRecoveryRequired,
        ServiceDisableCoordinatorOutcome.RecoveryRequired)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused,
        ServiceDisableCoordinatorOutcome.AcknowledgementNotReceived)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.TransactionPreflightRefused,
        ServiceDisableCoordinatorOutcome.AcknowledgementNotReceived)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch,
        ServiceDisableCoordinatorOutcome.AcknowledgementNotReceived)]
    [InlineData(
        ServiceInitiatingUserIdentityChannelOutcome.NoClientConnected,
        ServiceDisableCoordinatorOutcome.AcknowledgementNotReceived)]
    public void A_bounded_channel_outcome_maps_to_its_bounded_disable_outcome(
        ServiceInitiatingUserIdentityChannelOutcome channelOutcome,
        ServiceDisableCoordinatorOutcome expected)
    {
        Assert.Equal(
            expected,
            Run(new[] { ServiceDisableVerbs.InitiatorVerb }, client: new FakeChannelClient(channelOutcome)));
    }

    [Fact]
    public void A_different_owner_is_never_overridden_and_always_requires_attended_recovery()
    {
        // The channel refuses a validated anchor that names a different SID with
        // AnchorConflict. The coordinator must PRESERVE that meaning rather than
        // flattening it into a plain refusal a caller might retry.
        Assert.Equal(
            ServiceDisableCoordinatorOutcome.RecoveryRequired,
            Run(
                new[] { ServiceDisableVerbs.InitiatorVerb },
                client: new FakeChannelClient(
                    ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict)));

        Assert.NotEqual(
            ServiceDisableCoordinatorOutcome.Completed, ServiceDisableCoordinatorOutcome.RecoveryRequired);
    }

    [Fact]
    public void Only_a_completed_transaction_and_a_zero_helper_exit_report_completed()
    {
        foreach (ServiceInitiatingUserIdentityChannelOutcome outcome
                 in Enum.GetValues<ServiceInitiatingUserIdentityChannelOutcome>())
        {
            ServiceDisableCoordinatorOutcome result =
                Run(new[] { ServiceDisableVerbs.InitiatorVerb }, client: new FakeChannelClient(outcome));

            if (outcome == ServiceInitiatingUserIdentityChannelOutcome.Completed)
            {
                Assert.Equal(ServiceDisableCoordinatorOutcome.Completed, result);
            }
            else
            {
                Assert.NotEqual(ServiceDisableCoordinatorOutcome.Completed, result);
            }
        }
    }

    [Fact]
    public void The_elevated_disable_process_handle_is_always_disposed()
    {
        var process = new FakeElevatedProcess(SetupExitCodes.Ok, alreadyExited: false);
        Run(
            new[] { ServiceDisableVerbs.InitiatorVerb },
            launcher: new FakeLauncher(() => ServiceAnchorElevatedLaunch.Started(process)));

        Assert.True(process.Disposed);
    }

    [Fact]
    public void The_disable_timeouts_match_the_enable_path_and_stay_inside_the_helper_self_timeout()
    {
        Assert.Equal(
            ServiceEnableCoordinator.DefaultChannelTimeout, ServiceDisableCoordinator.DefaultChannelTimeout);
        Assert.Equal(
            ServiceEnableCoordinator.DefaultHelperExitTimeout,
            ServiceDisableCoordinator.DefaultHelperExitTimeout);

        Assert.Equal(TimeSpan.FromSeconds(180), ServiceDisableCoordinator.DefaultChannelTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), ServiceDisableCoordinator.DefaultHelperExitTimeout);
        Assert.Equal(TimeSpan.FromSeconds(240), ServiceEnableVerbs.HelperSelfTimeout);

        // The helper must outlive the rendezvous, or a healthy transaction could
        // be cut short by the wrong clock.
        Assert.True(ServiceEnableVerbs.HelperSelfTimeout > ServiceDisableCoordinator.DefaultChannelTimeout);
    }

    [Fact]
    public void Initiator_facts_are_captured_from_this_process_and_never_supplied_by_a_caller()
    {
        var facts = new FakeInitiatorFacts(SampleInitiator);
        ServiceDisableCoordinator.Run(
            new[] { ServiceDisableVerbs.InitiatorVerb },
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
            ServiceDisableCoordinatorOutcome.InitiatorFactsUnavailable,
            ServiceDisableCoordinator.Run(
                new[] { ServiceDisableVerbs.InitiatorVerb },
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
    // ORDINARY SETUP NEVER REACHES EITHER SERVICE-DISABLE VERB
    // =======================================================================

    [Fact]
    public void No_ordinary_setup_verb_or_flag_matches_either_service_disable_verb()
    {
        foreach (string verb in ArgParser.KnownVerbs)
        {
            Assert.False(ServiceDisableVerbs.IsRequested(verb));
            Assert.False(ServiceDisableVerbs.IsInitiatorRequested(verb));
            Assert.False(ServiceDisableVerbs.IsElevatedHelperRequested(verb));
        }

        foreach (string token in new[]
        {
            "install", "update", "repair", "apply-update", "uninstall", "status", "version", "help",
            "--install-root", "--payload-root", "--force", "--quiet", "--silent", "--dry-run",
            "provider-status", "provider-repair", "", "service-disable-extra", "service", "disable",
            "service-anchor-bind", "service-anchor-bind-elevated", "service-enable", "service-enable-elevated",
        })
        {
            Assert.False(ServiceDisableVerbs.IsRequested(token));
        }
    }

    [Fact]
    public void The_service_disable_verbs_are_not_public_setup_verbs()
    {
        Assert.DoesNotContain(ServiceDisableVerbs.InitiatorVerb, ArgParser.KnownVerbs);
        Assert.DoesNotContain(ServiceDisableVerbs.ElevatedHelperVerb, ArgParser.KnownVerbs);
    }

    [Fact]
    public void The_three_internal_verb_families_are_distinct_spellings()
    {
        Assert.NotEqual(ServiceDisableVerbs.InitiatorVerb, ServiceEnableVerbs.InitiatorVerb);
        Assert.NotEqual(ServiceDisableVerbs.InitiatorVerb, ServiceAnchorElevationVerbs.InitiatorVerb);
        Assert.NotEqual(
            ServiceDisableVerbs.ElevatedHelperVerb, ServiceEnableVerbs.ElevatedHelperVerb);
        Assert.NotEqual(
            ServiceDisableVerbs.ElevatedHelperVerb, ServiceAnchorElevationVerbs.ElevatedHelperVerb);

        Assert.False(ServiceEnableVerbs.IsRequested(ServiceDisableVerbs.InitiatorVerb));
        Assert.False(ServiceEnableVerbs.IsRequested(ServiceDisableVerbs.ElevatedHelperVerb));
        Assert.False(ServiceAnchorElevationVerbs.IsRequested(ServiceDisableVerbs.InitiatorVerb));
        Assert.False(ServiceAnchorElevationVerbs.IsRequested(ServiceDisableVerbs.ElevatedHelperVerb));
    }

    // =======================================================================
    // BOUNDED SURFACES LEAK NOTHING
    // =======================================================================

    [Fact]
    public void A_default_disable_outcome_is_unspecified_and_is_never_success()
    {
        Assert.Equal(ServiceDisableCoordinatorOutcome.Unspecified, default(ServiceDisableCoordinatorOutcome));
        Assert.Equal(ServiceDisableOperationOutcome.Unspecified, default(ServiceDisableOperationOutcome));
        Assert.NotEqual(ServiceDisableCoordinatorOutcome.Completed, default(ServiceDisableCoordinatorOutcome));
        Assert.NotEqual(ServiceDisableOperationOutcome.Completed, default(ServiceDisableOperationOutcome));

        // AlreadyDisabled is idempotence, never a removal.
        Assert.NotEqual(
            ServiceDisableOperationOutcome.Completed, ServiceDisableOperationOutcome.AlreadyDisabled);
    }

    [Fact]
    public void The_disable_launch_spec_to_string_carries_no_path_or_argument()
    {
        ServiceAnchorElevationLaunchSpec? spec = ServiceDisableCoordinator.TryComposeLaunchSpec(
            SampleHelperPath, ServiceInitiatingUserIdentityChannelContract.NewEndpointName(), SampleInitiator);
        Assert.NotNull(spec);

        string text = spec!.ToString();
        Assert.DoesNotContain(SampleHelperPath, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--endpoint", text, StringComparison.Ordinal);
        Assert.DoesNotContain("service-disable", text, StringComparison.Ordinal);
    }
}
