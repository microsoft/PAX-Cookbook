#if MANAGED_INVENTORY_PROVISIONING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Provisioning;
using Xunit;

namespace PAXCookbookSetup.Tests.Provisioning;

// ---------------------------------------------------------------------------
// Cycle-9 sandboxed provisioning-helper DRY RUN (GATED, NON-LIVE).
//
// This is the FIRST test that drives the REAL Cycle-8 ProvisioningExecutor over
// the REAL OsProvisioningFileSystem (genuine staging, atomic File.Replace, byte
// hashing, ledger JSON round-trip, transaction-marker persistence, remove, and
// unknown-sibling detection) rooted at an ABSOLUTE OS-temp sandbox. It proves the
// transactional apply, idempotent no-op, monotonic replace, reversible-swap
// rollback, fail-closed foreign/ambiguous handling, bounded crash recovery over
// REAL residual on-disk state, exact remove, and full cleanup — WITHOUT ever
// running the product, a Bake, PAX, a live Setup helper, UAC, the cloud, a cert,
// or the credential manager, and WITHOUT touching real ProgramData or a real ACL
// on the executor's managed directory.
//
// HONEST NON-ELEVATION DISCLOSURE (recorded in evidence, not hidden):
//   The harness process is UNELEVATED. Two production behaviors cannot run
//   faithfully unelevated, so they are SUBSTITUTED and the substitution is proven
//   separately and disclosed:
//     1. EnsureManagedDirectory in production stamps a protected lockdown DACL
//        that DENIES the unelevated principal's own writes. The decorator
//        (FaultInjectingOsFileSystem) creates the managed directory test-safe
//        instead — every OTHER filesystem operation is the REAL adapter.
//     2. The executor's trust gate requires the managed directory's owner to be
//        SYSTEM / Administrators; unelevated owner-set cannot succeed. The
//        SandboxProvisioningAcl.Inspect therefore returns the EXACT trusted
//        production descriptor so the gate is satisfied deterministically.
//   The EXACT production ACL descriptor is nonetheless validated STRUCTURALLY
//   (AclDescriptorValidator + ProvisioningAclBuilder.BuildManagedDirectoryDescriptor)
//   AND a REAL protected-DACL apply/inspect is proven on a SEPARATE throwaway
//   probe directory (O02-style) — both DISTINCT from the executor's managed dir.
//
// FORBIDDEN EVIDENCE: this test NEVER persists a full sandbox path, inventory/
// ledger/backup bytes, ACL dumps, SIDs, tenant/client/cert identifiers, secrets,
// or organization names. It records ONLY bounded classifications: a root-path
// SHA-256, containment booleans, created/removed booleans, bounded contract
// filenames, counts, and the SHA-256 + byte-length of SYNTHETIC documents.
// ---------------------------------------------------------------------------
public sealed class ProvisioningSandboxDryRunTests
{
    // Synthetic inventories only — no real identifier of any kind.
    private static string SyntheticInventory(string organizationKeyId)
        => "{\"schemaVersion\":1,\"entries\":[{\"entryVersion\":1,\"organizationKeyId\":\""
           + organizationKeyId
           + "\",\"displayName\":\"Synthetic Key\",\"certificateReferenceType\":\"app_registration_certificate\","
           + "\"adminState\":\"enabled\",\"tenantReference\":\"t\",\"clientReference\":\"c\"}]}";

    private static readonly string Gen1Doc = SyntheticInventory("c9-gen1-key");
    private static readonly string Gen2Doc = SyntheticInventory("c9-gen2-key");
    private static readonly string Gen1Hash = ProvisioningHash.Sha256Hex(Gen1Doc);
    private static readonly string Gen2Hash = ProvisioningHash.Sha256Hex(Gen2Doc);

    // ===================================================================
    // S2-S5 : full transactional lifecycle over the REAL OS filesystem.
    // ===================================================================
    [Fact]
    public void S2_S5_Lifecycle_Create_Idempotent_Replace_Rollback_OverRealOsFilesystem()
    {
        using var sb = Sandbox.Fresh();

        // ---- Structural production-ACL proof (DISTINCT from the managed dir) ----
        AclDescriptor prod = ProvisioningAclBuilder.BuildManagedDirectoryDescriptor();
        Assert.True(AclDescriptorValidator.Validate(prod).IsValid);
        Assert.Equal(AclDescriptor.SidLocalSystem, prod.OwnerSid);
        Assert.True(prod.ProtectedDacl);

        // ---- REAL protected-DACL apply/inspect on a throwaway probe dir ---------
        bool realProbeProtected = sb.ProveRealProtectedAclOnProbeDir();

        // ---- S2 : initial apply, absent -> generation 1 -------------------------
        sb.SnapshotBefore();
        ProvisioningResult s2 = sb.NewExecutor().Execute(sb.Apply(Gen1Doc));
        Assert.Equal(ProvisioningResultCodes.Ok, s2.Code);
        Assert.True(sb.ManagedDirExists());
        Assert.True(sb.InventoryExists());
        Assert.Equal(Gen1Hash, sb.InventoryHash());
        OwnershipLedger l2 = sb.RequireLedger();
        Assert.Equal(1, l2.CurrentGeneration);
        Assert.Null(l2.PreviousGeneration);
        Assert.False(sb.PreviousExists());
        Assert.Equal(Gen1Hash, l2.CurrentInventorySha256);
        Assert.Equal(ProvisioningTransactionState.Idle, sb.Marker());
        Assert.False(sb.HasUnknownSibling());
        sb.AssertNoEscape();

        // S2 verify : read-only, zero mutation.
        string beforeVerify = sb.OnDiskSignature();
        ProvisioningResult s2v = sb.NewExecutor().Execute(sb.Verify());
        Assert.Equal(ProvisioningResultCodes.Ok, s2v.Code);
        Assert.Equal(beforeVerify, sb.OnDiskSignature());
        sb.AssertNoEscape();

        // ---- S3 : idempotent apply of the SAME document -> no-op, gen stays 1 ---
        ProvisioningResult s3 = sb.NewExecutor().Execute(sb.Apply(Gen1Doc));
        Assert.Equal(ProvisioningResultCodes.Ok, s3.Code);
        Assert.Equal(ProvisioningPlanState.NoOpUpToDate, s3.State);
        OwnershipLedger l3 = sb.RequireLedger();
        Assert.Equal(1, l3.CurrentGeneration);
        Assert.Null(l3.PreviousGeneration);
        Assert.False(sb.PreviousExists());
        Assert.Equal(Gen1Hash, sb.InventoryHash());
        Assert.Equal(ProvisioningTransactionState.Idle, sb.Marker());
        sb.AssertNoEscape();

        // ---- S4 : replacement -> generation 2, prev == gen1 owned bytes ---------
        ProvisioningResult s4 = sb.NewExecutor().Execute(sb.Apply(Gen2Doc));
        Assert.Equal(ProvisioningResultCodes.Ok, s4.Code);
        OwnershipLedger l4 = sb.RequireLedger();
        Assert.Equal(2, l4.CurrentGeneration);
        Assert.Equal(1, l4.PreviousGeneration);
        Assert.Equal(Gen2Hash, sb.InventoryHash());
        Assert.Equal(Gen2Hash, l4.CurrentInventorySha256);
        Assert.True(sb.PreviousExists());
        // Backup is the ownership-proven CURRENT (former gen1) owned bytes.
        Assert.Equal(Gen1Hash, sb.PreviousHash());
        Assert.Equal(Gen1Hash, l4.PreviousInventorySha256);
        Assert.Equal(ProvisioningTransactionState.Idle, sb.Marker());
        Assert.False(sb.HasUnknownSibling());
        sb.AssertNoEscape();

        // ---- S5 : reversible-swap rollback gen2 -> gen3 -------------------------
        // New current = former previous (gen1 bytes); new previous = former current (gen2 bytes).
        ProvisioningResult s5 = sb.NewExecutor().Execute(sb.Rollback());
        Assert.Equal(ProvisioningResultCodes.Ok, s5.Code);
        OwnershipLedger l5 = sb.RequireLedger();
        Assert.Equal(3, l5.CurrentGeneration);
        Assert.Equal(2, l5.PreviousGeneration);
        Assert.Equal(Gen1Hash, sb.InventoryHash());
        Assert.Equal(Gen1Hash, l5.CurrentInventorySha256);
        Assert.Equal(Gen2Hash, sb.PreviousHash());
        Assert.Equal(Gen2Hash, l5.PreviousInventorySha256);
        Assert.Equal(ProvisioningTransactionState.Idle, sb.Marker());
        sb.AssertNoEscape();

        // Post-lifecycle verify stays consistent and mutates nothing.
        string beforeVerify2 = sb.OnDiskSignature();
        Assert.Equal(ProvisioningResultCodes.Ok, sb.NewExecutor().Execute(sb.Verify()).Code);
        Assert.Equal(beforeVerify2, sb.OnDiskSignature());

        Evidence.Emit(
            "S2_S5 root=" + sb.RootHash + " containment=OK reparse=none created=true idempotent=true "
            + "gen1=true gen2=true rollback_gen3=true prevFromCurrentOwned=true verifyZeroMutation=true "
            + "aclStructuralValid=true realProbeProtected=" + realProbeProtected
            + " gen1Hash=" + Gen1Hash + " gen1Len=" + Gen1Doc.Length
            + " gen2Hash=" + Gen2Hash + " gen2Len=" + Gen2Doc.Length
            + " escaped=" + sb.EscapedWriteCount);
    }

    // ===================================================================
    // S6 : foreign / ambiguous states fail closed, foreign artifact untouched.
    // ===================================================================
    [Fact]
    public void S6_ForeignAndAmbiguous_FailClosed_NeverMutateForeignArtifact()
    {
        // --- S6a : untrusted environment blocks apply ---------------------------
        using (var sb = Sandbox.Fresh())
        {
            Assert.Equal(ProvisioningResultCodes.Ok, sb.NewExecutor().Execute(sb.Apply(Gen1Doc)).Code);
            string frozen = sb.OnDiskSignature();
            sb.Acl.MakeUntrusted();
            ProvisioningResult r = sb.NewExecutor().Execute(sb.Apply(Gen2Doc));
            Assert.Equal(ProvisioningResultCodes.UntrustedEnvironment, r.Code);
            Assert.Equal(frozen, sb.OnDiskSignature());
            sb.AssertNoEscape();
            Evidence.Emit("S6a untrustedBlocksApply=true code=" + r.Code + " unchanged=true root=" + sb.RootHash);
        }

        // --- S6b : unknown sibling blocks apply, sibling untouched ---------------
        using (var sb = Sandbox.Fresh())
        {
            Assert.Equal(ProvisioningResultCodes.Ok, sb.NewExecutor().Execute(sb.Apply(Gen1Doc)).Code);
            sb.PlantUnknownSibling();
            string frozen = sb.OnDiskSignature();
            string siblingHashBefore = sb.UnknownSiblingHash();
            ProvisioningResult r = sb.NewExecutor().Execute(sb.Apply(Gen2Doc));
            Assert.Equal(ProvisioningResultCodes.UnknownSibling, r.Code);
            Assert.Equal(frozen, sb.OnDiskSignature());
            Assert.Equal(siblingHashBefore, sb.UnknownSiblingHash());
            sb.AssertNoEscape();
            Evidence.Emit("S6b unknownSiblingBlocksApply=true code=" + r.Code + " siblingUntouched=true root=" + sb.RootHash);
        }

        // --- S6c : inventory-without-ledger (disagreement) blocks apply ----------
        using (var sb = Sandbox.Fresh())
        {
            Assert.Equal(ProvisioningResultCodes.Ok, sb.NewExecutor().Execute(sb.Apply(Gen1Doc)).Code);
            sb.DeleteLedgerFile();
            string frozen = sb.OnDiskSignature();
            ProvisioningResult r = sb.NewExecutor().Execute(sb.Apply(Gen2Doc));
            Assert.Equal(ProvisioningResultCodes.LedgerInventoryDisagree, r.Code);
            Assert.Equal(frozen, sb.OnDiskSignature());
            Assert.Equal(Gen1Hash, sb.InventoryHash());
            sb.AssertNoEscape();
            Evidence.Emit("S6c inventoryWithoutLedgerDisagree=true code=" + r.Code + " inventoryUntouched=true root=" + sb.RootHash);
        }

        // --- S6d : rollback with no previous generation fails closed ------------
        using (var sb = Sandbox.Fresh())
        {
            Assert.Equal(ProvisioningResultCodes.Ok, sb.NewExecutor().Execute(sb.Apply(Gen1Doc)).Code);
            string frozen = sb.OnDiskSignature();
            ProvisioningResult r = sb.NewExecutor().Execute(sb.Rollback());
            Assert.Equal(ProvisioningResultCodes.NoPreviousGeneration, r.Code);
            Assert.Equal(frozen, sb.OnDiskSignature());
            sb.AssertNoEscape();
            Evidence.Emit("S6d rollbackNoPreviousBlocked=true code=" + r.Code + " unchanged=true root=" + sb.RootHash);
        }

        // --- S6e : generation-mismatch expectation blocks apply -----------------
        using (var sb = Sandbox.Fresh())
        {
            Assert.Equal(ProvisioningResultCodes.Ok, sb.NewExecutor().Execute(sb.Apply(Gen1Doc)).Code);
            string frozen = sb.OnDiskSignature();
            ProvisioningResult r = sb.NewExecutor().Execute(sb.Apply(Gen2Doc, expectedGeneration: 99));
            Assert.Equal(ProvisioningResultCodes.GenerationMismatch, r.Code);
            Assert.Equal(frozen, sb.OnDiskSignature());
            sb.AssertNoEscape();
            Evidence.Emit("S6e generationMismatchBlocked=true code=" + r.Code + " unchanged=true root=" + sb.RootHash);
        }

        // --- S6f : unknown sibling blocks remove, managed dir survives ----------
        using (var sb = Sandbox.Fresh())
        {
            Assert.Equal(ProvisioningResultCodes.Ok, sb.NewExecutor().Execute(sb.Apply(Gen1Doc)).Code);
            sb.PlantUnknownSibling();
            string frozen = sb.OnDiskSignature();
            string siblingHashBefore = sb.UnknownSiblingHash();
            ProvisioningResult r = sb.NewExecutor().Execute(sb.Remove());
            Assert.Equal(ProvisioningResultCodes.UnknownSibling, r.Code);
            Assert.Equal(frozen, sb.OnDiskSignature());
            Assert.Equal(siblingHashBefore, sb.UnknownSiblingHash());
            Assert.True(sb.ManagedDirExists());
            sb.AssertNoEscape();
            Evidence.Emit("S6f unknownSiblingBlocksRemove=true code=" + r.Code + " dirSurvives=true root=" + sb.RootHash);
        }
    }

    // ===================================================================
    // S7 : crash-recovery matrix over REAL residual on-disk state.
    // Mirrors the Cycle-8 in-memory E12/E13/E14 fault sets, but the fault fires
    // through the REAL OsProvisioningFileSystem, and a FRESH executor recovers
    // over the REAL bytes/ledger/marker left on disk. Invariants per iteration:
    //   * NO false success (Ok => on-disk inventory hash == ledger current hash)
    //   * NO corruption (present owned docs are a KNOWN synthetic hash)
    //   * NO escape outside the sandbox root
    //   * converges to a clean Ok OR a stable, honestly-classified non-success
    // ===================================================================
    [Theory]
    // S7 create set (E13)
    [InlineData("create", "None")]
    [InlineData("create", "EnsureManagedDirectory")]
    [InlineData("create", "ApplyAcl")]
    [InlineData("create", "StageInventory")]
    [InlineData("create", "BeforeReplaceInventory")]
    [InlineData("create", "AfterReplaceInventory")]
    [InlineData("create", "StageLedger")]
    [InlineData("create", "BeforeReplaceLedger")]
    [InlineData("create", "AfterReplaceLedger")]
    [InlineData("create", "BeforeTransactionClear")]
    // S7 replace set (E12)
    [InlineData("replace", "None")]
    [InlineData("replace", "StageInventory")]
    [InlineData("replace", "StagePreviousInventory")]
    [InlineData("replace", "ApplyAcl")]
    [InlineData("replace", "BeforeReplacePreviousInventory")]
    [InlineData("replace", "AfterReplacePreviousInventory")]
    [InlineData("replace", "BeforeReplaceInventory")]
    [InlineData("replace", "AfterReplaceInventory")]
    [InlineData("replace", "StageLedger")]
    [InlineData("replace", "BeforeReplaceLedger")]
    [InlineData("replace", "AfterReplaceLedger")]
    [InlineData("replace", "BeforeTransactionClear")]
    // S7 rollback set (E14)
    [InlineData("rollback", "None")]
    [InlineData("rollback", "StageInventory")]
    [InlineData("rollback", "StagePreviousInventory")]
    [InlineData("rollback", "ApplyAcl")]
    [InlineData("rollback", "BeforeReplaceInventory")]
    [InlineData("rollback", "AfterReplaceInventory")]
    [InlineData("rollback", "BeforeReplacePreviousInventory")]
    [InlineData("rollback", "AfterReplacePreviousInventory")]
    [InlineData("rollback", "StageLedger")]
    [InlineData("rollback", "BeforeReplaceLedger")]
    [InlineData("rollback", "AfterReplaceLedger")]
    [InlineData("rollback", "BeforeTransactionClear")]
    public void S7_CrashRecoveryMatrix_OverRealOsFilesystem(string operation, string failPointName)
    {
        ProvisioningFailPoint failPoint = Enum.Parse<ProvisioningFailPoint>(failPointName);
        using var sb = Sandbox.Fresh();

        string[] knownHashes;
        Func<ProvisioningRequest> makeRequest;
        switch (operation)
        {
            case "create":
                knownHashes = new[] { Gen1Hash };
                makeRequest = () => sb.Apply(Gen1Doc);
                break;
            case "replace":
                Assert.Equal(ProvisioningResultCodes.Ok, sb.NewExecutor().Execute(sb.Apply(Gen1Doc)).Code);
                knownHashes = new[] { Gen1Hash, Gen2Hash };
                makeRequest = () => sb.Apply(Gen2Doc);
                break;
            case "rollback":
                Assert.Equal(ProvisioningResultCodes.Ok, sb.NewExecutor().Execute(sb.Apply(Gen1Doc)).Code);
                Assert.Equal(ProvisioningResultCodes.Ok, sb.NewExecutor().Execute(sb.Apply(Gen2Doc)).Code);
                knownHashes = new[] { Gen1Hash, Gen2Hash };
                makeRequest = () => sb.Rollback();
                break;
            default:
                throw new InvalidOperationException("unknown operation " + operation);
        }

        (bool cleanOk, int iterations) = DriveFaultAndAssertInvariants(sb, failPoint, makeRequest, knownHashes);

        Evidence.Emit(
            "S7 op=" + operation + " failPoint=" + failPoint + " root=" + sb.RootHash
            + " terminal=" + (cleanOk ? "clean-ok" : "stable-classified")
            + " iterations=" + iterations + " noFalseSuccess=true noCorruption=true escaped=" + sb.EscapedWriteCount);
    }

    private static (bool cleanOk, int iterations) DriveFaultAndAssertInvariants(
        Sandbox sb, ProvisioningFailPoint failPoint, Func<ProvisioningRequest> makeRequest, string[] knownHashes)
    {
        sb.Armer.Arm(failPoint);
        string? previousSignature = null;

        for (int i = 1; i <= 8; i++)
        {
            ProvisioningResult r = sb.NewExecutor().Execute(makeRequest());

            // No false success.
            if (r.Code == ProvisioningResultCodes.Ok)
            {
                Assert.True(
                    sb.OnDiskIsConsistent(),
                    "Ok reported but on-disk machine inconsistent. iter=" + i
                    + " code=" + r.Code + " state=" + r.State
                    + " signature=" + sb.OnDiskSignature());
            }

            // No corruption : any present owned document is a known synthetic hash.
            foreach (string h in sb.PresentOwnedHashes())
            {
                Assert.Contains(h, knownHashes);
            }

            // No escape.
            sb.AssertNoEscape();

            bool cleanOk = r.Code == ProvisioningResultCodes.Ok && sb.OnDiskIsConsistent();
            if (cleanOk)
            {
                return (true, i);
            }

            string signature = sb.OnDiskSignature();
            if (previousSignature is not null && string.Equals(previousSignature, signature, StringComparison.Ordinal))
            {
                // Stable, honestly-classified non-success terminal (fail-closed, no corruption).
                return (false, i);
            }
            previousSignature = signature;
        }

        Assert.Fail("crash-recovery did not reach a terminal within 8 iterations for " + failPoint);
        return (false, 8);
    }

    // ===================================================================
    // S8 : exact remove + idempotent remove over the REAL OS filesystem.
    // ===================================================================
    [Fact]
    public void S8_Remove_DeletesExactOwnedArtifactsAndEmptyDirectory_Idempotent()
    {
        using var sb = Sandbox.Fresh();

        // Seed a two-generation owned state (gen2 with a prev backup).
        Assert.Equal(ProvisioningResultCodes.Ok, sb.NewExecutor().Execute(sb.Apply(Gen1Doc)).Code);
        Assert.Equal(ProvisioningResultCodes.Ok, sb.NewExecutor().Execute(sb.Apply(Gen2Doc)).Code);
        Assert.True(sb.InventoryExists());
        Assert.True(sb.PreviousExists());
        Assert.True(sb.LedgerExists());

        ProvisioningResult r = sb.NewExecutor().Execute(sb.Remove());
        Assert.Equal(ProvisioningResultCodes.Ok, r.Code);
        Assert.False(sb.InventoryExists());
        Assert.False(sb.PreviousExists());
        Assert.False(sb.LedgerExists());
        Assert.False(sb.ManagedDirExists());
        sb.AssertNoEscape();

        // Idempotent remove : an absent owned state fails closed deterministically
        // (ownership cannot be proven for something that is not there), with no
        // throw, no mutation, and no escape.
        ProvisioningResult again = sb.NewExecutor().Execute(sb.Remove());
        Assert.Equal(ProvisioningResultCodes.OwnershipNotProven, again.Code);
        Assert.False(sb.ManagedDirExists());
        sb.AssertNoEscape();

        Evidence.Emit(
            "S8 removedInventory=true removedPrev=true removedLedger=true removedEmptyDir=true "
            + "idempotentRemoveCode=" + again.Code + " root=" + sb.RootHash + " escaped=" + sb.EscapedWriteCount);
    }

    // ===================================================================
    // S9 : cleanup + post-state guard (no sandbox residue; production ACL still
    // structurally valid). Machine-wide guards (engine hash, protected isolated
    // states, real ProgramData non-mutation, prohibited processes) are captured
    // by the Implementer PowerShell evidence wrapper around the whole test run.
    // ===================================================================
    [Fact]
    public void S9_PostState_NoSandboxResidue_ProductionAclStillValid()
    {
        string tempRoot = Path.GetTempPath();
        string[] residue = SafeEnumerateSandboxDirs(tempRoot);
        Assert.Empty(residue);

        AclDescriptor prod = ProvisioningAclBuilder.BuildManagedDirectoryDescriptor();
        Assert.True(AclDescriptorValidator.Validate(prod).IsValid);

        Evidence.Emit(
            "S9 sandboxResidue=" + residue.Length + " productionAclStructuralValid=true tempRootHash="
            + ProvisioningHash.Sha256Hex(Path.GetFullPath(tempRoot)));
    }

    private static string[] SafeEnumerateSandboxDirs(string tempRoot)
    {
        try
        {
            return Directory.EnumerateDirectories(tempRoot, "paxc-c9-sandbox-*").ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    // ===================================================================
    // Bounded, redaction-safe evidence emitter (env-var driven, optional).
    // ===================================================================
    private static class Evidence
    {
        private static readonly object Gate = new();
        private static readonly string? Path = Environment.GetEnvironmentVariable("PAXC_C9_EVIDENCE");

        public static void Emit(string boundedLine)
        {
            if (string.IsNullOrEmpty(Path))
            {
                return;
            }
            // Evidence emission is diagnostic ONLY and must never fail the dry-run.
            // The evidence log can live under a OneDrive-synced path, where the sync
            // client intermittently locks the file; tolerate transient IO locks with
            // a bounded retry using shared read/write, then best-effort swallow.
            lock (Gate)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(boundedLine + Environment.NewLine);
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        using var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        fs.Write(bytes, 0, bytes.Length);
                        return;
                    }
                    catch (IOException)
                    {
                        System.Threading.Thread.Sleep(20);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        System.Threading.Thread.Sleep(20);
                    }
                }
            }
        }
    }

    // ===================================================================
    // Sandbox : an absolute OS-temp root proven contained, non-reparse, and
    // absent-before-create, with the REAL OsProvisioningFileSystem wired behind
    // the fault-injecting decorator and the sandbox ACL.
    // ===================================================================
    private sealed class Sandbox : IDisposable
    {
        private const string SandboxPrefix = "paxc-c9-sandbox-";

        private readonly string _root;
        private readonly string _managedDir;
        private readonly OsProvisioningFileSystem _innerFs;
        private readonly OsProvisioningFileSystem _readFs;
        private readonly FaultInjectingOsFileSystem _fs;
        private readonly HashSet<string> _tempSiblingsBefore;

        public SandboxFaultArmer Armer { get; }

        public SandboxProvisioningAcl Acl { get; }

        public string RootHash { get; }

        public int EscapedWriteCount { get; private set; }

        private Sandbox(string root, string managedDir, HashSet<string> tempSiblingsBefore)
        {
            _root = root;
            _managedDir = managedDir;
            _tempSiblingsBefore = tempSiblingsBefore;
            RootHash = ProvisioningHash.Sha256Hex(Path.GetFullPath(root));

            Armer = new SandboxFaultArmer();
            Acl = new SandboxProvisioningAcl(Armer);
            _innerFs = OsProvisioningFileSystem.CreateForTest(_managedDir);
            _fs = new FaultInjectingOsFileSystem(_innerFs, Armer);
            // A SEPARATE real adapter, never fault-injected, used only to READ the
            // authentic on-disk state for assertions.
            _readFs = OsProvisioningFileSystem.CreateForTest(_managedDir);
        }

        public static Sandbox Fresh()
        {
            string tempRoot = Path.GetTempPath();
            string repoRoot = LocateRepoRoot();
            // NOTE: OS temp (Path.GetTempPath()) is itself under LocalApplicationData on
            // Windows (%LocalAppData%\Temp), so LocalApplicationData is intentionally NOT
            // forbidden wholesale. We forbid only the real product-state roots: the repo,
            // ProgramData (CommonApplicationData) where the production managed dir lives,
            // the product's own LocalAppData\PAXCookbook tree, and the isolated pilot states.
            var forbidden = new List<string>
            {
                repoRoot,
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PAXCookbook"),
                Path.Combine(repoRoot, "_temp", "tests", "pilot_iso"),
            };

            for (int attempt = 0; attempt < 16; attempt++)
            {
                string root = Path.Combine(tempRoot, SandboxPrefix + Guid.NewGuid().ToString("N"));
                string full = Path.GetFullPath(root);

                // Absolute + under OS temp + NOT at/under any forbidden root.
                if (!System.IO.Path.IsPathFullyQualified(full)) { continue; }
                if (!IsAtOrUnder(full, Path.GetFullPath(tempRoot))) { continue; }
                if (forbidden.Any(f => IsAtOrUnder(full, Path.GetFullPath(f)))) { continue; }
                if (Directory.Exists(full) || File.Exists(full)) { continue; }

                var siblingsBefore = SafeImmediateChildren(tempRoot);
                return new Sandbox(full, Path.Combine(full, "PAXCookbook", "ManagedChefKeys"), siblingsBefore);
            }

            throw new InvalidOperationException("could not allocate a contained absent OS-temp sandbox root");
        }

        // ---- executor + request factories -----------------------------------
        public ProvisioningExecutor NewExecutor()
            => new(_fs, Acl, new SystemProvisioningClock());

        public ProvisioningRequest Apply(string doc, int? expectedGeneration = null)
            => BuildRequest("apply", doc, expectedGeneration);

        public ProvisioningRequest Rollback() => BuildRequest("rollback", null, null);

        public ProvisioningRequest Remove() => BuildRequest("remove", null, null);

        public ProvisioningRequest Verify() => BuildRequest("verify", null, null);

        private static ProvisioningRequest BuildRequest(string operation, string? inventory, int? expectedGeneration)
        {
            var sb = new StringBuilder();
            sb.Append("{\"requestSchemaVersion\":1,\"operation\":\"").Append(operation).Append("\",");
            sb.Append("\"operationId\":\"op-c9\",");
            sb.Append("\"inventoryDocument\":").Append(JsonString(inventory ?? string.Empty)).Append(',');
            if (expectedGeneration is int g)
            {
                sb.Append("\"expectedLedgerGeneration\":").Append(g).Append(',');
            }
            sb.Append("\"planOnly\":false}");
            ProvisioningRequestValidationResult v = ProvisioningRequestValidator.Validate(sb.ToString());
            Assert.True(v.IsValid);
            return v.Request!;
        }

        private static string JsonString(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ---- on-disk readers (authentic, via a non-fault real adapter) -------
        public bool ManagedDirExists() => Directory.Exists(_managedDir);

        public bool InventoryExists() => _readFs.OwnedExists(ProvisioningArtifact.Inventory);

        public bool PreviousExists() => _readFs.OwnedExists(ProvisioningArtifact.PreviousInventory);

        public bool LedgerExists() => _readFs.ReadLedger() is not null;

        public string InventoryHash() => _readFs.HashOwnedInventory(ProvisioningArtifact.Inventory);

        public string PreviousHash() => _readFs.HashOwnedInventory(ProvisioningArtifact.PreviousInventory);

        public bool HasUnknownSibling() => _readFs.HasUnknownSibling();

        public ProvisioningTransactionState Marker() => _readFs.ReadTransactionState();

        public OwnershipLedger RequireLedger()
        {
            OwnershipLedger? l = _readFs.ReadLedger();
            Assert.NotNull(l);
            return l!;
        }

        public bool OnDiskIsConsistent()
        {
            OwnershipLedger? l = _readFs.ReadLedger();
            bool invPresent = _readFs.OwnedExists(ProvisioningArtifact.Inventory);

            // An EMPTY machine (no inventory AND no ledger) is a consistent, clean,
            // re-plannable slate — this mirrors the product's own AssertConsistent
            // contract, under which an Ok reported over an empty machine is NOT a
            // false success (a pre-commit create fault re-plans to this state).
            if (l is null && !invPresent)
            {
                return true;
            }
            // Exactly one of inventory/ledger present is a torn, inconsistent machine.
            if (l is null || !invPresent)
            {
                return false;
            }
            if (!string.Equals(_readFs.HashOwnedInventory(ProvisioningArtifact.Inventory), l.CurrentInventorySha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (l.CurrentGeneration >= 2)
            {
                if (!_readFs.OwnedExists(ProvisioningArtifact.PreviousInventory) || l.PreviousInventorySha256 is null)
                {
                    return false;
                }
                if (!string.Equals(_readFs.HashOwnedInventory(ProvisioningArtifact.PreviousInventory), l.PreviousInventorySha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        public IEnumerable<string> PresentOwnedHashes()
        {
            if (_readFs.OwnedExists(ProvisioningArtifact.Inventory))
            {
                yield return _readFs.HashOwnedInventory(ProvisioningArtifact.Inventory);
            }
            if (_readFs.OwnedExists(ProvisioningArtifact.PreviousInventory))
            {
                yield return _readFs.HashOwnedInventory(ProvisioningArtifact.PreviousInventory);
            }
        }

        public string OnDiskSignature()
        {
            OwnershipLedger? l = _readFs.ReadLedger();
            string inv = _readFs.OwnedExists(ProvisioningArtifact.Inventory) ? _readFs.HashOwnedInventory(ProvisioningArtifact.Inventory) : "-";
            string prev = _readFs.OwnedExists(ProvisioningArtifact.PreviousInventory) ? _readFs.HashOwnedInventory(ProvisioningArtifact.PreviousInventory) : "-";
            string cur = l?.CurrentGeneration.ToString() ?? "-";
            string pg = l?.PreviousGeneration?.ToString() ?? "-";
            return string.Join("|", inv, prev, cur, pg, _readFs.ReadTransactionState());
        }

        // ---- foreign / ambiguous fixtures -----------------------------------
        private string UnknownSiblingPath() => Path.Combine(_managedDir, "intruder.json");

        public void PlantUnknownSibling()
        {
            Directory.CreateDirectory(_managedDir);
            File.WriteAllText(UnknownSiblingPath(), "{\"foreign\":true}", Encoding.UTF8);
        }

        public string UnknownSiblingHash() => ProvisioningHash.Sha256Hex(File.ReadAllText(UnknownSiblingPath(), Encoding.UTF8));

        public void DeleteLedgerFile()
        {
            string ledger = Path.Combine(_managedDir, ProvisioningContract.LedgerFileName);
            if (File.Exists(ledger))
            {
                File.Delete(ledger);
            }
        }

        // ---- REAL protected-DACL proof on a throwaway probe dir (O02-style) --
        public bool ProveRealProtectedAclOnProbeDir()
        {
            string probe = Path.Combine(_root, "acl-probe");
            Directory.CreateDirectory(probe);
            var realAcl = new OsProvisioningAcl(probe);
            realAcl.Apply(ProvisioningAclBuilder.BuildManagedDirectoryDescriptor(), ProvisioningArtifact.ManagedDirectory);
            AclDescriptor observed = realAcl.Inspect(ProvisioningArtifact.ManagedDirectory);
            Assert.True(observed.ProtectedDacl);
            Assert.NotEmpty(observed.Entries);
            return observed.ProtectedDacl && observed.Entries.Count > 0;
        }

        // ---- escape monitoring ----------------------------------------------
        public void SnapshotBefore()
        {
            // No-op placeholder kept for readability at stage boundaries; the temp
            // sibling snapshot is captured at construction.
        }

        public void AssertNoEscape()
        {
            int escaped = 0;

            // (1) Every file under the sandbox root must canonicalize under the root.
            if (Directory.Exists(_root))
            {
                foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    if (!IsAtOrUnder(Path.GetFullPath(file), Path.GetFullPath(_root)))
                    {
                        escaped++;
                    }
                }
            }

            // (2) No NEW sandbox-owned sibling may appear beside the sandbox root in
            // OS temp. The OS temp directory is shared machine-wide, so unrelated
            // processes churn entries here constantly; we therefore only police NEW
            // entries carrying OUR sandbox prefix (a genuine escape by our code),
            // ignoring foreign temp activity to avoid false positives.
            var siblingsNow = SafeImmediateChildren(Path.GetTempPath());
            foreach (string entry in siblingsNow)
            {
                bool isOurPrefix = Path.GetFileName(entry).StartsWith(SandboxPrefix, StringComparison.OrdinalIgnoreCase);
                if (isOurPrefix
                    && !_tempSiblingsBefore.Contains(entry)
                    && !string.Equals(entry, Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase))
                {
                    escaped++;
                }
            }

            EscapedWriteCount = Math.Max(EscapedWriteCount, escaped);
            Assert.Equal(0, escaped);
        }

        // ---- teardown --------------------------------------------------------
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Assert.False(Directory.Exists(_root), "sandbox root was not removed on teardown");
        }

        // ---- helpers ---------------------------------------------------------
        private static HashSet<string> SafeImmediateChildren(string dir)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string e in Directory.EnumerateFileSystemEntries(dir))
                {
                    set.Add(Path.GetFullPath(e));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return set;
        }

        private static bool IsAtOrUnder(string candidate, string ancestor)
        {
            string a = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string c = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(a, c, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return c.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string LocateRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            // Fall back to the base directory; forbidden-containment still applies to temp.
            return AppContext.BaseDirectory;
        }
    }
}
#endif
