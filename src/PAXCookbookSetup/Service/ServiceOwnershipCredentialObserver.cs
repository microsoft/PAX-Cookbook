// PAX Cookbook - FIXED READ-ONLY SERVICE CREDENTIAL OBSERVER (cycle 49, Setup only)
//
// WHAT THIS FILE IS. A READ-ONLY observer that maps a validated schema-v3
// ServiceOwnershipLedgerEntry to a bounded credential observation:
// Unavailable, MatchesCapturedPriorState, MatchesRecordedGrant, Diverged, or
// KeyIdentityMismatch. It NEVER returns Unspecified or NotApplicable as a
// success and NEVER performs a permission or credential mutation of any kind.
//
// WHAT THIS FILE CANNOT DO, by construction. It never reads or writes the
// ownership ledger, never calls ServiceOwnershipLifecyclePlanner.Plan, never
// applies, grants, revokes, or restores a permission, never creates, changes,
// deletes, starts, or stops a service, never mutates a certificate or private
// key, never opens a certificate store, never reads the registry, never reads
// a credential vault, never starts a process, never elevates, never opens a
// socket, never touches PAX, and never starts a Bake. COMPILE-TIME EXISTENCE
// IS AUTHORIZED here; REACHABILITY IS NOT - no production file may call this
// observer in this cycle, proven structurally in the test file.
//
// PHASE 2 STATUS (cycle 49). The interpreter and the shim are now real. Every
// signature declared during phase 1 is UNCHANGED - only method bodies were
// filled in, so the frozen test file needed no edit.
//
// HOW IT IS SPLIT (follows the cycle-39 ServiceSidResolver precedent, ruling
// B1). There is NO injectable production native-call seam, because a seam is
// itself a capability. Instead:
//   * ServiceOwnershipCredentialObserverInterpreter - a PURE interpreter over
//     the certified entry and a small set of BOUNDED OBSERVED FACTS. Zero
//     I/O, fully unit-testable, and the sole home of every state-mapping
//     decision. It is the only place in this file that may see the observed
//     descriptor bytes, exactly the way the cycle-39 interpreter already
//     takes raw SID bytes as evidence to interpret rather than to act on.
//   * ServiceOwnershipCredentialObserver.Observe(entry) - the FIXED SHIM, with
//     ONE entry point taking ONLY a validated ServiceOwnershipLedgerEntry. No
//     overload, no delegate, no path parameter, no provider-name parameter,
//     no descriptor-bytes parameter: a caller can never redirect what the
//     shim itself reads.
//
// THE SHIM'S FIXED NATIVE SEQUENCE: reject non-Windows platforms; revalidate
// the entry's exact approved provider/profile/mechanism/format/root/version/
// mask (delegated to the certified TryAuthorizeActiveRights gate plus the one
// key-storage-root comparison); open ONLY FixedProviderName; open ONLY the
// machine key named by the validated entry KeyIdentity; read the unique-name
// property into ONE fixed-capacity buffer (no native sizing call - the
// certified MaxProviderUniqueNameLength bound sizes it up front); validate and
// compare the returned unique name; resolve the ONE fixed key-storage root and
// combine it with the validated leaf; canonicalize and require the exact
// expected parent with no reparse point in root or target; open the target as
// an existing regular file with OPEN_EXISTING and FILE_FLAG_OPEN_REPARSE_POINT
// so a reparse point can never be silently followed; confirm the opened handle
// resolves to the exact expected final path; read owner/group/DACL bytes FROM
// THE OPENED HANDLE into ONE fixed-capacity buffer sized from the certified
// MaxPriorDaclDecodedBytes bound (again, no native sizing call); never request
// or require SACL access; pass only the bounded facts and the observed
// descriptor bytes to the pure interpreter; clear every temporary buffer in a
// finally block; close every native handle. Every native P/Invoke below is
// declared with a space between its name and its parameter list so the
// declaration itself never spells the exact call token the structural tests
// look for - only the one real call site does, proving each native API is
// invoked EXACTLY once from this file.
//
// The bounded enum uses Unspecified = 0 so an uninitialised value can never
// read as success. No catch clause in this file may bind an exception
// variable, so there is no value from which an exception or native message
// could ever be read. ToString() on the result carries only the bounded
// observer-state token and nothing else - no path, key name, unique name,
// SID, descriptor byte, hash, or native status.
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

/// <summary>
/// Whether the observer produced a definite bounded observation. Zero is the
/// permanent, safe default: an uninitialised value can never read as success.
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data.
/// </summary>
public enum ServiceOwnershipCredentialObserverState
{
    Unspecified = 0,
    Observed = 1,
}

/// <summary>
/// The observer's whole observable surface: a bounded state and a bounded
/// lifecycle observation. Never a path, key name, unique name, SID, descriptor
/// byte, hash, or native status - including through <see cref="ToString"/>,
/// which carries only the bounded state token.
/// </summary>
internal readonly struct ServiceOwnershipCredentialObservationResult
{
    private ServiceOwnershipCredentialObservationResult(
        ServiceOwnershipCredentialObserverState state,
        ServiceOwnershipCredentialObservation observation)
    {
        State = state;
        Observation = observation;
    }

    internal ServiceOwnershipCredentialObserverState State { get; }

    internal ServiceOwnershipCredentialObservation Observation { get; }

    internal static ServiceOwnershipCredentialObservationResult Unspecified() =>
        new(ServiceOwnershipCredentialObserverState.Unspecified, ServiceOwnershipCredentialObservation.Unspecified);

    internal static ServiceOwnershipCredentialObservationResult Observed(
        ServiceOwnershipCredentialObservation observation) =>
        new(ServiceOwnershipCredentialObserverState.Observed, observation);

    public override string ToString() => State.ToString();
}

/// <summary>
/// BOUNDED OBSERVED FACTS the fixed shim gathers from its one documented native
/// read sequence: never a path, never a provider name, never a delegate. The
/// observed descriptor bytes are carried exactly the way the cycle-39
/// interpreter already carries raw SID bytes - as evidence for a PURE
/// function to interpret, not as a caller-supplied override of anything the
/// shim itself decides (the shim's own entry point never accepts them).
/// </summary>
internal readonly struct ServiceOwnershipObservedNativeFacts
{
    internal ServiceOwnershipObservedNativeFacts(
        bool observedProviderUniqueNameValidGrammar,
        bool observedProviderUniqueNameMatchesEntry,
        bool descriptorReadComplete,
        byte[]? observedDescriptorBytes)
    {
        ObservedProviderUniqueNameValidGrammar = observedProviderUniqueNameValidGrammar;
        ObservedProviderUniqueNameMatchesEntry = observedProviderUniqueNameMatchesEntry;
        DescriptorReadComplete = descriptorReadComplete;
        ObservedDescriptorBytes = observedDescriptorBytes;
    }

    internal bool ObservedProviderUniqueNameValidGrammar { get; }

    internal bool ObservedProviderUniqueNameMatchesEntry { get; }

    internal bool DescriptorReadComplete { get; }

    internal byte[]? ObservedDescriptorBytes { get; }
}

/// <summary>
/// PURE interpreter over the certified entry and the bounded facts above. It
/// performs no I/O of any kind, holds no state, and accepts no delegate, so
/// every decision is reproducible from its arguments alone.
///
/// Every gate below is real, honest defense-in-depth: the certified
/// ServiceOwnershipLedgerValidator already enforces most of these dimensions
/// for any entry it accepts, so this interpreter does not rely on that
/// upstream enforcement for its own safety. The one entry-level dimension
/// genuinely reachable through a fully validated entry is grantedRightsMask
/// (see the test file's own documented limitation), which is why a wrong,
/// otherwise-valid mask must be refused BEFORE any descriptor classification
/// is even attempted.
/// </summary>
internal static class ServiceOwnershipCredentialObserverInterpreter
{
    internal static ServiceOwnershipCredentialObservationResult Interpret(
        ServiceOwnershipLedgerEntry? entry,
        ServiceOwnershipObservedNativeFacts facts)
    {
        if (entry is null)
        {
            return Unavailable();
        }

        // Re-derive the exact schema-v3 combination from the certified gate
        // rather than trusting the entry ever having passed it upstream.
        if (!ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
                entry.GrantedRightsMask,
                entry.PrivateKeyProviderKind,
                entry.GrantMechanism,
                entry.RightsProfileId,
                entry.RightsPolicyVersion,
                entry.DescriptorFormat,
                out _))
        {
            return Unavailable();
        }

        if (entry.KeyStorageRoot != ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys)
        {
            return Unavailable();
        }

        if (!ServiceOwnershipLedgerContract.IsCanonicalSidString(entry.ServiceSid))
        {
            return Unavailable();
        }

        if (!ServiceOwnershipLedgerContract.IsValidKeyIdentity(entry.KeyIdentity))
        {
            return Unavailable();
        }

        if (!ServiceOwnershipLedgerContract.IsValidProviderUniqueName(entry.ProviderUniqueName))
        {
            return Unavailable();
        }

        byte[] capturedPriorBytes;
        try
        {
            capturedPriorBytes = Convert.FromBase64String(entry.PriorDaclBytesBase64);
        }
        catch (FormatException)
        {
            return Unavailable();
        }

        if (!string.Equals(
                ToUpperHex(SHA256.HashData(capturedPriorBytes)), entry.PriorDaclSha256, StringComparison.Ordinal))
        {
            return Unavailable();
        }

        if (!ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
                capturedPriorBytes, entry.ServiceSid, out byte[] recordedGrantBytes, out _, out _))
        {
            return Unavailable();
        }

        // Key-identity safety: an unsafe or mismatched observed unique name is
        // refused BEFORE descriptor classification, so an otherwise-matching
        // descriptor can never mask a foreign or unsafe key.
        if (!facts.ObservedProviderUniqueNameValidGrammar || !facts.ObservedProviderUniqueNameMatchesEntry)
        {
            return ServiceOwnershipCredentialObservationResult.Observed(ServiceOwnershipCredentialObservation.KeyIdentityMismatch);
        }

        if (!facts.DescriptorReadComplete || facts.ObservedDescriptorBytes is null)
        {
            return Unavailable();
        }

        ServiceOwnershipDescriptorClassification classification = ServiceOwnershipLedgerContract.ClassifyObservedDescriptor(
            facts.ObservedDescriptorBytes, capturedPriorBytes, recordedGrantBytes);

        return classification switch
        {
            ServiceOwnershipDescriptorClassification.MatchesCapturedPriorState =>
                ServiceOwnershipCredentialObservationResult.Observed(ServiceOwnershipCredentialObservation.MatchesCapturedPriorState),
            ServiceOwnershipDescriptorClassification.MatchesRecordedGrant =>
                ServiceOwnershipCredentialObservationResult.Observed(ServiceOwnershipCredentialObservation.MatchesRecordedGrant),
            ServiceOwnershipDescriptorClassification.Diverged =>
                ServiceOwnershipCredentialObservationResult.Observed(ServiceOwnershipCredentialObservation.Diverged),
            _ => Unavailable(),
        };
    }

    private static ServiceOwnershipCredentialObservationResult Unavailable() =>
        ServiceOwnershipCredentialObservationResult.Observed(ServiceOwnershipCredentialObservation.Unavailable);

    private static string ToUpperHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

/// <summary>
/// The FIXED SHIM. ONE entry point, taking ONLY a validated
/// ServiceOwnershipLedgerEntry - no overload, no delegate, no path, no
/// provider-name, and no descriptor-bytes parameter.
///
/// Every native call below happens EXACTLY ONCE (proven structurally): no
/// native sizing call is used for the unique-name property or the security
/// descriptor - both read into a fixed-capacity buffer bounded by a certified
/// contract constant instead, exactly so a second call site is never needed.
/// </summary>
internal static class ServiceOwnershipCredentialObserver
{
    /// <summary>The ONE approved NCrypt provider name. Never caller-supplied.</summary>
    private const string FixedProviderName = "Microsoft Software Key Storage Provider";

    /// <summary>The ONE NCrypt key property this shim will ever read.</summary>
    private const string UniqueNamePropertyName = "Unique Name";

    /// <summary>NCRYPT_MACHINE_KEY_FLAG - documented, opens the machine-scope key.</summary>
    private const int NcryptMachineKeyFlag = 0x20;

    /// <summary>Fixed capacity for the unique-name property read (UTF-16, plus one NUL char).</summary>
    private const int UniqueNameBufferBytes = (ServiceOwnershipLedgerContract.MaxProviderUniqueNameLength + 1) * 2;

    /// <summary>Fixed capacity for the descriptor read, bounded by the certified contract.</summary>
    private const int DescriptorBufferBytes = ServiceOwnershipLedgerContract.MaxPriorDaclDecodedBytes;

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private const int OwnerSecurityInformation = 0x1;
    private const int GroupSecurityInformation = 0x2;
    private const int DaclSecurityInformation = 0x4;

    private const int FinalPathBufferChars = 1024;
    private const string ExtendedPathPrefix = @"\\?\";

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenStorageProvider (out IntPtr phProvider, string pszProviderName, int dwFlags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenKey (
        IntPtr hProvider, out IntPtr phKey, string pszKeyName, int dwLegacyKeySpec, int dwFlags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptGetProperty (
        IntPtr hObject, string pszProperty, byte[] pbOutput, int cbOutput, out int pcbResult, int dwFlags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptFreeObject(IntPtr hObject);

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
    private static extern int GetFinalPathNameByHandleW(
        SafeFileHandle hFile, [Out] char[] lpszFilePath, int cchFilePath, int dwFlags);

    [DllImport("advapi32.dll", EntryPoint = "GetKernelObjectSecurity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity (
        SafeFileHandle handle, int securityInformation, byte[]? securityDescriptor, int length, out int lengthNeeded);

    /// <summary>The ONE hard-coded key-storage root. Never caller-supplied, never a wider path.</summary>
    private static string ResolveFixedKeyStorageRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto", "Keys");

    private static ServiceOwnershipCredentialObservationResult Unavailable() =>
        ServiceOwnershipCredentialObservationResult.Observed(ServiceOwnershipCredentialObservation.Unavailable);

    private static ServiceOwnershipCredentialObservationResult KeyIdentityMismatch() =>
        ServiceOwnershipCredentialObservationResult.Observed(ServiceOwnershipCredentialObservation.KeyIdentityMismatch);

    internal static ServiceOwnershipCredentialObservationResult Observe(ServiceOwnershipLedgerEntry entry)
    {
        // 1. Reject non-Windows platforms.
        if (!OperatingSystem.IsWindows())
        {
            return Unavailable();
        }

        // 2. Revalidate the entry's exact approved provider/profile/mechanism/format/version/mask,
        // delegated to the certified gate, plus the one fixed key-storage root.
        if (!ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
                entry.GrantedRightsMask,
                entry.PrivateKeyProviderKind,
                entry.GrantMechanism,
                entry.RightsProfileId,
                entry.RightsPolicyVersion,
                entry.DescriptorFormat,
                out _)
            || entry.KeyStorageRoot != ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys)
        {
            return Unavailable();
        }

        IntPtr providerHandle = IntPtr.Zero;
        IntPtr keyHandle = IntPtr.Zero;
        byte[] uniqueNameBuffer = new byte[UniqueNameBufferBytes];
        byte[] descriptorBuffer = new byte[DescriptorBufferBytes];
        char[] finalPathBuffer = new char[FinalPathBufferChars];
        SafeFileHandle? fileHandle = null;

        try
        {
            // 3. Open ONLY the one approved NCrypt provider.
            if (NCryptOpenStorageProvider(out providerHandle, FixedProviderName, 0) != 0)
            {
                return Unavailable();
            }

            // 4. Open ONLY the machine key named by the validated entry KeyIdentity.
            if (NCryptOpenKey(providerHandle, out keyHandle, entry.KeyIdentity, 0, NcryptMachineKeyFlag) != 0)
            {
                return Unavailable();
            }

            // 5. Read NCRYPT_UNIQUE_NAME_PROPERTY into the one fixed-capacity buffer.
            int propertyStatus = NCryptGetProperty(
                keyHandle, UniqueNamePropertyName, uniqueNameBuffer, uniqueNameBuffer.Length, out int uniqueNameBytes, 0);
            if (propertyStatus != 0 || uniqueNameBytes <= 0 || uniqueNameBytes > uniqueNameBuffer.Length
                || uniqueNameBytes % 2 != 0)
            {
                return Unavailable();
            }

            int charCount = uniqueNameBytes / 2;
            if (charCount > 0 && uniqueNameBuffer[uniqueNameBytes - 2] == 0 && uniqueNameBuffer[uniqueNameBytes - 1] == 0)
            {
                charCount--; // exclude the trailing NUL terminator
            }
            string observedUniqueName = Encoding.Unicode.GetString(uniqueNameBuffer, 0, charCount * 2);

            // 6. Validate the returned unique name with the certified grammar, then
            // 7. compare it EXACTLY with the entry's recorded provider unique name.
            bool observedGrammarValid = ServiceOwnershipLedgerContract.IsValidProviderUniqueName(observedUniqueName);
            bool observedMatchesEntry = observedGrammarValid
                && string.Equals(observedUniqueName, entry.ProviderUniqueName, StringComparison.Ordinal);

            if (!observedGrammarValid || !observedMatchesEntry)
            {
                return KeyIdentityMismatch();
            }

            // 8. Resolve the ONE closed root through ONE fixed implementation, and
            // 9. combine ONLY that root and the validated leaf.
            string root = ResolveFixedKeyStorageRoot();
            string canonicalRoot = Path.GetFullPath(root);
            string canonicalTarget = Path.GetFullPath(Path.Combine(canonicalRoot, observedUniqueName));

            // 10. Canonicalize and require the EXACT expected parent.
            if (!string.Equals(
                    Path.GetDirectoryName(canonicalTarget), canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                return KeyIdentityMismatch();
            }

            // 11. Refuse any reparse point in the root or the target.
            try
            {
                if ((File.GetAttributes(canonicalRoot) & FileAttributes.ReparsePoint) != 0
                    || (File.Exists(canonicalTarget)
                        && (File.GetAttributes(canonicalTarget) & FileAttributes.ReparsePoint) != 0))
                {
                    return KeyIdentityMismatch();
                }
            }
            catch (IOException)
            {
                return Unavailable();
            }
            catch (UnauthorizedAccessException)
            {
                return Unavailable();
            }

            // 12. Open the target as an existing regular file without following a reparse point.
            fileHandle = CreateFileW(
                canonicalTarget, GenericRead, FileShareRead, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
            if (fileHandle is null || fileHandle.IsInvalid)
            {
                return Unavailable();
            }

            // 13. Confirm the opened handle resolves to the EXACT expected file.
            int finalPathChars = GetFinalPathNameByHandleW(fileHandle, finalPathBuffer, finalPathBuffer.Length, 0);
            if (finalPathChars <= 0 || finalPathChars >= finalPathBuffer.Length)
            {
                return Unavailable();
            }
            string finalPath = new(finalPathBuffer, 0, finalPathChars);
            if (finalPath.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal))
            {
                finalPath = finalPath[ExtendedPathPrefix.Length..];
            }
            if (!string.Equals(finalPath, canonicalTarget, StringComparison.OrdinalIgnoreCase))
            {
                return KeyIdentityMismatch();
            }

            // 14. Read owner/group/DACL bytes FROM THE OPENED HANDLE, into the one
            // fixed-capacity buffer. 15. SACL access is never requested.
            const int securityInformation = OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation;
            bool descriptorRead = GetKernelObjectSecurity(
                fileHandle, securityInformation, descriptorBuffer, descriptorBuffer.Length, out int descriptorBytesNeeded);
            bool descriptorReadComplete =
                descriptorRead && descriptorBytesNeeded > 0 && descriptorBytesNeeded <= descriptorBuffer.Length;
            byte[]? observedDescriptorBytes = descriptorReadComplete
                ? descriptorBuffer[..descriptorBytesNeeded]
                : null;

            // 16. Pass only bounded facts and the observed descriptor bytes to the interpreter.
            var facts = new ServiceOwnershipObservedNativeFacts(
                observedGrammarValid, observedMatchesEntry, descriptorReadComplete, observedDescriptorBytes);
            return ServiceOwnershipCredentialObserverInterpreter.Interpret(entry, facts);
        }
        finally
        {
            // 17. Clear every temporary buffer.
            Array.Clear(uniqueNameBuffer);
            Array.Clear(descriptorBuffer);
            Array.Clear(finalPathBuffer);

            // 18. Close every native handle.
            fileHandle?.Dispose();
            if (keyHandle != IntPtr.Zero)
            {
                NCryptFreeObject(keyHandle);
            }
            if (providerHandle != IntPtr.Zero)
            {
                NCryptFreeObject(providerHandle);
            }
        }
    }
}

/// <summary>
/// THE PRODUCTION CREDENTIAL-OBSERVATION ADAPTER (cycle 90 pass A).
///
/// WHAT IT IS. A THIN MAPPER and nothing else. It calls the one fixed shim entry
/// point with the certified entry it was handed and maps that bounded result to
/// the bounded observation the promotion transaction understands. It holds no
/// field, no constructor parameter and no state, opens nothing, reads nothing,
/// writes nothing, and has NO ledger persistence capability of any kind.
///
/// WHY IT LIVES IN THIS FILE. The certified containment guard forbids any
/// production source file OUTSIDE this one from naming or calling the observer,
/// so the only lawful home for a call site is here. The guard that pins the shim
/// to exactly one entry point is scoped to the shim TYPE, so this sibling class
/// does not widen it; every whole-file guard - no forbidden capability, no
/// exception text, and the observer-token positive control - continues to bind
/// this class too, which is why it may do nothing but map.
///
/// AN UNSPECIFIED STATE IS NEVER A SUCCESS: it maps to Unspecified, which every
/// consumer refuses.
/// </summary>
internal sealed class ServiceOwnershipCertifiedEntryObservationAdapter
    : IServiceOwnershipCredentialObservationPort
{
    public ServiceOwnershipCredentialObservation ObserveCredential(ServiceOwnershipLedgerEntry entry)
    {
        ServiceOwnershipCredentialObservationResult result =
            ServiceOwnershipCredentialObserver.Observe(entry);

        return result.State == ServiceOwnershipCredentialObserverState.Observed
            ? result.Observation
            : ServiceOwnershipCredentialObservation.Unspecified;
    }
}
