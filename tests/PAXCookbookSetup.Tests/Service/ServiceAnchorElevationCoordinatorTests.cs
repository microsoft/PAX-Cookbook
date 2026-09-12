using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 59 - SERVICE-ANCHOR ELEVATION COORDINATOR AND ELEVATED HELPER DISPATCH
// ===========================================================================
//
// SCOPE, stated plainly. Every test here uses FAKES. Nothing in this file
// elevates, triggers a UAC prompt, uses runas, starts a process, registers or
// touches a service, writes %ProgramData%, opens a certificate store or private
// key, reads or writes an ACL, the registry or a credential vault, opens a
// socket, runs PAX or starts a Bake. The coordinator's REAL launcher is never
// constructed; the launcher, the process facts and the channel client are all
// injected.
//
// DISCLOSED LIMITATION, recorded once. These tests run at a single integrity
// level and under a single account, so they cannot prove what Windows does when
// a DIFFERENT administrator approves the prompt. What they prove is the part
// that is account-independent: which identity the coordinator and helper USE,
// and that the approving account is never the one that becomes the owner.
public sealed class ServiceAnchorElevationCoordinatorTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);

    // ---- injected process facts, for BOTH supported launch shapes ----------

    private sealed class FakeProcessFacts : IServiceAnchorElevationProcessFacts
    {
        internal FakeProcessFacts(string? processPath, string? assemblyPath, ServiceInitiatorProcessFacts initiator)
        {
            ProcessPath = processPath;
            ManagedEntryAssemblyPath = assemblyPath;
            _initiator = initiator;
        }

        private readonly ServiceInitiatorProcessFacts _initiator;

        public string? ProcessPath { get; }

        public string? ManagedEntryAssemblyPath { get; }

        public ServiceInitiatorProcessFacts CaptureInitiator() => _initiator;
    }

    private static readonly ServiceInitiatorProcessFacts SampleInitiator = new(4321, 133_000_000_000_000_000L);

    /// <summary>A framework-dependent installed Setup: dotnet.exe runs the assembly.</summary>
    private static FakeProcessFacts DotnetHostShape() =>
        new(@"C:\Program Files\dotnet\dotnet.exe", @"C:\Apps\PAX Cookbook\PAXCookbookSetup.dll", SampleInitiator);

    /// <summary>A self-contained / single-file Setup: the executable IS Setup.</summary>
    private static FakeProcessFacts SelfContainedShape() =>
        new(@"C:\Apps\PAX Cookbook\PAXCookbookSetup.exe", null, SampleInitiator);

    // ---- injected elevated launcher ----------------------------------------

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

    // ---- injected channel client -------------------------------------------

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

    // =======================================================================
    // THE FIXED HELPER ENTRY POINT AND ITS CLOSED ARGUMENTS
    // =======================================================================

    [Fact]
    public void The_coordinator_launches_the_fixed_helper_verb_with_the_closed_argument_set()
    {
        var launcher = new FakeLauncher(() =>
            ServiceAnchorElevatedLaunch.Started(new FakeElevatedProcess(SetupExitCodes.Ok, alreadyExited: false)));
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        ServiceAnchorElevationOutcome outcome = ServiceAnchorElevationCoordinator.Run(
            SelfContainedShape(), launcher, client, Bounded, Bounded);

        Assert.Equal(ServiceAnchorElevationOutcome.Completed, outcome);

        ServiceAnchorElevationLaunchSpec spec = Assert.Single(launcher.Launched);
        Assert.Equal(ServiceAnchorElevationVerbs.ElevatedHelperVerb, spec.Arguments[0]);
        Assert.Equal(ServiceAnchorElevationVerbs.EndpointOption, spec.Arguments[1]);
        Assert.Equal(ServiceAnchorElevationVerbs.InitiatorProcessIdOption, spec.Arguments[3]);
        Assert.Equal(ServiceAnchorElevationVerbs.InitiatorCreatedOption, spec.Arguments[5]);

        // The pid and creation FILETIME the coordinator captured are the exact
        // values it passes, and NOTHING ELSE crosses the command line - no SID,
        // no challenge, no path, no service name, no registry key.
        Assert.Equal(
            SampleInitiator.ProcessId.ToString(CultureInfo.InvariantCulture), spec.Arguments[4]);
        Assert.Equal(
            SampleInitiator.CreationFileTime.ToString(CultureInfo.InvariantCulture), spec.Arguments[6]);
        Assert.Equal(7, spec.Arguments.Count);

        // The endpoint the client used is the endpoint that was handed over.
        Assert.Equal(spec.Arguments[2], client.LastEndpointName);
        Assert.True(ServiceInitiatingUserIdentityChannelContract.IsCanonicalEndpointName(spec.Arguments[2]));

        // The arguments the coordinator composes are accepted by the helper's
        // own closed parser, which is the only thing that will ever read them.
        string[] argv = spec.Arguments.ToArray();
        Assert.True(ServiceAnchorElevatedHelperDispatch.TryParse(argv, out ServiceAnchorElevatedArguments? parsed));
        Assert.NotNull(parsed);
        Assert.Equal(spec.Arguments[2], parsed!.EndpointName);
        Assert.Equal(SampleInitiator.ProcessId, parsed.Initiator.ProcessId);
        Assert.Equal(SampleInitiator.CreationFileTime, parsed.Initiator.CreationFileTime);
    }

    [Fact]
    public void Two_runs_never_reuse_a_rendezvous_name()
    {
        var launcher = new FakeLauncher(() =>
            ServiceAnchorElevatedLaunch.Started(new FakeElevatedProcess(SetupExitCodes.Ok, alreadyExited: false)));
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        ServiceAnchorElevationCoordinator.Run(SelfContainedShape(), launcher, client, Bounded, Bounded);
        ServiceAnchorElevationCoordinator.Run(SelfContainedShape(), launcher, client, Bounded, Bounded);

        Assert.Equal(2, launcher.Launched.Count);
        Assert.NotEqual(launcher.Launched[0].Arguments[2], launcher.Launched[1].Arguments[2]);
    }

    // =======================================================================
    // BOTH LAUNCH SHAPES (ruling 5), through injected process facts
    // =======================================================================

    [Fact]
    public void A_framework_dependent_setup_launches_the_current_dotnet_host_with_the_assembly_first()
    {
        FakeProcessFacts facts = DotnetHostShape();
        Assert.True(ServiceAnchorElevationLaunchSpecResolver.IsDotnetHostShape(facts));

        ServiceAnchorElevationLaunchSpec? spec = ServiceAnchorElevationLaunchSpecResolver.TryResolve(
            facts, ServiceInitiatingUserIdentityChannelContract.NewEndpointName(), SampleInitiator);

        Assert.NotNull(spec);
        Assert.Equal(facts.ProcessPath, spec!.FileName);
        Assert.Equal(8, spec.Arguments.Count);

        // The assembly path is the FIRST argument, then the fixed verb.
        Assert.Equal(facts.ManagedEntryAssemblyPath, spec.Arguments[0]);
        Assert.Equal(ServiceAnchorElevationVerbs.ElevatedHelperVerb, spec.Arguments[1]);

        // A path containing a space is quoted exactly once and nothing else is.
        string commandLine = Assert.IsType<string>(spec.TryComposeCommandLine());
        Assert.Contains("\"" + facts.ManagedEntryAssemblyPath + "\"", commandLine, StringComparison.Ordinal);
        Assert.Equal(2, commandLine.Count(c => c == '"'));
    }

    [Fact]
    public void A_self_contained_setup_launches_its_own_executable_with_no_assembly_argument()
    {
        FakeProcessFacts facts = SelfContainedShape();
        Assert.False(ServiceAnchorElevationLaunchSpecResolver.IsDotnetHostShape(facts));

        ServiceAnchorElevationLaunchSpec? spec = ServiceAnchorElevationLaunchSpecResolver.TryResolve(
            facts, ServiceInitiatingUserIdentityChannelContract.NewEndpointName(), SampleInitiator);

        Assert.NotNull(spec);
        Assert.Equal(facts.ProcessPath, spec!.FileName);
        Assert.Equal(7, spec.Arguments.Count);
        Assert.Equal(ServiceAnchorElevationVerbs.ElevatedHelperVerb, spec.Arguments[0]);

        // No assembly path is passed at all in this shape.
        Assert.DoesNotContain(spec.Arguments, a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

        string commandLine = Assert.IsType<string>(spec.TryComposeCommandLine());
        Assert.DoesNotContain("\"", commandLine, StringComparison.Ordinal);
    }

    [Theory]
    // A single-file publish has NO managed assembly path, so even a process
    // literally named dotnet.exe is not the host shape.
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", null, false)]
    [InlineData(@"C:\Program Files\dotnet\DOTNET.EXE", @"C:\Apps\PAXCookbookSetup.dll", true)]
    [InlineData(@"C:\Apps\PAXCookbookSetup.exe", @"C:\Apps\PAXCookbookSetup.dll", false)]
    [InlineData(@"C:\Apps\dotnet.exe.exe", @"C:\Apps\PAXCookbookSetup.dll", false)]
    public void The_launch_shape_is_decided_only_by_this_process_own_facts(
        string processPath, string? assemblyPath, bool expectedDotnetHost)
    {
        var facts = new FakeProcessFacts(processPath, assemblyPath, SampleInitiator);
        Assert.Equal(expectedDotnetHost, ServiceAnchorElevationLaunchSpecResolver.IsDotnetHostShape(facts));
    }

    [Fact]
    public void An_unresolvable_launch_shape_is_a_bounded_refusal_and_launches_nothing()
    {
        var launcher = new FakeLauncher(() =>
            ServiceAnchorElevatedLaunch.Started(new FakeElevatedProcess(SetupExitCodes.Ok, alreadyExited: false)));
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        var noPath = new FakeProcessFacts(null, null, SampleInitiator);
        Assert.Equal(
            ServiceAnchorElevationOutcome.LaunchSpecUnavailable,
            ServiceAnchorElevationCoordinator.Run(noPath, launcher, client, Bounded, Bounded));

        var noInitiator = new FakeProcessFacts(@"C:\Apps\PAXCookbookSetup.exe", null, default);
        Assert.Equal(
            ServiceAnchorElevationOutcome.InitiatorFactsUnavailable,
            ServiceAnchorElevationCoordinator.Run(noInitiator, launcher, client, Bounded, Bounded));

        Assert.Empty(launcher.Launched);
        Assert.Equal(0, client.Requests);
    }

    // =======================================================================
    // SEQUENTIAL LAUNCH SEMANTICS (ruling 4)
    // =======================================================================

    [Fact]
    public void A_declined_approval_performs_no_client_transaction_and_reports_a_bounded_refusal()
    {
        var launcher = new FakeLauncher(ServiceAnchorElevatedLaunch.Declined);
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        ServiceAnchorElevationOutcome outcome = ServiceAnchorElevationCoordinator.Run(
            SelfContainedShape(), launcher, client, Bounded, Bounded);

        Assert.Equal(ServiceAnchorElevationOutcome.ApprovalDeclined, outcome);

        // THE POINT OF THIS TEST: the client was never started, so a declined
        // UAC prompt cannot consume the rendezvous budget or leave a
        // half-finished transaction behind.
        Assert.Equal(0, client.Requests);
        Assert.Null(client.LastEndpointName);
        Assert.Single(launcher.Launched);
    }

    [Fact]
    public void A_failed_launch_is_distinguished_from_a_declined_approval()
    {
        var launcher = new FakeLauncher(ServiceAnchorElevatedLaunch.Failed);
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        Assert.Equal(
            ServiceAnchorElevationOutcome.HelperLaunchFailed,
            ServiceAnchorElevationCoordinator.Run(SelfContainedShape(), launcher, client, Bounded, Bounded));
        Assert.Equal(0, client.Requests);
    }

    [Fact]
    public void A_helper_that_exits_early_cancels_the_connection_wait()
    {
        // The helper is already gone, and the client never returns, so only the
        // helper-exit observation can end this wait.
        var launcher = new FakeLauncher(() =>
            ServiceAnchorElevatedLaunch.Started(new FakeElevatedProcess(SetupExitCodes.GenericError, alreadyExited: true)));
        var stalledClient = new StalledChannelClient();

        ServiceAnchorElevationOutcome outcome = ServiceAnchorElevationCoordinator.Run(
            SelfContainedShape(), launcher, stalledClient, TimeSpan.FromMinutes(10), Bounded);

        Assert.Equal(ServiceAnchorElevationOutcome.HelperExitedBeforeTransaction, outcome);
        stalledClient.Release();
    }

    private sealed class StalledChannelClient : IServiceAnchorChannelClient
    {
        private readonly ManualResetEventSlim _gate = new(false);

        internal void Release() => _gate.Set();

        public ServiceInitiatingUserIdentityChannelOutcome Request(string endpointName, TimeSpan timeout)
        {
            _gate.Wait(TimeSpan.FromSeconds(30));
            return ServiceInitiatingUserIdentityChannelOutcome.NoClientConnected;
        }
    }

    [Fact]
    public void A_helper_that_never_exits_after_a_successful_transaction_is_a_bounded_refusal()
    {
        var launcher = new FakeLauncher(() => ServiceAnchorElevatedLaunch.Started(new NeverExitingProcess()));
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        Assert.Equal(
            ServiceAnchorElevationOutcome.HelperDidNotExit,
            ServiceAnchorElevationCoordinator.Run(
                SelfContainedShape(), launcher, client, Bounded, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void The_helper_exit_code_is_observed_directly_after_the_transaction()
    {
        var launcher = new FakeLauncher(() =>
            ServiceAnchorElevatedLaunch.Started(new FakeElevatedProcess(SetupExitCodes.GenericError, alreadyExited: false)));
        var client = new FakeChannelClient(ServiceInitiatingUserIdentityChannelOutcome.Completed);

        Assert.Equal(
            ServiceAnchorElevationOutcome.HelperReportedFailure,
            ServiceAnchorElevationCoordinator.Run(SelfContainedShape(), launcher, client, Bounded, Bounded));
    }

    [Theory]
    [InlineData(ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict, ServiceAnchorElevationOutcome.RecoveryRequired)]
    [InlineData(ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch, ServiceAnchorElevationOutcome.AcknowledgementNotReceived)]
    [InlineData(ServiceInitiatingUserIdentityChannelOutcome.InitiatorNotBound, ServiceAnchorElevationOutcome.AcknowledgementNotReceived)]
    [InlineData(ServiceInitiatingUserIdentityChannelOutcome.AnchorPersistenceRefused, ServiceAnchorElevationOutcome.AcknowledgementNotReceived)]
    [InlineData(ServiceInitiatingUserIdentityChannelOutcome.AcknowledgementNotReceived, ServiceAnchorElevationOutcome.AcknowledgementNotReceived)]
    public void Every_channel_refusal_maps_to_a_bounded_coordinator_outcome(
        ServiceInitiatingUserIdentityChannelOutcome channelOutcome, ServiceAnchorElevationOutcome expected)
    {
        var launcher = new FakeLauncher(() =>
            ServiceAnchorElevatedLaunch.Started(new FakeElevatedProcess(SetupExitCodes.Ok, alreadyExited: false)));
        var client = new FakeChannelClient(channelOutcome);

        Assert.Equal(
            expected,
            ServiceAnchorElevationCoordinator.Run(SelfContainedShape(), launcher, client, Bounded, Bounded));
    }

    // =======================================================================
    // THE ELEVATED HELPER'S CLOSED ARGUMENT GRAMMAR
    // =======================================================================

    private static string[] ValidHelperArgv() => new[]
    {
        ServiceAnchorElevationVerbs.ElevatedHelperVerb,
        ServiceAnchorElevationVerbs.EndpointOption,
        ServiceInitiatingUserIdentityChannelContract.NewEndpointName(),
        ServiceAnchorElevationVerbs.InitiatorProcessIdOption,
        "4321",
        ServiceAnchorElevationVerbs.InitiatorCreatedOption,
        "133000000000000000",
    };

    [Fact]
    public void The_helper_accepts_exactly_the_closed_argument_set()
    {
        string[] argv = ValidHelperArgv();
        Assert.True(ServiceAnchorElevatedHelperDispatch.TryParse(argv, out ServiceAnchorElevatedArguments? parsed));
        Assert.NotNull(parsed);
        Assert.Equal(argv[2], parsed!.EndpointName);
        Assert.Equal(4321u, parsed.Initiator.ProcessId);
        Assert.Equal(133_000_000_000_000_000L, parsed.Initiator.CreationFileTime);
        Assert.True(parsed.Initiator.IsPresent);
    }

    public static TheoryData<string[]> RejectedHelperArgv()
    {
        var data = new TheoryData<string[]>();
        string endpoint = ServiceInitiatingUserIdentityChannelContract.NewEndpointName();

        string[] Base() => new[]
        {
            ServiceAnchorElevationVerbs.ElevatedHelperVerb,
            ServiceAnchorElevationVerbs.EndpointOption, endpoint,
            ServiceAnchorElevationVerbs.InitiatorProcessIdOption, "4321",
            ServiceAnchorElevationVerbs.InitiatorCreatedOption, "133000000000000000",
        };

        data.Add(Array.Empty<string>());
        data.Add(new[] { ServiceAnchorElevationVerbs.ElevatedHelperVerb });

        // Missing option, extra token, duplicate option.
        string[] missing = Base().Take(5).ToArray();
        data.Add(missing);
        string[] extra = Base().Concat(new[] { "--extra" }).ToArray();
        data.Add(extra);
        string[] duplicated = Base();
        duplicated[5] = ServiceAnchorElevationVerbs.EndpointOption;
        duplicated[6] = endpoint;
        data.Add(duplicated);

        // Unknown option spelling, and out-of-order options.
        string[] unknown = Base();
        unknown[3] = "--initiator-process";
        data.Add(unknown);
        string[] reordered = Base();
        (reordered[1], reordered[3]) = (reordered[3], reordered[1]);
        (reordered[2], reordered[4]) = (reordered[4], reordered[2]);
        data.Add(reordered);

        // Wrong verb - including the NON-elevated initiator verb, which must
        // never be accepted by the elevated parser.
        string[] wrongVerb = Base();
        wrongVerb[0] = ServiceAnchorElevationVerbs.InitiatorVerb;
        data.Add(wrongVerb);

        // Malformed endpoint names.
        foreach (string bad in new[] { "not-an-endpoint", "PAXCookbook.InitiatingUserIdentity.zzzz", "" })
        {
            string[] badEndpoint = Base();
            badEndpoint[2] = bad;
            data.Add(badEndpoint);
        }

        // Malformed pids: zero, signed, hex, spaced, non-numeric, overflowing.
        foreach (string bad in new[] { "0", "-1", "+1", "0x10", " 4321", "4 321", "abc", "4294967296" })
        {
            string[] badPid = Base();
            badPid[4] = bad;
            data.Add(badPid);
        }

        // Malformed creation times.
        foreach (string bad in new[] { "0", "-133000000000000000", "1.5", "abc", "99999999999999999999" })
        {
            string[] badCreated = Base();
            badCreated[6] = bad;
            data.Add(badCreated);
        }

        // Oversized token.
        string[] oversized = Base();
        oversized[4] = new string('9', ServiceAnchorElevationVerbs.MaxTokenLength + 1);
        data.Add(oversized);

        return data;
    }

    [Theory]
    [MemberData(nameof(RejectedHelperArgv))]
    public void The_helper_refuses_anything_outside_its_closed_grammar(string[] argv)
    {
        Assert.False(ServiceAnchorElevatedHelperDispatch.TryParse(argv, out ServiceAnchorElevatedArguments? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void A_null_argument_vector_is_refused_rather_than_defaulted()
    {
        Assert.False(ServiceAnchorElevatedHelperDispatch.TryParse(null, out ServiceAnchorElevatedArguments? parsed));
        Assert.Null(parsed);
        Assert.Equal(SetupExitCodes.UsageError, ServiceAnchorElevatedHelperDispatch.Run(null));
    }

    [Fact]
    public void The_helper_grammar_has_no_parameter_for_any_path_or_machine_object()
    {
        // The whole accepted vocabulary is three option spellings. If a future
        // change adds a fourth, this test is the thing that notices.
        string[] options =
        {
            ServiceAnchorElevationVerbs.EndpointOption,
            ServiceAnchorElevationVerbs.InitiatorProcessIdOption,
            ServiceAnchorElevationVerbs.InitiatorCreatedOption,
        };
        Assert.Equal(3, options.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ServiceAnchorElevationVerbs.ElevatedTokenCount, 1 + (options.Length * 2));

        foreach (string forbidden in new[]
        {
            "--path", "--directory", "--file", "--service", "--service-name", "--exe", "--executable",
            "--script", "--registry", "--key", "--certificate", "--thumbprint", "--store", "--command",
            "--env", "--environment", "--output", "--account", "--sid", "--user", "--token", "--secret",
        })
        {
            Assert.DoesNotContain(forbidden, options, StringComparer.OrdinalIgnoreCase);

            string[] argv = ValidHelperArgv();
            argv[1] = forbidden;
            Assert.False(ServiceAnchorElevatedHelperDispatch.TryParse(argv, out _));
        }
    }

    [Fact]
    public void A_valid_grammar_with_an_unresolvable_initiator_never_reaches_a_durable_write()
    {
        // The resolver refuses, so no endpoint is created and no anchor is
        // touched. The exit code is bounded and carries no identity.
        int exitCode = ServiceAnchorElevatedHelperDispatch.Run(
            ValidHelperArgv(), new RefusingResolver(), TimeSpan.FromMilliseconds(50));

        Assert.Equal(SetupExitCodes.GenericError, exitCode);
    }

    private sealed class RefusingResolver : IServiceInitiatorIdentityResolver
    {
        public string? TryResolveBoundInitiatorSid(ServiceInitiatorProcessFacts initiator) => null;
    }

    [Theory]
    [InlineData(ServiceInitiatingUserIdentityChannelOutcome.Completed, SetupExitCodes.Ok)]
    [InlineData(ServiceInitiatingUserIdentityChannelOutcome.UnsupportedPlatform, SetupExitCodes.UnsupportedWindowsVersion)]
    [InlineData(ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict, SetupExitCodes.GenericError)]
    [InlineData(ServiceInitiatingUserIdentityChannelOutcome.AdmissionFactMismatch, SetupExitCodes.GenericError)]
    [InlineData(ServiceInitiatingUserIdentityChannelOutcome.Unspecified, SetupExitCodes.GenericError)]
    public void The_exit_code_mapping_is_total_and_only_success_is_zero(
        ServiceInitiatingUserIdentityChannelOutcome outcome, int expected)
    {
        Assert.Equal(expected, ServiceAnchorElevatedHelperDispatch.MapExitCode(outcome));
    }

    [Fact]
    public void Only_the_completed_outcome_maps_to_zero()
    {
        foreach (ServiceInitiatingUserIdentityChannelOutcome outcome
                 in Enum.GetValues<ServiceInitiatingUserIdentityChannelOutcome>())
        {
            int mapped = ServiceAnchorElevatedHelperDispatch.MapExitCode(outcome);
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
    // ORDINARY SETUP NEVER REACHES EITHER VERB
    // =======================================================================

    [Fact]
    public void No_ordinary_setup_verb_or_flag_matches_either_internal_anchor_verb()
    {
        foreach (string verb in ArgParser.KnownVerbs)
        {
            Assert.False(ServiceAnchorElevationVerbs.IsRequested(verb));
            Assert.False(ServiceAnchorElevationVerbs.IsInitiatorRequested(verb));
            Assert.False(ServiceAnchorElevationVerbs.IsElevatedHelperRequested(verb));
        }

        foreach (string token in new[]
        {
            "install", "update", "repair", "apply-update", "uninstall", "status", "version", "help",
            "--install-root", "--payload-root", "--force", "--quiet", "--silent", "--dry-run",
            "provider-status", "provider-repair", "", "service-anchor", "service-anchor-bind-elevated-extra",
        })
        {
            Assert.False(ServiceAnchorElevationVerbs.IsRequested(token));
        }

        // Neither internal verb is a public verb, so ordinary parsing rejects
        // both as unknown and no help text can advertise them.
        Assert.DoesNotContain(ServiceAnchorElevationVerbs.InitiatorVerb, ArgParser.KnownVerbs);
        Assert.DoesNotContain(ServiceAnchorElevationVerbs.ElevatedHelperVerb, ArgParser.KnownVerbs);
    }

    [Fact]
    public void The_two_internal_verbs_are_distinct_and_each_matches_only_itself()
    {
        Assert.NotEqual(ServiceAnchorElevationVerbs.InitiatorVerb, ServiceAnchorElevationVerbs.ElevatedHelperVerb);

        Assert.True(ServiceAnchorElevationVerbs.IsInitiatorRequested(ServiceAnchorElevationVerbs.InitiatorVerb));
        Assert.False(ServiceAnchorElevationVerbs.IsElevatedHelperRequested(ServiceAnchorElevationVerbs.InitiatorVerb));

        Assert.True(ServiceAnchorElevationVerbs.IsElevatedHelperRequested(ServiceAnchorElevationVerbs.ElevatedHelperVerb));
        Assert.False(ServiceAnchorElevationVerbs.IsInitiatorRequested(ServiceAnchorElevationVerbs.ElevatedHelperVerb));
    }

    // =======================================================================
    // BOUNDED, IDENTITY-FREE SURFACE
    // =======================================================================

    [Fact]
    public void No_bounded_result_type_leaks_an_identity_through_to_string()
    {
        Assert.Equal(nameof(ServiceInitiatorProcessFacts), SampleInitiator.ToString());
        Assert.Equal("Unspecified", default(ServiceAnchorElevatedLaunch).ToString());
        Assert.Equal(ServiceAnchorElevationOutcome.Unspecified, default(ServiceAnchorElevationOutcome));

        var spec = new ServiceAnchorElevationLaunchSpec(@"C:\Apps\PAXCookbookSetup.exe", ValidHelperArgv());
        Assert.Equal(nameof(ServiceAnchorElevationLaunchSpec), spec.ToString());

        Assert.True(ServiceAnchorElevatedHelperDispatch.TryParse(ValidHelperArgv(), out ServiceAnchorElevatedArguments? parsed));
        Assert.Equal(nameof(ServiceAnchorElevatedArguments), parsed!.ToString());
    }

    [Fact]
    public void An_argument_containing_a_quote_is_refused_rather_than_escaped()
    {
        var spec = new ServiceAnchorElevationLaunchSpec(
            @"C:\Apps\PAXCookbookSetup.exe",
            new[] { ServiceAnchorElevationVerbs.ElevatedHelperVerb, "has\"quote" });

        Assert.Null(spec.TryComposeCommandLine());
    }
}
