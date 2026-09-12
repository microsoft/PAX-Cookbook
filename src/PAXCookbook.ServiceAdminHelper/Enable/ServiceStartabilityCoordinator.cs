// PAX Cookbook - SERVICE STARTABILITY VERIFICATION (cycle 63R, helper only)
//
// WHAT THIS FILE IS. The ONE place that turns "the service is registered" into
// "the service demonstrably started, is running as the right identity in Session
// 0, and is alive". Registration proved none of that; this file is the Cycle 62
// obligation being discharged.
//
// THE ORDER, and it is not negotiable:
//   1. StartServiceW - with NO argument vector - only when a start is authorized.
//   2. Wait, BOUNDED, for the SCM to report Running. A pending state is not a
//      running state and is never accepted early.
//   3. Verify the LIVE process the SCM reports: token user SID must equal the
//      fixed resolved service SID, and the session must be Session 0. This is a
//      READ-ONLY check; nothing is ever signalled or terminated.
//   4. Verify the status document under the CLOSED schema rules, inside a
//      BOUNDED 60 second readiness window. ONLY an absent document and a
//      well-formed, correct-context "starting" document are retried; EVERY
//      other refusal fails IMMEDIATELY with no further sleep.
//   5. Prove the heartbeat ADVANCES: two valid observations, bounded by a 45
//      second window, with DIFFERENT timestamps whose second value is strictly
//      later than the first. One valid heartbeat proves a file exists; only an
//      advancing pair proves a live host.
//   6. Verify no PAX or Bake child process exists beneath the service process.
//
// CYCLE 75 - WHY STEP 4 IS NO LONGER ONE SHOT. The service's own worker writes
// "starting" BEFORE it finishes coming up and only then writes "running", so a
// single immediate read can legitimately observe a document that is absent or
// still "starting". That was reported as a refusal. Waiting is allowed ONLY for
// those two TRANSIENT observations, the window is fixed and bounded, and the
// verdict names WHICH transient state was last seen so a timeout is still
// specific. Nothing is repaired, rewritten or deleted, and readiness is never
// inferred from a filesystem timestamp.
//
// LIVENESS IS NEVER INFERRED FROM A FILE TIMESTAMP. The document verifier reads
// no filesystem metadata, and neither does this coordinator.
//
// EVERY WAIT IS BOUNDED. There is no unbounded loop, no retry-forever and no
// exponential backoff. A wait that expires is a refusal.
//
// PRIVACY - FAIL CLOSED. Every answer is a bounded token. No path, process id,
// SID, timestamp, document body or native status is ever returned.
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>The fixed, bounded startability budgets. Compile-time constants only.</summary>
internal static class ServiceStartabilityContract
{
    /// <summary>How long the SCM may take to report Running after a start.</summary>
    internal static readonly TimeSpan RunningWait = TimeSpan.FromSeconds(60);

    /// <summary>How long the SCM may take to report Stopped after a stop.</summary>
    internal static readonly TimeSpan StoppedWait = TimeSpan.FromSeconds(60);

    /// <summary>How long SCM absence may take to become observable after a delete.</summary>
    internal static readonly TimeSpan AbsenceWait = TimeSpan.FromSeconds(30);

    /// <summary>The bounded window for proving TWO advancing heartbeat observations.</summary>
    internal static readonly TimeSpan HeartbeatProofWindow = TimeSpan.FromSeconds(45);

    /// <summary>
    /// CYCLE 75. The bounded window in which the status document may become a
    /// valid, correct-context "running" document. It is a FIXED budget, not a
    /// retry-until-success loop, and only two observations may consume it.
    /// </summary>
    internal static readonly TimeSpan StatusReadinessWindow = TimeSpan.FromSeconds(60);

    /// <summary>Polling granularity for every bounded wait in this file.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The number of ADVANCING heartbeat observations required. Two, never one.</summary>
    internal const int RequiredHeartbeatObservations = 2;
}

/// <summary>
/// The CLOSED set of startability verdicts. Zero is the permanent, safe default
/// so an uninitialised value can never read as verified.
/// </summary>
internal enum ServiceStartabilityState
{
    Unspecified = 0,

    /// <summary>Running, correct identity, Session 0, valid status, advancing heartbeat, no PAX child.</summary>
    Verified = 1,

    /// <summary>StartServiceW refused.</summary>
    StartRefused = 2,

    /// <summary>The SCM never reported Running inside the bounded wait.</summary>
    NeverReachedRunning = 3,

    /// <summary>The live process is not the fixed service identity, or not in Session 0.</summary>
    ProcessIdentityRefused = 4,

    /// <summary>
    /// The status document failed the closed schema, state, session or
    /// interactivity rules. RETAINED so an already-emitted code still decodes;
    /// NO live branch produces it after cycle 75.
    /// </summary>
    StatusDocumentRefused = 5,

    /// <summary>Two valid, strictly advancing heartbeat observations were not seen in the window.</summary>
    HeartbeatDidNotAdvance = 6,

    /// <summary>A PAX or Bake child process was observed beneath the service process.</summary>
    ForbiddenChildProcess = 7,

    /// <summary>A bounded access or stability failure. NEVER treated as verified.</summary>
    Unavailable = 8,

    // ---- CYCLE 75: THE BOUNDED STATUS-READINESS VERDICTS ------------------
    //
    // APPENDED, never renumbered. The FIRST TWO are DEADLINE verdicts and are
    // distinguished by the LAST TRANSIENT STATE actually observed. The rest are
    // TERMINAL and stop the wait immediately with no further sleep.

    /// <summary>The window expired having observed ONLY an absent status document.</summary>
    StatusMissingTimeout = 9,

    /// <summary>
    /// The window expired after at least one valid, correct-context "starting"
    /// document. The service was demonstrably coming up and did not finish.
    /// </summary>
    StatusStartingTimeout = 10,

    /// <summary>The status document exceeded the hard size bound or could not be read.</summary>
    StatusUnreadable = 11,

    /// <summary>The status document failed the strict UTF-8, JSON, property-set or value rules.</summary>
    StatusMalformed = 12,

    /// <summary>The status document reported a session other than 0, or an interactive host.</summary>
    StatusWrongContext = 13,

    /// <summary>The status document reported "stopped".</summary>
    StatusStopped = 14,

    /// <summary>The status document reported "failed".</summary>
    StatusFailed = 15,

    /// <summary>The status document reported a state string this build does not recognise.</summary>
    StatusUnknownState = 16,
}

/// <summary>
/// The clock seam. An interface ONLY so the focused tests can drive every
/// bounded wait deterministically and instantly. Production always uses
/// <see cref="RealServiceStartabilityClock"/>.
/// </summary>
internal interface IServiceStartabilityClock
{
    DateTimeOffset UtcNow { get; }

    void Sleep(TimeSpan duration);
}

internal sealed class RealServiceStartabilityClock : IServiceStartabilityClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
}

/// <summary>
/// Observes whether a PAX or Bake child process exists beneath a given process.
/// An interface ONLY so the focused tests can prove both answers without
/// launching anything. Production always uses
/// <see cref="WindowsServiceChildProcessObserver"/>.
/// </summary>
internal interface IServiceChildProcessObserver
{
    /// <summary>
    /// True when any direct child of the given process has an image name in the
    /// CLOSED forbidden set. It never returns a name, a path or a process id.
    /// </summary>
    bool AnyForbiddenChildProcess(uint parentProcessId);
}

/// <summary>
/// The real observer. It takes a read-only process snapshot and compares image
/// names against a closed list; it opens no process, reads no memory and signals
/// nothing.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceChildProcessObserver : IServiceChildProcessObserver
{
    /// <summary>
    /// The CLOSED forbidden child image names. The service must never have
    /// spawned an engine or a shell during startability verification.
    /// </summary>
    internal static readonly string[] ForbiddenChildImageNames =
    {
        "pwsh.exe", "powershell.exe", "cmd.exe", "wscript.exe", "cscript.exe",
    };

    private const uint Th32CsSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        internal uint dwSize;
        internal uint cntUsage;
        internal uint th32ProcessID;
        internal IntPtr th32DefaultHeapID;
        internal uint th32ModuleID;
        internal uint cntThreads;
        internal uint th32ParentProcessID;
        internal int pcPriClassBase;
        internal uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public bool AnyForbiddenChildProcess(uint parentProcessId)
    {
        if (parentProcessId == 0)
        {
            // Fail closed: an unusable parent id cannot be proven clean.
            return true;
        }

        IntPtr snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return true;
        }

        try
        {
            var entry = default(ProcessEntry32);
            entry.dwSize = (uint)Marshal.SizeOf<ProcessEntry32>();

            if (!Process32FirstW(snapshot, ref entry))
            {
                return true;
            }

            do
            {
                if (entry.th32ParentProcessID != parentProcessId)
                {
                    continue;
                }

                foreach (string forbidden in ForbiddenChildImageNames)
                {
                    if (string.Equals(entry.szExeFile, forbidden, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            while (Process32NextW(snapshot, ref entry));

            return false;
        }
        catch (Exception)
        {
            return true;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }
}

/// <summary>
/// THE STARTABILITY COORDINATOR. Every collaborator is injected so no test can
/// reach the real Service Control Manager, a real process or the real machine.
/// </summary>
internal sealed class ServiceStartabilityCoordinator
{
    private readonly IServiceControlManagerAdapter _serviceControl;
    private readonly IServiceRunningProcessVerifier _process;
    private readonly IServiceRuntimeDocumentVerifier _documents;
    private readonly IServiceChildProcessObserver _children;
    private readonly IServiceStartabilityClock _clock;

    internal ServiceStartabilityCoordinator(
        IServiceControlManagerAdapter serviceControl,
        IServiceRunningProcessVerifier process,
        IServiceRuntimeDocumentVerifier documents,
        IServiceChildProcessObserver children,
        IServiceStartabilityClock clock)
    {
        _serviceControl = serviceControl;
        _process = process;
        _documents = documents;
        _children = children;
        _clock = clock;
    }

    /// <summary>
    /// Starts the fixed service and verifies it end to end. Used ONLY when this
    /// attempt is authorized to start the service.
    /// </summary>
    internal ServiceStartabilityState StartAndVerify(
        string serviceSid, string statusPath, string heartbeatPath)
    {
        if (!ServiceStartAttemptResultMap.IsStarted(
                _serviceControl.TryStartService(ServiceIdentityContract.ServiceName)))
        {
            return ServiceStartabilityState.StartRefused;
        }

        return Verify(serviceSid, statusPath, heartbeatPath);
    }

    /// <summary>
    /// Verifies an ALREADY-RUNNING service without starting it. This is the R4
    /// path: a service that already satisfies every exact check is acknowledged,
    /// never restarted and never recreated.
    /// </summary>
    internal ServiceStartabilityState Verify(string serviceSid, string statusPath, string heartbeatPath)
    {
        if (string.IsNullOrWhiteSpace(serviceSid))
        {
            return ServiceStartabilityState.Unavailable;
        }

        ServiceStatusSnapshot snapshot;
        if (!WaitForRunning(out snapshot))
        {
            return ServiceStartabilityState.NeverReachedRunning;
        }

        ServiceProcessIdentityState identity = _process.Verify(snapshot.ProcessId, serviceSid);
        if (identity != ServiceProcessIdentityState.Verified)
        {
            return ServiceStartabilityState.ProcessIdentityRefused;
        }

        if (!WaitForRunningStatus(statusPath, out ServiceStartabilityState statusRefusal))
        {
            return statusRefusal;
        }

        if (!ProveHeartbeatAdvances(heartbeatPath))
        {
            return ServiceStartabilityState.HeartbeatDidNotAdvance;
        }

        return _children.AnyForbiddenChildProcess(snapshot.ProcessId)
            ? ServiceStartabilityState.ForbiddenChildProcess
            : ServiceStartabilityState.Verified;
    }

    /// <summary>
    /// THE BOUNDED STATUS-READINESS WAIT. It returns true ONLY on a valid,
    /// correct-context "running" document, and it returns the instant it sees
    /// one.
    ///
    /// EXACTLY TWO observations are TRANSIENT and may consume the window: an
    /// ABSENT document, and a well-formed, correct-execution-context "starting"
    /// document. Both are states the service's own worker legitimately produces
    /// while it is coming up.
    ///
    /// EVERY OTHER VERDICT IS TERMINAL AND RETURNS IMMEDIATELY, with no further
    /// sleep: unreadable, malformed, wrong execution context, stopped, failed,
    /// and an unrecognised state string. Waiting on those would only postpone a
    /// decision that is already made.
    ///
    /// THE DEADLINE VERDICT DEPENDS ON WHAT WAS ACTUALLY OBSERVED. If a valid
    /// "starting" was ever seen the timeout says so; if the document was only
    /// ever absent, the timeout says that instead. The two are different
    /// diagnoses and are never merged.
    ///
    /// IT NEVER REPAIRS. No document is created, rewritten, moved or deleted,
    /// and no filesystem timestamp is read - liveness and readiness come from
    /// the document's own contents or from nothing at all.
    /// </summary>
    internal bool WaitForRunningStatus(string statusPath, out ServiceStartabilityState refusal)
    {
        refusal = ServiceStartabilityState.Unspecified;

        DateTimeOffset deadline = _clock.UtcNow + ServiceStartabilityContract.StatusReadinessWindow;
        bool observedStarting = false;

        while (true)
        {
            ServiceRuntimeDocumentState observed = _documents.VerifyStatus(statusPath).State;

            switch (observed)
            {
                case ServiceRuntimeDocumentState.Valid:
                    return true;

                case ServiceRuntimeDocumentState.Missing:
                    break;

                case ServiceRuntimeDocumentState.Starting:
                    observedStarting = true;
                    break;

                default:
                    refusal = TerminalStatusRefusal(observed);
                    return false;
            }

            if (_clock.UtcNow >= deadline)
            {
                refusal = observedStarting
                    ? ServiceStartabilityState.StatusStartingTimeout
                    : ServiceStartabilityState.StatusMissingTimeout;
                return false;
            }

            _clock.Sleep(ServiceStartabilityContract.PollInterval);
        }
    }

    /// <summary>
    /// The TOTAL terminal-refusal map. Every value is a refusal, and an
    /// unrecognised verdict falls to the fail-closed
    /// <see cref="ServiceStartabilityState.StatusUnknownState"/> rather than to
    /// anything that could read as ready. It never returns a document value.
    /// </summary>
    internal static ServiceStartabilityState TerminalStatusRefusal(
        ServiceRuntimeDocumentState observed) => observed switch
    {
        ServiceRuntimeDocumentState.Unreadable => ServiceStartabilityState.StatusUnreadable,
        ServiceRuntimeDocumentState.Malformed => ServiceStartabilityState.StatusMalformed,
        ServiceRuntimeDocumentState.WrongExecutionContext => ServiceStartabilityState.StatusWrongContext,
        ServiceRuntimeDocumentState.Stopped => ServiceStartabilityState.StatusStopped,
        ServiceRuntimeDocumentState.Failed => ServiceStartabilityState.StatusFailed,
        _ => ServiceStartabilityState.StatusUnknownState,
    };

    /// <summary>
    /// THE BOUNDED HEARTBEAT PROOF. Two valid observations whose timestamps
    /// DIFFER, with the second strictly later than the first. A repeated
    /// identical timestamp is a stale file, not a live host, and is never
    /// counted as the second observation.
    /// </summary>
    internal bool ProveHeartbeatAdvances(string heartbeatPath)
    {
        DateTimeOffset deadline = _clock.UtcNow + ServiceStartabilityContract.HeartbeatProofWindow;
        DateTimeOffset? first = null;

        while (_clock.UtcNow < deadline)
        {
            ServiceRuntimeDocumentResult observation = _documents.VerifyHeartbeat(heartbeatPath);
            if (observation.IsValid)
            {
                if (first is null)
                {
                    first = observation.TimestampUtc;
                }
                else if (observation.TimestampUtc > first.Value)
                {
                    return true;
                }
            }

            _clock.Sleep(ServiceStartabilityContract.PollInterval);
        }

        return false;
    }

    /// <summary>Bounded wait for the SCM to report Running. Pending is never accepted.</summary>
    internal bool WaitForRunning(out ServiceStatusSnapshot snapshot)
    {
        snapshot = default;
        DateTimeOffset deadline = _clock.UtcNow + ServiceStartabilityContract.RunningWait;

        while (true)
        {
            if (_serviceControl.QueryStatus(ServiceIdentityContract.ServiceName, out ServiceStatusSnapshot current)
                    == ServiceQueryState.Present
                && current.State == ServiceRunState.Running
                && current.ProcessId != 0)
            {
                snapshot = current;
                return true;
            }

            if (_clock.UtcNow >= deadline)
            {
                return false;
            }

            _clock.Sleep(ServiceStartabilityContract.PollInterval);
        }
    }

    /// <summary>Bounded wait for the SCM to report Stopped after a stop request.</summary>
    internal bool WaitForStopped()
    {
        DateTimeOffset deadline = _clock.UtcNow + ServiceStartabilityContract.StoppedWait;

        while (true)
        {
            ServiceQueryState state =
                _serviceControl.QueryStatus(ServiceIdentityContract.ServiceName, out ServiceStatusSnapshot current);

            if (state == ServiceQueryState.Absent
                || (state == ServiceQueryState.Present && current.State == ServiceRunState.Stopped))
            {
                return true;
            }

            if (_clock.UtcNow >= deadline)
            {
                return false;
            }

            _clock.Sleep(ServiceStartabilityContract.PollInterval);
        }
    }

    /// <summary>Bounded wait for SCM absence after a delete. Absence is VERIFIED, never assumed.</summary>
    internal bool WaitForAbsence()
    {
        DateTimeOffset deadline = _clock.UtcNow + ServiceStartabilityContract.AbsenceWait;

        while (true)
        {
            if (_serviceControl.QueryConfiguration(ServiceIdentityContract.ServiceName, out _)
                == ServiceQueryState.Absent)
            {
                return true;
            }

            if (_clock.UtcNow >= deadline)
            {
                return false;
            }

            _clock.Sleep(ServiceStartabilityContract.PollInterval);
        }
    }
}
