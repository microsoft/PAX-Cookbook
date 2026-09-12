// PAX Cookbook - READ-ONLY RUNNING SERVICE PROCESS VERIFIER (cycle 63R, helper)
//
// WHAT THIS FILE IS. The ONE place that inspects the LIVE process the Service
// Control Manager reports for the fixed service. It answers exactly two
// questions - "is its token user the fixed service SID?" and "is it in Session
// 0?" - and it answers them READ ONLY.
//
// WHY THE TOKEN USER SID IS THE RIGHT QUESTION, and why it is valid here. The
// configured identity is the virtual account NT SERVICE\PAXCookbookService. For
// a virtual account the per-service SID IS the token user SID of the running
// service process, so comparing the token user against the resolved service SID
// is an exact identity check and not an approximation. This reasoning depends on
// the configured account: it would NOT hold for LocalSystem, LocalService or
// NetworkService, whose token user is the well-known account SID rather than the
// per-service SID. The configured identity is therefore deliberately NOT changed
// to LocalSystem.
//
// WHAT IT WILL NEVER DO. It never opens a process for anything beyond limited
// query and token read. It never terminates, suspends, resumes, debugs, reads or
// writes memory, injects, duplicates a handle, impersonates, or signals any
// process in any way. There is no TerminateProcess, no OpenThread and no
// PROCESS_ALL_ACCESS anywhere in this file.
//
// PRIVACY - FAIL CLOSED. Every answer is a bounded token. The process id, the
// token user SID, the image path, the token handle and the native error code are
// never returned, never logged and never embedded in any document.
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>
/// The CLOSED set of process-identity verdicts. Zero is the permanent, safe
/// default so an uninitialised value can never read as verified.
/// </summary>
internal enum ServiceProcessIdentityState
{
    Unspecified = 0,

    /// <summary>Token user is the fixed service SID AND the process is in Session 0.</summary>
    Verified = 1,

    /// <summary>The reported process id was zero or otherwise not a live process.</summary>
    ProcessUnavailable = 2,

    /// <summary>The process exists but its token user is NOT the fixed service SID.</summary>
    IdentityMismatch = 3,

    /// <summary>The process runs in an interactive session. Session 0 is required.</summary>
    NotSessionZero = 4,

    /// <summary>A bounded access or stability failure. NEVER treated as verified.</summary>
    Unavailable = 5,
}

/// <summary>
/// The read-only process verifier. An interface ONLY so the focused tests can
/// prove every bounded verdict without a live service. Production always uses
/// <see cref="WindowsServiceRunningProcessVerifier"/>.
/// </summary>
internal interface IServiceRunningProcessVerifier
{
    /// <summary>
    /// Verifies ONLY the process id the SCM reported, against the already
    /// resolved fixed service SID. It takes no path, no name and no handle.
    /// </summary>
    ServiceProcessIdentityState Verify(uint processId, string expectedServiceSid);
}

/// <summary>The real Windows verifier. Query-only rights and nothing else.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceRunningProcessVerifier : IServiceRunningProcessVerifier
{
    /// <summary>
    /// PROCESS_QUERY_LIMITED_INFORMATION. Deliberately the weakest right that
    /// answers the question: it permits neither memory access nor termination.
    /// </summary>
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>TOKEN_QUERY. Read the token; never adjust, duplicate or impersonate it.</summary>
    private const uint TokenQuery = 0x0008;

    /// <summary>TokenUser.</summary>
    private const int TokenUserInformationClass = 1;

    private const int ErrorInsufficientBuffer = 122;

    /// <summary>The required session. A service that is not in Session 0 is refused.</summary>
    internal const uint RequiredSessionId = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr token, int informationClass, IntPtr information, uint length, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public ServiceProcessIdentityState Verify(uint processId, string expectedServiceSid)
    {
        if (processId == 0 || string.IsNullOrWhiteSpace(expectedServiceSid))
        {
            return ServiceProcessIdentityState.ProcessUnavailable;
        }

        IntPtr process = IntPtr.Zero;
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero)
            {
                return ServiceProcessIdentityState.ProcessUnavailable;
            }

            if (!ProcessIdToSessionId(processId, out uint sessionId))
            {
                return ServiceProcessIdentityState.Unavailable;
            }

            if (!OpenProcessToken(process, TokenQuery, out token) || token == IntPtr.Zero)
            {
                return ServiceProcessIdentityState.Unavailable;
            }

            if (GetTokenInformation(token, TokenUserInformationClass, IntPtr.Zero, 0, out uint needed)
                || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer
                || needed == 0 || needed > 4096)
            {
                return ServiceProcessIdentityState.Unavailable;
            }

            buffer = Marshal.AllocHGlobal((int)needed);
            if (!GetTokenInformation(token, TokenUserInformationClass, buffer, needed, out _))
            {
                return ServiceProcessIdentityState.Unavailable;
            }

            SidAndAttributes user = Marshal.PtrToStructure<SidAndAttributes>(buffer);
            string? actualSid = TryFormatSid(user.Sid);
            if (actualSid is null)
            {
                return ServiceProcessIdentityState.Unavailable;
            }

            // IDENTITY FIRST, then session: a foreign process must never be
            // reported merely as "wrong session".
            if (!string.Equals(actualSid, expectedServiceSid, StringComparison.Ordinal))
            {
                return ServiceProcessIdentityState.IdentityMismatch;
            }

            return sessionId == RequiredSessionId
                ? ServiceProcessIdentityState.Verified
                : ServiceProcessIdentityState.NotSessionZero;
        }
        catch (Exception)
        {
            return ServiceProcessIdentityState.Unavailable;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
            if (process != IntPtr.Zero)
            {
                CloseHandle(process);
            }
        }
    }

    private static string? TryFormatSid(IntPtr sid)
    {
        if (sid == IntPtr.Zero || !ConvertSidToStringSidW(sid, out IntPtr stringSid) || stringSid == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(stringSid);
        }
        finally
        {
            LocalFree(stringSid);
        }
    }
}
