using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

// ---------------------------------------------------------------------------
// FIXED PROMOTED-RECIPE PERSISTENCE - cycle 82 (D3). SANDBOX ONLY.
//
// WHAT THIS IS. The narrow surface a future promotion transaction would use to
// place ONE validated Recipe into the machine Recipe store under ONE bounded
// promoted-job id, and to compensate that placement if the transaction unwinds.
//
// NOTHING IN THE PRODUCT CALLS IT. There is no production caller, no wiring into
// Settings, the Setup wizard, the service, IPC, scheduling or Cook execution, and
// no production root: the ONLY constructor takes a containment root, it is
// INTERNAL, and the only thing that supplies one is the test project.
//
// NO CALLER-SUPPLIED PATH, ANYWHERE. Persist takes a job id and validated content
// and nothing else. The destination is derived INTERNALLY from
// ServiceMachineStorageContract's fixed names - the machine root, the service
// metadata folder, and the machine Recipe store folder - plus the bounded job id.
// A caller cannot name a directory, a file, a leaf, an extension or a root. The
// containment root exists so this cycle can be exercised without touching
// %ProgramData%; it is NOT a path the surface composes on a caller's behalf.
//
// THE ORIGINAL PER-USER RECIPE IS NEVER TOUCHED. Structurally, not by promise:
// this type never receives a source path, so it has nothing to move, rename,
// copy-with-delete or truncate. It writes bytes it was handed, to a destination
// it derived, and the only file it ever deletes is its own staging file or a
// promoted Recipe whose CONTENT DIGEST matches the receipt this surface issued.
//
// FAIL CLOSED. A reparse point anywhere on the derived chain, an existing
// destination, an unrecognised sibling in the store, malformed Recipe content, or
// a reread that does not match the bytes written - each refuses, and each refuses
// BEFORE the destination is replaced. No reason string, path, exception text or
// Recipe byte escapes in any result.
// ---------------------------------------------------------------------------

/// <summary>Bounded promoted-Recipe persistence outcome. Zero always refuses.</summary>
internal enum ServiceOwnershipPromotedRecipePersistOutcome
{
    Unspecified = 0,
    Persisted = 1,

    /// <summary>The promoted-job id is not a bounded token.</summary>
    InvalidPromotedJobId = 2,

    /// <summary>The Recipe bytes did not pass bounded structural validation.</summary>
    InvalidRecipeContent = 3,

    /// <summary>The derived store could not be established or read.</summary>
    StoreUnavailable = 4,

    /// <summary>Something already occupies the derived destination.</summary>
    DestinationCollision = 5,

    /// <summary>The store holds a file this surface could not have written.</summary>
    UnknownSiblingPresent = 6,

    /// <summary>A reparse point stands on the derived chain, so the store may escape.</summary>
    ReparsePointRefused = 7,

    /// <summary>The reread did not match the bytes that were written.</summary>
    VerificationMismatch = 8,

    /// <summary>A bounded I/O failure. No exception text is carried.</summary>
    WriteFailed = 9,
}

/// <summary>Bounded compensation outcome. Zero always refuses.</summary>
internal enum ServiceOwnershipPromotedRecipeCompensationOutcome
{
    Unspecified = 0,
    Removed = 1,

    /// <summary>Nothing was there, which is a clean compensation, not an error.</summary>
    NothingToRemove = 2,

    /// <summary>The receipt was missing, unusable, or not a persisted receipt.</summary>
    InvalidReceipt = 3,

    /// <summary>
    /// A file is present but its content digest is NOT the one this surface wrote,
    /// so it is not a transaction-created promoted Recipe and is left alone.
    /// </summary>
    NotTransactionCreated = 4,

    /// <summary>A reparse point stands on the derived chain.</summary>
    ReparsePointRefused = 5,

    /// <summary>A bounded I/O failure. No exception text is carried.</summary>
    RemovalFailed = 6,
}

/// <summary>
/// VALIDATED Recipe bytes. The only constructor is PRIVATE, so an instance exists
/// only if <see cref="TryCreate"/> accepted the bytes.
///
/// WHAT "VALIDATED" MEANS HERE, STATED HONESTLY. This is BOUNDED STRUCTURAL
/// validation: non-empty, size-bounded, no byte-order mark, strict UTF-8, and a
/// well-formed JSON OBJECT. The product's full Recipe business schema lives in
/// PAXCookbook.App, which this assembly does not reference and must not, so this
/// surface deliberately does NOT claim to validate Recipe semantics. Because
/// nothing in the product calls this surface, no unvalidated Recipe can reach a
/// machine store through it today; wiring a production caller REQUIRES supplying a
/// real Recipe schema gate first.
/// </summary>
internal readonly struct ServiceOwnershipPromotedRecipeContent
{
    /// <summary>The largest Recipe this surface will carry, matching the ledger's own input bound.</summary>
    internal const int MaxRecipeBytes = 262144;

    private ServiceOwnershipPromotedRecipeContent(byte[] bytes, string sha256)
    {
        Bytes = bytes;
        Sha256 = sha256;
    }

    /// <summary>A defensive copy. The caller's array is never retained.</summary>
    internal byte[] Bytes { get; }

    /// <summary>Uppercase SHA-256 hex of <see cref="Bytes"/>.</summary>
    internal string Sha256 { get; }

    internal bool IsValidated => Bytes is not null && Bytes.Length > 0;

    internal static bool TryCreate(byte[]? recipeBytes, out ServiceOwnershipPromotedRecipeContent content)
    {
        content = default;

        if (recipeBytes is null || recipeBytes.Length == 0 || recipeBytes.Length > MaxRecipeBytes)
        {
            return false;
        }

        // A byte-order mark is refused for the same reason the ownership-ledger
        // reader refuses one: it makes byte-for-byte comparison ambiguous.
        if (recipeBytes.Length >= 3
            && recipeBytes[0] == 0xEF && recipeBytes[1] == 0xBB && recipeBytes[2] == 0xBF)
        {
            return false;
        }

        string text;
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = strict.GetString(recipeBytes);
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(text);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        var copy = new byte[recipeBytes.Length];
        Buffer.BlockCopy(recipeBytes, 0, copy, 0, recipeBytes.Length);
        content = new ServiceOwnershipPromotedRecipeContent(copy, ToUpperHex(SHA256.HashData(copy)));
        return true;
    }

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
/// Bounded persistence receipt. It carries NO PATH on purpose: a caller that never
/// learns where the file went cannot ask for a different one next time.
/// </summary>
internal sealed class ServiceOwnershipPromotedRecipePersistResult
{
    private ServiceOwnershipPromotedRecipePersistResult(
        ServiceOwnershipPromotedRecipePersistOutcome outcome,
        string promotedJobId,
        string contentSha256)
    {
        Outcome = outcome;
        PromotedJobId = promotedJobId;
        ContentSha256 = contentSha256;
    }

    internal ServiceOwnershipPromotedRecipePersistOutcome Outcome { get; }

    internal string PromotedJobId { get; }

    /// <summary>Uppercase SHA-256 of the persisted bytes, or empty when nothing was written.</summary>
    internal string ContentSha256 { get; }

    internal bool IsPersisted =>
        Outcome == ServiceOwnershipPromotedRecipePersistOutcome.Persisted;

    internal static ServiceOwnershipPromotedRecipePersistResult Refused(
        ServiceOwnershipPromotedRecipePersistOutcome outcome) =>
        new(outcome, string.Empty, string.Empty);

    internal static ServiceOwnershipPromotedRecipePersistResult Persisted(
        string promotedJobId, string contentSha256) =>
        new(ServiceOwnershipPromotedRecipePersistOutcome.Persisted, promotedJobId, contentSha256);
}

/// <summary>
/// The fixed promoted-Recipe store. One persist verb, one compensate verb, no
/// enumeration verb, no read verb, no path input and no path output.
/// </summary>
internal sealed class ServiceOwnershipPromotedRecipeStore
{
    private const string PromotedRecipeExtension = ".json";

    private readonly string containmentRoot;

    /// <summary>
    /// INTERNAL, and supplied only by tests. The containment root stands in for
    /// CommonApplicationData so this cycle can be exercised without touching
    /// %ProgramData%. The fixed structure BENEATH it is not negotiable and is never
    /// caller-supplied.
    /// </summary>
    internal ServiceOwnershipPromotedRecipeStore(string containmentRoot)
    {
        this.containmentRoot = containmentRoot ?? string.Empty;
    }

    internal ServiceOwnershipPromotedRecipePersistResult Persist(
        string? promotedJobId,
        ServiceOwnershipPromotedRecipeContent content)
    {
        if (!IsBoundedJobId(promotedJobId))
        {
            return ServiceOwnershipPromotedRecipePersistResult.Refused(
                ServiceOwnershipPromotedRecipePersistOutcome.InvalidPromotedJobId);
        }
        if (!content.IsValidated)
        {
            return ServiceOwnershipPromotedRecipePersistResult.Refused(
                ServiceOwnershipPromotedRecipePersistOutcome.InvalidRecipeContent);
        }

        string storeDirectory;
        try
        {
            storeDirectory = DeriveStoreDirectory();
            Directory.CreateDirectory(storeDirectory);
        }
        catch (Exception)
        {
            return ServiceOwnershipPromotedRecipePersistResult.Refused(
                ServiceOwnershipPromotedRecipePersistOutcome.StoreUnavailable);
        }

        if (ChainHasReparsePoint(storeDirectory))
        {
            return ServiceOwnershipPromotedRecipePersistResult.Refused(
                ServiceOwnershipPromotedRecipePersistOutcome.ReparsePointRefused);
        }

        string destination = Path.Combine(storeDirectory, promotedJobId! + PromotedRecipeExtension);

        // The derived destination must still be a direct child of the derived store
        // after full resolution. Nothing can escape upward or sideways.
        string resolvedDestination;
        try
        {
            resolvedDestination = Path.GetFullPath(destination);
        }
        catch (Exception)
        {
            return ServiceOwnershipPromotedRecipePersistResult.Refused(
                ServiceOwnershipPromotedRecipePersistOutcome.StoreUnavailable);
        }
        if (!string.Equals(
                Path.GetDirectoryName(resolvedDestination),
                Path.GetFullPath(storeDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return ServiceOwnershipPromotedRecipePersistResult.Refused(
                ServiceOwnershipPromotedRecipePersistOutcome.StoreUnavailable);
        }

        try
        {
            if (File.Exists(resolvedDestination) || Directory.Exists(resolvedDestination))
            {
                return ServiceOwnershipPromotedRecipePersistResult.Refused(
                    ServiceOwnershipPromotedRecipePersistOutcome.DestinationCollision);
            }
        }
        catch (Exception)
        {
            return ServiceOwnershipPromotedRecipePersistResult.Refused(
                ServiceOwnershipPromotedRecipePersistOutcome.StoreUnavailable);
        }

        ServiceOwnershipPromotedRecipePersistOutcome siblingOutcome = InspectSiblings(storeDirectory);
        if (siblingOutcome != ServiceOwnershipPromotedRecipePersistOutcome.Persisted)
        {
            return ServiceOwnershipPromotedRecipePersistResult.Refused(siblingOutcome);
        }

        // SAME-DIRECTORY STAGING. A cross-directory write could not be renamed
        // atomically, and a partially written destination is exactly the state this
        // surface exists to make impossible.
        string staging = Path.Combine(
            storeDirectory,
            promotedJobId + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp");

        try
        {
            using (var stream = new FileStream(
                staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content.Bytes, 0, content.Bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(staging, resolvedDestination);
        }
        catch (Exception)
        {
            TryDeleteStaging(staging);
            return ServiceOwnershipPromotedRecipePersistResult.Refused(
                ServiceOwnershipPromotedRecipePersistOutcome.WriteFailed);
        }

        // IMMEDIATE REREAD AND BYTE/HASH VERIFICATION.
        byte[] rereadBytes;
        try
        {
            rereadBytes = File.ReadAllBytes(resolvedDestination);
        }
        catch (Exception)
        {
            return ServiceOwnershipPromotedRecipePersistResult.Refused(
                ServiceOwnershipPromotedRecipePersistOutcome.VerificationMismatch);
        }

        if (rereadBytes.Length != content.Bytes.Length
            || !CryptographicOperations.FixedTimeEquals(rereadBytes, content.Bytes)
            || !string.Equals(ToUpperHex(SHA256.HashData(rereadBytes)), content.Sha256, StringComparison.Ordinal))
        {
            // The transaction did not complete, so its artifact does not survive.
            TryDeleteStaging(resolvedDestination);
            return ServiceOwnershipPromotedRecipePersistResult.Refused(
                ServiceOwnershipPromotedRecipePersistOutcome.VerificationMismatch);
        }

        return ServiceOwnershipPromotedRecipePersistResult.Persisted(promotedJobId!, content.Sha256);
    }

    /// <summary>
    /// Removes ONLY a promoted Recipe this surface created, proven by comparing the
    /// file's current content digest to the receipt's. A file whose digest differs
    /// is somebody else's and is left exactly where it is.
    /// </summary>
    internal ServiceOwnershipPromotedRecipeCompensationOutcome Compensate(
        ServiceOwnershipPromotedRecipePersistResult? receipt)
    {
        if (receipt is null
            || !receipt.IsPersisted
            || !IsBoundedJobId(receipt.PromotedJobId)
            || receipt.ContentSha256.Length != 64)
        {
            return ServiceOwnershipPromotedRecipeCompensationOutcome.InvalidReceipt;
        }

        string storeDirectory;
        string destination;
        try
        {
            storeDirectory = DeriveStoreDirectory();
            destination = Path.GetFullPath(
                Path.Combine(storeDirectory, receipt.PromotedJobId + PromotedRecipeExtension));
        }
        catch (Exception)
        {
            return ServiceOwnershipPromotedRecipeCompensationOutcome.RemovalFailed;
        }

        if (ChainHasReparsePoint(storeDirectory))
        {
            return ServiceOwnershipPromotedRecipeCompensationOutcome.ReparsePointRefused;
        }

        try
        {
            if (!File.Exists(destination))
            {
                return ServiceOwnershipPromotedRecipeCompensationOutcome.NothingToRemove;
            }

            var info = new FileInfo(destination);
            if ((info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                return ServiceOwnershipPromotedRecipeCompensationOutcome.ReparsePointRefused;
            }

            byte[] current = File.ReadAllBytes(destination);
            if (!string.Equals(
                    ToUpperHex(SHA256.HashData(current)), receipt.ContentSha256, StringComparison.Ordinal))
            {
                return ServiceOwnershipPromotedRecipeCompensationOutcome.NotTransactionCreated;
            }

            File.Delete(destination);
            return File.Exists(destination)
                ? ServiceOwnershipPromotedRecipeCompensationOutcome.RemovalFailed
                : ServiceOwnershipPromotedRecipeCompensationOutcome.Removed;
        }
        catch (Exception)
        {
            return ServiceOwnershipPromotedRecipeCompensationOutcome.RemovalFailed;
        }
    }

    /// <summary>
    /// The ONE derivation. Every name below comes from
    /// <see cref="ServiceMachineStorageContract"/>; none is retyped here.
    /// </summary>
    private string DeriveStoreDirectory() =>
        Path.Combine(
            containmentRoot,
            ServiceMachineStorageContract.MachineRootFolderName,
            ServiceMachineStorageContract.ServiceDataFolderName,
            ServiceMachineStorageContract.MachineRecipeStoreFolderName);

    /// <summary>
    /// The store may hold ONLY promoted Recipes this surface could have written:
    /// a bounded job id plus the fixed extension. A directory, a reparse point, or
    /// any other leaf means the namespace is not exclusively ours, and a namespace
    /// we do not exclusively own is not one we may compensate inside.
    /// </summary>
    private static ServiceOwnershipPromotedRecipePersistOutcome InspectSiblings(string storeDirectory)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(storeDirectory);
        }
        catch (Exception)
        {
            return ServiceOwnershipPromotedRecipePersistOutcome.StoreUnavailable;
        }

        try
        {
            foreach (string entry in entries)
            {
                var info = new FileInfo(entry);
                if ((info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    return ServiceOwnershipPromotedRecipePersistOutcome.ReparsePointRefused;
                }
                if ((info.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    return ServiceOwnershipPromotedRecipePersistOutcome.UnknownSiblingPresent;
                }

                string leaf = Path.GetFileName(entry);
                if (!leaf.EndsWith(PromotedRecipeExtension, StringComparison.Ordinal)
                    || !IsBoundedJobId(leaf.Substring(0, leaf.Length - PromotedRecipeExtension.Length)))
                {
                    return ServiceOwnershipPromotedRecipePersistOutcome.UnknownSiblingPresent;
                }
            }
        }
        catch (Exception)
        {
            return ServiceOwnershipPromotedRecipePersistOutcome.StoreUnavailable;
        }

        return ServiceOwnershipPromotedRecipePersistOutcome.Persisted;
    }

    /// <summary>
    /// Walks the derived chain from the store directory upward and refuses if ANY
    /// level is a reparse point, so a junction or symlink cannot redirect the store
    /// outside the containment root.
    /// </summary>
    private bool ChainHasReparsePoint(string storeDirectory)
    {
        try
        {
            string stop = Path.GetFullPath(containmentRoot);
            DirectoryInfo? current = new(Path.GetFullPath(storeDirectory));
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

    private static void TryDeleteStaging(string path)
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
            // Best effort. A stranded staging file is refused as an unknown sibling
            // on the next persist, which is the fail-closed direction.
        }
    }

    /// <summary>
    /// The SAME bounded-token rule the ownership ledger applies to promoted job ids,
    /// taken from the contract rather than retyped, so a job id that could never
    /// appear in the ledger can never name a file here either.
    /// </summary>
    private static bool IsBoundedJobId(string? value) =>
        ServiceOwnershipLedgerContract.IsValidBoundedToken(
            value, ServiceOwnershipLedgerContract.MaxStringLength);

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
/// THE PRODUCTION PROMOTED-RECIPE ADAPTER (cycle 92 pass B).
///
/// WHAT IT IS. The narrow bridge between the promotion transaction's bounded
/// promoted-Recipe port and the fixed store above. It persists ONLY the accepted
/// request's own certified Recipe bytes under the accepted request's own
/// promoted-job id, and it compensates ONLY the placement it itself made.
///
/// WHY IT TAKES A STORE AND NOT A ROOT. It accepts a CLOSED, already-constructed
/// store object. It never receives, derives, composes or exposes a destination:
/// no root, directory, leaf, extension or path of any kind reaches it, and it has
/// no way to ask for a different one. This file may not name the machine data
/// folder itself - a certified whole-file guard forbids it - so a store object is
/// the only lawful way a destination can ever be established.
///
/// THE RECEIPT IS THE WHOLE COMPENSATION AUTHORITY. A compensation runs only when
/// the promoted job id it is given EXACTLY matches the receipt this adapter is
/// holding, and the store then removes the file only if its CONTENT DIGEST still
/// matches that receipt. The receipt is cleared after a completed compensation or
/// on terminal disposal, and never otherwise, so a compensation for a job this
/// adapter does not own can never remove anything.
///
/// WHY THE JOB-ID GATE IS THE KEY-IDENTITY GRAMMAR. The store's own sibling rule
/// is a bounded-token rule that is a strict SUPERSET of the request grammar. This
/// adapter applies the narrower certified key-identity grammar before the store
/// is ever asked, so nothing looser than the ledger's own leaf grammar can name a
/// promoted Recipe through this port.
/// </summary>
internal sealed class ServiceOwnershipFixedPromotedRecipePort
    : IServiceOwnershipPromotedRecipePort, IDisposable
{
    private readonly ServiceOwnershipPromotedRecipeStore store;

    private ServiceOwnershipPromotedRecipePersistResult? receipt;

    internal ServiceOwnershipFixedPromotedRecipePort(ServiceOwnershipPromotedRecipeStore store)
    {
        this.store = store;
    }

    /// <summary>True only while this adapter is holding a persistence receipt.</summary>
    internal bool HasReceipt => receipt is not null;

    public ServiceOwnershipPromotedRecipePortOutcome PersistPromotedRecipe(ServicePromotionRequest request)
    {
        // ONE placement per adapter instance. A second persist would strand the
        // first receipt, and a stranded receipt is a placement nothing can undo.
        if (request is null || receipt is not null)
        {
            return ServiceOwnershipPromotedRecipePortOutcome.Refused;
        }

        if (!ServiceOwnershipLedgerContract.IsValidKeyIdentity(request.PromotedJobId))
        {
            return ServiceOwnershipPromotedRecipePortOutcome.Refused;
        }

        byte[] recipeBytes = request.RecipeBytes;

        // The accepted request already bound its own Recipe digest. Re-deriving it
        // here means this adapter can never persist bytes the request did not
        // actually carry.
        if (!string.Equals(
                Convert.ToHexString(SHA256.HashData(recipeBytes)),
                request.ExpectedRecipeSha256,
                StringComparison.Ordinal))
        {
            return ServiceOwnershipPromotedRecipePortOutcome.Refused;
        }

        if (!ServiceOwnershipPromotedRecipeContent.TryCreate(
                recipeBytes, out ServiceOwnershipPromotedRecipeContent content))
        {
            return ServiceOwnershipPromotedRecipePortOutcome.Refused;
        }

        ServiceOwnershipPromotedRecipePersistResult result =
            store.Persist(request.PromotedJobId, content);

        if (!result.IsPersisted)
        {
            return ServiceOwnershipPromotedRecipePortOutcome.Refused;
        }

        receipt = result;
        return ServiceOwnershipPromotedRecipePortOutcome.Completed;
    }

    public ServiceOwnershipPromotedRecipePortOutcome CompensatePromotedRecipe(string promotedJobId)
    {
        ServiceOwnershipPromotedRecipePersistResult? held = receipt;

        // No receipt means this adapter placed nothing, so it may remove nothing -
        // and an id that is not EXACTLY the receipt's is somebody else's job.
        if (held is null
            || !string.Equals(promotedJobId, held.PromotedJobId, StringComparison.Ordinal))
        {
            return ServiceOwnershipPromotedRecipePortOutcome.Refused;
        }

        ServiceOwnershipPromotedRecipePortOutcome mapped = MapCompensation(store.Compensate(held));

        if (mapped == ServiceOwnershipPromotedRecipePortOutcome.Completed)
        {
            receipt = null;
        }

        return mapped;
    }

    /// <summary>Terminal disposal. The retained receipt does not outlive this adapter.</summary>
    public void Dispose() => receipt = null;

    /// <summary>
    /// PURE mapping from the bounded compensation outcome to the bounded port
    /// outcome. "Nothing to remove" is a CLEAN compensation, not a failure.
    /// </summary>
    internal static ServiceOwnershipPromotedRecipePortOutcome MapCompensation(
        ServiceOwnershipPromotedRecipeCompensationOutcome outcome) =>
        outcome is ServiceOwnershipPromotedRecipeCompensationOutcome.Removed
            or ServiceOwnershipPromotedRecipeCompensationOutcome.NothingToRemove
            ? ServiceOwnershipPromotedRecipePortOutcome.Completed
            : ServiceOwnershipPromotedRecipePortOutcome.Refused;
}
