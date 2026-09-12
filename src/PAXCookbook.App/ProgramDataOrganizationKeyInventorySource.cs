using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.App;

// A single, immutable ACE view consumed by the PURE trust policy. It carries only
// a SID, a rights mask, and an allow/deny type -- never an account name, path, or
// any other identifier -- so the policy can be exercised deterministically against
// synthesized principals without touching the filesystem.
internal readonly struct OrgInventoryAce
{
    internal OrgInventoryAce(SecurityIdentifier sid, FileSystemRights rights, AccessControlType type)
    {
        Sid = sid;
        Rights = rights;
        Type = type;
    }

    internal SecurityIdentifier Sid { get; }

    internal FileSystemRights Rights { get; }

    internal AccessControlType Type { get; }
}

// The PURE, deterministic, side-effect-free closed-SID owner/ACL trust policy for
// the organization key inventory. It reads only the owner SID and the ACE list it
// is given; it never touches the filesystem, registry, network, certificate
// store, or credential vault, and it never throws through its boundary. Trust is
// closed by default: only LocalSystem and BuiltinAdministrators are trusted
// principals, so a document that anyone else can WRITE (or that anyone else owns)
// is rejected. Read/execute grants to broad principals are fine; only a
// write-capable ALLOW to a non-trusted principal breaks trust. DENY/audit ACEs
// never grant trust. This layer is unit-tested directly with synthesized SIDs, so
// the positive LocalSystem/BuiltinAdministrators case is proven without elevation.
internal static class ProgramDataOrgInventoryTrustPolicy
{
    internal static SecurityIdentifier LocalSystem { get; } =
        new(WellKnownSidType.LocalSystemSid, null);

    internal static SecurityIdentifier BuiltinAdministrators { get; } =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    // The write-capable rights. FullControl, Modify, and Write are COMPOSITE
    // rights that include one or more of these bits, so a bitwise AND against this
    // mask catches every write-capable grant, whether atomic or composite.
    internal static FileSystemRights WriteCapableMask { get; } =
        FileSystemRights.WriteData
        | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    // The closed set of principals allowed to own or write the inventory in
    // production. There is no configuration, environment variable, command-line
    // switch, or user/HTTP input that can widen this set.
    internal static IReadOnlySet<SecurityIdentifier> DefaultTrustedPrincipals { get; } =
        new HashSet<SecurityIdentifier> { LocalSystem, BuiltinAdministrators };

    internal static bool IsTrusted(
        SecurityIdentifier? owner,
        IReadOnlyList<OrgInventoryAce> aces,
        IReadOnlySet<SecurityIdentifier> trustedPrincipals)
    {
        // The owner must itself be a trusted principal.
        if (owner is null || !trustedPrincipals.Contains(owner))
        {
            return false;
        }

        foreach (OrgInventoryAce ace in aces)
        {
            // Only ALLOW ACEs can grant write; DENY/audit never establishes trust.
            if (ace.Type != AccessControlType.Allow)
            {
                continue;
            }

            // A read/execute-only grant to any principal is acceptable.
            if ((ace.Rights & WriteCapableMask) == 0)
            {
                continue;
            }

            // A write-capable ALLOW to a non-trusted principal breaks trust.
            if (!trustedPrincipals.Contains(ace.Sid))
            {
                return false;
            }
        }

        return true;
    }
}

// The REAL, read-only, trust-validating ProgramData organization-key-inventory
// source. It derives EXACTLY
//   Environment.GetFolderPath(CommonApplicationData) +
//   \PAXCookbook\ManagedChefKeys\organization-key-inventory.json
// and, before handing anything to the parser, enforces: path containment under
// the derived root, per-component reparse-point rejection from the root down to
// the file, closed-SID owner/ACL trust on BOTH the file and its parent directory,
// a byte-size bound checked BEFORE buffering, a stability re-check around a shared
// read, and strict UTF-8 decoding (a leading UTF-8 BOM is tolerated; any other
// BOM or non-UTF-8 byte sequence is rejected). It is READ-ONLY: it never creates,
// writes, deletes, repairs, or ACL-mutates anything under ProgramData, never
// touches a certificate store/private key/service/credential vault/registry/
// network, and never runs a Cook/PAX/Bake. It fails CLOSED: any access-denied or
// unexpected condition resolves to a bounded Unavailable/Untrusted/InvalidContent
// result and NEVER to Provided. In production there is NO override: the root is
// always CommonApplicationData and the trusted set is always the default closed
// SID set -- no environment variable, command line, HTTP, React, TestIsolation,
// or caller input can redirect it. An internal seam (used ONLY by tests, and NOT
// reachable from production wiring) supplies an absolute OS-temp test root plus
// additional trusted principals so a current-user-owned temp file can exercise
// the real read/decode pipeline without elevation.
internal sealed class ProgramDataOrganizationKeyInventorySource : IOrganizationKeyInventorySource
{
    internal const string ProductDirName = "PAXCookbook";
    internal const string ManagedDirName = "ManagedChefKeys";
    internal const string InventoryFileName = "organization-key-inventory.json";

    private readonly string? _testRootOverride;
    private readonly IReadOnlySet<SecurityIdentifier> _trustedPrincipals;

    // PRODUCTION constructor: derive ONLY from CommonApplicationData and trust ONLY
    // the default closed SID set. No override of any kind is reachable here.
    public ProgramDataOrganizationKeyInventorySource()
        : this(testRootOverride: null, additionalTrustedPrincipals: null)
    {
    }

    // INTERNAL test seam. NOT reachable from production wiring: production calls the
    // parameterless constructor above. The test root MUST be an absolute path (the
    // production derivation never uses it), and the additional principals let a
    // current-user-owned temp file be treated as trusted so the real read/decode/
    // parse pipeline can be exercised without elevation.
    internal ProgramDataOrganizationKeyInventorySource(
        string? testRootOverride,
        IReadOnlySet<SecurityIdentifier>? additionalTrustedPrincipals)
    {
        _testRootOverride = testRootOverride;
        if (additionalTrustedPrincipals is null || additionalTrustedPrincipals.Count == 0)
        {
            _trustedPrincipals = ProgramDataOrgInventoryTrustPolicy.DefaultTrustedPrincipals;
        }
        else
        {
            var set = new HashSet<SecurityIdentifier>(ProgramDataOrgInventoryTrustPolicy.DefaultTrustedPrincipals);
            foreach (SecurityIdentifier sid in additionalTrustedPrincipals)
            {
                set.Add(sid);
            }
            _trustedPrincipals = set;
        }
    }

    public OrganizationInventorySourceResult Load()
    {
        try
        {
            return LoadCore();
        }
        catch
        {
            // Fail closed on any unexpected condition; never surface an exception
            // or a permissive result across the boundary.
            return OrganizationInventorySourceResult.Unavailable();
        }
    }

    private OrganizationInventorySourceResult LoadCore()
    {
        // (1) Resolve the read root. Production uses ONLY CommonApplicationData.
        string root = _testRootOverride
            ?? Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolderOption.None);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            return OrganizationInventorySourceResult.Unavailable();
        }
        try
        {
            if (!Directory.Exists(root))
            {
                return OrganizationInventorySourceResult.Unavailable();
            }
        }
        catch (UnauthorizedAccessException)
        {
            return OrganizationInventorySourceResult.Unavailable();
        }
        catch (SecurityException)
        {
            return OrganizationInventorySourceResult.Unavailable();
        }

        // (2) Derive the fixed managed dir + file, and enforce containment plus the
        //     exact component names. A derived path that escapes the root or does
        //     not match the fixed structure is untrusted.
        string managedDir = Path.GetFullPath(Path.Combine(root, ProductDirName, ManagedDirName));
        string fullPath = Path.GetFullPath(Path.Combine(managedDir, InventoryFileName));
        string? productDir = Path.GetDirectoryName(managedDir);
        if (productDir is null
            || !IsAtOrUnder(managedDir, root)
            || !IsAtOrUnder(fullPath, managedDir)
            || !NameEquals(Path.GetFileName(fullPath), InventoryFileName)
            || !NameEquals(Path.GetFileName(managedDir), ManagedDirName)
            || !NameEquals(Path.GetFileName(productDir), ProductDirName))
        {
            return OrganizationInventorySourceResult.Untrusted();
        }

        // (3) Absent managed directory => not provisioned.
        try
        {
            if (!Directory.Exists(managedDir))
            {
                return OrganizationInventorySourceResult.NotProvisioned();
            }
        }
        catch (UnauthorizedAccessException)
        {
            return OrganizationInventorySourceResult.Unavailable();
        }
        catch (SecurityException)
        {
            return OrganizationInventorySourceResult.Unavailable();
        }

        // (4) A directory where the file must be => wrong object type => untrusted.
        //     An absent file => not provisioned.
        if (Directory.Exists(fullPath))
        {
            return OrganizationInventorySourceResult.Untrusted();
        }
        if (!File.Exists(fullPath))
        {
            return OrganizationInventorySourceResult.NotProvisioned();
        }

        // (5) Reject a reparse point on ANY component from the root down to the
        //     file itself. A redirected component could point trust at bytes that
        //     no longer live under the trusted managed directory.
        if (HasReparsePointFromRootToFileParent(root, fullPath) || IsReparsePoint(fullPath))
        {
            return OrganizationInventorySourceResult.Untrusted();
        }

        // (6) Closed-SID owner/ACL trust for BOTH the parent directory and the file.
        TrustReadOutcome dirTrust = EvaluateTrust(managedDir, isDirectory: true);
        if (dirTrust != TrustReadOutcome.Trusted)
        {
            return dirTrust == TrustReadOutcome.AccessDenied
                ? OrganizationInventorySourceResult.Unavailable()
                : OrganizationInventorySourceResult.Untrusted();
        }
        TrustReadOutcome fileTrust = EvaluateTrust(fullPath, isDirectory: false);
        if (fileTrust != TrustReadOutcome.Trusted)
        {
            return fileTrust == TrustReadOutcome.AccessDenied
                ? OrganizationInventorySourceResult.Unavailable()
                : OrganizationInventorySourceResult.Untrusted();
        }

        // (7) Size bound BEFORE buffering, so an oversized blob is never read into
        //     memory or handed to the parser.
        long preLength;
        DateTime preWrite;
        DateTime preCreate;
        try
        {
            var fi = new FileInfo(fullPath);
            preLength = fi.Length;
            preWrite = fi.LastWriteTimeUtc;
            preCreate = fi.CreationTimeUtc;
        }
        catch (UnauthorizedAccessException)
        {
            return OrganizationInventorySourceResult.Unavailable();
        }
        catch (SecurityException)
        {
            return OrganizationInventorySourceResult.Unavailable();
        }
        if (preLength > OrganizationKeyInventoryContract.MaxDocumentBytes)
        {
            return OrganizationInventorySourceResult.Untrusted();
        }

        // (8) Stable, bounded read: read under a shared-read lock and confirm the
        //     length/timestamps did not change around the read.
        byte[]? bytes = TryStableRead(fullPath, preLength, preWrite, preCreate);
        if (bytes is null)
        {
            return OrganizationInventorySourceResult.Unavailable();
        }

        // (9) Strict UTF-8 decode. A leading UTF-8 BOM is tolerated and stripped;
        //     any other BOM or non-UTF-8 byte sequence is invalid content.
        string? text = TryDecodeStrictUtf8(bytes);
        if (text is null)
        {
            return OrganizationInventorySourceResult.InvalidContent();
        }

        // (10) Hand the trusted, decoded bytes to the evaluator, which invokes the
        //      Cycle-5 parser EXACTLY ONCE. The source itself never parses.
        return OrganizationInventorySourceResult.Provided(text);
    }

    private enum TrustReadOutcome
    {
        Trusted,
        Untrusted,
        AccessDenied,
    }

    // Read the owner SID and DACL by SID, convert to the pure ACE view, and apply
    // the closed-SID policy. Access-denied is distinct from a query failure: the
    // former means we could not observe trust (fail closed to Unavailable), the
    // latter (unsupported/non-NTFS/corrupt) means we cannot establish trust (fail
    // closed to Untrusted).
    private TrustReadOutcome EvaluateTrust(string path, bool isDirectory)
    {
        SecurityIdentifier? owner;
        var aces = new List<OrgInventoryAce>();
        try
        {
            FileSystemSecurity sec = isDirectory
                ? new DirectorySecurity(path, AccessControlSections.Owner | AccessControlSections.Access)
                : new FileSecurity(path, AccessControlSections.Owner | AccessControlSections.Access);
            owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            AuthorizationRuleCollection rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (AuthorizationRule rule in rules)
            {
                if (rule is FileSystemAccessRule fsRule
                    && fsRule.IdentityReference is SecurityIdentifier sid)
                {
                    aces.Add(new OrgInventoryAce(sid, fsRule.FileSystemRights, fsRule.AccessControlType));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return TrustReadOutcome.AccessDenied;
        }
        catch (SecurityException)
        {
            return TrustReadOutcome.AccessDenied;
        }
        catch
        {
            return TrustReadOutcome.Untrusted;
        }

        return ProgramDataOrgInventoryTrustPolicy.IsTrusted(owner, aces, _trustedPrincipals)
            ? TrustReadOutcome.Trusted
            : TrustReadOutcome.Untrusted;
    }

    // Read at most MaxDocumentBytes bytes under a shared-read lock, and confirm the
    // observed length/timestamps match the pre-read stat so a file that changes
    // out from under us fails closed rather than yielding a torn document.
    private static byte[]? TryStableRead(
        string path,
        long expectedLength,
        DateTime expectedWrite,
        DateTime expectedCreate)
    {
        long cap = OrganizationKeyInventoryContract.MaxDocumentBytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length != expectedLength || fs.Length > cap)
            {
                return null;
            }

            int len = (int)fs.Length;
            var buffer = new byte[len];
            int offset = 0;
            while (offset < len)
            {
                int read = fs.Read(buffer, offset, len - offset);
                if (read <= 0)
                {
                    return null; // truncated relative to the stat
                }
                offset += read;
            }

            // Nothing beyond the expected length may remain.
            if (fs.ReadByte() != -1)
            {
                return null;
            }

            var after = new FileInfo(path);
            if (after.Length != expectedLength
                || after.LastWriteTimeUtc != expectedWrite
                || after.CreationTimeUtc != expectedCreate)
            {
                return null;
            }

            return buffer;
        }
        catch
        {
            return null;
        }
    }

    // Tolerate a single leading UTF-8 BOM; reject a UTF-16/UTF-32 BOM or any
    // non-UTF-8 byte sequence. Returns null on any decode failure.
    private static string? TryDecodeStrictUtf8(byte[] bytes)
    {
        int start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            start = 3;
        }
        else if (bytes.Length >= 2
            && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return null; // UTF-16 (LE/BE) BOM
        }
        else if (bytes.Length >= 4
            && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return null; // UTF-32 BE BOM
        }

        try
        {
            var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return enc.GetString(bytes, start, bytes.Length - start);
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

    // Separator-aware, case-insensitive containment: candidate is at or under the
    // boundary. Both are normalized before comparison.
    private static bool IsAtOrUnder(string candidate, string boundary)
    {
        char sep = Path.DirectorySeparatorChar;
        string c = Path.GetFullPath(candidate).TrimEnd(sep);
        string b = Path.GetFullPath(boundary).TrimEnd(sep);
        if (string.Equals(c, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return c.StartsWith(b + sep, StringComparison.OrdinalIgnoreCase);
    }

    private static bool NameEquals(string? actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true; // fail closed
        }
    }

    // Walk directory components from the file's parent up to (and including) the
    // root boundary; any reparse point on the way rejects the read. Never inspects
    // components above the boundary.
    private static bool HasReparsePointFromRootToFileParent(string boundaryRoot, string fullPath)
    {
        try
        {
            string root = Path.GetFullPath(boundaryRoot).TrimEnd(Path.DirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(Path.GetFullPath(fullPath));
            var dir = parent is null ? null : new DirectoryInfo(parent);
            while (dir is not null)
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
                string cur = dir.FullName.TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(cur, root, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                dir = dir.Parent;
            }
            return false;
        }
        catch
        {
            return true; // fail closed
        }
    }
}
