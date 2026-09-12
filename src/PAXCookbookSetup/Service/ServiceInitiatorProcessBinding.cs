// PAX Cookbook - INITIATOR PROCESS BINDING (cycle 59, Setup only)
//
// WHAT THIS FILE IS. The one place that binds a process by BOTH its process id
// AND its exact Windows creation FILETIME, and that resolves the SID of that
// bound process from its own kernel token.
//
// WHY BOTH VALUES. A process id alone is NOT an identity: Windows reuses pids,
// so a pid captured by a non-elevated initiator can, by the time an elevated
// helper looks at it, name a completely different process - potentially one a
// hostile local user created on purpose. The creation FILETIME is assigned by
// the kernel at process creation and is not reusable together with the same
// pid, so the PAIR is a stable handle to one specific process instance. The
// cycle-59 developer ruling REJECTS pid-only admission for exactly this reason.
//
// WHAT THIS IS USED FOR, AND WHAT IT IS NOT. The bound SID resolved here is
// ADMISSION FILTERING ONLY. It decides who the elevated helper is willing to
// open a pipe for, and it is the single ACE in that pipe's DACL. It is NEVER
// the value written into the installation anchor: ownership comes exclusively
// from the SID the kernel reports for the LIVE pipe connection. If the two
// ever disagree, that disagreement is a terminal refusal.
//
// PRIVACY - FAIL CLOSED. Nothing here logs, and no result type can carry a
// pid, a FILETIME, a SID, a path, a handle, a native status or an exception.
// Do not persist or log the pid, the creation-time hint, the provisional DACL
// identity, or the elevated approver identity. The anchor intentionally
// persists only the SID independently derived from the live pipe connection.
//
// WHAT THIS FILE CANNOT DO, by construction. It never starts a process, never
// writes a file, never opens a certificate store or private key, never reads
// or writes an ACL, the registry or a credential vault, never creates, changes,
// starts or stops a service, never elevates, never opens a socket, never
// touches PAX and never starts a Bake. It opens two kinds of read-only kernel
// handle - a query-limited process handle and a query-only token handle - and
// closes both on every path.
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PAXCookbookSetup.Service;

/// <summary>
/// The immutable pair that names ONE process instance: its process id and the
/// exact Windows creation FILETIME the kernel assigned it. Neither value is an
/// authority on its own and neither is ever persisted.
/// </summary>
internal readonly struct ServiceInitiatorProcessFacts
{
    internal ServiceInitiatorProcessFacts(uint processId, long creationFileTime)
    {
        ProcessId = processId;
        CreationFileTime = creationFileTime;
    }

    internal uint ProcessId { get; }

    internal long CreationFileTime { get; }

    /// <summary>
    /// True only when BOTH values are usable. Pid 0 is the system idle process
    /// and a zero FILETIME is never a real creation time, so the default value
    /// can never read as a bound process.
    /// </summary>
    internal bool IsPresent => ProcessId != 0 && CreationFileTime > 0;

    /// <summary>Carries the bounded type name only - never the pid or the FILETIME.</summary>
    public override string ToString() => nameof(ServiceInitiatorProcessFacts);
}

/// <summary>
/// Resolves the identity of a PID + creation-FILETIME bound process. It is an
/// interface ONLY so the focused tests can prove that the elevated helper's
/// DACL comes from this resolver and never from the elevated account itself;
/// production always uses <see cref="WindowsServiceInitiatorIdentityResolver"/>.
/// </summary>
internal interface IServiceInitiatorIdentityResolver
{
    /// <summary>
    /// The SID of the process named by BOTH values, or null when the process is
    /// gone, its creation FILETIME differs, or its token cannot be read. This is
    /// the ONLY member, deliberately: the helper's before-pipe and
    /// after-connection checks are the SAME question asked twice, and a second
    /// weaker "is it still alive" probe would be a redundant surface that could
    /// drift out of agreement with this one.
    /// </summary>
    string? TryResolveBoundInitiatorSid(ServiceInitiatorProcessFacts initiator);
}

/// <summary>The real Windows resolver. Read-only kernel queries only.</summary>
internal sealed class WindowsServiceInitiatorIdentityResolver : IServiceInitiatorIdentityResolver
{
    public string? TryResolveBoundInitiatorSid(ServiceInitiatorProcessFacts initiator) =>
        ServiceInitiatorProcessBinding.TryResolveBoundSid(initiator);
}

/// <summary>
/// The native binding primitives. Every entry point fails closed: any failure
/// at any step returns null or false rather than a partially trusted value.
/// </summary>
internal static class ServiceInitiatorProcessBinding
{
    // PROCESS_QUERY_LIMITED_INFORMATION is deliberately the weakest right that
    // still answers "when was this created" and "who owns it". It grants no
    // read of process memory, no write, no terminate and no handle duplication.
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint TokenQuery = 0x00000008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// This process's own pid paired with its own kernel creation FILETIME.
    /// Returns the default (not present) value if either cannot be read.
    /// </summary>
    internal static ServiceInitiatorProcessFacts CaptureCurrent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return default;
        }

        int ownProcessId = Environment.ProcessId;
        if (ownProcessId <= 0)
        {
            return default;
        }

        long? creation = TryReadCreationFileTime((uint)ownProcessId);
        return creation is long value && value > 0
            ? new ServiceInitiatorProcessFacts((uint)ownProcessId, value)
            : default;
    }

    /// <summary>The kernel creation FILETIME of a live process, or null.</summary>
    internal static long? TryReadCreationFileTime(uint processId)
    {
        if (!OperatingSystem.IsWindows() || processId == 0)
        {
            return null;
        }

        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return GetProcessTimes(process, out long creation, out _, out _, out _) && creation > 0
                ? creation
                : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    /// THE BINDING SEQUENCE. Open the process by pid; read its creation
    /// FILETIME and require an EXACT match before anything else is trusted;
    /// only then open its token and read the SID. A pid whose process was
    /// recycled fails at the FILETIME comparison and never reaches the token.
    /// </summary>
    internal static string? TryResolveBoundSid(ServiceInitiatorProcessFacts initiator)
    {
        if (!OperatingSystem.IsWindows() || !initiator.IsPresent)
        {
            return null;
        }

        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, initiator.ProcessId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        IntPtr token = IntPtr.Zero;
        try
        {
            if (!GetProcessTimes(process, out long creation, out _, out _, out _)
                || creation != initiator.CreationFileTime)
            {
                return null;
            }

            if (!OpenProcessToken(process, TokenQuery, out token) || token == IntPtr.Zero)
            {
                return null;
            }

            using var identity = new WindowsIdentity(token);
            return identity.User?.Value;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
            CloseHandle(process);
        }
    }
}
