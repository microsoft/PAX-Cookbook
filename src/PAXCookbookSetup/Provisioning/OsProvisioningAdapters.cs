#if MANAGED_INVENTORY_PROVISIONING
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Provisioning;

// ---------------------------------------------------------------------------
// Production OS adapters (COMPILED but NOT invoked live this cycle).
//
// These are the real System.IO / System.Security.AccessControl implementations
// the future elevated helper would use. Every path is derived INTERNALLY from
// Environment.GetFolderPath(CommonApplicationData) + fixed contract constants —
// no path, name, or destination ever crosses the executor boundary. A separate
// internal test-seam factory roots the SAME logic under an absolute OS-temp
// directory so a single integration test can exercise real atomic replace + real
// ACL apply/inspect without touching real ProgramData.
//
// This cycle NO dispatch / product / broker / React / Bake call site constructs
// the production adapter, so nothing runs live.
// ---------------------------------------------------------------------------
internal sealed class OsProvisioningFileSystem : IProvisioningFileSystem
{
    private readonly string _managedDir;
    private readonly IProvisioningAcl _acl;

    private OsProvisioningFileSystem(string managedDir)
    {
        _managedDir = managedDir;
        _acl = new OsProvisioningAcl(managedDir);
    }

    // The resolved managed directory, so the executor can wire an ACL adapter over
    // the identical path. Never used to build an arbitrary path in the executor.
    internal string ManagedDirectory => _managedDir;

    // Production wiring: the managed directory is derived internally from
    // CommonApplicationData; the caller supplies nothing.
    internal static OsProvisioningFileSystem CreateProduction()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string managedDir = Path.Combine(root, "PAXCookbook", "ManagedChefKeys");
        return new OsProvisioningFileSystem(managedDir);
    }

    // Test seam: root the identical logic under an absolute OS-temp directory. This
    // ctor is unreachable from production dispatch (which uses CreateProduction()).
    internal static OsProvisioningFileSystem CreateForTest(string absoluteOsTempManagedDir)
        => new(absoluteOsTempManagedDir);

    private string PathFor(ProvisioningArtifact artifact, string operationId = "") => artifact switch
    {
        ProvisioningArtifact.ManagedDirectory => _managedDir,
        ProvisioningArtifact.Inventory => Path.Combine(_managedDir, ProvisioningContract.InventoryFileName),
        ProvisioningArtifact.PreviousInventory => Path.Combine(_managedDir, ProvisioningContract.PreviousInventoryFileName),
        ProvisioningArtifact.Ledger => Path.Combine(_managedDir, ProvisioningContract.LedgerFileName),
        ProvisioningArtifact.TempInventory => Path.Combine(_managedDir, ProvisioningContract.InventoryFileName + ".tmp-" + Sanitize(operationId)),
        ProvisioningArtifact.TempPreviousInventory => Path.Combine(_managedDir, ProvisioningContract.PreviousInventoryFileName + ".tmp-" + Sanitize(operationId)),
        ProvisioningArtifact.TempLedger => Path.Combine(_managedDir, ProvisioningContract.LedgerFileName + ".tmp-" + Sanitize(operationId)),
        ProvisioningArtifact.TransactionMarker => Path.Combine(_managedDir, "provisioning.transaction"),
        _ => throw new ProvisioningAdapterFault(ProvisioningActionKind.StampAcl),
    };

    // operationId is already validated to [A-Za-z0-9._-]; this is belt-and-braces.
    private static string Sanitize(string operationId)
    {
        foreach (char c in operationId)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
            if (!ok)
            {
                return "op";
            }
        }
        return operationId.Length == 0 ? "op" : operationId;
    }

    public bool ManagedDirectoryExists() => Directory.Exists(_managedDir);

    public void EnsureManagedDirectory()
    {
        Directory.CreateDirectory(_managedDir);
        _acl.Apply(ProvisioningAclBuilder.BuildManagedDirectoryDescriptor(), ProvisioningArtifact.ManagedDirectory);
    }

    public bool OwnedExists(ProvisioningArtifact artifact) => File.Exists(PathFor(artifact));

    public string ReadOwnedInventory(ProvisioningArtifact artifact) => File.ReadAllText(PathFor(artifact), Encoding.UTF8);

    public string HashOwnedInventory(ProvisioningArtifact artifact)
        => Sha256HexOfBytes(File.ReadAllBytes(PathFor(artifact)));

    public bool HasUnknownSibling()
    {
        if (!Directory.Exists(_managedDir))
        {
            return false;
        }
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ProvisioningContract.InventoryFileName,
            ProvisioningContract.PreviousInventoryFileName,
            ProvisioningContract.LedgerFileName,
            "provisioning.transaction",
        };
        foreach (string file in Directory.EnumerateFiles(_managedDir))
        {
            string name = Path.GetFileName(file);
            bool isTemp = name.Contains(".tmp-", StringComparison.Ordinal);
            if (!isTemp && !owned.Contains(name))
            {
                return true;
            }
        }
        return false;
    }

    public void StageInventoryTemp(ProvisioningArtifact tempArtifact, string operationId, string content)
        => WriteDurable(PathFor(tempArtifact, operationId), content);

    public string ReadInventoryTemp(ProvisioningArtifact tempArtifact, string operationId)
        => File.ReadAllText(PathFor(tempArtifact, operationId), Encoding.UTF8);

    public string HashInventoryTemp(ProvisioningArtifact tempArtifact, string operationId)
        => Sha256HexOfBytes(File.ReadAllBytes(PathFor(tempArtifact, operationId)));

    public void ReplaceOwnedInventoryFromTemp(ProvisioningArtifact ownedArtifact, ProvisioningArtifact tempArtifact, string operationId)
        => AtomicReplace(PathFor(tempArtifact, operationId), PathFor(ownedArtifact));

    public void RemoveOwned(ProvisioningArtifact artifact) => DeleteIfExists(PathFor(artifact));

    public void RemoveInventoryTemp(ProvisioningArtifact tempArtifact, string operationId)
        => DeleteIfExists(PathFor(tempArtifact, operationId));

    public bool RemoveManagedDirectoryIfEmpty()
    {
        if (!Directory.Exists(_managedDir))
        {
            return true;
        }
        foreach (string _ in Directory.EnumerateFileSystemEntries(_managedDir))
        {
            return false;
        }
        Directory.Delete(_managedDir);
        return true;
    }

    public OwnershipLedger? ReadLedger()
    {
        string path = PathFor(ProvisioningArtifact.Ledger);
        if (!File.Exists(path))
        {
            return null;
        }
        string json = File.ReadAllText(path, Encoding.UTF8);
        OwnershipLedgerValidationResult validated = OwnershipLedgerValidator.Validate(json);
        return validated.IsValid ? ProvisioningLedgerJson.Deserialize(json) : null;
    }

    public void StageLedgerTemp(string operationId, OwnershipLedger ledger)
        => WriteDurable(PathFor(ProvisioningArtifact.TempLedger, operationId), ProvisioningLedgerJson.Serialize(ledger));

    public void ReplaceLedgerFromTemp(string operationId)
        => AtomicReplace(PathFor(ProvisioningArtifact.TempLedger, operationId), PathFor(ProvisioningArtifact.Ledger));

    public void RemoveLedgerTemp(string operationId) => DeleteIfExists(PathFor(ProvisioningArtifact.TempLedger, operationId));

    public ProvisioningTransactionState ReadTransactionState()
    {
        string path = PathFor(ProvisioningArtifact.TransactionMarker);
        if (!File.Exists(path))
        {
            return ProvisioningTransactionState.Idle;
        }
        string token = File.ReadAllText(path, Encoding.UTF8).Trim();
        return ProvisioningLedgerJson.ParseTransactionState(token) ?? ProvisioningTransactionState.Preparing;
    }

    public void WriteTransactionState(ProvisioningTransactionState state, string operationId)
        => WriteDurable(PathFor(ProvisioningArtifact.TransactionMarker), ProvisioningLedgerJson.TransactionToken(state));

    public void RemoveTransactionArtifact() => DeleteIfExists(PathFor(ProvisioningArtifact.TransactionMarker));

    private static void WriteDurable(string path, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    private static void AtomicReplace(string tempPath, string finalPath)
    {
        if (File.Exists(finalPath))
        {
            // Same-volume atomic replace.
            File.Replace(tempPath, finalPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, finalPath);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string Sha256HexOfBytes(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(64);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

// Real Windows ACL adapter: inspects/applies a protected DACL with the closed
// trusted-SID owner set. Compiled but not invoked live this cycle.
internal sealed class OsProvisioningAcl : IProvisioningAcl
{
    private readonly string _managedDir;

    public OsProvisioningAcl() : this(string.Empty)
    {
    }

    internal OsProvisioningAcl(string managedDir) => _managedDir = managedDir;

    private string TargetPath(ProvisioningArtifact artifact) => artifact switch
    {
        ProvisioningArtifact.ManagedDirectory => _managedDir,
        _ => _managedDir,
    };

    public AclDescriptor Inspect(ProvisioningArtifact artifact)
    {
        string path = TargetPath(artifact);
        var info = new DirectoryInfo(path);
        DirectorySecurity sec = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);

        IdentityReference ownerRef = sec.GetOwner(typeof(SecurityIdentifier))!;
        string ownerSid = ownerRef.Value;
        bool protectedDacl = sec.AreAccessRulesProtected;

        var entries = new List<AclEntry>();
        foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }
            string sid = rule.IdentityReference.Value;
            bool write = (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Modify | FileSystemRights.FullControl)) != 0;
            entries.Add(new AclEntry(sid, write ? AclAccess.Write : AclAccess.Read));
        }
        return new AclDescriptor(ownerSid, entries, protectedDacl);
    }

    public void Apply(AclDescriptor descriptor, ProvisioningArtifact artifact)
    {
        string path = TargetPath(artifact);
        if (!Directory.Exists(path))
        {
            return;
        }
        var info = new DirectoryInfo(path);

        // Pass 1: persist the DACL (protection + explicit ACEs) on its own. This
        // section is always writable by the directory owner, so it commits even
        // in an unprivileged context.
        DirectorySecurity dacl = info.GetAccessControl(AccessControlSections.Access);
        dacl.SetAccessRuleProtection(isProtected: descriptor.ProtectedDacl, preserveInheritance: false);
        foreach (FileSystemAccessRule existing in dacl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            dacl.RemoveAccessRule(existing);
        }
        foreach (AclEntry entry in descriptor.Entries)
        {
            var sid = new SecurityIdentifier(entry.Sid);
            FileSystemRights rights = entry.Access == AclAccess.Write
                ? FileSystemRights.FullControl
                : FileSystemRights.ReadAndExecute;
            dacl.AddAccessRule(new FileSystemAccessRule(
                sid, rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        info.SetAccessControl(dacl);

        // Pass 2: attempt to set the owner in an isolated Owner-only persist so a
        // privilege failure (e.g. assigning LocalSystem without elevation) cannot
        // roll back the DACL committed above. The future elevated helper supplies
        // the privilege; unprivileged/test contexts leave the existing owner.
        try
        {
            var ownerOnly = new DirectorySecurity();
            ownerOnly.SetOwner(new SecurityIdentifier(descriptor.OwnerSid));
            info.SetAccessControl(ownerOnly);
        }
        catch (InvalidOperationException)
        {
            // Privileged owner assignment requires the elevated helper.
        }
        catch (UnauthorizedAccessException)
        {
            // Insufficient privilege to change ownership in this context.
        }
    }
}

// Deterministic, bounded ledger JSON (de)serialization using the EXACT ledger
// field names + transaction-state tokens. It round-trips through the contract's
// OwnershipLedgerValidator on read. No dynamic types, comments, or extra fields.
internal static class ProvisioningLedgerJson
{
    public static string Serialize(OwnershipLedger l)
    {
        var buffer = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("ledgerSchemaVersion", l.LedgerSchemaVersion);
            w.WriteString("productOwnershipMarker", l.ProductOwnershipMarker);
            w.WriteString("managedFeatureId", l.ManagedFeatureId);
            w.WriteNumber("currentGeneration", l.CurrentGeneration);
            w.WriteString("currentInventorySha256", l.CurrentInventorySha256);
            if (l.PreviousGeneration is int pg)
            {
                w.WriteNumber("previousGeneration", pg);
            }
            else
            {
                w.WriteNull("previousGeneration");
            }
            if (l.PreviousInventorySha256 is string ph)
            {
                w.WriteString("previousInventorySha256", ph);
            }
            else
            {
                w.WriteNull("previousInventorySha256");
            }
            w.WriteString("transactionState", TransactionToken(l.TransactionState));
            w.WriteStartArray("ownedFileNames");
            foreach (string n in l.OwnedFileNames)
            {
                w.WriteStringValue(n);
            }
            w.WriteEndArray();
            w.WriteNumber("aclPolicyVersion", l.AclPolicyVersion);
            w.WriteString("createdUtc", l.CreatedUtc);
            w.WriteString("updatedUtc", l.UpdatedUtc);
            w.WriteString("lastOperationId", l.LastOperationId);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static OwnershipLedger Deserialize(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;
        int? prevGen = r.TryGetProperty("previousGeneration", out JsonElement pgEl) && pgEl.ValueKind == JsonValueKind.Number
            ? pgEl.GetInt32() : null;
        string? prevHash = r.TryGetProperty("previousInventorySha256", out JsonElement phEl) && phEl.ValueKind == JsonValueKind.String
            ? phEl.GetString() : null;
        var owned = new List<string>();
        foreach (JsonElement e in r.GetProperty("ownedFileNames").EnumerateArray())
        {
            owned.Add(e.GetString()!);
        }
        return new OwnershipLedger(
            r.GetProperty("ledgerSchemaVersion").GetInt32(),
            r.GetProperty("productOwnershipMarker").GetString()!,
            r.GetProperty("managedFeatureId").GetString()!,
            r.GetProperty("currentGeneration").GetInt32(),
            r.GetProperty("currentInventorySha256").GetString()!,
            prevGen,
            prevHash,
            ParseTransactionState(r.GetProperty("transactionState").GetString()!) ?? ProvisioningTransactionState.Idle,
            owned,
            r.GetProperty("aclPolicyVersion").GetInt32(),
            r.GetProperty("createdUtc").GetString()!,
            r.GetProperty("updatedUtc").GetString()!,
            r.GetProperty("lastOperationId").GetString()!);
    }

    public static string TransactionToken(ProvisioningTransactionState state) => state switch
    {
        ProvisioningTransactionState.Idle => "idle",
        ProvisioningTransactionState.Preparing => "preparing",
        ProvisioningTransactionState.InventoryCommitted => "inventoryCommitted",
        ProvisioningTransactionState.LedgerCommitted => "ledgerCommitted",
        ProvisioningTransactionState.Done => "done",
        _ => "idle",
    };

    public static ProvisioningTransactionState? ParseTransactionState(string token) => token switch
    {
        "idle" => ProvisioningTransactionState.Idle,
        "preparing" => ProvisioningTransactionState.Preparing,
        "inventoryCommitted" => ProvisioningTransactionState.InventoryCommitted,
        "ledgerCommitted" => ProvisioningTransactionState.LedgerCommitted,
        "done" => ProvisioningTransactionState.Done,
        _ => null,
    };
}
#endif
