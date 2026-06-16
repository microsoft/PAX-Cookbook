#requires -Version 7.4

# WindowsReAuth.ps1
#
# Phase AF -- Windows-native re-authentication via UserConsentVerifier
# (Windows Hello / PIN / lock-screen credential). This is the ONLY way
# Cookbook re-verifies an operator's identity for protected operations.
#
# Doctrine (verbatim, in force):
#   - Cookbook NEVER collects, hashes, compares, proxies, or stores any
#     Windows password material. The OS verifies; Cookbook only observes
#     the verdict.
#   - The verdict is one of seven enum values: Verified, DeviceNotPresent,
#     NotConfiguredForUser, DisabledByPolicy, DeviceBusy, RetriesExhausted,
#     Canceled. Any value other than Verified is FAIL-CLOSED.
#   - Re-auth gates: app open (first protected page), every manual cook
#     spawn, profile CRUD, secret bind/replace/remove, scheduled-task
#     configuration, update apply. This file ONLY exposes the primitive;
#     BrokerLock.ps1 orchestrates the per-op policy.
#   - Verification ≠ Graph permission. Even a Verified verdict does NOT
#     imply the operator has Graph workload authorization. Cookbook never
#     infers M365 permissions from a Windows verification.
#
# Why raw COM (not CsWinRT, not [Windows.*]):
#   PowerShell 7.6 on .NET 10 dropped legacy WindowsRuntime type
#   projection. Adding Microsoft.Windows.SDK.NET.Ref would vendor two
#   more DLLs (Microsoft.Windows.SDK.NET.dll + WinRT.Runtime.dll) onto
#   the appliance, expanding the signed-artefact surface. Raw COM
#   against combase!RoGetActivationFactory + the documented
#   IUserConsentVerifierInterop interop interface is the minimum-surface
#   path. The PIID for IAsyncOperation<UserConsentVerificationResult>
#   is computed at runtime via the standard WinRT PIID derivation
#   (UUIDv5 over namespace + parameterized signature) so we never hard-
#   code a value that could drift.

if (-not ('PAXCookbook.Native.WindowsReAuthNative' -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PAXCookbook.Native {

    public enum UserConsentVerificationResult {
        Verified             = 0,
        DeviceNotPresent     = 1,
        NotConfiguredForUser = 2,
        DisabledByPolicy     = 3,
        DeviceBusy           = 4,
        RetriesExhausted     = 5,
        Canceled             = 6,
        // Cookbook-private values (NOT returned by the OS); used to
        // signal infrastructure failure to PowerShell callers, all of
        // which fail closed identically to a non-Verified OS result.
        ComInteropFailure    = -1,
        Unknown              = -2
    }

    public enum UserConsentVerifierAvailability {
        Available             = 0,
        DeviceNotPresent      = 1,
        NotConfiguredForUser  = 2,
        DisabledByPolicy      = 3,
        DeviceBusy            = 4,
        Unknown               = -1
    }

    public enum WinRtAsyncStatus {
        Started   = 0,
        Completed = 1,
        Canceled  = 2,
        Error     = 3
    }

    // -------------------------------------------------------------
    // Function-pointer delegates for direct vtable invocation.
    // We do NOT use [ComImport] interfaces because the .NET 10
    // built-in COM marshaller does not project IInspectable's three
    // extra methods cleanly without CsWinRT. The vtable layouts we
    // depend on are documented by the WinRT ABI and stable since
    // Windows 8.
    //
    // Vtable layout for IInspectable-derived interfaces:
    //   slot 0: QueryInterface
    //   slot 1: AddRef
    //   slot 2: Release
    //   slot 3: GetIids
    //   slot 4: GetRuntimeClassName
    //   slot 5: GetTrustLevel
    //   slot 6+: interface-specific methods
    //
    // IAsyncInfo (extends IInspectable):
    //   slot 6: get_Id
    //   slot 7: get_Status
    //   slot 8: get_ErrorCode
    //   slot 9: Cancel
    //   slot 10: Close
    //
    // IAsyncOperation<T> (extends IInspectable):
    //   slot 6: put_Completed
    //   slot 7: get_Completed
    //   slot 8: GetResults
    //
    // IUserConsentVerifierInterop (extends IInspectable):
    //   slot 6: RequestVerificationForWindowAsync(HWND, HSTRING, REFIID, void**)
    //
    // IUserConsentVerifierStatics (extends IInspectable) -- the
    // activation factory for UserConsentVerifier:
    //   slot 6: CheckAvailabilityAsync(IAsyncOperation<UserConsentVerifierAvailability>**)
    //   slot 7: RequestVerificationAsync(HSTRING, IAsyncOperation<UserConsentVerificationResult>**)
    // -------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int QueryInterfaceDelegate(IntPtr thisPtr, ref Guid iid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ReleaseDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetStatusDelegate(IntPtr thisPtr, out int status);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetResultsDelegate(IntPtr thisPtr, out int result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int RequestVerificationForWindowAsyncDelegate(
        IntPtr thisPtr, IntPtr hwnd, IntPtr hstringMessage, ref Guid riid, out IntPtr asyncOp);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CheckAvailabilityAsyncDelegate(IntPtr thisPtr, out IntPtr asyncOp);

    public static class WindowsReAuthNative {

        // combase exports (WinRT runtime).
        [DllImport("api-ms-win-core-winrt-l1-1-0.dll", PreserveSig = true)]
        static extern int RoInitialize(uint initType);

        [DllImport("api-ms-win-core-winrt-l1-1-0.dll", PreserveSig = true)]
        static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", PreserveSig = true)]
        static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, uint length, out IntPtr hstring);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", PreserveSig = true)]
        static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetForegroundWindow();

        // Window-raise plumbing for the consent dialog. The prompt
        // raised by RequestVerificationForWindowAsync is positioned and
        // z-ordered relative to the owner HWND it is handed. When that
        // owner is hidden (default launch keeps the broker console
        // hidden) or sits behind the browser app window (Support Mode),
        // the system credential dialog can land behind the Cookbook
        // window or on another monitor, so the operator believes Windows
        // Hello never appeared. ResolveAndRaisePromptOwner therefore
        // prefers the foreground window the operator is actually looking
        // at (the browser app window the unlock click came from) and
        // best-effort raises it immediately before the prompt is shown.
        // SetForegroundWindow is subject to the Windows focus-stealing
        // guard; when the OS declines it flashes the taskbar instead,
        // and the SPA shows a visible "look for the Windows Security
        // prompt" status so the operator is never left guessing.
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hWnd);

        const int SW_SHOW    = 5;
        const int SW_RESTORE = 9;

        // Choose the owner window for the consent prompt and raise it.
        // Preference order: the current foreground window (the browser
        // app window the operator just clicked in), then the broker
        // console window. Returns the chosen HWND, or IntPtr.Zero when
        // neither is available (the platform then shows the prompt
        // unparented). Passing a foreground browser HWND only affects
        // dialog placement and z-order; it does not change the security
        // gate — the platform authenticator still drives the verdict.
        static IntPtr ResolveAndRaisePromptOwner() {
            IntPtr fg = GetForegroundWindow();
            IntPtr owner = (fg != IntPtr.Zero && IsWindow(fg)) ? fg : IntPtr.Zero;
            if (owner == IntPtr.Zero) {
                IntPtr con = GetConsoleWindow();
                if (con != IntPtr.Zero && IsWindow(con)) { owner = con; }
            }
            if (owner != IntPtr.Zero) {
                try {
                    if (IsIconic(owner)) { ShowWindow(owner, SW_RESTORE); }
                    else                 { ShowWindow(owner, SW_SHOW); }
                    SetForegroundWindow(owner);
                } catch { }
            }
            return owner;
        }

        const uint RO_INIT_MULTITHREADED = 1;

        // IID of IUserConsentVerifierInterop (well-known, Win32 interop
        // IID; documented in <UserConsentVerifierInterop.h>).
        static Guid IID_IUserConsentVerifierInterop =
            new Guid("39E050C3-4E74-441A-8DC0-B81104DF949C");

        // IID of IUserConsentVerifierStatics (the WinRT activation
        // factory interface for UserConsentVerifier; from
        // Windows.Security.Credentials.UI metadata).
        static Guid IID_IUserConsentVerifierStatics =
            new Guid("AF4F3F91-564C-4DDC-B8B5-973447627C65");

        // Once-per-process initialization guard. RoInitialize is
        // idempotent per apartment but the HRESULT semantics differ
        // (S_FALSE on already-initialized); we don't care about the
        // distinction.
        static int s_roInitDone = 0;

        static void EnsureRoInit() {
            if (Interlocked.CompareExchange(ref s_roInitDone, 1, 0) == 0) {
                RoInitialize(RO_INIT_MULTITHREADED);
            }
        }

        // Diagnostic capture. When Verify() returns ComInteropFailure,
        // LastFailureDetail holds a short tag identifying WHICH of the
        // seven interop paths fired, plus the relevant HRESULT (or
        // status code). The string is overwritten on every Verify()
        // call; PowerShell callers should snapshot it immediately
        // after a non-Verified result. Tag shape:
        //   <code>:<hr|status>
        // Codes (stable -- do not renumber; smoke greps for them):
        //   classname_hcs  -- WindowsCreateString(className) failed
        //   roget_factory  -- RoGetActivationFactory failed
        //   message_hcs    -- WindowsCreateString(message) failed
        //   reqverif       -- RequestVerificationForWindowAsync failed
        //   poll_qi        -- PollAsync IAsyncInfo QI failed
        //   poll_status    -- PollAsync get_Status failed
        //   poll_error     -- async op completed with status=Error(3)
        //   poll_timeout   -- timeoutMs elapsed before async op finished
        //   getresults     -- IAsyncOperation<T>::GetResults failed
        public static string LastFailureDetail;

        public static void ClearLastFailureDetail() {
            LastFailureDetail = null;
        }

        static UserConsentVerificationResult TagAndReturn(string code, int hr) {
            LastFailureDetail = code + ":" + hr.ToString();
            return UserConsentVerificationResult.ComInteropFailure;
        }

        // -------------------------------------------------------------
        // WinRT PIID derivation (UUIDv5 over namespace + signature).
        // See: https://learn.microsoft.com/en-us/uwp/winrt-cref/winmd-files#parameterized-interface-instantiation
        //
        // Namespace GUID (well-known WinRT PIID namespace):
        //   {11f47ad5-7b73-42c0-abae-878b1e16adee}
        //
        // Signatures used here:
        //   IAsyncOperation<UserConsentVerificationResult>:
        //     pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};enum(Windows.Security.Credentials.UI.UserConsentVerificationResult;i4))
        //   IAsyncOperation<UserConsentVerifierAvailability>:
        //     pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};enum(Windows.Security.Credentials.UI.UserConsentVerifierAvailability;i4))
        //
        // The unparameterized IAsyncOperation<T> generic interface IID
        // is {9fc2b0bb-e446-44e2-aa61-9cab8f636af2}.
        // -------------------------------------------------------------

        static byte[] s_piidNamespace = new byte[] {
            // {11f47ad5-7b73-42c0-abae-878b1e16adee} in big-endian
            // byte order (per the WinRT PIID spec, which uses RFC4122
            // network byte order for the namespace seed).
            0x11, 0xf4, 0x7a, 0xd5,
            0x7b, 0x73,
            0x42, 0xc0,
            0xab, 0xae,
            0x87, 0x8b, 0x1e, 0x16, 0xad, 0xee
        };

        public static Guid ComputePIID(string signature) {
            byte[] sigBytes = Encoding.UTF8.GetBytes(signature);
            byte[] data = new byte[s_piidNamespace.Length + sigBytes.Length];
            Buffer.BlockCopy(s_piidNamespace, 0, data, 0, s_piidNamespace.Length);
            Buffer.BlockCopy(sigBytes, 0, data, s_piidNamespace.Length, sigBytes.Length);

            byte[] hash;
            using (var sha1 = System.Security.Cryptography.SHA1.Create()) {
                hash = sha1.ComputeHash(data);
            }
            byte[] g = new byte[16];
            Buffer.BlockCopy(hash, 0, g, 0, 16);

            // Version 5 (name-based, SHA-1).
            g[6] = (byte)((g[6] & 0x0F) | 0x50);
            // Variant 10xx (RFC 4122).
            g[8] = (byte)((g[8] & 0x3F) | 0x80);

            // Guid(byte[]) interprets the first three groups as
            // little-endian fields; the hash is in network (big-endian)
            // order, so swap.
            Array.Reverse(g, 0, 4);
            Array.Reverse(g, 4, 2);
            Array.Reverse(g, 6, 2);
            return new Guid(g);
        }

        // -------------------------------------------------------------
        // Vtable helpers
        // -------------------------------------------------------------

        static T VtableMethod<T>(IntPtr comPtr, int slot) where T : Delegate {
            IntPtr vtable = Marshal.ReadIntPtr(comPtr);
            IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(method, typeof(T));
        }

        static int Release(IntPtr comPtr) {
            if (comPtr == IntPtr.Zero) return 0;
            return VtableMethod<ReleaseDelegate>(comPtr, 2)(comPtr);
        }

        // Polls an IAsyncInfo-implementing operation. Returns the final
        // WinRtAsyncStatus. The operation pointer is QI'd to IAsyncInfo
        // for the duration of the poll, then the IAsyncInfo pointer is
        // released; the original asyncOp pointer remains caller-owned.
        // Sets LastFailureDetail with a poll_* tag when returning Error,
        // so the caller's ComInteropFailure surface can attribute the
        // failure precisely.
        static WinRtAsyncStatus PollAsync(IntPtr asyncOp, int timeoutMs) {
            // IAsyncInfo IID: 00000036-0000-0000-C000-000000000046
            Guid iidAsyncInfo = new Guid("00000036-0000-0000-C000-000000000046");
            IntPtr asyncInfoPtr;
            var qi = VtableMethod<QueryInterfaceDelegate>(asyncOp, 0);
            int hr = qi(asyncOp, ref iidAsyncInfo, out asyncInfoPtr);
            if (hr < 0 || asyncInfoPtr == IntPtr.Zero) {
                LastFailureDetail = "poll_qi:" + hr.ToString();
                return WinRtAsyncStatus.Error;
            }
            try {
                var getStatus = VtableMethod<GetStatusDelegate>(asyncInfoPtr, 7);
                int elapsed = 0;
                while (true) {
                    int status;
                    hr = getStatus(asyncInfoPtr, out status);
                    if (hr < 0) {
                        LastFailureDetail = "poll_status:" + hr.ToString();
                        return WinRtAsyncStatus.Error;
                    }
                    if (status != (int)WinRtAsyncStatus.Started) {
                        var finalStatus = (WinRtAsyncStatus)status;
                        if (finalStatus == WinRtAsyncStatus.Error) {
                            LastFailureDetail = "poll_error:" + status.ToString();
                        }
                        return finalStatus;
                    }
                    if (elapsed >= timeoutMs) {
                        LastFailureDetail = "poll_timeout:" + elapsed.ToString();
                        return WinRtAsyncStatus.Error;
                    }
                    Thread.Sleep(50);
                    elapsed += 50;
                }
            } finally {
                Release(asyncInfoPtr);
            }
        }

        // -------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------

        public static UserConsentVerifierAvailability CheckAvailability() {
            EnsureRoInit();

            // Activate the UserConsentVerifier statics factory.
            IntPtr classNameHs;
            string className = "Windows.Security.Credentials.UI.UserConsentVerifier";
            int hr = WindowsCreateString(className, (uint)className.Length, out classNameHs);
            if (hr < 0) return UserConsentVerifierAvailability.Unknown;
            IntPtr factoryPtr = IntPtr.Zero;
            try {
                Guid iidStatics = IID_IUserConsentVerifierStatics;
                hr = RoGetActivationFactory(classNameHs, ref iidStatics, out factoryPtr);
                if (hr < 0 || factoryPtr == IntPtr.Zero) {
                    return UserConsentVerifierAvailability.Unknown;
                }
                // Slot 6 on IUserConsentVerifierStatics is CheckAvailabilityAsync.
                var checkAvail = VtableMethod<CheckAvailabilityAsyncDelegate>(factoryPtr, 6);
                IntPtr asyncOp;
                hr = checkAvail(factoryPtr, out asyncOp);
                if (hr < 0 || asyncOp == IntPtr.Zero) {
                    return UserConsentVerifierAvailability.Unknown;
                }
                try {
                    var status = PollAsync(asyncOp, 5000);
                    if (status != WinRtAsyncStatus.Completed) {
                        return UserConsentVerifierAvailability.Unknown;
                    }
                    // Slot 8 on IAsyncOperation<T> is GetResults.
                    var getResults = VtableMethod<GetResultsDelegate>(asyncOp, 8);
                    int result;
                    hr = getResults(asyncOp, out result);
                    if (hr < 0) return UserConsentVerifierAvailability.Unknown;
                    return (UserConsentVerifierAvailability)result;
                } finally {
                    Release(asyncOp);
                }
            } finally {
                if (factoryPtr != IntPtr.Zero) Release(factoryPtr);
                WindowsDeleteString(classNameHs);
            }
        }

        public static UserConsentVerificationResult Verify(string message, int timeoutMs) {
            // Reset diagnostic state on every entry. Callers read
            // LastFailureDetail iff the return value is
            // ComInteropFailure; on success or any other verdict the
            // tag is left empty.
            LastFailureDetail = null;

            if (string.IsNullOrEmpty(message)) {
                message = "PAX Cookbook needs to verify your identity to proceed.";
            }
            EnsureRoInit();

            IntPtr classNameHs;
            string className = "Windows.Security.Credentials.UI.UserConsentVerifier";
            int hr = WindowsCreateString(className, (uint)className.Length, out classNameHs);
            if (hr < 0) return TagAndReturn("classname_hcs", hr);

            IntPtr factoryPtr = IntPtr.Zero;
            IntPtr messageHs = IntPtr.Zero;
            try {
                Guid iidInterop = IID_IUserConsentVerifierInterop;
                hr = RoGetActivationFactory(classNameHs, ref iidInterop, out factoryPtr);
                if (hr < 0 || factoryPtr == IntPtr.Zero) {
                    return TagAndReturn("roget_factory", hr);
                }
                hr = WindowsCreateString(message, (uint)message.Length, out messageHs);
                if (hr < 0) return TagAndReturn("message_hcs", hr);

                // Resolve the owner window for the consent prompt and
                // raise it. We prefer the foreground window the operator
                // is looking at (the browser app window the unlock click
                // came from) over the broker console, then raise it so
                // the Hello dialog is visible and on the correct monitor
                // instead of buried behind the Cookbook window or shown
                // off a hidden console.
                IntPtr hwnd = ResolveAndRaisePromptOwner();

                Guid piidAsyncOpUcvr = ComputePIID(
                    "pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};enum(Windows.Security.Credentials.UI.UserConsentVerificationResult;i4))");

                var requestVerification = VtableMethod<RequestVerificationForWindowAsyncDelegate>(factoryPtr, 6);
                IntPtr asyncOp;
                hr = requestVerification(factoryPtr, hwnd, messageHs, ref piidAsyncOpUcvr, out asyncOp);
                if (hr < 0 || asyncOp == IntPtr.Zero) {
                    return TagAndReturn("reqverif", hr);
                }
                try {
                    var status = PollAsync(asyncOp, timeoutMs);
                    if (status == WinRtAsyncStatus.Canceled) {
                        return UserConsentVerificationResult.Canceled;
                    }
                    if (status != WinRtAsyncStatus.Completed) {
                        // PollAsync set LastFailureDetail with a
                        // poll_* tag already; do not overwrite.
                        return UserConsentVerificationResult.ComInteropFailure;
                    }
                    var getResults = VtableMethod<GetResultsDelegate>(asyncOp, 8);
                    int result;
                    hr = getResults(asyncOp, out result);
                    if (hr < 0) return TagAndReturn("getresults", hr);
                    return (UserConsentVerificationResult)result;
                } finally {
                    Release(asyncOp);
                }
            } finally {
                if (factoryPtr != IntPtr.Zero) Release(factoryPtr);
                if (messageHs != IntPtr.Zero) WindowsDeleteString(messageHs);
                WindowsDeleteString(classNameHs);
            }
        }
    }
}
'@
}

# ---------------------------------------------------------------------
# PowerShell wrappers
# ---------------------------------------------------------------------

# Default timeout for an interactive verification: 60 seconds. Hello/PIN
# prompts that exceed this typically reflect an operator who walked
# away; the broker fail-closes by returning an interop-failure verdict.
$Script:WindowsReAuthDefaultTimeoutMs = 60000

# Cache the availability probe per process. CheckAvailabilityAsync is
# cheap but does cross a COM boundary; the policy decision ("can this
# host even verify?") does not need to be re-evaluated on every call.
$Script:WindowsReAuthAvailabilityCache = $null

function Test-WindowsReAuthAvailable {
    # Returns one of:
    #   'Available' | 'DeviceNotPresent' | 'NotConfiguredForUser'
    #   'DisabledByPolicy' | 'DeviceBusy' | 'Unknown'
    #
    # Use -Force to bypass the per-process cache (the harness uses this).
    [CmdletBinding()]
    param([switch]$Force)
    if (-not $Force -and $null -ne $Script:WindowsReAuthAvailabilityCache) {
        return $Script:WindowsReAuthAvailabilityCache
    }
    try {
        $result = [PAXCookbook.Native.WindowsReAuthNative]::CheckAvailability()
        $Script:WindowsReAuthAvailabilityCache = $result.ToString()
        return $Script:WindowsReAuthAvailabilityCache
    } catch {
        $Script:WindowsReAuthAvailabilityCache = 'Unknown'
        return 'Unknown'
    }
}

function Invoke-WindowsReAuth {
    # Show a Windows Hello / PIN / lock-screen verification prompt and
    # return the OS verdict as a string.
    #
    # Returns one of:
    #   'Verified'              -- the ONLY result that grants the
    #                              caller permission to proceed with
    #                              the protected operation.
    #   'DeviceNotPresent'      -- no biometric sensor and no PIN/pwd
    #                              fallback configured.
    #   'NotConfiguredForUser'  -- the OS supports Hello but this user
    #                              has not enrolled.
    #   'DisabledByPolicy'      -- group policy blocks Hello.
    #   'DeviceBusy'            -- sensor in use by another caller.
    #   'RetriesExhausted'      -- operator failed too many times.
    #   'Canceled'              -- operator dismissed the prompt.
    #   'ComInteropFailure'     -- Cookbook-private; treat as fail-closed.
    #   'Unknown'               -- Cookbook-private; treat as fail-closed.
    #
    # The caller is responsible for fail-closed policy: ONLY 'Verified'
    # unlocks a protected operation. Every other value MUST be surfaced
    # to the operator with no privileged side-effect performed.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Message,
        [int]$TimeoutMs = $Script:WindowsReAuthDefaultTimeoutMs
    )
    if ([string]::IsNullOrWhiteSpace($Message)) {
        throw "Invoke-WindowsReAuth: -Message is required and must be non-empty."
    }
    $Script:WindowsReAuthLastFailureDetail = $null
    try {
        $result = [PAXCookbook.Native.WindowsReAuthNative]::Verify($Message, [int]$TimeoutMs)
        $verdict = $result.ToString()
        if ($verdict -eq 'ComInteropFailure') {
            # The C# layer recorded WHICH interop path failed in its
            # static LastFailureDetail. Snapshot it here so the route
            # handler can surface it to broker logs without reaching
            # into the native type itself.
            $Script:WindowsReAuthLastFailureDetail = [PAXCookbook.Native.WindowsReAuthNative]::LastFailureDetail
            Write-Verbose ('Invoke-WindowsReAuth: ComInteropFailure detail=' + $Script:WindowsReAuthLastFailureDetail)
        }
        return $verdict
    } catch {
        $Script:WindowsReAuthLastFailureDetail = 'native_exception:' + $_.Exception.GetType().FullName + ':' + $_.Exception.Message
        Write-Verbose "Invoke-WindowsReAuth: native call threw $($_.Exception.GetType().FullName): $($_.Exception.Message)"
        return 'ComInteropFailure'
    }
}

function Get-WindowsReAuthLastFailureDetail {
    # One-shot read of the diagnostic tag captured by the most recent
    # ComInteropFailure return from Invoke-WindowsReAuth. Returns
    # $null if the last call did not fail with ComInteropFailure (or
    # if no call has been made in this process).
    #
    # Tag shape:
    #   <code>:<hr_or_status>
    # Codes (see WindowsReAuthNative.LastFailureDetail comment):
    #   classname_hcs, roget_factory, message_hcs, reqverif,
    #   poll_qi, poll_status, poll_error, poll_timeout, getresults,
    #   native_exception
    #
    # Reading the detail does NOT clear it (the broker may want to
    # log the same detail in multiple places per request). It is
    # cleared at the start of the next Invoke-WindowsReAuth call.
    return $Script:WindowsReAuthLastFailureDetail
}

function Test-WindowsReAuthResultIsVerified {
    # Strict predicate used by BrokerLock.ps1 and the protected-op
    # wrappers. Returns $true ONLY for the literal string 'Verified'.
    # Every other input (including $null, empty, non-Verified enum
    # strings, infrastructure failure values) is $false.
    param([string]$Result)
    return ($Result -eq 'Verified')
}
