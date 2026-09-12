// PAX Cookbook - SERVICE CONTROL MANAGER ADAPTER (cycle 62, helper only)
//
// WHAT THIS FILE IS. The ONE narrow adapter through which this product talks to
// the Service Control Manager, and the ONLY SCM P/Invoke site in the codebase.
//
// THE PERMITTED CALLS, and nothing else:
//     OpenSCManagerW, OpenServiceW, CreateServiceW, QueryServiceConfigW,
//     ChangeServiceConfig2W(SERVICE_CONFIG_SERVICE_SID_INFO),
//     QueryServiceConfig2W(SERVICE_CONFIG_SERVICE_SID_INFO),
//     DeleteService, CloseServiceHandle,
//     StartServiceW (cycle 63R, with ZERO arguments and a null argument vector),
//     QueryServiceStatusEx(SC_STATUS_PROCESS_INFO) (cycle 63R),
//     ControlService(SERVICE_CONTROL_STOP) (cycle 63R, the ONLY control code).
//
// THE FORBIDDEN MECHANISMS, named so a future reader cannot claim ambiguity:
// sc.exe, PowerShell service cmdlets, WMI, any shell command,
// System.ServiceProcess.ServiceController, an arbitrary service name, and every
// control code other than SERVICE_CONTROL_STOP - pause, continue, interrogate,
// shutdown, param-change, netbind, hardware-profile, power-event, session-change
// and every user-defined code are absent by construction.
//
// STARTING PROVES NOTHING BY ITSELF. Cycle 63R starts the service, but a
// Running SCM state is only the FIRST of several independent checks: the process
// token SID, Session 0, the closed status document and an ADVANCING heartbeat
// are all verified separately before any runtime-readiness claim.

//
// THE FIXED CONFIGURATION. Name, display name, account, start type, error
// control and service type are all compile-time facts. The binary path is
// composed from the fixed installed locations and never from a caller argument.
// The password is always null: NT SERVICE virtual accounts have none, and this
// adapter has no parameter that could carry one.
//
// CONFIGURATION VERIFICATION COMPARES THE ACCOUNT STRING. It never translates
// the configured account to a SID, because a name-to-SID translation is a
// different question with different failure modes and would silently accept a
// renamed or re-pointed account whose SID happened to match.
//
// PRIVACY - FAIL CLOSED. Nothing here returns a native status, a handle, an
// exception or an account password. Every answer is a bool plus a bounded
// snapshot of already-fixed values.
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.ServiceAdminHelper.Enable;

/// <summary>
/// The FIXED service registration facts. Every member is a compile-time
/// constant except the binary path, which is composed from the fixed installed
/// locations.
/// </summary>
internal static class ServiceEnableRegistrationContract
{
    /// <summary>SERVICE_WIN32_OWN_PROCESS.</summary>
    internal const uint ServiceTypeOwnProcess = 0x00000010;

    /// <summary>SERVICE_AUTO_START.</summary>
    internal const uint StartTypeAutomatic = 0x00000002;

    /// <summary>SERVICE_ERROR_NORMAL.</summary>
    internal const uint ErrorControlNormal = 0x00000001;

    /// <summary>SERVICE_SID_TYPE_UNRESTRICTED, which winsvc.h defines as 0x00000001.</summary>
    internal const uint ServiceSidTypeUnrestricted = 0x00000001;

    /// <summary>SERVICE_SID_TYPE_NONE - the default, and never acceptable here.</summary>
    internal const uint ServiceSidTypeNone = 0x00000000;

    /// <summary>The managed entry assembly the machine-wide runtime host must load.</summary>
    internal const string ServiceAssemblyFileName = "PAXCookbook.Service.dll";

    /// <summary>
    /// The exact ImagePath: the fixed machine-wide runtime host, then the fixed
    /// installed service assembly. Both are quoted, and neither comes from a
    /// caller. No additional argument is ever appended - the service receives no
    /// caller-controlled command line.
    /// </summary>
    internal static string ComposeBinaryPath(string runtimeHostPath, string finalServiceDirectory) =>
        "\"" + runtimeHostPath + "\" \""
        + System.IO.Path.Combine(finalServiceDirectory, ServiceAssemblyFileName) + "\"";
}

/// <summary>
/// A bounded snapshot of one service's configuration. It carries only values
/// this product already fixes, so nothing customer-identifying can travel in it.
/// </summary>
internal readonly struct ServiceConfigurationSnapshot
{
    internal ServiceConfigurationSnapshot(
        string serviceName,
        string displayName,
        string binaryPath,
        string accountName,
        uint serviceType,
        uint startType,
        uint errorControl,
        bool hasDependencies,
        bool hasLoadOrderGroup,
        bool hasTag)
    {
        ServiceName = serviceName;
        DisplayName = displayName;
        BinaryPath = binaryPath;
        AccountName = accountName;
        ServiceType = serviceType;
        StartType = startType;
        ErrorControl = errorControl;
        HasDependencies = hasDependencies;
        HasLoadOrderGroup = hasLoadOrderGroup;
        HasTag = hasTag;
    }

    internal string ServiceName { get; }

    internal string DisplayName { get; }

    internal string BinaryPath { get; }

    /// <summary>The configured account STRING. It is never translated to a SID.</summary>
    internal string AccountName { get; }

    internal uint ServiceType { get; }

    internal uint StartType { get; }

    internal uint ErrorControl { get; }

    internal bool HasDependencies { get; }

    internal bool HasLoadOrderGroup { get; }

    internal bool HasTag { get; }

    /// <summary>
    /// The EXACT expected configuration for one installation. Interactive is not
    /// a field because SERVICE_INTERACTIVE_PROCESS is never set: it is proven
    /// absent by <see cref="ServiceType"/> equalling own-process exactly.
    /// </summary>
    internal static ServiceConfigurationSnapshot Expected(string runtimeHostPath, string finalServiceDirectory) =>
        new(
            ServiceIdentityContract.ServiceName,
            ServiceIdentityContract.ServiceDisplayName,
            ServiceEnableRegistrationContract.ComposeBinaryPath(runtimeHostPath, finalServiceDirectory),
            ServiceIdentityContract.QualifiedServiceAccountName,
            ServiceEnableRegistrationContract.ServiceTypeOwnProcess,
            ServiceEnableRegistrationContract.StartTypeAutomatic,
            ServiceEnableRegistrationContract.ErrorControlNormal,
            hasDependencies: false,
            hasLoadOrderGroup: false,
            hasTag: false);

    /// <summary>
    /// Exact equality across every field. The account comparison is an ORDINAL
    /// STRING comparison and never a SID translation.
    /// </summary>
    internal bool MatchesExactly(ServiceConfigurationSnapshot other) =>
        string.Equals(ServiceName, other.ServiceName, StringComparison.Ordinal)
        && string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal)
        && string.Equals(BinaryPath, other.BinaryPath, StringComparison.Ordinal)
        && string.Equals(AccountName, other.AccountName, StringComparison.Ordinal)
        && ServiceType == other.ServiceType
        && StartType == other.StartType
        && ErrorControl == other.ErrorControl
        && HasDependencies == other.HasDependencies
        && HasLoadOrderGroup == other.HasLoadOrderGroup
        && HasTag == other.HasTag;

    /// <summary>Carries the bounded type name only.</summary>
    public override string ToString() => nameof(ServiceConfigurationSnapshot);
}

/// <summary>Bounded outcome of an SCM query.</summary>
internal enum ServiceQueryState
{
    Unspecified = 0,

    /// <summary>The service exists and its configuration was read.</summary>
    Present = 1,

    /// <summary>The service genuinely does not exist.</summary>
    Absent = 2,

    /// <summary>A bounded access or stability failure. NEVER treated as absence.</summary>
    Unavailable = 3,
}

/// <summary>
/// CYCLE 63R. The CLOSED set of run states this product recognises. Anything the
/// SCM reports outside this set becomes <see cref="Unrecognised"/> and is a
/// refusal, never an optimistic guess.
/// </summary>
internal enum ServiceRunState
{
    Unspecified = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
    Unrecognised = 8,
}

/// <summary>
/// A bounded status snapshot. The process id is carried ONLY so the read-only
/// process verifier can open that exact process; it is never returned to a
/// caller, logged, or embedded in any document.
/// </summary>
internal readonly struct ServiceStatusSnapshot
{
    internal ServiceStatusSnapshot(ServiceRunState state, uint processId)
    {
        State = state;
        ProcessId = processId;
    }

    internal ServiceRunState State { get; }

    internal uint ProcessId { get; }

    /// <summary>Carries the bounded state name only - never the process id.</summary>
    public override string ToString() => State.ToString();
}

/// <summary>
/// CYCLE 63R. The CLOSED native-to-bounded run-state map. It is pure and total,
/// and every value outside the documented set becomes Unrecognised rather than
/// being treated as any recognised state.
/// </summary>
internal static class ServiceRunStateMap
{
    internal static ServiceRunState FromNative(uint nativeState) => nativeState switch
    {
        1 => ServiceRunState.Stopped,
        2 => ServiceRunState.StartPending,
        3 => ServiceRunState.StopPending,
        4 => ServiceRunState.Running,
        5 => ServiceRunState.ContinuePending,
        6 => ServiceRunState.PausePending,
        7 => ServiceRunState.Paused,
        _ => ServiceRunState.Unrecognised,
    };
}

/// <summary>
/// CYCLE 74. The CLOSED set of outcomes ONE StartServiceW attempt can report.
///
/// WHY IT EXISTS. The attempt used to return a bare <c>bool</c>, so the Win32
/// reason was read and immediately DISCARDED - the same shape that made the
/// cycle-67 <c>registration_refused</c> category useless. A refusal that cannot
/// say WHICH refusal it was is only marginally better than exit code 1.
///
/// THE BOUNDARY IS THE POINT. Raw native values are interpreted INSIDE this
/// file and never leave it. No member carries a code, a path, an account, an
/// identity, a process id, a timestamp, an exception or free-form text; each is
/// a category name and nothing else.
///
/// Zero is the permanent, safe default so an uninitialised value can never read
/// as started.
/// </summary>
internal enum ServiceStartAttemptResult
{
    Unspecified = 0,

    /// <summary>StartServiceW itself succeeded.</summary>
    Started = 1,

    /// <summary>
    /// An instance was ALREADY running. The caller's intent - "this service is
    /// started" - already holds, so this is not a refusal.
    /// </summary>
    AlreadyRunning = 2,

    /// <summary>ERROR_SERVICE_LOGON_FAILED. The configured account cannot log on as a service.</summary>
    LogonRefused = 3,

    /// <summary>ERROR_ACCESS_DENIED. The handle lacks SERVICE_START.</summary>
    AccessRefused = 4,

    /// <summary>ERROR_PATH_NOT_FOUND. The service binary file could not be found.</summary>
    BinaryUnavailable = 5,

    /// <summary>ERROR_SERVICE_DEPENDENCY_FAIL or ERROR_SERVICE_DEPENDENCY_DELETED.</summary>
    DependencyRefused = 6,

    /// <summary>
    /// ERROR_SERVICE_REQUEST_TIMEOUT. A SYNCHRONOUS StartServiceW refusal: the
    /// SCM processes one control notification at a time and the call blocks for
    /// 30 seconds when another service is busy in a control handler.
    ///
    /// DESPITE THE WORD "TIMEOUT" THIS IS NOT "the service never reached
    /// Running". StartServiceW returns as soon as the dispatcher reports that
    /// the ServiceMain thread was created and does NOT wait for the first status
    /// update, so the two are different stages and are never folded together.
    /// </summary>
    RequestTimeout = 7,

    /// <summary>
    /// THE CLOSED FALLBACK. A documented code with no dedicated category, or any
    /// value at all that this build does not recognise. It is never a success
    /// and never a guess dressed up as a category.
    /// </summary>
    Refused = 8,
}

/// <summary>
/// CYCLE 74. The PURE, TOTAL native-to-bounded start-attempt interpreter, and
/// the ONLY place a StartServiceW Win32 value is given meaning.
///
/// IT IS TOTAL OVER EVERY <c>int</c>. The documented set is explicitly NOT
/// exhaustive - the API documentation warns that other codes can be set by the
/// registry functions the SCM calls - so an unrecognised value MUST fail closed
/// to <see cref="ServiceStartAttemptResult.Refused"/>.
///
/// THE FIVE DOCUMENTED CODES WITH NO DEDICATED CATEGORY ARE ROUTED EXPLICITLY.
/// ERROR_INVALID_HANDLE, ERROR_SERVICE_DATABASE_LOCKED, ERROR_SERVICE_DISABLED,
/// ERROR_SERVICE_MARKED_FOR_DELETE and ERROR_SERVICE_NO_THREAD reach the
/// fallback through a NAMED arm rather than by silent default, so the map stays
/// auditable and a future decision to name one of them is a visible edit.
/// </summary>
internal static class ServiceStartAttemptResultMap
{
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInvalidHandle = 6;
    internal const int ErrorServiceNoThread = 1054;
    internal const int ErrorServiceDatabaseLocked = 1055;
    internal const int ErrorServiceRequestTimeout = 1053;
    internal const int ErrorServiceAlreadyRunning = 1056;
    internal const int ErrorServiceDisabled = 1058;
    internal const int ErrorServiceDependencyFail = 1068;
    internal const int ErrorServiceLogonFailed = 1069;
    internal const int ErrorServiceMarkedForDelete = 1072;
    internal const int ErrorServiceDependencyDeleted = 1075;

    /// <summary>
    /// THE TOTAL interpreter. Every arm is NAMED, including the five documented
    /// codes that deliberately have no category of their own, so the fallback is
    /// reached deliberately rather than by silent default.
    /// </summary>
    internal static ServiceStartAttemptResult FromNativeError(int nativeError) => nativeError switch
    {
        ErrorServiceAlreadyRunning => ServiceStartAttemptResult.AlreadyRunning,
        ErrorServiceLogonFailed => ServiceStartAttemptResult.LogonRefused,
        ErrorAccessDenied => ServiceStartAttemptResult.AccessRefused,
        ErrorPathNotFound => ServiceStartAttemptResult.BinaryUnavailable,

        // BOTH documented dependency codes share one category: "a service this
        // one depends on is unusable" is a single operator-actionable fact, and
        // this product registers NO dependencies, so either value is equally
        // surprising.
        ErrorServiceDependencyFail or ErrorServiceDependencyDeleted =>
            ServiceStartAttemptResult.DependencyRefused,

        ErrorServiceRequestTimeout => ServiceStartAttemptResult.RequestTimeout,

        // DOCUMENTED, DELIBERATELY UNNAMED. Each is routed here explicitly so
        // that naming one later is a visible edit rather than a silent
        // behaviour change.
        ErrorInvalidHandle
            or ErrorServiceDatabaseLocked
            or ErrorServiceDisabled
            or ErrorServiceMarkedForDelete
            or ErrorServiceNoThread => ServiceStartAttemptResult.Refused,

        // THE DOCUMENTED SET IS NOT EXHAUSTIVE: the API documentation warns that
        // other codes can be set by the registry functions the SCM calls. An
        // unrecognised value is never guessed at.
        _ => ServiceStartAttemptResult.Refused,
    };

    /// <summary>
    /// The ONLY two results that mean "the service is started". Everything else,
    /// including <see cref="ServiceStartAttemptResult.Unspecified"/>, is a
    /// refusal.
    /// </summary>
    internal static bool IsStarted(ServiceStartAttemptResult result) =>
        result == ServiceStartAttemptResult.Started
        || result == ServiceStartAttemptResult.AlreadyRunning;
}

/// <summary>
/// The narrow SCM adapter. An interface ONLY so the focused tests can prove
/// every ordering, verification and compensation path without querying or
/// mutating the real Service Control Manager. Production always uses
/// <see cref="WindowsServiceControlManagerAdapter"/>.
/// </summary>
internal interface IServiceControlManagerAdapter
{
    /// <summary>Reads one service's configuration, or reports bounded absence.</summary>
    ServiceQueryState QueryConfiguration(string serviceName, out ServiceConfigurationSnapshot snapshot);

    /// <summary>Creates the service with the exact fixed configuration. It is NEVER started.</summary>
    bool TryCreateService(ServiceConfigurationSnapshot desired);

    /// <summary>ChangeServiceConfig2(SERVICE_CONFIG_SERVICE_SID_INFO) to unrestricted.</summary>
    bool TrySetUnrestrictedServiceSidType(string serviceName);

    /// <summary>QueryServiceConfig2(SERVICE_CONFIG_SERVICE_SID_INFO).</summary>
    bool TryQueryServiceSidType(string serviceName, out uint sidType);

    /// <summary>
    /// Resolves the fixed service's SID. Called ONLY after the service exists
    /// and is configured unrestricted - a deliberate policy choice, not a
    /// technical necessity (see the transaction's header).
    /// </summary>
    string? TryResolveServiceSid(string serviceName);

    /// <summary>COMPENSATION ONLY, and only for a service THIS transaction created.</summary>
    bool TryDeleteService(string serviceName);

    /// <summary>
    /// CYCLE 63R. StartServiceW with NO argument vector. The service receives no
    /// caller-controlled command line, ever.
    ///
    /// CYCLE 74. It reports a BOUNDED <see cref="ServiceStartAttemptResult"/>
    /// rather than a bare bool, so a refusal can say WHICH refusal it was. The
    /// raw Win32 value never crosses this boundary.
    /// </summary>
    ServiceStartAttemptResult TryStartService(string serviceName);

    /// <summary>
    /// CYCLE 63R. QueryServiceStatusEx(SC_STATUS_PROCESS_INFO). It reports a
    /// bounded run state and the reported process id, and nothing else.
    /// </summary>
    ServiceQueryState QueryStatus(string serviceName, out ServiceStatusSnapshot snapshot);

    /// <summary>
    /// CYCLE 63R. ControlService(SERVICE_CONTROL_STOP). It is the ONLY control
    /// code this product may send; pause, continue, interrogate and every
    /// user-defined code are absent by construction.
    /// </summary>
    bool TryStopService(string serviceName);
}

/// <summary>
/// The real adapter. Every SCM P/Invoke in the product lives here, each declared
/// exactly once, with no retry loop, no fallback and no alternate name.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsServiceControlManagerAdapter : IServiceControlManagerAdapter
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint Delete = 0x00010000;
    private const uint ServiceConfigServiceSidInfo = 5;
    private const uint ScStatusProcessInfo = 0;
    private const uint ServiceControlStop = 0x00000001;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorServiceNotActive = 1062;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcessNative
    {
        internal uint dwServiceType;
        internal uint dwCurrentState;
        internal uint dwControlsAccepted;
        internal uint dwWin32ExitCode;
        internal uint dwServiceSpecificExitCode;
        internal uint dwCheckPoint;
        internal uint dwWaitHint;
        internal uint dwProcessId;
        internal uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusNative
    {
        internal uint dwServiceType;
        internal uint dwCurrentState;
        internal uint dwControlsAccepted;
        internal uint dwWin32ExitCode;
        internal uint dwServiceSpecificExitCode;
        internal uint dwCheckPoint;
        internal uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceConfigNative
    {
        internal uint dwServiceType;
        internal uint dwStartType;
        internal uint dwErrorControl;
        internal IntPtr lpBinaryPathName;
        internal IntPtr lpLoadOrderGroup;
        internal uint dwTagId;
        internal IntPtr lpDependencies;
        internal IntPtr lpServiceStartName;
        internal IntPtr lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceSidInfo
    {
        internal uint dwServiceSidType;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateServiceW(
        IntPtr scManager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(
        IntPtr service, IntPtr serviceConfig, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, IntPtr info);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2W(
        IntPtr service, uint infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr service, uint numServiceArgs, IntPtr serviceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service, uint infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatusNative status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public ServiceQueryState QueryConfiguration(string serviceName, out ServiceConfigurationSnapshot snapshot)
    {
        snapshot = default;
        if (!ServiceIdentityContract.IsFixedServiceName(serviceName))
        {
            return ServiceQueryState.Unavailable;
        }

        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return ServiceQueryState.Unavailable;
        }

        IntPtr service = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            service = OpenServiceW(manager, serviceName, ServiceQueryConfig);
            if (service == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                    ? ServiceQueryState.Absent
                    : ServiceQueryState.Unavailable;
            }

            if (QueryServiceConfigW(service, IntPtr.Zero, 0, out uint needed)
                || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer
                || needed == 0 || needed > 64 * 1024)
            {
                return ServiceQueryState.Unavailable;
            }

            buffer = Marshal.AllocHGlobal((int)needed);
            if (!QueryServiceConfigW(service, buffer, needed, out _))
            {
                return ServiceQueryState.Unavailable;
            }

            ServiceConfigNative config = Marshal.PtrToStructure<ServiceConfigNative>(buffer);
            string loadOrderGroup = ReadString(config.lpLoadOrderGroup);
            snapshot = new ServiceConfigurationSnapshot(
                serviceName,
                ReadString(config.lpDisplayName),
                ReadString(config.lpBinaryPathName),
                ReadString(config.lpServiceStartName),
                config.dwServiceType,
                config.dwStartType,
                config.dwErrorControl,
                hasDependencies: HasAnyDependency(config.lpDependencies),
                hasLoadOrderGroup: loadOrderGroup.Length > 0,
                hasTag: config.dwTagId != 0);
            return ServiceQueryState.Present;
        }
        catch (Exception)
        {
            return ServiceQueryState.Unavailable;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }
            CloseServiceHandle(manager);
        }
    }

    public bool TryCreateService(ServiceConfigurationSnapshot desired)
    {
        if (!ServiceIdentityContract.IsFixedServiceName(desired.ServiceName)
            || !ServiceIdentityContract.IsFixedServiceDisplayName(desired.DisplayName)
            || !ServiceIdentityContract.IsFixedQualifiedServiceAccountName(desired.AccountName)
            || string.IsNullOrEmpty(desired.BinaryPath))
        {
            return false;
        }

        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect | ScManagerCreateService);
        if (manager == IntPtr.Zero)
        {
            return false;
        }

        IntPtr service = IntPtr.Zero;
        try
        {
            // No dependencies, no load-order group, no tag id, no password, and
            // no interactive flag. Every one of those is a literal null here.
            service = CreateServiceW(
                manager,
                desired.ServiceName,
                desired.DisplayName,
                ServiceQueryConfig | ServiceChangeConfig,
                desired.ServiceType,
                desired.StartType,
                desired.ErrorControl,
                desired.BinaryPath,
                null,
                IntPtr.Zero,
                null,
                desired.AccountName,
                null);

            // The service is created and DELIBERATELY NOT STARTED. There is no
            // StartService call anywhere in this file.
            return service != IntPtr.Zero;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }
            CloseServiceHandle(manager);
        }
    }

    public bool TrySetUnrestrictedServiceSidType(string serviceName)
    {
        if (!ServiceIdentityContract.IsFixedServiceName(serviceName))
        {
            return false;
        }

        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return false;
        }

        IntPtr service = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            service = OpenServiceW(manager, serviceName, ServiceChangeConfig);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            var info = new ServiceSidInfo
            {
                dwServiceSidType = ServiceEnableRegistrationContract.ServiceSidTypeUnrestricted,
            };
            buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ServiceSidInfo>());
            Marshal.StructureToPtr(info, buffer, false);
            return ChangeServiceConfig2W(service, ServiceConfigServiceSidInfo, buffer);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }
            CloseServiceHandle(manager);
        }
    }

    public bool TryQueryServiceSidType(string serviceName, out uint sidType)
    {
        sidType = ServiceEnableRegistrationContract.ServiceSidTypeNone;
        if (!ServiceIdentityContract.IsFixedServiceName(serviceName))
        {
            return false;
        }

        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return false;
        }

        IntPtr service = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            service = OpenServiceW(manager, serviceName, ServiceQueryConfig);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            int size = Marshal.SizeOf<ServiceSidInfo>();
            buffer = Marshal.AllocHGlobal(size);
            if (!QueryServiceConfig2W(service, ServiceConfigServiceSidInfo, buffer, (uint)size, out _))
            {
                return false;
            }

            sidType = Marshal.PtrToStructure<ServiceSidInfo>(buffer).dwServiceSidType;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }
            CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// Resolves the SID of the now-existing fixed service through the shared
    /// Setup resolver. It deliberately does NOT shell out to `sc showsid` and
    /// does NOT reimplement the SID derivation algorithm.
    /// </summary>
    public string? TryResolveServiceSid(string serviceName)
    {
        if (!ServiceIdentityContract.IsFixedServiceName(serviceName))
        {
            return null;
        }

        PAXCookbookSetup.Service.ServiceSidResolution resolution =
            PAXCookbookSetup.Service.ServiceSidResolver.ResolveFixedServiceSid();
        return resolution.IsResolved ? resolution.Sid : null;
    }

    public bool TryDeleteService(string serviceName)
    {
        if (!ServiceIdentityContract.IsFixedServiceName(serviceName))
        {
            return false;
        }

        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return false;
        }

        IntPtr service = IntPtr.Zero;
        try
        {
            service = OpenServiceW(manager, serviceName, Delete);
            if (service == IntPtr.Zero)
            {
                // Already gone is a successful compensation.
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist;
            }

            return DeleteService(service);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }
            CloseServiceHandle(manager);
        }
    }

    private static string ReadString(IntPtr pointer) =>
        pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(pointer) ?? string.Empty;

    /// <summary>
    /// CYCLE 63R. Starts the fixed service with NO argument vector: zero
    /// arguments and a null pointer, so no caller-controlled value can ever
    /// reach the service's command line. Already-running is a success, because
    /// the caller's intent - "this service is started" - already holds.
    /// </summary>
    public ServiceStartAttemptResult TryStartService(string serviceName)
    {
        if (!ServiceIdentityContract.IsFixedServiceName(serviceName))
        {
            return ServiceStartAttemptResult.Refused;
        }

        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return ServiceStartAttemptResult.Refused;
        }

        IntPtr service = IntPtr.Zero;
        try
        {
            // An OPEN failure is not a START failure: it happened before
            // StartServiceW was ever called, so it is never given one of the
            // start-specific categories.
            service = OpenServiceW(manager, serviceName, ServiceStart | ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return ServiceStartAttemptResult.Refused;
            }

            if (StartServiceW(service, 0, IntPtr.Zero))
            {
                return ServiceStartAttemptResult.Started;
            }

            return ServiceStartAttemptResultMap.FromNativeError(Marshal.GetLastWin32Error());
        }
        catch (Exception)
        {
            return ServiceStartAttemptResult.Refused;
        }
        finally
        {
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }
            CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// CYCLE 63R. Reads the bounded run state and the reported process id. An
    /// unrecognised native state maps to <see cref="ServiceRunState.Unrecognised"/>
    /// rather than being guessed at.
    /// </summary>
    public ServiceQueryState QueryStatus(string serviceName, out ServiceStatusSnapshot snapshot)
    {
        snapshot = default;
        if (!ServiceIdentityContract.IsFixedServiceName(serviceName))
        {
            return ServiceQueryState.Unavailable;
        }

        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return ServiceQueryState.Unavailable;
        }

        IntPtr service = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            service = OpenServiceW(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                    ? ServiceQueryState.Absent
                    : ServiceQueryState.Unavailable;
            }

            int size = Marshal.SizeOf<ServiceStatusProcessNative>();
            buffer = Marshal.AllocHGlobal(size);
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, (uint)size, out _))
            {
                return ServiceQueryState.Unavailable;
            }

            ServiceStatusProcessNative status = Marshal.PtrToStructure<ServiceStatusProcessNative>(buffer);
            snapshot = new ServiceStatusSnapshot(
                ServiceRunStateMap.FromNative(status.dwCurrentState), status.dwProcessId);
            return ServiceQueryState.Present;
        }
        catch (Exception)
        {
            return ServiceQueryState.Unavailable;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }
            CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// CYCLE 63R. Sends SERVICE_CONTROL_STOP and nothing else. Already-stopped
    /// is a success for the same reason already-running is above.
    /// </summary>
    public bool TryStopService(string serviceName)
    {
        if (!ServiceIdentityContract.IsFixedServiceName(serviceName))
        {
            return false;
        }

        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return false;
        }

        IntPtr service = IntPtr.Zero;
        try
        {
            service = OpenServiceW(manager, serviceName, ServiceStop | ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            var status = default(ServiceStatusNative);
            if (ControlService(service, ServiceControlStop, ref status))
            {
                return true;
            }

            return Marshal.GetLastWin32Error() == ErrorServiceNotActive;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (service != IntPtr.Zero)
            {
                CloseServiceHandle(service);
            }
            CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// The dependency list is a double-null-terminated multi-string. It has a
    /// dependency only when its FIRST character is not the terminator.
    /// </summary>
    private static bool HasAnyDependency(IntPtr pointer) =>
        pointer != IntPtr.Zero && Marshal.ReadInt16(pointer) != 0;
}
