using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

// ---------------------------------------------------------------------------
// DURABLE OWNERSHIP-LEDGER WRITER - cycle 88. NO PRODUCTION CALLER.
//
// WHAT THIS IS. The FIRST and ONLY durable write surface for
// %ProgramData%\PAXCookbook\Service\ownership-ledger.json. Cycle 82 delivered a
// pure serializer that returns bytes and touches no file; cycle 88's transition
// authority decides what the next accepted state IS; this is the surface that
// puts those bytes on disk, and nothing else in the product may.
//
// NO PATH ARGUMENT, EVER. The production verb Write() takes a serialization
// result, an expected accepted outcome and an expected generation - and NOTHING
// of type string. The destination is composed INTERNALLY from the three fixed
// names in ServiceMachineStorageContract, which is the same single name
// authority every other surface derives this location from, so two spellings of
// the ledger path can never drift apart.
//
// THE ONE SEAM, AND WHY IT IS NAMED. WriteWithinContainmentRoot() exists so this
// cycle can be exercised end to end in an OS-temp sandbox without touching
// %ProgramData%, exactly as the promoted-Recipe store's containment root does.
// It is internal, it is named so its purpose cannot be mistaken, and a
// structural test asserts it is the ONLY method on this type carrying a
// containment root. The structure BENEATH the root is not negotiable and is
// never caller-supplied.
//
// THE DIRECTORY IS NEVER CREATED. This surface refuses when the fixed parent
// does not already exist. A writer that creates machine directories is a writer
// that can establish a namespace it was never granted.
//
// FAIL CLOSED, AND NO PARTIAL SUCCESS. The bytes are re-validated BEFORE any
// disk contact and must report the EXPECTED accepted outcome and the EXPECTED
// generation. A reparse point anywhere on the derived chain refuses. The write
// goes to a SAME-DIRECTORY uniquely named staging file, is flushed to disk, and
// is moved into place atomically. The prior bytes are held in memory until the
// replacement has been reread from disk and verified; if verification fails the
// prior state is put back - or the new file is removed when there was no prior
// state - and the outcome says so. When the rollback itself cannot be PROVEN,
// the outcome is RecoveryRequired, which is the one value meaning a human must
// look at the machine. Nothing here reports success on a document it has not
// reread from disk and re-validated.
//
// DISCLOSED VERIFICATION GAP, STATED WITHOUT EUPHEMISM. The post-write
// verification reaches ServiceOwnershipLedgerValidator directly, behind the same
// byte-order-mark refusal and the same strict UTF-8 decode the product's fixed
// read path applies. It does NOT go through that read path's own native shim, so
// the shim's final-path identity check and short-read/growth detection are NOT
// exercised by this verification. That containment is required by a certified
// single-file invariant this cycle does not amend, and the gap is real rather
// than papered over.
//
// PRIVACY. No path, byte, SID, thumbprint, exception text or native status ever
// escapes through a result or a ToString() - only the bounded outcome token.
// ---------------------------------------------------------------------------

/// <summary>
/// Bounded durable-write outcome. Zero always refuses, so an uninitialised value
/// can never read as a completed write.
/// </summary>
internal enum ServiceOwnershipLedgerWriteOutcome
{
    Unspecified = 0,

    /// <summary>The document is on disk, reread from disk, and verified.</summary>
    Written = 1,

    /// <summary>The source was null, refused, or carried no bytes.</summary>
    SourceNotSerialized = 2,

    /// <summary>The bytes do not re-validate to the outcome the caller declared.</summary>
    ExpectedOutcomeMismatch = 3,

    /// <summary>The bytes do not carry the generation the caller declared.</summary>
    ExpectedGenerationMismatch = 4,

    /// <summary>The platform is not supported.</summary>
    PlatformUnsupported = 5,

    /// <summary>The fixed path could not be composed or does not have the expected shape.</summary>
    StructuralPathFailure = 6,

    /// <summary>A reparse point stands on the derived chain, so the write may escape.</summary>
    ReparsePointRefused = 7,

    /// <summary>The fixed parent directory does not exist. It is never created here.</summary>
    ParentDirectoryUnavailable = 8,

    /// <summary>The staging file could not be created, written or flushed.</summary>
    StagingFailed = 9,

    /// <summary>The atomic replacement failed. The prior document is untouched.</summary>
    ReplaceFailed = 10,

    /// <summary>The replacement could not be reread from disk.</summary>
    RereadFailed = 11,

    /// <summary>The reread document was refused by the ledger validator.</summary>
    RereadRefused = 12,

    /// <summary>The reread document validated to a DIFFERENT outcome.</summary>
    RereadOutcomeMismatch = 13,

    /// <summary>The reread document carried a DIFFERENT generation.</summary>
    RereadGenerationMismatch = 14,

    /// <summary>The reread bytes are not the bytes that were written.</summary>
    RereadBytesMismatch = 15,

    /// <summary>
    /// Verification failed AND the prior state could not be proven restored. This
    /// is the one outcome that means a human must look at the machine.
    /// </summary>
    RecoveryRequired = 16,
}

/// <summary>
/// Bounded durable-write result. It carries NO PATH on purpose: a caller that
/// never learns where the document went cannot ask for a different one.
/// </summary>
internal sealed class ServiceOwnershipLedgerWriteResult
{
    private ServiceOwnershipLedgerWriteResult(
        ServiceOwnershipLedgerWriteOutcome outcome, bool priorStateRestored)
    {
        Outcome = outcome;
        PriorStateRestored = priorStateRestored;
    }

    internal ServiceOwnershipLedgerWriteOutcome Outcome { get; }

    /// <summary>
    /// True only when this call reached disk, failed verification, and the prior
    /// state was PROVEN put back. False for every refusal that never reached disk,
    /// and false for RecoveryRequired.
    /// </summary>
    internal bool PriorStateRestored { get; }

    internal bool IsWritten => Outcome == ServiceOwnershipLedgerWriteOutcome.Written;

    internal static ServiceOwnershipLedgerWriteResult Refused(
        ServiceOwnershipLedgerWriteOutcome outcome) => new(outcome, false);

    internal static ServiceOwnershipLedgerWriteResult RolledBack(
        ServiceOwnershipLedgerWriteOutcome outcome) => new(outcome, true);

    internal static ServiceOwnershipLedgerWriteResult Written() =>
        new(ServiceOwnershipLedgerWriteOutcome.Written, false);

    /// <summary>Carries only the bounded outcome token.</summary>
    public override string ToString() => Outcome.ToString();
}

/// <summary>
/// The fixed durable ledger write surface. One production verb taking no path,
/// one named containment seam for sandboxed tests, and no other entry point.
/// </summary>
internal static class ServiceOwnershipLedgerWriter
{
    private const string StagingExtension = ".tmp";
    private const string BackupExtension = ".bak";

    /// <summary>
    /// PRODUCTION. Writes an already-serialized, already-accepted schema-v3
    /// document to the ONE fixed machine location. There is no path, filename,
    /// root, delegate, option or override parameter, and there is no overload
    /// that accepts one.
    /// </summary>
    internal static ServiceOwnershipLedgerWriteResult Write(
        ServiceOwnershipLedgerSerializationResult? serialized,
        ServiceOwnershipLedgerOutcome expectedOutcome,
        int expectedGeneration)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.PlatformUnsupported);
        }

        string machineRoot;
        try
        {
            machineRoot = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        }
        catch (Exception)
        {
            return ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.StructuralPathFailure);
        }

        if (string.IsNullOrWhiteSpace(machineRoot))
        {
            return ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.StructuralPathFailure);
        }

        return WriteCore(machineRoot, serialized, expectedOutcome, expectedGeneration);
    }

    /// <summary>
    /// TEST SEAM, supplied only by tests. The containment root stands in for
    /// CommonApplicationData so this surface can be exercised without touching
    /// %ProgramData%. The fixed structure BENEATH it is identical to production
    /// and is never caller-supplied. It performs EXACTLY the same steps the
    /// production verb performs; only the base directory differs.
    /// </summary>
    internal static ServiceOwnershipLedgerWriteResult WriteWithinContainmentRoot(
        string containmentRoot,
        ServiceOwnershipLedgerSerializationResult? serialized,
        ServiceOwnershipLedgerOutcome expectedOutcome,
        int expectedGeneration) =>
        WriteCore(containmentRoot ?? string.Empty, serialized, expectedOutcome, expectedGeneration);

    // -----------------------------------------------------------------------
    // THE ONE CORE
    // -----------------------------------------------------------------------

    private static ServiceOwnershipLedgerWriteResult WriteCore(
        string baseDirectory,
        ServiceOwnershipLedgerSerializationResult? serialized,
        ServiceOwnershipLedgerOutcome expectedOutcome,
        int expectedGeneration)
    {
        // 1. NOTHING UNSERIALIZED IS EVER WRITTEN.
        if (serialized is null || !serialized.IsSerialized)
        {
            return ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.SourceNotSerialized);
        }

        byte[] payload = serialized.Utf8Bytes;
        if (payload.Length == 0 || payload.Length > ServiceOwnershipLedgerContract.MaxLedgerBytes)
        {
            return ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.SourceNotSerialized);
        }

        // 2. THE EXPECTATION IS CHECKED BEFORE ANY DISK CONTACT. A caller that is
        //    wrong about what it is writing never reaches the filesystem.
        ServiceOwnershipLedgerWriteOutcome expectation =
            CheckAgainstExpectation(payload, expectedOutcome, expectedGeneration);
        if (expectation != ServiceOwnershipLedgerWriteOutcome.Written)
        {
            return ServiceOwnershipLedgerWriteResult.Refused(expectation);
        }

        // 3. THE ONE DERIVATION. Every name comes from ServiceMachineStorageContract.
        string serviceDirectory;
        string ledgerPath;
        try
        {
            string root = Path.Combine(
                baseDirectory, ServiceMachineStorageContract.MachineRootFolderName);
            serviceDirectory = Path.GetFullPath(
                Path.Combine(root, ServiceMachineStorageContract.ServiceDataFolderName));
            ledgerPath = Path.GetFullPath(
                Path.Combine(serviceDirectory, ServiceMachineStorageContract.OwnershipLedgerFileName));

            if (!string.Equals(
                    Path.GetDirectoryName(ledgerPath), serviceDirectory, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetDirectoryName(serviceDirectory), Path.GetFullPath(root),
                    StringComparison.OrdinalIgnoreCase))
            {
                return ServiceOwnershipLedgerWriteResult.Refused(
                    ServiceOwnershipLedgerWriteOutcome.StructuralPathFailure);
            }
        }
        catch (Exception)
        {
            return ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.StructuralPathFailure);
        }

        // 4. THE PARENT IS NEVER CREATED.
        try
        {
            if (!Directory.Exists(serviceDirectory))
            {
                return ServiceOwnershipLedgerWriteResult.Refused(
                    ServiceOwnershipLedgerWriteOutcome.ParentDirectoryUnavailable);
            }
        }
        catch (Exception)
        {
            return ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.ParentDirectoryUnavailable);
        }

        // 5. A JUNCTION OR SYMLINK ANYWHERE ON THE CHAIN REFUSES.
        if (ChainHasReparsePoint(baseDirectory, serviceDirectory, ledgerPath))
        {
            return ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.ReparsePointRefused);
        }

        // 6. THE PRIOR BYTES ARE HELD UNTIL THE REPLACEMENT IS VERIFIED.
        byte[]? priorBytes;
        bool priorExists;
        try
        {
            priorExists = File.Exists(ledgerPath);
            priorBytes = priorExists ? File.ReadAllBytes(ledgerPath) : null;
        }
        catch (Exception)
        {
            return ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.StructuralPathFailure);
        }

        // 7. SAME-DIRECTORY UNIQUE STAGING. A cross-directory write could not be
        //    renamed atomically, and a partially written ledger is exactly the
        //    state this surface exists to make impossible.
        string staging = Path.Combine(
            serviceDirectory,
            ServiceMachineStorageContract.OwnershipLedgerFileName
            + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + StagingExtension);
        string backup = staging + BackupExtension;

        try
        {
            using var stream = new FileStream(
                staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(payload, 0, payload.Length);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception)
        {
            TryDelete(staging);
            return ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.StagingFailed);
        }

        // 8. ATOMIC REPLACEMENT.
        try
        {
            if (priorExists)
            {
                File.Replace(staging, ledgerPath, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(staging, ledgerPath);
            }
        }
        catch (Exception)
        {
            TryDelete(staging);
            TryDelete(backup);
            // The prior document was never removed, so nothing needs restoring.
            return ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.ReplaceFailed);
        }

        TryDelete(backup);

        // 9. IMMEDIATE REREAD FROM DISK, re-validated through the ledger validator.
        ServiceOwnershipLedgerWriteOutcome verification =
            VerifyOnDisk(ledgerPath, payload, expectedOutcome, expectedGeneration);
        if (verification == ServiceOwnershipLedgerWriteOutcome.Written)
        {
            return ServiceOwnershipLedgerWriteResult.Written();
        }

        // 10. NO PARTIAL SUCCESS. The transaction did not complete, so its artifact
        //     does not survive: the prior state goes back, or the new file goes
        //     away when there was no prior state.
        return Rollback(ledgerPath, priorBytes, verification);
    }

    private static ServiceOwnershipLedgerWriteOutcome CheckAgainstExpectation(
        byte[] payload, ServiceOwnershipLedgerOutcome expectedOutcome, int expectedGeneration)
    {
        ServiceOwnershipLedgerValidationResult? validation = InterpretBytes(payload);

        if (validation is null || validation.Document is null)
        {
            return ServiceOwnershipLedgerWriteOutcome.SourceNotSerialized;
        }
        if (validation.Outcome != expectedOutcome)
        {
            return ServiceOwnershipLedgerWriteOutcome.ExpectedOutcomeMismatch;
        }
        if (validation.Document.Generation != expectedGeneration)
        {
            return ServiceOwnershipLedgerWriteOutcome.ExpectedGenerationMismatch;
        }

        return ServiceOwnershipLedgerWriteOutcome.Written;
    }

    /// <summary>
    /// Byte-order-mark refusal, strict UTF-8 decode, then the ledger validator.
    /// Returns null when the bytes are not an ACCEPTED ledger document. See the
    /// disclosed verification gap in this file's header.
    /// </summary>
    private static ServiceOwnershipLedgerValidationResult? InterpretBytes(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return null;
        }

        string json;
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            json = strict.GetString(bytes);
        }
        catch (Exception)
        {
            return null;
        }

        ServiceOwnershipLedgerValidationResult validation =
            ServiceOwnershipLedgerValidator.Validate(json);
        return validation.IsAccepted ? validation : null;
    }

    private static ServiceOwnershipLedgerWriteOutcome VerifyOnDisk(
        string ledgerPath,
        byte[] payload,
        ServiceOwnershipLedgerOutcome expectedOutcome,
        int expectedGeneration)
    {
        byte[] reread;
        try
        {
            reread = File.ReadAllBytes(ledgerPath);
        }
        catch (Exception)
        {
            return ServiceOwnershipLedgerWriteOutcome.RereadFailed;
        }

        if (reread.Length != payload.Length
            || !CryptographicOperations.FixedTimeEquals(reread, payload))
        {
            return ServiceOwnershipLedgerWriteOutcome.RereadBytesMismatch;
        }

        ServiceOwnershipLedgerValidationResult? validation = InterpretBytes(reread);

        if (validation is null || validation.Document is null)
        {
            return ServiceOwnershipLedgerWriteOutcome.RereadRefused;
        }
        if (validation.Outcome != expectedOutcome)
        {
            return ServiceOwnershipLedgerWriteOutcome.RereadOutcomeMismatch;
        }
        if (validation.Document.Generation != expectedGeneration)
        {
            return ServiceOwnershipLedgerWriteOutcome.RereadGenerationMismatch;
        }

        return ServiceOwnershipLedgerWriteOutcome.Written;
    }

    private static ServiceOwnershipLedgerWriteResult Rollback(
        string ledgerPath, byte[]? priorBytes, ServiceOwnershipLedgerWriteOutcome failure)
    {
        try
        {
            if (priorBytes is null)
            {
                File.Delete(ledgerPath);
                return File.Exists(ledgerPath)
                    ? ServiceOwnershipLedgerWriteResult.Refused(
                        ServiceOwnershipLedgerWriteOutcome.RecoveryRequired)
                    : ServiceOwnershipLedgerWriteResult.RolledBack(failure);
            }

            using (var stream = new FileStream(
                ledgerPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(priorBytes, 0, priorBytes.Length);
                stream.Flush(flushToDisk: true);
            }

            byte[] restored = File.ReadAllBytes(ledgerPath);
            return restored.Length == priorBytes.Length
                   && CryptographicOperations.FixedTimeEquals(restored, priorBytes)
                ? ServiceOwnershipLedgerWriteResult.RolledBack(failure)
                : ServiceOwnershipLedgerWriteResult.Refused(
                    ServiceOwnershipLedgerWriteOutcome.RecoveryRequired);
        }
        catch (Exception)
        {
            return ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.RecoveryRequired);
        }
    }

    /// <summary>
    /// Walks the derived chain from the ledger file upward to the base directory
    /// and refuses if ANY level is a reparse point, so a junction or symlink
    /// cannot redirect the write outside the fixed location.
    /// </summary>
    private static bool ChainHasReparsePoint(
        string baseDirectory, string serviceDirectory, string ledgerPath)
    {
        try
        {
            var ledgerInfo = new FileInfo(ledgerPath);
            if (ledgerInfo.Exists
                && (ledgerInfo.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                return true;
            }

            string stop = Path.GetFullPath(baseDirectory);
            DirectoryInfo? current = new(serviceDirectory);
            while (current is not null)
            {
                if (current.Exists
                    && (current.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    return true;
                }
                if (string.Equals(current.FullName, stop, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                current = current.Parent;
            }
        }
        catch (Exception)
        {
            return true;
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort. A stranded staging file carries the fixed ledger name
            // plus a unique suffix, so it is never the ledger itself.
        }
    }
}

/// <summary>
/// THE PRODUCTION LEDGER-PERSISTENCE ADAPTER (cycle 92 pass B).
///
/// WHAT IT IS. A THIN MAPPER onto the ONE fixed durable write surface. It accepts
/// an already-serialized, already-accepted document plus the outcome and
/// generation the caller declares, and maps the bounded write outcome onto the
/// bounded persistence state the promotion transaction understands. It holds no
/// field, no constructor parameter and no state, composes no path, and has no
/// filesystem override of any kind.
///
/// WHY IT LIVES IN THIS FILE. A certified containment guard forbids any
/// production source file OUTSIDE this one from naming the durable write
/// surface, so this is the only lawful home for a production call site. Every
/// whole-file guard on this file - the reader-token ban, the storage-name
/// authority rule and the forbidden-capability scan - therefore binds this class
/// too, which is why it may do nothing but map.
///
/// AN UNSPECIFIED OUTCOME IS NEVER A SUCCESS: anything that is not a verified
/// write maps to a refusal, and only a rollback that was PROVEN maps to
/// RolledBack.
/// </summary>
internal sealed class ServiceOwnershipFixedLedgerPersistencePort : IServiceOwnershipLedgerPersistencePort
{
    public ServiceOwnershipLedgerPersistState Persist(
        ServiceOwnershipLedgerSerializationResult serialized,
        ServiceOwnershipLedgerOutcome expectedOutcome,
        int expectedGeneration) =>
        MapWriteResult(ServiceOwnershipLedgerWriter.Write(serialized, expectedOutcome, expectedGeneration));

    /// <summary>
    /// PURE mapping from the bounded durable-write result to the bounded
    /// persistence state. Separated so every branch is exhaustively testable
    /// without touching a filesystem.
    ///
    /// The unprovable-rollback case is checked FIRST and on its own, because it is
    /// the only value that means a human must look at the machine: collapsing it
    /// into an ordinary refusal would lose exactly the state that matters.
    /// </summary>
    internal static ServiceOwnershipLedgerPersistState MapWriteResult(
        ServiceOwnershipLedgerWriteResult? result)
    {
        if (result is null)
        {
            return ServiceOwnershipLedgerPersistState.Refused;
        }

        if (result.Outcome == ServiceOwnershipLedgerWriteOutcome.RecoveryRequired)
        {
            return ServiceOwnershipLedgerPersistState.RecoveryRequired;
        }

        if (result.IsWritten)
        {
            return ServiceOwnershipLedgerPersistState.Persisted;
        }

        return result.PriorStateRestored
            ? ServiceOwnershipLedgerPersistState.RolledBack
            : ServiceOwnershipLedgerPersistState.Refused;
    }
}
