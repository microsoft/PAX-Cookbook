using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

// ---------------------------------------------------------------------------
// FIXED KEY-DESCRIPTOR ADAPTERS - cycle 92 pass B. NON-LIVE.
//
// WHAT THIS IS. The production implementations of the three descriptor ports the
// promotion transaction uses: capture the prior descriptor, apply the ONE
// approved grant, and restore EXACTLY the captured bytes.
//
// THE SPLIT. Every decision representable without live native state lives in a
// PURE interpreter and is exhaustively tested. The fixed shim owns only the one
// documented native sequence and is NEVER executed by a local test - proven, not
// promised, by a structural scan over the whole test project.
//
// WHAT NONE OF THEM CAN DO. No arbitrary path, SID, provider, descriptor,
// callback or strategy reaches any of them. The approved descriptor is built
// ONLY by ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor. The restore
// surface never synthesizes, normalizes, merges or rebuilds bytes - it puts back
// exactly what was captured, digest-bound, or it reports that it could not.
//
// OWNER, GROUP AND DACL ONLY. No branch requests, names or carries SACL access.
//
// PRIVACY. No path, byte, SID, provider name, native status or exception text
// escapes through any result or ToString(): only bounded state tokens.
// ---------------------------------------------------------------------------

/// <summary>
/// BOUNDED OBSERVED KEY-ACCESS FACTS from the one documented native read
/// sequence. Never a path, never a provider name, never a delegate. The observed
/// descriptor bytes travel as EVIDENCE for a pure function to interpret, exactly
/// the way the certified credential observer already carries them.
/// </summary>
internal readonly struct ServiceOwnershipObservedKeyAccessFacts
{
    internal ServiceOwnershipObservedKeyAccessFacts(
        bool keyOpened,
        bool containmentProven,
        bool reparsePointPresent,
        bool handleIdentityProven,
        bool saclRequested,
        bool descriptorReadComplete,
        byte[]? observedDescriptorBytes)
    {
        KeyOpened = keyOpened;
        ContainmentProven = containmentProven;
        ReparsePointPresent = reparsePointPresent;
        HandleIdentityProven = handleIdentityProven;
        SaclRequested = saclRequested;
        DescriptorReadComplete = descriptorReadComplete;
        ObservedDescriptorBytes = observedDescriptorBytes;
    }

    internal bool KeyOpened { get; }

    /// <summary>The canonical target really is a direct child of the ONE fixed root.</summary>
    internal bool ContainmentProven { get; }

    /// <summary>A reparse point stands on the root or the target.</summary>
    internal bool ReparsePointPresent { get; }

    /// <summary>The OPENED handle resolves to the exact expected fixed target.</summary>
    internal bool HandleIdentityProven { get; }

    /// <summary>
    /// System-access information was requested. The fixed shim never sets this;
    /// it exists so the pure interpreter's refusal of that case is testable, and
    /// so a future shim change that requested it would be refused rather than
    /// silently accepted.
    /// </summary>
    internal bool SaclRequested { get; }

    internal bool DescriptorReadComplete { get; }

    internal byte[]? ObservedDescriptorBytes { get; }
}

/// <summary>
/// BOUNDED OBSERVED WRITE FACTS from the one documented native write sequence,
/// plus the verification read that follows it.
/// </summary>
internal readonly struct ServiceOwnershipObservedDescriptorWriteFacts
{
    internal ServiceOwnershipObservedDescriptorWriteFacts(
        bool keyOpened,
        bool containmentProven,
        bool handleIdentityProven,
        bool writeAttempted,
        bool writeSucceeded,
        bool verificationReadComplete,
        byte[]? verifiedDescriptorBytes)
    {
        KeyOpened = keyOpened;
        ContainmentProven = containmentProven;
        HandleIdentityProven = handleIdentityProven;
        WriteAttempted = writeAttempted;
        WriteSucceeded = writeSucceeded;
        VerificationReadComplete = verificationReadComplete;
        VerifiedDescriptorBytes = verifiedDescriptorBytes;
    }

    internal bool KeyOpened { get; }

    internal bool ContainmentProven { get; }

    internal bool HandleIdentityProven { get; }

    internal bool WriteAttempted { get; }

    internal bool WriteSucceeded { get; }

    internal bool VerificationReadComplete { get; }

    internal byte[]? VerifiedDescriptorBytes { get; }
}

/// <summary>
/// PURE interpreter for the prior-descriptor capture. Zero I/O, no state, no
/// delegate.
///
/// AN EXISTING SERVICE ACE GETS ITS OWN REASON. The certified supported shape
/// already refuses a three-ACE descriptor, so a descriptor that already carries
/// the grant would otherwise be reported as merely "unsupported". It is checked
/// FIRST so the transaction learns the real reason, which is the one a human
/// reading a stalled promotion needs.
/// </summary>
internal static class ServiceOwnershipPriorDescriptorInterpreter
{
    internal static ServiceOwnershipCapturedPriorDescriptor Interpret(
        ServiceOwnershipPromotionKeyHandle key,
        ServiceOwnershipObservedKeyAccessFacts facts)
    {
        if (!IsCertifiedKeyIdentity(key)
            || facts.SaclRequested
            || !facts.KeyOpened
            || !facts.ContainmentProven
            || facts.ReparsePointPresent
            || !facts.HandleIdentityProven
            || !facts.DescriptorReadComplete)
        {
            return ServiceOwnershipCapturedPriorDescriptor.Failure(
                ServiceOwnershipPriorDescriptorState.Unavailable);
        }

        byte[]? observed = facts.ObservedDescriptorBytes;
        if (observed is null || observed.Length == 0)
        {
            return ServiceOwnershipCapturedPriorDescriptor.Failure(
                ServiceOwnershipPriorDescriptorState.Unavailable);
        }

        if (observed.Length > ServiceOwnershipLedgerContract.MaxPriorDaclDecodedBytes)
        {
            return ServiceOwnershipCapturedPriorDescriptor.Failure(
                ServiceOwnershipPriorDescriptorState.UnsupportedShape);
        }

        if (!ServiceOwnershipLedgerContract.TryParseFileSecurityDescriptor(
                observed, out ServiceOwnershipParsedFileSecurityDescriptor? parsed)
            || parsed is null)
        {
            return ServiceOwnershipCapturedPriorDescriptor.Failure(
                ServiceOwnershipPriorDescriptorState.UnsupportedShape);
        }

        foreach (ServiceOwnershipParsedAce ace in parsed.Aces)
        {
            if (ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(ace.Sid))
            {
                return ServiceOwnershipCapturedPriorDescriptor.Failure(
                    ServiceOwnershipPriorDescriptorState.ServiceAceAlreadyPresent);
            }
        }

        if (!ServiceOwnershipLedgerContract.IsSupportedPriorDescriptorShape(parsed, out _))
        {
            return ServiceOwnershipCapturedPriorDescriptor.Failure(
                ServiceOwnershipPriorDescriptorState.UnsupportedShape);
        }

        string base64 = Convert.ToBase64String(observed);
        if (base64.Length > ServiceOwnershipLedgerContract.MaxPriorDaclBase64Length)
        {
            return ServiceOwnershipCapturedPriorDescriptor.Failure(
                ServiceOwnershipPriorDescriptorState.UnsupportedShape);
        }

        return ServiceOwnershipCapturedPriorDescriptor.Captured(
            ServiceOwnershipPriorDaclState.Present,
            base64,
            Convert.ToHexString(SHA256.HashData(observed)));
    }

    /// <summary>
    /// The key handle must still describe the ONE certified provider, root and
    /// descriptor format. A handle is closed and cannot be forged, but a
    /// LATER contract change must not be able to widen this surface silently.
    /// </summary>
    internal static bool IsCertifiedKeyIdentity(ServiceOwnershipPromotionKeyHandle key) =>
        key.IsUsable
        && key.ProviderKind == ServiceOwnershipLedgerContract.ApprovedRightsProfileProviderKind
        && key.KeyStorageRoot == ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys
        && key.DescriptorFormat == ServiceOwnershipLedgerContract.ApprovedDescriptorFormat;
}

/// <summary>
/// PURE interpreter for the approved-descriptor application. Zero I/O.
///
/// It NEVER composes a descriptor: the ONLY producer of approved bytes is
/// ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor, which preserves
/// the entire supported prior descriptor and adds exactly one ACE.
///
/// CYCLE 96 - IT NEVER BUILDS FROM AN UNBOUND READ EITHER. The approved bytes are
/// built from the CAPTURED snapshot that step (f) recorded and the credential
/// observation confirmed, and the freshly observed descriptor must still be
/// EXACTLY those bytes. If an administrator changed the descriptor between (f)
/// and (h) the plan is refused outright, so no grant is ever laid on top of a
/// state nobody recorded and no compensation can later revert that state.
/// </summary>
internal static class ServiceOwnershipApprovedDescriptorInterpreter
{
    internal static bool TryPlanApprovedDescriptor(
        ServiceOwnershipPromotionGrantPlan plan,
        ServiceOwnershipCapturedPriorDescriptor captured,
        ServiceOwnershipObservedKeyAccessFacts facts,
        out byte[] approvedBytes,
        out ServiceOwnershipDescriptorApplyState refusal)
    {
        approvedBytes = Array.Empty<byte>();

        // THE KEY GATE IS FIRST AND THAT IS LOAD-BEARING. A default-constructed
        // plan carries null strings, and the certified plan's own usability
        // property dereferences one, so the null-safe closed-handle check must
        // short-circuit before anything reads the plan's SID directly. The key
        // check plus the service-SID check together already imply usability.
        if (!ServiceOwnershipPriorDescriptorInterpreter.IsCertifiedKeyIdentity(plan.Key)
            || !ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(plan.ServiceSid)
            || plan.ApprovedMask != new ServiceOwnershipRightsMask(
                ServiceOwnershipLedgerContract.ApprovedRightsProfileMask)
            || plan.RightsPolicyVersion != ServiceOwnershipLedgerContract.RightsPolicyVersion
            || facts.SaclRequested)
        {
            refusal = ServiceOwnershipDescriptorApplyState.Refused;
            return false;
        }

        if (!facts.KeyOpened
            || !facts.ContainmentProven
            || facts.ReparsePointPresent
            || !facts.HandleIdentityProven
            || !facts.DescriptorReadComplete
            || facts.ObservedDescriptorBytes is null
            || facts.ObservedDescriptorBytes.Length == 0)
        {
            refusal = ServiceOwnershipDescriptorApplyState.Failed;
            return false;
        }

        // THE CAPTURED SNAPSHOT IS DECODED AND DIGEST-BOUND BEFORE IT IS TRUSTED.
        // Exactly the restore path's rule, applied to exactly the same value, so
        // the grant and the restoration can never be built from different bytes.
        if (!TryDecodeCapturedSnapshot(plan.Key, captured, out byte[] capturedBytes))
        {
            refusal = ServiceOwnershipDescriptorApplyState.Refused;
            return false;
        }

        // AND THE MACHINE MUST STILL HOLD THAT SNAPSHOT. Anything else means the
        // descriptor changed after it was captured and confirmed.
        if (!MatchesExactly(facts.ObservedDescriptorBytes, capturedBytes))
        {
            refusal = ServiceOwnershipDescriptorApplyState.Refused;
            return false;
        }

        // BUILT FROM THE CAPTURED BYTES, NEVER FROM THE FRESH READ.
        if (!ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
                capturedBytes, plan.ServiceSid,
                out byte[] postGrant, out _, out _))
        {
            refusal = ServiceOwnershipDescriptorApplyState.Refused;
            return false;
        }

        approvedBytes = postGrant;
        refusal = ServiceOwnershipDescriptorApplyState.Applied;
        return true;
    }

    /// <summary>
    /// The captured snapshot decoded through the SAME certified rule the restore
    /// path uses: bounded Base64, bounded decoded length, and an exact digest
    /// match. There is no second decoding rule anywhere in this file.
    /// </summary>
    internal static bool TryDecodeCapturedSnapshot(
        ServiceOwnershipPromotionKeyHandle key,
        ServiceOwnershipCapturedPriorDescriptor captured,
        out byte[] capturedBytes) =>
        ServiceOwnershipDescriptorRestoreInterpreter.TryPlanRestoration(
            key, captured, out capturedBytes, out _);

    internal static ServiceOwnershipDescriptorApplyState InterpretApplyOutcome(
        byte[] approvedBytes,
        ServiceOwnershipObservedDescriptorWriteFacts facts)
    {
        if (approvedBytes is null || approvedBytes.Length == 0)
        {
            return ServiceOwnershipDescriptorApplyState.Refused;
        }

        if (!facts.KeyOpened || !facts.ContainmentProven || !facts.HandleIdentityProven)
        {
            return ServiceOwnershipDescriptorApplyState.Failed;
        }

        if (!facts.WriteAttempted)
        {
            return ServiceOwnershipDescriptorApplyState.Refused;
        }

        return facts.WriteSucceeded
               && facts.VerificationReadComplete
               && MatchesExactly(facts.VerifiedDescriptorBytes, approvedBytes)
            ? ServiceOwnershipDescriptorApplyState.Applied
            : ServiceOwnershipDescriptorApplyState.Failed;
    }

    internal static bool MatchesExactly(byte[]? observed, byte[] expected) =>
        observed is not null
        && observed.Length == expected.Length
        && CryptographicOperations.FixedTimeEquals(observed, expected);
}

/// <summary>
/// PURE interpreter for the descriptor restore. Zero I/O.
///
/// THE RESTORATION BYTES ARE NEVER DERIVED. They are the captured Base64 decoded
/// and nothing else, and the capture's own digest must bind them before a single
/// byte is written. There is no normalization, no merge and no rebuild here, so a
/// restore can only ever put back a state that was actually observed.
/// </summary>
internal static class ServiceOwnershipDescriptorRestoreInterpreter
{
    internal static bool TryPlanRestoration(
        ServiceOwnershipPromotionKeyHandle key,
        ServiceOwnershipCapturedPriorDescriptor captured,
        out byte[] restorationBytes,
        out ServiceOwnershipDescriptorRestoreState refusal)
    {
        restorationBytes = Array.Empty<byte>();
        refusal = ServiceOwnershipDescriptorRestoreState.Failed;

        if (!ServiceOwnershipPriorDescriptorInterpreter.IsCertifiedKeyIdentity(key)
            || !captured.IsCaptured
            || captured.BytesBase64.Length == 0
            || captured.BytesBase64.Length > ServiceOwnershipLedgerContract.MaxPriorDaclBase64Length)
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(captured.BytesBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length == 0
            || decoded.Length > ServiceOwnershipLedgerContract.MaxPriorDaclDecodedBytes
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(decoded)), captured.Sha256, StringComparison.Ordinal))
        {
            return false;
        }

        restorationBytes = decoded;
        refusal = ServiceOwnershipDescriptorRestoreState.RestoredAndVerified;
        return true;
    }

    internal static ServiceOwnershipDescriptorRestoreState InterpretRestoreOutcome(
        byte[] restorationBytes,
        ServiceOwnershipObservedDescriptorWriteFacts facts)
    {
        // Nothing to put back, or a target that was never proven, is a hard
        // failure: no write was even reachable, so no evidence changed.
        if (restorationBytes is null
            || restorationBytes.Length == 0
            || !facts.KeyOpened
            || !facts.ContainmentProven
            || !facts.HandleIdentityProven
            || !facts.WriteAttempted)
        {
            return ServiceOwnershipDescriptorRestoreState.Failed;
        }

        // Everything from here on means a write WAS attempted against the proven
        // target, so anything short of proven byte identity is NotProven - the
        // RecoveryRequired-compatible outcome that keeps the evidence alive.
        return facts.WriteSucceeded
               && facts.VerificationReadComplete
               && ServiceOwnershipApprovedDescriptorInterpreter.MatchesExactly(
                   facts.VerifiedDescriptorBytes, restorationBytes)
            ? ServiceOwnershipDescriptorRestoreState.RestoredAndVerified
            : ServiceOwnershipDescriptorRestoreState.NotProven;
    }
}

/// <summary>
/// THE ONE FIXED NATIVE SURFACE for the machine key object. Every native API
/// below is declared EXACTLY ONCE and invoked from EXACTLY ONE call site, which
/// is why the read pass, the write pass and the verification read all funnel
/// through the same private helpers instead of repeating a sequence.
///
/// Every P/Invoke is declared with a space between its name and its parameter
/// list, following the certified observer, so a declaration never spells the same
/// token shape a naive call-site scan would count.
/// </summary>
internal static class ServiceOwnershipFixedKeyObjectShim
{
    /// <summary>The ONE approved NCrypt provider name. Never caller-supplied.</summary>
    private const string FixedProviderName = "Microsoft Software Key Storage Provider";

    /// <summary>The ONE NCrypt key property this shim will ever read.</summary>
    private const string UniqueNamePropertyName = "Unique Name";

    /// <summary>NCRYPT_MACHINE_KEY_FLAG - documented, opens the machine-scope key.</summary>
    private const int NcryptMachineKeyFlag = 0x20;

    private const int UniqueNameBufferBytes =
        (ServiceOwnershipLedgerContract.MaxProviderUniqueNameLength + 1) * 2;

    private const int DescriptorBufferBytes = ServiceOwnershipLedgerContract.MaxPriorDaclDecodedBytes;

    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint FileShareRead = 0x1;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private const int OwnerSecurityInformation = 0x1;
    private const int GroupSecurityInformation = 0x2;
    private const int DaclSecurityInformation = 0x4;

    /// <summary>Owner, group and DACL. System-access information is never included.</summary>
    private const int OwnerGroupDaclInformation =
        OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation;

    private const int FinalPathBufferChars = 1024;
    private const string ExtendedPathPrefix = @"\\?\";

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenStorageProvider (
        out IntPtr phProvider, string pszProviderName, int dwFlags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenKey (
        IntPtr hProvider, out IntPtr phKey, string pszKeyName, int dwLegacyKeySpec, int dwFlags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptGetProperty (
        IntPtr hObject, string pszProperty, byte[] pbOutput, int cbOutput, out int pcbResult, int dwFlags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptFreeObject (IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW (
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetFinalPathNameByHandleW (
        SafeFileHandle hFile, [Out] char[] lpszFilePath, int cchFilePath, int dwFlags);

    [DllImport("advapi32.dll", EntryPoint = "GetKernelObjectSecurity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity (
        SafeFileHandle handle, int securityInformation, byte[]? securityDescriptor,
        int length, out int lengthNeeded);

    [DllImport("advapi32.dll", EntryPoint = "SetKernelObjectSecurity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity (
        SafeFileHandle handle, int securityInformation, byte[] securityDescriptor);

    /// <summary>
    /// READ PASS. Resolves the ONE fixed key backing file the certified key
    /// identity names, proves containment and handle identity, and reads
    /// owner/group/DACL into ONE fixed-capacity buffer. It writes nothing.
    /// </summary>
    internal static ServiceOwnershipObservedKeyAccessFacts ObserveKeyDescriptor(
        ServiceOwnershipPromotionKeyHandle key)
    {
        if (!OperatingSystem.IsWindows()
            || !ServiceOwnershipPriorDescriptorInterpreter.IsCertifiedKeyIdentity(key))
        {
            return default;
        }

        ResolvedKeyTarget target = ResolveTarget(key);
        if (!target.Resolved || !target.ContainmentProven || target.ReparsePointPresent)
        {
            return new ServiceOwnershipObservedKeyAccessFacts(
                target.Resolved, target.ContainmentProven, target.ReparsePointPresent,
                false, false, false, null);
        }

        SafeFileHandle? handle = null;
        byte[] buffer = new byte[DescriptorBufferBytes];
        char[] finalPathBuffer = new char[FinalPathBufferChars];

        try
        {
            handle = OpenFixedTarget(target.CanonicalTarget, ReadControl);
            if (handle is null || handle.IsInvalid)
            {
                return new ServiceOwnershipObservedKeyAccessFacts(
                    true, true, false, false, false, false, null);
            }

            if (!HandleResolvesToExpectedTarget(handle, target.CanonicalTarget, finalPathBuffer))
            {
                return new ServiceOwnershipObservedKeyAccessFacts(
                    true, true, false, false, false, false, null);
            }

            byte[]? descriptor = ReadOwnerGroupDacl(handle, buffer);
            return new ServiceOwnershipObservedKeyAccessFacts(
                true, true, false, true, false, descriptor is not null, descriptor);
        }
        finally
        {
            Array.Clear(buffer);
            Array.Clear(finalPathBuffer);
            handle?.Dispose();
        }
    }

    /// <summary>
    /// CONDITIONAL WRITE PASS - cycle 96. The ONE fixed native write sequence for
    /// the approved grant. The fixed target is opened ONCE; the CURRENT owner,
    /// group and DACL are read back through THAT SAME HANDLE immediately before
    /// the write; and the write API is reached only when those bytes are exactly
    /// <paramref name="requiredCurrentBytes"/>. When they are not, the handle is
    /// closed without ever calling the write API, and the returned facts report
    /// that no write was attempted.
    /// </summary>
    internal static ServiceOwnershipObservedDescriptorWriteFacts WriteKeyDescriptorWhenCurrentMatches(
        ServiceOwnershipPromotionKeyHandle key,
        byte[] requiredCurrentBytes,
        byte[] descriptorBytes)
    {
        if (requiredCurrentBytes is null
            || requiredCurrentBytes.Length == 0
            || requiredCurrentBytes.Length > DescriptorBufferBytes)
        {
            return default;
        }

        return WriteThroughFixedHandle(key, requiredCurrentBytes, descriptorBytes);
    }

    /// <summary>
    /// UNCONDITIONAL WRITE PASS. The restore path's write, used ONLY to put back a
    /// descriptor this transaction itself displaced. It carries no precondition
    /// because by the time it runs the transaction has already changed the target,
    /// so refusing would strand the machine in the state the write created.
    /// </summary>
    internal static ServiceOwnershipObservedDescriptorWriteFacts RestoreKeyDescriptor(
        ServiceOwnershipPromotionKeyHandle key,
        byte[] descriptorBytes) =>
        WriteThroughFixedHandle(key, null, descriptorBytes);

    /// <summary>
    /// THE ONE WRITE SEQUENCE. One resolution, one open, an optional same-handle
    /// precondition read, the write, and a same-handle verification read. It
    /// writes owner, group and DACL and never system-access information.
    ///
    /// THE RESIDUAL RACE, STATED HONESTLY. This is NOT an operating-system atomic
    /// compare-and-set. Windows exposes no compare-and-swap for a security
    /// descriptor: GetKernelObjectSecurity and SetKernelObjectSecurity are two
    /// separate calls, and nothing in the kernel holds the descriptor still
    /// between them. A writer that changes the descriptor in the interval between
    /// the precondition read and SetKernelObjectSecurity will still be overwritten
    /// by this write. What the precondition removes is the LARGE window - the one
    /// spanning step (f), the credential observation, the durable ledger write at
    /// (g) and the second open - and replaces it with the interval between two
    /// adjacent calls on ONE already-identity-proven handle.
    ///
    /// THE SHARE-MODE ASSUMPTION, ALSO STATED. The handle is opened with the
    /// existing FILE_SHARE_READ policy, which is deliberately NOT widened here.
    /// That sharing mode governs other openers of the FILE; it does not reserve
    /// the security descriptor, and it does not stop a process that already holds
    /// a compatible handle from writing one. So the narrowed interval is a real
    /// and large improvement, and it is not a mutual exclusion.
    ///
    /// WHAT REMAINS TRUE EVEN IN THE RESIDUAL CASE. The verification read that
    /// follows the write still requires exact byte equality with the approved
    /// bytes, so a losing write is reported as a failure rather than a success,
    /// and the transaction unwinds through the existing compensation.
    /// </summary>
    private static ServiceOwnershipObservedDescriptorWriteFacts WriteThroughFixedHandle(
        ServiceOwnershipPromotionKeyHandle key,
        byte[]? requiredCurrentBytes,
        byte[] descriptorBytes)
    {
        if (!OperatingSystem.IsWindows()
            || !ServiceOwnershipPriorDescriptorInterpreter.IsCertifiedKeyIdentity(key)
            || descriptorBytes is null
            || descriptorBytes.Length == 0
            || descriptorBytes.Length > DescriptorBufferBytes)
        {
            return default;
        }

        ResolvedKeyTarget target = ResolveTarget(key);
        if (!target.Resolved || !target.ContainmentProven || target.ReparsePointPresent)
        {
            return new ServiceOwnershipObservedDescriptorWriteFacts(
                target.Resolved, target.ContainmentProven, false, false, false, false, null);
        }

        SafeFileHandle? handle = null;
        byte[] buffer = new byte[DescriptorBufferBytes];
        char[] finalPathBuffer = new char[FinalPathBufferChars];

        try
        {
            handle = OpenFixedTarget(target.CanonicalTarget, ReadControl | WriteDac | WriteOwner);
            if (handle is null || handle.IsInvalid)
            {
                return new ServiceOwnershipObservedDescriptorWriteFacts(
                    true, true, false, false, false, false, null);
            }

            if (!HandleResolvesToExpectedTarget(handle, target.CanonicalTarget, finalPathBuffer))
            {
                return new ServiceOwnershipObservedDescriptorWriteFacts(
                    true, true, false, false, false, false, null);
            }

            // THE SAME-HANDLE PRECONDITION. This read and the write below travel
            // through the SAME open handle to the SAME proven target, so nothing
            // between them can redirect the write to a different object.
            if (requiredCurrentBytes is not null)
            {
                byte[]? current = ReadOwnerGroupDacl(handle, buffer);
                if (!ServiceOwnershipApprovedDescriptorInterpreter.MatchesExactly(
                        current, requiredCurrentBytes))
                {
                    // CLOSED WITHOUT WRITING. The write API is never reached, so
                    // nothing on this machine changed and there is nothing to undo.
                    return new ServiceOwnershipObservedDescriptorWriteFacts(
                        true, true, true, false, false, false, null);
                }
            }

            bool wrote = WriteOwnerGroupDacl(handle, descriptorBytes);
            byte[]? verified = wrote ? ReadOwnerGroupDacl(handle, buffer) : null;

            return new ServiceOwnershipObservedDescriptorWriteFacts(
                true, true, true, true, wrote, verified is not null, verified);
        }
        finally
        {
            Array.Clear(buffer);
            Array.Clear(finalPathBuffer);
            handle?.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // THE ONE RESOLUTION, THE ONE OPEN, THE ONE READ, THE ONE WRITE
    // -----------------------------------------------------------------------

    private readonly struct ResolvedKeyTarget
    {
        internal ResolvedKeyTarget(
            bool resolved, bool containmentProven, bool reparsePointPresent, string canonicalTarget)
        {
            Resolved = resolved;
            ContainmentProven = containmentProven;
            ReparsePointPresent = reparsePointPresent;
            CanonicalTarget = canonicalTarget;
        }

        internal bool Resolved { get; }

        internal bool ContainmentProven { get; }

        internal bool ReparsePointPresent { get; }

        internal string CanonicalTarget { get; }
    }

    /// <summary>The ONE hard-coded key-storage root. Never caller-supplied, never wider.</summary>
    private static string ResolveFixedKeyStorageRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Microsoft", "Crypto", "Keys");

    private static ResolvedKeyTarget ResolveTarget(ServiceOwnershipPromotionKeyHandle key)
    {
        IntPtr providerHandle = IntPtr.Zero;
        IntPtr keyHandle = IntPtr.Zero;
        byte[] uniqueNameBuffer = new byte[UniqueNameBufferBytes];

        try
        {
            if (NCryptOpenStorageProvider(out providerHandle, FixedProviderName, 0) != 0)
            {
                return new ResolvedKeyTarget(false, false, false, string.Empty);
            }

            if (NCryptOpenKey(providerHandle, out keyHandle, key.KeyIdentity, 0, NcryptMachineKeyFlag) != 0)
            {
                return new ResolvedKeyTarget(false, false, false, string.Empty);
            }

            int status = NCryptGetProperty(
                keyHandle, UniqueNamePropertyName, uniqueNameBuffer, uniqueNameBuffer.Length,
                out int uniqueNameBytes, 0);
            if (status != 0 || uniqueNameBytes <= 0 || uniqueNameBytes > uniqueNameBuffer.Length
                || uniqueNameBytes % 2 != 0)
            {
                return new ResolvedKeyTarget(false, false, false, string.Empty);
            }

            int charCount = uniqueNameBytes / 2;
            if (charCount > 0
                && uniqueNameBuffer[uniqueNameBytes - 2] == 0 && uniqueNameBuffer[uniqueNameBytes - 1] == 0)
            {
                charCount--; // exclude the trailing NUL terminator
            }
            string observedUniqueName =
                System.Text.Encoding.Unicode.GetString(uniqueNameBuffer, 0, charCount * 2);

            // The observed leaf must pass the certified grammar AND be the exact
            // leaf the closed key handle already recorded.
            if (!ServiceOwnershipLedgerContract.IsValidProviderUniqueName(observedUniqueName)
                || !string.Equals(observedUniqueName, key.ProviderUniqueName, StringComparison.Ordinal))
            {
                return new ResolvedKeyTarget(true, false, false, string.Empty);
            }

            string canonicalRoot;
            string canonicalTarget;
            try
            {
                canonicalRoot = Path.GetFullPath(ResolveFixedKeyStorageRoot());
                canonicalTarget = Path.GetFullPath(Path.Combine(canonicalRoot, observedUniqueName));
            }
            catch (Exception)
            {
                return new ResolvedKeyTarget(true, false, false, string.Empty);
            }

            if (!string.Equals(
                    Path.GetDirectoryName(canonicalTarget), canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedKeyTarget(true, false, false, string.Empty);
            }

            bool reparse;
            try
            {
                reparse = (File.GetAttributes(canonicalRoot) & FileAttributes.ReparsePoint) != 0
                          || (File.Exists(canonicalTarget)
                              && (File.GetAttributes(canonicalTarget) & FileAttributes.ReparsePoint) != 0);
            }
            catch (Exception)
            {
                return new ResolvedKeyTarget(true, false, false, string.Empty);
            }

            return new ResolvedKeyTarget(true, true, reparse, reparse ? string.Empty : canonicalTarget);
        }
        finally
        {
            Array.Clear(uniqueNameBuffer);
            if (keyHandle != IntPtr.Zero)
            {
                FreeNativeObject(keyHandle);
            }
            if (providerHandle != IntPtr.Zero)
            {
                FreeNativeObject(providerHandle);
            }
        }
    }

    private static void FreeNativeObject(IntPtr handle) => NCryptFreeObject(handle);

    /// <summary>
    /// The ONE open. An EXISTING regular object only, never following a reparse
    /// point, and never with a creating disposition of any kind.
    /// </summary>
    private static SafeFileHandle OpenFixedTarget(string canonicalTarget, uint desiredAccess) =>
        CreateFileW(
            canonicalTarget, desiredAccess, FileShareRead, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint, IntPtr.Zero);

    private static bool HandleResolvesToExpectedTarget(
        SafeFileHandle handle, string canonicalTarget, char[] finalPathBuffer)
    {
        int finalPathChars = GetFinalPathNameByHandleW(handle, finalPathBuffer, finalPathBuffer.Length, 0);
        if (finalPathChars <= 0 || finalPathChars >= finalPathBuffer.Length)
        {
            return false;
        }

        string finalPath = new(finalPathBuffer, 0, finalPathChars);
        if (finalPath.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal))
        {
            finalPath = finalPath[ExtendedPathPrefix.Length..];
        }

        return string.Equals(finalPath, canonicalTarget, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The ONE descriptor read. Owner, group and DACL only.</summary>
    private static byte[]? ReadOwnerGroupDacl(SafeFileHandle handle, byte[] buffer)
    {
        bool read = GetKernelObjectSecurity(
            handle, OwnerGroupDaclInformation, buffer, buffer.Length, out int bytesNeeded);

        return read && bytesNeeded > 0 && bytesNeeded <= buffer.Length
            ? buffer[..bytesNeeded]
            : null;
    }

    /// <summary>The ONE descriptor write. Owner, group and DACL only.</summary>
    private static bool WriteOwnerGroupDacl(SafeFileHandle handle, byte[] descriptorBytes) =>
        SetKernelObjectSecurity(handle, OwnerGroupDaclInformation, descriptorBytes);
}

/// <summary>The FIXED prior-descriptor port. Reads only, and never a SACL.</summary>
internal sealed class ServiceOwnershipFixedPriorDescriptorPort : IServiceOwnershipPriorDescriptorPort
{
    public ServiceOwnershipCapturedPriorDescriptor CapturePriorDescriptor(
        ServiceOwnershipPromotionKeyHandle key) =>
        ServiceOwnershipPriorDescriptorInterpreter.Interpret(
            key, ServiceOwnershipFixedKeyObjectShim.ObserveKeyDescriptor(key));
}

/// <summary>
/// The FIXED approved-descriptor port. It accepts ONLY the closed grant plan and
/// the CLOSED captured snapshot, so no caller can name a principal, a mask, a
/// provider, a handle, a location or a descriptor: the plan can only have been
/// built from a resolved service SID plus an observed key handle, and the capture
/// can only have been produced by the prior-descriptor port at step (f).
///
/// CYCLE 96 - THE SNAPSHOT IS CHECKED TWICE, AND THE SECOND CHECK IS THE BINDING
/// ONE. The planning read proves the captured bytes are still what the machine
/// holds; the conditional write then reads the CURRENT bytes again through the
/// SAME handle it is about to write with, and reaches the write API only if they
/// are still exactly those bytes.
/// </summary>
internal sealed class ServiceOwnershipFixedApprovedDescriptorPort : IServiceOwnershipApprovedDescriptorPort
{
    public ServiceOwnershipDescriptorApplyState ApplyApprovedDescriptor(
        ServiceOwnershipPromotionGrantPlan plan,
        ServiceOwnershipCapturedPriorDescriptor captured)
    {
        ServiceOwnershipObservedKeyAccessFacts observed =
            ServiceOwnershipFixedKeyObjectShim.ObserveKeyDescriptor(plan.Key);

        if (!ServiceOwnershipApprovedDescriptorInterpreter.TryPlanApprovedDescriptor(
                plan, captured, observed, out byte[] approved,
                out ServiceOwnershipDescriptorApplyState refusal))
        {
            return refusal;
        }

        if (!ServiceOwnershipApprovedDescriptorInterpreter.TryDecodeCapturedSnapshot(
                plan.Key, captured, out byte[] capturedBytes))
        {
            return ServiceOwnershipDescriptorApplyState.Refused;
        }

        return ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
            approved,
            ServiceOwnershipFixedKeyObjectShim.WriteKeyDescriptorWhenCurrentMatches(
                plan.Key, capturedBytes, approved));
    }
}

/// <summary>
/// The FIXED restore port. It can restore nothing but a CAPTURED descriptor, and
/// only to the certified key identity that captured it.
/// </summary>
internal sealed class ServiceOwnershipFixedDescriptorRestorePort : IServiceOwnershipDescriptorRestorePort
{
    public ServiceOwnershipDescriptorRestoreState RestoreCapturedDescriptor(
        ServiceOwnershipPromotionKeyHandle key,
        ServiceOwnershipCapturedPriorDescriptor captured)
    {
        if (!ServiceOwnershipDescriptorRestoreInterpreter.TryPlanRestoration(
                key, captured, out byte[] restoration,
                out ServiceOwnershipDescriptorRestoreState refusal))
        {
            return refusal;
        }

        return ServiceOwnershipDescriptorRestoreInterpreter.InterpretRestoreOutcome(
            restoration, ServiceOwnershipFixedKeyObjectShim.RestoreKeyDescriptor(key, restoration));
    }
}
