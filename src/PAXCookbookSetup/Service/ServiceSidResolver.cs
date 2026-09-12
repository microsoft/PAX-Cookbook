// PAX Cookbook - FIXED SERVICE SID RESOLVER (cycle 39, Setup only)
//
// WHAT THIS FILE IS. A READ-ONLY, FIXED-NAME name-to-SID lookup for the one
// account named by ServiceIdentityContract.QualifiedServiceAccountName. It has
// exactly TWO native call sites, no retry, no fallback, no alternate name, no
// overload, no parameter, and no delegate.
//
// WHAT THIS FILE CANNOT DO, by construction. It never creates, changes,
// deletes, starts, or stops a service; never reads or writes an access-control
// list; never opens a certificate store or a private key; never reads or writes
// the registry; never reads a credential vault; never starts a process; never
// elevates; never opens a socket; never performs file access; never touches PAX
// and never starts a Bake. Nothing here proves any account exists on any
// machine.
//
// HOW IT IS SPLIT (ruling B1). There is NO injectable production native-call
// seam, because a seam is itself a capability. Instead the file is two pieces:
//   * ServiceSidLookupInterpreter - a PURE interpreter over the observable
//     results of the two documented calls. Zero I/O, fully unit-testable, and
//     the sole home of every state-mapping decision.
//   * ServiceSidResolver - a FIXED SHIM whose fixed name and call count are
//     proven STRUCTURALLY, from source.
//
// FIRST-CALL ACCEPTANCE (ruling A1). The sizing call is accepted ONLY when it
// returned zero AND reported a non-zero, in-bounds required SID size AND an
// in-bounds required domain size. A particular native error code is NEVER a
// success criterion: the documented page does not guarantee one. Native error
// codes guide bounded FAILURE mapping only, and are never stored or exposed.
//
// SID CONVERSION (correction 2). Bytes become text through
// new SecurityIdentifier(bytes, 0).Value. No SID-conversion P/Invoke exists
// here. Malformed bytes map to InvalidSid and the exception text is swallowed:
// no catch clause in this file binds an exception variable, so there is no
// value from which a message could ever be read.
//
// THE DOMAIN BUFFER (correction 3). The referenced-domain buffer is allocated
// ONLY because the native API demands one. On a machine that is not domain
// joined it receives the COMPUTER NAME, so it is never interpreted as an
// authority, never returned, logged, persisted, compared, or emitted, and it is
// cleared in a finally block the instant the lookup is done.
//
// SID_NAME_USE (correction 4). Recorded as an input and deliberately NOT
// over-constrained: the documented page states no value a per-service virtual
// account must produce. The CANONICAL SID SHAPE, delegated through
// ServiceIdentityContract, is the final identity check.
//
// WHAT A SUCCESS MAY CARRY (item 11/12). Only the canonical service SID. Never
// a machine name, user name, referenced-domain value, localized account name,
// exception text, Win32 message text, or raw native error value - including
// through ToString().
//
// ALLOCATION BOUNDS (item 13). Zero and over-limit sizes are refused before any
// allocation, sizes are compared against the plan rather than re-trusted, there
// is no retry loop beyond the documented two-call sequence, and a required size
// that changes unexpectedly between the calls fails closed.
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

/// <summary>
/// Bounded outcome of a fixed service-SID resolution. Exactly one member,
/// <see cref="Resolved"/>, is success; every other member is a failure and
/// carries no SID. <see cref="Unspecified"/> is the default so an uninitialised
/// value can never read as success.
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data.
/// </summary>
public enum ServiceSidResolutionState
{
    Unspecified = 0,
    Resolved = 1,
    NotFound = 2,
    AccessDenied = 3,
    InvalidNativeResponse = 4,
    InvalidSid = 5,
    WrongAuthority = 6,
    AllServicesGroup = 7,
    BufferLimitExceeded = 8,
    UnsupportedPlatform = 9,
    UnknownFailure = 10,
}

/// <summary>
/// The bounded allocation decision taken from the sizing call. It carries sizes
/// or a failure state and NEVER a native error value, so a raw error code cannot
/// be surfaced later.
/// </summary>
internal readonly struct ServiceSidBufferPlan
{
    private ServiceSidBufferPlan(
        bool proceed,
        int sidBufferBytes,
        int domainBufferChars,
        ServiceSidResolutionState failureState)
    {
        Proceed = proceed;
        SidBufferBytes = sidBufferBytes;
        DomainBufferChars = domainBufferChars;
        FailureState = failureState;
    }

    internal bool Proceed { get; }

    internal int SidBufferBytes { get; }

    internal int DomainBufferChars { get; }

    internal ServiceSidResolutionState FailureState { get; }

    internal static ServiceSidBufferPlan Continue(int sidBufferBytes, int domainBufferChars) =>
        new(true, sidBufferBytes, domainBufferChars, ServiceSidResolutionState.Unspecified);

    internal static ServiceSidBufferPlan Stop(ServiceSidResolutionState failureState) =>
        new(false, 0, 0, failureState);

    public override string ToString() => Proceed ? nameof(Continue) : FailureState.ToString();
}

/// <summary>
/// The result of a fixed service-SID resolution. Its whole observable surface is
/// a bounded state and, on success only, a canonical SID string.
/// </summary>
internal readonly struct ServiceSidResolution
{
    private readonly string? _sid;

    private ServiceSidResolution(ServiceSidResolutionState state, string? sid)
    {
        State = state;
        _sid = sid;
    }

    internal ServiceSidResolutionState State { get; }

    internal string Sid => _sid ?? string.Empty;

    internal bool IsResolved => State == ServiceSidResolutionState.Resolved;

    internal static ServiceSidResolution Failure(ServiceSidResolutionState state) =>
        new(state, null);

    internal static ServiceSidResolution Success(string sid) =>
        new(ServiceSidResolutionState.Resolved, sid);

    public override string ToString() => State.ToString();
}

/// <summary>
/// PURE interpreter over the observable results of the documented two-call
/// lookup sequence. It performs no I/O of any kind, holds no state, and accepts
/// no delegate, so every decision below is reproducible from its arguments
/// alone.
/// </summary>
internal static class ServiceSidLookupInterpreter
{
    private const string ServiceFamilySid = "S-1-5-80";
    private const string ServiceFamilyPrefix = "S-1-5-80-";
    private const string AllServicesGroupSid = "S-1-5-80-0";

    private const int NativeAccessDenied = 5;
    private const int NativeNoneMapped = 1332;

    /// <summary>
    /// Interprets the sizing call. Acceptance depends ONLY on a zero return plus
    /// bounded required sizes (ruling A1); the native error code guides bounded
    /// failure mapping and nothing else.
    /// </summary>
    internal static ServiceSidBufferPlan InterpretFirstCall(
        bool succeeded,
        int nativeErrorCode,
        int requiredSidByteLength,
        int requiredDomainCharLength)
    {
        // The documented sequence REQUIRES the sizing call to fail. A success is
        // an undocumented shape, so it fails closed instead of being trusted.
        if (succeeded)
        {
            return ServiceSidBufferPlan.Stop(ServiceSidResolutionState.InvalidNativeResponse);
        }

        if (requiredSidByteLength < 0 || requiredDomainCharLength < 0)
        {
            return ServiceSidBufferPlan.Stop(ServiceSidResolutionState.InvalidNativeResponse);
        }

        // No size reported at all: the lookup genuinely failed.
        if (requiredSidByteLength == 0)
        {
            return ServiceSidBufferPlan.Stop(MapNativeFailure(nativeErrorCode));
        }

        // Refused BEFORE any allocation.
        if (requiredSidByteLength > ServiceIdentityContract.MaxSidByteLength
            || requiredDomainCharLength > ServiceIdentityContract.MaxDomainNameCharLength)
        {
            return ServiceSidBufferPlan.Stop(ServiceSidResolutionState.BufferLimitExceeded);
        }

        if (!ServiceIdentityContract.IsBoundedSidByteLength(requiredSidByteLength)
            || !ServiceIdentityContract.IsBoundedDomainCharLength(requiredDomainCharLength))
        {
            return ServiceSidBufferPlan.Stop(ServiceSidResolutionState.InvalidNativeResponse);
        }

        return ServiceSidBufferPlan.Continue(requiredSidByteLength, requiredDomainCharLength);
    }

    /// <summary>
    /// Interprets the resolving call against the plan the sizing call produced.
    /// A stopped plan is propagated unchanged: no second-call result can promote
    /// a failure into a success.
    /// </summary>
    internal static ServiceSidResolution InterpretSecondCall(
        ServiceSidBufferPlan plan,
        bool succeeded,
        int nativeErrorCode,
        int returnedSidByteLength,
        int returnedDomainCharLength,
        byte[]? sidBytes,
        int sidNameUse)
    {
        // Correction 4: observed, deliberately never used as a gate.
        _ = sidNameUse;

        if (!plan.Proceed)
        {
            return ServiceSidResolution.Failure(plan.FailureState);
        }

        if (!succeeded)
        {
            return ServiceSidResolution.Failure(MapNativeFailure(nativeErrorCode));
        }

        // A required size that GREW between the calls fails closed; a shrunken
        // domain length is normal because the domain is never interpreted.
        if (returnedSidByteLength <= 0 || returnedSidByteLength > plan.SidBufferBytes)
        {
            return ServiceSidResolution.Failure(ServiceSidResolutionState.InvalidNativeResponse);
        }

        if (returnedDomainCharLength < 0 || returnedDomainCharLength > plan.DomainBufferChars)
        {
            return ServiceSidResolution.Failure(ServiceSidResolutionState.InvalidNativeResponse);
        }

        if (!ServiceIdentityContract.IsBoundedSidByteLength(returnedSidByteLength))
        {
            return ServiceSidResolution.Failure(ServiceSidResolutionState.InvalidNativeResponse);
        }

        if (sidBytes is null || sidBytes.Length < returnedSidByteLength)
        {
            return ServiceSidResolution.Failure(ServiceSidResolutionState.InvalidNativeResponse);
        }

        string value;
        try
        {
            value = new SecurityIdentifier(sidBytes, 0).Value;
        }
        catch (ArgumentException)
        {
            // Correction 2: nothing derived from the exception survives.
            return ServiceSidResolution.Failure(ServiceSidResolutionState.InvalidSid);
        }

        bool serviceFamily =
            string.Equals(value, ServiceFamilySid, StringComparison.Ordinal)
            || value.StartsWith(ServiceFamilyPrefix, StringComparison.Ordinal);
        if (!serviceFamily)
        {
            return ServiceSidResolution.Failure(ServiceSidResolutionState.WrongAuthority);
        }

        if (string.Equals(value, AllServicesGroupSid, StringComparison.Ordinal))
        {
            return ServiceSidResolution.Failure(ServiceSidResolutionState.AllServicesGroup);
        }

        // Delegated canonical shape check: the final identity gate.
        if (!ServiceIdentityContract.IsServiceIdentitySid(value))
        {
            return ServiceSidResolution.Failure(ServiceSidResolutionState.InvalidSid);
        }

        return ServiceSidResolution.Success(value);
    }

    private static ServiceSidResolutionState MapNativeFailure(int nativeErrorCode) => nativeErrorCode switch
    {
        NativeNoneMapped => ServiceSidResolutionState.NotFound,
        NativeAccessDenied => ServiceSidResolutionState.AccessDenied,
        _ => ServiceSidResolutionState.UnknownFailure,
    };
}

/// <summary>
/// The FIXED SHIM. One native import, exactly two call sites, both passing the
/// one qualified account name the identity contract composes. No parameter, no
/// overload, no retry, no fallback, no state, and no delegate.
/// </summary>
internal static class ServiceSidResolver
{
    [DllImport("advapi32.dll", EntryPoint = "LookupAccountNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountNameW(
        string? lpSystemName,
        string lpAccountName,
        byte[]? Sid,
        ref int cbSid,
        char[]? ReferencedDomainName,
        ref int cchReferencedDomainName,
        out int peUse);

    internal static ServiceSidResolution ResolveFixedServiceSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ServiceSidResolution.Failure(ServiceSidResolutionState.UnsupportedPlatform);
        }

        int requiredSidBytes = 0;
        int requiredDomainChars = 0;

        bool firstSucceeded = LookupAccountNameW(
            null,
            ServiceIdentityContract.QualifiedServiceAccountName,
            null,
            ref requiredSidBytes,
            null,
            ref requiredDomainChars,
            out _);
        int firstError = Marshal.GetLastWin32Error();

        ServiceSidBufferPlan plan = ServiceSidLookupInterpreter.InterpretFirstCall(
            firstSucceeded, firstError, requiredSidBytes, requiredDomainChars);
        if (!plan.Proceed)
        {
            return ServiceSidResolution.Failure(plan.FailureState);
        }

        byte[] sidBuffer = new byte[plan.SidBufferBytes];
        char[] domainBuffer = new char[plan.DomainBufferChars];
        int sidByteLength = plan.SidBufferBytes;
        int domainCharLength = plan.DomainBufferChars;

        try
        {
            bool secondSucceeded = LookupAccountNameW(
                null,
                ServiceIdentityContract.QualifiedServiceAccountName,
                sidBuffer,
                ref sidByteLength,
                domainBuffer,
                ref domainCharLength,
                out int sidNameUse);
            int secondError = Marshal.GetLastWin32Error();

            return ServiceSidLookupInterpreter.InterpretSecondCall(
                plan,
                secondSucceeded,
                secondError,
                sidByteLength,
                domainCharLength,
                sidBuffer,
                sidNameUse);
        }
        finally
        {
            Array.Clear(domainBuffer);
        }
    }
}
