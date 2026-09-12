// PAX Cookbook - SERVICE-ANCHOR ELEVATION COORDINATOR (cycle 59, Setup only)
//
// WHAT THIS FILE IS. The NON-ELEVATED half of one bounded installation-anchor
// transaction: it creates a fresh rendezvous name, launches exactly one fixed
// elevated Setup helper verb, runs the client half of the identity channel, and
// observes the helper's exit. It is the first production caller of
// ServiceInitiatingUserIdentityChannel.
//
// WHAT THIS IS NOT - READ THIS BEFORE DESCRIBING IT TO ANYONE. Completing this
// operation is NOT service enablement. Nothing here installs, registers,
// configures, enables or readies a Windows service, and no outcome of this file
// may ever be reported as "the service is installed", "enabled" or "ready". The
// ONLY durable effect of a successful transaction is one installation-anchor
// record naming the non-elevated user who initiated the installation.
//
// IT IS NOT INVOKED BY ORDINARY SETUP. Install, update and repair never reach
// this coordinator, and the cycle that introduced it never invoked it. It
// exists for a FUTURE explicit, informed service-enable flow.
//
// WHY THIS IS NOT IElevatedLauncher.RunElevatedAndWait. That launcher starts the
// elevated child and then BLOCKS until it exits. The elevated helper here does
// not exit until its client has connected and completed a transaction, and that
// client is this very process - so reusing RunElevatedAndWait would deadlock by
// construction. This file therefore starts the helper and keeps running.
//
// THE HUMAN MUST NOT CONSUME THE PIPE BUDGET. No channel timeout starts until
// Process.Start has RETURNED successfully. A user who stares at the UAC prompt
// for two minutes therefore cannot cause a spurious rendezvous timeout, and a
// DECLINED prompt returns immediately without ever starting a client.
//
// PRIVACY - FAIL CLOSED. The only value this file returns is a bounded outcome
// name. It never logs, and no result can carry the endpoint name, the challenge,
// a SID, a pid, a creation FILETIME, a raw argument, a native error code, an
// exception message or a path.
//
// WHAT THIS FILE CANNOT DO, by construction. It opens no certificate store or
// private key; reads or writes no ACL, registry key or credential vault;
// creates, changes, starts or stops no service; opens no socket; reads or
// writes no ownership ledger; touches no PAX and starts no Bake. It starts
// exactly ONE process: the fixed elevated Setup helper verb, with a closed
// argument set it composes itself.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace PAXCookbookSetup.Service;

/// <summary>
/// The bounded outcome of one coordinated anchor transaction. Zero is the
/// permanent, safe default so an uninitialised value can never read as success.
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data.
/// </summary>
public enum ServiceAnchorElevationOutcome
{
    Unspecified = 0,

    /// <summary>Anchor durable, acknowledgement received, helper exited cleanly.</summary>
    Completed = 1,

    UnsupportedPlatform = 2,

    /// <summary>This process could not read its own pid + creation FILETIME.</summary>
    InitiatorFactsUnavailable = 3,

    /// <summary>The fixed Setup launch shape could not be resolved.</summary>
    LaunchSpecUnavailable = 4,

    /// <summary>The administrator approval prompt was declined. No client ever ran.</summary>
    ApprovalDeclined = 5,

    HelperLaunchFailed = 6,

    /// <summary>The helper exited before the transaction completed, cancelling the wait.</summary>
    HelperExitedBeforeTransaction = 7,

    TransactionTimedOut = 8,

    /// <summary>The helper refused, or never acknowledged.</summary>
    AcknowledgementNotReceived = 9,

    /// <summary>
    /// Existing anchor state conflicts and is NEVER repaired automatically. A
    /// future attended, elevated recovery operation is the only remedy.
    /// </summary>
    RecoveryRequired = 10,

    /// <summary>The helper did not exit inside its bounded observation window.</summary>
    HelperDidNotExit = 11,

    /// <summary>The transaction acknowledged but the helper reported a nonzero exit.</summary>
    HelperReportedFailure = 12,
}

/// <summary>
/// The exact process to start and the exact closed arguments to pass it. There
/// is no caller-provided executable or assembly path anywhere in this type: the
/// resolver derives both from THIS process's own facts.
/// </summary>
internal sealed class ServiceAnchorElevationLaunchSpec
{
    internal ServiceAnchorElevationLaunchSpec(string fileName, IReadOnlyList<string> arguments)
    {
        FileName = fileName;
        Arguments = arguments;
    }

    internal string FileName { get; }

    internal IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// The command line, with each argument quoted only when it contains a
    /// space. An argument containing a double quote is REFUSED outright rather
    /// than escaped, so no quoting subtlety can ever change the parsed grammar.
    /// </summary>
    internal string? TryComposeCommandLine()
    {
        var parts = new List<string>(Arguments.Count);
        foreach (string argument in Arguments)
        {
            if (string.IsNullOrEmpty(argument) || argument.IndexOf('"') >= 0)
            {
                return null;
            }
            parts.Add(argument.IndexOf(' ') >= 0 ? "\"" + argument + "\"" : argument);
        }
        return string.Join(' ', parts);
    }

    /// <summary>Carries the bounded type name only - never a path or an argument.</summary>
    public override string ToString() => nameof(ServiceAnchorElevationLaunchSpec);
}

/// <summary>
/// The facts about THIS running Setup process that decide which of the two
/// supported launch shapes applies. An interface only so the focused tests can
/// prove BOTH shapes without a second installed Setup; production always uses
/// <see cref="RealServiceAnchorElevationProcessFacts"/>.
/// </summary>
internal interface IServiceAnchorElevationProcessFacts
{
    /// <summary>The executable image actually running: dotnet.exe, or Setup itself.</summary>
    string? ProcessPath { get; }

    /// <summary>
    /// The managed Setup assembly on disk, or null/empty for a single-file or
    /// self-contained build where there is no separate assembly to pass.
    /// </summary>
    string? ManagedEntryAssemblyPath { get; }

    /// <summary>This process's own pid paired with its own creation FILETIME.</summary>
    ServiceInitiatorProcessFacts CaptureInitiator();
}

/// <summary>The real facts, read from this process only.</summary>
internal sealed class RealServiceAnchorElevationProcessFacts : IServiceAnchorElevationProcessFacts
{
    public string? ProcessPath => Environment.ProcessPath;

    public string? ManagedEntryAssemblyPath
    {
        get
        {
            try
            {
                // Location is deliberately empty for single-file publishes,
                // which is exactly the signal the resolver needs.
                string location = typeof(RealServiceAnchorElevationProcessFacts).Assembly.Location;
                return string.IsNullOrEmpty(location) ? null : location;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public ServiceInitiatorProcessFacts CaptureInitiator() => ServiceInitiatorProcessBinding.CaptureCurrent();
}

/// <summary>
/// THE FIXED LAUNCH-SPEC RULE (cycle-59 ruling 5). It accepts NO caller-provided
/// executable or assembly path and has no override, delegate or settable field.
///
///   * FRAMEWORK-DEPENDENT INSTALLED SETUP - the running image is the shared
///     dotnet.exe host. Launch THAT dotnet.exe, with the current Setup assembly
///     path as the FIRST argument, then the fixed helper verb and closed
///     arguments.
///   * SELF-CONTAINED / SINGLE-FILE SETUP - the running image IS Setup. Launch
///     the current Setup executable directly with the fixed helper verb and
///     closed arguments, and pass no assembly path at all.
/// </summary>
internal static class ServiceAnchorElevationLaunchSpecResolver
{
    private const string DotnetHostFileName = "dotnet.exe";

    /// <summary>
    /// True when the running image is the shared dotnet host AND a real managed
    /// Setup assembly exists to hand it. Both conditions are required: a
    /// single-file publish has no assembly path, and a self-contained apphost is
    /// not named dotnet.exe.
    /// </summary>
    internal static bool IsDotnetHostShape(IServiceAnchorElevationProcessFacts facts)
    {
        if (facts is null || string.IsNullOrEmpty(facts.ProcessPath)
            || string.IsNullOrEmpty(facts.ManagedEntryAssemblyPath))
        {
            return false;
        }

        string leaf;
        try
        {
            leaf = Path.GetFileName(facts.ProcessPath);
        }
        catch (Exception)
        {
            return false;
        }

        return string.Equals(leaf, DotnetHostFileName, StringComparison.OrdinalIgnoreCase);
    }

    internal static ServiceAnchorElevationLaunchSpec? TryResolve(
        IServiceAnchorElevationProcessFacts facts, string endpointName, ServiceInitiatorProcessFacts initiator)
    {
        if (facts is null
            || string.IsNullOrEmpty(facts.ProcessPath)
            || !initiator.IsPresent
            || !ServiceInitiatingUserIdentityChannelContract.IsCanonicalEndpointName(endpointName))
        {
            return null;
        }

        var arguments = new List<string>(8);
        if (IsDotnetHostShape(facts))
        {
            arguments.Add(facts.ManagedEntryAssemblyPath!);
        }

        arguments.Add(ServiceAnchorElevationVerbs.ElevatedHelperVerb);
        arguments.Add(ServiceAnchorElevationVerbs.EndpointOption);
        arguments.Add(endpointName);
        arguments.Add(ServiceAnchorElevationVerbs.InitiatorProcessIdOption);
        arguments.Add(initiator.ProcessId.ToString(CultureInfo.InvariantCulture));
        arguments.Add(ServiceAnchorElevationVerbs.InitiatorCreatedOption);
        arguments.Add(initiator.CreationFileTime.ToString(CultureInfo.InvariantCulture));

        int expected = IsDotnetHostShape(facts) ? 8 : 7;
        return arguments.Count == expected
            ? new ServiceAnchorElevationLaunchSpec(facts.ProcessPath!, arguments)
            : null;
    }
}

/// <summary>Bounded result of asking Windows to start the elevated helper.</summary>
internal enum ServiceAnchorElevatedLaunchState
{
    Unspecified = 0,
    Started = 1,

    /// <summary>ERROR_CANCELLED (1223): the administrator approval was declined.</summary>
    Declined = 2,

    Failed = 3,
}

/// <summary>
/// The bounded process-handle abstraction the coordinator is allowed to see. It
/// exposes only what the sequential-launch ruling requires - has it exited, wait
/// for exit, what was the exit code - and nothing that could start, signal, kill
/// or inspect a process.
/// </summary>
internal interface IServiceAnchorElevatedProcess : IDisposable
{
    bool HasExited { get; }

    bool WaitForExit(TimeSpan timeout);

    int ExitCode { get; }
}

internal readonly struct ServiceAnchorElevatedLaunch
{
    private ServiceAnchorElevatedLaunch(ServiceAnchorElevatedLaunchState state, IServiceAnchorElevatedProcess? process)
    {
        State = state;
        Process = process;
    }

    internal ServiceAnchorElevatedLaunchState State { get; }

    internal IServiceAnchorElevatedProcess? Process { get; }

    internal static ServiceAnchorElevatedLaunch Started(IServiceAnchorElevatedProcess process) =>
        new(ServiceAnchorElevatedLaunchState.Started, process);

    internal static ServiceAnchorElevatedLaunch Declined() =>
        new(ServiceAnchorElevatedLaunchState.Declined, null);

    internal static ServiceAnchorElevatedLaunch Failed() =>
        new(ServiceAnchorElevatedLaunchState.Failed, null);

    public override string ToString() => State.ToString();
}

internal interface IServiceAnchorElevatedLauncher
{
    ServiceAnchorElevatedLaunch LaunchElevated(ServiceAnchorElevationLaunchSpec spec);
}

/// <summary>
/// The real launcher. It calls Process.Start with the runas verb and RETURNS -
/// it never waits - so the coordinator can immediately become the helper's
/// client. A declined UAC prompt is the documented ERROR_CANCELLED (1223)
/// Win32Exception, handled with the same pattern as the existing
/// RealElevatedLauncher, and is reported as a bounded decline rather than an
/// error. No native code, message or path escapes.
/// </summary>
internal sealed class RealServiceAnchorElevatedLauncher : IServiceAnchorElevatedLauncher
{
    private const int ErrorCancelled = 1223;

    public ServiceAnchorElevatedLaunch LaunchElevated(ServiceAnchorElevationLaunchSpec spec)
    {
        if (spec is null)
        {
            return ServiceAnchorElevatedLaunch.Failed();
        }

        string? commandLine = spec.TryComposeCommandLine();
        if (commandLine is null)
        {
            return ServiceAnchorElevatedLaunch.Failed();
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = spec.FileName,
                Arguments = commandLine,
                UseShellExecute = true,   // required for Verb = "runas"
                Verb = "runas",           // request elevation (UAC)
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process? started = Process.Start(startInfo);
            return started is null
                ? ServiceAnchorElevatedLaunch.Failed()
                : ServiceAnchorElevatedLaunch.Started(new RealServiceAnchorElevatedProcess(started));
        }
        catch (Win32Exception wex) when (wex.NativeErrorCode == ErrorCancelled)
        {
            return ServiceAnchorElevatedLaunch.Declined();
        }
        catch (Exception)
        {
            return ServiceAnchorElevatedLaunch.Failed();
        }
    }

    private sealed class RealServiceAnchorElevatedProcess : IServiceAnchorElevatedProcess
    {
        private readonly Process _process;

        internal RealServiceAnchorElevatedProcess(Process process) => _process = process;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (Exception)
                {
                    // An unobservable process is treated as still running; the
                    // bounded wait, not this probe, is what ends the operation.
                    return false;
                }
            }
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            try
            {
                return _process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 0d, int.MaxValue));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public int ExitCode
        {
            get
            {
                try
                {
                    return _process.ExitCode;
                }
                catch (Exception)
                {
                    return int.MinValue;
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _process.Dispose();
            }
            catch (Exception)
            {
                // Best effort; the handle is released either way.
            }
        }
    }
}

/// <summary>
/// The client half of the identity channel, behind an interface so the focused
/// tests can prove that a DECLINED approval performs no client transaction at
/// all. Production always uses <see cref="RealServiceAnchorChannelClient"/>.
/// </summary>
internal interface IServiceAnchorChannelClient
{
    ServiceInitiatingUserIdentityChannelOutcome Request(string endpointName, TimeSpan timeout);
}

internal sealed class RealServiceAnchorChannelClient : IServiceAnchorChannelClient
{
    public ServiceInitiatingUserIdentityChannelOutcome Request(string endpointName, TimeSpan timeout) =>
        ServiceInitiatingUserIdentityChannelClient.Request(endpointName, timeout);
}

/// <summary>
/// THE DEDICATED NON-ELEVATED COORDINATOR. One transaction, one helper, one
/// bounded outcome.
/// </summary>
internal static class ServiceAnchorElevationCoordinator
{
    /// <summary>How long the rendezvous may take, measured only AFTER the helper started.</summary>
    internal static readonly TimeSpan DefaultChannelTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How long the helper's own exit is observed after the transaction.</summary>
    internal static readonly TimeSpan DefaultHelperExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Granularity of the interleaved transaction / helper-exit wait.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>The fixed production entry point. It takes no path and no identity.</summary>
    internal static ServiceAnchorElevationOutcome Run() =>
        Run(
            new RealServiceAnchorElevationProcessFacts(),
            new RealServiceAnchorElevatedLauncher(),
            new RealServiceAnchorChannelClient(),
            DefaultChannelTimeout,
            DefaultHelperExitTimeout);

    internal static ServiceAnchorElevationOutcome Run(
        IServiceAnchorElevationProcessFacts facts,
        IServiceAnchorElevatedLauncher launcher,
        IServiceAnchorChannelClient client,
        TimeSpan channelTimeout,
        TimeSpan helperExitTimeout)
    {
        if (!OperatingSystem.IsWindows() || facts is null || launcher is null || client is null)
        {
            return ServiceAnchorElevationOutcome.UnsupportedPlatform;
        }

        ServiceInitiatorProcessFacts initiator = facts.CaptureInitiator();
        if (!initiator.IsPresent)
        {
            return ServiceAnchorElevationOutcome.InitiatorFactsUnavailable;
        }

        // The initiator creates ONLY the rendezvous NAME. It never creates the
        // pipe - the elevated helper does - and it never creates the challenge.
        string endpointName = ServiceInitiatingUserIdentityChannelContract.NewEndpointName();

        ServiceAnchorElevationLaunchSpec? spec =
            ServiceAnchorElevationLaunchSpecResolver.TryResolve(facts, endpointName, initiator);
        if (spec is null)
        {
            return ServiceAnchorElevationOutcome.LaunchSpecUnavailable;
        }

        // NO CHANNEL CLOCK IS RUNNING YET. Human UAC response time is spent
        // entirely inside this call and cannot consume the rendezvous budget.
        ServiceAnchorElevatedLaunch launch = launcher.LaunchElevated(spec);
        if (launch.State == ServiceAnchorElevatedLaunchState.Declined)
        {
            // Return IMMEDIATELY, without starting any client.
            return ServiceAnchorElevationOutcome.ApprovalDeclined;
        }
        if (launch.State != ServiceAnchorElevatedLaunchState.Started || launch.Process is null)
        {
            return ServiceAnchorElevationOutcome.HelperLaunchFailed;
        }

        using IServiceAnchorElevatedProcess helper = launch.Process;
        return RunTransaction(helper, client, endpointName, channelTimeout, helperExitTimeout);
    }

    private static ServiceAnchorElevationOutcome RunTransaction(
        IServiceAnchorElevatedProcess helper,
        IServiceAnchorChannelClient client,
        string endpointName,
        TimeSpan channelTimeout,
        TimeSpan helperExitTimeout)
    {
        Task<ServiceInitiatingUserIdentityChannelOutcome> transaction;
        try
        {
            transaction = Task.Run(() => client.Request(endpointName, channelTimeout));
        }
        catch (Exception)
        {
            return ServiceAnchorElevationOutcome.AcknowledgementNotReceived;
        }

        // The clock starts HERE, after a successful launch. The wait ends early
        // if the helper dies, so a helper that fails before creating its pipe
        // cannot hold the initiator for the whole rendezvous budget.
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            bool completed;
            try
            {
                completed = transaction.Wait(PollInterval);
            }
            catch (Exception)
            {
                return ServiceAnchorElevationOutcome.AcknowledgementNotReceived;
            }

            if (completed)
            {
                break;
            }
            if (helper.HasExited)
            {
                return ServiceAnchorElevationOutcome.HelperExitedBeforeTransaction;
            }
            if (elapsed.Elapsed >= channelTimeout)
            {
                return ServiceAnchorElevationOutcome.TransactionTimedOut;
            }
        }

        ServiceInitiatingUserIdentityChannelOutcome outcome;
        try
        {
            outcome = transaction.Result;
        }
        catch (Exception)
        {
            return ServiceAnchorElevationOutcome.AcknowledgementNotReceived;
        }

        if (outcome == ServiceInitiatingUserIdentityChannelOutcome.AnchorConflict)
        {
            // Existing anchor state is preserved, never repaired. Recovery is a
            // future attended, elevated operation.
            return ServiceAnchorElevationOutcome.RecoveryRequired;
        }
        if (outcome != ServiceInitiatingUserIdentityChannelOutcome.Completed)
        {
            return ServiceAnchorElevationOutcome.AcknowledgementNotReceived;
        }

        // The helper's exit is observed DIRECTLY, not inferred.
        if (!helper.WaitForExit(helperExitTimeout))
        {
            return ServiceAnchorElevationOutcome.HelperDidNotExit;
        }

        return helper.ExitCode == PAXCookbook.Shared.ExitCodes.SetupExitCodes.Ok
            ? ServiceAnchorElevationOutcome.Completed
            : ServiceAnchorElevationOutcome.HelperReportedFailure;
    }
}
