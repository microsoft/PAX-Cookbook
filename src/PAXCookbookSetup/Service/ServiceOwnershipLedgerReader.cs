// PAX Cookbook - FIXED READ-ONLY SERVICE OWNERSHIP-LEDGER READER (cycle 51, Setup only)
//
// WHAT THIS FILE IS. A READ-ONLY reader that reads exactly
// %ProgramData%\PAXCookbook\Service\ownership-ledger.json and nothing else,
// returning exactly one of Absent / Validated / Refused / Unavailable. NO
// PRODUCTION CALLER exists anywhere in this cycle: compile-time existence is
// authorized, reachability is not.
//
// WHAT THIS FILE CANNOT DO, by construction. It never creates, truncates,
// appends, deletes, renames, or replaces the ledger file or its directories;
// never creates the Service directory; never calls
// ServiceOwnershipCredentialObserver or ServiceOwnershipLifecyclePlanner.Plan;
// never mutates a permission, certificate, key, service, or registry entry;
// never touches PAX and never starts a Bake.
//
// HOW IT IS SPLIT (follows the cycle-39 ServiceSidResolver / cycle-49
// ServiceOwnershipCredentialObserver precedent, ruling B1). There is NO
// injectable production seam of any kind, because a seam is itself a
// capability:
//   * ServiceOwnershipLedgerReaderInterpreter - a PURE interpreter over a
//     small set of BOUNDED READ FACTS. Zero I/O, fully unit-testable, and the
//     sole home of every state-mapping decision (Absent / Validated / Refused
//     / Unavailable). It does NOT accept a caller-supplied validation result
//     or an "is valid" boolean - it decides that itself, by calling
//     ServiceOwnershipLedgerValidator.Validate exactly once for a complete
//     decoded document.
//   * ServiceOwnershipLedgerReader.Read() - the FIXED SHIM. ONE entry point,
//     taking NO ARGUMENT: no path, filename, delegate, callback, interface,
//     strategy, options, override, or settable static field anywhere.
//
// THE SHIM'S FIXED SEQUENCE (16 steps, all binding). Reject unsupported
// platforms; resolve CommonApplicationData; combine ONLY the three fixed
// Shared names (ServiceMachineStorageContract); canonicalize root, Service
// dir and ledger path; require the exact expected parent relationships;
// refuse a reparse point at machine root, Service dir AND ledger; distinguish
// confirmed absence (ENOENT-style) from an access failure at each of those
// three locations; open ONLY the existing ledger as a regular file with
// OPEN_EXISTING and FILE_FLAG_OPEN_REPARSE_POINT (no create/truncate/append/
// delete/rename/replace/write mode, and a final reparse point is never
// silently followed); validate the OPENED HANDLE's final path against the
// expected canonical path (handle-based identity - never a path validated
// then reopened through a different path-based API); read from THAT HANDLE
// ONLY via System.IO.RandomAccess; enforce MaxLedgerBytes BEFORE allocating a
// buffer (from the handle's own reported length, never a second sizing call);
// detect a short read and trailing/grown data by re-querying the handle's
// length and attempting one more read past the expected end; pass only
// bounded facts plus the complete bytes to the pure interpreter; clear owned
// buffers and close the handle in a finally block. The Service directory is
// NEVER created.
//
// PRIVACY - FAIL CLOSED. Never logs or emits file content, never enumerates
// siblings or ledger entries, never reveals WHICH entry failed, never
// partially accepts, never classifies a malformed file as absent, never
// repairs a permission, and never retries through an alternate location.
// ToString() on every result carries only the bounded state token.
//
// DISCLOSED LIMITATION. The decoded ledger JSON is briefly held in a managed
// System.String while ServiceOwnershipLedgerValidator.Validate runs. .NET
// strings are immutable and cannot be forcibly zeroed, so - unlike the raw
// byte and char buffers this file DOES own and clear in its finally block -
// that transient string cannot be scrubbed from process memory. This mirrors
// the same limitation already accepted by the certified
// ServiceOwnershipLedgerValidator itself, which also takes a string.
using System;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

/// <summary>
/// Bounded outcome of a fixed ownership-ledger read. Zero is the permanent,
/// safe default: an uninitialised value can never read as success.
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data.
/// </summary>
public enum ServiceOwnershipLedgerReadState
{
    Unspecified = 0,
    Absent = 1,
    Validated = 2,
    Refused = 3,
    Unavailable = 4,
}

/// <summary>
/// The reader's whole observable surface: a bounded state and, for every state
/// except <see cref="ServiceOwnershipLedgerReadState.Unavailable"/>, a bounded
/// <see cref="ServiceOwnershipLedgerValidationResult"/>. Unavailable carries no
/// document. Never a path, byte, SID, certificate reference, exception text,
/// or native status - including through <see cref="ToString"/>, which carries
/// only the bounded state token.
/// </summary>
internal readonly struct ServiceOwnershipLedgerReadResult
{
    private ServiceOwnershipLedgerReadResult(
        ServiceOwnershipLedgerReadState state, ServiceOwnershipLedgerValidationResult? validation)
    {
        State = state;
        Validation = validation;
    }

    internal ServiceOwnershipLedgerReadState State { get; }

    internal ServiceOwnershipLedgerValidationResult? Validation { get; }

    internal static ServiceOwnershipLedgerReadResult Unavailable() =>
        new(ServiceOwnershipLedgerReadState.Unavailable, null);

    /// <summary>Confirmed absence. ALWAYS uses <see cref="ServiceOwnershipLedgerValidator.ForAbsentLedger"/>.</summary>
    internal static ServiceOwnershipLedgerReadResult Absent() =>
        new(ServiceOwnershipLedgerReadState.Absent, ServiceOwnershipLedgerValidator.ForAbsentLedger());

    /// <summary>REQUIRES <paramref name="validation"/>.IsAccepted.</summary>
    internal static ServiceOwnershipLedgerReadResult Validated(ServiceOwnershipLedgerValidationResult validation) =>
        new(ServiceOwnershipLedgerReadState.Validated, validation);

    /// <summary>REQUIRES <paramref name="validation"/>.IsRefused.</summary>
    internal static ServiceOwnershipLedgerReadResult Refused(ServiceOwnershipLedgerValidationResult validation) =>
        new(ServiceOwnershipLedgerReadState.Refused, validation);

    public override string ToString() => State.ToString();
}

/// <summary>
/// BOUNDED READ FACTS the fixed shim gathers from its one documented,
/// read-only native sequence. Never a path, never a delegate, never a
/// caller-supplied validation result or "is valid" boolean. The named factory
/// methods intentionally alias the same underlying shape for every
/// access/stability failure (oversized, short read, growth, shrinkage,
/// trailing data, unstable, unsafe): all of them are, by contract, bounded
/// access failures that must map to Unavailable, and the distinct names exist
/// purely so tests and readers can see which real-world condition each one
/// documents.
/// </summary>
internal readonly struct ServiceOwnershipLedgerReadFacts
{
    private ServiceOwnershipLedgerReadFacts(
        bool platformSupported, bool confirmedAbsent, bool readComplete, byte[]? bytes)
    {
        PlatformSupported = platformSupported;
        ConfirmedAbsent = confirmedAbsent;
        ReadComplete = readComplete;
        Bytes = bytes;
    }

    internal bool PlatformSupported { get; }

    internal bool ConfirmedAbsent { get; }

    internal bool ReadComplete { get; }

    internal byte[]? Bytes { get; }

    internal static ServiceOwnershipLedgerReadFacts PlatformUnsupported() => new(false, false, false, null);

    internal static ServiceOwnershipLedgerReadFacts StructuralFailure() => new(true, false, false, null);

    internal static ServiceOwnershipLedgerReadFacts Oversized() => new(true, false, false, null);

    internal static ServiceOwnershipLedgerReadFacts ShortRead() => new(true, false, false, null);

    internal static ServiceOwnershipLedgerReadFacts Grew() => new(true, false, false, null);

    internal static ServiceOwnershipLedgerReadFacts Shrank() => new(true, false, false, null);

    internal static ServiceOwnershipLedgerReadFacts TrailingData() => new(true, false, false, null);

    internal static ServiceOwnershipLedgerReadFacts Absent() => new(true, true, false, null);

    internal static ServiceOwnershipLedgerReadFacts CompleteRead(byte[] bytes) => new(true, false, true, bytes);
}

/// <summary>
/// PURE interpreter over <see cref="ServiceOwnershipLedgerReadFacts"/>. It
/// performs no I/O of any kind, holds no state, and accepts no delegate, so
/// every decision is reproducible from its argument alone. It calls
/// <see cref="ServiceOwnershipLedgerValidator.Validate"/> EXACTLY ONCE, for a
/// complete decoded document, and never salvages entries from a refused
/// document.
/// </summary>
internal static class ServiceOwnershipLedgerReaderInterpreter
{
    internal static ServiceOwnershipLedgerReadResult Interpret(ServiceOwnershipLedgerReadFacts facts)
    {
        if (!facts.PlatformSupported)
        {
            return ServiceOwnershipLedgerReadResult.Unavailable();
        }

        if (facts.ConfirmedAbsent)
        {
            return ServiceOwnershipLedgerReadResult.Absent();
        }

        if (!facts.ReadComplete || facts.Bytes is null)
        {
            return ServiceOwnershipLedgerReadResult.Unavailable();
        }

        if (HasUtf8Bom(facts.Bytes))
        {
            return ServiceOwnershipLedgerReadResult.Unavailable();
        }

        string? json = TryDecodeStrictUtf8(facts.Bytes);
        if (json is null)
        {
            return ServiceOwnershipLedgerReadResult.Unavailable();
        }

        ServiceOwnershipLedgerValidationResult validation = ServiceOwnershipLedgerValidator.Validate(json);
        return validation.IsAccepted
            ? ServiceOwnershipLedgerReadResult.Validated(validation)
            : ServiceOwnershipLedgerReadResult.Refused(validation);
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    /// <summary>
    /// Strict UTF-8 decode: invalid bytes throw, are CONTAINED here, and never
    /// fall back to a replacement character (U+FFFD).
    /// </summary>
    private static string? TryDecodeStrictUtf8(byte[] bytes)
    {
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// The FIXED SHIM. ONE entry point, taking NO ARGUMENT. Every native P/Invoke
/// below is declared and invoked exactly once (see
/// ServiceOwnershipLedgerReaderStructuralTests, which uses the cycle-50
/// NativeCallGuard).
/// </summary>
internal static class ServiceOwnershipLedgerReader
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FinalPathBufferChars = 1024;
    private const string ExtendedPathPrefix = @"\\?\";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
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

    private enum PathProbeResult
    {
        Safe,
        Absent,
        Refused,
    }

    internal static ServiceOwnershipLedgerReadResult Read()
    {
        // 1. Reject unsupported platforms.
        if (!OperatingSystem.IsWindows())
        {
            return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.PlatformUnsupported());
        }

        string canonicalRoot;
        string canonicalServiceDir;
        string canonicalLedger;
        try
        {
            // 2. Resolve CommonApplicationData.
            string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.StructuralFailure());
            }

            // 3. Combine ONLY the three fixed Shared names.
            string root = Path.Combine(baseDirectory, ServiceMachineStorageContract.MachineRootFolderName);
            string serviceDir = Path.Combine(root, ServiceMachineStorageContract.ServiceDataFolderName);
            string ledger = Path.Combine(serviceDir, ServiceMachineStorageContract.OwnershipLedgerFileName);

            // 4. Canonicalize root, Service dir and ledger path.
            canonicalRoot = Path.GetFullPath(root);
            canonicalServiceDir = Path.GetFullPath(serviceDir);
            canonicalLedger = Path.GetFullPath(ledger);
        }
        catch (Exception)
        {
            return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.StructuralFailure());
        }

        // 5. Require the exact expected parent relationships.
        if (!string.Equals(Path.GetDirectoryName(canonicalServiceDir), canonicalRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(canonicalLedger), canonicalServiceDir, StringComparison.OrdinalIgnoreCase))
        {
            return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.StructuralFailure());
        }

        // 6 + 7. Refuse a reparse point at each location; distinguish confirmed
        // absence from an access failure, at the root, the Service dir, and the
        // ledger itself, in that order (the Service directory is NEVER created).
        PathProbeResult rootProbe = ProbePath(canonicalRoot, mustBeDirectory: true);
        if (rootProbe == PathProbeResult.Absent)
        {
            return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.Absent());
        }
        if (rootProbe != PathProbeResult.Safe)
        {
            return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.StructuralFailure());
        }

        PathProbeResult serviceDirProbe = ProbePath(canonicalServiceDir, mustBeDirectory: true);
        if (serviceDirProbe == PathProbeResult.Absent)
        {
            return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.Absent());
        }
        if (serviceDirProbe != PathProbeResult.Safe)
        {
            return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.StructuralFailure());
        }

        PathProbeResult ledgerProbe = ProbePath(canonicalLedger, mustBeDirectory: false);
        if (ledgerProbe == PathProbeResult.Absent)
        {
            return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.Absent());
        }
        if (ledgerProbe != PathProbeResult.Safe)
        {
            return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.StructuralFailure());
        }

        SafeFileHandle? handle = null;
        char[] finalPathBuffer = new char[FinalPathBufferChars];
        byte[]? buffer = null;
        try
        {
            // 8, 9, 10. Open ONLY the existing ledger as a regular file: no
            // create/truncate/append/delete/rename/replace/write mode, and a
            // final reparse point is never silently followed.
            handle = CreateFileW(
                canonicalLedger, GenericRead, FileShareRead, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle is null || handle.IsInvalid)
            {
                return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.StructuralFailure());
            }

            // 11. Validate the OPENED HANDLE's final path against the expected
            // canonical path - never a path validated then reopened elsewhere.
            int finalPathChars = GetFinalPathNameByHandleW(handle, finalPathBuffer, finalPathBuffer.Length, 0);
            if (finalPathChars <= 0 || finalPathChars >= finalPathBuffer.Length)
            {
                return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.StructuralFailure());
            }

            string finalPath = new(finalPathBuffer, 0, finalPathChars);
            if (finalPath.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal))
            {
                finalPath = finalPath[ExtendedPathPrefix.Length..];
            }
            if (!string.Equals(finalPath, canonicalLedger, StringComparison.OrdinalIgnoreCase))
            {
                return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.StructuralFailure());
            }

            // 12, 13. Read from THAT HANDLE ONLY; enforce MaxLedgerBytes BEFORE
            // allocating a buffer, from the handle's own reported length.
            long initialLength;
            try
            {
                initialLength = RandomAccess.GetLength(handle);
            }
            catch (Exception)
            {
                return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.StructuralFailure());
            }

            if (initialLength < 0 || initialLength > ServiceOwnershipLedgerContract.MaxLedgerBytes)
            {
                return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.Oversized());
            }

            int expectedLength = (int)initialLength;
            buffer = new byte[expectedLength];
            int totalRead = 0;
            try
            {
                while (totalRead < expectedLength)
                {
                    int read = RandomAccess.Read(handle, buffer.AsSpan(totalRead), totalRead);
                    if (read <= 0)
                    {
                        break;
                    }
                    totalRead += read;
                }
            }
            catch (Exception)
            {
                return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.StructuralFailure());
            }

            if (totalRead != expectedLength)
            {
                return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.ShortRead());
            }

            // 14. Detect growth/trailing data (one more read past the known
            // end must report zero bytes) and shrinkage (re-query the length).
            try
            {
                Span<byte> probe = stackalloc byte[1];
                int extra = RandomAccess.Read(handle, probe, expectedLength);
                if (extra > 0)
                {
                    return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.TrailingData());
                }

                long finalLength = RandomAccess.GetLength(handle);
                if (finalLength > initialLength)
                {
                    return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.Grew());
                }
                if (finalLength < initialLength)
                {
                    return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.Shrank());
                }
            }
            catch (Exception)
            {
                return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.StructuralFailure());
            }

            // 15. Pass only bounded facts plus the complete bytes to the interpreter.
            return ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.CompleteRead(buffer));
        }
        finally
        {
            // 16. Clear owned buffers and close the handle.
            if (buffer is not null)
            {
                Array.Clear(buffer);
            }
            Array.Clear(finalPathBuffer);
            handle?.Dispose();
        }
    }

    private static PathProbeResult ProbePath(string path, bool mustBeDirectory)
    {
        try
        {
            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                return PathProbeResult.Refused;
            }

            bool isDirectory = (attrs & FileAttributes.Directory) != 0;
            return isDirectory == mustBeDirectory ? PathProbeResult.Safe : PathProbeResult.Refused;
        }
        catch (FileNotFoundException)
        {
            return PathProbeResult.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return PathProbeResult.Absent;
        }
        catch (IOException)
        {
            return PathProbeResult.Refused;
        }
        catch (UnauthorizedAccessException)
        {
            return PathProbeResult.Refused;
        }
    }
}
