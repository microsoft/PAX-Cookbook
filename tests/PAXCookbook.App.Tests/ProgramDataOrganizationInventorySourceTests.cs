using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PAXCookbook.App;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// Deterministic tests for the REAL, read-only, trust-validating ProgramData
// ORGANIZATION-KEY INVENTORY SOURCE (Cycle 06).
//
// This cycle replaces ONLY the Cycle-5 disabled zero-I/O production source with
// ProgramDataOrganizationKeyInventorySource, which derives EXACTLY
//   Environment.GetFolderPath(CommonApplicationData) +
//   \PAXCookbook\ManagedChefKeys\organization-key-inventory.json
// enforces path-containment + per-component reparse rejection + closed-SID ACL
// trust + bounded-size + stable-read + strict-UTF-8, and feeds trusted bytes into
// the existing Cycle-5 parser EXACTLY ONCE. Read-only. It never writes, creates,
// deletes, repairs, or ACL-mutates ProgramData, never touches a certificate
// store/private key/service/Graph/PAX/Bake, and cannot be redirected by env,
// command line, HTTP, React, TestIsolation, registry, or caller input in
// production. Tests use an INTERNAL root-override seam that supplies a test root
// under absolute OS temp (NOT reachable from production wiring) plus an internal
// additional-trusted-principal seam so a current-user-owned temp file can
// exercise the real read/decode/parse pipeline. The REAL closed-SID owner/ACL
// policy (only LocalSystem + BuiltinAdministrators trusted) is proven directly
// against the pure ProgramDataOrgInventoryTrustPolicy with synthesized SIDs, so
// no elevation is required.
//
// All filesystem artifacts live ONLY under absolute OS temp; each test applies a
// protective test-owned ACL and deletes the tree afterward. No test touches real
// ProgramData, the registry, a certificate store, a credential vault, a service,
// WAM/Hello, Microsoft Graph, PAX, or a Bake.
public sealed class ProgramDataOrganizationInventorySourceTests : IDisposable
{
    private readonly List<string> _tempRoots = new();
    private static readonly SecurityIdentifier CurrentUser = WindowsIdentity.GetCurrent().User!;
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier BuiltinAdmins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier BuiltinUsers = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);
    // An unresolved / arbitrary SID (never a well-known trusted principal).
    private static readonly SecurityIdentifier UnknownSid = new("S-1-5-21-1111111111-2222222222-3333333333-4001");

    private static IReadOnlySet<SecurityIdentifier> SeamTrustCurrentUser()
        => new HashSet<SecurityIdentifier> { CurrentUser };

    // ---- temp tree helpers (absolute OS temp only) --------------------------

    private string NewTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "paxck6_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SetProtectiveAcl(root);
        _tempRoots.Add(root);
        return root;
    }

    // Disable inheritance and grant FullControl only to the current user + SYSTEM
    // + Administrators, so the DACL a test reads is deterministic regardless of
    // the machine's %TEMP% inheritance.
    private static void SetProtectiveAcl(string dir)
    {
        var di = new DirectoryInfo(dir);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(BuiltinAdmins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
    }

    // Build <root>\PAXCookbook\ManagedChefKeys and (optionally) write the file.
    private static string ManagedDir(string root)
        => Path.Combine(root, "PAXCookbook", "ManagedChefKeys");

    private static string InventoryPath(string root)
        => Path.Combine(ManagedDir(root), "organization-key-inventory.json");

    private static void CreateManagedDir(string root) => Directory.CreateDirectory(ManagedDir(root));

    private static string WriteInventoryBytes(string root, byte[] bytes)
    {
        CreateManagedDir(root);
        string p = InventoryPath(root);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static string WriteInventoryText(string root, string text)
        => WriteInventoryBytes(root, Encoding.UTF8.GetBytes(text));

    private static string ValidDoc(int entries = 1)
    {
        var sb = new StringBuilder();
        sb.Append("{ \"schemaVersion\": 1, \"entries\": [");
        for (int i = 0; i < entries; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{ \"entryVersion\": 1, \"organizationKeyId\": \"org-key-")
              .Append(i)
              .Append("\", \"displayName\": \"Contoso Managed Key\", \"certificateReferenceType\": \"app_registration_certificate\", \"adminState\": \"enabled\", \"tenantReference\": \"tenant-ref\", \"clientReference\": \"client-ref\" }");
        }
        sb.Append("] }");
        return sb.ToString();
    }

    private static void AddAce(string path, SecurityIdentifier sid, FileSystemRights rights, AccessControlType type)
    {
        var fi = new FileInfo(path);
        var sec = fi.GetAccessControl();
        sec.AddAccessRule(new FileSystemAccessRule(sid, rights, type));
        fi.SetAccessControl(sec);
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            return p.ExitCode == 0 && (new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    // ---- gates --------------------------------------------------------------

    private static ManagedChefKeysGateProjection AuthorizedGate()
        => ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.ConfiguredOrganizationManaged(
                MachinePolicyCapability.Enabled, MachinePolicyCapability.Disabled));

    private static ManagedChefKeysGateProjection DeniedGate()
        => ManagedChefKeysGate.Evaluate(MachinePolicyDetection.NotConfigured());

    // A source that counts invocations so a gate short-circuit can be proven.
    private sealed class CountingSource : IOrganizationKeyInventorySource
    {
        private readonly IOrganizationKeyInventorySource _inner;
        public CountingSource(IOrganizationKeyInventorySource inner) => _inner = inner;
        public int InvocationCount { get; private set; }
        public OrganizationInventorySourceResult Load()
        {
            InvocationCount++;
            return _inner.Load();
        }
    }

    // A source that records how many times Load ran, to prove exactly-once read.
    private sealed class OneShotProbe : IOrganizationKeyInventorySource
    {
        private readonly ProgramDataOrganizationKeyInventorySource _real;
        public OneShotProbe(ProgramDataOrganizationKeyInventorySource real) => _real = real;
        public int LoadCount { get; private set; }
        public OrganizationInventorySourceResult Load()
        {
            LoadCount++;
            return _real.Load();
        }
    }

    // =========================================================================
    // A. Authorization short-circuit — source NEVER constructed/invoked
    // =========================================================================

    [Fact] // C01
    public void C01_DeniedGate_SourceNeverInvoked()
    {
        var counting = new CountingSource(new ProgramDataOrganizationKeyInventorySource(NewTempRoot(), SeamTrustCurrentUser()));
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(DeniedGate(), counting);
        Assert.Equal(OrganizationInventoryState.NotAuthorized, p.State);
        Assert.Equal(0, counting.InvocationCount);
        Assert.False(p.InventoryLoaded);
    }

    [Fact] // C02
    public void C02_NullGate_SourceNeverInvoked()
    {
        var counting = new CountingSource(new ProgramDataOrganizationKeyInventorySource(NewTempRoot(), SeamTrustCurrentUser()));
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(null, counting);
        Assert.Equal(OrganizationInventoryState.Unavailable, p.State);
        Assert.Equal(0, counting.InvocationCount);
    }

    // =========================================================================
    // B. Absent directory / file -> not_provisioned
    // =========================================================================

    [Fact] // C03
    public void C03_ManagedDirAbsent_NotProvisioned()
    {
        string root = NewTempRoot(); // no PAXCookbook\ManagedChefKeys created
        var src = new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser());
        Assert.Equal(OrganizationInventorySourceKind.NotProvisioned, src.Load().Kind);
    }

    [Fact] // C04
    public void C04_FileAbsent_NotProvisioned()
    {
        string root = NewTempRoot();
        CreateManagedDir(root); // dir present, file absent
        var src = new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser());
        Assert.Equal(OrganizationInventorySourceKind.NotProvisioned, src.Load().Kind);
    }

    // =========================================================================
    // C. Root missing -> unavailable
    // =========================================================================

    [Fact] // C05
    public void C05_RootMissing_Unavailable()
    {
        string root = Path.Combine(Path.GetTempPath(), "paxck6_missing_" + Guid.NewGuid().ToString("N"));
        var src = new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser());
        Assert.Equal(OrganizationInventorySourceKind.Unavailable, src.Load().Kind);
    }

    [Fact] // C06
    public void C06_RootNotFullyQualified_Unavailable()
    {
        var src = new ProgramDataOrganizationKeyInventorySource("relative\\path", SeamTrustCurrentUser());
        Assert.Equal(OrganizationInventorySourceKind.Unavailable, src.Load().Kind);
    }

    // =========================================================================
    // D. Wrong object type -> untrusted
    // =========================================================================

    [Fact] // C07
    public void C07_DirectoryAtInventoryPath_Untrusted()
    {
        string root = NewTempRoot();
        Directory.CreateDirectory(InventoryPath(root)); // a directory where the file should be
        var src = new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser());
        Assert.Equal(OrganizationInventorySourceKind.Untrusted, src.Load().Kind);
    }

    // =========================================================================
    // E. Reparse points -> untrusted (any component root->file)
    // =========================================================================

    [Fact] // C08
    public void C08_ManagedDirIsJunction_Untrusted()
    {
        string root = NewTempRoot();
        string realTarget = Path.Combine(root, "real_target");
        Directory.CreateDirectory(realTarget);
        File.WriteAllText(Path.Combine(realTarget, "organization-key-inventory.json"), ValidDoc());
        string product = Path.Combine(root, "PAXCookbook");
        Directory.CreateDirectory(product);
        string junction = Path.Combine(product, "ManagedChefKeys");
        if (!TryCreateJunction(junction, realTarget))
        {
            return; // junction creation unavailable on this host; skip deterministically
        }
        var src = new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser());
        Assert.Equal(OrganizationInventorySourceKind.Untrusted, src.Load().Kind);
    }

    // =========================================================================
    // F. Closed-SID ACL trust policy — proven against the PURE policy
    //    (no elevation needed to synthesize SIDs)
    // =========================================================================

    private static IReadOnlyList<OrgInventoryAce> Aces(params OrgInventoryAce[] a) => a;

    [Fact] // C09
    public void C09_Policy_SystemOwner_ReadOnlyAce_Trusted()
    {
        var aces = Aces(
            new OrgInventoryAce(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow),
            new OrgInventoryAce(Everyone, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        Assert.True(ProgramDataOrgInventoryTrustPolicy.IsTrusted(LocalSystem, aces, ProgramDataOrgInventoryTrustPolicy.DefaultTrustedPrincipals));
    }

    [Fact] // C10
    public void C10_Policy_AdminsOwner_AdminsWrite_Trusted()
    {
        var aces = Aces(
            new OrgInventoryAce(BuiltinAdmins, FileSystemRights.FullControl, AccessControlType.Allow),
            new OrgInventoryAce(LocalSystem, FileSystemRights.Modify, AccessControlType.Allow));
        Assert.True(ProgramDataOrgInventoryTrustPolicy.IsTrusted(BuiltinAdmins, aces, ProgramDataOrgInventoryTrustPolicy.DefaultTrustedPrincipals));
    }

    [Fact] // C11
    public void C11_Policy_CurrentUserOwner_Rejected()
    {
        var aces = Aces(new OrgInventoryAce(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        Assert.False(ProgramDataOrgInventoryTrustPolicy.IsTrusted(CurrentUser, aces, ProgramDataOrgInventoryTrustPolicy.DefaultTrustedPrincipals));
    }

    [Fact] // C12
    public void C12_Policy_NullOwner_Rejected()
    {
        var aces = Aces(new OrgInventoryAce(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        Assert.False(ProgramDataOrgInventoryTrustPolicy.IsTrusted(null, aces, ProgramDataOrgInventoryTrustPolicy.DefaultTrustedPrincipals));
    }

    public static IEnumerable<object[]> BroadWritePrincipals()
    {
        yield return new object[] { Everyone };
        yield return new object[] { AuthenticatedUsers };
        yield return new object[] { BuiltinUsers };
        yield return new object[] { Interactive };
        yield return new object[] { CurrentUser };
        yield return new object[] { UnknownSid };
    }

    [Theory] // C13
    [MemberData(nameof(BroadWritePrincipals))]
    public void C13_Policy_BroadPrincipalWriteAce_Rejected(SecurityIdentifier principal)
    {
        var aces = Aces(
            new OrgInventoryAce(BuiltinAdmins, FileSystemRights.FullControl, AccessControlType.Allow),
            new OrgInventoryAce(principal, FileSystemRights.Modify, AccessControlType.Allow));
        Assert.False(ProgramDataOrgInventoryTrustPolicy.IsTrusted(BuiltinAdmins, aces, ProgramDataOrgInventoryTrustPolicy.DefaultTrustedPrincipals));
    }

    [Theory] // C14 — each write-capable right, from a broad principal, is rejected
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.WriteExtendedAttributes)]
    [InlineData(FileSystemRights.WriteAttributes)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.FullControl)]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(FileSystemRights.Write)]
    public void C14_Policy_EachWriteCapableRight_FromEveryone_Rejected(FileSystemRights right)
    {
        var aces = Aces(
            new OrgInventoryAce(BuiltinAdmins, FileSystemRights.FullControl, AccessControlType.Allow),
            new OrgInventoryAce(Everyone, right, AccessControlType.Allow));
        Assert.False(ProgramDataOrgInventoryTrustPolicy.IsTrusted(BuiltinAdmins, aces, ProgramDataOrgInventoryTrustPolicy.DefaultTrustedPrincipals));
    }

    [Fact] // C15 — read/execute allow to a broad principal is fine
    public void C15_Policy_BroadReadAllow_Trusted()
    {
        var aces = Aces(
            new OrgInventoryAce(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow),
            new OrgInventoryAce(Everyone, FileSystemRights.ReadAndExecute, AccessControlType.Allow),
            new OrgInventoryAce(BuiltinUsers, FileSystemRights.Read, AccessControlType.Allow));
        Assert.True(ProgramDataOrgInventoryTrustPolicy.IsTrusted(LocalSystem, aces, ProgramDataOrgInventoryTrustPolicy.DefaultTrustedPrincipals));
    }

    [Fact] // C16 — a DENY ACE to a broad principal never grants nor blocks trust
    public void C16_Policy_BroadDenyAce_Ignored()
    {
        var aces = Aces(
            new OrgInventoryAce(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow),
            new OrgInventoryAce(Everyone, FileSystemRights.FullControl, AccessControlType.Deny));
        Assert.True(ProgramDataOrgInventoryTrustPolicy.IsTrusted(LocalSystem, aces, ProgramDataOrgInventoryTrustPolicy.DefaultTrustedPrincipals));
    }

    // =========================================================================
    // G. End-to-end ACL rejection over a REAL temp file (real DACL read path)
    // =========================================================================

    [Fact] // C17 — current-user owner, production trust set (no seam) -> untrusted
    public void C17_RealFile_CurrentUserOwner_NoSeam_Untrusted()
    {
        string root = NewTempRoot();
        WriteInventoryText(root, ValidDoc());
        var src = new ProgramDataOrganizationKeyInventorySource(root, additionalTrustedPrincipals: null);
        Assert.Equal(OrganizationInventorySourceKind.Untrusted, src.Load().Kind);
    }

    [Fact] // C18 — real Everyone:Modify ALLOW ACE on the file -> untrusted (seam trusts owner only)
    public void C18_RealFile_EveryoneWriteAce_Untrusted()
    {
        string root = NewTempRoot();
        string p = WriteInventoryText(root, ValidDoc());
        AddAce(p, Everyone, FileSystemRights.Modify, AccessControlType.Allow);
        var src = new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser());
        Assert.Equal(OrganizationInventorySourceKind.Untrusted, src.Load().Kind);
    }

    // =========================================================================
    // H. Size bound -> untrusted before buffering
    // =========================================================================

    [Fact] // C19
    public void C19_Oversized_Untrusted()
    {
        string root = NewTempRoot();
        var big = new byte[OrganizationKeyInventoryContract.MaxDocumentBytes + 1];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)'a';
        WriteInventoryBytes(root, big);
        var src = new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser());
        Assert.Equal(OrganizationInventorySourceKind.Untrusted, src.Load().Kind);
    }

    [Fact] // C20 — exactly at the bound is not rejected for size (it parses)
    public void C20_ExactlyAtBound_NotSizeRejected()
    {
        string root = NewTempRoot();
        string doc = ValidDoc();
        byte[] bytes = Encoding.UTF8.GetBytes(doc);
        Assert.True(bytes.Length <= OrganizationKeyInventoryContract.MaxDocumentBytes);
        WriteInventoryBytes(root, bytes);
        var src = new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser());
        Assert.Equal(OrganizationInventorySourceKind.Provided, src.Load().Kind);
    }

    // =========================================================================
    // I. Encoding -> invalid content
    // =========================================================================

    [Fact] // C21
    public void C21_InvalidUtf8_InvalidContent()
    {
        string root = NewTempRoot();
        WriteInventoryBytes(root, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0xC0, 0x80 });
        var src = new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser());
        Assert.Equal(OrganizationInventorySourceKind.InvalidContent, src.Load().Kind);
    }

    [Fact] // C22 — UTF-16 LE BOM rejected as invalid content
    public void C22_Utf16Bom_InvalidContent()
    {
        string root = NewTempRoot();
        var bytes = new List<byte> { 0xFF, 0xFE };
        bytes.AddRange(Encoding.Unicode.GetBytes(ValidDoc()));
        WriteInventoryBytes(root, bytes.ToArray());
        var src = new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser());
        Assert.Equal(OrganizationInventorySourceKind.InvalidContent, src.Load().Kind);
    }

    [Fact] // C23 — a leading UTF-8 BOM is tolerated and stripped; document parses
    public void C23_Utf8Bom_Tolerated_Provided()
    {
        string root = NewTempRoot();
        var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
        bytes.AddRange(Encoding.UTF8.GetBytes(ValidDoc()));
        WriteInventoryBytes(root, bytes.ToArray());
        var src = new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser());
        OrganizationInventorySourceResult r = src.Load();
        Assert.Equal(OrganizationInventorySourceKind.Provided, r.Kind);
        // Through the evaluator, a BOM-prefixed valid document is provisioned.
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(),
            new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser()));
        Assert.Equal(OrganizationInventoryState.AuthorizedProvisioned, p.State);
        Assert.Equal(1, p.EntryCount);
    }

    // =========================================================================
    // J. Trusted valid UTF-8 reaches the parser exactly once
    // =========================================================================

    [Fact] // C24
    public void C24_ValidDocument_ReachesParser_ExactlyOnce_Provisioned()
    {
        string root = NewTempRoot();
        WriteInventoryText(root, ValidDoc(3));
        var probe = new OneShotProbe(new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser()));
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(), probe);
        Assert.Equal(1, probe.LoadCount);
        Assert.Equal(OrganizationInventoryState.AuthorizedProvisioned, p.State);
        Assert.Equal(3, p.EntryCount);
        Assert.True(p.InventoryLoaded);
        // Later stages are still all false.
        Assert.False(p.CertificateResolved);
        Assert.False(p.Usable);
        Assert.False(p.RecipeBound);
        Assert.False(p.BakeAuthorized);
        Assert.False(p.ServiceReady);
    }

    [Fact] // C25 — empty file is trusted but not JSON -> invalid
    public void C25_EmptyFile_Invalid()
    {
        string root = NewTempRoot();
        WriteInventoryBytes(root, Array.Empty<byte>());
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(),
            new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser()));
        Assert.Equal(OrganizationInventoryState.Invalid, p.State);
    }

    [Fact] // C26 — a well-formed but semantically invalid document -> invalid
    public void C26_ParserInvalid_Invalid()
    {
        string root = NewTempRoot();
        WriteInventoryText(root, "{ \"schemaVersion\": 3, \"entries\": [] }");
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(),
            new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser()));
        Assert.Equal(OrganizationInventoryState.Invalid, p.State);
        Assert.Equal("invalid", p.WireState);
    }

    // =========================================================================
    // K. Source cannot fabricate untrusted-as-trusted; production derivation
    // =========================================================================

    [Fact] // C27 — production ctor derives ONLY CommonApplicationData + constant
    public void C27_ProductionCtor_DerivesCommonApplicationData_ReachesNotProvisionedOnDevMachine()
    {
        // On the dev machine the ProgramData inventory file is absent, so the
        // production source (no override, default trust set) resolves to
        // not_provisioned or unavailable — NEVER provisioned/untrusted-as-trusted.
        var src = new ProgramDataOrganizationKeyInventorySource();
        OrganizationInventorySourceResult r = src.Load();
        Assert.True(
            r.Kind == OrganizationInventorySourceKind.NotProvisioned
            || r.Kind == OrganizationInventorySourceKind.Unavailable,
            $"unexpected production kind: {r.Kind}");
        // It never yields a provisioned document on this machine.
        Assert.NotEqual(OrganizationInventorySourceKind.Provided, r.Kind);
    }

    [Fact] // C28 — the result never exposes path/acl/bytes; only a bounded token
    public void C28_Result_ExposesOnlyBoundedKind()
    {
        string root = NewTempRoot();
        WriteInventoryText(root, ValidDoc());
        OrganizationInventorySourceResult r = new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser()).Load();
        // The only public surface is the bounded Kind; Document is internal.
        System.Reflection.PropertyInfo[] props = typeof(OrganizationInventorySourceResult)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.Single(props);
        Assert.Equal("Kind", props[0].Name);
        Assert.Equal(OrganizationInventorySourceKind.Provided, r.Kind);
    }

    // =========================================================================
    // L. WIRE projection — ChefKeyModel.List(projection) surfaces the REAL
    //    bounded state end-to-end (matrix items 56-60 at the wire level). The
    //    provisioned projection is produced through the evaluator + the REAL
    //    read-only ProgramData source over trusted synthetic bytes on disk, so
    //    the wire path is exercised end-to-end (never a fabricated projection).
    // =========================================================================

    // Serialize the ChefKeyModel.List body and return the organizationKeys object
    // plus the full JSON. Asserts the unchanged personal chefKeys array is present.
    private static (JsonElement Org, string Json) WireList(OrganizationInventoryProjection p)
    {
        (int status, object body) = ChefKeyModel.List(p);
        Assert.Equal(200, status);
        string json = JsonSerializer.Serialize(body);
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.True(root.TryGetProperty("chefKeys", out JsonElement chefKeys));
        Assert.Equal(JsonValueKind.Array, chefKeys.ValueKind);
        Assert.True(root.TryGetProperty("organizationKeys", out JsonElement org));
        return (org.Clone(), json);
    }

    // Cycle 14s NARROWING. `organizationKeyId` and `displayName` are permitted in
    // EXACTLY ONE place: an element of the nested
    // `organizationKeys.selectableKeys` array on GET /api/v1/chef-keys. They stay
    // BANNED everywhere else, INCLUDING elsewhere in the same response. Every
    // tenant/client reference and every certificate reference stays banned
    // EVERYWHERE with no exception at all.
    private static readonly string[] SelectorPermittedNames = { "organizationKeyId", "displayName" };

    // Returns the response JSON with the exact nested selector array removed, so
    // a substring ban can be applied to everything OUTSIDE that one shape.
    private static string OutsideSelectorArray(string json)
    {
        const string marker = "\"selectableKeys\":[";
        int start = json.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return json;
        }
        int depth = 0;
        for (int i = start + marker.Length - 1; i < json.Length; i++)
        {
            if (json[i] == '[') { depth++; }
            else if (json[i] == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return json.Remove(start, i - start + 1);
                }
            }
        }
        return json;
    }

    private static void AssertNoOrgIdentifiersOnWire(JsonElement org, string json)
    {
        foreach (string banned in new[]
        {
            "entryCount", // asserted separately per state
            "organizationKeyId", "displayName", "tenantReference", "clientReference",
            "tenantId", "clientId", "certificateReference", "certificateReferenceType",
            "path", "acl", "owner", "sid", "thumbprint", "certThumbprint", "secret",
            "clientSecret", "privateKey", "token", "rawSource",
        })
        {
            if (banned == "entryCount") continue; // handled by caller
            // Still banned as a DIRECT property of organizationKeys: the two
            // narrowed names are legal only INSIDE the nested selector array.
            Assert.False(org.TryGetProperty(banned, out _), $"wire leaked '{banned}'");
        }

        // Tenant/client and certificate references stay banned EVERYWHERE, with
        // no selector exception.
        Assert.DoesNotContain("tenant-ref", json);
        Assert.DoesNotContain("client-ref", json);
        Assert.DoesNotContain("app_registration_certificate", json);

        // Inside the organizationKeys object, the two narrowed names may appear
        // ONLY within the nested selector array. (The personal chefKeys array is
        // a separate projection and legitimately carries its own displayName.)
        string organizationOutsideSelector = OutsideSelectorArray(org.GetRawText());
        foreach (string permittedOnlyInSelector in SelectorPermittedNames)
        {
            Assert.DoesNotContain(permittedOnlyInSelector, organizationOutsideSelector, StringComparison.Ordinal);
        }

        // No raw synthetic identifier or name from the source document leaks
        // anywhere outside that one array.
        string outside = OutsideSelectorArray(json);
        Assert.DoesNotContain("org-key-", outside);
        Assert.DoesNotContain("Contoso", outside);
    }

    [Fact] // C29 (matrix 56) — provisioned wire: entryCount + inventoryLoaded:true, no identifiers.
    public void C29_Wire_Provisioned_EmitsEntryCount_NoIdentifiers()
    {
        string root = NewTempRoot();
        WriteInventoryText(root, ValidDoc(3));
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(
            AuthorizedGate(), new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser()));
        // End-to-end trust/read/parse produced the provisioned projection.
        Assert.Equal(OrganizationInventoryState.AuthorizedProvisioned, p.State);
        Assert.Equal(3, p.EntryCount);

        (JsonElement org, string json) = WireList(p);
        Assert.Equal("authorized_provisioned", org.GetProperty("state").GetString());
        Assert.Equal("provisioned", org.GetProperty("reason").GetString());
        Assert.True(org.GetProperty("readOnly").GetBoolean());
        Assert.True(org.GetProperty("certificateOnly").GetBoolean());
        Assert.True(org.GetProperty("inventoryLoaded").GetBoolean());
        // entryCount is present ONLY here, a bounded non-negative int.
        Assert.True(org.TryGetProperty("entryCount", out JsonElement ec));
        Assert.Equal(JsonValueKind.Number, ec.ValueKind);
        Assert.Equal(3, ec.GetInt32());
        Assert.True(ec.GetInt32() >= 0);
        AssertNoOrgIdentifiersOnWire(org, json);
    }

    [Fact] // C30 (matrix 57) — empty provisioned wire: entryCount 0 present, inventoryLoaded:true.
    public void C30_Wire_ProvisionedEmpty_EntryCountZero_Present()
    {
        string root = NewTempRoot();
        WriteInventoryText(root, "{ \"schemaVersion\": 1, \"entries\": [] }");
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(
            AuthorizedGate(), new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser()));
        Assert.Equal(OrganizationInventoryState.AuthorizedProvisioned, p.State);

        (JsonElement org, _) = WireList(p);
        Assert.True(org.GetProperty("inventoryLoaded").GetBoolean());
        // A provisioned-but-empty inventory still surfaces entryCount = 0 (present).
        Assert.True(org.TryGetProperty("entryCount", out JsonElement ec));
        Assert.Equal(0, ec.GetInt32());
    }

    [Fact] // C31 (matrix 58) — authorized_not_provisioned wire: NO entryCount, inventoryLoaded:false.
    public void C31_Wire_NotProvisioned_NoEntryCount()
    {
        string root = NewTempRoot(); // no ManagedChefKeys dir => not_provisioned
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(
            AuthorizedGate(), new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser()));
        Assert.Equal(OrganizationInventoryState.AuthorizedNotProvisioned, p.State);

        (JsonElement org, _) = WireList(p);
        Assert.Equal("authorized_not_provisioned", org.GetProperty("state").GetString());
        Assert.False(org.GetProperty("inventoryLoaded").GetBoolean());
        Assert.False(org.TryGetProperty("entryCount", out _));
    }

    [Fact] // C32 (matrix 59) — unavailable wire: NO entryCount, inventoryLoaded:false.
    public void C32_Wire_Unavailable_NoEntryCount()
    {
        string root = Path.Combine(Path.GetTempPath(), "paxck6_missing_" + Guid.NewGuid().ToString("N"));
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(
            AuthorizedGate(), new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser()));
        Assert.Equal(OrganizationInventoryState.Unavailable, p.State);

        (JsonElement org, _) = WireList(p);
        Assert.Equal("unavailable", org.GetProperty("state").GetString());
        Assert.False(org.GetProperty("inventoryLoaded").GetBoolean());
        Assert.False(org.TryGetProperty("entryCount", out _));
    }

    [Fact] // C33 (matrix 60) — untrusted wire: NO entryCount, inventoryLoaded:false.
    public void C33_Wire_Untrusted_NoEntryCount()
    {
        string root = NewTempRoot();
        string file = WriteInventoryText(root, ValidDoc());
        AddAce(file, Everyone, FileSystemRights.Modify, AccessControlType.Allow);
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(
            AuthorizedGate(), new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser()));
        Assert.Equal(OrganizationInventoryState.Untrusted, p.State);

        (JsonElement org, string json) = WireList(p);
        Assert.Equal("untrusted", org.GetProperty("state").GetString());
        Assert.False(org.GetProperty("inventoryLoaded").GetBoolean());
        Assert.False(org.TryGetProperty("entryCount", out _));
        // Even an untrusted synthetic document leaks no identifier onto the wire.
        AssertNoOrgIdentifiersOnWire(org, json);
    }

    [Fact] // C34 — invalid wire: NO entryCount, inventoryLoaded:false.
    public void C34_Wire_Invalid_NoEntryCount()
    {
        string root = NewTempRoot();
        WriteInventoryText(root, "{ \"schemaVersion\": 3, \"entries\": [] }");
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(
            AuthorizedGate(), new ProgramDataOrganizationKeyInventorySource(root, SeamTrustCurrentUser()));
        Assert.Equal(OrganizationInventoryState.Invalid, p.State);

        (JsonElement org, _) = WireList(p);
        Assert.Equal("invalid", org.GetProperty("state").GetString());
        Assert.False(org.GetProperty("inventoryLoaded").GetBoolean());
        Assert.False(org.TryGetProperty("entryCount", out _));
    }

    [Fact] // C35 — a denied gate wire still surfaces the gate passthrough with NO entryCount.
    public void C35_Wire_DeniedGate_NoEntryCount()
    {
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(
            DeniedGate(), new ProgramDataOrganizationKeyInventorySource(NewTempRoot(), SeamTrustCurrentUser()));
        Assert.Equal(OrganizationInventoryState.NotAuthorized, p.State);

        (JsonElement org, _) = WireList(p);
        Assert.False(org.GetProperty("inventoryLoaded").GetBoolean());
        Assert.False(org.TryGetProperty("entryCount", out _));
    }

    public void Dispose()
    {
        foreach (string root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    // Remove any junctions first (delete the link, not the target).
                    foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                    {
                        var di = new DirectoryInfo(dir);
                        if ((di.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            try { di.Delete(); } catch { /* best effort */ }
                        }
                    }
                    Directory.Delete(root, true);
                }
            }
            catch
            {
                // best-effort cleanup; tests never depend on real ProgramData.
            }
        }
    }
}
