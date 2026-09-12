// PAX Cookbook - INSTALLATION-ANCHOR STORE (cycle 58, Setup only)
//
// WHAT THIS FILE IS. The ONE owner of installation-anchor.json I/O. It reads
// %ProgramData%\PAXCookbook\Service\installation-anchor.json, and it writes
// that file EXACTLY ONCE per installation: a first write creates it, a
// repeated write with the SAME immutable identity is accepted as a no-op, and
// a write with DIFFERENT identity data is REFUSED. An existing file that does
// not validate is refused outright and never overwritten, because a partially
// written or foreign anchor is evidence of a state this cycle is not
// authorized to repair.
//
// WHAT THIS FILE CANNOT DO, by construction. It never reads or writes the
// ownership ledger; never opens a certificate store or private key; never
// reads or writes an ACL, the registry or a credential vault; never creates,
// changes, starts or stops a service; never elevates; never opens a socket;
// never starts a process; never touches PAX and never starts a Bake. It
// touches exactly two directories and two file names (the anchor and its own
// same-directory temporary), and nothing else.
//
// NO PRODUCTION CALLER exists for the fixed entry points in this cycle:
// compile-time existence is authorized, reachability is not. The identity
// channel in this same cycle calls the store, and the channel itself has no
// production caller either.
//
// HOW IT IS SPLIT (follows the cycle-39 / cycle-49 / cycle-51 precedent).
//   * ServiceInstallationAnchorStoreInterpreter - a PURE decision function
//     over an already-read anchor state and a proposed document. Zero I/O,
//     fully unit-testable, and the sole home of the create / idempotent /
//     conflict / refuse decision. It never decides from a caller-supplied
//     "is valid" boolean: it is handed the validator's own result.
//   * ServiceInstallationAnchorStore.Read() / .Write(document) - the FIXED
//     entry points. NO path, filename, delegate, callback, interface, options
//     object, override or settable static field: a production caller can never
//     redirect what the store reads or writes.
//
// THE DISCLOSED TEST SEAM. ReadFrom(serviceDirectory) and
// WriteTo(serviceDirectory, document) are INTERNAL and take a directory. They
// exist so this cycle's focused tests can exercise the real write/reread path
// against an OS temp directory instead of the real machine location, which
// requires elevation and would mutate a shared machine. This is a DELIBERATE,
// DISCLOSED deviation from the cycle-51 "no seam at all" rule, authorized by
// this cycle's prompt. It is bounded three ways: the seam is internal (visible
// only to PAXCookbookSetup.Tests via InternalsVisibleTo), the FIXED entry
// points take no argument and derive the one real directory themselves, and
// the seam accepts a DIRECTORY only - the anchor and temporary leaf names are
// always taken from ServiceMachineStorageContract and can never be supplied.
//
// PRIVACY - FAIL CLOSED. No result carries a path, a SID, an installation id,
// file content, an exception, a native status or an account name. ToString()
// on every result carries only the bounded state token. No catch clause in
// this file binds an exception variable, so there is no value from which a
// message could ever be read.
using System;
using System.IO;
using System.Text;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

/// <summary>
/// Bounded outcome of an anchor read. Zero is the permanent, safe default: an
/// uninitialised value can never read as a validated anchor.
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data.
/// </summary>
public enum ServiceInstallationAnchorReadState
{
    Unspecified = 0,
    Absent = 1,
    Validated = 2,
    Refused = 3,
    Unavailable = 4,
}

/// <summary>
/// Bounded outcome of an anchor write. Zero is the permanent, safe default.
/// Exactly two members are success: <see cref="Created"/> (the anchor did not
/// exist and now does) and <see cref="AlreadyMatching"/> (the anchor already
/// existed with the SAME immutable identity, so nothing was written).
///
/// PUBLIC only because the bounded states are named directly in test theory
/// signatures; it carries no capability and no data.
/// </summary>
public enum ServiceInstallationAnchorWriteState
{
    Unspecified = 0,
    Created = 1,
    AlreadyMatching = 2,

    /// <summary>A valid anchor exists whose installation id or initiating-user SID differs.</summary>
    ConflictingIdentity = 3,

    /// <summary>A file exists at the anchor path and does NOT validate. Never overwritten.</summary>
    ExistingStateRefused = 4,

    /// <summary>The proposed document does not satisfy the anchor contract.</summary>
    InvalidProposal = 5,

    /// <summary>Unsupported platform, or a bounded access/stability failure.</summary>
    Unavailable = 6,

    /// <summary>
    /// The bytes were replaced but the immediate reread did not reproduce the
    /// exact accepted anchor. Distinct from <see cref="Unavailable"/> because it
    /// says the durable state is UNKNOWN rather than untouched.
    /// </summary>
    VerificationFailed = 7,

    /// <summary>
    /// CYCLE 67R. The write reached the one <c>Directory.CreateDirectory</c>
    /// call in <c>WriteTo</c> and then failed at or after it. The fixed machine
    /// root and service directories MAY now exist where they did not before,
    /// with INHERITED access control and no anchor inside them.
    ///
    /// IT IS SPLIT OUT OF <see cref="Unavailable"/> DELIBERATELY. Unavailable is
    /// a pre-create refusal and is reported outward as "nothing was mutated";
    /// saying that here would be provably false, and an operator who believed it
    /// would retry against residue that the next attempt can no longer claim it
    /// created and therefore can no longer remove.
    /// </summary>
    PersistenceFailedAfterCreate = 8,
}

/// <summary>
/// CYCLE 62. Bounded outcome of the ONE narrow same-transaction anchor removal
/// primitive. Zero is the permanent, safe default so an uninitialised value can
/// never read as a proven removal.
/// </summary>
public enum ServiceInstallationAnchorRemovalState
{
    Unspecified = 0,

    /// <summary>The anchor was removed AND an immediate reread confirmed its absence.</summary>
    Removed = 1,

    /// <summary>The on-disk anchor does not carry the exact expected immutable identity. Untouched.</summary>
    IdentityMismatch = 2,

    /// <summary>Nothing exists at the anchor path. Nothing was done.</summary>
    AlreadyAbsent = 3,

    /// <summary>The removal was attempted and the reread did NOT confirm absence.</summary>
    VerificationFailed = 4,

    /// <summary>Unsupported platform, or a bounded access/stability failure. Untouched.</summary>
    Unavailable = 5,
}

/// <summary>
/// The read result's whole observable surface: a bounded state and, for every
/// state except <see cref="ServiceInstallationAnchorReadState.Unavailable"/>, a
/// bounded validation result. Never a path, byte, SID, installation id or
/// exception text - including through <see cref="ToString"/>.
/// </summary>
internal readonly struct ServiceInstallationAnchorReadResult
{
    private ServiceInstallationAnchorReadResult(
        ServiceInstallationAnchorReadState state, ServiceInstallationAnchorValidationResult? validation)
    {
        State = state;
        Validation = validation;
    }

    internal ServiceInstallationAnchorReadState State { get; }

    internal ServiceInstallationAnchorValidationResult? Validation { get; }

    internal static ServiceInstallationAnchorReadResult Unavailable() =>
        new(ServiceInstallationAnchorReadState.Unavailable, null);

    /// <summary>Confirmed absence. ALWAYS uses <see cref="ServiceInstallationAnchorValidator.ForAbsentAnchor"/>.</summary>
    internal static ServiceInstallationAnchorReadResult Absent() =>
        new(ServiceInstallationAnchorReadState.Absent, ServiceInstallationAnchorValidator.ForAbsentAnchor());

    internal static ServiceInstallationAnchorReadResult Validated(
        ServiceInstallationAnchorValidationResult validation) =>
        new(ServiceInstallationAnchorReadState.Validated, validation);

    internal static ServiceInstallationAnchorReadResult Refused(
        ServiceInstallationAnchorValidationResult validation) =>
        new(ServiceInstallationAnchorReadState.Refused, validation);

    public override string ToString() => State.ToString();
}

/// <summary>
/// PURE decision function over an already-observed anchor state and a proposed
/// document. It performs no I/O of any kind, holds no state and accepts no
/// delegate, so every decision is reproducible from its arguments alone.
/// </summary>
internal static class ServiceInstallationAnchorStoreInterpreter
{
    /// <summary>
    /// The bounded decision a write must take BEFORE any byte is written.
    /// <c>ProceedToWrite</c> is the ONLY value that authorizes touching disk.
    /// </summary>
    internal enum WriteDecision
    {
        Unspecified = 0,
        ProceedToWrite = 1,
        AlreadyMatching = 2,
        ConflictingIdentity = 3,
        ExistingStateRefused = 4,
        InvalidProposal = 5,
        Unavailable = 6,
    }

    internal static WriteDecision Decide(
        ServiceInstallationAnchorReadResult existing,
        ServiceInstallationAnchorDocument? proposed)
    {
        // The proposal is checked FIRST and on its own terms, so an invalid
        // proposal can never be excused by a convenient on-disk state.
        if (!ServiceInstallationAnchorContract.TrySerialize(proposed, out _))
        {
            return WriteDecision.InvalidProposal;
        }

        switch (existing.State)
        {
            case ServiceInstallationAnchorReadState.Absent:
                return WriteDecision.ProceedToWrite;

            case ServiceInstallationAnchorReadState.Validated:
                // A validated read always carries its document; a missing one is
                // a contradiction and fails closed rather than being written over.
                ServiceInstallationAnchorDocument? current = existing.Validation?.Document;
                if (current is null)
                {
                    return WriteDecision.Unavailable;
                }
                return ServiceInstallationAnchorContract.HasIdenticalImmutableIdentity(current, proposed)
                    ? WriteDecision.AlreadyMatching
                    : WriteDecision.ConflictingIdentity;

            case ServiceInstallationAnchorReadState.Refused:
                return WriteDecision.ExistingStateRefused;

            default:
                // Unavailable and the impossible Unspecified both refuse.
                return WriteDecision.Unavailable;
        }
    }

    /// <summary>
    /// Maps the pre-write decision to its terminal write state. Only
    /// <c>ProceedToWrite</c> has no mapping here, because its outcome is
    /// decided by the write itself.
    /// </summary>
    internal static ServiceInstallationAnchorWriteState ToTerminalState(WriteDecision decision) => decision switch
    {
        WriteDecision.AlreadyMatching => ServiceInstallationAnchorWriteState.AlreadyMatching,
        WriteDecision.ConflictingIdentity => ServiceInstallationAnchorWriteState.ConflictingIdentity,
        WriteDecision.ExistingStateRefused => ServiceInstallationAnchorWriteState.ExistingStateRefused,
        WriteDecision.InvalidProposal => ServiceInstallationAnchorWriteState.InvalidProposal,
        _ => ServiceInstallationAnchorWriteState.Unavailable,
    };

    /// <summary>
    /// Interprets already-gathered read facts. A file that exists but decodes
    /// to nothing usable is REFUSED, never reported as absent: reporting a
    /// truncated write as absence is exactly how an anchor would be silently
    /// overwritten.
    /// </summary>
    internal static ServiceInstallationAnchorReadResult Interpret(
        bool platformSupported, bool confirmedAbsent, bool oversized, byte[]? bytes)
    {
        if (!platformSupported)
        {
            return ServiceInstallationAnchorReadResult.Unavailable();
        }

        if (confirmedAbsent)
        {
            return ServiceInstallationAnchorReadResult.Absent();
        }

        // An oversized file EXISTS and is not our document, so it is a refusal,
        // decided without the bytes ever being read.
        if (oversized)
        {
            return ServiceInstallationAnchorReadResult.Refused(
                ServiceInstallationAnchorValidator.ForRefusedAnchor(
                    ServiceInstallationAnchorInvalidReason.OversizedInput));
        }

        if (bytes is null)
        {
            return ServiceInstallationAnchorReadResult.Unavailable();
        }

        string? json = TryDecodeStrictUtf8(bytes);
        if (json is null)
        {
            // Undecodable bytes at an EXISTING anchor path are a refusal, not an
            // access failure: something is there and it is not our document.
            return ServiceInstallationAnchorReadResult.Refused(
                ServiceInstallationAnchorValidator.ForRefusedAnchor(
                    ServiceInstallationAnchorInvalidReason.MalformedJson));
        }

        ServiceInstallationAnchorValidationResult validation =
            ServiceInstallationAnchorValidator.Validate(json);
        return validation.IsAccepted && validation.Outcome == ServiceInstallationAnchorOutcome.Valid
            ? ServiceInstallationAnchorReadResult.Validated(validation)
            : ServiceInstallationAnchorReadResult.Refused(validation);
    }

    /// <summary>
    /// Strict UTF-8 decode with the BOM refused outright: invalid bytes throw,
    /// are CONTAINED here, and never fall back to a replacement character.
    /// </summary>
    private static string? TryDecodeStrictUtf8(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return null;
        }

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
/// The FIXED store. <see cref="Read"/> and <see cref="Write"/> take no path and
/// derive the ONE real machine location themselves; the internal
/// directory-taking overloads are the disclosed test seam described in the file
/// header.
/// </summary>
internal static class ServiceInstallationAnchorStore
{
    /// <summary>
    /// The same-directory temporary leaf. Fixed, never caller-supplied, and
    /// always a sibling of the anchor so the replace stays on one volume.
    /// </summary>
    private const string TemporaryLeafName = ServiceMachineStorageContract.InstallationAnchorFileName + ".tmp";

    /// <summary>
    /// CYCLE 62. The fixed temporary leaf name, exposed READ-ONLY so a
    /// compensating transaction can prove the Service directory contains no
    /// member except the matching anchor and this one fixed temporary leaf. It
    /// grants no capability: it is a string.
    /// </summary>
    internal static string TemporaryLeafFileName => TemporaryLeafName;

    /// <summary>Reads the anchor from the ONE fixed machine location.</summary>
    internal static ServiceInstallationAnchorReadResult Read()
    {
        string? serviceDirectory = TryResolveFixedServiceDirectory();
        return serviceDirectory is null
            ? ServiceInstallationAnchorReadResult.Unavailable()
            : ReadFrom(serviceDirectory);
    }

    /// <summary>Writes the anchor to the ONE fixed machine location.</summary>
    internal static ServiceInstallationAnchorWriteState Write(ServiceInstallationAnchorDocument? document)
    {
        string? serviceDirectory = TryResolveFixedServiceDirectory();
        return serviceDirectory is null
            ? ServiceInstallationAnchorWriteState.Unavailable
            : WriteTo(serviceDirectory, document);
    }

    /// <summary>
    /// Composes %ProgramData%\PAXCookbook\Service from the fixed Shared names
    /// only, and requires the exact expected parent relationship. It NEVER
    /// creates anything.
    /// </summary>
    internal static string? TryResolveFixedServiceDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return null;
            }

            string root = Path.GetFullPath(
                Path.Combine(baseDirectory, ServiceMachineStorageContract.MachineRootFolderName));
            string serviceDirectory = Path.GetFullPath(
                Path.Combine(root, ServiceMachineStorageContract.ServiceDataFolderName));

            return string.Equals(
                Path.GetDirectoryName(serviceDirectory), root, StringComparison.OrdinalIgnoreCase)
                ? serviceDirectory
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- the disclosed internal test seam -----------------------------------

    internal static ServiceInstallationAnchorReadResult ReadFrom(string serviceDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ServiceInstallationAnchorStoreInterpreter.Interpret(false, false, false, null);
        }

        string anchorPath;
        try
        {
            anchorPath = ComposeAnchorPath(serviceDirectory);
        }
        catch (Exception)
        {
            return ServiceInstallationAnchorStoreInterpreter.Interpret(true, false, false, null);
        }

        byte[]? bytes = null;
        try
        {
            // A missing directory and a missing file are both CONFIRMED absence;
            // anything else that throws is an access failure, never absence.
            if (!Directory.Exists(serviceDirectory) || !File.Exists(anchorPath))
            {
                return ServiceInstallationAnchorStoreInterpreter.Interpret(true, true, false, null);
            }

            // Bounded BEFORE any buffer is allocated, from the file's own length.
            var info = new FileInfo(anchorPath);
            if (info.Length > ServiceInstallationAnchorContract.MaxAnchorBytes)
            {
                return ServiceInstallationAnchorStoreInterpreter.Interpret(true, false, true, null);
            }

            bytes = File.ReadAllBytes(anchorPath);
            return ServiceInstallationAnchorStoreInterpreter.Interpret(true, false, false, bytes);
        }
        catch (Exception)
        {
            return ServiceInstallationAnchorStoreInterpreter.Interpret(true, false, false, null);
        }
        finally
        {
            if (bytes is not null)
            {
                Array.Clear(bytes);
            }
        }
    }

    /// <summary>
    /// THE BINDING WRITE SEQUENCE. Decide from the CURRENT on-disk state before
    /// touching anything; write only when the decision authorizes it; write to a
    /// same-directory temporary, flush to disk, atomically replace, then
    /// immediately reread from disk and require the reread to reproduce the
    /// exact accepted anchor.
    /// </summary>
    internal static ServiceInstallationAnchorWriteState WriteTo(
        string serviceDirectory, ServiceInstallationAnchorDocument? document)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ServiceInstallationAnchorWriteState.Unavailable;
        }

        if (!ServiceInstallationAnchorContract.TrySerialize(document, out string json))
        {
            return ServiceInstallationAnchorWriteState.InvalidProposal;
        }

        ServiceInstallationAnchorReadResult existing = ReadFrom(serviceDirectory);
        ServiceInstallationAnchorStoreInterpreter.WriteDecision decision =
            ServiceInstallationAnchorStoreInterpreter.Decide(existing, document);
        if (decision != ServiceInstallationAnchorStoreInterpreter.WriteDecision.ProceedToWrite)
        {
            return ServiceInstallationAnchorStoreInterpreter.ToTerminalState(decision);
        }

        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
        string anchorPath;
        string temporaryPath;
        try
        {
            anchorPath = ComposeAnchorPath(serviceDirectory);
            temporaryPath = Path.Combine(serviceDirectory, TemporaryLeafName);
        }
        catch (Exception)
        {
            Array.Clear(bytes);
            return ServiceInstallationAnchorWriteState.Unavailable;
        }

        try
        {
            // THE MUTATION BOUNDARY (cycle 67R). Everything above this line is
            // a decision; this line is the first durable change. A failure at
            // or after it may leave the fixed machine root and service
            // directories present with INHERITED access control and no anchor
            // inside them, so no later report may claim nothing was touched.
            Directory.CreateDirectory(serviceDirectory);

            using (var stream = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(anchorPath))
            {
                File.Replace(temporaryPath, anchorPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, anchorPath);
            }
        }
        catch (Exception)
        {
            TryRemoveTemporary(temporaryPath);
            return ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate;
        }
        finally
        {
            Array.Clear(bytes);
        }

        // Immediate disk reread. The anchor is durable only if the bytes that
        // came back validate AND carry the exact identity that was written.
        ServiceInstallationAnchorReadResult verified = ReadFrom(serviceDirectory);
        if (verified.State != ServiceInstallationAnchorReadState.Validated
            || !ServiceInstallationAnchorContract.HasIdenticalImmutableIdentity(
                verified.Validation?.Document, document))
        {
            return ServiceInstallationAnchorWriteState.VerificationFailed;
        }

        return ServiceInstallationAnchorWriteState.Created;
    }

    private static string ComposeAnchorPath(string serviceDirectory) =>
        Path.Combine(serviceDirectory, ServiceMachineStorageContract.InstallationAnchorFileName);

    /// <summary>
    /// CYCLE 67R. THE ONE STATEMENT OF THE MUTATION BOUNDARY.
    ///
    /// TRUE when the state can only be produced AT OR AFTER the single
    /// <c>Directory.CreateDirectory</c> call inside <see cref="WriteTo"/>, so
    /// the fixed machine root and service directories may now exist where they
    /// did not before. FALSE only for the states decided strictly BEFORE that
    /// call, which are the only ones entitled to be reported outward as
    /// refused-before-mutation.
    ///
    /// AN UNDEFINED CAST IS TRUE. A vocabulary this build does not understand is
    /// never read as proof that nothing was mutated.
    /// </summary>
    internal static bool MayHaveCreatedDirectories(ServiceInstallationAnchorWriteState state) => state switch
    {
        ServiceInstallationAnchorWriteState.Unspecified
            or ServiceInstallationAnchorWriteState.AlreadyMatching
            or ServiceInstallationAnchorWriteState.ConflictingIdentity
            or ServiceInstallationAnchorWriteState.ExistingStateRefused
            or ServiceInstallationAnchorWriteState.InvalidProposal
            or ServiceInstallationAnchorWriteState.Unavailable => false,
        _ => true,
    };

    /// <summary>
    /// CYCLE 62. THE ONE NARROW REMOVAL PRIMITIVE, and the only deletion
    /// authority anywhere in the anchor store.
    ///
    /// IT IS NOT A REPAIR, A RESET OR A RECOVERY OPERATION. It exists solely so
    /// a failing same-transaction compensation can undo an anchor THAT VERY
    /// ATTEMPT created. It refuses unless the bytes currently on disk validate
    /// AND carry the EXACT expected immutable identity, so a conflicting,
    /// malformed, foreign or already-changed anchor is never deleted. The caller
    /// is responsible for proving - before calling - that no other machine state
    /// depends on the anchor; this primitive proves only identity and absence.
    /// </summary>
    internal static ServiceInstallationAnchorRemovalState RemoveAnchorCreatedThisTransactionFrom(
        string serviceDirectory, ServiceInstallationAnchorDocument? expected)
    {
        if (!OperatingSystem.IsWindows() || expected is null)
        {
            return ServiceInstallationAnchorRemovalState.Unavailable;
        }

        ServiceInstallationAnchorReadResult current = ReadFrom(serviceDirectory);
        switch (current.State)
        {
            case ServiceInstallationAnchorReadState.Absent:
                return ServiceInstallationAnchorRemovalState.AlreadyAbsent;

            case ServiceInstallationAnchorReadState.Validated:
                break;

            case ServiceInstallationAnchorReadState.Refused:
                // Existing state that does not validate is NEVER removed. It is
                // exactly the evidence an attended recovery would need.
                return ServiceInstallationAnchorRemovalState.IdentityMismatch;

            default:
                return ServiceInstallationAnchorRemovalState.Unavailable;
        }

        if (!ServiceInstallationAnchorContract.HasIdenticalImmutableIdentity(
                current.Validation?.Document, expected))
        {
            return ServiceInstallationAnchorRemovalState.IdentityMismatch;
        }

        string anchorPath;
        try
        {
            anchorPath = ComposeAnchorPath(serviceDirectory);
        }
        catch (Exception)
        {
            return ServiceInstallationAnchorRemovalState.Unavailable;
        }

        try
        {
            File.Delete(anchorPath);
        }
        catch (Exception)
        {
            return ServiceInstallationAnchorRemovalState.Unavailable;
        }

        // Immediate disk reread. Removal is proven only by confirmed absence.
        return ReadFrom(serviceDirectory).State == ServiceInstallationAnchorReadState.Absent
            ? ServiceInstallationAnchorRemovalState.Removed
            : ServiceInstallationAnchorRemovalState.VerificationFailed;
    }

    private static void TryRemoveTemporary(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception)
        {
            // Best effort: a leftover temporary is never read and is always
            // overwritten by the next write.
        }
    }
}
