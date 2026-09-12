using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using PAXCookbook.App;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// Deterministic, fail-closed MACHINE-POLICY DETECTION tests.
//
// These cover the full 27-item classification matrix using an in-memory injected
// IMachinePolicySource / directly-constructed snapshots. Detection is pure: it
// reads a materialized snapshot and never touches the registry, the session
// provider, the network, a token, WAM, Windows Hello, a certificate store, the
// Windows service, a Chef's Key, PAX, or a Bake. The production reader is
// exercised only through a single non-mutating read-only HKLM probe.
public sealed class MachinePolicyDetectionTests
{
    // ---- helpers -------------------------------------------------------------

    private static MachinePolicySnapshot Present(params (string name, MachinePolicyRawValue value)[] values)
    {
        var map = new Dictionary<string, MachinePolicyRawValue>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, MachinePolicyRawValue value) in values)
        {
            map[name] = value;
        }
        return MachinePolicySnapshot.Present(map);
    }

    private static MachinePolicyRawValue Sz(string s) => MachinePolicyRawValue.ForString(s);

    private static MachinePolicyRawValue Dword(int i) => MachinePolicyRawValue.ForDword(i);

    private static MachinePolicyRawValue Schema1 => Dword(MachinePolicyContract.SupportedSchemaVersion);

    private sealed class InMemorySource : IMachinePolicySource
    {
        private readonly MachinePolicySnapshot _snapshot;
        public InMemorySource(MachinePolicySnapshot snapshot) => _snapshot = snapshot;
        public MachinePolicySnapshot Read() => _snapshot;
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // tests/PAXCookbook.App.Tests/<file>  ->  repo root is two levels up.
        string dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private const string ContractRel = "src/PAXCookbook.Shared/Contracts/MachinePolicyContract.cs";
    private const string ReaderRel = "src/PAXCookbook.App/MachinePolicyRegistrySource.cs";

    // ==== 1. absent -> not_configured / self_service / both disabled ==========
    [Fact]
    public void M01_Absent_NotConfigured_SelfService_BothDisabled()
    {
        MachinePolicyDetection d = MachinePolicyParser.Classify(MachinePolicySnapshot.Absent());
        Assert.Equal(MachinePolicyState.NotConfigured, d.State);
        Assert.Equal(MachinePolicyDesktopAccess.SelfService, d.DesktopAccess);
        Assert.Equal(MachinePolicyCapability.Disabled, d.ManagedChefKeys);
        Assert.Equal(MachinePolicyCapability.Disabled, d.WindowsService);
        Assert.Equal(MachinePolicyRecoveryReason.None, d.RecoveryReason);
    }

    [Fact]
    public void M01b_PresentWithNoCanonicalValues_NotConfigured()
    {
        // Extra unrecognized value names are ignored (forward-compat) and never
        // change the classification: present-but-no-canonical == not_configured.
        MachinePolicySnapshot snap = Present(("SomeFutureFlag", Sz("whatever")));
        MachinePolicyDetection d = MachinePolicyParser.Classify(snap);
        Assert.Equal(MachinePolicyState.NotConfigured, d.State);
        Assert.Equal(MachinePolicyDesktopAccess.SelfService, d.DesktopAccess);
    }

    // ==== 2. valid self-service ===============================================
    [Fact]
    public void M02_ValidSelfService_Configured()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Sz(MachinePolicyContract.DesktopAccessSelfService)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityDisabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));
        MachinePolicyDetection d = MachinePolicyParser.Classify(snap);
        Assert.Equal(MachinePolicyState.Configured, d.State);
        Assert.Equal(MachinePolicyDesktopAccess.SelfService, d.DesktopAccess);
        Assert.Equal(MachinePolicyCapability.Disabled, d.ManagedChefKeys);
        Assert.Equal(MachinePolicyCapability.Disabled, d.WindowsService);
        Assert.Equal(MachinePolicyRecoveryReason.None, d.RecoveryReason);
    }

    // ==== 3. org-managed both disabled ========================================
    [Fact]
    public void M03_OrgManaged_BothDisabled_Configured()
    {
        MachinePolicyDetection d = OrgManaged(
            MachinePolicyContract.CapabilityDisabled, MachinePolicyContract.CapabilityDisabled);
        Assert.Equal(MachinePolicyState.Configured, d.State);
        Assert.Equal(MachinePolicyDesktopAccess.OrganizationManaged, d.DesktopAccess);
        Assert.Equal(MachinePolicyCapability.Disabled, d.ManagedChefKeys);
        Assert.Equal(MachinePolicyCapability.Disabled, d.WindowsService);
    }

    // ==== 4. org-managed managed-keys-enabled only ============================
    [Fact]
    public void M04_OrgManaged_ChefKeysEnabledOnly()
    {
        MachinePolicyDetection d = OrgManaged(
            MachinePolicyContract.CapabilityEnabled, MachinePolicyContract.CapabilityDisabled);
        Assert.Equal(MachinePolicyState.Configured, d.State);
        Assert.Equal(MachinePolicyCapability.Enabled, d.ManagedChefKeys);
        Assert.Equal(MachinePolicyCapability.Disabled, d.WindowsService);
    }

    // ==== 5. org-managed service-enabled only =================================
    [Fact]
    public void M05_OrgManaged_ServiceEnabledOnly()
    {
        MachinePolicyDetection d = OrgManaged(
            MachinePolicyContract.CapabilityDisabled, MachinePolicyContract.CapabilityEnabled);
        Assert.Equal(MachinePolicyState.Configured, d.State);
        Assert.Equal(MachinePolicyCapability.Disabled, d.ManagedChefKeys);
        Assert.Equal(MachinePolicyCapability.Enabled, d.WindowsService);
    }

    // ==== 6. org-managed both enabled =========================================
    [Fact]
    public void M06_OrgManaged_BothEnabled()
    {
        MachinePolicyDetection d = OrgManaged(
            MachinePolicyContract.CapabilityEnabled, MachinePolicyContract.CapabilityEnabled);
        Assert.Equal(MachinePolicyState.Configured, d.State);
        Assert.Equal(MachinePolicyCapability.Enabled, d.ManagedChefKeys);
        Assert.Equal(MachinePolicyCapability.Enabled, d.WindowsService);
    }

    private static MachinePolicyDetection OrgManaged(string chefKeys, string service)
        => MachinePolicyParser.Classify(Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Sz(MachinePolicyContract.DesktopAccessOrganizationManaged)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(chefKeys)),
            (MachinePolicyContract.ValueWindowsService, Sz(service))));

    // ==== 7. missing schema version ===========================================
    [Fact]
    public void M07_MissingSchemaVersion_InvalidPartial()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValueDesktopAccess, Sz(MachinePolicyContract.DesktopAccessOrganizationManaged)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityDisabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));
        AssertInvalid(MachinePolicyParser.Classify(snap), MachinePolicyRecoveryReason.Partial);
    }

    // ==== 8. unsupported schema version =======================================
    [Fact]
    public void M08_UnsupportedSchemaVersion_Invalid()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Dword(2)),
            (MachinePolicyContract.ValueDesktopAccess, Sz(MachinePolicyContract.DesktopAccessOrganizationManaged)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityDisabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));
        AssertInvalid(MachinePolicyParser.Classify(snap), MachinePolicyRecoveryReason.UnsupportedSchema);
    }

    // ==== 9. each required dimension missing independently =====================
    [Fact]
    public void M09a_MissingDesktopAccess_InvalidPartial()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityDisabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));
        AssertInvalid(MachinePolicyParser.Classify(snap), MachinePolicyRecoveryReason.Partial);
    }

    [Fact]
    public void M09b_MissingManagedChefKeys_InvalidPartial()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Sz(MachinePolicyContract.DesktopAccessOrganizationManaged)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));
        AssertInvalid(MachinePolicyParser.Classify(snap), MachinePolicyRecoveryReason.Partial);
    }

    [Fact]
    public void M09c_MissingWindowsService_InvalidPartial()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Sz(MachinePolicyContract.DesktopAccessOrganizationManaged)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityDisabled)));
        AssertInvalid(MachinePolicyParser.Classify(snap), MachinePolicyRecoveryReason.Partial);
    }

    // ==== 10. empty values ====================================================
    [Fact]
    public void M10_EmptyDimensionValue_InvalidUnknownEnum()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Sz(string.Empty)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityDisabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));
        AssertInvalid(MachinePolicyParser.Classify(snap), MachinePolicyRecoveryReason.UnknownEnum);
    }

    // ==== 11. unknown enum values =============================================
    [Fact]
    public void M11_UnknownEnumValue_InvalidUnknownEnum()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Sz("managed_by_someone")),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityDisabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));
        AssertInvalid(MachinePolicyParser.Classify(snap), MachinePolicyRecoveryReason.UnknownEnum);
    }

    // ==== 12. wrong registry value type =======================================
    [Fact]
    public void M12a_DesktopAccessWrongType_InvalidTypeMismatch()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Dword(1)), // should be REG_SZ
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityDisabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));
        AssertInvalid(MachinePolicyParser.Classify(snap), MachinePolicyRecoveryReason.TypeMismatch);
    }

    [Fact]
    public void M12b_SchemaWrongType_InvalidTypeMismatch()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Sz("1")), // should be REG_DWORD
            (MachinePolicyContract.ValueDesktopAccess, Sz(MachinePolicyContract.DesktopAccessOrganizationManaged)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityDisabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));
        AssertInvalid(MachinePolicyParser.Classify(snap), MachinePolicyRecoveryReason.TypeMismatch);
    }

    // ==== 13. oversized value =================================================
    [Fact]
    public void M13_OversizedValue_InvalidOversized()
    {
        string huge = new string('x', MachinePolicyContract.MaxRegSzLength + 1);
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Sz(huge)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityDisabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));
        AssertInvalid(MachinePolicyParser.Classify(snap), MachinePolicyRecoveryReason.Oversized);
    }

    // ==== 14. conflicting values (self_service + a capability enabled) =========
    [Fact]
    public void M14a_SelfServiceWithChefKeysEnabled_InvalidConflicting()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Sz(MachinePolicyContract.DesktopAccessSelfService)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityEnabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));
        AssertInvalid(MachinePolicyParser.Classify(snap), MachinePolicyRecoveryReason.Conflicting);
    }

    [Fact]
    public void M14b_SelfServiceWithServiceEnabled_InvalidConflicting()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Sz(MachinePolicyContract.DesktopAccessSelfService)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityDisabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityEnabled)));
        AssertInvalid(MachinePolicyParser.Classify(snap), MachinePolicyRecoveryReason.Conflicting);
    }

    // ==== 15. access denied ===================================================
    [Fact]
    public void M15_AccessDenied_InvalidAccessDenied()
    {
        AssertInvalid(
            MachinePolicyParser.Classify(MachinePolicySnapshot.AccessDenied()),
            MachinePolicyRecoveryReason.AccessDenied);
    }

    // ==== 16. source exists but untrusted =====================================
    [Fact]
    public void M16_UntrustedSource_Untrusted()
    {
        MachinePolicyDetection d = MachinePolicyParser.Classify(MachinePolicySnapshot.Untrusted());
        Assert.Equal(MachinePolicyState.Untrusted, d.State);
        Assert.Equal(MachinePolicyDesktopAccess.Undetermined, d.DesktopAccess);
        Assert.Equal(MachinePolicyCapability.Disabled, d.ManagedChefKeys);
        Assert.Equal(MachinePolicyCapability.Disabled, d.WindowsService);
        Assert.Equal(MachinePolicyRecoveryReason.UntrustedSource, d.RecoveryReason);
    }

    // ==== 17. HKCU / user-source ignored & cannot override HKLM ================
    [Fact]
    public void M17_ProductionReader_ReadsOnlyHklm_NoUserHive()
    {
        string reader = ReadSource(ReaderRel);
        Assert.Contains("Registry.LocalMachine", reader);
        Assert.DoesNotContain("Registry.CurrentUser", reader);
        Assert.DoesNotContain("CurrentUser", reader);
        Assert.DoesNotContain("HKEY_CURRENT_USER", reader);
        Assert.Contains("writable: false", reader);
        Assert.DoesNotContain("writable: true", reader);
        // The path is a hardcoded contract constant, not a runtime input.
        Assert.Contains("MachinePolicyContract.PolicySubKeyPath", reader);
    }

    // ==== 18. env vars cannot override ========================================
    [Fact]
    public void M18_ProductionReader_NoEnvironmentOverride()
    {
        string reader = ReadSource(ReaderRel);
        Assert.DoesNotContain("GetEnvironmentVariable", reader);
        Assert.DoesNotContain("ExpandEnvironmentVariables", reader);
        // Env-name expansion of REG_SZ is explicitly disabled.
        Assert.Contains("DoNotExpandEnvironmentNames", reader);
    }

    // ==== 19. command-line cannot override ====================================
    [Fact]
    public void M19_ProductionReader_NoCommandLineOverride()
    {
        string reader = ReadSource(ReaderRel);
        Assert.DoesNotContain("GetCommandLineArgs", reader);
        Assert.DoesNotContain("CommandLine", reader);
        // The reader takes no path/source parameter from runtime input.
        ConstructorInfo[] ctors = typeof(MachinePolicyRegistrySource).GetConstructors();
        Assert.All(ctors, c => Assert.Empty(c.GetParameters()));
    }

    // ==== 20/21/22. invalid cannot enable caps or grant self-service ==========
    [Fact]
    public void M20_21_22_InvalidNeverEnablesCapsOrGrantsSelfService()
    {
        // A malformed policy that "tries" to enable everything and claim self-service.
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Dword(999)), // unsupported => invalid
            (MachinePolicyContract.ValueDesktopAccess, Sz(MachinePolicyContract.DesktopAccessSelfService)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityEnabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityEnabled)));
        MachinePolicyDetection d = MachinePolicyParser.Classify(snap);
        Assert.Equal(MachinePolicyState.Invalid, d.State);
        // 20: managed keys never enabled by invalid policy.
        Assert.Equal(MachinePolicyCapability.Disabled, d.ManagedChefKeys);
        // 21: service never enabled by invalid policy.
        Assert.Equal(MachinePolicyCapability.Disabled, d.WindowsService);
        // 22: never coerced to self-service; explicit undetermined.
        Assert.Equal(MachinePolicyDesktopAccess.Undetermined, d.DesktopAccess);
        Assert.NotEqual(MachinePolicyDesktopAccess.SelfService, d.DesktopAccess);
    }

    [Fact]
    public void M22b_UntrustedNeverGrantsSelfService()
    {
        MachinePolicyDetection d = MachinePolicyParser.Classify(MachinePolicySnapshot.Untrusted());
        Assert.Equal(MachinePolicyDesktopAccess.Undetermined, d.DesktopAccess);
        Assert.NotEqual(MachinePolicyDesktopAccess.SelfService, d.DesktopAccess);
    }

    // ==== 23. no result contains raw policy content or exception text ==========
    [Fact]
    public void M23_ResultNeverLeaksRawContent()
    {
        const string secret = "RAWSECRET_do_not_leak_1234567890";
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Sz(secret)), // unknown enum, carries "secret"
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityDisabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));
        MachinePolicyDetection d = MachinePolicyParser.Classify(snap);
        Assert.DoesNotContain(secret, d.ToString());

        MachinePolicyStatusProjection p = MachinePolicyStatusProjection.Project(d);
        Assert.DoesNotContain(secret, p.Title);
        Assert.DoesNotContain(secret, p.Description);
        // The projection also never leaks the registry path or enum internals.
        Assert.DoesNotContain(MachinePolicyContract.PolicySubKeyPath, p.Title + p.Description);
        Assert.DoesNotContain("UnknownEnum", p.Title + p.Description);
    }

    // ==== 24. provider independence ===========================================
    [Fact]
    public void M24_ProviderIndependence_SameSnapshotSameResult()
    {
        // The detection takes NO session-provider input, so the result is
        // identical whether the selected provider is Windows Hello or Work account.
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Sz(MachinePolicyContract.DesktopAccessOrganizationManaged)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityEnabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityDisabled)));

        MachinePolicyDetection underHello = MachinePolicyParser.Classify(snap);
        MachinePolicyDetection underWork = MachinePolicyParser.Classify(snap);
        AssertSameDetection(underHello, underWork);

        // And the new code never references the session provider at all.
        string contract = ReadSource(ContractRel);
        string reader = ReadSource(ReaderRel);
        Assert.DoesNotContain("SessionProvider", contract);
        Assert.DoesNotContain("session-provider", contract);
        Assert.DoesNotContain("SessionProvider", reader);
        Assert.DoesNotContain("session-provider", reader);
    }

    // ==== 25. parsing deterministic & side-effect-free ========================
    [Fact]
    public void M25_Deterministic_And_PureContract()
    {
        MachinePolicySnapshot snap = Present(
            (MachinePolicyContract.ValuePolicySchemaVersion, Schema1),
            (MachinePolicyContract.ValueDesktopAccess, Sz(MachinePolicyContract.DesktopAccessOrganizationManaged)),
            (MachinePolicyContract.ValueManagedChefKeys, Sz(MachinePolicyContract.CapabilityEnabled)),
            (MachinePolicyContract.ValueWindowsService, Sz(MachinePolicyContract.CapabilityEnabled)));

        MachinePolicyDetection first = MachinePolicyParser.Classify(snap);
        for (int i = 0; i < 25; i++)
        {
            AssertSameDetection(first, MachinePolicyParser.Classify(snap));
        }

        // The contract (pure parser + DTOs) touches no I/O, registry, network, or process.
        string contract = ReadSource(ContractRel);
        Assert.DoesNotContain("Registry.", contract);
        Assert.DoesNotContain("Microsoft.Win32", contract);
        Assert.DoesNotContain("File.", contract);
        Assert.DoesNotContain("HttpClient", contract);
        Assert.DoesNotContain("Process.Start", contract);
        Assert.DoesNotContain("Environment.", contract);
    }

    // ==== 26. normal-user projection exposes no mutation action ===============
    [Fact]
    public void M26_Projection_NoManagedPolicyMutationSurface()
    {
        Type t = typeof(MachinePolicyStatusProjection);

        // No public settable property (read-only bounded DTO).
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.False(p.CanWrite, $"Projection property {p.Name} must be read-only.");
        }

        // The only public instance methods are object overrides; no policy mutator.
        string[] allowed = { "ToString", "Equals", "GetHashCode", "GetType" };
        MethodInfo[] instanceMethods = t
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(instanceMethods, m => !m.IsSpecialName && !allowed.Contains(m.Name));

        // Managed-by-org copy does not imply the ordinary user can change policy.
        MachinePolicyStatusProjection managed = MachinePolicyStatusProjection.Project(
            OrgManaged(MachinePolicyContract.CapabilityEnabled, MachinePolicyContract.CapabilityEnabled));
        Assert.Equal(MachinePolicyDisplayState.ManagedByOrganization, managed.DisplayState);
        string copy = (managed.Title + " " + managed.Description).ToLowerInvariant();
        Assert.DoesNotContain("edit", copy);
        Assert.DoesNotContain("change these", copy);
        Assert.DoesNotContain("disable", copy);
    }

    // ==== 27. no service/key/cert/Graph/token/WAM/Hello/PAX/Bake action ========
    [Fact]
    public void M27_NewCode_NoProhibitedExecutionOrMutationPath()
    {
        string contract = ReadSource(ContractRel);
        string reader = ReadSource(ReaderRel);

        // Concrete API identifiers that would indicate a prohibited path. (Bare
        // prose words like "PAX"/"Bake" appear only in doc-comments; we assert on
        // concrete call sites instead.)
        string[] prohibited =
        {
            "ServiceController", "System.ServiceProcess", "sc.exe",
            "X509Store", "X509Certificate", "CertStore",
            "graph.microsoft.com", "GraphServiceClient",
            "Microsoft.Identity", "PublicClientApplication", "AcquireToken", "IPublicClient",
            "WebAuthn", "KeyCredential", "Windows.Security.Credentials",
            "Process.Start", "ProcessStartInfo",
            // No registry WRITE and no user-hive / override sources anywhere.
            "SetValue", "CreateSubKey", "DeleteValue", "DeleteSubKey",
            "Registry.CurrentUser", "Registry.Users",
        };

        foreach (string token in prohibited)
        {
            Assert.DoesNotContain(token, contract);
            Assert.DoesNotContain(token, reader);
        }

        // The reader's only registry surface is a read-only HKLM open.
        Assert.Contains("Registry.LocalMachine.OpenSubKey", reader);
        Assert.Contains("writable: false", reader);
    }

    // ==== production reader: non-mutating, trusted-by-construction =============
    [Fact]
    public void ProductionReader_Read_NeverThrows_NeverUntrusted()
    {
        var source = new MachinePolicyRegistrySource();
        MachinePolicySnapshot snap = source.Read(); // read-only HKLM probe; no write
        Assert.NotEqual(MachinePolicySourceAccess.Untrusted, snap.Access);
        // Whatever the machine reports, classification never throws.
        MachinePolicyDetection d = MachinePolicyParser.Classify(snap);
        Assert.NotNull(d);
    }

    // ---- assertion helpers ---------------------------------------------------

    private static void AssertInvalid(MachinePolicyDetection d, MachinePolicyRecoveryReason expectedReason)
    {
        Assert.Equal(MachinePolicyState.Invalid, d.State);
        Assert.Equal(MachinePolicyDesktopAccess.Undetermined, d.DesktopAccess);
        Assert.Equal(MachinePolicyCapability.Disabled, d.ManagedChefKeys);
        Assert.Equal(MachinePolicyCapability.Disabled, d.WindowsService);
        Assert.Equal(expectedReason, d.RecoveryReason);
    }

    private static void AssertSameDetection(MachinePolicyDetection a, MachinePolicyDetection b)
    {
        Assert.Equal(a.State, b.State);
        Assert.Equal(a.DesktopAccess, b.DesktopAccess);
        Assert.Equal(a.ManagedChefKeys, b.ManagedChefKeys);
        Assert.Equal(a.WindowsService, b.WindowsService);
        Assert.Equal(a.RecoveryReason, b.RecoveryReason);
    }
}
