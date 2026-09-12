using System;
using System.IO;
using System.Runtime.CompilerServices;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// Deterministic tests for the MANAGED CHEF'S KEYS AUTHORIZATION GATE (Cycle 04).
//
// The gate is a pure function of the typed Cycle 3 MachinePolicyDetection. It
// authorizes a FUTURE organization-provided Chef's Keys inventory ONLY when the
// detection is simultaneously Configured + OrganizationManaged + ManagedChefKeys
// Enabled; every other input (including null and every invalid/untrusted/
// inaccessible policy) fails closed to Unavailable. Authorization != availability:
// the authorized terminal state is authorized_not_provisioned with
// InventoryLoaded == false.
//
// These tests never touch the registry, the session provider, the network, a
// token, WAM, Windows Hello, a certificate store, the Windows service, a Chef's
// Key vault, PAX, or a Bake. Integration wiring and containment are proven by
// deterministic source scans, mirroring MachinePolicyDetectionTests.
public sealed class ManagedChefKeysGateTests
{
    // ---- helpers -------------------------------------------------------------

    private static MachinePolicyDetection OrgManaged(
        MachinePolicyCapability chefKeys, MachinePolicyCapability service)
        => MachinePolicyDetection.ConfiguredOrganizationManaged(chefKeys, service);

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // tests/PAXCookbook.App.Tests/<file>  ->  repo root is two levels up.
        string dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private const string GateContractRel = "src/PAXCookbook.Shared/Contracts/ManagedChefKeysGateContract.cs";
    private const string ProgramRel = "src/PAXCookbook.App/Program.cs";
    private const string ChefKeyModelRel = "src/PAXCookbook.App/ChefKeyModel.cs";

    private static string GateContract => ReadSource(GateContractRel);
    private static string Program => ReadSource(ProgramRel);
    private static string ChefKeyModelSource => ReadSource(ChefKeyModelRel);

    // ==== detection -> projection mapping (authoritative table) ===============

    [Fact] // G01
    public void G01_NullDetection_Unavailable_Unknown_NotAuthorized()
    {
        ManagedChefKeysGateProjection p = ManagedChefKeysGate.Evaluate(null);
        Assert.Equal(ManagedChefKeysGateState.Unavailable, p.State);
        Assert.Equal(ManagedChefKeysGateReason.Unknown, p.Reason);
        Assert.False(p.Authorized);
    }

    [Fact] // G02
    public void G02_NotConfigured_NotConfigured_NotConfigured_NotAuthorized()
    {
        ManagedChefKeysGateProjection p = ManagedChefKeysGate.Evaluate(MachinePolicyDetection.NotConfigured());
        Assert.Equal(ManagedChefKeysGateState.NotConfigured, p.State);
        Assert.Equal(ManagedChefKeysGateReason.NotConfigured, p.Reason);
        Assert.False(p.Authorized);
    }

    [Fact] // G03
    public void G03_ConfiguredSelfService_NotConfigured_SelfService_NotAuthorized()
    {
        ManagedChefKeysGateProjection p = ManagedChefKeysGate.Evaluate(MachinePolicyDetection.ConfiguredSelfService());
        Assert.Equal(ManagedChefKeysGateState.NotConfigured, p.State);
        Assert.Equal(ManagedChefKeysGateReason.SelfService, p.Reason);
        Assert.False(p.Authorized);
    }

    [Fact] // G04
    public void G04_OrgManaged_ChefKeysDisabled_Disabled_DisabledByPolicy_NotAuthorized()
    {
        ManagedChefKeysGateProjection p = ManagedChefKeysGate.Evaluate(
            OrgManaged(MachinePolicyCapability.Disabled, MachinePolicyCapability.Disabled));
        Assert.Equal(ManagedChefKeysGateState.Disabled, p.State);
        Assert.Equal(ManagedChefKeysGateReason.DisabledByPolicy, p.Reason);
        Assert.False(p.Authorized);
    }

    [Fact] // G05
    public void G05_OrgManaged_ChefKeysEnabled_AuthorizedNotProvisioned_Authorized()
    {
        ManagedChefKeysGateProjection p = ManagedChefKeysGate.Evaluate(
            OrgManaged(MachinePolicyCapability.Enabled, MachinePolicyCapability.Disabled));
        Assert.Equal(ManagedChefKeysGateState.AuthorizedNotProvisioned, p.State);
        Assert.Equal(ManagedChefKeysGateReason.AuthorizedNotProvisioned, p.Reason);
        Assert.True(p.Authorized);
    }

    [Fact] // G06 — the windows-service dimension never affects the chef-keys gate.
    public void G06_OrgManaged_ChefKeysEnabled_ServiceEnabled_StillAuthorized()
    {
        ManagedChefKeysGateProjection p = ManagedChefKeysGate.Evaluate(
            OrgManaged(MachinePolicyCapability.Enabled, MachinePolicyCapability.Enabled));
        Assert.Equal(ManagedChefKeysGateState.AuthorizedNotProvisioned, p.State);
        Assert.True(p.Authorized);
    }

    [Fact] // G07 — service enabled but chef-keys disabled is still Disabled.
    public void G07_OrgManaged_ChefKeysDisabled_ServiceEnabled_StillDisabled()
    {
        ManagedChefKeysGateProjection p = ManagedChefKeysGate.Evaluate(
            OrgManaged(MachinePolicyCapability.Disabled, MachinePolicyCapability.Enabled));
        Assert.Equal(ManagedChefKeysGateState.Disabled, p.State);
        Assert.False(p.Authorized);
    }

    [Fact] // G08
    public void G08_Invalid_Malformed_Unavailable_PolicyInvalid()
    {
        ManagedChefKeysGateProjection p = ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.Malformed));
        Assert.Equal(ManagedChefKeysGateState.Unavailable, p.State);
        Assert.Equal(ManagedChefKeysGateReason.PolicyInvalid, p.Reason);
        Assert.False(p.Authorized);
    }

    [Fact] // G09
    public void G09_Invalid_AccessDenied_Unavailable_PolicyUnavailable()
    {
        ManagedChefKeysGateProjection p = ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.AccessDenied));
        Assert.Equal(ManagedChefKeysGateState.Unavailable, p.State);
        Assert.Equal(ManagedChefKeysGateReason.PolicyUnavailable, p.Reason);
        Assert.False(p.Authorized);
    }

    [Theory] // G10 — every non-AccessDenied invalid reason maps to PolicyInvalid.
    [InlineData(MachinePolicyRecoveryReason.Partial)]
    [InlineData(MachinePolicyRecoveryReason.UnsupportedSchema)]
    [InlineData(MachinePolicyRecoveryReason.TypeMismatch)]
    [InlineData(MachinePolicyRecoveryReason.Oversized)]
    [InlineData(MachinePolicyRecoveryReason.UnknownEnum)]
    [InlineData(MachinePolicyRecoveryReason.Conflicting)]
    public void G10_Invalid_OtherReasons_Unavailable_PolicyInvalid(MachinePolicyRecoveryReason reason)
    {
        ManagedChefKeysGateProjection p = ManagedChefKeysGate.Evaluate(MachinePolicyDetection.Invalid(reason));
        Assert.Equal(ManagedChefKeysGateState.Unavailable, p.State);
        Assert.Equal(ManagedChefKeysGateReason.PolicyInvalid, p.Reason);
        Assert.False(p.Authorized);
    }

    [Fact] // G11
    public void G11_Untrusted_Unavailable_PolicyUntrusted()
    {
        ManagedChefKeysGateProjection p = ManagedChefKeysGate.Evaluate(MachinePolicyDetection.Untrusted());
        Assert.Equal(ManagedChefKeysGateState.Unavailable, p.State);
        Assert.Equal(ManagedChefKeysGateReason.PolicyUntrusted, p.Reason);
        Assert.False(p.Authorized);
    }

    // ==== the all-three predicate: ONLY Configured+OrgManaged+Enabled grants ==

    [Fact] // G12 — the single authorizing combination.
    public void G12_OnlyOrgManagedEnabled_Authorizes()
    {
        Assert.True(ManagedChefKeysGate.Evaluate(
            OrgManaged(MachinePolicyCapability.Enabled, MachinePolicyCapability.Disabled)).Authorized);
    }

    [Fact] // G13 — self-service can never authorize.
    public void G13_SelfService_NeverAuthorizes()
    {
        Assert.False(ManagedChefKeysGate.Evaluate(MachinePolicyDetection.ConfiguredSelfService()).Authorized);
        Assert.False(ManagedChefKeysGate.Evaluate(MachinePolicyDetection.NotConfigured()).Authorized);
    }

    [Fact] // G14 — invalid/untrusted can never authorize.
    public void G14_InvalidUntrusted_NeverAuthorizes()
    {
        Assert.False(ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.Malformed)).Authorized);
        Assert.False(ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.AccessDenied)).Authorized);
        Assert.False(ManagedChefKeysGate.Evaluate(MachinePolicyDetection.Untrusted()).Authorized);
    }

    // ==== wire tokens =========================================================

    [Fact] // G15 — state wire tokens.
    public void G15_WireStateTokens()
    {
        Assert.Equal("not_configured", ManagedChefKeysGate.Evaluate(MachinePolicyDetection.NotConfigured()).WireState);
        Assert.Equal("disabled", ManagedChefKeysGate.Evaluate(
            OrgManaged(MachinePolicyCapability.Disabled, MachinePolicyCapability.Disabled)).WireState);
        Assert.Equal("authorized_not_provisioned", ManagedChefKeysGate.Evaluate(
            OrgManaged(MachinePolicyCapability.Enabled, MachinePolicyCapability.Disabled)).WireState);
        Assert.Equal("unavailable", ManagedChefKeysGate.Evaluate(MachinePolicyDetection.Untrusted()).WireState);
    }

    [Fact] // G16 — reason wire tokens.
    public void G16_WireReasonTokens()
    {
        Assert.Equal("not_configured", ManagedChefKeysGate.Evaluate(MachinePolicyDetection.NotConfigured()).WireReason);
        Assert.Equal("self_service", ManagedChefKeysGate.Evaluate(MachinePolicyDetection.ConfiguredSelfService()).WireReason);
        Assert.Equal("disabled_by_policy", ManagedChefKeysGate.Evaluate(
            OrgManaged(MachinePolicyCapability.Disabled, MachinePolicyCapability.Disabled)).WireReason);
        Assert.Equal("authorized_not_provisioned", ManagedChefKeysGate.Evaluate(
            OrgManaged(MachinePolicyCapability.Enabled, MachinePolicyCapability.Disabled)).WireReason);
        Assert.Equal("policy_invalid", ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.Malformed)).WireReason);
        Assert.Equal("policy_unavailable", ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.AccessDenied)).WireReason);
        Assert.Equal("policy_untrusted", ManagedChefKeysGate.Evaluate(MachinePolicyDetection.Untrusted()).WireReason);
        Assert.Equal("unknown", ManagedChefKeysGate.Evaluate(null).WireReason);
    }

    // ==== bounded projection invariants =======================================

    [Fact] // G17/G18/G19 — read-only, certificate-only, inventory-not-loaded on every state.
    public void G17_ProjectionConstantsHoldForEveryState()
    {
        foreach (ManagedChefKeysGateProjection p in AllReachableProjections())
        {
            Assert.True(p.ReadOnly);
            Assert.True(p.CertificateOnly);
            Assert.False(p.InventoryLoaded);
        }
    }

    [Fact] // G20 — Authorized is true ONLY in the AuthorizedNotProvisioned state.
    public void G20_AuthorizedTrue_OnlyForAuthorizedNotProvisioned()
    {
        foreach (ManagedChefKeysGateProjection p in AllReachableProjections())
        {
            Assert.Equal(p.State == ManagedChefKeysGateState.AuthorizedNotProvisioned, p.Authorized);
        }
    }

    [Fact] // G21 — ToString is content-free (only bounded wire tokens; no digits/paths/backslashes).
    public void G21_ToString_ContentFree()
    {
        foreach (ManagedChefKeysGateProjection p in AllReachableProjections())
        {
            string s = p.ToString();
            Assert.Contains(p.WireState, s);
            Assert.Contains(p.WireReason, s);
            Assert.DoesNotContain("\\", s);
            Assert.DoesNotContain("SOFTWARE", s);
            Assert.DoesNotContain("HKEY", s);
        }
    }

    [Fact] // G22 — Unavailable factory coerces any out-of-range reason to Unknown (fail closed).
    public void G22_Unavailable_CoercesOutOfRangeReason_ToUnknown()
    {
        ManagedChefKeysGateProjection p = ManagedChefKeysGateProjection.Unavailable(ManagedChefKeysGateReason.SelfService);
        Assert.Equal(ManagedChefKeysGateState.Unavailable, p.State);
        Assert.Equal(ManagedChefKeysGateReason.Unknown, p.Reason);
        Assert.False(p.Authorized);
    }

    // ==== organization auth type is certificate-only ==========================

    [Fact] // G23 — the only permitted organization auth type is app-registration certificate.
    public void G23_OrganizationKeyAuthType_IsCertificateOnly()
    {
        Array values = Enum.GetValues(typeof(OrganizationKeyAuthType));
        Assert.Single(values);
        Assert.Equal(OrganizationKeyAuthType.AppRegistrationCertificate, (OrganizationKeyAuthType)values.GetValue(0)!);
    }

    [Fact] // G24 — ChefKeyOrigin tags ownership (Personal + Organization) and nothing else.
    public void G24_ChefKeyOrigin_HasExactlyPersonalAndOrganization()
    {
        Array values = Enum.GetValues(typeof(ChefKeyOrigin));
        Assert.Equal(2, values.Length);
        Assert.Contains(ChefKeyOrigin.Personal, (ChefKeyOrigin[])values);
        Assert.Contains(ChefKeyOrigin.Organization, (ChefKeyOrigin[])values);
    }

    // ==== deterministic source-scan containment ===============================

    [Fact] // G25 — the gate contract touches no certificate store / X509 API.
    public void G25_GateContract_NoCertificateStoreAccess()
    {
        string src = GateContract;
        Assert.DoesNotContain("X509Store", src);
        Assert.DoesNotContain("X509Certificate", src);
        Assert.DoesNotContain("StoreName", src);
        Assert.DoesNotContain("FindByThumbprint", src);
    }

    [Fact] // G26 — the gate contract touches no Windows service-control API.
    public void G26_GateContract_NoServiceControlApi()
    {
        string src = GateContract;
        Assert.DoesNotContain("ServiceController", src);
        Assert.DoesNotContain("CreateService", src);
        Assert.DoesNotContain("sc.exe", src);
        Assert.DoesNotContain("ServiceInstaller", src);
    }

    [Fact] // G27 — the gate contract reaches no Microsoft Graph / token endpoint.
    public void G27_GateContract_NoGraphOrToken()
    {
        string src = GateContract;
        Assert.DoesNotContain("graph.microsoft.com", src);
        Assert.DoesNotContain("GraphServiceClient", src);
        Assert.DoesNotContain("AcquireToken", src);
        Assert.DoesNotContain("access_token", src);
    }

    [Fact] // G28 — the gate contract has no WAM / Windows Hello / MSAL path.
    public void G28_GateContract_NoWamOrHello()
    {
        string src = GateContract;
        Assert.DoesNotContain("PublicClientApplication", src);
        Assert.DoesNotContain("WithBroker", src);
        Assert.DoesNotContain("WebAuthn", src);
        Assert.DoesNotContain("Microsoft.Identity", src);
    }

    [Fact] // G29 — the gate contract never runs PAX or a Bake.
    public void G29_GateContract_NoPaxOrBake()
    {
        string src = GateContract;
        Assert.DoesNotContain("Process.Start", src);
        Assert.DoesNotContain("Bake", src);
        Assert.DoesNotContain(".ps1", src);
        Assert.DoesNotContain("PAX_Purview", src);
    }

    [Fact] // G30 — the gate contract writes no registry and reads no user-controlled policy source.
    public void G30_GateContract_NoRegistryWrite_NoUserPolicyFallback()
    {
        string src = GateContract;
        Assert.DoesNotContain("Microsoft.Win32", src);
        Assert.DoesNotContain("RegistryKey", src);
        Assert.DoesNotContain("SetValue", src);
        Assert.DoesNotContain("Registry.CurrentUser", src);
        Assert.DoesNotContain("HKEY_CURRENT_USER", src);
        Assert.DoesNotContain("GetEnvironmentVariable", src);
        Assert.DoesNotContain("CommandLine", src);
    }

    [Fact] // G31 — the gate is provider-independent (never reads the session provider).
    public void G31_GateContract_ProviderIndependent()
    {
        string src = GateContract;
        Assert.DoesNotContain("SessionProvider", src);
        Assert.DoesNotContain("session-provider", src);
    }

    [Fact] // G32 — the projection type carries no secret / identifier field.
    public void G32_GateContract_NoSecretOrIdentifierFields()
    {
        string src = GateContract;
        Assert.DoesNotContain("clientSecret", src);
        Assert.DoesNotContain("ClientSecret", src);
        Assert.DoesNotContain("thumbprint", src);
        Assert.DoesNotContain("Thumbprint", src);
        Assert.DoesNotContain("tenantId", src);
        Assert.DoesNotContain("clientId", src);
        Assert.DoesNotContain("PrivateKey", src);
    }

    // ==== product-reachable integration (broker-owned) ========================

    [Fact] // G33 — the GET chef-keys route computes the gate from the HKLM reader and feeds List.
    public void G33_ChefKeysRoute_ComputesGate_FromPolicyReader()
    {
        string src = Program;
        Assert.Contains("MapGet(\"/api/v1/chef-keys\"", src);
        Assert.Contains("ChefKeyModel.List(", src);
        // Cycle 16 — the route consumes the SINGLE production authority factory
        // instead of assembling the chain inline. The policy-reader guarantee is
        // unchanged and is now ALSO pinned to exactly one wiring location.
        Assert.Contains("ProductionOrganizationAuthority.CreateLocalAuthority()", src);
        Assert.DoesNotContain("new MachinePolicyRegistrySource().Read()", src);

        string authority = ReadSource("src/PAXCookbook.App/ProductionOrganizationAuthority.cs");
        Assert.Contains("new MachinePolicyRegistrySource().Read()", authority);
        Assert.Contains("MachinePolicyParser.Classify", authority);
        Assert.Contains("ManagedChefKeysGate.Evaluate", authority);
    }

    [Fact] // G34 — no organization mutation route or org inventory route exists.
    public void G34_NoOrganizationMutationRoute()
    {
        string src = Program;
        Assert.DoesNotContain("/api/v1/organization-keys", src);
        Assert.DoesNotContain("/api/v1/chef-keys/organization", src);
        Assert.DoesNotContain("OrganizationChefKeyInventory", src);
    }

    [Fact] // G35 — personal write routes never accept an organization/origin field.
    public void G35_PersonalRoutes_RejectOrganizationFields()
    {
        string src = ChefKeyModelSource;
        // The allow-list is the only accepted request-key set; adding origin/
        // organization would appear here. It must not.
        Assert.DoesNotContain("\"origin\"", src);
        Assert.DoesNotContain("\"organization\"", src);
        // The unknown-field rejection path is preserved.
        Assert.Contains("unknown_field", src);
        Assert.Contains("AllowedRequestKeys", src);
    }

    [Fact] // G36 — List emits the bounded organizationKeys wire object beside unchanged personal chefKeys.
    public void G36_List_EmitsBoundedOrganizationKeys_WithUnchangedPersonalArray()
    {
        string src = ChefKeyModelSource;
        Assert.Contains("List(ManagedChefKeysGateProjection organizationKeys)", src);
        Assert.Contains("chefKeys = items", src);
        Assert.Contains("organizationKeys.WireState", src);
        Assert.Contains("organizationKeys.WireReason", src);
        Assert.Contains("inventoryLoaded = false", src);
        // No organization array / count / identifier leaks into the wire object.
        Assert.DoesNotContain("organizationKeys.Count", src);
        Assert.DoesNotContain("organizationItems", src);
    }

    [Fact] // G37 — the gate contract depends only on System (portable; no Win32 into Shared).
    public void G37_GateContract_DependsOnlyOnSystem()
    {
        string src = GateContract;
        Assert.Contains("using System;", src);
        Assert.DoesNotContain("using Microsoft.Win32", src);
        Assert.DoesNotContain("using System.Security.Cryptography.X509Certificates", src);
        Assert.DoesNotContain("using System.Net", src);
    }

    // ---- shared helper -------------------------------------------------------

    private static ManagedChefKeysGateProjection[] AllReachableProjections() => new[]
    {
        ManagedChefKeysGate.Evaluate(MachinePolicyDetection.NotConfigured()),
        ManagedChefKeysGate.Evaluate(MachinePolicyDetection.ConfiguredSelfService()),
        ManagedChefKeysGate.Evaluate(OrgManaged(MachinePolicyCapability.Disabled, MachinePolicyCapability.Disabled)),
        ManagedChefKeysGate.Evaluate(OrgManaged(MachinePolicyCapability.Enabled, MachinePolicyCapability.Disabled)),
        ManagedChefKeysGate.Evaluate(MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.Malformed)),
        ManagedChefKeysGate.Evaluate(MachinePolicyDetection.Invalid(MachinePolicyRecoveryReason.AccessDenied)),
        ManagedChefKeysGate.Evaluate(MachinePolicyDetection.Untrusted()),
        ManagedChefKeysGate.Evaluate(null),
    };
}
